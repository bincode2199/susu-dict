using System.Text.Json;
using Susu.Contracts;
using Susu.Plugins;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F05.1/F05.2 through the real AppContainer sandbox + QuickJS engine + IPC channel (not just the
/// C#-envelope level of BrokerTests/NetworkBrokerTests): a plugin capability calling <c>ctx.$http</c>
/// and <c>ctx.$http.stream</c> against a real loopback socket. Same staging approach as
/// PluginHostIntegrationTests; skips itself when susu.exe has not been published next to this
/// assembly (see that class's comment for why NativeAOT publish is required, not a Debug build).
/// </summary>
public class NetworkBrokerIntegrationTests
{
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
        string staged = Path.Combine(Path.GetTempPath(), "susu-netbroker-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staged);
        foreach (string file in Directory.EnumerateFiles(output))
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(staged, Path.GetFileName(file)));
        string plugins = Path.Combine(staged, "plugins");
        Directory.CreateDirectory(plugins);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "plugins"), "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(plugins, Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "fixtures", "plugins"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return staged;
    }

    [Fact]
    public async Task A_plugin_calling_ctx_http_gets_a_real_response_through_the_full_broker_pipeline()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"ok":true,"value":42}"""));
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load("echo", "plugins/echo").Ok);
            var (_, _, task) = session.Invoke("echo", "httpGet", $$"""{"url":"{{server.Origin}}/x"}""", jobId: "job-http", origins: [server.Origin]);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(IpcMessageType.Completed, envelope.Type);
            var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
            Assert.True(completed.Ok, completed.Error?.Detail);
            var result = completed.Result!.Value;
            Assert.Equal(200, result.GetProperty("status").GetInt32());
            Assert.True(result.GetProperty("body").GetProperty("ok").GetBoolean());
            Assert.Equal(42, result.GetProperty("body").GetProperty("value").GetInt32());
        }
        finally { session.Shutdown(2000); }
    }

    [Fact]
    public async Task A_plugin_calling_ctx_http_stream_receives_every_SSE_piece_through_the_real_bridge()
    {
        string? staged = StageHost();
        if (staged is null) return;
        string[] events = ["event: a\ndata: one\n\n", "event: b\ndata: two\n\n", "event: c\ndata: three\n\n"];
        using var server = new LoopbackHttpServer(async (_, stream, ct) =>
        {
            await LoopbackHttpServer.WriteSseHeadAsync(stream, ct);
            foreach (var e in events) { await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(e), ct); await Task.Delay(20, ct); }
        });
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load("echo", "plugins/echo").Ok);
            var (_, _, task) = session.Invoke("echo", "httpStream", $$"""{"url":"{{server.Origin}}/sse"}""", jobId: "job-sse", origins: [server.Origin]);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(IpcMessageType.Completed, envelope.Type);
            var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
            Assert.True(completed.Ok, completed.Error?.Detail);
            var result = completed.Result!.Value;
            Assert.Equal(200, result.GetProperty("status").GetInt32());
            string joined = string.Concat(result.GetProperty("pieces").EnumerateArray().Select(p => p.GetString()));
            Assert.Equal(string.Concat(events), joined); // every piece the server sent arrived, in order, none dropped or merged incorrectly
        }
        finally { session.Shutdown(2000); }
    }

    /// <summary>
    /// StreamWindow credit (PLAN 4.5.4.4/F04.3): the server pushes far more SSE data than one call's
    /// unacked budget (256 KiB) allows before the plugin ever reads a single piece. The host-side pump
    /// must stall (not buffer it all in memory) until the plugin starts consuming - proven here by the
    /// call still completing successfully with everything received once the plugin does read, which
    /// would not be possible if the pump had thrown/aborted on a full window instead of backing off.
    /// </summary>
    [Fact]
    public async Task Streaming_more_than_the_window_allows_backs_off_instead_of_failing()
    {
        string? staged = StageHost();
        if (staged is null) return;
        // Each piece is 8 KiB; 40 of them (320 KiB) exceed the 256 KiB per-call unacked window, so the
        // pump must apply backpressure partway through rather than buffering all of it unbounded.
        string piece = new string('x', 8 * 1024);
        int count = 40;
        using var server = new LoopbackHttpServer(async (_, stream, ct) =>
        {
            await LoopbackHttpServer.WriteSseHeadAsync(stream, ct);
            // A tiny gap between writes keeps each one its own decoded piece (realistic SSE pacing);
            // a true back-to-back burst can coalesce into an oversized single read instead (see the
            // S09 comment at the bottom of this file for why that is not separately asserted here).
            for (int i = 0; i < count; i++) { await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(piece), ct); await Task.Delay(15, ct); } // > NetworkBroker.CoalesceWindow, so pieces don't merge
        });
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load("echo", "plugins/echo").Ok);
            var (_, _, task) = session.Invoke("echo", "httpStream", $$"""{"url":"{{server.Origin}}/big"}""", jobId: "job-window", origins: [server.Origin]);
            // Generous timeout: the plugin reading slowly (one round trip per piece) plus the pump
            // backing off is expected to take real wall-clock time, not be instantaneous.
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(IpcMessageType.Completed, envelope.Type);
            var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
            Assert.True(completed.Ok, completed.Error?.Detail);
            string joined = string.Concat(completed.Result!.Value.GetProperty("pieces").EnumerateArray().Select(p => p.GetString()));
            Assert.Equal(piece.Length * count, joined.Length); // nothing lost across the backpressure stall
        }
        finally { session.Shutdown(2000); }
    }

    /// <summary>
    /// B07-style cleanup: a plugin that reads only the first piece and returns without draining the
    /// generator (httpStreamFirstOnly never iterates via <c>for...of</c>, so the generator's
    /// try/finally never runs and http.stream.close is never called explicitly) must not leak the open
    /// stream/pump/StreamWindow reservation forever - Broker.Revoke, run when the capability call itself
    /// completes, must close it.
    /// </summary>
    [Fact]
    public async Task An_abandoned_stream_is_cleaned_up_when_its_call_completes()
    {
        string? staged = StageHost();
        if (staged is null) return;
        string piece = new string('z', 1024);
        using var server = new LoopbackHttpServer(async (_, stream, ct) =>
        {
            await LoopbackHttpServer.WriteSseHeadAsync(stream, ct);
            for (int i = 0; i < 5; i++) { await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(piece), ct); await Task.Delay(20, ct); }
        });
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load("echo", "plugins/echo").Ok);
            var (_, _, task) = session.Invoke("echo", "httpStreamFirstOnly", $$"""{"url":"{{server.Origin}}/abandoned"}""", jobId: "job-abandon", origins: [server.Origin]);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(IpcMessageType.Completed, envelope.Type);
            var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
            Assert.True(completed.Ok, completed.Error?.Detail);
            Assert.Equal(piece, completed.Result!.Value.GetProperty("first").GetString());
            Assert.Equal(0, session.Broker.ActiveStreams); // Revoke cleaned it up; nothing left dangling
        }
        finally { session.Shutdown(2000); }
    }

    // S09 (oversized single stream-chunk rejection): Broker.PumpStreamAsync checks each decoded piece
    // against ProtocolLimits.MaxStreamChunkBytes (64 KiB) before buffering it and throws bad_response
    // if it is over. This was manually verified to fire correctly (confirmed via a temporary trace: a
    // 70 KiB single-write server response produced one 71680-byte decoded piece and the expected
    // "stream chunk exceeded the frame size limit" rejection). It is deliberately not asserted by an
    // automated test here: whether one particular TCP read happens to coalesce a burst into a single
    // >64 KiB piece, vs. arriving as many smaller pieces as the segments/OS buffers actually deliver
    // them, turned out to be a genuine, unfixable-from-the-test-side race - TCP delivers bytes to a
    // reader progressively as they arrive, not held back until a sender-side write call "completes",
    // so no server-side write/timing pattern reliably forces a large single read; several were tried
    // (single write, many small un-paced writes, single write + a completion-side pause, all through
    // both the real sandbox and a direct in-process Broker call) and none reproduced it deterministically
    // enough for CI. A flaky test would be worse than this documented gap.
}
