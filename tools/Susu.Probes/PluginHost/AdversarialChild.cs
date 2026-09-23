using System.IO.Pipes;
using System.Text.Json;
using Susu.Windows;

namespace Susu.Probes.PluginHost;

/// <summary>
/// Simulates a plugin host whose engine has been compromised (X07/S09 prototype): it holds a
/// legitimate connection and tries to misuse it. The main process must deny every forged call.
/// </summary>
internal static class AdversarialChild
{
    public static int Run(string pipeName, int expectedServerPid)
    {
        string? nonce = Console.In.ReadLine();
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(3000);
        if (ContainerHost.GetServerPid(pipe.SafePipeHandle) != expectedServerPid) return 20;
        var gate = new object();
        long seq = 0;
        void Send(Frame f) => Protocol.Write(pipe, f with { Sequence = ++seq }, gate);
        Send(new Frame(Protocol.Version, FrameTypes.Hello, Payload: Protocol.Element(new HelloPayload(nonce ?? "", "plugin-host", "adversarial", Environment.ProcessId), PluginJson.Default.HelloPayload)));
        Frame? invoke;
        do invoke = Protocol.Read(pipe); while (invoke is not null && invoke.Type != FrameTypes.Invoke);
        if (invoke is null) return 1;
        var call = invoke.Payload!.Value.Deserialize(PluginJson.Default.InvokePayload)!;
        JsonElement Args(string url) => Protocol.Element($"{{\"method\":\"GET\",\"url\":\"{url}\"}}");
        Frame Api(string? grant, string? plugin, int callId, int apiId) => new(Protocol.Version, FrameTypes.ApiCall, invoke.RequestId, plugin, Grant: grant,
            Payload: Protocol.Element(new ApiCallPayload(apiId, callId, "http", Args("https://api.bench.example/x")), PluginJson.Default.ApiCallPayload));
        Send(Api("00", invoke.PluginId, call.CallId, 1));                     // forged grant
        Send(Api(invoke.Grant, "com.other.plugin", call.CallId, 2));           // another plugin identity
        Send(Api(invoke.Grant, invoke.PluginId, call.CallId + 100, 3));        // another call
        Send(new Frame(Protocol.Version, FrameTypes.Completed, "forged-request", invoke.PluginId,
            Payload: Protocol.Element(new CompletedPayload(1, true, Protocol.Element("{}"), null), PluginJson.Default.CompletedPayload)));
        Send(new Frame(Protocol.Version, FrameTypes.Completed, invoke.RequestId, invoke.PluginId,
            Payload: Protocol.Element(new CompletedPayload(call.CallId, true, Protocol.Element("{\"text\":\"ok\"}"), null), PluginJson.Default.CompletedPayload)));
        Thread.Sleep(300);
        Send(Api(invoke.Grant, invoke.PluginId, call.CallId, 4));              // grant revoked at completion
        Thread.Sleep(300);
        Send(new Frame(Protocol.Version, "Evil", invoke.RequestId));           // unknown type must terminate the session
        Thread.Sleep(2000);
        return 0;
    }
}
