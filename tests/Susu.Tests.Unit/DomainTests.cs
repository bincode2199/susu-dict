using System.Text;
using Susu.Contracts;
using Susu.Domain;
using Xunit;

namespace Susu.Tests.Unit;

public class ChunkingTests
{
    private static readonly TranslationLimits MyMemory = new(InputUnit.Utf8Bytes, 500, BatchMode.Single, 1, 500);

    private static void AssertValid(string text, IReadOnlyList<TextChunk> chunks, TranslationLimits limits)
    {
        Assert.Equal(text, string.Concat(chunks.Select(c => c.Text)));
        int position = 0;
        foreach (var chunk in chunks)
        {
            Assert.Equal(position, chunk.Start);
            Assert.True(limits.Measure(chunk.Text) <= limits.MaxInput, $"chunk {chunk.Index} measures {limits.Measure(chunk.Text)}");
            Assert.False(char.IsHighSurrogate(chunk.Text[^1]), "chunk ends inside a surrogate pair");
            Assert.False(char.IsLowSurrogate(chunk.Text[0]), "chunk starts inside a surrogate pair");
            position += chunk.Length;
        }
    }

    [Fact] // T01
    public void Exactly_500_ascii_bytes_is_one_chunk_and_501_is_two()
    {
        Assert.Single(TextChunker.Split(new string('a', 500), MyMemory));
        var text = new string('a', 501);
        var chunks = TextChunker.Split(text, MyMemory);
        Assert.True(chunks.Count >= 2);
        AssertValid(text, chunks, MyMemory);
    }

    [Fact] // T01
    public void Chinese_166_characters_fit_and_167_split()
    {
        Assert.Single(TextChunker.Split(new string('汉', 166), MyMemory)); // 498 UTF-8 bytes
        var text = new string('汉', 167);                                    // 501 UTF-8 bytes
        var chunks = TextChunker.Split(text, MyMemory);
        Assert.True(chunks.Count >= 2);
        AssertValid(text, chunks, MyMemory);
    }

    [Fact] // T01
    public void Emoji_and_newlines_are_never_cut_or_lost()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < 300; i++) builder.Append(i % 7 == 0 ? "\n" : i % 3 == 0 ? "😀" : "字a");
        string text = builder.ToString();
        var chunks = TextChunker.Split(text, MyMemory);
        AssertValid(text, chunks, MyMemory);
    }

    [Fact]
    public void Paragraph_then_sentence_then_space_boundaries_are_preferred()
    {
        string paragraph = new string('x', 300) + "\n" + new string('y', 300);
        var p = TextChunker.Split(paragraph, MyMemory);
        Assert.EndsWith("\n", p[0].Text);
        Assert.False(p[0].HardSplit);

        string sentences = string.Join(" ", Enumerable.Repeat("This is a sentence.", 40));
        var s = TextChunker.Split(sentences, MyMemory);
        Assert.EndsWith(". ", s[0].Text);

        string cjk = string.Concat(Enumerable.Repeat("这是一个句子。", 30));
        var c = TextChunker.Split(cjk, MyMemory);
        Assert.EndsWith("。", c[0].Text);

        var hard = TextChunker.Split(new string('z', 1200), MyMemory);
        Assert.All(hard.Take(hard.Count - 1), x => Assert.True(x.HardSplit));
    }

    [Fact]
    public void Unicode_scalar_limits_count_supplementary_characters_once()
    {
        var limits = new TranslationLimits(InputUnit.UnicodeScalars, 10, BatchMode.Single, 1, 10);
        string text = string.Concat(Enumerable.Repeat("😀", 25));
        var chunks = TextChunker.Split(text, limits);
        Assert.Equal(3, chunks.Count);
        AssertValid(text, chunks, limits);
    }

    [Fact]
    public void Invalid_limit_declarations_are_reported()
    {
        Assert.NotNull(new TranslationLimits(InputUnit.Utf8Bytes, 0, BatchMode.Single, 1, 1).Validate());
        Assert.NotNull(new TranslationLimits(InputUnit.Utf8Bytes, 10, BatchMode.Single, 5, 10).Validate());
        Assert.Null(MyMemory.Validate());
    }
}

