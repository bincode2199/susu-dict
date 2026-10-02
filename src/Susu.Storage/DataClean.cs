using Susu.Abstractions;
using Susu.Domain;

namespace Susu.Storage;

/// <summary>
/// The data-clean entries of the About page (F17.2, TEST-PLAN DATA08 "只删本应用非活动数据"). Each kind reuses the storage layer that owns the data and nothing else:
/// <list type="bullet">
/// <item><b>caches</b>: <see cref="FileLeases"/> (finished sessions and unleased files in cache/; files a running task holds stay).</item>
/// <item><b>logs</b>: the rotated <c>susu-*.jsonl</c> files; the one being written is emptied, not removed.</item>
/// <item><b>screenshots</b>: the kept copies this app indexed (<see cref="IndexedFileStore"/>); a file the user replaced or that a task holds is left alone, and nothing else in the Pictures folder is listed.</item>
/// <item><b>favorites</b>: every favorite, its sync rows and the export records in the database (<see cref="FavoritesRepository.ClearAll"/>).</item>
/// <item><b>settings</b>: back to the defaults through the journaled <see cref="IConfigService"/>; accounts, grants, key values and instance account bindings are kept, so the keys are not lost by a reset.</item>
/// <item><b>accounts</b>: every account, grant, binding and saved key (including the proxy password), in one two-file transaction.</item>
/// </list>
/// User files outside the app's data folders, installed plugin packages, the backup restore point and the settings file itself are never deleted.
/// </summary>
public sealed class DataCleanService(AppPaths paths, IClock clock, IConfigService config, SecretStore secrets, FileLeases? leases, IndexedFileStore? screenshots, FavoritesRepository? favorites) : IDataCleanService
{
    private readonly object gate = new();

    public IReadOnlyList<DataCleanItem> Items()
    {
        var cache = leases?.CacheUsage() ?? (0, 0);
        var logs = LogFiles();
        var shots = screenshots?.Entries ?? [];
        var s = config.State.Effective;
        return
        [
            new(DataCleanKinds.Caches, cache.Item1, cache.Item2, leases is not null),
            new(DataCleanKinds.Logs, logs.Count, logs.Sum(f => f.Length), true),
            new(DataCleanKinds.Screenshots, shots.Count, shots.Sum(e => e.Bytes), screenshots is not null),
            new(DataCleanKinds.Favorites, favorites?.ActiveCount() ?? 0, 0, favorites is not null),
            new(DataCleanKinds.Settings, 0, 0, true),
            new(DataCleanKinds.Accounts, s.Accounts.Count, 0, true),
        ];
    }

