using System.Collections.Concurrent;
using System.Text.Json;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Plugins;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F09.2 real sandbox (DICT01/DICT02): a translation session over the shipped Youdao package, built through
/// <see cref="PluginTranslationProviders.Create"/> with the dictionary service enabled, through the real AppContainer
/// sandbox + QuickJS + Broker digest signing, against a LoopbackHttpServer standing in for openapi.youdao.com.
/// Needs susu.exe already published; skips itself otherwise.
/// </summary>
public class YoudaoDictionarySessionTests
{
    private static readonly ConfigSnapshot Config = new(1, 1, 1, 2, TimeSpan.FromSeconds(30));

    private sealed class Rig : IDisposable
    {
        public required Supervisor<HostSession> Supervisor { get; init; }
        public required TranslationSession Session { get; init; }
        public void Dispose() => Supervisor.Dispose();

        public async Task<CardSnapshot> WaitForAsync(Func<CardSnapshot, bool> condition, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var card = (await Session.SnapshotAsync()).Cards.Single();
                if (condition(card)) return card;
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            var last = (await Session.SnapshotAsync()).Cards.Single();
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
        return new Rig { Supervisor = supervisor, Session = session };
    }

    private static string Path(LoopbackHttpRequest req) => req.Path.Split('?')[0];

    private static string TranslateOk(string text) => JsonSerializer.Serialize(new { errorCode = "0", query = "q", translation = new[] { text }, l = "en2zh-CHS" });

    [Fact] // dictionary -> legal empty entry (120) -> one translate, strictly after the lookup finished
    public async Task Empty_entry_falls_back_to_one_translate_after_the_lookup()
    {
        string? staged = YoudaoPluginTests.StageHost();
        if (staged is null) return;
        var log = new ConcurrentQueue<string>();
        using var server = new LoopbackHttpServer(req =>
        {
            string path = Path(req);
            log.Enqueue("start " + path);
            if (path == "/v2/dict") { Thread.Sleep(200); log.Enqueue("end " + path); return LoopbackHttpResponse.Json(200, """{"errorCode":"120"}"""); }
            log.Enqueue("end " + path);
            return LoopbackHttpResponse.Json(200, TranslateOk("未知词"));
        });
        using var rig = Build(staged, server);
        await rig.Session.SubmitAsync("xyzzyq", "en", "zh-Hans");
        var card = await rig.WaitForAsync(c => c.State is CardState.Ready or CardState.Failed, "fallback");
        await rig.Session.IdleAsync();
        Assert.Equal((CardState.Ready, "未知词", false), (card.State, card.Text, card.Dictionary));
        Assert.Null(card.Entry);
        Assert.Equal(["start /v2/dict", "end /v2/dict", "start /api", "end /api"], log);
    }

    [Fact] // an entry renders as structured data with an opaque audio id; no translate request is made
    public async Task Entry_shows_structured_data_and_sends_no_translate()
    {
        string? staged = YoudaoPluginTests.StageHost();
        if (staged is null) return;
        var paths = new ConcurrentQueue<string>();
        using var server = new LoopbackHttpServer(req =>
        {
            paths.Enqueue(Path(req));
            return Path(req) == "/v2/dict" ? LoopbackHttpResponse.Json(200, JsonSerializer.Serialize(YoudaoPluginTests.FullEntry())) : LoopbackHttpResponse.Json(200, TranslateOk("好"));
        });
        using var rig = Build(staged, server);
        await rig.Session.SubmitAsync("good", "en", "zh-Hans");
        var card = await rig.WaitForAsync(c => c.State is CardState.Ready or CardState.Failed, "entry");
        await rig.Session.IdleAsync();
        Assert.True(card.Dictionary, $"{card.State} {card.Error}");
        var entry = card.Entry!;
        Assert.Equal("good", entry.Word);
        Assert.Equal(["adj.", "n.", "adv."], entry.Parts.Select(p => p.Pos));
        Assert.Equal([("比较级", "better"), ("最高级", "best")], entry.Forms.Select(f => (f.Name, f.Value)));
        Assert.Equal([("Good morning!", "早上好！")], entry.Examples.Select(e => (e.Src, e.Dst)));
        string audioId = entry.Phonetics[0].AudioId!;
        Assert.DoesNotContain("youdao", audioId);
        Assert.Null(entry.Phonetics[1].AudioId);
        Assert.Equal("https://openapi.youdao.com/ttsapi?q=good&langType=en-USA", await rig.Session.ResolveAudioAsync(audioId));
        Assert.Equal(["/v2/dict"], paths);
    }

    [Fact] // a vendor error on the lookup fails the card; no translate hides it
    public async Task Dictionary_vendor_error_fails_without_translate()
    {
        string? staged = YoudaoPluginTests.StageHost();
        if (staged is null) return;
        var paths = new ConcurrentQueue<string>();
        using var server = new LoopbackHttpServer(req => { paths.Enqueue(Path(req)); return LoopbackHttpResponse.Json(200, """{"errorCode":"202"}"""); });
        using var rig = Build(staged, server);
        await rig.Session.SubmitAsync("good", "en", "zh-Hans");
        var card = await rig.WaitForAsync(c => c.State is CardState.Ready or CardState.Failed, "error");
        await rig.Session.IdleAsync();
        Assert.Equal((CardState.Failed, ErrorKind.Auth), (card.State, card.Error));
        Assert.Equal(["/v2/dict"], paths);
    }

    [Fact] // collapsed card: no request at all; expanding sends exactly one lookup; reopening a finished card sends nothing
    public async Task Collapsed_card_sends_nothing_until_expanded()
    {
        string? staged = YoudaoPluginTests.StageHost();
        if (staged is null) return;
        var paths = new ConcurrentQueue<string>();
        using var server = new LoopbackHttpServer(req => { paths.Enqueue(Path(req)); return LoopbackHttpResponse.Json(200, JsonSerializer.Serialize(YoudaoPluginTests.FullEntry())); });
        using var rig = Build(staged, server, expanded: 0);
        await rig.Session.SubmitAsync("good", "en", "zh-Hans");
        await rig.Session.IdleAsync();
        Assert.Equal(CardState.CollapsedIdle, (await rig.Session.SnapshotAsync()).Cards.Single().State);
        Assert.Empty(paths);
        await rig.Session.ToggleAsync("youdao/translate");
        await rig.WaitForAsync(c => c.State == CardState.Ready, "expanded");
        await rig.Session.ToggleAsync("youdao/translate");
        await rig.Session.ToggleAsync("youdao/translate");
        await rig.Session.IdleAsync();
        Assert.True((await rig.Session.SnapshotAsync()).Cards.Single().Dictionary);
        Assert.Equal(["/v2/dict"], paths);
    }

    [Fact] // with the dictionary service disabled, a word form is an ordinary translation
    public async Task Disabled_dictionary_translates_directly()
    {
        string? staged = YoudaoPluginTests.StageHost();
        if (staged is null) return;
        var paths = new ConcurrentQueue<string>();
        using var server = new LoopbackHttpServer(req => { paths.Enqueue(Path(req)); return LoopbackHttpResponse.Json(200, TranslateOk("好")); });
        using var rig = Build(staged, server, dictionary: false);
        await rig.Session.SubmitAsync("good", "en", "zh-Hans");
        var card = await rig.WaitForAsync(c => c.State is CardState.Ready or CardState.Failed, "translate");
        Assert.Equal(("好", false), (card.Text, card.Dictionary));
        Assert.Equal(["/api"], paths);
    }
}
