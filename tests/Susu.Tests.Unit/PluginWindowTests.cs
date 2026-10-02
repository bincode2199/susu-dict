using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Plugins.Install;
using Susu.Storage;
using Susu.Testing;
using Susu.Ui;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F16.1 Settings Plugins page, host side, with the real installer on a temp folder and a fake file dialog: pick then preview, the permission diff on the
/// view, confirm by token only, a refused package listed with reasons, discard, uninstall, an override of a built-in and its uninstall restoring the
/// shipped version (UPD01/UPD02/X02/S02 host part), a failing dialog, a busy second action, and the page being absent without an installer.
/// </summary>
public sealed class PluginWindowTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class Picker : IPluginPackagePicker
    {
        public string? Path;
        public bool Throw;
        public TaskCompletionSource<string?>? Hold;
        public Task<string?> PickAsync(CancellationToken ct)
            => Throw ? Task.FromException<string?>(new InvalidOperationException("dialog")) : Hold?.Task ?? Task.FromResult(Path);
    }

    private sealed class Rig : IDisposable
    {
        public readonly TempRoot Root = new();
        public readonly FakePlatform Platform = new();
        public readonly SettingsStore Settings;
        public readonly ConfigService Config;
        public readonly ShellCoordinator Shell;
        public readonly Database Db;
        public readonly PluginInstaller? Installer;
        public readonly Picker Dialog = new();
        public readonly string Zips;
        private long counter;

        public Rig(bool withInstaller = true, Func<HealthRequest, HealthResult>? health = null)
        {
            var clock = new ManualClock();
            Settings = new SettingsStore(Root.Paths, clock);
            Config = new ConfigService(Settings, new SecretStore(Root.Paths.Secrets, new XorProtector()));
            Db = Database.Open(Root.Paths.Database);
            Zips = Path.Combine(Root.Paths.Roaming, "zips");
            Directory.CreateDirectory(Zips);
            var features = new FeatureRegistry();
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
            Shell = new ShellCoordinator(Platform, Config, features, c => c is Capability.Translate, new ShellOptions(false, false), _ => null, null,
                new TranslationBackend(true, (_, _) => null), null);
            if (withInstaller)
            {
                var builtIns = new BuiltInPackages([new BuiltInPackage("app.susu.deepl", "1.2.0", new PermissionSet(["translate"], ["https://api.deepl.com:443"], ["apiKey"]))]);
                var keyring = new HostKeyring([new KeyValuePair<string, byte[]>(PackageFactory.HostKeyId, Ed25519.PublicKey(PackageFactory.SeedA))]);
                Installer = new PluginInstaller(Path.Combine(Root.Paths.Roaming, "user-plugins"), new PluginInstallationRepository(Db), builtIns, keyring, health);
                Shell.PluginInstaller = Installer;
                Shell.PluginPicker = Dialog;
            }
            Shell.Start();
            Shell.Open(WindowKind.Settings);
            Shell.OnPageMessage(WindowKind.Settings, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Ready", windowSessionId = Platform.Session(WindowKind.Settings) }));
        }

        public string Package(string name, string version, string hosts = "", string id = "com.example.echo", byte[]? seed = null, string? keyId = null, string secrets = "")
            => PackageFactory.Zip(Zips, name, PackageFactory.Files(PackageFactory.Manifest(id, version, "translate", hosts, secrets)), seed, keyId);

        public CommandResult Run(string name, object? payload = null)
        {
            string id = $"c{++counter}";
            Shell.OnPageMessage(WindowKind.Settings, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Command", windowSessionId = Platform.Session(WindowKind.Settings), name, correlationId = id, payload = payload ?? new { } }, Web));
            for (var poll = System.Diagnostics.Stopwatch.StartNew(); poll.Elapsed < Eventually.DefaultTimeout;)
            {
                var hit = Platform.Posted.FirstOrDefault(p => p.Envelope.CorrelationId == id);
                if (hit.Envelope is not null) return hit.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.CommandResult)!;
                Thread.Sleep(5);
            }
            throw new TimeoutException(id);
        }

        public PluginsView View(CommandResult r)
        {
            Assert.True(r.Ok, r.Error);
            return r.Value!.Value.Deserialize(ContractsJson.Default.SettingsView)!.Plugins!;
        }

        public PluginsView Pick(string path) { Dialog.Path = path; return View(Run(UiCommands.PluginPick)); }

        public void Dispose() { Shell.PluginInstaller = null; Db.Dispose(); Settings.Dispose(); Root.Dispose(); }
    }

    [Fact] // first install: the page sees the full permission set, confirms by token, and the package is listed
    public void Pick_previews_the_full_permission_set_and_confirm_installs_by_token()
    {
        using var rig = new Rig();
        var empty = rig.View(rig.Run(UiCommands.SettingsRead));
        Assert.Empty(empty.Installed);
        Assert.True(empty.CanPick);

        var view = rig.Pick(rig.Package("a.susuext", "1.0.0", "https://dict.example:443", secrets: "apiKey", seed: PackageFactory.SeedB));
        var pending = view.Pending!;
        Assert.Equal(("com.example.echo", "1.0.0", "thirdParty", "none"), (pending.Id, pending.Version, pending.SignerKind, pending.Against));
        Assert.Equal(["translate"], pending.AddedCapabilities);
        Assert.Equal(["https://dict.example:443"], pending.AddedOrigins);
        Assert.Equal(["apiKey"], pending.AddedSecrets);
        Assert.Empty(rig.Installer!.Installed()); // nothing active before the confirmation
        Assert.False(File.Exists(Path.Combine(rig.Root.Paths.Roaming, "user-plugins", "packages", "com.example.echo", "1.0.0", "manifest.yaml")));

        var done = rig.View(rig.Run(UiCommands.PluginConfirm, new { token = pending.Token }));
        Assert.Null(done.Pending);
        Assert.Equal(("install", null), (done.Last!.Action, done.Last.Error));
        var item = Assert.Single(done.Installed);
        Assert.Equal(("com.example.echo", "1.0.0"), (item.Id, item.Version));
    }

    [Fact] // an update shows only what changed against the installed version
    public void An_update_is_diffed_against_the_installed_version()
    {
        using var rig = new Rig();
        rig.Pick(rig.Package("a.susuext", "1.0.0", "https://old.example:443", seed: PackageFactory.SeedB));
        rig.Run(UiCommands.PluginConfirm, new { token = rig.View(rig.Run(UiCommands.SettingsRead)).Pending!.Token });
        var pending = rig.Pick(rig.Package("b.susuext", "1.1.0", "https://new.example:443", seed: PackageFactory.SeedB)).Pending!;
        Assert.Equal(("installed", "1.0.0", "1.0.0"), (pending.Against, pending.BaseVersion, pending.ReplacesVersion));
        Assert.Equal(["https://new.example:443"], pending.AddedOrigins);
        Assert.Equal(["https://old.example:443"], pending.RemovedOrigins);
        Assert.Empty(pending.AddedCapabilities);
    }

    [Fact] // a wrong token installs nothing; discard drops the pending package
    public void A_wrong_token_is_refused_and_discard_clears_the_pending_package()
    {
        using var rig = new Rig();
        var pending = rig.Pick(rig.Package("a.susuext", "1.0.0")).Pending!;
        var wrong = rig.Run(UiCommands.PluginConfirm, new { token = "nope" });
        Assert.False(wrong.Ok);
        Assert.Empty(rig.Installer!.Installed());
        var after = rig.View(rig.Run(UiCommands.PluginDiscard, new { token = pending.Token }));
        Assert.Null(after.Pending);
        Assert.False(rig.Run(UiCommands.PluginConfirm, new { token = pending.Token }).Ok); // the discarded token is gone
        Assert.Empty(rig.Installer.Installed());
    }

    [Fact] // adversarial or wrong files are refused with reasons and leave nothing pending
    public void Refused_packages_are_listed_with_reasons_and_nothing_is_pending()
    {
        using var rig = new Rig();
        var notZip = Path.Combine(rig.Zips, "x.txt");
        File.WriteAllText(notZip, "hello");
        var view = rig.Pick(notZip);
        Assert.Null(view.Pending);
        Assert.Equal(("preview", "install.rejected"), (view.Last!.Action, view.Last.Error));
        Assert.Equal("not-a-package", Assert.Single(view.Last.Issues).Code);

        var broken = Path.Combine(rig.Zips, "broken.susuext");
        File.WriteAllText(broken, "this is not a zip");
        Assert.Equal("unreadable", Assert.Single(rig.Pick(broken).Last!.Issues).Code);

        var escape = PackageFactory.Zip(rig.Zips, "escape.susuext", PackageFactory.Files(PackageFactory.Manifest()), null, null, ["../evil.js"]);
        view = rig.Pick(escape);
        Assert.Null(view.Pending);
        Assert.Contains(view.Last!.Issues, i => i.Code == "traversal");

        // a third party cannot take a built-in id
        view = rig.Pick(rig.Package("deepl.susuext", "9.0.0", id: "app.susu.deepl", seed: PackageFactory.SeedB, secrets: "apiKey"));
        Assert.Contains(view.Last!.Issues, i => i.Code == "builtin-id-not-host-signed");
        Assert.Empty(rig.Installer!.Installed());
    }

    [Fact] // cancelling the dialog changes nothing
    public void Cancelling_the_dialog_keeps_the_page_as_it_was()
    {
        using var rig = new Rig();
        rig.Dialog.Path = null;
        var view = rig.View(rig.Run(UiCommands.PluginPick));
        Assert.Null(view.Pending);
        Assert.Null(view.Last);
    }

    [Fact] // a failing dialog is reported as a command failure, not a crash
    public void A_failing_dialog_is_a_failed_command()
    {
        using var rig = new Rig();
        rig.Dialog.Throw = true;
        var r = rig.Run(UiCommands.PluginPick);
        Assert.False(r.Ok);
        Assert.Equal("picker", r.Error);
        rig.Dialog.Throw = false;
        Assert.NotNull(rig.Pick(rig.Package("a.susuext", "1.0.0")).Pending); // the page recovers
    }

    [Fact] // a second action while one runs is refused as busy
    public async Task A_second_action_while_the_dialog_is_open_is_busy()
    {
        using var rig = new Rig();
        rig.Dialog.Hold = new TaskCompletionSource<string?>();
        var first = Task.Run(() => rig.Run(UiCommands.PluginPick), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var second = rig.Run(UiCommands.PluginUninstall, new { id = "com.example.echo" });
        Assert.Equal("busy", second.Error);
        rig.Dialog.Hold.SetResult(null);
        Assert.True((await first).Ok);
    }

    [Fact] // activation fails: the page shows the failure and the previous state stays
    public void A_failed_activation_is_shown_and_the_previous_version_stays()
    {
        bool fail = false;
        using var rig = new Rig(health: r => fail ? new HealthResult(false, "boom") : PluginInstaller.Structural(r));
        rig.Pick(rig.Package("a.susuext", "1.0.0", seed: PackageFactory.SeedB));
        rig.Run(UiCommands.PluginConfirm, new { token = rig.View(rig.Run(UiCommands.SettingsRead)).Pending!.Token });
        fail = true;
        var pending = rig.Pick(rig.Package("b.susuext", "1.1.0", seed: PackageFactory.SeedB)).Pending!;
        var view = rig.View(rig.Run(UiCommands.PluginConfirm, new { token = pending.Token }));
        Assert.Equal("install.healthFailed", view.Last!.Error);
        Assert.Equal("1.0.0", Assert.Single(view.Installed).Version);
        Assert.Null(view.Pending);
    }

    [Fact] // host-signed override of a built-in; uninstalling it restores the shipped version
    public void Overriding_a_built_in_and_uninstalling_restores_the_shipped_version()
    {
        using var rig = new Rig();
        var pending = rig.Pick(rig.Package("d.susuext", "1.3.0", "https://api.deepl.com:443,https://extra.example:443", "app.susu.deepl", PackageFactory.SeedA, PackageFactory.HostKeyId, "apiKey")).Pending!;
        Assert.Equal(("builtin", "1.2.0", "1.2.0", "host"), (pending.Against, pending.BaseVersion, pending.OverridesBuiltIn, pending.SignerKind));
        Assert.Equal(["https://extra.example:443"], pending.AddedOrigins);
        var installed = rig.View(rig.Run(UiCommands.PluginConfirm, new { token = pending.Token }));
        Assert.Equal("1.2.0", Assert.Single(installed.Installed).OverridesBuiltIn);

        var after = rig.View(rig.Run(UiCommands.PluginUninstall, new { id = "app.susu.deepl" }));
        Assert.Empty(after.Installed);
        Assert.Equal(("uninstall", "1.2.0"), (after.Last!.Action, after.Last.RestoredBuiltIn));
        Assert.Null(rig.Installer!.ActiveDirectory("app.susu.deepl"));

        var again = rig.View(rig.Run(UiCommands.PluginUninstall, new { id = "app.susu.deepl" }));
        Assert.Equal("uninstall.notInstalled", again.Last!.Error);
    }

    [Fact] // a drop (native path) goes through the same preview and the page is told with an event
    public async Task A_dropped_path_previews_like_the_dialog_and_broadcasts_to_the_settings_window()
    {
        using var rig = new Rig();
        var before = rig.Platform.Posted.Count(p => p.Kind == WindowKind.Settings && p.Envelope.Name == "settings");
        var r = await rig.Shell.PreviewPluginPackageAsync(rig.Package("a.susuext", "1.0.0"));
        Assert.True(r.Ok);
        var view = rig.View(rig.Run(UiCommands.SettingsRead));
        Assert.Equal("1.0.0", view.Pending!.Version);
        Assert.True(await Eventually.WaitAsync(() => rig.Platform.Posted.Count(p => p.Kind == WindowKind.Settings && p.Envelope.Name == "settings") > before));
    }

    [Fact] // no installer: no plugins section and the commands are unavailable
    public void Without_an_installer_the_page_is_absent_and_commands_are_unavailable()
    {
        using var rig = new Rig(withInstaller: false);
        var settings = rig.Run(UiCommands.SettingsRead).Value!.Value.Deserialize(ContractsJson.Default.SettingsView)!;
        Assert.Null(settings.Plugins);
        Assert.Equal("unavailable", rig.Run(UiCommands.PluginPick).Error);
        Assert.Equal("unavailable", rig.Run(UiCommands.PluginUninstall, new { id = "x" }).Error);
    }

    [Fact] // the commands are Settings-window only
    public void Plugin_commands_are_allowed_only_from_the_Settings_window()
    {
        foreach (var name in new[] { UiCommands.PluginPick, UiCommands.PluginConfirm, UiCommands.PluginDiscard, UiCommands.PluginUninstall })
        {
            Assert.True(UiCommands.IsAllowed(WindowKind.Settings, name));
            Assert.False(UiCommands.IsAllowed(WindowKind.Main, name));
            Assert.False(UiCommands.IsAllowed(WindowKind.Ocr, name));
        }
    }
}