public class SubtitleTests
{
    private static readonly TranslationLimits Items = new(InputUnit.UnicodeScalars, 40, BatchMode.Items, 50, 2000);

    private static List<SubtitleSegment> Twenty() => [.. Enumerable.Range(1, 20).Select(i => new SubtitleSegment($"s{i}", i, i + 0.9, $"line {i}\nsecond line {i}"))];

    [Fact] // T03
    public void Out_of_order_results_are_matched_by_id_not_by_line()
    {
        var segments = Twenty();
        var parts = SubtitleMapper.ToParts(segments, Items);
        var returned = parts.Reverse().Select(p => (p.Id, (string?)$"T[{p.Text}]")).ToList();
        Assert.Equal(BatchProblem.None, SubtitleMapper.Validate(parts, returned.Select(r => ((string?)r.Id, r.Item2)).ToList()));
        var merged = SubtitleMapper.Merge(segments, parts, returned.ToDictionary(r => r.Id, r => r.Item2!));
        Assert.Equal(20, merged.Count);
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(segments[i].Start, merged[i].Start);
            Assert.Equal($"T[{segments[i].Text}]", merged[i].Text);
        }
    }

    [Fact] // T04
    public void Missing_duplicate_extra_and_null_results_invalidate_the_batch()
    {
        var parts = SubtitleMapper.ToParts(Twenty().Take(3), Items);
        var ok = parts.Select(p => ((string?)p.Id, (string?)"x")).ToList();
        Assert.Equal(BatchProblem.Missing, SubtitleMapper.Validate(parts, ok.Take(2).ToList()));
        Assert.Equal(BatchProblem.Duplicate, SubtitleMapper.Validate(parts, [.. ok, ok[0]]));
        Assert.Equal(BatchProblem.Extra, SubtitleMapper.Validate(parts, [.. ok, ("s99#0", "x")]));
        Assert.Equal(BatchProblem.Extra, SubtitleMapper.Validate(parts, [.. ok.Take(2), (null, "x")]));
        Assert.Equal(BatchProblem.InvalidText, SubtitleMapper.Validate(parts, [.. ok.Take(2), (ok[2].Item1, null)]));
    }

    [Fact] // T05
    public void Long_cues_are_split_into_parts_and_merged_with_original_timing()
    {
        var single = new TranslationLimits(InputUnit.UnicodeScalars, 10, BatchMode.Single, 1, 10);
        var cue = new SubtitleSegment("s1", 12.5, 15.25, "abcdefghij klmnopqrst uvwxyz");
        var parts = SubtitleMapper.ToParts([cue], single);
        Assert.True(parts.Count >= 3);
        Assert.All(parts, p => Assert.True(single.Measure(p.Text) <= 10));
        var merged = SubtitleMapper.Merge([cue], parts, parts.ToDictionary(p => p.Id, p => p.Text.ToUpperInvariant()));
        Assert.Equal(cue with { Text = cue.Text.ToUpperInvariant() }, merged.Single());
        Assert.All(SubtitleMapper.Batches(parts, single), b => Assert.Single(b)); // single-mode engine: one item per call
    }

    [Fact]
    public void Incomplete_segments_are_not_merged()
    {
        var parts = SubtitleMapper.ToParts(Twenty().Take(2), Items);
        var merged = SubtitleMapper.Merge(Twenty().Take(2).ToList(), parts, new Dictionary<string, string> { [parts[0].Id] = "only" });
        Assert.Single(merged);
    }

    [Fact]
    public void Video_batches_respect_the_soft_cap_and_service_limits()
    {
        var parts = SubtitleMapper.ToParts(Enumerable.Range(1, 55).Select(i => new SubtitleSegment($"s{i}", i, i + 1, new string('a', 30))), Items);
        var batches = SubtitleMapper.Batches(parts, Items);
        Assert.All(batches, b => { Assert.True(b.Count <= 20); Assert.True(b.Sum(p => p.Text.Length) <= 1500); });
        Assert.Equal(parts.Count, batches.Sum(b => b.Count));
    }
}

