using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Susu.Net;

/// <summary>
/// Host-side signing primitives (PLAN 4.5.3). These never return a derived key or the signature to
/// the plugin: callers write the computed value straight into a pre-reserved request target via
/// <see cref="CredentialInjector"/>-style injection. Two kinds exist: raw primitives (<see cref="DigestSigner"/>,
/// <see cref="HmacSigner"/>) that a plugin composes into its own scheme, and named multi-step schemes
/// (<see cref="TencentTc3Signer"/>, <see cref="AwsSigV4Signer"/>) that a plugin cannot assemble itself
/// because they need the account's raw secret key, not just an opaque digest.
/// </summary>
public static class DigestSigner
{
    /// <param name="alg">"md5" | "sha1" | "sha256".</param>
    /// <param name="encoding">"hex" (default) | "base64".</param>
    public static string Compute(string alg, string input, string encoding = "hex")
        => Encode(Hash(alg, Encoding.UTF8.GetBytes(input)), encoding);

    private static byte[] Hash(string alg, byte[] data) => alg switch
    {
        "md5" => MD5.HashData(data),
        "sha1" => SHA1.HashData(data),
        "sha256" => SHA256.HashData(data),
        _ => throw new ArgumentException($"unsupported digest algorithm '{alg}'"),
    };

    internal static string Encode(byte[] bytes, string encoding) => encoding switch
    {
        "hex" => Convert.ToHexStringLower(bytes),
        "base64" => Convert.ToBase64String(bytes),
        _ => throw new ArgumentException($"unsupported encoding '{encoding}'"),
    };
}

public static class HmacSigner
{
    /// <param name="alg">"sha1" | "sha256".</param>
    public static string Compute(string alg, byte[] key, string input, string encoding = "hex")
        => DigestSigner.Encode(HashData(alg, key, Encoding.UTF8.GetBytes(input)), encoding);

    internal static byte[] HashData(string alg, byte[] key, byte[] data) => alg switch
    {
        "sha1" => HMACSHA1.HashData(key, data),
        "sha256" => HMACSHA256.HashData(key, data),
        _ => throw new ArgumentException($"unsupported hmac algorithm '{alg}'"),
    };
}

/// <summary>One canonical HTTP request shape the named signers operate on: method, absolute URI (query
/// already final), the exact header set present at signing time, and the exact final body bytes.</summary>
public sealed record SignableRequest(string Method, Uri Uri, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[] Body);

