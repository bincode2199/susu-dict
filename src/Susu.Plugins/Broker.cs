using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Net;
using Susu.Runtime;
using Susu.Storage;

namespace Susu.Plugins;

/// <summary>
/// S02 account/origin binding (PLAN 4.5.4.1), wired into Broker's secret resolution when supplied. The
/// enforcement rule itself (Susu.Domain.CredentialAuthorizer) was already built and unit-tested in F02;
/// this just adapts it to what Broker has at resolve time (a grant's plugin id/instance id/signer, a
/// request's origin and credential target).
///
/// No production composition root builds one yet: doing so needs a live view of AppSettings.Accounts/
/// Instances (from ISettingsStore) plus each loaded package's *confirmed* signer identity, and needs
/// every Invoke/Issue call site to carry an instance id, not just a package id - none of which exists
/// yet, because nothing calls Invoke with a real vendor account today (F06 is what will wire real
/// adapters). Exactly what F07 (or F06, whichever lands the first real paid/keyed adapter) must add:
/// 1. Thread instanceId through HostSession.Invoke/Broker.Issue (currently only pluginId/package flows).
/// 2. Resolve each package's confirmed PluginIdentity.Signer from the installed-package record (F16),
///    not assume "unsigned".
/// 3. Construct one AccountAuthorization from the live ISettingsStore state (rebuild-on-change, same
///    shape as NetworkBrokerProvider) and pass it into Broker's constructor.
/// Until then Broker.ResolveSecret falls back to namespacing secrets by plugin id, documented on
/// <see cref="Broker"/> itself.
/// </summary>
public sealed class AccountAuthorization(Func<(IReadOnlyList<AccountSettings> Accounts, IReadOnlyList<InstanceSettings> Instances)> settings)
{
    public (CredentialDecision Decision, string? AccountId) Authorize(string instanceId, string packageId, string signer, string secretName, string origin, string use)
    {
        var (accounts, instances) = settings();
        var instance = instances.FirstOrDefault(i => i.Id == instanceId);
        if (instance is null) return (CredentialDecision.UnknownAccount, null);
        return CredentialAuthorizer.Authorize(accounts, instance, new PluginIdentity(packageId, signer), secretName, origin, use);
    }
}

/// <summary>
/// Main-process authorization for plugin API calls (PLAN 4.5.4). Grants are unguessable, bound to
/// one request/plugin/call and revoked at completion; the child's self-reported plugin id or paths
/// are never trusted. Migrated from the F00 prototype (tools/Susu.Probes/PluginHost/Broker.cs).
///
/// F05.1/F05.2: the "http" op now runs the real <see cref="NetworkBroker"/> (origin/DNS/redirect
/// policy, credential injection, signing, JSON/multipart transforms) instead of the F04 local-only
/// stub; "http.stream.*" drives $http.stream (SSE) through the same pipeline. Secrets are looked up
/// in <see cref="ISecretStore"/> under the plugin's own id as the account id - a simplification until
/// F06/F07's full account-binding UI exists, noted in the F05 evidence.
/// </summary>
public sealed class Broker : IDisposable
{
    public sealed class GrantInfo(string grant, string requestId, string pluginId, int callId, HashSet<string> origins, HashSet<string> secrets, HashSet<string> handles, DateTime expires, string instanceId, string signer)
    {
        public string Grant { get; } = grant;
        public string RequestId { get; } = requestId;
        public string PluginId { get; } = pluginId;
        public int CallId { get; } = callId;
        public HashSet<string> Origins { get; } = origins;
        public HashSet<string> Secrets { get; } = secrets;
        public HashSet<string> Handles { get; } = handles;
        public DateTime Expires { get; } = expires;
        public bool Revoked { get; set; }
        public int InFlight;
        /// <summary>S02: which configured instance this call belongs to (defaults to PluginId when the
        /// caller does not track instances separately from packages yet) and the package's confirmed
        /// signer identity (defaults to "unsigned:&lt;pluginId&gt;" until F16 tracks real signatures).</summary>
        public string InstanceId { get; } = instanceId;
        public string Signer { get; } = signer;
    }

    /// <summary>
    /// One open $http.stream call. A background pump reads the live HTTP response ahead of the plugin
    /// and buffers pieces into <see cref="Channel"/>, but only after reserving <see cref="StreamWindow"/>
    /// credit for each one - so a plugin that reads slowly stalls the pump (and therefore the upstream
    /// socket read) instead of letting host memory grow unboundedly (PLAN 4.5.4.4's per-call/per-process
    /// unacked budget, the same primitive F04.3 built for outbound Chunk frames). Each successful
    /// http.stream.read both dequeues one piece and acks its bytes, freeing credit for the pump.
    /// </summary>
    private sealed class StreamState(System.Threading.Channels.Channel<byte[]> channel, Task pump, CancellationTokenSource cts, string callKey, IReadOnlyList<string> resolvedSecrets)
    {
        public System.Threading.Channels.Channel<byte[]> Channel { get; } = channel;
        public Task Pump { get; } = pump;
        public CancellationTokenSource Cts { get; } = cts;
        public string CallKey { get; } = callKey;
        /// <summary>S10: checked against every piece as it is read (StreamReadAsync).</summary>
        public IReadOnlyList<string> ResolvedSecrets { get; } = resolvedSecrets;
    }

