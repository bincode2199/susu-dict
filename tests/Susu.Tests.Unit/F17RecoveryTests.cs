using System.Text;
using Microsoft.Data.Sqlite;
using Susu.Abstractions;
using Susu.Domain;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F17.3 recovery drills (TEST-PLAN DATA03, DATA04 and the F17 exit "old configuration is recoverable before import"). Each drill injects one failure
/// at one step of the F17.1 export / preview / switch, of the ConfigTransaction journal or of a database migration, and checks the three things the
/// recovery note promises: what state the files are in, that the old configuration is intact, and that a retry after the cause is gone works.
/// Faults are injected through <see cref="IFaultPoint"/> (a crash is <see cref="SimulatedCrash"/>, a full disk is an IOException with ERROR_DISK_FULL);
/// a few drills use real failures (a read-only target, a torn file). User-facing texts are checked in ui/tests/f17-recovery.test.ts.
/// </summary>
public class F17RecoveryTests
{
    private const string Password = "correct horse battery";
    private static readonly BackupExportOptions WithKeys = new(true, Password);
    private static readonly BackupExportOptions Plain = new(false, null);

    private static IOException DiskFull() => new("There is not enough space on the disk.", unchecked((int)0x80070070));
    private static FaultAt FullAt(string stage) => new(stage, _ => DiskFull());

    private sealed class FaultsAt(params string[] stages) : IFaultPoint
    {
        public void Hit(string stage) { if (stages.Contains(stage)) throw DiskFull(); }
    }

    private static BackupService ServiceWith(BackupRig rig, IFaultPoint faults)
        => new(rig.Paths, rig.Store, rig.Secrets, rig.Protector, rig.Clock, new BackupHost("1.2.3", () => rig.Available, () => rig.UserPlugins, () => rig.Local), faults);

    private static string Staged(BackupRig rig) => Path.Combine(rig.Paths.Imports, "staged");
    private static string PendingFile(BackupRig rig) => Path.Combine(rig.Paths.Imports, "pending.json");
    private static string Label(BackupRig rig) => rig.Store.State.Effective.Accounts.Single().Label;

    private static void AssertNothingStaged(BackupRig rig)
    {
        Assert.False(Directory.Exists(Staged(rig)));
        Assert.False(File.Exists(PendingFile(rig)));
        Assert.Equal("None", rig.Service.Status().State);
    }

    private static void AssertNoTransactionLeft(BackupRig rig)
        => Assert.True(!Directory.Exists(rig.Paths.Transactions) || Directory.GetDirectories(rig.Paths.Transactions).Length == 0);

    private static string JournalState(BackupRig rig)
    {
        if (!Directory.Exists(rig.Paths.Transactions) || Directory.GetDirectories(rig.Paths.Transactions).Length == 0) return "no-folder";
        string dir = Assert.Single(Directory.GetDirectories(rig.Paths.Transactions));
        string journal = Path.Combine(dir, "journal.json");
        if (!File.Exists(journal)) return "no-journal";
        string text = File.ReadAllText(journal);
        return text.Contains("Committed", StringComparison.Ordinal) ? "Committed" : text.Contains("Prepared", StringComparison.Ordinal) ? "Prepared" : "unknown " + text;
    }

    // ================= wrong password =================

    [Fact] // no lockout, no oracle, no partial state
    public void A_wrong_password_many_times_gives_one_answer_whatever_the_file_and_leaves_no_state()
    {
        using var rig = new BackupRig();
        rig.Seed();
        string withKeys = rig.Export("k.susubak", WithKeys);
        string keyless = rig.Export("e.susubak", new BackupExportOptions(false, Password));
        byte[] altered = File.ReadAllBytes(withKeys);
        altered[^1] ^= 0x01; // the tag
        string alteredPath = F17BackupTests.WriteTemp(rig, altered, "altered.susubak");
        byte[] saltChanged = File.ReadAllBytes(withKeys);
        int saltAt = Encoding.Latin1.GetString(saltChanged).IndexOf("\"salt\":\"", StringComparison.Ordinal) + 8;
        Assert.True(saltAt > 8);
        saltChanged[saltAt] = saltChanged[saltAt] == (byte)'A' ? (byte)'B' : (byte)'A'; // a different salt: the header is authenticated and the key differs, so it reads like a wrong password
        string saltPath = F17BackupTests.WriteTemp(rig, saltChanged, "salt.susubak");
        string settings = rig.SettingsHash, secrets = rig.SecretsHash;

        string[] guesses = ["wrong password!!", Password + " ", Password.ToUpperInvariant(), new string('x', 8), new string('é', 256), Password[..^1], " " + Password, Password + "​"]; // not a trailing NUL: HMAC pads keys with zero bytes, so PBKDF2 gives "abc" and "abc\0" one key (a property of PBKDF2; a NUL cannot be typed)
        var answers = new HashSet<string>();
        foreach (string guess in guesses)
            foreach (string file in new[] { withKeys, keyless, alteredPath, saltPath })
            {
                BackupException? e = null;
                try { rig.Service.Preview(file, guess); } catch (BackupException x) { e = x; }
                Assert.True(e is not null, $"the guess of {guess.Length} characters opened {Path.GetFileName(file)}");
                answers.Add(e.Code + "|" + e.Message);
            }
        // the right password on an altered file is the same refusal as a wrong password on a good one (the file is not told apart from the guess)
        foreach (string file in new[] { alteredPath, saltPath })
        {
            var e = Assert.Throws<BackupException>(() => rig.Service.Preview(file, Password));
            answers.Add(e.Code + "|" + e.Message);
        }
        Assert.Equal(["decrypt-failed|decrypt-failed"], answers);

        Assert.Equal(settings, rig.SettingsHash);
        Assert.Equal(secrets, rig.SecretsHash);
        AssertNothingStaged(rig);
        Assert.False(Directory.Exists(rig.Paths.Imports) && Directory.EnumerateFileSystemEntries(rig.Paths.Imports).Any(), "a wrong password wrote nothing at all");

        // not locked out: the right password works at once, and only now is anything staged
        Assert.Equal(1, rig.Service.Preview(withKeys, Password).BackupSecrets);
        Assert.True(Directory.Exists(Staged(rig)));
    }

