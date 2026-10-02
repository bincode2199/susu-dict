using System.Text;
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
/// F17.2 data-clean entries on the real storage layer (what each deletes and keeps; user files and plugin packages outside the app's data stay) and the
/// About section of the Settings window, host side: facts without a path, the license list from the generated notices, the log folder opened by the host
/// only, the diagnostics export through a fake save dialog, Data.Clear needing the confirmation, and the Settings-only whitelist.
/// </summary>
public sealed class F17AboutTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly TempRoot root = new();
    private readonly ManualClock clock = new();
    private readonly List<IDisposable> toDispose = [];

    public void Dispose() { foreach (var d in toDispose) d.Dispose(); root.Dispose(); }

    private sealed class Dialog : IDiagnosticsFilePicker
    {
        public string? Path;
        public bool Throw;
        public int Calls;
        public Task<string?> PickSaveAsync(string suggestedFileName, CancellationToken ct)
        {
            Calls++;
            Assert.EndsWith(".zip", suggestedFileName, StringComparison.Ordinal);
            return Throw ? Task.FromException<string?>(new InvalidOperationException("dialog")) : Task.FromResult(Path);
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly SettingsStore Settings;
        public readonly SecretStore Secrets;
        public readonly ConfigService Config;
        public readonly FileLeases Leases;
        public readonly KeptScreenshots Shots;
        public readonly Database Db;
        public readonly FavoritesRepository Favorites;
        public readonly DataCleanService Clean;
        public readonly AppPaths Paths;
        public readonly ManualClock Clock;
        private int ids;

        public Rig(TempRoot root, ManualClock clock)
        {
            Paths = root.Paths;
            Clock = clock;
            Settings = new SettingsStore(Paths, clock);
            Secrets = new SecretStore(Paths.Secrets, new XorProtector());
            Config = new ConfigService(Settings, Secrets);
            Leases = new FileLeases(Paths.Cache);
            Shots = KeptScreenshots.For(Paths);
            Db = Database.Open(Paths.Database);
            Favorites = new FavoritesRepository(Db, clock, null, () => $"id{++ids}");
            Clean = new DataCleanService(Paths, clock, Config, Secrets, Leases, Shots.Store, Favorites);
        }

        public void Seed()
        {
            var state = Settings.State;
            var grant = new CredentialGrant("app.susu.deepl", "builtin", "apiKey", "https://api-free.deepl.com:443", "header:Authorization");
            var next = state.Effective with
            {
                General = state.Effective.General with { UiLanguage = "en", SourceLanguage = "zh-Hans", TargetLanguage = "en", DefaultExpandedCards = 4 },
                Hotkeys = new HotkeySettings(new Dictionary<string, string>(state.Effective.Hotkeys.Chords) { ["inputTranslate"] = "Ctrl+Alt+Q" }),
                Network = state.Effective.Network with { ProxyMode = ProxyMode.Http, ProxyHost = "proxy.test", ProxyPort = 8080, ProxyUsername = "u" },
                Accounts = [new AccountSettings("acct-deepl", "DeepL", ["apiKey"], [grant])],
                Instances = [.. state.Effective.Instances.Select(i => i.Id == "deepl" ? i with { AccountBindings = new Dictionary<string, string> { ["apiKey"] = "acct-deepl" } } : i)],
                Prompts = [new PromptProfile("p1", "Mine", "Translate: {text}")],
            };
            var saved = Settings.Save(next, state.Revision, state.FileHash);
            Assert.True(saved.Status == SaveStatus.Saved, string.Join("; ", saved.Issues.Select(i => i.Path + ":" + i.Code)));
            Secrets.Write("acct-deepl", "apiKey", "sk-clean-test-KEY-123");
            Secrets.Write(NetworkSettings.ProxyAccountId, "password", "proxy-pass-123");
        }

        public void Dispose() { Settings.Dispose(); Leases.Dispose(); Db.Dispose(); }
    }

    private Rig NewRig() { var rig = new Rig(root, clock); toDispose.Add(rig); return rig; }

    private static byte[] Png(int n) => [0x89, 0x50, 0x4E, 0x47, (byte)n, 1, 2, 3];

    // ---- what each clean deletes and keeps ----

    [Fact]
    public void Items_list_every_kind_with_what_a_clear_would_remove()
    {
        var rig = NewRig();
        rig.Seed();
        rig.Leases.Create("tts", "mp3");
        File.WriteAllText(Path.Combine(rig.Paths.Cache, "loose.tmp"), "12345");
        File.WriteAllText(Path.Combine(rig.Paths.Logs, "susu-20261002.jsonl"), "{}\n{}\n");
        rig.Shots.Keep(Png(1), clock.UtcNow);
        rig.Favorites.Favorite(new FavoriteCard("en", "apple", "{}"), []);
        var items = rig.Clean.Items().ToDictionary(i => i.Kind);
        Assert.Equal(DataCleanKinds.All.Order(), items.Keys.Order());
        Assert.Equal(1, items[DataCleanKinds.Caches].Count);   // the loose file; the active lease is not counted
        Assert.Equal(1, items[DataCleanKinds.Logs].Count);
        Assert.Equal(1, items[DataCleanKinds.Screenshots].Count);
        Assert.Equal(1, items[DataCleanKinds.Favorites].Count);
        Assert.Equal(1, items[DataCleanKinds.Accounts].Count);
        Assert.All(items.Values, i => Assert.True(i.Available));
    }

    [Fact]
    public void Clearing_caches_removes_finished_sessions_and_loose_files_but_not_a_file_a_task_holds()
    {
        var rig = NewRig();
        var held = rig.Leases.Create("tts", "mp3");
        File.WriteAllText(held.FilePath, "playing");
        string stale = Path.Combine(rig.Paths.Cache, "999999-1");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "old.mp3"), "old");
        File.WriteAllText(Path.Combine(rig.Paths.Cache, "loose.tmp"), "x");
        var outcome = rig.Clean.Clear(DataCleanKinds.Caches);
        Assert.True(outcome.Ok);
        Assert.Equal(2, outcome.Removed);
        Assert.Equal(1, outcome.Skipped); // the active lease
        Assert.True(File.Exists(held.FilePath));
        Assert.False(Directory.Exists(stale));
        Assert.False(File.Exists(Path.Combine(rig.Paths.Cache, "loose.tmp")));
    }

    [Fact]
    public void Clearing_logs_removes_log_files_only()
    {
        var rig = NewRig();
        File.WriteAllText(Path.Combine(rig.Paths.Logs, "susu-20261001.jsonl"), "{}");
        File.WriteAllText(Path.Combine(rig.Paths.Logs, "susu-20261002.jsonl"), "{}");
        File.WriteAllText(Path.Combine(rig.Paths.Logs, "keep.txt"), "mine");
        var outcome = rig.Clean.Clear(DataCleanKinds.Logs);
        Assert.True(outcome.Ok);
        Assert.Equal(2, outcome.Removed);
        Assert.Empty(Directory.GetFiles(rig.Paths.Logs, "susu-*.jsonl"));
        Assert.True(File.Exists(Path.Combine(rig.Paths.Logs, "keep.txt")));
    }

    [Fact]
    public void Clearing_a_log_the_logger_has_open_still_works()
    {
        var rig = NewRig();
        using var log = new RedactingLog(rig.Paths.Logs, clock);
        log.Event("a");
        using var open = new FileStream(log.CurrentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); // a viewer or the app holds it without FileShare.Delete
        var outcome = rig.Clean.Clear(DataCleanKinds.Logs);
        Assert.True(outcome.Ok);
        Assert.Equal(1, outcome.Removed + outcome.Skipped);
        if (outcome.Skipped == 0) Assert.Equal(0, new FileInfo(log.CurrentFile).Length);
    }

    [Fact]
    public void Clearing_screenshots_deletes_the_index_entries_only_and_spares_replaced_and_held_copies_and_the_users_own_pictures()
    {
        var rig = NewRig();
        var a = rig.Shots.Keep(Png(1), clock.UtcNow.AddMinutes(-2));
        var b = rig.Shots.Keep(Png(2), clock.UtcNow.AddMinutes(-1));
        var c = rig.Shots.Keep(Png(3), clock.UtcNow);
        string folder = rig.Paths.KeptScreenshots;
        File.WriteAllBytes(Path.Combine(folder, "holiday.png"), Png(9)); // the user's own picture in the same folder
        File.WriteAllBytes(Path.Combine(folder, b.FileName!), Png(99));  // the user replaced this one
        using var hold = rig.Shots.Store.Hold(c.FileName!);              // a task is still using this one
        var outcome = rig.Clean.Clear(DataCleanKinds.Screenshots);
        Assert.True(outcome.Ok);
        Assert.Equal(1, outcome.Removed);
        Assert.Equal(2, outcome.Skipped);
        Assert.False(File.Exists(Path.Combine(folder, a.FileName!)));
        Assert.True(File.Exists(Path.Combine(folder, b.FileName!)));
        Assert.True(File.Exists(Path.Combine(folder, c.FileName!)));
        Assert.True(File.Exists(Path.Combine(folder, "holiday.png")));
    }

    [Fact]
    public void Clearing_favorites_removes_words_sync_rows_and_export_records_and_nothing_else()
    {
        var rig = NewRig();
        rig.Seed();
        rig.Favorites.Favorite(new FavoriteCard("en", "apple", "{}"), ["ankiconnect"]);
        rig.Favorites.Favorite(new FavoriteCard("en", "pear", "{}"), []);
        rig.Db.Write(w => w.Exec("INSERT INTO vocab_exports(export_id, format, file_hash, created_at, outcome) VALUES('e1','txt','h',1,'Done');"));
        var outcome = rig.Clean.Clear(DataCleanKinds.Favorites);
        Assert.True(outcome.Ok);
        Assert.Equal(2, outcome.Removed);
        Assert.Equal(0, rig.Favorites.ActiveCount());
        Assert.Empty(rig.Favorites.List(includeDeleted: true));
        Assert.Equal(0L, rig.Db.Read(c => Database.Scalar(c, null, "SELECT count(*) FROM vocab_exports;")));
        Assert.Equal(0L, rig.Db.Read(c => Database.Scalar(c, null, "SELECT count(*) FROM vocab_deliveries;")));
        Assert.True(rig.Secrets.Has("acct-deepl", "apiKey")); // keys and settings are not touched
        Assert.Equal("en", rig.Settings.State.Effective.General.UiLanguage);
    }

    [Fact]
    public void Resetting_settings_restores_the_defaults_and_keeps_accounts_grants_bindings_and_keys()
    {
        var rig = NewRig();
        rig.Seed();
        long before = rig.Settings.State.Revision;
        var outcome = rig.Clean.Clear(DataCleanKinds.Settings);
        Assert.True(outcome.Ok, outcome.Error);
        var s = rig.Settings.State.Effective;
        var d = BuiltInCatalog.Defaults();
        Assert.Equal(d.General.UiLanguage, s.General.UiLanguage);
        Assert.Equal(d.General.TargetLanguage, s.General.TargetLanguage);
        Assert.Equal(d.General.DefaultExpandedCards, s.General.DefaultExpandedCards);
        Assert.Equal(d.Hotkeys.Chords["inputTranslate"], s.Hotkeys.Chords["inputTranslate"]);
        Assert.Equal(ProxyMode.System, s.Network.ProxyMode);
        Assert.Empty(s.Prompts);
        Assert.True(s.Revision > before);
        Assert.Single(s.Accounts);
        Assert.Single(s.Accounts[0].Grants);
        Assert.Equal("acct-deepl", s.Instances.First(i => i.Id == "deepl").AccountBindings["apiKey"]);
        Assert.True(rig.Secrets.Has("acct-deepl", "apiKey"));
        // and it is on disk: a fresh load sees the same
        using var again = new SettingsStore(rig.Paths, clock);
        Assert.Equal("zh-Hans", again.State.Effective.General.UiLanguage);
        Assert.Single(again.State.Effective.Accounts);
    }

    [Fact]
    public void Deleting_accounts_removes_accounts_grants_bindings_and_every_key_including_the_proxy_password_in_one_commit()
    {
        var rig = NewRig();
        rig.Seed();
        Assert.Contains("sk-clean-test-KEY-123", Encoding.UTF8.GetString(new XorProtector().Unprotect(Convert.FromBase64String(ReadBlob(rig.Paths.Secrets, "apiKey")))));
        var outcome = rig.Clean.Clear(DataCleanKinds.Accounts);
        Assert.True(outcome.Ok, outcome.Error);
        var s = rig.Settings.State.Effective;
        Assert.Empty(s.Accounts);
        Assert.All(s.Instances, i => Assert.Empty(i.AccountBindings));
        Assert.Empty(rig.Secrets.Entries());
        Assert.False(rig.Secrets.Has("acct-deepl", "apiKey"));
        Assert.False(rig.Secrets.Has(NetworkSettings.ProxyAccountId, "password"));
        Assert.DoesNotContain("apiKey", File.ReadAllText(rig.Paths.Secrets));
        Assert.Equal("en", s.General.UiLanguage); // other settings stay
        Assert.Equal("proxy.test", s.Network.ProxyHost);
        using var again = new SettingsStore(rig.Paths, clock);
        Assert.Empty(again.State.Effective.Accounts);
        Assert.Empty(new SecretStore(rig.Paths.Secrets, new XorProtector()).Entries());
    }

    private static string ReadBlob(string path, string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
            if (e.GetProperty("name").GetString() == name) return e.GetProperty("blob").GetString()!;
        throw new InvalidOperationException(name);
    }

    [Fact]
    public void Clears_do_not_touch_files_outside_the_app_data_and_unknown_kinds_are_refused()
    {
        var rig = NewRig();
        rig.Seed();
        string outside = Path.Combine(root.Root, "user-documents");
        Directory.CreateDirectory(outside);
        string precious = Path.Combine(outside, "thesis.docx");
        File.WriteAllText(precious, "mine");
        string pluginFile = Path.Combine(rig.Paths.UserPlugins, "acme.dict", "plugin.js");
        Directory.CreateDirectory(Path.GetDirectoryName(pluginFile)!);
        File.WriteAllText(pluginFile, "code");
        string pictures = rig.Paths.KeptScreenshots;
        Directory.CreateDirectory(pictures);
        File.WriteAllText(Path.Combine(pictures, "family.jpg"), "mine");
        foreach (var kind in DataCleanKinds.All) Assert.True(rig.Clean.Clear(kind).Ok, kind);
        Assert.Equal("mine", File.ReadAllText(precious));
        Assert.Equal("code", File.ReadAllText(pluginFile));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(pictures, "family.jpg")));
        Assert.True(File.Exists(rig.Paths.Settings)); // the settings file itself stays (it was reset, not removed)
        Assert.True(File.Exists(rig.Paths.Database));
        Assert.Equal("unavailable", rig.Clean.Clear("everything").Error);
        Assert.Equal("unavailable", rig.Clean.Clear("../../Windows").Error);
        Assert.Equal("unavailable", rig.Clean.Clear(@"C:\Users").Error);
    }

    [Fact]
    public void A_service_without_a_store_reports_it_unavailable()
    {
        var rig = NewRig();
        var bare = new DataCleanService(rig.Paths, clock, rig.Config, rig.Secrets, null, null, null);
        Assert.False(bare.Clear(DataCleanKinds.Caches).Ok);
        Assert.Equal("unavailable", bare.Clear(DataCleanKinds.Screenshots).Error);
        Assert.Equal("unavailable", bare.Clear(DataCleanKinds.Favorites).Error);
        Assert.All(bare.Items().Where(i => i.Kind is "caches" or "screenshots" or "favorites"), i => Assert.False(i.Available));
    }

    // ---- the About section of the Settings window ----

    private sealed class ShellRig : IDisposable
    {
        public readonly Rig Data;
        public readonly FakePlatform Platform = new();
        public readonly ShellCoordinator Shell;
        public readonly Dialog Diagnostics = new();
        public readonly List<string> Opened = [];
        public bool OpenResult = true;
        private long counter;

        public ShellRig(TempRoot root, ManualClock clock, bool withAbout = true)
        {
            Data = new Rig(root, clock);
            var features = new FeatureRegistry();
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
            Shell = new ShellCoordinator(Platform, Data.Config, features, c => c is Capability.Translate, new ShellOptions(false, false), _ => null, null,
                new TranslationBackend(true, (_, _) => null), null);
            if (withAbout)
            {
                var literals = new SensitiveLiterals();
                var exporter = new DiagnosticsExporter(Data.Paths, clock, literals, () => new DiagnosticsInfo("1.2.3.0", "1.2.3", "Windows 10", ".NET 10", "zh-Hans", "system", 1, 1, 1, 1, 1, 1));
                Shell.About = new AboutService(new AboutInfo("1.2.3.0", "1.2.3", "Windows 10", ".NET 10", ""), Data.Paths, exporter, folder => { Opened.Add(folder); return OpenResult; });
                Shell.DiagnosticsPicker = Diagnostics;
                Shell.DataClean = Data.Clean;
            }
            Shell.Start();
            Shell.Open(WindowKind.Settings);
            Shell.OnPageMessage(WindowKind.Settings, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Ready", windowSessionId = Platform.Session(WindowKind.Settings) }));
        }

        public string LastJson = "";

        public CommandResult Run(string name, object? payload = null)
        {
            string id = $"a{++counter}";
            Shell.OnPageMessage(WindowKind.Settings, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Command", windowSessionId = Platform.Session(WindowKind.Settings), name, correlationId = id, payload = payload ?? new { } }, Web));
            for (var poll = System.Diagnostics.Stopwatch.StartNew(); poll.Elapsed < Eventually.DefaultTimeout;)
            {
                var hit = Platform.Posted.FirstOrDefault(p => p.Envelope.CorrelationId == id);
                if (hit.Envelope is not null)
                {
                    LastJson = hit.Envelope.Payload!.Value.GetRawText();
                    return hit.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.CommandResult)!;
                }
                Thread.Sleep(5);
            }
            throw new TimeoutException(id);
        }

        public AboutView? View(CommandResult r)
        {
            Assert.True(r.Ok, r.Error);
            return r.Value!.Value.Deserialize(ContractsJson.Default.SettingsView)!.About;
        }

        public void Dispose() => Data.Dispose();
    }

    private ShellRig NewShell(bool withAbout = true) { var rig = new ShellRig(root, clock, withAbout); toDispose.Add(rig); return rig; }

    [Fact]
    public void The_view_has_version_facts_licenses_data_entries_and_no_path()
    {
        var rig = NewShell();
        File.WriteAllText(Path.Combine(rig.Data.Paths.Logs, "susu-20261002.jsonl"), "{}\n");
        var view = rig.View(rig.Run(UiCommands.SettingsRead))!;
        Assert.Equal("1.2.3.0", view.Version);
        Assert.Equal("1.2.3", view.Build);
        Assert.Equal(1, view.LogFiles);
        Assert.True(view.CanOpenLogs);
        Assert.True(view.CanExport);
        Assert.Equal(DataCleanKinds.All, view.Data.Select(d => d.Kind).ToArray());
        Assert.Contains(view.Licenses, l => l.Name == "QuickJS-NG" && l.License == "MIT");
        Assert.Contains(view.Licenses, l => l.Name == "YamlDotNet" && l.License == "MIT");
        Assert.Contains(view.Licenses, l => l.Name == "vue" && l.Kind == "npm");
        Assert.True(view.Licenses.Length >= 30);
        Assert.All(view.Licenses, l => { Assert.NotEmpty(l.Name); Assert.NotEmpty(l.Version); Assert.NotEmpty(l.License); });
        // no absolute path, user name or data root reaches the page
        Assert.DoesNotContain(rig.Data.Paths.Root().Replace("\\", "\\\\"), rig.LastJson);
        Assert.DoesNotContain(Environment.UserName, rig.LastJson, StringComparison.OrdinalIgnoreCase);
        Assert.True(view.LogLocation.StartsWith("%LOCALAPPDATA%", StringComparison.Ordinal) || view.LogLocation.StartsWith("<data folder>", StringComparison.Ordinal), view.LogLocation);
    }

    [Fact]
    public void Open_logs_uses_the_hosts_folder_and_takes_no_path_from_the_page()
    {
        var rig = NewShell();
        Assert.True(rig.Run(UiCommands.AboutOpenLogs, new { path = @"C:\Windows\System32", folder = "..\\.." }).Ok);
        Assert.Equal([rig.Data.Paths.Logs], rig.Opened);
        rig.OpenResult = false;
        var failed = rig.Run(UiCommands.AboutOpenLogs);
        Assert.False(failed.Ok);
        Assert.Equal("open-failed", failed.Error);
    }

    [Fact]
    public void Export_diagnostics_writes_the_zip_the_dialog_chose_and_reports_the_file_name_only()
    {
        var rig = NewShell();
        File.WriteAllLines(Path.Combine(rig.Data.Paths.Logs, "susu-20261002.jsonl"),
            [JsonSerializer.Serialize(new Dictionary<string, object> { ["t"] = "2026-10-02T00:00:00.0000000Z", ["event"] = "a", ["status"] = 200 }), "garbage line"]);
        rig.Diagnostics.Path = Path.Combine(root.Root, "diag.zip");
        var view = rig.View(rig.Run(UiCommands.AboutExportDiagnostics))!;
        Assert.Null(view.Diagnostics!.Error);
        Assert.Equal("diag.zip", view.Diagnostics.FileName);
        Assert.Equal(1, view.Diagnostics.LogLines);
        Assert.Equal(1, view.Diagnostics.DroppedLines);
        Assert.True(File.Exists(rig.Diagnostics.Path));
        Assert.DoesNotContain(root.Root.Replace("\\", "\\\\"), rig.LastJson);
        // cancelled: nothing new, nothing written
        rig.Diagnostics.Path = null;
        Assert.Equal("diag.zip", rig.View(rig.Run(UiCommands.AboutExportDiagnostics))!.Diagnostics!.FileName); // the last result stays until dismissed
        Assert.Equal(2, rig.Diagnostics.Calls);
        Assert.Null(rig.View(rig.Run(UiCommands.AboutDismiss))!.Diagnostics);
    }

    [Fact]
    public void Export_failures_reach_the_page_as_stable_keys()
    {
        var rig = NewShell();
        rig.Diagnostics.Throw = true;
        Assert.Equal("picker", rig.View(rig.Run(UiCommands.AboutExportDiagnostics))!.Diagnostics!.Error);
        rig.Diagnostics.Throw = false;
        rig.Diagnostics.Path = Path.Combine(root.Root, "no-such-folder", "d.zip");
        var view = rig.View(rig.Run(UiCommands.AboutExportDiagnostics))!;
        Assert.Equal("write-failed", view.Diagnostics!.Error);
        Assert.Null(view.Diagnostics.FileName);
    }

    [Fact]
    public void Data_clear_needs_the_confirmation_and_a_known_kind()
    {
        var rig = NewShell();
        rig.Data.Seed();
        var unconfirmed = rig.Run(UiCommands.DataClear, new { kind = "accounts" });
        Assert.False(unconfirmed.Ok);
        Assert.Equal("confirm-required", unconfirmed.Error);
        Assert.Equal("confirm-required", rig.Run(UiCommands.DataClear, new { kind = "accounts", confirm = false }).Error);
        Assert.Equal("unknown-kind", rig.Run(UiCommands.DataClear, new { kind = "everything", confirm = true }).Error);
        Assert.Equal("unknown-kind", rig.Run(UiCommands.DataClear, new { kind = @"..\..", confirm = true }).Error);
        Assert.Single(rig.Data.Settings.State.Effective.Accounts);
        Assert.True(rig.Data.Secrets.Has("acct-deepl", "apiKey"));

        var view = rig.View(rig.Run(UiCommands.DataClear, new { kind = "accounts", confirm = true }))!;
        Assert.Equal("accounts", view.Cleaned!.Kind);
        Assert.Null(view.Cleaned.Error);
        Assert.Empty(rig.Data.Settings.State.Effective.Accounts);
        Assert.Empty(rig.Data.Secrets.Entries());
        Assert.Equal(0, view.Data.First(d => d.Kind == "accounts").Count);
    }

    [Fact]
    public void Data_clear_of_caches_reports_the_items_in_use_that_were_kept()
    {
        var rig = NewShell();
        var held = rig.Data.Leases.Create("tts", "mp3");
        File.WriteAllText(held.FilePath, "x");
        var view = rig.View(rig.Run(UiCommands.DataClear, new { kind = "caches", confirm = true }))!;
        Assert.Equal(1, view.Cleaned!.Skipped);
        Assert.True(File.Exists(held.FilePath));
    }

    [Fact]
    public void A_build_without_the_services_has_no_section_and_the_commands_refuse()
    {
        var rig = NewShell(withAbout: false);
        Assert.Null(rig.View(rig.Run(UiCommands.SettingsRead)));
        Assert.Equal("unavailable", rig.Run(UiCommands.AboutOpenLogs).Error);
        Assert.Equal("unavailable", rig.Run(UiCommands.AboutExportDiagnostics).Error);
        Assert.Equal("unavailable", rig.Run(UiCommands.DataClear, new { kind = "logs", confirm = true }).Error);
    }

    [Fact]
    public void The_new_commands_are_allowed_in_the_settings_window_only()
    {
        foreach (var command in new[] { UiCommands.AboutOpenLogs, UiCommands.AboutExportDiagnostics, UiCommands.AboutDismiss, UiCommands.DataClear })
            foreach (var window in Enum.GetValues<WindowKind>())
                Assert.Equal(window == WindowKind.Settings, UiCommands.IsAllowed(window, command));
    }
}

internal static class PathsExtensions
{
    /// <summary>The data root folder of an <see cref="AppPaths.UnderRoot"/> layout.</summary>
    public static string Root(this AppPaths paths) => Path.GetDirectoryName(Path.GetDirectoryName(paths.Local))!;
}
