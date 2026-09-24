using System.Text.Json;
using Susu.Plugins;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// End-to-end exercise of the real AppContainer sandbox + QuickJS engine (F04.1-F04.3), the same
/// route F00 validated as X01-X07 (54/54). Needs susu.exe already built next to this test assembly
/// (`dotnet build Susu.slnx` before `dotnet test`, as `tools/dev.ps1` does) and an interactive-capable
/// Windows session that can create AppContainer profiles; skips itself otherwise rather than failing
/// the suite on a host that cannot run it.
/// </summary>
public class PluginHostIntegrationTests
{
    private static string? FindHostBuildOutput()
    {
        // The AppContainer profile grants read+execute to exactly one directory, and a Debug
        // (framework-dependent) susu.exe cannot load the shared .NET runtime from outside it - so
        // this needs the NativeAOT self-contained publish (`dotnet publish -c Release -r win-x64`),
        // the same shape susu.exe ships in. tests/Susu.Tests.Unit/bin/<config>/net10.0 -> repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish");
            if (File.Exists(Path.Combine(candidate, "susu.exe"))) return candidate;
        }
        return null;
    }

    /// <summary>
    /// The AppContainer profile grants a scoped read ACL to one directory (ContainerHost.Open's
    /// resourceDirectory), so the plugin-host executable and every DLL it loads (.NET host + native
    /// bridges) must live inside it, alongside the plugin packages under a "plugins" subfolder - the
    /// same staging shape as the F00 X-matrix harness (tools/Susu.Probes/PluginHost/XMatrix.Stage).
    /// </summary>
    private static string? StageHost()
    {
        string? output = FindHostBuildOutput();
        if (output is null) return null;
        string staged = Path.Combine(Path.GetTempPath(), "susu-plugin-it-" + Guid.NewGuid().ToString("N"));
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
    public async Task Loads_a_plugin_and_invokes_a_capability_in_the_real_sandbox()
    {
        string? staged = StageHost();
        if (staged is null) return; // build susu.exe first; this test only runs when it is available
        string host = Path.Combine(staged, "susu.exe");

        using var session = HostSession.Start(new HostSession.Options(host, staged, "quickjs", KeepProfile: false));
        try
        {
            var loaded = session.Load("echo", "plugins/echo");
            Assert.True(loaded.Ok, loaded.Error);

            var (_, _, task) = session.Invoke("echo", "translate", "{\"text\":\"hi\"}", jobId: "job-1", origins: []);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(Susu.Contracts.IpcMessageType.Completed, envelope.Type);
        }
        finally
        {
            session.Shutdown(2000);
        }
    }

    [Fact]
    public async Task Exceeding_the_execution_budget_rebuilds_the_runtime_and_fails_in_flight_calls()
    {
        string? staged = StageHost();
        if (staged is null) return;
        string host = Path.Combine(staged, "susu.exe");

        using var session = HostSession.Start(new HostSession.Options(host, staged, "quickjs", KeepProfile: false));
        try
        {
            var loaded = session.Load("echo", "plugins/echo");
            Assert.True(loaded.Ok, loaded.Error);

            var (_, _, task) = session.Invoke("echo", "spin", "{}", jobId: "job-2", origins: []);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(Susu.Contracts.IpcMessageType.Failed, envelope.Type);

            // The runtime is rebuilt in place; a fresh call on the same plugin still works.
            var (_, _, retry) = session.Invoke("echo", "translate", "{\"text\":\"again\"}", jobId: "job-3", origins: []);
            var retryEnvelope = await retry.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(Susu.Contracts.IpcMessageType.Completed, retryEnvelope.Type);
        }
        finally
        {
            session.Shutdown(2000);
        }
    }

    /// <summary>
    /// F04.2 gap: the execution budget is a wall-clock deadline, not a per-top-level-call reset, so it
    /// must also interrupt a call that never runs a synchronous loop and instead re-schedules itself
    /// forever as microtasks (Promise.resolve().then(again)). Mirrors the synchronous-loop budget test
    /// above; a second package must stay unaffected, same as J06.
    /// </summary>
    [Fact]
    public async Task An_infinite_microtask_chain_also_trips_the_execution_budget_and_other_calls_survive()
    {
        string? staged = StageHost();
        if (staged is null) return;
        string host = Path.Combine(staged, "susu.exe");

        using var session = HostSession.Start(new HostSession.Options(host, staged, "quickjs", KeepProfile: false));
        try
        {
            Assert.True(session.Load("echo", "plugins/echo").Ok);
            Assert.True(session.Load("second", "plugins/second").Ok);

            var (_, _, spinTask) = session.Invoke("echo", "spinMicrotask", "{}", jobId: "job-spin-mt", origins: []);
            var (_, _, secondTask) = session.Invoke("second", "translate", "{\"text\":\"still-alive\"}", jobId: "job-second-mt", origins: []);

            var spinResult = await spinTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(Susu.Contracts.IpcMessageType.Failed, spinResult.Type); // interrupted despite never running a synchronous loop

            var secondResult = await secondTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(Susu.Contracts.IpcMessageType.Completed, secondResult.Type); // unrelated package unaffected

            // The rebuilt "echo" runtime still works for a fresh call afterwards.
            var (_, _, retry) = session.Invoke("echo", "translate", "{\"text\":\"again\"}", jobId: "job-retry-mt", origins: []);
            var retryEnvelope = await retry.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(Susu.Contracts.IpcMessageType.Completed, retryEnvelope.Type);
        }
        finally { session.Shutdown(2000); }
    }

    /// <summary>F04.2: the module lockdown (native/quickjs-bridge bootstrap) removes eval/Function/process/require.</summary>
    [Fact]
    public async Task The_restricted_Web_API_surface_has_no_dynamic_code_execution()
    {
        string? staged = StageHost();
        if (staged is null) return;
        string host = Path.Combine(staged, "susu.exe");

        using var session = HostSession.Start(new HostSession.Options(host, staged, "quickjs", KeepProfile: false));
        try
        {
            Assert.True(session.Load("echo", "plugins/echo").Ok);
            var (_, _, task) = session.Invoke("echo", "restricted", "{}", jobId: "job-r", origins: []);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(Susu.Contracts.IpcMessageType.Completed, envelope.Type);
            var completed = envelope.Payload!.Value.Deserialize(Susu.Contracts.ContractsJson.Default.CompletedPayload)!;
            var checks = completed.Result!.Value;
            Assert.True(checks.GetProperty("evalRemoved").GetBoolean());
            Assert.True(checks.GetProperty("functionRemoved").GetBoolean());
            Assert.True(checks.GetProperty("noProcess").GetBoolean());
            Assert.True(checks.GetProperty("noRequire").GetBoolean());
            Assert.True(checks.GetProperty("noFsImport").GetBoolean());
        }
        finally { session.Shutdown(2000); }
    }

    /// <summary>
    /// F04.2: cancelling one in-flight capability call must not disturb a different capability call
    /// concurrently in flight on the same plugin runtime (both suspended on a host round trip -
    /// PLAN 4.5.4 allows up to 2 in-flight calls per runtime, only the JS slice itself is serial).
    /// </summary>
    [Fact]
    public async Task Cancelling_one_in_flight_call_does_not_disturb_a_concurrent_call_on_the_same_plugin()
    {
        string? staged = StageHost();
        if (staged is null) return;
        string host = Path.Combine(staged, "susu.exe");

        using var session = HostSession.Start(new HostSession.Options(host, staged, "quickjs", KeepProfile: false));
        try
        {
            Assert.True(session.Load("echo", "plugins/echo").Ok);
            var (requestIdA, callIdA, taskA) = session.Invoke("echo", "slowA", "{}", jobId: "job-a", origins: []);
            var (_, _, taskB) = session.Invoke("echo", "slowB", "{}", jobId: "job-b", origins: []);
            session.Cancel("echo", requestIdA, "job-a", callIdA);

            var envelopeB = await taskB.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(Susu.Contracts.IpcMessageType.Completed, envelopeB.Type);
            var resultB = envelopeB.Payload!.Value.Deserialize(Susu.Contracts.ContractsJson.Default.CompletedPayload)!;
            Assert.Equal("B-done", resultB.Result!.Value.GetProperty("text").GetString());

            // A resolves one way or the other (cancelled, or it beat the Cancel message) but never hangs.
            var envelopeA = await taskA.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(envelopeA.Type is Susu.Contracts.IpcMessageType.Completed or Susu.Contracts.IpcMessageType.Failed);

            // The runtime is still healthy for further calls on either capability.
            var (_, _, taskAfter) = session.Invoke("echo", "translate", "{\"text\":\"z\"}", jobId: "job-after", origins: []);
            var envelopeAfter = await taskAfter.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(Susu.Contracts.IpcMessageType.Completed, envelopeAfter.Type);
        }
        finally { session.Shutdown(2000); }
    }

    /// <summary>F04.3/J04: a runtime already at its in-flight call limit gives explicit "busy" feedback, not a silent hang.</summary>
    [Fact]
    public async Task A_third_concurrent_call_on_one_runtime_is_rejected_as_busy()
    {
        string? staged = StageHost();
        if (staged is null) return;
        string host = Path.Combine(staged, "susu.exe");

        using var session = HostSession.Start(new HostSession.Options(host, staged, "quickjs", KeepProfile: false));
        try
        {
            Assert.True(session.Load("echo", "plugins/echo").Ok);
            var (_, _, taskA) = session.Invoke("echo", "slowA", "{}", jobId: "job-a", origins: []);
            var (_, _, taskB) = session.Invoke("echo", "slowB", "{}", jobId: "job-b", origins: []);
            // The runtime already has 2 in-flight calls (PLAN 4.5.4's per-runtime cap); a third is
            // refused immediately with an explicit Failed("busy"), not queued silently.
            var (_, _, taskC) = session.Invoke("echo", "translate", "{\"text\":\"c\"}", jobId: "job-c", origins: []);
            var envelopeC = await taskC.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(Susu.Contracts.IpcMessageType.Failed, envelopeC.Type);
            var resultC = envelopeC.Payload!.Value.Deserialize(Susu.Contracts.ContractsJson.Default.CompletedPayload)!;
            Assert.Equal("busy", resultC.Error!.Kind);

            await taskA.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await taskB.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally { session.Shutdown(2000); }
    }

    /// <summary>F04.2/J06: one package's execution-budget rebuild does not affect another package's runtime.</summary>
    [Fact]
    public async Task One_packages_budget_rebuild_does_not_affect_another_packages_runtime()
    {
        string? staged = StageHost();
        if (staged is null) return;
        string host = Path.Combine(staged, "susu.exe");

        using var session = HostSession.Start(new HostSession.Options(host, staged, "quickjs", KeepProfile: false));
        try
        {
            Assert.True(session.Load("echo", "plugins/echo").Ok);
            Assert.True(session.Load("second", "plugins/second").Ok);

            var (_, _, spinTask) = session.Invoke("echo", "spin", "{}", jobId: "job-spin", origins: []);
            var (_, _, secondTask) = session.Invoke("second", "translate", "{\"text\":\"still-alive\"}", jobId: "job-second", origins: []);

            var spinResult = await spinTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(Susu.Contracts.IpcMessageType.Failed, spinResult.Type); // "echo" rebuilt after its budget was exceeded

            var secondResult = await secondTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(Susu.Contracts.IpcMessageType.Completed, secondResult.Type); // "second" was never touched
            var payload = secondResult.Payload!.Value.Deserialize(Susu.Contracts.ContractsJson.Default.CompletedPayload)!;
            Assert.Equal("second:still-alive", payload.Result!.Value.GetProperty("text").GetString());
        }
        finally { session.Shutdown(2000); }
    }
}
