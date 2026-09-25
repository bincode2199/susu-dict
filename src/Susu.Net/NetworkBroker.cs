using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Susu.Contracts;

namespace Susu.Net;

public enum BodyKind { None, Json, Text, File, Multipart }

public sealed record RequestBody(BodyKind Kind, JsonNode? Json = null, string? Text = null, byte[]? FileBytes = null, string? FileContentType = null, IReadOnlyList<MultipartFieldSpec>? MultipartFields = null)
{
    public static readonly RequestBody None = new(BodyKind.None);
}

/// <summary>One JSON/Base64 request insertion (PLAN 4.5.1): the pointer must already hold JSON null.</summary>
public sealed record BodyFileInsertion(string Pointer, byte[] Bytes);

public enum ResponseKind { Json, Text, File }

/// <summary>One JSON/Base64 response extraction target (PLAN 4.5.1).</summary>
public sealed record ResponseFileSpec(string Name, string Pointer, string Mime);

/// <summary>One <c>digest</c>/<c>hmac</c> primitive (PLAN 4.5.3): <paramref name="Input"/> is literal/secret
/// parts hashed together (never injected verbatim), the result is written to <paramref name="Into"/>.</summary>
public sealed record PrimitiveSign(string Kind, string Alg, IReadOnlyList<CredentialPart> Input, CredentialArea IntoArea, string IntoTarget, string Encoding, string? HmacKeySecret);

/// <summary>One named multi-step scheme (PLAN 4.5.3): needs the account's raw secret, so only the host
/// can run it. <paramref name="SecretIdSecret"/>/<paramref name="SecretKeySecret"/> are local secret
/// names, resolved and authorized the same way <see cref="CredentialSpec"/> secrets are.</summary>
public sealed record NamedSign(string Scheme, string Service, string? Region, string SecretIdSecret, string SecretKeySecret);

public sealed record SignSpec(PrimitiveSign? Primitive, NamedSign? Named);

public sealed record BrokerHttpRequest(
    string Method,
    Uri Uri,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    RequestBody Body,
    IReadOnlyList<BodyFileInsertion> BodyFiles,
    IReadOnlyList<CredentialSpec> Credentials,
    Func<CredentialSpec, string, string> ResolveSecret,
    SignSpec? Sign,
    ResponseKind ResponseType,
    IReadOnlyList<ResponseFileSpec> ResponseFiles,
    string? ErrorPointer,
    bool LocalOriginApproved);

public sealed record BrokerFile(string Name, byte[] Bytes, string Mime);

public sealed record BrokerHttpResponse(
    int Status,
    IReadOnlyDictionary<string, string> Headers,
    JsonNode? JsonBody,
    string? TextBody,
    byte[]? RawFile,
    string? RawFileMime,
    IReadOnlyList<BrokerFile> Files,
    bool Truncated,
    string? RedirectUrl);

public abstract record BrokerOutcome;
public sealed record BrokerSuccess(BrokerHttpResponse Response) : BrokerOutcome;
/// <summary><paramref name="Kind"/> matches the plugin-visible error kinds (PLAN 4.4): "network", "timeout"
/// for transport-level failures the host classifies itself; "bad_response" for contract violations.</summary>
public sealed record BrokerFailure(string Kind, string Detail) : BrokerOutcome;

/// <summary>Result of <see cref="NetworkBroker.ExecuteStreamAsync"/> ($http.stream). A 2xx response
/// streams; a non-2xx response never does ("非 2xx 时只有有界 error", PLAN 4.5) so it carries only a
/// bounded error body, exactly like the non-streaming $http shape's error path.</summary>
public abstract record BrokerStreamOutcome;
public sealed record BrokerStreamStarted(int Status, IReadOnlyDictionary<string, string> Headers, IAsyncEnumerable<string> Text) : BrokerStreamOutcome;
public sealed record BrokerStreamRejected(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body, bool Truncated) : BrokerStreamOutcome;
public sealed record BrokerStreamFailure(string Kind, string Detail) : BrokerStreamOutcome;

