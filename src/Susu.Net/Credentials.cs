using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Susu.Net;

/// <summary>One piece of a credential value: a literal string or a reference to a local secret name.</summary>
public sealed record CredentialPart(string? Literal = null, string? Secret = null)
{
    public static CredentialPart Text(string literal) => new(Literal: literal);
    public static CredentialPart Ref(string secret) => new(Secret: secret);
}

public enum CredentialArea { Header, Query, Json }

/// <summary>The only way a plugin asks for a secret (PLAN 4.5.2): a separate control field, never text interpolation.</summary>
public sealed record CredentialSpec(CredentialArea Area, string Target, IReadOnlyList<CredentialPart> Parts, string? UseOverride = null)
{
    /// <summary>The grant "use" string this spec needs (matches <c>CredentialGrant.Use</c>); named signers use <c>signer:&lt;scheme&gt;</c>.</summary>
    public string Use => UseOverride ?? $"{Area.ToString().ToLowerInvariant()}:{Target}";
}

/// <summary>A request description before transport; the plugin never sees the filled form.</summary>
public sealed record RequestDescriptor(string Method, string Url, IReadOnlyList<KeyValuePair<string, string>> Headers, JsonNode? Body);

public sealed class CredentialRejectedException(string reason) : Exception(reason);

/// <summary>
/// Fills declared credential targets (PLAN 4.5.2). Header/query targets must already exist and be empty,
/// JSON targets must exist and be <c>null</c>; routing/transport headers can never be targets; header values
/// with CR/LF are rejected; query values go through one encoder and JSON through one serializer. Nothing
/// else in the request is inspected for secret syntax, so <c>{{secret.apiKey}}</c> or <c>{"secret":"x"}</c>
/// in ordinary data stays literal.
/// </summary>
public static class CredentialInjector
{
    private static readonly HashSet<string> forbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Proxy-Authorization", "Proxy-Connection", "Upgrade", "TE", "Trailer", "Keep-Alive",
    };

    /// <param name="resolve">Returns the secret value for a local secret name after authorization, or throws.</param>
    public static RequestDescriptor Apply(RequestDescriptor request, IReadOnlyList<CredentialSpec> credentials, Func<CredentialSpec, string, string> resolve)
    {
        var headers = request.Headers.ToList();
        var uri = new Uri(request.Url, UriKind.Absolute);
        var query = ParseQuery(uri.Query);
        JsonNode? body = request.Body?.DeepClone();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in credentials)
        {
            if (!seen.Add(spec.Use)) throw new CredentialRejectedException($"duplicate credential target {spec.Use}");
            if (spec.Parts.Count == 0 || spec.Parts.Any(p => (p.Literal is null) == (p.Secret is null))) throw new CredentialRejectedException("each part must be exactly one of literal or secret");
            var value = new StringBuilder();
            foreach (var part in spec.Parts) value.Append(part.Literal ?? resolve(spec, part.Secret!));
            string text = value.ToString();
            switch (spec.Area)
            {
                case CredentialArea.Header:
                    if (forbiddenHeaders.Contains(spec.Target)) throw new CredentialRejectedException($"header {spec.Target} cannot carry credentials");
                    if (text.AsSpan().IndexOfAny('\r', '\n') >= 0 || text.Contains('\0')) throw new CredentialRejectedException("CR/LF/NUL in header value");
                    int header = SingleEmpty(headers.Select(h => (h.Key, h.Value)).ToList(), spec.Target, StringComparer.OrdinalIgnoreCase, "header");
                    headers[header] = new(headers[header].Key, text);
                    break;
                case CredentialArea.Query:
                    int item = SingleEmpty(query, spec.Target, StringComparer.Ordinal, "query");
                    query[item] = (query[item].Key, text);
                    break;
                case CredentialArea.Json:
                    body = SetJsonNull(body, spec.Target, text);
                    break;
            }
        }
        var builder = new UriBuilder(uri) { Query = string.Join('&', query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}")) };
        return request with { Url = builder.Uri.AbsoluteUri, Headers = headers, Body = body };
    }

    private static int SingleEmpty(List<(string Key, string Value)> items, string name, StringComparer comparer, string area)
    {
        var matches = items.Select((x, i) => (x, i)).Where(x => comparer.Equals(x.x.Key, name)).ToList();
        if (matches.Count != 1) throw new CredentialRejectedException($"{area} target {name} must be declared exactly once");
        if (matches[0].x.Value.Length != 0) throw new CredentialRejectedException($"{area} target {name} must be reserved empty");
        return matches[0].i;
    }

    private static List<(string Key, string Value)> ParseQuery(string query)
        => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Select(kv => (Uri.UnescapeDataString(kv[0]), kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : ""))
            .ToList();

    /// <summary>RFC 6901 pointer; the target must exist and hold JSON null.</summary>
    private static JsonNode SetJsonNull(JsonNode? body, string pointer, string value)
    {
        if (body is null || !pointer.StartsWith('/')) throw new CredentialRejectedException($"json target {pointer} is not a JSON pointer into the body");
        var tokens = pointer[1..].Split('/').Select(t => t.Replace("~1", "/").Replace("~0", "~")).ToArray();
        JsonNode? parent = body;
        for (int i = 0; i < tokens.Length - 1; i++) parent = Child(parent, tokens[i]) ?? throw new CredentialRejectedException($"json target {pointer} does not exist");
        string last = tokens[^1];
        switch (parent)
        {
            case JsonObject obj when obj.TryGetPropertyValue(last, out var existing) && existing is null:
                obj[last] = JsonValue.Create(value);
                break;
            case JsonArray array when int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < array.Count && array[index] is null:
                array[index] = JsonValue.Create(value);
                break;
            default:
                throw new CredentialRejectedException($"json target {pointer} must exist and be reserved null");
        }
        return body;
    }

    private static JsonNode? Child(JsonNode? node, string token) => node switch
    {
        JsonObject obj => obj.TryGetPropertyValue(token, out var child) ? child : null,
        JsonArray array when int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < array.Count => array[index],
        _ => null,
    };
}
