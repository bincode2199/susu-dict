using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Plugins;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F06 independent verification: the coverage gaps listed in the F06 record, each end to end through
/// the real shipped packages, the real sandbox and the production consumer path (<see cref="PluginProvider"/>
/// under a lazy <see cref="Supervisor{HostSession}"/>, <see cref="TranslationSession"/>, the F01 card
/// reducer). The vendor is a <see cref="LoopbackHttpServer"/>. Skips itself when susu.exe is not published.
/// </summary>
public class F06VerificationTests
{
    private const string Full = "Hello, world!";
    private static readonly ConfigSnapshot Config = new(1, 1, 1, 1, TimeSpan.FromSeconds(30));

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

    /// <summary>susu.exe/DLLs plus the real shipped <paramref name="plugin"/> package, staged for the AppContainer.</summary>
    private static string? StageHost(string plugin)
    {
        string? output = FindHostBuildOutput();
        if (output is null) return null;
        string sourcePlugin = Path.Combine(output, "plugins", plugin);
        if (!File.Exists(Path.Combine(sourcePlugin, "main.js"))) return null;
        string staged = TestTemp.NewDir("susu-f06-verify");
        foreach (string file in Directory.EnumerateFiles(output))
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(staged, Path.GetFileName(file)));
        string targetPlugin = Path.Combine(staged, "plugins", plugin);
        Directory.CreateDirectory(targetPlugin);
        foreach (string file in Directory.EnumerateFiles(sourcePlugin))
            File.Copy(file, Path.Combine(targetPlugin, Path.GetFileName(file)));
        return staged;
    }

    private sealed class Rig : IDisposable
    {
        public required Supervisor<HostSession> Supervisor { get; init; }
        public required TranslationSession Session { get; init; }
        public required RecordingUsage Usage { get; init; }
        public ConcurrentQueue<CardSnapshot> Patches { get; } = new();
        public void Dispose() => Supervisor.Dispose();

        public async Task<CardSnapshot> WaitForAsync(Func<CardSnapshot, bool> condition, string what, int seconds = 30)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                var card = (await Session.SnapshotAsync()).Cards.Single();
                if (condition(card)) return card;
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            var last = (await Session.SnapshotAsync()).Cards.Single();
            throw new TimeoutException($"{what}: card stayed {last.State} \"{last.Text}\" ({last.Error}, collapsed={last.Collapsed})");
        }
    }

    private static Rig Build(Func<HostSession> launch, ITranslationProvider provider)
    {
        var supervisor = new Supervisor<HostSession>(launch, SystemClock.Instance, TimeSpan.FromMinutes(10));
        var usage = new RecordingUsage();
        var session = new TranslationSession([provider], new TranslationSessionOptions(Config, 1),
            new InvocationScheduler(new SchedulerLimits()), SystemClock.Instance, new FixedJitter(0), usage);
        var rig = new Rig { Supervisor = supervisor, Session = session, Usage = usage };
        session.CardChanged += patch => rig.Patches.Enqueue(patch.Card);
        return rig;
    }

    private static Rig BuildOpenAI(string staged, LoopbackHttpServer server)
    {
        var options = OpenAIPluginTests.Options(staged, server.Origin);
        Supervisor<HostSession>? supervisor = null;
        var package = TranslationPackages.Find(TranslationPackages.OpenAI)!;
        Func<HostSession> launch = () =>
        {
            var session = HostSession.Start(options);
            session.Broker.ApproveLocalOrigin(server.Origin);
            var loaded = session.Load(OpenAIPluginTests.PackageId, "plugins/openai");
            if (!loaded.Ok) { session.Shutdown(2000); throw new InvalidOperationException($"openai failed to load: {loaded.Error}"); }
            return session;
        };
        var rig = Build(launch, new ProxyProvider(() => new PluginProvider(OpenAIPluginTests.PackageId, "openai/translate", "OpenAI", package.Limits, supervisor!, [server.Origin],
            OpenAIPluginTests.InstanceId, OpenAIPluginTests.Signer, ["apiKey"], new Dictionary<string, string> { ["baseUrl"] = server.Origin }), "openai/translate", package.Limits));
        supervisor = rig.Supervisor;
        return rig;
    }

    private static string Events(params string[] deltas)
    {
        string sse = OpenAIPluginTests.Sse(deltas);
        return sse[..sse.LastIndexOf("data: [DONE]", StringComparison.Ordinal)];
    }

    private static async Task WriteChunkAsync(NetworkStream stream, string text, CancellationToken ct)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{data.Length:X}\r\n"), ct);
        await stream.WriteAsync(data, ct);
        await stream.WriteAsync("\r\n"u8.ToArray(), ct);
    }

    /// <summary>
    /// TEST-PLAN T01 at the wire: a long mixed text (ASCII runs, three-byte CJK, four-byte emoji, newlines)
    /// through the shipped MyMemory package and the production MyMemory limits. Every `q` the vendor
    /// receives is at most 500 UTF-8 bytes, decodes cleanly (no split code point), and the pieces in
    /// arrival order rebuild the original exactly; the card shows the whole text once.
    /// </summary>
    [Fact]
    public async Task T01_every_MyMemory_q_on_the_wire_is_at_most_500_UTF8_bytes_and_nothing_is_lost()
    {
        string? staged = StageHost("mymemory");
        if (staged is null) return;
        var package = TranslationPackages.Find(TranslationPackages.MyMemory)!;
        string original = new string('a', 501) + "\n" + string.Concat(Enumerable.Repeat("常见汉字测试", 30)) + "😀🎉\n" + string.Concat(Enumerable.Repeat("word 😀 ", 90)) + new string('界', 167);
        Assert.True(Encoding.UTF8.GetByteCount(original) > 2000);
        var received = new ConcurrentQueue<string>();
        using var server = new LoopbackHttpServer(req =>
        {
            string query = req.Path[(req.Path.IndexOf('?') + 1)..];
            string q = query.Split('&').Select(p => p.Split('=', 2)).Where(p => p[0] == "q").Select(p => Uri.UnescapeDataString(p[1])).Single();
            received.Enqueue(q);
            string echo = System.Text.Json.JsonSerializer.Serialize(new { responseData = new { translatedText = q }, responseStatus = 200 });
            return LoopbackHttpResponse.Json(200, echo);
        });
        Supervisor<HostSession>? supervisor = null;
        var rig = Build(() =>
        {
            var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false));
            session.Broker.ApproveLocalOrigin(server.Origin);
            if (!session.Load(package.PackageId, package.Directory).Ok) { session.Shutdown(2000); throw new InvalidOperationException("mymemory failed to load"); }
            return session;
        }, new ProxyProvider(() => new PluginProvider(package.PackageId, "mymemory/translate", "MyMemory", package.Limits, supervisor!, [server.Origin],
            config: new Dictionary<string, string> { ["baseUrl"] = server.Origin }), "mymemory/translate", package.Limits));
        supervisor = rig.Supervisor;
        using var _ = rig;

        await rig.Session.SubmitAsync(original, "en", "zh-Hans");
        var card = await rig.WaitForAsync(c => c.State is CardState.Ready or CardState.Failed, "T01");
        Assert.Equal(CardState.Ready, card.State);

        string[] qs = [.. received];
        Assert.True(qs.Length >= 5, $"only {qs.Length} request(s)");
        Assert.All(qs, q => Assert.True(Encoding.UTF8.GetByteCount(q) <= 500, $"q of {Encoding.UTF8.GetByteCount(q)} bytes"));
        Assert.All(qs, q => Assert.DoesNotContain('�', q));
        Assert.NotEqual(new string('a', 501), qs[0]); // 501 ASCII never fits one piece
        Assert.Equal(original, string.Concat(qs));
        Assert.Equal(original, card.Text);
        Assert.True(card.Chunked);
    }

    private sealed class ProxyProvider(Func<PluginProvider> make, string serviceId, TranslationLimits limits) : ITranslationProvider
    {
        private PluginProvider? inner;
        private PluginProvider Inner => inner ??= make();
        public string ServiceId => serviceId;
        public string DisplayName => serviceId;
        public string LimiterKey => "plugin:" + serviceId;
        public TranslationLimits Limits { get; } = limits;
        public bool SupportsLanguagePair(string from, string to) => true;
        public Task<ProviderOutcome> TranslateAsync(TranslateCall call, Func<string, ValueTask> onChunk, CancellationToken cancellationToken)
            => Inner.TranslateAsync(call, onChunk, cancellationToken);
    }

    /// <summary>A vendor that sends "Hello, " and then holds the stream open (no more data) until the client
    /// closes the connection, as a slow model would; later requests stream the whole answer.</summary>
    private static LoopbackHttpServer HoldingServer(Action<int> onRequest, SemaphoreSlim firstPieceSent, TaskCompletionSource<bool> clientGone)
    {
        int requests = 0;
        return new LoopbackHttpServer(async (_, stream, ct) =>
        {
            int n = Interlocked.Increment(ref requests);
            onRequest(n);
            await LoopbackHttpServer.WriteSseHeadAsync(stream, ct);
            if (n == 1)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(Events("Hello, ")), ct);
                firstPieceSent.Release();
                var socket = stream.Socket;
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline)
                {
                    if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0) { clientGone.TrySetResult(true); return; }
                    await Task.Delay(20, ct);
                }
                clientGone.TrySetResult(false);
                return;
            }
            foreach (var d in new[] { "Hello, ", "world", "!" }) { await stream.WriteAsync(Encoding.UTF8.GetBytes(Events(d)), ct); await Task.Delay(10, ct); }
            await stream.WriteAsync("data: [DONE]\n\n"u8.ToArray(), ct);
        });
    }

    /// <summary>
    /// TEST-PLAN J03 with a real plugin call: collapsing a streaming card cancels the attempt (the card ends
    /// collapsed, not Ready, the attempt is recorded as cancelled and is not retried), and expanding it again
    /// starts a new, normal request that completes. This is also the G1 demo's "collapse and cancel, then
    /// reopen" step. Whether the vendor connection really closes is the separate test below.
    /// </summary>
    [Fact]
    public async Task J03_collapsing_a_streaming_card_cancels_it_and_reopening_requests_again()
    {
        string? staged = OpenAIPluginTests.StageHost();
        if (staged is null) return;
        int requests = 0;
        using var firstPieceSent = new SemaphoreSlim(0);
        var clientGone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = HoldingServer(n => Volatile.Write(ref requests, n), firstPieceSent, clientGone);
        using var rig = BuildOpenAI(staged, server);

        await rig.Session.SubmitAsync("你好，世界！", "zh-Hans", "en");
        await firstPieceSent.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await rig.WaitForAsync(c => c.State == CardState.Streaming && c.Text == "Hello, ", "streaming");
        await rig.Session.ToggleAsync("openai/translate");
        var collapsed = await rig.WaitForAsync(c => c.Collapsed && c.State != CardState.Streaming, "collapse");
        Assert.Equal(CardState.Cancelled, collapsed.State);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref requests)); // a cancel is not retried
        Assert.Equal(["cancelled"], rig.Usage.Records.Select(r => r.Outcome));

        await rig.Session.ToggleAsync("openai/translate");
        var ready = await rig.WaitForAsync(c => c.State is CardState.Ready or CardState.Failed && !c.Collapsed, "reopen");
        Assert.Equal(CardState.Ready, ready.State);
        Assert.Equal(Full, ready.Text);
        Assert.Equal(2, Volatile.Read(ref requests));
    }

    /// <summary>
    /// J03 / F05.2 through the production path: after a collapse cancels a streaming call, the vendor
    /// connection must close (a cancelled call must not keep network I/O and a broker stream running).
    /// </summary>
    [Fact]
    public async Task J03_collapsing_a_streaming_card_closes_the_vendor_connection()
    {
        string? staged = OpenAIPluginTests.StageHost();
        if (staged is null) return;
        using var firstPieceSent = new SemaphoreSlim(0);
        var clientGone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = HoldingServer(_ => { }, firstPieceSent, clientGone);
        using var rig = BuildOpenAI(staged, server);

        await rig.Session.SubmitAsync("你好，世界！", "zh-Hans", "en");
        await firstPieceSent.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await rig.WaitForAsync(c => c.State == CardState.Streaming && c.Text == "Hello, ", "streaming");
        await rig.Session.ToggleAsync("openai/translate");
        await rig.WaitForAsync(c => c.Collapsed && c.State != CardState.Streaming, "collapse");
        Assert.True(await clientGone.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken), "the vendor connection stayed open after the collapse");
        rig.Supervisor.TryGetCurrent(out var host);
        Assert.Equal(0, host!.Broker.ActiveStreams);
    }

    /// <summary>The same cancel at the PluginProvider seam, token cancelled as the first piece arrives.</summary>
    [Fact]
    public async Task Cancelling_a_PluginProvider_stream_closes_the_vendor_connection()
    {
        string? staged = OpenAIPluginTests.StageHost();
        if (staged is null) return;
        using var firstPieceSent = new SemaphoreSlim(0);
        var clientGone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = HoldingServer(_ => { }, firstPieceSent, clientGone);
        var package = TranslationPackages.Find(TranslationPackages.OpenAI)!;
        var options = OpenAIPluginTests.Options(staged, server.Origin);
        using var supervisor = new Supervisor<HostSession>(() =>
        {
            var s = HostSession.Start(options);
            s.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(s.Load(OpenAIPluginTests.PackageId, "plugins/openai").Ok);
            return s;
        }, SystemClock.Instance, TimeSpan.FromMinutes(10));
        var provider = new PluginProvider(OpenAIPluginTests.PackageId, "openai/translate", "OpenAI", package.Limits, supervisor, [server.Origin],
            OpenAIPluginTests.InstanceId, OpenAIPluginTests.Signer, ["apiKey"], new Dictionary<string, string> { ["baseUrl"] = server.Origin });
        using var cts = new CancellationTokenSource();
        var call = provider.TranslateAsync(new TranslateCall("hi", "en", "fr", "att-1", Config, TimeSpan.FromSeconds(30)), _ => { cts.Cancel(); return ValueTask.CompletedTask; }, cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.True(await clientGone.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken), "the vendor connection stayed open after the cancel");
        supervisor.TryGetCurrent(out var host);
        Assert.Equal(0, host!.Broker.ActiveStreams);
    }

    /// <summary>Control for the two skipped tests: HostSession.Cancel sent from inside the chunk callback
    /// (the same thread and moment) does close the vendor connection and release the broker stream.</summary>
    [Fact]
    public async Task HostSession_cancel_from_the_chunk_callback_closes_the_vendor_connection()
    {
        string? staged = OpenAIPluginTests.StageHost();
        if (staged is null) return;
        using var firstPieceSent = new SemaphoreSlim(0);
        var clientGone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = HoldingServer(_ => { }, firstPieceSent, clientGone);
        using var session = HostSession.Start(OpenAIPluginTests.Options(staged, server.Origin));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load(OpenAIPluginTests.PackageId, "plugins/openai").Ok);
            string requestJson = System.Text.Json.JsonSerializer.Serialize(new TranslateRequest("hi", "en", "fr"), ContractsJson.Default.TranslateRequest);
            (string? RequestId, int CallId) ids = default;
            var (requestId, callId, task) = session.Invoke(OpenAIPluginTests.PackageId, "translate", requestJson, jobId: "job-f06-cancel", origins: [server.Origin], secrets: ["apiKey"],
                configJson: System.Text.Json.JsonSerializer.Serialize(new { baseUrl = server.Origin }), instanceId: OpenAIPluginTests.InstanceId, signer: OpenAIPluginTests.Signer,
                onChunk: _ => { session.Cancel(OpenAIPluginTests.PackageId, ids.RequestId!, "job-f06-cancel", ids.CallId); return ValueTask.CompletedTask; });
            ids = (requestId, callId);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(IpcMessageType.Failed, envelope.Type);
            Assert.True(await clientGone.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken), "the vendor connection stayed open after the cancel");
            Assert.Equal(0, session.Broker.ActiveStreams);
        }
        finally { session.Shutdown(2000); }
    }

    /// <summary>
    /// TEST-PLAN J05, end to end: a 429 with Retry-After: 1 through the real OpenAI package and PluginProvider
    /// is retried once, after at least the vendor's delay, and the second answer is shown.
    /// </summary>
    [Fact]
    public async Task J05_a_429_with_Retry_After_is_retried_once_after_the_vendor_delay()
    {
        string? staged = OpenAIPluginTests.StageHost();
        if (staged is null) return;
        var times = new ConcurrentQueue<long>();
        int requests = 0;
        using var server = new LoopbackHttpServer(async (_, stream, ct) =>
        {
            times.Enqueue(Stopwatch.GetTimestamp());
            if (Interlocked.Increment(ref requests) == 1)
            {
                byte[] body = """{"error":{"code":"rate_limit_exceeded","message":"slow down"}}"""u8.ToArray();
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 429 Too Many Requests\r\nContent-Type: application/json\r\nRetry-After: 1\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), ct);
                await stream.WriteAsync(body, ct);
                return;
            }
            await LoopbackHttpServer.WriteSseHeadAsync(stream, ct);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(OpenAIPluginTests.Sse("Hello, ", "world", "!")), ct);
        });
        using var rig = BuildOpenAI(staged, server);

        await rig.Session.SubmitAsync("你好，世界！", "zh-Hans", "en");
        var card = await rig.WaitForAsync(c => c.State is CardState.Ready or CardState.Failed, "J05 429");
        Assert.Equal(CardState.Ready, card.State);
        Assert.Equal(Full, card.Text);
        long[] t = [.. times];
        Assert.Equal(2, t.Length);
        Assert.True(Stopwatch.GetElapsedTime(t[0], t[1]) >= TimeSpan.FromMilliseconds(950), $"retried after {Stopwatch.GetElapsedTime(t[0], t[1]).TotalMilliseconds} ms");
    }

    /// <summary>J05: a Retry-After beyond the 60 s cap is not waited for; the card fails rate_limited after one request.</summary>
    [Fact]
    public async Task J05_a_Retry_After_over_60_seconds_is_not_retried()
    {
        string? staged = OpenAIPluginTests.StageHost();
        if (staged is null) return;
        int requests = 0;
        using var server = new LoopbackHttpServer(req =>
        {
            Interlocked.Increment(ref requests);
            var response = LoopbackHttpResponse.Json(429, """{"error":{"code":"rate_limit_exceeded","message":"slow down"}}""");
            return response with { Headers = new Dictionary<string, string>(response.Headers ?? new Dictionary<string, string>()) { ["Retry-After"] = "120" } };
        });
        using var rig = BuildOpenAI(staged, server);

        await rig.Session.SubmitAsync("你好", "zh-Hans", "en");
        var card = await rig.WaitForAsync(c => c.State is CardState.Ready or CardState.Failed, "J05 cap");
        Assert.Equal(CardState.Failed, card.State);
        Assert.Equal(ErrorKind.RateLimited, card.Error);
        await rig.Session.IdleAsync();
        Assert.Equal(1, Volatile.Read(ref requests));
    }

    /// <summary>J05: a 200 whose body is not an OpenAI stream is bad_response, and a structural error is not retried.</summary>
    [Theory]
    [InlineData("{\"id\":\"x\",\"object\":\"chat.completion\"}")]
    [InlineData("<html><body>gateway</body></html>")]
    [InlineData("data: {not json\n\ndata: [DONE]\n\n")]
    public async Task J05_a_malformed_OpenAI_response_is_bad_response_and_not_retried(string body)
    {
        string? staged = OpenAIPluginTests.StageHost();
        if (staged is null) return;
        int requests = 0;
        using var server = new LoopbackHttpServer(async (_, stream, ct) =>
        {
            Interlocked.Increment(ref requests);
            await LoopbackHttpServer.WriteSseHeadAsync(stream, ct);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(body), ct);
        });
        using var rig = BuildOpenAI(staged, server);

        await rig.Session.SubmitAsync("你好", "zh-Hans", "en");
        var card = await rig.WaitForAsync(c => c.State is CardState.Ready or CardState.Failed, "malformed");
        Assert.Equal(CardState.Failed, card.State);
        Assert.Equal(ErrorKind.BadResponse, card.Error);
        await rig.Session.IdleAsync();
        Assert.Equal(1, Volatile.Read(ref requests));
    }

    /// <summary>
    /// Residual risk from the preparation pass: the vendor ends the HTTP body cleanly (terminating zero-length
    /// chunk) after part of the answer but without the SSE "data: [DONE]" terminator. OpenAI always sends
    /// [DONE] at the end of a complete stream, so this is a truncated answer; the card must not end Ready
    /// with the truncated text as if it were the whole translation.
    /// </summary>
    [Fact]
    public async Task A_stream_that_ends_cleanly_without_DONE_is_not_shown_as_a_complete_answer()
    {
        string? staged = OpenAIPluginTests.StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(async (_, stream, ct) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n"), ct);
            await WriteChunkAsync(stream, Events("Hello, ", "wor"), ct);
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), ct); // clean end of body, no [DONE]
        });
        using var rig = BuildOpenAI(staged, server);

        await rig.Session.SubmitAsync("你好，世界！", "zh-Hans", "en");
        var card = await rig.WaitForAsync(c => c.State is CardState.Ready or CardState.Failed, "clean close");
        Assert.False(card.State == CardState.Ready && card.Text == "Hello, wor", "a truncated stream was shown as a complete answer");
    }
}
