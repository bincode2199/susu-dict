using System.Reflection;
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
using Susu.Windows.Capture;
using Susu.Windows.Media;
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
        if (InstallerMode.IsInstallerCommand(args)) return InstallerMode.Run(args); // F18.1: installer/uninstaller helper commands, before any service starts
        if (UpdateHost.IsUpdateCommand(args)) return UpdateHost.Run(args); // F18.2: update helper and probe commands, before any service starts
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

        if (RecoverUpdate(paths, mode.DataRoot, log)) return 0; // F18.2: an interrupted application update is finished (or rolled back by the helper) before anything else runs
        if (FirstStartGate(paths, mode.DataRoot, log)) return 0; // F18.3: a new version that failed to start twice is rolled back by the helper instead of starting again
        int rolledBack = new ConfigTransaction(paths).Recover();
        if (rolledBack > 0) log.Event("config.recovered", ("count", rolledBack));
        // F17.1: a confirmed backup import is switched here, after the journal is recovered and before settings and secrets are read.
        try
        {
            if (BackupImport.ApplyPending(paths, clock) is { } importResult)
                log.Event("backup.import", ("state", importResult.State), ("code", importResult.Error ?? ""), ("source", importResult.Source));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Event("backup.import-failed", ("code", e.GetType().Name)); }
        using var settings = new SettingsStore(paths, clock);
        settings.StartWatching();
        // F17V-4: a damaged secrets.dat is moved aside and the store opens empty; the host starts and the user is told, never a crash loop.
        var secrets = SecretStore.OpenOrQuarantine(paths.Secrets, new DpapiProtector(), out bool secretsDamaged);
        if (secretsDamaged)
        {
            log.Event("secrets.damaged", ("kept", "secrets.dat.damaged"));
            if (mode.SmokeReport is null)
                Win32Prompt.Error("Su-Su", "The saved keys file was damaged and has been set aside as secrets.dat.damaged. Su-Su started without saved keys; enter them again in Settings.\n保存密钥的文件已损坏，已另存为 secrets.dat.damaged。Su-Su 已在没有保存密钥的状态下启动，请在设置中重新输入。");
        }
        var config = new ConfigService(settings, secrets);
        // F17.2: everything the log must never show (saved keys, user and machine names, account names, proxy host and user) is masked in every encoding from here on.
        SensitiveRegistry.Attach(log.Literals, secrets, settings, [MachineGuid()]);
        Database database;
        try { database = Database.Open(paths.Database); }
        catch (DatabaseVersionException e)
        {
            log.Event("db.version", ("code", $"{e.Found}>{e.Supported}"));
            Win32Prompt.Error("Su-Su", e.UserMessage(config.State.Effective.General.UiLanguage != "en"));
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
        // F11.3: screenshot OCR is available once an OCR service can run (enabled, credentials saved and granted).
        features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Ocr, FeatureState.Available, null, [Capability.Ocr]));
        // F12.3: voice translation (hotkey, tray, window) is available once the selected recording/audio ASR service can run.
        features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Voice, FeatureState.Available, null, [Capability.Asr]));
        // F13.2: system-audio translation is the same recorder, ASR and window over the output device; it needs the same ASR service.
        features.Register(new FeatureDescriptor(FeatureRegistry.Ids.SystemAudio, FeatureState.Available, null, [Capability.Asr]));
        // F14.4: video transcription is available once the video ASR selection can run and returns timecodes (the shell resolves that itself).
        features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Transcription, FeatureState.Available, null, []));
        features.Register(new FeatureDescriptor("update", FeatureState.InDevelopment, "feature.inDevelopment", []));

        var usage = new UsageRepository(db, clock);
        // F15.1: local favorites; Sending rows left by a crash become Uncertain, never resent blindly (DATA06).
        // F15.4: the card star, the sync loop and SetVocab are wired below with the coordinator.
        var favorites = new FavoritesRepository(db, clock);
        favorites.RecoverInterrupted();
        // F15.2: an export interrupted between the file move and the DB commit is resolved by file hash; no second copy is made.
        foreach (var r in new VocabExporter(db, favorites, clock).Recover()) log.Event("vocab.export-recovered", ("exportId", r.ExportId), ("recovered", r.Recovered.ToString()));
        // F07.2: settings controls come from each package's manifest schema; F07.3: the same schema gates model
        // parameters (temperature) on every plugin call.
        // F10.1: the native SAPI instance has no manifest; its voice/speed controls come from its built-in schema.
        var schemas = new Dictionary<string, IReadOnlyList<ConfigField>>(PluginTranslationProviders.LoadSchemas(exeFolder, package => log.Event("plugin.manifest-invalid", ("package", package))))
        {
            [BuiltInCatalog.NativeTts] = SapiTtsProvider.Schema,
        };
        // F16.1/F16.2: plugin installation. User packages live in the roaming plugins folder; the host keyring is empty until the release build embeds
        // the offline root (F18), so no package counts as host-signed yet and a built-in id cannot be overridden. The sandboxed host may read the
        // packages folder (HostSession.Options.ExtraReadRoots), so the post-switch health check is a real sandbox load once the plugin host exists.
        var hostControl = new LateHostControl();
        Func<HostSession.Options>? probeOptions = null;
        var kv = new PluginKvRepository(db);
        var pluginInstaller = new Susu.Plugins.Install.PluginInstaller(paths.UserPlugins, new PluginInstallationRepository(db),
            Susu.Plugins.Install.BuiltInPackages.FromDirectory(Path.Combine(exeFolder, "plugins")), Susu.Plugins.Install.HostKeyring.Empty,
            health: request => probeOptions is { } options ? Susu.Plugins.Install.PluginLoadProbe.Create(options)(request) : Susu.Plugins.Install.PluginInstaller.Structural(request),
            host: hostControl, removeData: id => kv.DeletePackage(id));
        try { pluginInstaller.Recover(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { log.Event("plugin.recover-failed", ("code", e.GetType().Name)); }
        var translation = BuildTranslationRuntime(exeFolder, settings, secrets, config, leases, clock, log, ReleaseAfter(mode) ?? TimeSpan.FromMinutes(10), schemas, pluginInstaller, kv);
        if (translation.Supervisor is { } pluginSupervisor)
        {
            hostControl.Target = new Susu.Plugins.Install.PluginHostController<HostSession>(pluginSupervisor, diagnostic: text => log.Event("plugin-host.control", ("text", text)));
            probeOptions = translation.ProbeOptions;
        }
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
            Capability.Ocr => translation.Supervisor is { } ocrHost && PluginOcrProviders.Resolve(config.State.Effective, secrets.Has, ocrHost, schemas) is not null,
            Capability.Asr => translation.Supervisor is { } asrHost && PluginAsrProviders.Create(config.State.Effective, SpeechSlot.Asr, secrets.Has, asrHost, schemas) is not null,
            Capability.Vocab => translation.Supervisor is not null, // F15.4: the vocabulary packages run in the plugin host
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
        // F11.1/F11.3: screenshot capture port for OCR. Kept copies are written only when SetOcr "keep screenshots" is on (off by
        // default: OCR03); indexed copies older than the SetOcr retention (7 days by default) go at startup.
        var keptScreenshots = KeptScreenshots.For(paths, () => config.State.Effective.Ocr.RetentionDays);
        try { keptScreenshots.Cleanup(clock.UtcNow); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Event("capture.retention", ("code", e.GetType().Name)); }
        coordinator.ScreenCapture = new ScreenCaptureCoordinator(new GdiScreenGrabber(), new RegionOverlay(), platform, new LeasedFiles(leases), clock, keptScreenshots,
            keepScreenshots: () => config.State.Effective.Ocr.KeepScreenshots,
            overlay: () => new RegionSelectOptions(config.State.Effective.General.UiLanguage == "en" ? ScreenCaptureCoordinator.HintEn : ScreenCaptureCoordinator.HintZh,
                config.State.Effective.General.Theme == "dark"));
        // F12.1: microphone recording port (WASAPI); each recording is a WAV file lease (F12.3 opens it from the voice entry).
        coordinator.AudioCapture = new AudioCaptureCoordinator(new WasapiMicrophone(), new LeasedFiles(leases), clock);
        // F13.2: the same coordinator over WASAPI loopback (default output device); the microphone is never opened for it.
        coordinator.SystemAudioCapture = new AudioCaptureCoordinator(WasapiMicrophone.SystemLoopback(), new LeasedFiles(leases), clock, null, AudioSourceKind.SystemLoopback);
        // F11.2: a captured image goes to the selected OCR service by handle; recognized text enters the OCR window's translation
        // session (T02) when SetOcr "translate after recognition" is on. F11.3: the shell owns each captured image, opens the OCR
        // window and runs this job; closing the window cancels it. Only status is logged, never text.
        var ocr = new OcrJob(() => translation.Supervisor is { } ocrSupervisor ? PluginOcrProviders.Resolve(config.State.Effective, secrets.Has, ocrSupervisor, schemas) : null,
            async text => await coordinator.SubmitRecognizedTextAsync(text), autoTranslate: () => config.State.Effective.Ocr.AutoTranslate);
        ocr.StateChanged += state => log.Event("ocr", ("phase", state.Phase.ToString()), ("service", state.ServiceId ?? ""), ("kind", state.Error?.Kind.ToString() ?? ""));
        coordinator.Ocr = ocr;
        // F12.2/F12.3: a finished recording goes to the selected ASR service by handle, chunk by chunk; the text enters the voice
        // window's translation session (T02). The shell owns each recording: closing the window or exiting cancels it and releases
        // the microphone. Only status is logged, never audio or text.
        var asrJob = new AsrJob(() => translation.Supervisor is { } asrSupervisor ? PluginAsrProviders.Create(config.State.Effective, SpeechSlot.Asr, secrets.Has, asrSupervisor, schemas) : null,
            new LeasedFiles(leases), async text => await coordinator.SubmitRecognizedTextAsync(text, WindowKind.Voice), autoTranslate: () => true);
        asrJob.StateChanged += state => log.Event("asr", ("phase", state.Phase.ToString()), ("service", state.ServiceId ?? ""), ("kind", state.Error?.Kind.ToString() ?? ""));
        coordinator.Asr = asrJob;
        // F14.2: the video job. ASR comes from the video selection (never the microphone voice one); translation is the video selection (SetSpeechB) or the first enabled
        // service, one text per request (an items-capable plugin API is not wired yet). The Transcribe window (F14.4) sets Confirmation.
        var mediaTokens = new MediaTokens();
        coordinator.MediaTokens = mediaTokens;
        // F15.4: favorites, the vocabulary sync loop and the file export. Favorites and export work without any target or the plugin host; a target is
        // built from the current settings for each pass, only for services that are enabled and have what they need (key saved and granted, a loopback
        // AnkiConnect address). The loop runs at start, after every favorite and on a timer; it is stopped (bounded) before the plugin host goes.
        var vocabService = new VocabService(favorites,
            new VocabSyncWorker(favorites, id => translation.Supervisor is { } vocabHost ? PluginVocabTargets.Create(config.State.Effective, id, vocabHost, schemas) : null, clock),
            () => translation.Supervisor is null ? [] : VocabTargets.Usable(config.State.Effective, secrets.Has), clock,
            new VocabFileExporter(new VocabExporter(db, favorites, clock)));
        coordinator.Vocab = vocabService;
        coordinator.VocabSavePicker = new Win32VocabSavePicker();
        // F17.1: backup and restore. Packages the installation has: the shipped catalog, the native providers and the user-installed ones (identities only; no code is ever exported or imported).
        var installationRows = new PluginInstallationRepository(db);
        coordinator.Backup = new BackupService(paths, settings, secrets, new DpapiProtector(), clock, new BackupHost(typeof(Program).Assembly.GetName().Version?.ToString() ?? "0",
            () =>
            {
                var available = BuiltInCatalog.Packages.ToDictionary(p => p.PackageId, p => (string?)null);
                available["native.els"] = null; available["native.sapi"] = null;
                foreach (var installed in pluginInstaller.Installed()) available[installed.Id] = installed.Version;
                return available;
            },
            () => [.. installationRows.ActiveRecords().Select(r => new BackupPluginRef(r.PackageId, r.Version, r.Signer, r.Hash))],
            () => new BackupLocalData(vocabService.Count(), vocabService.Status([.. VocabCatalog.All.Select(v => v.InstanceId)]).Sum(s => s.Pending + s.Retrying + s.Failed + s.Uncertain))));
        coordinator.BackupPicker = new Win32BackupPicker();
        // F17.2: About (facts, log folder, diagnostics export) and the data-clean entries. The diagnostics file is built by allow-listing log fields and is checked against the
        // same secret/identity set the log masks with (saved keys, user and machine names, account names, proxy host and user), kept current by SensitiveRegistry.
        var about = new AboutInfo(typeof(Program).Assembly.GetName().Version?.ToString() ?? "0",
            (typeof(Program).Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0").Split('+')[0],
            System.Runtime.InteropServices.RuntimeInformation.OSDescription, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "");
        var diagnosticsExporter = new DiagnosticsExporter(paths, clock, log.Literals, () =>
        {
            var s = config.State.Effective;
            return new DiagnosticsInfo(about.Version, about.Build, about.Os, about.Runtime, s.General.UiLanguage, s.Network.ProxyMode.ToString().ToLowerInvariant(),
                s.Accounts.Count, secrets.Entries().Count, s.Instances.Count, s.Services.Count(x => x.Enabled), favorites.ActiveCount(),
                File.Exists(paths.Database) ? new FileInfo(paths.Database).Length : 0);
        });
        coordinator.About = new AboutService(about, paths, diagnosticsExporter, Win32FolderOpener.Open);
        coordinator.DiagnosticsPicker = new Win32DiagnosticsPicker();
        coordinator.DataClean = new DataCleanService(paths, clock, config, secrets, leases, keptScreenshots.Store, favorites);
        coordinator.PluginInstaller = pluginInstaller;
        coordinator.PluginUpdates = new Susu.Plugins.Install.PluginUpdateService(pluginInstaller, source: null, Path.Combine(paths.UserPlugins, ".downloads")); // no update feed is specified yet (DEV-PLAN F16.2): no source, so the page offers no check
        // F18.2: application update. No source is registered unless update-source.json names an https manifest (none is by default); the embedded production key slot is empty
        // in this build, so even a configured source reports 'no trusted keys' instead of offering anything. Checks may run on the shared schedule; install only on the user's command.
        int InFlightNow() => (coordinator.IsRecording ? 1 : 0) + (coordinator.VideoJobs?.Active is not null ? 1 : 0)
            + (translation.Supervisor is { } flightSupervisor && flightSupervisor.TryGetCurrent(out var flightCurrent) ? flightCurrent!.InFlightCalls().Count : 0);
        string installFolder = AppContext.BaseDirectory.TrimEnd('\\');
        string appVersion = AppVersionText();
        var appUpdater = new Susu.Plugins.AppUpdate.AppUpdater(installFolder, paths.Database, paths.Updates, new ProcessUpdateEnvironment(instanceName, mode.DataRoot));
        var updateTrust = new Susu.Plugins.AppUpdate.UpdateTrustStore(Path.Combine(paths.Updates, "trust.json"), Susu.Plugins.AppUpdate.UpdateKeyring.Production);
        var updatePrefs = new Susu.Plugins.AppUpdate.UpdatePrefs(Path.Combine(paths.Updates, "schedule.json"));
        Susu.Abstractions.IAppUpdateSource? appUpdateSource = Susu.Plugins.AppUpdate.UpdateSourceConfig.Load(paths.Updates) is { } manifestUrl
            ? new Susu.Plugins.AppUpdate.SignedManifestAppUpdateSource(manifestUrl, new Susu.Plugins.AppUpdate.HttpUpdateTransport(new HttpClient { Timeout = TimeSpan.FromMinutes(10) }),
                new Susu.Plugins.AppUpdate.UpdateVerifier(updateTrust), updateTrust) : null;
        var appUpdates = new Susu.Plugins.AppUpdate.AppUpdateService(appVersion, appUpdateSource, appUpdater, updatePrefs, clock, InFlightNow,
            new ProcessUpdateLauncher(installFolder, paths.Updates, mode.DataRoot), keyringEmbedded: !Susu.Plugins.AppUpdate.UpdateKeyring.Production.IsEmpty);
        coordinator.AppUpdates = appUpdates;
        coordinator.PluginPicker = new Win32PluginPackagePicker();
        coordinator.MediaPicker = new Win32MediaPicker(mediaTokens);
        coordinator.SubtitleSavePicker = new Win32SubtitleSavePicker();
        coordinator.VideoJobs = new VideoJobs(mediaTokens, new MediaFoundationDecoder(), new LeasedFiles(leases),
            () => translation.Supervisor is { } videoSupervisor ? PluginAsrProviders.Create(config.State.Effective, SpeechSlot.VideoAsr, secrets.Has, videoSupervisor, schemas) : null,
            () => VideoTranslatorOf(translation.Providers(config.State.Effective), config.State.Effective.Speech.VideoTranslator) is { } videoTranslator
                ? new SingleItemSubtitleProvider(videoTranslator, () => config.State.Effective.Snapshot(), videoTranslator.ServiceId == "mymemory" ? "video.quota.sharedDaily" : null) : null);

        using var tray = new TrayIcon(dispatcher, assets);
        platform.WindowRequested += coordinator.OnWindowRequest;
        platform.PageMessage += coordinator.OnPageMessage;
        platform.HotkeyPressed += coordinator.OnHotkey;
        platform.Diagnostic += message => log.Event("shell", ("code", message));
        platform.Timing += (kind, phase, ms) => log.Event("timing", ("window", kind.ToString()), ("phase", phase), ("durationMs", ms));
        coordinator.Diagnostic += message => log.Event("ui", ("code", message));
        coordinator.Timing += (kind, phase, ms) => log.Event("timing", ("window", kind.ToString()), ("phase", phase), ("durationMs", Math.Round(ms, 1)));
        dispatcher.ActivateRequested += coordinator.OnActivateRequest;
        // F18.1: the installer asks the running instance to exit (or how many tasks are in flight) through the instance's own message window.
        dispatcher.InFlightProvider = InFlightNow;
        tray.DoubleClick += coordinator.OnTrayDoubleClick;
        tray.MenuRequested += coordinator.OnTrayMenu;
        // F12.3: a recording that continues in a minimized (hidden) voice window stays visible as the icon's tooltip (DESIGN 窗口关闭).
        coordinator.RecordingStatusChanged += (phase, captured) =>
            tray.SetTip(phase is null ? "Su-Su" : $"Su-Su · {(config.State.Effective.General.UiLanguage == "en" ? (phase == "paused" ? "Recording paused" : "Recording") : (phase == "paused" ? "录音已暂停" : "录音中"))} {(int)captured.TotalMinutes}:{captured.Seconds:00}");
        bool exiting = false;
        platform.ExitRequested += () => { if (!exiting) { exiting = true; dispatcher.Quit(); } };
        dispatcher.InstallerExitRequested += () => { if (!exiting) { exiting = true; log.Event("app.exit-requested", ("by", "installer")); dispatcher.Quit(); } };

        if (!tray.Add()) log.Event("tray.add-failed");
        coordinator.Start();
        vocabService.Start();
        // F18.2 / F16.2: one low-priority serial schedule for the application and plugin update checks (15 s after start, then at most every 24 h). Check only.
        using var updateSchedule = new CancellationTokenSource();
        if (appUpdates.Available || coordinator.PluginUpdates?.Available == true)
        {
            var checks = new List<Func<CancellationToken, Task>>();
            if (appUpdates.Available) checks.Add(async token => { await appUpdates.CheckAsync(automatic: true, token).ConfigureAwait(false); });
            if (coordinator.PluginUpdates is { Available: true } pluginUpdates) checks.Add(async token => { await pluginUpdates.CheckAsync(token).ConfigureAwait(false); });
            _ = Task.Run(async () =>
            {
                try { await new Susu.Plugins.AppUpdate.UpdateSchedule(clock, updatePrefs, checks).RunAsync(updateSchedule.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            });
        }
        var failed = coordinator.HotkeyResults.Where(r => !r.Value).Select(r => r.Key).ToList();
        if (failed.Count > 0)
            tray.Notify("Su-Su", config.State.Effective.General.UiLanguage == "en" ? "A hotkey could not be registered. Choose another one in Settings." : "有快捷键注册失败，请在设置中更换。");

        if (InstallOptions.ApplyPending(paths, config) is { } installed) log.Event("install.options", ("launchAtStartup", installed));
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

        _ = Task.Run(async () => // F18.3: the new version counts as started once it has been running for 15 s with the message loop up
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15), updateSchedule.Token).ConfigureAwait(false); new Susu.Plugins.AppUpdate.FirstStartGuard(paths.Updates).Confirm(AppVersionText()); }
            catch (Exception e) when (e is OperationCanceledException or IOException or UnauthorizedAccessException) { }
        });
        dispatcher.Run();
        updateSchedule.Cancel();

        coordinator.ReleaseAudio(); // a recording still running (or a minimized one) is discarded and the microphone released (REC02)
        coordinator.VideoJobs?.CancelActive(); // F14.2: a running video job stops its upload and releases its slices
        vocabService.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); // F15.4: a call in flight is cancelled (its row becomes Uncertain, not resent blindly)
        log.Event("app.exit");
        (hostControl.Target as IDisposable)?.Dispose();
        translation.Supervisor?.Dispose(); // bounded: kills the plugin-host child process (ARCHITECTURE 5.2)
        smoke?.Finish();
