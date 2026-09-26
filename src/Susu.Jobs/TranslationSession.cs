using System.Text;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;

namespace Susu.Jobs;

public sealed record TranslationSessionOptions(ConfigSnapshot Config, int DefaultExpanded, Func<string>? IdFactory = null, InvocationPriority Priority = InvocationPriority.Interactive);

/// <summary>
/// One translation window session: a card per enabled translation service, all mutations serialized
/// through a mailbox and filtered by generation/attempt/sequence (ARCHITECTURE 5). Providers run outside
/// the mailbox; their events are posted back. Emits a patch per card change and a full snapshot on demand.
/// </summary>
public sealed class TranslationSession
{
    private readonly IReadOnlyList<ITranslationProvider> providers;
    private readonly Dictionary<string, ITranslationProvider> byService;
    private readonly TranslationSessionOptions options;
    private readonly InvocationScheduler scheduler;
    private readonly IClock clock;
    private readonly IJitter jitter;
    private readonly IUsageSink usage;
    private readonly SerialMailbox mailbox = new();
    private readonly Dictionary<string, Card> cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> running = new(StringComparer.Ordinal); // by attemptId
    private readonly List<Task> attempts = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string ServiceId, long Generation, string Url)> audio = new(StringComparer.Ordinal);
    private readonly Func<string> newId;
    private long generation, revision;
    private string source = "", from = Languages.English.Code, to = Languages.ChineseSimplified.Code;
    private bool closed;

    public event Action<CardPatch>? CardChanged;

    public TranslationSession(IReadOnlyList<ITranslationProvider> providers, TranslationSessionOptions options, InvocationScheduler scheduler, IClock clock, IJitter jitter, IUsageSink usage)
    {
        this.providers = providers; this.options = options; this.scheduler = scheduler; this.clock = clock; this.jitter = jitter; this.usage = usage;
        byService = providers.ToDictionary(p => p.ServiceId, StringComparer.Ordinal);
        newId = options.IdFactory ?? (() => Guid.NewGuid().ToString("N"));
        for (int i = 0; i < providers.Count; i++) cards[providers[i].ServiceId] = Card.Create(providers[i].ServiceId, collapsed: i >= options.DefaultExpanded);
    }

    /// <summary>New source text or language pair: a new generation; expanded cards request immediately.</summary>
    public Task SubmitAsync(string text, string sourceLanguage, string targetLanguage) => mailbox.Post(() =>
    {
        if (closed) return;
        generation++;
        source = text; from = sourceLanguage; to = targetLanguage;
        foreach (var stale in audio.Where(a => a.Value.Generation < generation).Select(a => a.Key).ToArray()) audio.TryRemove(stale, out _);
        foreach (var serviceId in cards.Keys.ToArray()) Apply(serviceId, new CardEvent.NewGeneration(generation, newId()));
    });

    public Task ToggleAsync(string serviceId) => mailbox.Post(() =>
    {
        if (closed || !cards.TryGetValue(serviceId, out var card)) return;
        Apply(serviceId, card.Collapsed ? new CardEvent.Expand(newId()) : new CardEvent.Collapse());
    });

    public Task RetryAsync(string serviceId) => mailbox.Post(() => { if (!closed && cards.ContainsKey(serviceId)) Apply(serviceId, new CardEvent.ManualRetry(newId())); });

    /// <summary>Close/Esc: cancel unfinished work; completed projections stay until the session is reset (ARCHITECTURE 5.2).</summary>
    public async Task CloseAsync(TimeSpan cleanup)
    {
        await mailbox.Post(() =>
        {
            closed = true;
            foreach (var serviceId in cards.Keys.ToArray()) if (cards[serviceId].IsActive) Apply(serviceId, new CardEvent.Collapse());
            foreach (var cts in running.Values) cts.Cancel();
        });
        Task[] pending;
        lock (attempts) pending = [.. attempts];
        await Task.WhenAny(Task.WhenAll(pending), Task.Delay(cleanup));
    }

    public Task<TranslationSnapshot> SnapshotAsync()
    {
        var result = new TaskCompletionSource<TranslationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        mailbox.Post(() => result.SetResult(new TranslationSnapshot(revision, generation, source, from, to, [.. providers.Select(p => Project(cards[p.ServiceId]))], NetworkState.IsOffline(cards.Values))));
        return result.Task;
    }

    /// <summary>Waits until every posted mutation and all provider attempts have settled (tests and shutdown).</summary>
    public async Task IdleAsync()
    {
        while (true)
        {
            await mailbox.Post(() => { });
            Task[] pending;
            lock (attempts) pending = [.. attempts.Where(t => !t.IsCompleted)];
            if (pending.Length == 0) return;
            await Task.WhenAll(pending);
        }
    }

    private CardSnapshot Project(Card card)
        => new(card.ServiceId, byService[card.ServiceId].DisplayName, card.State, card.Collapsed, card.Text, card.Error, Chunked: byService[card.ServiceId].Limits.Measure(source) > byService[card.ServiceId].Limits.MaxInput,
            Dictionary: card.Entry is not null, Entry: card.Entry);

    /// <summary>Runs on the mailbox only.</summary>
    private void Apply(string serviceId, CardEvent @event)
    {
        var before = cards[serviceId];
        var (after, effects) = CardReducer.Reduce(before, @event);
        cards[serviceId] = after;
        if (!Equals(before, after))
        {
            revision++;
            CardChanged?.Invoke(new CardPatch(revision, generation, Project(after), NetworkState.IsOffline(cards.Values)));
        }
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case CardEffect.Cancel c:
                    if (running.Remove(c.AttemptId, out var cts)) cts.Cancel();
                    break;
                case CardEffect.Request r:
                    var provider = byService[r.ServiceId];
                    var token = new CancellationTokenSource();
                    running[r.AttemptId] = token;
                    var task = Task.Run(() => RunAsync(provider, r.Generation, r.AttemptId, source, from, to, token.Token));
                    lock (attempts) { attempts.RemoveAll(t => t.IsCompleted); attempts.Add(task); }
                    break;
            }
        }
    }

    private void Post(string serviceId, CardEvent @event) => mailbox.Post(() => { if (cards.ContainsKey(serviceId)) Apply(serviceId, @event); });

    private async Task RunAsync(ITranslationProvider provider, long gen, string firstAttempt, string text, string sourceLanguage, string targetLanguage, CancellationToken cancel)
    {
        string serviceId = provider.ServiceId, attemptId = firstAttempt;
        long sequence = 0;
        EventStamp Next() => new(gen, attemptId, ++sequence);
        if (!provider.SupportsLanguagePair(sourceLanguage, targetLanguage)) { Post(serviceId, new CardEvent.Unsupported()); return; }
        var timeout = options.Config.AiTimeout > TimeSpan.Zero ? options.Config.AiTimeout : ConfigSnapshot.EffectiveTimeout(Capability.Translate, null);
        long deadline = clock.NowMilliseconds + (long)timeout.TotalMilliseconds;
        var chunks = TextChunker.Split(text, provider.Limits);
        int attemptsMade = 1;
        // PLAN 6.1 / DICT01-02: a word form on a card whose instance has an enabled dictionary service looks the word
        // up first, inside this same attempt (so collapse/cancel/retry/reuse are exactly an ordinary card's). The
        // translate call runs only after a legal empty entry, never alongside the lookup; an error is the card's error.
        var dictionary = provider as IDictionaryProvider;
        string? word = dictionary is { DictionaryEnabled: true } ? TextForms.DictionaryWord(text) : null;
        Post(serviceId, new CardEvent.Started(Next()));
        while (true)
        {
            var assembled = new StringBuilder();
            bool streamed = false;
            ProviderError? failure = null;
            if (word is not null)
            {
                TimeSpan left = TimeSpan.FromMilliseconds(Math.Max(0, deadline - clock.NowMilliseconds));
                var lookup = await LookupAsync(provider, dictionary!, word, sourceLanguage, targetLanguage, attemptId, left, cancel);
                if (cancel.IsCancellationRequested) { usage.Record(serviceId, attemptId, "dictionary", 0, "cancelled"); return; }
                if (lookup is DictionaryOutcome.Entry found)
                {
                    long gen0 = gen;
                    var view = DictionaryEntries.IsEmpty(found.Result) ? null : DictionaryEntries.ToView(found.Result, url => RegisterAudio(serviceId, gen0, url));
                    bool empty = view is null || DictionaryEntries.IsEmpty(view);
                    usage.Record(serviceId, attemptId, "dictionary", word.Length, empty ? "empty" : "ok");
                    if (!empty) { Post(serviceId, new CardEvent.DictionaryCompleted(Next(), view!)); return; }
                    word = null; // legal empty entry: this card falls back to the plain translation, once
                }
                else
                {
                    failure = ((DictionaryOutcome.Failure)lookup).Error;
                    usage.Record(serviceId, attemptId, "dictionary", 0, failure.Kind.ToString());
                }
            }
            if (failure is null)
            {
                // Usage (DATA04, F06.2 de-duplication): one event per attempt, keyed by the attempt id, carrying
                // the characters of every chunk the vendor accepted in that attempt. Recording per chunk under the
                // shared attempt id dropped every chunk after the first (the event table is unique per attempt
                // and metric); a retry is a new attempt and a new vendor request, so it is its own event.
                long accepted = 0;
                foreach (var chunk in chunks)
                {
                    TimeSpan remaining = TimeSpan.FromMilliseconds(Math.Max(0, deadline - clock.NowMilliseconds));
                    var outcome = await CallAsync(provider, chunk.Text, sourceLanguage, targetLanguage, attemptId, remaining, piece =>
                    {
                        streamed = true;
                        Post(serviceId, new CardEvent.Chunk(Next(), piece));
                        return ValueTask.CompletedTask;
                    }, cancel);
                    if (cancel.IsCancellationRequested) { usage.Record(serviceId, attemptId, "chars", accepted, "cancelled"); return; }
                    if (outcome is ProviderOutcome.Success success)
                    {
                        accepted += chunk.Text.Length;
                        assembled.Append(success.Text);
                        if (!streamed && chunks.Count > 1) Post(serviceId, new CardEvent.Chunk(Next(), success.Text)); // in-order progress for chunked text
                        continue;
                    }
                    failure = ((ProviderOutcome.Failure)outcome).Error;
                    break;
                }
                usage.Record(serviceId, attemptId, "chars", accepted, failure is null ? "ok" : failure.Kind.ToString());
                if (failure is null) { Post(serviceId, new CardEvent.Completed(Next(), assembled.ToString())); return; }
            }
            var decision = RetryPolicy.Decide(failure, attemptsMade, TimeSpan.FromMilliseconds(Math.Max(0, deadline - clock.NowMilliseconds)), streamed || assembled.Length > 0, readOnly: true, jitter.Next());
            if (decision is RetryDecision.Stop) { Post(serviceId, new CardEvent.Failed(Next(), failure.Kind)); return; }
            var retry = (RetryDecision.Retry)decision;
            try { await clock.Delay(retry.Delay, cancel); } catch (OperationCanceledException) { return; }
            // A retry is a new attempt: ResultReset first so text of two attempts is never concatenated.
            string previous = attemptId;
            attemptId = newId();
            sequence = 0;
            attemptsMade++;
            await mailbox.Post(() =>
            {
                if (!running.Remove(previous, out var cts)) return; // cancelled while waiting
                running[attemptId] = cts;
                if (cards.ContainsKey(serviceId)) Apply(serviceId, new CardEvent.Reset(new EventStamp(gen, attemptId, 0)));
            });
            if (cancel.IsCancellationRequested) return;
        }
    }

    /// <summary>One dictionary request under the same scheduler lease, deadline and cancellation as a translate call.</summary>
    private async Task<DictionaryOutcome> LookupAsync(ITranslationProvider provider, IDictionaryProvider dictionary, string word, string sourceLanguage, string targetLanguage,
        string attemptId, TimeSpan remaining, CancellationToken cancel)
    {
        if (remaining <= TimeSpan.Zero) return new DictionaryOutcome.Failure(new ProviderError(ErrorKind.Timeout, "deadline exhausted"));
        InvocationScheduler.Lease lease;
        try { lease = await scheduler.AcquireAsync(new InvocationTicket(provider.ServiceId, provider.LimiterKey, options.Priority), cancel); }
        catch (SchedulerBusyException) { return new DictionaryOutcome.Failure(new ProviderError(ErrorKind.Busy)); }
        catch (OperationCanceledException) { return new DictionaryOutcome.Failure(new ProviderError(ErrorKind.Cancelled)); }
        using (lease)
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            var call = dictionary.LookupAsync(new DictionaryCall(word, sourceLanguage, targetLanguage, attemptId, options.Config, remaining), linked.Token);
            var timer = clock.Delay(remaining, linked.Token);
            var first = await Task.WhenAny(call, timer);
            if (first == timer && !call.IsCompleted)
            {
                linked.Cancel();
                try { await call; } catch (OperationCanceledException) { }
                return new DictionaryOutcome.Failure(new ProviderError(ErrorKind.Timeout, "call deadline"));
            }
            linked.Cancel();
            try { return await call; }
            catch (OperationCanceledException) { return new DictionaryOutcome.Failure(new ProviderError(ErrorKind.Cancelled)); }
        }
    }

    /// <summary>An opaque id for an https audio link of an entry (never the URL itself on the page).</summary>
    private string RegisterAudio(string serviceId, long gen, string url)
    {
        string id = "audio-" + Guid.NewGuid().ToString("N");
        audio[id] = (serviceId, gen, url);
        return id;
    }

    /// <summary>
    /// The audio link behind <paramref name="audioId"/>, only while a card of the current generation still shows the
    /// entry that carries it (F09.3 authorizes and plays it); null for an unknown, stale or foreign id.
    /// </summary>
    public async Task<string?> ResolveAudioAsync(string audioId) => (await ResolveAudioLinkAsync(audioId))?.Url;

    /// <summary>
    /// Like <see cref="ResolveAudioAsync"/>, plus the card's service id, so the host can authorize the download
    /// against that provider's declared origins only (F09.3, S06; see <c>DictionaryAudioFetcher</c>).
    /// </summary>
    public Task<DictionaryAudioLink?> ResolveAudioLinkAsync(string audioId)
    {
        var result = new TaskCompletionSource<DictionaryAudioLink?>(TaskCreationOptions.RunContinuationsAsynchronously);
        mailbox.Post(() =>
        {
            DictionaryAudioLink? resolved = null;
            if (audio.TryGetValue(audioId, out var link) && link.Generation == generation && cards.TryGetValue(link.ServiceId, out var card)
                && card.Entry is { } entry && entry.Phonetics.Any(p => p.AudioId == audioId)) resolved = new DictionaryAudioLink(link.ServiceId, link.Url);
            result.SetResult(resolved);
        });
        return result.Task;
    }

    private async Task<ProviderOutcome> CallAsync(ITranslationProvider provider, string text, string sourceLanguage, string targetLanguage, string attemptId, TimeSpan remaining,
        Func<string, ValueTask> onChunk, CancellationToken cancel)
    {
        if (remaining <= TimeSpan.Zero) return new ProviderOutcome.Failure(new ProviderError(ErrorKind.Timeout, "deadline exhausted"));
        InvocationScheduler.Lease lease;
        try { lease = await scheduler.AcquireAsync(new InvocationTicket(provider.ServiceId, provider.LimiterKey, options.Priority), cancel); }
        catch (SchedulerBusyException) { return new ProviderOutcome.Failure(new ProviderError(ErrorKind.Busy)); }
        catch (OperationCanceledException) { return new ProviderOutcome.Failure(new ProviderError(ErrorKind.Cancelled)); }
        using (lease)
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            var call = provider.TranslateAsync(new TranslateCall(text, sourceLanguage, targetLanguage, attemptId, options.Config, remaining), onChunk, linked.Token);
            var timer = clock.Delay(remaining, linked.Token);
            var first = await Task.WhenAny(call, timer);
            if (first == timer && !call.IsCompleted)
            {
                linked.Cancel();
                try { await call; } catch (OperationCanceledException) { }
                return new ProviderOutcome.Failure(new ProviderError(ErrorKind.Timeout, "call deadline"));
            }
            linked.Cancel(); // stop the timer
            try { return await call; }
            catch (OperationCanceledException) { return new ProviderOutcome.Failure(new ProviderError(ErrorKind.Cancelled)); }
        }
    }
}
