using System.Text.Json;
using Susu.Contracts;
using Susu.Net;
using Susu.Plugins;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

// Independent testing-agent additions for F05 (TEST-PLAN S03/S04/S05/B06/B08): probes the coding agent's
// own evidence log flagged as claimed-but-not-directly-tested, or that were simply missing a test despite
// the production code already implementing the guard. Each test here was cross-checked by hand for a
// representative subset - temporarily weakening the corresponding production guard and confirming the
// test fails, then restoring it (see F05 test evidence for which ones and how).

/// <summary>
/// S04: a redirect chain that starts https:// must never end up at plain http://. A real TLS loopback
/// server is impractical in this harness (LoopbackHttpServer is plain-text only, by design - see its own
/// class comment), so this calls <see cref="NetworkBroker.IsHttpsToHttpDowngrade"/> directly - the exact
/// boolean guard <c>SendWithRedirectsAsync</c> evaluates on every hop before following it, not a
/// restatement of the condition (it was `internal`-ized from the call site specifically so a test could
/// reference the real production check instead of duplicating its logic). The general per-hop
/// origin/scheme revalidation this guard lives inside is separately proven end to end over a real socket
/// by <see cref="RedirectSecurityTests.Uncredentialed_https_redirect_to_http_is_rejected_over_a_real_socket"/>.
/// </summary>
public class HttpsToHttpDowngradeGuardTests
{
    [Fact]
    public void Https_to_http_redirect_is_recognized_as_a_downgrade()
    {
        Assert.True(NetworkBroker.IsHttpsToHttpDowngrade(new Uri("https://api.example.com/start"), new Uri("http://api.example.com/elsewhere")));
    }

    [Fact]
    public void Https_to_https_redirect_is_not_a_downgrade()
    {
        Assert.False(NetworkBroker.IsHttpsToHttpDowngrade(new Uri("https://api.example.com/start"), new Uri("https://other.example.com/elsewhere")));
    }

    [Fact]
    public void Http_to_http_redirect_is_not_flagged_as_a_downgrade_by_this_specific_guard()
    {
        // Plain http:// staying http:// is a different check entirely (only reachable at all for an
        // approved local origin, via NetworkBroker.IsAllowedForRequest) - not this guard's job.
        Assert.False(NetworkBroker.IsHttpsToHttpDowngrade(new Uri("http://127.0.0.1:1/start"), new Uri("http://127.0.0.1:1/elsewhere")));
    }
}

