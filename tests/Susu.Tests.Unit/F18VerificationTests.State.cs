using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Susu.Abstractions;
using Susu.Plugins.AppUpdate;
using Susu.Storage;
using Xunit;

namespace Susu.Tests.Unit;

// Planted state, TOCTOU, replacement of known files, first-start counters, loop guard, DATA03 and path-rule extras.
public partial class F18VerificationTests
{
    // ================= planted journal and backup state =================

    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string[] HostilePaths(string outsideFile) =>
    [
        @"..\outside\keep.txt", "../outside/keep.txt", @"ui\..\..\outside\keep.txt", outsideFile, @"C:\Windows\System32\drivers\etc\hosts", @"C:x", @"\\?\C:\x", @"\\server\share\x",
        "susu.exe:stream", "ui/index.html::$DATA", "CON", "ui/NUL.txt", "a.", "a ", "/abs/x", "", " ", new string('a', 300), "ui//x", "ui\\\\x", "a/./b", "x\0y", "dir/", "..", ".",
        "ui/app.js.susu-new", "ui/<>|x", "COM1.txt", "LPT9", "ui/AUX.js", "aux",
    ];

    [Fact] // UPD08: a planted journal naming any hostile path form is unreadable, and recovery/apply with it changes nothing outside the install folder
    public void A_planted_journal_with_hostile_paths_is_treated_as_damaged_and_nothing_outside_the_install_folder_changes()
    {
        var rig = new UpRig();
        string outside = Path.Combine(rig.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(rig.Root, "sibling.txt"), "keep");
        var before = Tree(outside);
        rig.Staged();
        string installKey = Path.GetFullPath(rig.Install).TrimEnd('\\');
        string journalPath = Path.Combine(rig.Updates, "journal.json");
        int checkedForms = 0;
        foreach (string evil in HostilePaths(Path.Combine(outside, "keep.txt")))
            foreach (string stage in new[] { UpdateStages.Replacing, UpdateStages.RollingBack, UpdateStages.Staged, UpdateStages.Committed, UpdateStages.Healthy })
            {
                var journal = new UpdateJournal("x", stage, "1.1.0", "1.2.0", 5, "susu-1.2.0.zip", rig.Offer.Sha256, rig.Offer.Size, [new UpdateFileEntry(evil, new string('0', 64), 1)], null, installKey);
                File.WriteAllBytes(journalPath, JsonSerializer.SerializeToUtf8Bytes(journal, Camel));
                File.Delete(journalPath + ".prev");
                var updater = rig.Updater();
                Assert.True(updater.Read() is null, $"journal naming '{evil}' was accepted at {stage}");
                updater.Recover();
                updater.Apply();
                Assert.True(Tree(outside).SequenceEqual(before), $"outside changed by journal path '{evil}' at {stage}");
                Assert.Equal("keep", File.ReadAllText(Path.Combine(rig.Root, "sibling.txt")));
                checkedForms++;
                rig.Staged(); // a damaged journal makes recovery clear the stage: stage again for the next form
            }
        Assert.True(checkedForms >= 150);
        AssertOldPair(rig);
    }

