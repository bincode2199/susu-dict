using System.Collections.Concurrent;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F09.2 (PLAN 6.1, TEST-PLAN DICT01/DICT02): the dictionary card inside a translation session. A word form on a card
/// whose instance has an enabled dictionary service looks the word up first; only a legal empty entry falls back to
/// translate; an error is the card's error; nothing is requested while collapsed, and collapse/cancel/reopen/reuse
/// behave exactly like an ordinary card with lookup and translate never in flight together.
/// </summary>
public class DictionarySessionTests
{
    private static readonly ConfigSnapshot Config = new(1, 1, 1, 2, TimeSpan.FromSeconds(30));

    private static readonly DictionaryResult Good = new("good",
        [new Phonetic("us", "ɡʊd", "https://dict.example/good-us.mp3"), new Phonetic("uk", "ɡʊd", "http://insecure.example/good-uk.mp3")],
        [new PartOfSpeech("adj.", ["好的", "优良的"])], [new WordForm("比较级", "better")], [new Example("Good job.", "干得好。")]);

    private static readonly DictionaryResult Empty = new("xyzzy", [], []);

    /// <summary>A provider with both capabilities. Lookup steps are consumed per call and the last one repeats;
    /// translate is delegated to a <see cref="ScriptedProvider"/>. Records call order and peak concurrency.</summary>
    private sealed class DictionaryProvider(string serviceId, ScriptedProvider translate, params Func<DictionaryCall, CancellationToken, Task<DictionaryOutcome>>[] lookups)
        : ITranslationProvider, IDictionaryProvider
    {
        private readonly ConcurrentQueue<Func<DictionaryCall, CancellationToken, Task<DictionaryOutcome>>> script = new(lookups);
        private Func<DictionaryCall, CancellationToken, Task<DictionaryOutcome>> last = lookups[^1];
        private int inFlight;
        public int MaxInFlight;
        public int LookupCancellations;
        public ConcurrentQueue<string> Log { get; } = new();
        public ConcurrentQueue<DictionaryCall> Lookups { get; } = new();
        public ScriptedProvider Translate => translate;
        public bool DictionaryEnabled { get; init; } = true;
        public string ServiceId => serviceId;
        public string DisplayName => serviceId;
        public string LimiterKey => $"key:{serviceId}";
        public TranslationLimits Limits => ScriptedProvider.Generous;
        public bool SupportsLanguagePair(string from, string to) => true;

        private void Enter() { int now = Interlocked.Increment(ref inFlight); int seen; while (now > (seen = Volatile.Read(ref MaxInFlight)) && Interlocked.CompareExchange(ref MaxInFlight, now, seen) != seen) { } }

        public async Task<ProviderOutcome> TranslateAsync(TranslateCall call, Func<string, ValueTask> onChunk, CancellationToken cancellationToken)
        {
            Log.Enqueue($"translate:{call.Text}");
            Enter();
            try { return await translate.TranslateAsync(call, onChunk, cancellationToken); }
            finally { Interlocked.Decrement(ref inFlight); }
        }

        public async Task<DictionaryOutcome> LookupAsync(DictionaryCall call, CancellationToken cancellationToken)
        {
            Log.Enqueue($"dictionary:{call.Word}");
            Lookups.Enqueue(call);
            var step = script.TryDequeue(out var next) ? (last = next) : last;
            Enter();
            try { return await step(call, cancellationToken); }
            catch (OperationCanceledException) { Interlocked.Increment(ref LookupCancellations); throw; }
            finally { Interlocked.Decrement(ref inFlight); }
        }
    }

    private static Func<DictionaryCall, CancellationToken, Task<DictionaryOutcome>> Found(DictionaryResult r) => (_, _) => Task.FromResult<DictionaryOutcome>(new DictionaryOutcome.Entry(r));
    private static Func<DictionaryCall, CancellationToken, Task<DictionaryOutcome>> Fails(ErrorKind kind) => (_, _) => Task.FromResult<DictionaryOutcome>(new DictionaryOutcome.Failure(new ProviderError(kind)));
    private static Func<DictionaryCall, CancellationToken, Task<DictionaryOutcome>> Hangs() => async (_, cancel) => { await Task.Delay(Timeout.Infinite, cancel); throw new InvalidOperationException(); };
    private static Func<DictionaryCall, CancellationToken, Task<DictionaryOutcome>> Gated(TaskCompletionSource release, DictionaryResult r)
        => async (_, cancel) => { await release.Task.WaitAsync(cancel); return new DictionaryOutcome.Entry(r); };

