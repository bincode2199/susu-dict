using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Susu.Windows;
if (args.Length == 4 && args[0] == "--plugin-host") return Susu.Probes.PluginHost.ChildHost.Run(args[1], int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), Susu.Probes.PluginHost.QuickJsRuntime.Create, "quickjs");
if (args.Length == 4 && args[0] == "--plugin-host-adversarial") return Susu.Probes.PluginHost.AdversarialChild.Run(args[1], int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
if (args.Length >= 1 && args[0] == "--x-probe") return Susu.Probes.PluginHost.XProbeChild.Run(args[1..]);
if (args.Length == 1 && args[0] == "--x-probe-idle") { Thread.Sleep(60000); return 0; }
if (args.Length == 3 && args[0] == "--x06-parent") return Susu.Probes.PluginHost.XMatrix.CrashParent(args[1], args[2]);
if (args.Length == 2 && args[0] == "--x-matrix") return Susu.Probes.PluginHost.XMatrix.Run(args[1]);
if (args.Length == 5 && args[0] == "--engine-bench") return Susu.Probes.PluginHost.EngineBench.Run(args[1], args[2], args[3], int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture));
if (args.Length == 3 && args[0] == "--webview-bench") return Susu.Probes.PluginHost.EngineBench.RunWebView(args[1], int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
if (args.Length == 2 && args[0] == "--calibrate-budget") return Susu.Probes.PluginHost.EngineBench.Calibrate(args[1]);

if (args.Length == 3 && args[0] == "--sandbox-child") return SandboxChild.Run(args[1], int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
if (args.Length == 1 && args[0] == "--sandbox-grandchild") return 0;
if(args.Length==2&&args[0]=="--sandbox-lifecycle")
{
    if(!SandboxProbe.IsAppContainer())return 2;
    if(args[1]=="memory")
    {
        int error=SandboxProbe.CheckMemoryLimit();
        bool denied=error is 8 or 1455 or 1816;
        Console.WriteLine($"memoryLimitDenied={denied}; win32Error={error}; requestedMiB=256; jobLimitMiB=128");return denied?0:1;
    }
    if(args[1]=="job-close"){Console.WriteLine("containerReady=True");Console.Out.Flush();Thread.Sleep(30000);return 3;}
    if(args[1]=="own-storage")
    {
        try{string folder=SandboxProbe.CheckOwnStorage();Console.WriteLine($"ownStorageWrite=True; registryRoundTrip=True; fileRoundTrip=True; folder={folder}");return 0;}
        catch(Exception error){Console.WriteLine($"ownStorageWrite=False; error={error.GetType().Name}; HRESULT=0x{error.HResult:X8}");return 1;}
    }
    return 4;
}
if (args.Length == 1 && args[0] == "--selection-stall") { Thread.Sleep(10000); return 0; }
if (args.Length == 2 && args[0] == "--selection-target") { SelectionProbe.RunTarget(args[1] == "password" ? 1 : 0,args[1] == "empty" ? 1 : 0); return 0; }
if(args.Length==2 && args[0] is "--read-selection" or "--read-ia2")
{
    try{Console.WriteLine(SelectionTests.ReadExternal(args[1],args[0]=="--read-ia2"));return 0;}
    catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
}
if (args.Length == 2 && args[0] is "--selection-child" or "--ia2-child")
{
    try
    {
        var result = SelectionProbe.ReadWindow((nint)long.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture),args[0]=="--ia2-child");
        Console.WriteLine(JsonSerializer.Serialize(new SelectionResult(result.Text,result.Reason),ProbeJson.Default.SelectionResult));
        return 0;
    }
    catch(Exception error){Console.Error.WriteLine($"Selection native error {error.GetType().Name} 0x{error.HResult:X8}");return 1;}
}
if (args.Length is 2 or 3 && args[0] is "--measure-windows" or "--smoke-windows")
{
    string data = Path.GetFullPath(Path.Combine("artifacts", "probe-data", "windows", Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(data);
    int memoryMode=args.Length==3?args[2] switch{"suspend"=>1,"low"=>2,"baseline"=>0,_=>throw new ArgumentException("Unknown memory mode")}:0;
    try { ExtendedPlatformProbes.MeasureWindows(Path.GetFullPath(args[1]), data, args[0] == "--measure-windows",memoryMode); return 0; }
    catch (Exception error) { Console.Error.WriteLine($"Window measurement failed: {error.Message}"); return 1; }
}
var results = new List<ProbeResult>();
void Run(string id, Action action)
{
    var timer = Stopwatch.StartNew();
    try { action(); results.Add(new(id, "passed", timer.Elapsed.TotalMilliseconds, null)); }
    catch (Exception error) { results.Add(new(id, "failed", timer.Elapsed.TotalMilliseconds, $"{error.GetType().Name}: {error.Message}")); }
}
Run("NativeAOT", () => { if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("Run the published NativeAOT executable, not dotnet run."); });
Run("HWND-create-destroy", PlatformProbes.Window);
Run("UIA-MTA-activation", () => PlatformProbes.ActivateCom(new("ff48dba4-60ef-4201-aa87-54103eef594e"), new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")));
Run("SAPI-ISpVoice-activation", () => PlatformProbes.ActivateCom(new("96749377-3391-11d2-9ee3-00c04f797396"), new("6c44df74-72b9-4992-a1ec-ef996e0422d4")));
Run("WASAPI-MMDeviceEnumerator-activation", () => PlatformProbes.ActivateCom(new("bcde0395-e52f-467c-8e3d-c4579291692e"), new("a95664d2-9614-4f35-a746-de8db63617e6")));
Run("MF-startup-shutdown", PlatformProbes.MediaFoundation);
Run("DPAPI-roundtrip-tamper", PlatformProbes.Dpapi);
Run("SQLite-parameters-online-backup", LibraryProbes.Sqlite);
Run("YAML-low-level-parser", LibraryProbes.Yaml);
Run("Ed25519-RFC8032-tamper", LibraryProbes.Ed25519);
Run("QuickJS-ESM-Promise-interrupt", QuickJsProbe.Run);
Run("ELS-language-detection", ExtendedPlatformProbes.LanguageDetection);
if(args.Contains("--selection")) Run("UIA-selected-empty-password-timeout", SelectionTests.Run);
if(args.Contains("--selection")) Run("IA2-hand-vtable-synthetic-bounds", SelectionProbe.VerifyIa2Abi);
if(args.Contains("--selection")) Run("MSAA-password-unsupported-500ms",()=>SelectionTests.RunMsaa(500));
if(args.Contains("--msaa-diagnostic")) Run("MSAA-diagnostic-3s-not-acceptance",()=>SelectionTests.RunMsaa(3000));
if (args.Contains("--sandbox")) Run("AppContainer-token-resource-boundary", () =>
{
    string secret = Path.GetFullPath("artifacts/probe-data/synthetic-secret.txt");
    Directory.CreateDirectory(Path.GetDirectoryName(secret)!);
    File.WriteAllText(secret, "Synthetic secret: no user data");
    string evidence = SandboxProbe.Run(Environment.ProcessPath!, secret);
    File.WriteAllText("artifacts/probe-data/sandbox-result.txt", evidence);
});
if (args.Contains("--webview")) Run("WebView-controller-script-exit", () =>
{
    string data = Path.GetFullPath(Path.Combine("artifacts", "probe-data", "webview", Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(data);
    double[] samples = ExtendedPlatformProbes.WebViewRoundTrips(data);
    Directory.CreateDirectory("artifacts/probe-data");
    File.WriteAllText("artifacts/probe-data/webview-roundtrips.json", JsonSerializer.Serialize(samples, ProbeJson.Default.DoubleArray));
});
var report = new ProbeReport(DateTimeOffset.UtcNow, RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), results.ToArray());
Console.WriteLine(JsonSerializer.Serialize(report, ProbeJson.Default.ProbeReport));
return results.Any(r => r.Status == "failed") ? 1 : 0;

internal sealed record ProbeResult(string Id, string Status, double Milliseconds, string? Error);
internal sealed record ProbeReport(DateTimeOffset Timestamp, string OS, string Architecture, ProbeResult[] Results);
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ProbeReport))]
[JsonSerializable(typeof(double[]))]
[JsonSerializable(typeof(SelectionResult))]
internal partial class ProbeJson : JsonSerializerContext;
