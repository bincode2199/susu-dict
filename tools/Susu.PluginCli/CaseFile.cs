using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Susu.PluginCli;

internal sealed class CaseFileException(string message) : Exception(message);

internal sealed record InputFile(byte[] Bytes, string Mime, string Extension, double? DurationMs, int? Width, int? Height);
internal sealed record VendorReply(int Status, byte[] Body, string ContentType, Dictionary<string, string> Headers, int DelayMs);
internal sealed record RequestExpect(string? Method, string? Path, string? PathContains, string? BodyContains, JsonNode? BodyJson, Dictionary<string, string> HeaderContains);

internal sealed record TestCase(
    string Name, string? Capability, JsonNode Request, JsonObject Config, Dictionary<string, string> Secrets,
    Dictionary<string, InputFile> Inputs, List<VendorReply> Vendor, JsonNode? ExpectResult, string? ExpectError,
    string? ExpectDetailContains, List<RequestExpect>? ExpectRequests, int TimeoutMs);

/// <summary>
/// The `susu-plugin.test.json` format (documented in docs/development/PLUGIN-AUTHOR-GUIDE.md): a list of cases, each with an input
/// request, the canned vendor answers and the expected result or error class. Parsing is strict: an unknown key, a wrong type or a
/// missing field is a <see cref="CaseFileException"/> naming the JSON path, so a typo never turns into a silently weaker test.
/// </summary>
internal static class CaseFile
{
    public const string DefaultName = "susu-plugin.test.json";
    public const int DefaultTimeoutMs = 10_000;
    private static readonly string[] caseKeys = ["name", "capability", "request", "config", "secrets", "inputs", "vendor", "expect", "expectRequests", "timeoutMs"];
    private static readonly string[] expectKeys = ["result", "error", "detailContains"];
    private static readonly string[] replyKeys = ["status", "json", "text", "base64", "contentType", "headers", "delayMs"];
    private static readonly string[] inputKeys = ["text", "base64", "mime", "extension", "durationMs", "width", "height"];
    private static readonly string[] requestKeys = ["method", "path", "pathContains", "bodyContains", "bodyJson", "headerContains"];
    private static readonly string[] errorKinds = ["auth", "quota", "rate_limited", "network", "timeout", "unsupported_language", "bad_response", "cancelled", "busy", "unavailable"];

