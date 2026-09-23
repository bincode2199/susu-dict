using System.Diagnostics;

namespace Susu.Probes.PluginHost;

/// <summary>Callbacks from a plugin runtime into its plugin-host process (engine thread only).</summary>
internal interface IRuntimeCallbacks
{
    /// <summary>Returns 0 when the API request was forwarded; nonzero rejects the JS promise.</summary>
    int ApiCall(string pluginId, int apiId, string json);
    void Completed(string pluginId, string json);
    void Log(string pluginId, string json);
}

/// <summary>
/// One engine runtime per plugin package. All members are called on the single engine
/// thread. Status codes: 0 ok, 1 error, 2 execution budget exceeded (runtime must be rebuilt).
/// </summary>
internal interface IPluginRuntime : IDisposable
{
    string? Load(string entry, out int status);
    int Invoke(int callId, string capability, string requestJson, string configJson);
    int Settle(int apiId, bool ok, string json);
    int Abort(int callId);
    long EngineBytes { get; }
}

internal delegate IPluginRuntime RuntimeFactory(string pluginId, string root, int memoryMiB, ExecutionBudget budget, IRuntimeCallbacks callbacks);

/// <summary>
/// Continuous-execution budget shared by one runtime (ARCHITECTURE 4: initial 100 ms,
/// not reset per microtask). Readable from native interrupt callbacks.
/// </summary>
internal sealed unsafe class ExecutionBudget : IDisposable
{
    // [0] = deadline timestamp; [1] = GCHandle of the owning runtime (for native callbacks).
    public readonly long* Cell = (long*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(16);
    // F00 calibration (docs/evidence/F00/quickjs-budget-calibration.json): 100 ms is below the
    // plugin-side traversal of the largest legal 4 MiB JSON on the reference VM, so the slice is
    // 250 ms and starts after host JSON materialization (bounded by the 4 MiB cap).
    public long SliceTicks { get; set; } = Stopwatch.Frequency / 4;
    public long LastStart { get; private set; }
    public void Start(long ticks = 0) { LastStart = Stopwatch.GetTimestamp(); System.Threading.Volatile.Write(ref Cell[0], LastStart + (ticks > 0 ? ticks : SliceTicks)); }
    public void Stop() => System.Threading.Volatile.Write(ref Cell[0], long.MaxValue);
    public bool Expired => Stopwatch.GetTimestamp() >= System.Threading.Volatile.Read(ref Cell[0]);
    public void Dispose() => System.Runtime.InteropServices.NativeMemory.Free(Cell);
}

internal static class PluginFiles
{
    /// <summary>Resolves a module name inside the plugin root; null when outside or missing.</summary>
    public static string? Resolve(string root, string name)
    {
        if (name.Length == 0 || name.Contains('\\') || name.Contains(':') || Path.IsPathRooted(name)) return null;
        string full = Path.GetFullPath(Path.Combine(root, name));
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var info = new FileInfo(full);
        if (!info.Exists || info.Length > 4 * 1024 * 1024 || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
        return full;
    }
}
