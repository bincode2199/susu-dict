using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
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
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// Independent F12 verification (testing agent). Gaps the coding agents' tests leave: the recorder's real WAV through AsrJob and
/// the real OpenAI and Gemini packages in the sandbox against a loopback vendor (REC01 + A01 + A08 end to end); B02 with the
/// recorder's own audio; the request cap on the real Base64 wire body with the worst case for '+' escaping, and the AOT-safety
/// of the encoder options; hard cut and long silence at the real model limits (A05); bad segments (A06) through the job with
/// nothing leaving it, including a bad chunk after a good one; cancel mid-upload for Gemini (B07); the 10-minute product limit
/// with a fake clock and busy start (REC01); the Voice entry decided by the real capability predicate (A02/A03). Sandbox tests
/// need susu.exe published and skip themselves otherwise. Real vendors and a real microphone: not executed.
/// </summary>
public class F12VerificationTests
{
    private const int Rate = 16000;
    private const string ApiKey = "asr-verify-key-0123456789abcdef";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------- recorder rig (fake device, real recorder) ----------------

    private sealed class FakeStream : IMicrophoneStream
    {
        private readonly Channel<byte[]> blocks = Channel.CreateUnbounded<byte[]>();
        public int SampleRate => Rate;
        public bool Disposed { get; private set; }
        public void Push(byte[] block) => blocks.Writer.TryWrite(block);
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
        public FakeStream? Last;
        public bool HasDevice() => true;
        public IMicrophoneStream Open()
        {
            Opens++;
            if (OpenFails is { } f) throw new MicrophoneException(f, f.ToString());
            return Last = new FakeStream();
        }
    }

    /// <summary>A block of <paramref name="ms"/> ms: square wave at <paramref name="amplitude"/>, or digital silence at 0.</summary>
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

    // ---------------- sandbox rig (the shipped packages through the real host) ----------------

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
        string dir = TestTemp.NewDir("susu-f12v-it");
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

    internal sealed class Rig : IDisposable
    {
        public required FileLeases Leases { get; init; }
        public required Supervisor<HostSession> Supervisor { get; init; }
        public required WiredPackage Package { get; init; }
        public required InstanceSettings Instance { get; init; }
        public required SpeechPackage Speech { get; init; }
        public HostSession? Session;
        public LeasedFiles Files => new(Leases);

        public PluginAsrProvider Provider(string model, AsrLimits? limits = null, bool? timecodes = null)
        {
            var m = Speech.Models.Single(x => x.Id == model);
            if (limits is not null) m = new SpeechModel(m.Id, timecodes ?? m.Timecodes, limits);
            return new PluginAsrProvider(Package, Instance, m, Supervisor);
        }

        public ILeasedFile File_(byte[] wav)
        {
            var file = Files.Create("asr-input", "audio/wav", "wav");
            File.WriteAllBytes(file.FilePath, wav);
            return file;
        }

        public RecordedAudio Recorded(byte[] wav, bool silent = false) => new(File_(wav), TimeSpan.FromSeconds((wav.Length - 44) / 2.0 / Rate), Rate, silent);

        public async Task AssertNoFilesLeftAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((Leases.ActiveCount > 0 || (Session is not null && (Session.Broker.ActiveResponseFiles > 0 || Session.Broker.ActiveGrants > 0))) && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
            Assert.Equal(0, Leases.ActiveCount);
            if (Session is not null) { Assert.Equal(0, Session.Broker.ActiveGrants); Assert.Equal(0, Session.Broker.ActiveResponseFiles); }
        }

        public void Dispose() { Supervisor.Dispose(); Leases.Dispose(); }
    }

