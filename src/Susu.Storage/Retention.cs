using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Susu.Abstractions;

namespace Susu.Storage;

/// <summary>One file this app wrote into a user-visible folder, as recorded in its index.</summary>
public sealed record IndexedFile(string Name, long Bytes, string Sha256, DateTimeOffset CreatedUtc);

/// <summary>Age and optional size bounds for an indexed folder (ARCHITECTURE 8.4).</summary>
public sealed record RetentionRules(TimeSpan MaxAge, long? MaxBytes = null)
{
    /// <summary>ARCHITECTURE 8.4 "保留截图副本 … 默认保留 7 天" (PLAN 6.2 "截图按设置保留 N 天（默认 7 天）"); no size bound in the spec.</summary>
    public static RetentionRules KeptScreenshots(int days = 7) => new(TimeSpan.FromDays(Math.Clamp(days, 1, 3650)));
}

/// <summary>Deleted: expired files removed. Forgotten: index entries whose file was already gone. InUse: expired but held by a task.
/// Changed: expired entries whose file no longer matches what this app wrote (replaced by the user): dropped from the index, file kept.</summary>
public sealed record CleanupReport(int Deleted, int Forgotten, int InUse, int Changed);

/// <summary>
/// Files the app writes into a folder it shares with the user (Pictures/Su-Su), tracked by an index kept in LocalAppData
/// (TEST-PLAN DATA08 "只删本应用非活动数据", OCR03 "只清本应用索引文件", ARCHITECTURE 8.4 "只删带本应用索引的文件，不扫用户其他图片").
///
/// <para>Cleanup never lists the folder: it walks the index only. An entry is deleted only when it is past the rules, not held
/// by a running task (<see cref="Hold"/>), and its file still has the size and SHA-256 recorded when it was written; a file
/// the user replaced is left alone and forgotten. Index names are bare file names; anything with a path is ignored, so a
/// damaged index cannot point outside the folder. A write failure (disk full) removes the partial file, leaves the index
/// unchanged and propagates, so callers report a real error.</para>
/// </summary>
public sealed class IndexedFileStore
{
    private readonly string directory;
    private readonly string indexPath;
    private readonly Action<string, ReadOnlyMemory<byte>> writeFile;
    private readonly object gate = new();
    private readonly Dictionary<string, int> holds = new(StringComparer.OrdinalIgnoreCase);
    private List<IndexedFile>? entries;

    public IndexedFileStore(string directory, string indexPath, Action<string, ReadOnlyMemory<byte>>? writeFile = null)
    {
        this.directory = Path.GetFullPath(directory);
        this.indexPath = Path.GetFullPath(indexPath);
        this.writeFile = writeFile ?? ((path, bytes) => AtomicFile.WriteFlushed(path, bytes.Span));
    }

    public string Directory => directory;

    public IReadOnlyList<IndexedFile> Entries { get { lock (gate) return [.. Load()]; } }

