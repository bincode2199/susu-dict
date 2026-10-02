using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Susu.Abstractions;
using Susu.Plugins.AppUpdate;
using Susu.Storage;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F18.2 staged update, paired backup, health check, rollback and recovery (TEST-PLAN UPD06). The install folder is a temp folder (susu-f18-*); the real
/// install, the real running app and system settings are never touched. A crash is <see cref="SimulatedCrash"/> thrown from a named step (the updater
/// never handles it, like a killed process); a full disk is an IOException with ERROR_DISK_FULL.
/// </summary>
public class F18UpdateApplyTests
{
    private static IOException DiskFull() => new("There is not enough space on the disk.", unchecked((int)0x80070070));

    private sealed class Recorder : IFaultPoint
    {
        public readonly List<string> Hits = [];
        public void Hit(string stage) => Hits.Add(stage);
    }

    private sealed class FakeEnv(string dbPath) : IUpdateEnvironment
    {
        public UpdateExitOutcome Exit = UpdateExitOutcome.Exited;
        public bool MigrateOk = true, HealthOk = true;
        public long Free = long.MaxValue;
        public int Stops, Migrations, Probes;
        public UpdateExitOutcome StopApp(TimeSpan timeout) { Stops++; return Exit; }
        public bool Migrate(string installDirectory, string databasePath, out string? detail)
        {
            Migrations++;
            detail = MigrateOk ? null : "boom";
            Execute(dbPath, "INSERT INTO notes(text) VALUES ('migrated');"); // the new build's migration changes the database
            return MigrateOk;
        }
        public bool HealthCheck(string installDirectory, string databasePath, string expectedVersion, out string? detail) { Probes++; detail = HealthOk ? null : "unhealthy"; return HealthOk; }
        public long FreeBytes(string path) => Free;
    }

    private sealed class Rig
    {
        public string Root = TestTemp.NewDir("susu-f18-update");
        public string Install => Path.Combine(Root, "app");
        public string Db => Path.Combine(Root, "data", "susu.db");
        public string Updates => Path.Combine(Root, "data", "updates");
        public FakeEnv Env;
        public IUpdateEnvironment? Override;
        public byte[] Zip = [];
        public AppUpdateOffer Offer = null!;
        public string ZipPath => Path.Combine(Updates, "downloads", "package.zip");
        /// <summary>The package sits in the updater's downloads folder (BeginDownload clears that folder first).</summary>
        public void Place() { Directory.CreateDirectory(Path.GetDirectoryName(ZipPath)!); File.WriteAllBytes(ZipPath, Zip); }
        public Dictionary<string, string> Old = new()
        {
            ["susu.exe"] = "OLD exe 1.1.0", ["susu_native.dll"] = "OLD native", ["ui/index.html"] = "OLD ui", ["old-only.dll"] = "only in the old version",
        };
        public Dictionary<string, string> New = new()
        {
            ["susu.exe"] = "NEW exe 1.2.0", ["susu_native.dll"] = "NEW native", ["ui/index.html"] = "NEW ui", ["ui/app.js"] = "NEW js",
        };

        public Rig(Dictionary<string, string>? newFiles = null)
        {
            if (newFiles is not null) New = newFiles;
            // Like the real installer (staged-manifest.json ships in the install folder and in every package) so the updater knows which files an old version brought.
            Old["staged-manifest.json"] = ManifestOf(Old.Keys);
            if (!New.ContainsKey("staged-manifest.json")) New = new Dictionary<string, string>(New) { ["staged-manifest.json"] = ManifestOf(New.Keys) };
            Directory.CreateDirectory(Install);
            Directory.CreateDirectory(Path.GetDirectoryName(Db)!);
            foreach (var (path, text) in Old) Put(Install, path, text);
            Execute(Db, "PRAGMA journal_mode=WAL; CREATE TABLE notes(id INTEGER PRIMARY KEY, text TEXT); INSERT INTO notes(text) VALUES ('user data one'), ('user data two');");
            Env = new FakeEnv(Db);
            SetPackage(New);
        }

