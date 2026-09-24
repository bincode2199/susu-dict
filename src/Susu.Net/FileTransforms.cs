using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Susu.Net;

public sealed class FileTransformException(string reason) : Exception(reason);

/// <summary>
/// JSON Pointer (RFC 6901) helpers for the JSON/Base64 request and response transforms (PLAN 4.5.1):
/// a target must already exist and hold JSON <c>null</c> - the host never creates a path, and never
/// writes anywhere the plugin did not already reserve. Used for both request-side insertion
/// (<c>bodyFiles</c>) and response-side extraction (<c>responseFiles</c>).
/// </summary>
public static class JsonPointerOps
{
    /// <summary>Two pointers are duplicate or overlapping when one is a prefix of the other's token path
    /// (including being equal) - PLAN 4.5.1 forbids both, so one binary blob can never land under another
    /// or the same slot twice.</summary>
    public static bool AnyDuplicateOrOverlap(IReadOnlyList<string> pointers)
    {
        for (int i = 0; i < pointers.Count; i++)
            for (int j = i + 1; j < pointers.Count; j++)
                if (IsPrefixOrEqual(Tokens(pointers[i]), Tokens(pointers[j])) || IsPrefixOrEqual(Tokens(pointers[j]), Tokens(pointers[i])))
                    return true;
        return false;
    }

    private static bool IsPrefixOrEqual(string[] a, string[] b)
    {
        if (a.Length > b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>Writes <paramref name="value"/> into the pointer's slot; the slot must exist and be
    /// JSON null (an array element already present as null, or an object property present as null).</summary>
    public static void SetAtReservedNull(JsonNode root, string pointer, string value)
    {
        var (parent, key, index) = ResolveReservedNull(root, pointer);
        if (parent is JsonObject obj) obj[key!] = JsonValue.Create(value);
        else ((JsonArray)parent)[index] = JsonValue.Create(value);
    }

    /// <summary>Reads the string at the pointer and replaces it with JSON null in place (response
    /// extraction: "将该字段在 body 中置 null"). The slot must exist and hold a JSON string.</summary>
    public static string ExtractAndNull(JsonNode root, string pointer)
    {
        var (node, parent, key, index) = Resolve(root, pointer);
        if (node is not JsonValue value || !value.TryGetValue(out string? text) || text is null)
            throw new FileTransformException($"json target {pointer} is not a string");
        if (parent is JsonObject obj) obj[key!] = null;
        else if (parent is JsonArray arr) arr[index] = null;
        else throw new FileTransformException($"json target {pointer} has no addressable parent");
        return text;
    }

    private static (JsonNode Parent, string? Key, int Index) ResolveReservedNull(JsonNode root, string pointer)
    {
        var (node, parent, key, index) = Resolve(root, pointer);
        if (node is not null) throw new FileTransformException($"json target {pointer} must be reserved null");
        if (parent is null) throw new FileTransformException($"json target {pointer} does not exist");
        return (parent, key, index);
    }

    private static (JsonNode? Node, JsonNode? Parent, string? Key, int Index) Resolve(JsonNode root, string pointer)
    {
        if (!pointer.StartsWith('/')) throw new FileTransformException($"'{pointer}' is not a JSON pointer");
        var tokens = Tokens(pointer);
        JsonNode? parent = null;
        JsonNode? node = root;
        string? key = null;
        int index = -1;
        foreach (string token in tokens)
        {
            parent = node;
            switch (parent)
            {
                case JsonObject obj:
                    key = token; index = -1;
                    obj.TryGetPropertyValue(token, out node);
                    if (!obj.ContainsKey(token)) throw new FileTransformException($"json target does not exist at '{token}'");
                    break;
                case JsonArray arr when int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int i) && i < arr.Count:
                    key = null; index = i;
                    node = arr[i];
                    break;
                default:
                    throw new FileTransformException("json target path does not exist");
            }
        }
        return (node, parent, key, index);
    }

    private static string[] Tokens(string pointer)
        => pointer.Length <= 1 ? [] : pointer[1..].Split('/').Select(t => t.Replace("~1", "/").Replace("~0", "~")).ToArray();
}

public sealed record MultipartFieldSpec(string Name, string? Filename, string? ContentType, string? Text, byte[]? FileBytes);

/// <summary>Builds a <c>multipart/form-data</c> body from the plugin's field list (PLAN 4.5.1 "原始／multipart
/// 请求"). File bytes are already resolved and size-checked by the caller before this runs.</summary>
public static class MultipartBuilder
{
    public static MultipartFormDataContent Build(IReadOnlyList<MultipartFieldSpec> fields)
    {
        var content = new MultipartFormDataContent("susu-" + Guid.NewGuid().ToString("N"));
        foreach (var field in fields)
        {
            if (field.FileBytes is not null)
            {
                var part = new ByteArrayContent(field.FileBytes);
                if (field.ContentType is not null) part.Headers.ContentType = MediaTypeHeaderValue.Parse(field.ContentType);
                content.Add(part, field.Name, field.Filename ?? field.Name);
            }
            else
            {
                var part = new StringContent(field.Text ?? "");
                part.Headers.ContentType = field.ContentType is null ? null : MediaTypeHeaderValue.Parse(field.ContentType);
                content.Add(part, field.Name);
            }
        }
        return content;
    }
}
