using System.Text.Json;
using Susu.Contracts;
using Susu.Plugins;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

public class ManifestTests
{
    private const string Valid = """
        id: com.example.deepl
        name: DeepL
        apiVersion: 1
        minHost: 1
        entry: main.js
        capabilities:
          - translate
        hosts:
          - https://api.deepl.example
        """;

    [Fact]
    public void Parses_a_valid_manifest()
    {
        var (manifest, issues) = PackageManifest.Parse(Valid);
        Assert.Empty(issues);
        Assert.NotNull(manifest);
        Assert.Equal("com.example.deepl", manifest!.Id);
        Assert.Equal(["translate"], manifest.Capabilities);
        Assert.Equal(["https://api.deepl.example"], manifest.Hosts);
    }

    [Fact]
    public void Rejects_invalid_package_id()
    {
        var (manifest, issues) = PackageManifest.Parse(Valid.Replace("com.example.deepl", "NotReverseDomain"));
        Assert.Null(manifest);
        Assert.Contains(issues, i => i.Path == "id" && i.Code == "invalid");
    }

    [Fact]
    public void Rejects_unsupported_api_version()
    {
        var (manifest, issues) = PackageManifest.Parse(Valid.Replace("apiVersion: 1", "apiVersion: 99"));
        Assert.Null(manifest);
        Assert.Contains(issues, i => i.Path == "apiVersion" && i.Code == "unsupported");
    }

    [Fact]
    public void Rejects_unknown_capability()
    {
        var (manifest, issues) = PackageManifest.Parse(Valid.Replace("- translate", "- not-a-capability"));
        Assert.Null(manifest);
        Assert.Contains(issues, i => i.Path == "capabilities" && i.Code == "unknown");
    }

    [Fact]
    public void Rejects_path_escaping_entry()
    {
        var (manifest, issues) = PackageManifest.Parse(Valid.Replace("entry: main.js", "entry: ../../evil.js"));
        Assert.Null(manifest);
        Assert.Contains(issues, i => i.Path == "entry" && i.Code == "invalid");
    }

    [Fact]
    public void Rejects_missing_required_fields()
    {
        var (manifest, issues) = PackageManifest.Parse("id: com.example.x\n");
        Assert.Null(manifest);
        Assert.Contains(issues, i => i.Path == "name");
        Assert.Contains(issues, i => i.Path == "capabilities" && i.Code == "empty");
    }

    [Fact]
    public void Rejects_duplicate_keys_and_anchors()
    {
        var (dup, dupIssues) = PackageManifest.Parse("id: com.example.a\nid: com.example.b\nname: X\napiVersion: 1\nminHost: 1\ncapabilities: [translate]\n");
        Assert.Null(dup);
        Assert.Contains(dupIssues, i => i.Code == "syntax");

        var (anchor, anchorIssues) = PackageManifest.Parse("id: &a com.example.a\nname: X\napiVersion: 1\nminHost: 1\ncapabilities: [translate]\n");
        Assert.Null(anchor);
        Assert.Contains(anchorIssues, i => i.Code == "syntax");
    }

    [Fact]
    public void Empty_manifest_is_an_issue_not_a_crash()
    {
        var (manifest, issues) = PackageManifest.Parse("");
        Assert.Null(manifest);
        Assert.Contains(issues, i => i.Code == "empty");
    }
}

