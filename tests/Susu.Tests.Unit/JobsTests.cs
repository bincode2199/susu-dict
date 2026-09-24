using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

public class SchedulerTests
{
    private static InvocationTicket T(string instance, InvocationPriority priority = InvocationPriority.Interactive, string? key = null, string? category = null)
        => new(instance, key ?? $"key-{instance}", priority, category);

    [Fact] // J04
    public async Task Global_limit_queues_the_ninth_call_and_overflow_is_busy()
    {
        var scheduler = new InvocationScheduler(new SchedulerLimits(Global: 8, QueueCapacity: 2));
        var leases = new List<InvocationScheduler.Lease>();
        for (int i = 0; i < 8; i++) leases.Add(await scheduler.AcquireAsync(T($"i{i}"), CancellationToken.None));
        var ninth = scheduler.AcquireAsync(T("i8"), CancellationToken.None);
        var tenth = scheduler.AcquireAsync(T("i9"), CancellationToken.None);
        Assert.False(ninth.IsCompleted);
        Assert.Throws<SchedulerBusyException>(() => { _ = scheduler.AcquireAsync(T("i10"), CancellationToken.None); });
        leases[0].Dispose();
        using var admitted = await ninth.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(8, scheduler.Running);
        foreach (var lease in leases) lease.Dispose();
        (await tenth).Dispose();
    }