    private static readonly HashSet<string> allowedFields = ["method", "url", "headers", "query", "body", "responseType", "bodyFiles", "responseFiles", "credentials", "sign", "errorPointer"];
    private static readonly HashSet<string> forbiddenHeaders = new(StringComparer.OrdinalIgnoreCase) { "host", "content-length", "proxy-authorization", "transfer-encoding", "connection" };
    private readonly Dictionary<string, GrantInfo> grants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> stores = new(StringComparer.Ordinal);
    private readonly Func<NetworkBroker> getNetwork;
    private readonly NetworkBroker? ownedNetwork;
    private readonly FileLeases? leases;
    private readonly ISecretStore? secretStore;
    private readonly AccountAuthorization? accounts;
    private readonly StreamWindow streamWindow = new();
    private readonly Dictionary<string, StreamState> streams = new(StringComparer.Ordinal);
    private int processInFlight;

    /// <summary>Fired synchronously for a "progress.emit" ApiCall (ctx.$emit in the plugin bootstrap):
    /// a streaming capability's incremental piece of its own in-flight result, keyed by the owning
    /// call's request id. HostSession forwards it to whatever <c>onChunk</c> the caller registered at
    /// <see cref="HostSession.Invoke"/> - fire-and-forget, so a slow/failed UI update never blocks the
    /// plugin's own next $http.stream.read.</summary>
    public event Action<string, string>? Progress;

    public Broker(NetworkBroker? network = null, FileLeases? leases = null, ISecretStore? secretStore = null, AccountAuthorization? accounts = null)
    {
        ownedNetwork = network ?? new NetworkBroker(new NetworkBrokerOptions());
        getNetwork = () => ownedNetwork;
        this.leases = leases;
        this.secretStore = secretStore;
        this.accounts = accounts;
    }

    /// <summary>Reads the current broker on every call instead of fixing one at construction time, so a
    /// proxy/timeout settings change (<see cref="NetworkBrokerProvider"/>) takes effect for the next
    /// request without restarting the plugin host. The provider owns disposal of the brokers it builds.</summary>
    public Broker(NetworkBrokerProvider provider, FileLeases? leases = null, ISecretStore? secretStore = null, AccountAuthorization? accounts = null)
    {
        getNetwork = () => provider.Current;
        this.leases = leases;
        this.secretStore = secretStore;
        this.accounts = accounts;
    }

    public void ApproveLocalOrigin(string origin) => getNetwork().LocalOrigins.Approve(origin);