    private static DictionaryProvider Youdao(Step translate, params Func<DictionaryCall, CancellationToken, Task<DictionaryOutcome>>[] lookups)
        => new("youdao/translate", new ScriptedProvider("youdao/translate", ScriptedProvider.Generous, translate), lookups);

    private static (TranslationSession Session, ManualClock Clock, RecordingUsage Usage) Create(int expanded, params ITranslationProvider[] providers)
    {
        var clock = new ManualClock();
        var usage = new RecordingUsage();
        int ids = 0;
        var session = new TranslationSession(providers, new TranslationSessionOptions(Config, expanded, () => $"a{Interlocked.Increment(ref ids)}"),
            new InvocationScheduler(new SchedulerLimits()), clock, new FixedJitter(0), usage);
        return (session, clock, usage);
    }

    private static async Task<CardSnapshot> CardOf(TranslationSession session, string serviceId = "youdao/translate")
        => (await session.SnapshotAsync()).Cards.Single(c => c.ServiceId == serviceId);

    private static async Task Until(Func<Task<bool>> condition, ManualClock? clock = null, int stepMs = 0)
    {
        for (int i = 0; i < 400; i++)
        {
            if (await condition()) return;
            if (clock is not null && stepMs > 0) clock.Advance(TimeSpan.FromMilliseconds(stepMs));
            await Task.Delay(5);
        }
        throw new TimeoutException("condition not reached");
    }

    // ---- DICT01: routing ----

    [Fact] // a word form on an expanded card with an entry: dictionary only, structured body, no translate
    public async Task Word_form_with_an_entry_shows_the_dictionary_and_never_translates()
    {
        var youdao = Youdao(new Step.Succeed("好"), Found(Good));
        var (session, _, usage) = Create(1, youdao);
        await session.SubmitAsync("  good ", "en", "zh-Hans");
        await session.IdleAsync();
        var card = await CardOf(session);
        Assert.Equal(CardState.Ready, card.State);
        Assert.True(card.Dictionary);
        Assert.Equal("", card.Text);
        Assert.Equal(["dictionary:good"], youdao.Log);
        Assert.Equal(("good", "en", "zh-Hans"), (youdao.Lookups.Single().Word, youdao.Lookups.Single().From, youdao.Lookups.Single().To));
        var entry = card.Entry!;
        Assert.Equal("good", entry.Word);
        Assert.Equal([("adj.", "好的|优良的")], entry.Parts.Select(p => (p.Pos, string.Join('|', p.Means))));
        Assert.Equal([("比较级", "better")], entry.Forms.Select(f => (f.Name, f.Value)));
        Assert.Equal([("Good job.", "干得好。")], entry.Examples.Select(e => (e.Src, e.Dst)));
        Assert.Single(usage.Records, r => r.Metric == "dictionary" && r.Outcome == "ok");
        Assert.DoesNotContain(usage.Records, r => r.Metric == "chars");
    }

    [Fact] // audio links reach the page only as opaque ids; only https links survive; ids resolve only while current
    public async Task Audio_links_become_opaque_session_ids_that_expire_with_the_generation()
    {
        var youdao = Youdao(new Step.Succeed("x"), Found(Good));
        var (session, _, _) = Create(1, youdao);
        await session.SubmitAsync("good", "en", "zh-Hans");
        await session.IdleAsync();
        var phonetics = (await CardOf(session)).Entry!.Phonetics;
        Assert.Equal(["us", "uk"], phonetics.Select(p => p.Accent));
        string id = phonetics[0].AudioId!;
        Assert.DoesNotContain("http", id);
        Assert.DoesNotContain("example", id);
        Assert.Null(phonetics[1].AudioId); // http link dropped
        Assert.Equal("https://dict.example/good-us.mp3", await session.ResolveAudioAsync(id));
        Assert.Null(await session.ResolveAudioAsync("https://dict.example/good-us.mp3"));
        Assert.Null(await session.ResolveAudioAsync("audio-unknown"));
        await session.SubmitAsync("hello there.", "en", "zh-Hans");
        await session.IdleAsync();
        Assert.Null(await session.ResolveAudioAsync(id));
    }