    [Fact] // UPD08: a planted backup index naming hostile paths is "damaged": a user rollback and recovery refuse and nothing outside or inside changes
    public void A_planted_backup_index_with_hostile_paths_makes_the_rollback_refuse_and_changes_nothing()
    {
        foreach (string evil in HostilePaths("x"))
        {
            var rig = new UpRig();
            string outside = Path.Combine(rig.Root, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
            var before = Tree(outside);
            var updater = rig.Staged();
            Assert.Equal("committed", updater.Apply().Outcome);
            string installKey = Path.GetFullPath(rig.Install).TrimEnd('\\');
            string indexPath = Path.Combine(rig.Updates, "backup", "backup.json");
            File.WriteAllBytes(indexPath, JsonSerializer.SerializeToUtf8Bytes(new BackupIndex("1.1.0", [new UpdateFileEntry(evil, new string('0', 64), 1)], true, "", installKey), Camel));
            var fresh = rig.Updater();
            var result = fresh.RollBackToPrevious(AppUpdater.UserRollback);
            Assert.True(result.Outcome is "no-backup" or "failed", $"{evil}: {result}");
            fresh.Recover();
            Assert.True(Tree(outside).SequenceEqual(before), "outside changed by backup path " + evil);
            AssertNewPair(rig, "backup path " + evil); // the committed new version is untouched by a refused rollback
        }
    }

    [Fact] // UPD08: a backup directory replaced by a junction is never used (rollback refused, a later update refused); the junction target is not modified
    public void A_junction_in_place_of_the_backup_folder_is_never_followed()
    {
        var rig = new UpRig();
        var updater = rig.Staged();
        Assert.Equal("committed", updater.Apply().Outcome);
        string backup = Path.Combine(rig.Updates, "backup");
        Directory.Move(backup, Path.Combine(rig.Root, "real-backup"));
        string evilTarget = Path.Combine(rig.Root, "evil-backup");
        Directory.CreateDirectory(evilTarget);
        File.WriteAllText(Path.Combine(evilTarget, "marker.txt"), "marker");
        Assert.Equal(0, Cmd($"mklink /J \"{backup}\" \"{evilTarget}\""));
        var fresh = rig.Updater();
        Assert.Equal("no-backup", fresh.RollBackToPrevious(AppUpdater.UserRollback).Outcome);
        Assert.Equal(["marker.txt"], Directory.EnumerateFiles(evilTarget).Select(Path.GetFileName).ToArray());
        AssertNewPair(rig);
        // An update started over a junctioned backup folder does not write its backup through the link either.
        rig.SetPackage(new Dictionary<string, string>(rig.New) { ["ui/app.js"] = "NEWER js" }, "1.3.0", 6);
        var again = rig.Updater();
        again.BeginDownload(rig.Offer, "1.2.0");
        rig.Place();
        var staged = again.Stage(rig.ZipPath);
        var applied = staged.Ok ? again.Apply() : new ApplyResult("not-staged", staged.Error);
        Assert.NotEqual("committed", applied.Outcome);
        Assert.Equal(["marker.txt"], Directory.EnumerateFiles(evilTarget).Select(Path.GetFileName).ToArray());
    }

    private static int Cmd(string command)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + command) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    // ================= TOCTOU between verify and replace =================

    [Fact] // UPD08: the stage folder's subfolder swapped for a junction to look-alike evil files between the verification and the copy is caught and rolled back
    public void A_stage_subfolder_swapped_for_a_junction_after_verification_is_caught_by_the_per_file_hash()
    {
        var rig = new UpRig();
        string evil = Path.Combine(rig.Root, "evil-ui");
        Directory.CreateDirectory(evil);
        File.WriteAllText(Path.Combine(evil, "index.html"), "EVIL ui");
        File.WriteAllText(Path.Combine(evil, "app.js"), "EVIL js");
        var hook = new HookPoint(new()
        {
            ["replace:journal"] = () =>
            {
                string ui = Path.Combine(rig.Updates, "stage", "ui");
                Directory.Delete(ui, recursive: true);
                Assert.Equal(0, Cmd($"mklink /J \"{ui}\" \"{evil}\""));
            },
        });
        var result = rig.Staged(hook).Apply();
        Assert.Equal("rolled-back", result.Outcome);
        AssertOldPair(rig);
        Assert.Equal("EVIL ui", File.ReadAllText(Path.Combine(evil, "index.html"))); // the link target was only read
    }

    [Fact] // UPD08: the downloaded package replaced after it was staged does not matter: the verified stage is what is installed
    public void A_package_swapped_after_staging_changes_nothing_that_is_installed()
    {
        var rig = new UpRig();
        var updater = rig.Staged();
        File.WriteAllBytes(rig.ZipPath, ZipOfFiles([("susu.exe", "EVIL exe"u8.ToArray()), ("ui/index.html", "EVIL"u8.ToArray())]));
        Assert.Equal("committed", updater.Apply().Outcome);
        AssertNewPair(rig);
    }

