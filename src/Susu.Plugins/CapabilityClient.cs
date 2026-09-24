using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Susu.Contracts;

namespace Susu.Plugins;

/// <summary>One capability call's outcome, typed to the capability's result contract (PLAN 4.4/4.7).</summary>
public sealed record CapabilityOutcome<TResult>(bool Ok, TResult? Result, ErrorKind? ErrorKind, string? ErrorDetail);

/// <summary>
/// F05.4: the minimal reusable adapter for calling a plugin capability through the real host and
/// parsing its typed result - the same Invoke/await/deserialize shape <see cref="PluginProvider"/>
/// already uses for "translate", generalized so F09 (dictionary/OCR)/F10 (TTS)/F12 (ASR)/F15 (vocab)
/// do not each reinvent request/response plumbing, timeout/cancel handling and error classification.
/// </summary>
public static class CapabilityClient
{
    public static async Task<CapabilityOutcome<TResult>> InvokeAsync<TResult>(
        HostSession host, string pluginId, string capability, string requestJson, string jobId,
        IEnumerable<string> origins, JsonTypeInfo<TResult> resultType,
        IEnumerable<string>? secrets = null, IEnumerable<string>? handles = null, string configJson = "{}",
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        (string RequestId, int CallId, Task<IpcEnvelope> Result) invocation;
        try { invocation = host.Invoke(pluginId, capability, requestJson, jobId, origins, secrets, configJson, handles); }
        catch (IOException) { return new(false, default, Susu.Contracts.ErrorKind.Unavailable, "plugin host disconnected"); }
        var (requestId, callId, task) = invocation;
        IpcEnvelope envelope;
        try { envelope = await task.WaitAsync(timeout ?? TimeSpan.FromSeconds(30), cancellationToken); }
        catch (TimeoutException) { try { host.Cancel(pluginId, requestId, jobId, callId); } catch (IOException) { } return new(false, default, Susu.Contracts.ErrorKind.Timeout, null); }
        catch (IOException) { return new(false, default, Susu.Contracts.ErrorKind.Unavailable, "plugin host disconnected"); }
        var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
        if (!completed.Ok || completed.Result is null)
            return new(false, default, ErrorKinds.FromPlugin(completed.Error?.Kind), completed.Error?.Detail);
        TResult? result;
        try { result = completed.Result.Value.Deserialize(resultType); }
        catch (JsonException error) { return new(false, default, Susu.Contracts.ErrorKind.BadResponse, error.Message); }
        return new(true, result, null, null);
    }
}