public sealed class NetworkBrokerOptions
{
    public IWebProxy? Proxy { get; init; }
    /// <summary>Use the OS-configured default proxy (PLAN NetworkSettings.ProxyMode.System) when
    /// <see cref="Proxy"/> is null. Ignored when <see cref="Proxy"/> is set.</summary>
    public bool UseSystemProxy { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public ApprovedLocalOrigins LocalOrigins { get; init; } = new();
    /// <summary>Response headers ever forwarded to a plugin (PLAN 4.5.1): Content-Type, Retry-After and
    /// caller-declared extra allowed headers (e.g. a provider's request-id/usage header).</summary>
    public IReadOnlySet<string>? ExtraAllowedResponseHeaders { get; init; }
}

/// <summary>
/// The production network broker (F05.1/F05.2): one HttpClient per broker instance, DNS-pinned and
/// origin-validated per connection, proxy-aware, with the credential/signing pipeline (PLAN 4.5.2/4.5.3)
/// and the binary/JSON transforms (4.5.1) applied to every request. This class only executes an
/// already-authorized, fully-described request; origin/secret/handle *authorization* is the caller's
/// job (Susu.Plugins.Broker), matching the production/test-double split DEV-PLAN §1 requires.
/// </summary>
public sealed class NetworkBroker : IDisposable
{
    private static readonly HashSet<string> alwaysAllowedResponseHeaders = new(StringComparer.OrdinalIgnoreCase) { "content-type", "retry-after" };
    private readonly SocketsHttpHandler handler;
    private readonly HttpClient client;
    private readonly NetworkBrokerOptions options;

    public NetworkBroker(NetworkBrokerOptions options)
    {
        this.options = options;
        handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // redirects are handled by hand (S04: no auto-follow with credentials, 5-hop cap otherwise)
            UseProxy = options.Proxy is not null || options.UseSystemProxy,
            Proxy = options.Proxy,
            ConnectCallback = ConnectValidatedAsync,
            AutomaticDecompression = System.Net.DecompressionMethods.None, // a plugin never sees a caller-uncontrolled decompression bomb (B08)
        };
        client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    /// <summary>The set of explicitly-approved local origins (127.0.0.1:8765/11434-style) this broker
    /// instance uses; a composition root adds to it at install/configure time (PLAN 4.5.4.2).</summary>
    public ApprovedLocalOrigins LocalOrigins => options.LocalOrigins;

    public async Task<BrokerOutcome> ExecuteAsync(BrokerHttpRequest request, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.Timeout);
        try
        {
            var (uri, headers, body, authorized) = Prepare(request);
            return await SendWithRedirectsAsync(request, uri, headers, body, authorized, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BrokerFailure("timeout", $"request exceeded {options.Timeout}");
        }
        catch (FileTransformException error) { return new BrokerFailure("bad_response", error.Message); }
        catch (CredentialRejectedException error) { return new BrokerFailure("bad_response", error.Message); }
        catch (HttpRequestException error) { return new BrokerFailure("network", error.Message); }
        catch (SocketException error) { return new BrokerFailure("network", error.Message); }
        catch (IOException error) { return new BrokerFailure("network", error.Message); }
        catch (InvalidOperationException error) { return new BrokerFailure("network", error.Message); }
    }

    /// <summary>
    /// SSE-shaped streaming variant of <see cref="ExecuteAsync"/> ($http.stream, PLAN 4.5): same
    /// origin/credential/signing pipeline, but on a 2xx response the body is handed back as an
    /// <see cref="IAsyncEnumerable{T}"/> of decoded UTF-8 text pieces read progressively from the live
    /// socket, instead of being buffered - the caller (Susu.Plugins.Broker) is responsible for pacing
    /// its own consumption against StreamWindow credit. Disposing the enumerator (stop enumerating,
    /// including via cancellation) closes the underlying response/connection.
    /// </summary>
    public async Task<BrokerStreamOutcome> ExecuteStreamAsync(BrokerHttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var (uri, headers, body, authorized) = Prepare(request);
            return await SendStreamingAsync(request, uri, headers, body, authorized, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new BrokerStreamFailure("timeout", $"request exceeded {options.Timeout}"); }
        catch (FileTransformException error) { return new BrokerStreamFailure("bad_response", error.Message); }
        catch (CredentialRejectedException error) { return new BrokerStreamFailure("bad_response", error.Message); }
        catch (HttpRequestException error) { return new BrokerStreamFailure("network", error.Message); }
        catch (SocketException error) { return new BrokerStreamFailure("network", error.Message); }
        catch (IOException error) { return new BrokerStreamFailure("network", error.Message); }
        catch (InvalidOperationException error) { return new BrokerStreamFailure("network", error.Message); }
    }

