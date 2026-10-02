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

/// <summary>F18.2 host side of the update section of the Settings window: the projected view carries no path or URL, the commands are Settings-only, and install needs the acknowledgement.</summary>
public sealed class F18UpdateShellTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly TempRoot root = new();
    private readonly List<IDisposable> toDispose = [];

    public void Dispose() { foreach (var d in toDispose) d.Dispose(); root.Dispose(); }

    private sealed class FakeUpdates : IAppUpdateService
    {
        public bool Available { get; set; } = true;
        public string State = "available";
        public string? Error;
        public int Checks, Downloads, Installs, Discards, InFlight;
        public bool? LastAck;
        public bool Auto = true;
        public AppUpdateInfo Status() => new(State, "1.1.0", State is "available" or "ready" ? new AppUpdateOffer("1.2.0", 5, "susu-1.2.0.zip", 22_400_000, new string('a', 64), "notes") : null, Error, Available, Auto,
            new DateTimeOffset(2026, 10, 2, 8, 0, 0, TimeSpan.Zero), InFlight, KeyringEmbedded: false);
        public Task<AppUpdateInfo> CheckAsync(bool automatic, CancellationToken cancellationToken) { Checks++; return Task.FromResult(Status()); }
        public Task<AppUpdateInfo> DownloadAsync(CancellationToken cancellationToken) { Downloads++; State = "ready"; return Task.FromResult(Status()); }
        public AppUpdateInstallOutcome Install(bool acknowledgedInFlight)
        {
            LastAck = acknowledgedInFlight;
            if (State != "ready") return new AppUpdateInstallOutcome(false, "not-ready");
            if (InFlight > 0 && !acknowledgedInFlight) return new AppUpdateInstallOutcome(false, "in-flight-needs-confirmation");
            Installs++;
            State = "installing";
            return new AppUpdateInstallOutcome(true, null);
        }
        public void Discard() { Discards++; State = "none"; }
        public void SetAutoCheck(bool enabled) => Auto = enabled;
    }

    private sealed class ShellRig : IDisposable
    {
        public readonly FakePlatform Platform = new();
        public readonly ShellCoordinator Shell;
        private readonly SettingsStore settings;
        private long counter;

        public ShellRig(TempRoot root, FakeUpdates? updates)
        {
            var clock = new ManualClock();
            settings = new SettingsStore(root.Paths, clock);
            var config = new ConfigService(settings, new SecretStore(root.Paths.Secrets, new XorProtector()));
            var features = new FeatureRegistry();
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
            Shell = new ShellCoordinator(Platform, config, features, c => c is Capability.Translate, new ShellOptions(false, false), _ => null, null, new TranslationBackend(true, (_, _) => null), null);
            Shell.AppUpdates = updates;
            Shell.Start();
            Shell.Open(WindowKind.Settings);
            Shell.OnPageMessage(WindowKind.Settings, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Ready", windowSessionId = Platform.Session(WindowKind.Settings) }));
        }

        public string LastJson = "";

        public CommandResult Run(string name, object? payload = null)
        {
            string id = $"u{++counter}";
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

        public UpdateView? View(CommandResult r)
        {
            Assert.True(r.Ok, r.Error);
            return r.Value!.Value.Deserialize(ContractsJson.Default.SettingsView)!.Update;
        }

        public void Dispose() { }
    }

    private ShellRig NewShell(FakeUpdates? updates) { var rig = new ShellRig(root, updates); toDispose.Add(rig); return rig; }

    [Fact]
    public void The_view_carries_state_version_size_and_no_url_path_or_hash()
    {
        var rig = NewShell(new FakeUpdates());
        var view = rig.View(rig.Run(UiCommands.SettingsRead))!;
        Assert.Equal(("available", "1.1.0", true, true, false), (view.State, view.CurrentVersion, view.CanCheck, view.AutoCheck, view.KeyringEmbedded));
        Assert.Equal(("1.2.0", 22_400_000L, "notes"), (view.Offer!.Version, view.Offer.Size, view.Offer.Notes));
        Assert.DoesNotContain("susu-1.2.0.zip", rig.LastJson);
        Assert.DoesNotContain(new string('a', 64), rig.LastJson);
        Assert.StartsWith("2026-10-02T08:00:00", view.LastCheck);
    }

    [Fact]
    public void Without_an_update_service_there_is_no_section_and_the_commands_are_unavailable()
    {
        var rig = NewShell(null);
        Assert.Null(rig.View(rig.Run(UiCommands.SettingsRead)));
        foreach (string command in new[] { UiCommands.UpdateCheck, UiCommands.UpdateDownload, UiCommands.UpdateDiscard })
            Assert.Equal("unavailable", rig.Run(command).Error);
        Assert.Equal("unavailable", rig.Run(UiCommands.UpdateInstall, new { acknowledgedInFlight = true }).Error);
        var noSource = NewShell(new FakeUpdates { Available = false });
        Assert.False(noSource.View(noSource.Run(UiCommands.SettingsRead))!.CanCheck);
        Assert.Equal("unavailable", noSource.Run(UiCommands.UpdateCheck).Error);
    }

    [Fact]
    public void Check_download_and_install_are_separate_commands_and_install_needs_the_acknowledgement_when_tasks_run()
    {
        var updates = new FakeUpdates { InFlight = 2 };
        var rig = NewShell(updates);
        Assert.Equal("available", rig.View(rig.Run(UiCommands.UpdateCheck))!.State);
        Assert.Equal((1, 0, 0), (updates.Checks, updates.Downloads, updates.Installs)); // a check does not download or install
        Assert.Equal("not-ready", rig.Run(UiCommands.UpdateInstall, new { acknowledgedInFlight = true }).Error);
        Assert.Equal("ready", rig.View(rig.Run(UiCommands.UpdateDownload))!.State);
        Assert.Equal((1, 1, 0), (updates.Checks, updates.Downloads, updates.Installs));
        var refused = rig.Run(UiCommands.UpdateInstall, new { acknowledgedInFlight = false });
        Assert.Equal("in-flight-needs-confirmation", refused.Error);
        Assert.Equal(0, updates.Installs);
        Assert.True(rig.Run(UiCommands.UpdateInstall, new { acknowledgedInFlight = true }).Ok);
        Assert.Equal((1, true), (updates.Installs, updates.LastAck));
        Assert.Equal("none", rig.View(rig.Run(UiCommands.UpdateDiscard))!.State);
    }

    [Fact]
    public void The_auto_check_switch_reaches_the_service_and_the_view_shows_a_failure_by_its_key()
    {
        var updates = new FakeUpdates();
        var rig = NewShell(updates);
        Assert.False(rig.View(rig.Run(UiCommands.UpdateAutoCheck, new { enabled = false }))!.AutoCheck);
        Assert.False(updates.Auto);
        updates.State = "failed";
        updates.Error = "signature-invalid";
        var view = rig.View(rig.Run(UiCommands.UpdateCheck))!;
        Assert.Equal(("failed", "signature-invalid", null), (view.State, view.Error, view.Offer));
    }

    [Fact]
    public void The_update_commands_are_whitelisted_for_the_settings_window_only()
    {
        foreach (string command in new[] { UiCommands.UpdateCheck, UiCommands.UpdateDownload, UiCommands.UpdateInstall, UiCommands.UpdateDiscard, UiCommands.UpdateAutoCheck })
            foreach (var window in Enum.GetValues<WindowKind>())
                Assert.Equal(window == WindowKind.Settings, UiCommands.IsAllowed(window, command));
    }
}
