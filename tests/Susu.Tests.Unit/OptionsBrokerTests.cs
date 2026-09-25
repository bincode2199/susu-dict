using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Plugins;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>F07.2 CFG02: dynamic options paging, 10 s timeout, 200-item cap, 5-minute cache and stale revisions (deterministic clock).</summary>
public class OptionsBrokerTests
{
    private static OptionItem[] Items(int count, string prefix = "m") => [.. Enumerable.Range(0, count).Select(i => new OptionItem($"{prefix}{i}", $"{prefix}{i}"))];

    private sealed class Loader
    {
        public readonly List<OptionsQuery> Calls = [];
        public readonly List<CancellationToken> Tokens = [];
        public Func<OptionsQuery, Task<OptionsLoad>> Answer = q => Task.FromResult(new OptionsLoad(Items(3)));
        public Task<OptionsLoad> Invoke(OptionsQuery q, CancellationToken ct) { Calls.Add(q); Tokens.Add(ct); return Answer(q); }
    }

    private static (OptionsBroker Broker, Loader Loader, ManualClock Clock) Rig()
    {
        var loader = new Loader();
        var clock = new ManualClock();
        return (new OptionsBroker(loader.Invoke, clock), loader, clock);
    }

    private static OptionsQuery Q(long revision, string? cursor = null) => new("openai", "model", "options", revision, cursor);