    /// <param name="expiresAt">Test-only override of the 10-minute default grant lifetime (F04.3 S09: a call token is rejected once expired).</param>
    public GrantInfo Issue(string requestId, string pluginId, int callId, IEnumerable<string> origins, IEnumerable<string>? secrets = null, IEnumerable<string>? handles = null,
        DateTime? expiresAt = null, string? instanceId = null, string? signer = null)
    {
        var info = new GrantInfo(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), requestId, pluginId, callId,
            [.. origins.Select(o => Origin(new Uri(o)) ?? throw new ArgumentException($"Invalid origin {o}"))], [.. secrets ?? []], [.. handles ?? []],
            expiresAt ?? DateTime.UtcNow.AddMinutes(10), instanceId ?? pluginId, signer ?? $"unsigned:{pluginId}");
        lock (grants) grants[info.Grant] = info;
        return info;
    }

    public void Revoke(string grant)
    {
        List<(string Id, StreamState State)>? toClose = null;
        lock (grants) if (grants.Remove(grant, out var info)) info.Revoked = true;
        lock (streams)
        {
            foreach (var (id, state) in streams) if (state.CallKey.StartsWith(grant + ":", StringComparison.Ordinal)) (toClose ??= []).Add((id, state));
            if (toClose is not null) foreach (var (id, _) in toClose) streams.Remove(id);
        }
        // Best-effort cleanup on call teardown without an explicit stream.close from the plugin's
        // generator finally-block (e.g. a crashed/cancelled call): stops the pump and releases any
        // StreamWindow credit it still holds rather than leaking it until process exit.
        if (toClose is not null) foreach (var (id, state) in toClose) { state.Cts.Cancel(); streamWindow.Reset(id); }
        CancelCall(grant); // any plain (non-stream) $http still in flight for this call is aborted too
    }

    /// <summary>Aborts the upstream HTTP request(s) currently in flight for one capability call, so a
    /// Cancel arriving mid-request actually closes the connection promptly instead of letting it run to
    /// completion in the background (F05.2 "取消/超时/插件退出撤销其调用租约，停止 I/O"). Safe to call for
    /// a grant with nothing in flight (no-op).</summary>
    public void CancelCall(string grant)
    {
        List<CancellationTokenSource>? toCancel;
        lock (callCts) { if (callCts.Remove(grant, out var set)) toCancel = [.. set]; else toCancel = null; }
        if (toCancel is not null) foreach (var cts in toCancel) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
    }

    private readonly Dictionary<string, HashSet<CancellationTokenSource>> callCts = new(StringComparer.Ordinal);

    /// <summary>Registers a fresh, linked CTS for one in-flight $http/$http.stream.open op under
    /// <paramref name="grant"/>; <see cref="CancelCall"/> or <see cref="Revoke"/> cancels it. Always pair
    /// with <see cref="EndHttpCall"/> in a finally.</summary>
    private CancellationTokenSource BeginHttpCall(string grant)
    {
        var cts = new CancellationTokenSource();
        lock (callCts) (callCts.TryGetValue(grant, out var set) ? set : callCts[grant] = []).Add(cts);
        return cts;
    }

    private void EndHttpCall(string grant, CancellationTokenSource cts)
    {
        lock (callCts) if (callCts.TryGetValue(grant, out var set)) { set.Remove(cts); if (set.Count == 0) callCts.Remove(grant); }
        cts.Dispose();
    }

    public int ActiveGrants { get { lock (grants) return grants.Count; } }
    /// <summary>Open $http.stream calls (diagnostics/tests): should drop to 0 once every owning call's
    /// grant is revoked (B07-style cleanup), never accumulate across calls that forgot to close.</summary>
    public int ActiveStreams { get { lock (streams) return streams.Count; } }

    /// <summary>Validates and executes one ApiCall envelope; returns the ApiResult payload.</summary>
    public async Task<ApiResultPayload> HandleAsync(IpcEnvelope envelope)
    {
        var call = envelope.Payload?.Deserialize(ContractsJson.Default.ApiCallPayload);
        if (call is null) return Deny(0, "malformed ApiCall");
        GrantInfo? grant;
        lock (grants) grants.TryGetValue(envelope.Grant ?? "", out grant);
        if (grant is null || grant.Revoked) return Deny(call.ApiId, "unknown or revoked grant");
        if (grant.Expires < DateTime.UtcNow) return Deny(call.ApiId, "expired grant");
        if (grant.RequestId != envelope.RequestId || grant.PluginId != envelope.PluginId || grant.CallId != call.CallId) return Deny(call.ApiId, "grant not bound to this request/plugin/call");
        if (Interlocked.Increment(ref grant.InFlight) > 4) { Interlocked.Decrement(ref grant.InFlight); return Deny(call.ApiId, "per-call API concurrency exceeded"); }
        if (Interlocked.Increment(ref processInFlight) > ProtocolLimits.MaxHostOperationsPerProcess) { Interlocked.Decrement(ref processInFlight); Interlocked.Decrement(ref grant.InFlight); return Deny(call.ApiId, "process API concurrency exceeded"); }
        try
        {
            return call.Op switch
            {
                "http" => await HttpAsync(grant, call),
                "http.stream.open" => await StreamOpenAsync(grant, call),
                "http.stream.read" => await StreamReadAsync(grant, call),
                "http.stream.close" => await StreamCloseAsync(grant, call),
                "store.get" or "store.set" => Store(grant, call),
                "progress.emit" => ProgressEmit(grant, call),
                _ => Deny(call.ApiId, $"operation '{call.Op}' not provided"),
            };
        }
        finally { Interlocked.Decrement(ref processInFlight); Interlocked.Decrement(ref grant.InFlight); }
    }

    private ApiResultPayload ProgressEmit(GrantInfo grant, ApiCallPayload call)
    {
        string text = call.Args.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
        if (text.Length > 8192) text = text[..8192];
        if (text.Length > 0) Progress?.Invoke(grant.RequestId, text);
        return Allow(call.ApiId, "true");
    }

    private ApiResultPayload Store(GrantInfo grant, ApiCallPayload call)
    {
        if (!call.Args.TryGetProperty("key", out var keyElement) || keyElement.ValueKind != JsonValueKind.String || keyElement.GetString()!.Length is 0 or > 256)
            return Deny(call.ApiId, "invalid store key");
        string key = keyElement.GetString()!;
        lock (stores)
        {
            // Namespace is the grant's plugin id, never a plugin-supplied value.
            var store = stores.TryGetValue(grant.PluginId, out var s) ? s : stores[grant.PluginId] = new(StringComparer.Ordinal);
            if (call.Op == "store.get")
                return Allow(call.ApiId, store.TryGetValue(key, out var value) ? value : "null");
            string raw = call.Args.TryGetProperty("value", out var v) ? v.GetRawText() : "null";
            if (store.Where(kv => kv.Key != key).Sum(kv => kv.Key.Length + kv.Value.Length) + key.Length + raw.Length > 1024 * 1024) return Deny(call.ApiId, "plugin store quota exceeded");
            store[key] = raw;
            return Allow(call.ApiId, "true");
        }
    }

    // ---- $http / $http.stream ----

    private async Task<ApiResultPayload> HttpAsync(GrantInfo grant, ApiCallPayload call)
    {
        BrokerHttpRequest request;
        List<(string Handle, FileLease Lease)> takenLeases = [];
        var resolvedSecrets = new List<string>();
        try { request = BuildRequest(grant, call.Args, takenLeases, resolvedSecrets); }
        catch (BrokerDenyException deny) { ReleaseAll(takenLeases); return Deny(call.ApiId, deny.Message, deny.Kind); }
        var cts = BeginHttpCall(grant.Grant);
        try
        {
            var outcome = await getNetwork().ExecuteAsync(request, cts.Token);
            return outcome switch
            {
                // S10: a response that echoes back a known literal form of a credential the host just
                // injected is intercepted here, before the plugin ever sees it (defense in depth only -
                // any other transformation of the value is out of scope, PLAN says so explicitly).
                BrokerSuccess success when CredentialLeakScanner.ContainsKnownForm(success.Response, resolvedSecrets)
                    => Deny(call.ApiId, "response echoed a known credential form", "bad_response"),
                BrokerSuccess success => Allow(call.ApiId, BuildResultJson(success.Response)),
                BrokerFailure failure => Deny(call.ApiId, failure.Detail, failure.Kind),
                _ => Deny(call.ApiId, "unknown broker outcome"),
            };
        }
        // A mid-flight Cancel (Broker.CancelCall/Revoke) aborted the upstream request; NetworkBroker
        // does not itself classify this (it only catches its own internal timeout), so it propagates
        // here uncaught - the host, not the plugin, is what cancelled it, so this is never surfaced as
        // a plugin-thrown kind (S09/PLAN 4.5.1 "取消...撤销其调用租约，停止 I/O").
        catch (OperationCanceledException) { return Deny(call.ApiId, "cancelled by host", "cancelled"); }
        catch (BrokerDenyException deny) { return Deny(call.ApiId, deny.Message, deny.Kind); }
        finally { EndHttpCall(grant.Grant, cts); ReleaseAll(takenLeases); }
    }

    private async Task<ApiResultPayload> StreamOpenAsync(GrantInfo grant, ApiCallPayload call)
    {
        BrokerHttpRequest request;
        List<(string Handle, FileLease Lease)> takenLeases = [];
        var resolvedSecrets = new List<string>();
        try { request = BuildRequest(grant, call.Args, takenLeases, resolvedSecrets); }
        catch (BrokerDenyException deny) { return Deny(call.ApiId, deny.Message, deny.Kind); }
        finally { ReleaseAll(takenLeases); } // stream bodies never carry file handles (SSE requests are small/text)
        var cts = BeginHttpCall(grant.Grant);
        BrokerStreamOutcome outcome;
        try { outcome = await getNetwork().ExecuteStreamAsync(request, cts.Token); }
        catch (OperationCanceledException) { EndHttpCall(grant.Grant, cts); return Deny(call.ApiId, "cancelled by host", "cancelled"); }
        EndHttpCall(grant.Grant, cts); // the stream.open request itself is done; the live body read (if any) is owned by the pump from here
        switch (outcome)
        {
            case BrokerStreamRejected rejected:
            {
                var node = new JsonObject { ["status"] = rejected.Status, ["headers"] = HeadersNode(rejected.Headers), ["error"] = new JsonObject { ["body"] = System.Text.Encoding.UTF8.GetString(rejected.Body), ["truncated"] = rejected.Truncated } };
                return Allow(call.ApiId, node.ToJsonString());
            }
            case BrokerStreamFailure failure:
                return Deny(call.ApiId, failure.Detail, failure.Kind);
            case BrokerStreamStarted started:
            {
                // S10: the headers half of the started response is scanned the same as a non-streaming
                // response's; body pieces are scanned as they are read (StreamReadAsync), since the
                // full body is never buffered here.
                foreach (var value in started.Headers.Values)
                    if (CredentialLeakScanner.ContainsKnownForm(value, resolvedSecrets))
                        return Deny(call.ApiId, "response echoed a known credential form", "bad_response");
                string streamId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
                string callKey = $"{grant.Grant}:{call.CallId}";
                var channel = System.Threading.Channels.Channel.CreateUnbounded<byte[]>(new() { SingleReader = true, SingleWriter = true });
                var pumpCts = new CancellationTokenSource();
                var pump = Task.Run(() => PumpStreamAsync(streamId, started.Text, channel.Writer, pumpCts.Token));
                lock (streams) streams[streamId] = new StreamState(channel, pump, pumpCts, callKey, resolvedSecrets);
                var node = new JsonObject { ["status"] = started.Status, ["headers"] = HeadersNode(started.Headers), ["streamId"] = streamId };
                return Allow(call.ApiId, node.ToJsonString());
            }
            default: return Deny(call.ApiId, "unknown stream outcome");
        }
    }

    /// <summary>Reads the live HTTP response ahead of the plugin, but only as fast as StreamWindow credit
    /// allows (PLAN 4.5.4.4) - a slow-reading plugin stalls this loop, and therefore the upstream socket
    /// read, instead of the host buffering an unbounded amount of un-consumed SSE data in memory.</summary>
    private async Task PumpStreamAsync(string streamId, IAsyncEnumerable<string> text, System.Threading.Channels.ChannelWriter<byte[]> writer, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var piece in text.WithCancellation(cancellationToken))
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(piece);
                if (bytes.Length > ProtocolLimits.MaxStreamChunkBytes) throw new BrokerDenyException("stream chunk exceeded the frame size limit");
                while (!streamWindow.TryReserve(streamId, bytes.Length))
                    await Task.Delay(15, cancellationToken);
                await writer.WriteAsync(bytes, cancellationToken);
            }
            writer.TryComplete();
        }
        catch (OperationCanceledException) { writer.TryComplete(); }
        catch (Exception error) { writer.TryComplete(error); }
    }

    private async Task<ApiResultPayload> StreamReadAsync(GrantInfo grant, ApiCallPayload call)
    {
        if (!call.Args.TryGetProperty("streamId", out var idElement) || idElement.ValueKind != JsonValueKind.String) return Deny(call.ApiId, "streamId required");
        string streamId = idElement.GetString()!;
        StreamState? state;
        lock (streams) streams.TryGetValue(streamId, out state);
        if (state is null || state.CallKey != $"{grant.Grant}:{call.CallId}") return Deny(call.ApiId, "unknown stream");
        byte[] bytes;
        try
        {
            bytes = await state.Channel.Reader.ReadAsync(CancellationToken.None);
        }
        catch (ChannelClosedException closed)
        {
            lock (streams) streams.Remove(streamId);
            streamWindow.Reset(streamId);
            state.Cts.Cancel();
            // ReadAsync wraps writer.TryComplete(error) in ChannelClosedException; only an empty one is a normal end
            // (J02: a dropped vendor stream must fail, not read as done; S09: a rejected piece must stay a denial).
            return closed.InnerException switch
            {
                null => Allow(call.ApiId, """{"done":true}"""),
                BrokerDenyException deny => Deny(call.ApiId, deny.Message, deny.Kind),
                var error => Deny(call.ApiId, error.Message, "network"),
            };
        }
        catch (BrokerDenyException deny) // writer.TryComplete(error): PumpStreamAsync rejected a piece
        {
            lock (streams) streams.Remove(streamId);
            streamWindow.Reset(streamId);
            state.Cts.Cancel();
            return Deny(call.ApiId, deny.Message, deny.Kind);
        }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            lock (streams) streams.Remove(streamId);
            streamWindow.Reset(streamId);
            state.Cts.Cancel();
            return Deny(call.ApiId, error.Message, "network");
        }
        streamWindow.Ack(streamId, bytes.Length); // one read = one piece = one ack; frees the pump to buffer its next piece
        string text = System.Text.Encoding.UTF8.GetString(bytes);
        if (CredentialLeakScanner.ContainsKnownForm(text, state.ResolvedSecrets)) // S10: same check, applied per streamed piece
        {
            lock (streams) streams.Remove(streamId);
            streamWindow.Reset(streamId);
            state.Cts.Cancel();
            return Deny(call.ApiId, "response echoed a known credential form", "bad_response");
        }
        var node = new JsonObject { ["done"] = false, ["text"] = text };
        return Allow(call.ApiId, node.ToJsonString());
    }

    private async Task<ApiResultPayload> StreamCloseAsync(GrantInfo grant, ApiCallPayload call)
    {
        if (!call.Args.TryGetProperty("streamId", out var idElement) || idElement.ValueKind != JsonValueKind.String) return Deny(call.ApiId, "streamId required");
        string streamId = idElement.GetString()!;
        StreamState? state;
        lock (streams) { if (streams.TryGetValue(streamId, out state) && state.CallKey == $"{grant.Grant}:{call.CallId}") streams.Remove(streamId); else state = null; }
        if (state is not null) { state.Cts.Cancel(); streamWindow.Reset(streamId); }
        return Allow(call.ApiId, """{"closed":true}""");
    }

    private static JsonObject HeadersNode(IReadOnlyDictionary<string, string> headers)
    {
        var node = new JsonObject();
        foreach (var (name, value) in headers) node[name] = value;
        return node;
    }

    /// <summary>Turns a completed response into the plugin-visible <c>$http</c> result shape (PLAN 4.5).
    /// Every extracted/raw binary is written into a fresh <see cref="FileLease"/> here, never inlined as
    /// bytes into the JSON that crosses IPC to the plugin (PLAN "大二进制永不进 JS").</summary>
    private string BuildResultJson(BrokerHttpResponse response)
    {
        var result = new JsonObject { ["status"] = response.Status, ["headers"] = HeadersNode(response.Headers) };
        if (response.Truncated) result["truncated"] = true;
        if (response.RedirectUrl is not null) result["redirectUrl"] = response.RedirectUrl;
        if (response.RawFile is not null)
            result["body"] = FileHandleNode(response.RawFile, response.RawFileMime ?? "application/octet-stream");
        else if (response.JsonBody is not null) result["body"] = response.JsonBody.DeepClone();
        else if (response.TextBody is not null) result["body"] = response.TextBody;
        else result["body"] = null;
        if (response.Files.Count > 0)
        {
            var files = new JsonObject();
            foreach (var file in response.Files) files[file.Name] = FileHandleNode(file.Bytes, file.Mime);
            result["files"] = files;
        }
        return result.ToJsonString();
    }

    private JsonObject FileHandleNode(byte[] bytes, string mime)
    {
        if (leases is null) throw new BrokerDenyException("no file lease store is configured for this host");
        var lease = leases.Create("http-response", ExtensionFor(mime));
        File.WriteAllBytes(leases.PathOf(lease), bytes);
        return new JsonObject { ["id"] = lease.Id, ["mime"] = mime, ["bytes"] = bytes.LongLength };
    }

    private static string ExtensionFor(string mime) => mime switch
    {
        "audio/mpeg" => "mp3",
        "audio/wav" or "audio/x-wav" or "audio/wave" => "wav",
        "audio/ogg" => "ogg",
        "image/png" => "png",
        "image/jpeg" => "jpg",
        _ => "bin",
    };

    // ---- Request parsing (PLAN 4.5/4.5.1/4.5.2/4.5.3) ----

    private sealed class BrokerDenyException(string message, string kind = "bad_response") : Exception(message) { public string Kind { get; } = kind; }

    private BrokerHttpRequest BuildRequest(GrantInfo grant, JsonElement args, List<(string Handle, FileLease Lease)> takenLeases, List<string> resolvedSecretValues)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new BrokerDenyException("request must be an object");
        foreach (var property in args.EnumerateObject())
            if (!allowedFields.Contains(property.Name)) throw new BrokerDenyException($"field '{property.Name}' is not part of the request contract");
        string method = args.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "GET";
        if (method is not ("GET" or "POST" or "PUT" or "DELETE" or "PATCH")) throw new BrokerDenyException("method not allowed");
        if (!args.TryGetProperty("url", out var u) || u.ValueKind != JsonValueKind.String || !Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri))
            throw new BrokerDenyException("absolute URL required");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new BrokerDenyException("URL userinfo rejected");
        string? origin = Origin(uri);
        if (origin is null) throw new BrokerDenyException($"scheme '{uri.Scheme}' not allowed");
        if (!grant.Origins.Contains(origin)) throw new BrokerDenyException($"origin {origin} not granted");

        var headers = new List<KeyValuePair<string, string>>();
        if (args.TryGetProperty("headers", out var headersElement) && headersElement.ValueKind == JsonValueKind.Object)
            foreach (var h in headersElement.EnumerateObject())
            {
                if (forbiddenHeaders.Contains(h.Name) || h.Value.ValueKind != JsonValueKind.String || h.Value.GetString()!.AsSpan().IndexOfAny('\r', '\n') >= 0 || h.Name.AsSpan().IndexOfAny('\r', '\n') >= 0)
                    throw new BrokerDenyException($"header '{h.Name}' rejected");
                headers.Add(new(h.Name, h.Value.GetString()!));
            }
        if (args.TryGetProperty("query", out var queryElement) && queryElement.ValueKind == JsonValueKind.Object)
        {
            var builder = new UriBuilder(uri);
            var pairs = string.IsNullOrEmpty(builder.Query) ? [] : builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).ToList();
            foreach (var q in queryElement.EnumerateObject()) pairs.Add($"{Uri.EscapeDataString(q.Name)}={Uri.EscapeDataString(q.Value.GetString() ?? "")}");
            builder.Query = string.Join('&', pairs);
            uri = builder.Uri;
        }

        var (body, bodyHandles) = ParseBody(args, takenLeases);
        var bodyFiles = ParseBodyFiles(args, grant, takenLeases);
        var credentials = ParseCredentials(args, grant);
        var sign = ParseSign(args, grant);
        var (responseType, responseFiles) = ParseResponse(args);
        string? errorPointer = args.TryGetProperty("errorPointer", out var ep) && ep.ValueKind == JsonValueKind.String ? ep.GetString() : null;

        foreach (string handle in bodyHandles) if (!grant.Handles.Contains(handle)) throw new BrokerDenyException("file handle not granted to this call");

        return new BrokerHttpRequest(method, uri, headers, body, bodyFiles, credentials, (spec, name) => ResolveSecret(grant, spec, name, origin, resolvedSecretValues), sign, responseType, responseFiles, errorPointer, LocalOriginApproved: true);
    }

    private string ResolveSecret(GrantInfo grant, CredentialSpec spec, string secretName, string origin, List<string> resolvedSecretValues)
    {
        if (!grant.Secrets.Contains(secretName)) throw new BrokerDenyException($"secret '{secretName}' is not bound to this plugin/account");
        // S02: when a real account-binding model is wired (AccountAuthorization - see its class comment
        // for exactly what F07 must add), the plugin's declared identity/origin must match a user-confirmed
        // grant for this exact secret/origin/use; a changed package identity or origin never inherits an
        // existing account. Without one wired, secrets stay namespaced by plugin id only (documented on
        // Broker itself) - the pre-F07 simplification, not a silent bypass of this check when it exists.
        string accountId = grant.PluginId;
        if (accounts is not null)
        {
            var (decision, resolvedAccountId) = accounts.Authorize(grant.InstanceId, grant.PluginId, grant.Signer, secretName, origin, spec.Use);
            if (decision != CredentialDecision.Allowed || resolvedAccountId is null)
                throw new BrokerDenyException($"credential '{secretName}' not authorized for this identity/origin: {decision}", "auth");
            accountId = resolvedAccountId;
        }
        if (secretStore is null || !secretStore.TryRead(accountId, secretName, out string value)) throw new BrokerDenyException($"secret '{secretName}' is not configured");
        resolvedSecretValues.Add(value); // S10: known-form leak scan checks the response against every secret actually used
        return value;
    }

    private (RequestBody Body, IReadOnlyList<string> Handles) ParseBody(JsonElement args, List<(string Handle, FileLease Lease)> takenLeases)
    {
        if (!args.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object) return (RequestBody.None, []);
        string kind = body.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
        switch (kind)
        {
            case "json":
                var value = body.TryGetProperty("value", out var v) ? JsonNode.Parse(v.GetRawText()) : throw new BrokerDenyException("json body requires a value");
                return (new RequestBody(BodyKind.Json, Json: value), []);
            case "text":
                return (new RequestBody(BodyKind.Text, Text: body.TryGetProperty("value", out var t) ? t.GetString() ?? "" : ""), []);
            case "file":
            {
                string handle = body.TryGetProperty("file", out var f) ? f.GetString() ?? "" : throw new BrokerDenyException("file body requires a handle");
                byte[] bytes = ReadHandle(handle, takenLeases, out string mime);
                return (new RequestBody(BodyKind.File, FileBytes: bytes, FileContentType: mime), [handle]);
            }
            case "multipart":
            {
                if (!body.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array) throw new BrokerDenyException("multipart body requires fields");
                var specs = new List<MultipartFieldSpec>();
                var handles = new List<string>();
                foreach (var field in fields.EnumerateArray())
                {
                    string name = field.GetProperty("name").GetString() ?? throw new BrokerDenyException("multipart field name required");
                    string? filename = field.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
                    string? contentType = field.TryGetProperty("contentType", out var ct) ? ct.GetString() : null;
                    if (field.TryGetProperty("file", out var fileHandleEl) && fileHandleEl.ValueKind == JsonValueKind.String)
                    {
                        string handle = fileHandleEl.GetString()!;
                        byte[] bytes = ReadHandle(handle, takenLeases, out string mime);
                        specs.Add(new MultipartFieldSpec(name, filename, contentType ?? mime, null, bytes));
                        handles.Add(handle);
                    }
                    else
                    {
                        string text = field.TryGetProperty("text", out var txt) ? txt.GetString() ?? "" : "";
                        specs.Add(new MultipartFieldSpec(name, filename, contentType, text, null));
                    }
                }
                return (new RequestBody(BodyKind.Multipart, MultipartFields: specs), handles);
            }
            default: throw new BrokerDenyException($"unknown body kind '{kind}'");
        }
    }

    private IReadOnlyList<BodyFileInsertion> ParseBodyFiles(JsonElement args, GrantInfo grant, List<(string Handle, FileLease Lease)> takenLeases)
    {
        if (!args.TryGetProperty("bodyFiles", out var files) || files.ValueKind != JsonValueKind.Array) return [];
        var result = new List<BodyFileInsertion>();
        foreach (var file in files.EnumerateArray())
        {
            string pointer = file.GetProperty("pointer").GetString() ?? throw new BrokerDenyException("bodyFiles pointer required");
            string handle = file.GetProperty("file").GetString() ?? throw new BrokerDenyException("bodyFiles handle required");
            if (!grant.Handles.Contains(handle)) throw new BrokerDenyException("file handle not granted to this call");
            byte[] bytes = ReadHandle(handle, takenLeases, out _);
            result.Add(new BodyFileInsertion(pointer, bytes));
        }
        return result;
    }

    private byte[] ReadHandle(string handleId, List<(string Handle, FileLease Lease)> takenLeases, out string mime)
    {
        if (leases is null) throw new BrokerDenyException("no file lease store is configured for this host");
        var lease = leases.AddReference(handleId) ?? throw new BrokerDenyException("unknown or expired file handle");
        takenLeases.Add((handleId, lease));
        string path = leases.PathOf(lease);
        var info = new FileInfo(path);
        if (!info.Exists) throw new BrokerDenyException("file handle points at a missing file");
        if (info.Length > ProtocolLimits.MaxBinaryBytes) throw new BrokerDenyException("input file exceeds the binary size limit");
        mime = "application/octet-stream";
        return File.ReadAllBytes(path);
    }

    private void ReleaseAll(List<(string Handle, FileLease Lease)> takenLeases)
    {
        if (leases is null) return;
        foreach (var (_, lease) in takenLeases) leases.Release(lease);
    }

    private List<CredentialSpec> ParseCredentials(JsonElement args, GrantInfo grant)
    {
        if (!args.TryGetProperty("credentials", out var credentials)) return [];
        if (credentials.ValueKind != JsonValueKind.Array) throw new BrokerDenyException("credentials must be an array");
        var result = new List<CredentialSpec>();
        foreach (var credential in credentials.EnumerateArray())
        {
            var (area, target) = ParseTarget(credential.GetProperty("target"));
            if (!credential.TryGetProperty("parts", out var partsElement) || partsElement.ValueKind != JsonValueKind.Array) throw new BrokerDenyException("credential parts required");
            var parts = partsElement.EnumerateArray().Select(ParsePart).ToList();
            foreach (var part in parts)
                if (part.Secret is not null && !grant.Secrets.Contains(part.Secret))
                    throw new BrokerDenyException($"secret '{part.Secret}' is not bound to this plugin/account");
            result.Add(new CredentialSpec(area, target, parts));
        }
        return result;
    }

    private static (CredentialArea Area, string Target) ParseTarget(JsonElement target)
    {
        string area = target.GetProperty("area").GetString() ?? throw new BrokerDenyException("target area required");
        var parsed = area switch { "header" => CredentialArea.Header, "query" => CredentialArea.Query, "json" => CredentialArea.Json, _ => throw new BrokerDenyException($"unknown target area '{area}'") };
        string name = parsed == CredentialArea.Json
            ? target.GetProperty("pointer").GetString() ?? throw new BrokerDenyException("json target requires a pointer")
            : target.GetProperty("name").GetString() ?? throw new BrokerDenyException("header/query target requires a name");
        return (parsed, name);
    }

    private static CredentialPart ParsePart(JsonElement part)
    {
        if (part.TryGetProperty("literal", out var lit)) return CredentialPart.Text(lit.GetString() ?? "");
        if (part.TryGetProperty("secret", out var sec)) return CredentialPart.Ref(sec.GetString() ?? "");
        throw new BrokerDenyException("credential part must be literal or secret");
    }

    private static SignSpec? ParseSign(JsonElement args, GrantInfo grant)
    {
        if (!args.TryGetProperty("sign", out var sign) || sign.ValueKind != JsonValueKind.Object) return null;
        string scheme = sign.GetProperty("scheme").GetString() ?? throw new BrokerDenyException("sign.scheme required");
        if (scheme is "digest" or "hmac")
        {
            string alg = sign.GetProperty("alg").GetString() ?? throw new BrokerDenyException("sign.alg required");
            if (!sign.TryGetProperty("input", out var inputElement) || inputElement.ValueKind != JsonValueKind.Array) throw new BrokerDenyException("sign.input required");
            var input = inputElement.EnumerateArray().Select(ParsePart).ToList();
            var (area, target) = ParseTarget(sign.GetProperty("into"));
            string encoding = sign.TryGetProperty("encoding", out var e) ? e.GetString() ?? "hex" : "hex";
            string? hmacKey = scheme == "hmac" ? sign.GetProperty("key").GetString() ?? throw new BrokerDenyException("hmac requires a key secret name") : null;
            foreach (var p in input) if (p.Secret is not null && !grant.Secrets.Contains(p.Secret)) throw new BrokerDenyException($"secret '{p.Secret}' is not bound to this plugin/account");
            if (hmacKey is not null && !grant.Secrets.Contains(hmacKey)) throw new BrokerDenyException($"secret '{hmacKey}' is not bound to this plugin/account");
            return new SignSpec(new PrimitiveSign(scheme, alg, input, area, target, encoding, hmacKey), null);
        }
        if (scheme is "tencent-tc3" or "aws-sigv4")
        {
            string service = sign.GetProperty("service").GetString() ?? throw new BrokerDenyException("sign.service required");
            string? region = sign.TryGetProperty("region", out var r) ? r.GetString() : null;
            if (scheme == "aws-sigv4" && region is null) throw new BrokerDenyException("aws-sigv4 requires a region");
            // Fixed local secret names per signer scheme (PLAN 4.5.3): the plugin cannot pick different
            // names per call, they are bound by the account's credential setup.
            (string idSecret, string keySecret) = scheme == "tencent-tc3" ? ("secretId", "secretKey") : ("accessKeyId", "secretAccessKey");
            if (!grant.Secrets.Contains(idSecret) || !grant.Secrets.Contains(keySecret)) throw new BrokerDenyException($"'{idSecret}'/'{keySecret}' are not bound to this plugin/account");
            return new SignSpec(null, new NamedSign(scheme, service, region, idSecret, keySecret));
        }
        throw new BrokerDenyException($"unknown sign scheme '{scheme}'");
    }

    private static (ResponseKind Kind, IReadOnlyList<ResponseFileSpec> Files) ParseResponse(JsonElement args)
    {
        string type = args.TryGetProperty("responseType", out var rt) ? rt.GetString() ?? "json" : "json";
        var kind = type switch { "json" => ResponseKind.Json, "text" => ResponseKind.Text, "file" => ResponseKind.File, _ => throw new BrokerDenyException($"unknown responseType '{type}'") };
        var files = new List<ResponseFileSpec>();
        if (args.TryGetProperty("responseFiles", out var responseFiles) && responseFiles.ValueKind == JsonValueKind.Array)
            foreach (var file in responseFiles.EnumerateArray())
                files.Add(new ResponseFileSpec(file.GetProperty("name").GetString() ?? throw new BrokerDenyException("responseFiles name required"),
                    file.GetProperty("pointer").GetString() ?? throw new BrokerDenyException("responseFiles pointer required"),
                    file.TryGetProperty("mime", out var mime) ? mime.GetString() ?? "application/octet-stream" : "application/octet-stream"));
        return (kind, files);
    }

    /// <summary>Exact origin (scheme://host:port), https or http only.</summary>
    public static string? Origin(Uri uri)
        => uri.Scheme is "https" or "http" && uri.IsAbsoluteUri ? $"{uri.Scheme}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}" : null;

    private static ApiResultPayload Deny(int apiId, string reason, string kind = "bad_response")
        => new(apiId, false, JsonSerializer.SerializeToElement(new DenyValue(kind, $"denied: {reason}"), BrokerJson.Default.DenyValue));

    private static ApiResultPayload Allow(int apiId, string json)
    {
        using var document = JsonDocument.Parse(json);
        return new(apiId, true, document.RootElement.Clone());
    }

    public void Dispose() => ownedNetwork?.Dispose();
}

internal sealed record DenyValue(string Kind, string Detail);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DenyValue))]
internal partial class BrokerJson : JsonSerializerContext;
