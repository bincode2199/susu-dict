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
        mailbox.Post(() => result.SetResult(new TranslationSnapshot(revision, generation, source, from, to, [.. providers.Select(p => Project(cards[p.ServiceId]))])));
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
        => new(card.ServiceId, byService[card.ServiceId].DisplayName, card.State, card.Collapsed, card.Text, card.Error, Chunked: byService[card.ServiceId].Limits.Measure(source) > byService[card.ServiceId].Limits.MaxInput);

    /// <summary>Runs on the mailbox only.</summary>
    private void Apply(string serviceId, CardEvent @event)
    {
        var before = cards[serviceId];
        var (after, effects) = CardReducer.Reduce(before, @event);
        cards[serviceId] = after;
        if (!Equals(before, after))
        {
            revision++;
            CardChanged?.Invoke(new CardPatch(revision, generation, Project(after)));
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
        Post(serviceId, new CardEvent.Started(Next()));
        while (true)
        {
            var assembled = new StringBuilder();
            bool streamed = false;
            ProviderError? failure = null;
            foreach (var chunk in chunks)
            {
                TimeSpan remaining = TimeSpan.FromMilliseconds(Math.Max(0, deadline - clock.NowMilliseconds));
                var outcome = await CallAsync(provider, chunk.Text, sourceLanguage, targetLanguage, attemptId, remaining, piece =>
                {
                    streamed = true;
                    Post(serviceId, new CardEvent.Chunk(Next(), piece));
                    return ValueTask.CompletedTask;
                }, cancel);
                if (cancel.IsCancellationRequested) { usage.Record(serviceId, attemptId, "chars", chunk.Text.Length, "cancelled"); return; }
                if (outcome is ProviderOutcome.Success success)
                {
                    usage.Record(serviceId, attemptId, "chars", chunk.Text.Length, "ok");
                    assembled.Append(success.Text);
                    if (!streamed && chunks.Count > 1) Post(serviceId, new CardEvent.Chunk(Next(), success.Text)); // in-order progress for chunked text
                    continue;
                }
                failure = ((ProviderOutcome.Failure)outcome).Error;
                usage.Record(serviceId, attemptId, "chars", chunk.Text.Length, failure.Kind.ToString());
                break;
            }
            if (failure is null) { Post(serviceId, new CardEvent.Completed(Next(), assembled.ToString())); return; }
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