    [Fact] // UPD08: a staged file replaced by a same-size other, a bigger and an empty file, at each file, is caught before it is run
    public void A_staged_file_swapped_at_each_file_by_same_size_other_size_or_empty_content_is_never_installed()
    {
        foreach (string target in new[] { "susu.exe", "susu_native.dll", "ui/index.html", "ui/app.js" })
            foreach (string kind in new[] { "same-size", "bigger", "empty" })
            {
                var rig = new UpRig();
                var hook = new HookPoint(new()
                {
                    ["replace:journal"] = () =>
                    {
                        string path = Path.Combine(rig.Updates, "stage", target.Replace('/', '\\'));
                        string current = File.ReadAllText(path);
                        File.WriteAllText(path, kind switch { "same-size" => new string('E', current.Length), "bigger" => current + " EVIL", _ => "" });
                    },
                });
                var result = rig.Staged(hook).Apply();
                Assert.True(result.Outcome == "rolled-back" && result.Error == "stage-corrupt", $"{target}/{kind}: {result}");
                AssertOldPair(rig, $"{target}/{kind}");
            }
    }

    // ================= replacement keeps the user's files =================

    [Fact] // UPD08: only files the earlier version is KNOWN to have brought are removed; look-alike names, other case and alternate streams survive an update, a rollback and a second update
    public void Replacement_removes_only_known_files_and_leaves_user_files_with_look_alike_names_and_streams()
    {
        var rig = new UpRig();
        var user = new Dictionary<string, string>
        {
            ["plugins/susu.exe"] = "user copy of a program name in a subfolder",
            ["ui/SUSU_NATIVE.DLL"] = "same name as a root entry, in the ui folder",
            ["ui/legacy/mine.txt"] = "user file in a folder the old version made",
            ["Susu.exe.bak"] = "backup the user made",
            ["ui/Index.html.bak"] = "case-different look-alike",
            ["notes.txt"] = "plain user note",
            ["UI/user-theme.css"] = "user file under a case-different folder name",
        };
        foreach (var (path, text) in user) PutFile(rig.Install, path, text);
        File.WriteAllText(Path.Combine(rig.Install, "notes.txt") + ":meta", "alternate stream of a user file");
        void AssertUserFiles(string when)
        {
            foreach (var (path, text) in user)
            {
                string full = Path.Combine(rig.Install, path.Replace('/', '\\'));
                Assert.True(File.Exists(full) && File.ReadAllText(full) == text, $"{when}: user file {path} changed or removed");
            }
            Assert.Equal("alternate stream of a user file", File.ReadAllText(Path.Combine(rig.Install, "notes.txt") + ":meta"));
        }

        // 1. forward update: old-only.dll and ui/legacy/x.js go (known), user files stay, ui/legacy keeps mine.txt
        Assert.Equal("committed", rig.Staged().Apply().Outcome);
        Assert.False(File.Exists(Path.Combine(rig.Install, "old-only.dll")));
        Assert.False(File.Exists(Path.Combine(rig.Install, "ui", "legacy", "x.js")));
        Assert.Equal("NEW exe 1.2.0", File.ReadAllText(Path.Combine(rig.Install, "susu.exe")));
        AssertUserFiles("after the update");

        // 2. the same user files across a user rollback to the previous version
        Assert.Equal("rolled-back", rig.Updater().RollBackToPrevious(AppUpdater.UserRollback).Outcome);
        Assert.Equal("OLD exe 1.1.0", File.ReadAllText(Path.Combine(rig.Install, "susu.exe")));
        Assert.True(File.Exists(Path.Combine(rig.Install, "old-only.dll")));
        AssertUserFiles("after the rollback");

        // 3. a failing update (health) over the user files, then a good second update
        rig.Env.HealthOk = false;
        Assert.Equal("rolled-back", rig.Staged().Apply().Outcome);
        AssertUserFiles("after a failed update");
        rig.Env.HealthOk = true;
        Assert.Equal("committed", rig.Staged().Apply().Outcome);
        AssertUserFiles("after the second update");
    }

    // ================= first-start guard: counter and state-file manipulation =================

    private static int StartsThatProceed(FirstStartGuard guard, string version = "1.2.0")
    {
        int n = 0;
        while (n < 40 && guard.OnStart(version) == StartDecision.Proceed) n++;
        return n;
    }

    private static string GuardJson(object attempts, bool confirmed = false, string version = "1.2.0")
        => $"{{\"version\":\"{version}\",\"fromVersion\":\"1.1.0\",\"attempts\":{attempts},\"confirmed\":{(confirmed ? "true" : "false")}}}";