    /// <summary>Writes a new file (never overwriting one) and records it. Returns the file name actually used.</summary>
    public string Add(string preferredName, ReadOnlySpan<byte> bytes, DateTimeOffset now)
    {
        if (!IsBareName(preferredName)) throw new ArgumentException("a bare file name is required", nameof(preferredName));
        byte[] copy = bytes.ToArray();
        lock (gate)
        {
            var list = Load();
            System.IO.Directory.CreateDirectory(directory);
            string name = preferredName;
            for (int i = 2; File.Exists(Path.Combine(directory, name)) || list.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
                name = $"{Path.GetFileNameWithoutExtension(preferredName)}-{i.ToString(CultureInfo.InvariantCulture)}{Path.GetExtension(preferredName)}";
            string path = Path.Combine(directory, name);
            try { writeFile(path, copy); }
            catch { TryDelete(path); throw; }
            var updated = new List<IndexedFile>(list) { new(name, copy.LongLength, AtomicFile.Hash(copy), now.ToUniversalTime()) };
            try { Save(updated); }
            catch { TryDelete(path); throw; } // an unindexed file would never be cleaned up: do not leave one
            entries = updated;
            return name;
        }
    }

    /// <summary>Marks a file as in use by a task; cleanup skips it until every hold is disposed.</summary>
    public IDisposable Hold(string name)
    {
        lock (gate) holds[name] = holds.GetValueOrDefault(name) + 1;
        return new Release(this, name);
    }

    public CleanupReport Cleanup(DateTimeOffset now, RetentionRules rules)
    {
        lock (gate)
        {
            var list = Load();
            var keep = new List<IndexedFile>();
            int deleted = 0, forgotten = 0, inUse = 0, changed = 0;
            var cutoff = now.ToUniversalTime() - rules.MaxAge;
            var ordered = list.OrderBy(e => e.CreatedUtc).ToList();
            long total = ordered.Sum(e => e.Bytes);
            foreach (var entry in ordered)
            {
                bool expired = entry.CreatedUtc < cutoff || (rules.MaxBytes is { } max && total > max);
                if (!expired) { keep.Add(entry); continue; }
                string path = Path.Combine(directory, entry.Name);
                if (!File.Exists(path)) { forgotten++; total -= entry.Bytes; continue; }
                if (holds.GetValueOrDefault(entry.Name) > 0) { inUse++; keep.Add(entry); continue; }
                if (!Matches(path, entry)) { changed++; total -= entry.Bytes; continue; }
                try { File.Delete(path); deleted++; total -= entry.Bytes; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { inUse++; keep.Add(entry); } // open elsewhere: retry next time
            }
            if (keep.Count != list.Count)
            {
                Save(keep);
                entries = keep;
            }
            return new CleanupReport(deleted, forgotten, inUse, changed);
        }
    }

    private static bool Matches(string path, IndexedFile entry)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length != entry.Bytes) return false;
            return string.Equals(AtomicFile.Hash(File.ReadAllBytes(path)), entry.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private List<IndexedFile> Load()
    {
        if (entries is not null) return entries;
        List<IndexedFile> loaded = [];
        try
        {
            if (File.Exists(indexPath) && JsonSerializer.Deserialize(File.ReadAllBytes(indexPath), RetentionJson.Default.IndexFile) is { } file)
                loaded = [.. file.Files.Where(e => e is not null && IsBareName(e.Name) && e.Bytes >= 0 && e.Sha256 is { Length: 64 })];
        }
        catch (JsonException) { } // a damaged index means nothing is ours to delete; the folder is never scanned instead
        return entries = loaded;
    }

    private void Save(List<IndexedFile> list)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        AtomicFile.Write(indexPath, JsonSerializer.SerializeToUtf8Bytes(new IndexFile(1, [.. list]), RetentionJson.Default.IndexFile));
    }

    private static bool IsBareName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name == Path.GetFileName(name) && name is not ("." or "..") && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Release(IndexedFileStore store, string name) : IDisposable
    {
        private int done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) != 0) return;
            lock (store.gate)
            {
                if (store.holds.GetValueOrDefault(name) <= 1) store.holds.Remove(name);
                else store.holds[name]--;
            }
        }
    }

    internal sealed record IndexFile(int Version, IndexedFile[] Files);
}

/// <summary>
/// "Keep screenshots" copies (ARCHITECTURE 8.1 Pictures/Su-Su, 8.4; TEST-PLAN OCR03, DATA08). Written only when the capture
/// coordinator is told the user enabled it; each copy is indexed so retention deletes only this app's files, 7 days by default.
/// Disk-full and other write failures come back as <see cref="KeepResult.ErrorCode"/> (<c>capture.diskFull</c> /
/// <c>capture.writeFailed</c>), never as a kept file.
/// </summary>
public sealed class KeptScreenshots(IndexedFileStore store, Func<int>? retentionDays = null) : IScreenshotArchive
{
    public const string IndexFileName = "kept-screenshots.json";
    private readonly Func<int> retentionDays = retentionDays ?? (() => 7);

    public static KeptScreenshots For(AppPaths paths, Func<int>? retentionDays = null, Action<string, ReadOnlyMemory<byte>>? writeFile = null)
        => new(new IndexedFileStore(paths.KeptScreenshots, Path.Combine(paths.Local, IndexFileName), writeFile), retentionDays);

    public IndexedFileStore Store => store;

    public KeepResult Keep(ReadOnlySpan<byte> png, DateTimeOffset now)
    {
        string name = $"Su-Su {now.ToLocalTime().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.png";
        try { return new KeepResult(true, store.Add(name, png, now), null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new KeepResult(false, null, IsDiskFull(e) ? "capture.diskFull" : "capture.writeFailed");
        }
    }

    /// <summary>Startup/periodic cleanup with the configured retention (default 7 days).</summary>
    public CleanupReport Cleanup(DateTimeOffset now) => store.Cleanup(now, RetentionRules.KeptScreenshots(retentionDays()));

    public static bool IsDiskFull(Exception e) => e is IOException && (e.HResult & 0xFFFF) is 39 or 112;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IndexedFileStore.IndexFile))]
internal sealed partial class RetentionJson : JsonSerializerContext;