public class LanguageTests
{
    [Theory]
    [InlineData("hello world", "en")]
    [InlineData("你好世界", "zh-Hans")]
    [InlineData("Hello 世界", null)]
    [InlineData("12345 !?", null)]
    public void Script_fast_path(string text, string? expected) => Assert.Equal(expected, ScriptDetector.Detect(text));

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-Hant", null)]
    [InlineData("ja", null)]
    public void Detector_names_map_to_canonical_codes(string name, string? expected) => Assert.Equal(expected, Languages.FromDetector(name));

    [Fact]
    public void Only_canonical_codes_are_accepted()
    {
        Assert.True(Languages.IsCanonical("zh-Hans"));
        Assert.False(Languages.IsCanonical("zh"));
        Assert.Throws<ArgumentException>(() => Languages.Get("EN"));
    }
}

public class ServiceResolutionTests
{
    private static ProviderInstance Instance(string id, Capability[] caps, string[] secrets, Dictionary<string, string>? bindings = null)
        => new(id, ProviderKind.Plugin, id, caps.ToHashSet(), secrets, bindings ?? [], 1);

    [Fact]
    public void Availability_is_resolved_per_capability_and_credential()
    {
        var instances = new Dictionary<string, ProviderInstance>
        {
            ["mymemory"] = Instance("mymemory", [Capability.Translate], []),
            ["deepl"] = Instance("deepl", [Capability.Translate], ["apiKey"]),
            ["openai"] = Instance("openai", [Capability.Translate, Capability.Asr], ["apiKey"], new() { ["apiKey"] = "acct-openai" }),
            ["youdao"] = Instance("youdao", [Capability.Translate, Capability.Dictionary], ["appKey"], new() { ["appKey"] = "acct-youdao" }),
        };
        var bindings = new[]
        {
            new ServiceBinding("openai", Capability.Translate, true, "b"),
            new ServiceBinding("mymemory", Capability.Translate, true, "a"),
            new ServiceBinding("deepl", Capability.Translate, true, "c"),
            new ServiceBinding("youdao", Capability.Translate, false, "d"),
            new ServiceBinding("mymemory", Capability.Ocr, true, "e"),
        };
        var resolved = CapabilityResolver.Resolve(Capability.Translate, bindings, instances, (account, _) => account == "acct-openai", id => id == "never");
        Assert.Equal(["mymemory", "openai", "deepl", "youdao"], resolved.Select(r => r.Instance.InstanceId));
        Assert.Equal([Availability.Ready, Availability.Ready, Availability.MissingCredential, Availability.Disabled], resolved.Select(r => r.Availability));
        Assert.Equal(Availability.UnsupportedCapability, CapabilityResolver.Resolve(Capability.Ocr, bindings, instances, (_, _) => true).Single().Availability);
    }

    [Fact]
    public void Page_local_reorder_keeps_other_page_slots()
    {
        // translate engines: t1,t2,t3 ; AI: a1,a2 interleaved in one translationOrder
        string[] order = ["t1", "a1", "t2", "a2", "t3"];
        var result = CapabilityResolver.ReorderWithinPage(order, id => id.StartsWith('t'), "t3", 0);
        Assert.Equal(["t3", "a1", "t1", "a2", "t2"], result);
        var ai = CapabilityResolver.ReorderWithinPage(order, id => id.StartsWith('a'), "a1", 1);
        Assert.Equal(["t1", "a2", "t2", "a1", "t3"], ai);
    }

    [Theory]
    [InlineData(Capability.Translate, null, 30)]
    [InlineData(Capability.Ocr, null, 60)]
    [InlineData(Capability.Asr, null, 300)]
    [InlineData(Capability.Translate, 900, 600)]
    [InlineData(Capability.Translate, 45, 45)]
    public void Effective_timeouts_are_bounded_by_the_host_ceiling(Capability capability, int? overrideSeconds, int expected)
        => Assert.Equal(TimeSpan.FromSeconds(expected), ConfigSnapshot.EffectiveTimeout(capability, overrideSeconds is null ? null : TimeSpan.FromSeconds(overrideSeconds.Value)));
}

public class JobStateMachineTests
{
    [Fact]
    public void Terminal_states_accept_no_further_transition()
    {
        foreach (var terminal in new[] { JobState.Completed, JobState.CompletedWithErrors, JobState.Failed, JobState.Cancelled })
            foreach (var target in Enum.GetValues<JobState>())
                Assert.False(JobStateMachine.CanTransition(terminal, target, supportsPause: true));
    }

