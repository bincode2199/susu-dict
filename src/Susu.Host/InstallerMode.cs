using System.Runtime.Versioning;
using Susu.Storage;
using Susu.Windows;

namespace Susu.Host;

/// <summary>
/// F18.1: the non-UI entry points the NSIS installer and uninstaller call (ARCHITECTURE install section). They are dispatched in Program.Main before
/// any application service starts. The result is the exit code plus, when --report is given, a one-line JSON file (susu.exe is a WinExe, so there is
/// no console). Exit codes: 0 ok / nothing running; 100 + min(n, 99) an instance is running with n tasks in flight (query); 11 the instance did not exit in time; 12 the uninstall step
/// failed; 13 uninstall refused because an instance still runs; 2 bad arguments.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class InstallerMode
{
    public const int RunningBase = 100, TimedOut = 11, Failed = 12, StillRunning = 13, BadArguments = 2;

    public static bool IsInstallerCommand(string[] args) => args.Length > 0 && args[0] is "--installer-query" or "--installer-exit" or "--installer-uninstall";

    public static int Run(string[] args)
    {
        string? dataRoot = Environment.GetEnvironmentVariable(AppPaths.DataRootVariable), report = null;
        string profilePrefix = InstallerSupport.PluginProfilePrefix;
        int timeoutMs = 30_000;
        bool deleteData = false;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data-root" when i + 1 < args.Length: dataRoot = args[++i]; break;
                case "--report" when i + 1 < args.Length: report = args[++i]; break;
                case "--profile-prefix" when i + 1 < args.Length && args[i + 1].StartsWith("Susu.", StringComparison.Ordinal): profilePrefix = args[++i]; break; // tests use a private prefix
                case "--timeout-ms" when i + 1 < args.Length && int.TryParse(args[i + 1], out int t) && t is >= 0 and <= 600_000: timeoutMs = t; i++; break;
                case "--delete-user-data" when args[0] == "--installer-uninstall": deleteData = true; break;
                default: return BadArguments;
            }
        }
        if (dataRoot is not null)
        {
            dataRoot = Susu.Plugins.AppUpdate.UpdatePathRules.PlainAbsolute(dataRoot); // UPD08: a tampered path (relative, .., UNC, device, stream) is refused before anything runs
            if (dataRoot is null) return BadArguments;
        }
        string instance = MainMode.InstanceName(dataRoot);
        int code;
        string json;
        switch (args[0])
        {
            case "--installer-query":
            {
                int? count = InstallerSupport.QueryRunning(instance);
                code = count is null ? 0 : RunningBase + Math.Min(count.Value, 99);
                json = $"{{\"state\":\"{(count is null ? "not-running" : "running")}\",\"inFlight\":{count ?? 0}}}";
                break;
            }
            case "--installer-exit":
            {
                var outcome = InstallerSupport.RequestExit(instance, TimeSpan.FromMilliseconds(timeoutMs), out int inFlight);
                code = outcome == InstallerSupport.ExitOutcome.TimedOut ? TimedOut : 0;
                json = $"{{\"state\":\"{outcome switch { InstallerSupport.ExitOutcome.NotRunning => "not-running", InstallerSupport.ExitOutcome.Exited => "exited", _ => "timed-out" }}\",\"inFlight\":{inFlight}}}";
                break;
            }
            default:
            {
                if (InstallerSupport.QueryRunning(instance) is not null) { code = StillRunning; json = "{\"state\":\"still-running\"}"; break; }
                try
                {
                    var paths = AppPaths.Resolve(dataRoot, development: false);
                    var result = InstallerSupport.Uninstall([paths.Roaming, paths.Local], deleteData, profilePrefix, removeAutostart: dataRoot is null); // a custom data root never wrote the login entry
                    code = result.FoldersRefused > 0 ? Failed : 0; // a data folder that is not Su-Su's own by name is never deleted
                    json = $"{{\"state\":\"done\",\"profilesRemoved\":{result.ProfilesRemoved},\"autostartRemoved\":{(result.AutostartRemoved ? "true" : "false")},\"userDataDeleted\":{(result.UserDataDeleted ? "true" : "false")},\"foldersRefused\":{result.FoldersRefused}}}";
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    code = Failed;
                    json = $"{{\"state\":\"failed\",\"code\":\"{e.GetType().Name}\"}}";
                }
                break;
            }
        }
        if (report is not null)
        {
            try { File.WriteAllText(report, json); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return code;
    }
}