    public DataCleanOutcome Clear(string kind)
    {
        lock (gate)
        {
            try
            {
                return kind switch
                {
                    DataCleanKinds.Caches => ClearCaches(),
                    DataCleanKinds.Logs => ClearLogs(),
                    DataCleanKinds.Screenshots => ClearScreenshots(),
                    DataCleanKinds.Favorites => ClearFavorites(),
                    DataCleanKinds.Settings => ResetSettings(),
                    DataCleanKinds.Accounts => DeleteAccounts(),
                    _ => new DataCleanOutcome(kind, false, "unavailable", 0, 0, 0),
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                return new DataCleanOutcome(kind, false, "failed", 0, 0, 0);
            }
        }
    }

    private DataCleanOutcome ClearCaches()
    {
        if (leases is null) return new(DataCleanKinds.Caches, false, "unavailable", 0, 0, 0);
        var (files, bytes) = leases.ClearCache();
        return new(DataCleanKinds.Caches, true, null, files, bytes, leases.ActiveCount);
    }

    private List<FileInfo> LogFiles() => Directory.Exists(paths.Logs) ? [.. new DirectoryInfo(paths.Logs).GetFiles("susu-*.jsonl")] : [];

    private DataCleanOutcome ClearLogs()
    {
        int removed = 0, skipped = 0; long bytes = 0;
        foreach (var file in LogFiles())
        {
            long length = file.Length;
            try
            {
                // The file being written stays open for append by the logger between writes only, so a delete normally works; if it is held, empty it instead.
                file.Delete();
                removed++; bytes += length;
            }
            catch (IOException)
            {
                try { using var stream = new FileStream(file.FullName, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); removed++; bytes += length; }
                catch (IOException) { skipped++; }
            }
            catch (UnauthorizedAccessException) { skipped++; }
        }
        return new(DataCleanKinds.Logs, true, null, removed, bytes, skipped);
    }

    private DataCleanOutcome ClearScreenshots()
    {
        if (screenshots is null) return new(DataCleanKinds.Screenshots, false, "unavailable", 0, 0, 0);
        var before = screenshots.Entries;
        // Everything the index lists is past a zero-length retention; the index walk (not a folder listing) decides what is deleted.
        var report = screenshots.Cleanup(clock.UtcNow.AddSeconds(1), new RetentionRules(TimeSpan.Zero));
        long bytes = before.Sum(e => e.Bytes) - screenshots.Entries.Sum(e => e.Bytes);
        return new(DataCleanKinds.Screenshots, true, null, report.Deleted, Math.Max(0, bytes), report.InUse + report.Changed);
    }

    private DataCleanOutcome ClearFavorites()
    {
        if (favorites is null) return new(DataCleanKinds.Favorites, false, "unavailable", 0, 0, 0);
        return new(DataCleanKinds.Favorites, true, null, favorites.ClearAll(), 0, 0);
    }

    private DataCleanOutcome ResetSettings()
    {
        var state = config.State;
        var current = state.Effective;
        var defaults = BuiltInCatalog.Defaults();
        var bindings = current.Instances.ToDictionary(i => i.Id, i => i.AccountBindings, StringComparer.Ordinal);
        var next = defaults with
        {
            Accounts = current.Accounts,
            Instances = [.. defaults.Instances.Select(i => bindings.TryGetValue(i.Id, out var b) ? i with { AccountBindings = b } : i)],
        };
        var saved = config.Save(next, state.Revision, state.FileHash);
        return saved.Status == SaveStatus.Saved ? new(DataCleanKinds.Settings, true, null, 1, 0, 0)
            : new(DataCleanKinds.Settings, false, saved.Status == SaveStatus.Conflict ? "conflict" : "failed", 0, 0, 0);
    }

    private DataCleanOutcome DeleteAccounts()
    {
        var state = config.State;
        var current = state.Effective;
        var keys = secrets.Entries();
        var next = current with
        {
            Accounts = [],
            Instances = [.. current.Instances.Select(i => i with { AccountBindings = new Dictionary<string, string>() })],
        };
        var saved = config.SaveWithSecrets(next, state.Revision, state.FileHash, [.. keys.Select(k => (k.Account, k.Name, (string?)null))]);
        if (saved.Status == SaveStatus.Saved) RemovePreviousCopies(); // F17V-10: the .prev copies hold the old keys and accounts
        return saved.Status == SaveStatus.Saved ? new(DataCleanKinds.Accounts, true, null, current.Accounts.Count + keys.Count(k => !current.Accounts.Any(a => a.Id == k.Account)), 0, 0)
            : new(DataCleanKinds.Accounts, false, saved.Status == SaveStatus.Conflict ? "conflict" : "failed", 0, 0, 0);
    }

    /// <summary>
    /// Every atomic write keeps the previous version as <c>.prev</c>; after "delete accounts and keys" those copies still hold the old key file and the
    /// accounts, so they are deleted too (F17V-10). A copy that is locked is emptied instead; only a copy that cannot be touched at all stays.
    /// </summary>
    private void RemovePreviousCopies()
    {
        foreach (string file in new[] { paths.Secrets + ".prev", paths.Settings + ".prev", paths.Secrets + ".damaged" })
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (IOException) { try { File.WriteAllBytes(file, []); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            catch (UnauthorizedAccessException) { }
        }
    }
}
