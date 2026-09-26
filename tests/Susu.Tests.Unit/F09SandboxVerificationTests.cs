using System.Collections.Concurrent;
using System.Text.Json;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Net;
using Susu.Plugins;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F09 independent verification through the real AppContainer sandbox + QuickJS + Broker (needs susu.exe published;
/// each test returns early otherwise, like the other real-sandbox tests).
/// <list type="bullet">
/// <item>Regression for d6868fb (PLAN 4.5.4 item 3, S06): a plugin <c>$http</c> call without credentials follows a
/// redirect only inside the call's granted origins, even when the target is another approved loopback origin; a
/// credentialed (signed) call never auto-follows 3xx.</item>
/// <item>DICT02 through the shipped Youdao package: collapse/cancel/reopen request counting, nothing in parallel.</item>
/// <item>DICT03/S06 through the shipped Youdao package: hostile responses are capped data, non-https audio links never
/// get an id, and an https link outside the provider's grant is refused by the audio fetcher.</item>
/// </list>
/// </summary>
public class F09SandboxVerificationTests
{
    private static readonly ConfigSnapshot Config = new(1, 1, 1, 2, TimeSpan.FromSeconds(30));
    private static int jobCounter;

    // ---- staging (echo fixture), same approach as NetworkBrokerIntegrationTests ----

    private static string? StageEcho()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? output = null;
        for (int i = 0; i < 6 && dir is not null && output is null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish");
            if (File.Exists(Path.Combine(candidate, "susu.exe"))) output = candidate;
        }
        if (output is null) return null;
        string staged = TestTemp.NewDir("susu-f09-verify-it");
        foreach (string file in Directory.EnumerateFiles(output))
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(staged, Path.GetFileName(file)));
        string source = Path.Combine(AppContext.BaseDirectory, "fixtures", "plugins", "echo");
        string target = Path.Combine(staged, "plugins", "echo");
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        return staged;
    }

    /// <summary>Ok with the result, or not ok with the failure payload text (a refused call ends as a Failed envelope
    /// or as a Completed one with ok=false, depending on where the refusal surfaces).</summary>
    private static async Task<(bool Ok, JsonElement? Result, string Detail)> EchoGetAsync(HostSession session, string url, params string[] origins)
    {
        var (_, _, task) = session.Invoke("echo", "httpGet", JsonSerializer.Serialize(new { url }), jobId: $"job-f09v-{Interlocked.Increment(ref jobCounter)}", origins: origins);
        var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        if (envelope.Type != IpcMessageType.Completed) return (false, null, $"{envelope.Type}: {envelope.Payload}");
        var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
        return (completed.Ok, completed.Result, completed.Error?.Detail ?? "");
    }

    [Fact] // d6868fb regression: redirect from the granted origin to an approved but NOT granted loopback origin is refused
    public async Task Uncredentialed_plugin_redirect_outside_the_granted_origins_is_refused()
    {
        string? staged = StageEcho();
        if (staged is null) return;
        using var outside = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"leaked":true}"""));
        using var granted = new LoopbackHttpServer(req => req.Path switch
        {
            "/out" => LoopbackHttpResponse.Redirect(302, outside.Origin + "/steal"),
            "/out307" => LoopbackHttpResponse.Redirect(307, outside.Origin + "/steal"),
            "/in" => LoopbackHttpResponse.Redirect(302, "/done"),
            _ => LoopbackHttpResponse.Json(200, """{"ok":true}"""),
        });
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false));
        try
        {
            // Both loopback origins are approved local origins, so only the per-call grant can stop the hop (before
            // d6868fb the broker checked address/protocol per hop but not the call's origins).
            session.Broker.ApproveLocalOrigin(granted.Origin);
            session.Broker.ApproveLocalOrigin(outside.Origin);
            Assert.True(session.Load("echo", "plugins/echo").Ok);

            foreach (string path in new[] { "/out", "/out307" })
            {
                var refused = await EchoGetAsync(session, granted.Origin + path, granted.Origin);
                Assert.False(refused.Ok, $"{path}: redirect to a non-granted origin was followed");
                TestContext.Current.TestOutputHelper?.WriteLine($"{path} refused: {refused.Detail}");
            }
            Assert.Equal(0, outside.RequestCount);

            var within = await EchoGetAsync(session, granted.Origin + "/in", granted.Origin);
            Assert.True(within.Ok, within.Detail);
            Assert.Equal(200, within.Result!.Value.GetProperty("status").GetInt32());
            Assert.True(within.Result!.Value.GetProperty("body").GetProperty("ok").GetBoolean());

            // Control: the same cross-origin redirect is followed when the call is granted both origins.
            var both = await EchoGetAsync(session, granted.Origin + "/out", granted.Origin, outside.Origin);
            Assert.True(both.Ok, both.Detail);
            Assert.Equal(1, outside.RequestCount);
        }
        finally { session.Shutdown(2000); }
    }

    [Theory] // PLAN 4.5.4 item 4: a plugin $http JSON result over one IPC frame (1 MiB) is split by transferId and reassembled (<= 4 MiB)
    [InlineData(900_000)]
    [InlineData(2_000_000)]
    public async Task Plugin_http_json_response_over_one_frame_completes(int size)
    {
        string? staged = StageEcho();
        if (staged is null) return;
        string body = JsonSerializer.Serialize(new { items = Enumerable.Range(0, size / 20).Select(i => $"item-{i:D10}").ToArray() });
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, body));
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load("echo", "plugins/echo").Ok);
            var (_, _, task) = session.Invoke("echo", "httpGet", JsonSerializer.Serialize(new { url = server.Origin + "/x" }), jobId: $"job-f09v-{Interlocked.Increment(ref jobCounter)}", origins: [server.Origin]);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            Assert.Equal(IpcMessageType.Completed, envelope.Type);
            var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
            Assert.True(completed.Ok, completed.Error?.Detail);
            Assert.Equal(size / 20, completed.Result!.Value.GetProperty("body").GetProperty("items").GetArrayLength());
        }
        finally { session.Shutdown(2000); }
    }

    [Fact] // PLAN 4.5.4 item 3: a credentialed/signed request (Youdao translate) does not auto-follow 3xx, even within its origin
    public async Task Credentialed_plugin_request_does_not_follow_redirects()
    {
        string? staged = YoudaoPluginTests.StageHost();
        if (staged is null) return;
        using var outside = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"errorCode":"0","translation":["leaked"],"l":"en2zh-CHS"}"""));
        var paths = new ConcurrentQueue<string>();
        using var granted = new LoopbackHttpServer(req =>
        {
            paths.Enqueue(req.Path.Split('?')[0]);
            return req.Path.StartsWith("/api") ? LoopbackHttpResponse.Redirect(302, "/elsewhere")
                : req.Path.StartsWith("/v2/dict") ? LoopbackHttpResponse.Redirect(307, outside.Origin + "/v2/dict")
                : LoopbackHttpResponse.Json(200, """{"errorCode":"0","translation":["followed"],"l":"en2zh-CHS"}""");
        });
        using var session = HostSession.Start(YoudaoPluginTests.Options(staged, granted.Origin));
        try
        {
            session.Broker.ApproveLocalOrigin(granted.Origin);
            session.Broker.ApproveLocalOrigin(outside.Origin);
            Assert.True(session.Load(YoudaoPluginTests.PackageId, YoudaoPluginTests.PackageDir).Ok);
            string config = JsonSerializer.Serialize(new { baseUrl = granted.Origin });
            async Task<CompletedPayload> Run(string capability, string json)
            {
                var (_, _, task) = session.Invoke(YoudaoPluginTests.PackageId, capability, json, jobId: $"job-f09v-{Interlocked.Increment(ref jobCounter)}",
                    origins: [granted.Origin], secrets: ["appKey", "appSecret"], configJson: config, instanceId: YoudaoPluginTests.InstanceId, signer: YoudaoPluginTests.Signer);
                var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                return envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
            }

            var translate = await Run("translate", JsonSerializer.Serialize(new TranslateRequest("hello", "en", "zh-Hans"), ContractsJson.Default.TranslateRequest));
            Assert.False(translate.Ok, "a signed request followed a redirect");
            var lookup = await Run("dictionary", JsonSerializer.Serialize(new { word = "hello" }));
            Assert.False(lookup.Ok, "a signed lookup followed a cross-origin redirect");
            Assert.Equal(["/api", "/v2/dict"], paths); // "/elsewhere" never requested
            Assert.Equal(0, outside.RequestCount);
        }
        finally { session.Shutdown(2000); }
    }

    // ---- DICT02 / DICT03 through a translation session over the shipped Youdao package ----

    private sealed class Rig : IDisposable
    {
        public required Supervisor<HostSession> Supervisor { get; init; }
        public required TranslationSession Session { get; init; }
        public required PluginProvider Provider { get; init; }
        public void Dispose() => Supervisor.Dispose();

        public async Task<CardSnapshot> Card() => (await Session.SnapshotAsync()).Cards.Single();

        public async Task<CardSnapshot> WaitForAsync(Func<CardSnapshot, bool> condition, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var card = await Card();
                if (condition(card)) return card;
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            var last = await Card();
            throw new TimeoutException($"{what}: card stayed {last.State} \"{last.Text}\" ({last.Error})");
        }
    }

    private static Rig Build(string staged, LoopbackHttpServer server, int expanded = 1, bool dictionary = true)
    {
        var options = YoudaoPluginTests.Options(staged, server.Origin);
        var supervisor = new Supervisor<HostSession>(() =>
        {
            var session = HostSession.Start(options);
            session.Broker.ApproveLocalOrigin(server.Origin);
            var loaded = session.Load(YoudaoPluginTests.PackageId, YoudaoPluginTests.PackageDir);
            if (!loaded.Ok) { session.Shutdown(2000); throw new InvalidOperationException($"youdao failed to load: {loaded.Error}"); }
            return session;
        }, SystemClock.Instance, TimeSpan.FromMinutes(10));
        var package = TranslationPackages.Find(TranslationPackages.Youdao)!;
        var instance = new InstanceSettings(YoudaoPluginTests.InstanceId, YoudaoPluginTests.PackageId, 1, new Dictionary<string, string> { ["baseUrl"] = server.Origin },
            new Dictionary<string, string> { ["appKey"] = "account-youdao", ["appSecret"] = "account-youdao" });
        var plan = new TranslationServicePlan(package, instance, new ServiceSettings(YoudaoPluginTests.InstanceId, Capability.Translate, true), dictionary);
        var provider = PluginTranslationProviders.Create(plan, supervisor);
        var session = new TranslationSession([provider], new TranslationSessionOptions(Config, expanded),
            new InvocationScheduler(new SchedulerLimits()), SystemClock.Instance, new FixedJitter(0), new RecordingUsage());
        return new Rig { Supervisor = supervisor, Session = session, Provider = provider };
    }

    private const string CardId = "youdao/translate";
    // Hostile payload: 1 result x 2000 items per array (~340 KB, under one IPC frame; see the skipped frame bug test).
    private const int HostileN = 2000, HostileResults = 1;
    private static string TranslateOk(string text) => JsonSerializer.Serialize(new { errorCode = "0", query = "q", translation = new[] { text }, l = "en2zh-CHS" });

    /// <summary>Server that records every request path and the maximum number of requests in flight at once.</summary>
    private sealed class Recorder
    {
        public readonly ConcurrentQueue<string> Paths = new();
        private int inFlight;
        public int MaxInFlight;
        public int InFlight => Volatile.Read(ref inFlight);

        public LoopbackHttpResponse Handle(LoopbackHttpRequest req, Func<string, int, LoopbackHttpResponse> respond)
        {
            string path = req.Path.Split('?')[0];
            int now = Interlocked.Increment(ref inFlight);
            int seen;
            while ((seen = MaxInFlight) < now && Interlocked.CompareExchange(ref MaxInFlight, now, seen) != seen) { }
            Paths.Enqueue(path);
            try { return respond(path, Paths.Count(p => p == path)); }
            finally { Interlocked.Decrement(ref inFlight); }
        }
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact] // DICT02: collapsing during the real lookup cancels it with no translate; reopening sends exactly one new lookup
    public async Task Collapse_during_real_lookup_cancels_and_reopen_looks_up_once()
    {
        string? staged = YoudaoPluginTests.StageHost();
        if (staged is null) return;
        using var gate = new ManualResetEventSlim(false);
        var rec = new Recorder();
        using var server = new LoopbackHttpServer(req => rec.Handle(req, (path, n) =>
        {
            if (path == "/v2/dict" && n == 1) gate.Wait(TimeSpan.FromSeconds(30));
            return path == "/v2/dict" ? LoopbackHttpResponse.Json(200, JsonSerializer.Serialize(YoudaoPluginTests.FullEntry())) : LoopbackHttpResponse.Json(200, TranslateOk("好"));
        }));
        using var rig = Build(staged, server);
        await rig.Session.SubmitAsync("good", "en", "zh-Hans");
        await UntilAsync(() => rec.Paths.Count == 1, "first lookup reached the server");
        await rig.Session.ToggleAsync(CardId);
        await rig.Session.IdleAsync();
        var collapsed = await rig.Card();
        Assert.Equal((CardState.Cancelled, true, false), (collapsed.State, collapsed.Collapsed, collapsed.Dictionary));

        gate.Set();
        await UntilAsync(() => rec.InFlight == 0, "cancelled lookup drained");
        await rig.Session.ToggleAsync(CardId);
        var card = await rig.WaitForAsync(c => c.State is CardState.Ready or CardState.Failed, "reopened");
        await rig.Session.IdleAsync();
        Assert.True(card.Dictionary, $"{card.State} {card.Error}");
        Assert.Equal(["/v2/dict", "/v2/dict"], rec.Paths);
        Assert.Equal(1, rec.MaxInFlight);

        // Reopening a finished card reuses the result, like an ordinary card.
        await rig.Session.ToggleAsync(CardId);
        await rig.Session.ToggleAsync(CardId);
        await rig.Session.IdleAsync();
        Assert.True((await rig.Card()).Dictionary);
        Assert.Equal(2, rec.Paths.Count);
    }

    [Fact] // DICT02: collapse during the fallback translate; reopen restarts lookup -> translate, strictly one at a time
    public async Task Collapse_during_real_fallback_translate_restarts_sequentially()
    {
        string? staged = YoudaoPluginTests.StageHost();
        if (staged is null) return;
        using var gate = new ManualResetEventSlim(false);
        var rec = new Recorder();
        using var server = new LoopbackHttpServer(req => rec.Handle(req, (path, n) =>
        {
            if (path == "/v2/dict") return LoopbackHttpResponse.Json(200, """{"errorCode":"120"}""");
            if (n == 1) gate.Wait(TimeSpan.FromSeconds(30));
            return LoopbackHttpResponse.Json(200, TranslateOk("未知词"));
        }));
        using var rig = Build(staged, server);
        await rig.Session.SubmitAsync("xyzzyq", "en", "zh-Hans");
        await UntilAsync(() => rec.Paths.Count == 2, "fallback translate reached the server");
        await rig.Session.ToggleAsync(CardId);
        await rig.Session.IdleAsync();
        Assert.Equal(CardState.Cancelled, (await rig.Card()).State);

        gate.Set();
        await UntilAsync(() => rec.InFlight == 0, "cancelled translate drained");
        await rig.Session.ToggleAsync(CardId);
        var card = await rig.WaitForAsync(c => c.State is CardState.Ready or CardState.Failed, "reopened");
        await rig.Session.IdleAsync();
        Assert.Equal((CardState.Ready, "未知词", false), (card.State, card.Text, card.Dictionary));
        Assert.Equal(["/v2/dict", "/api", "/v2/dict", "/api"], rec.Paths);
        Assert.Equal(1, rec.MaxInFlight);
    }

    [Fact] // DICT02 parity: an ordinary (dictionary disabled) card also sends one request per expansion and reuses it on reopen
    public async Task Ordinary_card_reopen_matches_the_dictionary_card()
    {
        string? staged = YoudaoPluginTests.StageHost();
        if (staged is null) return;
        var rec = new Recorder();
        using var server = new LoopbackHttpServer(req => rec.Handle(req, (_, _) => LoopbackHttpResponse.Json(200, TranslateOk("好"))));
        using var rig = Build(staged, server, expanded: 0, dictionary: false);
        await rig.Session.SubmitAsync("good", "en", "zh-Hans");
        await rig.Session.IdleAsync();
        Assert.Empty(rec.Paths);
        await rig.Session.ToggleAsync(CardId);
        await rig.WaitForAsync(c => c.State == CardState.Ready, "expanded");
        await rig.Session.ToggleAsync(CardId);
        await rig.Session.ToggleAsync(CardId);
        await rig.Session.IdleAsync();
        Assert.Equal(("好", false), ((await rig.Card()).Text, (await rig.Card()).Dictionary));
        Assert.Equal(["/api"], rec.Paths);
    }

    [Fact] // DICT03: hostile Youdao output through the real package and session: links other than https never get an id, arrays are capped, text stays literal
    public async Task Hostile_youdao_entry_is_capped_inert_data_with_no_unsafe_audio_ids()
    {
        string? staged = YoudaoPluginTests.StageHost();
        if (staged is null) return;
        const string script = "<script>alert(1)</script>";
        var hostile = new
        {
            errorCode = "0",
            result = Enumerable.Range(0, HostileResults).Select(r => (object)new
            {
                ec = new
                {
                    basic = new
                    {
                        usPhonetic = "<svg onload=alert(1)>",
                        usSpeech = "javascript:alert(1)",
                        ukPhonetic = "uk",
                        ukSpeech = "data:audio/mpeg;base64,SUQz",
                        explains = Enumerable.Range(0, HostileN).Select(i => $"n. {script}{i}").ToArray(),
                        wordFormats = Enumerable.Range(0, HostileN).Select(i => new { name = "<b>f</b>", value = $"javascript:v{i}" }).ToArray(),
                    },
                    sentenceSample = Enumerable.Range(0, HostileN).Select(i => new { sentence = $"<a href=\"javascript:x\">{i}</a>", translation = "<img src=x onerror=alert(1)>" }).ToArray(),
                },
            }).ToArray(),
        };
        string body = JsonSerializer.Serialize(hostile);
        var rec = new Recorder();
        using var server = new LoopbackHttpServer(req => rec.Handle(req, (path, _) => path == "/v2/dict" ? LoopbackHttpResponse.Json(200, body) : LoopbackHttpResponse.Json(200, TranslateOk("x"))));
        using var rig = Build(staged, server);
        await rig.Session.SubmitAsync("evil", "en", "zh-Hans");
        var card = await rig.WaitForAsync(c => c.State is CardState.Ready or CardState.Failed, "hostile entry");
        await rig.Session.IdleAsync();
        Assert.True(card.Dictionary, $"{card.State} {card.Error} (response {body.Length} bytes)");
        var entry = card.Entry!;
        Assert.All(entry.Phonetics, p => Assert.Null(p.AudioId));
        Assert.Contains(entry.Phonetics, p => p.Ipa == "<svg onload=alert(1)>");
        Assert.InRange(entry.Parts.Length, 1, 24);
        Assert.All(entry.Parts, p => Assert.InRange(p.Means.Length, 1, 16));
        Assert.InRange(entry.Forms.Length, 1, 16);
        Assert.InRange(entry.Examples.Length, 1, 8);
        Assert.Equal($"{script}0", entry.Parts[0].Means[0]);
        Assert.Equal(new DictionaryFormView("<b>f</b>", "javascript:v0"), entry.Forms[0]);
        Assert.Equal(new DictionaryExampleView("<a href=\"javascript:x\">0</a>", "<img src=x onerror=alert(1)>"), entry.Examples[0]);
        Assert.All(entry.Parts.SelectMany(p => p.Means).Concat(entry.Forms.Select(f => f.Value)), s => Assert.True(s.Length <= 400));
        Assert.Equal(["/v2/dict"], rec.Paths);
    }

    // DICT03: a dictionary response larger than the 4 MiB JSON limit fails the card promptly as an error, never a fallback
    [Fact]
    public async Task Oversized_youdao_response_fails_promptly_without_fallback()
    {
        string? staged = YoudaoPluginTests.StageHost();
        if (staged is null) return;
        string body = JsonSerializer.Serialize(new { errorCode = "0", result = new[] { new { ec = new { basic = new { explains = Enumerable.Range(0, 200_000).Select(i => $"n. meaning number {i} padded").ToArray() } } } } });
        Assert.True(body.Length > ProtocolLimits.MaxReassembledJsonBytes);
        var rec = new Recorder();
        using var server = new LoopbackHttpServer(req => rec.Handle(req, (path, _) => path == "/v2/dict" ? LoopbackHttpResponse.Json(200, body) : LoopbackHttpResponse.Json(200, TranslateOk("x"))));
        using var rig = Build(staged, server);
        var started = DateTime.UtcNow;
        await rig.Session.SubmitAsync("evil", "en", "zh-Hans");
        var card = await rig.WaitForAsync(c => c.State is CardState.Ready or CardState.Failed, "oversized entry");
        var elapsed = DateTime.UtcNow - started;
        await rig.Session.IdleAsync();
        Assert.Equal(CardState.Failed, card.State);
        Assert.NotEqual(ErrorKind.Timeout, card.Error);
        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"took {elapsed}");
        Assert.Equal(["/v2/dict"], rec.Paths);
    }

    [Fact] // S06: an https audio link on a real entry gets an opaque id, but the fetcher refuses it outside the provider's granted origins (no request)
    public async Task Real_entry_audio_outside_the_granted_origin_is_refused_by_the_fetcher()
    {
        string? staged = YoudaoPluginTests.StageHost();
        if (staged is null) return;
        var rec = new Recorder();
        using var server = new LoopbackHttpServer(req => rec.Handle(req, (path, _) => LoopbackHttpResponse.Json(200, JsonSerializer.Serialize(YoudaoPluginTests.FullEntry("https://evil.example/a.mp3")))));
        using var rig = Build(staged, server);
        await rig.Session.SubmitAsync("good", "en", "zh-Hans");
        var card = await rig.WaitForAsync(c => c.State is CardState.Ready or CardState.Failed, "entry");
        await rig.Session.IdleAsync();
        string audioId = card.Entry!.Phonetics[0].AudioId!;
        Assert.DoesNotContain("evil", audioId);
        Assert.Contains(NetworkBroker.Origin(new Uri(server.Origin)), DictionaryAudioFetcher.AllowedOrigins(rig.Provider.HostOrigins));

        string cache = Path.Combine(Path.GetTempPath(), "susu-f09-verify-cache-" + Guid.NewGuid().ToString("N"));
        using (var leases = new FileLeases(cache))
        {
            var options = new NetworkBrokerOptions { Timeout = TimeSpan.FromSeconds(5) };
            options.LocalOrigins.Approve(server.Origin);
            using var broker = new NetworkBroker(options);
            var fetcher = new DictionaryAudioFetcher(rig.Session.ResolveAudioLinkAsync,
                service => service == CardId ? rig.Provider.HostOrigins : null, () => broker, leases);
            Assert.Equal(new DictionaryAudioOutcome.Refused(AudioRefusal.OriginNotDeclared), await fetcher.FetchAsync(audioId, TestContext.Current.CancellationToken));

            // A new source makes the id stale.
            await rig.Session.SubmitAsync("fine", "en", "zh-Hans");
            await rig.Session.IdleAsync();
            Assert.Equal(new DictionaryAudioOutcome.Refused(AudioRefusal.UnknownOrStale), await fetcher.FetchAsync(audioId, TestContext.Current.CancellationToken));
            Assert.Equal(0, leases.ActiveCount);
        }
        try { Directory.Delete(cache, recursive: true); } catch (DirectoryNotFoundException) { }
        Assert.All(rec.Paths, p => Assert.Equal("/v2/dict", p));
    }
}