        public void SetPackage(Dictionary<string, string> files)
        {
            Zip = BuildZip(files.Select(f => (f.Key, Encoding.UTF8.GetBytes(f.Value))));
            Place();
            Offer = new AppUpdateOffer("1.2.0", 5, "susu-1.2.0.zip", Zip.Length, Convert.ToHexStringLower(SHA256.HashData(Zip)), "");
        }

        public AppUpdater Updater(IFaultPoint? faults = null) => new(Install, Db, Updates, Override ?? Env, faults);

        public AppUpdater Staged(IFaultPoint? faults = null)
        {
            var updater = Updater(faults);
            updater.BeginDownload(Offer, "1.1.0"); Place();
            Assert.True(updater.Stage(ZipPath).Ok);
            return updater;
        }
    }

    private static string ManifestOf(IEnumerable<string> paths) => "{\"product\":\"Su-Su\",\"files\":[" + string.Join(",", paths.Select(p => "{\"path\":\"" + p + "\"}")) + "]}";

    private static void Put(string root, string rel, string text)
    {
        string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static byte[] BuildZip(IEnumerable<(string Name, byte[] Bytes)> entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(bytes);
            }
        return stream.ToArray();
    }

    private static void Execute(string db, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string[] Rows(string db)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT text FROM notes ORDER BY id;";
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add(r.GetString(0));
        return [.. rows];
    }

