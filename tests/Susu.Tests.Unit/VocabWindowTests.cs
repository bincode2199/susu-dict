using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Storage;
using Susu.Testing;
using Susu.Ui;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F15.4 card star and SetVocab with fakes (no desktop, no Anki, no Eudic): Vocab.Collect from a result window (state, favorite, unfavorite, local
/// only without a target, queued to the usable targets, content built by the host), the per-target state and reason (CFG02), the status counts,
/// Uncertain/Failed rows and their manual check, retry failed, queue existing, and the file export through the save-dialog port (DATA07/DATA08 host part).
/// </summary>
public sealed class VocabWindowTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DictionaryResult Good = new("good",
        [new Phonetic("us", "ɡʊd"), new Phonetic("uk", "ɡʊd")], [new PartOfSpeech("adj.", ["好的", "优良的"])], null, [new Example("a good day", "美好的一天")]);

    private sealed class DictionaryService(string serviceId, DictionaryResult entry) : ITranslationProvider, IDictionaryProvider
    {
        public string ServiceId => serviceId;
        public string DisplayName => "Youdao";
        public string LimiterKey => serviceId;
        public TranslationLimits Limits => ScriptedProvider.Generous;
        public bool SupportsLanguagePair(string from, string to) => true;
        public bool DictionaryEnabled => true;
        public Task<ProviderOutcome> TranslateAsync(TranslateCall call, Func<string, ValueTask> onChunk, CancellationToken cancellationToken)
            => new ScriptedProvider(serviceId, ScriptedProvider.Generous, new Step.Echo("D:")).TranslateAsync(call, onChunk, cancellationToken);
        public Task<DictionaryOutcome> LookupAsync(DictionaryCall call, CancellationToken cancellationToken) => Task.FromResult<DictionaryOutcome>(new DictionaryOutcome.Entry(entry));
    }

    private sealed class Target(string id, Func<VocabSyncRequest, Task<VocabSyncOutcome>> handler) : IVocabSyncTarget
    {
        public List<VocabSyncRequest> Requests { get; } = [];
        public string InstanceId => id;
        public bool SupportsLookup => false;
        public Task<VocabSyncOutcome> SendAsync(VocabSyncRequest request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            return handler(request);
        }
    }

    private sealed class SavePicker : IVocabSavePicker
    {
        public string? Path;
        public bool Throw;
        public readonly List<(string Name, string Format)> Asked = [];
        public Task<string?> PickAsync(string name, string format, CancellationToken ct)
        {
            Asked.Add((name, format));
            return Throw ? Task.FromException<string?>(new InvalidOperationException("dialog")) : Task.FromResult(Path);
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly TempRoot Root = new();
        public readonly FakePlatform Platform = new();
        public readonly SettingsStore Settings;
        public readonly ConfigService Config;
        public readonly ShellCoordinator Shell;
        public readonly Database Db;
        public readonly FavoritesRepository Favorites;
        public readonly VocabService? Vocab;
        public readonly Target Anki = new("ankiconnect", r => Task.FromResult<VocabSyncOutcome>(new VocabSyncOutcome.Applied("note-" + r.EntryId)));
        public readonly Target Eudic = new("eudic", r => Task.FromResult<VocabSyncOutcome>(new VocabSyncOutcome.Applied("lang:" + r.Word)));
        public readonly SavePicker Picker = new();
        private readonly ManualClock clock = new();
        private long counter;

        public Rig(bool withVocab = true, bool withExporter = true)
        {
            Settings = new SettingsStore(Root.Paths, clock);
            Config = new ConfigService(Settings, new SecretStore(Root.Paths.Secrets, new XorProtector()));
            Db = Database.Open(Root.Paths.Database);
            int ids = 0;
            Favorites = new FavoritesRepository(Db, clock, null, () => $"id{++ids}");
            var features = new FeatureRegistry();
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
            var snapshot = new ConfigSnapshot(1, 1, 1, 2, TimeSpan.FromSeconds(30));
            int attempts = 0;
            Func<AppSettings, TranslationSession?> sessions = _ => new TranslationSession([new DictionaryService("youdao/translate", Good)],
                new TranslationSessionOptions(snapshot, 2, () => $"a{Interlocked.Increment(ref attempts)}"), new InvocationScheduler(new SchedulerLimits()), new ManualClock(), new FixedJitter(0), new RecordingUsage());
            Shell = new ShellCoordinator(Platform, Config, features, c => c is Capability.Translate or Capability.Vocab, new ShellOptions(false, false), sessions, null,
                new TranslationBackend(true, (_, _) => null), null);
            if (withVocab)
            {
                Vocab = new VocabService(Favorites, new VocabSyncWorker(Favorites, id => id == "ankiconnect" ? Anki : id == "eudic" ? Eudic : null, clock), () => VocabTargets.Usable(Config.State.Effective, Config.Secrets.Has), clock,
                    withExporter ? new VocabFileExporter(new VocabExporter(Db, Favorites, clock)) : null);
                Shell.Vocab = Vocab;
                Shell.VocabSavePicker = Picker;
            }
            Shell.Start();
        }

        public void Ready(WindowKind kind) => Shell.OnPageMessage(kind, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Ready", windowSessionId = Platform.Session(kind) }));

        public CommandResult Run(WindowKind kind, string name, object payload)
        {
            string id = $"c{++counter}";
            Shell.OnPageMessage(kind, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Command", windowSessionId = Platform.Session(kind), name, correlationId = id, payload }, Web));
            for (var poll = System.Diagnostics.Stopwatch.StartNew(); poll.Elapsed < Eventually.DefaultTimeout;)
            {
                var hit = Platform.Posted.FirstOrDefault(p => p.Envelope.CorrelationId == id);
                if (hit.Envelope is not null) return hit.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.CommandResult)!;
                Thread.Sleep(5);
            }
            throw new TimeoutException(id);
        }

        public async Task<CardSnapshot> ShowAsync()
        {
            Shell.Open(WindowKind.Main);
            Ready(WindowKind.Main);
            Assert.True(await Eventually.WaitAsync(() => Platform.Posted.Any(p => p.Kind == WindowKind.Main && p.Envelope.Kind == UiMessageKind.Snapshot)));
            Assert.True(Run(WindowKind.Main, UiCommands.SubmitText, new { text = "good" }).Ok);
            CardSnapshot? card = null;
            Assert.True(await Eventually.WaitAsync(() => (card = Cards().LastOrDefault(c => c.ServiceId == "youdao/translate" && c.State == CardState.Ready)) is not null));
            return card!;
        }

        public IEnumerable<CardSnapshot> Cards() => Platform.Posted.ToArray()
            .Where(p => p.Kind == WindowKind.Main && (p.Envelope.Kind == UiMessageKind.Patch || p.Envelope.Name == "translation"))
            .SelectMany(p => p.Envelope.Kind == UiMessageKind.Patch
                ? [p.Envelope.Payload!.Value.GetProperty("card").Deserialize(ContractsJson.Default.CardSnapshot)!]
                : p.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.TranslationSnapshot)!.Cards);

        public CollectView Collect(bool? favorite)
        {
            var r = Run(WindowKind.Main, UiCommands.Collect, favorite is null ? new { serviceId = "youdao/translate" } : new { serviceId = "youdao/translate", favorite });
            Assert.True(r.Ok, r.Error);
            return r.Value!.Value.Deserialize(ContractsJson.Default.CollectView)!;
        }

        public void OpenSettings() { Shell.Open(WindowKind.Settings); Ready(WindowKind.Settings); }

        public VocabSettingsView View()
        {
            var r = Run(WindowKind.Settings, UiCommands.SettingsRead, new { });
            Assert.True(r.Ok);
            return r.Value!.Value.Deserialize(ContractsJson.Default.SettingsView)!.Vocab!;
        }

        public VocabSettingsView Settings2(CommandResult r)
        {
            Assert.True(r.Ok, r.Error);
            return r.Value!.Value.Deserialize(ContractsJson.Default.SettingsView)!.Vocab!;
        }

        public void Enable(string instance, Func<InstanceSettings, InstanceSettings>? edit = null)
        {
            var s = Config.State.Effective;
            var next = s with
            {
                Services = [.. s.Services.Select(x => x.Instance == instance && x.Capability == Capability.Vocab ? x with { Enabled = true } : x)],
                Instances = [.. s.Instances.Select(i => i.Id == instance && edit is not null ? edit(i) with { Revision = i.Revision + 1 } : i)],
            };
            Assert.Equal(SaveStatus.Saved, Config.Save(next, Config.State.Revision, Config.State.FileHash).Status);
        }

        public static InstanceSettings With(InstanceSettings i, string key, string value) => i with { Config = new Dictionary<string, string>(i.Config) { [key] = value } };

        public void Dispose() { Shell.Vocab = null; Vocab?.StopAsync().GetAwaiter().GetResult(); Db.Dispose(); Settings.Dispose(); Root.Dispose(); }
    }

    private static VocabTargetView Target2(VocabSettingsView v, string id) => v.Targets.Single(t => t.InstanceId == id);

    [Fact] // star: a favorite works with no sync target and says so; the host builds the content from the shown entry
    public async Task Star_favorites_locally_when_no_target_exists_and_unfavorites()
    {
        using var rig = new Rig();
        await rig.ShowAsync();
        Assert.Equal(new CollectView(false, 0), rig.Collect(null));

        Assert.Equal(new CollectView(true, 0), rig.Collect(true));
        Assert.Equal(new CollectView(true, 0), rig.Collect(null));
        var entry = Assert.Single(rig.Favorites.List());
        Assert.Equal(("en", "good"), (entry.Lang, entry.DisplayText));
        using var content = JsonDocument.Parse(entry.ContentJson);
        var root = content.RootElement;
        Assert.Equal("Youdao", root.GetProperty("source").GetString());
        Assert.Equal("ɡʊd", root.GetProperty("phonetics")[0].GetProperty("ipa").GetString());
        Assert.Equal("优良的", root.GetProperty("meanings")[0].GetProperty("means")[1].GetString());
        Assert.Equal("美好的一天", root.GetProperty("examples")[0].GetProperty("dst").GetString());
        Assert.Empty(rig.Favorites.Deliveries(entry.EntryId));

        Assert.Equal(new CollectView(false, 0), rig.Collect(false));
        Assert.False(rig.Favorites.IsFavorite("en", "good"));
        Assert.Equal(new CollectView(true, 0), rig.Collect(true)); // re-favorite restores it
        Assert.Single(rig.Favorites.List());
    }

    [Fact]
    public async Task Star_refuses_unknown_cards_and_a_host_without_the_service()
    {
        using var rig = new Rig();
        await rig.ShowAsync();
        Assert.Equal("not-ready", rig.Run(WindowKind.Main, UiCommands.Collect, new { serviceId = "nope", favorite = true }).Error);
        Assert.Empty(rig.Favorites.List());

        using var bare = new Rig(withVocab: false);
        await bare.ShowAsync();
        Assert.Equal("unavailable", bare.Run(WindowKind.Main, UiCommands.Collect, new { serviceId = "youdao/translate" }).Error);
    }

    [Fact] // the star queues to the enabled targets, and the loop delivers it; unfavorite never calls a target
    public async Task Star_queues_to_enabled_targets_and_the_sync_loop_delivers()
    {
        using var rig = new Rig();
        rig.Vocab!.Start();
        rig.Enable("ankiconnect"); // loopback default address, key optional and off
        await rig.ShowAsync();

        Assert.Equal(new CollectView(true, 1), rig.Collect(true));

        Assert.True(await Eventually.WaitAsync(() => rig.Favorites.Deliveries(rig.Favorites.List().Single().EntryId).Single().State == DeliveryState.Succeeded));
        var sent = Assert.Single(rig.Anki.Requests);
        Assert.Equal(("good", "en", VocabAction.Upsert), (sent.Word, sent.Lang, sent.Action));
        Assert.Empty(rig.Eudic.Requests);
        rig.Collect(false);
        Assert.Single(rig.Anki.Requests);
    }

    [Fact] // CFG02 / A03-style honesty: each target says what it is missing; nothing is queued to a target that cannot run
    public async Task Targets_report_their_state_and_unusable_ones_get_nothing()
    {
        using var rig = new Rig();
        rig.OpenSettings();
        var view = rig.View();
        Assert.Equal(["ankiconnect", "eudic"], view.Targets.Select(t => t.InstanceId));
        Assert.All(view.Targets, t => Assert.Equal(("vocab.reason.disabled", false, false), (t.ReasonKey, t.Usable, t.Enabled)));
        Assert.True(view.CanExport);

        rig.Enable("eudic");
        Assert.Equal(("vocab.reason.missingKey", false), (Target2(rig.View(), "eudic").ReasonKey, Target2(rig.View(), "eudic").Usable));
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("eudic", "apiKey", "NIS-SECRET-KEY", ConfirmGrants: true)).Ok);
        var eudic = Target2(rig.View(), "eudic");
        Assert.Equal((true, null, "https://api.frdic.com:443"), (eudic.Usable, eudic.ReasonKey, eudic.Origin));
        Assert.DoesNotContain("NIS-SECRET-KEY", JsonSerializer.Serialize(rig.View(), ContractsJson.Default.VocabSettingsView));

        rig.Enable("ankiconnect", i => Rig.With(i, "baseUrl", "http://192.168.1.50:8765")); // a LAN host is not "local"
        var anki = Target2(rig.View(), "ankiconnect");
        Assert.Equal((false, "vocab.reason.originNotLocal", "http://192.168.1.50:8765"), (anki.Usable, anki.ReasonKey, anki.Origin));
        rig.Enable("ankiconnect", i => Rig.With(i, "baseUrl", "http://127.0.0.1:9000"));
        Assert.True(Target2(rig.View(), "ankiconnect").Usable);
        rig.Enable("ankiconnect", i => Rig.With(i, "useApiKey", "true")); // the optional key now counts
        Assert.Equal("vocab.reason.missingKey", Target2(rig.View(), "ankiconnect").ReasonKey);

        await rig.ShowAsync();
        Assert.Equal(new CollectView(true, 1), rig.Collect(true)); // only Eudic can take it
        Assert.Equal(["eudic"], rig.Favorites.Deliveries(rig.Favorites.List().Single().EntryId).Select(d => d.TargetInstanceId));
    }

    [Fact] // the service rows of the page: an AnkiConnect key can be saved; a Local package's address check shows up in the service availability
    public void Service_rows_for_vocabulary_packages_carry_credential_targets()
    {
        using var rig = new Rig();
        rig.Enable("ankiconnect");
        var settings = rig.Shell.ProjectSettings(rig.Config.State);
        var anki = settings.Services.Single(s => s.ServiceId == "ankiconnect/vocab");
        Assert.Equal(("vocab", true, "Ready"), (anki.Page, anki.Enabled, anki.Availability));
        Assert.Equal(["apiKey"], anki.SecretNames);
        Assert.Equal("http://127.0.0.1:8765", Assert.Single(anki.CredentialTargets!).Origin);
        var eudic = settings.Services.Single(s => s.ServiceId == "eudic/vocab");
        Assert.Equal(("Disabled", "https://api.frdic.com:443"), (eudic.Availability, Assert.Single(eudic.CredentialTargets!).Origin));
    }

    [Fact] // DATA06 UI half: an Uncertain row is listed and checked by the user; Failed rows have retry; counts follow
    public async Task Rows_to_check_and_retry_failed_through_the_page_commands()
    {
        using var rig = new Rig();
        rig.Enable("ankiconnect");
        rig.Anki.Requests.Clear();
        var entry = rig.Favorites.Favorite(new FavoriteCard("en", "apple", """{"meanings":["fruit"]}"""), ["ankiconnect"]);
        rig.Favorites.Claim("ankiconnect");
        rig.Favorites.Complete(entry.EntryId, "ankiconnect", 1, DeliveryState.Uncertain);
        var bad = rig.Favorites.Favorite(new FavoriteCard("en", "pear", """{"meanings":["fruit"]}"""), ["ankiconnect"]);
        rig.Favorites.Claim("ankiconnect");
        rig.Favorites.Complete(bad.EntryId, "ankiconnect", 1, DeliveryState.Failed);
        rig.OpenSettings();

        var view = rig.View();
        Assert.Equal(["apple", "pear"], view.Rows.Select(r => r.Word));
        Assert.Equal(["Uncertain", "Failed"], view.Rows.Select(r => r.State));
        var anki = Target2(view, "ankiconnect");
        Assert.Equal((1, 1, 0), (anki.Uncertain, anki.Failed, anki.Succeeded));

        // "It is there": Succeeded, no resend
        view = rig.Settings2(rig.Run(WindowKind.Settings, UiCommands.VocabResolve, new VocabResolveRequest(entry.EntryId, "ankiconnect", 1, true)));
        Assert.Equal(["pear"], view.Rows.Select(r => r.Word));
        Assert.Equal(1, Target2(view, "ankiconnect").Succeeded);
        Assert.Empty(rig.Anki.Requests);
        Assert.Equal("not-found", rig.Run(WindowKind.Settings, UiCommands.VocabResolve, new VocabResolveRequest(entry.EntryId, "ankiconnect", 1, true)).Error);

        // "retry failed": the same row goes out again with its operationId
        string op = rig.Favorites.Deliveries(bad.EntryId).Single().OperationId;
        rig.Vocab!.Start();
        view = rig.Settings2(rig.Run(WindowKind.Settings, UiCommands.VocabSync, new VocabSyncCommand("retryFailed", "ankiconnect")));
        Assert.True(await Eventually.WaitAsync(() => rig.Favorites.Deliveries(bad.EntryId).Single().State == DeliveryState.Succeeded));
        Assert.Equal(op, Assert.Single(rig.Anki.Requests).OperationId);
        Assert.Empty(rig.View().Rows);
    }

    [Fact]
    public async Task Sync_commands_validate_their_target_and_queue_existing_favorites()
    {
        using var rig = new Rig();
        rig.OpenSettings();
        Assert.Equal("unknown-target", rig.Run(WindowKind.Settings, UiCommands.VocabSync, new VocabSyncCommand("retryFailed", "nope")).Error);
        Assert.Equal("not-usable", rig.Run(WindowKind.Settings, UiCommands.VocabSync, new VocabSyncCommand("queueExisting", "ankiconnect")).Error);
        Assert.Equal("range", rig.Run(WindowKind.Settings, UiCommands.VocabSync, new VocabSyncCommand("explode", "ankiconnect")).Error);
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.VocabSync, new VocabSyncCommand("sync")).Ok);

        await rig.ShowAsync();
        rig.Collect(true); // saved locally, no target yet
        rig.Enable("ankiconnect");
        var view = rig.Settings2(rig.Run(WindowKind.Settings, UiCommands.VocabSync, new VocabSyncCommand("queueExisting", "ankiconnect")));
        Assert.Equal(1, Target2(view, "ankiconnect").Pending);
        await rig.Vocab!.SyncNowAsync(TestContext.Current.CancellationToken);
        Assert.Single(rig.Anki.Requests);
    }

    [Fact] // DATA07/DATA08 host part: the save dialog gives the path, the exporter writes it, the page gets the path or the error
    public async Task Export_writes_through_the_save_dialog_and_shows_path_or_error()
    {
        using var rig = new Rig();
        rig.OpenSettings();
        var command = new VocabExportCommand("csv", true, true, false, false);
        var empty = rig.Settings2(rig.Run(WindowKind.Settings, UiCommands.VocabExport, command)); // cancelled dialog: nothing to report
        Assert.Null(empty.Export);
        Assert.Equal([("su-su-vocabulary.csv", "csv")], rig.Picker.Asked);

        rig.Picker.Path = Path.Combine(rig.Root.Root, "none", "out.csv"); // folder does not exist
        var failed = rig.Settings2(rig.Run(WindowKind.Settings, UiCommands.VocabExport, command)).Export!;
        Assert.Equal((null, "csv"), (failed.Path, failed.Format));
        Assert.False(string.IsNullOrEmpty(failed.Error));

        await rig.ShowAsync();
        rig.Collect(true);
        rig.Picker.Path = Path.Combine(rig.Root.Root, "out.csv");
        var done = rig.Settings2(rig.Run(WindowKind.Settings, UiCommands.VocabExport, command)).Export!;
        Assert.Equal((rig.Picker.Path, null, 1), (done.Path, done.Error, done.Exported));
        string text = File.ReadAllText(rig.Picker.Path);
        Assert.Contains("good", text);
        Assert.Contains("Phonetic", text);
        Assert.DoesNotContain("Example", text); // the field choice drops the column

        var again = rig.Settings2(rig.Run(WindowKind.Settings, UiCommands.VocabExport, command with { OnlyNew = true })).Export!;
        Assert.Equal("export.nothingToExport", again.Error); // an unchanged set is not exported twice

        rig.Picker.Path = Path.Combine(rig.Root.Root, "deck.apkg");
        var apkg = rig.Settings2(rig.Run(WindowKind.Settings, UiCommands.VocabExport, new VocabExportCommand("apkg", true, true, true, false, "My deck"))).Export!;
        Assert.Equal((rig.Picker.Path, null), (apkg.Path, apkg.Error));
        Assert.True(File.Exists(rig.Picker.Path));
        Assert.Equal("range", rig.Run(WindowKind.Settings, UiCommands.VocabExport, new VocabExportCommand("pdf", true, true, true, false)).Error);
    }

    [Fact]
    public void Export_is_unavailable_without_an_exporter_or_a_dialog_and_a_broken_dialog_is_an_error_line()
    {
        using var noExporter = new Rig(withExporter: false);
        noExporter.OpenSettings();
        Assert.False(noExporter.View().CanExport);
        Assert.Equal("unavailable", noExporter.Run(WindowKind.Settings, UiCommands.VocabExport, new VocabExportCommand("txt", true, true, true, false)).Error);

        using var rig = new Rig();
        rig.OpenSettings();
        rig.Picker.Throw = true;
        var view = rig.Settings2(rig.Run(WindowKind.Settings, UiCommands.VocabExport, new VocabExportCommand("txt", true, true, true, false)));
        Assert.Equal("export.pathInvalid", view.Export!.Error);
    }

    [Fact] // the page is told when counts change (a favorite, a finished pass), without polling
    public async Task Settings_page_gets_a_settings_event_when_vocabulary_changes()
    {
        using var rig = new Rig();
        rig.OpenSettings();
        await rig.ShowAsync();
        int before = rig.Platform.Posted.Count(p => p.Kind == WindowKind.Settings && p.Envelope.Name == "settings");

        rig.Collect(true);

        Assert.True(await Eventually.WaitAsync(() => rig.Platform.Posted.Count(p => p.Kind == WindowKind.Settings && p.Envelope.Name == "settings") > before));
        var last = rig.Platform.Posted.ToArray().Last(p => p.Kind == WindowKind.Settings && p.Envelope.Name == "settings");
        Assert.Equal(1, last.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.SettingsView)!.Vocab!.Favorites);
    }
}