    [Fact] // UPD08: the guard stops the third unconfirmed start; confirmation, damaged files, huge counters and a state for another version behave as designed
    public void The_first_start_guard_counts_exactly_and_refuses_damaged_or_extreme_counters()
    {
        string folder = TestTemp.NewDir("susu-f18v-guard");
        var guard = new FirstStartGuard(folder);
        string state = Path.Combine(folder, "first-start.json");

        guard.Arm("1.2.0", "1.1.0");
        Assert.Equal(2, StartsThatProceed(guard)); // two counted starts, the third falls back
        Assert.Equal(StartDecision.Fallback, guard.OnStart("1.2.0")); // and keeps refusing
        guard.Confirm("1.2.0");
        Assert.False(File.Exists(state)); // a confirmation releases the guard
        Assert.Equal(StartDecision.Proceed, guard.OnStart("1.2.0"));

        foreach (var (why, text) in new (string, string)[]
        {
            ("counter int.MaxValue", GuardJson(int.MaxValue)), ("counter 2", GuardJson(2)), ("counter far past the limit", GuardJson(1_000_000)),
            ("counter as a float", GuardJson("1.5")), ("counter as a string", GuardJson("\"1\"")), ("counter beyond int", GuardJson("99999999999999999999")),
            ("empty file", ""), ("not json", "{ not json"), ("json array", "[]"), ("truncated", "{\"version\":\"1.2.0\",\"attem"), ("binary", "\u0001\u0002\u0003"),
        })
        {
            File.WriteAllText(state, text);
            Assert.True(StartsThatProceed(guard) == 0, $"{why}: the guard must refuse at once");
        }
        // A start of ANOTHER version removes a stale state and proceeds (the old version running after a fallback).
        File.WriteAllText(state, GuardJson(2, version: "1.3.0"));
        Assert.Equal(StartDecision.Proceed, guard.OnStart("1.2.0"));
        Assert.False(File.Exists(state));
        // A confirmed state never counts.
        File.WriteAllText(state, GuardJson(0, confirmed: true));
        Assert.Equal(40, StartsThatProceed(guard));
    }

    [Fact] // UPD08: Known_gap (DEFECT D4, low): a hand-edited state file can switch the guard off or force a fallback; the same-user threat model accepts it, pinned so it is explicit
    public void Known_gap_the_first_start_state_file_can_be_edited_to_disable_the_guard_or_to_force_a_fallback()
    {
        string folder = TestTemp.NewDir("susu-f18v-guard2");
        var guard = new FirstStartGuard(folder);
        string state = Path.Combine(folder, "first-start.json");
        // negative counters extend the crash loop: -5 allows seven unconfirmed starts instead of two
        File.WriteAllText(state, GuardJson(-1)); Assert.Equal(3, StartsThatProceed(guard));
        File.WriteAllText(state, GuardJson(-5)); Assert.Equal(7, StartsThatProceed(guard));
        File.WriteAllText(state, GuardJson(int.MinValue)); Assert.Equal(40, StartsThatProceed(guard)); // effectively off
        // "null", "{}" and a missing file read as "no guard"
        File.WriteAllText(state, "null"); Assert.Equal(40, StartsThatProceed(guard));
        File.WriteAllText(state, "{}"); Assert.Equal(StartDecision.Proceed, guard.OnStart("1.2.0")); Assert.False(File.Exists(state)); // cleared as "another version"
        File.Delete(state); Assert.Equal(40, StartsThatProceed(guard));
        // a directory (or a junction) where the file should be: File.Exists is false, nothing is ever counted
        Directory.CreateDirectory(state);
        Assert.Equal(40, StartsThatProceed(guard));
        Directory.Delete(state);
        // a read-only state file: the start cannot be counted, so it is refused (fallback) even though the version may be perfectly healthy
        File.WriteAllText(state, GuardJson(0));
        File.SetAttributes(state, FileAttributes.ReadOnly);
        try { Assert.Equal(StartDecision.Fallback, guard.OnStart("1.2.0")); }
        finally { File.SetAttributes(state, FileAttributes.Normal); }
    }

