using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Susu.Abstractions;
using Susu.Storage;

namespace Susu.Plugins.AppUpdate;

public static class UpdateStages
{
    public const string Downloading = "Downloading", Staged = "Staged", Exiting = "Exiting", BackedUp = "BackedUp", Replacing = "Replacing", Replaced = "Replaced",
        Migrating = "Migrating", Migrated = "Migrated", HealthChecking = "HealthChecking", Healthy = "Healthy", Committed = "Committed",
        RollingBack = "RollingBack", RolledBack = "RolledBack";
}

public sealed record UpdateFileEntry(string Path, string Sha256, long Size);

/// <summary>
/// The update journal (F18.2). One file in the updates folder, replaced atomically at every stage change, so a crash at any moment leaves the last
/// stage that was fully reached. Stage is one of <see cref="UpdateStages"/>. Paths in it are never trusted on recovery: the updater's own install
/// folder and database path decide what is touched.
/// </summary>
public sealed record UpdateJournal(string Id, string Stage, string FromVersion, string ToVersion, long Sequence, string PackageFile, string PackageSha256, long PackageSize,
    UpdateFileEntry[] Files, string? Error, string? InstallDir = null);

public sealed record BackupIndex(string Version, UpdateFileEntry[] Files, bool HadDatabase, string DatabaseCopySha256, string? InstallDir = null);

public enum UpdateExitOutcome { NotRunning, Exited, TimedOut }

/// <summary>What the updater needs from the machine: stopping the running app, running the new build's migration and health probe, free space. Real in the host, fakes in tests.</summary>
public interface IUpdateEnvironment
{
    UpdateExitOutcome StopApp(TimeSpan timeout);

    /// <summary>Runs the NEW build's first-start migration against the database. Returns false with a detail on failure.</summary>
    bool Migrate(string installDirectory, string databasePath, out string? detail);

    /// <summary>Runs the NEW build's health probe. Returns false with a detail on failure.</summary>
    bool HealthCheck(string installDirectory, string databasePath, string expectedVersion, out string? detail);

    long FreeBytes(string path);
}

public sealed record StageResult(bool Ok, string? Error);

/// <summary>Outcome: committed | rolled-back | aborted (nothing was changed; still staged) | not-staged | failed (rollback incomplete; recovery will finish it).</summary>
public sealed record ApplyResult(string Outcome, string? Error);

/// <summary>Action: none | cleared | resumable (staged again) | rolled-back | committed-cleanup | failed.</summary>
public sealed record RecoveryResult(string Action, string? Stage);

/// <summary>
/// The staged application update (ARCHITECTURE 10, TEST-PLAN UPD06): download check, verify, stop app, back up the old binaries and database as a pair,
/// replace, migrate, health check, commit. Every stage change is written to the journal first; <see cref="Recover"/> reads it at the next start and either
/// returns to "staged" (nothing was replaced), rolls the binaries and the database back together (anything from the first replaced file to the last
/// uncommitted step), or only cleans up (committed). Success is declared only by the Committed marker. Faults are injected through the fault point
/// at every named step (a crash is any non-I/O exception and is never handled here).
/// </summary>
public sealed class AppUpdater(string installDirectory, string databasePath, string updatesFolder, IUpdateEnvironment env, IFaultPoint? faults = null, string mainExecutable = "susu.exe")
{
    public const int MaxEntries = 4096;
    public const long MaxUnpackedBytes = 1L << 30;
    private const string NewSuffix = ".susu-new";
    private static readonly TimeSpan exitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>First-start counter and the failed-version list (F18.3).</summary>
    public FirstStartGuard Guard { get; } = new(updatesFolder);

    public string DownloadsFolder => Path.Combine(updatesFolder, "downloads");
    public string StageFolder => Path.Combine(updatesFolder, "stage");
    public string BackupFolder => Path.Combine(updatesFolder, "backup");
    private string JournalPath => Path.Combine(updatesFolder, "journal.json");
    private string BackupAppFolder => Path.Combine(BackupFolder, "app");
    private string BackupDatabasePath => Path.Combine(BackupFolder, "susu.db");
    private string BackupIndexPath => Path.Combine(BackupFolder, "backup.json");