    [Fact] // the cost of a guess is one key derivation at the file's own (bounded) cost; an absurd guess costs none
    public void Guessing_cost_is_bounded_by_the_file_and_an_absurd_guess_runs_no_key_derivation()
    {
        using var rig = new BackupRig();
        rig.Seed();
        string file = rig.Export("k.susubak", WithKeys);
        string hugeGuess = new('x', BackupLimits.MaxPasswordChars + 1);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 60; i++) Assert.Equal("decrypt-failed", F17BackupTests.CodeOf(() => rig.Service.Preview(file, hugeGuess)));
        // 60 key derivations at 600,000 rounds would take well over 5 s; none run, so this is milliseconds
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"60 oversize guesses took {clock.Elapsed}");
        // the file's own cost is capped by the header limits (checked before any derivation): see Kdf_parameters_outside_the_bounds in F17BackupTests
        Assert.Equal(600_000, BackupLimits.MinIterations);
        Assert.True(BackupLimits.MaxIterations <= 2_000_000);
    }

    // ================= disk full / failure while exporting =================

    [Theory]
    [InlineData("export:partial")]
    [InlineData("export:written")]
    public void Disk_full_while_exporting_leaves_no_partial_file_and_keeps_the_old_backup(string stage)
    {
        using var rig = new BackupRig();
        rig.Seed();
        string dir = Path.Combine(rig.Root.Root, "out");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "b.susubak");
        Assert.True(rig.Service.Export(path, Plain).Ok);
        byte[] old = File.ReadAllBytes(path);
        rig.Seed("Changed", "sk-other"); // a new export would differ

        var outcome = ServiceWith(rig, FullAt(stage)).Export(path, Plain);
        Assert.False(outcome.Ok);
        Assert.Equal("disk-full", outcome.Error);
        Assert.Equal(old, File.ReadAllBytes(path)); // the old file is whole and unchanged
        Assert.Equal([path], Directory.GetFileSystemEntries(dir)); // no temp file, no partial copy
        Assert.NotNull(rig.Service.Preview(path, null)); // and it still opens

        // with no old file there is nothing left at all
        string fresh = Path.Combine(dir, "fresh.susubak");
        Assert.Equal("disk-full", ServiceWith(rig, FullAt(stage)).Export(fresh, Plain).Error);
        Assert.False(File.Exists(fresh));
        Assert.Equal([path], Directory.GetFileSystemEntries(dir));

        // space freed: the same export now works and replaces the file
        Assert.True(rig.Service.Export(path, Plain).Ok);
        Assert.NotEqual(old, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("export:partial")]
    [InlineData("export:written")]
    public void Power_loss_while_exporting_never_leaves_a_half_file_under_the_backup_name(string stage)
    {
        using var rig = new BackupRig();
        rig.Seed();
        string dir = Path.Combine(rig.Root.Root, "out");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "b.susubak");
        Assert.True(rig.Service.Export(path, Plain).Ok);
        byte[] old = File.ReadAllBytes(path);
        string other = Path.Combine(dir, "new.susubak");

        Assert.Throws<SimulatedCrash>(() => ServiceWith(rig, new FaultAt(stage)).Export(path, Plain));
        Assert.Throws<SimulatedCrash>(() => ServiceWith(rig, new FaultAt(stage)).Export(other, Plain));
        Assert.Equal(old, File.ReadAllBytes(path));
        Assert.False(File.Exists(other));
        // the process died, so the temporary files are still there; none of them can be mistaken for a backup
        Assert.All(Directory.GetFiles(dir).Where(f => f != path), f => Assert.EndsWith(".tmp", f, StringComparison.Ordinal));
        Assert.NotNull(rig.Service.Preview(path, null));
    }

    [Fact] // a real failure, no injection: the existing file is read-only
    public void An_export_that_cannot_replace_the_target_keeps_it_and_leaves_no_temp_file()
    {
        using var rig = new BackupRig();
        rig.Seed();
        string dir = Path.Combine(rig.Root.Root, "out");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "b.susubak");
        Assert.True(rig.Service.Export(path, Plain).Ok);
        byte[] old = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Equal("write-failed", rig.Service.Export(path, Plain).Error);
            Assert.Equal(old, File.ReadAllBytes(path));
            Assert.Equal([path], Directory.GetFileSystemEntries(dir));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    // ================= disk full while preparing the restore =================

    [Theory]
    [InlineData("import-stage:settings")]
    [InlineData("import-stage:secrets")]
    [InlineData("import-stage:pending")]
    public void Disk_full_while_preparing_a_restore_stages_nothing_and_the_live_pair_stays(string stage)
    {
        var (target, file) = F17BackupTests.Pair();
        using var _ = target;
        string settings = target.SettingsHash, secrets = target.SecretsHash;
        var earlier = target.Service.Preview(file, Password); // an earlier preview is staged
        Assert.True(Directory.Exists(Staged(target)));

        Assert.Equal("disk-full", F17BackupTests.CodeOf(() => ServiceWith(target, FullAt(stage)).Preview(file, Password)));
        Assert.Equal(settings, target.SettingsHash);
        Assert.Equal(secrets, target.SecretsHash);
        AssertNothingStaged(target);
        Assert.False(target.Service.Apply(earlier.Token)); // the superseded token cannot confirm a half-staged import

        // a lock or a permission problem is "stage-failed", with the same clean state
        Assert.Equal("stage-failed", F17BackupTests.CodeOf(() => ServiceWith(target, new FaultAt(stage, _ => new IOException("locked"))).Preview(file, Password)));
        Assert.Equal("stage-failed", F17BackupTests.CodeOf(() => ServiceWith(target, new FaultAt(stage, _ => new UnauthorizedAccessException())).Preview(file, Password)));
        AssertNothingStaged(target);
        Assert.Equal(settings, target.SettingsHash);

        // space freed: the import goes through
        Assert.True(target.Service.Apply(target.Service.Preview(file, Password).Token));
        Assert.Equal("Applied", target.Restart()!.State);
        Assert.Equal("Source", Label(target));
    }

    [Fact] // confirming needs one more small write; if it fails the stage is dropped and nothing is scheduled
    public void Disk_full_while_confirming_schedules_nothing()
    {
        var (target, file) = F17BackupTests.Pair();
        using var _ = target;
        string settings = target.SettingsHash, secrets = target.SecretsHash;
        var preview = target.Service.Preview(file, Password);
        Assert.Equal("disk-full", F17BackupTests.CodeOf(() => ServiceWith(target, FullAt("import-apply:pending")).Apply(preview.Token)));
        AssertNothingStaged(target);
        Assert.Null(target.Restart()); // nothing happens at the next start
        Assert.Equal(settings, target.SettingsHash);
        Assert.Equal(secrets, target.SecretsHash);
    }

    // ================= disk full during the switch at start =================

    [Theory] // every step after which a write can fail: the pair is entirely old, the journal is gone, a retry works
    [InlineData("import:begin", false)]
    [InlineData("import:restore-point-written", false)]
    [InlineData("import:before-commit", true)]
    [InlineData("stage:0", true)]
    [InlineData("stage:1", true)]
    [InlineData("prepared", true)]
    [InlineData("replace:0:flushed", true)]
    [InlineData("replace:0:replaced", true)]
    [InlineData("replace:1:flushed", true)]
    [InlineData("replace:1:replaced", true)]
    public void Disk_full_at_any_step_of_the_switch_keeps_the_old_pair_and_a_retry_works(string stage, bool restorePointComplete)
    {
        var (target, file) = F17BackupTests.Pair();
        using var _ = target;
        string settings = target.SettingsHash, secrets = target.SecretsHash;
        Assert.True(target.Service.Apply(target.Service.Preview(file, Password).Token));

        var result = target.Restart(FullAt(stage));
        Assert.Equal("Failed", result!.State);
        Assert.Equal("disk-full", result.Error);
        Assert.Equal(settings, target.SettingsHash);
        Assert.Equal(secrets, target.SecretsHash);
        Assert.Equal("Target", Label(target));
        Assert.True(target.Secrets.TryRead("acct-target", "apiKey", out string key));
        Assert.Equal("sk-target-KEY", key);
        AssertNoTransactionLeft(target);
        AssertNothingStaged(target);
        Assert.False(Directory.Exists(Path.Combine(target.Paths.Imports, "restore-point.tmp")));
        Assert.Equal(restorePointComplete, target.Service.Status().CanUndo);
        Assert.Equal("Failed", target.Service.Status().Result!.State);

        // the user frees space and chooses the file again
        Assert.True(target.Service.Apply(target.Service.Preview(file, Password).Token));
        Assert.Equal("Applied", target.Restart()!.State);
        Assert.Equal("Source", Label(target));
    }

    [Fact] // the rollback itself needs to write; if that fails too it is finished before settings are read
    public void Disk_full_again_while_rolling_back_is_finished_before_the_app_loads_its_settings()
    {
        var (target, file) = F17BackupTests.Pair();
        using var _ = target;
        string settings = target.SettingsHash, secrets = target.SecretsHash;
        Assert.True(target.Service.Apply(target.Service.Preview(file, Password).Token));

        var result = target.Restart(new FaultsAt("replace:1:flushed", "rollback:0:flushed")); // first file replaced, second fails, undoing the first fails
        Assert.Equal("Failed", result!.State);
        Assert.Equal(settings, target.SettingsHash); // not a settings.yaml from the backup beside the old secrets
        Assert.Equal(secrets, target.SecretsHash);
        AssertNoTransactionLeft(target);
        Assert.Equal("Target", Label(target));
    }

    [Fact] // a failure after the files switched (writing the result record) must not be reported as a failed import
    public void Disk_full_after_the_switch_is_still_reported_as_applied_and_the_next_start_tidies_up()
    {
        var (target, file) = F17BackupTests.Pair();
        using var _ = target;
        Assert.True(target.Service.Apply(target.Service.Preview(file, Password).Token));

        var result = target.Restart(FullAt("import:committed"));
        Assert.Equal("Applied", result!.State);
        Assert.Equal("Source", Label(target));
        Assert.True(target.Secrets.TryRead("acct-deepl", "apiKey", out string key));
        Assert.Equal("sk-source-KEY", key);
        string settings = target.SettingsHash;

        var next = target.Restart(); // the pending record is still there: the finished commit is recognised, not repeated
        Assert.Equal("Applied", next!.State);
        Assert.Equal(settings, target.SettingsHash);
        AssertNothingStaged(target);
        Assert.Null(target.Restart());
    }

    [Fact] // the restore point of the last import is replaced only by a complete new one
    public void A_failed_later_import_does_not_replace_the_restore_point_of_the_earlier_one()
    {
        var (target, file) = F17BackupTests.Pair();
        using var _ = target;
        string beforeFirstS = target.SettingsHash, beforeFirstK = target.SecretsHash;
        Assert.True(target.Service.Apply(target.Service.Preview(file, Password).Token));
        Assert.Equal("Applied", target.Restart()!.State);
        string afterFirstS = target.SettingsHash, afterFirstK = target.SecretsHash;

        string second = Path.Combine(Path.GetTempPath(), "susu-tests", Guid.NewGuid().ToString("N") + ".susubak");
        using (var source = new BackupRig())
        {
            source.Seed("Second", "sk-second-KEY", account: "acct-second");
            Assert.True(source.Service.Export(second, WithKeys).Ok);
        }
        Assert.True(target.Service.Apply(target.Service.Preview(second, Password).Token));
        var failed = target.Restart(FullAt("import:restore-point-written"));
        Assert.Equal("disk-full", failed!.Error);

        string rp = Path.Combine(target.Paths.Imports, "restore-point");
        Assert.Equal(beforeFirstS, AtomicFile.Hash(File.ReadAllBytes(Path.Combine(rp, "settings.yaml")))); // still the config from before the FIRST import
        Assert.Equal(beforeFirstK, AtomicFile.Hash(File.ReadAllBytes(Path.Combine(rp, "secrets.dat"))));
        Assert.False(Directory.Exists(rp + ".tmp"));
        Assert.Equal(afterFirstS, target.SettingsHash);
        Assert.Equal(afterFirstK, target.SecretsHash);

        Assert.True(target.Service.ScheduleUndo()); // and the undo still takes the first import back
        Assert.Equal("Applied", target.Restart()!.State);
        Assert.Equal("Target", Label(target));
    }

    // ================= power loss: every kill point, through the real journal =================

    [Theory] // (kill point, journal folder state it leaves, transactions rolled back by recovery, the pair after recovery, a half-switched pair before it)
    [InlineData("import:begin", "no-folder", 0, "old", false)]
    [InlineData("import:restore-point-written", "no-folder", 0, "old", false)]
    [InlineData("import:before-commit", "no-folder", 0, "old", false)]
    [InlineData("stage:0", "no-journal", 0, "old", false)]
    [InlineData("stage:1", "no-journal", 0, "old", false)]
    [InlineData("prepared", "Prepared", 1, "old", false)]
    [InlineData("replace:0:flushed", "Prepared", 1, "old", false)]
    [InlineData("replace:0:replaced", "Prepared", 1, "old", true)]
    [InlineData("replace:1:flushed", "Prepared", 1, "old", true)]
    [InlineData("replace:1:replaced", "Prepared", 1, "old", false)]
    [InlineData("committed", "Committed", 0, "new", false)]
    [InlineData("import:committed", "no-folder", 0, "new", false)]
    [InlineData("import:done", "no-folder", 0, "new", false)]
    public void Power_loss_at_each_kill_point_leaves_the_journal_state_that_recovery_expects(string stage, string journal, int rolledBack, string pair, bool halfSwitched)
    {
        var (target, file) = F17BackupTests.Pair();
        using var _ = target;
        string oldS = target.SettingsHash, oldK = target.SecretsHash;
        Assert.True(target.Service.Apply(target.Service.Preview(file, Password).Token));

        target.Store.Dispose();
        Assert.Throws<SimulatedCrash>(() => BackupImport.ApplyPending(target.Paths, target.Clock, new FaultAt(stage)));
        Assert.Equal(journal, JournalState(target)); // the real ConfigTransaction journal is what the crash left behind
        Assert.Equal(halfSwitched, target.SettingsHash != oldS && target.SecretsHash == oldK); // without the journal this would be a new settings.yaml beside old keys

        Assert.Equal(rolledBack, new ConfigTransaction(target.Paths).Recover());
        AssertNoTransactionLeft(target);
        if (pair == "old") { Assert.Equal(oldS, target.SettingsHash); Assert.Equal(oldK, target.SecretsHash); }
        else { Assert.NotEqual(oldS, target.SettingsHash); Assert.NotEqual(oldK, target.SecretsHash); }

        BackupImport.ApplyPending(target.Paths, target.Clock);
        target.Open();
        Assert.Equal("Source", Label(target)); // every kill point ends with the import finished after one more start
        Assert.Equal(oldS, AtomicFile.Hash(File.ReadAllBytes(Path.Combine(target.Paths.Imports, "restore-point", "settings.yaml"))));
    }

    [Theory] // a torn file (power loss on a file system that does not keep the replace atomic): the journal's copy of the old bytes wins
    [InlineData(0)]
    [InlineData(1)]
    public void A_torn_settings_file_in_the_middle_of_the_switch_is_restored_from_the_journal(int keepHalf)
    {
        var (target, file) = F17BackupTests.Pair();
        using var _ = target;
        string oldS = target.SettingsHash, oldK = target.SecretsHash;
        Assert.True(target.Service.Apply(target.Service.Preview(file, Password).Token));
        target.Store.Dispose();
        Assert.Throws<SimulatedCrash>(() => BackupImport.ApplyPending(target.Paths, target.Clock, new FaultAt("replace:1:flushed")));
        byte[] now = File.ReadAllBytes(target.Paths.Settings);
        File.WriteAllBytes(target.Paths.Settings, now[..(now.Length / 2 * keepHalf)]);
        Assert.Null(SettingsYaml.Read(File.ReadAllText(target.Paths.Settings)).Settings); // really unreadable

        Assert.Equal(1, new ConfigTransaction(target.Paths).Recover());
        Assert.Equal(oldS, target.SettingsHash);
        Assert.Equal(oldK, target.SecretsHash);
        target.Open();
        Assert.Equal("Target", Label(target));
        Assert.Empty(target.Store.State.Issues);
    }

    // ================= version incompatibility =================

    [Fact] // newer layout: refused before anything is staged; the message says to update; the next good file works
    public void A_backup_from_a_newer_schema_or_format_is_refused_and_nothing_changes()
    {
        using var rig = new BackupRig();
        rig.Seed();
        string settings = rig.SettingsHash, secrets = rig.SecretsHash;
        Assert.Equal("schema-newer", F17BackupTests.CodeOf(() => rig.Service.Preview(F17BackupTests.WriteTemp(rig, F17BackupTests.Craft(schema: AppSettings.CurrentSchemaVersion + 1), "s.susubak"), null)));
        Assert.Equal("format-newer", F17BackupTests.CodeOf(() => rig.Service.Preview(F17BackupTests.WriteTemp(rig, F17BackupTests.Craft(formatVersion: 2), "f.susubak"), null)));
        // the manifest claims the current schema but the settings inside are from a newer one
        string sneaky = F17BackupTests.WriteTemp(rig, F17BackupTests.Craft(f => f["settings.yaml"] = F17BackupTests.ValidSettings(t => t.Replace($"schemaVersion: {AppSettings.CurrentSchemaVersion}", $"schemaVersion: {AppSettings.CurrentSchemaVersion + 5}"))), "n.susubak");
        Assert.Contains(F17BackupTests.CodeOf(() => rig.Service.Preview(sneaky, null)), new[] { "schema-newer", "settings-invalid" });
        Assert.Equal(settings, rig.SettingsHash);
        Assert.Equal(secrets, rig.SecretsHash);
        AssertNothingStaged(rig);
        Assert.Null(rig.Restart()); // and a start with no import does nothing
        Assert.NotNull(rig.Service.Preview(F17BackupTests.WriteTemp(rig, F17BackupTests.Craft(), "ok.susubak"), null)); // nothing is wedged
    }

    [Fact] // compatibility is the file format and settings schema, not the app version string
    public void A_backup_from_a_newer_app_version_with_the_same_schema_is_accepted()
    {
        using var rig = new BackupRig();
        var preview = rig.Service.Preview(F17BackupTests.WriteTemp(rig, F17BackupTests.Craft()), null); // the crafted manifest says app 9.9, the host is 1.2.3
        Assert.Equal("9.9", preview.AppVersion);
        Assert.False(preview.SchemaOlder);
    }

    [Fact] // an older schema is upgraded on the way in and the app then loads it without issues
    public void An_older_schema_is_upgraded_at_the_switch_and_the_restore_point_keeps_the_old_config()
    {
        using var rig = new BackupRig();
        rig.Seed();
        string before = rig.SettingsHash;
        byte[] older = F17BackupTests.Craft(f => f["settings.yaml"] = F17BackupTests.ValidSettings(t => t.Replace($"schemaVersion: {AppSettings.CurrentSchemaVersion}", "schemaVersion: 0").Replace("uiLanguage:", "language:")), schema: 0);
        var preview = rig.Service.Preview(F17BackupTests.WriteTemp(rig, older), null);
        Assert.True(preview.SchemaOlder);
        Assert.True(rig.Service.Apply(preview.Token));
        Assert.Equal("Applied", rig.Restart()!.State);
        Assert.Empty(rig.Store.State.Issues);
        Assert.Equal(AppSettings.CurrentSchemaVersion, SettingsYaml.Read(File.ReadAllText(rig.Paths.Settings)).Settings!.SchemaVersion);
        Assert.Equal(before, AtomicFile.Hash(File.ReadAllBytes(Path.Combine(rig.Paths.Imports, "restore-point", "settings.yaml"))));
    }

    [Fact] // a pending record written by a different build (unknown state) is dropped with a reason and the config stays
    public void A_pending_record_in_an_unknown_state_is_abandoned_and_the_config_stays()
    {
        var (target, file) = F17BackupTests.Pair();
        using var _ = target;
        string settings = target.SettingsHash, secrets = target.SecretsHash;
        Assert.True(target.Service.Apply(target.Service.Preview(file, Password).Token));
        File.WriteAllText(PendingFile(target), File.ReadAllText(PendingFile(target)).Replace("\"Ready\"", "\"FutureState\""));
        var result = target.Restart();
        Assert.Equal("Failed", result!.State);
        Assert.Equal("pending-corrupt", result.Error);
        Assert.Equal(settings, target.SettingsHash);
        Assert.Equal(secrets, target.SecretsHash);
        AssertNothingStaged(target);
    }

    // ================= settings.yaml damaged at start =================

    [Fact] // the previous saved version is used; the damaged file is kept aside before the first save
    public void A_damaged_settings_file_falls_back_to_the_previous_version_and_is_kept_aside_on_the_next_save()
    {
        using var rig = new BackupRig();
        rig.Seed("One", "sk-1");
        rig.Seed("Two", "sk-2");
        Assert.True(File.Exists(rig.Paths.Settings + ".prev"));
        File.WriteAllText(rig.Paths.Settings, "version: [unclosed\n\t- garbage");
        string damagedHash = rig.SettingsHash;
        rig.Open();
        Assert.NotEmpty(rig.Store.State.Issues);
        Assert.Equal("One", Label(rig)); // the version before the last save
        Assert.Equal(damagedHash, rig.SettingsHash); // the damaged file is not rewritten by loading

        var state = rig.Store.State;
        Assert.Equal(SaveStatus.Saved, rig.Store.Save(state.Effective, state.Revision, state.FileHash).Status);
        Assert.Single(Directory.GetFiles(rig.Paths.Roaming, "settings.invalid-*.yaml"));
        Assert.Empty(rig.Store.State.Issues);
    }

    [Fact] // both the file and its previous version are unreadable: defaults load, and the restore point of the last import brings the config back
    public void With_the_file_and_its_previous_version_both_damaged_the_restore_point_recovers_the_config()
    {
        var (target, file) = F17BackupTests.Pair();
        using var _ = target;
        Assert.True(target.Service.Apply(target.Service.Preview(file, Password).Token));
        Assert.Equal("Applied", target.Restart()!.State); // the restore point now holds the "Target" config
        Assert.Equal("Source", Label(target));

        File.WriteAllText(target.Paths.Settings, "\0\0\0 not yaml");
        File.WriteAllText(target.Paths.Settings + ".prev", "\0\0\0 not yaml either");
        target.Open();
        Assert.NotEmpty(target.Store.State.Issues);
        Assert.Empty(target.Store.State.Effective.Accounts); // defaults, in memory

        Assert.True(target.Service.Status().CanUndo);
        Assert.True(target.Service.ScheduleUndo());
        Assert.Equal("Applied", target.Restart()!.State);
        Assert.Empty(target.Store.State.Issues);
        Assert.Equal("Target", Label(target));
        Assert.True(target.Secrets.TryRead("acct-target", "apiKey", out string key));
        Assert.Equal("sk-target-KEY", key);
    }

    // ================= undo after import =================

    [Fact] // back and forth through the same safe switch; a full disk while scheduling the undo changes nothing
    public void Undo_after_import_goes_back_and_forth_and_a_failure_while_scheduling_it_changes_nothing()
    {
        var (target, file) = F17BackupTests.Pair();
        using var _ = target;
        Assert.True(target.Service.Apply(target.Service.Preview(file, Password).Token));
        Assert.Equal("Applied", target.Restart()!.State);
        string imported = target.SettingsHash;

        Assert.Equal("disk-full", F17BackupTests.CodeOf(() => ServiceWith(target, FullAt("import-stage:secrets")).ScheduleUndo()));
        AssertNothingStaged(target);
        Assert.True(target.Service.Status().CanUndo); // the restore point is untouched
        Assert.Equal(imported, target.SettingsHash);

        Assert.True(target.Service.ScheduleUndo());
        Assert.Equal("Applied", target.Restart()!.State);
        Assert.Equal("Target", Label(target));
        Assert.True(target.Service.ScheduleUndo()); // the undo kept its own restore point: go forward again
        Assert.Equal("Applied", target.Restart()!.State);
        Assert.Equal("Source", Label(target));
    }

    // ================= database migrations (DATA03) =================

    private static async Task SeedWindow(Database db) { new WindowStateRepository(db).Save(new WindowPlacement("main", "m", 1, 2, 96)); await db.FlushAsync(); }
    private static long TableCount(Database db, string table) => (long)db.Read(c => Database.Scalar(c, null, "SELECT count(*) FROM sqlite_master WHERE name=$n;", ("$n", table)))!;

    [Fact] // a crash right after the pre-migration copy: old version, old data, and a good copy
    public async Task A_crash_after_the_pre_migration_copy_leaves_the_old_database_and_a_good_copy()
    {
        using var root = new TempRoot();
        int cur = Database.SchemaVersion, next = cur + 1;
        var migrations = new Dictionary<int, string>(Database.Migrations) { [next] = "CREATE TABLE f17_probe (id TEXT PRIMARY KEY);" };
        using (var v = Database.Open(root.Paths.Database)) await SeedWindow(v);

        Assert.Throws<SimulatedCrash>(() => Database.Open(root.Paths.Database, new FaultAt("migrate:backed-up"), next, migrations));
        string bak = Database.BackupPath(root.Paths.Database, cur);
        Assert.True(File.Exists(bak));
        using (var copy = new SqliteConnection($"Data Source={bak};Pooling=False;Mode=ReadOnly"))
        {
            copy.Open();
            Assert.Equal(1L, Database.Scalar(copy, null, "SELECT count(*) FROM window_state;"));
        }
        using (var still = Database.Open(root.Paths.Database))
        {
            Assert.Equal(cur, still.Version);
            Assert.NotNull(new WindowStateRepository(still).Get("main"));
        }
        using var done = Database.Open(root.Paths.Database, null, next, migrations); // the next start simply migrates
        Assert.Equal(next, done.Version);
        Assert.NotNull(new WindowStateRepository(done).Get("main"));
    }

    [Fact] // a failure between two migration steps, then an older app: refused with the copy named, and the copy restores it
    public async Task A_failure_between_two_migration_steps_resumes_later_and_an_older_app_can_restore_its_copy()
    {
        using var root = new TempRoot();
        int cur = Database.SchemaVersion;
        var migrations = new Dictionary<int, string>(Database.Migrations)
        {
            [cur + 1] = "CREATE TABLE f17_one (id TEXT PRIMARY KEY);",
            [cur + 2] = "CREATE TABLE f17_two (id TEXT PRIMARY KEY);",
        };
        using (var v = Database.Open(root.Paths.Database)) await SeedWindow(v);

        Assert.Throws<SimulatedCrash>(() => Database.Open(root.Paths.Database, new FaultAt($"migrate:{cur + 2}"), cur + 2, migrations));
        using (var half = Database.Open(root.Paths.Database, null, cur + 1, migrations)) // step 1 was committed, step 2 rolled back
        {
            Assert.Equal(cur + 1, half.Version);
            Assert.Equal(1L, TableCount(half, "f17_one"));
            Assert.Equal(0L, TableCount(half, "f17_two"));
        }
        using (var full = Database.Open(root.Paths.Database, null, cur + 2, migrations))
        {
            Assert.Equal(cur + 2, full.Version);
            Assert.Equal(1L, TableCount(full, "f17_two"));
            Assert.NotNull(new WindowStateRepository(full).Get("main"));
        }

        var refused = Assert.Throws<DatabaseVersionException>(() => Database.Open(root.Paths.Database)); // the older app
        Assert.Equal((cur + 2, cur), (refused.Found, refused.Supported));
        Assert.Equal(Database.BackupPath(root.Paths.Database, cur), refused.CompatibleBackup);
        Database.RestoreBackup(refused.CompatibleBackup!, root.Paths.Database);
        using var old = Database.Open(root.Paths.Database);
        Assert.Equal(cur, old.Version);
        Assert.Equal(0L, TableCount(old, "f17_one"));
        Assert.Equal(1, new WindowStateRepository(old).Get("main")!.X); // data that matches the program version
    }

    [Fact] // a migration whose SQL fails half way rolls back as a whole
    public async Task A_migration_that_fails_in_the_middle_rolls_back_to_the_old_version_and_data()
    {
        using var root = new TempRoot();
        int cur = Database.SchemaVersion, next = cur + 1;
        var migrations = new Dictionary<int, string>(Database.Migrations) { [next] = "CREATE TABLE f17_half (id TEXT); INSERT INTO f17_missing VALUES (1);" };
        using (var v = Database.Open(root.Paths.Database)) await SeedWindow(v);

        Assert.ThrowsAny<SqliteException>(() => Database.Open(root.Paths.Database, null, next, migrations));
        using var still = Database.Open(root.Paths.Database);
        Assert.Equal(cur, still.Version);
        Assert.Equal(0L, TableCount(still, "f17_half")); // the first statement was undone with the second
        Assert.NotNull(new WindowStateRepository(still).Get("main"));
        Assert.True(File.Exists(Database.BackupPath(root.Paths.Database, cur)));
    }

    [Fact] // disk full while copying: the database is untouched, no partial copy appears, and an earlier good copy survives
    public async Task Disk_full_while_writing_the_pre_migration_copy_leaves_the_database_and_the_earlier_copy_alone()
    {
        using var root = new TempRoot();
        int cur = Database.SchemaVersion, next = cur + 1;
        var migrations = new Dictionary<int, string>(Database.Migrations) { [next] = "CREATE TABLE f17_probe (id TEXT PRIMARY KEY);" };
        using (var v = Database.Open(root.Paths.Database)) await SeedWindow(v);
        Assert.Throws<SimulatedCrash>(() => Database.Open(root.Paths.Database, new FaultAt("migrate:backed-up"), next, migrations)); // leaves a good copy
        string bak = Database.BackupPath(root.Paths.Database, cur);
        byte[] good = File.ReadAllBytes(bak);

        Assert.Throws<IOException>(() => Database.Open(root.Paths.Database, FullAt("migrate:backup-writing"), next, migrations));
        Assert.Equal(good, File.ReadAllBytes(bak));
        Assert.False(File.Exists(bak + ".tmp"));
        using (var still = Database.Open(root.Paths.Database))
        {
            Assert.Equal(cur, still.Version);
            Assert.NotNull(new WindowStateRepository(still).Get("main"));
        }
        using var done = Database.Open(root.Paths.Database, null, next, migrations); // space freed: it migrates
        Assert.Equal(next, done.Version);
    }

    [Fact] // the text an older app shows for a newer database: what happened, data untouched, what to do, in both languages
    public void A_newer_database_is_explained_in_both_languages_and_names_the_copy()
    {
        var e = new DatabaseVersionException(6, 4, Path.Combine("data", "susu.db.v4.bak"));
        string zh = e.UserMessage(chinese: true), en = e.UserMessage(chinese: false);
        Assert.Contains("susu.db.v4.bak", zh, StringComparison.Ordinal);
        Assert.Contains("susu.db.v4.bak", en, StringComparison.Ordinal);
        Assert.Contains("数据没有被改动", zh, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", en, StringComparison.Ordinal);
        Assert.Contains("新版 Su-Su", zh, StringComparison.Ordinal);
        Assert.Contains("newer Su-Su", en, StringComparison.Ordinal);
        var none = new DatabaseVersionException(6, 4, null);
        Assert.DoesNotContain(".bak", none.UserMessage(true), StringComparison.Ordinal);
        Assert.DoesNotContain(".bak", none.UserMessage(false), StringComparison.Ordinal);
    }
}
