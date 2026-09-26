using System.Runtime.Versioning;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Plugins;
using Susu.Storage;
using Susu.Ui;
using Susu.Windows;
using Susu.Windows.Audio;
using Susu.Windows.Shell;

[assembly: SupportedOSPlatform("windows10.0.19041")]

namespace Susu.Host;

/// <summary>Startup mode, decided before anything else is initialized (ARCHITECTURE 2).</summary>
internal sealed record StartupMode(string Kind, string? DataRoot, bool Autostart, bool DevTools, string? SmokeReport, int SmokeCycles, string? MeasureEvents = null, int? ReleaseAfterSeconds = null, string? GuardFixture = null, string? GuardReport = null)
{
    public static StartupMode Parse(string[] args)
    {
        if (args.Length > 0 && args[0] is "--plugin-host" or "--selection-host" or "--clipboard-host") return new(args[0][2..], null, false, false, null, 0);
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
            case "plugin-host": return PluginHostMode.Run(args);
            case "selection-host":
                // F08.1: the temporary selection helper (ARCHITECTURE 4.1). Dispatched before anything else so no
                // main-mode service (settings, secrets, plugins, network, WebView) is initialized in the helper.
                return Susu.Windows.Selection.SelectionHost.Run(args.AsSpan(1), Console.Out, Console.Error);
            case "clipboard-host":
                // F08.2: the clipboard-borrow helper (PLAN 3.1 level 3); same early dispatch as the selection helper.
                return Susu.Windows.Clipboard.ClipboardHost.Run(Console.OpenStandardInput(), Console.OpenStandardOutput());
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
        // Input translation (F06) and selection/clipboard translation (F08) are real, shipped entry points; everything
        // else is still in development and stays off the release entry-point list regardless of capabilityReady.
        features.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
        features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Selection, FeatureState.Available, null, [Capability.Translate]));
        features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Clipboard, FeatureState.Available, null, [Capability.Translate]));
        // F10.2: pronunciation (hotkey, bar, card read-aloud) is available once the selected pronunciation service can run.
        features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Pronunciation, FeatureState.Available, null, [Capability.Tts]));
        foreach (var id in new[] { FeatureRegistry.Ids.Ocr,
                     FeatureRegistry.Ids.Voice, FeatureRegistry.Ids.SystemAudio, FeatureRegistry.Ids.Transcription, "update" })
            features.Register(new FeatureDescriptor(id, FeatureState.InDevelopment, "feature.inDevelopment", []));

        var usage = new UsageRepository(db, clock);
        // F07.2: settings controls come from each package's manifest schema; F07.3: the same schema gates model
        // parameters (temperature) on every plugin call.
        // F10.1: the native SAPI instance has no manifest; its voice/speed controls come from its built-in schema.
        var schemas = new Dictionary<string, IReadOnlyList<ConfigField>>(PluginTranslationProviders.LoadSchemas(exeFolder, package => log.Event("plugin.manifest-invalid", ("package", package))))
        {
            [BuiltInCatalog.NativeTts] = SapiTtsProvider.Schema,
        };
        var translation = BuildTranslationRuntime(exeFolder, settings, secrets, config, leases, clock, log, ReleaseAfter(mode) ?? TimeSpan.FromMinutes(10), schemas);
        Func<AppSettings, TranslationSession?> sessions = s =>
        {
            var providers = translation.Providers(s);
#if DEV_PREVIEW
            providers = [.. providers, .. FixtureProvider.All()];
#endif
            return providers.Count == 0 ? null : new TranslationSession(providers, new TranslationSessionOptions(s.Snapshot(), s.General.DefaultExpandedCards),
                new InvocationScheduler(SchedulerLimits.Default), clock, new SystemJitter(), usage);
        };
        SpeechBackend? speechPort = null;
        Func<Capability, bool> capabilityReady = c => c switch
        {
            Capability.Translate => translation.Providers(config.State.Effective).Count > 0,
            Capability.Tts => speechPort is { } port && port.Tts(config.State.Effective, config.State.Effective.Speech.Tts.Instance) is not null,
            _ => false,
        };
        // F10.1 native SAPI (no plugin host). F10.3: its phase timings are logged (no text), and the engine is warmed once, off the
        // UI thread, when the native voice list is first opened in Settings; never at startup (idle memory, PER02).
        var sapi = new SapiTtsProvider(new LeasedAudioFiles(leases));
        sapi.Timed += t => log.Event("tts.sapi", ("voiceMs", Math.Round(t.VoiceMs)), ("setupMs", Math.Round(t.SetupMs)), ("speakMs", Math.Round(t.SpeakMs)));
        // F07.2: dynamic fields load through the package's own options method with the instance's current config and grants.
        OptionsBroker? optionsBroker = translation.Supervisor is not { } supervisor ? null : new OptionsBroker(async (query, cancel) =>
        {
            if (query.InstanceId == BuiltInCatalog.NativeTts) // SAPI voices come from the OS, not the plugin host
            {
                _ = sapi.WarmUpAsync();
                return new OptionsLoad([.. (await SapiTtsProvider.VoicesAsync()).Select(v => new OptionItem(v.Id, v.Lang.Length == 0 ? v.Name : $"{v.Name} ({v.Lang})"))], null);
            }
            var outcome = await PluginTranslationProviders.LoadOptionsAsync(supervisor, config.State.Effective, query.InstanceId, query.Method, query.Field,
                query.Revision, query.Cursor, OptionsBroker.Timeout, cancel);
            if (outcome.Ok) return new OptionsLoad(outcome.Result?.Items, outcome.Result?.NextCursor);
            log.Event("options.failed", ("instance", query.InstanceId), ("kind", (outcome.ErrorKind ?? ErrorKind.Unavailable).ToString()));
            return new OptionsLoad(null, null, outcome.ErrorKind ?? ErrorKind.Unavailable);
        }, clock);
        // F08.2: three-level capture (UIA/IA2 helper, optional clipboard borrow in its own helper). Status only is logged, never text.
        var capture = new CaptureCoordinator(new Susu.Windows.Selection.SelectionReader(Susu.Windows.Selection.Win32SelectionPlatform.ForCurrentProcess()),
            new ClipboardBorrower(Susu.Windows.Clipboard.Win32ClipboardPlatform.ForCurrentProcess()), () => config.State.Effective.General.AllowClipboardBorrowing);
        capture.Completed += (trigger, status, source, reason, ms) => log.Event("capture", ("trigger", trigger.ToString()), ("status", status.ToString()), ("source", source), ("reason", reason), ("ms", ms));
        // F10.1 pronunciation port (F10.2 adds the commands and the bar): one player on the WASAPI output; native SAPI works
        // without the plugin host; cloud TTS through the installed speech packages; F09 dictionary audio downloads only
        // within the source service's declared origins, for entries still shown.
        ShellCoordinator? shell = null;
        DictionaryAudioFetcher? dictionaryAudio = translation.Network is not { } audioNetwork ? null : new DictionaryAudioFetcher(
            id => shell?.ResolveAudioLinkAsync(id) ?? Task.FromResult<DictionaryAudioLink?>(null),
            serviceId => translation.Providers(config.State.Effective).OfType<PluginProvider>().FirstOrDefault(p => p.ServiceId == serviceId)?.HostOrigins,
            () => audioNetwork.Current, leases);
        // F10.3: replays of the same text/service/voice/speed/config reuse the leased clip (ARCHITECTURE 8.4: 32 MiB, 7 days, session-scoped).
        using var ttsCache = new TtsCache(clock);
        var speech = new SpeechBackend(new SpeechPlayer(new WasapiAudioSink()),
            (s, instanceId) => instanceId == BuiltInCatalog.NativeTts ? sapi
                : translation.Supervisor is { } ttsHost ? PluginTtsProviders.Create(s, instanceId, secrets.Has, ttsHost, schemas) : null,
            dictionaryAudio is null ? null : dictionaryAudio.FetchClipAsync, ttsCache);
        speechPort = speech;
        speech.Player.StateChanged += state => log.Event("tts.player", ("phase", state.Phase.ToString()));
        var coordinator = new ShellCoordinator(platform, config, features, capabilityReady,
            new ShellOptions(Program.DevelopmentBuild, Program.DevelopmentBuild, ReleaseAfter(mode)), sessions, new ElsLanguageDetector(),
            new TranslationBackend(translation.Supervisor is not null, translation.ValidationProvider,
                serviceId => usage.Count(serviceId, "chars", clock.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)),
                schemas, optionsBroker, (network, targets, cancel) => NetworkProbe.RunAsync(network, secrets, targets, cancel), speech), capture);
        shell = coordinator;

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
        translation.Supervisor?.Dispose(); // bounded: kills the plugin-host child process (ARCHITECTURE 5.2)
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

    /// <summary>Providers are built from the settings each call (F06.3a), so service changes need no restart.</summary>
    private sealed record TranslationRuntime(Supervisor<HostSession>? Supervisor, Func<AppSettings, IReadOnlyList<ITranslationProvider>> Providers,
        Func<AppSettings, string, ITranslationProvider?> ValidationProvider, NetworkBrokerProvider? Network = null);

    /// <summary>
    /// F06.1/F06.3a composition root: the real F04/F05 plugin runtime (NetworkBrokerProvider, Supervisor
    /// &lt;HostSession&gt;, PluginProvider, S02's AccountAuthorization built from live settings). One plugin
    /// host loads the four wired built-in translation packages (<see cref="TranslationPackages"/>: MyMemory,
    /// keyless and on by default; Tencent, DeepL and OpenAI once enabled with a granted key); the providers
    /// are rebuilt from the current settings for every translation session.
    /// The child plugin-host process is this same susu.exe re-invoked with --plugin-host (Program.Main),
    /// so its resource directory is exactly this install's own exeFolder (susu.exe + every DLL it loads +
    /// the "plugins" subfolder), no staging copy needed the way tests need one.
    ///
    /// A failure anywhere here (sandbox unavailable, plugin failed to load) never blocks app startup: it
    /// is logged and input translation stays unavailable (capabilityReady stays false) instead of
    /// crashing the host - the same "detection/adapter failure never blocks the app" rule as ARCHITECTURE 7.
    ///
    /// The plugin host (an AppContainer child process) is not launched here: <paramref name="idleTimeout"/>
    /// puts <see cref="Supervisor{HostSession}"/> in lazy-start mode (F06.2a/PER02) - the first real
    /// translate call launches it via <c>PluginProvider</c>'s <c>Acquire()</c>, and it is released again
    /// after <paramref name="idleTimeout"/> with no call in flight, the same keep-warm-then-release shape
    /// F03 uses for the WebView (10 minutes in production; shortened for --smoke/--release-after-seconds).
    /// </summary>
    private static TranslationRuntime BuildTranslationRuntime(string exeFolder, SettingsStore settings, SecretStore secrets, ConfigService config, FileLeases leases, IClock clock, RedactingLog log, TimeSpan idleTimeout,
        IReadOnlyDictionary<string, IReadOnlyList<ConfigField>> schemas)
    {
        try
        {
            var networkBrokerProvider = new NetworkBrokerProvider(settings, secrets);
            var accountAuthorization = new AccountAuthorization(() => (config.State.Effective.Accounts, config.State.Effective.Instances));
            string executable = Environment.ProcessPath ?? Path.Combine(exeFolder, "susu.exe");
            var hostOptions = new HostSession.Options(executable, exeFolder, "quickjs",
                MakeBroker: () => new Broker(networkBrokerProvider, leases, secrets, accountAuthorization));
            var supervisor = new Supervisor<HostSession>(
                () => PluginTranslationProviders.LoadAll(HostSession.Start(hostOptions), (package, _) => log.Event("plugin.load-failed", ("package", package))),
                clock, idleTimeout);
            supervisor.RestartFailed += error => log.Event("plugin-host.restart-failed", ("code", error.GetType().Name));
            supervisor.Stalled += () => log.Event("plugin-host.stalled");
            return new TranslationRuntime(supervisor, settings => PluginTranslationProviders.Build(settings, secrets.Has, supervisor, schemas),
                (settings, serviceId) => PluginTranslationProviders.ForValidation(settings, serviceId, supervisor, schemas), networkBrokerProvider);
        }
        catch (Exception error)
        {
            log.Event("plugin-host.start-failed", ("code", error.GetType().Name));
            return new TranslationRuntime(null, _ => [], (_, _) => null);
        }
    }

    private static void ApplyAutostart(bool enabled)
    {
        try { WindowPlatform.SetLaunchAtStartup(enabled, Environment.ProcessPath!); }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>One instance per user; a separate data root (tests, development) is its own instance.</summary>
    private static string InstanceName(string? dataRoot)
        => dataRoot is null ? "Su-Su.Instance" : $"Su-Su.Instance.{AtomicFile.Hash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).ToLowerInvariant()))[..16]}";
}
