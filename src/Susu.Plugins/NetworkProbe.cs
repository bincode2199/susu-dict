using System.Diagnostics;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Net;

namespace Susu.Plugins;

/// <summary>
/// SetNetwork "测试连接" (F07.3, CFG05): one unauthenticated GET per path through a <see cref="NetworkBroker"/>
/// built from the proxy settings being edited, exactly the broker and proxy rules a plugin call would get (no
/// separate HttpClient). No credential is attached, so nothing but the proxy password (from secrets.dat) leaves
/// the machine. Each path gets its own result; loopback origins go direct and are reported as local.
/// </summary>
public static class NetworkProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<IReadOnlyList<NetworkProbeResult>> RunAsync(NetworkSettings network, ISecretStore secrets, IReadOnlyList<NetworkProbeTarget> targets,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var local = new ApprovedLocalOrigins();
        var proxy = ProxyFactory.ForBroker(network, secrets);
        var uris = targets.Select(t => Uri.TryCreate(t.Origin + "/", UriKind.Absolute, out var uri) ? uri : null).ToList();
        foreach (var uri in uris)
            if (uri is not null && ProxyFactory.IsLoopback(uri)) local.Approve(NetworkBroker.Origin(uri));
        using var broker = new NetworkBroker(new NetworkBrokerOptions { Proxy = proxy, Timeout = timeout ?? Timeout, LocalOrigins = local });

        async Task<NetworkProbeResult> ProbeAsync(NetworkProbeTarget target, Uri? uri)
        {
            if (uri is null) return new(target.Origin, target.Services, "direct", false, ErrorKind.BadResponse, null, 0);
            bool loopback = ProxyFactory.IsLoopback(uri);
            string route = loopback ? "local" : proxy is not null && !proxy.IsBypassed(uri) && proxy.GetProxy(uri) is { } via && via != uri ? "proxy" : "direct";
            var request = new BrokerHttpRequest("GET", uri, [], RequestBody.None, [], [], (_, _) => throw new InvalidOperationException("no credentials in a network test"),
                null, ResponseKind.Text, [], null, LocalOriginApproved: loopback);
            var started = Stopwatch.GetTimestamp();
            var outcome = await broker.ExecuteAsync(request, cancellationToken);
            long elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return outcome switch
            {
                BrokerSuccess success => new(target.Origin, target.Services, route, true, null, success.Response.Status, elapsed),
                BrokerFailure failure => new(target.Origin, target.Services, route, false, ErrorKinds.FromPlugin(failure.Kind), null, elapsed),
                _ => new(target.Origin, target.Services, route, false, ErrorKind.BadResponse, null, elapsed),
            };
        }

        return await Task.WhenAll(targets.Select((t, i) => ProbeAsync(t, uris[i])));
    }
}
