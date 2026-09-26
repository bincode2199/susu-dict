using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Net;
using Susu.Plugins;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F09 independent verification, DICT01: adversarial word-form classification per PLAN 6.1 ("拉丁文字：只含字母、
/// 连字符、撇号，无空白，≤ 32 字符；中文：只含汉字，≤ 4 字；含数字、标点、空白，或两种文字混排，一律不触发").
/// Only the dictionary decision (<see cref="TextForms.UsesDictionary"/>) is asserted; the finer labels are not a contract.
/// </summary>
public class F09ClassifierVerificationTests
{
    [Theory]
    [InlineData("mother-in-law")]
    [InlineData("o'clock")]
    [InlineData("'tis")]
    [InlineData("rock‐and‐roll")]   // U+2010 hyphen
    [InlineData("non‑stop")]             // U+2011 non-breaking hyphen
    [InlineData("A")]
    [InlineData("Straße")]
    [InlineData("Ångström")]
    [InlineData("Việt")]                      // Latin Extended Additional
    [InlineData("HELLO")]
    [InlineData("　你好　")]           // ideographic spaces trim away
    [InlineData("東京")]
    [InlineData("\U0002A6D6")]                // CJK Ext B, one scalar / two UTF-16 units
    [InlineData("abcdefghijklmnopqrstuvwxyzabcde-")] // 31 letters + hyphen = 32
    public void Word_forms_use_the_dictionary(string text) => Assert.True(TextForms.UsesDictionary(text, dictionaryConfigured: true), TextForms.Classify(text).ToString());

    [Theory]
    [InlineData("hello！")]                  // full-width punctuation
    [InlineData("你好。")]
    [InlineData("【词典】")]
    [InlineData("ｈｅｌｌｏ")]                 // full-width Latin letters are not Latin letters
    [InlineData("hello—world")]              // em dash
    [InlineData("well–known")]               // en dash is not a hyphen
    [InlineData("e-mail2")]
    [InlineData("３Ｄ")]
    [InlineData("Ⅻ")]
    [InlineData("x²")]
    [InlineData("😀")]
    [InlineData("👍🏻")]
    [InlineData("hello👋")]
    [InlineData("👨‍👩‍👧")]
    [InlineData("A股")]
    [InlineData("Ｔ恤")]
    [InlineData("卡拉OK")]
    [InlineData("你-好")]
    [InlineData("你'好")]
    [InlineData("hello​world")]         // zero-width space
    [InlineData("hello world")]         // no-break space
    [InlineData("hello\tworld")]
    [InlineData("λόγος")]
    [InlineData("مرحبا")]
    [InlineData("한국어")]
    [InlineData("中文·英文")]
    [InlineData("__init__")]
    [InlineData("C++")]
    [InlineData("C#")]
    [InlineData("http://x.com")]
    [InlineData("‮hello")]              // RTL override
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdef'")] // 32 letters + apostrophe = 33
    [InlineData("好好好好好")]                  // 5 Han
    public void Non_word_forms_never_use_the_dictionary(string text) => Assert.False(TextForms.UsesDictionary(text, dictionaryConfigured: true), TextForms.Classify(text).ToString());

    [Fact]
    public void Very_long_single_tokens_are_not_word_forms()
    {
        Assert.False(TextForms.UsesDictionary(new string('a', 100_000), true));
        Assert.False(TextForms.UsesDictionary(new string('好', 100_000), true));
        Assert.False(TextForms.UsesDictionary(string.Concat(Enumerable.Repeat("\U0002A6D6", 5)), true)); // 5 Ext B = 10 UTF-16 units
        Assert.True(TextForms.UsesDictionary(string.Concat(Enumerable.Repeat("\U0002A6D6", 4)), true));  // counted as scalars, not units
        Assert.False(TextForms.UsesDictionary("hello\uD800", true)); // lone surrogate: no exception, no lookup
        Assert.False(TextForms.UsesDictionary("\uDC00好", true));
    }

    [Fact]
    public void Nothing_uses_the_dictionary_without_a_configured_service_and_the_word_is_trimmed()
    {
        Assert.False(TextForms.UsesDictionary("hello", dictionaryConfigured: false));
        Assert.Equal("你好", TextForms.DictionaryWord("　你好　"));
        Assert.Equal("don't", TextForms.DictionaryWord("  don't\r\n"));
        Assert.Null(TextForms.DictionaryWord("hello！"));
    }

    [Fact] // a decomposed (NFD) Latin word classifies like its NFC form; the looked-up word is NFC
    public void Decomposed_latin_word_is_a_word_form()
    {
        string nfd = "cafe" + (char)0x0301, nfc = "caf" + (char)0x00E9; // e + combining acute vs precomposed é
        Assert.True(TextForms.UsesDictionary(nfd, true));
        Assert.Equal(TextForm.LatinWord, TextForms.Classify(nfd));
        Assert.Equal(nfc, TextForms.DictionaryWord($" {nfd} ")); // the vendor lookup receives NFC
        Assert.Null(TextForms.DictionaryWord("a\ud800b"));
    }
}

/// <summary>
/// F09 independent verification, S06/DICT03: dictionary audio fetch edge cases beyond DictionaryAudioTests, through the
/// production <see cref="NetworkBroker"/> over real loopback sockets.
/// </summary>
public sealed class F09AudioVerificationTests : IDisposable
{
    private const string Service = "youdao/translate";
    private static readonly byte[] Mp3 = [0x49, 0x44, 0x33, 0x04, 0x00, 0x00, 0x01, 0x02];
    private readonly string cacheDir = Path.Combine(Path.GetTempPath(), "susu-f09-verify-" + Guid.NewGuid().ToString("N"));
    private readonly FileLeases leases;

    public F09AudioVerificationTests() => leases = new FileLeases(cacheDir);

    public void Dispose()
    {
        leases.Dispose();
        try { Directory.Delete(cacheDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static NetworkBroker Broker(params LoopbackHttpServer[] servers)
    {
        var options = new NetworkBrokerOptions { Timeout = TimeSpan.FromSeconds(5) };
        foreach (var server in servers) { options.LocalOrigins.Approve(server.Origin); options.LocalOrigins.Approve($"http://localhost:{server.Port}"); }
        return new NetworkBroker(options);
    }

    private DictionaryAudioFetcher Fetcher(NetworkBroker broker, string url, params string[] declared)
        => new(id => Task.FromResult(id == "audio-1" ? new DictionaryAudioLink(Service, url) : null), s => s == Service ? declared : null, () => broker, leases);

    [Fact] // origins are scheme-sensitive: an http link is not inside a declared https origin of the same host:port
    public async Task Http_link_is_not_inside_a_declared_https_origin()
    {
        using var server = new LoopbackHttpServer(_ => new(200, Mp3, ContentType: "audio/mpeg"));
        using var broker = Broker(server);
        var fetcher = Fetcher(broker, server.Origin + "/a.mp3", $"https://127.0.0.1:{server.Port}");
        Assert.Equal(new DictionaryAudioOutcome.Refused(AudioRefusal.OriginNotDeclared), await fetcher.FetchAsync("audio-1", TestContext.Current.CancellationToken));
        Assert.Equal(0, server.RequestCount);
    }

    [Theory] // a redirect to another name of the same socket (localhost vs 127.0.0.1) or via 307/308/301/303 still leaves the declared origin
    [InlineData(302, "localhost")]
    [InlineData(307, "localhost")]
    [InlineData(308, "localhost")]
    [InlineData(301, "localhost")]
    [InlineData(303, "localhost")]
    public async Task Redirect_to_an_alias_of_the_declared_origin_is_refused(int status, string host)
    {
        LoopbackHttpServer? self = null;
        using var server = new LoopbackHttpServer(req => req.Path == "/a.mp3" ? LoopbackHttpResponse.Redirect(status, $"http://{host}:{self!.Port}/b.mp3") : new(200, Mp3, ContentType: "audio/mpeg"));
        self = server;
        using var broker = Broker(server); // the alias is an approved local origin too: only the declared set refuses it
        var fetcher = Fetcher(broker, server.Origin + "/a.mp3", server.Origin);
        Assert.Equal(new DictionaryAudioOutcome.Refused(AudioRefusal.RedirectOutsidePolicy), await fetcher.FetchAsync("audio-1", TestContext.Current.CancellationToken));
        Assert.Equal(1, server.RequestCount);
        Assert.Equal(0, leases.ActiveCount);
    }

    [Fact] // the audio GET carries no credential, cookie or body
    public async Task Audio_request_carries_no_credentials()
    {
        LoopbackHttpRequest? seen = null;
        using var server = new LoopbackHttpServer(req => { seen = req; return new(200, Mp3, ContentType: "audio/mpeg"); });
        using var broker = Broker(server);
        var ready = Assert.IsType<DictionaryAudioOutcome.Ready>(await Fetcher(broker, server.Origin + "/a.mp3?q=good", server.Origin).FetchAsync("audio-1", TestContext.Current.CancellationToken));
        leases.Release(ready.Lease);
        Assert.Equal("GET", seen!.Method);
        Assert.Empty(seen.Body);
        foreach (var name in new[] { "Authorization", "Cookie", "Proxy-Authorization", "X-Api-Key" })
            Assert.DoesNotContain(seen.Headers.Keys, k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    [Theory] // media type parameters and case do not matter for real audio
    [InlineData("audio/mpeg; charset=binary", "mp3")]
    [InlineData("AUDIO/MPEG", "mp3")]
    [InlineData("audio/wav", "wav")]
    public async Task Audio_media_types_are_kept(string contentType, string extension)
    {
        using var server = new LoopbackHttpServer(_ => new(200, Mp3, ContentType: contentType));
        using var broker = Broker(server);
        var ready = Assert.IsType<DictionaryAudioOutcome.Ready>(await Fetcher(broker, server.Origin + "/a", server.Origin).FetchAsync("audio-1", TestContext.Current.CancellationToken));
        Assert.EndsWith("." + extension, leases.PathOf(ready.Lease));
        leases.Release(ready.Lease);
    }

    [Theory] // non-audio responses served as 200 are never kept
    [InlineData("image/svg+xml", "<svg onload=\"alert(1)\"/>")]
    [InlineData("video/mp4", "xxxx")]
    [InlineData("text/plain", "hello")]
    [InlineData("text/html; charset=utf-8", "<script>alert(1)</script>")]
    [InlineData("application/javascript", "alert(1)")]
    public async Task Non_audio_media_types_are_bad_responses(string contentType, string body)
    {
        using var server = new LoopbackHttpServer(_ => new(200, System.Text.Encoding.UTF8.GetBytes(body), ContentType: contentType));
        using var broker = Broker(server);
        Assert.Equal(new DictionaryAudioOutcome.Failure(ErrorKind.BadResponse), await Fetcher(broker, server.Origin + "/a", server.Origin).FetchAsync("audio-1", TestContext.Current.CancellationToken));
        Assert.Equal(0, leases.ActiveCount);
    }

    [Fact(Skip = "F09 finding (low): DictionaryAudioFetcher.ExtensionFor accepts application/octet-stream (and a missing Content-Type, which the broker reports as octet-stream), so an HTML/JSON body served that way is kept as a .bin audio lease. F09.md says 'audio MIME types only'. Possibly deliberate while the real Youdao TTS MIME is unconfirmed; master to decide.")]
    public async Task Octet_stream_is_not_audio()
    {
        using var server = new LoopbackHttpServer(_ => new(200, "<html><script>alert(1)</script></html>"u8.ToArray(), ContentType: "application/octet-stream"));
        using var broker = Broker(server);
        Assert.Equal(new DictionaryAudioOutcome.Failure(ErrorKind.BadResponse), await Fetcher(broker, server.Origin + "/a", server.Origin).FetchAsync("audio-1", TestContext.Current.CancellationToken));
    }

    [Fact] // a cancelled fetch reports Cancelled and leaves no lease
    public async Task Cancelled_fetch_leaves_nothing()
    {
        using var server = new LoopbackHttpServer(_ => new(200, Mp3, ContentType: "audio/mpeg"));
        using var broker = Broker(server);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(new DictionaryAudioOutcome.Failure(ErrorKind.Cancelled), await Fetcher(broker, server.Origin + "/a", server.Origin).FetchAsync("audio-1", cts.Token));
        Assert.Equal(0, leases.ActiveCount);
    }
}