    private (Uri Uri, List<KeyValuePair<string, string>> Headers, byte[] Body, bool Authorized) Prepare(BrokerHttpRequest request)
    {
        bool authorized = request.Credentials.Count > 0 || request.Sign is not null;

        // ---- Build the final body: bodyFiles insertion -> credential injection -> digest/hmac -> named signer. ----
        var pointers = request.BodyFiles.Select(f => f.Pointer).ToList();
        if (request.Sign?.Primitive is { IntoArea: CredentialArea.Json } primitiveJson) pointers.Add(primitiveJson.IntoTarget);
        if (JsonPointerOps.AnyDuplicateOrOverlap(pointers)) throw new FileTransformException("duplicate or overlapping bodyFiles/credential JSON pointers");

        JsonNode? json = request.Body.Kind == BodyKind.Json ? request.Body.Json?.DeepClone() : null;
        if (request.Body.Kind == BodyKind.Json && json is null) throw new FileTransformException("json body required");
        foreach (var file in request.BodyFiles)
        {
            if (request.Body.Kind != BodyKind.Json) throw new FileTransformException("bodyFiles only apply to a json body");
            JsonPointerOps.SetAtReservedNull(json!, file.Pointer, Convert.ToBase64String(file.Bytes));
        }

        var descriptor = new RequestDescriptor(request.Method, request.Uri.AbsoluteUri, request.Headers, json);
        var filled = CredentialInjector.Apply(descriptor, request.Credentials, request.ResolveSecret);
        json = filled.Body;
        var headers = filled.Headers.ToList();
        var uri = new Uri(filled.Url, UriKind.Absolute);

        if (request.Sign?.Primitive is { } primitive)
        {
            string input = string.Concat(primitive.Input.Select(p => p.Literal ?? ResolvePrimitiveSecret(request, primitive, p.Secret!)));
            string value = primitive.Kind == "hmac"
                ? HmacSigner.Compute(primitive.Alg, System.Text.Encoding.UTF8.GetBytes(ResolvePrimitiveSecret(request, primitive, primitive.HmacKeySecret ?? throw new FileTransformException("hmac requires a key secret"))), input, primitive.Encoding)
                : DigestSigner.Compute(primitive.Alg, input, primitive.Encoding);
            var target = new CredentialSpec(primitive.IntoArea, primitive.IntoTarget, [CredentialPart.Text(value)]);
            var afterPrimitive = CredentialInjector.Apply(new RequestDescriptor(request.Method, uri.AbsoluteUri, headers, json), [target], (_, _) => value);
            json = afterPrimitive.Body;
            headers = afterPrimitive.Headers.ToList();
            uri = new Uri(afterPrimitive.Url, UriKind.Absolute);
        }

        byte[] finalBody = request.Body.Kind switch
        {
            BodyKind.Json => System.Text.Encoding.UTF8.GetBytes(json!.ToJsonString()),
            BodyKind.Text => System.Text.Encoding.UTF8.GetBytes(request.Body.Text ?? ""),
            BodyKind.File => request.Body.FileBytes ?? [],
            BodyKind.Multipart or BodyKind.None => [],
            _ => [],
        };

        if (request.Sign?.Named is { } named)
        {
            // PLAN 4.5.2: a named signer's secrets are granted as "signer:<scheme>", not as the header it fills.
            var signerSpec = new CredentialSpec(CredentialArea.Header, "Authorization", [], $"signer:{named.Scheme}");
            string secretId = request.ResolveSecret(signerSpec, named.SecretIdSecret);
            string secretKey = request.ResolveSecret(signerSpec, named.SecretKeySecret);
            var signable = new SignableRequest(request.Method, uri, headers, finalBody);
            var signature = named.Scheme switch
            {
                "tencent-tc3" => TencentTc3Signer.Sign(signable, named.Service, secretId, secretKey, DateTimeOffset.UtcNow),
                "aws-sigv4" => AwsSigV4Signer.Sign(signable, named.Service, named.Region ?? throw new FileTransformException("aws-sigv4 requires a region"), secretId, secretKey, DateTimeOffset.UtcNow),
                _ => throw new FileTransformException($"unknown sign scheme '{named.Scheme}'"),
            };
            foreach (var header in signature.Headers)
            {
                int index = headers.FindIndex(h => string.Equals(h.Key, header.Key, StringComparison.OrdinalIgnoreCase));
                if (index < 0) throw new FileTransformException($"header '{header.Key}' must be reserved for the '{named.Scheme}' signer");
                if (headers[index].Value.Length != 0) throw new FileTransformException($"header '{header.Key}' must be reserved empty for the '{named.Scheme}' signer");
                headers[index] = new(headers[index].Key, header.Value);
            }
        }

        return (uri, headers, finalBody, authorized);
    }

