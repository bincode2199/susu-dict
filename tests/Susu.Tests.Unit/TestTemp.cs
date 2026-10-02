using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;

[assembly: AssemblyFixture(typeof(Susu.Tests.Unit.TestTempCleanup))]

namespace Susu.Tests.Unit;

/// <summary>
/// Per-run temp folders for the real-sandbox staging copies (susu.exe plus DLLs, about 12 MB each).
/// Every folder made here is deleted when the test run ends (<see cref="TestTempCleanup"/>), and
/// folders left behind by an earlier run that crashed or was killed are swept at the start.
/// Deletion is best effort: a file still locked (a straggling child process) is retried briefly,
/// then left for the next run's sweep.
/// </summary>
internal static class TestTemp
{
    private static readonly ConcurrentBag<string> created = [];

    /// <summary>Creates %TEMP%\{prefix}-{guid} and registers it for deletion at the end of the run.</summary>
    public static string NewDir(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        created.Add(path);
        return path;
    }

    internal static void DeleteCreated()
    {
        while (created.TryTake(out string? path)) TryDelete(path);
    }

    /// <summary>Staging folders from an earlier run, older than a few hours (so a concurrent run in
    /// another worktree is never touched). Only the staging prefixes this project uses.</summary>
    internal static void SweepStale()
    {
        DateTime cutoff = DateTime.UtcNow - TimeSpan.FromHours(6);
        IEnumerable<DirectoryInfo> dirs;
        try { dirs = new DirectoryInfo(Path.GetTempPath()).EnumerateDirectories("susu-*"); }
        catch (IOException) { return; }
        foreach (var dir in dirs)
        {
            if (!IsStagingName(dir.Name)) continue;
            try { if (dir.CreationTimeUtc > cutoff) continue; } catch (IOException) { continue; }
            TryDelete(dir.FullName, attempts: 1);
        }
    }

    // "susu-<name>-it-<32 hex>" or "susu-f06-verify-<32 hex>": what NewDir callers produce.
    private static bool IsStagingName(string name)
    {
        int dash = name.LastIndexOf('-');
        if (dash < 0 || name.Length - dash - 1 != 32) return false;
        string stem = name[..dash];
        return stem.EndsWith("-it", StringComparison.Ordinal) || stem == "susu-f06-verify";
    }

    private static void TryDelete(string path, int attempts = 5)
    {
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (i + 1 < attempts) Thread.Sleep(200);
            }
        }
        Debug.WriteLine($"TestTemp: left {path} (still in use)");
    }
}

/// <summary>Assembly fixture: sweeps stale staging folders at start, deletes this run's at the end.
/// Also raises the thread pool's minimum: many tests block pool threads (sync polling, real-process
/// waits) while running in parallel, and the pool's slow thread injection then delays unrelated
/// timers and continuations by seconds - the source of timing flakes in otherwise correct tests.</summary>
public sealed class TestTempCleanup : IDisposable
{
    public TestTempCleanup()
    {
        ThreadPool.GetMinThreads(out int worker, out int io);
        ThreadPool.SetMinThreads(Math.Max(worker, 64), Math.Max(io, 64));
        TestTemp.SweepStale();
    }

    public void Dispose() => TestTemp.DeleteCreated();
}

/// <summary>Deadline-based polling for tests whose subject is ordering or eventual state, not speed:
/// the budget is generous so a loaded machine (real-process tests running in parallel) does not
/// turn a slow pass into a failure; a passing run still returns as soon as the condition holds.</summary>
internal static class Eventually
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        TimeSpan limit = timeout ?? DefaultTimeout;
        while (!condition())
        {
            if (sw.Elapsed > limit) return condition();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        return true;
    }

    public static bool Wait(Func<bool> condition, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        TimeSpan limit = timeout ?? DefaultTimeout;
        while (!condition())
        {
            if (sw.Elapsed > limit) return condition();
            Thread.Sleep(5);
        }
        return true;
    }
}
