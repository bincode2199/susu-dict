using System.Text.Json;
using Susu.Contracts;
using Susu.Domain;
using Susu.Plugins;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F06.2b P-T03: the real, shipped DeepL package (src/Susu.Host/plugins/deepl) through the real
/// AppContainer sandbox + QuickJS engine + IPC + Broker/$http credential injection, against a local
/// LoopbackHttpServer standing in for api(-free).deepl.com. Needs susu.exe already published
/// (`dotnet publish src/Susu.Host -c Release -r win-x64`); skips itself otherwise.
///
/// There is no real DeepL account available to this agent - the real-vendor call is
/// "not executed - no account" here.
/// </summary>
public class DeepLPluginTests
{
    private const string PackageId = "app.susu.deepl";
    private const string InstanceId = "deepl";
    private const string PackageDir = "plugins/deepl";
    private const string Signer = "unsigned:app.susu.deepl";
    private const string ApiKey = "00000000-test-key-0000:fx";

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
        string sourcePlugin = Path.Combine(output, "plugins", "deepl");
        if (!File.Exists(Path.Combine(sourcePlugin, "main.js"))) return null;
        string staged = TestTemp.NewDir("susu-deepl-it");
        foreach (string file in Directory.EnumerateFiles(output))
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(staged, Path.GetFileName(file)));
        string targetPlugin = Path.Combine(staged, "plugins", "deepl");
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

    /// <summary>One account holding "apiKey", granted to this package/signer/origin for header:Authorization, bound to "deepl".</summary>
    private static HostSession.Options Options(string staged, string origin)
    {
        var secrets = new FakeSecretStore();
        secrets.Set("account-deepl", "apiKey", ApiKey);
        var account = new AccountSettings("account-deepl", "My DeepL", ["apiKey"],
            [new CredentialGrant(PackageId, Signer, "apiKey", Origin.Normalize(origin), "header:Authorization")]);
        var instance = new InstanceSettings(InstanceId, PackageId, 1, new Dictionary<string, string>(), new Dictionary<string, string> { ["apiKey"] = "account-deepl" });
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
            var (_, _, task) = session.Invoke(PackageId, "translate", requestJson, jobId: $"job-dl-{Interlocked.Increment(ref jobCounter)}", origins: [origin],
                secrets: ["apiKey"], configJson: configJson, instanceId: InstanceId, signer: Signer);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            return envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
        }
        finally { session.Shutdown(2000); }
    }

    private static string? H(LoopbackHttpRequest req, string name)
        => req.Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string Ok(string text, string detected = "EN")
        => JsonSerializer.Serialize(new { translations = new[] { new { detected_source_language = detected, text } } });

    private static LoopbackHttpResponse WithRetryAfter(LoopbackHttpResponse response, string value)
        => response with { Headers = new Dictionary<string, string>(response.Headers ?? new Dictionary<string, string>()) { ["Retry-After"] = value } };

    [Fact] // P-T03: DeepL-Auth-Key header from the bound account, /v2/translate, canonical -> DeepL codes
    public async Task Injects_the_auth_key_and_maps_canonical_languages()
    {
        string? staged = StageHost();
        if (staged is null) return;
        LoopbackHttpRequest? captured = null;
        using var server = new LoopbackHttpServer(req => { captured = req; return LoopbackHttpResponse.Json(200, Ok("你好")); });
        var completed = await RunAsync(staged, server.Origin, new TranslateRequest("hello", "en", "zh-Hans"));
        Assert.True(completed.Ok, completed.Error?.Detail);
        var result = completed.Result!.Value.Deserialize(ContractsJson.Default.TranslateResult)!;
        Assert.Equal("你好", result.Text);
        Assert.Null(result.DetectedFrom); // source was given, nothing detected

        Assert.NotNull(captured);
        Assert.Equal("POST", captured.Method);
        Assert.Equal("/v2/translate", captured.Path);
        Assert.Equal($"DeepL-Auth-Key {ApiKey}", H(captured, "Authorization"));
        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal("hello", Assert.Single(body.RootElement.GetProperty("text").EnumerateArray()).GetString());
        Assert.Equal("EN", body.RootElement.GetProperty("source_lang").GetString());
        Assert.Equal("ZH-HANS", body.RootElement.GetProperty("target_lang").GetString()); // canonical zh-Hans -> DeepL ZH-HANS
    }

    [Fact] // auto source: no source_lang sent, target en -> EN-US (bare EN is deprecated as a target), detected ZH -> zh-Hans
    public async Task Auto_source_omits_source_lang_and_maps_the_detected_language_back()
    {
        string? staged = StageHost();
        if (staged is null) return;
        LoopbackHttpRequest? captured = null;
        using var server = new LoopbackHttpServer(req => { captured = req; return LoopbackHttpResponse.Json(200, Ok("hello", detected: "ZH")); });
        var completed = await RunAsync(staged, server.Origin, new TranslateRequest("你好", null, "en"));
        Assert.True(completed.Ok, completed.Error?.Detail);
        var result = completed.Result!.Value.Deserialize(ContractsJson.Default.TranslateResult)!;
        Assert.Equal("hello", result.Text);
        Assert.Equal("zh-Hans", result.DetectedFrom);
        using var body = JsonDocument.Parse(captured!.Body);
        Assert.False(body.RootElement.TryGetProperty("source_lang", out _));
        Assert.Equal("EN-US", body.RootElement.GetProperty("target_lang").GetString());
    }

    [Theory] // without baseUrl, plan picks the vendor endpoint; the call's origin list (the local server only) refuses it, which names the host it tried
    [InlineData(null, "api-free.deepl.com")]
    [InlineData("free", "api-free.deepl.com")]
    [InlineData("pro", "api.deepl.com")]
    public async Task Plan_config_selects_the_free_or_pro_endpoint(string? plan, string expectedHost)
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, Ok("x")));
        var completed = await RunAsync(staged, server.Origin, new TranslateRequest("hi", "en", "zh-Hans"), plan is null ? new { } : new { plan });
        Assert.False(completed.Ok);
        Assert.Contains($"//{expectedHost}", completed.Error?.Detail);
        Assert.Equal(0, server.RequestCount);
    }

    [Theory] // J05: DeepL's documented status codes
    [InlineData(403, """{"message":"Authorization failure, check auth_key"}""", ErrorKind.Auth)]
    [InlineData(401, "", ErrorKind.Auth)]
    [InlineData(456, """{"message":"Quota exceeded"}""", ErrorKind.Quota)]
    [InlineData(400, """{"message":"Value for 'target_lang' not supported."}""", ErrorKind.UnsupportedLanguage)]
    [InlineData(400, """{"message":"Bad request. Reason: Parameter 'text' not specified."}""", ErrorKind.BadResponse)]
    [InlineData(413, """{"message":"Request Entity Too Large"}""", ErrorKind.BadResponse)]
    [InlineData(500, "", ErrorKind.Network)]
    [InlineData(503, """{"message":"Service unavailable"}""", ErrorKind.Network)]
    public async Task Vendor_status_codes_map_to_host_error_kinds(int status, string body, ErrorKind expected)
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => body.Length == 0 ? LoopbackHttpResponse.Text(status, "") : LoopbackHttpResponse.Json(status, body));
        var completed = await RunAsync(staged, server.Origin, new TranslateRequest("hi", "en", "zh-Hans"));
        Assert.False(completed.Ok);
        Assert.Equal(expected, ErrorKinds.FromPlugin(completed.Error?.Kind));
    }

    [Fact] // a Free key sent to the Pro endpoint (or vice versa) is an auth failure whose detail points at the plan setting
    public async Task Wrong_endpoint_403_is_auth_and_names_the_plan_setting()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(403, """{"message":"Wrong endpoint. Use https://api.deepl.com"}"""));
        var completed = await RunAsync(staged, server.Origin, new TranslateRequest("hi", "en", "zh-Hans"));
        Assert.False(completed.Ok);
        Assert.Equal(ErrorKind.Auth, ErrorKinds.FromPlugin(completed.Error?.Kind));
        Assert.Contains("plan", completed.Error?.Detail);
    }

    [Theory] // 429 and DeepL's 529 are rate limits; Retry-After reaches the host raw
    [InlineData(429)]
    [InlineData(529)]
    public async Task Rate_limits_map_to_rate_limited_with_Retry_After_passed_through(int status)
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => WithRetryAfter(LoopbackHttpResponse.Json(status, """{"message":"Too many requests"}"""), "5"));
        var completed = await RunAsync(staged, server.Origin, new TranslateRequest("hi", "en", "zh-Hans"));
        Assert.False(completed.Ok);
        Assert.Equal(ErrorKind.RateLimited, ErrorKinds.FromPlugin(completed.Error?.Kind));
        Assert.Equal(TimeSpan.FromSeconds(5), RetryPolicy.ParseRetryAfter(completed.Error?.RetryAfterRaw, DateTimeOffset.UtcNow));
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

    [Fact] // DeepL caps a request body at 128 KiB: a text that cannot fit is refused before any request
    public async Task Text_over_the_request_size_limit_is_refused_before_the_request()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, Ok("ok")));
        var fits = await RunAsync(staged, server.Origin, new TranslateRequest(new string('a', 100 * 1024), "en", "zh-Hans"));
        Assert.True(fits.Ok, fits.Error?.Detail);
        Assert.Equal(1, server.RequestCount);
        var over = await RunAsync(staged, server.Origin, new TranslateRequest(new string('中', 44 * 1024), "en", "zh-Hans")); // 132 KiB of UTF-8
        Assert.False(over.Ok);
        Assert.Equal(ErrorKind.BadResponse, ErrorKinds.FromPlugin(over.Error?.Kind));
        Assert.Contains("too long", over.Error?.Detail);
        Assert.Equal(1, server.RequestCount);
    }
}