    private static string ResolvePrimitiveSecret(BrokerHttpRequest request, PrimitiveSign primitive, string secretName)
        => request.ResolveSecret(new CredentialSpec(primitive.IntoArea, primitive.IntoTarget, []), secretName);

    private async Task<BrokerOutcome> SendWithRedirectsAsync(BrokerHttpRequest request, Uri uri, List<KeyValuePair<string, string>> headers, byte[] body, bool authorized, CancellationToken cancellationToken)
    {
        string method = request.Method;
        for (int hop = 0; ; hop++)
        {
            if (!IsAllowedForRequest(request, uri)) return new BrokerFailure("network", $"origin {Origin(uri)} not allowed");

            using var httpRequest = new HttpRequestMessage(new HttpMethod(method), uri);
            HttpContent? content = BuildContent(request.Body, headers, body);
            if (content is not null) httpRequest.Content = content;
            foreach (var header in headers)
            {
                if (content is not null && IsContentHeader(header.Key)) { TrySetContentHeader(content, header.Key, header.Value); continue; }
                httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            bool redirect = response.StatusCode is System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.Found or System.Net.HttpStatusCode.SeeOther
                or System.Net.HttpStatusCode.TemporaryRedirect or System.Net.HttpStatusCode.PermanentRedirect;
            if (redirect)
            {
                string? location = response.Headers.Location?.IsAbsoluteUri == true ? response.Headers.Location.AbsoluteUri
                    : response.Headers.Location is not null ? new Uri(uri, response.Headers.Location).AbsoluteUri : null;
                if (authorized || location is null) return await BuildResultAsync(request, response, cancellationToken, redirectUrl: location);
                var next = new Uri(location, UriKind.Absolute);
                if (hop + 1 >= 5) return new BrokerFailure("network", "redirect limit (5) exceeded");
                if (IsHttpsToHttpDowngrade(uri, next)) return new BrokerFailure("network", "redirect would downgrade HTTPS to HTTP");
                uri = next;
                if (response.StatusCode == System.Net.HttpStatusCode.SeeOther) { method = "GET"; body = []; }
                continue;
            }
            return await BuildResultAsync(request, response, cancellationToken, redirectUrl: null);
        }
    }

    /// <summary>No redirect-following: an SSE/streaming endpoint is not expected to redirect, and
    /// deciding whether to auto-follow needs the whole response body semantics streaming intentionally
    /// avoids buffering. A redirect response is surfaced as a rejection instead.</summary>
    private async Task<BrokerStreamOutcome> SendStreamingAsync(BrokerHttpRequest request, Uri uri, List<KeyValuePair<string, string>> headers, byte[] body, bool authorized, CancellationToken cancellationToken)
    {
        _ = authorized;
        if (!IsAllowedForRequest(request, uri)) return new BrokerStreamFailure("network", $"origin {Origin(uri)} not allowed");

        var httpRequest = new HttpRequestMessage(new HttpMethod(request.Method), uri);
        HttpContent? content = BuildContent(request.Body, headers, body);
        if (content is not null) httpRequest.Content = content;
        foreach (var header in headers)
        {
            if (content is not null && IsContentHeader(header.Key)) { TrySetContentHeader(content, header.Key, header.Value); continue; }
            httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        bool redirect = response.StatusCode is System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.Found or System.Net.HttpStatusCode.SeeOther
            or System.Net.HttpStatusCode.TemporaryRedirect or System.Net.HttpStatusCode.PermanentRedirect;
        var visibleHeaders = FilterResponseHeaders(response, options.ExtraAllowedResponseHeaders);
        if (!response.IsSuccessStatusCode || redirect)
        {
            int status = (int)response.StatusCode;
            var (bytes, _) = await ReadBoundedAsync(response.Content, ProtocolLimits.MaxHttpErrorBodyBytes, cancellationToken);
            bool truncated = bytes.Length >= ProtocolLimits.MaxHttpErrorBodyBytes;
            httpRequest.Dispose();
            response.Dispose();
            return new BrokerStreamRejected(status, visibleHeaders, bytes, truncated);
        }
        return new BrokerStreamStarted((int)response.StatusCode, visibleHeaders, DecodeText(httpRequest, response, cancellationToken));
    }

    /// <summary>Disposes the request/response once enumeration ends (including the caller stopping
    /// early on cancel/error) and delegates the actual read/coalesce/decode work to <see cref="DecodeTextFromStream"/>,
    /// which takes a plain <see cref="Stream"/> so it is testable without a real socket.</summary>
    private static async IAsyncEnumerable<string> DecodeText(HttpRequestMessage httpRequest, HttpResponseMessage response,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var _req = httpRequest;
        using var _resp = response;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var piece in DecodeTextFromStream(stream, cancellationToken)) yield return piece;
    }

    /// <summary>
    /// Reads <paramref name="stream"/> progressively, decodes UTF-8 and yields text pieces - not
    /// SSE-event-boundary-aware (PLAN 4.5 explicitly does not promise that), but *does* coalesce bytes
    /// that arrive close together in time into one piece instead of yielding one piece per raw
    /// read call: after every read it races a short (<see cref="CoalesceWindow"/>) further read against
    /// a timer, without cancelling that pending read, so a burst that is still arriving (whether from
    /// real OS/socket buffering or a test feeding many tiny reads back to back) is not needlessly split
    /// into pieces at whatever boundary happened to land at. internal (not private) so a test can feed
    /// it an in-memory stream directly instead of a real socket - see NetworkBrokerTests.
    /// </summary>
    internal static async IAsyncEnumerable<string> DecodeTextFromStream(Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken, TimeSpan? coalesceWindow = null)
    {
        TimeSpan window = coalesceWindow ?? CoalesceWindow;
        var decoder = System.Text.Encoding.UTF8.GetDecoder();
        var accumulated = new System.Text.StringBuilder();
        var buffer = new byte[64 * 1024];
        // Never more than one ReadAsync in flight on `stream` at a time (most streams, including a real
        // socket's content stream, do not support concurrent reads): a probe that loses the timer race
        // is not abandoned, it is carried forward and awaited as the *next* iteration's read instead of
        // starting a second, overlapping one.
        Task<int>? pending = null;

        async Task<int> ReadNextAsync()
        {
            pending ??= stream.ReadAsync(buffer, cancellationToken).AsTask();
            int n = await pending;
            pending = null;
            return n;
        }

        while (true)
        {
            int read = await ReadNextAsync(); // blocking wait for at least one chunk
            if (read == 0) { if (accumulated.Length > 0) { yield return accumulated.ToString(); } yield break; }
            AppendDecoded(decoder, accumulated, buffer, read);
            // A safety ceiling independent of any caller's own frame-size policy: even if data keeps
            // arriving within the coalesce window indefinitely (e.g. many tiny reads back to back with
            // no gap), this loop must still eventually flush instead of accumulating unbounded memory -
            // the caller (Broker.PumpStreamAsync) is what actually rejects an oversized piece; this just
            // guarantees it gets handed one promptly, bounded, rather than growing forever first.
            while (accumulated.Length <= AccumulationCeilingChars)
            {
                pending = stream.ReadAsync(buffer, cancellationToken).AsTask();
                var winner = await Task.WhenAny(pending, Task.Delay(window, cancellationToken));
                if (!ReferenceEquals(winner, pending)) break; // nothing arrived within the window: flush below, `pending` carries over
                int more = await pending;
                pending = null;
                if (more == 0) { yield return accumulated.ToString(); yield break; }
                AppendDecoded(decoder, accumulated, buffer, more);
            }
            string piece = accumulated.ToString();
            accumulated.Clear();
            if (piece.Length > 0) yield return piece;
        }
    }

    /// <summary>How long to wait for another read to already have data ready before flushing what has
    /// accumulated so far as one piece. Small enough that real SSE pacing (events spaced well above
    /// this) still yields one piece per event; long enough that a genuine burst - one big read, or many
    /// tiny reads delivered back to back - reliably coalesces into a single piece for the cap check.</summary>
    internal static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(4);

    /// <summary>Force a flush once accumulated (not-yet-yielded) characters pass this many - a generous
    /// multiple of ProtocolLimits.MaxStreamChunkBytes so any piece Broker.PumpStreamAsync would reject
    /// as oversized is always reached well before this fires, while still bounding this loop's own
    /// worst-case memory independent of that caller's policy.</summary>
    private const int AccumulationCeilingChars = ProtocolLimits.MaxStreamChunkBytes * 4;

    private static void AppendDecoded(System.Text.Decoder decoder, System.Text.StringBuilder accumulated, byte[] buffer, int count)
    {
        int charCount = decoder.GetCharCount(buffer, 0, count, flush: false);
        if (charCount == 0) return;
        var chars = new char[charCount];
        int written = decoder.GetChars(buffer, 0, count, chars, 0, flush: false);
        if (written > 0) accumulated.Append(chars, 0, written);
    }

    /// <summary>S04: a redirect chain that starts https:// must never end up at plain http:// - checked
    /// per hop (not just the final destination) so an intermediate downgrade cannot slip through even if
    /// a later hop redirects back to https://. internal so a test can call the exact production check
    /// directly instead of restating its condition.</summary>
    internal static bool IsHttpsToHttpDowngrade(Uri from, Uri to) => from.Scheme == "https" && to.Scheme != "https";

    private bool IsAllowedForRequest(BrokerHttpRequest request, Uri uri)
    {
        if (uri.Scheme is not ("https" or "http")) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (string.Equals(request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase)) return false; // only the handler opens proxy tunnels
        bool local = options.LocalOrigins.Contains(Origin(uri)) && request.LocalOriginApproved;
        if (uri.Scheme == "http" && !local) return false;
        return true;
    }

    private static bool IsContentHeader(string name) => name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase);

