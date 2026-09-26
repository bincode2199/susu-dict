using System.Text.Json;
using Susu.Contracts;
using Susu.Domain;
using Susu.Plugins;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F06.2a P-A01: the real, shipped OpenAI package (src/Susu.Host/plugins/openai, not a test fixture)
/// through the real AppContainer sandbox + QuickJS engine + IPC + Broker/$http.stream pipeline, against a
/// local LoopbackHttpServer standing in for the vendor - same shape as MyMemoryPluginTests. Needs
/// susu.exe already published (`dotnet publish src/Susu.Host -c Release -r win-x64`) and the native
/// bridge rebuilt (tools/build-native.ps1's cmake step, for ctx.$emit); skips itself otherwise.
///
/// There is no real OpenAI account available to this agent - the real-vendor call TEST-PLAN expects
/// (like RealVendorCallTests does for MyMemory/DeepL/Tencent) is "not executed - no account" here.
/// </summary>
public class OpenAIPluginTests
{
    internal const string PackageId = "app.susu.openai";
    internal const string InstanceId = "openai";
    internal const string Signer = "unsigned:app.susu.openai";

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

    /// <summary>Stages susu.exe/DLLs plus the *real* shipped openai package into the AppContainer's
    /// scoped resource directory (not a fixtures/plugins copy).</summary>
    internal static string? StageHost()
    {
        string? output = FindHostBuildOutput();
        if (output is null) return null;
        string sourcePlugin = Path.Combine(output, "plugins", "openai");
        if (!File.Exists(Path.Combine(sourcePlugin, "main.js"))) return null;
        string staged = TestTemp.NewDir("susu-openai-it");
        foreach (string file in Directory.EnumerateFiles(output))
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(staged, Path.GetFileName(file)));
        string targetPlugin = Path.Combine(staged, "plugins", "openai");
        Directory.CreateDirectory(targetPlugin);
        foreach (string file in Directory.EnumerateFiles(sourcePlugin))
            File.Copy(file, Path.Combine(targetPlugin, Path.GetFileName(file)));
        return staged;
    }

    private sealed class FakeSecretStore : Susu.Abstractions.ISecretStore
    {
        private readonly Dictionary<(string, string), string> values = [];
        public void Set(string account, string name, string value) => values[(account, name)] = value;
        public bool Has(string a, string n) => values.ContainsKey((a, n));
        public IReadOnlyList<string> Names(string a) => [.. values.Keys.Where(k => k.Item1 == a).Select(k => k.Item2)];
        public void Write(string a, string n, ReadOnlySpan<char> v) => values[(a, n)] = v.ToString();
        public bool Delete(string a, string n) => values.Remove((a, n));
        public bool TryRead(string a, string n, out string v) => values.TryGetValue((a, n), out v!);
    }

    /// <summary>The S02-bound account/instance a real deployment's settings UI would produce (F06.3):
    /// one account holding "apiKey", granted to this exact package/signer/origin/use, bound to the
    /// "openai" instance.</summary>
    internal static HostSession.Options Options(string staged, string origin)
    {
        var secrets = new FakeSecretStore();
        secrets.Set("account-openai", "apiKey", "sk-test-secret-value");
        var account = new AccountSettings("account-openai", "My OpenAI", ["apiKey"],
            [new CredentialGrant(PackageId, Signer, "apiKey", origin, "header:Authorization")]);
        var instance = new InstanceSettings(InstanceId, PackageId, 1, new Dictionary<string, string>(), new Dictionary<string, string> { ["apiKey"] = "account-openai" });
        var accounts = new AccountAuthorization(() => ([account], [instance]));
        return new(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false,
            MakeBroker: () => new Broker(secretStore: secrets, accounts: accounts));
    }

    private static string ConfigJson(string baseUrl) => JsonSerializer.Serialize(new { baseUrl });

    internal static string Sse(params string[] deltas)
    {
        var text = new System.Text.StringBuilder();
        foreach (var d in deltas)
            text.Append("data: ").Append(JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = d } } } })).Append("\n\n");
        text.Append("data: [DONE]\n\n");
        return text.ToString();
    }

    [Fact]
    public async Task Streams_deltas_through_ctx_emit_and_assembles_the_final_text()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(async (req, stream, ct) =>
        {
            Assert.Equal("Bearer sk-test-secret-value", req.Headers["Authorization"]);
            await LoopbackHttpServer.WriteSseHeadAsync(stream, ct);
            foreach (var piece in new[] { Sse("Hello") /* first event alone */, "" })
            {
                if (piece.Length == 0) continue;
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(piece), ct);
            }
        });
        using var session = HostSession.Start(Options(staged, server.Origin));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load(PackageId, "plugins/openai").Ok);
            string requestJson = JsonSerializer.Serialize(new TranslateRequest("hi", "en", "zh-Hans"), ContractsJson.Default.TranslateRequest);
            var pieces = new List<string>();
            var (_, _, task) = session.Invoke(PackageId, "translate", requestJson, jobId: "job-oa-1", origins: [server.Origin],
                secrets: ["apiKey"], configJson: ConfigJson(server.Origin), instanceId: InstanceId, signer: Signer,
                onChunk: text => { pieces.Add(text); return ValueTask.CompletedTask; });
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
            Assert.True(completed.Ok, completed.Error?.Detail);
            var result = completed.Result!.Value.Deserialize(ContractsJson.Default.TranslateResult)!;
            Assert.Equal("Hello", result.Text);
            Assert.Equal(["Hello"], pieces); // ctx.$emit reached the host's onChunk in order
        }
        finally { session.Shutdown(2000); }
    }

    [Fact]
    public async Task An_SSE_event_split_across_two_pieces_is_reassembled()
    {
        string? staged = StageHost();
        if (staged is null) return;
        string full = Sse("Bon", "jour"); // two delta events -> "Bonjour"
        using var server = new LoopbackHttpServer(async (_, stream, ct) =>
        {
            await LoopbackHttpServer.WriteSseHeadAsync(stream, ct);
            int mid = full.Length / 2; // split mid-event on purpose
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(full[..mid]), ct);
            await Task.Delay(20, ct);
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(full[mid..]), ct);
        });
        using var session = HostSession.Start(Options(staged, server.Origin));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load(PackageId, "plugins/openai").Ok);
            string requestJson = JsonSerializer.Serialize(new TranslateRequest("hi", "en", "fr"), ContractsJson.Default.TranslateRequest);
            var (_, _, task) = session.Invoke(PackageId, "translate", requestJson, jobId: "job-oa-2", origins: [server.Origin],
                secrets: ["apiKey"], configJson: ConfigJson(server.Origin), instanceId: InstanceId, signer: Signer);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
            Assert.True(completed.Ok, completed.Error?.Detail);
            var result = completed.Result!.Value.Deserialize(ContractsJson.Default.TranslateResult)!;
            Assert.Equal("Bonjour", result.Text);
        }
        finally { session.Shutdown(2000); }
    }

    [Fact]
    public async Task A_429_with_insufficient_quota_maps_to_quota_not_rate_limited()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(429, """{"error":{"code":"insufficient_quota","message":"you exceeded your quota"}}"""));
        using var session = HostSession.Start(Options(staged, server.Origin));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load(PackageId, "plugins/openai").Ok);
            string requestJson = JsonSerializer.Serialize(new TranslateRequest("hi", "en", "fr"), ContractsJson.Default.TranslateRequest);
            var (_, _, task) = session.Invoke(PackageId, "translate", requestJson, jobId: "job-oa-3", origins: [server.Origin], secrets: ["apiKey"], configJson: ConfigJson(server.Origin), instanceId: InstanceId, signer: Signer);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
            Assert.False(completed.Ok);
            Assert.Equal(ErrorKind.Quota, ErrorKinds.FromPlugin(completed.Error?.Kind));
        }
        finally { session.Shutdown(2000); }
    }

    [Fact]
    public async Task A_429_without_a_quota_code_maps_to_rate_limited_with_Retry_After_passed_through()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(req =>
        {
            var response = LoopbackHttpResponse.Json(429, """{"error":{"code":"rate_limit_exceeded","message":"slow down"}}""");
            return response with { Headers = new Dictionary<string, string>(response.Headers ?? new Dictionary<string, string>()) { ["Retry-After"] = "7" } };
        });
        using var session = HostSession.Start(Options(staged, server.Origin));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load(PackageId, "plugins/openai").Ok);
            string requestJson = JsonSerializer.Serialize(new TranslateRequest("hi", "en", "fr"), ContractsJson.Default.TranslateRequest);
            var (_, _, task) = session.Invoke(PackageId, "translate", requestJson, jobId: "job-oa-4", origins: [server.Origin], secrets: ["apiKey"], configJson: ConfigJson(server.Origin), instanceId: InstanceId, signer: Signer);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
            Assert.False(completed.Ok);
            Assert.Equal(ErrorKind.RateLimited, ErrorKinds.FromPlugin(completed.Error?.Kind));
            Assert.Equal(TimeSpan.FromSeconds(7), RetryPolicy.ParseRetryAfter(completed.Error?.RetryAfterRaw, DateTimeOffset.UtcNow));
        }
        finally { session.Shutdown(2000); }
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Unauthorized_maps_to_auth(int status)
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(status, """{"error":{"message":"invalid api key"}}"""));
        using var session = HostSession.Start(Options(staged, server.Origin));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load(PackageId, "plugins/openai").Ok);
            string requestJson = JsonSerializer.Serialize(new TranslateRequest("hi", "en", "fr"), ContractsJson.Default.TranslateRequest);
            var (_, _, task) = session.Invoke(PackageId, "translate", requestJson, jobId: "job-oa-5", origins: [server.Origin], secrets: ["apiKey"], configJson: ConfigJson(server.Origin), instanceId: InstanceId, signer: Signer);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
            Assert.False(completed.Ok);
            Assert.Equal(ErrorKind.Auth, ErrorKinds.FromPlugin(completed.Error?.Kind));
        }
        finally { session.Shutdown(2000); }
    }

    [Fact]
    public async Task A_5xx_maps_to_network_a_transient_shape_the_host_may_retry()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(503, """{"error":{"message":"overloaded"}}"""));
        using var session = HostSession.Start(Options(staged, server.Origin));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load(PackageId, "plugins/openai").Ok);
            string requestJson = JsonSerializer.Serialize(new TranslateRequest("hi", "en", "fr"), ContractsJson.Default.TranslateRequest);
            var (_, _, task) = session.Invoke(PackageId, "translate", requestJson, jobId: "job-oa-6", origins: [server.Origin], secrets: ["apiKey"], configJson: ConfigJson(server.Origin), instanceId: InstanceId, signer: Signer);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
            Assert.False(completed.Ok);
            Assert.Equal(ErrorKind.Network, ErrorKinds.FromPlugin(completed.Error?.Kind));
        }
        finally { session.Shutdown(2000); }
    }

    [Fact]
    public async Task Cancelling_mid_stream_stops_the_call_instead_of_completing_it()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var gate = new SemaphoreSlim(0);
        using var server = new LoopbackHttpServer(async (_, stream, ct) =>
        {
            await LoopbackHttpServer.WriteSseHeadAsync(stream, ct);
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Sse("first")), ct);
            gate.Release();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { } // held open until the client disconnects
        });
        using var session = HostSession.Start(Options(staged, server.Origin));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load(PackageId, "plugins/openai").Ok);
            string requestJson = JsonSerializer.Serialize(new TranslateRequest("hi", "en", "fr"), ContractsJson.Default.TranslateRequest);
            var (requestId, callId, task) = session.Invoke(PackageId, "translate", requestJson, jobId: "job-oa-7", origins: [server.Origin], secrets: ["apiKey"], configJson: ConfigJson(server.Origin), instanceId: InstanceId, signer: Signer);
            await gate.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); // at least one piece arrived: the call is genuinely mid-stream
            session.Cancel(PackageId, requestId, "job-oa-7", callId);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
            Assert.False(completed.Ok);
            Assert.Equal("cancelled", completed.Error?.Kind);
        }
        finally { session.Shutdown(2000); }
    }

    // ---- F07.2: the optional `options` method (model list from GET /v1/models) ----

    private static async Task<CapabilityOutcome<OptionsResult>> Options(HostSession session, string origin, string? cursor)
        => await CapabilityClient.InvokeAsync(session, PackageId, "options", JsonSerializer.Serialize(new OptionsRequest("model", 3, cursor), ContractsJson.Default.OptionsRequest),
            "job-oa-options", [origin], ContractsJson.Default.OptionsResult, ["apiKey"], configJson: ConfigJson(origin), timeout: TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken, instanceId: InstanceId, signer: Signer);

    [Fact]
    public async Task Options_lists_chat_models_from_v1_models_in_pages_of_200()
    {
        string? staged = StageHost();
        if (staged is null) return;
        var ids = Enumerable.Range(0, 230).Select(i => $"model-{i:D3}").Concat(["text-embedding-3-small", "whisper-1", "tts-1", "dall-e-3", "omni-moderation-latest"]);
        string body = JsonSerializer.Serialize(new { @object = "list", data = ids.Select(id => new { id, @object = "model", owned_by = "system" }) });
        var seen = new List<LoopbackHttpRequest>();
        using var server = new LoopbackHttpServer(req => { lock (seen) seen.Add(req); return LoopbackHttpResponse.Json(200, body); });
        using var session = HostSession.Start(Options(staged, server.Origin));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load(PackageId, "plugins/openai").Ok);
            var first = await Options(session, server.Origin, null);
            Assert.True(first.Ok, first.ErrorDetail);
            Assert.Equal(200, first.Result!.Items.Length);
            Assert.Equal("model-000", first.Result.Items[0].Value);
            Assert.Equal("200", first.Result.NextCursor);
            var second = await Options(session, server.Origin, first.Result.NextCursor);
            Assert.True(second.Ok, second.ErrorDetail);
            Assert.Equal(Enumerable.Range(200, 30).Select(i => $"model-{i:D3}"), second.Result!.Items.Select(i => i.Value));
            Assert.Null(second.Result.NextCursor);
            Assert.All(seen, r => { Assert.Equal("GET", r.Method); Assert.Equal("/v1/models", r.Path); Assert.Equal("Bearer sk-test-secret-value", r.Headers["Authorization"]); });
        }
        finally { session.Shutdown(2000); }
    }

    [Fact]
    public async Task Options_failure_is_classified_and_carries_no_key()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(401, """{"error":{"message":"Incorrect API key provided: sk-test-****alue","code":"invalid_api_key"}}"""));
        using var session = HostSession.Start(Options(staged, server.Origin));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load(PackageId, "plugins/openai").Ok);
            var outcome = await Options(session, server.Origin, null);
            Assert.False(outcome.Ok);
            Assert.Equal(ErrorKind.Auth, outcome.ErrorKind);
            Assert.DoesNotContain("sk-test-secret-value", outcome.ErrorDetail ?? "");
        }
        finally { session.Shutdown(2000); }
    }
}