    public static List<TestCase> Load(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException e) { throw new CaseFileException($"cannot read {path}: {e.Message}"); }
        return Parse(text);
    }

    public static List<TestCase> Parse(string text)
    {
        try { return ParseCore(text); }
        catch (ArgumentException e) { throw new CaseFileException("$: duplicate or invalid key: " + e.Message.Split('\n')[0]); } // System.Text.Json reports a duplicate key only when the object is first enumerated
    }

    private static List<TestCase> ParseCore(string text)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e) { throw new CaseFileException($"not valid JSON: {e.Message}"); }
        if (root is not JsonObject top) throw new CaseFileException("$: the file must be a JSON object with a \"cases\" array");
        foreach (var key in top.Select(p => p.Key))
            if (key is not ("version" or "cases")) throw new CaseFileException($"$.{key}: unknown key (allowed: version, cases)");
        if (top["version"] is { } v && !(v is JsonValue vv && vv.TryGetValue<int>(out int ver) && ver == 1)) throw new CaseFileException("$.version: only version 1 exists");
        if (top["cases"] is not JsonArray cases) throw new CaseFileException("$.cases: required array");
        if (cases.Count == 0) throw new CaseFileException("$.cases: at least one case is required");
        var result = new List<TestCase>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < cases.Count; i++)
        {
            var parsed = ParseCase(cases[i], $"$.cases[{i}]");
            if (!names.Add(parsed.Name)) throw new CaseFileException($"$.cases[{i}].name: duplicate case name '{parsed.Name}'");
            result.Add(parsed);
        }
        return result;
    }

    private static TestCase ParseCase(JsonNode? node, string at)
    {
        if (node is not JsonObject o) throw new CaseFileException($"{at}: a case must be an object");
        Reject(o, caseKeys, at);
        string name = Str(o, "name", at, required: true)!;
        string? capability = Str(o, "capability", at);
        if (o["request"] is null) throw new CaseFileException($"{at}.request: required");
        var request = o["request"]!.DeepClone();
        var config = o["config"] is null ? new JsonObject() : o["config"] as JsonObject ?? throw new CaseFileException($"{at}.config: must be an object");
        config = (JsonObject)config.DeepClone();

        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (o["secrets"] is { } s)
        {
            if (s is not JsonObject so) throw new CaseFileException($"{at}.secrets: must be an object of name: test value");
            foreach (var (k, val) in so)
                secrets[k] = val is JsonValue jv && jv.TryGetValue<string>(out string? sv) ? sv : throw new CaseFileException($"{at}.secrets.{k}: must be a string");
        }

        var inputs = new Dictionary<string, InputFile>(StringComparer.Ordinal);
        if (o["inputs"] is { } inn)
        {
            if (inn is not JsonObject io) throw new CaseFileException($"{at}.inputs: must be an object of name: file");
            foreach (var (k, val) in io) inputs[k] = ParseInput(val, $"{at}.inputs.{k}");
        }

        var vendor = new List<VendorReply>();
        if (o["vendor"] is { } vn)
        {
            if (vn is not JsonArray va) throw new CaseFileException($"{at}.vendor: must be an array of replies");
            for (int i = 0; i < va.Count; i++) vendor.Add(ParseReply(va[i], $"{at}.vendor[{i}]"));
        }

        if (o["expect"] is not JsonObject ex) throw new CaseFileException($"{at}.expect: required object with \"result\" or \"error\"");
        Reject(ex, expectKeys, $"{at}.expect");
        bool hasResult = ex.ContainsKey("result"), hasError = ex.ContainsKey("error");
        if (hasResult == hasError) throw new CaseFileException($"{at}.expect: give exactly one of \"result\" and \"error\"");
        string? error = Str(ex, "error", $"{at}.expect");
        if (error is not null && !errorKinds.Contains(error)) throw new CaseFileException($"{at}.expect.error: '{error}' is not an error class ({string.Join(", ", errorKinds)})");
        string? detail = Str(ex, "detailContains", $"{at}.expect");
        if (detail is not null && !hasError) throw new CaseFileException($"{at}.expect.detailContains: only valid with \"error\"");

        List<RequestExpect>? expectRequests = null;
        if (o["expectRequests"] is { } er)
        {
            if (er is not JsonArray ea) throw new CaseFileException($"{at}.expectRequests: must be an array");
            expectRequests = [];
            for (int i = 0; i < ea.Count; i++) expectRequests.Add(ParseRequestExpect(ea[i], $"{at}.expectRequests[{i}]"));
        }

        int timeout = DefaultTimeoutMs;
        if (o["timeoutMs"] is { } t)
        {
            if (!(t is JsonValue tv && tv.TryGetValue<int>(out timeout) && timeout is >= 100 and <= 120_000)) throw new CaseFileException($"{at}.timeoutMs: an integer from 100 to 120000");
        }
        return new TestCase(name, capability, request, config, secrets, inputs, vendor, hasResult ? ex["result"]?.DeepClone() ?? JsonValue.Create((string?)null) : null, error, detail, expectRequests, timeout);
    }

    private static InputFile ParseInput(JsonNode? node, string at)
    {
        if (node is not JsonObject o) throw new CaseFileException($"{at}: must be an object");
        Reject(o, inputKeys, at);
        string? text = Str(o, "text", at), b64 = Str(o, "base64", at);
        if ((text is null) == (b64 is null)) throw new CaseFileException($"{at}: give exactly one of \"text\" and \"base64\"");
        byte[] bytes;
        try { bytes = text is not null ? Encoding.UTF8.GetBytes(text) : Convert.FromBase64String(b64!); }
        catch (FormatException) { throw new CaseFileException($"{at}.base64: not valid base64"); }
        string mime = Str(o, "mime", at) ?? "application/octet-stream";
        string ext = (Str(o, "extension", at) ?? "bin").TrimStart('.');
        if (ext.Length == 0 || ext.Length > 8 || !ext.All(char.IsAsciiLetterOrDigit)) throw new CaseFileException($"{at}.extension: letters and digits only, up to 8");
        return new InputFile(bytes, mime, ext, Num(o, "durationMs", at), (int?)Num(o, "width", at), (int?)Num(o, "height", at));
    }

    private static VendorReply ParseReply(JsonNode? node, string at)
    {
        if (node is not JsonObject o) throw new CaseFileException($"{at}: must be an object");
        Reject(o, replyKeys, at);
        int status = 200;
        if (o["status"] is { } st && !(st is JsonValue sv && sv.TryGetValue(out status) && status is >= 100 and <= 599)) throw new CaseFileException($"{at}.status: an HTTP status from 100 to 599");
        int bodies = new[] { "json", "text", "base64" }.Count(o.ContainsKey);
        if (bodies > 1) throw new CaseFileException($"{at}: give at most one of json, text, base64");
        byte[] body = [];
        string contentType = "application/json";
        if (o.ContainsKey("json")) body = Encoding.UTF8.GetBytes(o["json"]?.ToJsonString() ?? "null");
        else if (o.ContainsKey("text")) { body = Encoding.UTF8.GetBytes(Str(o, "text", at) ?? ""); contentType = "text/plain; charset=utf-8"; }
        else if (o.ContainsKey("base64"))
        {
            try { body = Convert.FromBase64String(Str(o, "base64", at) ?? ""); }
            catch (FormatException) { throw new CaseFileException($"{at}.base64: not valid base64"); }
            contentType = "application/octet-stream";
        }
        contentType = Str(o, "contentType", at) ?? contentType;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (o["headers"] is { } h)
        {
            if (h is not JsonObject ho) throw new CaseFileException($"{at}.headers: must be an object");
            foreach (var (k, v) in ho) headers[k] = v is JsonValue jv && jv.TryGetValue<string>(out string? hv) ? hv : throw new CaseFileException($"{at}.headers.{k}: must be a string");
        }
        int delay = 0;
        if (o["delayMs"] is { } d && !(d is JsonValue dv && dv.TryGetValue(out delay) && delay is >= 0 and <= 120_000)) throw new CaseFileException($"{at}.delayMs: an integer from 0 to 120000");
        return new VendorReply(status, body, contentType, headers, delay);
    }

    private static RequestExpect ParseRequestExpect(JsonNode? node, string at)
    {
        if (node is not JsonObject o) throw new CaseFileException($"{at}: must be an object");
        Reject(o, requestKeys, at);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (o["headerContains"] is { } h)
        {
            if (h is not JsonObject ho) throw new CaseFileException($"{at}.headerContains: must be an object");
            foreach (var (k, v) in ho) headers[k] = v is JsonValue jv && jv.TryGetValue<string>(out string? sv) ? sv : throw new CaseFileException($"{at}.headerContains.{k}: must be a string");
        }
        return new RequestExpect(Str(o, "method", at)?.ToUpperInvariant(), Str(o, "path", at), Str(o, "pathContains", at), Str(o, "bodyContains", at), o["bodyJson"]?.DeepClone(), headers);
    }

    private static void Reject(JsonObject o, string[] allowed, string at)
    {
        foreach (var key in o.Select(p => p.Key))
            if (!allowed.Contains(key)) throw new CaseFileException($"{at}.{key}: unknown key (allowed: {string.Join(", ", allowed)})");
    }

    private static string? Str(JsonObject o, string key, string at, bool required = false)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is null)
            return required ? throw new CaseFileException($"{at}.{key}: required string") : null;
        return n is JsonValue v && v.TryGetValue<string>(out string? s) ? s : throw new CaseFileException($"{at}.{key}: must be a string");
    }

    private static double? Num(JsonObject o, string key, string at)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is null) return null;
        return n is JsonValue v && v.TryGetValue<double>(out double d) ? d : throw new CaseFileException($"{at}.{key}: must be a number");
    }

    // ---- result / request matching ----

    /// <summary>Subset match: every key of <paramref name="expected"/> must be present and match; arrays match element by element
    /// and by length; "$any" matches any present value. Returns null on a match, otherwise a one-line explanation with the JSON path.</summary>
    public static string? Match(JsonNode? expected, JsonNode? actual, string path = "result")
    {
        if (expected is JsonValue ev && ev.TryGetValue<string>(out string? es) && es == "$any")
            return actual is null ? $"{path}: expected a value, found none" : null;
        switch (expected)
        {
            case null:
                return actual is null ? null : $"{path}: expected null, got {Short(actual)}";
            case JsonObject eo:
                if (actual is not JsonObject ao) return $"{path}: expected an object, got {Short(actual)}";
                foreach (var (key, value) in eo)
                {
                    if (!ao.TryGetPropertyValue(key, out var av)) return $"{path}.{key}: missing (got {Short(actual)})";
                    if (Match(value, av, $"{path}.{key}") is { } mismatch) return mismatch;
                }
                return null;
            case JsonArray ea:
                if (actual is not JsonArray aa) return $"{path}: expected an array, got {Short(actual)}";
                if (ea.Count != aa.Count) return $"{path}: expected {ea.Count} items, got {aa.Count}";
                for (int i = 0; i < ea.Count; i++)
                    if (Match(ea[i], aa[i], $"{path}[{i}]") is { } mismatch) return mismatch;
                return null;
            default:
                if (actual is not JsonValue) return $"{path}: expected {Short(expected)}, got {Short(actual)}";
                if (expected is JsonValue x && actual is JsonValue y && x.TryGetValue<double>(out double dx) && y.TryGetValue<double>(out double dy))
                    return Math.Abs(dx - dy) < 1e-9 ? null : $"{path}: expected {Short(expected)}, got {Short(actual)}";
                return expected.ToJsonString() == actual.ToJsonString() ? null : $"{path}: expected {Short(expected)}, got {Short(actual)}";
        }
    }

    /// <summary>Readable JSON for messages: non-ASCII text stays as text instead of \uXXXX escapes.</summary>
    private static string Render(JsonNode? node)
    {
        if (node is null) return "null";
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            node.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string Short(JsonNode? node)
    {
        string s = Render(node);
        return s.Length <= 160 ? s : s[..157] + "...";
    }
}