    private static void TrySetContentHeader(HttpContent content, string name, string value)
    {
        if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
    }

    private static HttpContent? BuildContent(RequestBody body, List<KeyValuePair<string, string>> headers, byte[] finalBody)
    {
        switch (body.Kind)
        {
            case BodyKind.None: return null;
            case BodyKind.Json:
                var jsonContent = new ByteArrayContent(finalBody);
                jsonContent.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                return jsonContent;
            case BodyKind.Text:
                var textContent = new ByteArrayContent(finalBody);
                textContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
                return textContent;
            case BodyKind.File:
                var fileContent = new ByteArrayContent(finalBody);
                fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(body.FileContentType ?? "application/octet-stream");
                return fileContent;
            case BodyKind.Multipart:
                return MultipartBuilder.Build(body.MultipartFields ?? []);
            default: return null;
        }
    }

    private async Task<BrokerOutcome> BuildResultAsync(BrokerHttpRequest request, HttpResponseMessage response, CancellationToken cancellationToken, string? redirectUrl)
    {
        bool ok = response.IsSuccessStatusCode;
        int cap = ok ? MaxBytesFor(request) : ProtocolLimits.MaxHttpErrorBodyBytes;
        var (bytes, truncated) = await ReadBoundedAsync(response.Content, cap, cancellationToken);
        var visibleHeaders = FilterResponseHeaders(response, options.ExtraAllowedResponseHeaders);

        if (!ok || request.ResponseType == ResponseKind.Text || (request.ResponseType == ResponseKind.Json && request.ResponseFiles.Count == 0))
        {
            JsonNode? asJson = null;
            string? asText = null;
            if (!truncated) { try { asJson = JsonNode.Parse(bytes); } catch (JsonException) { } }
            if (asJson is null) asText = System.Text.Encoding.UTF8.GetString(bytes);
            return new BrokerSuccess(new BrokerHttpResponse((int)response.StatusCode, visibleHeaders, asJson, asJson is null ? asText : null, null, null, [], truncated, redirectUrl));
        }

        if (request.ResponseType == ResponseKind.File)
        {
            string mime = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            if (truncated) return new BrokerFailure("bad_response", "file response exceeded the binary size limit");
            return new BrokerSuccess(new BrokerHttpResponse((int)response.StatusCode, visibleHeaders, null, null, bytes, mime, [], false, redirectUrl));
        }

        // ResponseKind.Json with responseFiles: JSON/Base64 extraction (PLAN 4.5.1).
        if (truncated) return new BrokerFailure("bad_response", "json response exceeded the transfer size limit");
        JsonNode? root;
        try { root = JsonNode.Parse(bytes); } catch (JsonException error) { return new BrokerFailure("bad_response", $"invalid JSON: {error.Message}"); }
        if (root is null) return new BrokerFailure("bad_response", "empty JSON response");

        if (JsonPointerOps.AnyDuplicateOrOverlap([.. request.ResponseFiles.Select(f => f.Pointer)]))
            return new BrokerFailure("bad_response", "duplicate or overlapping responseFiles pointers");

        var files = new List<BrokerFile>();
        try
        {
            foreach (var spec in request.ResponseFiles)
            {
                string base64 = JsonPointerOps.ExtractAndNull(root, spec.Pointer);
                byte[] decoded;
                try { decoded = Convert.FromBase64String(base64); } catch (FormatException) { throw new FileTransformException($"field at {spec.Pointer} is not valid Base64"); }
                if (decoded.LongLength > ProtocolLimits.MaxBinaryBytes) throw new FileTransformException($"decoded file at {spec.Pointer} exceeds the binary size limit");
                files.Add(new BrokerFile(spec.Name, decoded, spec.Mime));
            }
        }
        catch (FileTransformException error)
        {
            if (request.ErrorPointer is { } errorPointer)
            {
                JsonNode? subtree;
                try { subtree = ResolveForRead(root, errorPointer); } catch (FileTransformException) { subtree = null; }
                if (subtree is not null)
                {
                    string subtreeJson = subtree.ToJsonString();
                    bool subtreeTruncated = System.Text.Encoding.UTF8.GetByteCount(subtreeJson) > ProtocolLimits.MaxHttpErrorBodyBytes;
                    return new BrokerSuccess(new BrokerHttpResponse((int)response.StatusCode, visibleHeaders, subtreeTruncated ? null : subtree, subtreeTruncated ? subtreeJson[..Math.Min(subtreeJson.Length, ProtocolLimits.MaxHttpErrorBodyBytes)] : null, null, null, [], subtreeTruncated, redirectUrl));
                }
            }
            return new BrokerFailure("bad_response", error.Message);
        }

        string finalJson = root.ToJsonString();
        if (System.Text.Encoding.UTF8.GetByteCount(finalJson) > ProtocolLimits.MaxReassembledJsonBytes)
            return new BrokerFailure("bad_response", "json metadata exceeds the post-extraction size limit");
        return new BrokerSuccess(new BrokerHttpResponse((int)response.StatusCode, visibleHeaders, root, null, null, null, files, false, redirectUrl));
    }