    [Fact]
    public async Task Results_are_cached_for_five_minutes_per_revision_and_cursor()
    {
        var (broker, loader, clock) = Rig();
        long rev = broker.Track("openai", "model", "fp-1");
        var first = Assert.IsType<OptionsOutcome.Loaded>(await broker.LoadAsync(Q(rev), cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(first.Cached);
        clock.Advance(TimeSpan.FromMinutes(4));
        var second = Assert.IsType<OptionsOutcome.Loaded>(await broker.LoadAsync(Q(rev), cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(second.Cached);
        Assert.Single(loader.Calls);
        // Another page is its own entry.
        await broker.LoadAsync(Q(rev, "200"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, loader.Calls.Count);
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromMilliseconds(1));
        var expired = Assert.IsType<OptionsOutcome.Loaded>(await broker.LoadAsync(Q(rev), cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(expired.Cached);
        Assert.Equal(3, loader.Calls.Count);
        // Refresh bypasses a live entry.
        await broker.LoadAsync(Q(rev), refresh: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, loader.Calls.Count);
    }

    [Fact]
    public async Task A_dependency_change_bumps_the_revision_drops_the_cache_and_refuses_the_old_revision()
    {
        var (broker, loader, _) = Rig();
        long rev1 = broker.Track("openai", "model", "fp-1");
        Assert.Equal(rev1, broker.Track("openai", "model", "fp-1")); // unchanged fingerprint: same revision
        await broker.LoadAsync(Q(rev1), cancellationToken: TestContext.Current.CancellationToken);
        long rev2 = broker.Track("openai", "model", "fp-2");
        Assert.True(rev2 > rev1);
        var stale = Assert.IsType<OptionsOutcome.Stale>(await broker.LoadAsync(Q(rev1), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(rev2, stale.CurrentRevision);
        Assert.Single(loader.Calls); // a request for the old revision never reaches the plugin
        var fresh = Assert.IsType<OptionsOutcome.Loaded>(await broker.LoadAsync(Q(rev2), cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(fresh.Cached);
    }

    [Fact]
    public async Task A_response_that_arrives_after_the_revision_changed_is_stale_and_never_cached()
    {
        var (broker, loader, _) = Rig();
        long rev1 = broker.Track("openai", "model", "account-A");
        var gate = new TaskCompletionSource<OptionsLoad>(TaskCreationOptions.RunContinuationsAsynchronously);
        loader.Answer = _ => gate.Task;
        var inFlight = broker.LoadAsync(Q(rev1), cancellationToken: TestContext.Current.CancellationToken);
        long rev2 = broker.Track("openai", "model", "account-B"); // user switched account mid-load
        gate.SetResult(new OptionsLoad(Items(2, "old")));
        var outcome = Assert.IsType<OptionsOutcome.Stale>(await inFlight);
        Assert.Equal(rev2, outcome.CurrentRevision);
        // Nothing from account A was cached under either revision.
        loader.Answer = _ => Task.FromResult(new OptionsLoad(Items(1, "new")));
        var fresh = Assert.IsType<OptionsOutcome.Loaded>(await broker.LoadAsync(Q(rev2), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(["new0"], fresh.Items.Select(i => i.Value));
        Assert.False(fresh.Cached);
    }

    [Fact]
    public async Task A_failure_for_an_old_revision_is_stale_and_credential_invalidation_covers_every_field()
    {
        var (broker, loader, _) = Rig();
        long model = broker.Track("openai", "model", "fp");
        long deck = broker.Track("openai", "deck", "fp");
        broker.Track("deepl", "model", "fp");
        var gate = new TaskCompletionSource<OptionsLoad>(TaskCreationOptions.RunContinuationsAsynchronously);
        loader.Answer = _ => gate.Task;
        var inFlight = broker.LoadAsync(Q(model), cancellationToken: TestContext.Current.CancellationToken);
        broker.Invalidate("openai"); // key rewritten
        gate.SetResult(new OptionsLoad(null, null, ErrorKind.Auth));
        Assert.IsType<OptionsOutcome.Stale>(await inFlight);
        Assert.Equal(model + 1, broker.RevisionOf("openai", "model"));
        Assert.Equal(deck + 1, broker.RevisionOf("openai", "deck"));
        Assert.Equal(1, broker.RevisionOf("deepl", "model")); // other instances untouched
    }

    [Fact]
    public async Task A_load_that_exceeds_ten_seconds_times_out_and_cancels_the_plugin_call()
    {
        var (broker, loader, clock) = Rig();
        long rev = broker.Track("openai", "model", "fp");
        var never = new TaskCompletionSource<OptionsLoad>();
        loader.Answer = _ => never.Task;
        var pending = broker.LoadAsync(Q(rev), cancellationToken: TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(9.9));
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var failed = Assert.IsType<OptionsOutcome.Failed>(await pending);
        Assert.Equal(ErrorKind.Timeout, failed.Kind);
        Assert.True(loader.Tokens.Single().IsCancellationRequested);
    }

    [Fact]
    public async Task Pages_over_200_items_and_malformed_output_are_rejected_not_truncated_or_cached()
    {
        var (broker, loader, _) = Rig();
        long rev = broker.Track("openai", "model", "fp");
        loader.Answer = _ => Task.FromResult(new OptionsLoad(Items(200), "200"));
        var page = Assert.IsType<OptionsOutcome.Loaded>(await broker.LoadAsync(Q(rev), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(200, page.Items.Count);
        Assert.Equal("200", page.NextCursor);

        foreach (var bad in new OptionsLoad[]
        {
            new(Items(201)),
            new(null),
            new([new OptionItem("a", "A"), new OptionItem("a", "again")]),
            new([new OptionItem("", "empty")]),
            new([new OptionItem("x\ny", "control")]),
            new([new OptionItem(new string('m', 513), "long")]),
            new(Items(1), "200"), // a cursor that does not move would page forever
        })
        {
            loader.Answer = _ => Task.FromResult(bad);
            var failed = Assert.IsType<OptionsOutcome.Failed>(await broker.LoadAsync(Q(rev, "200"), refresh: true, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(ErrorKind.BadResponse, failed.Kind);
        }
    }

    [Fact]
    public async Task A_plugin_error_comes_back_as_its_kind_only_and_a_throwing_loader_as_unavailable()
    {
        var (broker, loader, _) = Rig();
        long rev = broker.Track("openai", "model", "fp");
        loader.Answer = _ => Task.FromResult(new OptionsLoad(null, null, ErrorKind.Auth));
        Assert.Equal(ErrorKind.Auth, Assert.IsType<OptionsOutcome.Failed>(await broker.LoadAsync(Q(rev), cancellationToken: TestContext.Current.CancellationToken)).Kind);
        loader.Answer = _ => throw new InvalidOperationException("sk-secret-in-message");
        var failed = Assert.IsType<OptionsOutcome.Failed>(await broker.LoadAsync(Q(rev), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(ErrorKind.Unavailable, failed.Kind);
        Assert.DoesNotContain("sk-secret", failed.ToString());
    }
}

/// <summary>F07.2: the manifest <c>config</c> schema the settings controls are generated from (PLAN 4.6).</summary>
public class ConfigSchemaTests
{
    private const string Head = "id: com.example.x\nname: X\napiVersion: 1\nminHost: 1\ncapabilities: [translate]\ncredentialUse: [apiKey]\nentry: main.js\n";

    [Fact]
    public void The_shipped_openai_manifest_declares_model_options_and_a_base_url()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Susu.slnx"))) dir = dir.Parent;
        string path = Path.Combine(dir!.FullName, "src", "Susu.Host", "plugins", "openai", "manifest.yaml");
        var (manifest, issues) = PackageManifest.Parse(File.ReadAllText(path));
        Assert.Empty(issues);
        Assert.True(manifest!.DeclaresOptions);
        var model = manifest.ConfigFields.Single(f => f.Name == "model");
        Assert.Equal(OptionsSource.OptionsMethod, model.Options!.Method);
        Assert.Equal(["baseUrl", "apiKey"], model.Options.DependsOn);
        var baseUrl = manifest.ConfigFields.Single(f => f.Name == "baseUrl");
        Assert.Equal("uri", baseUrl.Format);
        Assert.Equal("advanced", baseUrl.Group);
    }

    [Fact]
    public void Types_enums_ranges_show_when_and_voices_parse()
    {
        string yaml = Head + """
            config:
              type: object
              properties:
                formality: { type: string, enum: [default, more, less], default: default }
                temperature: { type: number, minimum: 0, maximum: 2, default: 0.3 }
                stream: { type: boolean, default: "true" }
                voice: { type: string, x-susu: { optionsSource: { method: voices, dependsOn: [formality] }, showWhen: { field: stream, equals: "true" } } }
            """;
        var (manifest, issues) = PackageManifest.Parse(yaml);
        Assert.Equal("", string.Join("; ", issues));
        Assert.False(manifest!.DeclaresOptions); // voices is the TTS method, not options
        var f = manifest.ConfigFields;
        Assert.Equal(["formality", "temperature", "stream", "voice"], f.Select(x => x.Name));
        Assert.Equal(["default", "more", "less"], f[0].Enum!);
        Assert.Equal((0d, 2d), (f[1].Minimum!.Value, f[1].Maximum!.Value));
        Assert.Equal(ConfigFieldType.Boolean, f[2].Type);
        Assert.Equal(("stream", "true"), (f[3].ShowWhenField, f[3].ShowWhenEquals));
        Assert.Equal(OptionsSource.VoicesMethod, f[3].Options!.Method);
    }

    [Theory]
    [InlineData("m: { type: string, x-susu: { optionsSource: { dependsOn: [otherPackageField] } } }", "dependsOn")]
    [InlineData("m: { type: string, x-susu: { optionsSource: { dependsOn: [m] } } }", "dependsOn")]
    [InlineData("m: { type: string, x-susu: { optionsSource: { method: exec } } }", "method")]
    [InlineData("m: { type: integer, x-susu: { optionsSource: {} } }", "optionsSource")]
    [InlineData("m: { type: string, x-susu: { widget: html } }", "widget")]
    [InlineData("m: { type: string, onChange: run }", "onChange")]
    [InlineData("m: { type: object }", "type")]
    [InlineData("m: { type: string, format: email }", "format")]
    [InlineData("m: { type: string, enum: [a, b], default: c }", "default")]
    [InlineData("m: { type: string, x-susu: { showWhen: { field: nope, equals: x } } }", "showWhen")]
    [InlineData("1bad: { type: string }", "1bad")]
    public void Unsafe_or_unknown_schema_is_rejected(string property, string pathPart)
    {
        var (manifest, issues) = PackageManifest.Parse(Head + "config:\n  type: object\n  properties:\n    " + property + "\n");
        Assert.Null(manifest);
        Assert.Contains(issues, i => i.Path.Contains(pathPart));
    }

    [Theory]
    [InlineData(ConfigFieldType.String, "https://llm.example.com/v1", "uri", null)]
    [InlineData(ConfigFieldType.String, "http://127.0.0.1:11434", "uri", null)]
    [InlineData(ConfigFieldType.String, "http://llm.example.com", "uri", "uri")]
    [InlineData(ConfigFieldType.String, "https://user:pw@llm.example.com", "uri", "uri")]
    [InlineData(ConfigFieldType.String, "https://llm.example.com/?key=1", "uri", "uri")]
    [InlineData(ConfigFieldType.String, "line\nbreak", null, "format")]
    [InlineData(ConfigFieldType.Boolean, "yes", null, "type")]
    [InlineData(ConfigFieldType.Integer, "1.5", null, "type")]
    [InlineData(ConfigFieldType.Number, "NaN", null, "type")]
    [InlineData(ConfigFieldType.Number, "2.5", null, "range")]
    [InlineData(ConfigFieldType.Number, "1.5", null, null)]
    public void Host_side_value_checks(ConfigFieldType type, string value, string? format, string? expected)
    {
        var field = new ConfigField("f", type, Format: format, Minimum: 0, Maximum: 2);
        Assert.Equal(expected, ConfigSchema.Check(field, value));
    }

    [Fact]
    public void A_dynamic_field_accepts_a_value_its_list_no_longer_shows_but_a_static_enum_does_not()
    {
        Assert.Equal("enum", ConfigSchema.Check(new ConfigField("f", ConfigFieldType.String, Enum: ["a"]), "b"));
        Assert.Null(ConfigSchema.Check(new ConfigField("f", ConfigFieldType.String, Enum: ["a"], Options: new OptionsSource("options", [])), "b"));
    }
}