    [Fact] // UPD08: the loop guard end to end through the real source and service: a failed version is not offered again, also from a replayed manifest with a higher sequence
    public async Task A_failed_version_is_not_offered_again_and_a_replayed_manifest_for_it_with_a_higher_sequence_has_no_effect()
    {
        var ct = TestContext.Current.CancellationToken;
        string root = TestTemp.NewDir("susu-f18v-loop");
        var first = new DownloadRig((rig, r) => Ok(rig.PackageBytes), null, "1.2.0", 5, root);
        Assert.Equal("available", (await first.Service.CheckAsync(false, ct)).State);
        first.Updater.Guard.MarkFailed("1.2.0"); // what the first-start fallback records
        var blocked = await first.Service.CheckAsync(false, ct);
        Assert.True(blocked.State == "failed" && blocked.Error == "version-failed-before" && blocked.Offer is null, blocked.State + " " + blocked.Error);
        Assert.Equal("failed", (await first.Service.DownloadAsync(ct)).State);
        Assert.Null(first.Updater.Read()); // nothing was downloaded
        Assert.False(first.Service.Install(false).Ok);
        // a replayed manifest for the same version with a higher sequence (a genuinely signed re-release), as after an app restart (new service, same data)
        var replay = new DownloadRig((rig, r) => Ok(rig.PackageBytes), null, "1.2.0", 9, root);
        var replayed = await replay.Service.CheckAsync(false, ct);
        Assert.True(replayed.State == "failed" && replayed.Error == "version-failed-before", replayed.State + " " + replayed.Error);
        Assert.Equal("failed", (await replay.Service.DownloadAsync(ct)).State);
        Assert.Null(replay.Updater.Read());
        // Known gap (D4): the record is a file in the user's own profile; deleting it re-offers the version.
        File.Delete(Path.Combine(root, "updates", "failed-versions.json"));
        var again = new DownloadRig((rig, r) => Ok(rig.PackageBytes), null, "1.2.0", 11, root);
        Assert.Equal("available", (await again.Service.CheckAsync(false, ct)).State);
        again.Updater.Guard.MarkFailed("1.2.0");
        // the next version number is offered normally
        var next = new DownloadRig((rig, r) => Ok(rig.PackageBytes), null, "1.2.1", 12, root);
        Assert.Equal("available", (await next.Service.CheckAsync(false, ct)).State);
    }

    // ================= DATA03: an older build against a newer database =================

    [Fact] // DATA03: an older build refuses a newer schema (any distance) with both messages and leaves the data readable and unchanged
    public void An_older_build_refuses_a_newer_database_of_any_distance_and_the_data_is_unchanged()
    {
        foreach (int version in new[] { Database.SchemaVersion + 1, Database.SchemaVersion + 1000, int.MaxValue })
        {
            string path = Path.Combine(TestTemp.NewDir("susu-f18v-db"), "susu.db");
            Sql(path, $"PRAGMA journal_mode=WAL; CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT); INSERT INTO meta VALUES('schemaVersion','{version}'); CREATE TABLE notes(id INTEGER PRIMARY KEY, text TEXT); INSERT INTO notes(text) VALUES ('newer data');");
            byte[] bytes = File.ReadAllBytes(path);
            var error = Assert.Throws<DatabaseVersionException>(() => Database.Open(path));
            Assert.Equal((version, Database.SchemaVersion), (error.Found, error.Supported));
            Assert.Contains(version.ToString(), error.UserMessage(chinese: false));
            Assert.Contains(version.ToString(), error.UserMessage(chinese: true));
            Assert.Equal(bytes, File.ReadAllBytes(path)); // a database already in WAL mode is not modified at all
            Assert.False(File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > 0);
            Assert.Equal(["newer data"], NoteRows(path));
            Assert.Equal(version, Database.PeekVersion(path));
        }
        // A rollback-journal database (not what a newer Su-Su writes) is switched to WAL by the PRAGMA before the version is read: the data and the version are unchanged.
        string legacy = Path.Combine(TestTemp.NewDir("susu-f18v-db"), "susu.db");
        Sql(legacy, $"CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT); INSERT INTO meta VALUES('schemaVersion','{Database.SchemaVersion + 1}'); CREATE TABLE notes(id INTEGER PRIMARY KEY, text TEXT); INSERT INTO notes(text) VALUES ('legacy');");
        Assert.Throws<DatabaseVersionException>(() => Database.Open(legacy));
        Assert.Equal(["legacy"], NoteRows(legacy));
        Assert.Equal(Database.SchemaVersion + 1, Database.PeekVersion(legacy));
    }

