using System.Net;
using Susu.Abstractions;
using Susu.Domain;

namespace Susu.Plugins;

/// <summary>
/// Builds the broker's proxy configuration from F02's NetworkSettings + SecretStore (PLAN §5/network
/// settings section): mode/host/port/username come from settings.yaml, the password comes from
/// secrets.dat under <see cref="NetworkSettings.ProxyAccountId"/> like any other credential. Never logs
/// or otherwise surfaces the password; <see cref="ProxyResult.ToString"/> and any diagnostic text this
/// type produces omit it by construction (there is nowhere in this type the password is ever formatted
/// into a string other than the WebProxy.Credentials object itself, which .NET does not log).
/// </summary>
public static class ProxyFactory
{
    /// <summary>Local secret name for the proxy password under the "proxy" account (same secrets.dat
    /// storage path every other credential uses - PLAN 5.4).</summary>
    public const string ProxyPasswordSecret = "password";

    public readonly record struct ProxyResult(IWebProxy? Proxy, bool UseSystemProxy);

    public static ProxyResult Build(NetworkSettings settings, ISecretStore secretStore)
    {
        switch (settings.ProxyMode)
        {
            case ProxyMode.None:
                return new ProxyResult(null, false);
            case ProxyMode.System:
                return new ProxyResult(null, true); // SocketsHttpHandler resolves the OS default itself
            case ProxyMode.Http or ProxyMode.Socks5:
                if (string.IsNullOrWhiteSpace(settings.ProxyHost) || settings.ProxyPort is <= 0 or > 65535)
                    return new ProxyResult(null, false); // misconfigured: no proxy rather than a broken one
                string scheme = settings.ProxyMode == ProxyMode.Socks5 ? "socks5" : "http";
                var proxy = new WebProxy(new Uri($"{scheme}://{settings.ProxyHost}:{settings.ProxyPort}"));
                if (!string.IsNullOrEmpty(settings.ProxyUsername) && secretStore.TryRead(NetworkSettings.ProxyAccountId, ProxyPasswordSecret, out string password))
                    proxy.Credentials = new NetworkCredential(settings.ProxyUsername, password);
                return new ProxyResult(proxy, false);
            default:
                return new ProxyResult(null, false);
        }
    }

    /// <summary>
    /// The proxy a production broker uses (F07.3, CFG05): <see cref="Build"/>, with loopback targets always sent
    /// directly. A local service (Ollama, AnkiConnect, a local OpenAI-compatible server) keeps working when the
    /// proxy is down or cannot reach this machine's loopback. System mode wraps the OS default proxy the same way.
    /// </summary>
    public static IWebProxy? ForBroker(NetworkSettings settings, ISecretStore secretStore)
    {
        var (proxy, useSystemProxy) = Build(settings, secretStore);
        IWebProxy? inner = proxy ?? (useSystemProxy ? HttpClient.DefaultProxy : null);
        return inner is null ? null : new LoopbackBypassProxy(inner);
    }

    /// <summary>True when <paramref name="uri"/> never goes through a proxy (loopback host).</summary>
    public static bool IsLoopback(Uri uri) => uri.IsLoopback || uri.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    private sealed class LoopbackBypassProxy(IWebProxy inner) : IWebProxy
    {
        public ICredentials? Credentials { get => inner.Credentials; set => inner.Credentials = value; }
        public Uri? GetProxy(Uri destination) => IsLoopback(destination) ? null : inner.GetProxy(destination);
        public bool IsBypassed(Uri host) => IsLoopback(host) || inner.IsBypassed(host);
    }
}