public class SafePackageTests
{
    [Fact]
    public void Accepts_a_small_clean_package()
    {
        string dir = Path.Combine(Path.GetTempPath(), "susu-safe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "main.js"), "export default {};");
            var issues = SafePackage.Validate(dir);
            Assert.Empty(issues);
            var hashes = SafePackage.CanonicalHashes(dir);
            Assert.Single(hashes);
            Assert.Equal("main.js", hashes[0].Path);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Rejects_a_file_over_the_per_file_cap()
    {
        string dir = Path.Combine(Path.GetTempPath(), "susu-safe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "big.bin"), new byte[SafePackage.MaxFileBytes + 1]);
            var issues = SafePackage.Validate(dir);
            Assert.Contains(issues, i => i.Code == "too-large");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Case-duplicate paths (e.g. "Main.js" and "main.js") cannot be reproduced on an NTFS directory
    // scan - the filesystem itself only keeps one entry. SafePackage.Validate still carries the check
    // (harmless here) for when F16 applies it to a zip's raw entry list before anything is extracted.

    [Fact]
    public void Canonical_hashes_are_sorted_and_stable()
    {
        string dir = Path.Combine(Path.GetTempPath(), "susu-safe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "lib"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "main.js"), "a");
            File.WriteAllText(Path.Combine(dir, "lib", "b.js"), "b");
            var hashes = SafePackage.CanonicalHashes(dir);
            Assert.Equal(["lib/b.js", "main.js"], hashes.Select(h => h.Path));
            Assert.All(hashes, h => Assert.Equal(64, h.Sha256Hex.Length));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

public class SignatureFileTests
{
    [Fact]
    public void Parses_a_well_formed_signature()
    {
        string text = "keyId: root-2026\nalgorithm: ed25519\nsignature: " + Convert.ToBase64String([1, 2, 3]) + "\n";
        var (file, issues) = SignatureFile.Parse(text);
        Assert.Empty(issues);
        Assert.NotNull(file);
        Assert.Equal("root-2026", file!.KeyId);
        Assert.Equal([1, 2, 3], file.Signature);
    }

    [Fact]
    public void Rejects_unknown_algorithm_and_extra_fields()
    {
        string text = "keyId: k\nalgorithm: rsa\nsignature: AA==\nextra: nope\n";
        var (file, issues) = SignatureFile.Parse(text);
        Assert.Null(file);
        Assert.Contains(issues, i => i.Path == "algorithm");
        Assert.Contains(issues, i => i.Code == "unknown-field");
    }

    [Fact]
    public void Manifest_signed_bytes_are_domain_separated()
    {
        byte[] a = SignatureFile.ManifestSignedBytes("susu-plugin-manifest-v1", [1, 2]);
        byte[] b = SignatureFile.ManifestSignedBytes("susu-app-manifest-v1", [1, 2]);
        Assert.NotEqual(a, b); // same payload, different domain tag: different signed bytes
    }
}

public class BrokerTests
{
    [Fact]
    public async Task An_expired_grant_is_rejected_even_though_it_was_never_revoked()
    {
        var broker = new Broker();
        var grant = broker.Issue("r1", "p1", 1, ["https://allowed.example"], expiresAt: DateTime.UtcNow.AddMilliseconds(-1));
        var envelope = new IpcEnvelope(ProtocolVersions.Ipc, IpcMessageType.ApiCall, "r1", "j1", PluginId: "p1", Grant: grant.Grant,
            Payload: JsonSerializer.SerializeToElement(new ApiCallPayload(1, 1, "http", JsonDocument.Parse("{\"method\":\"GET\",\"url\":\"https://allowed.example\"}").RootElement), ContractsJson.Default.ApiCallPayload));
        var result = await broker.HandleAsync(envelope);
        Assert.False(result.Ok);
        Assert.Equal(1, broker.ActiveGrants); // still tracked (not revoked), but every use is denied
    }

    [Fact]
    public async Task Denies_a_call_with_no_grant()
    {
        var broker = new Broker();
        var envelope = new IpcEnvelope(ProtocolVersions.Ipc, IpcMessageType.ApiCall, "r1", "j1", PluginId: "p1", Grant: "not-issued",
            Payload: JsonSerializer.SerializeToElement(new ApiCallPayload(1, 1, "http", JsonDocument.Parse("{\"method\":\"GET\",\"url\":\"https://x.example\"}").RootElement), ContractsJson.Default.ApiCallPayload));
        var result = await broker.HandleAsync(envelope);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Denies_an_origin_outside_the_grant()
    {
        var broker = new Broker();
        var grant = broker.Issue("r1", "p1", 1, ["https://allowed.example"]);
        var envelope = new IpcEnvelope(ProtocolVersions.Ipc, IpcMessageType.ApiCall, "r1", "j1", PluginId: "p1", Grant: grant.Grant,
            Payload: JsonSerializer.SerializeToElement(new ApiCallPayload(1, 1, "http", JsonDocument.Parse("{\"method\":\"GET\",\"url\":\"https://other.example\"}").RootElement), ContractsJson.Default.ApiCallPayload));
        var result = await broker.HandleAsync(envelope);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Revoked_grant_is_denied()
    {
        var broker = new Broker();
        var grant = broker.Issue("r1", "p1", 1, ["https://allowed.example"]);
        broker.Revoke(grant.Grant);
        var envelope = new IpcEnvelope(ProtocolVersions.Ipc, IpcMessageType.ApiCall, "r1", "j1", PluginId: "p1", Grant: grant.Grant,
            Payload: JsonSerializer.SerializeToElement(new ApiCallPayload(1, 1, "store.get", JsonDocument.Parse("{\"key\":\"k\"}").RootElement), ContractsJson.Default.ApiCallPayload));
        var result = await broker.HandleAsync(envelope);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Store_is_namespaced_per_plugin()
    {
        var broker = new Broker();
        var grantA = broker.Issue("r1", "pluginA", 1, []);
        var setA = await broker.HandleAsync(Call(grantA.Grant, "r1", "pluginA", 1, "store.set", "{\"key\":\"k\",\"value\":\"a\"}"));
        Assert.True(setA.Ok);
        var grantB = broker.Issue("r2", "pluginB", 1, []);
        var getB = await broker.HandleAsync(Call(grantB.Grant, "r2", "pluginB", 1, "store.get", "{\"key\":\"k\"}"));
        Assert.True(getB.Ok);
        Assert.Equal("null", getB.Value.GetRawText());
    }

    private static IpcEnvelope Call(string grant, string requestId, string pluginId, int callId, string op, string argsJson)
        => new(ProtocolVersions.Ipc, IpcMessageType.ApiCall, requestId, "job", PluginId: pluginId, Grant: grant,
            Payload: JsonSerializer.SerializeToElement(new ApiCallPayload(1, callId, op, JsonDocument.Parse(argsJson).RootElement), ContractsJson.Default.ApiCallPayload));
}

/// <summary>Minimal in-memory session double so Supervisor's timing can be unit-tested without a real sandbox.</summary>
internal sealed class FakeSession : IHostSessionHandle
{
    public event Action? Disconnected;
    public bool Disposed { get; private set; }
    public void Crash() => Disconnected?.Invoke();
    public void Dispose() => Disposed = true;
}

public class SupervisorTests
{
    // ManualClock resolves Delay() continuations via RunContinuationsAsynchronously (a background
    // thread-pool hop), so a settle pause is needed after Advance() before asserting - mirrors the
    // pattern in JobsTests.cs.
    private static Task Settle() => Task.Delay(30, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Restarts_with_1_2_4_second_backoff_after_each_crash()
    {
        var clock = new ManualClock();
        int launches = 0;
        var supervisor = new Supervisor<FakeSession>(() => { launches++; return new FakeSession(); }, clock);
        var first = supervisor.Start();
        Assert.Equal(1, launches);

        first.Crash();
        clock.Advance(TimeSpan.FromMilliseconds(999));
        await Settle();
        Assert.Equal(1, launches); // still short of the 1 s backoff
        clock.Advance(TimeSpan.FromMilliseconds(2));
        await Settle();
        Assert.Equal(2, launches); // relaunched after 1 s

        supervisor.Current.Crash();
        clock.Advance(TimeSpan.FromSeconds(2));
        await Settle();
        Assert.Equal(3, launches); // second failure backs off 2 s

        supervisor.Current.Crash();
        clock.Advance(TimeSpan.FromSeconds(4));
        await Settle();
        Assert.Equal(4, launches); // third failure backs off 4 s
        Assert.False(supervisor.Stopped);
    }

    [Fact]
    public async Task Stops_automatic_restart_after_a_fourth_failure_inside_the_60s_window()
    {
        var clock = new ManualClock();
        var supervisor = new Supervisor<FakeSession>(() => new FakeSession(), clock);
        bool stalled = false;
        supervisor.Stalled += () => stalled = true;
        var session = supervisor.Start();

        session.Crash(); clock.Advance(TimeSpan.FromSeconds(1)); await Settle();
        supervisor.Current.Crash(); clock.Advance(TimeSpan.FromSeconds(2)); await Settle();
        supervisor.Current.Crash(); clock.Advance(TimeSpan.FromSeconds(4)); await Settle();
        // A fourth crash inside the 60 s window stops automatic restart instead of relaunching.
        supervisor.Current.Crash();
        await Settle();
        Assert.True(supervisor.Stopped);
        Assert.True(stalled);
        Assert.Throws<InvalidOperationException>(() => supervisor.Current);
    }

    [Fact]
    public async Task A_failure_outside_the_60s_window_resets_the_backoff_counter()
    {
        var clock = new ManualClock();
        int launches = 0;
        var supervisor = new Supervisor<FakeSession>(() => { launches++; return new FakeSession(); }, clock);
        var session = supervisor.Start();

        session.Crash(); clock.Advance(TimeSpan.FromSeconds(1)); await Settle();
        Assert.Equal(2, launches);
        // Nothing else fails for over 60 s: the failure history should have aged out.
        clock.Advance(TimeSpan.FromSeconds(61));
        supervisor.Current.Crash();
        clock.Advance(TimeSpan.FromSeconds(1)); // first-failure backoff again, not stalled
        await Settle();
        Assert.Equal(3, launches);
        Assert.False(supervisor.Stopped);
    }

    [Fact]
    public async Task Manual_restart_clears_the_stopped_state_and_disposes_the_old_session()
    {
        var clock = new ManualClock();
        var supervisor = new Supervisor<FakeSession>(() => new FakeSession(), clock);
        var session = supervisor.Start();
        session.Crash(); clock.Advance(TimeSpan.FromSeconds(1)); await Settle();
        supervisor.Current.Crash(); clock.Advance(TimeSpan.FromSeconds(2)); await Settle();
        supervisor.Current.Crash(); clock.Advance(TimeSpan.FromSeconds(4)); await Settle();
        supervisor.Current.Crash(); // stalls
        await Settle();
        Assert.True(supervisor.Stopped);

        var restarted = supervisor.ManualRestart();
        Assert.False(supervisor.Stopped);
        Assert.NotNull(restarted);
    }

    [Fact]
    public async Task A_crash_never_resends_calls_a_new_session_never_saw()
    {
        // The property this protects: Supervisor only ever creates a brand-new TSession on restart
        // (LaunchAndWatch calls `launch()` fresh); it holds no queue of "pending calls" to replay, so
        // whatever the disposed session's callers were waiting on simply completes with a disconnect
        // error there (HostSession.ReadLoop faults every outstanding TaskCompletionSource) and is
        // never resubmitted to the new process.
        var clock = new ManualClock();
        var sessions = new List<FakeSession>();
        var supervisor = new Supervisor<FakeSession>(() => { var s = new FakeSession(); sessions.Add(s); return s; }, clock);
        var first = supervisor.Start();
        first.Crash();
        clock.Advance(TimeSpan.FromSeconds(1));
        await Settle();
        Assert.Equal(2, sessions.Count);
        Assert.NotSame(sessions[0], sessions[1]);
        Assert.False(sessions[0].Disposed); // the crashed session already disconnected itself; Supervisor did not also Dispose it here
    }

    [Fact]
    public void A_stale_Disconnected_from_a_replaced_session_does_not_disturb_the_new_one()
    {
        // Regression: HostSession.Dispose() joins its reader thread, so a just-replaced session's
        // Disconnected can still fire (from that reader thread) after ManualRestart has already
        // installed a fresh session under the same Supervisor. Before OnDisconnected checked identity,
        // this stale notification nulled out - and triggered a spurious restart of - the live session,
        // corrupting state the caller (PluginProvider.TryGetCurrent) would otherwise see.
        var clock = new ManualClock();
        var supervisor = new Supervisor<FakeSession>(() => new FakeSession(), clock);
        var first = supervisor.Start();
        var second = supervisor.ManualRestart();
        Assert.NotSame(first, second);

        first.Crash(); // late/stale notification from the already-replaced session

        Assert.Same(second, supervisor.Current); // still the fresh session, not nulled
        Assert.False(supervisor.Stopped);
    }

    [Fact]
    public void Acquire_does_not_launch_until_the_first_call()
    {
        var clock = new ManualClock();
        int launches = 0;
        var supervisor = new Supervisor<FakeSession>(() => { launches++; return new FakeSession(); }, clock, TimeSpan.FromMinutes(10));
        Assert.Equal(0, launches); // F06.2a/PER02: no AppContainer launch just from constructing the Supervisor

        var host = supervisor.Acquire();
        Assert.NotNull(host);
        Assert.Equal(1, launches);
        supervisor.Release();
    }

    [Fact]
    public async Task Idle_release_disposes_the_session_once_no_call_is_in_flight_for_the_idle_window()
    {
        var clock = new ManualClock();
        int launches = 0;
        var supervisor = new Supervisor<FakeSession>(() => { launches++; return new FakeSession(); }, clock, TimeSpan.FromMinutes(10));
        var host = supervisor.Acquire()!;
        supervisor.Release();

        clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromMilliseconds(1));
        await Settle();
        Assert.False(host.Disposed); // not yet at the idle window

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Settle();
        Assert.True(host.Disposed);

        // A later call starts a fresh session rather than reusing the released one.
        var second = supervisor.Acquire()!;
        supervisor.Release();
        Assert.NotSame(host, second);
        Assert.Equal(2, launches);
    }

    [Fact]
    public async Task A_call_in_flight_postpones_idle_release_and_a_new_call_cancels_a_pending_one()
    {
        var clock = new ManualClock();
        var supervisor = new Supervisor<FakeSession>(() => new FakeSession(), clock, TimeSpan.FromMinutes(10));
        var host = supervisor.Acquire()!;
        // Simulate a second concurrent call still in flight: Release only drops the count to 1, so no
        // idle timer is scheduled yet even once the window elapses.
        supervisor.Acquire();
        supervisor.Release();
        clock.Advance(TimeSpan.FromMinutes(20));
        await Settle();
        Assert.False(host.Disposed);

        // The last call finishes: idle release is now scheduled, but a fresh Acquire before it fires
        // cancels it instead of racing a torn-down session out from under the new call.
        supervisor.Release();
        clock.Advance(TimeSpan.FromMinutes(5));
        await Settle();
        supervisor.Acquire();
        clock.Advance(TimeSpan.FromMinutes(10));
        await Settle();
        Assert.False(host.Disposed); // the pending timer from before was cancelled, not merely postponed
        supervisor.Release();
    }

    [Fact]
    public async Task Acquire_returns_null_instead_of_launching_while_a_crash_is_mid_backoff()
    {
        var clock = new ManualClock();
        int launches = 0;
        var supervisor = new Supervisor<FakeSession>(() => { launches++; return new FakeSession(); }, clock, TimeSpan.FromMinutes(10));
        var host = supervisor.Acquire()!;
        supervisor.Release();
        Assert.Equal(1, launches);

        host.Crash();
        Assert.Null(supervisor.Acquire()); // mid-backoff: must not race RestartAsync's own scheduled relaunch
        Assert.Equal(1, launches);

        clock.Advance(TimeSpan.FromSeconds(1));
        await Settle();
        Assert.Equal(2, launches); // RestartAsync's own relaunch, not Acquire's

        var resumed = supervisor.Acquire()!;
        Assert.NotNull(resumed);
        supervisor.Release();
    }
}