    [Fact]
    public void Pause_is_only_available_to_pausable_pipelines()
    {
        Assert.True(JobStateMachine.CanTransition(JobState.Running, JobState.Pausing, supportsPause: true));
        Assert.False(JobStateMachine.CanTransition(JobState.Running, JobState.Pausing, supportsPause: false));
        Assert.Equal(JobState.Queued, JobStateMachine.Transition(JobState.Paused, JobState.Queued, true));
        Assert.Throws<InvalidOperationException>(() => JobStateMachine.Transition(JobState.Queued, JobState.Completed, true));
    }
}

public class RetryPolicyTests
{
    private static readonly TimeSpan Plenty = TimeSpan.FromSeconds(30);

    [Theory] // J05
    [InlineData(ErrorKind.Network)]
    [InlineData(ErrorKind.Timeout)]
    public void Transient_errors_retry_once_with_bounded_jitter(ErrorKind kind)
    {
        var first = Assert.IsType<RetryDecision.Retry>(RetryPolicy.Decide(new ProviderError(kind), 1, Plenty, false, true, 1.0));
        Assert.Equal(TimeSpan.FromMilliseconds(750), first.Delay);
        Assert.IsType<RetryDecision.Stop>(RetryPolicy.Decide(new ProviderError(kind), 2, Plenty, false, true, 0));
    }

    [Theory] // J05
    [InlineData(ErrorKind.Auth)]
    [InlineData(ErrorKind.Quota)]
    [InlineData(ErrorKind.UnsupportedLanguage)]
    [InlineData(ErrorKind.BadResponse)]
    [InlineData(ErrorKind.Cancelled)]
    [InlineData(ErrorKind.Busy)]
    public void Non_transient_errors_are_never_retried(ErrorKind kind)
        => Assert.IsType<RetryDecision.Stop>(RetryPolicy.Decide(new ProviderError(kind), 1, Plenty, false, true, 0));

    [Fact] // J05
    public void Rate_limit_honours_retry_after_up_to_sixty_seconds()
    {
        var wait = Assert.IsType<RetryDecision.Retry>(RetryPolicy.Decide(new ProviderError(ErrorKind.RateLimited, RetryAfter: TimeSpan.FromSeconds(12)), 1, TimeSpan.FromSeconds(100), false, true, 0));
        Assert.Equal(TimeSpan.FromSeconds(12), wait.Delay);
        Assert.IsType<RetryDecision.Stop>(RetryPolicy.Decide(new ProviderError(ErrorKind.RateLimited, RetryAfter: TimeSpan.FromSeconds(61)), 1, TimeSpan.FromSeconds(100), false, true, 0));
        Assert.Equal(RetryPolicy.DefaultRateLimitDelay, Assert.IsType<RetryDecision.Retry>(RetryPolicy.Decide(new ProviderError(ErrorKind.RateLimited), 1, Plenty, false, true, 0)).Delay);
    }

    [Fact] // J02/J05: attempts share one deadline
    public void No_retry_when_the_shared_deadline_cannot_fit_the_delay()
        => Assert.IsType<RetryDecision.Stop>(RetryPolicy.Decide(new ProviderError(ErrorKind.Network), 1, TimeSpan.FromMilliseconds(400), false, true, 0));

    [Fact]
    public void Partial_streams_retry_only_with_a_reset_and_writes_never_retry()
    {
        Assert.True(Assert.IsType<RetryDecision.Retry>(RetryPolicy.Decide(new ProviderError(ErrorKind.Network), 1, Plenty, true, true, 0)).ResetStream);
        Assert.IsType<RetryDecision.Stop>(RetryPolicy.Decide(new ProviderError(ErrorKind.Network), 1, Plenty, false, readOnly: false, 0));
    }