    private static SortedDictionary<string, string> Snapshot(string dir)
        => new(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(dir, p).Replace('\\', '/'), p => File.ReadAllText(p)));

    private static void AssertOld(Rig rig)
    {
        Assert.Equal(new SortedDictionary<string, string>(rig.Old), Snapshot(rig.Install));
        Assert.Equal(["user data one", "user data two"], Rows(rig.Db));
    }

    private static void AssertNew(Rig rig)
    {
        Assert.Equal(new SortedDictionary<string, string>(rig.New), Snapshot(rig.Install));
        Assert.Equal(["user data one", "user data two", "migrated"], Rows(rig.Db));
    }

    // ================= happy path =================

    [Fact]
    public void A_staged_update_is_applied_migrated_health_checked_and_committed_without_losing_user_data()
    {
        var rig = new Rig();
        var updater = rig.Staged();
        Assert.Equal(UpdateStages.Staged, updater.Read()!.Stage);
        AssertOld(rig); // staging changed nothing
        var result = updater.Apply();
        Assert.Equal(new ApplyResult("committed", null), result);
        AssertNew(rig);
        Assert.Equal(UpdateStages.Committed, updater.Read()!.Stage);
        Assert.False(Directory.Exists(updater.StageFolder));
        Assert.False(Directory.Exists(updater.DownloadsFolder));
        Assert.Equal((1, 1, 1), (rig.Env.Stops, rig.Env.Migrations, rig.Env.Probes));
        // The paired backup of the old version stays for the old-version fallback (F18.3): old binaries and the database as they were.
        Assert.Equal("OLD exe 1.1.0", File.ReadAllText(Path.Combine(updater.BackupFolder, "app", "susu.exe")));
        Assert.Equal(["user data one", "user data two"], Rows(Path.Combine(updater.BackupFolder, "susu.db")));
        Assert.Equal("committed-cleanup", rig.Updater().Recover().Action);
        AssertNew(rig);
    }

    [Fact]
    public void The_journal_records_every_stage_in_order()
    {
        var rig = new Rig();
        var stages = new List<string>();
        AppUpdater? updater = null;
        var spy = new Spy(() => stages.Add(updater!.Read()!.Stage));
        updater = rig.Staged(spy);
        updater.Apply();
        var distinct = stages.Where((s, i) => i == 0 || s != stages[i - 1]).ToList();
        Assert.Equal([UpdateStages.Exiting, UpdateStages.BackedUp, UpdateStages.Replacing, UpdateStages.Replaced, UpdateStages.Migrating, UpdateStages.Migrated,
            UpdateStages.HealthChecking, UpdateStages.Healthy, UpdateStages.Committed], distinct);
    }

    private sealed class Spy(Action onHit) : IFaultPoint
    {
        public void Hit(string stage) { if (stage.StartsWith("stage:", StringComparison.Ordinal) || stage.StartsWith("download:", StringComparison.Ordinal)) return; onHit(); }
    }

    // ================= health check and migration failure: the pair is rolled back =================

    [Fact]
    public void A_failed_health_check_rolls_the_old_binaries_and_the_old_database_back_together()
    {
        var rig = new Rig { Env = null! };
        rig.Env = new FakeEnv(rig.Db) { HealthOk = false };
        var updater = rig.Staged();
        var result = updater.Apply();
        Assert.Equal(new ApplyResult("rolled-back", "health-check-failed"), result);
        Assert.Equal(1, rig.Env.Migrations); // the new build did migrate (the 'migrated' row existed) and that migration was undone with the binaries
        AssertOld(rig);
        var journal = updater.Read()!;
        Assert.Equal((UpdateStages.RolledBack, "health-check-failed"), (journal.Stage, journal.Error));
        Assert.Empty(Directory.EnumerateFiles(rig.Install, "*.susu-new", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_failed_migration_rolls_back_the_pair_and_a_database_file_with_wal_content_is_restored_whole()
    {
        var rig = new Rig();
        rig.Env.MigrateOk = false;
        Assert.Equal(new ApplyResult("rolled-back", "migration-failed"), rig.Staged().Apply());
        AssertOld(rig);
        Assert.False(File.Exists(rig.Db + "-wal") && new FileInfo(rig.Db + "-wal").Length > 0 && Rows(rig.Db).Contains("migrated"));
    }

    [Fact]
    public void Files_tampered_after_the_replacement_fail_the_health_check_and_roll_back()
    {
        var rig = new Rig();
        rig.Override = new TamperEnv(rig.Install);
        Assert.Equal(new ApplyResult("rolled-back", "health-check-failed"), rig.Staged().Apply());
        AssertOld(rig);
    }

    private sealed class TamperEnv(string install) : IUpdateEnvironment
    {
        public UpdateExitOutcome StopApp(TimeSpan timeout) => UpdateExitOutcome.Exited;
        public bool Migrate(string i, string d, out string? detail) { detail = null; File.WriteAllText(Path.Combine(install, "susu.exe"), "TAMPERED"); return true; }
        public bool HealthCheck(string i, string d, string v, out string? detail) { detail = null; return true; }
        public long FreeBytes(string path) => long.MaxValue;
    }

    [Fact]
    public void An_app_that_will_not_exit_aborts_the_update_before_anything_changes_and_it_can_be_retried()
    {
        var rig = new Rig();
        rig.Env.Exit = UpdateExitOutcome.TimedOut;
        var updater = rig.Staged();
        Assert.Equal(new ApplyResult("aborted", "exit-timeout"), updater.Apply());
        AssertOld(rig);
        Assert.Equal(UpdateStages.Staged, updater.Read()!.Stage);
        rig.Env.Exit = UpdateExitOutcome.NotRunning;
        Assert.Equal("committed", updater.Apply().Outcome);
        AssertNew(rig);
    }

    // ================= hash, size, package safety =================

    [Fact]
    public void A_wrong_hash_a_truncated_download_and_an_oversized_download_are_rejected_and_nothing_is_kept()
    {
        foreach (var (mutate, expected) in new (Func<byte[], byte[]>, string)[]
        {
            (z => { var c = (byte[])z.Clone(); c[c.Length / 2] ^= 0xFF; return c; }, "hash-mismatch"),
            (z => z[..^10], "download-truncated"),
            (z => z[..(z.Length / 2)], "download-truncated"),
            (z => [], "download-truncated"),
            (z => [.. z, 0, 0, 0], "size-mismatch"),
        })
        {
            var rig = new Rig();
            var updater = rig.Updater();
            updater.BeginDownload(rig.Offer, "1.1.0"); rig.Place();
            File.WriteAllBytes(rig.ZipPath, mutate(rig.Zip));
            var result = updater.Stage(rig.ZipPath);
            Assert.Equal(new StageResult(false, expected), result);
            Assert.Null(updater.Read());
            Assert.False(Directory.Exists(updater.StageFolder));
            AssertOld(rig);
            Assert.Equal("not-staged", updater.Apply().Outcome);
        }
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("..\\evil.dll")]
    [InlineData("/abs/evil.dll")]
    [InlineData("C:\\evil.dll")]
    [InlineData("sub/../../evil.dll")]
    [InlineData("susu.exe:stream")]
    [InlineData("ui/CON.txt")]
    [InlineData("ui/trailing.")]
    [InlineData("ui/x.susu-new")]
    public void A_package_with_an_unsafe_entry_path_is_rejected(string name)
    {
        var rig = new Rig();
        rig.SetPackage(new Dictionary<string, string>(rig.New) { [name] = "evil" });
        var updater = rig.Updater();
        updater.BeginDownload(rig.Offer, "1.1.0"); rig.Place();
        Assert.Equal(new StageResult(false, "package-invalid"), updater.Stage(rig.ZipPath));
        Assert.False(File.Exists(Path.Combine(rig.Root, "evil.dll")));
        AssertOld(rig);
    }

    [Fact]
    public void A_package_without_the_main_executable_duplicate_names_or_too_many_entries_is_rejected()
    {
        var rig = new Rig();
        foreach (var files in new[]
        {
            new Dictionary<string, string> { ["other.dll"] = "x" },
            Enumerable.Range(0, AppUpdater.MaxEntries + 1).ToDictionary(i => $"f{i}.txt", _ => "x").Concat([new KeyValuePair<string, string>("susu.exe", "x")]).ToDictionary(p => p.Key, p => p.Value),
        })
        {
            rig.SetPackage(files);
            var updater = rig.Updater();
            updater.BeginDownload(rig.Offer, "1.1.0"); rig.Place();
            Assert.Equal("package-invalid", updater.Stage(rig.ZipPath).Error);
        }
        // Names that differ only in case are one file on Windows.
        rig.Zip = BuildZip([("susu.exe", [1]), ("Susu.EXE", [2])]);
        rig.Offer = rig.Offer with { Size = rig.Zip.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(rig.Zip)) };
        var u2 = rig.Updater();
        u2.BeginDownload(rig.Offer, "1.1.0"); rig.Place();
        Assert.Equal("package-invalid", u2.Stage(rig.ZipPath).Error);
        AssertOld(rig);
    }

    [Fact]
    public void A_staged_file_changed_after_verification_is_caught_before_the_app_is_stopped()
    {
        var rig = new Rig();
        var updater = rig.Staged();
        File.WriteAllText(Path.Combine(updater.StageFolder, "susu.exe"), "swapped after hashing");
        Assert.Equal(new ApplyResult("not-staged", "stage-corrupt"), updater.Apply());
        Assert.Equal(0, rig.Env.Stops);
        AssertOld(rig);
    }

    // ================= disk full =================

    [Fact]
    public void Not_enough_free_space_is_reported_before_anything_is_touched()
    {
        var rig = new Rig();
        rig.Env.Free = 1024;
        var updater = rig.Staged();
        Assert.Equal(new ApplyResult("aborted", "disk-full"), updater.Apply());
        AssertOld(rig);
        Assert.False(Directory.Exists(updater.BackupFolder));
        Assert.Equal(UpdateStages.Staged, updater.Read()!.Stage);
    }

    [Theory]
    [InlineData("backup:app:0", "aborted")]
    [InlineData("backup:db", "aborted")]
    [InlineData("backup:index", "aborted")]
    [InlineData("replace:0", "rolled-back")]
    [InlineData("replace:2", "rolled-back")]
    [InlineData("replace:deleted", "rolled-back")]
    [InlineData("migrate:done", "rolled-back")]
    public void A_full_disk_at_a_step_leaves_the_old_version_and_its_data_in_place_with_a_clear_error(string stage, string outcome)
    {
        var rig = new Rig();
        var updater = rig.Staged(new FaultAt(stage, _ => DiskFull()));
        Assert.Equal(new ApplyResult(outcome, "disk-full"), updater.Apply());
        AssertOld(rig);
        Assert.Empty(Directory.EnumerateFiles(rig.Install, "*.susu-new", SearchOption.AllDirectories));
        if (outcome == "aborted") Assert.Equal(UpdateStages.Staged, updater.Read()!.Stage);
        // With space again the same update goes through.
        if (outcome == "aborted") { Assert.Equal("committed", rig.Updater().Apply().Outcome); AssertNew(rig); }
    }

    [Fact]
    public void A_full_disk_while_unpacking_discards_the_download_and_keeps_the_installed_version()
    {
        var rig = new Rig();
        var updater = rig.Updater(new FaultAt("stage:extracted", _ => DiskFull()));
        updater.BeginDownload(rig.Offer, "1.1.0"); rig.Place();
        Assert.Equal(new StageResult(false, "disk-full"), updater.Stage(rig.ZipPath));
        Assert.Null(updater.Read());
        Assert.False(Directory.Exists(updater.StageFolder));
        AssertOld(rig);
    }

    // ================= forced interruption at every stage =================

    private static List<string> HitsOf(bool failHealth)
    {
        var rig = new Rig();
        rig.Env.HealthOk = !failHealth;
        var recorder = new Recorder();
        var updater = rig.Updater(recorder);
        updater.BeginDownload(rig.Offer, "1.1.0"); rig.Place();
        updater.Stage(rig.ZipPath);
        updater.Apply();
        return recorder.Hits;
    }

    [Fact]
    public void The_kill_point_table_covers_download_exit_backup_replace_migration_health_check_commit_and_rollback()
    {
        var forward = HitsOf(failHealth: false);
        var backward = HitsOf(failHealth: true);
        string[] expected = ["download:begin", "stage:begin", "stage:hashed", "stage:extracted", "stage:journal", "exit:journal", "exit:requested", "exit:done", "backup:begin", "backup:app:0", "backup:app:3",
            "backup:db", "backup:index", "backup:journal", "replace:journal", "replace:0", "replace:3", "replace:deleted", "replace:done", "migrate:begin", "migrate:done", "health:begin", "health:done",
            "health:journal", "commit:journal", "commit:cleanup"];
        foreach (string hit in expected) Assert.Contains(hit, forward);
        foreach (string hit in new[] { "rollback:journal", "rollback:file:0", "rollback:files", "rollback:db", "rollback:done" }) Assert.Contains(hit, backward);
    }

    private static string[] KillPoints()
    {
        var all = HitsOf(false).Concat(HitsOf(true)).Distinct().ToList();
        return [.. all];
    }

    /// <summary>The invariant after any interruption and one recovery: the install folder and the database are a matching pair (all old or all new and committed), no temp leftovers.</summary>
    private static string AssertPair(Rig rig, AppUpdater recovered)
    {
        var journal = recovered.Read();
        Assert.Empty(Directory.EnumerateFiles(rig.Install, "*.susu-new", SearchOption.AllDirectories));
        var files = Snapshot(rig.Install);
        if (journal?.Stage == UpdateStages.Committed)
        {
            AssertNew(rig);
            return "new";
        }
        AssertOld(rig);
        return "old";
    }

    [Fact]
    public void An_interruption_at_every_step_of_a_forward_update_recovers_to_a_matching_pair_and_the_update_can_be_retried()
    {
        string[] points = KillPoints().Where(p => !p.StartsWith("rollback:", StringComparison.Ordinal)).ToArray();
        Assert.True(points.Length >= 24, string.Join(",", points));
        var outcomes = new Dictionary<string, string>();
        foreach (string point in points)
        {
            var rig = new Rig();
            var updater = rig.Updater(new FaultAt(point));
            try
            {
                updater.BeginDownload(rig.Offer, "1.1.0"); rig.Place();
                updater.Stage(rig.ZipPath);
                updater.Apply();
                Assert.Fail("no crash at " + point);
            }
            catch (SimulatedCrash) { }
            // The process is gone: a fresh updater, as at the next start, recovers.
            var fresh = rig.Updater();
            var recovery = fresh.Recover();
            Assert.NotEqual("failed", recovery.Action);
            outcomes[point] = AssertPair(rig, fresh);
            // Recovery is idempotent.
            Assert.NotEqual("failed", rig.Updater().Recover().Action);
            AssertPair(rig, rig.Updater());
            // And the user can update again (stage again unless the staged copy survived).
            var retry = rig.Updater();
            if (retry.Read()?.Stage != UpdateStages.Staged && retry.Read()?.Stage != UpdateStages.Committed)
            {
                retry.BeginDownload(rig.Offer, "1.1.0"); rig.Place();
                Assert.True(retry.Stage(rig.ZipPath).Ok, point);
            }
            if (retry.Read()?.Stage == UpdateStages.Staged)
            {
                Assert.Equal("committed", retry.Apply().Outcome);
                AssertNew(rig);
            }
        }
        // Before the first file is replaced the old version simply continues; commit:cleanup is after the commit marker so the new pair stays.
        Assert.Equal("new", outcomes["commit:cleanup"]);
        Assert.Equal("new", outcomes["commit:journal"]); // the marker is written: the new pair stays
        Assert.Equal("old", outcomes["health:journal"]); // healthy but not yet committed: rolled back
        Assert.All(points.Where(p => p is not ("commit:cleanup" or "commit:journal")), p => Assert.Equal("old", outcomes[p]));
    }

    [Fact]
    public void An_interruption_during_a_rollback_is_finished_by_the_next_start()
    {
        string[] points = KillPoints().Where(p => p.StartsWith("rollback:", StringComparison.Ordinal)).ToArray();
        Assert.True(points.Length >= 5);
        foreach (string point in points)
        {
            var rig = new Rig();
            rig.Env.HealthOk = false;
            var updater = rig.Staged(new FaultAt(point));
            Assert.Throws<SimulatedCrash>(() => updater.Apply());
            var fresh = rig.Updater();
            Assert.Equal(point == "rollback:done" ? "none" : "rolled-back", fresh.Recover().Action); // after rollback:done the journal already says RolledBack
            AssertOld(rig);
            Assert.Equal(UpdateStages.RolledBack, fresh.Read()!.Stage);
        }
    }

    [Fact]
    public void Power_loss_in_the_middle_of_the_replacement_with_a_torn_file_and_a_leftover_temp_file_is_restored_from_the_pair()
    {
        var rig = new Rig();
        var updater = rig.Staged(new FaultAt("replace:1"));
        Assert.Throws<SimulatedCrash>(() => updater.Apply());
        // What a power cut can leave: a half-written target, a partly copied temp file, and the database already touched by the new build.
        File.WriteAllText(Path.Combine(rig.Install, "susu_native.dll"), "NEW na"); // torn
        File.WriteAllText(Path.Combine(rig.Install, "ui", "index.html.susu-new"), "partial");
        Execute(rig.Db, "INSERT INTO notes(text) VALUES ('written by the new build before the cut');");
        var fresh = rig.Updater();
        Assert.Equal("rolled-back", fresh.Recover().Action);
        AssertOld(rig);
        Assert.False(File.Exists(Path.Combine(rig.Install, "ui", "app.js"))); // a file the new version added is gone again
    }

    [Fact]
    public void A_damaged_journal_with_a_backup_pair_is_rolled_back_and_a_damaged_journal_alone_is_cleared()
    {
        var rig = new Rig();
        var updater = rig.Staged(new FaultAt("replace:1"));
        Assert.Throws<SimulatedCrash>(() => updater.Apply());
        File.WriteAllText(Path.Combine(rig.Updates, "journal.json"), "{ torn");
        File.WriteAllText(Path.Combine(rig.Updates, "journal.json.prev"), "also torn");
        var fresh = rig.Updater();
        Assert.Equal("rolled-back", fresh.Recover().Action);
        AssertOld(rig);
        Assert.Null(fresh.Read());

        var alone = new Rig();
        Directory.CreateDirectory(alone.Updates);
        File.WriteAllText(Path.Combine(alone.Updates, "journal.json"), "garbage");
        Assert.Equal("cleared", alone.Updater().Recover().Action);
        AssertOld(alone);
    }

    [Fact]
    public void A_backup_that_is_damaged_cannot_be_restored_silently_and_the_rollback_is_reported_incomplete_and_retried()
    {
        var rig = new Rig { Env = null! };
        rig.Env = new FakeEnv(rig.Db) { HealthOk = false };
        var updater = rig.Staged(new DamageBackup(rig.Updates));
        var result = updater.Apply();
        Assert.Equal(new ApplyResult("failed", "rollback-incomplete"), result);
        Assert.Equal(UpdateStages.RollingBack, updater.Read()!.Stage); // not "rolled back": the next start tries again
        Assert.Equal("failed", rig.Updater().Recover().Action);
    }

    private sealed class DamageBackup(string updates) : IFaultPoint
    {
        public void Hit(string stage)
        {
            if (stage == "rollback:journal") File.WriteAllText(Path.Combine(updates, "backup", "app", "susu.exe"), "damaged backup");
        }
    }

    [Fact]
    public void A_staged_update_survives_a_restart_and_a_partial_download_is_cleared()
    {
        var rig = new Rig();
        rig.Staged();
        var fresh = rig.Updater();
        Assert.Equal(new RecoveryResult("none", UpdateStages.Staged), fresh.Recover());
        Assert.Equal("committed", fresh.Apply().Outcome);

        var partial = new Rig();
        var u = partial.Updater();
        u.BeginDownload(partial.Offer, "1.1.0"); partial.Place();
        File.WriteAllBytes(Path.Combine(u.DownloadsFolder, partial.Offer.FileName), partial.Zip[..100]);
        Assert.Equal("cleared", partial.Updater().Recover().Action);
        Assert.False(Directory.Exists(u.DownloadsFolder));
        Assert.Null(partial.Updater().Read());
    }

    [Fact]
    public void An_update_replaces_a_larger_install_and_normal_use_loses_nothing_across_two_consecutive_updates()
    {
        var rig = new Rig();
        Assert.Equal("committed", rig.Staged().Apply().Outcome);
        Execute(rig.Db, "INSERT INTO notes(text) VALUES ('written by the user after the first update');");
        // Second update: 1.3.0
        rig.Old = new Dictionary<string, string>(rig.New);
        rig.New = new Dictionary<string, string> { ["susu.exe"] = "NEWER exe", ["ui/index.html"] = "NEWER ui" };
        rig.SetPackage(rig.New);
        rig.Offer = rig.Offer with { Version = "1.3.0", Sequence = 6 };
        var updater = rig.Updater();
        updater.BeginDownload(rig.Offer, "1.2.0"); rig.Place();
        Assert.True(updater.Stage(rig.ZipPath).Ok);
        Assert.Equal("committed", updater.Apply().Outcome);
        Assert.Equal(new SortedDictionary<string, string>(rig.New), Snapshot(rig.Install));
        Assert.Equal(["user data one", "user data two", "migrated", "written by the user after the first update", "migrated"], Rows(rig.Db));
        // The backup is now the 1.2.0 pair.
        Assert.Equal("NEW exe 1.2.0", File.ReadAllText(Path.Combine(updater.BackupFolder, "app", "susu.exe")));
    }

    [Fact]
    public void Database_copy_includes_committed_pages_that_are_still_only_in_the_wal_file()
    {
        string dir = TestTemp.NewDir("susu-f18-wal");
        string db = Path.Combine(dir, "a.db");
        Execute(db, "PRAGMA journal_mode=WAL; CREATE TABLE notes(id INTEGER PRIMARY KEY, text TEXT);");
        using var open = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        open.Open();
        using (var cmd = open.CreateCommand()) { cmd.CommandText = "PRAGMA wal_autocheckpoint=0; INSERT INTO notes(text) VALUES ('only in wal');"; cmd.ExecuteNonQuery(); }
        Assert.True(new FileInfo(db + "-wal").Length > 0);
        string copy = Path.Combine(dir, "copy.db");
        Database.CopyFile(db, copy);
        Assert.Equal(["only in wal"], Rows(copy));
        Database.ReplaceFileWithCopy(copy, Path.Combine(dir, "restored.db"));
        Assert.Equal(["only in wal"], Rows(Path.Combine(dir, "restored.db")));
    }
}
