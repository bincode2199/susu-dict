using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Susu.Abstractions;

namespace Susu.Storage;

/// <summary>
/// Data directories (PLAN 5.1). Tests and development builds pass an explicit root so they never touch the
/// real user profile (F02.4); production uses %APPDATA%\Su-Su and %LOCALAPPDATA%\Su-Su.
/// </summary>
public sealed record AppPaths(string Roaming, string Local)
{
    public const string DataRootVariable = "SUSU_DATA_ROOT";

    public string Settings => Path.Combine(Roaming, "settings.yaml");
    public string Secrets => Path.Combine(Roaming, "secrets.dat");
    public string UserPlugins => Path.Combine(Roaming, "plugins");
    public string Database => Path.Combine(Local, "susu.db");
    public string Logs => Path.Combine(Local, "logs");
    public string Cache => Path.Combine(Local, "cache");
    public string WebView => Path.Combine(Local, "webview");
    public string Transactions => Path.Combine(Local, "transactions");
    public string Updates => Path.Combine(Local, "updates");

    /// <summary>
    /// ARCHITECTURE 8.1 "Pictures/Su-Su": copies kept only when the user enables "keep screenshots". Not created up front.
    /// Defaults to a Pictures folder beside <see cref="Local"/>'s parent so explicit test roots stay self-contained;
    /// <see cref="Resolve"/> points production at the user's Pictures folder.
    /// </summary>
    public string KeptScreenshots { get; init; } = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(Local))) ?? Local, "Pictures", "Su-Su");

    public static AppPaths UnderRoot(string root) => new(Path.Combine(root, "Roaming", "Su-Su"), Path.Combine(root, "Local", "Su-Su")) { KeptScreenshots = Path.Combine(root, "Pictures", "Su-Su") };

    /// <param name="overrideRoot">From <c>--data-root</c> or <see cref="DataRootVariable"/>.</param>
    /// <param name="development">Development builds default to a separate Su-Su-Dev folder instead of real user data.</param>
    public static AppPaths Resolve(string? overrideRoot, bool development)
    {
        if (!string.IsNullOrWhiteSpace(overrideRoot)) return UnderRoot(Path.GetFullPath(overrideRoot));
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (development) return UnderRoot(Path.Combine(local, "Su-Su-Dev"));
        return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Su-Su"), Path.Combine(local, "Su-Su"))
        {
            KeptScreenshots = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Su-Su"),
        };
    }

    public AppPaths EnsureCreated()
    {
        foreach (var dir in new[] { Roaming, Local, UserPlugins, Logs, Cache, WebView, Transactions, Updates }) Directory.CreateDirectory(dir);
        return this;
    }
}

public static class AtomicFile
{
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string HashOf(string path) => File.Exists(path) ? Hash(File.ReadAllBytes(path)) : "";

    /// <summary>Writes a sibling temp file, flushes it to disk, then atomically replaces the target, keeping the previous version as <c>.prev</c>.</summary>
    public static void Write(string path, ReadOnlySpan<byte> bytes, IFaultPoint? faults = null, string stage = "write")
    {
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            WriteFlushed(temp, bytes);
            faults?.Hit($"{stage}:flushed");
            if (File.Exists(path)) File.Replace(temp, path, $"{path}.prev", ignoreMetadataErrors: true);
            else File.Move(temp, path);
            faults?.Hit($"{stage}:replaced");
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static void WriteFlushed(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}

/// <summary>
/// Commits settings.yaml and secrets.dat together (ARCHITECTURE 8.1). Two renames are not one atomic
/// operation, so a journal records Prepared (new and old copies flushed in transactions/) before any
/// target is replaced and Committed afterwards. Startup recovery rolls back every file of a Prepared
/// transaction and cleans up a Committed one. Backups keep the original bytes, so secrets stay DPAPI ciphertext.
/// </summary>
public sealed class ConfigTransaction(string transactionsDirectory, IFaultPoint? faults = null)
{
    public sealed record Journal(string State, JournalFile[] Files);
    public sealed record JournalFile(string Target, bool HadOld);

    public void Commit(IReadOnlyList<(string Target, byte[] Bytes)> files)
    {
        Directory.CreateDirectory(transactionsDirectory);
        string dir = Path.Combine(transactionsDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        bool prepared = false;
        try
        {
            var entries = new JournalFile[files.Count];
            for (int i = 0; i < files.Count; i++)
            {
                AtomicFile.WriteFlushed(Path.Combine(dir, $"{i}.new"), files[i].Bytes);
                bool hadOld = File.Exists(files[i].Target);
                if (hadOld) AtomicFile.WriteFlushed(Path.Combine(dir, $"{i}.old"), File.ReadAllBytes(files[i].Target));
                entries[i] = new JournalFile(files[i].Target, hadOld);
                faults?.Hit($"stage:{i}");
            }
            WriteJournal(dir, new Journal("Prepared", entries));
            prepared = true;
            faults?.Hit("prepared");
            for (int i = 0; i < files.Count; i++)
            {
                AtomicFile.Write(files[i].Target, File.ReadAllBytes(Path.Combine(dir, $"{i}.new")), faults, $"replace:{i}");
            }
            WriteJournal(dir, new Journal("Committed", entries));
            faults?.Hit("committed");
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // A real I/O failure (disk full, sharing violation) in this process: undo now rather than at next start.
            if (prepared) RecoverOne(dir);
            else TryDelete(dir);
            throw;
        }
    }

    /// <summary>Run at startup before settings/secrets are loaded. Returns how many transactions were rolled back.</summary>
    public int Recover()
    {
        if (!Directory.Exists(transactionsDirectory)) return 0;
        int rolledBack = 0;
        foreach (var dir in Directory.GetDirectories(transactionsDirectory))
            if (RecoverOne(dir)) rolledBack++;
        return rolledBack;
    }

    private static bool RecoverOne(string dir)
    {
        string journalPath = Path.Combine(dir, "journal.json");
        Journal? journal = null;
        if (File.Exists(journalPath))
        {
            try { journal = JsonSerializer.Deserialize(File.ReadAllBytes(journalPath), StorageJson.Default.Journal); }
            catch (JsonException) { journal = null; }
        }
        bool rolledBack = false;
        if (journal?.State == "Prepared")
        {
            for (int i = 0; i < journal.Files.Length; i++)
            {
                var file = journal.Files[i];
                if (file.HadOld) AtomicFile.Write(file.Target, File.ReadAllBytes(Path.Combine(dir, $"{i}.old")));
                else if (File.Exists(file.Target)) File.Delete(file.Target);
            }
            rolledBack = true;
        }
        // No journal: nothing was replaced yet. Committed: only cleanup is left.
        TryDelete(dir);
        return rolledBack;
    }

    private static void WriteJournal(string dir, Journal journal)
    {
        string path = Path.Combine(dir, "journal.json");
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(journal, StorageJson.Default.Journal));
        if (File.Exists($"{path}.prev")) File.Delete($"{path}.prev");
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConfigTransaction.Journal))]
[JsonSerializable(typeof(SecretFile))]
internal sealed partial class StorageJson : JsonSerializerContext;
