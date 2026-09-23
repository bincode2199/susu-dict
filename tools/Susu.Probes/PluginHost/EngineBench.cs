using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Susu.Windows;

namespace Susu.Probes.PluginHost;

/// <summary>
/// PER01 for the in-process engine routes: identical plugin source, IPC, broker and samples.
/// The child executable decides the engine (QuickJS probe or the Jint comparison build).
/// </summary>
internal static partial class EngineBench
{
    internal sealed record Stat(int Samples, double P50, double P95, double Max, double Min, double Mean);
    internal sealed record Memory(int Plugins, double PrivateWorkingSetMiB, double PrivateBytesMiB, double PeakPrivateBytesMiB, double EngineHeapMiB);
    internal sealed record Report(
        DateTimeOffset Timestamp, string Engine, string Executable, string Scope, int Samples,
        double ProfileCreateMs, Stat ColdStartToReady, Stat Launch, Stat Connect, Stat Hello, Stat Load,
        Stat RoundTrip, Stat CancelAwaitingHost, Stat SpinToFailure, Stat SpinRebuild, Stat MicrotaskToFailure,
        Memory[] Memory, string[] Notes);

    public static Stat Summarize(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        double Percentile(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new Stat(sorted.Length, Round(Percentile(0.5)), Round(Percentile(0.95)), Round(sorted[^1]), Round(sorted[0]), Round(sorted.Average()));
    }

    private static double Round(double v) => Math.Round(v, 3);

    public static int Run(string engine, string childExecutable, string evidencePath, int samples)
    {
        string staged = XMatrix.Stage($"bench-{engine}");
        string exe;
        if (childExecutable == "self") exe = Path.Combine(staged, Path.GetFileName(Environment.ProcessPath!));
        else
        {
            // Stage the comparison executable and its native DLLs next to the same plugins.
            foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(childExecutable))!))
                if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    File.Copy(file, Path.Combine(staged, Path.GetFileName(file)), true);
            exe = Path.Combine(staged, Path.GetFileName(childExecutable));
        }
        for (int i = 1; i <= 21; i++)
        {
            string target = Path.Combine(staged, "plugins", $"p{i:00}");
            Directory.CreateDirectory(Path.Combine(target, "lib"));
            File.Copy(Path.Combine(staged, "plugins", "bench", "main.js"), Path.Combine(target, "main.js"));
            File.Copy(Path.Combine(staged, "plugins", "bench", "lib", "shape.js"), Path.Combine(target, "lib", "shape.js"));
        }
        string profile = $"Susu.F00.Bench.{engine}.{Guid.NewGuid():N}";
        var notes = new List<string>
        {
            "Network responses are a synthetic in-process broker fixture; round trips measure IPC + engine + broker, not the Internet.",
            "Cold start reuses one pre-created AppContainer profile (production creates it once at first run); profile creation is reported separately.",
        };
        var timer = Stopwatch.StartNew();
        var created = ContainerHost.Open(profile, staged, allowExisting: false);
        double profileMs = timer.Elapsed.TotalMilliseconds;
        created.Close(deleteProfile: false);
        List<double> cold = [], launch = [], connect = [], hello = [], load = [], roundTrip = [], cancel = [], spin = [], rebuild = [], microtask = [];
        var memory = new List<Memory>();
        try
        {
            for (int i = 0; i < samples + 2; i++)
            {
                timer.Restart();
                using var session = HostSession.Start(new HostSession.Options(exe, staged, engine, ProfileName: profile, KeepProfile: true));
                var loaded = session.Load("bench", "plugins/bench");
                double total = timer.Elapsed.TotalMilliseconds;
                if (!loaded.Ok) throw new InvalidOperationException($"Load failed: {loaded.Error}");
                session.Shutdown();
                if (i < 2) continue; // discard first two (file cache warm-up), recorded in notes
                cold.Add(total); launch.Add(session.Timings.LaunchMs); connect.Add(session.Timings.ConnectMs - session.Timings.LaunchMs);
                hello.Add(session.Timings.HelloMs - session.Timings.ConnectMs); load.Add(loaded.Milliseconds);
            }
            notes.Add("Cold start discards two warm-up launches; all later samples are separate processes.");

            using (var session = HostSession.Start(new HostSession.Options(exe, staged, engine, ProfileName: profile, KeepProfile: true)))
            {
                if (!session.Load("bench", "plugins/bench").Ok) throw new InvalidOperationException("bench load failed");
                string[] origins = ["https://api.bench.example"];
                for (int i = 0; i < samples + 5; i++)
                {
                    timer.Restart();
                    var call = session.Invoke("bench", "translate", $"{{\"text\":\"sample {i} 中文\"}}", origins);
                    var frame = Wait(call.Result, 5000);
                    double ms = timer.Elapsed.TotalMilliseconds;
                    if (frame.Type != FrameTypes.Completed) throw new InvalidOperationException($"translate failed: {frame.Payload}");
                    if (i >= 5) roundTrip.Add(ms);
                }
                for (int i = 0; i < samples; i++)
                {
                    var call = session.Invoke("bench", "wait", "{}", origins);
                    Thread.Sleep(20);
                    timer.Restart();
                    session.Cancel("bench", call.RequestId, call.CallId);
                    var frame = Wait(call.Result, 5000);
                    cancel.Add(timer.Elapsed.TotalMilliseconds);
                    if (Kind(frame) != "cancelled") throw new InvalidOperationException($"wait cancel returned {Kind(frame)}");
                }
                foreach (var (capability, list) in new[] { ("spin", spin), ("microtasks", microtask) })
                {
                    for (int i = 0; i < samples; i++)
                    {
                        while (session.Events.TryDequeue(out _)) { }
                        timer.Restart();
                        var call = session.Invoke("bench", capability, "{}", origins);
                        var frame = Wait(call.Result, 10000);
                        list.Add(timer.Elapsed.TotalMilliseconds);
                        if (Kind(frame) != "timeout") throw new InvalidOperationException($"{capability} returned {Kind(frame)}");
                        var fault = WaitFault(session);
                        if (capability == "spin") rebuild.Add(fault.RebuildMilliseconds);
                        // runtime must be usable after rebuild
                        var check = session.Invoke("bench", "translate", "{\"text\":\"after\"}", origins);
                        if (Wait(check.Result, 5000).Type != FrameTypes.Completed) throw new InvalidOperationException("runtime unusable after rebuild");
                    }
                }
                session.Shutdown();
            }

            using (var session = HostSession.Start(new HostSession.Options(exe, staged, engine, ProfileName: profile, KeepProfile: true)))
            {
                memory.Add(Measure(session, 0));
                session.Load("bench", "plugins/bench");
                Exercise(session, "bench");
                memory.Add(Measure(session, 1));
                for (int i = 1; i <= 21; i++)
                {
                    var loaded = session.Load($"p{i:00}", $"plugins/p{i:00}");
                    if (!loaded.Ok) throw new InvalidOperationException($"p{i:00}: {loaded.Error}");
                    Exercise(session, $"p{i:00}");
                }
                Thread.Sleep(1000);
                memory.Add(Measure(session, 22));
                session.Shutdown();
            }
            notes.Add("Memory: bench plus 21 copies of the same synthetic plugin, each invoked once; real plugins are larger and are measured again in F19.");
        }
        finally
        {
            var cleanup = ContainerHost.Open(profile, staged, allowExisting: true);
            cleanup.Close(deleteProfile: true);
        }
        var report = new Report(DateTimeOffset.UtcNow, engine, Path.GetFileName(exe), "F00 PER01 engine route comparison; synthetic plugin and broker fixture; AppContainer child; NativeAOT unless stated", samples,
            Round(profileMs), Summarize(cold), Summarize(launch), Summarize(connect), Summarize(hello), Summarize(load), Summarize(roundTrip), Summarize(cancel),
            Summarize(spin), Summarize(rebuild), Summarize(microtask), [.. memory], [.. notes]);
        File.WriteAllText(evidencePath, JsonSerializer.Serialize(report, BenchJson.Default.Report));
        Console.WriteLine(JsonSerializer.Serialize(report, BenchJson.Default.Report));
        return 0;
    }

    private static void Exercise(HostSession session, string plugin)
    {
        var call = session.Invoke(plugin, "translate", "{\"text\":\"warm\"}", ["https://api.bench.example"]);
        if (Wait(call.Result, 5000).Type != FrameTypes.Completed) throw new InvalidOperationException($"{plugin} translate failed");
    }

    private static Memory Measure(HostSession session, int plugins)
    {
        var (pws, privateBytes, peak) = ContainerHost.ProcessMemory(session.Process);
        var stats = session.Stats();
        return new Memory(plugins, Round(pws / 1048576.0), Round(privateBytes / 1048576.0), Round(peak / 1048576.0), Round(stats.EngineBytes / 1048576.0));
    }

    private static Frame Wait(Task<Frame> task, int ms, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0) => task.Wait(ms) ? task.Result : throw new TimeoutException($"Call did not finish (EngineBench.cs line {line}).");

    private static FaultPayload WaitFault(HostSession session)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 5000)
        {
            while (session.Events.TryDequeue(out var frame))
                if (frame.Type == FrameTypes.RuntimeFault) return frame.Payload!.Value.Deserialize(PluginJson.Default.FaultPayload)!;
            Thread.Sleep(5);
        }
        throw new TimeoutException("No RuntimeFault event.");
    }

    private static string Kind(Frame frame)
        => frame.Payload?.Deserialize(PluginJson.Default.CompletedPayload)?.Error?.Kind ?? (frame.Type == FrameTypes.Completed ? "completed" : "unknown");

    /// <summary>In-process calibration of the 100 ms slice against the largest legal JSON (4 MiB).</summary>
    public static int Calibrate(string evidencePath)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "plugins", "bench");
        var results = new List<string>();
        foreach (int kib in new[] { 256, 1024, 4096 })
        {
            // Size by UTF-8 bytes: the bridge and PLAN 4.5.1 limit normal JSON to 4 MiB encoded.
            var items = new StringBuilder("{\"items\":[");
            int count = 0, bytes = 12;
            const int itemOverhead = 64;
            while (bytes + itemOverhead < kib * 1024)
            {
                string item = $"{(count++ > 0 ? "," : "")}{{\"id\":\"{count}\",\"text\":\"Synthetic calibration sentence 中文 {count}\"}}";
                items.Append(item);
                bytes += Encoding.UTF8.GetByteCount(item);
            }
            items.Append("]}");
            string json = items.ToString();
            var times = new List<double>();
            var parseOnly = new List<double>();
            var jsOnly = new List<double>();
            for (int i = 0; i < 10; i++)
            {
                foreach (var (capability, list) in new[] { ("parse", times), ("noop", parseOnly) })
                {
                    using var budget = new ExecutionBudget { SliceTicks = Stopwatch.Frequency * 10 };
                    var sink = new CalibrationSink();
                    using var runtime = QuickJsRuntime.Create("bench", root, 256, budget, sink);
                    if (runtime.Load("main.js", out _) is { } error) throw new InvalidOperationException(error);
                    var timer = Stopwatch.StartNew();
                    int status = runtime.Invoke(1, capability, json, "{}");
                    list.Add(timer.Elapsed.TotalMilliseconds);
                    if (capability == "parse") jsOnly.Add((Stopwatch.GetTimestamp() - budget.LastStart) * 1000.0 / Stopwatch.Frequency);
                    if (status != 0 || sink.Last is null || !sink.Last.Contains("\"ok\":true", StringComparison.Ordinal)) throw new InvalidOperationException($"calibration call failed: status={status}; {sink.Last}");
                }
            }
            var stat = Summarize(times);
            var materialize = Summarize(parseOnly);
            var js = Summarize(jsOnly);
            results.Add($"{{\"requestKiB\":{kib},\"bytes\":{Encoding.UTF8.GetByteCount(json)},\"totalP50\":{stat.P50},\"totalP95\":{stat.P95},\"totalMax\":{stat.Max},\"hostJsonMaterializeP50\":{materialize.P50},\"hostJsonMaterializeP95\":{materialize.P95},\"hostJsonMaterializeMax\":{materialize.Max},\"jsSliceP50\":{js.P50},\"jsSliceP95\":{js.P95},\"jsSliceMax\":{js.Max},\"totalWithinOld100ms\":{(stat.Max <= 100 ? "true" : "false")},\"jsWithin250msSlice\":{(js.Max <= 250 ? "true" : "false")}}}");
        }
        string output = $"{{\"engine\":\"quickjs\",\"scope\":\"total = host JSON materialization of the request + plugin loop over all items in one synchronous slice; noop capability isolates materialization; NativeAOT, fresh runtime per run, 10 runs each\",\"results\":[{string.Join(',', results)}]}}";
        File.WriteAllText(evidencePath, output);
        Console.WriteLine(output);
        return 0;
    }

    private sealed class CalibrationSink : IRuntimeCallbacks
    {
        public string? Last;
        public int ApiCall(string pluginId, int apiId, string json) => 1;
        public void Completed(string pluginId, string json) => Last = json;
        public void Log(string pluginId, string json) { }
    }

    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(Report))]
    internal partial class BenchJson : JsonSerializerContext;
}