    [Fact] // J04
    public async Task Per_instance_and_shared_account_origin_limits_apply()
    {
        var scheduler = new InvocationScheduler(new SchedulerLimits());
        using var a = await scheduler.AcquireAsync(T("tencent-translate", key: "acct|https://tmt"), CancellationToken.None);
        using var b = await scheduler.AcquireAsync(T("tencent-ocr", key: "acct|https://tmt"), CancellationToken.None);
        var c = scheduler.AcquireAsync(T("tencent-tts", key: "acct|https://tmt"), CancellationToken.None);
        Assert.False(c.IsCompleted); // same account + origin: limit 2 across different plugins
        var other = await scheduler.AcquireAsync(T("deepl"), CancellationToken.None);
        other.Dispose();
        a.Dispose();
        (await c.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Dispose();
    }

    [Fact] // J04: no starvation of background work
    public async Task Background_calls_get_a_slot_after_five_interactive_admissions()
    {
        var scheduler = new InvocationScheduler(new SchedulerLimits(Global: 1, PerInstance: 10, PerLimiterKey: 10));
        var holder = await scheduler.AcquireAsync(T("x"), CancellationToken.None);
        var background = scheduler.AcquireAsync(T("video", InvocationPriority.Background), CancellationToken.None);
        var tasks = new List<(string Name, Task<InvocationScheduler.Lease> Task)> { ("bg", background) };
        for (int i = 0; i < 8; i++) tasks.Add(($"i{i}", scheduler.AcquireAsync(T("x"), CancellationToken.None)));
        var order = new List<string>();
        holder.Dispose();
        for (int step = 0; step < tasks.Count; step++)
        {
            var pending = tasks.Where(t => !order.Contains(t.Name)).ToList();
            var done = await Task.WhenAny(pending.Select(p => p.Task)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            order.Add(pending.First(p => p.Task == done).Name);
            (await done).Dispose();
        }
        int backgroundPosition = order.IndexOf("bg");
        // Global limit 1: after at most five interactive admissions the waiting background call must run.
        Assert.InRange(backgroundPosition, 1, 5);
    }

    [Fact] // J04: cancelling a waiting call does not wait for traffic
    public async Task Cancelling_a_queued_call_returns_immediately()
    {
        var scheduler = new InvocationScheduler(new SchedulerLimits(Global: 1));
        using var holder = await scheduler.AcquireAsync(T("x"), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var waiting = scheduler.AcquireAsync(T("y"), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Equal(0, scheduler.Waiting);
    }

    [Fact]
    public async Task Category_limits_bound_video_pipelines()
    {
        var scheduler = new InvocationScheduler(SchedulerLimits.Default);
        using var asr = await scheduler.AcquireAsync(T("openai", InvocationPriority.Background, category: "video-asr"), CancellationToken.None);
        var second = scheduler.AcquireAsync(T("gemini", InvocationPriority.Background, category: "video-asr"), CancellationToken.None);
        Assert.False(second.IsCompleted);
        asr.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Dispose();
    }
}

public class TranslationSessionTests
{
    private static readonly ConfigSnapshot Config = new(1, 1, 1, 2, TimeSpan.FromSeconds(30));

    private static (TranslationSession Session, ManualClock Clock, RecordingUsage Usage, List<CardPatch> Patches) Create(int expanded, params ITranslationProvider[] providers)
    {
        var clock = new ManualClock();
        var usage = new RecordingUsage();
        int ids = 0;
        var session = new TranslationSession(providers, new TranslationSessionOptions(Config, expanded, () => $"a{Interlocked.Increment(ref ids)}"), new InvocationScheduler(new SchedulerLimits()), clock, new FixedJitter(0), usage);
        var patches = new List<CardPatch>();
        session.CardChanged += p => { lock (patches) patches.Add(p); };
        return (session, clock, usage, patches);
    }

    private static async Task<CardSnapshot> CardOf(TranslationSession session, string serviceId) => (await session.SnapshotAsync()).Cards.Single(c => c.ServiceId == serviceId);

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

    [Fact] // default expansion: only the first N cards request
    public async Task Default_expanded_cards_request_and_collapsed_ones_wait()
    {
        var a = new ScriptedProvider("a", ScriptedProvider.Generous, new Step.Echo("A:"));
        var b = new ScriptedProvider("b", ScriptedProvider.Generous, new Step.Echo("B:"));
        var c = new ScriptedProvider("c", ScriptedProvider.Generous, new Step.Echo("C:"));
        var (session, _, _, _) = Create(2, a, b, c);
        await session.SubmitAsync("hello", "en", "zh-Hans");
        await session.IdleAsync();
        var snapshot = await session.SnapshotAsync();
        Assert.Equal([CardState.Ready, CardState.Ready, CardState.CollapsedIdle], snapshot.Cards.Select(x => x.State));
        Assert.Empty(c.Calls);
        await session.ToggleAsync("c");
        await session.IdleAsync();
        Assert.Equal("C:hello", (await CardOf(session, "c")).Text);
    }

    [Fact] // J01
    public async Task Late_results_of_an_older_generation_never_overwrite_the_current_one()
    {
        var release = new TaskCompletionSource();
        var provider = new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Gate(release, "OLD"), new Step.Succeed("NEW"));
        var (session, _, _, patches) = Create(1, provider);
        await session.SubmitAsync("first", "en", "zh-Hans");
        await Until(async () => provider.Calls.Count == 1);
        await session.SubmitAsync("second", "en", "zh-Hans");
        await Until(async () => (await CardOf(session, "svc")).Text == "NEW");
        release.TrySetResult(); // old call was cancelled; even if it completed it is stale
        await session.IdleAsync();
        var card = await CardOf(session, "svc");
        Assert.Equal((CardState.Ready, "NEW"), (card.State, card.Text));
        Assert.Equal(2, (await session.SnapshotAsync()).Generation);
        Assert.Single(patches, p => p.Card.State == CardState.Ready); // terminal committed once
    }

    [Fact] // J02
    public async Task Partial_stream_then_network_failure_retries_once_with_reset_and_per_attempt_usage()
    {
        var provider = new ScriptedProvider("ai", ScriptedProvider.Generous,
            new Step.StreamThenFail(["Hel", "lo "], new ProviderError(ErrorKind.Network)),
            new Step.Stream(["你", "好"], "你好"));
        var (session, clock, usage, patches) = Create(1, provider);
        await session.SubmitAsync("hello", "en", "zh-Hans");
        await Until(async () => (await CardOf(session, "ai")).State == CardState.Ready, clock, 100);
        await session.IdleAsync();
        Assert.Equal("你好", (await CardOf(session, "ai")).Text); // never "Hello 你好"
        Assert.Equal(2, provider.Calls.Count);
        Assert.Contains(patches, p => p.Card.State == CardState.Loading && p.Card.Text == ""); // reset before the new stream
        var records = usage.Records.ToArray();
        Assert.Equal(2, records.Select(r => r.Attempt).Distinct().Count());
        Assert.Equal(records.Length, records.Select(r => (r.Attempt, r.Metric)).Distinct().Count());
    }

    [Fact] // J02
    public async Task A_second_failure_is_final_no_third_attempt()
    {
        var provider = new ScriptedProvider("ai", ScriptedProvider.Generous, new Step.Fail(new ProviderError(ErrorKind.Timeout)));
        var (session, clock, _, _) = Create(1, provider);
        await session.SubmitAsync("hello", "en", "zh-Hans");
        await Until(async () => (await CardOf(session, "ai")).State == CardState.Failed, clock, 100);
        await session.IdleAsync();
        Assert.Equal(2, provider.Calls.Count);
        Assert.Equal(ErrorKind.Timeout, (await CardOf(session, "ai")).Error);
    }

    [Fact] // J03
    public async Task Collapsing_a_loading_card_cancels_its_call_and_expanding_requests_again()
    {
        var provider = new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Hang(), new Step.Succeed("done"));
        var (session, _, _, _) = Create(1, provider);
        await session.SubmitAsync("hello", "en", "zh-Hans");
        await Until(async () => provider.Calls.Count == 1);
        await session.ToggleAsync("svc");
        await Until(async () => provider.Cancellations == 1);
        var collapsed = await CardOf(session, "svc");
        Assert.Equal((CardState.Cancelled, true, ""), (collapsed.State, collapsed.Collapsed, collapsed.Text));
        await session.ToggleAsync("svc");
        await session.IdleAsync();
        Assert.Equal("done", (await CardOf(session, "svc")).Text);
        await session.ToggleAsync("svc");
        await session.ToggleAsync("svc");
        await session.IdleAsync();
        Assert.Equal(2, provider.Calls.Count); // completed result reused
    }

    [Fact] // J05
    public async Task Rate_limit_waits_for_retry_after_and_long_waits_are_not_automatic()
    {
        var polite = new ScriptedProvider("p", ScriptedProvider.Generous, new Step.Fail(new ProviderError(ErrorKind.RateLimited, RetryAfter: TimeSpan.FromSeconds(5))), new Step.Succeed("ok"));
        var (session, clock, _, _) = Create(1, polite);
        await session.SubmitAsync("hello", "en", "zh-Hans");
        await Until(async () => clock.PendingTimers > 0 && polite.Calls.Count == 1);
        clock.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Single(polite.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Until(async () => (await CardOf(session, "p")).State == CardState.Ready);

        var rude = new ScriptedProvider("r", ScriptedProvider.Generous, new Step.Fail(new ProviderError(ErrorKind.RateLimited, RetryAfter: TimeSpan.FromSeconds(120))));
        var (other, _, _, _) = Create(1, rude);
        await other.SubmitAsync("hello", "en", "zh-Hans");
        await other.IdleAsync();
        Assert.Equal((CardState.Failed, ErrorKind.RateLimited), ((await CardOf(other, "r")).State, (await CardOf(other, "r")).Error));
        Assert.Single(rude.Calls);
    }

    [Fact] // J05
    public async Task Auth_failures_are_not_retried()
    {
        var provider = new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Fail(new ProviderError(ErrorKind.Auth)));
        var (session, _, _, _) = Create(1, provider);
        await session.SubmitAsync("hello", "en", "zh-Hans");
        await session.IdleAsync();
        Assert.Single(provider.Calls);
        Assert.Equal(ErrorKind.Auth, (await CardOf(session, "svc")).Error);
    }

    [Fact] // T01/T02: service limits applied in the shared session path, results in original order
    public async Task Long_text_is_chunked_by_service_limits_and_merged_in_order()
    {
        var provider = new ScriptedProvider("mymemory", ScriptedProvider.MyMemory, new Step.Echo(""));
        var (session, _, _, patches) = Create(1, provider);
        string text = string.Join(" ", Enumerable.Range(1, 150).Select(i => $"w{i:000}")); // ~749 bytes
        await session.SubmitAsync(text, "en", "zh-Hans");
        await session.IdleAsync();
        var calls = provider.Calls.ToArray();
        Assert.True(calls.Length >= 2);
        Assert.All(calls, c => Assert.True(System.Text.Encoding.UTF8.GetByteCount(c.Text) <= 500));
        Assert.Equal(text, string.Concat(calls.Select(c => c.Text)));
        var card = await CardOf(session, "mymemory");
        Assert.Equal(text, card.Text);
        Assert.True(card.Chunked);
        Assert.Contains(patches, p => p.Card.State == CardState.Streaming); // in-order progress
    }

    [Fact]
    public async Task Unsupported_language_pairs_are_marked_without_a_call()
    {
        var provider = new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Succeed("x")) { Pairs = (from, to) => from == "en" };
        var (session, _, _, _) = Create(1, provider);
        await session.SubmitAsync("你好", "zh-Hans", "en");
        await session.IdleAsync();
        Assert.Equal(CardState.Unsupported, (await CardOf(session, "svc")).State);
        Assert.Empty(provider.Calls);
    }

    [Fact] // J04 feedback path: a full queue is shown as busy, not dropped
    public async Task Full_scheduler_queue_fails_the_card_as_busy()
    {
        var clock = new ManualClock();
        var scheduler = new InvocationScheduler(new SchedulerLimits(Global: 1, QueueCapacity: 0));
        using var hog = await scheduler.AcquireAsync(new InvocationTicket("other", "k", InvocationPriority.Interactive), CancellationToken.None);
        var provider = new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Succeed("x"));
        var session = new TranslationSession([provider], new TranslationSessionOptions(Config, 1), scheduler, clock, new FixedJitter(), new RecordingUsage());
        await session.SubmitAsync("hello", "en", "zh-Hans");
        await session.IdleAsync();
        Assert.Equal(ErrorKind.Busy, (await CardOf(session, "svc")).Error);
    }

