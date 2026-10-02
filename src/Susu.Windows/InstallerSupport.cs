using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Susu.Windows.Shell;

namespace Susu.Windows;

/// <summary>F18.1: what the NSIS installer and uninstaller need from the application (ARCHITECTURE install section). Each member has a real-API test.</summary>
[SupportedOSPlatform("windows")]
public static partial class InstallerSupport
{
    /// <summary>The name prefix of every AppContainer profile the plugin host creates (HostSession.Start); the uninstaller removes leftovers with it.</summary>
    public const string PluginProfilePrefix = "Susu.Plugin.Host.";

    private const string MappingsKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Mappings";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [LibraryImport("userenv.dll", EntryPoint = "DeleteAppContainerProfile", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeleteAppContainerProfile(string name);

    public enum ExitOutcome { NotRunning, Exited, TimedOut }

    /// <summary>How many tasks the running instance with this name reports in flight; null when none runs.</summary>
    public static int? QueryRunning(string instanceName)
        => SingleInstance.Request(instanceName, exit: false, out int count) ? count : null;

    /// <summary>
    /// Asks the running instance to exit through its normal shutdown path and waits until its window is gone and its single-instance mutex is
    /// released (so file replacement cannot race a still-running process). Never kills the process: a timeout is reported, the installer aborts.
    /// </summary>
    public static ExitOutcome RequestExit(string instanceName, TimeSpan timeout, out int inFlight)
    {
        inFlight = 0;
        if (!SingleInstance.Request(instanceName, exit: true, out inFlight))
            return SingleInstance.IsRunning(instanceName) ? ExitOutcome.TimedOut : ExitOutcome.NotRunning;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!SingleInstance.IsRunning(instanceName)) return ExitOutcome.Exited;
            Thread.Sleep(100);
        }
        return ExitOutcome.TimedOut;
    }

    /// <summary>Names of the AppContainer profiles of the current user that start with the prefix (from the profile mapping registry).</summary>
    public static IReadOnlyList<string> ListContainerProfiles(string prefix)
    {
        using var mappings = Registry.CurrentUser.OpenSubKey(MappingsKey);
        if (mappings is null) return [];
        var names = new List<string>();
        foreach (string sid in mappings.GetSubKeyNames())
        {
            using var key = mappings.OpenSubKey(sid);
            // The Moniker value is lower-cased by Windows; DisplayName keeps the name the profile was created with.
            if (key?.GetValue("Moniker") is string moniker && moniker.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) names.Add(key.GetValue("DisplayName") as string ?? moniker);
        }
        return names;
    }

    /// <summary>Deletes the AppContainer profiles with the prefix; returns how many were removed. A profile that is already gone is not an error.</summary>
    public static int DeleteContainerProfiles(string prefix)
    {
        int removed = 0;
        foreach (string name in ListContainerProfiles(prefix))
        {
            int hr = DeleteAppContainerProfile(name);
            if (hr >= 0) removed++;
        }
        return removed;
    }

    /// <summary>Removes the start-at-login entry (the one SetLaunchAtStartup writes). True when an entry existed.</summary>
    public static bool RemoveAutostart(string valueName = "Su-Su", string runKeyPath = RunKey)
    {
        using var run = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
        if (run?.GetValue(valueName) is null) return false;
        run.DeleteValue(valueName);
        return true;
    }

    /// <summary>
    /// The uninstall cleanup step (run by the uninstaller before it deletes its own files): container profiles and the start-at-login entry always;
    /// the user data folder only when <paramref name="deleteUserData"/> is true (the user chose it explicitly). Returns what was done.
    /// </summary>
    public static UninstallResult Uninstall(IReadOnlyList<string> dataFolders, bool deleteUserData, string profilePrefix = PluginProfilePrefix, bool removeAutostart = true, string autostartValue = "Su-Su", string runKeyPath = RunKey)
    {
        int profiles = DeleteContainerProfiles(profilePrefix);
        bool autostart = removeAutostart && RemoveAutostart(autostartValue, runKeyPath);
        bool data = false;
        int refused = 0;
        if (deleteUserData)
            foreach (string folder in dataFolders)
            {
                if (!Directory.Exists(folder)) continue;
                // UPD08/DATA: only a folder that is Su-Su's own by name is ever deleted (a tampered --data-root cannot point the deletion at another folder), and links inside it are
                // unlinked, never followed.
                if (!IsOwnDataFolder(folder)) { refused++; continue; }
                var info = new DirectoryInfo(folder);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) info.Delete();
                else DeleteTreeWithoutFollowing(folder);
                data = true;
            }
        return new UninstallResult(profiles, autostart, data, refused);
    }

    /// <summary>The data folders are always <c>...\Su-Su</c> (<c>AppPaths</c>); anything else is not ours to delete.</summary>
    public static bool IsOwnDataFolder(string folder)
    {
        string full = Path.GetFullPath(folder).TrimEnd('\\');
        return string.Equals(Path.GetFileName(full), "Su-Su", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(full) is not null;
    }

    private static void DeleteTreeWithoutFollowing(string folder)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(entry, recursive: false); // a junction or directory symlink: only the link goes
                else { File.SetAttributes(entry, FileAttributes.Normal); File.Delete(entry); }
            }
            else if ((attributes & FileAttributes.Directory) != 0) DeleteTreeWithoutFollowing(entry);
            else { File.SetAttributes(entry, FileAttributes.Normal); File.Delete(entry); }
        }
        Directory.Delete(folder, recursive: false);
    }

    public sealed record UninstallResult(int ProfilesRemoved, bool AutostartRemoved, bool UserDataDeleted, int FoldersRefused = 0);
}