    [Fact] // J05
    public void Retry_after_parsing_covers_seconds_dates_invalid_and_huge_values()
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(30), RetryPolicy.ParseRetryAfter("30", now));
        Assert.Equal(TimeSpan.FromSeconds(90), RetryPolicy.ParseRetryAfter("Wed, 23 Sep 2026 12:01:30 GMT", now));
        Assert.Equal(TimeSpan.Zero, RetryPolicy.ParseRetryAfter("Wed, 23 Sep 2026 11:00:00 GMT", now));
        Assert.Null(RetryPolicy.ParseRetryAfter("soon", now));
        Assert.Null(RetryPolicy.ParseRetryAfter("-5", now));
        Assert.Null(RetryPolicy.ParseRetryAfter("", now));
        Assert.Equal(TimeSpan.MaxValue, RetryPolicy.ParseRetryAfter("99999999999999999999", now));
    }
}

public class RuntimeRegistryTests
{
    [Fact] // J06
    public void Disabling_one_capability_keeps_the_runtime_for_the_other()
    {
        var registry = new RuntimeRegistry();
        Assert.Single(registry.Enable("youdao", Capability.Translate), e => e is RuntimeRegistry.Effect.Load);
        Assert.Empty(registry.Enable("youdao", Capability.Dictionary));
        Assert.Null(registry.Begin("youdao", Capability.Translate, "c1"));
        Assert.Null(registry.Begin("youdao", Capability.Dictionary, "c2"));
        var effects = registry.Disable("youdao", Capability.Dictionary);
        Assert.Equal([new RuntimeRegistry.Effect.FailCall("c2", ErrorKind.Cancelled)], effects);
        Assert.True(registry.IsLoaded("youdao"));
        Assert.Equal(1, registry.InFlight("youdao"));
        Assert.Equal(ErrorKind.Unavailable, registry.Begin("youdao", Capability.Dictionary, "c3"));
    }

    [Fact] // J06
    public void Forced_rebuild_fails_every_call_of_that_package_only()
    {
        var registry = new RuntimeRegistry();
        registry.Enable("a", Capability.Translate);
        registry.Enable("b", Capability.Translate);
        registry.Begin("a", Capability.Translate, "a1");
        registry.Begin("a", Capability.Translate, "a2");
        registry.Begin("b", Capability.Translate, "b1");
        var effects = registry.Rebuild("a");
        Assert.Equal(2, effects.Count(e => e is RuntimeRegistry.Effect.FailCall { Kind: ErrorKind.Timeout }));
        Assert.Contains(new RuntimeRegistry.Effect.Load("a"), effects);
        Assert.Equal(1, registry.InFlight("b"));
        Assert.True(registry.IsLoaded("a"));
    }

    [Fact]
    public void Last_disable_unloads_and_the_in_flight_limit_is_two()
    {
        var registry = new RuntimeRegistry();
        registry.Enable("p", Capability.Translate);
        Assert.Null(registry.Begin("p", Capability.Translate, "1"));
        Assert.Null(registry.Begin("p", Capability.Translate, "2"));
        Assert.Equal(ErrorKind.Busy, registry.Begin("p", Capability.Translate, "3"));
        var effects = registry.Disable("p", Capability.Translate);
        Assert.Contains(new RuntimeRegistry.Effect.Unload("p"), effects);
        Assert.False(registry.IsLoaded("p"));
    }
}

public class CardReducerTests
{
    private static (Card, IReadOnlyList<CardEffect>) R(Card c, CardEvent e) => CardReducer.Reduce(c, e);

    [Fact] // J01
    public void Only_the_current_generation_and_attempt_update_a_card()
    {
        var (card, effects) = R(Card.Create("svc", false), new CardEvent.NewGeneration(1, "a1"));
        Assert.Single(effects);
        (card, _) = R(card, new CardEvent.Started(new(1, "a1", 1)));
        (card, effects) = R(card, new CardEvent.NewGeneration(2, "a2")); // new source text
        Assert.Contains(effects, e => e is CardEffect.Cancel { AttemptId: "a1" });
        (card, _) = R(card, new CardEvent.Completed(new(1, "a1", 2), "stale")); // late result of old generation
        Assert.Equal(CardState.Queued, card.State);
        (card, _) = R(card, new CardEvent.Completed(new(2, "zz", 1), "wrong attempt"));
        Assert.Equal("", card.Text);
        (card, _) = R(card, new CardEvent.Completed(new(2, "a2", 1), "fresh"));
        Assert.Equal((CardState.Ready, "fresh"), (card.State, card.Text));
        (card, _) = R(card, new CardEvent.Failed(new(2, "a2", 2), ErrorKind.Network)); // duplicate terminal
        Assert.Equal((CardState.Ready, "fresh"), (card.State, card.Text));
    }

