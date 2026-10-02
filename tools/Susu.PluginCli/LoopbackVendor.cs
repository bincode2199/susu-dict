using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Susu.PluginCli;

/// <summary>One request the plugin sent to the loopback vendor (recorded for `expectRequests`).</summary>
internal sealed record VendorRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string BodyText => Encoding.UTF8.GetString(Body);
}

/// <summary>A canned vendor answer.</summary>
internal sealed record VendorResponse(int Status, byte[] Body, string ContentType, IReadOnlyDictionary<string, string> Headers, int DelayMs);

/// <summary>
/// The stand-in vendor `susu-plugin test` points a plugin at: a minimal HTTP/1.1 server on a loopback
/// TCP port (not HttpListener, which needs a URL reservation), one request per connection. It only
/// answers; the plugin still goes through the real sandbox, IPC, broker and network layer to reach it.
/// </summary>
internal sealed class LoopbackVendor : IDisposable
{
    private readonly TcpListener listener;
    private readonly Func<int, VendorResponse> responder;
    private readonly CancellationTokenSource cts = new();
    private readonly List<VendorRequest> seen = [];
    private int count;

    public string Origin { get; }

    /// <param name="responder">Maps the 0-based request index to the answer.</param>
    public LoopbackVendor(Func<int, VendorResponse> responder)
    {
        this.responder = responder;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        _ = Task.Run(AcceptLoop);
    }

    public IReadOnlyList<VendorRequest> Requests { get { lock (seen) return [.. seen]; } }

    private async Task AcceptLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cts.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = Task.Run(() => Handle(client));
        }
    }

    private async Task Handle(TcpClient client)
    {
        using var _ = client;
        try
        {
            await using var stream = client.GetStream();
            var request = await ReadRequest(stream);
            if (request is null) return;
            int index;
            lock (seen) { index = count++; seen.Add(request); }
            var response = responder(index);
            if (response.DelayMs > 0) await Task.Delay(response.DelayMs, cts.Token);
            var head = new StringBuilder();
            head.Append($"HTTP/1.1 {response.Status} {Reason(response.Status)}\r\n");
            head.Append($"Content-Type: {response.ContentType}\r\nContent-Length: {response.Body.Length}\r\nConnection: close\r\n");
            foreach (var (name, value) in response.Headers) head.Append($"{name}: {value}\r\n");
            head.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cts.Token);
            await stream.WriteAsync(response.Body, cts.Token);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException) { }
    }

    private static async Task<VendorRequest?> ReadRequest(NetworkStream stream)
    {
        var headerBytes = new List<byte>();
        var one = new byte[1];
        while (!EndsWithBlankLine(headerBytes))
        {
            if (await stream.ReadAsync(one) == 0) return null;
            headerBytes.Add(one[0]);
            if (headerBytes.Count > 64 * 1024) return null;
        }
        var lines = Encoding.ASCII.GetString([.. headerBytes]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        byte[] body = [];
        if (headers.TryGetValue("Content-Length", out string? text) && int.TryParse(text, out int length) && length > 0)
        {
            body = new byte[length];
            int read = 0;
            while (read < length)
            {
                int n = await stream.ReadAsync(body.AsMemory(read));
                if (n == 0) break;
                read += n;
            }
        }
        return new VendorRequest(first[0], first[1], headers, body);
    }

    private static bool EndsWithBlankLine(List<byte> b)
        => b.Count >= 4 && b[^4] == '\r' && b[^3] == '\n' && b[^2] == '\r' && b[^1] == '\n';

    private static string Reason(int status) => status switch
    {
        200 => "OK", 201 => "Created", 204 => "No Content", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
        404 => "Not Found", 408 => "Request Timeout", 429 => "Too Many Requests", 500 => "Internal Server Error",
        502 => "Bad Gateway", 503 => "Service Unavailable", 504 => "Gateway Timeout", _ => "Status",
    };

    public void Dispose()
    {
        cts.Cancel();
        try { listener.Stop(); } catch (SocketException) { }
    }
}