    [Fact] // J03: close cancels unfinished work within the cleanup bound
    public async Task Close_cancels_active_calls_and_ignores_new_input()
    {
        var provider = new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Hang());
        var (session, _, _, _) = Create(1, provider);
        await session.SubmitAsync("hello", "en", "zh-Hans");
        await Until(async () => provider.Calls.Count == 1);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await session.CloseAsync(TimeSpan.FromSeconds(3));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Equal(1, provider.Cancellations);
        await session.SubmitAsync("again", "en", "zh-Hans");
        await session.IdleAsync();
        Assert.Single(provider.Calls);
    }
}

public class FeatureRegistryTests
{
    [Fact] // UI06 logic: no clickable entry for unimplemented or unserviceable features
    public void Only_available_features_with_ready_services_become_entry_points()
    {
        var registry = new FeatureRegistry();
        registry.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
        registry.Register(new FeatureDescriptor(FeatureRegistry.Ids.Ocr, FeatureState.Available, null, [Capability.Ocr, Capability.Translate]));
        registry.Register(new FeatureDescriptor(FeatureRegistry.Ids.Transcription, FeatureState.InDevelopment, "feature.inDevelopment", [Capability.Asr]));
        Func<Capability, bool> ready = c => c == Capability.Translate;
        Assert.Equal([FeatureRegistry.Ids.InputTranslation], registry.EntryPoints(ready, developmentBuild: false));
        Assert.Equal((FeatureState.Unavailable, "feature.noService.ocr"), registry.Resolve(FeatureRegistry.Ids.Ocr, ready));
        Assert.Contains(FeatureRegistry.Ids.Transcription, registry.EntryPoints(ready, developmentBuild: true));
        Assert.Equal(FeatureState.Unavailable, registry.Resolve("voice", ready).State);
        Assert.Throws<InvalidOperationException>(() => registry.Register(new FeatureDescriptor(FeatureRegistry.Ids.Ocr, FeatureState.Available, null, [])));
    }
}
