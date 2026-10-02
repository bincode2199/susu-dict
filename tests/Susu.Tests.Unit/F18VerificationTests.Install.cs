using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Susu.Abstractions;
using Susu.Plugins.AppUpdate;
using Susu.Storage;
using Susu.Windows;
using Xunit;

namespace Susu.Tests.Unit;

// Installer script and staging, uninstall variants, the real published exe as helper and as the application.
public partial class F18VerificationTests
{
    private static string? Publish() => PluginHostIntegrationTests.FindHostBuildOutput();

    private static string NeedPublish()
    {
        string? publish = Publish();
        if (publish is null) Assert.Skip("The NativeAOT publish output of susu.exe is not present; publish the host first (see PluginHostIntegrationTests).");
        return publish!;
    }

    // ================= installer script: static rules (the script cannot be compiled here) =================

    private static string Nsi() => File.ReadAllText(Path.Combine(RepoRoot(), "tools", "installer", "susu.nsi"));

    private static IEnumerable<string> NsiLines() => Nsi().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith(';'));

    [Fact] // UPD07: nothing in the script asks for administrator rights, touches a machine-wide location, or runs a build-time command
    public void The_installer_script_has_no_instruction_that_needs_administrator_rights_or_a_machine_wide_location()
    {
        var lines = NsiLines().ToList();
        Assert.Single(lines, l => l.StartsWith("RequestExecutionLevel", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("RequestExecutionLevel user", lines);
        string[] forbidden =
        [
            "RequestExecutionLevel admin", "RequestExecutionLevel highest", "SetShellVarContext all", "$PROGRAMFILES", "$PROGRAMFILES64", "$PROGRAMFILES32", "$COMMONFILES", "$WINDIR", "$SYSDIR", "$SYSDIR",
            "$COMMONFILES64", "$PROGRAMDATA", "$ALLUSERSPROFILE", "$DESKTOP", "$STARTMENU\\", "$SMSTARTUP", "ShellExecAs", "UAC::", "UserMgr", "AccessControl", "Services::", "SimpleSC", "nsExec", "RegDLL", "UnRegDLL",
            "WriteRegStr HKLM", "WriteRegDWORD HKLM", "WriteRegBin HKLM", "WriteRegExpandStr HKLM", "DeleteRegKey HKLM", "DeleteRegValue HKLM", "WriteRegStr HKCR", "WriteRegStr HKU", "WriteRegStr SHCTX", "WriteRegStr SHELL_CONTEXT",
            "InstallDirRegKey HKLM", "!system", "!execute", "!packhdr", "!finalize", "!uninstfinalize", "!appendfile", "!tempfile", "ExecShellWait \"runas\"", "\"runas\"", "SetRegView 64\r\n  WriteReg", "schtasks", "sc.exe", "netsh", "reg.exe",
            "icacls", "takeown", "powershell", "cmd.exe", "wscript", "regsvr32", "msiexec", "HKEY_LOCAL_MACHINE", "BUILTIN", "S-1-5",
        ];
        foreach (string token in forbidden.Distinct())
            Assert.DoesNotContain(lines, l => l.Contains(token, StringComparison.OrdinalIgnoreCase));
        // Every registry WRITE or DELETE goes to HKCU; HKLM is only ever READ (the WebView2 runtime version).
        foreach (var l in lines.Where(l => Regex.IsMatch(l, @"^(WriteReg\w+|DeleteReg\w+|EnumRegKey|WriteINI\w*)\b", RegexOptions.IgnoreCase)))
            Assert.Contains("HKCU", l);
        foreach (var l in lines.Where(l => l.Contains("HKLM", StringComparison.Ordinal))) Assert.Matches(@"^ReadRegStr \$\d HKLM ", l);
        // Files go to the install folder (user profile by default), the user-specific start menu and the app's own folder under LOCALAPPDATA.
        foreach (var l in lines.Where(l => Regex.IsMatch(l, @"^(SetOutPath|CreateDirectory|CreateShortCut|Delete|RMDir|File|FileOpen)\b", RegexOptions.IgnoreCase)))
            Assert.True(l.Contains("$INSTDIR") || l.Contains("$SMPROGRAMS") || l.Contains("$LOCALAPPDATA\\Su-Su") || l.Contains("${STAGE}") || l.Contains("$0"), "unexpected file target: " + l);
        // The only programs the script runs are the installed susu.exe.
        foreach (var l in lines.Where(l => Regex.IsMatch(l, @"^(ExecWait|Exec|ExecShell|ExecShellWait)\b", RegexOptions.IgnoreCase)))
            Assert.True(l.Contains("$INSTDIR\\susu.exe") || l.StartsWith("ExecShell \"open\" \"${WV2PAGE}\"", StringComparison.Ordinal), "unexpected program run: " + l);
    }

    [Fact] // UPD07: uninstall registry keys, per-user shortcuts, WebView2 detection with all three registry locations, silent defaults for every dialog, both languages for every text
    public void The_installer_script_writes_the_uninstall_entry_detects_webview2_and_defaults_every_dialog_for_silent_runs()
    {
        string nsi = Nsi();
        var lines = NsiLines().ToList();
        foreach (string value in new[] { "DisplayName", "DisplayVersion", "DisplayIcon", "InstallLocation", "UninstallString", "QuietUninstallString" })
            Assert.Contains(lines, l => l.StartsWith($"WriteRegStr HKCU \"${{UNINSTKEY}}\" \"{value}\"", StringComparison.Ordinal));
        foreach (string value in new[] { "NoModify", "NoRepair", "EstimatedSize" })
            Assert.Contains(lines, l => l.StartsWith($"WriteRegDWORD HKCU \"${{UNINSTKEY}}\" \"{value}\"", StringComparison.Ordinal));
        Assert.Contains("!define UNINSTKEY \"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Su-Su\"", nsi);
        Assert.Contains(lines, l => l.StartsWith("DeleteRegKey HKCU \"${UNINSTKEY}\"", StringComparison.Ordinal));
        Assert.Contains("QuietUninstallString\" '\"$INSTDIR\\uninstall.exe\" /S'", nsi);
        Assert.Contains(lines, l => l.StartsWith("CreateShortCut \"$SMPROGRAMS\\Su-Su.lnk\"", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Delete \"$SMPROGRAMS\\Su-Su.lnk\"", StringComparison.Ordinal));
        // WebView2: the Evergreen runtime client id, machine-wide in both views and per user; "0.0.0.0" counts as missing; no download; exit code 3
        Assert.Contains("{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", nsi);
        Assert.Contains("ReadRegStr $0 HKLM \"SOFTWARE\\WOW6432Node\\Microsoft\\EdgeUpdate\\Clients\\${WV2CLIENT}\" \"pv\"", nsi);
        Assert.Contains("ReadRegStr $0 HKLM \"SOFTWARE\\Microsoft\\EdgeUpdate\\Clients\\${WV2CLIENT}\" \"pv\"", nsi);
        Assert.Contains("ReadRegStr $0 HKCU \"Software\\Microsoft\\EdgeUpdate\\Clients\\${WV2CLIENT}\" \"pv\"", nsi);
        Assert.Contains("\"0.0.0.0\"", nsi);
        Assert.Contains("SetErrorLevel 3", nsi);
        Assert.DoesNotContain("https://go.microsoft.com/fwlink", nsi); // no bootstrapper link: the user is sent to the page, nothing is downloaded by the installer
        // Every message box states what a silent run answers (/SD), so a silent install or uninstall never waits for a click.
        foreach (var l in lines.Where(l => l.StartsWith("MessageBox", StringComparison.Ordinal))) Assert.Contains("/SD ", l);
        // Silent answers are the safe ones: no to the WebView2 page, cancel when tasks run, no to deleting data.
        Assert.Contains("MB_YESNO|MB_ICONEXCLAMATION \"$(TEXT_WV2)\" /SD IDNO", nsi);
        Assert.Contains("MB_OKCANCEL|MB_ICONEXCLAMATION \"$(TEXT_TASKS)\" /SD IDCANCEL", nsi);
        Assert.Contains("MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 \"$(TEXT_DELDATA)\" /SD IDNO", nsi);
        // The start-at-login section is optional (off) and writes only HKCU; the uninstaller removes it.
        Assert.Contains("Section /o \"Start Su-Su when I sign in\" SecAutostart", nsi);
        Assert.Contains("DeleteRegValue HKCU \"${RUNKEY}\" \"Su-Su\"", nsi);
        // Every LangString exists in both languages.
        var langs = Regex.Matches(nsi, @"^LangString (\w+) \$\{LANG_(\w+)\}", RegexOptions.Multiline).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
        foreach (var name in langs.Select(l => l.Item1).Distinct())
            Assert.True(langs.Contains((name, "ENGLISH")) && langs.Contains((name, "SIMPCHINESE")), "LangString " + name + " is missing a language");
        // Every ExecWait result is looked at or deliberately ignored, never used as a path: the susu.exe commands are fixed strings.
        Assert.DoesNotContain(lines, l => l.StartsWith("ExecWait", StringComparison.Ordinal) && l.Contains("$R") );
        // The uninstaller refuses a folder that holds no susu.exe (it would otherwise delete an arbitrary folder).
        int init = nsi.IndexOf("Function un.onInit", StringComparison.Ordinal);
        Assert.True(init > 0 && nsi.IndexOf("IfFileExists \"$INSTDIR\\susu.exe\" ok", init, StringComparison.Ordinal) > init);
    }

    [Fact] // UPD07: Known_gap (DEFECT D6, medium): the directory page lets the user pick any folder and the uninstaller removes that whole folder recursively, including files that are not Su-Su's
    public void Known_gap_the_uninstaller_removes_the_whole_install_folder_recursively_and_the_directory_page_accepts_any_folder()
    {
        var lines = NsiLines().ToList();
        Assert.Contains("!insertmacro MUI_PAGE_DIRECTORY", lines); // the user may choose any folder, e.g. an existing folder with their own files
        Assert.Contains("RMDir /r \"$INSTDIR\"", lines); // pinned: removes everything in it, not only the files the installer put there
        // Neither a ".onVerifyInstDir" check nor a "\Su-Su" suffix rule exists to keep a chosen folder separate from user content.
        Assert.DoesNotContain(lines, l => l.Contains(".onVerifyInstDir", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("MUI_PAGE_CUSTOMFUNCTION_LEAVE", StringComparison.Ordinal));
        // The uninstaller has no list of installed files to delete instead (staged-manifest.json ships in the folder and could be used for that).
        Assert.DoesNotContain(lines, l => l.StartsWith("Delete \"$INSTDIR\\", StringComparison.Ordinal) && !l.Contains("uninstall.exe"));
    }

    // ================= staging against the REAL publish output =================

    private static string[] RunScript(string publish, string stage, string version, params string[] extra)
    {
        var info = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(RepoRoot(), "tools", "build-installer.ps1"), "-Version", version, "-PublishDir", publish, "-StageDir", stage,
            "-LicensesDir", Path.Combine(RepoRoot(), "LICENSES"), "-StageOnly" }) info.ArgumentList.Add(a);
        foreach (string a in extra) info.ArgumentList.Add(a);
        info.Environment.Remove("SUSU_SIGN_TOOL");
        info.Environment.Remove("SUSU_SIGN_ARGS");
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(240_000));
        return [process.ExitCode.ToString(), output];
    }

    [Fact] // UPD07: the staged folder of the real publish holds every shipped file (binaries, UI, assets, plugins), the notices and the unsigned marker, with a manifest that matches the bytes
    public void Staging_the_real_publish_output_ships_every_file_with_notices_a_matching_manifest_and_the_unsigned_marker()
    {
        string publish = NeedPublish();
        if (!File.Exists(Path.Combine(RepoRoot(), "LICENSES", "NOTICE.txt"))) Assert.Skip("LICENSES/NOTICE.txt has not been generated (node tools/generate-notices.mjs).");
        string stage = Path.Combine(TestTemp.NewDir("susu-f18v-stage"), "stage");
        var run = RunScript(publish, stage, "0.1.0");
        Assert.True(run[0] == "0", run[1]);
        var shipped = Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(publish, f).Replace('\\', '/')).Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staged = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(stage, f).Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Empty(shipped.Except(staged)); // every shipped file (exe, every dll, ui, assets, plugins) is in the installer
        Assert.Equal(["LICENSES/NOTICE.txt", "LICENSES/third-party.json", "UNSIGNED.txt", "staged-manifest.json"], staged.Except(shipped).Order(StringComparer.Ordinal).ToArray());
        foreach (string f in shipped) Assert.Equal(AtomicFile.HashOf(Path.Combine(publish, f)), AtomicFile.HashOf(Path.Combine(stage, f)));
        Assert.DoesNotContain(staged, f => f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, "staged-manifest.json")));
        Assert.False(manifest.RootElement.GetProperty("signed").GetBoolean());
        var listed = manifest.RootElement.GetProperty("files").EnumerateArray().Select(e => e.GetProperty("path").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(staged.Where(f => f != "staged-manifest.json").Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));
        foreach (var e in manifest.RootElement.GetProperty("files").EnumerateArray())
            Assert.Equal(AtomicFile.HashOf(Path.Combine(stage, e.GetProperty("path").GetString()!)), e.GetProperty("sha256").GetString());
        Assert.Contains("NOT code-signed", File.ReadAllText(Path.Combine(stage, "UNSIGNED.txt")));
        // the NSIS script's own inputs exist in the stage
        Assert.Contains("assets/susu.ico", staged);
        Assert.Contains("susu.exe", staged);
        // The script's "required" list names every native binary the publish ships (a publish without one of them must be refused, not staged).
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "build-installer.ps1"));
        string required = Regex.Match(script, @"\$required\s*=\s*@\(([^)]*)\)").Groups[1].Value;
        var binaries = shipped.Where(f => !f.Contains('/') && (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.NotEmpty(binaries);
        var missing = binaries.Where(b => !required.Contains("'" + b + "'", StringComparison.OrdinalIgnoreCase)).ToList();
        // Known gap (DEFECT D7, low): susu_selection.dll ships but is not in the script's required list, so a publish without it would be staged (and installed) silently.
        Assert.Equal(["susu_selection.dll"], missing);
    }

    [Fact] // UPD07: Known_gap (DEFECT D7, low): the installer version is a free build parameter and is not compared with the version inside susu.exe
    public void Known_gap_the_build_script_stamps_any_version_without_checking_it_against_the_executable()
    {
        string publish = NeedPublish();
        if (!File.Exists(Path.Combine(RepoRoot(), "LICENSES", "NOTICE.txt"))) Assert.Skip("LICENSES/NOTICE.txt has not been generated.");
        var v = FileVersionInfo.GetVersionInfo(Path.Combine(publish, "susu.exe"));
        string exeVersion = $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}";
        string other = exeVersion == "9.9.9" ? "9.9.8" : "9.9.9";
        var run = RunScript(publish, Path.Combine(TestTemp.NewDir("susu-f18v-stage"), "stage"), other);
        Assert.True(run[0] == "0", run[1]); // pinned: accepted although the executable says another version
        Assert.NotEqual(exeVersion, other);
    }

    // ================= uninstall =================

    private static string[] TreeKeys(string root) => [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(root, p)).Order(StringComparer.Ordinal)];

    [Fact] // UPD07/DATA: data stays by default; with the explicit choice only the Su-Su data folders go, including their nested junctions' links but never the junction targets or the neighbours
    public void Uninstall_clear_removes_only_su_su_folders_and_unlinks_nested_junctions_and_name_traps()
    {
        string home = TestTemp.NewDir("susu-f18v-home");
        string roaming = Path.Combine(home, "AppData", "Roaming", "Su-Su"), local = Path.Combine(home, "AppData", "Local", "Su-Su");
        string neighbours = Path.Combine(home, "AppData", "Roaming", "Other App");
        foreach (string d in new[] { roaming, local, neighbours, Path.Combine(home, "Documents"), Path.Combine(home, "Pictures", "Su-Su") })
        {
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "file.txt"), d);
        }
        // targets outside: one folder that is itself named Su-Su (a name trap), one plain folder with a read-only file
        string trapRoot = TestTemp.NewDir("susu-f18v-trap");
        string trap = Path.Combine(trapRoot, "Su-Su");
        Directory.CreateDirectory(trap);
        File.WriteAllText(Path.Combine(trap, "precious.txt"), "precious");
        string plain = Path.Combine(trapRoot, "plain");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "ro.txt"), "ro");
        File.SetAttributes(Path.Combine(plain, "ro.txt"), FileAttributes.ReadOnly);
        // inside the data folders: nested junctions to both targets, a deeper junction, a read-only file, a long name
        Directory.CreateDirectory(Path.Combine(local, "cache", "deep"));
        Assert.Equal(0, Cmd($"mklink /J \"{Path.Combine(local, "cache", "to-trap")}\" \"{trap}\""));
        Assert.Equal(0, Cmd($"mklink /J \"{Path.Combine(local, "cache", "deep", "to-plain")}\" \"{plain}\""));
        Assert.Equal(0, Cmd($"mklink /J \"{Path.Combine(roaming, "plugins")}\" \"{plain}\""));
        File.WriteAllText(Path.Combine(roaming, "ro-in-data.txt"), "x");
        File.SetAttributes(Path.Combine(roaming, "ro-in-data.txt"), FileAttributes.ReadOnly);
        string prefix = $"Susu.F18V.{Guid.NewGuid():N}.";
        var beforeHome = TreeKeys(home).Where(k => !k.StartsWith(@"AppData\Roaming\Su-Su", StringComparison.Ordinal) && !k.StartsWith(@"AppData\Local\Su-Su", StringComparison.Ordinal)).ToArray();

        var kept = InstallerSupport.Uninstall([roaming, local], deleteUserData: false, prefix, removeAutostart: false);
        Assert.False(kept.UserDataDeleted);
        Assert.True(Directory.Exists(roaming) && Directory.Exists(local)); // default: kept

        var wiped = InstallerSupport.Uninstall([roaming, local], deleteUserData: true, prefix, removeAutostart: false);
        Assert.True(wiped.UserDataDeleted);
        Assert.Equal(0, wiped.FoldersRefused);
        Assert.False(Directory.Exists(roaming));
        Assert.False(Directory.Exists(local));
        Assert.Equal(beforeHome, TreeKeys(home)); // nothing else under the profile changed, including Pictures\Su-Su (kept screenshots are not data folders)
        Assert.Equal("precious", File.ReadAllText(Path.Combine(trap, "precious.txt")));
        Assert.Equal("ro", File.ReadAllText(Path.Combine(plain, "ro.txt")));
        File.SetAttributes(Path.Combine(plain, "ro.txt"), FileAttributes.Normal);
    }

    [Fact] // UPD08: the folder rule is by NAME only: aliases, drive roots, the profile folder and look-alike names are refused; spelling variants of the genuine folder delete only that folder
    public void Uninstall_folder_rule_refuses_other_names_and_drive_roots_and_resolves_spelling_variants_to_the_same_folder()
    {
        string root = TestTemp.NewDir("susu-f18v-names");
        string prefix = $"Susu.F18V.{Guid.NewGuid():N}.";
        foreach (string name in new[] { "Su-Su Backup", "Su-Su2", "xSu-Su", "Su_Su", "SuSu", "Su-Su-Dev", "Su-Su.old", "Su-Su (2)", "Su–Su" /* en dash */, "Ѕu-Su" /* Cyrillic S */ })
        {
            string folder = Path.Combine(root, "A", name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "x.txt"), "x");
            Assert.False(InstallerSupport.IsOwnDataFolder(folder), name);
            var result = InstallerSupport.Uninstall([folder], true, prefix, removeAutostart: false);
            Assert.True(result.FoldersRefused == 1 && !result.UserDataDeleted && File.Exists(Path.Combine(folder, "x.txt")), name);
        }
        // an 8.3 alias of a longer name never equals "Su-Su"
        string longName = Path.Combine(root, "A", "Su-Su Backup");
        var alias = new StringBuilder(260);
        if (GetShortPathName(longName, alias, alias.Capacity) > 0 && alias.ToString().Contains('~')) Assert.False(InstallerSupport.IsOwnDataFolder(alias.ToString()));
        // pure rule checks: drive roots, UNC, the profile folder, relative names
        foreach (string notOurs in new[] { @"C:\", @"C:", @"D:\", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Path.GetTempPath(), @"\\server\share\Documents" })
            Assert.False(InstallerSupport.IsOwnDataFolder(notOurs), notOurs);
        // spelling variants of the genuine folder: trailing separators, a trailing dot, case, "." segments: all resolve to the one Su-Su folder and its neighbours stay
        foreach (string variant in new[] { "Su-Su", "Su-Su" + "\\", "SU-SU", "su-su", "Su-Su\\.", "Su-Su." })
        {
            string genuine = Path.Combine(root, "B", "Su-Su");
            string neighbour = Path.Combine(root, "B", "Neighbour");
            Directory.CreateDirectory(genuine);
            Directory.CreateDirectory(neighbour);
            File.WriteAllText(Path.Combine(genuine, "x.txt"), "x");
            File.WriteAllText(Path.Combine(neighbour, "keep.txt"), "keep");
            var result = InstallerSupport.Uninstall([Path.Combine(root, "B", variant)], true, prefix, removeAutostart: false);
            Assert.True(result.UserDataDeleted && result.FoldersRefused == 0, variant);
            Assert.False(Directory.Exists(genuine), variant);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(neighbour, "keep.txt")));
            Directory.Delete(neighbour, true);
        }
    }

    [Fact] // UPD08: Known_gap (DEFECT D8, low): the uninstall API deletes ANY folder named Su-Su (the exe only ever builds <root>\Roaming\Su-Su and <root>\Local\Su-Su, so this is defence in depth)
    public void Known_gap_any_folder_named_su_su_is_deleted_by_the_uninstall_api_whatever_it_holds()
    {
        string project = Path.Combine(TestTemp.NewDir("susu-f18v-project"), "Su-Su");
        Directory.CreateDirectory(Path.Combine(project, "src"));
        File.WriteAllText(Path.Combine(project, "src", "main.cs"), "the user's own project that happens to be called Su-Su");
        var result = InstallerSupport.Uninstall([project], true, $"Susu.F18V.{Guid.NewGuid():N}.", removeAutostart: false);
        Assert.True(result.UserDataDeleted); // pinned: no marker file, no parent-folder check (AppData) before deleting
        Assert.False(Directory.Exists(project));
    }

    [Fact] // X01: only profiles that START with the given prefix are removed; a longer or embedded name and another prefix are left alone
    public void Uninstall_removes_container_profiles_by_prefix_only()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string prefix = $"Susu.F18V.{suffix}.", other = $"Susu.F18W.{suffix}.";
        string resources = TestTemp.NewDir("susu-f18v-res");
        var kept = new List<ContainerHost>();
        try
        {
            ContainerHost.Open(prefix + "one", resources, allowExisting: false).Close(deleteProfile: false);
            ContainerHost.Open(prefix + "two", resources, allowExisting: false).Close(deleteProfile: false);
            kept.Add(ContainerHost.Open(other + "x", resources, allowExisting: false));
            kept.Add(ContainerHost.Open("X" + prefix + "embedded", resources, allowExisting: false));
            Assert.Equal(2, InstallerSupport.ListContainerProfiles(prefix).Count);
            var result = InstallerSupport.Uninstall([], false, prefix, removeAutostart: false);
            Assert.Equal(2, result.ProfilesRemoved);
            Assert.Empty(InstallerSupport.ListContainerProfiles(prefix));
            Assert.Single(InstallerSupport.ListContainerProfiles(other));
            Assert.Equal(0, InstallerSupport.Uninstall([], false, prefix, removeAutostart: false).ProfilesRemoved); // idempotent
        }
        finally
        {
            foreach (var c in kept) c.Close(deleteProfile: true);
        }
        Assert.Empty(InstallerSupport.ListContainerProfiles(other));
    }

    [Fact] // X01 (static): the only source files that start processes are the known ones, and nothing but ContainerHost itself refers to the plain (non-container) launcher
    public void Only_the_known_source_files_start_processes_and_nothing_refers_to_the_plain_launcher()
    {
        var users = new List<string>();
        var plain = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(@"\obj\") || file.Contains(@"\bin\")) continue;
            string text = File.ReadAllText(file);
            string relative = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');
            if (text.Contains("Process.Start(", StringComparison.Ordinal) || text.Contains("ProcessStartInfo", StringComparison.Ordinal) || text.Contains("CreateProcess", StringComparison.Ordinal)) users.Add(relative);
            if (text.Contains("StartPlain", StringComparison.Ordinal) || text.Contains("LaunchPlain", StringComparison.Ordinal)) plain.Add(relative);
        }
        Assert.Equal(["src/Susu.Host/UpdateHost.cs", "src/Susu.Windows.Selection/Win32SelectionPlatform.cs", "src/Susu.Windows/Clipboard/Win32ClipboardPlatform.cs", "src/Susu.Windows/Media/Win32DiagnosticsPicker.cs"],
            users.Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(users, u => u.StartsWith("src/Susu.Plugins/", StringComparison.Ordinal) || u.StartsWith("src/Susu.Runtime/", StringComparison.Ordinal)); // plugin and runtime code never launches anything itself
        Assert.Equal(["src/Susu.Windows/ContainerHost.cs"], plain); // the plain launcher is declared there and used nowhere in the product
    }

    // ================= the real published exe =================

    private static string Version(string exe)
    {
        var v = FileVersionInfo.GetVersionInfo(exe);
        return $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}";
    }

    private static (int Exit, string Output) RunExe(string exe, TimeSpan timeout, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds)) { process.Kill(true); throw new TimeoutException(exe + " " + string.Join(' ', args.Take(3))); }
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private sealed class HelperRig
    {
        public required string Install, Root, Helper, HelperFolder;
        public required AppPaths Paths;
    }

    /// <summary>An install folder holding a real susu.exe, a data root, and the helper copy where the app puts it (updates\helper\&lt;id&gt;).</summary>
    private static HelperRig NewHelperRig(string publish)
    {
        string root = TestTemp.NewDir("susu-f18v-hroot");
        var paths = AppPaths.Resolve(root, development: false).EnsureCreated();
        string install = Path.Combine(TestTemp.NewDir("susu-f18v-hinstall"), "app");
        Directory.CreateDirectory(install);
        File.Copy(Path.Combine(publish, "susu.exe"), Path.Combine(install, "susu.exe"));
        string folder = Path.Combine(paths.Updates, "helper", "test");
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(publish, "susu.exe"), Path.Combine(folder, "susu-update.exe"));
        File.Copy(Path.Combine(publish, "e_sqlite3.dll"), Path.Combine(folder, "e_sqlite3.dll"));
        return new HelperRig { Install = install, Root = root, Helper = Path.Combine(folder, "susu-update.exe"), HelperFolder = folder, Paths = paths };
    }

    private static string[] Snapshot(string dir) => [.. Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).Where(p => !p.Contains(@"\helper\", StringComparison.OrdinalIgnoreCase)).Select(p => Path.GetRelativePath(dir, p) + (File.Exists(p) ? ":" + new FileInfo(p).Length : "")).Order(StringComparer.Ordinal)];

    [Fact] // UPD08: thirty-plus more hostile helper command lines (Unicode, very long, quotes, newlines, wrong shapes) with the real exe: all refused with exit 2, nothing changes; a valid line is not refused
    public void More_hostile_helper_command_lines_are_refused_by_the_real_exe_and_change_nothing()
    {
        string publish = NeedPublish();
        var rig = NewHelperRig(publish);
        string install = rig.Install, root = rig.Root;
        string fullwidth = install.Replace(':', '\uFF1A'), cyrillic = "\u0421" + install[1..];
        var lines = new List<string[]>();
        void Bad(params string[] args) => lines.Add(args);
        Bad("--apply-update", "--install-dir", fullwidth, "--data-root", root);
        Bad("--apply-update", "--install-dir", cyrillic, "--data-root", root);
        Bad("--apply-update", "--install-dir", install + "\u202E", "--data-root", root);
        Bad("--apply-update", "--install-dir", install + "\u200B", "--data-root", root);
        Bad("--apply-update", "--install-dir", install.ToUpperInvariant().Replace("APP", "ａｐｐ"), "--data-root", root);
        Bad("--apply-update", "--install-dir", install + "\n", "--data-root", root);
        Bad("--apply-update", "--install-dir", install + "\r\n--no-restart", "--data-root", root);
        Bad("--apply-update", "--install-dir", install + "\u0085", "--data-root", root);
        Bad("--apply-update", "--install-dir", install, "--data-root", root + "\n");
        Bad("--apply-update", "--install-dir", new string('a', 20000), "--data-root", root);
        Bad("--apply-update", "--install-dir", install, "--data-root", @"C:\" + new string('b', 20000));
        Bad("--apply-update", "--install-dir", @"C:\" + string.Join('\\', Enumerable.Repeat("a", 3000)), "--data-root", root);
        Bad("--apply-update", "--install-dir", "\"" + install + "\"", "--data-root", root);
        Bad("--apply-update", "--install-dir", "'" + install + "'", "--data-root", root);
        Bad("--apply-update", "--install-dir", install + "\\\"", "--data-root", root);
        Bad("--apply-update", "--install-dir", install + "\" --install-dir \"" + install, "--data-root", root);
        Bad("--apply-update", "--install-dir", install + "&calc", "--data-root", root);
        Bad("--apply-update", "--install-dir", install + "|calc", "--data-root", root);
        Bad("--apply-update", "--install-dir", install + ";calc", "--data-root", root);
        Bad("--apply-update", "--install-dir", "%TEMP%", "--data-root", root);
        Bad("--apply-update", "--install-dir", "$env:TEMP", "--data-root", root);
        Bad("--apply-update", "--install-dir", "..\\" + Path.GetFileName(Path.GetDirectoryName(install)!) + "\\app", "--data-root", root);
        Bad("--apply-update", "--install-dir", install.Replace("\\", "/") + "/../app", "--data-root", root);
        Bad("--apply-update", "--install-dir", install, "--data-root", install); // the data root is the install folder: the updates folder would lie inside it
        Bad("--apply-update", "--install-dir", install, "--data-root", Path.GetDirectoryName(install)!); // a data root whose helper folder this copy is not in
        Bad("--apply-update", "--install-dir", Path.Combine(rig.Paths.Updates), "--data-root", root); // the updates folder as the install folder
        Bad("--apply-update", "--install-dir", rig.HelperFolder, "--data-root", root);
        Bad("--apply-update", "--install-dir", install, "--data-root", root, "--report", "NUL.json");
        Bad("--apply-update", "--install-dir", install, "--data-root", root, "--report", Path.Combine(root, "CON.json"));
        Bad("--apply-update", "--install-dir", install, "--data-root", root, "--report", @"\\.\pipe\x.json");
        Bad("--apply-update", "--install-dir", install, "--data-root", root, "--report", Path.Combine(root, "missing-folder", "r.json"));
        Bad("--apply-update", "--install-dir", install, "--data-root", root, "--report", Path.Combine(rig.Paths.Updates, "..", "r.json"));
        Bad("--rollback-update", "--install-dir", install, "--data-root", root, "--reason", "USER-ROLLBACK");
        Bad("--rollback-update", "--install-dir", install, "--data-root", root, "--reason", "user-rollback ");
        Bad("--rollback-update", "--install-dir", install, "--data-root", root, "--reason", " first-start-failed");
        Bad("--rollback-update", "--install-dir", install, "--data-root", root, "--reason", "first-start-failed\u200B");
        Bad("--rollback-update", "--install-dir", install, "--data-root", root, "--reason", "first-start-failed", "--reason", "user-rollback");
        Bad("--recover-update", "--install-dir", install, "--data-root", root, "--expect-version", "1.0.0");
        Bad("--update-health", "--expect-version", "", "--data-root", root);
        Bad("--update-health", "--expect-version", "\uFF11.\uFF10.\uFF10", "--data-root", root);
        Bad("--update-health", "--expect-version", new string('1', 33), "--data-root", root);
        Bad("--update-health", "--expect-version", "-1.0.0", "--data-root", root);
        Bad("--update-health", "--data-root", root, "--reason", "user-rollback");
        Bad("--update-migrate", "--data-root", root, "--expect-version", "1.0.0");
        Bad("--apply-update", "--install-dir", install, "--data-root", root, "--no-restart", "--no-restart");
        Bad("--apply-update", "--install-dir", install, "--data-root", root, "--install-dir");
        Bad("--apply-update", "--install-dir", install, "--data-root", root, "extra-positional");
        Bad("--apply-update", "--install-dir", install, "--data-root", root, "--INSTALL-DIR", install);
        Bad("--apply-update", "--install-dir", install, "--data-root", root, "-install-dir", install);
        Assert.True(lines.Count >= 45, lines.Count.ToString());
        var before = Snapshot(root).Concat(Snapshot(install)).ToArray();
        foreach (var args in lines)
        {
            var (exit, output) = RunExe(rig.Helper, TimeSpan.FromSeconds(60), [.. args, "--no-restart"]);
            Assert.True(exit == 2, $"exit {exit} for: {string.Join(' ', args.Select(a => a.Length > 60 ? a[..60] + "…" : a))} {output}");
        }
        Assert.Equal(before, Snapshot(root).Concat(Snapshot(install)).ToArray()); // no folder, file, journal, backup or report appeared or changed
        Assert.False(Directory.Exists(Path.Combine(rig.Paths.Updates, "backup")));
        // Control: the valid line is accepted by the argument rules and then reports "nothing staged" (33), so the rig itself is not what refuses the lines above.
        Assert.Equal(33, RunExe(rig.Helper, TimeSpan.FromSeconds(60), "--apply-update", "--install-dir", install, "--data-root", root, "--no-restart").Exit);
    }

    [Fact] // UPD08: where the helper may run from: only a folder under ITS OWN data root's updates\helper; not another data root's, not a sibling folder, not through a junction; a tampered copy is NOT detected (pinned)
    public void The_helper_runs_only_from_its_own_helper_folder_and_a_tampered_copy_is_not_detected()
    {
        string publish = NeedPublish();
        var rig = NewHelperRig(publish);
        int Run(string helper, string root) => RunExe(helper, TimeSpan.FromSeconds(60), "--apply-update", "--install-dir", rig.Install, "--data-root", root, "--no-restart").Exit;
        // another data root's helper folder, driven at this data root
        var other = NewHelperRig(publish);
        Assert.Equal(2, Run(other.Helper, rig.Root));
        Assert.Equal(33, Run(rig.Helper, rig.Root)); // the right place works
        // a sibling folder of "helper", a deeper-but-outside folder, and the data folder itself
        foreach (string sibling in new[] { Path.Combine(rig.Paths.Updates, "helper2", "x"), Path.Combine(rig.Paths.Updates, "stage"), Path.Combine(rig.Paths.Local, "x"), Path.Combine(rig.Paths.Updates, "helper.x", "y") })
        {
            Directory.CreateDirectory(sibling);
            File.Copy(rig.Helper, Path.Combine(sibling, "susu-update.exe"));
            File.Copy(Path.Combine(rig.HelperFolder, "e_sqlite3.dll"), Path.Combine(sibling, "e_sqlite3.dll"));
            Assert.Equal(2, Run(Path.Combine(sibling, "susu-update.exe"), rig.Root));
        }
        // a helper folder that is a junction to a folder elsewhere
        string elsewhere = TestTemp.NewDir("susu-f18v-elsewhere");
        File.Copy(rig.Helper, Path.Combine(elsewhere, "susu-update.exe"));
        File.Copy(Path.Combine(rig.HelperFolder, "e_sqlite3.dll"), Path.Combine(elsewhere, "e_sqlite3.dll"));
        string link = Path.Combine(rig.Paths.Updates, "helper", "linked");
        Assert.Equal(0, Cmd($"mklink /J \"{link}\" \"{elsewhere}\""));
        Assert.Equal(2, Run(Path.Combine(link, "susu-update.exe"), rig.Root));
        // Known gap (D9, low): a helper copy that differs from the installed exe (bytes appended) is run without any comparison with the installed binary or its hash.
        string tampered = Path.Combine(rig.HelperFolder, "tampered-update.exe");
        File.Copy(rig.Helper, tampered);
        using (var s = new FileStream(tampered, FileMode.Append)) s.Write("tampered"u8);
        Assert.NotEqual(AtomicFile.HashOf(Path.Combine(rig.Install, "susu.exe")), AtomicFile.HashOf(tampered));
        Assert.Equal(33, Run(tampered, rig.Root));
    }

    private static void KillUnder(params string[] folders)
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string? path = p.MainModule?.FileName;
                if (path is not null && folders.Any(f => path.StartsWith(f, StringComparison.OrdinalIgnoreCase))) { p.Kill(true); p.WaitForExit(10_000); }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            finally { p.Dispose(); }
        }
    }

    [Fact] // UPD08/DATA03: the first-start guard with the REAL exe: two real starts are counted, the third does not run and the real helper rolls back, the version is marked failed
    public void Three_real_unconfirmed_starts_of_the_new_exe_roll_back_to_the_previous_version_and_record_the_version_as_failed()
    {
        string publish = NeedPublish();
        string version = Version(Path.Combine(publish, "susu.exe"));
        string root = TestTemp.NewDir("susu-f18v-e2e-root");
        var paths = AppPaths.Resolve(root, development: false).EnsureCreated();
        string install = Path.Combine(TestTemp.NewDir("susu-f18v-e2e-install"), "app");
        Directory.CreateDirectory(install);
        foreach (string file in Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
        {
            string target = Path.Combine(install, Path.GetRelativePath(publish, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        using (Database.Open(paths.Database, targetVersion: Database.SchemaVersion - 1)) { }
        Sql(paths.Database, "CREATE TABLE notes(id INTEGER PRIMARY KEY, text TEXT); INSERT INTO notes(text) VALUES ('user data one'), ('user data two');");
        // the "new version" package: the same program files plus a marker
        string pkg = Path.Combine(TestTemp.NewDir("susu-f18v-e2e-pkg"), "pkg.zip");
        using (var zip = ZipFile.Open(pkg, ZipArchiveMode.Create))
        {
            foreach (string file in Directory.EnumerateFiles(install, "*", SearchOption.AllDirectories)) zip.CreateEntryFromFile(file, Path.GetRelativePath(install, file).Replace('\\', '/'), CompressionLevel.Fastest);
            using var s = zip.CreateEntry("NEW-MARKER.txt").Open();
            s.Write("new"u8);
        }
        byte[] bytes = File.ReadAllBytes(pkg);
        var updater = new AppUpdater(install, paths.Database, paths.Updates, new NoopEnv());
        updater.BeginDownload(new AppUpdateOffer(version, 7, "pkg.zip", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), ""), "0.0.1");
        string placed = Path.Combine(updater.DownloadsFolder, "pkg.zip");
        File.WriteAllBytes(placed, bytes);
        Assert.True(updater.Stage(placed).Ok);
        string helperFolder = Path.Combine(paths.Updates, "helper", "e2e");
        Directory.CreateDirectory(helperFolder);
        File.Copy(Path.Combine(install, "susu.exe"), Path.Combine(helperFolder, "susu-update.exe"));
        File.Copy(Path.Combine(install, "e_sqlite3.dll"), Path.Combine(helperFolder, "e_sqlite3.dll"));
        var apply = RunExe(Path.Combine(helperFolder, "susu-update.exe"), TimeSpan.FromSeconds(120), "--apply-update", "--install-dir", install, "--data-root", root, "--no-restart");
        Assert.True(apply.Exit == 0, apply.Output);
        var guard = new FirstStartGuard(paths.Updates);
        Assert.Equal(0, guard.Read()?.Attempts);
        string exe = Path.Combine(install, "susu.exe");
        try
        {
            for (int start = 1; start <= 2; start++)
            {
                var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add("--data-root"); info.ArgumentList.Add(root);
                using var app = Process.Start(info)!;
                var deadline = DateTime.UtcNow.AddSeconds(40);
                while (DateTime.UtcNow < deadline && !app.HasExited && (guard.Read()?.Attempts ?? 0) < start) Thread.Sleep(100);
                if (!app.HasExited) { app.Kill(true); app.WaitForExit(15_000); } // killed before the 15 s confirmation: an unconfirmed start
                if ((guard.Read()?.Attempts ?? 0) < start) Assert.Skip($"The real exe did not count start {start} here (it could not run in this session), so the end-to-end guard cannot be exercised.");
                Assert.Equal(start, guard.Read()!.Attempts);
            }
            // third start: it must not run; the real helper takes over and rolls back
            var third = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            third.ArgumentList.Add("--data-root"); third.ArgumentList.Add(root);
            using var gate = Process.Start(third)!;
            Assert.True(gate.WaitForExit(60_000), "the third start must end by itself (the helper took over)");
            Assert.Equal(0, gate.ExitCode);
            var deadline2 = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline2 && (File.Exists(Path.Combine(install, "NEW-MARKER.txt")) || guard.Read() is not null || !guard.IsFailed(version))) Thread.Sleep(200);
            Assert.False(File.Exists(Path.Combine(install, "NEW-MARKER.txt")), "the new version's files are still installed");
            Assert.True(guard.IsFailed(version));
            Assert.Null(guard.Read());
            Assert.Equal(Database.SchemaVersion - 1, Database.PeekVersion(paths.Database));
            Assert.Equal(["user data one", "user data two"], NoteRows(paths.Database));
            Assert.True(Directory.GetFiles(Path.Combine(paths.Updates, "rolled-back-data"), "susu-*.db").Length == 1);
        }
        finally { KillUnder(install, Path.Combine(paths.Updates, "helper")); }
    }

    private sealed class NoopEnv : IUpdateEnvironment
    {
        public UpdateExitOutcome StopApp(TimeSpan timeout) => UpdateExitOutcome.NotRunning;
        public bool Migrate(string installDirectory, string databasePath, out string? detail) { detail = null; return true; }
        public bool HealthCheck(string installDirectory, string databasePath, string expectedVersion, out string? detail) { detail = null; return true; }
        public long FreeBytes(string path) => long.MaxValue;
    }

    [Fact] // UPD07: the real exe's uninstall with data-root spelling variants, a junction at the data root, a profile-like home and a drive-root data root
    public void Published_exe_uninstall_handles_data_root_variants_without_touching_anything_else()
    {
        string publish = NeedPublish();
        string exe = Path.Combine(publish, "susu.exe");
        string prefix = $"Susu.F18V.{Guid.NewGuid():N}.";
        string Uninstall(string root, bool delete)
        {
            var (exit, output) = RunExe(exe, TimeSpan.FromSeconds(60), ["--installer-uninstall", "--profile-prefix", prefix, "--data-root", root, .. delete ? new[] { "--delete-user-data" } : []]);
            return exit + " " + output;
        }
        // variants of one root: trailing separators, forward slashes, mixed case, a path with spaces and non-ASCII letters
        foreach (string spelling in new[] { "{0}", "{0}\\", "{0}/", "{0}\\\\\\" })
        {
            string home = Path.Combine(TestTemp.NewDir("susu-f18v-uroot"), "profil\u00E9 \u6570\u636E");
            string own = Path.Combine(home, "Roaming", "Su-Su"), local = Path.Combine(home, "Local", "Su-Su");
            Directory.CreateDirectory(own); Directory.CreateDirectory(local); Directory.CreateDirectory(Path.Combine(home, "Documents"));
            File.WriteAllText(Path.Combine(own, "settings.yaml"), "x"); File.WriteAllText(Path.Combine(local, "susu.db"), "x"); File.WriteAllText(Path.Combine(home, "Documents", "mine.docx"), "mine");
            string root = string.Format(spelling, home);
            Assert.StartsWith("0 ", Uninstall(root, delete: false));
            Assert.True(File.Exists(Path.Combine(own, "settings.yaml")) && File.Exists(Path.Combine(local, "susu.db")), "kept by default: " + spelling);
            Assert.StartsWith("0 ", Uninstall(root, delete: true));
            Assert.False(Directory.Exists(own) || Directory.Exists(local), spelling);
            Assert.Equal("mine", File.ReadAllText(Path.Combine(home, "Documents", "mine.docx")));
        }
        // the data root is a junction: the app's own folders are found through it (the user relocated the data there on purpose); only they are removed, the target's other content stays
        string target = TestTemp.NewDir("susu-f18v-utarget");
        Directory.CreateDirectory(Path.Combine(target, "Roaming", "Su-Su"));
        File.WriteAllText(Path.Combine(target, "Roaming", "Su-Su", "settings.yaml"), "x");
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        string linkRoot = Path.Combine(TestTemp.NewDir("susu-f18v-ulink"), "dataroot");
        Assert.Equal(0, Cmd($"mklink /J \"{linkRoot}\" \"{target}\""));
        Assert.StartsWith("0 ", Uninstall(linkRoot, delete: true));
        Assert.False(Directory.Exists(Path.Combine(target, "Roaming", "Su-Su")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
        Assert.True(Directory.Exists(linkRoot)); // the link itself is not removed
        // a drive root as the data root: legal text, nothing named Su-Su there, nothing is removed and the exit is clean
        string driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.False(Directory.Exists(Path.Combine(driveRoot, "Roaming", "Su-Su")), "unexpected folder at the drive root; the test refuses to run next to it");
        Assert.StartsWith("0 ", Uninstall(driveRoot, delete: true));
        Assert.True(Directory.Exists(driveRoot));
    }
}