    // ===== journal =====
    /// <summary>The install folder as the journal and the backup index record it (full path, no trailing separator). The helper refuses a journal or a backup pair made for another folder (UPD08).</summary>
    private string InstallKey => Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar);

    private bool SameInstall(string? recorded) => recorded is null || string.Equals(recorded.TrimEnd(Path.DirectorySeparatorChar), InstallKey, StringComparison.OrdinalIgnoreCase);

    private static bool PathsPlain(IEnumerable<UpdateFileEntry>? files) => files is not null && files.All(f => f.Path is not null && NormalizePath(f.Path) is not null);


    public UpdateJournal? Read()
    {
        foreach (string path in new[] { JournalPath, JournalPath + ".prev" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var journal = JsonSerializer.Deserialize(File.ReadAllBytes(path), AppUpdateJson.Default.UpdateJournal);
                if (journal is { Files: not null, Stage: not null } && PathsPlain(journal.Files)) return journal; // a journal naming a path outside the install folder is treated as damaged
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return null;
    }

    private void Write(UpdateJournal journal)
    {
        Directory.CreateDirectory(updatesFolder);
        AtomicFile.Write(JournalPath, JsonSerializer.SerializeToUtf8Bytes(journal, AppUpdateJson.Default.UpdateJournal));
    }

    private void Enter(ref UpdateJournal journal, string stage, string? error = null)
    {
        journal = journal with { Stage = stage, Error = error };
        Write(journal);
    }

    /// <summary>Removes the journal and every staged or downloaded file. The backup pair stays until the next update replaces it.</summary>
    public void Discard()
    {
        TryDeleteDirectory(StageFolder);
        TryDeleteDirectory(DownloadsFolder);
        TryDeleteFile(JournalPath);
        TryDeleteFile(JournalPath + ".prev");
    }

    // ===== staging =====

    /// <summary>Records that a download started (so a crash leaves a journal that recovery clears) and returns the folder to download into.</summary>
    public string BeginDownload(AppUpdateOffer offer, string fromVersion)
    {
        Discard();
        Directory.CreateDirectory(DownloadsFolder);
        Write(new UpdateJournal(Guid.NewGuid().ToString("N"), UpdateStages.Downloading, fromVersion, offer.Version, offer.Sequence, offer.FileName, offer.Sha256, offer.Size, [], null, InstallKey));
        faults?.Hit("download:begin");
        return DownloadsFolder;
    }

    /// <summary>
    /// Checks the downloaded package against the VERIFIED manifest values (exact size, SHA-256), unpacks it with bounds into the stage folder and journals
    /// "Staged". A failure removes the download and the stage and leaves the installed version untouched. Error: download-truncated | size-mismatch |
    /// hash-mismatch | package-invalid | disk-full | stage-failed.
    /// </summary>
    public StageResult Stage(string zipPath)
    {
        var journal = Read();
        if (journal is not { Stage: UpdateStages.Downloading }) return new StageResult(false, "not-downloading");
        try
        {
            string? failure = StageFromPackage(journal, zipPath, out var entries); // the package handle is closed again before any cleanup below
            if (failure is not null) return Fail(failure);
            Write(journal with { Stage = UpdateStages.Staged, Files = entries!, Error = null });
            faults?.Hit("stage:journal");
            return new StageResult(true, null);
        }
        catch (Exception e) when (IsHandled(e)) { return Fail(e is IOException io && StorageErrors.IsDiskFull(io) ? "disk-full" : "stage-failed"); }

        StageResult Fail(string code)
        {
            Discard();
            return new StageResult(false, code);
        }
    }

    private string? StageFromPackage(UpdateJournal journal, string zipPath, out UpdateFileEntry[]? entries)
    {
        entries = null;
        faults?.Hit("stage:begin");
        // UPD08: the package must be a plain file inside this updater's own downloads folder, and no work folder may be a junction or symlink.
        if (!WorkFoldersPlain()) return "stage-failed";
        string fullZip = Path.GetFullPath(zipPath);
        if (!UpdatePathRules.IsUnder(fullZip, Path.GetFullPath(DownloadsFolder)) || UpdatePathRules.IsReparsePoint(fullZip)) return "download-path-invalid";
        if (!File.Exists(fullZip)) return "download-truncated";
        // One handle for the size check, the hash and the unpacking: writers are denied while it is open, so the bytes that were verified are the bytes that are unpacked.
        using var package = new FileStream(fullZip, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (package.Length < journal.PackageSize) return "download-truncated";
        if (package.Length != journal.PackageSize) return "size-mismatch";
        string hash = Convert.ToHexStringLower(SHA256.HashData(package));
        if (!string.Equals(hash, journal.PackageSha256, StringComparison.Ordinal)) return "hash-mismatch";
        package.Position = 0;
        faults?.Hit("stage:hashed");
        TryDeleteDirectory(StageFolder);
        Directory.CreateDirectory(StageFolder);
        entries = Extract(package, StageFolder, out string? error);
        if (entries is null) return error ?? "package-invalid";
        faults?.Hit("stage:extracted");
        if (!entries.Any(e => string.Equals(e.Path, mainExecutable, StringComparison.OrdinalIgnoreCase))) return "package-invalid";
        return null;
    }

    /// <summary>True when the updates folder and the folders under it are not reparse points (a junction could point the stage or the backup anywhere).</summary>
    private bool WorkFoldersPlain()
        => new[] { updatesFolder, DownloadsFolder, StageFolder, BackupFolder }.All(p => !UpdatePathRules.IsReparsePoint(p));

    private static UpdateFileEntry[]? Extract(Stream zipStream, string destination, out string? error)
    {
        error = null;
        var result = new List<UpdateFileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        try
        {
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count is 0 or > MaxEntries) { error = "package-invalid"; return null; }
            string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue; // directories are created by their files
                string? rel = NormalizePath(entry.FullName);
                if (rel is null || !seen.Add(rel)) { error = "package-invalid"; return null; }
                if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) { error = "package-invalid"; return null; } // a symbolic link
                total += entry.Length;
                if (total > MaxUnpackedBytes) { error = "package-invalid"; return null; }
                string target = Path.GetFullPath(Path.Combine(destination, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { error = "package-invalid"; return null; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += read;
                    if (written > entry.Length) { error = "package-invalid"; return null; } // the header lied about the size
                    output.Write(buffer, 0, read);
                    hasher.AppendData(buffer, 0, read);
                }
                if (written != entry.Length) { error = "package-invalid"; return null; }
                output.Flush(flushToDisk: true);
                result.Add(new UpdateFileEntry(rel, Convert.ToHexStringLower(hasher.GetHashAndReset()), written));
            }
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException) { error = "package-invalid"; return null; }
        return [.. result];
    }

    private static readonly string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>A relative forward-slash path of plain names, or null (absolute, traversal, drive or stream syntax, reserved device name, trailing dot or space).</summary>
    internal static string? NormalizePath(string name)
    {
        string path = name.Replace('\\', '/');
        if (path.Length is 0 or > 200 || path.StartsWith('/') || path.Contains('\0')) return null;
        var parts = path.Split('/');
        foreach (string part in parts)
        {
            if (part.Length is 0 or > 100 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.EndsWith(NewSuffix, StringComparison.OrdinalIgnoreCase)) return null;
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(':')) return null;
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (reserved.Contains(stem)) return null;
        }
        return string.Join('/', parts);
    }

    // ===== apply =====

    /// <summary>
    /// Stop the app, back up, replace, migrate, health check, commit (the journal stage after each step is named in <see cref="UpdateStages"/>). Before the first
    /// file is replaced a failure only returns to "staged" (outcome aborted). From the first replacement on, a failure rolls the binaries and the database back
    /// together (outcome rolled-back). Error is a stable key: exit-timeout | disk-full | stage-corrupt | backup-failed | replace-failed | migration-failed |
    /// health-check-failed | write-failed | rollback-incomplete.
    /// </summary>
    public ApplyResult Apply()
    {
        var journal = Read();
        if (journal is not { Stage: UpdateStages.Staged }) return new ApplyResult("not-staged", null);
        string error;
        try
        {
            if (!WorkFoldersPlain()) { Discard(); return new ApplyResult("not-staged", "stage-failed"); } // a junction in the work folders: nothing is applied
            if (!SameInstall(journal.InstallDir)) return new ApplyResult("not-staged", "install-dir-mismatch"); // staged for another folder: nothing is replaced here
            if (!StageIntact(journal)) { Discard(); return new ApplyResult("not-staged", "stage-corrupt"); }
            if (!HasRoomFor(journal)) return Abort(ref journal, "disk-full"); // before the app is stopped
            Enter(ref journal, UpdateStages.Exiting);
            faults?.Hit("exit:journal");
            var exit = env.StopApp(exitTimeout);
            faults?.Hit("exit:requested");
            if (exit == UpdateExitOutcome.TimedOut) return Abort(ref journal, "exit-timeout");
            faults?.Hit("exit:done");
            TryDeleteDirectory(BackupFolder);
            Backup(journal);
            Enter(ref journal, UpdateStages.BackedUp);
            faults?.Hit("backup:journal");
        }
        catch (Exception e) when (IsHandled(e))
        {
            journal = Read() ?? journal;
            TryDeleteDirectory(BackupFolder);
            return Abort(ref journal, Classify(e, "backup-failed"));
        }

        try
        {
            Enter(ref journal, UpdateStages.Replacing);
            faults?.Hit("replace:journal");
            Replace(journal);
            Enter(ref journal, UpdateStages.Replaced);
            faults?.Hit("replace:done");

            // UPD08: only files that are byte-identical to the verified package are executed. The new executable is checked again right before it is run.
            if (!InstalledFilesMatch(journal)) throw new UpdateStepException("stage-corrupt", "installed files differ from the verified package");
            Enter(ref journal, UpdateStages.Migrating);
            faults?.Hit("migrate:begin");
            if (!env.Migrate(installDirectory, databasePath, out string? migrateDetail)) throw new UpdateStepException("migration-failed", migrateDetail);
            Enter(ref journal, UpdateStages.Migrated);
            faults?.Hit("migrate:done");

            Enter(ref journal, UpdateStages.HealthChecking);
            faults?.Hit("health:begin");
            if (!InstalledFilesMatch(journal)) throw new UpdateStepException("health-check-failed", "files");
            if (!env.HealthCheck(installDirectory, databasePath, journal.ToVersion, out string? healthDetail)) throw new UpdateStepException("health-check-failed", healthDetail);
            faults?.Hit("health:done");
            Enter(ref journal, UpdateStages.Healthy);
            faults?.Hit("health:journal");

            Guard.Arm(journal.ToVersion, journal.FromVersion); // F18.3: the first start of the new version is counted; before the Committed marker so a commit is never unguarded
            Enter(ref journal, UpdateStages.Committed);
            faults?.Hit("commit:journal");
            WriteInstalledList(journal);
            CleanAfterCommit();
            faults?.Hit("commit:cleanup");
            return new ApplyResult("committed", null);
        }
        catch (Exception e) when (IsHandled(e))
        {
            error = Classify(e, "replace-failed");
        }
        journal = Read() ?? journal;
        try
        {
            RollBack(ref journal, error);
            return new ApplyResult("rolled-back", error);
        }
        catch (Exception e) when (IsHandled(e))
        {
            return new ApplyResult("failed", "rollback-incomplete"); // the journal says RollingBack; Recover finishes it at the next start
        }
    }

    private ApplyResult Abort(ref UpdateJournal journal, string error)
    {
        Enter(ref journal, UpdateStages.Staged, error);
        return new ApplyResult("aborted", error);
    }

    private static bool IsHandled(Exception e) => e is IOException or UnauthorizedAccessException or UpdateStepException or InvalidDataException or System.Data.Common.DbException;

    private static string Classify(Exception e, string fallback)
        => e is IOException io && StorageErrors.IsDiskFull(io) ? "disk-full" : e is UpdateStepException s ? s.Code : fallback;

    private bool StageIntact(UpdateJournal journal)
        => journal.Files.Length > 0 && journal.Files.All(f => FileMatches(Path.Combine(StageFolder, f.Path.Replace('/', Path.DirectorySeparatorChar)), f));

    private bool InstalledFilesMatch(UpdateJournal journal)
        => journal.Files.All(f => FileMatches(Path.Combine(installDirectory, f.Path.Replace('/', Path.DirectorySeparatorChar)), f));

    private static bool FileMatches(string path, UpdateFileEntry entry)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != entry.Size) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), entry.Sha256, StringComparison.Ordinal);
    }

    private bool HasRoomFor(UpdateJournal journal)
    {
        long installed = InstalledFiles().Sum(p => new FileInfo(p).Length);
        long db = File.Exists(databasePath) ? new FileInfo(databasePath).Length * 2 : 0;
        long largest = journal.Files.Length == 0 ? 0 : journal.Files.Max(f => f.Size);
        long need = installed + db + largest * 2 + (4L << 20);
        return env.FreeBytes(updatesFolder) >= need && env.FreeBytes(installDirectory) >= largest * 2 + (4L << 20);
    }

    private IEnumerable<string> InstalledFiles()
    {
        if (!Directory.Exists(installDirectory)) return [];
        return Directory.EnumerateFiles(installDirectory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(p => !p.EndsWith(NewSuffix, StringComparison.OrdinalIgnoreCase));
    }

    private void Backup(UpdateJournal journal)
    {
        faults?.Hit("backup:begin");
        Directory.CreateDirectory(BackupAppFolder);
        var files = new List<UpdateFileEntry>();
        string root = Path.GetFullPath(installDirectory);
        int i = 0;
        foreach (string source in InstalledFiles())
        {
            string rel = Path.GetRelativePath(root, source).Replace('\\', '/');
            string target = Path.Combine(BackupAppFolder, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string hash = CopyFlushed(source, target);
            files.Add(new UpdateFileEntry(rel, hash, new FileInfo(target).Length));
            faults?.Hit($"backup:app:{i++}");
        }
        bool hadDb = File.Exists(databasePath);
        string dbHash = "";
        if (hadDb)
        {
            Database.CopyFile(databasePath, BackupDatabasePath);
            dbHash = AtomicFile.HashOf(BackupDatabasePath);
            faults?.Hit("backup:db");
        }
        var index = new BackupIndex(journal.FromVersion, [.. files], hadDb, dbHash, InstallKey);
        AtomicFile.Write(BackupIndexPath, JsonSerializer.SerializeToUtf8Bytes(index, AppUpdateJson.Default.BackupIndex));
        faults?.Hit("backup:index");
    }

    private void Replace(UpdateJournal journal)
    {
        var knownOld = KnownInstalledPaths(); // read before the first replacement: the installer's staged-manifest.json is itself replaced
        string backupTemp;
        int i = 0;
        foreach (var file in journal.Files)
        {
            string source = Path.Combine(StageFolder, file.Path.Replace('/', Path.DirectorySeparatorChar));
            string target = Path.Combine(installDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!TargetIsPlain(target)) throw new UpdateStepException("replace-failed", "link in the install folder"); // never write through a junction or symlink
            backupTemp = target + NewSuffix;
            TryDeleteFile(backupTemp);
            // The staged file is read with writers denied and hashed while it is copied: what lands in the install folder is exactly what the verified package held.
            string copied = CopyFlushed(source, backupTemp);
            if (!string.Equals(copied, file.Sha256, StringComparison.Ordinal))
            {
                TryDeleteFile(backupTemp);
                throw new UpdateStepException("stage-corrupt", "staged file changed after verification");
            }
            if (File.Exists(target))
            {
                File.SetAttributes(target, FileAttributes.Normal);
                File.Replace(backupTemp, target, null, ignoreMetadataErrors: true);
            }
            else File.Move(backupTemp, target);
            faults?.Hit($"replace:{i++}");
        }
        // Only files of the previous version that the new one no longer ships are removed, and only files this updater knows the old version brought (its installed list and the
        // installer's own file list). Anything else in the folder is the user's and stays; the backup holds all of it anyway.
        var keep = new HashSet<string>(journal.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        foreach (string old in knownOld)
        {
            if (keep.Contains(old)) continue;
            string path = Path.Combine(installDirectory, old.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path) && TargetIsPlain(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
        }
        faults?.Hit("replace:deleted");
    }

    /// <summary>The target and every folder between the install folder and it are ordinary (no reparse point).</summary>
    private bool TargetIsPlain(string target)
    {
        string root = Path.GetFullPath(installDirectory).TrimEnd('\\');
        string? dir = Path.GetDirectoryName(Path.GetFullPath(target));
        while (dir is not null && dir.Length > root.Length && UpdatePathRules.IsUnder(dir, root))
        {
            if (UpdatePathRules.IsReparsePoint(dir)) return false;
            dir = Path.GetDirectoryName(dir);
        }
        return !UpdatePathRules.IsReparsePoint(target);
    }

    private string InstalledListPath => Path.Combine(updatesFolder, "installed-files.json");

    /// <summary>The files the installed version brought: the updater's own list from its last commit plus the installer's staged-manifest.json. Names are re-validated; nothing else is ever deleted.</summary>
    private HashSet<string> KnownInstalledPaths()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? name) { if (name is not null && NormalizePath(name) is { } n && !string.Equals(n, mainExecutable, StringComparison.OrdinalIgnoreCase)) known.Add(n); }
        try
        {
            if (File.Exists(InstalledListPath))
                foreach (string name in JsonSerializer.Deserialize(File.ReadAllBytes(InstalledListPath), AppUpdateJson.Default.StringArray) ?? []) Add(name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        try
        {
            string manifest = Path.Combine(installDirectory, "staged-manifest.json");
            if (File.Exists(manifest))
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
                if (doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                    foreach (var f in files.EnumerateArray())
                        if (f.ValueKind == JsonValueKind.Object && f.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String) Add(p.GetString());
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return known;
    }

    private void WriteInstalledList(UpdateJournal journal)
    {
        try { AtomicFile.Write(InstalledListPath, JsonSerializer.SerializeToUtf8Bytes(journal.Files.Select(f => f.Path).ToArray(), AppUpdateJson.Default.StringArray)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // the next update then removes nothing it cannot prove it brought
    }

    private void CleanAfterCommit()
    {
        TryDeleteDirectory(StageFolder);
        TryDeleteDirectory(DownloadsFolder);
    }

    // ===== rollback =====

    public const string FirstStartFailed = "first-start-failed", UserRollback = "user-rollback";

    private void RollBack(ref UpdateJournal journal, string error)
    {
        if (error == FirstStartFailed) Guard.MarkFailed(journal.ToVersion); // the loop guard is written first: even a crash during the restore leaves the version marked
        Enter(ref journal, UpdateStages.RollingBack, error);
        faults?.Hit("rollback:journal");
        RestorePair(journal);
        Guard.Clear(); // the old version is back; the first-start counter belongs to the version that was removed
        Enter(ref journal, UpdateStages.RolledBack, error);
        faults?.Hit("rollback:done");
    }

    /// <summary>True when a backup pair of the previous version exists and the update that made it is committed (or its journal was dismissed): the user may go back.</summary>
    public bool CanRollBackToPrevious()
        => File.Exists(BackupIndexPath) && Read() is null or { Stage: UpdateStages.Committed };

    private string RolledBackDataFolder => Path.Combine(updatesFolder, "rolled-back-data");

    /// <summary>
    /// F18.3: the old-version fallback, from the user's command or from the first-start guard. Needs the committed update's backup pair (kept until the next update). Stops the app, keeps
    /// the CURRENT database as <c>rolled-back-data/susu-&lt;version&gt;.db</c> (anything saved since the update is therefore not lost, only not in the restored database), then restores the old
    /// binaries and the old database together. Outcome: rolled-back | aborted (exit timeout, nothing changed) | no-backup | failed (rollback-incomplete; recovery finishes it).
    /// </summary>
    public ApplyResult RollBackToPrevious(string reason, string? rolledBackVersion = null)
    {
        if (!CanRollBackToPrevious()) return new ApplyResult("no-backup", null);
        try
        {
            if (!WorkFoldersPlain()) return new ApplyResult("no-backup", "stage-failed");
            var journal = Read();
            BackupIndex? index = null;
            try { index = JsonSerializer.Deserialize(File.ReadAllBytes(BackupIndexPath), AppUpdateJson.Default.BackupIndex); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
            if (index?.Files is null || !PathsPlain(index.Files) || !SameInstall(index.InstallDir)) return new ApplyResult("no-backup", "backup-damaged");
            string version = journal?.ToVersion ?? rolledBackVersion ?? Guard.Read()?.Version ?? "unknown";
            if (version == "?") version = rolledBackVersion ?? "unknown";
            if (env.StopApp(exitTimeout) == UpdateExitOutcome.TimedOut) return new ApplyResult("aborted", "exit-timeout");
            if (File.Exists(databasePath))
            {
                Directory.CreateDirectory(RolledBackDataFolder);
                string keep = Path.Combine(RolledBackDataFolder, "susu-" + SafeName(version) + ".db");
                try { Database.CopyFile(databasePath, keep); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Data.Common.DbException) { File.Copy(databasePath, keep, overwrite: true); }
                faults?.Hit("fallback:kept");
            }
            string[] newFiles = journal?.Files.Select(f => f.Path).ToArray() ?? ReadInstalledList();
            var synthetic = new UpdateJournal(journal?.Id ?? Guid.NewGuid().ToString("N"), UpdateStages.Committed, index.Version, version, journal?.Sequence ?? 0,
                journal?.PackageFile ?? "", journal?.PackageSha256 ?? "", journal?.PackageSize ?? 0, [.. newFiles.Select(p => new UpdateFileEntry(p, "", 0))], null);
            RollBack(ref synthetic, reason);
            return new ApplyResult("rolled-back", reason);
        }
        catch (Exception e) when (IsHandled(e)) { return new ApplyResult("failed", "rollback-incomplete"); }
    }

    private string[] ReadInstalledList()
    {
        try { return File.Exists(InstalledListPath) ? (JsonSerializer.Deserialize(File.ReadAllBytes(InstalledListPath), AppUpdateJson.Default.StringArray) ?? []).Where(p => p is not null && NormalizePath(p) is not null).ToArray() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private static string SafeName(string version) => new(version.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());

    /// <summary>Puts the old binaries and the old database back from the pair taken before the replacement; verifies each restored file against the backup index.</summary>
    private void RestorePair(UpdateJournal journal)
    {
        BackupIndex? index = null;
        try { index = JsonSerializer.Deserialize(File.ReadAllBytes(BackupIndexPath), AppUpdateJson.Default.BackupIndex); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        if (index?.Files is null || !PathsPlain(index.Files)) throw new UpdateStepException("rollback-incomplete", "no backup index");
        if (!SameInstall(index.InstallDir)) throw new UpdateStepException("rollback-incomplete", "backup belongs to another install folder");
        // The backup must be intact before anything is overwritten from it.
        foreach (var f in index.Files)
            if (!FileMatches(Path.Combine(BackupAppFolder, f.Path.Replace('/', Path.DirectorySeparatorChar)), f)) throw new UpdateStepException("rollback-incomplete", "backup damaged");
        if (index.HadDatabase && !string.Equals(AtomicFile.HashOf(BackupDatabasePath), index.DatabaseCopySha256, StringComparison.Ordinal)) throw new UpdateStepException("rollback-incomplete", "backup db damaged");

        int i = 0;
        foreach (var f in index.Files)
        {
            string rel = f.Path.Replace('/', Path.DirectorySeparatorChar);
            string target = Path.Combine(installDirectory, rel);
            if (!FileMatches(target, f))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string temp = target + NewSuffix;
                TryDeleteFile(temp);
                CopyFlushed(Path.Combine(BackupAppFolder, rel), temp);
                if (File.Exists(target)) { File.SetAttributes(target, FileAttributes.Normal); File.Replace(temp, target, null, ignoreMetadataErrors: true); }
                else File.Move(temp, target);
            }
            faults?.Hit($"rollback:file:{i++}");
        }
        // Files the new version added are removed; leftovers of an interrupted replacement are removed.
        var old = new HashSet<string>(index.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        string root = Path.GetFullPath(installDirectory);
        foreach (var added in journal.Files.Where(f => !old.Contains(f.Path)))
            TryDeleteFile(Path.Combine(installDirectory, added.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (Directory.Exists(installDirectory))
            foreach (string leftover in Directory.EnumerateFiles(installDirectory, "*" + NewSuffix, SearchOption.AllDirectories)) TryDeleteFile(leftover);
        faults?.Hit("rollback:files");
        if (index.HadDatabase)
        {
            Database.ReplaceFileWithCopy(BackupDatabasePath, databasePath);
            faults?.Hit("rollback:db");
        }
        else
        {
            // There was no database before the update; a database the new build created is removed so the old build starts as it would have.
            TryDeleteFile(databasePath); TryDeleteFile(databasePath + "-wal"); TryDeleteFile(databasePath + "-shm");
        }
        if (!index.Files.All(f => FileMatches(Path.Combine(root, f.Path.Replace('/', Path.DirectorySeparatorChar)), f))) throw new UpdateStepException("rollback-incomplete", "verify");
    }

    // ===== recovery =====

    /// <summary>
    /// True when recovery has to rewrite the install folder (a replacement started and did not commit, a rollback was interrupted, or the journal is damaged next to a
    /// backup pair). The running app cannot do that to its own files; it starts the helper in recovery mode instead of calling <see cref="Recover"/>.
    /// </summary>
    public bool NeedsFileRecovery()
    {
        var journal = Read();
        if (journal is null) return File.Exists(BackupIndexPath) && (File.Exists(JournalPath) || File.Exists(JournalPath + ".prev"));
        return journal.Stage is UpdateStages.Replacing or UpdateStages.Replaced or UpdateStages.Migrating or UpdateStages.Migrated or UpdateStages.HealthChecking or UpdateStages.Healthy or UpdateStages.RollingBack;
    }

    /// <summary>
    /// Run at every start of the app and of the helper, before anything else uses the install folder. Never throws.
    /// Downloading: the partial download is cleared. Staged: kept (the user may still install it). Exiting and BackedUp: nothing was replaced, back to staged.
    /// Replacing through Healthy, RollingBack: rolled back as a pair. Committed: cleanup only. An unreadable journal with a backup index rolls back as well.
    /// </summary>
    public RecoveryResult Recover()
    {
        try
        {
            var journal = Read();
            if (journal is null)
            {
                if (File.Exists(JournalPath) || File.Exists(JournalPath + ".prev") || File.Exists(BackupIndexPath) && Directory.Exists(StageFolder))
                {
                    // Damaged journal: if a backup pair exists the install may be half replaced; restore the pair, then drop the damaged journal.
                    if (File.Exists(BackupIndexPath))
                    {
                        RestorePair(new UpdateJournal("", UpdateStages.RollingBack, "", "", 0, "", "", 0, StagedPaths(), "journal-damaged"));
                        Discard();
                        return new RecoveryResult("rolled-back", null);
                    }
                    Discard();
                    return new RecoveryResult("cleared", null);
                }
                return new RecoveryResult("none", null);
            }
            switch (journal.Stage)
            {
                case UpdateStages.Downloading:
                    Discard();
                    return new RecoveryResult("cleared", journal.Stage);
                case UpdateStages.Staged:
                    if (!StageIntact(journal)) { Discard(); return new RecoveryResult("cleared", journal.Stage); }
                    return new RecoveryResult("none", journal.Stage);
                case UpdateStages.Exiting:
                case UpdateStages.BackedUp:
                    if (!StageIntact(journal)) { Discard(); return new RecoveryResult("cleared", journal.Stage); }
                    Write(journal with { Stage = UpdateStages.Staged, Error = "interrupted" });
                    return new RecoveryResult("resumable", journal.Stage);
                case UpdateStages.Replacing:
                case UpdateStages.Replaced:
                case UpdateStages.Migrating:
                case UpdateStages.Migrated:
                case UpdateStages.HealthChecking:
                case UpdateStages.Healthy:
                case UpdateStages.RollingBack:
                    RollBack(ref journal, journal.Error ?? "interrupted");
                    return new RecoveryResult("rolled-back", journal.Stage);
                case UpdateStages.Committed:
                    CleanAfterCommit();
                    return new RecoveryResult("committed-cleanup", journal.Stage);
                default:
                    return new RecoveryResult("none", journal.Stage); // RolledBack: shown to the user until dismissed
            }
        }
        catch (Exception e) when (IsHandled(e))
        {
            return new RecoveryResult("failed", null); // retried at the next start; the journal and the backup pair are kept
        }
    }

    // ===== helpers =====

    /// <summary>With the journal gone the list of files the new version brought is read from the stage folder (the same names the replacement used).</summary>
    private UpdateFileEntry[] StagedPaths()
        => !Directory.Exists(StageFolder) ? [] : [.. Directory.EnumerateFiles(StageFolder, "*", SearchOption.AllDirectories)
            .Select(p => new UpdateFileEntry(Path.GetRelativePath(StageFolder, p).Replace('\\', '/'), "", 0))];


    private static string CopyFlushed(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            hasher.AppendData(buffer, 0, read);
        }
        output.Flush(flushToDisk: true);
        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            if (UpdatePathRules.IsReparsePoint(path)) Directory.Delete(path, recursive: false); // a junction or symlink is unlinked, its target is never touched
            else Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

public sealed class UpdateStepException(string code, string? detail = null) : Exception(code + (detail is null ? "" : ": " + detail))
{
    public string Code { get; } = code;
}

/// <summary>The updater's files (journal, trust state, schedule). Generated serialization keeps the NativeAOT host free of reflection.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpdateJournal))]
[JsonSerializable(typeof(BackupIndex))]
[JsonSerializable(typeof(TrustFile))]
[JsonSerializable(typeof(ScheduleFile))]
[JsonSerializable(typeof(UpdateSourceFile))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class AppUpdateJson : JsonSerializerContext;