    [Fact] // DATA03: Known_gap (DEFECT D5, low): a non-numeric or overflowing schema version is not a DatabaseVersionException, so the start-up handler for it does not apply
    public void Known_gap_a_corrupt_schema_version_value_throws_a_plain_format_or_overflow_exception()
    {
        foreach (string value in new[] { "abc", "99999999999", "" })
        {
            string path = Path.Combine(TestTemp.NewDir("susu-f18v-db"), "susu.db");
            Sql(path, $"PRAGMA journal_mode=WAL; CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT); INSERT INTO meta VALUES('schemaVersion','{value}'); CREATE TABLE notes(id INTEGER PRIMARY KEY, text TEXT); INSERT INTO notes(text) VALUES ('kept');");
            var error = Assert.ThrowsAny<Exception>(() => Database.Open(path));
            Assert.IsNotType<DatabaseVersionException>(error);
            Assert.True(error is FormatException or OverflowException, error.GetType().Name);
            Assert.Equal(["kept"], NoteRows(path));
        }
    }

    // ================= path rule extras =================

    [Fact] // UPD08: more tampered path forms than the coding agents' table (Unicode look-alikes, device namespaces, stream syntax, length edges, 8.3 aliases)
    public void Path_rules_refuse_unicode_lookalikes_namespace_prefixes_and_length_edges_and_accept_short_names_as_other_spellings()
    {
        string temp = Path.GetTempPath().TrimEnd('\\');
        foreach (string bad in new[]
        {
            "\uFF23:\\x", "C\uFF1A\\x", "\u0421:\\x", @"\??\C:\x", @"\\?\GLOBALROOT\Device\x", @"\\.\pipe\x", @"\\?\Volume{00000000-0000-0000-0000-000000000000}\x", @"Global\C:\x",
            @"C:\x\y:$I30:$INDEX_ALLOCATION", @"C:\x\y::$DATA", @"C:\x\y:", @"C:\x:y\z", @"C::\x", @"CC:\x", @"C:\x\y\ ",
            @"C:\x\con.", @"C:\x\Prn.txt", @"C:\x\COM9.log", @"C:\x\lpt1", @"C:\x\NUL", @"C:\x\a\..", @"C:\..", @"C:\.",
            @"C:/x/../y", @"C:\x\..\..\y", @"C:\\x", @"//server/share/x", @"C:\x\%USERPROFILE%\..", @"C:\x" + new string('a', 250), "C:\\x\u0001y", "C:\\x\ty",
        })
            Assert.True(UpdatePathRules.PlainAbsolute(bad) is null, $"accepted: {bad}");
        // length edge: exactly the maximum is accepted when otherwise plain, one more is not
        string prefix = temp + @"\";
        string atLimit = prefix + new string('a', UpdatePathRules.MaxLength - prefix.Length);
        Assert.Equal(UpdatePathRules.MaxLength, atLimit.Length);
        Assert.NotNull(UpdatePathRules.PlainAbsolute(atLimit));
        Assert.Null(UpdatePathRules.PlainAbsolute(atLimit + "a"));
        // an 8.3 alias is just another plain spelling: the path rule accepts it (the journal binding then refuses a folder spelled differently from the one the journal names)
        string install = Path.Combine(TestTemp.NewDir("susu-f18v-short"), "Program Folder With Spaces");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "susu.exe"), "x");
        var shortName = new StringBuilder(260);
        if (GetShortPathName(install, shortName, shortName.Capacity) == 0 || !shortName.ToString().Contains('~')) Assert.Skip("8.3 short names are disabled on this volume.");
        string updates = Path.Combine(TestTemp.NewDir("susu-f18v-short-updates"), "updates");
        Assert.NotNull(UpdatePathRules.InstallFolder(shortName.ToString(), updates));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferSize);
}