    [Theory] // non-word forms translate directly: phrase, sentence, punctuation, digits, mixed scripts, long Han run
    [InlineData("good morning")]
    [InlineData("How are you?")]
    [InlineData("hello,")]
    [InlineData("mp3")]
    [InlineData("good好")]
    [InlineData("中华人民共和国")]
    public async Task Non_word_forms_use_translate_only(string text)
    {
        var youdao = Youdao(new Step.Echo("T:"), Found(Good));
        var (session, _, _) = Create(1, youdao);
        await session.SubmitAsync(text, "en", "zh-Hans");
        await session.IdleAsync();
        var card = await CardOf(session);
        Assert.Equal((CardState.Ready, "T:" + text, false), (card.State, card.Text, card.Dictionary));
        Assert.Null(card.Entry);
        Assert.Empty(youdao.Lookups);
    }

    [Theory] // Chinese word forms (≤ 4 Han) look up too
    [InlineData("你好")]
    [InlineData("图书馆员")]
    public async Task Chinese_word_forms_look_up(string word)
    {
        var youdao = Youdao(new Step.Echo("T:"), Found(Good with { Word = word }));
        var (session, _, _) = Create(1, youdao);
        await session.SubmitAsync(word, "zh-Hans", "en");
        await session.IdleAsync();
        Assert.True((await CardOf(session)).Dictionary);
        Assert.Equal([$"dictionary:{word}"], youdao.Log);
    }

    [Fact] // no enabled dictionary service: no lookup, ordinary translation (PLAN 6.1 "没有已配置的 dictionary 服务时不触发")
    public async Task Disabled_dictionary_service_translates_word_forms()
    {
        var youdao = new DictionaryProvider("youdao/translate", new ScriptedProvider("youdao/translate", ScriptedProvider.Generous, new Step.Echo("T:")), Found(Good)) { DictionaryEnabled = false };
        var (session, _, _) = Create(1, youdao);
        await session.SubmitAsync("good", "en", "zh-Hans");
        await session.IdleAsync();
        Assert.Equal("T:good", (await CardOf(session)).Text);
        Assert.Empty(youdao.Lookups);
    }

    [Fact] // other providers are never a dictionary source, even for a word form (DICT01 "有道之外不伪造词典来源")
    public async Task Providers_without_a_dictionary_capability_just_translate()
    {
        var youdao = Youdao(new Step.Echo("Y:"), Found(Good));
        var plain = new ScriptedProvider("mymemory/translate", ScriptedProvider.Generous, new Step.Echo("M:"));
        var (session, _, _) = Create(2, youdao, plain);
        await session.SubmitAsync("good", "en", "zh-Hans");
        await session.IdleAsync();
        var other = await CardOf(session, "mymemory/translate");
        Assert.Equal(("M:good", false), (other.Text, other.Dictionary));
        Assert.True((await CardOf(session)).Dictionary);
    }

    [Fact] // legal empty entry: exactly one lookup, then exactly one translate, sequential, on the same card
    public async Task Legal_empty_entry_falls_back_to_translate_sequentially()
    {
        var youdao = Youdao(new Step.Echo("T:"), Found(Empty));
        var (session, _, usage) = Create(1, youdao);
        await session.SubmitAsync("xyzzy", "en", "zh-Hans");
        await session.IdleAsync();
        var card = await CardOf(session);
        Assert.Equal((CardState.Ready, "T:xyzzy", false), (card.State, card.Text, card.Dictionary));
        Assert.Null(card.Entry);
        Assert.Equal(["dictionary:xyzzy", "translate:xyzzy"], youdao.Log);
        Assert.Equal(1, youdao.MaxInFlight);
        Assert.Single(usage.Records, r => r.Metric == "dictionary" && r.Outcome == "empty");
        Assert.Single(usage.Records, r => r.Metric == "chars" && r.Outcome == "ok");
    }

    [Fact] // an entry whose every item is unusable (no text) counts as empty, not as a blank dictionary card
    public async Task Entry_with_only_unusable_items_falls_back()
    {
        var junk = new DictionaryResult("good", [new Phonetic("us", "", "https://dict.example/a.mp3")], [new PartOfSpeech("adj.", ["", " "])]);
        var youdao = Youdao(new Step.Echo("T:"), Found(junk));
        var (session, _, _) = Create(1, youdao);
        await session.SubmitAsync("good", "en", "zh-Hans");
        await session.IdleAsync();
        Assert.Equal(("T:good", false), ((await CardOf(session)).Text, (await CardOf(session)).Dictionary));
    }

