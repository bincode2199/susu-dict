using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Susu.Testing;

/// <summary>
/// A minimal forward HTTP proxy over a raw <see cref="TcpListener"/> (plain HTTP target only - no
/// CONNECT/TLS tunnelling, which the F05 proxy tests do not need): parses the absolute-URI request
/// line/headers a real proxy-aware HttpClient sends, optionally requires Basic Proxy-Authorization
/// (407 challenge, same as a real proxy), then makes its own request to the target and relays the
/// response back. Records every request it actually received, for asserting requests really went
/// through it and that no other channel saw the same credentials.
/// </summary>
public sealed class LoopbackProxyServer : IDisposable
{
    public sealed record ProxyRequest(string Method, string AbsoluteUri, string? ProxyAuthorizationHeader);

    private readonly TcpListener listener;
    private readonly string? requireUser;
    private readonly string? requirePassword;
    private readonly CancellationTokenSource cts = new();
    private readonly Task loop;
    private readonly System.Collections.Concurrent.ConcurrentQueue<ProxyRequest> received = new();

    public int Port { get; }
    public string Origin => $"http://127.0.0.1:{Port}";
    public IReadOnlyCollection<ProxyRequest> Received => received;

    public LoopbackProxyServer(string? requireUser = null, string? requirePassword = null)
    {
        this.requireUser = requireUser;
        this.requirePassword = requirePassword;
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
            var (method, absoluteUri, headers, body) = await ReadRequestAsync(stream, cts.Token);
            if (absoluteUri is null) return;
            headers.TryGetValue("Proxy-Authorization", out string? auth);
            received.Enqueue(new ProxyRequest(method, absoluteUri, auth));

            if (requireUser is not null)
            {
                string expected = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes($"{requireUser}:{requirePassword}"));
                if (auth != expected)
                {
                    byte[] challenge = Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test-proxy\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(challenge, cts.Token);
                    return;
                }
            }

            var target = new Uri(absoluteUri);
            using var forward = new TcpClient();
            await forward.ConnectAsync(target.Host, target.Port, cts.Token);
            await using var forwardStream = forward.GetStream();
            var requestLine = $"{method} {target.PathAndQuery} HTTP/1.1\r\nHost: {target.Authority}\r\nConnection: close\r\n\r\n";
            await forwardStream.WriteAsync(Encoding.ASCII.GetBytes(requestLine), cts.Token);
            if (body.Length > 0) await forwardStream.WriteAsync(body, cts.Token);
            await forwardStream.CopyToAsync(stream, cts.Token);
        }
        catch (IOException) { } catch (ObjectDisposedException) { } catch (SocketException) { }
    }

    private static async Task<(string Method, string? AbsoluteUri, Dictionary<string, string> Headers, byte[] Body)> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var headerBytes = new List<byte>();
        var one = new byte[1];
        while (headerBytes.Count < 4 || !(headerBytes[^4] == '\r' && headerBytes[^3] == '\n' && headerBytes[^2] == '\r' && headerBytes[^1] == '\n'))
        {
            int read = await stream.ReadAsync(one, ct);
            if (read == 0) return ("", null, [], []);
            headerBytes.Add(one[0]);
            if (headerBytes.Count > 64 * 1024) return ("", null, [], []);
        }
        string headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestLine = lines[0].Split(' ');
        string method = requestLine[0];
        string? uri = requestLine.Length > 1 ? requestLine[1] : null;
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
            while (total < length) { int r = await stream.ReadAsync(body.AsMemory(total, length - total), ct); if (r == 0) break; total += r; }
        }
        return (method, uri, headers, body);
    }

    public void Dispose()
    {
        cts.Cancel();
        listener.Stop();
        try { loop.Wait(1000); } catch (AggregateException) { }
        cts.Dispose();
    }
}
