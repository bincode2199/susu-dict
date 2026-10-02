using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Susu.Abstractions;
using Susu.Plugins.AppUpdate;
using Susu.Storage;
using Susu.Testing;
using Susu.Windows;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F18.3: UPD08 (tampered updater input, package swapped after verification, links in work or install folders), first-start guard and old-version
/// fallback (DATA03), user files in the install folder, uninstall rules. Temp folders are susu-f18-*; the real install and data are never touched.
/// </summary>
[SupportedOSPlatform("windows")]
public class F18UpdateSafetyTests
{
    // ---------------- rig ----------------

    private sealed class Hook(Dictionary<string, Action> actions) : IFaultPoint
    {
        public void Hit(string stage) { if (actions.TryGetValue(stage, out var action)) action(); }
    }

    private sealed class Env(string db) : IUpdateEnvironment
    {
        public UpdateExitOutcome Exit = UpdateExitOutcome.Exited;
        public bool Real; // run the real database migration instead of a marker row
        public int Stops;
        public UpdateExitOutcome StopApp(TimeSpan timeout) { Stops++; return Exit; }
        public bool Migrate(string installDirectory, string databasePath, out string? detail)
        {
            detail = null;
            if (Real) { using var d = Database.Open(databasePath); }
            else Execute(db, "INSERT INTO notes(text) VALUES ('migrated');");
            return true;
        }
        public bool HealthCheck(string installDirectory, string databasePath, string expectedVersion, out string? detail) { detail = null; return true; }
        public long FreeBytes(string path) => long.MaxValue;
    }

    private sealed class Rig
    {
        public readonly string Root = TestTemp.NewDir("susu-f18-safety");
        public string Install => Path.Combine(Root, "app");
        public string Db => Path.Combine(Root, "data", "susu.db");
        public string Updates => Path.Combine(Root, "data", "updates");
        public string Downloads => Path.Combine(Updates, "downloads");
        public readonly Env Env;
        public byte[] Zip = [];
        public AppUpdateOffer Offer = null!;
        public Dictionary<string, string> Old = new() { ["susu.exe"] = "OLD exe 1.1.0", ["susu_native.dll"] = "OLD native", ["ui/index.html"] = "OLD ui", ["old-only.dll"] = "only in the old version" };
        public Dictionary<string, string> New = new() { ["susu.exe"] = "NEW exe 1.2.0", ["susu_native.dll"] = "NEW native", ["ui/index.html"] = "NEW ui", ["ui/app.js"] = "NEW js" };

        public Rig(bool real = false)
        {
            Directory.CreateDirectory(Install);
            Directory.CreateDirectory(Path.GetDirectoryName(Db)!);
            Old["staged-manifest.json"] = Manifest(Old.Keys);
            New["staged-manifest.json"] = Manifest(New.Keys);
            foreach (var (path, text) in Old) Put(Install, path, text);
            if (real)
            {
                using (Database.Open(Db, targetVersion: Database.SchemaVersion - 1)) { }
                Execute(Db, "CREATE TABLE notes(id INTEGER PRIMARY KEY, text TEXT); INSERT INTO notes(text) VALUES ('user data one'), ('user data two');");
            }
            else Execute(Db, "PRAGMA journal_mode=WAL; CREATE TABLE notes(id INTEGER PRIMARY KEY, text TEXT); INSERT INTO notes(text) VALUES ('user data one'), ('user data two');");
            Env = new Env(Db) { Real = real };
            SetPackage(New);
        }

        public void SetPackage(Dictionary<string, string> files, string version = "1.2.0", long sequence = 5)
        {
            Zip = BuildZip(files.Select(f => (f.Key, Encoding.UTF8.GetBytes(f.Value))));
            Offer = new AppUpdateOffer(version, sequence, $"susu-{version}.zip", Zip.Length, Convert.ToHexStringLower(SHA256.HashData(Zip)), "");
        }

        public AppUpdater Updater(IFaultPoint? faults = null) => new(Install, Db, Updates, Env, faults);

        public string PlaceZip(AppUpdater updater, string from = "1.1.0")
        {
            updater.BeginDownload(Offer, from);
            string path = Path.Combine(Downloads, Offer.FileName);
            File.WriteAllBytes(path, Zip);
            return path;
        }

        public AppUpdater Staged(IFaultPoint? faults = null)
        {
            var updater = Updater(faults);
            Assert.True(updater.Stage(PlaceZip(updater)).Ok);
            return updater;
        }

        public AppUpdater Committed()
        {
            var updater = Staged();
            Assert.Equal(new ApplyResult("committed", null), updater.Apply());
            return updater;
        }
    }