/// <summary>What a named signer computed: values to write into declared, pre-reserved header targets
/// (never returned to the plugin as a function result - only written to the request by the caller).</summary>
public sealed record NamedSignature(IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <summary>
/// Tencent Cloud TC3-HMAC-SHA256 (https://cloud.tencent.com/document/api/213/30654), used by 腾讯翻译君/OCR/TTS.
/// Signs the Authorization header from method, canonical URI/query, canonical headers (content-type + host),
/// the SHA-256 of the final body, and a date-scoped signing key derived from the account's secret key -
/// the plugin only ever sees the resulting header value, never the derived key.
/// </summary>
public static class TencentTc3Signer
{
    /// <summary>Canonical-request pieces that do not need the secret key, so tests can check them against
    /// Tencent's published worked example (cloud.tencent.com/document/product/213/30654) even though that
    /// document masks its demo SecretKey with asterisks and only the derived SecretSigning bytes can be
    /// cross-checked end to end (see <see cref="SignFromDerivedKey"/>).</summary>
    public readonly record struct CanonicalPieces(string CanonicalRequest, string StringToSign, string CredentialScope, string SignedHeaders, string Timestamp);

    public static NamedSignature Sign(SignableRequest request, string service, string region, string action, string version, string secretId, string secretKey, DateTimeOffset timestamp)
    {
        var pieces = BuildCanonicalPieces(request, service, timestamp);
        byte[] secretDate = HmacSigner.HashData("sha256", Encoding.UTF8.GetBytes("TC3" + secretKey), Encoding.UTF8.GetBytes(timestamp.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        byte[] secretService = HmacSigner.HashData("sha256", secretDate, Encoding.UTF8.GetBytes(service));
        byte[] secretSigning = HmacSigner.HashData("sha256", secretService, Encoding.UTF8.GetBytes("tc3_request"));
        string signature = SignFromDerivedKey(secretSigning, pieces.StringToSign);
        string authorization = $"TC3-HMAC-SHA256 Credential={secretId}/{pieces.CredentialScope}, SignedHeaders={pieces.SignedHeaders}, Signature={signature}";
        return new NamedSignature([
            new("Authorization", authorization),
            new("X-TC-Timestamp", pieces.Timestamp),
            new("X-TC-Action", action),
            new("X-TC-Version", version),
            new("X-TC-Region", region),
        ]);
    }

    /// <summary>The final HMAC step alone, given an already-derived SecretSigning key. Lets a test verify
    /// against Tencent's published example, which prints SecretDate/SecretService/SecretSigning in hex
    /// even though the demo SecretKey itself is masked in the document.</summary>
    public static string SignFromDerivedKey(byte[] secretSigning, string stringToSign)
        => Convert.ToHexStringLower(HmacSigner.HashData("sha256", secretSigning, Encoding.UTF8.GetBytes(stringToSign)));

    /// <summary>Builds CanonicalRequest/StringToSign exactly as PLAN 4.5.3 requires: content-type and host
    /// are always signed; any other X-TC-* header the caller already set (e.g. X-TC-Action) is folded in
    /// too, the same optional-extra-header convention Tencent's own worked example demonstrates.</summary>
    public static CanonicalPieces BuildCanonicalPieces(SignableRequest request, string service, DateTimeOffset timestamp)
    {
        string date = timestamp.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string ts = timestamp.ToUniversalTime().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        string contentType = HeaderValue(request.Headers, "content-type") ?? "application/json";
        string host = HeaderValue(request.Headers, "host") ?? request.Uri.IdnHost;
        var extra = request.Headers
            .Where(h => h.Key.StartsWith("x-tc-", StringComparison.OrdinalIgnoreCase) && !string.Equals(h.Key, "x-tc-timestamp", StringComparison.OrdinalIgnoreCase))
            .Select(h => (Name: h.Key.ToLowerInvariant(), Value: h.Value.Trim().ToLowerInvariant()))
            .GroupBy(h => h.Name, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(h => h.Name, StringComparer.Ordinal).ToList();
        var signedHeaderNames = new List<string> { "content-type", "host" };
        signedHeaderNames.AddRange(extra.Select(h => h.Name));
        // Tencent's canonical-header rule lowercases both header name and value (its worked example
        // signs X-TC-Action: DescribeInstances as "x-tc-action:describeinstances").
        string canonicalHeaders = $"content-type:{contentType.Trim().ToLowerInvariant()}\nhost:{host.Trim().ToLowerInvariant()}\n" + string.Concat(extra.Select(h => $"{h.Name}:{h.Value}\n"));
        string signedHeaders = string.Join(';', signedHeaderNames);

        string canonicalQuery = CanonicalQuery(request.Uri);
        string hashedPayload = Convert.ToHexStringLower(SHA256.HashData(request.Body));
        string canonicalRequest = $"{request.Method}\n{CanonicalPath(request.Uri)}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{hashedPayload}";
        string hashedCanonicalRequest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));

        string credentialScope = $"{date}/{service}/tc3_request";
        string stringToSign = $"TC3-HMAC-SHA256\n{ts}\n{credentialScope}\n{hashedCanonicalRequest}";
        return new CanonicalPieces(canonicalRequest, stringToSign, credentialScope, signedHeaders, ts);
    }

    private static string CanonicalPath(Uri uri) => string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;

    private static string CanonicalQuery(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Query)) return "";
        var pairs = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Select(kv => (Key: Uri.UnescapeDataString(kv[0]), Value: kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : ""))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{RfcEncode(kv.Key)}={RfcEncode(kv.Value)}");
        return string.Join('&', pairs);
    }

    private static string RfcEncode(string value) => Uri.EscapeDataString(value);

    private static string? HeaderValue(IReadOnlyList<KeyValuePair<string, string>> headers, string name)
        => headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>
/// AWS Signature Version 4 (https://docs.aws.amazon.com/general/latest/gr/sigv4-signing-process.html),
/// used by Amazon Translate. Same shape as TC3: signs Authorization from a canonical request over the
/// final body and a date/region/service-scoped derived key; the account's raw access key never leaves
/// the host.
/// </summary>
public static class AwsSigV4Signer
{
    public static NamedSignature Sign(SignableRequest request, string service, string region, string accessKeyId, string secretAccessKey, DateTimeOffset timestamp)
    {
        string amzDate = timestamp.ToUniversalTime().ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string dateStamp = timestamp.ToUniversalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string payloadHash = Convert.ToHexStringLower(SHA256.HashData(request.Body));

        string host = HeaderValue(request.Headers, "host") ?? request.Uri.IdnHost;
        // Signs host, content-type (if present) and every x-amz-* header already on the request, plus
        // the x-amz-date this signer itself sets - the same convention every AWS SDK canonical-header
        // set follows for a non-streaming, non-S3 service call.
        var toSign = new List<(string Name, string Value)> { ("host", host), ("x-amz-date", amzDate) };
        string? contentType = HeaderValue(request.Headers, "content-type");
        if (contentType is not null) toSign.Add(("content-type", contentType));
        foreach (var h in request.Headers)
            if (h.Key.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase) && !string.Equals(h.Key, "x-amz-date", StringComparison.OrdinalIgnoreCase))
                toSign.Add((h.Key.ToLowerInvariant(), h.Value));
        var ordered = toSign.GroupBy(x => x.Name, StringComparer.Ordinal).Select(g => g.First()).OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
        string canonicalHeaders = string.Concat(ordered.Select(x => $"{x.Name}:{x.Value.Trim()}\n"));
        string signedHeaders = string.Join(';', ordered.Select(x => x.Name));

        string canonicalRequest = $"{request.Method}\n{CanonicalPath(request.Uri)}\n{CanonicalQuery(request.Uri)}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        string hashedCanonicalRequest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));
        string credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
        string stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{hashedCanonicalRequest}";

        byte[] kDate = HmacSigner.HashData("sha256", Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), Encoding.UTF8.GetBytes(dateStamp));
        byte[] kRegion = HmacSigner.HashData("sha256", kDate, Encoding.UTF8.GetBytes(region));
        byte[] kService = HmacSigner.HashData("sha256", kRegion, Encoding.UTF8.GetBytes(service));
        byte[] kSigning = HmacSigner.HashData("sha256", kService, Encoding.UTF8.GetBytes("aws4_request"));
        string signature = Convert.ToHexStringLower(HmacSigner.HashData("sha256", kSigning, Encoding.UTF8.GetBytes(stringToSign)));

        string authorization = $"AWS4-HMAC-SHA256 Credential={accessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
        return new NamedSignature([new("Authorization", authorization), new("X-Amz-Date", amzDate)]);
    }

    private static string CanonicalPath(Uri uri) => string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;

    private static string CanonicalQuery(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Query)) return "";
        var pairs = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Select(kv => (Key: Uri.UnescapeDataString(kv[0]), Value: kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : ""))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");
        return string.Join('&', pairs);
    }

    private static string? HeaderValue(IReadOnlyList<KeyValuePair<string, string>> headers, string name)
        => headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
