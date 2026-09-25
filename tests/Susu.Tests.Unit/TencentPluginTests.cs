using System.Text.Json;
using Susu.Contracts;
using Susu.Domain;
using Susu.Net;
using Susu.Plugins;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F06.2b P-T02: the real, shipped Tencent TMT package (src/Susu.Host/plugins/tencent-translate) through
/// the real AppContainer sandbox + QuickJS engine + IPC + Broker/$http + host `tencent-tc3` signer, against
/// a local LoopbackHttpServer standing in for tmt.tencentcloudapi.com. Needs susu.exe already published
/// (`dotnet publish src/Susu.Host -c Release -r win-x64`); skips itself otherwise.
///
/// There is no real Tencent Cloud account available to this agent - the real-vendor call is
/// "not executed - no account" here.
/// </summary>
public class TencentPluginTests
{
    private const string PackageId = "app.susu.tencent-translate";
    private const string InstanceId = "tencent-translate";
    private const string PackageDir = "plugins/tencent-translate";
    private const string Signer = "unsigned:app.susu.tencent-translate";
    private const string SecretId = "AKIDtestSecretId0123456789";
    private const string SecretKey = "testSecretKey0123456789abcdef";

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

    /// <summary>Stages susu.exe/DLLs plus the *real* shipped package into the AppContainer's scoped resource directory.</summary>
    private static string? StageHost()
    {
        string? output = FindHostBuildOutput();
        if (output is null) return null;
        string sourcePlugin = Path.Combine(output, "plugins", "tencent-translate");
        if (!File.Exists(Path.Combine(sourcePlugin, "main.js"))) return null;
        string staged = Path.Combine(Path.GetTempPath(), "susu-tencent-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staged);
        foreach (string file in Directory.EnumerateFiles(output))
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(staged, Path.GetFileName(file)));
        string targetPlugin = Path.Combine(staged, "plugins", "tencent-translate");
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

    /// <summary>
    /// One shared Tencent Cloud account (PLAN 1.3: TMT and OCR use the same SecretId/SecretKey) holding
    /// both secrets, granted to the translate package and - separately confirmed - to Tencent OCR, and
    /// bound to the "tencent-translate" instance. Grants carry both the documented use
    /// ("signer:tencent-tc3") and the one today's Broker actually checks for named signers
    /// ("header:Authorization", NetworkBroker's named-sign ResolveSecret) so the test holds either way.
    /// </summary>
    private static HostSession.Options Options(string staged, string origin)
    {
        var secrets = new FakeSecretStore();
        secrets.Set("account-tencent", "secretId", SecretId);
        secrets.Set("account-tencent", "secretKey", SecretKey);
        string normalized = Origin.Normalize(origin);
        var grants = new List<CredentialGrant>();
        foreach (string secret in new[] { "secretId", "secretKey" })
            foreach (string use in new[] { "signer:tencent-tc3", "header:Authorization" })
            {
                grants.Add(new CredentialGrant(PackageId, Signer, secret, normalized, use));
                grants.Add(new CredentialGrant("app.susu.tencent-ocr", "builtin", secret, "https://ocr.tencentcloudapi.com:443", use));
            }
        var account = new AccountSettings("account-tencent", "Tencent Cloud", ["secretId", "secretKey"], grants);
        var instance = new InstanceSettings(InstanceId, PackageId, 1, new Dictionary<string, string>(),
            new Dictionary<string, string> { ["secretId"] = "account-tencent", ["secretKey"] = "account-tencent" });
        var accounts = new AccountAuthorization(() => ([account], [instance]));
        return new(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false,
            MakeBroker: () => new Broker(secretStore: secrets, accounts: accounts));
    }

    private static int jobCounter;

    private static async Task<CompletedPayload> RunAsync(string staged, string origin, TranslateRequest request, object? config = null)
    {
        using var session = HostSession.Start(Options(staged, origin));
        try
        {
            session.Broker.ApproveLocalOrigin(origin);
            Assert.True(session.Load(PackageId, PackageDir).Ok);
            string requestJson = JsonSerializer.Serialize(request, ContractsJson.Default.TranslateRequest);
            string configJson = JsonSerializer.Serialize(config ?? new { baseUrl = origin });
            var (_, _, task) = session.Invoke(PackageId, "translate", requestJson, jobId: $"job-tc-{Interlocked.Increment(ref jobCounter)}", origins: [origin],
                secrets: ["secretId", "secretKey"], configJson: configJson, instanceId: InstanceId, signer: Signer);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
        }
        finally { session.Shutdown(2000); }
    }

    private static string? H(LoopbackHttpRequest req, string name)
        => req.Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string Ok(string text, string source = "en", string target = "zh")
        => JsonSerializer.Serialize(new { Response = new { TargetText = text, Source = source, Target = target, RequestId = "req-1" } });

    private static string VendorError(string code)
        => JsonSerializer.Serialize(new { Response = new { Error = new { Code = code, Message = "vendor says no" }, RequestId = "req-1" } });

    [Fact] // P-T02: TC3 signature from the host signer, the TMT action headers, and canonical -> TMT language codes
    public async Task Signs_with_TC3_and_maps_canonical_languages()
    {
        string? staged = StageHost();
        if (staged is null) return;
        LoopbackHttpRequest? captured = null;
        using var server = new LoopbackHttpServer(req => { captured = req; return LoopbackHttpResponse.Json(200, Ok("ä½ å¥½")); });
        var completed = await RunAsync(staged, server.Origin, new TranslateRequest("hello", "en", "zh-Hans"));
        Assert.True(completed.Ok, completed.Error?.Detail);
        Assert.Equal("ä½ å¥½", completed.Result!.Value.Deserialize(ContractsJson.Default.TranslateResult)!.Text);

        Assert.NotNull(captured);
        Assert.Equal("POST", captured.Method);
        Assert.Equal("/", captured.Path);
        Assert.Equal("TextTranslate", H(captured, "X-TC-Action"));
        Assert.Equal("2018-03-21", H(captured, "X-TC-Version"));
        Assert.Equal("ap-guangzhou", H(captured, "X-TC-Region")); // default region
        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal("hello", body.RootElement.GetProperty("SourceText").GetString());
        Assert.Equal("en", body.RootElement.GetProperty("Source").GetString());
        Assert.Equal("zh", body.RootElement.GetProperty("Target").GetString()); // canonical zh-Hans -> TMT zh
        Assert.Equal(0, body.RootElement.GetProperty("ProjectId").GetInt32());
        Assert.DoesNotContain(SecretKey, System.Text.Encoding.UTF8.GetString(captured.Body));

        // The Authorization header is exactly what the F05 signer computes for the bytes that went on the
        // wire at the timestamp the host stamped - recomputed here independently of the plugin.
        string? authorization = H(captured, "Authorization");
        long timestamp = long.Parse(H(captured, "X-TC-Timestamp")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(timestamp, DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
        var signedHeaders = new[] { "Content-Type", "X-TC-Action", "X-TC-Version", "X-TC-Region" }
            .Select(n => new KeyValuePair<string, string>(n, H(captured, n)!)).ToList();
        var expected = TencentTc3Signer.Sign(new SignableRequest("POST", new Uri(server.Origin + "/"), signedHeaders, captured.Body),
            "tmt", SecretId, SecretKey, DateTimeOffset.FromUnixTimeSeconds(timestamp));
        Assert.Equal(expected.Headers.Single(h => h.Key == "Authorization").Value, authorization);
        string date = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.StartsWith($"TC3-HMAC-SHA256 Credential={SecretId}/{date}/tmt/tc3_request, SignedHeaders=content-type;host;x-tc-action;x-tc-region;x-tc-version, Signature=", authorization);
    }

    [Fact] // region config reaches X-TC-Region; auto source sends "auto" and maps the detected TMT code back
    public async Task Region_config_and_auto_source_with_detected_language()
    {
        string? staged = StageHost();
        if (staged is null) return;
        LoopbackHttpRequest? captured = null;
        using var server = new LoopbackHttpServer(req => { captured = req; return LoopbackHttpResponse.Json(200, Ok("hello", source: "zh", target: "en")); });
        var completed = await RunAsync(staged, server.Origin, new TranslateRequest("ä½ å¥½", null, "en"), new { baseUrl = server.Origin, region = "ap-shanghai" });
        Assert.True(completed.Ok, completed.Error?.Detail);
        var result = completed.Result!.Value.Deserialize(ContractsJson.Default.TranslateResult)!;
        Assert.Equal("hello", result.Text);
        Assert.Equal("zh-Hans", result.DetectedFrom);
        Assert.Equal("ap-shanghai", H(captured!, "X-TC-Region"));
        using var body = JsonDocument.Parse(captured!.Body);
        Assert.Equal("auto", body.RootElement.GetProperty("Source").GetString());
        Assert.Equal("en", body.RootElement.GetProperty("Target").GetString());
    }

    [Theory] // J05: TMT reports failures as HTTP 200 + Response.Error.Code
    [InlineData("AuthFailure.SignatureFailure", ErrorKind.Auth)]
    [InlineData("AuthFailure.SecretIdNotFound", ErrorKind.Auth)]
    [InlineData("FailedOperation.UserNotRegistered", ErrorKind.Auth)]
    [InlineData("FailedOperation.NoFreeAmount", ErrorKind.Quota)]
    [InlineData("FailedOperation.ServiceIsolate", ErrorKind.Quota)]
    [InlineData("RequestLimitExceeded", ErrorKind.RateLimited)]
    [InlineData("LimitExceeded.LimitedAccessFrequency", ErrorKind.RateLimited)]
    [InlineData("UnsupportedOperation.UnsupportedLanguage", ErrorKind.UnsupportedLanguage)]
    [InlineData("UnsupportedOperation.UnSupportedTargetLanguage", ErrorKind.UnsupportedLanguage)]
    [InlineData("InternalError.BackendTimeout", ErrorKind.Network)]
    [InlineData("UnsupportedOperation.TextTooLong", ErrorKind.BadResponse)]
    public async Task Vendor_error_codes_map_to_host_error_kinds(string code, ErrorKind expected)
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, VendorError(code)));
        var completed = await RunAsync(staged, server.Origin, new TranslateRequest("hi", "en", "zh-Hans"));
        Assert.False(completed.Ok);
        Assert.Equal(expected, ErrorKinds.FromPlugin(completed.Error?.Kind));
        Assert.Contains(code, completed.Error?.Detail);
    }

