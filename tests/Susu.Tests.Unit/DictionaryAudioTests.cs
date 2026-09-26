using Susu.Abstractions;
using Susu.Contracts;
using Susu.Net;
using Susu.Plugins;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F09.3 (TEST-PLAN DICT03, S06): dictionary audio is fetched by the host only, for an id the session still shows,
/// only within the source provider's declared origins, and every redirect hop stays inside them. Real loopback
/// sockets through the production <see cref="NetworkBroker"/>; the audio ends up in an F05 cache lease.
/// </summary>
public sealed class DictionaryAudioTests : IDisposable
{
    private const string Service = "youdao/dictionary";
    private static readonly byte[] Mp3 = [0x49, 0x44, 0x33, 0x04, 0x00, 0x00, 0x01, 0x02];
    private readonly string cacheDir = Path.Combine(Path.GetTempPath(), "susu-f09-audio-" + Guid.NewGuid().ToString("N"));
    private readonly FileLeases leases;

    public DictionaryAudioTests() => leases = new FileLeases(cacheDir);

    public void Dispose()
    {
        leases.Dispose();
        try { Directory.Delete(cacheDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static NetworkBroker Broker(params LoopbackHttpServer[] servers)
    {
        var options = new NetworkBrokerOptions { Timeout = TimeSpan.FromSeconds(5) };
        foreach (var server in servers) options.LocalOrigins.Approve(server.Origin);
        return new NetworkBroker(options);
    }

    private DictionaryAudioFetcher Fetcher(NetworkBroker broker, IReadOnlyDictionary<string, string> links, params string[] declared)
        => new(id => Task.FromResult(links.TryGetValue(id, out var url) ? new DictionaryAudioLink(Service, url) : null),
            service => service == Service ? declared : null, () => broker, leases);

    private static LoopbackHttpResponse Audio() => new(200, Mp3, ContentType: "audio/mpeg");

    [Fact]
    public async Task A_current_id_within_the_declared_origin_is_downloaded_into_a_lease()
    {
        using var server = new LoopbackHttpServer(req => req.Path == "/tts?q=good" ? Audio() : LoopbackHttpResponse.Json(404, "{}"));
        using var broker = Broker(server);
        var fetcher = Fetcher(broker, new Dictionary<string, string> { ["audio-1"] = server.Origin + "/tts?q=good" }, server.Origin);

        var ready = Assert.IsType<DictionaryAudioOutcome.Ready>(await fetcher.FetchAsync("audio-1", TestContext.Current.CancellationToken));
        Assert.Equal("audio/mpeg", ready.Mime);
        Assert.Equal(Mp3.Length, ready.Length);
        string path = leases.PathOf(ready.Lease);
        Assert.StartsWith(Path.GetFullPath(cacheDir), path);
        Assert.EndsWith(".mp3", path);
        Assert.Equal(Mp3, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        leases.Release(ready.Lease);
        Assert.False(File.Exists(path));
        Assert.Equal(0, leases.ActiveCount);
    }

    [Fact]
    public async Task Unknown_or_stale_ids_are_refused_without_any_request()
    {
        using var server = new LoopbackHttpServer(_ => Audio());
        using var broker = Broker(server);
        var fetcher = Fetcher(broker, new Dictionary<string, string>(), server.Origin);
        foreach (var id in new[] { "audio-unknown", server.Origin + "/tts", "" })
            Assert.Equal(new DictionaryAudioOutcome.Refused(AudioRefusal.UnknownOrStale), await fetcher.FetchAsync(id, TestContext.Current.CancellationToken));
        Assert.Equal(0, server.RequestCount);
        Assert.Equal(0, leases.ActiveCount);
    }

    [Fact]
    public async Task A_link_outside_the_declared_origins_is_refused_before_any_request()
    {
        using var declared = new LoopbackHttpServer(_ => Audio());
        using var other = new LoopbackHttpServer(_ => Audio());
        using var broker = Broker(declared, other); // both reachable as far as the broker goes; only the provider's grant differs
        var fetcher = Fetcher(broker, new Dictionary<string, string>
        {
            ["other"] = other.Origin + "/a.mp3",
            ["user-info"] = declared.Origin.Replace("http://", "http://user:pw@") + "/a.mp3",
            ["scheme"] = "file:///C:/Windows/win.ini",
            ["relative"] = "/a.mp3",
        }, declared.Origin);

        Assert.Equal(new DictionaryAudioOutcome.Refused(AudioRefusal.OriginNotDeclared), await fetcher.FetchAsync("other", TestContext.Current.CancellationToken));
        foreach (var id in new[] { "user-info", "scheme", "relative" })
            Assert.Equal(new DictionaryAudioOutcome.Refused(AudioRefusal.InvalidLink), await fetcher.FetchAsync(id, TestContext.Current.CancellationToken));
        Assert.Equal(0, other.RequestCount);
        Assert.Equal(0, declared.RequestCount);
    }

    [Fact]
    public async Task A_service_without_declared_origins_gets_nothing()
    {
        using var server = new LoopbackHttpServer(_ => Audio());
        using var broker = Broker(server);
        var fetcher = new DictionaryAudioFetcher(_ => Task.FromResult<DictionaryAudioLink?>(new DictionaryAudioLink("other/service", server.Origin + "/a.mp3")),
            _ => null, () => broker, leases);
        Assert.Equal(new DictionaryAudioOutcome.Refused(AudioRefusal.OriginNotDeclared), await fetcher.FetchAsync("audio-1", TestContext.Current.CancellationToken));
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task A_redirect_leaving_the_declared_origins_is_refused_and_the_target_never_contacted()
    {
        using var outside = new LoopbackHttpServer(_ => Audio());
        using var declared = new LoopbackHttpServer(_ => LoopbackHttpResponse.Redirect(302, outside.Origin + "/evil.mp3"));
        using var broker = Broker(declared, outside);
        var fetcher = Fetcher(broker, new Dictionary<string, string> { ["audio-1"] = declared.Origin + "/a.mp3" }, declared.Origin);

        Assert.Equal(new DictionaryAudioOutcome.Refused(AudioRefusal.RedirectOutsidePolicy), await fetcher.FetchAsync("audio-1", TestContext.Current.CancellationToken));
        Assert.Equal(1, declared.RequestCount);
        Assert.Equal(0, outside.RequestCount);
        Assert.Equal(0, leases.ActiveCount);
    }

    [Fact]
    public async Task A_redirect_within_the_declared_origins_is_followed()
    {
        using var second = new LoopbackHttpServer(_ => Audio());
        using var first = new LoopbackHttpServer(req => req.Path == "/a.mp3" ? LoopbackHttpResponse.Redirect(302, "/b.mp3") : LoopbackHttpResponse.Redirect(302, second.Origin + "/c.mp3"));
        using var broker = Broker(first, second);
        var fetcher = Fetcher(broker, new Dictionary<string, string> { ["audio-1"] = first.Origin + "/a.mp3" }, first.Origin, second.Origin);

        var ready = Assert.IsType<DictionaryAudioOutcome.Ready>(await fetcher.FetchAsync("audio-1", TestContext.Current.CancellationToken));
        Assert.Equal(2, first.RequestCount);
        Assert.Equal(1, second.RequestCount);
        leases.Release(ready.Lease);
    }

    [Fact]
    public async Task Non_audio_or_failed_responses_are_never_kept()
    {
        var responses = new Dictionary<string, LoopbackHttpResponse>
        {
            ["/html"] = new(200, "<script>alert(1)</script>"u8.ToArray(), ContentType: "text/html"),
            ["/json"] = LoopbackHttpResponse.Json(200, """{"errorCode":"108"}"""),
            ["/empty"] = new(200, [], ContentType: "audio/mpeg"),
            ["/404"] = new(404, Mp3, ContentType: "audio/mpeg"),
        };
        using var server = new LoopbackHttpServer(req => responses[req.Path]);
        using var broker = Broker(server);
        var fetcher = Fetcher(broker, responses.Keys.ToDictionary(k => k, k => server.Origin + k), server.Origin);
        foreach (var id in responses.Keys)
            Assert.Equal(new DictionaryAudioOutcome.Failure(ErrorKind.BadResponse), await fetcher.FetchAsync(id, TestContext.Current.CancellationToken));
        Assert.Equal(0, leases.ActiveCount);
    }

    [Fact]
    public void Declared_origins_normalize_to_the_broker_origin_form()
    {
        var set = DictionaryAudioFetcher.AllowedOrigins(["https://openapi.youdao.com", "https://OPENAPI.youdao.com:443/", "not a url", "ftp://x.example"]);
        Assert.Equal(["https://openapi.youdao.com:443"], set);
    }

    [Fact]
    public async Task The_broker_allowed_origin_set_applies_to_the_first_hop_as_well()
    {
        using var server = new LoopbackHttpServer(_ => Audio());
        using var broker = Broker(server);
        var request = new BrokerHttpRequest("GET", new Uri(server.Origin + "/a.mp3"), [], RequestBody.None, [], [], (_, _) => throw new InvalidOperationException(),
            null, ResponseKind.File, [], null, LocalOriginApproved: true, AllowedOrigins: new HashSet<string> { "https://elsewhere.example:443" });
        var failure = Assert.IsType<BrokerFailure>(await broker.ExecuteAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal("forbidden", failure.Kind);
        Assert.Equal(0, server.RequestCount);
    }
}