    [Theory] // an error is an error: no translate call hides it
    [InlineData(ErrorKind.Auth)]
    [InlineData(ErrorKind.Quota)]
    [InlineData(ErrorKind.BadResponse)]
    public async Task Dictionary_error_fails_the_card_without_fallback(ErrorKind kind)
    {
        var youdao = Youdao(new Step.Echo("T:"), Fails(kind));
        var (session, _, _) = Create(1, youdao);
        await session.SubmitAsync("good", "en", "zh-Hans");
        await session.IdleAsync();
        var card = await CardOf(session);
        Assert.Equal((CardState.Failed, kind), (card.State, card.Error));
        Assert.Empty(youdao.Translate.Calls);
    }

    [Fact] // a retryable dictionary error retries the lookup (new attempt), still never translate
    public async Task Retryable_dictionary_error_retries_the_lookup_only()
    {
        var youdao = Youdao(new Step.Echo("T:"), Fails(ErrorKind.Network), Found(Good));
        var (session, clock, _) = Create(1, youdao);
        await session.SubmitAsync("good", "en", "zh-Hans");
        await Until(async () => (await CardOf(session)).State == CardState.Ready, clock, 500);
        await session.IdleAsync();
        Assert.True((await CardOf(session)).Dictionary);
        Assert.Equal(["dictionary:good", "dictionary:good"], youdao.Log);
        Assert.NotEqual(youdao.Lookups.First().AttemptId, youdao.Lookups.Last().AttemptId);
    }

    [Fact] // after an empty entry, a translate retry repeats translate only (the empty entry is not asked again)
    public async Task Translate_retry_after_empty_entry_does_not_repeat_the_lookup()
    {
        var translate = new ScriptedProvider("youdao/translate", ScriptedProvider.Generous, new Step.Fail(new ProviderError(ErrorKind.Network)), new Step.Echo("T:"));
        var youdao = new DictionaryProvider("youdao/translate", translate, Found(Empty));
        var (session, clock, _) = Create(1, youdao);
        await session.SubmitAsync("xyzzy", "en", "zh-Hans");
        await Until(async () => (await CardOf(session)).State == CardState.Ready, clock, 500);
        await session.IdleAsync();
        Assert.Equal("T:xyzzy", (await CardOf(session)).Text);
        Assert.Equal(["dictionary:xyzzy", "translate:xyzzy", "translate:xyzzy"], youdao.Log);
    }

    [Fact] // manual retry after a dictionary error is a new operation: lookup again
    public async Task Manual_retry_after_a_dictionary_error_looks_up_again()
    {
        var youdao = Youdao(new Step.Echo("T:"), Fails(ErrorKind.Auth), Found(Good));
        var (session, _, _) = Create(1, youdao);
        await session.SubmitAsync("good", "en", "zh-Hans");
        await session.IdleAsync();
        Assert.Equal(CardState.Failed, (await CardOf(session)).State);
        await session.RetryAsync("youdao/translate");
        await session.IdleAsync();
        Assert.True((await CardOf(session)).Dictionary);
        Assert.Equal(2, youdao.Lookups.Count);
        Assert.Empty(youdao.Translate.Calls);
    }

    // ---- DICT02: collapse / expand / cancel / reopen ----

    [Fact] // collapsed: nothing is sent; each expansion sends exactly one lookup; a finished entry is reused on reopen
    public async Task Collapsed_card_sends_nothing_and_each_expansion_sends_one_lookup()
    {
        var youdao = Youdao(new Step.Echo("T:"), Found(Good));
        var plain = new ScriptedProvider("mymemory/translate", ScriptedProvider.Generous, new Step.Echo("M:"));
        var (session, _, _) = Create(1, plain, youdao); // only the first card is expanded by default
        await session.SubmitAsync("good", "en", "zh-Hans");
        await session.IdleAsync();
        Assert.Equal(CardState.CollapsedIdle, (await CardOf(session)).State);
        Assert.Empty(youdao.Log);

        await session.ToggleAsync("youdao/translate");
        await session.IdleAsync();
        Assert.True((await CardOf(session)).Dictionary);
        Assert.Equal(["dictionary:good"], youdao.Log);

        await session.ToggleAsync("youdao/translate"); // collapse a finished card
        await session.ToggleAsync("youdao/translate"); // reopen: reuse, no new request
        await session.IdleAsync();
        var card = await CardOf(session);
        Assert.Equal((CardState.Ready, false, true), (card.State, card.Collapsed, card.Dictionary));
        Assert.Equal(["dictionary:good"], youdao.Log);
    }