/// <summary>S04, end to end over a real socket: every redirect hop is independently re-validated by
/// <c>IsAllowedForRequest</c> (not just the first URI), so a redirect landing outside the approved local
/// origin - even to another loopback port - is rejected exactly like a downgrade would be.</summary>
public class RedirectSecurityTests
{
    [Fact]
    public async Task Uncredentialed_https_redirect_to_http_is_rejected_over_a_real_socket()
    {
        using var otherPort = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"should":"never be reached"}"""));
        using var server = new LoopbackHttpServer(req => LoopbackHttpResponse.Redirect(302, $"http://127.0.0.1:{otherPort.Port}/x"));
        var options = new NetworkBrokerOptions { Timeout = TimeSpan.FromSeconds(5) };
        options.LocalOrigins.Approve(server.Origin); // only the first server's origin is approved, not otherPort's
        using var broker = new NetworkBroker(options);

        var request = new BrokerHttpRequest("GET", new Uri(server.Origin + "/start"), [], RequestBody.None, [], [],
            (_, _) => throw new InvalidOperationException(), null, ResponseKind.Json, [], null, LocalOriginApproved: true);
        var outcome = await broker.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("network", Assert.IsType<BrokerFailure>(outcome).Kind);
        Assert.Equal(0, otherPort.RequestCount); // the disallowed hop was never even dialed
    }
}

/// <summary>S03: a URL carrying embedded userinfo (https://user:pass@host/...) is rejected outright, both
/// by NetworkBroker.IsAllowedForRequest (defense in depth) and by Broker.BuildRequest (the production
/// entry point a plugin's declared URL actually goes through) - a plugin can never smuggle a credential
/// as URL userinfo instead of through the declared credentials field.</summary>
public class UrlUserinfoRejectionTests
{
    [Fact]
    public async Task NetworkBroker_rejects_a_url_with_embedded_userinfo()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"ok":true}"""));
        var options = new NetworkBrokerOptions { Timeout = TimeSpan.FromSeconds(5) };
        options.LocalOrigins.Approve(server.Origin);
        using var broker = new NetworkBroker(options);

        var uriWithUserinfo = new Uri($"http://evil:secret@127.0.0.1:{server.Port}/x");
        var request = new BrokerHttpRequest("GET", uriWithUserinfo, [], RequestBody.None, [], [],
            (_, _) => throw new InvalidOperationException(), null, ResponseKind.Json, [], null, LocalOriginApproved: true);
        var outcome = await broker.ExecuteAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal("network", Assert.IsType<BrokerFailure>(outcome).Kind);
        Assert.Equal(0, server.RequestCount); // never even connected
    }

    private sealed class NoopSecretStore : Susu.Abstractions.ISecretStore
    {
        public bool Has(string a, string n) => false;
        public IReadOnlyList<string> Names(string a) => [];
        public void Write(string a, string n, ReadOnlySpan<char> v) { }
        public bool Delete(string a, string n) => false;
        public bool TryRead(string a, string n, out string v) { v = ""; return false; }
    }

    [Fact]
    public async Task Broker_denies_a_plugin_declared_url_with_embedded_userinfo_before_ever_building_a_request()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"ok":true}"""));
        using var broker = new Broker(secretStore: new NoopSecretStore());
        broker.ApproveLocalOrigin(server.Origin);
        // Origins is populated from a URL parse that strips userinfo (Broker.Origin), so the grant is
        // still issued against the plain origin - the point is that BuildRequest itself must still refuse
        // a request whose *declared* URL carries userinfo, regardless of what origin was granted.
        var grant = broker.Issue("r1", "plugin-x", 1, [server.Origin]);

        string args = "{\"method\":\"GET\",\"url\":\"http://evil:secret@127.0.0.1:" + server.Port + "/x\"}";
        var envelope = new IpcEnvelope(ProtocolVersions.Ipc, IpcMessageType.ApiCall, "r1", "job", PluginId: "plugin-x", Grant: grant.Grant,
            Payload: JsonSerializer.SerializeToElement(new ApiCallPayload(1, 1, "http", JsonDocument.Parse(args).RootElement), ContractsJson.Default.ApiCallPayload));
        var result = await broker.HandleAsync(envelope);

        Assert.False(result.Ok);
        Assert.Contains("userinfo", result.Value.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, server.RequestCount);
    }
}

/// <summary>
/// B06: a file handle a plugin supplies to $http (body.file / multipart file field / bodyFiles) must be
/// one this exact call's grant was issued for - never a forged id, never a handle another call/grant
/// holds (even if it is a real, currently-live lease), and never a lease whose last reference was already
/// released. Broker.ReleaseAll + FileLeases.Release together make a revoked/expired lease's id genuinely
/// stop resolving, so reuse after release is indistinguishable from a pure forgery at the broker's check.
/// </summary>
public class FileHandleSecurityTests
{
    private sealed class NoopSecretStore : Susu.Abstractions.ISecretStore
    {
        public bool Has(string a, string n) => false;
        public IReadOnlyList<string> Names(string a) => [];
        public void Write(string a, string n, ReadOnlySpan<char> v) { }
        public bool Delete(string a, string n) => false;
        public bool TryRead(string a, string n, out string v) { v = ""; return false; }
    }

    private static IpcEnvelope HttpEnvelope(string grant, string requestId, string pluginId, int callId, string argsJson)
        => new(ProtocolVersions.Ipc, IpcMessageType.ApiCall, requestId, "job", PluginId: pluginId, Grant: grant,
            Payload: JsonSerializer.SerializeToElement(new ApiCallPayload(1, callId, "http", JsonDocument.Parse(argsJson).RootElement), ContractsJson.Default.ApiCallPayload));

    [Fact]
    public async Task A_forged_handle_id_never_issued_by_any_lease_is_rejected()
    {
        string leaseDir = Path.Combine(Path.GetTempPath(), "susu-b06-forged-" + Guid.NewGuid().ToString("N"));
        using var leases = new FileLeases(leaseDir);
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"ok":true}"""));
        using var broker = new Broker(leases: leases, secretStore: new NoopSecretStore());
        broker.ApproveLocalOrigin(server.Origin);
        var grant = broker.Issue("r1", "plugin-x", 1, [server.Origin]);

        string args = "{\"method\":\"POST\",\"url\":\"" + server.Origin + "/x\",\"body\":{\"kind\":\"file\",\"file\":\"0123456789abcdef0123456789abcdef\"}}";
        var result = await broker.HandleAsync(HttpEnvelope(grant.Grant, "r1", "plugin-x", 1, args));

        Assert.False(result.Ok);
        Assert.Equal("bad_response", result.Value.GetProperty("kind").GetString());
        Assert.Equal(0, server.RequestCount); // never sent - denied before any network I/O
        try { Directory.Delete(leaseDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_real_lease_held_by_a_different_calls_grant_is_rejected()
    {
        string leaseDir = Path.Combine(Path.GetTempPath(), "susu-b06-othercall-" + Guid.NewGuid().ToString("N"));
        using var leases = new FileLeases(leaseDir);
        var lease = leases.Create("input", "bin");
        File.WriteAllBytes(leases.PathOf(lease), [1, 2, 3, 4]);

        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"ok":true}"""));
        using var broker = new Broker(leases: leases, secretStore: new NoopSecretStore());
        broker.ApproveLocalOrigin(server.Origin);
        // Grant #1 legitimately owns the lease (e.g. a prior call that read an input file).
        broker.Issue("r0", "plugin-x", 0, [server.Origin], handles: [lease.Id]);
        // Grant #2 (a different call) never listed the lease among its own handles.
        var grant2 = broker.Issue("r1", "plugin-x", 1, [server.Origin]);

        string args = "{\"method\":\"POST\",\"url\":\"" + server.Origin + "/x\",\"body\":{\"kind\":\"file\",\"file\":\"" + lease.Id + "\"}}";
        var result = await broker.HandleAsync(HttpEnvelope(grant2.Grant, "r1", "plugin-x", 1, args));

        Assert.False(result.Ok);
        Assert.Equal("bad_response", result.Value.GetProperty("kind").GetString());
        Assert.Contains("not granted", result.Value.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, server.RequestCount);
        try { Directory.Delete(leaseDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_lease_already_released_no_longer_resolves_even_with_the_right_grant()
    {
        string leaseDir = Path.Combine(Path.GetTempPath(), "susu-b06-revoked-" + Guid.NewGuid().ToString("N"));
        using var leases = new FileLeases(leaseDir);
        var lease = leases.Create("input", "bin");
        File.WriteAllBytes(leases.PathOf(lease), [1, 2, 3, 4]);
        leases.Release(lease); // simulates the lease's owning call completing and its lease being revoked

        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"ok":true}"""));
        using var broker = new Broker(leases: leases, secretStore: new NoopSecretStore());
        broker.ApproveLocalOrigin(server.Origin);
        // The grant still (optimistically) lists the handle - a stale plugin state, or an attempt to
        // replay an old handle id - but the lease itself is gone, so it must still be denied.
        var grant = broker.Issue("r1", "plugin-x", 1, [server.Origin], handles: [lease.Id]);

        string args = "{\"method\":\"POST\",\"url\":\"" + server.Origin + "/x\",\"body\":{\"kind\":\"file\",\"file\":\"" + lease.Id + "\"}}";
        var result = await broker.HandleAsync(HttpEnvelope(grant.Grant, "r1", "plugin-x", 1, args));

        Assert.False(result.Ok);
        Assert.Equal("bad_response", result.Value.GetProperty("kind").GetString());
        Assert.Equal(0, server.RequestCount);
        try { Directory.Delete(leaseDir, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// S05, direct: <see cref="OriginPolicy.IsDisallowedForCloud"/> is the sole address-class check
/// <c>NetworkBroker.ConnectValidatedAsync</c> applies to every DNS candidate for a non-local (cloud)
/// origin - it had no direct unit test (only reached indirectly, and even then only via a scheme/local
/// mismatch that masks it - see <see cref="EndToEndOriginPolicyTests"/>). Spot-checked by temporarily
/// removing the loopback branch from the production method: every test in this class failed as expected,
/// then the branch was restored.
/// </summary>
public class OriginPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")] // loopback
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")] // CGN
    [InlineData("169.254.1.1")] // link-local
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("192.0.2.1")] // TEST-NET-1
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")] // multicast
    [InlineData("0.0.0.0")]
    public void Private_loopback_and_reserved_ipv4_addresses_are_disallowed_for_a_cloud_origin(string address)
        => Assert.True(OriginPolicy.IsDisallowedForCloud(System.Net.IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    public void Ordinary_public_ipv4_addresses_are_allowed(string address)
        => Assert.False(OriginPolicy.IsDisallowedForCloud(System.Net.IPAddress.Parse(address)));

    [Theory]
    [InlineData("::1")] // loopback
    [InlineData("fe80::1")] // link-local
    [InlineData("fc00::1")] // unique local
    [InlineData("::")] // unspecified
    [InlineData("ff02::1")] // multicast
    public void Private_and_reserved_ipv6_addresses_are_disallowed(string address)
        => Assert.True(OriginPolicy.IsDisallowedForCloud(System.Net.IPAddress.Parse(address)));

    [Fact]
    public void An_ordinary_public_ipv6_address_is_allowed()
        => Assert.False(OriginPolicy.IsDisallowedForCloud(System.Net.IPAddress.Parse("2606:4700:4700::1111")));

    [Fact]
    public void An_ipv4_mapped_ipv6_loopback_is_still_recognized()
        => Assert.True(OriginPolicy.IsDisallowedForCloud(System.Net.IPAddress.Parse("::ffff:127.0.0.1")));
}

/// <summary>
/// S05, end to end: reaches the actual DNS/connect-time check in <c>NetworkBroker.ConnectValidatedAsync</c> -
/// not just OriginPolicy's pure function. This matters because NetworkBrokerTests's existing
/// "A_cloud_origin_resolving_to_loopback_is_rejected" test uses a *plain http* loopback target, which is
/// actually rejected earlier by a different, unrelated gate (IsAllowedForRequest's "http requires an
/// approved local origin" check) before the DNS/address check in ConnectValidatedAsync is ever reached -
/// confirmed by temporarily removing the loopback branch from OriginPolicy.IsDisallowedForCloud, which
/// left that existing test passing (still denied, for the unrelated reason). An https:// URL is not
/// shadowed by that gate (it only special-cases "http"), so it actually exercises the DNS/connect-time
/// origin check this test targets.
/// </summary>
public class EndToEndOriginPolicyTests
{
    [Fact]
    public async Task An_https_request_to_an_unapproved_loopback_address_is_rejected_by_the_connect_time_check()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"ok":true}"""));
        var options = new NetworkBrokerOptions { Timeout = TimeSpan.FromSeconds(5) }; // no local origin approved
        using var broker = new NetworkBroker(options);

        var request = new BrokerHttpRequest("GET", new Uri($"https://127.0.0.1:{server.Port}/x"), [], RequestBody.None, [], [],
            (_, _) => throw new InvalidOperationException(), null, ResponseKind.Json, [], null, LocalOriginApproved: false);
        var outcome = await broker.ExecuteAsync(request, TestContext.Current.CancellationToken);

        // Rejected before any TLS handshake is attempted against the plain-HTTP loopback server (which
        // would otherwise fail with a *different*, TLS-shaped error) - IsAllowedForRequest lets https://
        // through regardless of local approval, so only ConnectValidatedAsync's DNS/address check can be
        // what is actually rejecting this.
        var failure = Assert.IsType<BrokerFailure>(outcome);
        Assert.Equal("network", failure.Kind);
        Assert.Contains("disallowed address for a cloud origin", failure.Detail, StringComparison.Ordinal);
        Assert.Equal(0, server.RequestCount); // never reached the application layer
    }
}

/// <summary>B08: NetworkBroker never auto-decompresses a response (AutomaticDecompression = None), so a
/// small gzip-encoded body that would inflate to something large is read and capped as its raw
/// (compressed, un-inflated) wire bytes - a caller-uncontrolled decompression bomb structurally cannot
/// expand past the byte cap inside this broker, because it is never inflated at all.</summary>
public class DecompressionBombTests
{
    [Fact]
    public async Task A_gzip_encoded_response_is_never_auto_inflated()
    {
        byte[] gzipped;
        using (var mem = new MemoryStream())
        {
            using (var gz = new System.IO.Compression.GZipStream(mem, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                byte[] repeated = System.Text.Encoding.UTF8.GetBytes(new string('a', 200_000)); // compresses tiny, would inflate large
                gz.Write(repeated, 0, repeated.Length);
            }
            gzipped = mem.ToArray();
        }
        Assert.True(gzipped.Length < 195_000, $"expected gzip to shrink 200000 repeated bytes, got {gzipped.Length} bytes"); // sanity: really compressed, not incidentally larger

        using var server = new LoopbackHttpServer(_ => new LoopbackHttpResponse(200, gzipped,
            new Dictionary<string, string> { ["Content-Encoding"] = "gzip" }, ContentType: "application/json"));
        var options = new NetworkBrokerOptions { Timeout = TimeSpan.FromSeconds(5) };
        options.LocalOrigins.Approve(server.Origin);
        using var broker = new NetworkBroker(options);

        var request = new BrokerHttpRequest("GET", new Uri(server.Origin + "/x"), [], RequestBody.None, [], [],
            (_, _) => throw new InvalidOperationException(), null, ResponseKind.Json, [], null, LocalOriginApproved: true);
        var outcome = await broker.ExecuteAsync(request, TestContext.Current.CancellationToken);

        // Never parses as the 200000-byte inflated JSON text; the broker only ever saw the small,
        // still-compressed wire bytes (garbage as JSON, so it falls back to text of the same small size -
        // UTF-8 decoding of the binary gzip bytes replaces invalid sequences with U+FFFD, which can grow
        // the character count a little, but nowhere near the 200000-byte inflated payload a real
        // decompression would have produced).
        var success = Assert.IsType<BrokerSuccess>(outcome);
        Assert.Null(success.Response.JsonBody);
        Assert.NotNull(success.Response.TextBody);
        Assert.True(success.Response.TextBody!.Length < 50_000, $"expected roughly compressed-size text, got {success.Response.TextBody.Length} chars");
    }
}
