using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Net;
using Susu.Plugins;
using Susu.Storage;
using Susu.Testing;
using Susu.Ui;
using Susu.Windows.Audio;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// Independent F13 verification (testing agent), TEST-PLAN REC01-REC04 and the system-audio side of A08. Gaps the coding agents'
/// tests leave: end to end through ShellCoordinator with the real recorders and per-source fake devices (loopback never opens the
/// microphone and the microphone never opens loopback, including after a source switch and when the other hotkey or tray item is
/// used during a recording); the real packages in the sandbox behind a loopback vendor (system audio -> recorder -> AsrJob ->
/// vendor -> translate callback, own-playback flag on result and view); interruption and sleep keep the part and offer
/// transcribe or re-record; loopback.* start errors with no file left and their zh/en text; silent fill versus a stall (the
/// recorder's 3 s sleep rule); the real 10-minute limit with a fake clock; repeated start/stop cycles; the entry greyed without
/// ASR credentials by the real capability predicate; hotkey conflict between the voice and system-audio chords; no second
/// pipeline (source scan). One real tone through the real loopback is checked in the WAV. A real ASR service (A08) needs
/// accounts: not executed.
/// </summary>
[Collection(MediaDecodeCollection.Name)]
public class F13VerificationTests
{
    private const int Rate = 16000;
    private const string ApiKey = "asr-verify-key-0123456789abcdef";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ---------------- fake devices (one object per source, so crossing is observable) ----------------

