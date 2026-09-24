using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Susu.Runtime;

/// <summary>
/// Continuous-execution budget shared by one plugin runtime (ARCHITECTURE 4). The slice is not
/// reset per microtask, so an infinite microtask chain still trips the budget. Readable from a
/// native interrupt callback without any managed transition.
/// </summary>
public sealed unsafe class ExecutionBudget : IDisposable
{
    // [0] = deadline timestamp (Stopwatch ticks); [1] = GCHandle of the owning runtime, for native callbacks.
    public readonly long* Cell = (long*)NativeMemory.AllocZeroed(16);

    /// <summary>
    /// F00 calibration (docs/evidence/F00/quickjs-budget-calibration.json): 100 ms proved too small for the
    /// largest legal 4 MiB JSON request on the reference VM, so the slice is 250 ms and starts after host JSON
    /// materialization (itself bounded by the 4 MiB cap and excluded from the budget, since it is not interruptible).
    /// </summary>
    public long SliceTicks { get; set; } = Stopwatch.Frequency / 4;

    public long LastStart { get; private set; }

    public void Start(long ticks = 0)
    {
        LastStart = Stopwatch.GetTimestamp();
        Volatile.Write(ref Cell[0], LastStart + (ticks > 0 ? ticks : SliceTicks));
    }

    public void Stop() => Volatile.Write(ref Cell[0], long.MaxValue);

    public bool Expired => Stopwatch.GetTimestamp() >= Volatile.Read(ref Cell[0]);

    public void Dispose() => NativeMemory.Free(Cell);
}

/// <summary>Read-only plugin resource access: module lookups may never escape the package directory.</summary>
public static class PluginFiles
{
    /// <summary>Resolves a module name inside the plugin root; null when outside, missing, too large or a reparse point.</summary>
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