    [Fact]
    public void Sequences_must_increase_and_duplicates_are_dropped()
    {
        var (card, _) = R(Card.Create("svc", false), new CardEvent.NewGeneration(1, "a"));
        (card, _) = R(card, new CardEvent.Chunk(new(1, "a", 1), "A"));
        (card, _) = R(card, new CardEvent.Chunk(new(1, "a", 1), "A"));
        (card, _) = R(card, new CardEvent.Chunk(new(1, "a", 3), "C"));
        (card, _) = R(card, new CardEvent.Chunk(new(1, "a", 2), "B"));
        Assert.Equal("AC", card.Text);
        Assert.Equal(CardState.Streaming, card.State);
    }

    [Fact] // J03
    public void Collapse_cancels_unfinished_work_and_reexpand_reuses_completed_results()
    {
        var (card, _) = R(Card.Create("svc", false), new CardEvent.NewGeneration(1, "a1"));
        (card, _) = R(card, new CardEvent.Chunk(new(1, "a1", 1), "partial"));
        var (collapsed, effects) = R(card, new CardEvent.Collapse());
        Assert.Equal((CardState.Cancelled, "", true), (collapsed.State, collapsed.Text, collapsed.Collapsed));
        Assert.Contains(effects, e => e is CardEffect.Cancel { AttemptId: "a1" });
        var (expanded, requests) = R(collapsed, new CardEvent.Expand("a2"));
        Assert.Equal(CardState.Queued, expanded.State);
        Assert.Contains(requests, e => e is CardEffect.Request { AttemptId: "a2" });

        var (done, _) = R(expanded, new CardEvent.Completed(new(1, "a2", 1), "complete"));
        var (again, _) = R(done, new CardEvent.Collapse());
        var (reopened, none) = R(again, new CardEvent.Expand("a3"));
        Assert.Empty(none); // completed result reused within its generation
        Assert.Equal((CardState.Ready, "complete"), (reopened.State, reopened.Text));
    }

    [Fact]
    public void Collapsed_cards_do_not_request_until_first_expanded()
    {
        var (card, effects) = R(Card.Create("svc", collapsed: true), new CardEvent.NewGeneration(1, "a1"));
        Assert.Empty(effects);
        Assert.Equal(CardState.CollapsedIdle, card.State);
        (card, effects) = R(card, new CardEvent.Expand("a2"));
        Assert.Single(effects);
    }

    [Fact] // J02
    public void Reset_replaces_the_partial_stream_of_the_previous_attempt()
    {
        var (card, _) = R(Card.Create("svc", false), new CardEvent.NewGeneration(1, "a1"));
        (card, _) = R(card, new CardEvent.Chunk(new(1, "a1", 1), "old partial"));
        (card, _) = R(card, new CardEvent.Reset(new(1, "a2", 0)));
        Assert.Equal("", card.Text);
        (card, _) = R(card, new CardEvent.Chunk(new(1, "a1", 5), "late old"));
        (card, _) = R(card, new CardEvent.Chunk(new(1, "a2", 1), "new"));
        Assert.Equal("new", card.Text);
    }

    [Fact]
    public void Unsupported_language_collapses_and_manual_retry_is_a_new_attempt()
    {
        var (card, _) = R(Card.Create("svc", false), new CardEvent.NewGeneration(1, "a1"));
        (card, _) = R(card, new CardEvent.Failed(new(1, "a1", 1), ErrorKind.UnsupportedLanguage));
        Assert.Equal((CardState.Unsupported, true), (card.State, card.Collapsed));
        var (other, _) = R(Card.Create("x", false), new CardEvent.NewGeneration(1, "b1"));
        (other, _) = R(other, new CardEvent.Failed(new(1, "b1", 1), ErrorKind.Network));
        var (retried, effects) = R(other, new CardEvent.ManualRetry("b2"));
        Assert.Equal(CardState.Queued, retried.State);
        Assert.Contains(effects, e => e is CardEffect.Request { AttemptId: "b2" });
    }
}