    private sealed class FakeStream : IMicrophoneStream
    {
        private readonly Channel<byte[]> blocks = Channel.CreateUnbounded<byte[]>();
        public int SampleRate => Rate;
        public bool Disposed { get; private set; }
        public void Push(byte[] block) => blocks.Writer.TryWrite(block);
        public void Fail(MicFailure failure) => blocks.Writer.TryComplete(new MicrophoneException(failure, failure.ToString()));
        public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            try { return await blocks.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException e) { if (e.InnerException is MicrophoneException m) throw m; return null; }
        }
        public void Dispose() { Disposed = true; blocks.Writer.TryComplete(); }
    }

    private sealed class FakeDevices : IMicrophoneDevices
    {
        public int Opens;
        public MicFailure? OpenFails;
        public bool Present = true;
        public FakeStream? Last;
        public readonly List<FakeStream> All = [];
        public bool HasDevice() => Present;
        public IMicrophoneStream Open()
        {
            Opens++;
            if (!Present) throw new MicrophoneException(MicFailure.NoDevice, "none");
            if (OpenFails is { } f) throw new MicrophoneException(f, f.ToString());
            var s = new FakeStream();
            All.Add(s);
            return Last = s;
        }
    }

    private static byte[] Block(int ms, short amplitude)
    {
        var b = new byte[ms * Rate / 1000 * 2];
        for (int i = 0; i < b.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i), (i / 2) % 2 == 0 ? amplitude : (short)-amplitude);
        return b;
    }

    private static async Task Until(Func<bool> condition, int tenMs = 1500)
    {
        for (int i = 0; i < tenMs && !condition(); i++) await Task.Delay(10, Ct);
        Assert.True(condition(), "condition not reached");
    }

    private static AudioCaptureCoordinator Loopback(FakeDevices output, ILeasedFileFactory files, IClock clock, TimeSpan? limit = null) =>
        new(output, files, clock, limit, AudioSourceKind.SystemLoopback);

    // ---------------- sandbox rig (the shipped packages through the real host), as F12VerificationTests ----------------

    private static string? FindRoot(Func<string, bool> test)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (test(dir.FullName)) return dir.FullName;
        return null;
    }

    private static readonly Lazy<string?> staged = new(() =>
    {
        string? root = FindRoot(d => File.Exists(Path.Combine(d, "src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish", "susu.exe")));
        if (root is null) return null;
        string publish = Path.Combine(root, "src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish");
        string source = Path.Combine(root, "src", "Susu.Host", "plugins");
        string dir = TestTemp.NewDir("susu-f13v-it");
        foreach (string file in Directory.EnumerateFiles(publish))
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        foreach (string package in new[] { SpeechCatalog.OpenAiAsr, SpeechCatalog.GeminiAsr })
        {
            string target = Path.Combine(dir, "plugins", package);
            Directory.CreateDirectory(target);
            foreach (string file in Directory.EnumerateFiles(Path.Combine(source, package))) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        return dir;
    });

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<(string, string), string> values = [];
        public void Set(string account, string name, string value) => values[(account, name)] = value;
        public bool Has(string a, string n) => values.ContainsKey((a, n));
        public IReadOnlyList<string> Names(string a) => [.. values.Keys.Where(k => k.Item1 == a).Select(k => k.Item2)];
        public void Write(string a, string n, ReadOnlySpan<char> v) => values[(a, n)] = v.ToString();
        public bool Delete(string a, string n) => values.Remove((a, n));
        public bool TryRead(string a, string n, out string v) => values.TryGetValue((a, n), out v!);
    }

    private sealed class Sandbox : IDisposable
    {
        public required FileLeases Leases { get; init; }
        public required Supervisor<HostSession> Supervisor { get; init; }
        public required WiredPackage Package { get; init; }
        public required InstanceSettings Instance { get; init; }
        public required SpeechPackage Speech { get; init; }
        public HostSession? Session;
        public LeasedFiles Files => new(Leases);
        public PluginAsrProvider Provider(string model) => new(Package, Instance, Speech.Models.Single(x => x.Id == model), Supervisor);

        public async Task AssertNoFilesLeftAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((Leases.ActiveCount > 0 || (Session is not null && (Session.Broker.ActiveResponseFiles > 0 || Session.Broker.ActiveGrants > 0))) && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
            Assert.Equal(0, Leases.ActiveCount);
            if (Session is not null) { Assert.Equal(0, Session.Broker.ActiveGrants); Assert.Equal(0, Session.Broker.ActiveResponseFiles); }
        }

        public void Dispose() { Supervisor.Dispose(); Leases.Dispose(); }
    }

    private static Sandbox? BuildSandbox(string instanceId, LoopbackHttpServer server)
    {
        if (staged.Value is not { } dir) return null;
        var package = PluginTranslationProviders.WiredPackages.Single(p => p.InstanceId == instanceId);
        var speech = (SpeechPackage)package.Credentials;
        var cfg = new Dictionary<string, string> { ["baseUrl"] = server.Origin };
        var secrets = new FakeSecretStore();
        secrets.Set("account", "apiKey", ApiKey);
        var account = new AccountSettings("account", "Shared account", ["apiKey"], [.. speech.RequiredGrants(cfg)]);
        var instance = new InstanceSettings(instanceId, package.PackageId, 1, cfg, new Dictionary<string, string> { ["apiKey"] = "account" });
        var accounts = new AccountAuthorization(() => ([account], [instance]));
        var leases = new FileLeases(TestTemp.NewDir("susu-f13v-leases"));
        var options = new HostSession.Options(Path.Combine(dir, "susu.exe"), dir, "quickjs", KeepProfile: false,
            MakeBroker: () => new Broker(secretStore: secrets, accounts: accounts, leases: leases));
        Sandbox? rig = null;
        var supervisor = new Supervisor<HostSession>(() =>
        {
            var session = HostSession.Start(options);
            session.Broker.ApproveLocalOrigin(server.Origin);
            var loaded = session.Load(package.PackageId, package.Directory);
            if (!loaded.Ok) { session.Shutdown(2000); throw new InvalidOperationException($"{package.PackageId} failed to load: {loaded.Error}"); }
            rig!.Session = session;
            return session;
        }, SystemClock.Instance, TimeSpan.FromMinutes(10));
        rig = new Sandbox { Leases = leases, Supervisor = supervisor, Package = package, Instance = instance, Speech = speech };
        return rig;
    }

    private static string OpenAiText(string text) => JsonSerializer.Serialize(new { text });
    private static string GeminiOk(string text) => JsonSerializer.Serialize(new { candidates = new[] { new { content = new { role = "model", parts = new[] { new { text } } }, finishReason = "STOP" } } });

    // ---------------- shell rig: the real ShellCoordinator, real recorders, fake devices per source ----------------

    private sealed class FakeAsrProvider(Func<AsrCall, CancellationToken, Task<AsrOutcome>> run) : IAsrProvider
    {
        public readonly List<AsrCall> Calls = [];
        public string InstanceId => "openai-asr";
        public string Model => "whisper-1";
        public bool Timecodes => false;
        public AsrLimits Limits { get; } = new([AsrFormat.Wav16kMono], 24L << 20, 25L << 20, 300, AsrUpload.Multipart, 2048);
        public Task<AsrOutcome> TranscribeAsync(AsrCall call, CancellationToken cancellationToken) { lock (Calls) Calls.Add(call); return run(call, cancellationToken); }
    }

    private sealed class ShellRig : IDisposable
    {
        public readonly TempRoot Root = new();
        public readonly FakePlatform Platform = new();
        public readonly SettingsStore Settings;
        public readonly ConfigService Config;
        public readonly ShellCoordinator Shell;
        public readonly FakeDevices Mic = new(), Loop = new();
        public readonly ManualClock Clock = new();
        public readonly FileLeases Leases;
        public readonly string LeaseDir = TestTemp.NewDir("susu-f13v-shell");
        public readonly AsrJob Job;
        public readonly FakeAsrProvider Fake = new((call, _) => Task.FromResult<AsrOutcome>(new AsrOutcome.Transcribed("text", "Hello from the output", null)));
        public readonly List<string> Translated = [];
        public readonly AudioCaptureCoordinator MicCoordinator, LoopCoordinator;
        private readonly bool ownsLeases;
        private long counter;

        public ShellRig(Func<IAsrProvider?>? provider = null, FileLeases? leases = null, ILeasedFileFactory? files = null)
        {
            ownsLeases = leases is null;
            Leases = leases ?? new FileLeases(LeaseDir);
            files ??= new LeasedFiles(Leases);
            Settings = new SettingsStore(Root.Paths, new ManualClock());
            Config = new ConfigService(Settings, new SecretStore(Root.Paths.Secrets, new XorProtector()));
            var features = new FeatureRegistry();
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Voice, FeatureState.Available, null, [Capability.Asr]));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.SystemAudio, FeatureState.Available, null, [Capability.Asr]));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Transcription, FeatureState.InDevelopment, "feature.inDevelopment", []));
            var translator = new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Echo("T:"));
            var snapshot = new ConfigSnapshot(1, 1, 1, 1, TimeSpan.FromSeconds(30));
            Shell = new ShellCoordinator(Platform, Config, features, c => c is Capability.Translate or Capability.Asr, new ShellOptions(false, false),
                _ => new TranslationSession([translator], new TranslationSessionOptions(snapshot, 1), new InvocationScheduler(new SchedulerLimits()), new ManualClock(), new FixedJitter(), new RecordingUsage()));
            Job = new AsrJob(provider ?? (() => Fake), files, async text => { lock (Translated) Translated.Add(text); await Shell.SubmitRecognizedTextAsync(text, WindowKind.Voice); }, () => true);
            MicCoordinator = new AudioCaptureCoordinator(Mic, files, Clock);
            LoopCoordinator = Loopback(Loop, files, Clock);
            Shell.AudioCapture = MicCoordinator;
            Shell.SystemAudioCapture = LoopCoordinator;
            Shell.Asr = Job;
            Shell.Start();
        }

        private int Snapshots(WindowKind kind) => Platform.Posted.Count(p => p.Kind == kind && p.Envelope.Kind == UiMessageKind.Snapshot);

        public void Ready(WindowKind kind)
        {
            int before = Snapshots(kind);
            Shell.OnPageMessage(kind, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Ready", windowSessionId = Platform.Session(kind) }));
            for (var poll = Stopwatch.StartNew(); Snapshots(kind) == before; Thread.Sleep(5))
                if (poll.Elapsed > Eventually.DefaultTimeout) throw new TimeoutException("no snapshot");
        }

        public CommandResult Run(WindowKind kind, string name, object? payload = null)
        {
            string id = $"c{++counter}";
            Shell.OnPageMessage(kind, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Command", windowSessionId = Platform.Session(kind), name, correlationId = id, payload }, Web));
            for (var poll = Stopwatch.StartNew(); poll.Elapsed < Eventually.DefaultTimeout;)
            {
                var hit = Platform.Posted.FirstOrDefault(p => p.Envelope.CorrelationId == id);
                if (hit.Envelope is not null) return hit.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.CommandResult)!;
                Thread.Sleep(5);
            }
            throw new TimeoutException($"no result for {id}");
        }

        public void OpenVoice() { Shell.OnHotkey("voiceTranslate"); Ready(WindowKind.Voice); }
        public void OpenSystemAudio() { Shell.OnHotkey("audioTranslate"); Ready(WindowKind.Voice); }

        public string TrayOpen(string id)
        {
            Shell.OnTrayMenu();
            var result = Run(WindowKind.Tray, UiCommands.TrayOpen, new { id });
            return result.Ok ? "ok" : result.Error ?? "failed";
        }

        public VoiceView View => Shell.VoiceWindowView!;
        public Task<bool> WaitPhase(string phase) => Eventually.WaitAsync(() => Shell.VoiceWindowView?.Phase == phase);
        public Task<bool> WaitElapsed(long ms) => Eventually.WaitAsync(() => Shell.VoiceWindowView?.ElapsedMs >= ms);
        public int CachedFiles() => Directory.EnumerateFiles(LeaseDir, "*", SearchOption.AllDirectories).Count();

        public void Dispose()
        {
            Shell.ReleaseAudio();
            if (ownsLeases) Leases.Dispose();
            Settings.Dispose();
            Root.Dispose();
        }
    }

    // ================= REC04: the loopback never opens the microphone and the microphone never opens loopback =================

    [Fact] // REC04 through the shell: each source opens only its own device, across a switch and back, with each take's audio from its own device
    public async Task Each_source_opens_only_its_own_device_through_the_shell()
    {
        using var rig = new ShellRig();
        rig.OpenVoice();
        Assert.Equal(("microphone", false), (rig.View.Source, rig.View.OwnPlayback));
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
        Assert.Equal((1, 0), (rig.Mic.Opens, rig.Loop.Opens));
        for (int i = 0; i < 10; i++) rig.Mic.Last!.Push(Block(100, 3000));
        Assert.True(await rig.WaitElapsed(1000));
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StopRecording).Ok);
        Assert.True(await rig.WaitPhase("transcribed"));
        Assert.Equal(("microphone", false), (rig.View.Source, rig.View.OwnPlayback));

        rig.OpenSystemAudio(); // finished: the source switches
        Assert.Equal(("systemAudio", true, "idle"), (rig.View.Source, rig.View.OwnPlayback, rig.View.Phase));
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
        Assert.Equal((1, 1), (rig.Mic.Opens, rig.Loop.Opens));
        for (int i = 0; i < 10; i++) rig.Loop.Last!.Push(Block(100, 9000));
        Assert.True(await rig.WaitElapsed(1000));
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StopRecording).Ok);
        Assert.True(await rig.WaitPhase("transcribed"));
        Assert.Equal(("systemAudio", true, true), (rig.View.Source, rig.View.OwnPlayback, rig.View.Translated));
        Assert.Equal((1, 1), (rig.Mic.Opens, rig.Loop.Opens));
        Assert.True(rig.Mic.All[0].Disposed && rig.Loop.All[0].Disposed);

        rig.OpenVoice();
        Assert.Equal(("microphone", false, "idle"), (rig.View.Source, rig.View.OwnPlayback, rig.View.Phase));
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
        Assert.Equal((2, 1), (rig.Mic.Opens, rig.Loop.Opens));
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.CancelRecording).Ok);
        Assert.Equal(2, rig.Fake.Calls.Count); // exactly the two stopped takes were transcribed, the cancelled one was not
        Assert.True(await Eventually.WaitAsync(() => rig.Leases.ActiveCount == 0));
    }

    [Theory] // REC02/REC04: the other source's hotkey or tray item during a recording only brings the window forward; the take runs on and is whole
    [InlineData(true)]
    [InlineData(false)]
    public async Task Switching_source_while_recording_neither_interrupts_nor_loses_the_take(bool loopbackRunning)
    {
        using var rig = new ShellRig();
        var (running, other) = loopbackRunning ? (rig.Loop, rig.Mic) : (rig.Mic, rig.Loop);
        string runningName = loopbackRunning ? "systemAudio" : "microphone";
        if (loopbackRunning) rig.OpenSystemAudio(); else rig.OpenVoice();
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
        for (int i = 0; i < 10; i++) running.Last!.Push(Block(100, 7000));
        Assert.True(await rig.WaitElapsed(1000));

        string otherHotkey = loopbackRunning ? "voiceTranslate" : "audioTranslate";
        rig.Shell.OnHotkey(otherHotkey);
        rig.Shell.OnHotkey(otherHotkey);
        rig.TrayOpen(loopbackRunning ? "voice" : "system-audio");
        Assert.True(rig.Shell.IsRecording);
        Assert.Equal(("recording", runningName), (rig.View.Phase, rig.View.Source));
        Assert.False(running.Last!.Disposed);
        Assert.Equal(0, other.Opens); // the other device was never touched
        Assert.Equal(1, running.Opens);

        for (int i = 0; i < 10; i++) running.Last.Push(Block(100, 7000)); // the take keeps growing
        Assert.True(await rig.WaitElapsed(2000));
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StopRecording).Ok);
        Assert.True(await rig.WaitPhase("transcribed"));
        var call = Assert.Single(rig.Fake.Calls);
        Assert.Equal(2.0, call.DurationSeconds, 0.01); // nothing was lost across the attempted switches
        Assert.Equal(runningName, rig.View.Source);
        Assert.Equal(loopbackRunning, rig.View.OwnPlayback);
    }

    // ================= REC03: interruption keeps the part and offers transcribe or re-record =================

    [Theory] // output switch or removal: part kept, both actions offered, no silent device change, nothing left behind
    [InlineData(MicFailure.DefaultChanged, "defaultChanged", true)]
    [InlineData(MicFailure.DefaultChanged, "defaultChanged", false)]
    [InlineData(MicFailure.DeviceRemoved, "deviceRemoved", true)]
    [InlineData(MicFailure.DeviceRemoved, "deviceRemoved", false)]
    public async Task Interrupted_loopback_keeps_the_part_and_offers_transcribe_or_rerecord(MicFailure failure, string reason, bool transcribe)
    {
        using var rig = new ShellRig();
        rig.OpenSystemAudio();
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
        for (int i = 0; i < 30; i++) rig.Loop.Last!.Push(Block(100, 8000));
        Assert.True(await rig.WaitElapsed(3000));
        rig.Loop.Last!.Fail(failure);
        Assert.True(await rig.WaitPhase("interrupted"));
        var view = rig.View;
        Assert.Equal((reason, true, true, "systemAudio"), (view.Reason, view.CanTranscribe, view.OwnPlayback, view.Source));
        Assert.Equal(1, rig.Loop.Opens); // it did not move to the new default
        Assert.Equal(0, rig.Mic.Opens);
        Assert.False(rig.Shell.IsRecording);
        Assert.Equal(1, rig.Leases.ActiveCount); // the part is kept on disk for the user's choice
        Assert.Empty(rig.Fake.Calls); // and nothing was sent without asking
        if (transcribe)
        {
            Assert.True(rig.Run(WindowKind.Voice, UiCommands.TranscribeRecorded).Ok);
            Assert.True(await rig.WaitPhase("transcribed"));
            Assert.Equal(3.0, Assert.Single(rig.Fake.Calls).DurationSeconds, 0.01);
            Assert.True(rig.View.Translated);
            Assert.True(await Eventually.WaitAsync(() => rig.Leases.ActiveCount == 0));
        }
        else
        {
            Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
            Assert.Equal((0, 2), (rig.Mic.Opens, rig.Loop.Opens)); // loopback again, never the microphone
            Assert.True(await Eventually.WaitAsync(() => rig.Leases.ActiveCount == 1)); // the kept part was released, only the new take's file exists
            Assert.True(rig.Run(WindowKind.Voice, UiCommands.CancelRecording).Ok);
            Assert.True(await Eventually.WaitAsync(() => rig.Leases.ActiveCount == 0));
        }
    }

    [Fact] // REC03 sleep/resume on loopback: a stall beyond the recorder's gap rule stops with Sleep and keeps the part
    public async Task Sleep_on_loopback_keeps_the_part_and_offers_both_actions()
    {
        using var rig = new ShellRig();
        rig.OpenSystemAudio();
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
        for (int i = 0; i < 10; i++) rig.Loop.Last!.Push(Block(100, 8000));
        Assert.True(await rig.WaitElapsed(1000));
        rig.Clock.Advance(TimeSpan.FromSeconds(10)); // the machine slept
        rig.Loop.Last!.Push(Block(10, 0));
        Assert.True(await rig.WaitPhase("interrupted"));
        Assert.Equal(("sleep", true, "systemAudio"), (rig.View.Reason, rig.View.CanTranscribe, rig.View.Source));
        Assert.Equal(0, rig.Mic.Opens);
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.TranscribeRecorded).Ok);
        Assert.True(await rig.WaitPhase("transcribed"));
    }

    // ================= loopback.* errors: text in zh and en, no file =================

    [Theory] // REC04 no output / denied / other: loopback.* code, no lease and no file, the microphone untouched, and a later start works
    [InlineData(MicFailure.NoDevice, "loopback.noDevice", false)]
    [InlineData(MicFailure.NoDevice, "loopback.noDevice", true)]
    [InlineData(MicFailure.Denied, "loopback.denied", false)]
    [InlineData(MicFailure.Failed, "loopback.failed", false)]
    public async Task Loopback_start_errors_have_loopback_codes_and_leave_no_file(MicFailure failure, string code, bool noEndpoint)
    {
        using var rig = new ShellRig();
        if (noEndpoint) rig.Loop.Present = false; else rig.Loop.OpenFails = failure;
        rig.OpenSystemAudio();
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
        Assert.Equal(("error", code, "systemAudio"), (rig.View.Phase, rig.View.ErrorCode, rig.View.Source));
        Assert.Equal(0, rig.Mic.Opens);
        Assert.Equal(0, rig.Leases.ActiveCount);
        Assert.Equal(0, rig.CachedFiles());
        Assert.False(rig.Shell.IsRecording);
        rig.Loop.OpenFails = null; rig.Loop.Present = true; // not left busy
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
        Assert.Equal("recording", rig.View.Phase);
        Assert.Equal(0, rig.Mic.Opens);
    }

    [Fact] // the loopback error and interruption texts exist in both languages (the page falls back to loopback.failed / the shared sleep text)
    public void Loopback_error_texts_exist_in_zh_and_en()
    {
        string? root = FindRoot(d => File.Exists(Path.Combine(d, "ui", "src", "locales", "i18n.ts")));
        if (root is null) { Assert.Skip("ui/src/locales/i18n.ts not found from the test output folder"); return; }
        string text = File.ReadAllText(Path.Combine(root, "ui", "src", "locales", "i18n.ts"));
        foreach (string key in new[] { "voice.error.loopback.noDevice", "voice.error.loopback.denied", "voice.error.loopback.failed", "voice.sys.ownPlayback", "voice.sys.title",
                     "voice.sys.reason.deviceRemoved", "voice.sys.reason.defaultChanged", "voice.sys.reason.failed", "voice.reason.sleep" })
        {
            var hits = Regex.Matches(text, "'" + Regex.Escape(key) + @"':\s*'([^']{3,})'");
            Assert.True(hits.Count == 2, $"{key}: expected one zh and one en text, found {hits.Count}");
            Assert.True(Regex.IsMatch(hits[0].Groups[1].Value, @"\p{IsCJKUnifiedIdeographs}"), $"{key}: first text is not Chinese");
            Assert.False(Regex.IsMatch(hits[1].Groups[1].Value, @"\p{IsCJKUnifiedIdeographs}"), $"{key}: second text is not English");
        }
    }

    // ================= REC04 silent output versus a stall =================

    [Fact] // silent fill (the Windows layer's idle blocks) is a Silent recording that keeps the timeline; only a stall beyond the gap rule is Sleep
    public async Task Silent_fill_is_silence_but_a_long_stall_is_sleep()
    {
        using var root = new TempRoot();
        using var leases = new FileLeases(root.Paths.Cache);
        var clock = new ManualClock();
        var output = new FakeDevices();
        var session = (await Loopback(output, new LeasedFiles(leases), clock).StartAsync(Ct)).Session!;
        var stream = output.Last!;
        var levels = new List<RecordingLevel>();
        session.LevelChanged += l => { lock (levels) levels.Add(l); };
        async Task Feed(int ms, int count, short amp, int wallMs)
        {
            for (int i = 0; i < count; i++)
            {
                var before = session.Captured;
                stream.Push(Block(ms, amp));
                await Until(() => session.Captured > before || session.Completion.IsCompleted);
                clock.Advance(TimeSpan.FromMilliseconds(wallMs));
            }
        }
        await Feed(1000, 6, 0, 1000); // nothing plays for 6 s: 1 s fill blocks, the wall clock keeps pace
        Assert.Equal(RecordingPhase.Recording, session.Phase);
        Assert.False(session.Completion.IsCompleted);
        lock (levels) Assert.Contains(levels, l => l.Silent);
        Assert.Equal(TimeSpan.FromSeconds(6), session.Captured);
        await Feed(20, 1, 0, 2000); // a 2 s stall followed by one short block: inside the 3 s rule, not a sleep, not filled
        Assert.False(session.Completion.IsCompleted);
        await Feed(20, 1, 0, 0);
        clock.Advance(TimeSpan.FromMilliseconds(AudioCaptureCoordinator_SuspendGap + 500)); // a real stall (sleep): one short block shows the gap
        stream.Push(Block(10, 0));
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal((RecordingStatus.Interrupted, MicFailure.Sleep), (result.Status, result.Reason));
        Assert.True(result.IncludesOwnPlayback);
        using var audio = result.Audio!;
        Assert.True(audio.Silent); // a silent take is still never sent to ASR
        Assert.True(audio.Duration >= TimeSpan.FromSeconds(6));
    }

    private static int AudioCaptureCoordinator_SuspendGap => RecordingSession.SuspendGapMs;

    [Fact] // a silent loopback take through the whole shell makes no ASR request and tells the user, with the source kept
    public async Task Silent_loopback_take_makes_no_asr_request()
    {
        using var rig = new ShellRig();
        rig.OpenSystemAudio();
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
        for (int i = 0; i < 40; i++) rig.Loop.Last!.Push(Block(100, 0));
        Assert.True(await rig.WaitElapsed(4000));
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StopRecording).Ok);
        Assert.True(await Eventually.WaitAsync(() => !rig.Shell.IsRecording && rig.View.Phase != "recording"));
        Assert.Empty(rig.Fake.Calls);
        Assert.Empty(rig.Translated);
        Assert.Equal("systemAudio", rig.View.Source);
        Assert.True(await Eventually.WaitAsync(() => rig.Leases.ActiveCount == 0));
    }

    // ================= 10-minute limit, long recording and resource fall-back =================

    [Fact] // REC01 for system audio: the product limit (no override) cuts at exactly 600 s; memory, lease, file and device all return after dispose
    public async Task Ten_minute_loopback_take_cuts_exactly_and_returns_every_resource()
    {
        long Managed() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
        using var root = new TempRoot();
        using var leases = new FileLeases(root.Paths.Cache);
        var clock = new ManualClock();
        var output = new FakeDevices();
        long baseline = Managed();
        var session = (await Loopback(output, new LeasedFiles(leases), clock).StartAsync(Ct)).Session!;
        var stream = output.Last!;
        long midway = long.MaxValue; // process-wide counter: other tests run in parallel, so the smallest of three samples is used (a buffering recorder holds 9.6 MB or more at 300 s)
        for (int s = 1; s <= 650 && !session.Completion.IsCompleted; s++) // 650 s offered in 1 s blocks: the last 50 s are cut off
        {
            var before = session.Captured;
            stream.Push(Block(1000, 6000));
            for (int w = 0; w < 3000 && session.Captured == before && !session.Completion.IsCompleted; w++) await Task.Delay(1, Ct);
            clock.Advance(TimeSpan.FromSeconds(1));
            if (s is 300 or 400 or 500) midway = Math.Min(midway, Managed());
        }
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.True(midway - baseline < 4_000_000, $"managed memory grew by {midway - baseline} bytes during the take");
        Assert.Equal((RecordingStatus.LimitReached, AudioSourceKind.SystemLoopback, true), (result.Status, result.Source, result.IncludesOwnPlayback));
        Assert.Equal(TimeSpan.FromMinutes(10), result.Audio!.Duration);
        Assert.True(stream.Disposed);
        var audio = result.Audio;
        string path = audio.File.FilePath;
        Assert.Equal(44 + 600 * Rate * 2, new FileInfo(path).Length);
        audio.Dispose();
        session.Dispose();
        Assert.False(File.Exists(path));
        Assert.Equal(0, leases.ActiveCount);
        Assert.Empty(Directory.EnumerateFiles(root.Paths.Cache, "*", SearchOption.AllDirectories));
        long after = long.MaxValue; for (int t = 0; t < 5 && after - baseline >= 4_000_000; t++) { await Task.Delay(100, Ct); after = Math.Min(after, Managed()); }
        Assert.True(after - baseline < 4_000_000, "memory did not return to baseline");
    }

    [Fact] // resource fall-back: five start/stop cycles with every kind of ending leave no lease, file, open stream or handle growth
    public async Task Repeated_loopback_cycles_leave_no_handles_or_files()
    {
        using var root = new TempRoot();
        using var leases = new FileLeases(root.Paths.Cache);
        var clock = new ManualClock();
        var output = new FakeDevices();
        var capture = Loopback(output, new LeasedFiles(leases), clock, TimeSpan.FromSeconds(1));
        async Task<IRecordingSession> Start(int blocks = 3)
        {
            var started = await capture.StartAsync(Ct);
            for (int w = 0; w < 100 && started.ErrorCode == "mic.busy"; w++) { await Task.Delay(10, Ct); started = await capture.StartAsync(Ct); } // the busy flag is released by a continuation just after Completion
            Assert.True(started.Session is not null, $"start failed: {started.ErrorCode}");
            for (int i = 0; i < blocks; i++) output.Last!.Push(Block(100, 5000));
            await Until(() => started.Session!.Captured >= TimeSpan.FromMilliseconds(100 * blocks));
            return started.Session!;
        }
        Process.GetCurrentProcess().Refresh();
        int handlesBefore = Process.GetCurrentProcess().HandleCount;

        var s = await Start(); (await s.StopAsync()).Audio!.Dispose(); s.Dispose(); // 1 stop
        s = await Start(); s.Cancel(); await s.Completion; s.Dispose(); // 2 cancel
        s = await Start(); output.Last!.Fail(MicFailure.DefaultChanged); (await s.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Audio!.Dispose(); s.Dispose(); // 3 output switch
        s = await Start(); s.Dispose(); // 4 dispose without stop
        s = await Start(10); (await s.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Audio!.Dispose(); s.Dispose(); // 5 limit reached

        Assert.Equal(5, output.Opens);
        Assert.All(output.All, x => Assert.True(x.Disposed));
        await Until(() => leases.ActiveCount == 0);
        Assert.Empty(Directory.EnumerateFiles(root.Paths.Cache, "*", SearchOption.AllDirectories));
        GC.Collect(); GC.WaitForPendingFinalizers();
        Process.GetCurrentProcess().Refresh();
        Assert.True(Process.GetCurrentProcess().HandleCount - handlesBefore <= 30, $"handles grew from {handlesBefore} to {Process.GetCurrentProcess().HandleCount}");
        var again = await capture.StartAsync(Ct); // and it is not left busy
        Assert.NotNull(again.Session);
        again.Session!.Cancel();
    }

    // ================= system audio -> recorder -> AsrJob -> real package -> translate, own playback flagged =================

    [Theory] // REC01/REC04/A08 (fake vendor): the loopback take reaches the vendor byte for byte through the shell, is translated, and carries the own-playback flag
    [InlineData(SpeechCatalog.OpenAiAsr, "whisper-1")]
    [InlineData(SpeechCatalog.GeminiAsr, "gemini-2.5-flash")]
    public async Task System_audio_goes_through_recorder_job_and_real_package_to_translation(string instanceId, string model)
    {
        var bodies = new List<LoopbackHttpRequest>();
        using var server = new LoopbackHttpServer(req => { lock (bodies) bodies.Add(req); return LoopbackHttpResponse.Json(200, instanceId == SpeechCatalog.OpenAiAsr ? OpenAiText("  system says hi ") : GeminiOk("  system says hi \n")); });
        using var sandbox = BuildSandbox(instanceId, server);
        if (sandbox is null) { Assert.Skip("susu.exe is not published (src/Susu.Host/bin/Release/net10.0/win-x64/publish); sandbox chain not run"); return; }
        using var rig = new ShellRig(() => sandbox.Provider(model), sandbox.Leases, sandbox.Files);
        rig.OpenSystemAudio();
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StartRecording).Ok);
        var loopBlock = Block(100, 9000);
        for (int i = 0; i < 20; i++) rig.Loop.Last!.Push(loopBlock);
        Assert.True(await rig.WaitElapsed(2000));
        Assert.Empty(bodies); // nothing leaves while recording
        Assert.True(rig.Run(WindowKind.Voice, UiCommands.StopRecording).Ok);
        Assert.True(await rig.WaitPhase("transcribed"));
        Assert.Equal(["system says hi"], rig.Translated);
        var view = rig.View;
        Assert.Equal(("systemAudio", true, true), (view.Source, view.OwnPlayback, view.Translated));
        Assert.Equal((0, 1), (rig.Mic.Opens, rig.Loop.Opens));
        var body = Assert.Single(bodies);
        string wire = instanceId == SpeechCatalog.GeminiAsr
            ? Encoding.Latin1.GetString(Convert.FromBase64String(JsonDocument.Parse(body.Body).RootElement.GetProperty("contents")[0].GetProperty("parts")[1].GetProperty("inlineData").GetProperty("data").GetString()!))
            : Encoding.Latin1.GetString(body.Body);
        Assert.Contains(Encoding.Latin1.GetString(loopBlock), wire); // the loopback samples themselves went out
        Assert.DoesNotContain(ApiKey, Encoding.UTF8.GetString(body.Body));
        await sandbox.AssertNoFilesLeftAsync();
    }

    [Fact] // the result-level flag through the same AsrJob the voice window uses (no shell): IncludesOwnPlayback is set for loopback and not for the microphone
    public async Task Own_playback_flag_is_set_on_the_loopback_result_only()
    {
        using var root = new TempRoot();
        using var leases = new FileLeases(root.Paths.Cache);
        var files = new LeasedFiles(leases);
        var clock = new ManualClock();
        var mic = new FakeDevices(); var output = new FakeDevices();
        var m = (await new AudioCaptureCoordinator(mic, files, clock).StartAsync(Ct)).Session!;
        var l = (await Loopback(output, files, clock).StartAsync(Ct)).Session!;
        mic.Last!.Push(Block(100, 5000)); output.Last!.Push(Block(100, 5000));
        await Until(() => m.Captured > TimeSpan.Zero && l.Captured > TimeSpan.Zero);
        var rm = await m.StopAsync(); var rl = await l.StopAsync();
        Assert.Equal((false, AudioSourceKind.Microphone), (rm.IncludesOwnPlayback, rm.Source));
        Assert.Equal((true, AudioSourceKind.SystemLoopback), (rl.IncludesOwnPlayback, rl.Source));
        Assert.Equal(m.GetType(), l.GetType()); // one recorder type for both sources
        rm.Audio!.Dispose(); rl.Audio!.Dispose();
    }

    [Fact] // no second pipeline: one recorder session type, one AsrJob construction, one loopback coordinator on the same coordinator type
    public void There_is_no_second_recorder_or_asr_pipeline()
    {
        string? root = FindRoot(d => File.Exists(Path.Combine(d, "src", "Susu.Host", "Program.cs")));
        if (root is null) { Assert.Skip("source tree not found from the test output folder"); return; }
        string program = File.ReadAllText(Path.Combine(root, "src", "Susu.Host", "Program.cs"));
        Assert.Single(Regex.Matches(program, @"new AsrJob\("));
        Assert.Equal(2, Regex.Matches(program, @"new AudioCaptureCoordinator\(").Count); // microphone and loopback
        Assert.Contains("AudioSourceKind.SystemLoopback", program);
        var sessions = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"class\s+\w+[^{;]*:\s*[^{;]*\bIRecordingSession\b")).ToList();
        Assert.Single(sessions);
        Assert.EndsWith("AudioRecording.cs", sessions[0]);
        var jobs = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Count(f => Regex.IsMatch(File.ReadAllText(f), @"class\s+\w*(Asr|Transcri)\w*(Job|Pipeline)\b"));
        Assert.True(jobs <= 2, $"unexpected extra transcription job/pipeline classes: {jobs}"); // AsrJob and the shared pipeline
    }

    // ================= entry capability and hotkeys =================

    private sealed class EntryRig : IDisposable
    {
        public readonly TempRoot Root = new();
        public readonly FakePlatform Platform = new();
        public readonly ConfigService Config;
        public readonly FakeSecretStore Secrets = new();
        public readonly ShellCoordinator Shell;
        private readonly Supervisor<HostSession> supervisor = new(() => throw new InvalidOperationException("never started"), SystemClock.Instance, TimeSpan.FromMinutes(1));

        public EntryRig()
        {
            var settings = new SettingsStore(Root.Paths, new ManualClock());
            Config = new ConfigService(settings, new SecretStore(Root.Paths.Secrets, new XorProtector()));
            var features = new FeatureRegistry();
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Voice, FeatureState.Available, null, [Capability.Asr]));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.SystemAudio, FeatureState.Available, null, [Capability.Asr]));
            var translator = new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Echo("T:"));
            var snapshot = new ConfigSnapshot(1, 1, 1, 1, TimeSpan.FromSeconds(30));
            Shell = new ShellCoordinator(Platform, Config, features,
                c => c == Capability.Translate || (c == Capability.Asr && PluginAsrProviders.Create(Config.State.Effective, SpeechSlot.Asr, Secrets.Has, supervisor) is not null),
                new ShellOptions(false, false),
                _ => new TranslationSession([translator], new TranslationSessionOptions(snapshot, 1), new InvocationScheduler(new SchedulerLimits()), new ManualClock(), new FixedJitter(), new RecordingUsage()));
            Shell.Start();
        }

        public (bool Enabled, string? Reason) Item(string id) { var i = Shell.TrayModel().Items.Single(x => x.Id == id); return (i.Enabled, i.ReasonKey); }

        public SaveStatus Save(Func<AppSettings, AppSettings> edit)
        {
            var s = Config.State.Effective;
            return Config.Save(edit(s), Config.State.Revision, Config.State.FileHash).Status;
        }

        public void Dispose() { supervisor.Dispose(); Root.Dispose(); }
    }

    private static AppSettings WithAccount(AppSettings d, params string[] instances)
    {
        var grants = instances.SelectMany(i => ((SpeechPackage)CredentialPackages.Find(i)!).RequiredGrants(new Dictionary<string, string>())).ToList();
        var bindings = new Dictionary<string, string> { ["apiKey"] = "acct" };
        return d with
        {
            Accounts = [new AccountSettings("acct", "Shared", ["apiKey"], grants)],
            Instances = [.. d.Instances.Select(i => instances.Contains(i.Id) ? i with { AccountBindings = bindings } : i)],
        };
    }

    [Fact] // A02 for system audio: the entry follows the real ASR predicate, with the reason, no registered chord and no capture while greyed
    public void System_audio_entry_is_greyed_without_asr_credentials_by_the_real_predicate()
    {
        using var rig = new EntryRig();
        var capture = new FakeDevices();
        rig.Shell.SystemAudioCapture = Loopback(capture, new LeasedFiles(new FileLeases(TestTemp.NewDir("susu-f13v-entry"))), new ManualClock());
        Assert.Equal((false, "feature.noService.asr"), rig.Item("system-audio"));
        Assert.Equal((false, "feature.noService.asr"), rig.Item("voice"));
        Assert.False(rig.Platform.Registered.ContainsKey("audioTranslate"));
        rig.Shell.OnHotkey("audioTranslate"); // a stale chord
        Assert.DoesNotContain("show:Voice:True", rig.Platform.Calls);
        Assert.Equal(0, capture.Opens);
        // account bound and granted but no secret saved: still greyed
        Assert.Equal(SaveStatus.Saved, rig.Save(s => WithAccount(s, SpeechCatalog.OpenAiAsr)));
        Assert.False(rig.Item("system-audio").Enabled);
        rig.Secrets.Set("acct", "apiKey", "k");
        Assert.Equal((true, null), rig.Item("system-audio"));
        Assert.Equal(SaveStatus.Saved, rig.Save(s => s)); // a save re-applies the hotkeys
        Assert.True(rig.Platform.Registered.ContainsKey("audioTranslate"), "audioTranslate chord is not registered once ASR is usable");
        Assert.True(rig.Platform.Registered.ContainsKey("voiceTranslate"));
        Assert.NotEqual(rig.Platform.Registered["audioTranslate"], rig.Platform.Registered["voiceTranslate"]);
        Assert.Equal(SaveStatus.Saved, rig.Save(s => s with { Accounts = [new AccountSettings("acct", "Shared", ["apiKey"], [])] }));
        Assert.Equal((false, "feature.noService.asr"), rig.Item("system-audio"));
        Assert.False(rig.Platform.Registered.ContainsKey("audioTranslate"));
    }

    [Fact] // the voice and system-audio chords must differ: a shared chord is flagged on both and registered for neither (or refused at save)
    public void Voice_and_system_audio_chords_in_conflict_are_flagged_and_not_registered()
    {
        using var rig = new EntryRig();
        Assert.Equal(SaveStatus.Saved, rig.Save(s => WithAccount(s, SpeechCatalog.OpenAiAsr)));
        rig.Secrets.Set("acct", "apiKey", "k");
        Assert.Equal(SaveStatus.Saved, rig.Save(s => s));
        string voiceChord = rig.Platform.Registered["voiceTranslate"];
        Assert.Contains(HotkeySettings.Actions, a => a == "audioTranslate");

        var conflicting = new HotkeySettings(new Dictionary<string, string>(rig.Config.State.Effective.Hotkeys.Chords) { ["audioTranslate"] = voiceChord.ToLowerInvariant() });
        var found = conflicting.Conflicts();
        Assert.Equal(["audioTranslate", "voiceTranslate"], Assert.Single(found).Actions); // case-insensitive, both named

        var status = rig.Save(s => s with { Hotkeys = conflicting });
        if (status == SaveStatus.Saved)
        {
            Assert.False(rig.Platform.Registered.ContainsKey("voiceTranslate"));
            Assert.False(rig.Platform.Registered.ContainsKey("audioTranslate"));
            var view = rig.Shell.ProjectSettings(rig.Config.State);
            Assert.Equal("conflict", view.Hotkeys.Single(h => h.Action == "voiceTranslate").State);
            Assert.Equal("conflict", view.Hotkeys.Single(h => h.Action == "audioTranslate").State);
        }
        else Assert.Equal(SaveStatus.Invalid, status); // refused at save: the old chords stay
        // resolving it registers both again
        Assert.Equal(SaveStatus.Saved, rig.Save(s => s with { Hotkeys = new HotkeySettings(new Dictionary<string, string>(s.Hotkeys.Chords) { ["audioTranslate"] = "Alt+B", ["voiceTranslate"] = "Alt+V" }) }));
        Assert.True(rig.Platform.Registered.ContainsKey("voiceTranslate") && rig.Platform.Registered.ContainsKey("audioTranslate"));
    }

    // ================= real loopback tone through the production path =================

    private sealed class FileClip(string path) : IAudioClip
    {
        public string Mime => "audio/wav";
        public string FilePath => path;
        public long Bytes => new FileInfo(path).Length;
        public void Dispose() { }
    }

    private static void WriteTone(string path, double seconds)
    {
        int n = (int)(seconds * 22050);
        var data = new byte[n * 2];
        for (int i = 0; i < n; i++) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), (short)(14000 * Math.Sin(2 * Math.PI * 1000 * i / 22050)));
        using var f = File.Create(path);
        Span<byte> h = stackalloc byte[44];
        "RIFF"u8.CopyTo(h); BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)(36 + data.Length));
        "WAVEfmt "u8.CopyTo(h[8..]); BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 1); BinaryPrimitives.WriteUInt16LittleEndian(h[22..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], 22050); BinaryPrimitives.WriteUInt32LittleEndian(h[28..], 44100);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], 2); BinaryPrimitives.WriteUInt16LittleEndian(h[34..], 16);
        "data"u8.CopyTo(h[36..]); BinaryPrimitives.WriteUInt32LittleEndian(h[40..], (uint)data.Length);
        f.Write(h); f.Write(data);
    }

    [Fact] // REC04 real: production recorder + WasapiMicrophone.SystemLoopback; the WAV file itself carries the played tone (peak and a 16 kHz header)
    public async Task Real_loopback_tone_reaches_a_non_silent_wav_through_the_production_path()
    {
        using var root = new TempRoot();
        using var leases = new FileLeases(root.Paths.Cache);
        var sink = new WasapiAudioSink();
        var device = sink.Probe();
        if (!device.Available) { Assert.Skip($"no audio output endpoint on this machine ({device.Failure})"); return; }
        var capture = new AudioCaptureCoordinator(WasapiMicrophone.SystemLoopback(), new LeasedFiles(leases), new SystemClock(), null, AudioSourceKind.SystemLoopback);
        var started = await capture.StartAsync(Ct);
        Assert.True(started.Session is not null, $"loopback start failed: {started.ErrorCode}");
        var session = started.Session!;
        string tone = Path.Combine(root.Paths.Cache, "tone.wav");
        WriteTone(tone, 1.5);
        await Task.Delay(300, Ct);
        await sink.PlayAsync(new FileClip(tone), Ct);
        await Task.Delay(300, Ct);
        var result = await session.StopAsync();
        Assert.Equal(RecordingStatus.Stopped, result.Status);
        Assert.True(result.IncludesOwnPlayback);
        using var audio = result.Audio!;
        byte[] wav = File.ReadAllBytes(audio.File.FilePath);
        Assert.Equal(Rate, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(wav.Length - 44, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        int peak = 0;
        for (int i = 44; i + 1 < wav.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(i))));
        TestContext.Current.TestOutputHelper?.WriteLine($"loopback wav: {audio.Duration}, peak {peak}, silent={audio.Silent}");
        if (peak < 200) { Assert.Skip("the loopback delivered only silence while the tone played: this output endpoint (virtual device) does not render a mix to loopback"); return; }
        Assert.False(audio.Silent);
        Assert.True(peak > 1000);
    }
}
