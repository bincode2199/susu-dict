using System.Text.Json;
using Susu.Contracts;
using Susu.Plugins;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F05.4: capability contracts (PLAN 4.4/4.7) run through the real AppContainer sandbox + QuickJS
/// engine + IPC channel + the real Susu.Net.NetworkBroker, against local test servers standing in for
/// real vendors - this environment has no vendor accounts. The fixture "vendor" plugin
/// (fixtures/plugins/vendor) exercises each capability the way a real adapter plugin would: a JSON/
/// multipart request built via ctx.$http, mapped from the vendor's own field names into the PLAN
/// contract shape (Susu.Contracts.PluginApi). Susu.Plugins.CapabilityClient is the minimal reusable
/// adapter later modules (F09-F12/F15) can call directly instead of hand-rolling Invoke/await/parse.
/// </summary>
public class ContractProbeTests
{
    private static string? FindHostBuildOutput()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish");
            if (File.Exists(Path.Combine(candidate, "susu.exe"))) return candidate;
        }
        return null;
    }

    private static string? StageHost()
    {
        string? output = FindHostBuildOutput();
        if (output is null) return null;
        string staged = TestTemp.NewDir("susu-contract-it");
        foreach (string file in Directory.EnumerateFiles(output))
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(staged, Path.GetFileName(file)));
        string plugins = Path.Combine(staged, "plugins");
        Directory.CreateDirectory(plugins);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "plugins"), "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(plugins, Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "fixtures", "plugins"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return staged;
    }

    /// <summary>Batch translate: BatchItem[] request/result (PLAN 4.4), a vendor JSON array round trip
    /// with no file handles.</summary>
    [Fact]
    public async Task TranslateBatch_round_trips_through_the_real_broker()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200,
            """{"Items":[{"Id":"1","Text":"你好"},{"Id":"2","Text":"世界"}]}"""));
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load("vendor", "plugins/vendor").Ok);
            string requestJson = $$"""{"url":"{{server.Origin}}/translate","items":[{"id":"1","text":"hello"},{"id":"2","text":"world"}]}""";
            var outcome = await CapabilityClient.InvokeAsync(session, "vendor", "translateBatch", requestJson, "job-batch",
                [server.Origin], ContractsJson.Default.TranslateBatchResult, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(outcome.Ok, outcome.ErrorDetail);
            Assert.Equal(2, outcome.Result!.Items.Length);
            Assert.Equal("你好", outcome.Result.Items[0].Text);
            Assert.Equal("1", outcome.Result.Items[0].Id);
        }
        finally { session.Shutdown(2000); }
    }

    /// <summary>Options (manifest schema dynamic loading, PLAN 4.7): items/nextCursor, no file handles.</summary>
    [Fact]
    public async Task Options_round_trips_through_the_real_broker()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200,
            """{"Items":[{"Value":"en","Label":"English"},{"Value":"zh","Label":"Chinese"}],"NextCursor":null}"""));
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load("vendor", "plugins/vendor").Ok);
            string requestJson = $$"""{"url":"{{server.Origin}}/options","field":"targetLang"}""";
            var outcome = await CapabilityClient.InvokeAsync(session, "vendor", "optionsFetch", requestJson, "job-options",
                [server.Origin], ContractsJson.Default.OptionsResult, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(outcome.Ok, outcome.ErrorDetail);
            Assert.Equal(2, outcome.Result!.Items.Length);
            Assert.Equal("English", outcome.Result.Items[0].Label);
        }
        finally { session.Shutdown(2000); }
    }

    /// <summary>Vocab upsert (PLAN 4.7/F15): operationId/action/word/lang in, status/remoteId out.</summary>
    [Theory]
    [InlineData("applied", "remote-1")]
    [InlineData("found", null)]
    public async Task VocabUpsert_round_trips_through_the_real_broker(string status, string? remoteId)
    {
        string? staged = StageHost();
        if (staged is null) return;
        string remoteIdJson = remoteId is null ? "null" : $"\"{remoteId}\"";
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, $$"""{"Status":"{{status}}","RemoteId":{{remoteIdJson}}}"""));
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load("vendor", "plugins/vendor").Ok);
            string requestJson = $$"""{"url":"{{server.Origin}}/vocab","operationId":"op-1","action":"upsert","word":"hello","lang":"en"}""";
            var outcome = await CapabilityClient.InvokeAsync(session, "vendor", "vocabUpsert", requestJson, "job-vocab",
                [server.Origin], ContractsJson.Default.VocabResult, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(outcome.Ok, outcome.ErrorDetail);
            Assert.Equal(status, outcome.Result!.Status);
            Assert.Equal(remoteId, outcome.Result.RemoteId);
        }
        finally { session.Shutdown(2000); }
    }

    /// <summary>B01-shaped: OCR JSON/ImageBase64 request with a real input FileHandle - the host fills
    /// the Base64 field from the leased file's bytes, the plugin only ever sees the handle's id.</summary>
    [Fact]
    public async Task Ocr_round_trips_a_real_input_file_handle_through_the_real_broker()
    {
        string? staged = StageHost();
        if (staged is null) return;
        byte[] imageBytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4]; // synthetic "PNG-ish" bytes, contents don't matter here
        string leaseDir = Path.Combine(Path.GetTempPath(), "susu-ocr-leases-" + Guid.NewGuid().ToString("N"));
        using var leases = new FileLeases(leaseDir);
        var lease = leases.Create("ocr-input", "png");
        File.WriteAllBytes(leases.PathOf(lease), imageBytes);

        LoopbackHttpRequest? seen = null;
        using var server = new LoopbackHttpServer(req => { seen = req; return LoopbackHttpResponse.Json(200, """{"TextDetections":[{"DetectedText":"hello world"}]}"""); });
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false,
            MakeBroker: () => new Broker(leases: leases)));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load("vendor", "plugins/vendor").Ok);
            string requestJson = "{\"url\":\"" + server.Origin + "/ocr\",\"lang\":\"en\",\"image\":{\"id\":\"" + lease.Id + "\"}}";
            var outcome = await CapabilityClient.InvokeAsync(session, "vendor", "ocr", requestJson, "job-ocr",
                [server.Origin], ContractsJson.Default.OcrResult, handles: [lease.Id], cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(outcome.Ok, outcome.ErrorDetail);
            Assert.Single(outcome.Result!.Blocks);
            Assert.Equal("hello world", outcome.Result.Blocks[0].Text);

            var sentBody = System.Text.Json.Nodes.JsonNode.Parse(seen!.Body)!;
            Assert.Equal(Convert.ToBase64String(imageBytes), sentBody["ImageBase64"]!.GetValue<string>()); // real bytes, host-filled
        }
        finally { session.Shutdown(2000); try { Directory.Delete(leaseDir, recursive: true); } catch (IOException) { } }
    }

    /// <summary>B02-shaped: ASR multipart upload (OpenAI/whisper style) - the audio file goes as a real
    /// multipart field built from a leased FileHandle, "segments" output kind.</summary>
    [Fact]
    public async Task AsrMultipart_uploads_a_real_input_file_handle_through_the_real_broker()
    {
        string? staged = StageHost();
        if (staged is null) return;
        byte[] audioBytes = System.Text.Encoding.ASCII.GetBytes("RIFF-fake-wav-bytes-0123456789");
        string leaseDir = Path.Combine(Path.GetTempPath(), "susu-asr-leases-" + Guid.NewGuid().ToString("N"));
        using var leases = new FileLeases(leaseDir);
        var lease = leases.Create("asr-input", "wav");
        File.WriteAllBytes(leases.PathOf(lease), audioBytes);

        LoopbackHttpRequest? seen = null;
        using var server = new LoopbackHttpServer(req => { seen = req; return LoopbackHttpResponse.Json(200, """{"segments":[{"start":0,"end":1.2,"text":"hi there"}]}"""); });
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false,
            MakeBroker: () => new Broker(leases: leases)));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load("vendor", "plugins/vendor").Ok);
            string requestJson = "{\"url\":\"" + server.Origin + "/asr\",\"model\":\"whisper-1\",\"audio\":{\"id\":\"" + lease.Id + "\"}}";
            var outcome = await CapabilityClient.InvokeAsync(session, "vendor", "asrMultipart", requestJson, "job-asr-multipart",
                [server.Origin], ContractsJson.Default.AsrResult, handles: [lease.Id], cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(outcome.Ok, outcome.ErrorDetail);
            Assert.Equal("segments", outcome.Result!.Kind);
            Assert.Single(outcome.Result.Segments!);
            Assert.Equal("hi there", outcome.Result.Segments![0].Text);

            // The real audio bytes and the model field actually reached the server as multipart content
            // (B02: "整包大小可核验" - the whole upload package, not just a summary, is checkable).
            Assert.NotNull(FindSubsequence(seen!.Body, audioBytes));
            Assert.NotNull(FindSubsequence(seen.Body, "whisper-1"u8.ToArray()));
            Assert.Contains("multipart/form-data", seen.Headers["Content-Type"]);
        }
        finally { session.Shutdown(2000); try { Directory.Delete(leaseDir, recursive: true); } catch (IOException) { } }
    }

    /// <summary>B02-shaped: ASR inlineData (Gemini style) - a JSON Base64 audio field, "text" output kind.</summary>
    [Fact]
    public async Task AsrInline_round_trips_a_real_input_file_handle_through_the_real_broker()
    {
        string? staged = StageHost();
        if (staged is null) return;
        byte[] audioBytes = [1, 2, 3, 4, 5, 6, 7, 8];
        string leaseDir = Path.Combine(Path.GetTempPath(), "susu-asr-inline-leases-" + Guid.NewGuid().ToString("N"));
        using var leases = new FileLeases(leaseDir);
        var lease = leases.Create("asr-input", "wav");
        File.WriteAllBytes(leases.PathOf(lease), audioBytes);

        LoopbackHttpRequest? seen = null;
        using var server = new LoopbackHttpServer(req => { seen = req; return LoopbackHttpResponse.Json(200, """{"text":"transcribed text"}"""); });
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false,
            MakeBroker: () => new Broker(leases: leases)));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load("vendor", "plugins/vendor").Ok);
            string requestJson = "{\"url\":\"" + server.Origin + "/asr-inline\",\"model\":\"gemini-2.5-flash\",\"audio\":{\"id\":\"" + lease.Id + "\"}}";
            var outcome = await CapabilityClient.InvokeAsync(session, "vendor", "asrInline", requestJson, "job-asr-inline",
                [server.Origin], ContractsJson.Default.AsrResult, handles: [lease.Id], cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(outcome.Ok, outcome.ErrorDetail);
            Assert.Equal("text", outcome.Result!.Kind);
            Assert.Equal("transcribed text", outcome.Result.Text);

            var sentBody = System.Text.Json.Nodes.JsonNode.Parse(seen!.Body)!;
            Assert.Equal(Convert.ToBase64String(audioBytes), sentBody["InlineAudio"]!.GetValue<string>());
        }
        finally { session.Shutdown(2000); try { Directory.Delete(leaseDir, recursive: true); } catch (IOException) { } }
    }

    private static int? FindSubsequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++) if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return null;
    }

    private static async Task<FileLease?> InvokeAdoptingAsync(HostSession session, string capability, string requestJson, string origin)
    {
        var (requestId, _, task) = session.Invoke("vendor", capability, requestJson, $"job-{capability}-adopt", [origin], adoptResultFiles: true);
        var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var result = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!.Result!.Value.Deserialize(ContractsJson.Default.TtsResult)!;
        var adopted = session.TakeAdoptedFiles(requestId);
        return adopted.SingleOrDefault(l => l.Id == result.Audio.Id);
    }

    /// <summary>B03-shaped: TTS JSON response - a Base64 audio field extracted host-side into a fresh
    /// FileHandle; the plugin only ever sees {id, mime, bytes}, never the encoded audio bytes.</summary>
    [Fact]
    public async Task TtsJson_extracts_a_real_output_file_handle_through_the_real_broker()
    {
        string? staged = StageHost();
        if (staged is null) return;
        byte[] audioBytes = [0x49, 0x44, 0x33, 1, 2, 3, 4, 5]; // synthetic MP3-ish bytes
        string audioBase64 = Convert.ToBase64String(audioBytes);
        string leaseDir = Path.Combine(Path.GetTempPath(), "susu-tts-leases-" + Guid.NewGuid().ToString("N"));
        using var leases = new FileLeases(leaseDir);

        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200,
            "{\"Response\":{\"Audio\":\"" + audioBase64 + "\",\"RequestId\":\"r1\"}}"));
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false,
            MakeBroker: () => new Broker(leases: leases)));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load("vendor", "plugins/vendor").Ok);
            string requestJson = "{\"url\":\"" + server.Origin + "/tts\",\"text\":\"hello\",\"voice\":\"female\"}";
            var outcome = await CapabilityClient.InvokeAsync(session, "vendor", "ttsJson", requestJson, "job-tts-json",
                [server.Origin], ContractsJson.Default.TtsResult, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(outcome.Ok, outcome.ErrorDetail);
            Assert.Equal("audio/mpeg", outcome.Result!.Audio.Mime);
            Assert.Equal(audioBytes.LongLength, outcome.Result.Audio.Bytes);

            // B07: a result file nobody adopted is released with the call's grant; the host takes it over only on request (F10.1).
            Assert.Null(leases.AddReference(outcome.Result.Audio.Id));
            var lease = await InvokeAdoptingAsync(session, "ttsJson", requestJson, server.Origin);
            Assert.NotNull(lease);
            Assert.Equal(audioBytes, File.ReadAllBytes(leases.PathOf(lease!))); // the real decoded bytes are on disk, not just referenced
        }
        finally { session.Shutdown(2000); try { Directory.Delete(leaseDir, recursive: true); } catch (IOException) { } }
    }

    /// <summary>B04-shaped: a raw (non-JSON) TTS audio response becomes a file handle result directly,
    /// with no JSON parsing attempted on the audio bytes.</summary>
    [Fact]
    public async Task TtsRaw_returns_a_real_output_file_handle_through_the_real_broker()
    {
        string? staged = StageHost();
        if (staged is null) return;
        byte[] audioBytes = [0xFF, 0xFB, 9, 9, 9, 9];
        string leaseDir = Path.Combine(Path.GetTempPath(), "susu-tts-raw-leases-" + Guid.NewGuid().ToString("N"));
        using var leases = new FileLeases(leaseDir);

        using var server = new LoopbackHttpServer(_ => new LoopbackHttpResponse(200, audioBytes, ContentType: "audio/mpeg"));
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false,
            MakeBroker: () => new Broker(leases: leases)));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load("vendor", "plugins/vendor").Ok);
            string requestJson = "{\"url\":\"" + server.Origin + "/tts-raw\",\"text\":\"hello\"}";
            var outcome = await CapabilityClient.InvokeAsync(session, "vendor", "ttsRaw", requestJson, "job-tts-raw",
                [server.Origin], ContractsJson.Default.TtsResult, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(outcome.Ok, outcome.ErrorDetail);
            Assert.Equal("audio/mpeg", outcome.Result!.Audio.Mime);

            // B07: a result file nobody adopted is released with the call's grant; the host takes it over only on request (F10.1).
            Assert.Null(leases.AddReference(outcome.Result.Audio.Id));
            var lease = await InvokeAdoptingAsync(session, "ttsRaw", requestJson, server.Origin);
            Assert.NotNull(lease);
            Assert.Equal(audioBytes, File.ReadAllBytes(leases.PathOf(lease!)));
        }
        finally { session.Shutdown(2000); try { Directory.Delete(leaseDir, recursive: true); } catch (IOException) { } }
    }
}
