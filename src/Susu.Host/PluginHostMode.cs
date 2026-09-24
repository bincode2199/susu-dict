using Susu.Runtime;

namespace Susu.Host;

/// <summary>
/// `susu.exe --plugin-host &lt;pipeName&gt; &lt;serverPid&gt; &lt;engine&gt;`: the plugin-host child process
/// (F04.1/F04.2). Launched only by <c>Susu.Plugins.HostSession.Start</c> inside an AppContainer; it
/// never runs standalone against a real user's data.
/// </summary>
internal static class PluginHostMode
{
    public static int Run(string[] args)
    {
        // args[0] == "--plugin-host"
        if (args.Length < 4) { Console.Error.WriteLine("usage: susu --plugin-host <pipeName> <serverPid> <engine>"); return 2; }
        string pipeName = args[1];
        if (!int.TryParse(args[2], out int serverPid)) { Console.Error.WriteLine("invalid serverPid"); return 2; }
        string engine = args[3];
        RuntimeFactory factory = engine switch
        {
            "quickjs" => QuickJsRuntime.Create,
            _ => throw new NotSupportedException($"engine '{engine}' is not available; QuickJS-NG is the only production route (F00 G0)."),
        };
        return ChildHost.Run(pipeName, serverPid, factory, engine, Susu.Contracts.HostBuild.Current);
    }
}
