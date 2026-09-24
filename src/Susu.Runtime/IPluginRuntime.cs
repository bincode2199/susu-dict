namespace Susu.Runtime;

/// <summary>Callbacks from a plugin runtime into its plugin-host process. Invoked on the engine thread only.</summary>
public interface IRuntimeCallbacks
{
    /// <summary>Returns 0 when the API request was forwarded to the main process; nonzero rejects the JS promise.</summary>
    int ApiCall(string pluginId, int apiId, string json);
    void Completed(string pluginId, string json);
    void Log(string pluginId, string json);
}

/// <summary>
/// One engine runtime per plugin package. Every member is called on the single engine thread
/// (ARCHITECTURE 4: two fixed threads per plugin-host process, one pipe reader and one engine).
/// Status codes: 0 ok, 1 error, 2 execution budget exceeded (the runtime must be rebuilt).
/// </summary>
public interface IPluginRuntime : IDisposable
{
    string? Load(string entry, out int status);
    int Invoke(int callId, string capability, string requestJson, string configJson);
    int Settle(int apiId, bool ok, string json);
    int Abort(int callId);
    long EngineBytes { get; }
}

public delegate IPluginRuntime RuntimeFactory(string pluginId, string root, int memoryMiB, ExecutionBudget budget, IRuntimeCallbacks callbacks);
