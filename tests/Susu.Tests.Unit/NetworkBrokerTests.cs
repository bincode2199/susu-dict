using System.Text.Json.Nodes;
using Susu.Net;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F05.1/F05.2: the production network broker against a real loopback socket (<see cref="LoopbackHttpServer"/>).
/// TEST-PLAN B01-B08 (binary/JSON-Base64 transforms, byte limits, error classification) and the parts
/// of S04/S05 (redirect/origin policy) that do not need a real cloud DNS/vendor are exercised here; the
/// signer-specific parts are in SignersTests.cs and the real-sandbox parts in PluginHostIntegrationTests.cs.
/// </summary>
public class NetworkBrokerTests
{
    private static NetworkBroker MakeBroker(LoopbackHttpServer server, bool approveLocal = true)
    {
        var options = new NetworkBrokerOptions { Timeout = TimeSpan.FromSeconds(5) };
        if (approveLocal) options.LocalOrigins.Approve(server.Origin);
        return new NetworkBroker(options);
    }

    private static BrokerHttpRequest Get(Uri uri, bool localApproved = true, ResponseKind responseType = ResponseKind.Json, IReadOnlyList<ResponseFileSpec>? responseFiles = null, string? errorPointer = null)
        => new("GET", uri, [], RequestBody.None, [], [], (_, _) => throw new InvalidOperationException("no secret expected"), null, responseType, responseFiles ?? [], errorPointer, localApproved);

