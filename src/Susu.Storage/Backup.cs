using System.Text;
using System.Text.Json;
using Susu.Abstractions;
using Susu.Domain;

namespace Susu.Storage;

/// <summary>
/// What the host tells the backup service about this installation. AvailablePackages: package id to installed version (null for built-in and native
/// packages); UserPlugins: the installed user packages as identities only.
/// </summary>
public sealed record BackupHost(string AppVersion, Func<IReadOnlyDictionary<string, string?>> AvailablePackages, Func<IReadOnlyList<BackupPluginRef>> UserPlugins, Func<BackupLocalData> LocalData);
internal sealed record BackupPending(string Token, string State, string SettingsHash, string SecretsHash, string? FinalSettingsHash, int Attempts, string CreatedUtc, string Source, int Disabled);
internal sealed record BackupResult(string State, string? Error, string Token, string AtUtc, int Disabled, string Source);
internal sealed record BackupRestorePointMeta(string Token, bool HadSettings, bool HadSecrets, string CreatedUtc);

/// <summary>
/// Backup and restore of settings (F17.1, ARCHITECTURE 8.1). Export never includes keys unless asked and then only inside a password-encrypted file.
/// Import is three steps: Preview verifies the whole file (password, KDF and size bounds, hashes, schema) and stages the new settings.yaml and a
/// secrets.dat already protected for the importing user, writing nothing to the live config; Apply marks the stage Ready; the next start
/// (<see cref="BackupImport.ApplyPending"/>) commits both files through the journaled <see cref="ConfigTransaction"/> after keeping a restore point of the
/// old pair. Plugin code, caches, the database and granted authorizations are never in a backup, so an import cannot install or authorize anything.
/// </summary>
public sealed class BackupService(AppPaths paths, ISettingsStore settings, SecretStore secrets, ISecretProtector protector, IClock clock, BackupHost host, IFaultPoint? faults = null) : IBackupService
{
    private readonly object gate = new();

    // ---------- export ----------