    [Theory]
    [InlineData(503, ErrorKind.Network)]
    [InlineData(403, ErrorKind.Auth)]
    [InlineData(429, ErrorKind.RateLimited)]
    public async Task Http_status_failures_map_to_host_error_kinds(int status, ErrorKind expected)
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Text(status, "nope"));
        var completed = await RunAsync(staged, server.Origin, new TranslateRequest("hi", "en", "zh-Hans"));
        Assert.False(completed.Ok);
        Assert.Equal(expected, ErrorKinds.FromPlugin(completed.Error?.Kind));
    }

    [Fact] // nothing listening on the origin: connection refused surfaces as network
    public async Task Unreachable_server_maps_to_network()
    {
        string? staged = StageHost();
        if (staged is null) return;
        string origin;
        using (var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, Ok("x")))) origin = server.Origin;
        var completed = await RunAsync(staged, origin, new TranslateRequest("hi", "en", "zh-Hans"));
        Assert.False(completed.Ok);
        Assert.Equal(ErrorKind.Network, ErrorKinds.FromPlugin(completed.Error?.Kind));
    }

    [Fact] // TextTranslate takes under 6000 characters per request: 5999 goes out, 6000 is refused before any request
    public async Task Text_length_limit_is_enforced_before_the_request()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, Ok("ok")));
        var atLimit = await RunAsync(staged, server.Origin, new TranslateRequest(new string('a', 5999), "en", "zh-Hans"));
        Assert.True(atLimit.Ok, atLimit.Error?.Detail);
        Assert.Equal(1, server.RequestCount);
        var over = await RunAsync(staged, server.Origin, new TranslateRequest(new string('a', 6000), "en", "zh-Hans"));
        Assert.False(over.Ok);
        Assert.Equal(ErrorKind.BadResponse, ErrorKinds.FromPlugin(over.Error?.Kind));
        Assert.Contains("too long", over.Error?.Detail);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact] // a language TMT does not offer is refused locally as unsupported_language, no request sent
    public async Task Unsupported_target_language_is_refused_without_a_request()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, Ok("x")));
        var completed = await RunAsync(staged, server.Origin, new TranslateRequest("hi", "en", "xx"));
        Assert.False(completed.Ok);
        Assert.Equal(ErrorKind.UnsupportedLanguage, ErrorKinds.FromPlugin(completed.Error?.Kind));
        Assert.Equal(0, server.RequestCount);
    }
}
