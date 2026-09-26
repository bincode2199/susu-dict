using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Susu.Contracts;
using Susu.Domain;
using Susu.Plugins;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F09.1 P-T07: the real, shipped Youdao package (src/Susu.Host/plugins/youdao) through the real AppContainer
/// sandbox + QuickJS engine + IPC + Broker/$http + the host `digest` signing primitive, against a local
/// LoopbackHttpServer standing in for openapi.youdao.com. Needs susu.exe already published
/// (`dotnet publish src/Susu.Host -c Release -r win-x64`); skips itself otherwise.
///
/// The v3 signature is recomputed here independently (own truncation + SHA-256, not DigestSigner). There is
/// no real Youdao account available - the real-vendor call is "not executed - no account".
/// </summary>
public class YoudaoPluginTests
{
    private const string PackageId = "app.susu.youdao";
    private const string InstanceId = "youdao";
    private const string PackageDir = "plugins/youdao";
    private const string Signer = "unsigned:app.susu.youdao";
    private const string AppKey = "0123456789abcdefTESTAPPKEY";
    private const string AppSecret = "testAppSecretZYXWVUT9876543210";

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
        string sourcePlugin = Path.Combine(output, "plugins", "youdao");
        if (!File.Exists(Path.Combine(sourcePlugin, "main.js"))) return null;
        string staged = TestTemp.NewDir("susu-youdao-it");
        foreach (string file in Directory.EnumerateFiles(output))
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(staged, Path.GetFileName(file)));
        string targetPlugin = Path.Combine(staged, "plugins", "youdao");
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

    /// <summary>The grants the shipped package declares (TranslationPackages): appKey for its query field and
    /// the digest target, appSecret for the digest target only. <paramref name="omitSecretDigestGrant"/> drops
    /// the appSecret grant to prove the digest primitive enforces it.</summary>
    private static HostSession.Options Options(string staged, string origin, bool omitSecretDigestGrant = false)
    {
        var secrets = new FakeSecretStore();
        secrets.Set("account-youdao", "appKey", AppKey);
        secrets.Set("account-youdao", "appSecret", AppSecret);
        string normalized = Origin.Normalize(origin);
        var package = TranslationPackages.Find(InstanceId)!;
        var grants = package.Credentials
            .Where(c => !(omitSecretDigestGrant && c.Secret == "appSecret"))
            .Select(c => new CredentialGrant(PackageId, Signer, c.Secret, normalized, c.Use)).ToList();
        var account = new AccountSettings("account-youdao", "Youdao", ["appKey", "appSecret"], grants);
        var instance = new InstanceSettings(InstanceId, PackageId, 1, new Dictionary<string, string>(),
            new Dictionary<string, string> { ["appKey"] = "account-youdao", ["appSecret"] = "account-youdao" });
        var accounts = new AccountAuthorization(() => ([account], [instance]));
        return new(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false,
            MakeBroker: () => new Broker(secretStore: secrets, accounts: accounts));
    }

    private static int jobCounter;

    private static async Task<CompletedPayload> RunAsync(string staged, string origin, string capability, string requestJson, bool omitSecretDigestGrant = false)
    {
        using var session = HostSession.Start(Options(staged, origin, omitSecretDigestGrant));
        try
        {
            session.Broker.ApproveLocalOrigin(origin);
            Assert.True(session.Load(PackageId, PackageDir).Ok);
            string configJson = JsonSerializer.Serialize(new { baseUrl = origin });
            var (_, _, task) = session.Invoke(PackageId, capability, requestJson, jobId: $"job-yd-{Interlocked.Increment(ref jobCounter)}", origins: [origin],
                secrets: ["appKey", "appSecret"], configJson: configJson, instanceId: InstanceId, signer: Signer);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            return envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
        }
        finally { session.Shutdown(2000); }
    }

    private static Task<CompletedPayload> TranslateAsync(string staged, string origin, TranslateRequest request, bool omitSecretDigestGrant = false)
        => RunAsync(staged, origin, "translate", JsonSerializer.Serialize(request, ContractsJson.Default.TranslateRequest), omitSecretDigestGrant);

    private static Task<CompletedPayload> LookupAsync(string staged, string origin, string word)
        => RunAsync(staged, origin, "dictionary", JsonSerializer.Serialize(new { word }));

    private static DictionaryResult Entry(CompletedPayload completed)
    {
        Assert.True(completed.Ok, completed.Error?.Detail);
        return completed.Result!.Value.Deserialize(ContractsJson.Default.DictionaryResult)!;
    }

    // ---- independent request decoding and v3 signature ----

    private static string FormDecode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static Dictionary<string, string> ParseForm(string encoded)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in encoded.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = pair.Split('=', 2);
            result[FormDecode(kv[0])] = kv.Length > 1 ? FormDecode(kv[1]) : "";
        }
        return result;
    }

    private static (string Path, Dictionary<string, string> Query) SplitPath(string pathAndQuery)
    {
        int q = pathAndQuery.IndexOf('?');
        return q < 0 ? (pathAndQuery, []) : (pathAndQuery[..q], ParseForm(pathAndQuery[(q + 1)..]));
    }

    /// <summary>Youdao's documented rule (Java demo: String.length/substring, UTF-16 units).</summary>
    private static string ExpectedInput(string q) => q.Length <= 20 ? q : q[..10] + q.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + q[^10..];

    private static string ExpectedSign(string q, string salt, string curtime)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(AppKey + ExpectedInput(q) + salt + curtime + AppSecret)));

    /// <summary>Asserts the signed control fields on a captured request and returns (query, body form).</summary>
    private static (Dictionary<string, string> Query, Dictionary<string, string> Form) AssertSigned(LoopbackHttpRequest captured, string expectedPath, string q)
    {
        Assert.Equal("POST", captured.Method);
        var (path, query) = SplitPath(captured.Path);
        Assert.Equal(expectedPath, path);
        string contentType = captured.Headers.FirstOrDefault(h => string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)).Value ?? "";
        Assert.StartsWith("application/x-www-form-urlencoded", contentType);
        var form = ParseForm(Encoding.UTF8.GetString(captured.Body));
        Assert.Equal(q, form["q"]);
        Assert.Equal(AppKey, query["appKey"]);
        Assert.Equal("v3", query["signType"]);
        Assert.True(Guid.TryParse(query["salt"], out _), $"salt '{query["salt"]}' is not a UUID");
        long curtime = long.Parse(query["curtime"], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(curtime, DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
        Assert.Equal(ExpectedSign(q, query["salt"], query["curtime"]), query["sign"]);
        Assert.DoesNotContain(AppSecret, captured.Path);
        Assert.DoesNotContain(AppSecret, Encoding.UTF8.GetString(captured.Body));
        return (query, form);
    }

    private static string TranslateOk(string text, string l = "en2zh-CHS")
        => JsonSerializer.Serialize(new { errorCode = "0", query = "q", translation = new[] { text }, l });

    private static string Code(string code) => JsonSerializer.Serialize(new { errorCode = code, l = "en2zh-CHS" });

    // ---- registration ----

    [Fact] // P-T07 is a wired translation package; limits and credential targets are what the plugin writes
    public void Youdao_is_wired_with_limits_and_digest_credential_targets()
    {
        var package = TranslationPackages.Find(TranslationPackages.Youdao)!;
        Assert.Equal(PackageId, package.PackageId);
        Assert.Equal(PackageDir, package.Directory);
        Assert.Equal(new TranslationLimits(InputUnit.UnicodeScalars, 5000, BatchMode.Single, 1, 5000), package.Limits);
        Assert.Null(package.Limits.Validate());
        Assert.Equal(["appKey", "appSecret"], package.SecretNames);
        var grants = package.RequiredGrants(new Dictionary<string, string>());
        Assert.Equal(
            [("appKey", "query:appKey"), ("appKey", "query:sign"), ("appSecret", "query:sign")],
            grants.Select(g => (g.Secret, g.Use)).ToArray());
        Assert.All(grants, g => Assert.Equal("https://openapi.youdao.com:443", g.Origin));
        // BuiltInCatalog already lists youdao with both capabilities and the same secret names.
        var catalog = BuiltInCatalog.Packages.Single(p => p.InstanceId == TranslationPackages.Youdao);
        Assert.Equal([Capability.Translate, Capability.Dictionary], catalog.Capabilities);
        Assert.Equal(package.SecretNames, catalog.Secrets);
    }

    // ---- translate ----

    [Theory] // v3 digest over the truncated input: <= 20 characters verbatim, longer first 10 + length + last 10
    [InlineData("hello")]
    [InlineData("exactly twenty chars")]
    [InlineData("twenty-one characters")]
    [InlineData("The quick brown fox jumps over the lazy dog.")]
    [InlineData("今天天气很好，我们一起去公园散步吧，好不好呀？")]
    [InlineData("a+b=c & d%20e?f#g")]
    public async Task Translate_signs_v3_digest_over_truncated_input(string text)
    {
        string? staged = StageHost();
        if (staged is null) return;
        LoopbackHttpRequest? captured = null;
        using var server = new LoopbackHttpServer(req => { captured = req; return LoopbackHttpResponse.Json(200, TranslateOk("译文")); });
        var completed = await TranslateAsync(staged, server.Origin, new TranslateRequest(text, "en", "zh-Hans"));
        Assert.True(completed.Ok, completed.Error?.Detail);
        Assert.Equal("译文", completed.Result!.Value.Deserialize(ContractsJson.Default.TranslateResult)!.Text);
        var (query, _) = AssertSigned(captured!, "/api", text);
        Assert.Equal("en", query["from"]);
        Assert.Equal("zh-CHS", query["to"]);
        Assert.Equal("true", query["strict"]);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact] // auto source sends "auto" and maps Youdao's l ("zh-CHS2en") back to the canonical code
    public async Task Auto_source_reports_detected_language()
    {
        string? staged = StageHost();
        if (staged is null) return;
        LoopbackHttpRequest? captured = null;
        using var server = new LoopbackHttpServer(req => { captured = req; return LoopbackHttpResponse.Json(200, TranslateOk("hello", "zh-CHS2en")); });
        var completed = await TranslateAsync(staged, server.Origin, new TranslateRequest("你好", null, "en"));
        var result = completed.Result!.Value.Deserialize(ContractsJson.Default.TranslateResult)!;
        Assert.Equal("hello", result.Text);
        Assert.Equal("zh-Hans", result.DetectedFrom);
        var (_, query) = SplitPath(captured!.Path);
        Assert.Equal("auto", query["from"]);
        Assert.Equal("en", query["to"]);
    }

    [Theory] // J05: Youdao reports failures as HTTP 200 + errorCode
    [InlineData("108", ErrorKind.Auth)]
    [InlineData("110", ErrorKind.Auth)]
    [InlineData("111", ErrorKind.Auth)]
    [InlineData("202", ErrorKind.Auth)]
    [InlineData("203", ErrorKind.Auth)]
    [InlineData("206", ErrorKind.Auth)]
    [InlineData("401", ErrorKind.Quota)]
    [InlineData("411", ErrorKind.RateLimited)]
    [InlineData("412", ErrorKind.RateLimited)]
    [InlineData("102", ErrorKind.UnsupportedLanguage)]
    [InlineData("302", ErrorKind.Network)]
    [InlineData("303", ErrorKind.Network)]
    [InlineData("101", ErrorKind.BadResponse)]
    [InlineData("103", ErrorKind.BadResponse)]
    [InlineData("113", ErrorKind.BadResponse)]
    [InlineData("207", ErrorKind.BadResponse)]
    [InlineData("99999", ErrorKind.BadResponse)]
    public async Task Vendor_error_codes_map_to_host_error_kinds(string code, ErrorKind expected)
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, Code(code)));
        var translated = await TranslateAsync(staged, server.Origin, new TranslateRequest("hi", "en", "zh-Hans"));
        Assert.False(translated.Ok);
        Assert.Equal(expected, ErrorKinds.FromPlugin(translated.Error?.Kind));
        Assert.Contains(code, translated.Error?.Detail);
        var looked = await LookupAsync(staged, server.Origin, "hi"); // the dictionary endpoint shares the table
        Assert.False(looked.Ok);
        Assert.Equal(expected, ErrorKinds.FromPlugin(looked.Error?.Kind));
    }

    [Theory]
    [InlineData(503, ErrorKind.Network)]
    [InlineData(403, ErrorKind.Auth)]
    [InlineData(429, ErrorKind.RateLimited)]
    [InlineData(404, ErrorKind.BadResponse)]
    public async Task Http_status_failures_map_to_host_error_kinds(int status, ErrorKind expected)
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Text(status, "nope"));
        var completed = await TranslateAsync(staged, server.Origin, new TranslateRequest("hi", "en", "zh-Hans"));
        Assert.False(completed.Ok);
        Assert.Equal(expected, ErrorKinds.FromPlugin(completed.Error?.Kind));
    }

    [Theory] // malformed success bodies are bad_response, never an empty translation
    [InlineData("{\"errorCode\":\"0\"}")]
    [InlineData("{\"errorCode\":\"0\",\"translation\":[]}")]
    [InlineData("{\"errorCode\":\"0\",\"translation\":[1]}")]
    [InlineData("{\"translation\":[\"x\"]}")]
    [InlineData("[]")]
    public async Task Malformed_translate_responses_are_bad_response(string body)
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, body));
        var completed = await TranslateAsync(staged, server.Origin, new TranslateRequest("hi", "en", "zh-Hans"));
        Assert.False(completed.Ok);
        Assert.Equal(ErrorKind.BadResponse, ErrorKinds.FromPlugin(completed.Error?.Kind));
    }

    [Fact] // 5000 characters per query: 5000 goes out, 5001 is refused before any request; unknown language likewise
    public async Task Length_limit_and_unsupported_language_are_refused_before_the_request()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, TranslateOk("ok")));
        var atLimit = await TranslateAsync(staged, server.Origin, new TranslateRequest(new string('a', 5000), "en", "zh-Hans"));
        Assert.True(atLimit.Ok, atLimit.Error?.Detail);
        Assert.Equal(1, server.RequestCount);
        var over = await TranslateAsync(staged, server.Origin, new TranslateRequest(new string('a', 5001), "en", "zh-Hans"));
        Assert.False(over.Ok);
        Assert.Equal(ErrorKind.BadResponse, ErrorKinds.FromPlugin(over.Error?.Kind));
        Assert.Contains("too long", over.Error?.Detail);
        var language = await TranslateAsync(staged, server.Origin, new TranslateRequest("hi", "en", "xx"));
        Assert.Equal(ErrorKind.UnsupportedLanguage, ErrorKinds.FromPlugin(language.Error?.Kind));
        Assert.Equal(1, server.RequestCount);
    }

    [Fact] // S02: the digest primitive resolves appSecret only under its own grant (query:sign); no grant, no request
    public async Task Digest_secret_needs_its_grant()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, TranslateOk("x")));
        var completed = await TranslateAsync(staged, server.Origin, new TranslateRequest("hi", "en", "zh-Hans"), omitSecretDigestGrant: true);
        Assert.False(completed.Ok);
        Assert.Equal(ErrorKind.Auth, ErrorKinds.FromPlugin(completed.Error?.Kind));
        Assert.Equal(0, server.RequestCount);
    }

    // ---- dictionary ----

    private static object FullEntry(string usAudio = "https://openapi.youdao.com/ttsapi?q=good&langType=en-USA") => new
    {
        errorCode = "0",
        result = new object[]
        {
            new
            {
                ec = new
                {
                    basic = new
                    {
                        usPhonetic = "ɡʊd",
                        ukPhonetic = "ɡʊd",
                        usSpeech = usAudio,
                        ukSpeech = "javascript:alert(1)",
                        explains = new[] { "adj. 好的；优秀的；有益的", "n. 好处；善行", "adv. 好" },
                        wordFormats = new[] { new { name = "比较级", value = "better" }, new { name = "最高级", value = "best" } },
                    },
                    sentenceSample = new[] { new { sentence = "Good morning!", sentenceBold = "<b>Good</b> morning!", translation = "早上好！" } },
                },
            },
        },
    };

    [Fact] // DICT01 entry: phonetics (+https audio as data), parts of speech, word forms, examples; signed /v2/dict ec request
    public async Task English_word_maps_the_dictionary_entry()
    {
        string? staged = StageHost();
        if (staged is null) return;
        LoopbackHttpRequest? captured = null;
        using var server = new LoopbackHttpServer(req => { captured = req; return LoopbackHttpResponse.Json(200, JsonSerializer.Serialize(FullEntry())); });
        var entry = Entry(await LookupAsync(staged, server.Origin, "  good "));
        var (query, _) = AssertSigned(captured!, "/v2/dict", "good");
        Assert.Equal("en", query["langType"]);
        Assert.Equal("ec", query["dicts"]);

        Assert.Equal("good", entry.Word);
        Assert.False(DictionaryEntries.IsEmpty(entry));
        Assert.Equal(2, entry.Phonetics.Length);
        Assert.Equal(new Phonetic("us", "ɡʊd", "https://openapi.youdao.com/ttsapi?q=good&langType=en-USA"), entry.Phonetics[0]);
        Assert.Equal(new Phonetic("uk", "ɡʊd"), entry.Phonetics[1]); // javascript: audio link dropped, phonetic kept
        Assert.Equal(["adj.", "n.", "adv."], entry.Parts.Select(p => p.Pos).ToArray());
        Assert.Equal(["好的", "优秀的", "有益的"], entry.Parts[0].Means);
        Assert.Equal([new WordForm("比较级", "better"), new WordForm("最高级", "best")], entry.Forms!);
        Assert.Equal([new Example("Good morning!", "早上好！")], entry.Examples!);
    }

    [Fact] // legacy /api names (us-phonetic, us-speech, wfs.wf) are read too
    public async Task Legacy_basic_field_names_are_accepted()
    {
        string? staged = StageHost();
        if (staged is null) return;
        string body = """
        {"errorCode":"0","basic":{"us-phonetic":"ˈwɜːrld","uk-phonetic":"wɜːld","us-speech":"https://openapi.youdao.com/ttsapi?q=world",
          "explains":["n. 世界；领域"],"wfs":[{"wf":{"name":"复数","value":"worlds"}}]}}
        """;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, body));
        var entry = Entry(await LookupAsync(staged, server.Origin, "world"));
        Assert.Equal([new Phonetic("us", "ˈwɜːrld", "https://openapi.youdao.com/ttsapi?q=world"), new Phonetic("uk", "wɜːld")], entry.Phonetics);
        Assert.Equal([new WordForm("复数", "worlds")], entry.Forms!);
        Assert.Equal("n.", entry.Parts.Single().Pos);
        Assert.Equal(["世界", "领域"], entry.Parts.Single().Means);
    }

    [Fact] // a Han word goes to the ce (汉英) dictionary with langType zh-CHS; explains without a POS keep pos ""
    public async Task Chinese_word_uses_the_ce_dictionary()
    {
        string? staged = StageHost();
        if (staged is null) return;
        LoopbackHttpRequest? captured = null;
        string body = """{"errorCode":"0","result":[{"ce":{"basic":{"explains":["good; fine","well"]}}}]}""";
        using var server = new LoopbackHttpServer(req => { captured = req; return LoopbackHttpResponse.Json(200, body); });
        var entry = Entry(await LookupAsync(staged, server.Origin, "好"));
        var (query, _) = AssertSigned(captured!, "/v2/dict", "好");
        Assert.Equal("zh-CHS", query["langType"]);
        Assert.Equal("ce", query["dicts"]);
        Assert.Empty(entry.Phonetics);
        Assert.Equal(["", ""], entry.Parts.Select(p => p.Pos).ToArray());
        Assert.Equal(["good", "fine"], entry.Parts[0].Means);
        Assert.Equal(["well"], entry.Parts[1].Means);
    }

    [Theory] // DICT01: a legal empty entry is a successful, empty result - distinguishable from an error
    [InlineData("""{"errorCode":"120"}""")]
    [InlineData("""{"errorCode":"0","result":[]}""")]
    [InlineData("""{"errorCode":"0","result":[{"ec":{"basic":{"explains":[]}}}]}""")]
    [InlineData("""{"errorCode":0}""")]
    public async Task No_entry_is_a_legal_empty_result(string body)
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, body));
        var entry = Entry(await LookupAsync(staged, server.Origin, "xyzzyq"));
        Assert.Equal("xyzzyq", entry.Word);
        Assert.True(DictionaryEntries.IsEmpty(entry));
    }

    [Theory] // ...whereas failures stay failures (never an empty entry that would silently fall back)
    [InlineData("""{"errorCode":"202"}""", ErrorKind.Auth)]
    [InlineData("""{"errorCode":"390001"}""", ErrorKind.BadResponse)]
    [InlineData("""{"result":[]}""", ErrorKind.BadResponse)]
    [InlineData("""[]""", ErrorKind.BadResponse)]
    public async Task Dictionary_failures_are_errors_not_empty_entries(string body, ErrorKind expected)
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, body));
        var completed = await LookupAsync(staged, server.Origin, "good");
        Assert.False(completed.Ok);
        Assert.Equal(expected, ErrorKinds.FromPlugin(completed.Error?.Kind));
    }

    [Fact] // an empty or oversized word is refused without a request
    public async Task Bad_words_are_refused_without_a_request()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"errorCode":"120"}"""));
        var empty = await LookupAsync(staged, server.Origin, "   ");
        Assert.Equal(ErrorKind.BadResponse, ErrorKinds.FromPlugin(empty.Error?.Kind));
        var tooLong = await LookupAsync(staged, server.Origin, new string('a', 65));
        Assert.Equal(ErrorKind.BadResponse, ErrorKinds.FromPlugin(tooLong.Error?.Kind));
        Assert.Equal(0, server.RequestCount);
    }

    [Fact] // S06 groundwork: vendor HTML/script text stays literal data; only https audio links survive; sizes are capped
    public async Task Hostile_fields_stay_data_and_are_capped()
    {
        string? staged = StageHost();
        if (staged is null) return;
        string script = "<script>alert('x')</script>";
        var hostile = new
        {
            errorCode = "0",
            result = new object[]
            {
                new
                {
                    ec = new
                    {
                        basic = new
                        {
                            usPhonetic = "<img src=x onerror=alert(1)>",
                            usSpeech = "http://evil.example/a.mp3",
                            ukPhonetic = "uk",
                            ukSpeech = "https://evil.example/ok.mp3\" onload=\"x",
                            explains = Enumerable.Range(0, 100).Select(i => $"n. {script}{i}").Prepend("vt. " + new string('长', 1000)).ToArray(),
                            wordFormats = new object[] { new { name = "<b>复数</b>", value = "{{secret.appSecret}}" }, new { name = 5, value = "x" } },
                        },
                        sentenceSample = new object[] { new { sentence = "<b>bold</b>", translation = "<i>斜</i>" }, "not an object" },
                    },
                },
            },
        };
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, JsonSerializer.Serialize(hostile)));
        var entry = Entry(await LookupAsync(staged, server.Origin, "evil"));
        Assert.Equal(new Phonetic("us", "<img src=x onerror=alert(1)>"), entry.Phonetics[0]); // http audio dropped
        Assert.Equal(new Phonetic("uk", "uk"), entry.Phonetics[1]);                            // URL with spaces/quotes dropped
        Assert.Equal(24, entry.Parts.Length);
        Assert.Equal(400, entry.Parts[0].Means.Single().Length);
        Assert.Equal($"{script}0", entry.Parts[1].Means.Single());
        Assert.Equal([new WordForm("<b>复数</b>", "{{secret.appSecret}}")], entry.Forms!); // literal, never a secret reference
        Assert.Equal([new Example("<b>bold</b>", "<i>斜</i>")], entry.Examples!);
    }
}
