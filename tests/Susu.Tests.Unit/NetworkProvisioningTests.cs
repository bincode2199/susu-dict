using Susu.Abstractions;
using Susu.Domain;
using Susu.Net;
using Susu.Plugins;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>In-memory ISecretStore double (no DPAPI dependency) for proxy/provider tests.</summary>
internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<(string, string), string> values = [];
    public bool Has(string accountId, string secretName) => values.ContainsKey((accountId, secretName));
    public IReadOnlyList<string> Names(string accountId) => [.. values.Keys.Where(k => k.Item1 == accountId).Select(k => k.Item2)];
    public void Write(string accountId, string secretName, ReadOnlySpan<char> value) => values[(accountId, secretName)] = value.ToString();
    public bool Delete(string accountId, string secretName) => values.Remove((accountId, secretName));
    public bool TryRead(string accountId, string secretName, out string value) => values.TryGetValue((accountId, secretName), out value!);
}

/// <summary>Minimal ISettingsStore double that fires Changed on demand, for NetworkBrokerProvider tests.</summary>
internal sealed class FakeSettingsStore(AppSettings initial) : ISettingsStore
{
    public SettingsState State { get; private set; } = new(initial, 1, "h1", []);
    public event Action<SettingsState>? Changed;
    public void PushNetwork(NetworkSettings network)
    {
        State = State with { Effective = State.Effective with { Network = network }, Revision = State.Revision + 1 };
        Changed?.Invoke(State);
    }
    public SaveResult Save(AppSettings proposed, long expectedRevision, string expectedFileHash) => throw new NotSupportedException();
}

public class ProxyFactoryTests
{
    private static NetworkSettings Settings(ProxyMode mode, string host = "", int port = 0, string user = "") => new(mode, host, port, user, 30);

    [Fact]
    public void Mode_none_yields_no_proxy()
    {
        var result = ProxyFactory.Build(Settings(ProxyMode.None), new InMemorySecretStore());
        Assert.Null(result.Proxy);
        Assert.False(result.UseSystemProxy);
    }

    [Fact]
    public void Mode_system_yields_no_explicit_proxy_but_flags_system_default()
    {
        var result = ProxyFactory.Build(Settings(ProxyMode.System), new InMemorySecretStore());
        Assert.Null(result.Proxy);
        Assert.True(result.UseSystemProxy);
    }

    [Fact]
    public void Mode_http_without_a_host_yields_no_proxy_rather_than_a_broken_one()
    {
        var result = ProxyFactory.Build(Settings(ProxyMode.Http, host: "", port: 0), new InMemorySecretStore());
        Assert.Null(result.Proxy);
    }

    [Fact]
    public void Mode_http_with_credentials_builds_a_WebProxy_and_never_stringifies_the_password()
    {
        var secrets = new InMemorySecretStore();
        secrets.Write(NetworkSettings.ProxyAccountId, ProxyFactory.ProxyPasswordSecret, "s3cr3t-password");
        var result = ProxyFactory.Build(Settings(ProxyMode.Http, host: "proxy.example", port: 8080, user: "alice"), secrets);
        var proxy = Assert.IsType<System.Net.WebProxy>(result.Proxy);
        Assert.Equal("proxy.example", proxy.Address!.Host);
        Assert.Equal(8080, proxy.Address.Port);
        var credential = Assert.IsType<System.Net.NetworkCredential>(proxy.Credentials);
        Assert.Equal("s3cr3t-password", credential.Password); // the real value is reachable by the caller...
        Assert.DoesNotContain("s3cr3t-password", proxy.ToString()); // ...but never appears in any diagnostic text
        Assert.DoesNotContain("s3cr3t-password", credential.ToString());
        Assert.DoesNotContain("s3cr3t-password", proxy.Address.ToString());
    }
}

public class NetworkBrokerProviderTests
{
    private static AppSettings BaseSettings(NetworkSettings network) => BuiltInCatalog.Defaults() with { Network = network };

    [Fact]
    public void Rebuilds_the_broker_when_network_settings_change()
    {
        var store = new FakeSettingsStore(BaseSettings(new NetworkSettings(ProxyMode.None, "", 0, "", 30)));
        using var provider = new NetworkBrokerProvider(store, new InMemorySecretStore());
        var before = provider.Current;

        store.PushNetwork(new NetworkSettings(ProxyMode.Http, "proxy.example", 8080, "", 30));

        Assert.NotSame(before, provider.Current);
    }

    [Fact]
    public void Approved_local_origins_survive_a_rebuild()
    {
        var store = new FakeSettingsStore(BaseSettings(new NetworkSettings(ProxyMode.None, "", 0, "", 30)));
        using var provider = new NetworkBrokerProvider(store, new InMemorySecretStore());
        provider.Current.LocalOrigins.Approve("http://127.0.0.1:11434");

        store.PushNetwork(new NetworkSettings(ProxyMode.Http, "proxy.example", 8080, "", 30));

        Assert.True(provider.Current.LocalOrigins.Contains("http://127.0.0.1:11434"));
    }
}

public class ProxyRoutingTests
{
    [Fact]
    public async Task A_request_actually_travels_through_the_configured_proxy_with_credentials()
    {
        using var target = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"ok":true}"""));
        using var proxy = new LoopbackProxyServer(requireUser: "alice", requirePassword: "s3cr3t-password");

        var secrets = new InMemorySecretStore();
        secrets.Write(NetworkSettings.ProxyAccountId, ProxyFactory.ProxyPasswordSecret, "s3cr3t-password");
        var network = new NetworkSettings(ProxyMode.Http, "127.0.0.1", proxy.Port, "alice", 30);
        var (webProxy, _) = ProxyFactory.Build(network, secrets);

        var options = new NetworkBrokerOptions { Proxy = webProxy, Timeout = TimeSpan.FromSeconds(10) };
        options.LocalOrigins.Approve(target.Origin); // target is plain HTTP loopback
        using var broker = new NetworkBroker(options);

        var request = new BrokerHttpRequest("GET", new Uri(target.Origin + "/x"), [], RequestBody.None, [], [],
            (_, _) => throw new InvalidOperationException(), null, ResponseKind.Json, [], null, LocalOriginApproved: true);
        var outcome = await broker.ExecuteAsync(request, TestContext.Current.CancellationToken);

        var success = Assert.IsType<BrokerSuccess>(outcome);
        Assert.True(success.Response.JsonBody!["ok"]!.GetValue<bool>());
        Assert.NotEmpty(proxy.Received); // the request really went through the proxy, not directly to the target
        Assert.Contains(proxy.Received, r => r.AbsoluteUri.Contains(target.Origin));
    }
}