    internal static Rig? Build(string instanceId, LoopbackHttpServer server)
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
        var leases = new FileLeases(TestTemp.NewDir("susu-f12v-leases"));
        var options = new HostSession.Options(Path.Combine(dir, "susu.exe"), dir, "quickjs", KeepProfile: false,
            MakeBroker: () => new Broker(secretStore: secrets, accounts: accounts, leases: leases));
        Rig? rig = null;
        var supervisor = new Supervisor<HostSession>(() =>
        {
            var session = HostSession.Start(options);
            session.Broker.ApproveLocalOrigin(server.Origin);
            var loaded = session.Load(package.PackageId, package.Directory);
            if (!loaded.Ok) { session.Shutdown(2000); throw new InvalidOperationException($"{package.PackageId} failed to load: {loaded.Error}"); }
            rig!.Session = session;
            return session;
        }, SystemClock.Instance, TimeSpan.FromMinutes(10));
        rig = new Rig { Leases = leases, Supervisor = supervisor, Package = package, Instance = instance, Speech = speech };
        return rig;
    }

    private static string OpenAiText(string text) => JsonSerializer.Serialize(new { text });
    private static string GeminiOk(string text) => JsonSerializer.Serialize(new { candidates = new[] { new { content = new { role = "model", parts = new[] { new { text } } }, finishReason = "STOP" } } });
    private static AsrLimits SmallWav(long request, int seconds, AsrUpload upload) => new([AsrFormat.Wav16kMono], 12_000_000, request, seconds, upload, upload == AsrUpload.Base64Json ? 4096 : 2048);

    /// <summary>A 16 kHz mono WAV whose file bytes (from offset 45 on) cycle FB EF BE, so every aligned Base64 quad is "++++".</summary>
    private static byte[] PlusHeavyWav(double seconds)
    {
        var plain = AsrPipelineTests.Wav((true, seconds));
        for (int p = 45; p < plain.Length; p++) plain[p] = (new byte[] { 0xFB, 0xEF, 0xBE })[p % 3];
        plain[44] = (new byte[] { 0xFB, 0xEF, 0xBE })[44 % 3];
        return plain;
    }

    // ================= REC01: the real recorder's WAV through AsrJob and both real packages =================

    [Theory] // REC01 + A01 + A08 end to end: no request while recording; the recorder's exact WAV reaches the vendor; text is translated; nothing is left
    [InlineData(SpeechCatalog.OpenAiAsr, "whisper-1")]
    [InlineData(SpeechCatalog.GeminiAsr, "gemini-2.5-flash")]
    public async Task Recorded_wav_goes_through_the_job_and_the_real_package_to_translation(string instanceId, string model)
    {
        var bodies = new List<LoopbackHttpRequest>();
        using var server = new LoopbackHttpServer(req => { lock (bodies) bodies.Add(req); return LoopbackHttpResponse.Json(200, instanceId == SpeechCatalog.OpenAiAsr ? OpenAiText("  hello world ") : GeminiOk("  hello world \n")); });
        using var rig = Build(instanceId, server);
        if (rig is null) return;
        using var root = new TempRoot();
        var devices = new FakeDevices();
        var capture = new AudioCaptureCoordinator(devices, rig.Files, new ManualClock());
        var started = await capture.StartAsync(Ct);
        var session = started.Session!;
        var stream = devices.Last!;
        for (int i = 0; i < 30; i++) stream.Push(Block(100, 9000)); // 3 s of sound
        await Until(() => session.Captured >= TimeSpan.FromSeconds(3));
        Assert.Empty(bodies); // no live transcription: nothing is sent while recording
        var result = await session.StopAsync();
        Assert.Equal(RecordingStatus.Stopped, result.Status);
        Assert.True(stream.Disposed);
        var audio = result.Audio!;
        string recordedPath = audio.File.FilePath;
        byte[] recorded = File.ReadAllBytes(recordedPath);
        Assert.Equal(44 + 3 * Rate * 2, recorded.Length);
        Assert.Empty(bodies); // still nothing until transcription is requested

        var translated = new List<string>();
        var job = new AsrJob(() => rig.Provider(model), rig.Files, t => { translated.Add(t); return Task.CompletedTask; }, () => true);
        var state = await job.TranscribeAsync(audio);
        Assert.True(state.Phase == AsrPhase.Transcribed, $"{state.Phase} {state.Error}");
        Assert.Equal("hello world", state.Text);
        Assert.Equal(["hello world"], translated);
        Assert.Single(bodies);
        // The vendor received exactly the recorder's bytes.
        if (instanceId == SpeechCatalog.GeminiAsr)
        {
            using var doc = JsonDocument.Parse(bodies[0].Body);
            Assert.Equal(recorded, Convert.FromBase64String(doc.RootElement.GetProperty("contents")[0].GetProperty("parts")[1].GetProperty("inlineData").GetProperty("data").GetString()!));
        }
        else Assert.Contains(Encoding.Latin1.GetString(recorded), Encoding.Latin1.GetString(bodies[0].Body));
        Assert.DoesNotContain(ApiKey, Encoding.UTF8.GetString(bodies[0].Body));
        Assert.False(File.Exists(recordedPath)); // the job released the recording
        await rig.AssertNoFilesLeftAsync();
    }

    [Theory] // REC01 + A08: a recording that is silence makes no request through the whole chain
    [InlineData(SpeechCatalog.OpenAiAsr, "whisper-1")]
    [InlineData(SpeechCatalog.GeminiAsr, "gemini-2.5-flash")]
    public async Task A_silent_recording_makes_no_vendor_request(string instanceId, string model)
    {
        int hits = 0;
        using var server = new LoopbackHttpServer(_ => { Interlocked.Increment(ref hits); return LoopbackHttpResponse.Json(200, OpenAiText("x")); });
        using var rig = Build(instanceId, server);
        if (rig is null) return;
        var devices = new FakeDevices();
        var started = await new AudioCaptureCoordinator(devices, rig.Files, new ManualClock()).StartAsync(Ct);
        for (int i = 0; i < 40; i++) devices.Last!.Push(Block(100, 0));
        await Until(() => started.Session!.Captured >= TimeSpan.FromSeconds(4));
        var result = await started.Session!.StopAsync();
        Assert.True(result.Audio!.Silent);
        int translated = 0;
        var job = new AsrJob(() => rig.Provider(model), rig.Files, _ => { translated++; return Task.CompletedTask; }, () => true);
        Assert.Equal(AsrPhase.NoSpeech, (await job.TranscribeAsync(result.Audio)).Phase);
        Assert.Equal((0, 0), (hits, translated));
        await rig.AssertNoFilesLeftAsync();
    }

    // ================= B02 with the recorder's audio: the plugin sees a handle =================

    /// <summary>A copy of a package whose code throws what it saw (req and r) right after its $http call.</summary>
    private static string ProbeCopy(string packageDir, string marker)
    {
        string source = Path.Combine(staged.Value!, packageDir);
        string relative = packageDir + "-probe-" + Guid.NewGuid().ToString("N")[..8];
        string target = Path.Combine(staged.Value!, relative);
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        string main = File.ReadAllText(Path.Combine(target, "main.js"));
        int at = main.IndexOf("if (r.status < 200 || r.status >= 300)", StringComparison.Ordinal);
        Assert.True(at > 0, "probe anchor not found");
        string probe = "{ const seen = JSON.stringify({ req: req, r: r }); throw new PluginError('bad_response', 'HAS=' + (seen.indexOf('" + marker +
            "') >= 0) + ' LEN=' + seen.length + ' PROBE ' + seen.slice(0, 1200)); }";
        File.WriteAllText(Path.Combine(target, "main.js"), main[..at] + probe + main[at..]);
        return relative;
    }

    [Theory] // B02: neither the audio bytes nor any slice of its Base64 appear in what the plugin code sees (a distinctive recorded pattern)
    [InlineData(SpeechCatalog.OpenAiAsr, "whisper-1")]
    [InlineData(SpeechCatalog.GeminiAsr, "gemini-2.5-flash")]
    public async Task Plugin_never_sees_audio_bytes_from_a_recording(string instanceId, string model)
    {
        if (staged.Value is null) return;
        byte[] wav = PlusHeavyWav(2);
        string b64 = Convert.ToBase64String(wav);
        LoopbackHttpRequest? sent = null;
        using var server = new LoopbackHttpServer(req => { sent = req; return LoopbackHttpResponse.Json(200, "{}"); });
        var package = PluginTranslationProviders.WiredPackages.Single(p => p.InstanceId == instanceId);
        string probeDir = ProbeCopy(package.Directory, b64[100..140]);
        var speech = (SpeechPackage)package.Credentials;
        var cfg = new Dictionary<string, string> { ["baseUrl"] = server.Origin };
        var secrets = new FakeSecretStore();
        secrets.Set("account", "apiKey", ApiKey);
        var account = new AccountSettings("account", "Shared account", ["apiKey"], [.. speech.RequiredGrants(cfg)]);
        var instance = new InstanceSettings(instanceId, package.PackageId, 1, cfg, new Dictionary<string, string> { ["apiKey"] = "account" });
        var leases = new FileLeases(TestTemp.NewDir("susu-f12v-leases"));
        var options = new HostSession.Options(Path.Combine(staged.Value, "susu.exe"), staged.Value, "quickjs", KeepProfile: false,
            MakeBroker: () => new Broker(secretStore: secrets, accounts: new AccountAuthorization(() => ([account], [instance])), leases: leases));
        using var supervisor = new Supervisor<HostSession>(() =>
        {
            var session = HostSession.Start(options);
            session.Broker.ApproveLocalOrigin(server.Origin);
            var loaded = session.Load(package.PackageId, probeDir);
            if (!loaded.Ok) throw new InvalidOperationException(loaded.Error);
            return session;
        }, SystemClock.Instance, TimeSpan.FromMinutes(10));
        using var _ = leases;
        var provider = new PluginAsrProvider(package, instance, speech.Models.Single(m => m.Id == model), supervisor);
        var files = new LeasedFiles(leases);
        var file = files.Create("asr", "audio/wav", "wav");
        File.WriteAllBytes(file.FilePath, wav);
        var outcome = await provider.TranscribeAsync(new AsrCall(file.LeaseId, file.Mime, file.Bytes, 2, provider.Model, "text", null, "a1", TimeSpan.FromSeconds(60), provider.Limits.EffectiveRequestCap), Ct);
        string detail = Assert.IsType<AsrOutcome.Failure>(outcome).Error.Detail ?? "";
        TestContext.Current.TestOutputHelper?.WriteLine(detail);
        Assert.Contains("PROBE", detail);
        Assert.Contains("HAS=false", detail);
        int len = int.Parse(System.Text.RegularExpressions.Regex.Match(detail, @"LEN=(\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(len < 4000, $"plugin-visible JSON {len} chars vs {b64.Length} Base64 chars of audio");
        Assert.Contains(file.LeaseId, detail);
        Assert.NotNull(sent);
        Assert.True(sent.Body.Length >= wav.Length); // the vendor still received the audio
        file.Dispose();
    }

    // ================= A04: request cap on the real wire body, '+' worst case, AOT =================

    [Fact] // A04: the worst case for JSON escaping of '+' (all-'+' Base64) still fits the cap on the wire, with the host estimate an upper bound
    public async Task Plus_heavy_audio_never_exceeds_the_request_cap_on_the_wire()
    {
        var bodies = new List<byte[]>();
        using var server = new LoopbackHttpServer(req => { lock (bodies) bodies.Add(req.Body); return LoopbackHttpResponse.Json(200, GeminiOk("piece")); });
        using var rig = Build(SpeechCatalog.GeminiAsr, server);
        if (rig is null) return;
        const long cap = 100_000;
        var provider = rig.Provider("gemini-2.5-flash", SmallWav(cap, 300, AsrUpload.Base64Json), false);
        var job = new AsrJob(() => provider, rig.Files, _ => Task.CompletedTask, () => true);
        byte[] wav = PlusHeavyWav(10);
        var state = await job.TranscribeAsync(rig.Recorded(wav));
        Assert.True(state.Phase == AsrPhase.Transcribed, $"{state.Phase} {state.Error}");
        Assert.True(bodies.Count >= 4, $"{bodies.Count} requests");
        long audioBytes = 0, plus = 0;
        foreach (byte[] body in bodies)
        {
            Assert.True(body.Length <= cap, $"{body.Length} > {cap}");
            string text = Encoding.UTF8.GetString(body);
            Assert.DoesNotContain("\\u002B", text, StringComparison.OrdinalIgnoreCase); // '+' is not escaped
            using var doc = JsonDocument.Parse(body);
            string data = doc.RootElement.GetProperty("contents")[0].GetProperty("parts")[1].GetProperty("inlineData").GetProperty("data").GetString()!;
            plus += data.Count(c => c == '+');
            byte[] chunk = Convert.FromBase64String(data);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(chunk, 0, 4));
            audioBytes += chunk.Length - 44;
            Assert.True(body.Length <= provider.Limits.EstimateRequestBytes(chunk.Length), "the host estimate is not an upper bound");
        }
        Assert.True(plus > 20_000, $"only {plus} '+' characters on the wire: the test did not exercise the worst case");
        Assert.Equal(wav.Length - 44, audioBytes); // every sample was sent exactly once: no audio lost or duplicated
        await rig.AssertNoFilesLeftAsync();
    }

    [Fact] // A04: the same cap on the real OpenAI multipart body (binary, no escaping), every body fits
    public async Task Multipart_bodies_fit_a_small_cap_and_cover_all_audio()
    {
        var sizes = new List<int>();
        using var server = new LoopbackHttpServer(req => { lock (sizes) sizes.Add(req.Body.Length); return LoopbackHttpResponse.Json(200, OpenAiText("p")); });
        using var rig = Build(SpeechCatalog.OpenAiAsr, server);
        if (rig is null) return;
        var provider = rig.Provider("whisper-1", SmallWav(80_000, 300, AsrUpload.Multipart), true);
        var job = new AsrJob(() => provider, rig.Files, _ => Task.CompletedTask, () => true);
        var state = await job.TranscribeAsync(rig.Recorded(AsrPipelineTests.Wav((true, 8))));
        Assert.True(state.Phase == AsrPhase.Transcribed, $"{state.Phase} {state.Error}");
        Assert.True(sizes.Count >= 4);
        Assert.All(sizes, s => Assert.True(s <= 80_000, $"{s} bytes"));
        await rig.AssertNoFilesLeftAsync();
    }

    [Fact] // A04/AOT: the Base64 JSON writer needs no reflection-based serializer metadata: it works with an empty type-info resolver and keeps '+' raw
    public void Base64_json_options_are_reflection_free()
    {
        var field = typeof(NetworkBroker).GetField("Base64JsonOptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(field);
        var options = (JsonSerializerOptions)field.GetValue(null)!;
        Assert.Null(options.TypeInfoResolver is null ? null : options.TypeInfoResolver); // no custom resolver or converter is needed
        var noReflection = new JsonSerializerOptions(options) { TypeInfoResolver = JsonTypeInfoResolver.Combine() }; // an AOT build without metadata
        var node = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = "转写 <b> & 'x'" }, new JsonObject { ["inlineData"] = new JsonObject { ["data"] = "ab++/+cd==" } }) }),
            ["n"] = 5, ["f"] = 1.5, ["t"] = true, ["z"] = null,
        };
        string json = node.ToJsonString(noReflection);
        Assert.Contains("ab++/+cd==", json);
        Assert.DoesNotContain("\\u002B", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ab++/+cd==", JsonNode.Parse(json)!["contents"]![0]!["parts"]![1]!["inlineData"]!["data"]!.GetValue<string>());
        Assert.Equal("转写 <b> & 'x'", JsonNode.Parse(json)!["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
        // The default writer does escape '+', which is why the estimate needs the relaxed options (the finding in the record).
        Assert.Contains("\\u002B", node.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    // ================= A05: hard cut and long silence at the real model limits (own provider) =================

    private sealed class Seen(AsrLimits limits, bool timecodes) : IAsrProvider
    {
        public readonly List<(double Seconds, long Bytes)> Calls = [];
        public string InstanceId => "probe-asr";
        public string Model => "probe";
        public bool Timecodes => timecodes;
        public AsrLimits Limits => limits;
        public Task<AsrOutcome> TranscribeAsync(AsrCall call, CancellationToken cancellationToken)
        {
            int index;
            lock (Calls) { Calls.Add((call.DurationSeconds, call.Bytes)); index = Calls.Count; }
            return Task.FromResult<AsrOutcome>(call.Output == "segments"
                ? new AsrOutcome.Transcribed("segments", $"p{index}", [new AsrSegment(0.1, call.DurationSeconds - 0.1, $"p{index}")])
                : new AsrOutcome.Transcribed("text", $"p{index}", null));
        }
    }

    private static async Task<(AsrRunOutcome Outcome, Seen Provider)> Run(byte[] wav, string output = "segments", AsrLimits? limits = null)
    {
        using var leases = new FileLeases(TestTemp.NewDir("susu-f12v-leases"));
        var files = new LeasedFiles(leases);
        var provider = new Seen(limits ?? ((SpeechPackage)SpeechCatalog.Find(SpeechCatalog.OpenAiAsr)!).Models.Single(m => m.Id == "whisper-1").Limits!, true);
        using var input = files.Create("asr-input", "audio/wav", "wav");
        File.WriteAllBytes(input.FilePath, wav);
        var outcome = await new AsrTranscriber(files, TimeSpan.FromSeconds(30)).RunAsync(provider, input, output, null, Ct);
        Assert.Equal(0, leases.ActiveCount - 1); // only the input remains
        return (outcome, provider);
    }

    [Fact] // A05: more than 5 minutes of continuous sound is cut hard at the model limit; all audio is sent; offsets only grow
    public async Task Eleven_minutes_of_continuous_sound_is_cut_hard_every_300_seconds()
    {
        var (outcome, provider) = await Run(AsrPipelineTests.Wav((true, 660.5)));
        var done = Assert.IsType<AsrRunOutcome.Done>(outcome).Transcript;
        Assert.Equal(3, provider.Calls.Count);
        Assert.All(provider.Calls, c => Assert.True(c.Seconds <= 300.0 + 1e-6, $"{c.Seconds}"));
        Assert.InRange(provider.Calls.Sum(c => c.Seconds), 660.4, 660.6); // nothing lost
        var seg = done.Segments!;
        Assert.Equal(3, seg.Count);
        Assert.True(seg[1].Start >= 300 && seg[1].Start < 300.5, $"{seg[1].Start}");
        Assert.True(seg[2].Start >= 600 && seg[2].Start < 600.5, $"{seg[2].Start}");
        for (int i = 1; i < seg.Count; i++) Assert.True(seg[i].Start >= seg[i - 1].Start && seg[i].Start >= seg[i - 1].End - 0.2);
        Assert.Equal("p1 p2 p3", done.Text);
    }

    [Fact] // A05: a pause longer than a whole chunk keeps its place on the timeline and is not uploaded
    public async Task A_seven_minute_silence_keeps_the_timeline_and_is_not_uploaded()
    {
        var (outcome, provider) = await Run(AsrPipelineTests.Wav((true, 4), (false, 420), (true, 4)));
        var seg = Assert.IsType<AsrRunOutcome.Done>(outcome).Transcript.Segments!;
        Assert.Equal(2, provider.Calls.Count);
        Assert.All(provider.Calls, c => Assert.True(c.Seconds < 6, $"a chunk of {c.Seconds} s carried silence"));
        Assert.InRange(seg[1].Start, 423.5, 424.5); // 4 + 420 on the real timeline (less the kept pad)
        Assert.True(seg[1].End <= 428.1);
        Assert.True(seg[0].End < 4.5);
    }

    [Fact] // A05: speech, silence, speech inside one chunk window: one request, the silent part is cut at the silence, not in speech
    public async Task Medium_silence_splits_the_chunk_at_the_silence()
    {
        var (outcome, provider) = await Run(AsrPipelineTests.Wav((true, 200), (false, 2), (true, 140)));
        var seg = Assert.IsType<AsrRunOutcome.Done>(outcome).Transcript.Segments!;
        Assert.Equal(2, provider.Calls.Count);
        Assert.InRange(seg[0].End, 150, 202.5); // the first cut falls at the silence in the second half of the window
        Assert.InRange(seg[1].Start, 200, 203);
    }

    // ================= A06: bad segments through the job, real OpenAI package =================

    public static TheoryData<string, string> BadSegmentBodies => new()
    {
        { "literal NaN start", """{"segments":[{"start":NaN,"end":1,"text":"x"}]}""" },
        { "literal Infinity end", """{"segments":[{"start":0,"end":Infinity,"text":"x"}]}""" },
        { "overflow number", """{"segments":[{"start":0,"end":1e999,"text":"x"}]}""" },
        { "negative start", """{"segments":[{"start":-0.5,"end":1,"text":"x"}]}""" },
        { "end equals start", """{"segments":[{"start":1,"end":1,"text":"x"}]}""" },
        { "end before start", """{"segments":[{"start":2,"end":1,"text":"x"}]}""" },
        { "unordered", """{"segments":[{"start":1.5,"end":2,"text":"b"},{"start":0.2,"end":0.9,"text":"a"}]}""" },
        { "past the chunk", """{"segments":[{"start":0,"end":90,"text":"x"}]}""" },
        { "string time", """{"segments":[{"start":"0","end":"1","text":"x"}]}""" },
        { "good then bad", """{"segments":[{"start":0,"end":1,"text":"ok"},{"start":1,"end":0.5,"text":"bad"}]}""" },
    };

    [Theory]
    [MemberData(nameof(BadSegmentBodies))]
    public async Task Bad_segments_are_bad_response_and_nothing_leaves_the_job(string name, string vendorBody)
    {
        int hits = 0, translated = 0;
        using var server = new LoopbackHttpServer(_ => { Interlocked.Increment(ref hits); return LoopbackHttpResponse.Json(200, vendorBody); });
        using var rig = Build(SpeechCatalog.OpenAiAsr, server);
        if (rig is null) return;
        var job = new AsrJob(() => rig.Provider("whisper-1"), rig.Files, _ => { translated++; return Task.CompletedTask; }, () => true);
        var states = new List<AsrState>();
        job.StateChanged += states.Add;
        var state = await job.TranscribeAsync(rig.Recorded(AsrPipelineTests.Wav((true, 3))), output: "segments");
        Assert.True(hits >= 1, $"{name}: the vendor was never reached");
        Assert.Equal(AsrPhase.Failed, state.Phase);
        Assert.Equal(ErrorKind.BadResponse, state.Error!.Kind);
        Assert.Null(state.Text);
        Assert.Null(state.Segments);
        Assert.Equal(0, translated);
        Assert.All(states, s => { Assert.Null(s.Text); Assert.Null(s.Segments); });
        await rig.AssertNoFilesLeftAsync();
    }

    [Fact] // A06: chunk 1 is good, chunk 2 is bad: the run fails with no partial text, chunk 3 is never uploaded, nothing is translated
    public async Task A_bad_second_chunk_discards_the_first_chunks_text()
    {
        int hits = 0, translated = 0;
        using var server = new LoopbackHttpServer(_ =>
        {
            int n = Interlocked.Increment(ref hits);
            return LoopbackHttpResponse.Json(200, n == 1 ? """{"segments":[{"start":0.2,"end":2,"text":"first"}]}""" : """{"segments":[{"start":3,"end":1,"text":"bad"}]}""");
        });
        using var rig = Build(SpeechCatalog.OpenAiAsr, server);
        if (rig is null) return;
        var provider = rig.Provider("whisper-1", SmallWav(25_000_000, 6, AsrUpload.Multipart), true);
        var job = new AsrJob(() => provider, rig.Files, _ => { translated++; return Task.CompletedTask; }, () => true);
        var state = await job.TranscribeAsync(rig.Recorded(AsrPipelineTests.Wav((true, 20))), output: "segments");
        Assert.Equal(AsrPhase.Failed, state.Phase);
        Assert.Equal(ErrorKind.BadResponse, state.Error!.Kind);
        Assert.Null(state.Text);
        Assert.Null(state.Segments);
        Assert.Equal(2, hits);
        Assert.Equal(0, translated);
        await rig.AssertNoFilesLeftAsync();
    }

    // ================= B07: cancel mid-upload, Gemini =================

    [Fact] // B07/A05: cancel while the vendor holds the first Base64 upload: no lease, response file or grant is left, later chunks never go, nothing is translated
    public async Task Gemini_cancel_mid_upload_leaves_no_lease_file_or_grant()
    {
        var gate = new ManualResetEventSlim(false);
        int hits = 0, translated = 0;
        using var server = new LoopbackHttpServer(_ =>
        {
            if (Interlocked.Increment(ref hits) == 1) gate.Wait(TimeSpan.FromSeconds(10));
            return LoopbackHttpResponse.Json(200, GeminiOk("late"));
        });
        using var rig = Build(SpeechCatalog.GeminiAsr, server);
        if (rig is null) return;
        var provider = rig.Provider("gemini-2.5-flash", SmallWav(100_000, 300, AsrUpload.Base64Json), false);
        var job = new AsrJob(() => provider, rig.Files, _ => { Interlocked.Increment(ref translated); return Task.CompletedTask; }, () => true);
        var audio = rig.Recorded(AsrPipelineTests.Wav((true, 10)));
        string path = audio.File.FilePath;
        var running = job.TranscribeAsync(audio);
        for (var deadline = DateTime.UtcNow.AddSeconds(15); Volatile.Read(ref hits) < 1 && DateTime.UtcNow < deadline;) await Task.Delay(10, Ct);
        Assert.Equal(1, Volatile.Read(ref hits));
        job.Cancel();
        Assert.Equal(AsrPhase.Cancelled, (await running.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Phase);
        gate.Set();
        await rig.AssertNoFilesLeftAsync();
        Assert.False(File.Exists(path)); // the recording itself is gone
        await Task.Delay(300, Ct);
        Assert.Equal(1, Volatile.Read(ref hits));
        Assert.Equal(0, translated);
        // and the service works again
        Assert.Equal(AsrPhase.Transcribed, (await job.TranscribeAsync(rig.Recorded(AsrPipelineTests.Wav((true, 1))))).Phase);
        await rig.AssertNoFilesLeftAsync();
    }

    // ================= REC01: ten-minute product limit with a fake clock, busy start =================

    [Fact] // REC01: the product's 10-minute limit (no test override) cuts at exactly 600 s of captured audio; time spent paused does not count
    public async Task The_real_ten_minute_limit_cuts_exactly_at_600_seconds()
    {
        using var root = new TempRoot();
        using var leases = new FileLeases(root.Paths.Cache);
        var clock = new ManualClock();
        var devices = new FakeDevices();
        var started = await new AudioCaptureCoordinator(devices, new LeasedFiles(leases), clock).StartAsync(Ct);
        var session = started.Session!;
        var stream = devices.Last!;
        async Task Feed(int ms, int count) { for (int i = 0; i < count && !session.Completion.IsCompleted; i++) { var before = session.Captured; stream.Push(Block(ms, 6000)); for (int w = 0; w < 2000 && session.Captured == before && !session.Completion.IsCompleted; w++) await Task.Delay(1, Ct); clock.Advance(TimeSpan.FromMilliseconds(ms)); } }
        await Feed(1000, 300);
        session.Pause();
        for (int i = 0; i < 20; i++) stream.Push(Block(100, 6000)); // dropped while paused
        clock.Advance(TimeSpan.FromMinutes(45)); // far past the limit in wall time
        await Task.Delay(100, Ct);
        Assert.Equal(RecordingPhase.Paused, session.Phase);
        Assert.True(session.Captured < TimeSpan.FromSeconds(300.001));
        session.Resume();
        Assert.False(session.Completion.IsCompleted);
        await Feed(700, 443); // 310 s more offered, in blocks that straddle the limit
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(RecordingStatus.LimitReached, result.Status);
        Assert.Equal(TimeSpan.FromMinutes(10), result.Audio!.Duration);
        Assert.True(stream.Disposed);
        using var audio = result.Audio;
        byte[] wav = File.ReadAllBytes(audio.File.FilePath);
        Assert.Equal(44 + 600 * Rate * 2, wav.Length);
        Assert.Equal(600 * Rate * 2, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
    }

    [Fact] // REC01: starting while a recording runs is refused as busy and leaves the running one alone; a failed open does not leave it busy
    public async Task Start_while_busy_is_refused_without_touching_the_running_recording()
    {
        using var root = new TempRoot();
        using var leases = new FileLeases(root.Paths.Cache);
        var devices = new FakeDevices();
        var capture = new AudioCaptureCoordinator(devices, new LeasedFiles(leases), new ManualClock());
        var first = (await capture.StartAsync(Ct)).Session!;
        var firstStream = devices.Last!;
        var second = await capture.StartAsync(Ct);
        Assert.Null(second.Session);
        Assert.Equal("mic.busy", second.ErrorCode);
        Assert.Equal(1, devices.Opens); // the second attempt never reached the device
        Assert.False(firstStream.Disposed);
        Assert.Equal(1, leases.ActiveCount);
        firstStream.Push(Block(200, 8000));
        await Until(() => first.Captured >= TimeSpan.FromMilliseconds(200));
        var firstResult = await first.StopAsync();
        Assert.Equal(RecordingStatus.Stopped, firstResult.Status);
        firstResult.Audio!.Dispose();
        first.Dispose();
        devices.OpenFails = MicFailure.Denied;
        Assert.Equal("mic.denied", (await capture.StartAsync(Ct)).ErrorCode);
        devices.OpenFails = null;
        var third = await capture.StartAsync(Ct);
        Assert.NotNull(third.Session); // busy flag released after stop and after a failed open
        third.Session!.Cancel();
        Assert.Equal(RecordingStatus.Cancelled, (await third.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Status);
        await Until(() => leases.ActiveCount == 0);
    }

    // ================= A02/A03: the Voice and video entries from the real capability predicate =================

    private sealed class ShellRig : IDisposable
    {
        public readonly TempRoot Root = new();
        public readonly FakePlatform Platform = new();
        public readonly ConfigService Config;
        public readonly FakeSecretStore Secrets = new();
        public readonly ShellCoordinator Shell;
        private readonly Supervisor<HostSession> supervisor = new(() => throw new InvalidOperationException("never started"), SystemClock.Instance, TimeSpan.FromMinutes(1));

        public ShellRig()
        {
            var settings = new SettingsStore(Root.Paths, new ManualClock());
            Config = new ConfigService(settings, new SecretStore(Root.Paths.Secrets, new XorProtector()));
            var features = new FeatureRegistry();
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Voice, FeatureState.Available, null, [Capability.Asr]));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Transcription, FeatureState.InDevelopment, "feature.inDevelopment", []));
            var translator = new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Echo("T:"));
            var snapshot = new ConfigSnapshot(1, 1, 1, 1, TimeSpan.FromSeconds(30));
            // Program.cs: Asr is ready only when PluginAsrProviders.Create over the effective settings and the saved secrets finds a provider.
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

    [Fact] // A02: without credentials the Voice entry is greyed with its reason; with a saved and granted key it opens
    public void Voice_entry_is_greyed_without_credentials_and_ready_with_them()
    {
        using var rig = new ShellRig();
        Assert.Equal((false, "feature.noService.asr"), rig.Item("voice"));
        // An account bound and granted, but no secret saved: still greyed.
        Assert.Equal(SaveStatus.Saved, rig.Save(s => WithAccount(s, SpeechCatalog.OpenAiAsr)));
        Assert.False(rig.Item("voice").Enabled);
        rig.Secrets.Set("acct", "apiKey", "k");
        Assert.True(rig.Item("voice").Enabled);
        Assert.Null(rig.Item("voice").Reason);
        // Removing the grant greys it again (the key is still saved).
        Assert.Equal(SaveStatus.Saved, rig.Save(s => s with { Accounts = [new AccountSettings("acct", "Shared", ["apiKey"], [])] }));
        Assert.Equal((false, "feature.noService.asr"), rig.Item("voice"));
    }

    [Fact] // A03: only Gemini (text, no timecodes) configured: Voice ready, the video entry greyed with the timecode reason
    public void Video_entry_is_greyed_when_only_gemini_is_configured()
    {
        using var rig = new ShellRig();
        Assert.Equal(SaveStatus.Saved, rig.Save(s =>
        {
            var d = WithAccount(s, SpeechCatalog.GeminiAsr);
            return d with { Speech = d.Speech.With(SpeechSlot.Asr, new SpeechSelection(SpeechCatalog.GeminiAsr, "gemini-2.5-flash"))};
        }));
        rig.Secrets.Set("acct", "apiKey", "k");
        Assert.True(rig.Item("voice").Enabled);
        var video = rig.Item("transcription");
        Assert.False(video.Enabled);
        Assert.Equal("feature.noService.videoAsr", video.Reason);
        Assert.Equal("needs-timecodes", SpeechCatalog.Check(SpeechSlot.VideoAsr, new SpeechSelection(SpeechCatalog.GeminiAsr, "gemini-2.5-flash")));
    }
}
