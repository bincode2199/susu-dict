using System.Runtime.CompilerServices;
using Susu.Probes.PluginHost;

// PER01 Jint route: same plugin-host protocol as `Susu.Probes --plugin-host`, Jint engine.
if (args.Length == 4 && args[0] == "--plugin-host")
    return ChildHost.Run(args[1], int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), JintRuntime.Create, "jint");
Console.Error.WriteLine($"Usage: --plugin-host <pipe> <serverPid> jint (NativeAOT={!RuntimeFeature.IsDynamicCodeSupported})");
return 2;
