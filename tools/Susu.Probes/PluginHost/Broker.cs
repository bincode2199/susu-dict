using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Susu.Probes.PluginHost;

/// <summary>
/// F00 prototype of the main-process authorization for plugin API calls (PLAN 4.5.4).
/// Grants are unguessable, bound to one request/plugin/call and revoked at completion;
/// the child's self-reported plugin id or paths are never trusted.
/// Network responses for non-local origins are a synthetic fixture: no real Internet request.
/// </summary>
internal sealed class Broker
{
    internal sealed class GrantInfo(string grant, string requestId, string pluginId, int callId, HashSet<string> origins, HashSet<string> secrets, HashSet<string> handles, DateTime expires)
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
    }

    private static readonly HashSet<string> allowedFields = ["method", "url", "headers", "query", "body", "responseType", "bodyFiles", "responseFiles", "credentials", "sign", "errorPointer"];
    private static readonly HashSet<string> forbiddenHeaders = new(StringComparer.OrdinalIgnoreCase) { "host", "content-length", "proxy-authorization", "transfer-encoding", "connection" };
    private readonly Dictionary<string, GrantInfo> grants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> stores = new(StringComparer.Ordinal);
    private readonly HashSet<string> approvedLocalOrigins = new(StringComparer.Ordinal);
    private readonly HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
    private int processInFlight;
    public List<string> Decisions { get; } = [];

    public void ApproveLocalOrigin(string origin) { lock (grants) approvedLocalOrigins.Add(origin); }

    public GrantInfo Issue(string requestId, string pluginId, int callId, IEnumerable<string> origins, IEnumerable<string>? secrets = null, IEnumerable<string>? handles = null)
    {
        var info = new GrantInfo(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), requestId, pluginId, callId,
            [.. origins.Select(o => Origin(new Uri(o)) ?? throw new ArgumentException($"Invalid origin {o}"))], [.. secrets ?? []], [.. handles ?? []], DateTime.UtcNow.AddMinutes(10));
        lock (grants) grants[info.Grant] = info;
        return info;
    }

    public void Revoke(string grant) { lock (grants) if (grants.Remove(grant, out var info)) info.Revoked = true; }

    public int ActiveGrants { get { lock (grants) return grants.Count; } }

    /// <summary>Validates and executes one ApiCall frame; returns the ApiResult payload.</summary>
    public async Task<ApiResultPayload> HandleAsync(Frame frame)
    {
        var call = frame.Payload?.Deserialize(PluginJson.Default.ApiCallPayload);
        if (call is null) return Deny(0, "malformed ApiCall");
        GrantInfo? grant;
        lock (grants) grants.TryGetValue(frame.Grant ?? "", out grant);
        if (grant is null || grant.Revoked) return Deny(call.ApiId, "unknown or revoked grant");
        if (grant.Expires < DateTime.UtcNow) return Deny(call.ApiId, "expired grant");
        if (grant.RequestId != frame.RequestId || grant.PluginId != frame.PluginId || grant.CallId != call.CallId) return Deny(call.ApiId, "grant not bound to this request/plugin/call");
        if (Interlocked.Increment(ref grant.InFlight) > 4) { Interlocked.Decrement(ref grant.InFlight); return Deny(call.ApiId, "per-call API concurrency exceeded"); }
        if (Interlocked.Increment(ref processInFlight) > 32) { Interlocked.Decrement(ref processInFlight); Interlocked.Decrement(ref grant.InFlight); return Deny(call.ApiId, "process API concurrency exceeded"); }
        try
        {
            return call.Op switch
            {
                "http" => await HttpAsync(grant, call),
                "store.get" or "store.set" => Store(grant, call),
                _ => Deny(call.ApiId, $"operation '{call.Op}' not provided"),
            };
        }
        finally { Interlocked.Decrement(ref processInFlight); Interlocked.Decrement(ref grant.InFlight); }
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

    private async Task<ApiResultPayload> HttpAsync(GrantInfo grant, ApiCallPayload call)
    {
        var args = call.Args;
        if (args.ValueKind != JsonValueKind.Object) return Deny(call.ApiId, "request must be an object");
        foreach (var property in args.EnumerateObject())
            if (!allowedFields.Contains(property.Name)) return Deny(call.ApiId, $"field '{property.Name}' is not part of the request contract");
        string method = args.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "GET";
        if (method is not ("GET" or "POST")) return Deny(call.ApiId, "method not allowed");
        if (!args.TryGetProperty("url", out var u) || u.ValueKind != JsonValueKind.String || !Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri))
            return Deny(call.ApiId, "absolute URL required");
        if (!string.IsNullOrEmpty(uri.UserInfo)) return Deny(call.ApiId, "URL userinfo rejected");
        string? origin = Origin(uri);
        if (origin is null) return Deny(call.ApiId, $"scheme '{uri.Scheme}' not allowed");
        bool local;
        lock (grants) local = approvedLocalOrigins.Contains(origin);
        if (uri.Scheme == "http" && !local) return Deny(call.ApiId, "plain HTTP only for an approved local origin");
        if (!grant.Origins.Contains(origin)) return Deny(call.ApiId, $"origin {origin} not granted");
        if (args.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
            foreach (var h in headers.EnumerateObject())
                if (forbiddenHeaders.Contains(h.Name) || h.Value.ValueKind != JsonValueKind.String || h.Value.GetString()!.AsSpan().IndexOfAny('\r', '\n') >= 0 || h.Name.AsSpan().IndexOfAny('\r', '\n') >= 0)
                    return Deny(call.ApiId, $"header '{h.Name}' rejected");
        if (args.TryGetProperty("credentials", out var credentials))
        {
            if (credentials.ValueKind != JsonValueKind.Array) return Deny(call.ApiId, "credentials must be an array");
            foreach (var credential in credentials.EnumerateArray())
            {
                if (!credential.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) return Deny(call.ApiId, "credential parts required");
                foreach (var part in parts.EnumerateArray())
                    if (part.TryGetProperty("secret", out var secret) && !grant.Secrets.Contains(secret.GetString() ?? ""))
                        return Deny(call.ApiId, $"secret '{secret.GetString()}' is not bound to this plugin/account");
            }
        }
        foreach (string handle in Handles(args))
            if (!grant.Handles.Contains(handle)) return Deny(call.ApiId, "file handle not granted to this call");
        if (local)
        {
            try
            {
                using var response = await http.GetAsync(uri);
                string text = await response.Content.ReadAsStringAsync();
                if (text.Length > 16 * 1024) text = text[..(16 * 1024)];
                lock (Decisions) Decisions.Add($"allow local {origin}");
                return Allow(call.ApiId, JsonSerializer.Serialize(new LocalHttpResult((int)response.StatusCode, text), BrokerJson.Default.LocalHttpResult));
            }
            catch (HttpRequestException error) { return Deny(call.ApiId, $"network: {error.HttpRequestError}", "network"); }
        }
        if (uri.AbsolutePath == "/slow") await Task.Delay(10000); // fixture for cancellation while awaiting the host
        lock (Decisions) Decisions.Add($"allow synthetic {origin}");
        string q = args.TryGetProperty("body", out var body) && body.TryGetProperty("value", out var value) && value.TryGetProperty("q", out var qv) ? qv.GetString() ?? "" : "";
        return Allow(call.ApiId, JsonSerializer.Serialize(new SyntheticHttpResult(200, new SyntheticBody($"[synthetic:{q.Length}]")), BrokerJson.Default.SyntheticHttpResult));
    }

    private static IEnumerable<string> Handles(JsonElement args)
    {
        if (args.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object && body.TryGetProperty("kind", out var kind) && kind.GetString() == "file")
            yield return body.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "";
        if (args.TryGetProperty("bodyFiles", out var files) && files.ValueKind == JsonValueKind.Array)
            foreach (var file in files.EnumerateArray()) yield return file.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "";
    }

    /// <summary>Exact origin (scheme://host:port), https or http only.</summary>
    public static string? Origin(Uri uri)
        => uri.Scheme is "https" or "http" && uri.IsAbsoluteUri ? $"{uri.Scheme}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}" : null;

    private ApiResultPayload Deny(int apiId, string reason, string kind = "bad_response")
    {
        lock (Decisions) Decisions.Add($"deny {reason}");
        return new ApiResultPayload(apiId, false, JsonSerializer.SerializeToElement(new DenyValue(kind, $"denied: {reason}"), BrokerJson.Default.DenyValue));
    }

    private static ApiResultPayload Allow(int apiId, string json) => new(apiId, true, Protocol.Element(json));
}

internal sealed record DenyValue(string Kind, string Detail);
internal sealed record SyntheticBody(string Text);
internal sealed record SyntheticHttpResult(int Status, SyntheticBody Body);
internal sealed record LocalHttpResult(int Status, string Body);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(DenyValue))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SyntheticHttpResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(LocalHttpResult))]
internal partial class BrokerJson : System.Text.Json.Serialization.JsonSerializerContext;
