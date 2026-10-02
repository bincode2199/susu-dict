using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Susu.Abstractions;
using Susu.Plugins.AppUpdate;
using Susu.Storage;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F18.2 with the real published susu.exe (NativeAOT, self-contained): the probe commands run by the updater, and the helper (<c>--apply-update</c>,
/// <c>--recover-update</c>) run as a separate process from a COPY of the executable outside the install folder against a temp install folder (susu-f18-*) and
/// a temp data root. The real install, the real data and the machine's settings are never touched. Skipped (returns) when the publish output is absent.
/// </summary>
[SupportedOSPlatform("windows")]
public class F18UpdateHostTests
{
    private static string? Publish() => PluginHostIntegrationTests.FindHostBuildOutput();

    private static string VersionOf(string exe)
    {
        var v = FileVersionInfo.GetVersionInfo(exe);
        return $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}";
    }

    private static (int Exit, string Report) Run(string exe, params string[] args)
    {
        string report = Path.Combine(TestTemp.NewDir("susu-f18-hostreport"), "r.json");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string a in args) info.ArgumentList.Add(a);
        info.ArgumentList.Add("--report");
        info.ArgumentList.Add(report);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.True(process.WaitForExit(120_000));
        return (process.ExitCode, (File.Exists(report) ? File.ReadAllText(report) : "") + (process.ExitCode != 0 && error.Result.Length > 0 ? " STDERR: " + error.Result : ""));
    }

    private static void CopyProgramFiles(string publish, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(publish).Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
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

    private sealed class Rig
    {
        public required string Publish, Install, Root, ExeVersion;
        public required AppPaths Paths;
        public required AppUpdater Updater;
        public string HelperCopy = "";
        public string Exe => Path.Combine(Install, "susu.exe");
    }

    private sealed class RealEnvStub : IUpdateEnvironment
    {
        public UpdateExitOutcome StopApp(TimeSpan timeout) => UpdateExitOutcome.NotRunning;
        public bool Migrate(string installDirectory, string databasePath, out string? detail) { detail = null; return true; }
        public bool HealthCheck(string installDirectory, string databasePath, string expectedVersion, out string? detail) { detail = null; return true; }
        public long FreeBytes(string path) => long.MaxValue;
    }

    private static Rig NewRig(string publish, string toVersion, IFaultPoint? faults = null)
    {
        string root = TestTemp.NewDir("susu-f18-hostroot");
        var paths = AppPaths.Resolve(root, development: false).EnsureCreated();
        string install = Path.Combine(TestTemp.NewDir("susu-f18-install"), "app");
        CopyProgramFiles(publish, install);
        // The user's database at the previous schema version with user data.
        using (Database.Open(paths.Database, targetVersion: Database.SchemaVersion - 1)) { }
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.Database, Pooling = false }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE notes(id INTEGER PRIMARY KEY, text TEXT); INSERT INTO notes(text) VALUES ('user data one'), ('user data two');";
            cmd.ExecuteNonQuery();
        }
        // The new package: the same program files plus a marker (same build, so its probes pass; the marker proves the replacement happened).
        string zipPath = Path.Combine(TestTemp.NewDir("susu-f18-pkg"), "susu-new.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (string file in Directory.EnumerateFiles(install)) zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Fastest);
            var marker = zip.CreateEntry("NEW-MARKER.txt");
            using (var s = marker.Open()) s.Write("new version"u8);
        }
        byte[] bytes = File.ReadAllBytes(zipPath);
        var offer = new AppUpdateOffer(toVersion, 5, "susu-new.zip", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), "");
        var updater = new AppUpdater(install, paths.Database, paths.Updates, new RealEnvStub(), faults);
        updater.BeginDownload(offer, "0.0.1");
        zipPath = Path.Combine(paths.Updates, "downloads", "susu-new.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        File.WriteAllBytes(zipPath, bytes);
        Assert.True(updater.Stage(zipPath).Ok);
        // The helper is a copy of the executable outside the install folder (the install folder's files are replaced while it runs).
        string helperFolder = Path.Combine(paths.Updates, "helper", "test");
        Directory.CreateDirectory(helperFolder);
        string helper = Path.Combine(helperFolder, "susu-update.exe");
        File.Copy(Path.Combine(install, "susu.exe"), helper);
        File.Copy(Path.Combine(install, "e_sqlite3.dll"), Path.Combine(helperFolder, "e_sqlite3.dll")); // what ProcessUpdateLauncher copies
        return new Rig { Publish = publish, Install = install, Root = root, ExeVersion = VersionOf(Path.Combine(install, "susu.exe")), Paths = paths, Updater = updater, HelperCopy = helper };
    }

    [Fact]
    public void The_probe_commands_of_the_published_exe_migrate_the_database_and_check_the_expected_version()
    {
        string? publish = Publish();
        if (publish is null) return;
        string exe = Path.Combine(publish, "susu.exe");
        string root = TestTemp.NewDir("susu-f18-probe");
        var paths = AppPaths.Resolve(root, development: false).EnsureCreated();
        using (Database.Open(paths.Database, targetVersion: Database.SchemaVersion - 1)) { }
        Assert.Equal(Database.SchemaVersion - 1, Database.PeekVersion(paths.Database));
        string version = VersionOf(exe);
        Assert.Equal(22, Run(exe, "--update-health", "--expect-version", version, "--data-root", root).Exit); // not migrated yet: the schema is older than this build's
        Assert.Equal(0, Run(exe, "--update-migrate", "--data-root", root).Exit);
        Assert.Equal(Database.SchemaVersion, Database.PeekVersion(paths.Database));
        Assert.Equal(0, Run(exe, "--update-health", "--expect-version", version, "--data-root", root).Exit);
        Assert.Equal(22, Run(exe, "--update-health", "--expect-version", "9.9.9", "--data-root", root).Exit);
        Assert.Equal(2, Run(exe, "--update-health", "--data-root", root).Exit); // no expected version
        Assert.Equal(2, Run(exe, "--update-migrate", "--bogus").Exit);
    }

    [Fact]
    public void The_real_helper_stops_nothing_replaces_the_files_migrates_with_the_new_exe_and_commits()
    {
        string? publish = Publish();
        if (publish is null) return;
        string probe = Path.Combine(publish, "susu.exe");
        var rig = NewRig(publish, VersionOf(probe));
        var run = Run(rig.HelperCopy, "--apply-update", "--install-dir", rig.Install, "--data-root", rig.Root, "--no-restart");
        Assert.True(run.Exit == 0, run.Report);
        Assert.Contains("\"state\":\"committed\"", run.Report);
        Assert.True(File.Exists(Path.Combine(rig.Install, "NEW-MARKER.txt")));
        Assert.Equal(UpdateStages.Committed, rig.Updater.Read()!.Stage);
        Assert.Equal(Database.SchemaVersion, Database.PeekVersion(rig.Paths.Database)); // the new build migrated the database
        Assert.Equal(["user data one", "user data two"], Rows(rig.Paths.Database));
        // The paired backup holds the old version of the database.
        Assert.Equal(Database.SchemaVersion - 1, Database.PeekVersion(Path.Combine(rig.Updater.BackupFolder, "susu.db")));
        Assert.Empty(Directory.EnumerateFiles(rig.Install, "*.susu-new", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_failed_health_check_of_the_real_new_exe_restores_the_old_files_and_the_old_database_together()
    {
        string? publish = Publish();
        if (publish is null) return;
        var rig = NewRig(publish, "9.9.9"); // the journal expects 9.9.9; the package's exe is another version, so its health probe fails
        var run = Run(rig.HelperCopy, "--apply-update", "--install-dir", rig.Install, "--data-root", rig.Root, "--no-restart");
        Assert.Equal(30, run.Exit);
        Assert.Contains("rolled-back:health-check-failed", run.Report);
        Assert.False(File.Exists(Path.Combine(rig.Install, "NEW-MARKER.txt")));
        Assert.Equal(Database.SchemaVersion - 1, Database.PeekVersion(rig.Paths.Database)); // the migration the new exe did is undone with the files
        Assert.Equal(["user data one", "user data two"], Rows(rig.Paths.Database));
        Assert.Equal(UpdateStages.RolledBack, rig.Updater.Read()!.Stage);
        // The restored old exe still works.
        Assert.Equal(0, Run(rig.Exe, "--update-migrate", "--data-root", rig.Root).Exit);
    }

    [Fact]
    public void A_helper_started_for_recovery_finishes_a_replacement_that_was_cut_off()
    {
        string? publish = Publish();
        if (publish is null) return;
        var rig = NewRig(publish, VersionOf(Path.Combine(publish, "susu.exe")), new FaultAt("replace:2"));
        Assert.Throws<SimulatedCrash>(() => rig.Updater.Apply()); // the process "died" with the install folder half replaced
        Assert.True(File.Exists(Path.Combine(rig.Install, "NEW-MARKER.txt")) || rig.Updater.Read()!.Stage == UpdateStages.Replacing);
        var recovering = new AppUpdater(rig.Install, rig.Paths.Database, rig.Paths.Updates, new RealEnvStub());
        Assert.True(recovering.NeedsFileRecovery()); // the app would start the helper rather than touch its own files
        var run = Run(rig.HelperCopy, "--recover-update", "--install-dir", rig.Install, "--data-root", rig.Root, "--no-restart");
        Assert.True(run.Exit == 0, run.Report);
        Assert.Contains("recovered:rolled-back", run.Report);
        Assert.False(File.Exists(Path.Combine(rig.Install, "NEW-MARKER.txt")));
        Assert.Equal(["user data one", "user data two"], Rows(rig.Paths.Database));
        Assert.False(recovering.NeedsFileRecovery());
        // Nothing staged any more is not an update to apply.
        Assert.Equal(33, Run(rig.HelperCopy, "--apply-update", "--install-dir", rig.Install, "--data-root", rig.Root, "--no-restart").Exit);
    }

    [Fact]
    public void The_helper_refuses_bad_arguments_a_missing_install_folder_and_a_second_helper()
    {
        string? publish = Publish();
        if (publish is null) return;
        string exe = Path.Combine(publish, "susu.exe");
        string root = TestTemp.NewDir("susu-f18-args");
        Assert.Equal(2, Run(exe, "--apply-update", "--data-root", root).Exit); // no install folder
        Assert.Equal(2, Run(exe, "--apply-update", "--install-dir", Path.Combine(root, "missing"), "--data-root", root).Exit);
        Assert.Equal(2, Run(exe, "--apply-update", "--unknown-flag").Exit);
        // While another helper holds the update lock, a second one does not run.
        var holder = UpdateLockForTest(root);
        try { Assert.Equal(34, Run(exe, "--apply-update", "--install-dir", publish, "--data-root", root, "--no-restart").Exit); }
        finally { holder.ReleaseMutex(); holder.Dispose(); }
    }

    private static Mutex UpdateLockForTest(string dataRoot)
    {
        // The same name the helper computes: "Local\Su-Su.Update." + the instance-name suffix for the data root.
        string suffix = AtomicFile.Hash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).ToLowerInvariant()))[..16];
        var mutex = new Mutex(false, "Local\\Su-Su.Update." + suffix);
        Assert.True(mutex.WaitOne(0));
        return mutex;
    }
}
