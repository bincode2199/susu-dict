using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace Susu.Storage;

/// <summary>A temporary media file owned by one or more calls; the id is unguessable and never a path.</summary>
public sealed class FileLease
{
    internal FileLease(string id, string path, string purpose) { Id = id; FilePath = path; Purpose = purpose; }
    public string Id { get; }
    public string Purpose { get; }
    internal string FilePath { get; }
    internal int References;
}

/// <summary>
/// Lease index for cache/ (ARCHITECTURE 8.4): a file is deleted when its last lease is released, the whole
/// session folder on exit, and folders of earlier sessions whose owner process is gone at startup. Active
/// leases are never deleted. Only files inside this cache folder are ever touched.
/// </summary>
public sealed class FileLeases : IDisposable
{
    private readonly string sessionDirectory;
    private readonly string cacheDirectory;
    private readonly Dictionary<string, FileLease> leases = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public FileLeases(string cacheDirectory)
    {
        this.cacheDirectory = Path.GetFullPath(cacheDirectory);
        using var self = Process.GetCurrentProcess();
        SessionId = $"{Environment.ProcessId}-{self.StartTime.ToUniversalTime().Ticks}";
        sessionDirectory = Path.Combine(this.cacheDirectory, SessionId);
        Directory.CreateDirectory(sessionDirectory);
    }

    public string SessionId { get; }
    public int ActiveCount { get { lock (gate) return leases.Count; } }

    public FileLease Create(string purpose, string extension)
    {
        if (extension.Length is 0 or > 8 || !extension.All(char.IsAsciiLetterOrDigit)) throw new ArgumentException("extension must be 1-8 ASCII letters/digits");
        string id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var lease = new FileLease(id, Path.Combine(sessionDirectory, $"{id}.{extension}"), purpose) { References = 1 };
        lock (gate) leases[id] = lease;
        return lease;
    }

    /// <summary>Returns the path only to host code that already holds the lease (never exposed to plugins or UI).</summary>
    public string PathOf(FileLease lease) { lock (gate) return leases.ContainsKey(lease.Id) ? lease.FilePath : throw new ObjectDisposedException(nameof(FileLease)); }

    public FileLease? AddReference(string id)
    {
        lock (gate)
        {
            if (!leases.TryGetValue(id, out var lease)) return null;
            lease.References++;
            return lease;
        }
    }

    public void Release(FileLease lease)
    {
        lock (gate)
        {
            if (!leases.TryGetValue(lease.Id, out var current) || --current.References > 0) return;
            leases.Remove(lease.Id);
        }
        TryDeleteFile(lease.FilePath);
    }

    /// <summary>Deletes cache folders left by sessions whose process no longer runs. Returns how many were removed.</summary>
    public int CleanupStaleSessions()
    {
        int removed = 0;
        foreach (var dir in Directory.GetDirectories(cacheDirectory))
        {
            string name = Path.GetFileName(dir);
            if (name == SessionId || IsAlive(name)) continue;
            try { Directory.Delete(dir, recursive: true); removed++; }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    private static bool IsAlive(string sessionName)
    {
        var parts = sessionName.Split('-');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime().Ticks == ticks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; } // exists but not inspectable: do not delete
    }

    /// <summary>The files in cache/ that a clear would remove: finished sessions of this app and files no lease holds. Active leases and live sessions are skipped.</summary>
    public (int Files, long Bytes) CacheUsage() => Sweep(delete: false);

    /// <summary>F17.2 data clean: deletes what <see cref="CacheUsage"/> reports. Only files inside the cache folder are touched.</summary>
    public (int Files, long Bytes) ClearCache() => Sweep(delete: true);

    private (int Files, long Bytes) Sweep(bool delete)
    {
        int files = 0; long bytes = 0;
        HashSet<string> held;
        lock (gate) held = new(leases.Values.Select(l => Path.GetFullPath(l.FilePath)), StringComparer.OrdinalIgnoreCase);
        void Take(FileInfo f)
        {
            if (held.Contains(f.FullName)) return;
            long length = f.Length;
            if (delete) { try { f.Delete(); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; } }
            files++; bytes += length;
        }
        var root = new DirectoryInfo(cacheDirectory);
        if (!root.Exists) return (0, 0);
        foreach (var f in root.GetFiles()) Take(f);
        foreach (var dir in root.GetDirectories())
        {
            bool own = dir.Name == SessionId;
            if (!own && IsAlive(dir.Name)) continue;
            foreach (var f in dir.GetFiles("*", SearchOption.AllDirectories)) Take(f);
            if (delete && !own) { try { dir.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        return (files, bytes);
    }

    public void Dispose()
    {
        lock (gate) leases.Clear();
        try { Directory.Delete(sessionDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// One owner's reference on a leased audio file (F10.1): <see cref="Dispose"/> releases it exactly once, so the file
/// goes when the last owner (player, cache) lets go (ARCHITECTURE 8.4).
/// </summary>
public sealed class LeasedAudioClip(FileLeases leases, FileLease lease, string mime) : Susu.Abstractions.IShareableAudioClip
{
    private int disposed;

    /// <summary>Another owner on the same file (the TTS cache keeps one, F10.3); null once this owner released it or the file is gone.</summary>
    public Susu.Abstractions.IAudioClip? Share()
        => Released || leases.AddReference(Lease.Id) is not { } shared ? null : new LeasedAudioClip(leases, shared, Mime);

    public FileLease Lease { get; } = lease;
    public string Mime { get; } = mime;
    public string FilePath => leases.PathOf(Lease);
    public long Bytes { get { try { return new FileInfo(FilePath).Length; } catch (Exception e) when (e is IOException or ObjectDisposedException) { return 0; } } }
    public bool Released => Volatile.Read(ref disposed) != 0;

    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) leases.Release(Lease); }
}

/// <summary>One owner's reference on a leased file (F11.1 screenshots): disposing releases it exactly once.</summary>
public sealed class LeasedFile(FileLeases leases, FileLease lease, string mime) : Susu.Abstractions.ILeasedFile
{
    private int disposed;
    public FileLease Lease { get; } = lease;
    public string LeaseId => Lease.Id;
    public string Mime { get; } = mime;
    public string FilePath => leases.PathOf(Lease);
    public long Bytes { get { try { return new FileInfo(FilePath).Length; } catch (Exception e) when (e is IOException or ObjectDisposedException) { return 0; } } }
    public bool Released => Volatile.Read(ref disposed) != 0;
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) leases.Release(Lease); }
}

/// <summary>Empty leased files in the session cache folder (ARCHITECTURE 8.4 "临时 OCR … lease 到零删除").</summary>
public sealed class LeasedFiles(FileLeases leases) : Susu.Abstractions.ILeasedFileFactory
{
    public Susu.Abstractions.ILeasedFile Create(string purpose, string mime, string extension) => new LeasedFile(leases, leases.Create(purpose, extension), mime);
}

/// <summary>Empty leased files for host-produced audio (SAPI synthesis).</summary>
public sealed class LeasedAudioFiles(FileLeases leases, string purpose = "tts") : Susu.Abstractions.IAudioFileFactory
{
    public Susu.Abstractions.IAudioClip Create(string mime, string extension) => new LeasedAudioClip(leases, leases.Create(purpose, extension), mime);
}
