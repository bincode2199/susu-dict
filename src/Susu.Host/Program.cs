using System.Runtime.Versioning;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Storage;
using Susu.Ui;
using Susu.Windows;
using Susu.Windows.Shell;

[assembly: SupportedOSPlatform("windows10.0.19041")]

namespace Susu.Host;

/// <summary>Startup mode, decided before anything else is initialized (ARCHITECTURE 2).</summary>
internal sealed record StartupMode(string Kind, string? DataRoot, bool Autostart, bool DevTools, string? SmokeReport, int SmokeCycles, string? MeasureEvents = null, int? ReleaseAfterSeconds = null, string? GuardFixture = null, string? GuardReport = null)
{
    public static StartupMode Parse(string[] args)
    {
        if (args.Length > 0 && args[0] is "--plugin-host" or "--selection-host") return new(args[0][2..], null, false, false, null, 0);
        string? dataRoot = null, smoke = null, measure = null;
        bool autostart = false, devTools = false;
        int cycles = 3;
        int? releaseAfter = null;
        string? guardFixture = null, guardReport = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data-root" when i + 1 < args.Length: dataRoot = args[++i]; break;
                case "--autostart": autostart = true; break;
                case "--dev-tools": devTools = true; break;
                case "--smoke" when i + 1 < args.Length: smoke = args[++i]; break;
                case "--measure" when i + 1 < args.Length: measure = args[++i]; break;
                case "--guard-test" when i + 2 < args.Length: guardFixture = Path.GetFullPath(args[++i]); guardReport = args[++i]; break;
                case "--release-after-seconds" when i + 1 < args.Length && int.TryParse(args[i + 1], out int r) && r is > 0 and <= 3600: releaseAfter = r; i++; break;
                case "--cycles" when i + 1 < args.Length && int.TryParse(args[i + 1], out int n) && n is > 0 and <= 1000: cycles = n; i++; break;
                default: return new("invalid", null, false, false, null, 0);
            }
        }
        dataRoot ??= Environment.GetEnvironmentVariable(AppPaths.DataRootVariable);
        return new("main", dataRoot, autostart, devTools, smoke, cycles, measure, releaseAfter, guardFixture, guardReport);
    }
}

internal static class Program
{
#if DEV_PREVIEW
    public const bool DevelopmentBuild = true;
#else
    public const bool DevelopmentBuild = false;
#endif

    [STAThread]
    private static int Main(string[] args)
    {
        var mode = StartupMode.Parse(args);
        switch (mode.Kind)
        {
            case "main": return MainMode.Run(mode);
            case "plugin-host":
            case "selection-host":
                // Child-process modes are dispatched here first so no main-mode service initializes. Their
                // implementations are F04 (plugin host) and F08 (selection helper); this build refuses them.
                Console.Error.WriteLine($"susu: --{mode.Kind} is not available in this build");
                return 64;
            default:
                Console.Error.WriteLine("usage: susu [--data-root <dir>] [--autostart]");
                return 2;
        }
    }
}

