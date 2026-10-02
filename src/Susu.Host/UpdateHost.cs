using System.Diagnostics;
using System.Runtime.Versioning;
using Susu.Plugins.AppUpdate;
using Susu.Storage;
using Susu.Windows;

namespace Susu.Host;

/// <summary>
/// F18.2: the machine side of the application update (ARCHITECTURE 10). The running app never replaces its own files. "Install" starts a COPY of the
/// executable from the updates folder with <c>--apply-update</c>; that helper takes the update lock, asks the app to exit through the same message-window
/// protocol the installer uses (<see cref="InstallerSupport.RequestExit"/>), runs <see cref="AppUpdater.Apply"/>, and starts whichever version is on disk afterwards.
/// The new build is exercised through two probe commands, <c>--update-migrate</c> and <c>--update-health</c>, run from the install folder. None of the arguments
/// is ever supplied by the page: the install folder is the app's own, the package path is fixed inside the updates folder.
/// Exit codes: 0 committed / recovery done; 30 rolled back; 31 aborted (nothing changed); 32 rollback incomplete; 33 nothing staged; 34 another update runs; 2 bad arguments.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UpdateHost
{
    public const int Committed = 0, RolledBack = 30, Aborted = 31, Failed = 32, NotStaged = 33, Busy = 34, BadArguments = 2, MigrationFailed = 21, HealthFailed = 22;

    public static bool IsUpdateCommand(string[] args) => args.Length > 0 && args[0] is "--apply-update" or "--recover-update" or "--rollback-update" or "--update-migrate" or "--update-health";

    public const int NoBackup = 35;

    /// <summary>
    /// Command-line rules (TEST-PLAN UPD08). Each option may appear once and takes exactly one value that is not itself an option. Paths are checked by <see cref="Run"/> with
    /// <see cref="UpdatePathRules"/>.
    /// </summary>
    internal static bool TryParse(string[] args, string? defaultDataRoot, out string? dataRoot, out string? installDir, out string? report, out string? expect, out string? reason, out bool noRestart)
    {
        dataRoot = defaultDataRoot; installDir = report = expect = reason = null; noRestart = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            string name = args[i];
            if (name == "--no-restart") { if (!seen.Add(name)) return false; noRestart = true; continue; }
            if (name is not ("--data-root" or "--install-dir" or "--report" or "--expect-version" or "--reason") || !seen.Add(name) || i + 1 >= args.Length) return false;
            string value = args[++i];
            if (value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal) || value.Any(char.IsControl)) return false;
            switch (name)
            {
                case "--data-root": dataRoot = value; break;
                case "--install-dir": installDir = value; break;
                case "--report": report = value; break;
                case "--expect-version": expect = value; break;
                default: reason = value; break;
            }
        }
        return true;
    }

    private static bool VersionText(string? text) => text is { Length: > 0 and <= 32 } && text.All(c => char.IsAsciiDigit(c) || c == '.');

    public static string LockName(string? dataRoot) => "Su-Su.Update." + MainMode.InstanceName(dataRoot)["Su-Su.Instance".Length..].TrimStart('.');

    /// <summary>The update lock: held by the helper for a whole run and briefly by the app's start-up recovery, so the two never overlap.</summary>
    public static Mutex? TryLock(string? dataRoot)
    {
        var mutex = new Mutex(false, "Local\\" + LockName(dataRoot));
        try
        {
            if (mutex.WaitOne(0)) return mutex;
        }
        catch (AbandonedMutexException) { return mutex; } // a helper that died: the lock is ours now
        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// UPD08: every path is untrusted text and must be a plain absolute local path (no relative form, <c>..</c>, UNC, device or stream syntax, junction or symlink folder). The install
    /// folder must hold a regular susu.exe and not overlap the updates folder. The helper itself must run from the helper folder under this data root's updates folder, i.e. from the
    /// copy the app made, never from a path somebody passed. Nothing in the arguments names a package or an installer: the only source of files is the verified stage folder.
    /// </summary>
    public static int Run(string[] args, Func<string?, IUpdateEnvironment>? environment = null, Func<string, bool>? restart = null, string? processPath = null)
    {
        if (!TryParse(args, Environment.GetEnvironmentVariable(AppPaths.DataRootVariable), out string? dataRoot, out string? installDir, out string? report, out string? expect, out string? reason, out bool noRestart)) return BadArguments;
        if (dataRoot is not null)
        {
            dataRoot = UpdatePathRules.PlainAbsolute(dataRoot);
            if (dataRoot is null) return BadArguments;
        }
        var paths = AppPaths.Resolve(dataRoot, development: false);
        switch (args[0])
        {
            case "--update-migrate": return reason is null && expect is null && installDir is null ? Probe(paths, migrate: true, null) : BadArguments;
            case "--update-health": return expect is null || !VersionText(expect) || reason is not null || installDir is not null ? BadArguments : Probe(paths, migrate: false, expect);
        }
        // Helper modes: apply, recover, roll back.
        string? install = UpdatePathRules.InstallFolder(installDir, paths.Updates);
        if (install is null || expect is not null) return BadArguments;
        string? self = processPath ?? Environment.ProcessPath;
        string helperFolder = Path.GetFullPath(Path.Combine(paths.Updates, "helper"));
        if (self is null || !UpdatePathRules.IsUnder(Path.GetFullPath(self), helperFolder) || UpdatePathRules.IsReparsePoint(Path.GetDirectoryName(Path.GetFullPath(self))!)) return BadArguments;
        if (report is not null)
        {
            report = UpdatePathRules.PlainAbsolute(report);
            if (report is null || !report.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || UpdatePathRules.IsUnder(report, install)
                || Path.GetDirectoryName(report) is not { } reportFolder || !Directory.Exists(reportFolder) || UpdatePathRules.IsReparsePoint(report)) return BadArguments;
        }
        if (args[0] == "--rollback-update" ? reason is not (AppUpdater.UserRollback or AppUpdater.FirstStartFailed) : reason is not null) return BadArguments;
        using var gate = TryLock(dataRoot);
        if (gate is null) return Write(report, Busy, "busy");
        var env = environment?.Invoke(dataRoot) ?? new ProcessUpdateEnvironment(MainMode.InstanceName(dataRoot), dataRoot);
        var updater = new AppUpdater(install, paths.Database, paths.Updates, env);
        int code;
        string state;
        if (args[0] == "--recover-update")
        {
            if (env.StopApp(TimeSpan.FromSeconds(30)) == UpdateExitOutcome.TimedOut) return Write(report, Failed, "recovery-waiting-for-exit"); // the app that started this helper is still ending
            var recovery = updater.Recover();
            code = recovery.Action == "failed" ? Failed : Committed;
            state = "recovered:" + recovery.Action;
        }
        else if (args[0] == "--rollback-update")
        {
            var result = updater.RollBackToPrevious(reason!);
            (code, state) = result.Outcome switch
            {
                "rolled-back" => (RolledBack, "rolled-back:" + result.Error),
                "aborted" => (Aborted, "aborted:" + result.Error),
                "failed" => (Failed, "failed:" + result.Error),
                _ => (NoBackup, "no-backup"),
            };
            // No pair to go back to (or it is damaged): the failing start is not retried forever; the guard is released and the version on disk starts as it is.
            if (code == NoBackup && reason == AppUpdater.FirstStartFailed) updater.Guard.Clear();
            if (code == RolledBack && reason == AppUpdater.FirstStartFailed && !noRestart && report is null)
                Win32Prompt.Error("Su-Su", "The new version could not start twice in a row, so Su-Su went back to the previous version. Your data from before the update was restored; what you saved in the new version is kept in the updates folder.\n新版本连续两次无法启动，Su-Su 已回到上一个版本，并恢复了更新前的数据；新版本中保存的内容保留在 updates 文件夹中。");
        }
        else
        {
            var result = updater.Apply();
            (code, state) = result.Outcome switch
            {
                "committed" => (Committed, "committed"),
                "rolled-back" => (RolledBack, "rolled-back:" + result.Error),
                "aborted" => (Aborted, "aborted:" + result.Error),
                "failed" => (Failed, "failed:" + result.Error),
                _ => (NotStaged, "not-staged"),
            };
        }
        if (!noRestart && code != Failed) (restart ?? StartApp)(Path.Combine(install, "susu.exe")); // the version on disk now, new or restored, is started again
        return Write(report, code, state);
    }

    private static int Write(string? report, int code, string state)
    {
        if (report is not null)
        {
            try { File.WriteAllText(report, $"{{\"code\":{code},\"state\":\"{state}\"}}"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return code;
    }

    private static bool StartApp(string exe)
    {
        try
        {
            if (!File.Exists(exe)) return false;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return false; }
    }

    /// <summary>
    /// The new build's own checks. Migrate opens the database, which applies the schema migrations (with the build's own pre-migration copy). Health needs the database to
    /// open at exactly this build's schema version and this build to be the version the journal expects.
    /// </summary>
    private static int Probe(AppPaths paths, bool migrate, string? expectVersion)
    {
        try
        {
            if (migrate)
            {
                using var db = Database.Open(paths.Database);
                return 0;
            }
            var v = typeof(UpdateHost).Assembly.GetName().Version;
            string mine = v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
            if (mine != expectVersion) return HealthFailed;
            if (File.Exists(paths.Database) && Database.PeekVersion(paths.Database) != Database.SchemaVersion) return HealthFailed;
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Data.Common.DbException or DatabaseVersionException)
        {
            return migrate ? MigrationFailed : HealthFailed;
        }
    }
}

/// <summary>The real environment of the helper: exit coordination through the single-instance window, the probes through the new executable, free disk space.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ProcessUpdateEnvironment(string instanceName, string? dataRoot) : IUpdateEnvironment
{
    public UpdateExitOutcome StopApp(TimeSpan timeout)
        => InstallerSupport.RequestExit(instanceName, timeout, out _) switch
        {
            InstallerSupport.ExitOutcome.NotRunning => UpdateExitOutcome.NotRunning,
            InstallerSupport.ExitOutcome.Exited => UpdateExitOutcome.Exited,
            _ => UpdateExitOutcome.TimedOut,
        };

    public bool Migrate(string installDirectory, string databasePath, out string? detail) => Run(installDirectory, "--update-migrate", null, out detail);

    public bool HealthCheck(string installDirectory, string databasePath, string expectedVersion, out string? detail) => Run(installDirectory, "--update-health", expectedVersion, out detail);

    public long FreeBytes(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            while (!Directory.Exists(full) && Path.GetDirectoryName(full) is { } parent) full = parent;
            return new DriveInfo(Path.GetPathRoot(full)!).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return 0; }
    }

    private bool Run(string installDirectory, string command, string? expectedVersion, out string? detail)
    {
        detail = null;
        string exe = Path.Combine(installDirectory, "susu.exe");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = installDirectory };
        info.ArgumentList.Add(command);
        if (dataRoot is not null) { info.ArgumentList.Add("--data-root"); info.ArgumentList.Add(dataRoot); }
        if (expectedVersion is not null) { info.ArgumentList.Add("--expect-version"); info.ArgumentList.Add(expectedVersion); }
        try
        {
            using var process = Process.Start(info);
            if (process is null) { detail = "start"; return false; }
            if (!process.WaitForExit(TimeSpan.FromMinutes(2))) { try { process.Kill(); } catch (InvalidOperationException) { } detail = "timeout"; return false; }
            detail = "exit " + process.ExitCode;
            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { detail = e.GetType().Name; return false; }
    }
}

/// <summary>Starts the helper from a copy of the running executable placed under the updates folder (the install folder's files are about to be replaced).</summary>
[SupportedOSPlatform("windows")]
internal sealed class ProcessUpdateLauncher(string installDirectory, string updatesFolder, string? dataRoot) : Susu.Plugins.AppUpdate.IUpdateLauncher
{
    public bool LaunchHelper() => Launch("--apply-update");

    public bool LaunchRollback() => Launch("--rollback-update", "--reason", Susu.Plugins.AppUpdate.AppUpdater.UserRollback);

    public bool Launch(string command, params string[] extra)
    {
        try
        {
            string? self = Environment.ProcessPath;
            if (self is null || !File.Exists(self)) return false;
            // The helper needs the executable and the SQLite native library (it copies and restores the database); nothing else from the install folder, which is being replaced.
            string folder = Path.Combine(updatesFolder, "helper", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string copy = Path.Combine(folder, "susu-update.exe");
            File.Copy(self, copy);
            string sqlite = Path.Combine(Path.GetDirectoryName(self)!, "e_sqlite3.dll");
            if (File.Exists(sqlite)) File.Copy(sqlite, Path.Combine(folder, "e_sqlite3.dll"));
            var info = new ProcessStartInfo(copy) { UseShellExecute = false, WorkingDirectory = folder };
            info.ArgumentList.Add(command);
            foreach (string e in extra) info.ArgumentList.Add(e);
            info.ArgumentList.Add("--install-dir");
            info.ArgumentList.Add(installDirectory);
            if (dataRoot is not null) { info.ArgumentList.Add("--data-root"); info.ArgumentList.Add(dataRoot); }
            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }

    /// <summary>Removes helper copies of earlier runs (best effort; a copy that is still running stays).</summary>
    public static void CleanHelpers(string updatesFolder)
    {
        try
        {
            string folder = Path.Combine(updatesFolder, "helper");
            if (!Directory.Exists(folder)) return;
            foreach (string sub in Directory.EnumerateDirectories(folder))
            {
                try { Directory.Delete(sub, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