    [Fact]
    public async Task A_plain_json_get_round_trips_through_the_real_socket()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"ok":true}"""));
        using var broker = MakeBroker(server);
        var outcome = await broker.ExecuteAsync(Get(new Uri(server.Origin + "/ping")), TestContext.Current.CancellationToken);
        var success = Assert.IsType<BrokerSuccess>(outcome);
        Assert.Equal(200, success.Response.Status);
        Assert.True(success.Response.JsonBody!["ok"]!.GetValue<bool>());
    }

    /// <summary>B01-shaped: a JSON body with a Base64 image field is inserted host-side from a file's bytes
    /// (never plugin-visible bytes), and the final POST body actually carries the Base64 the server sees.</summary>
    [Fact]
    public async Task JsonBase64_request_insertion_lands_in_the_final_body_the_server_receives()
    {
        LoopbackHttpRequest? seen = null;
        using var server = new LoopbackHttpServer(req => { seen = req; return LoopbackHttpResponse.Json(200, """{"ok":true}"""); });
        using var broker = MakeBroker(server);

        var body = new RequestBody(BodyKind.Json, JsonNode.Parse("""{"ImageBase64":null,"lang":"en"}"""));
        byte[] imageBytes = [1, 2, 3, 4, 5];
        var request = new BrokerHttpRequest("POST", new Uri(server.Origin + "/ocr"), [new("content-type", "application/json")], body,
            [new BodyFileInsertion("/ImageBase64", imageBytes)], [], (_, _) => throw new InvalidOperationException(), null, ResponseKind.Json, [], null, true);

        var outcome = await broker.ExecuteAsync(request, TestContext.Current.CancellationToken);
        Assert.IsType<BrokerSuccess>(outcome);
        var sentJson = JsonNode.Parse(seen!.Body)!;
        Assert.Equal(Convert.ToBase64String(imageBytes), sentJson["ImageBase64"]!.GetValue<string>());
        Assert.Equal("en", sentJson["lang"]!.GetValue<string>());
    }

    /// <summary>B03-shaped: a JSON response with a Base64 audio field is extracted host-side into a file,
    /// the field is nulled in the returned JSON, and the plugin-visible metadata never carries the bytes.</summary>
    [Fact]
    public async Task JsonBase64_response_extraction_yields_a_file_and_nulls_the_field()
    {
        byte[] audioBytes = [9, 8, 7, 6, 5, 4];
        string audioBase64 = Convert.ToBase64String(audioBytes);
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, "{\"Response\":{\"Audio\":\"" + audioBase64 + "\",\"RequestId\":\"r1\"}}"));
        using var broker = MakeBroker(server);

        var request = Get(new Uri(server.Origin + "/tts"), responseType: ResponseKind.Json, responseFiles: [new ResponseFileSpec("audio", "/Response/Audio", "audio/mpeg")]);
        var outcome = await broker.ExecuteAsync(request, TestContext.Current.CancellationToken);
        var success = Assert.IsType<BrokerSuccess>(outcome);
        Assert.Single(success.Response.Files);
        Assert.Equal(audioBytes, success.Response.Files[0].Bytes);
        Assert.Null(success.Response.JsonBody!["Response"]!["Audio"]); // extracted field nulled in place
        Assert.Equal("r1", success.Response.JsonBody["Response"]!["RequestId"]!.GetValue<string>());
    }

    /// <summary>B04-shaped: a raw (non-JSON) audio response becomes a file handle result, not JSON.</summary>
    [Fact]
    public async Task Raw_file_response_is_returned_as_bytes_not_parsed_as_json()
    {
        byte[] mp3 = [0xFF, 0xFB, 1, 2, 3];
        using var server = new LoopbackHttpServer(_ => new LoopbackHttpResponse(200, mp3, ContentType: "audio/mpeg"));
        using var broker = MakeBroker(server);
        var outcome = await broker.ExecuteAsync(Get(new Uri(server.Origin + "/audio.mp3"), responseType: ResponseKind.File), TestContext.Current.CancellationToken);
        var success = Assert.IsType<BrokerSuccess>(outcome);
        Assert.Equal(mp3, success.Response.RawFile);
        Assert.Equal("audio/mpeg", success.Response.RawFileMime);
    }

    /// <summary>B04-shaped: HTTP 401/429/5xx are returned with status+body for the plugin to classify, no
    /// file extraction attempted even if responseFiles were declared.</summary>
    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Non_2xx_status_is_returned_without_attempting_file_extraction(int status)
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(status, """{"error":"nope"}"""));
        using var broker = MakeBroker(server);
        var request = Get(new Uri(server.Origin + "/x"), responseType: ResponseKind.Json, responseFiles: [new ResponseFileSpec("audio", "/audio", "audio/mpeg")]);
        var outcome = await broker.ExecuteAsync(request, TestContext.Current.CancellationToken);
        var success = Assert.IsType<BrokerSuccess>(outcome);
        Assert.Equal(status, success.Response.Status);
        Assert.Empty(success.Response.Files);
    }

    /// <summary>B04-shaped: a 2xx business error (Tencent convention) with errorPointer returns just the
    /// error subtree, not a fabricated file and not the whole raw response.</summary>
    [Fact]
    public async Task Business_error_via_errorPointer_returns_only_the_error_subtree()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"Response":{"Error":{"Code":"AuthFailure","Message":"bad key"},"RequestId":"r2"}}"""));
        using var broker = MakeBroker(server);
        var request = Get(new Uri(server.Origin + "/tts"), responseType: ResponseKind.Json,
            responseFiles: [new ResponseFileSpec("audio", "/Response/Audio", "audio/mpeg")], errorPointer: "/Response/Error");
        var outcome = await broker.ExecuteAsync(request, TestContext.Current.CancellationToken);
        var success = Assert.IsType<BrokerSuccess>(outcome);
        Assert.Empty(success.Response.Files);
        Assert.Equal("AuthFailure", success.Response.JsonBody!["Code"]!.GetValue<string>());
    }

    /// <summary>B05-shaped: missing target field, no errorPointer -> bad_response, not the whole raw body.</summary>
    [Fact]
    public async Task Missing_responseFiles_target_without_errorPointer_is_bad_response()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"Response":{"RequestId":"r3"}}"""));
        using var broker = MakeBroker(server);
        var request = Get(new Uri(server.Origin + "/tts"), responseType: ResponseKind.Json, responseFiles: [new ResponseFileSpec("audio", "/Response/Audio", "audio/mpeg")]);
        var outcome = await broker.ExecuteAsync(request, TestContext.Current.CancellationToken);
        var failure = Assert.IsType<BrokerFailure>(outcome);
        Assert.Equal("bad_response", failure.Kind);
    }

    /// <summary>B05-shaped: corrupt Base64 at the target field -> bad_response.</summary>
    [Fact]
    public async Task Corrupt_base64_in_a_responseFiles_target_is_bad_response()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"Response":{"Audio":"not-valid-base64!!!","RequestId":"r4"}}"""));
        using var broker = MakeBroker(server);
        var request = Get(new Uri(server.Origin + "/tts"), responseType: ResponseKind.Json, responseFiles: [new ResponseFileSpec("audio", "/Response/Audio", "audio/mpeg")]);
        var outcome = await broker.ExecuteAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal("bad_response", Assert.IsType<BrokerFailure>(outcome).Kind);
    }

    /// <summary>B05-shaped: two responseFiles pointers that overlap (one a prefix of the other) are rejected
    /// before any extraction is attempted.</summary>
    [Fact]
    public async Task Overlapping_responseFiles_pointers_are_rejected()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"a":{"b":"x"}}"""));
        using var broker = MakeBroker(server);
        var request = Get(new Uri(server.Origin + "/x"), responseType: ResponseKind.Json, responseFiles: [new ResponseFileSpec("f1", "/a", "x"), new ResponseFileSpec("f2", "/a/b", "x")]);
        var outcome = await broker.ExecuteAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal("bad_response", Assert.IsType<BrokerFailure>(outcome).Kind);
    }

    /// <summary>B08-shaped: a raw file response over the 32 MiB binary cap aborts rather than buffering an
    /// unbounded amount of memory or silently truncating a "successful" file.</summary>
    [Fact]
    public async Task Oversized_file_response_aborts_instead_of_buffering_unbounded_memory()
    {
        byte[] chunk = new byte[1024 * 1024];
        using var server = new LoopbackHttpServer(_ => new LoopbackHttpResponse(200, Enumerable.Repeat(chunk, 33).SelectMany(x => x).ToArray(), ContentType: "audio/mpeg"));
        using var broker = MakeBroker(server);
        var outcome = await broker.ExecuteAsync(Get(new Uri(server.Origin + "/big.mp3"), responseType: ResponseKind.File), TestContext.Current.CancellationToken);
        Assert.Equal("bad_response", Assert.IsType<BrokerFailure>(outcome).Kind);
    }

    /// <summary>B08-shaped: a server that declares a Content-Length far larger than what it actually sends
    /// (then closes) must never hang the caller. The underlying HTTP stack detects the truncation itself
    /// (a real close before the declared length) and the broker surfaces that as a bounded, explicit
    /// network failure rather than silently returning partial/wrong data or waiting forever.</summary>
    [Fact]
    public async Task A_lying_Content_Length_fails_fast_instead_of_hanging_or_trusting_partial_data()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.WithLyingContentLength("""{"ok":true}"""u8.ToArray(), declaredLength: 999_999));
        using var broker = MakeBroker(server);
        var outcome = await broker.ExecuteAsync(Get(new Uri(server.Origin + "/x")), TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("network", Assert.IsType<BrokerFailure>(outcome).Kind);
    }

    /// <summary>S05-shaped: a cloud request (not an approved local origin) whose target resolves to a
    /// loopback/private address is rejected even though the TCP connection would otherwise succeed.</summary>
    [Fact]
    public async Task A_cloud_origin_resolving_to_loopback_is_rejected()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"ok":true}"""));
        using var broker = MakeBroker(server, approveLocal: false); // not approved as a local origin: treated as a cloud request
        var outcome = await broker.ExecuteAsync(Get(new Uri(server.Origin + "/x"), localApproved: false), TestContext.Current.CancellationToken);
        var failure = Assert.IsType<BrokerFailure>(outcome);
        Assert.Equal("network", failure.Kind);
    }

    /// <summary>S05-shaped: two different local services cannot borrow each other's approval - only the
    /// exact approved origin (host+port) is treated as local.</summary>
    [Fact]
    public async Task A_local_origin_not_in_the_approved_set_is_still_treated_as_cloud()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"ok":true}"""));
        var options = new NetworkBrokerOptions { Timeout = TimeSpan.FromSeconds(5) };
        options.LocalOrigins.Approve("http://127.0.0.1:8765"); // a different port than the server's
        using var broker = new NetworkBroker(options);
        var outcome = await broker.ExecuteAsync(Get(new Uri(server.Origin + "/x"), localApproved: false), TestContext.Current.CancellationToken);
        Assert.Equal("network", Assert.IsType<BrokerFailure>(outcome).Kind);
    }

    /// <summary>S04-shaped: an unauthenticated GET follows a same-origin-policy-respecting redirect chain
    /// up to the 5-hop cap, then fails explicitly instead of looping forever.</summary>
    [Fact]
    public async Task Uncredentialed_redirects_beyond_five_hops_are_rejected()
    {
        using var server = new LoopbackHttpServer(req =>
        {
            int hop = int.Parse(req.Path.TrimStart('/').Split('/').Last() is var s && s.Length > 0 ? s : "0");
            return hop >= 10 ? LoopbackHttpResponse.Json(200, "{}") : LoopbackHttpResponse.Redirect(302, $"/hop/{hop + 1}");
        });
        using var broker = MakeBroker(server);
        var outcome = await broker.ExecuteAsync(Get(new Uri(server.Origin + "/hop/0")), TestContext.Current.CancellationToken);
        Assert.Equal("network", Assert.IsType<BrokerFailure>(outcome).Kind);
    }

    /// <summary>S04-shaped: a redirect chain that eventually lands within 5 hops succeeds.</summary>
    [Fact]
    public async Task Uncredentialed_redirect_within_the_hop_limit_succeeds()
    {
        using var server = new LoopbackHttpServer(req => req.Path == "/start" ? LoopbackHttpResponse.Redirect(302, "/done") : LoopbackHttpResponse.Json(200, """{"ok":true}"""));
        using var broker = MakeBroker(server);
        var outcome = await broker.ExecuteAsync(Get(new Uri(server.Origin + "/start")), TestContext.Current.CancellationToken);
        Assert.True(Assert.IsType<BrokerSuccess>(outcome).Response.JsonBody!["ok"]!.GetValue<bool>());
    }

    /// <summary>S04-shaped: a request carrying explicit credentials never auto-follows a redirect - the
    /// caller gets the 3xx status and the validated target back instead.</summary>
    [Fact]
    public async Task Credentialed_requests_never_auto_follow_a_redirect()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Redirect(302, "/elsewhere"));
        using var broker = MakeBroker(server);
        var body = new RequestBody(BodyKind.None);
        var request = new BrokerHttpRequest("GET", new Uri(server.Origin + "/x"), [new("Authorization", "")], body, [],
            [new CredentialSpec(CredentialArea.Header, "Authorization", [CredentialPart.Text("Bearer secret")])], (_, _) => "secret", null, ResponseKind.Json, [], null, true);
        var outcome = await broker.ExecuteAsync(request, TestContext.Current.CancellationToken);
        var success = Assert.IsType<BrokerSuccess>(outcome);
        Assert.Equal(302, success.Response.Status);
        Assert.NotNull(success.Response.RedirectUrl);
        Assert.Equal(1, server.RequestCount); // never sent a second request
    }

    /// <summary>Response headers not on the allow-list (PLAN 4.5.1) never reach the plugin.</summary>
    [Fact]
    public async Task Response_headers_outside_the_allow_list_are_not_forwarded()
    {
        using var server = new LoopbackHttpServer(_ => new LoopbackHttpResponse(200, """{"ok":true}"""u8.ToArray(),
            new Dictionary<string, string> { ["Set-Cookie"] = "session=evil", ["X-Custom-Secret"] = "leak" }));
        using var broker = MakeBroker(server);
        var outcome = await broker.ExecuteAsync(Get(new Uri(server.Origin + "/x")), TestContext.Current.CancellationToken);
        var success = Assert.IsType<BrokerSuccess>(outcome);
        Assert.DoesNotContain("Set-Cookie", success.Response.Headers.Keys);
        Assert.DoesNotContain("X-Custom-Secret", success.Response.Headers.Keys);
    }
}