    public BackupExportOutcome Export(string path, BackupExportOptions options)
    {
        bool encrypted = !string.IsNullOrEmpty(options.Password);
        if (options.IncludeSecrets && !encrypted) return new BackupExportOutcome(false, "password-required", false, false, 0);
        if (encrypted && (options.Password!.Length < BackupLimits.MinPasswordChars || options.Password.Length > BackupLimits.MaxPasswordChars))
            return new BackupExportOutcome(false, "password-length", false, false, 0);
        if (string.IsNullOrWhiteSpace(path)) return new BackupExportOutcome(false, "path-invalid", encrypted, false, 0);

        byte[] file;
        int secretCount = 0;
        try
        {
            var state = settings.State.Effective;
            string yaml = SettingsYaml.Write(WithoutGrants(state));
            List<(string, string, string)>? values = null;
            if (options.IncludeSecrets)
            {
                values = [];
                foreach (var (account, name) in secrets.Entries())
                {
                    if (!secrets.TryRead(account, name, out string value)) continue;
                    values.Add((account, name, value));
                }
                secretCount = values.Count;
            }
            file = BackupArchive.Build(host.AppVersion, clock.UtcNow, Encoding.UTF8.GetBytes(yaml), [.. host.UserPlugins()], values, options.Password);
        }
        catch (BackupException e) { return new BackupExportOutcome(false, e.Code, encrypted, false, 0); }
        catch (System.Security.Cryptography.CryptographicException) { return new BackupExportOutcome(false, "secrets-unreadable", encrypted, false, 0); }

        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Written beside the target under a temporary name and moved into place only when complete and flushed, so a failure at any point (disk full,
            // lock, power loss) never leaves a partial .susubak and an existing file of that name stays whole.
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                int half = file.Length / 2;
                stream.Write(file, 0, half);
                faults?.Hit("export:partial");
                stream.Write(file, half, file.Length - half);
                stream.Flush(flushToDisk: true);
            }
            faults?.Hit("export:written");
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return new BackupExportOutcome(false, BackupImport.FailureCode(e, "write-failed"), encrypted, false, 0);
        }
        return new BackupExportOutcome(true, null, encrypted, options.IncludeSecrets, secretCount);
    }

    // ---------- import: preview and stage ----------

    /// <summary>True when the file at <paramref name="path"/> is password-encrypted (so the page asks for the password first).</summary>
    public bool NeedsPassword(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> head = stackalloc byte[8];
            return stream.Read(head) == 8 && BackupArchive.IsEncrypted(head);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>
    /// Verifies the file and stages the import. Writes only under <see cref="AppPaths.Imports"/>; the live settings.yaml and secrets.dat are untouched.
    /// Throws <see cref="BackupException"/> with a stable code when the file is refused.
    /// </summary>
    public BackupPreview Preview(string path, string? password)
    {
        // F17V-2: a confirmed import (or scheduled undo) is never dropped silently by another preview; the user discards it first.
        if (BackupImport.ReadPending(paths) is { State: "Ready" or "Applying" }) throw new BackupException("import-scheduled");
        var contents = BackupArchive.Open(BackupArchive.ReadFile(path), password);
        var available = host.AvailablePackages();
        var (restored, disabled) = BackupImport.Sanitize(contents.Settings, available);
        var current = settings.State.Effective;
        var supplied = contents.Secrets.Select(s => (s.Account, s.Name)).ToHashSet();
        var currentKeys = secrets.Entries();
        int keysRemoved = currentKeys.Count(k => !supplied.Contains(k));

        byte[] settingsBytes = Encoding.UTF8.GetBytes(SettingsYaml.Write(restored));
        byte[] secretsBytes = SecretStore.BuildFile(protector, contents.Secrets);
        string token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        lock (gate)
        {
            try { BackupImport.Stage(paths, new BackupPending(token, "Previewed", AtomicFile.Hash(settingsBytes), AtomicFile.Hash(secretsBytes), null, 0, clock.UtcNow.UtcDateTime.ToString("O"), "backup", disabled.Count), settingsBytes, secretsBytes, faults); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                BackupImport.ClearStage(paths); // nothing half-staged is left, and an earlier preview's token no longer applies
                throw new BackupException(BackupImport.FailureCode(e, "stage-failed"));
            }
        }

        var plugins = contents.Plugins.Select(p =>
        {
            if (!available.TryGetValue(p.Id, out var installed)) return new BackupPluginStatus(p.Id, p.Version, null, "missing");
            if (installed is null) return new BackupPluginStatus(p.Id, p.Version, null, "builtin");
            return new BackupPluginStatus(p.Id, p.Version, installed, Compare(p.Version, installed));
        }).ToArray();
        var missing = contents.Settings.Instances.Select(i => i.Package).Where(id => !available.ContainsKey(id))
            .Concat(plugins.Where(p => p.Status == "missing").Select(p => p.Id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var accounts = restored.Accounts.Select(a => new BackupAccountStatus(a.Id, a.Label, [.. a.Secrets.Where(n => !supplied.Contains((a.Id, n)))])).ToArray();

        var deltas = new List<BackupDelta>
        {
            new("settings", 0, 0, !current.ContentEquals(restored with { Revision = current.Revision })),
            Delta("accounts", current.Accounts.Count, restored.Accounts.Count, !current.Accounts.Select(a => (a.Id, a.Label)).SequenceEqual(restored.Accounts.Select(a => (a.Id, a.Label)))),
            Delta("instances", current.Instances.Count, restored.Instances.Count, !current.Instances.Select(i => i.Id).Order().SequenceEqual(restored.Instances.Select(i => i.Id).Order())),
            Delta("enabledServices", current.Services.Count(s => s.Enabled), restored.Services.Count(s => s.Enabled), !current.Services.Where(s => s.Enabled).Select(s => s.ServiceId).Order().SequenceEqual(restored.Services.Where(s => s.Enabled).Select(s => s.ServiceId).Order())),
            Delta("prompts", current.Prompts.Count, restored.Prompts.Count, !current.Prompts.SequenceEqual(restored.Prompts)),
        };
        var conflicts = new List<string>();
        if (missing.Length > 0) conflicts.Add("plugins-missing");
        if (disabled.Count > 0) conflicts.Add("services-disabled");
        if (accounts.Length > 0) conflicts.Add("accounts-need-authorization");
        if (accounts.Any(a => a.MissingSecrets.Length > 0)) conflicts.Add("keys-missing");
        if (keysRemoved > 0) conflicts.Add("keys-removed");
        DateTimeOffset? created = DateTimeOffset.TryParse(contents.Manifest.CreatedUtc, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var c) ? c : null;
        return new BackupPreview(token, created, contents.Manifest.AppVersion ?? "", contents.Encrypted, contents.Manifest.IncludesSecrets, contents.Manifest.SchemaVersion < AppSettings.CurrentSchemaVersion,
            deltas, plugins, missing, disabled, accounts, contents.Secrets.Count, keysRemoved, host.LocalData(), conflicts);
    }

    private static BackupDelta Delta(string area, int current, int backup, bool differs) => new(area, current, backup, differs);

    private static string Compare(string backup, string installed)
    {
        if (backup == installed) return "same";
        return Version.TryParse(backup, out var b) && Version.TryParse(installed, out var i) ? (b < i ? "older" : "newer") : "newer";
    }

    /// <summary>Confirms the staged import for the next start. Returns false when the token is not the staged one.</summary>
    public bool Apply(string token)
    {
        lock (gate)
        {
            var pending = BackupImport.ReadPending(paths);
            if (pending is null || pending.Token != token || pending.State != "Previewed") return false;
            if (!BackupImport.StagedMatches(paths, pending)) { BackupImport.ClearStage(paths); return false; }
            try
            {
                faults?.Hit("import-apply:pending");
                BackupImport.WritePending(paths, pending with { State = "Ready" });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                BackupImport.ClearStage(paths);
                throw new BackupException(BackupImport.FailureCode(e, "stage-failed"));
            }
            return true;
        }
    }

    /// <summary>Drops the staged import, also one already marked Ready (before the restart).</summary>
    public void Discard()
    {
        lock (gate) BackupImport.ClearStage(paths);
    }

    /// <summary>
    /// Stages the config that was in effect before the last import (the restore point) as a Ready import; it is switched back at the next start
    /// through the same transaction. Returns false when there is no restore point.
    /// </summary>
    public bool ScheduleUndo()
    {
        lock (gate)
        {
            try { return BackupImport.StageRestorePoint(paths, protector, clock, faults); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                BackupImport.ClearStage(paths);
                throw new BackupException(BackupImport.FailureCode(e, "stage-failed"));
            }
        }
    }

    public BackupStatus Status()
    {
        lock (gate)
        {
            var pending = BackupImport.ReadPending(paths);
            return new BackupStatus(pending?.State == "Ready" || pending?.State == "Applying" ? "Ready" : pending is null ? "None" : "Previewed", pending?.Source,
                BackupImport.HasRestorePoint(paths), BackupImport.ReadResult(paths));
        }
    }

    public void DismissResult()
    {
        lock (gate) BackupImport.ClearResult(paths);
    }

    private static AppSettings WithoutGrants(AppSettings s) => s with { Accounts = [.. s.Accounts.Select(a => a with { Grants = [] })] };
}

/// <summary>The restart-time half of an import, plus the helpers the service shares with it.</summary>
public static class BackupImport
{
    private const int MaxAttempts = 3;

    /// <summary>The restored settings: grants dropped (authorization is never imported) and every service of a package this install does not have turned off.</summary>
    internal static (AppSettings Settings, IReadOnlyList<string> DisabledInstances) Sanitize(AppSettings backup, IReadOnlyDictionary<string, string?> available)
    {
        var missing = backup.Instances.Where(i => !available.ContainsKey(i.Package)).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var disabled = backup.Services.Where(s => s.Enabled && missing.Contains(s.Instance)).Select(s => s.Instance).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var next = backup with
        {
            Accounts = [.. backup.Accounts.Select(a => a with { Grants = [] })],
            Services = [.. backup.Services.Select(s => missing.Contains(s.Instance) ? s with { Enabled = false } : s)],
        };
        return (next, disabled);
    }

    // ---------- the stage ----------

    private static string StageDir(AppPaths p) => Path.Combine(p.Imports, "staged");
    private static string PendingPath(AppPaths p) => Path.Combine(p.Imports, "pending.json");
    private static string ResultPath(AppPaths p) => Path.Combine(p.Imports, "result.json");
    private static string RestorePointDir(AppPaths p) => Path.Combine(p.Imports, "restore-point");

    /// <summary>The user-facing code for a failed write: "disk-full" when the disk is full, else <paramref name="otherwise"/>.</summary>
    internal static string FailureCode(Exception e, string otherwise) => e is IOException io && StorageErrors.IsDiskFull(io) ? "disk-full" : otherwise;

    internal static void Stage(AppPaths p, BackupPending pending, byte[] settingsBytes, byte[] secretsBytes, IFaultPoint? faults = null)
    {
        ClearStage(p);
        PrivateFolder.Ensure(p.Imports);
        Directory.CreateDirectory(StageDir(p));
        AtomicFile.WriteFlushed(Path.Combine(StageDir(p), "settings.yaml"), settingsBytes);
        faults?.Hit("import-stage:settings");
        AtomicFile.WriteFlushed(Path.Combine(StageDir(p), "secrets.dat"), secretsBytes);
        faults?.Hit("import-stage:secrets");
        WritePending(p, pending);
        faults?.Hit("import-stage:pending");
    }

    internal static void ClearStage(AppPaths p)
    {
        try { if (File.Exists(PendingPath(p))) File.Delete(PendingPath(p)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { if (Directory.Exists(StageDir(p))) Directory.Delete(StageDir(p), recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static BackupPending? ReadPending(AppPaths p)
    {
        try { return File.Exists(PendingPath(p)) ? JsonSerializer.Deserialize(File.ReadAllBytes(PendingPath(p)), BackupJson.Default.BackupPending) : null; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    internal static void WritePending(AppPaths p, BackupPending pending) => WriteReplacing(PendingPath(p), JsonSerializer.SerializeToUtf8Bytes(pending, BackupJson.Default.BackupPending));

    private static void WriteReplacing(string path, byte[] bytes)
    {
        PrivateFolder.Ensure(Path.GetDirectoryName(path)!);
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        AtomicFile.WriteFlushed(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    internal static bool StagedMatches(AppPaths p, BackupPending pending)
        => AtomicFile.HashOf(Path.Combine(StageDir(p), "settings.yaml")) == pending.SettingsHash && AtomicFile.HashOf(Path.Combine(StageDir(p), "secrets.dat")) == pending.SecretsHash;

    internal static BackupApplyResult? ReadResult(AppPaths p)
    {
        try
        {
            if (!File.Exists(ResultPath(p))) return null;
            var r = JsonSerializer.Deserialize(File.ReadAllBytes(ResultPath(p)), BackupJson.Default.BackupResult);
            return r is null ? null : new BackupApplyResult(r.State, r.Error, r.Source, r.Disabled, DateTimeOffset.TryParse(r.AtUtc, out var at) ? at : DateTimeOffset.MinValue);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    internal static void ClearResult(AppPaths p)
    {
        try { if (File.Exists(ResultPath(p))) File.Delete(ResultPath(p)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static bool HasRestorePoint(AppPaths p) => File.Exists(Path.Combine(RestorePointDir(p), "meta.json")) && File.Exists(Path.Combine(RestorePointDir(p), "settings.yaml"));

    internal static bool StageRestorePoint(AppPaths p, ISecretProtector protector, IClock clock, IFaultPoint? faults = null)
    {
        if (!HasRestorePoint(p)) return false;
        string dir = RestorePointDir(p);
        byte[] settingsBytes = File.ReadAllBytes(Path.Combine(dir, "settings.yaml"));
        if (SettingsYaml.Read(Decode(settingsBytes)).Settings is null) return false;
        byte[] secretsBytes = File.Exists(Path.Combine(dir, "secrets.dat")) ? File.ReadAllBytes(Path.Combine(dir, "secrets.dat")) : SecretStore.BuildFile(protector, []);
        if (!SecretStore.IsValidFile(secretsBytes)) return false; // F17V-4: a damaged restore point is never offered
        string token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var pending = new BackupPending(token, "Ready", AtomicFile.Hash(settingsBytes), AtomicFile.Hash(secretsBytes), null, 0, clock.UtcNow.UtcDateTime.ToString("O"), "undo", 0);
        Stage(p, pending, settingsBytes, secretsBytes, faults);
        return true;
    }

    // ---------- the switch at start ----------

    /// <summary>
    /// Run at start, after <see cref="ConfigTransaction.Recover"/> and before settings and secrets are loaded. Applies a Ready import, or finishes one
    /// that a power loss interrupted, and leaves the old pair in place when anything fails. A stage that was previewed but never confirmed is dropped.
    /// Returns null when there was nothing to do.
    /// </summary>
    public static BackupApplyResult? ApplyPending(AppPaths paths, IClock clock, IFaultPoint? faults = null)
    {
        if (!Directory.Exists(paths.Imports)) return null;
        if (!File.Exists(PendingPath(paths)))
        {
            if (Directory.Exists(StageDir(paths))) ClearStage(paths); // an orphan from a crash between writing the files and the pending record
            return null;
        }
        var pending = ReadPending(paths);
        if (pending is null) return Fail(paths, clock, "pending-corrupt", "", "backup");
        if (pending.State == "Previewed") { ClearStage(paths); return null; }
        if (pending.State is not ("Ready" or "Applying")) return Fail(paths, clock, "pending-corrupt", pending.Token, pending.Source);
        if (pending.Attempts >= MaxAttempts) return Fail(paths, clock, "too-many-attempts", pending.Token, pending.Source);

        try
        {
            pending = pending with { State = "Applying", Attempts = pending.Attempts + 1 };
            WritePending(paths, pending);
            faults?.Hit("import:begin");
            if (!StagedMatches(paths, pending)) return Fail(paths, clock, "staged-tampered", pending.Token, pending.Source);
            byte[] staged = File.ReadAllBytes(Path.Combine(StageDir(paths), "settings.yaml"));
            byte[] secretsBytes = File.ReadAllBytes(Path.Combine(StageDir(paths), "secrets.dat"));
            var (settings, _) = SettingsYaml.Read(Decode(staged));
            if (settings is null) return Fail(paths, clock, "settings-invalid", pending.Token, pending.Source);
            if (!SecretStore.IsValidFile(secretsBytes)) return Fail(paths, clock, "settings-invalid", pending.Token, pending.Source);

            // The final settings bytes (revision moved past the current file's) are fixed once and recorded, so a retry after a power loss compares
            // against the same bytes and recognises a commit that already happened.
            string finalPath = Path.Combine(StageDir(paths), "settings.final.yaml");
            byte[] finalBytes;
            if (pending.FinalSettingsHash is null)
            {
                long top = Math.Max(CurrentRevision(paths), settings.Revision);
                if (top < 0 || top >= long.MaxValue - 1) return Fail(paths, clock, "settings-invalid", pending.Token, pending.Source); // F17V-3: no overflow
                finalBytes = Encoding.UTF8.GetBytes(SettingsYaml.Write(settings with { Revision = top + 1 }));
                // never write a settings.yaml the loader would reject
                if (SettingsYaml.Read(Decode(finalBytes)).Settings is null) return Fail(paths, clock, "settings-invalid", pending.Token, pending.Source);
                if (File.Exists(finalPath)) File.Delete(finalPath);
                AtomicFile.WriteFlushed(finalPath, finalBytes);
                pending = pending with { FinalSettingsHash = AtomicFile.Hash(finalBytes) };
                WritePending(paths, pending);
            }
            else
            {
                finalBytes = File.Exists(finalPath) ? File.ReadAllBytes(finalPath) : [];
                if (AtomicFile.Hash(finalBytes) != pending.FinalSettingsHash) return Fail(paths, clock, "staged-tampered", pending.Token, pending.Source);
            }

            bool alreadyCommitted = AtomicFile.HashOf(paths.Settings) == pending.FinalSettingsHash && AtomicFile.HashOf(paths.Secrets) == pending.SecretsHash;
            if (!alreadyCommitted)
            {
                EnsureRestorePoint(paths, pending.Token, clock, faults);
                faults?.Hit("import:before-commit");
                new ConfigTransaction(paths, faults).Commit([(paths.Settings, finalBytes), (paths.Secrets, secretsBytes)]);
            }
            try
            {
                if (!alreadyCommitted) faults?.Hit("import:committed");
                var applied = new BackupResult("Applied", null, pending.Token, clock.UtcNow.UtcDateTime.ToString("O"), pending.Disabled, pending.Source);
                WriteReplacing(ResultPath(paths), JsonSerializer.SerializeToUtf8Bytes(applied, BackupJson.Default.BackupResult));
                ClearStage(paths);
                faults?.Hit("import:done");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The switch is done and durable; only the bookkeeping failed (disk full, lock). It is reported as applied, never as failed, and the next
                // start recognises the finished commit and tidies up.
                return new BackupApplyResult("Applied", null, pending.Source, pending.Disabled, clock.UtcNow);
            }
            return ReadResult(paths);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Disk full or a locked file: ConfigTransaction has put the old pair back. If even that rollback failed it is finished here, so this start never
            // runs on a half-switched pair (the journal is also recovered at every start). The import is dropped, the user can try again.
            try { new ConfigTransaction(paths).Recover(); } catch (Exception r) when (r is IOException or UnauthorizedAccessException) { }
            return Fail(paths, clock, FailureCode(e, "io"), pending.Token, pending.Source);
        }
    }

    private static void EnsureRestorePoint(AppPaths paths, string token, IClock clock, IFaultPoint? faults)
    {
        string dir = RestorePointDir(paths);
        string metaPath = Path.Combine(dir, "meta.json");
        try
        {
            if (File.Exists(metaPath) && JsonSerializer.Deserialize(File.ReadAllBytes(metaPath), BackupJson.Default.BackupRestorePointMeta)?.Token == token) return; // made by an earlier try of this import
        }
        catch (JsonException) { }
        string temp = dir + ".tmp";
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        PrivateFolder.Ensure(paths.Imports);
        Directory.CreateDirectory(temp);
        bool hadSettings = File.Exists(paths.Settings), hadSecrets = File.Exists(paths.Secrets);
        if (hadSettings) AtomicFile.WriteFlushed(Path.Combine(temp, "settings.yaml"), File.ReadAllBytes(paths.Settings));
        if (hadSecrets) AtomicFile.WriteFlushed(Path.Combine(temp, "secrets.dat"), File.ReadAllBytes(paths.Secrets));
        AtomicFile.WriteFlushed(Path.Combine(temp, "meta.json"), JsonSerializer.SerializeToUtf8Bytes(
            new BackupRestorePointMeta(token, hadSettings, hadSecrets, clock.UtcNow.UtcDateTime.ToString("O")), BackupJson.Default.BackupRestorePointMeta));
        faults?.Hit("import:restore-point-written");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.Move(temp, dir);
    }

    private static BackupApplyResult? Fail(AppPaths paths, IClock clock, string code, string token, string source)
    {
        int disabled = ReadPending(paths)?.Disabled ?? 0;
        ClearStage(paths);
        try { if (Directory.Exists(RestorePointDir(paths) + ".tmp")) Directory.Delete(RestorePointDir(paths) + ".tmp", recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        var failed = new BackupResult("Failed", code, token, clock.UtcNow.UtcDateTime.ToString("O"), disabled, source);
        try { WriteReplacing(ResultPath(paths), JsonSerializer.SerializeToUtf8Bytes(failed, BackupJson.Default.BackupResult)); }
        catch (IOException) { }
        return ReadResult(paths) ?? new BackupApplyResult("Failed", code, source, disabled, clock.UtcNow);
    }

    private static long CurrentRevision(AppPaths paths)
    {
        try { return File.Exists(paths.Settings) ? SettingsYaml.Read(Decode(File.ReadAllBytes(paths.Settings))).Settings?.Revision ?? 0 : 0; }
        catch (IOException) { return 0; }
    }

    private static string Decode(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.StartsWith(Encoding.UTF8.Preamble)) span = span[3..];
        return Encoding.UTF8.GetString(span);
    }
}
