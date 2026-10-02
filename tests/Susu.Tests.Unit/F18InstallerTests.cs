using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using Susu.Plugins;
using Susu.Windows;
using Susu.Windows.Shell;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F18.1: staging and manifest, installer script rules, the installer's behaviour modeled against real APIs (registry scratch key, real AppContainer
/// profiles, real message-window exit protocol, real processes and Job objects). The real NSIS compile and a real install run are NOT covered here:
/// makensis is not installed on this machine (see docs/evidence/F18/F18.md).
/// </summary>
[SupportedOSPlatform("windows")]
public class F18InstallerTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "build-installer.ps1"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    // ---------- staging and manifest ----------

    private static string FakePublish(bool complete = true)
    {
        string dir = TestTemp.NewDir("susu-f18-publish");
        foreach (string name in new[] { "susu.exe", "susu_native.dll", "susu_plugin_sandbox.dll", "susu_quickjs.dll", "e_sqlite3.dll", "assets/susu.ico", "ui/index.html", "susu.pdb", "ui/assets/app.js" })
        {
            if (!complete && name == "susu_native.dll") continue;
            string path = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "content of " + name);
        }
        return dir;
    }

    private static string FakeLicenses()
    {
        string dir = TestTemp.NewDir("susu-f18-licenses");
        File.WriteAllText(Path.Combine(dir, "NOTICE.txt"), "notice text");
        File.WriteAllText(Path.Combine(dir, "third-party.json"), "{}");
        return dir;
    }

    private static (int Exit, string Output) RunBuildScript(string publish, string stage, string licenses, Dictionary<string, string>? env = null, params string[] extra)
    {
        var info = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(RepoRoot(), "tools", "build-installer.ps1"),
            "-Version", "1.2.3", "-PublishDir", publish, "-StageDir", stage, "-LicensesDir", licenses, "-StageOnly" }) info.ArgumentList.Add(a);
        foreach (string a in extra) info.ArgumentList.Add(a);
        info.Environment.Remove("SUSU_SIGN_TOOL");
        info.Environment.Remove("SUSU_SIGN_ARGS");
        foreach (var (k, v) in env ?? []) info.Environment[k] = v;
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.True(process.WaitForExit(120_000));
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    [Fact] // UPD07: the staged output holds the app, the native DLLs, LICENSES/NOTICE.txt and a manifest whose hashes match; no symbols; unsigned is marked
    public void Staging_copies_the_app_and_licenses_with_a_verifiable_manifest_and_marks_an_unsigned_build()
    {
        string stage = Path.Combine(TestTemp.NewDir("susu-f18-stage"), "stage");
        var (exit, output) = RunBuildScript(FakePublish(), stage, FakeLicenses());
        Assert.True(exit == 0, output);
        Assert.True(File.Exists(Path.Combine(stage, "susu.exe")));
        Assert.True(File.Exists(Path.Combine(stage, "LICENSES", "NOTICE.txt")));
        Assert.True(File.Exists(Path.Combine(stage, "LICENSES", "third-party.json")));
        Assert.True(File.Exists(Path.Combine(stage, "UNSIGNED.txt")));
        Assert.False(File.Exists(Path.Combine(stage, "susu.pdb")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, "staged-manifest.json")));
        Assert.False(manifest.RootElement.GetProperty("signed").GetBoolean());
        Assert.Equal("1.2.3", manifest.RootElement.GetProperty("version").GetString());
        var listed = new HashSet<string>();
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            string path = file.GetProperty("path").GetString()!;
            listed.Add(path);
            byte[] bytes = File.ReadAllBytes(Path.Combine(stage, path));
            Assert.Equal(bytes.Length, file.GetProperty("size").GetInt64());
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), file.GetProperty("sha256").GetString());
        }
        // every staged file except the manifest itself is listed, and nothing else
        var actual = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(stage, f).Replace('\\', '/')).Where(f => f != "staged-manifest.json").ToHashSet();
        Assert.Equal(actual, listed);
        Assert.Contains("ui/assets/app.js", listed);
    }

    [Fact] // an incomplete publish or missing notices is refused, not staged
    public void Staging_refuses_an_incomplete_publish_and_missing_notices()
    {
        string stage = Path.Combine(TestTemp.NewDir("susu-f18-stage"), "stage");
        var (exit, output) = RunBuildScript(FakePublish(complete: false), stage, FakeLicenses());
        Assert.NotEqual(0, exit);
        Assert.Contains("susu_native.dll", output);
        var (exit2, output2) = RunBuildScript(FakePublish(), stage, TestTemp.NewDir("susu-f18-nolicenses"));
        Assert.NotEqual(0, exit2);
        Assert.Contains("NOTICE.txt", output2);
    }

    [Fact] // the signing hook is parameterized: it signs our binaries (not third-party DLLs), drops the unsigned marker, and a failing hook fails the build
    public void Signing_hook_runs_on_our_binaries_only_and_replaces_the_unsigned_marker()
    {
        string stage = Path.Combine(TestTemp.NewDir("susu-f18-stage"), "stage");
        var env = new Dictionary<string, string> { ["SUSU_SIGN_TOOL"] = "cmd.exe", ["SUSU_SIGN_ARGS"] = "/c|echo signed>{file}.sig" };
        var (exit, output) = RunBuildScript(FakePublish(), stage, FakeLicenses(), env);
        Assert.True(exit == 0, output);
        Assert.True(File.Exists(Path.Combine(stage, "susu.exe.sig")));
        Assert.True(File.Exists(Path.Combine(stage, "susu_native.dll.sig")));
        Assert.False(File.Exists(Path.Combine(stage, "e_sqlite3.dll.sig")));
        Assert.False(File.Exists(Path.Combine(stage, "UNSIGNED.txt")));
        using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, "staged-manifest.json"))))
            Assert.True(manifest.RootElement.GetProperty("signed").GetBoolean());

        var failing = new Dictionary<string, string> { ["SUSU_SIGN_TOOL"] = "cmd.exe", ["SUSU_SIGN_ARGS"] = "/c|exit 7|{file}" };
        Assert.NotEqual(0, RunBuildScript(FakePublish(), stage, FakeLicenses(), failing).Exit);
        Assert.NotEqual(0, RunBuildScript(FakePublish(), stage, FakeLicenses(), null, "-RequireSigned").Exit); // no hook configured
    }

    [Fact] // the NSIS script's rules (static; the real compile is Not executed): per-user, no download, user data only on explicit Yes, exit coordination
    public void Installer_script_states_the_per_user_and_uninstall_rules()
    {
        string nsi = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "installer", "susu.nsi"));
        Assert.Contains("RequestExecutionLevel user", nsi);
        Assert.DoesNotContain("RequestExecutionLevel admin", nsi);
        Assert.DoesNotContain("$PROGRAMFILES", nsi);
        Assert.DoesNotContain("HKLM \"${RUNKEY}\"", nsi);
        Assert.DoesNotContain("inetc", nsi, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NSISdl", nsi, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InetLoad", nsi, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("InstallDir \"$LOCALAPPDATA\\Programs\\Su-Su\"", nsi);
        Assert.Contains("--installer-query", nsi);
        Assert.Contains("--installer-exit", nsi);
        Assert.Contains("--installer-uninstall", nsi);
        Assert.Contains("SetErrorLevel 3", nsi); // WebView2 missing exit
        Assert.Contains("SetErrorLevel 4", nsi); // silent run with tasks in flight
        // the data question defaults to No (also silent), and --delete-user-data is set only on the Yes path
        Assert.Contains("MB_DEFBUTTON2 \"$(TEXT_DELDATA)\" /SD IDNO IDNO keepdata", nsi);
        int ask = nsi.IndexOf("TEXT_DELDATA)\" /SD IDNO", StringComparison.Ordinal);
        int set = nsi.IndexOf("StrCpy $1 \"--delete-user-data\"", StringComparison.Ordinal);
        int label = nsi.IndexOf("keepdata:", StringComparison.Ordinal);
        Assert.True(ask >= 0 && ask < set && set < label);
        Assert.Contains("UNSIGNED", nsi);
        Assert.Contains("Section /o \"Start Su-Su when I sign in\" SecAutostart", nsi); // start-at-login is off unless chosen
    }

    // ---------- start-at-login and uninstall (real registry scratch key, real files) ----------

    [Fact] // UPD07: uninstall keeps user data by default and deletes it only on the explicit choice; a junction is removed as a link, never followed
    public void Uninstall_keeps_user_data_by_default_and_deletes_it_only_when_asked()
    {
        string root = TestTemp.NewDir("susu-f18-data");
        string roaming = Path.Combine(root, "Roaming", "Su-Su"), local = Path.Combine(root, "Local", "Su-Su");
        foreach (string f in new[] { roaming, local }) { Directory.CreateDirectory(f); File.WriteAllText(Path.Combine(f, "keep.txt"), "x"); }
        string prefix = $"Susu.F18Test.{Guid.NewGuid():N}.";
        string runKey = $@"Software\Susu.F18Test.{Guid.NewGuid():N}\Run";
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(runKey)) key.SetValue("Su-Su", "\"C:\\x\\susu.exe\" --autostart");

            var kept = InstallerSupport.Uninstall([roaming, local], deleteUserData: false, prefix, runKeyPath: runKey);
            Assert.True(kept.AutostartRemoved);
            Assert.False(kept.UserDataDeleted);
            Assert.True(File.Exists(Path.Combine(roaming, "keep.txt")) && File.Exists(Path.Combine(local, "keep.txt")));
            using (var key = Registry.CurrentUser.OpenSubKey(runKey)) Assert.Null(key!.GetValue("Su-Su"));

            // a second uninstall with nothing left to remove is not an error
            Assert.False(InstallerSupport.Uninstall([roaming, local], false, prefix, runKeyPath: runKey).AutostartRemoved);

            // explicit choice: data goes; a junction named like the data folder is unlinked and its target survives
            string target = TestTemp.NewDir("susu-f18-target");
            File.WriteAllText(Path.Combine(target, "precious.txt"), "x");
            Directory.Delete(local, true);
            Assert.Equal(0, Cmd($"mklink /J \"{local}\" \"{target}\""));
            var deleted = InstallerSupport.Uninstall([roaming, local], deleteUserData: true, prefix, runKeyPath: runKey);
            Assert.True(deleted.UserDataDeleted);
            Assert.False(Directory.Exists(roaming));
            Assert.False(Directory.Exists(local));
            Assert.True(File.Exists(Path.Combine(target, "precious.txt")));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(Path.GetDirectoryName(runKey)!, throwOnMissingSubKey: false); }
    }

    private static int Cmd(string command)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + command) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    // ---------- X01: AppContainer profile lifecycle ----------

    [Fact] // X01: created on start, gone after a normal close, and a leftover (crash) profile is removed by the uninstall step
    public void Container_profile_is_created_listed_and_removed_by_uninstall()
    {
        string prefix = $"Susu.F18Test.{Guid.NewGuid():N}.";
        string resources = TestTemp.NewDir("susu-f18-res");
        string normal = prefix + "normal", leftover = prefix + "leftover";
        using (var closed = ContainerHost.Open(normal, resources, allowExisting: false))
        {
            Assert.True(closed.Created);
            Assert.Contains(normal, InstallerSupport.ListContainerProfiles(prefix));
            closed.Close(deleteProfile: true);
        }
        Assert.DoesNotContain(normal, InstallerSupport.ListContainerProfiles(prefix));

        var crashed = ContainerHost.Open(leftover, resources, allowExisting: false);
        crashed.Close(deleteProfile: false); // what an unclean exit leaves behind
        Assert.Contains(leftover, InstallerSupport.ListContainerProfiles(prefix));
        Assert.Empty(InstallerSupport.ListContainerProfiles("Susu.F18Other.")); // a different prefix is never touched
        var result = InstallerSupport.Uninstall([], false, prefix, removeAutostart: false);
        Assert.Equal(1, result.ProfilesRemoved);
        Assert.Empty(InstallerSupport.ListContainerProfiles(prefix));
    }

    [Fact] // X01: when the profile cannot be created the sandboxed session fails; it never starts a plain process
    public void A_container_that_cannot_be_created_fails_the_session_and_never_starts_a_plain_process()
    {
        string prefix = $"Susu.F18Test.{Guid.NewGuid():N}.";
        string resources = TestTemp.NewDir("susu-f18-res");
        string name = prefix + "taken";
        using var existing = ContainerHost.Open(name, resources, allowExisting: false);
        try
        {
            // The profile name is already taken (a deterministic creation failure). The executable does not exist: a fallback to CreateProcess without a
            // container would fail with a file error instead; the creation failure (ERROR_ALREADY_EXISTS) must surface.
            string missing = Path.Combine(resources, "no-such-susu.exe");
            var error = Assert.ThrowsAny<Exception>(() => HostSession.Start(new HostSession.Options(missing, resources, "quickjs", ProfileName: name, KeepProfile: false)));
            Assert.Equal(unchecked((int)0x800700B7), error.HResult);
        }
        finally { existing.Close(deleteProfile: true); }
    }

    [Fact] // X01 (static guard): no production code starts a process through the plain, non-container launcher
    public void No_production_code_uses_the_plain_launcher()
    {
        foreach (string folder in new[] { "src/Susu.Plugins", "src/Susu.Host", "src/Susu.Runtime" })
            foreach (string file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), folder), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                Assert.DoesNotContain("StartPlain", File.ReadAllText(file));
            }
    }

    // ---------- running-instance exit coordination (UPD07 single instance and exit) ----------

    [Fact] // the installer asks a running instance how many tasks are in flight, then to exit; it waits until the instance is really gone
    public void Installer_queries_and_exits_a_running_instance_and_waits_for_it()
    {
        string name = $"Su-Su.Test.{Guid.NewGuid():N}";
        Exception? failure = null;
        int exitRaised = 0;
        using var ready = new ManualResetEventSlim();
        var instance = new Thread(() =>
        {
            try
            {
                using var mutex = SingleInstance.TryAcquire(name);
                Assert.NotNull(mutex);
                using var dispatcher = new UiDispatcher(name) { InFlightProvider = () => 3 };
                dispatcher.InstallerExitRequested += () => { Interlocked.Increment(ref exitRaised); dispatcher.Quit(); };
                ready.Set();
                dispatcher.Run();
            }
            catch (Exception e) { failure = e; ready.Set(); }
        });
        instance.SetApartmentState(ApartmentState.STA);
        instance.Start();
        Assert.True(ready.Wait(10_000, TestContext.Current.CancellationToken));
        Assert.Null(failure);

        Assert.Equal(3, InstallerSupport.QueryRunning(name));
        Assert.Equal(0, exitRaised); // a query has no side effect
        var outcome = InstallerSupport.RequestExit(name, TimeSpan.FromSeconds(15), out int inFlight);
        Assert.Equal(InstallerSupport.ExitOutcome.Exited, outcome);
        Assert.Equal(3, inFlight);
        Assert.True(instance.Join(10_000));
        Assert.Equal(1, exitRaised);
        Assert.Null(InstallerSupport.QueryRunning(name));
        Assert.Equal(InstallerSupport.ExitOutcome.NotRunning, InstallerSupport.RequestExit(name, TimeSpan.FromSeconds(1), out _));
    }

    [Fact] // an instance that holds the mutex but never exits is reported as timed out; it is not killed
    public void A_running_instance_that_does_not_exit_is_reported_not_killed()
    {
        string name = $"Su-Su.Test.{Guid.NewGuid():N}";
        using var ready = new ManualResetEventSlim();
        using var stop = new ManualResetEventSlim();
        var instance = new Thread(() =>
        {
            using var mutex = SingleInstance.TryAcquire(name);
            using var dispatcher = new UiDispatcher(name); // nobody handles InstallerExitRequested
            ready.Set();
            long end = Environment.TickCount64 + 30_000;
            while (!stop.IsSet && Environment.TickCount64 < end) { PumpOnce(); Thread.Sleep(5); }
        });
        instance.SetApartmentState(ApartmentState.STA);
        instance.Start();
        Assert.True(ready.Wait(10_000, TestContext.Current.CancellationToken));
        try
        {
            Assert.Equal(InstallerSupport.ExitOutcome.TimedOut, InstallerSupport.RequestExit(name, TimeSpan.FromMilliseconds(600), out _));
            Assert.True(instance.IsAlive);
        }
        finally { stop.Set(); instance.Join(10_000); }
    }

    private static void PumpOnce()
    {
        while (PeekMessage(out var message, 0, 0, 0, 1)) { TranslateMessage(message); DispatchMessage(message); }
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Msg { public nint Hwnd; public uint Message; public nint WParam, LParam; public uint Time; public int X, Y, Private; }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(out Msg message, nint hwnd, uint min, uint max, uint remove);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool TranslateMessage(in Msg message);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(in Msg message);

    // ---------- the real published susu.exe: installer commands end to end ----------

    private static (int Exit, string Report) RunHost(string exe, params string[] args)
    {
        string report = Path.Combine(TestTemp.NewDir("susu-f18-report"), "r.json");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in args) info.ArgumentList.Add(a);
        info.ArgumentList.Add("--report");
        info.ArgumentList.Add(report);
        using var process = Process.Start(info)!;
        Assert.True(process.WaitForExit(60_000));
        return (process.ExitCode, File.Exists(report) ? File.ReadAllText(report) : "");
    }

    [Fact] // UPD07: the published exe's query/exit/uninstall commands; a real running instance is asked to exit and is really gone
    public async Task Published_exe_installer_commands_work_against_a_real_running_instance()
    {
        string? output = PluginHostIntegrationTests.FindHostBuildOutput();
        if (output is null) Assert.Skip("The NativeAOT publish output of susu.exe is not present; publish the host first (see PluginHostIntegrationTests).");
        string exe = Path.Combine(output, "susu.exe");
        string root = TestTemp.NewDir("susu-f18-root");

        Assert.Equal((0, "{\"state\":\"not-running\",\"inFlight\":0}"), RunHost(exe, "--installer-query", "--data-root", root));
        Assert.Equal((0, "{\"state\":\"not-running\",\"inFlight\":0}"), RunHost(exe, "--installer-exit", "--data-root", root));

        string prefix = $"Susu.F18Test.{Guid.NewGuid():N}.";
        string resources = TestTemp.NewDir("susu-f18-res");
        ContainerHost.Open(prefix + "x", resources, allowExisting: false).Close(deleteProfile: false); // a leftover profile
        var paths = Susu.Storage.AppPaths.UnderRoot(root).EnsureCreated();
        File.WriteAllText(Path.Combine(paths.Roaming, "settings.yaml"), "x");
        var kept = RunHost(exe, "--installer-uninstall", "--data-root", root, "--profile-prefix", prefix);
        Assert.Equal(0, kept.Exit);
        Assert.Contains("\"profilesRemoved\":1", kept.Report);
        Assert.Contains("\"userDataDeleted\":false", kept.Report);
        Assert.True(File.Exists(Path.Combine(paths.Roaming, "settings.yaml")));
        var wiped = RunHost(exe, "--installer-uninstall", "--data-root", root, "--profile-prefix", prefix, "--delete-user-data");
        Assert.Equal(0, wiped.Exit);
        Assert.Contains("\"userDataDeleted\":true", wiped.Report);
        Assert.False(Directory.Exists(paths.Roaming));
        Assert.Empty(InstallerSupport.ListContainerProfiles(prefix));

        // a real instance, with its own data root
        string running = TestTemp.NewDir("susu-f18-running");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--data-root");
        start.ArgumentList.Add(running);
        using var app = Process.Start(start)!;
        try
        {
            (int Exit, string Report) query = (0, "");
            for (int i = 0; i < 100 && query.Exit < 100 && !app.HasExited; i++)
            {
                query = RunHost(exe, "--installer-query", "--data-root", running);
                if (query.Exit < 100) await Task.Delay(200, TestContext.Current.CancellationToken);
            }
            if (app.HasExited && query.Exit < 100) Assert.Skip("The app exited on its own (no usable desktop or WebView2 runtime here), so there is no running instance to coordinate with."); // no usable desktop or WebView2 runtime here: the app exits on its own, nothing to coordinate
            Assert.Equal(100, query.Exit); // running, nothing in flight
            Assert.Contains("\"state\":\"running\"", query.Report);
            // uninstall refuses while the instance runs
            Assert.Equal(13, RunHost(exe, "--installer-uninstall", "--data-root", running).Exit);
            var exit = RunHost(exe, "--installer-exit", "--data-root", running, "--timeout-ms", "20000");
            Assert.Equal(0, exit.Exit);
            Assert.Contains("\"state\":\"exited\"", exit.Report);
            Assert.True(app.WaitForExit(20_000), "the process must be gone once the installer's exit request returns");
        }
        finally { if (!app.HasExited) app.Kill(true); }
    }

    // ---------- X06: the plugin-host process tree dies with its parent ----------

    private const string ParentRole = "SUSU_F18_X06_PARENT";

    [Fact] // X06: killing the parent abnormally leaves no plugin-host child (Job object KILL_ON_JOB_CLOSE), with a real parent process and a real sandboxed child
    public async Task Killing_the_parent_process_leaves_no_orphan_plugin_host()
    {
        string? staged = PluginHostIntegrationTests.StageHost();
        if (staged is null) Assert.Skip("Needs the NativeAOT publish output (see PluginHostIntegrationTests).");
        string pidFile = Path.Combine(TestTemp.NewDir("susu-f18-x06"), "child.pid");
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-method");
        info.ArgumentList.Add("*X06_parent_role_helper");
        info.Environment[ParentRole] = pidFile + "|" + staged;
        // the test executable is an apphost; give it the .NET installation this test run uses (the repository-local one is not registered machine-wide)
        info.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        using var parent = Process.Start(info)!;
        _ = parent.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        _ = parent.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!File.Exists(pidFile) && DateTime.UtcNow < deadline && !parent.HasExited) await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(pidFile), "the helper parent did not start a sandboxed child");
            await Task.Delay(300, TestContext.Current.CancellationToken);
            int childPid = int.Parse(File.ReadAllText(pidFile));
            using var child = Process.GetProcessById(childPid);
            Assert.False(child.HasExited);
            parent.Kill(entireProcessTree: false); // abnormal termination of the parent: no Dispose, no Shutdown
            Assert.True(parent.WaitForExit(10_000));
            Assert.True(child.WaitForExit(10_000), "the sandboxed child must be terminated when its parent dies");
        }
        finally { if (!parent.HasExited) parent.Kill(true); }
    }

    [Fact] // helper for the test above; does nothing in a normal run
    public void X06_parent_role_helper()
    {
        string? role = Environment.GetEnvironmentVariable(ParentRole);
        if (role is null) return; // a child-process role launched by the X06 parent test; run directly it has nothing to do
        string[] parts = role.Split('|');
        string host = Path.Combine(parts[1], "susu.exe");
        var session = HostSession.Start(new HostSession.Options(host, parts[1], "quickjs", KeepProfile: false));
        File.WriteAllText(parts[0], session.ChildPid.ToString());
        Thread.Sleep(TimeSpan.FromMinutes(2)); // killed by the driver long before
        session.Dispose();
    }

    [Fact] // X06: ending the session ends the child, and a budget far below what the runtime needs fails the start visibly with no child left behind
    public void Closing_the_session_ends_the_child_and_a_tiny_memory_budget_fails_visibly()
    {
        string? staged = PluginHostIntegrationTests.StageHost();
        if (staged is null) Assert.Skip("Needs the NativeAOT publish output (see PluginHostIntegrationTests).");
        string host = Path.Combine(staged, "susu.exe");
        var session = HostSession.Start(new HostSession.Options(host, staged, "quickjs", KeepProfile: false));
        using var child = Process.GetProcessById(session.ChildPid);
        Assert.False(child.HasExited);
        session.Dispose();
        Assert.True(child.WaitForExit(10_000));

        var error = Record.Exception(() => { using var tiny = HostSession.Start(new HostSession.Options(host, staged, "quickjs", MemoryLimit: 4UL << 20, KeepProfile: false)); });
        Assert.NotNull(error);
    }

    [Fact] // UPD07/UPD08: the uninstall command refuses a tampered data root and, with a valid one, deletes only the app's own Su-Su folders
    public void Published_exe_uninstall_refuses_tampered_data_roots_and_deletes_only_its_own_folders()
    {
        string? output = PluginHostIntegrationTests.FindHostBuildOutput();
        if (output is null) Assert.Skip("The NativeAOT publish output of susu.exe is not present; publish the host first (see PluginHostIntegrationTests).");
        string exe = Path.Combine(output, "susu.exe");
        string root = TestTemp.NewDir("susu-f18-uninstall-root");
        string own = Path.Combine(root, "Roaming", "Su-Su"), local = Path.Combine(root, "Local", "Su-Su");
        Directory.CreateDirectory(own);
        Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(own, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(root, "Roaming", "other-app.txt"), "keep");
        string prefix = $"Susu.F18Test.{Guid.NewGuid():N}.";
        foreach (string bad in new[] { "relative", @"..\x", root + @"\..\" + Path.GetFileName(root), @"\\localhost\c$\x", @"\\?\" + root, root + ":stream", "C:\\x\" --delete-user-data" })
        {
            Assert.Equal(2, RunHost(exe, "--installer-uninstall", "--delete-user-data", "--profile-prefix", prefix, "--data-root", bad).Exit);
            Assert.True(File.Exists(Path.Combine(own, "settings.json")), bad);
        }
        Assert.Equal(0, RunHost(exe, "--installer-uninstall", "--profile-prefix", prefix, "--data-root", root).Exit); // default: keep
        Assert.True(File.Exists(Path.Combine(own, "settings.json")));
        Assert.Equal(0, RunHost(exe, "--installer-uninstall", "--delete-user-data", "--profile-prefix", prefix, "--data-root", root).Exit);
        Assert.False(Directory.Exists(own));
        Assert.False(Directory.Exists(local));
        Assert.True(File.Exists(Path.Combine(root, "Roaming", "other-app.txt"))); // nothing outside Su-Su's own folders
    }
}
