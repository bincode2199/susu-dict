using Susu.Windows;

namespace Susu.Probes.PluginHost;

/// <summary>
/// PER02 whole-tree workload: the sandboxed plugin host with N synthetic plugin runtimes loaded and
/// exercised, plus the eight-window UI fixture (keep-warm, then release). tools/measure-windows.ps1
/// samples every process under this one (plugin host and WebView2 processes included).
/// </summary>
internal static class FullWorkload
{
    public static int Run(string uiDist, int plugins, int memoryMode, bool fullDuration)
    {
        string staged = XMatrix.Stage("measure-full");
        for (int i = 1; i <= plugins; i++)
        {
            string target = Path.Combine(staged, "plugins", $"p{i:00}");
            Directory.CreateDirectory(Path.Combine(target, "lib"));
            File.Copy(Path.Combine(staged, "plugins", "bench", "main.js"), Path.Combine(target, "main.js"));
            File.Copy(Path.Combine(staged, "plugins", "bench", "lib", "shape.js"), Path.Combine(target, "lib", "shape.js"));
        }
        string exe = Path.Combine(staged, Path.GetFileName(Environment.ProcessPath!));
        using var session = HostSession.Start(new HostSession.Options(exe, staged, "quickjs"));
        for (int i = 1; i <= plugins; i++)
        {
            var loaded = session.Load($"p{i:00}", $"plugins/p{i:00}");
            if (!loaded.Ok) throw new InvalidOperationException($"p{i:00}: {loaded.Error}");
            var call = session.Invoke($"p{i:00}", "translate", "{\"text\":\"warm\"}", ["https://api.bench.example"]);
            if (!call.Result.Wait(5000) || call.Result.Result.Type != FrameTypes.Completed) throw new InvalidOperationException($"p{i:00} call failed");
        }
        Console.WriteLine($"{{\"event\":\"plugin-host-ready\",\"pid\":{session.ChildPid},\"plugins\":{plugins},\"tick\":{Environment.TickCount64}}}");
        Console.Out.Flush();
        string data = Path.GetFullPath(Path.Combine("artifacts", "probe-data", "windows", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(data);
        ExtendedPlatformProbes.MeasureWindows(Path.GetFullPath(uiDist), data, fullDuration, memoryMode);
        bool alive = !session.Process.IsInvalid && !new ProcessWaitHandle(session.Process).WaitOne(0);
        Console.WriteLine($"{{\"event\":\"plugin-host-alive-at-end\",\"value\":{(alive ? "true" : "false")},\"tick\":{Environment.TickCount64}}}");
        session.Shutdown();
        return alive ? 0 : 1;
    }
}