    [Fact] // collapse during the lookup cancels it (no translate follows); reopen sends exactly one new lookup
    public async Task Collapse_cancels_the_lookup_and_reopen_looks_up_once_more()
    {
        var release = new TaskCompletionSource();
        var youdao = Youdao(new Step.Echo("T:"), Hangs(), Gated(release, Good));
        var (session, _, usage) = Create(1, youdao);
        await session.SubmitAsync("good", "en", "zh-Hans");
        await Until(async () => youdao.Lookups.Count == 1);
        await session.ToggleAsync("youdao/translate");
        await session.IdleAsync();
        var card = await CardOf(session);
        Assert.Equal((CardState.Cancelled, true, false), (card.State, card.Collapsed, card.Dictionary));
        Assert.Equal(1, youdao.LookupCancellations);
        Assert.Contains(usage.Records, r => r.Metric == "dictionary" && r.Outcome == "cancelled");

        await session.ToggleAsync("youdao/translate");
        await Until(async () => youdao.Lookups.Count == 2);
        release.SetResult();
        await session.IdleAsync();
        Assert.True((await CardOf(session)).Dictionary);
        Assert.Equal(["dictionary:good", "dictionary:good"], youdao.Log);
        Assert.Empty(youdao.Translate.Calls);
        Assert.Equal(1, youdao.MaxInFlight);
    }

    [Fact] // collapse during the fallback translate cancels it; reopen is a new attempt (lookup, then translate) with nothing in parallel
    public async Task Collapse_during_fallback_translate_cancels_and_reopen_restarts_the_card()
    {
        var translate = new ScriptedProvider("youdao/translate", ScriptedProvider.Generous, new Step.Hang(), new Step.Echo("T:"));
        var youdao = new DictionaryProvider("youdao/translate", translate, Found(Empty));
        var (session, _, _) = Create(1, youdao);
        await session.SubmitAsync("xyzzy", "en", "zh-Hans");
        await Until(async () => translate.Calls.Count == 1);
        await session.ToggleAsync("youdao/translate");
        await session.IdleAsync();
        Assert.Equal(CardState.Cancelled, (await CardOf(session)).State);
        Assert.Equal(1, translate.Cancellations);

        await session.ToggleAsync("youdao/translate");
        await session.IdleAsync();
        Assert.Equal("T:xyzzy", (await CardOf(session)).Text);
        Assert.Equal(["dictionary:xyzzy", "translate:xyzzy", "dictionary:xyzzy", "translate:xyzzy"], youdao.Log);
        Assert.Equal(1, youdao.MaxInFlight);
    }

    [Fact] // a new source while the lookup runs: the old lookup is cancelled and its late entry never lands
    public async Task New_generation_cancels_the_running_lookup()
    {
        var release = new TaskCompletionSource();
        var youdao = Youdao(new Step.Echo("T:"), Gated(release, Good), Found(Good with { Word = "fine" }));
        var (session, _, _) = Create(1, youdao);
        await session.SubmitAsync("good", "en", "zh-Hans");
        await Until(async () => youdao.Lookups.Count == 1);
        await session.SubmitAsync("fine", "en", "zh-Hans");
        await session.IdleAsync();
        release.TrySetResult();
        await session.IdleAsync();
        Assert.Equal("fine", (await CardOf(session)).Entry!.Word);
        Assert.Equal(1, youdao.LookupCancellations);
        Assert.Equal(2, youdao.Lookups.Count);
    }

    [Fact] // closing the window cancels an unfinished lookup
    public async Task Close_cancels_the_lookup()
    {
        var youdao = Youdao(new Step.Echo("T:"), Hangs());
        var (session, _, _) = Create(1, youdao);
        await session.SubmitAsync("good", "en", "zh-Hans");
        await Until(async () => youdao.Lookups.Count == 1);
        await session.CloseAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, youdao.LookupCancellations);
        Assert.Empty(youdao.Translate.Calls);
    }

    [Fact] // a hung lookup ends at the card deadline as a timeout; no translate is sent after it
    public async Task Lookup_deadline_is_a_timeout_not_a_fallback()
    {
        var youdao = Youdao(new Step.Echo("T:"), Hangs());
        var (session, clock, _) = Create(1, youdao);
        await session.SubmitAsync("good", "en", "zh-Hans");
        await Until(async () => (await CardOf(session)).State == CardState.Failed, clock, 5000);
        Assert.Equal(ErrorKind.Timeout, (await CardOf(session)).Error);
        Assert.Empty(youdao.Translate.Calls);
    }
}