    private static string Manifest(IEnumerable<string> paths) => "{\"files\":[" + string.Join(",", paths.Select(p => "{\"path\":\"" + p + "\"}")) + "]}";

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
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT text FROM notes ORDER BY id;";
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add(r.GetString(0));
        return [.. rows];
    }

    private static SortedDictionary<string, string> Snapshot(string dir)
        => new(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(dir, p).Replace('\\', '/'), File.ReadAllText));

    private static void AssertOldInstall(Rig rig) => Assert.Equal(new SortedDictionary<string, string>(rig.Old), Snapshot(rig.Install));

    private static int Cmd(string command)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + command) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static void Junction(string link, string target) => Assert.Equal(0, Cmd($"mklink /J \"{link}\" \"{target}\""));

    // ---------------- UPD08: path rules ----------------

    [Fact]
    public void Path_rules_accept_a_plain_local_path_and_refuse_every_tampered_form()
    {
        string ok = Path.Combine(Path.GetTempPath(), "susu-f18-rules", "app");
        Assert.Equal(Path.GetFullPath(ok), UpdatePathRules.PlainAbsolute(ok));
        Assert.Equal(Path.GetFullPath(ok), UpdatePathRules.PlainAbsolute(ok + "\\")); // a trailing separator is harmless
        string drive = Path.GetPathRoot(Path.GetTempPath())!;
        foreach (string bad in new[]
        {
            "", "   ", "app", @".\app", @"..\app", @"\app", @"C:app", @"C:\a\..\b", @"C:\a\.\b", @"C:\a\\b",
            @"\\server\share\x", @"\\localhost\c$\x", @"\\?\C:\x", @"\\.\C:\x", @"\\?\UNC\server\share", @"\\.\PhysicalDrive0",
            @"C:\x:stream", @"C:\x\file.txt::$DATA", @"C:\x\file.txt:evil.exe", @"C:\CON", @"C:\x\NUL.txt", @"C:\x\COM1", @"C:\x\a.", @"C:\x\a ",
            "C:\\x\" --no-restart", "C:\\x\nC:\\y", "C:\\x\0y", @"C:\x|y", @"C:\x*", @"C:\x?", @"C:\x<y", @"http://example.com/x", @"file:///C:/x", @"~\x", "%TEMP%\\x",
            @"C:\" + new string('a', 300),
        })
            Assert.Null(UpdatePathRules.PlainAbsolute(bad));
        Assert.NotNull(UpdatePathRules.PlainAbsolute(drive));
    }

    [Fact]
    public void Folder_rules_refuse_a_junction_a_drive_root_a_windows_folder_and_overlap_with_the_updates_folder()
    {
        var rig = new Rig();
        Assert.Equal(Path.GetFullPath(rig.Install), UpdatePathRules.InstallFolder(rig.Install, rig.Updates));
        Assert.Null(UpdatePathRules.InstallFolder(Path.Combine(rig.Root, "missing"), rig.Updates));
        Assert.Null(UpdatePathRules.InstallFolder(Path.GetPathRoot(rig.Root), rig.Updates)); // a drive root
        Assert.Null(UpdatePathRules.InstallFolder(Environment.GetFolderPath(Environment.SpecialFolder.Windows), rig.Updates));
        Assert.Null(UpdatePathRules.InstallFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"), rig.Updates));
        Assert.Null(UpdatePathRules.InstallFolder(rig.Root, Path.Combine(rig.Install, "updates"))); // updates folder under the install folder
        Assert.Null(UpdatePathRules.InstallFolder(Path.Combine(rig.Updates, "helper"), rig.Updates)); // install folder under the updates folder
        string link = Path.Combine(rig.Root, "link");
        Junction(link, rig.Install);
        Assert.Null(UpdatePathRules.InstallFolder(link, rig.Updates)); // a junction to the real folder
        string empty = Path.Combine(rig.Root, "empty");
        Directory.CreateDirectory(empty);
        Assert.Null(UpdatePathRules.InstallFolder(empty, rig.Updates)); // no susu.exe
    }

    // ---------------- UPD08: package and work folders ----------------

    [Fact]
    public void A_package_outside_the_downloads_folder_or_with_links_in_the_work_folders_is_never_staged()
    {
        var rig = new Rig();
        var updater = rig.Updater();
        // not in the downloads folder
        updater.BeginDownload(rig.Offer, "1.1.0");
        string elsewhere = Path.Combine(rig.Root, "elsewhere.zip");
        File.WriteAllBytes(elsewhere, rig.Zip);
        Assert.Equal(new StageResult(false, "download-path-invalid"), updater.Stage(elsewhere));
        Assert.Null(updater.Read());
        // a path that climbs out of the downloads folder
        updater.BeginDownload(rig.Offer, "1.1.0");
        File.WriteAllBytes(Path.Combine(rig.Updates, "x.zip"), rig.Zip);
        Assert.Equal("download-path-invalid", updater.Stage(Path.Combine(rig.Downloads, "..", "x.zip")).Error);
        // the downloads folder is a junction to a folder holding a valid package
        string outside = Path.Combine(rig.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, rig.Offer.FileName), rig.Zip);
        updater.BeginDownload(rig.Offer, "1.1.0");
        Directory.Delete(rig.Downloads, true);
        Junction(rig.Downloads, outside);
        Assert.Equal("stage-failed", updater.Stage(Path.Combine(rig.Downloads, rig.Offer.FileName)).Error);
        Assert.True(File.Exists(Path.Combine(outside, rig.Offer.FileName))); // the link was unlinked or left, its target was not emptied
        Assert.Null(updater.Read());
        // the stage folder is a junction: nothing is unpacked through it
        string target = Path.Combine(rig.Root, "stage-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "sentinel.txt"), "keep");
        string path = rig.PlaceZip(updater);
        Junction(updater.StageFolder, target);
        Assert.Equal("stage-failed", updater.Stage(path).Error);
        Assert.Equal(["sentinel.txt"], Directory.EnumerateFiles(target).Select(Path.GetFileName).ToArray());
        AssertOldInstall(rig);
    }

    [Fact]
    public void The_verified_package_cannot_be_changed_while_it_is_checked_and_unpacked()
    {
        var rig = new Rig();
        string? zipPath = null;
        var failures = new List<string>();
        var hook = new Hook(new()
        {
            // Between "hash verified" and "unpacked": the file is held open without write or delete sharing, so a swap is refused by the system.
            ["stage:hashed"] = () =>
            {
                try { File.WriteAllBytes(zipPath!, BuildZip([("susu.exe", "EVIL"u8.ToArray())])); failures.Add("overwrite"); } catch (IOException) { }
                try { File.Delete(zipPath!); failures.Add("delete"); } catch (IOException) { }
                try { File.Move(zipPath!, zipPath + ".moved"); failures.Add("move"); } catch (IOException) { }
            },
        });
        var updater = rig.Updater(hook);
        zipPath = rig.PlaceZip(updater);
        Assert.True(updater.Stage(zipPath).Ok);
        Assert.Empty(failures);
        Assert.Equal("NEW exe 1.2.0", File.ReadAllText(Path.Combine(updater.StageFolder, "susu.exe")));
    }

    [Fact]
    public void A_staged_file_swapped_after_verification_is_caught_before_it_is_ever_installed_or_run()
    {
        // 1. swapped while staged (between "Staged" and the install click)
        var rig = new Rig();
        var staged = rig.Staged();
        File.WriteAllText(Path.Combine(staged.StageFolder, "susu.exe"), "EVIL exe");
        Assert.Equal(new ApplyResult("not-staged", "stage-corrupt"), staged.Apply());
        AssertOldInstall(rig);
        Assert.Equal(0, rig.Env.Stops); // the app was not even asked to exit

        // 2. swapped during the replacement, after the whole stage was verified: each file is hashed again while it is copied
        var rig2 = new Rig();
        var hook = new Hook(new() { ["replace:0"] = () => File.WriteAllText(Path.Combine(rig2.Updates, "stage", "ui", "index.html"), "EVIL ui") });
        var updater = rig2.Staged(hook);
        var result = updater.Apply();
        Assert.Equal(new ApplyResult("rolled-back", "stage-corrupt"), result);
        AssertOldInstall(rig2);
        Assert.Equal(["user data one", "user data two"], Rows(rig2.Db));

        // 3. an installed file changed after the replacement is not executed by the migration probe
        var rig3 = new Rig();
        string exe = Path.Combine(rig3.Install, "susu.exe");
        var hook3 = new Hook(new() { ["replace:done"] = () => File.WriteAllText(exe, "EVIL exe") });
        var result3 = rig3.Staged(hook3).Apply();
        Assert.Equal(new ApplyResult("rolled-back", "stage-corrupt"), result3);
        Assert.Equal(0, rig3.Env.Stops - 1); // one stop for the update, none extra
        AssertOldInstall(rig3);
    }

    [Fact]
    public void A_link_inside_the_install_folder_is_never_written_through()
    {
        var rig = new Rig();
        string outside = Path.Combine(rig.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep");
        Directory.Delete(Path.Combine(rig.Install, "ui"), true);
        Junction(Path.Combine(rig.Install, "ui"), outside);
        var result = rig.Staged().Apply();
        Assert.Equal("rolled-back", result.Outcome);
        Assert.Equal("replace-failed", result.Error);
        Assert.Equal(["precious.txt"], Directory.EnumerateFileSystemEntries(outside).Select(Path.GetFileName).ToArray()); // nothing was written through the link
        Assert.Equal("OLD exe 1.1.0", File.ReadAllText(Path.Combine(rig.Install, "susu.exe")));
        Assert.Equal(["user data one", "user data two"], Rows(rig.Db));
    }

    [Fact]
    public void A_journal_or_backup_naming_a_path_outside_the_install_folder_is_treated_as_damaged_and_nothing_outside_is_touched()
    {
        var rig = new Rig();
        var updater = rig.Staged();
        string journal = Path.Combine(rig.Updates, "journal.json");
        string evil = Path.Combine(rig.Root, "evil.txt");
        File.WriteAllText(evil, "keep");
        File.WriteAllText(journal, File.ReadAllText(journal).Replace("\"path\":\"ui/index.html\"", "\"path\":\"../../../evil.txt\""));
        File.Delete(journal + ".prev");
        Assert.Null(updater.Read());
        Assert.Equal(new ApplyResult("not-staged", null), updater.Apply());
        Assert.Equal("keep", File.ReadAllText(evil));
        updater.Recover();
        Assert.Equal("keep", File.ReadAllText(evil));
        AssertOldInstall(rig);

        // a committed update whose backup index was edited to point outside: the rollback refuses
        var rig2 = new Rig();
        var committed = rig2.Committed();
        string index = Path.Combine(rig2.Updates, "backup", "backup.json");
        string evil2 = Path.Combine(rig2.Root, "evil2.txt");
        File.WriteAllText(evil2, "keep");
        File.WriteAllText(index, File.ReadAllText(index).Replace("\"path\":\"old-only.dll\"", "\"path\":\"../../evil2.txt\""));
        Assert.Equal("no-backup", committed.RollBackToPrevious(AppUpdater.UserRollback).Outcome);
        Assert.Equal("keep", File.ReadAllText(evil2));
        Assert.Equal("NEW exe 1.2.0", File.ReadAllText(Path.Combine(rig2.Install, "susu.exe")));
    }

    [Fact]
    public void A_journal_or_backup_made_for_another_install_folder_is_not_used()
    {
        var rig = new Rig();
        var staged = rig.Staged();
        // the same updates folder, but a helper told to work on a different install folder
        string other = Path.Combine(rig.Root, "other-app");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "susu.exe"), "OTHER exe");
        var wrong = new AppUpdater(other, rig.Db, rig.Updates, rig.Env);
        Assert.Equal(new ApplyResult("not-staged", "install-dir-mismatch"), wrong.Apply());
        Assert.Equal("OTHER exe", File.ReadAllText(Path.Combine(other, "susu.exe")));
        Assert.Equal(0, rig.Env.Stops);
        Assert.Equal("committed", staged.Apply().Outcome); // the right folder still works
        Assert.Equal("no-backup", new AppUpdater(other, rig.Db, rig.Updates, rig.Env).RollBackToPrevious(AppUpdater.UserRollback).Outcome);
        Assert.Equal("OTHER exe", File.ReadAllText(Path.Combine(other, "susu.exe")));
    }

    // ---------------- user files in the install folder ----------------

    [Fact]
    public void Files_the_update_does_not_know_stay_and_only_files_an_earlier_version_brought_are_removed()
    {
        var rig = new Rig();
        Put(rig.Install, "user-notes.txt", "mine");
        Put(rig.Install, "plugins/mine.dll", "my plugin");
        var updater = rig.Staged();
        Assert.Equal("committed", updater.Apply().Outcome);
        Assert.False(File.Exists(Path.Combine(rig.Install, "old-only.dll"))); // the old version brought it (staged-manifest.json), the new one dropped it
        Assert.Equal("mine", File.ReadAllText(Path.Combine(rig.Install, "user-notes.txt")));
        Assert.Equal("my plugin", File.ReadAllText(Path.Combine(rig.Install, "plugins", "mine.dll")));
        Assert.Equal("NEW exe 1.2.0", File.ReadAllText(Path.Combine(rig.Install, "susu.exe")));
        Assert.True(File.Exists(Path.Combine(rig.Updates, "installed-files.json"))); // the list the next update relies on

        // the next update removes what the 1.2.0 package brought and 1.3.0 drops, and still leaves user files
        rig.SetPackage(new Dictionary<string, string> { ["susu.exe"] = "NEWER exe", ["staged-manifest.json"] = Manifest(["susu.exe"]) }, "1.3.0", 6);
        var second = rig.Updater();
        Assert.True(second.Stage(rig.PlaceZip(second, "1.2.0")).Ok);
        Assert.Equal("committed", second.Apply().Outcome);
        Assert.False(File.Exists(Path.Combine(rig.Install, "ui", "app.js")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(rig.Install, "user-notes.txt")));

        // a rollback restores the previous version and keeps the user file too
        Assert.Equal("rolled-back", second.RollBackToPrevious(AppUpdater.UserRollback).Outcome);
        Assert.Equal("NEW exe 1.2.0", File.ReadAllText(Path.Combine(rig.Install, "susu.exe")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(rig.Install, "user-notes.txt")));
    }

    [Fact]
    public void A_rolled_back_update_leaves_user_files_and_removes_only_what_the_new_version_added()
    {
        var rig = new Rig();
        Put(rig.Install, "user-notes.txt", "mine");
        rig.Env.Exit = UpdateExitOutcome.Exited;
        var hook = new Hook(new() { ["health:begin"] = () => throw new InvalidDataException("boom") });
        var result = rig.Staged(hook).Apply();
        Assert.Equal("rolled-back", result.Outcome);
        Assert.False(File.Exists(Path.Combine(rig.Install, "ui", "app.js")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(rig.Install, "user-notes.txt")));
        Assert.Equal("OLD exe 1.1.0", File.ReadAllText(Path.Combine(rig.Install, "susu.exe")));
    }

    // ---------------- exit coordination (UPD08: tasks running) ----------------

    [Fact]
    public void An_app_that_will_not_exit_leaves_everything_in_place_and_nothing_runs_before_the_exit()
    {
        var rig = new Rig();
        rig.Env.Exit = UpdateExitOutcome.TimedOut;
        var result = rig.Staged().Apply();
        Assert.Equal(new ApplyResult("aborted", "exit-timeout"), result);
        AssertOldInstall(rig);
        Assert.Equal(["user data one", "user data two"], Rows(rig.Db));
        Assert.False(Directory.Exists(Path.Combine(rig.Updates, "backup")) && File.Exists(Path.Combine(rig.Updates, "backup", "backup.json")));
    }

    // ---------------- first-start guard ----------------

    [Fact]
    public void The_first_start_guard_counts_starts_is_released_by_a_confirmation_and_stops_the_third_unconfirmed_start()
    {
        string folder = TestTemp.NewDir("susu-f18-guard");
        var guard = new FirstStartGuard(folder);
        Assert.Equal(StartDecision.Proceed, guard.OnStart("1.2.0")); // nothing armed
        guard.Arm("1.2.0", "1.1.0");
        Assert.Equal(StartDecision.Proceed, guard.OnStart("1.2.0"));
        Assert.Equal(1, guard.Read()!.Attempts);
        guard.Confirm("1.2.0");
        Assert.Null(guard.Read());

        guard.Arm("1.2.0", "1.1.0");
        Assert.Equal(StartDecision.Proceed, guard.OnStart("1.2.0"));
        Assert.Equal(StartDecision.Proceed, guard.OnStart("1.2.0"));
        Assert.Equal(StartDecision.Fallback, guard.OnStart("1.2.0")); // two unconfirmed starts: the third does not run
        Assert.Equal(StartDecision.Fallback, guard.OnStart("1.2.0")); // and keeps being refused until the helper has rolled back
        guard.Confirm("9.9.9"); // another version's confirmation does not release it
        Assert.Equal(StartDecision.Fallback, guard.OnStart("1.2.0"));
        Assert.Equal(StartDecision.Proceed, guard.OnStart("1.1.0")); // the old version after a fallback: the stale state is removed
        Assert.Null(guard.Read());

        // a damaged state file never hides a crash loop
        guard.Arm("1.2.0", "1.1.0");
        File.WriteAllText(Path.Combine(folder, "first-start.json"), "{ not json");
        Assert.Equal(StartDecision.Fallback, guard.OnStart("1.2.0"));

        // failed versions are remembered
        Assert.False(guard.IsFailed("1.2.0"));
        guard.MarkFailed("1.2.0");
        guard.MarkFailed("1.2.0");
        Assert.Equal(["1.2.0"], guard.FailedVersions());
        Assert.True(new FirstStartGuard(folder).IsFailed("1.2.0"));
    }

    [Fact]
    public void A_commit_arms_the_guard_and_a_rollback_releases_it()
    {
        var rig = new Rig();
        var updater = rig.Committed();
        var state = updater.Guard.Read()!;
        Assert.Equal(("1.2.0", "1.1.0", 0, false), (state.Version, state.FromVersion, state.Attempts, state.Confirmed));
        // a health failure on the way to the commit never leaves the guard armed
        var rig2 = new Rig();
        var hook = new Hook(new() { ["health:begin"] = () => throw new InvalidDataException("boom") });
        Assert.Equal("rolled-back", rig2.Staged(hook).Apply().Outcome);
        Assert.Null(new FirstStartGuard(rig2.Updates).Read());
    }

    // ---------------- first-start failure and the old-version fallback (DATA03) ----------------

    private sealed class Launcher : IUpdateLauncher
    {
        public int Installs, Rollbacks;
        public bool LaunchHelper() { Installs++; return true; }
        public bool LaunchRollback() { Rollbacks++; return true; }
    }

    private sealed class OfferSource(string version) : IAppUpdateSource
    {
        public int Fetches;
        public Task<AppUpdateCheck> CheckAsync(string currentVersion, CancellationToken cancellationToken)
            => Task.FromResult(AppUpdateCheck.Available(new AppUpdateOffer(version, 9, $"susu-{version}.zip", 1, new string('a', 64), "")));
        public Task<string> FetchAsync(AppUpdateOffer offer, string folder, CancellationToken cancellationToken) { Fetches++; throw new InvalidOperationException(); }
    }

    [Fact]
    public async Task A_new_version_that_fails_its_first_starts_is_rolled_back_with_the_paired_data_and_is_not_offered_again()
    {
        var rig = new Rig(real: true);
        var updater = rig.Committed();
        Assert.Equal(Database.SchemaVersion, Database.PeekVersion(rig.Db)); // the new build migrated the database
        Execute(rig.Db, "INSERT INTO notes(text) VALUES ('saved in the new version');");

        // First starts of the new version: each one fails (a post-commit migration or start-up crash) and is never confirmed.
        var guard = updater.Guard;
        Assert.Equal(StartDecision.Proceed, guard.OnStart("1.2.0"));
        Assert.Equal(StartDecision.Proceed, guard.OnStart("1.2.0"));
        Assert.Equal(StartDecision.Fallback, guard.OnStart("1.2.0"));

        // The helper rolls back (reason first-start-failed) while the failed app has exited.
        var result = updater.RollBackToPrevious(AppUpdater.FirstStartFailed);
        Assert.Equal(new ApplyResult("rolled-back", AppUpdater.FirstStartFailed), result);
        Assert.Equal(new SortedDictionary<string, string>(rig.Old), Snapshot(rig.Install)); // binaries: the old pair
        Assert.Equal(Database.SchemaVersion - 1, Database.PeekVersion(rig.Db)); // database: the old pair
        Assert.Equal(["user data one", "user data two"], Rows(rig.Db));
        // what was saved in the failed version is not lost: it is kept next to the update state
        string kept = Path.Combine(rig.Updates, "rolled-back-data", "susu-1.2.0.db");
        Assert.Equal(["user data one", "user data two", "saved in the new version"], Rows(kept));
        // loop guard
        Assert.True(guard.IsFailed("1.2.0"));
        Assert.Null(guard.Read());
        Assert.Equal(StartDecision.Proceed, guard.OnStart("1.1.0")); // the old version starts normally
        var journal = updater.Read()!;
        Assert.Equal((UpdateStages.RolledBack, AppUpdater.FirstStartFailed, "1.2.0"), (journal.Stage, journal.Error, journal.ToVersion));
        // the old version opens its own database at its own schema
        using (var old = Database.Open(rig.Db, targetVersion: Database.SchemaVersion - 1)) Assert.Equal(Database.SchemaVersion - 1, old.Version);

        // The same version is not offered, downloaded or installed again; a newer one is.
        var source = new OfferSource("1.2.0");
        var service = new AppUpdateService("1.1.0", source, updater, new UpdatePrefs(Path.Combine(rig.Root, "schedule.json")), new ManualClock(), () => 0, new Launcher(), true);
        service.Discard(); // the page shows the rolled-back notice until it is dismissed; after that checks report normally
        var checkedInfo = await service.CheckAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(("failed", "version-failed-before"), (checkedInfo.State, checkedInfo.Error));
        Assert.Equal("failed", (await service.DownloadAsync(TestContext.Current.CancellationToken)).State);
        Assert.Equal(0, source.Fetches);
        updater.Discard();
        var newer = new AppUpdateService("1.1.0", new OfferSource("1.3.0"), updater, new UpdatePrefs(Path.Combine(rig.Root, "schedule2.json")), new ManualClock(), () => 0, new Launcher(), true);
        Assert.Equal("available", (await newer.CheckAsync(false, TestContext.Current.CancellationToken)).State);
    }

    [Fact] // DATA03: the old app refuses the newer database with a clear message, and the paired copy is what the fallback restores
    public void An_older_app_refuses_the_newer_database_and_the_fallback_restores_the_paired_copy()
    {
        var rig = new Rig(real: true);
        var updater = rig.Committed();
        var refused = Assert.Throws<DatabaseVersionException>(() => Database.Open(rig.Db, targetVersion: Database.SchemaVersion - 1));
        Assert.Equal((Database.SchemaVersion, Database.SchemaVersion - 1), (refused.Found, refused.Supported));
        Assert.Contains("newer", refused.UserMessage(chinese: false));
        Assert.Contains("新", refused.UserMessage(chinese: true));
        Assert.Equal(Database.SchemaVersion, Database.PeekVersion(rig.Db)); // refusing changed nothing

        Assert.Equal("rolled-back", updater.RollBackToPrevious(AppUpdater.UserRollback).Outcome);
        using var old = Database.Open(rig.Db, targetVersion: Database.SchemaVersion - 1);
        Assert.Equal(Database.SchemaVersion - 1, old.Version);
        Assert.Equal(["user data one", "user data two"], Rows(rig.Db));
    }

    [Theory]
    [InlineData("fallback:kept")]
    [InlineData("rollback:journal")]
    [InlineData("rollback:file:0")]
    [InlineData("rollback:file:2")]
    [InlineData("rollback:files")]
    [InlineData("rollback:db")]
    [InlineData("rollback:done")]
    public void A_fallback_cut_off_at_any_step_is_finished_by_recovery_and_the_version_stays_marked(string point)
    {
        var rig = new Rig(real: true);
        rig.Committed();
        Execute(rig.Db, "INSERT INTO notes(text) VALUES ('after update');");
        var crashing = rig.Updater(new FaultAt(point));
        Assert.Throws<SimulatedCrash>(() => crashing.RollBackToPrevious(AppUpdater.FirstStartFailed));
        var fresh = rig.Updater();
        if (point == "fallback:kept")
        {
            // Cut off before the journal changed: the committed new version and its database are still a matching pair; the rollback simply runs again.
            Assert.Equal("committed-cleanup", fresh.Recover().Action);
            Assert.Equal("NEW exe 1.2.0", File.ReadAllText(Path.Combine(rig.Install, "susu.exe")));
            Assert.Equal(new ApplyResult("rolled-back", AppUpdater.FirstStartFailed), fresh.RollBackToPrevious(AppUpdater.FirstStartFailed));
        }
        var recovery = fresh.Recover();
        Assert.Contains(recovery.Action, new[] { "rolled-back", "none" }); // none: the crash came after the journal said RolledBack
        Assert.Equal(new SortedDictionary<string, string>(rig.Old), Snapshot(rig.Install));
        Assert.Equal(["user data one", "user data two"], Rows(rig.Db));
        Assert.Equal(Database.SchemaVersion - 1, Database.PeekVersion(rig.Db));
        Assert.True(fresh.Guard.IsFailed("1.2.0"));
        Assert.Null(fresh.Guard.Read());
        Assert.Equal(new RecoveryResult("none", UpdateStages.RolledBack), fresh.Recover()); // idempotent
    }

    [Fact]
    public void The_user_can_go_back_to_the_previous_version_once_while_the_pair_exists()
    {
        var rig = new Rig(real: true);
        var updater = rig.Committed();
        Assert.True(updater.CanRollBackToPrevious());
        rig.Env.Exit = UpdateExitOutcome.TimedOut;
        Assert.Equal(new ApplyResult("aborted", "exit-timeout"), updater.RollBackToPrevious(AppUpdater.UserRollback)); // the app is still running: nothing changes
        Assert.Equal("NEW exe 1.2.0", File.ReadAllText(Path.Combine(rig.Install, "susu.exe")));
        Assert.Equal(Database.SchemaVersion, Database.PeekVersion(rig.Db));
        rig.Env.Exit = UpdateExitOutcome.Exited;

        updater.Discard(); // the page's journal was dismissed; the pair is still there
        Assert.True(updater.CanRollBackToPrevious());
        Assert.Equal(new ApplyResult("rolled-back", AppUpdater.UserRollback), updater.RollBackToPrevious(AppUpdater.UserRollback, "1.2.0"));
        Assert.Equal(new SortedDictionary<string, string>(rig.Old), Snapshot(rig.Install));
        Assert.False(updater.Guard.IsFailed("1.2.0")); // a user's own choice does not block the version
        Assert.False(updater.CanRollBackToPrevious()); // once
        Assert.Equal("no-backup", updater.RollBackToPrevious(AppUpdater.UserRollback).Outcome);
    }

    [Fact]
    public void Going_back_needs_a_committed_update_and_an_intact_pair()
    {
        var rig = new Rig();
        Assert.Equal("no-backup", rig.Updater().RollBackToPrevious(AppUpdater.UserRollback).Outcome); // no update ever ran
        var staged = rig.Staged();
        Assert.False(staged.CanRollBackToPrevious()); // staged only
        Assert.Equal("committed", staged.Apply().Outcome);
        // a damaged backup is reported and the installed (new) version is left as it is
        string backedUp = Path.Combine(rig.Updates, "backup", "app", "susu.exe");
        File.WriteAllText(backedUp, "tampered backup");
        var result = staged.RollBackToPrevious(AppUpdater.UserRollback);
        Assert.Equal(new ApplyResult("failed", "rollback-incomplete"), result);
        Assert.Equal("NEW exe 1.2.0", File.ReadAllText(Path.Combine(rig.Install, "susu.exe")));
        Assert.Contains("migrated", Rows(rig.Db));
    }

    [Fact]
    public void The_service_starts_the_rollback_helper_only_on_the_users_command_and_names_the_running_tasks()
    {
        var rig = new Rig();
        var updater = rig.Committed();
        int tasks = 2;
        var launcher = new Launcher();
        var service = new AppUpdateService("1.2.0", null, updater, new UpdatePrefs(Path.Combine(rig.Root, "schedule.json")), new ManualClock(), () => tasks, launcher, true);
        Assert.True(service.CanRollBack);
        Assert.Equal(new AppUpdateInstallOutcome(false, "in-flight-needs-confirmation"), service.RollBack(false));
        Assert.Equal(0, launcher.Rollbacks);
        Assert.Equal(new AppUpdateInstallOutcome(true, null), service.RollBack(true));
        Assert.Equal(1, launcher.Rollbacks);
        var none = new AppUpdateService("1.2.0", null, new Rig().Updater(), new UpdatePrefs(Path.Combine(rig.Root, "s2.json")), new ManualClock(), () => 0, launcher, true);
        Assert.Equal(new AppUpdateInstallOutcome(false, "no-backup"), none.RollBack(true));
    }

    // ---------------- uninstall ----------------

    [Fact]
    public void Uninstall_deletes_only_a_folder_named_Su_Su_and_never_follows_links_inside_it()
    {
        string root = TestTemp.NewDir("susu-f18-uninstall");
        string prefix = $"Susu.F18Test.{Guid.NewGuid():N}.";
        string runKey = $@"Software\Susu.F18Test.{Guid.NewGuid():N}\Run";
        try
        {
            // a tampered data folder that is somebody else's folder is refused, even with the explicit delete choice
            string documents = Path.Combine(root, "Documents");
            Directory.CreateDirectory(documents);
            File.WriteAllText(Path.Combine(documents, "thesis.txt"), "keep");
            var refused = InstallerSupport.Uninstall([documents], deleteUserData: true, prefix, removeAutostart: false, runKeyPath: runKey);
            Assert.False(refused.UserDataDeleted);
            Assert.Equal(1, refused.FoldersRefused);
            Assert.True(File.Exists(Path.Combine(documents, "thesis.txt")));

            // a real data folder with a junction inside it, and a read-only file
            string data = Path.Combine(root, "Local", "Su-Su");
            string outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(Path.Combine(data, "cache"));
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep");
            File.WriteAllText(Path.Combine(data, "settings.json"), "{}");
            File.SetAttributes(Path.Combine(data, "settings.json"), FileAttributes.ReadOnly);
            Junction(Path.Combine(data, "cache", "link"), outside);
            bool symlink = Cmd($"mklink /D \"{Path.Combine(data, "symlink")}\" \"{outside}\"") == 0; // needs a privilege; the junction case above is the always-run one

            // default: nothing is deleted
            var kept = InstallerSupport.Uninstall([data], deleteUserData: false, prefix, removeAutostart: false, runKeyPath: runKey);
            Assert.False(kept.UserDataDeleted);
            Assert.True(File.Exists(Path.Combine(data, "settings.json")));

            var deleted = InstallerSupport.Uninstall([data], deleteUserData: true, prefix, removeAutostart: false, runKeyPath: runKey);
            Assert.True(deleted.UserDataDeleted);
            Assert.Equal(0, deleted.FoldersRefused);
            Assert.False(Directory.Exists(data));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "precious.txt")));
            _ = symlink;
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Path.GetDirectoryName(runKey)!, throwOnMissingSubKey: false); }
    }
}
