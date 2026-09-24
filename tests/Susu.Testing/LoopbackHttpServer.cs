using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Susu.Testing;

public sealed record LoopbackHttpRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body);
public sealed record LoopbackHttpResponse(int Status, byte[] Body, IReadOnlyDictionary<string, string>? Headers = null, string ContentType = "application/json", int? DeclaredContentLength = null)
{
    public static LoopbackHttpResponse Json(int status, string body) => new(status, Encoding.UTF8.GetBytes(body));
    public static LoopbackHttpResponse Text(int status, string body) => new(status, Encoding.UTF8.GetBytes(body), ContentType: "text/plain; charset=utf-8");
    public static LoopbackHttpResponse Redirect(int status, string location) => new(status, [], new Dictionary<string, string> { ["Location"] = location });

    /// <summary>Declares a Content-Length larger than the bytes actually sent, then closes the connection
    /// (B08: a reader that trusted Content-Length would wait forever or crash; NetworkBroker must not).</summary>
    public static LoopbackHttpResponse WithLyingContentLength(byte[] actualBody, int declaredLength) => new(200, actualBody, DeclaredContentLength: declaredLength);
}

/// <summary>
/// A minimal single-purpose HTTP/1.1 server over a raw <see cref="TcpListener"/> (not
/// <see cref="HttpListener"/>, which needs a URL-ACL reservation for non-admin users on Windows): one
/// request per connection, no chunked transfer, no keep-alive - just enough to exercise
/// Susu.Net.NetworkBroker against a real socket for F05.1/F05.2 tests (proxy/redirect/byte-limit/
/// multipart/JSON-Base64 behavior that only shows up talking to an actual server).
/// </summary>
public sealed class LoopbackHttpServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly Func<LoopbackHttpRequest, LoopbackHttpResponse> handler;
    private readonly CancellationTokenSource cts = new();
    private readonly Task loop;

    public int Port { get; }
    public string Origin => $"http://127.0.0.1:{Port}";
    public int RequestCount;

    public LoopbackHttpServer(Func<LoopbackHttpRequest, LoopbackHttpResponse> handler)
    {
        this.handler = handler;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        loop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        await using var stream = client.GetStream();
        try
        {
            var request = await ReadRequestAsync(stream, cts.Token);
            if (request is null) return;
            Interlocked.Increment(ref RequestCount);
            var response = handler(request);
            await WriteResponseAsync(stream, response, cts.Token);
        }
        catch (IOException) { } catch (ObjectDisposedException) { }
    }

    private static async Task<LoopbackHttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>();
        var one = new byte[1];
        while (headerBytes.Count < 4 || !EndsWithCrLfCrLf(headerBytes))
        {
            int read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0) return headerBytes.Count == 0 ? null : throw new IOException("connection closed mid-headers");
            headerBytes.Add(one[0]);
            if (headerBytes.Count > 64 * 1024) throw new IOException("headers too large");
        }
        string headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestLine = lines[0].Split(' ');
        string method = requestLine[0];
        string path = requestLine[1];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        byte[] body = [];
        if (headers.TryGetValue("Content-Length", out string? lengthText) && int.TryParse(lengthText, out int length) && length > 0)
        {
            body = new byte[length];
            int total = 0;
            while (total < length)
            {
                int read = await stream.ReadAsync(body.AsMemory(total, length - total), cancellationToken);
                if (read == 0) throw new IOException("connection closed mid-body");
                total += read;
            }
        }
        return new LoopbackHttpRequest(method, path, headers, body);
    }

    private static bool EndsWithCrLfCrLf(List<byte> bytes)
    {
        int n = bytes.Count;
        return n >= 4 && bytes[n - 4] == (byte)'\r' && bytes[n - 3] == (byte)'\n' && bytes[n - 2] == (byte)'\r' && bytes[n - 1] == (byte)'\n';
    }

    private static async Task WriteResponseAsync(NetworkStream stream, LoopbackHttpResponse response, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append($"HTTP/1.1 {response.Status} {ReasonPhrase(response.Status)}\r\n");
        builder.Append($"Content-Type: {response.ContentType}\r\n");
        builder.Append($"Content-Length: {response.DeclaredContentLength ?? response.Body.Length}\r\n");
        builder.Append("Connection: close\r\n");
        if (response.Headers is not null) foreach (var (name, value) in response.Headers) builder.Append($"{name}: {value}\r\n");
        builder.Append("\r\n");
        byte[] head = Encoding.ASCII.GetBytes(builder.ToString());
        await stream.WriteAsync(head, cancellationToken);
        if (response.Body.Length > 0) await stream.WriteAsync(response.Body, cancellationToken);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK", 201 => "Created", 301 => "Moved Permanently", 302 => "Found", 303 => "See Other",
        307 => "Temporary Redirect", 308 => "Permanent Redirect", 400 => "Bad Request", 401 => "Unauthorized",
        403 => "Forbidden", 404 => "Not Found", 429 => "Too Many Requests", 500 => "Internal Server Error", _ => "OK",
    };

    public void Dispose()
    {
        cts.Cancel();
        listener.Stop();
        try { loop.Wait(1000); } catch (AggregateException) { }
        cts.Dispose();
    }
}