internal static class MainMode
{
    public static int Run(StartupMode mode)
    {
        string exeFolder = AppContext.BaseDirectory;
        string assets = Path.Combine(exeFolder, "assets");
        var paths = AppPaths.Resolve(mode.DataRoot, Program.DevelopmentBuild).EnsureCreated();
        string instanceName = InstanceName(mode.DataRoot);
        using var instance = SingleInstance.TryAcquire(instanceName);
        if (instance is null) return 0; // an instance already runs; it has been asked to wake up

        var clock = SystemClock.Instance;
        var log = new RedactingLog(paths.Logs, clock);
        log.Event("app.start", ("phase", Program.DevelopmentBuild ? "development" : "release"));

        if (WebViewRuntime.InstalledVersion() is null)
        {
            log.Event("webview.missing");
            if (mode.SmokeReport is not null) { Smoke.WriteFailure(mode.SmokeReport, "webview2-runtime-missing"); return 3; }
            if (Win32Prompt.AskInstallWebView()) Win32Prompt.OpenInBrowser(WebViewRuntime.DownloadPage);
            return 3;
        }

        int rolledBack = new ConfigTransaction(paths.Transactions).Recover();
        if (rolledBack > 0) log.Event("config.recovered", ("count", rolledBack));
        using var settings = new SettingsStore(paths, clock);
        settings.StartWatching();
        var secrets = new SecretStore(paths.Secrets, new DpapiProtector());
        var config = new ConfigService(settings, secrets);
        Database database;
        try { database = Database.Open(paths.Database); }
        catch (DatabaseVersionException e)
        {
            log.Event("db.version", ("code", $"{e.Found}>{e.Supported}"));
            Win32Prompt.Error("Su-Su", e.Message);
            return 4;
        }
        using var db = database;
        using var leases = new FileLeases(paths.Cache);
        leases.CleanupStaleSessions();

        using var dispatcher = new UiDispatcher(instanceName);
        ShellEnvironment.Initialize(assets, e => log.Event("ui.exception", ("code", e.GetType().Name)));
        using var platform = new WindowPlatform(dispatcher, new UiHosting(Program.DevelopmentBuild && mode.GuardFixture is not null ? mode.GuardFixture : Path.Combine(exeFolder, "ui"), paths.WebView, mode.DevTools && Program.DevelopmentBuild),
            new WindowStateRepository(db), () => config.State.Effective.General.UiLanguage);

        var features = new FeatureRegistry();
        foreach (var id in new[] { FeatureRegistry.Ids.InputTranslation, FeatureRegistry.Ids.Selection, FeatureRegistry.Ids.Clipboard, FeatureRegistry.Ids.Ocr,
                     FeatureRegistry.Ids.Voice, FeatureRegistry.Ids.SystemAudio, FeatureRegistry.Ids.Transcription, FeatureRegistry.Ids.Pronunciation, "update" })
            features.Register(new FeatureDescriptor(id, FeatureState.InDevelopment, "feature.inDevelopment", id == FeatureRegistry.Ids.InputTranslation ? [Capability.Translate] : []));

        Func<AppSettings, TranslationSession?>? sessions = null;
        Func<Capability, bool> capabilityReady = _ => false; // no adapter exists before F05/F06
#if DEV_PREVIEW
        var usage = new UsageRepository(db, clock);
        capabilityReady = c => c == Capability.Translate;
        sessions = s => new TranslationSession(FixtureProvider.All(), new TranslationSessionOptions(s.Snapshot(), s.General.DefaultExpandedCards),
            new InvocationScheduler(SchedulerLimits.Default), clock, new SystemJitter(), usage);
#endif
        var coordinator = new ShellCoordinator(platform, config, features, capabilityReady,
            new ShellOptions(Program.DevelopmentBuild, Program.DevelopmentBuild, ReleaseAfter(mode)), sessions);

        using var tray = new TrayIcon(dispatcher, assets);
        platform.WindowRequested += coordinator.OnWindowRequest;
        platform.PageMessage += coordinator.OnPageMessage;
        platform.HotkeyPressed += coordinator.OnHotkey;
        platform.Diagnostic += message => log.Event("shell", ("code", message));
        platform.Timing += (kind, phase, ms) => log.Event("timing", ("window", kind.ToString()), ("phase", phase), ("durationMs", ms));
        coordinator.Diagnostic += message => log.Event("ui", ("code", message));
        coordinator.Timing += (kind, phase, ms) => log.Event("timing", ("window", kind.ToString()), ("phase", phase), ("durationMs", Math.Round(ms, 1)));
        dispatcher.ActivateRequested += coordinator.OnActivateRequest;
        tray.DoubleClick += coordinator.OnTrayDoubleClick;
        tray.MenuRequested += coordinator.OnTrayMenu;
        bool exiting = false;
        platform.ExitRequested += () => { if (!exiting) { exiting = true; dispatcher.Quit(); } };

        if (!tray.Add()) log.Event("tray.add-failed");
        coordinator.Start();
        var failed = coordinator.HotkeyResults.Where(r => !r.Value).Select(r => r.Key).ToList();
        if (failed.Count > 0)
            tray.Notify("Su-Su", config.State.Effective.General.UiLanguage == "en" ? "A hotkey could not be registered. Choose another one in Settings." : "有快捷键注册失败，请在设置中更换。");

        if (!Program.DevelopmentBuild && mode.DataRoot is null) ApplyAutostart(config.State.Effective.General.LaunchAtStartup);
        config.Changed += state => { if (!Program.DevelopmentBuild && mode.DataRoot is null) ApplyAutostart(state.Effective.General.LaunchAtStartup); };

        Smoke? smoke = mode.SmokeReport is null ? null : new Smoke(mode.SmokeReport, mode.SmokeCycles, coordinator, platform, dispatcher, tray, Program.DevelopmentBuild);
        smoke?.Start();
        Measure? measure = mode.MeasureEvents is null ? null : new Measure(mode.MeasureEvents, coordinator, platform, dispatcher, Program.DevelopmentBuild);
        measure?.Start();
#if DEV_PREVIEW
        GuardTest? guard = mode.GuardFixture is null ? null : new GuardTest(mode.GuardReport!, platform, dispatcher);
        guard?.Start();
#endif

        dispatcher.Run();

        log.Event("app.exit");
        smoke?.Finish();
#if DEV_PREVIEW
        if (guard is not null) return guard.ExitCode;
#endif
        return smoke?.ExitCode ?? measure?.ExitCode ?? 0;
    }

    /// <summary>Keep-warm override: 2 s for --smoke; --release-after-seconds only in development builds (PER03 cold runs).</summary>
    private static TimeSpan? ReleaseAfter(StartupMode mode)
        => mode.SmokeReport is not null ? TimeSpan.FromSeconds(2)
        : Program.DevelopmentBuild && mode.ReleaseAfterSeconds is int seconds ? TimeSpan.FromSeconds(seconds) : null;

    private static void ApplyAutostart(bool enabled)
    {
        try { WindowPlatform.SetLaunchAtStartup(enabled, Environment.ProcessPath!); }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>One instance per user; a separate data root (tests, development) is its own instance.</summary>
    private static string InstanceName(string? dataRoot)
        => dataRoot is null ? "Su-Su.Instance" : $"Su-Su.Instance.{AtomicFile.Hash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).ToLowerInvariant()))[..16]}";
}