    private static JsonNode? ResolveForRead(JsonNode root, string pointer)
    {
        if (pointer == "/" || pointer.Length == 0) return root;
        var tokens = pointer.StartsWith('/') ? pointer[1..].Split('/') : throw new FileTransformException("invalid pointer");
        JsonNode? node = root;
        foreach (string raw in tokens)
        {
            string token = raw.Replace("~1", "/").Replace("~0", "~");
            node = node switch
            {
                JsonObject obj => obj.TryGetPropertyValue(token, out var v) ? v : throw new FileTransformException("missing"),
                JsonArray arr when int.TryParse(token, out int i) && i < arr.Count => arr[i],
                _ => throw new FileTransformException("missing"),
            };
        }
        return node;
    }

    private static int MaxBytesFor(BrokerHttpRequest request) => request.ResponseType switch
    {
        ResponseKind.File => ProtocolLimits.MaxBinaryBytes,
        ResponseKind.Json when request.ResponseFiles.Count > 0 => ProtocolLimits.MaxBase64JsonBytes,
        _ => ProtocolLimits.MaxReassembledJsonBytes,
    };

    /// <summary>Reads up to <paramref name="cap"/> bytes without trusting Content-Length (B08): a server
    /// that lies about its length, or a decompression bomb, can never grow memory past the cap.</summary>
    private static async Task<(byte[] Bytes, bool Truncated)> ReadBoundedAsync(HttpContent content, int cap, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int total = 0;
        bool truncated = false;
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > cap) { truncated = true; break; }
            buffer.Write(chunk, 0, read);
        }
        return (buffer.ToArray(), truncated);
    }

    private static IReadOnlyDictionary<string, string> FilterResponseHeaders(HttpResponseMessage response, IReadOnlySet<string>? extra)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
            if (alwaysAllowedResponseHeaders.Contains(header.Key) || (extra?.Contains(header.Key) ?? false))
                result[header.Key] = string.Join(", ", header.Value);
        return result;
    }

    private async ValueTask<System.IO.Stream> ConnectValidatedAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        string targetHost = context.InitialRequestMessage.RequestUri!.IdnHost;
        string targetOrigin = Origin(context.InitialRequestMessage.RequestUri!);
        bool local = options.LocalOrigins.Contains(targetOrigin);
        // This connection goes to the user's configured proxy, not the target: either the handler's own CONNECT
        // tunnel request (the broker never sends CONNECT itself, see IsAllowedForRequest) or a plain request whose
        // endpoint differs from its URI. A proxy on 127.0.0.1 or the LAN is the user's explicit choice (F07.3,
        // CFG05), and the proxy resolves the cloud host itself. A direct connection is still checked below.
        var requestUri = context.InitialRequestMessage.RequestUri!;
        bool toProxy = (options.Proxy is not null || options.UseSystemProxy)
            && (context.InitialRequestMessage.Method == HttpMethod.Connect
                || !(string.Equals(context.DnsEndPoint.Host, requestUri.IdnHost, StringComparison.OrdinalIgnoreCase) && context.DnsEndPoint.Port == requestUri.Port));
        local |= toProxy;

        IPAddress[] addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var direct)
            ? [direct]
            : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
        if (!local)
            foreach (var address in addresses)
                if (OriginPolicy.IsDisallowedForCloud(address))
                    throw new InvalidOperationException($"'{targetHost}' resolved to a disallowed address for a cloud origin");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            // Connect to exactly the address just validated (first candidate) - no second, independent
            // resolution that could answer differently between check and connect (S05 TOCTOU).
            await socket.ConnectAsync(addresses[0], context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    public static string Origin(Uri uri) => $"{uri.Scheme}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}";

    public void Dispose() { client.Dispose(); handler.Dispose(); }
}