#if DEV_PREVIEW
        if (guard is not null) return guard.ExitCode;
#endif
        return smoke?.ExitCode ?? measure?.ExitCode ?? 0;
    }

    /// <summary>Keep-warm override: 2 s for --smoke; --release-after-seconds only in development builds (PER03 cold runs).</summary>
    /// <summary>F14.4: the video translation service: the one SetSpeechB names when it is still enabled, else the first enabled service.</summary>
    private static ITranslationProvider? VideoTranslatorOf(IEnumerable<ITranslationProvider> providers, string selected)
        => providers.FirstOrDefault(p => selected != "" && p.ServiceId == selected) ?? providers.FirstOrDefault();

    private static TimeSpan? ReleaseAfter(StartupMode mode)
        => mode.SmokeReport is not null ? TimeSpan.FromSeconds(2)
        : Program.DevelopmentBuild && mode.ReleaseAfterSeconds is int seconds ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>The plugin-host controller exists only after the translation runtime is built, which itself needs the installer: this forwards to it once it is set.</summary>
    private sealed class LateHostControl : IPluginHostControl
    {
        public IPluginHostControl? Target { get; set; }
        public IReadOnlyList<PluginTaskInfo> InFlight(string packageId) => Target?.InFlight(packageId) ?? [];
        public int Restart(string packageId) => Target?.Restart(packageId) ?? 0;
    }

    /// <summary>Providers are built from the settings each call (F06.3a), so service changes need no restart.</summary>
    private sealed record TranslationRuntime(Supervisor<HostSession>? Supervisor, Func<AppSettings, IReadOnlyList<ITranslationProvider>> Providers,
        Func<AppSettings, string, ITranslationProvider?> ValidationProvider, NetworkBrokerProvider? Network = null, Func<HostSession.Options>? ProbeOptions = null);

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
        IReadOnlyDictionary<string, IReadOnlyList<ConfigField>> schemas, Susu.Plugins.Install.PluginInstaller installer, PluginKvRepository kv)
    {
        try
        {
            var networkBrokerProvider = new NetworkBrokerProvider(settings, secrets);
            var accountAuthorization = new AccountAuthorization(() => (config.State.Effective.Accounts, config.State.Effective.Instances));
            string executable = Environment.ProcessPath ?? Path.Combine(exeFolder, "susu.exe");
            var hostOptions = new HostSession.Options(executable, exeFolder, "quickjs",
                MakeBroker: () => new Broker(networkBrokerProvider, leases, secrets, accountAuthorization) { PluginKv = kv });
            // F16.2: the installed-plugin folder is readable by the sandbox once it exists; each launch asks again so a first install is picked up.
            HostSession.Options Options() => hostOptions with { ExtraReadRoots = Directory.Exists(installer.PackagesFolder) ? [installer.PackagesFolder] : null };
            var supervisor = new Supervisor<HostSession>(
                () => PluginTranslationProviders.LoadAll(HostSession.Start(Options()), (package, _) => log.Event("plugin.load-failed", ("package", package)),
                    installer.ActiveDirectory, installer.ActivePackages),
                clock, idleTimeout);
            supervisor.RestartFailed += error => log.Event("plugin-host.restart-failed", ("code", error.GetType().Name));
            supervisor.Stalled += () => log.Event("plugin-host.stalled");
            return new TranslationRuntime(supervisor, settings => PluginTranslationProviders.Build(settings, secrets.Has, supervisor, schemas),
                (settings, serviceId) => PluginTranslationProviders.ForValidation(settings, serviceId, supervisor, schemas), networkBrokerProvider, () => hostOptions with { ExtraReadRoots = [installer.PackagesFolder] });
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

    /// <summary>The Windows machine id (registered with the log's masking set so it never appears in a log or a diagnostics file). Null when it cannot be read.</summary>
    private static string? MachineGuid()
    {
        try { return OperatingSystem.IsWindows() ? Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null) as string : null; }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// F18.2: at start, an update journal that is not at rest is recovered. Stages that did not touch the install folder are recovered here; a half-replaced install folder is restored
    /// by the helper (a copy of this executable, because the files are in use), and this start ends so the helper can run. Returns true when the helper was started.
    /// </summary>
    private static bool RecoverUpdate(AppPaths paths, string? dataRoot, RedactingLog log)
    {
        try
        {
            UpdateHostCleanup(paths);
            using var gate = UpdateHost.TryLock(dataRoot);
            if (gate is null) return false; // a helper is running right now: it owns the journal
            string installDir = AppContext.BaseDirectory.TrimEnd('\\');
            var updater = new Susu.Plugins.AppUpdate.AppUpdater(installDir, paths.Database, paths.Updates, new ProcessUpdateEnvironment(InstanceName(dataRoot), dataRoot));
            if (updater.NeedsFileRecovery())
            {
                gate.ReleaseMutex();
                bool started = new ProcessUpdateLauncher(installDir, paths.Updates, dataRoot).Launch("--recover-update");
                log.Event("update.recover-helper", ("started", started));
                return started;
            }
            var result = updater.Recover();
            if (result.Action is not "none") log.Event("update.recovered", ("action", result.Action), ("stage", result.Stage ?? ""));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ApplicationException) { log.Event("update.recover-failed", ("code", e.GetType().Name)); }
        return false;
    }

    internal static string AppVersionText()
    {
        var v = typeof(Program).Assembly.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
    }

    /// <summary>
    /// F18.3: counts this start of a freshly committed version. A version whose first starts were never confirmed (see the 15 s confirmation near the message loop) twice does not run a
    /// third time: the helper restores the paired old binaries and database (loop guard: the failed version is recorded and never offered again) and starts the old version.
    /// Returns true when the helper took over and this process must end.
    /// </summary>
    private static bool FirstStartGate(AppPaths paths, string? dataRoot, RedactingLog log)
    {
        try
        {
            var guard = new Susu.Plugins.AppUpdate.FirstStartGuard(paths.Updates);
            if (guard.OnStart(AppVersionText()) == Susu.Plugins.AppUpdate.StartDecision.Proceed) return false;
            string installDir = AppContext.BaseDirectory.TrimEnd('\\');
            log.Event("update.first-start-failed", ("version", AppVersionText()));
            bool started = new ProcessUpdateLauncher(installDir, paths.Updates, dataRoot).Launch("--rollback-update", "--reason", Susu.Plugins.AppUpdate.AppUpdater.FirstStartFailed);
            if (!started) guard.Clear(); // no helper could be started: do not lock the user out, run this version again
            return started;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ApplicationException) { log.Event("update.first-start-gate-failed", ("code", e.GetType().Name)); return false; }
    }

    private static void UpdateHostCleanup(AppPaths paths) => ProcessUpdateLauncher.CleanHelpers(paths.Updates);

    /// <summary>One instance per user; a separate data root (tests, development) is its own instance.</summary>
    internal static string InstanceName(string? dataRoot)
        => dataRoot is null ? "Su-Su.Instance" : $"Su-Su.Instance.{AtomicFile.Hash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).ToLowerInvariant()))[..16]}";
}
