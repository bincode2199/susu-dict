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
}
