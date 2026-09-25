using Susu.Abstractions;
using Susu.Domain;
using Susu.Net;

namespace Susu.Plugins;

/// <summary>
/// Composition-root piece: keeps one live <see cref="NetworkBroker"/> built from F02's current
/// NetworkSettings (proxy mode/host/port/username; password from <see cref="ISecretStore"/>) and
/// rebuilds it whenever settings change, so a proxy edit takes effect for the next request without
/// restarting the plugin host. <see cref="Broker"/> reads <see cref="Current"/> on every call instead
/// of fixing one NetworkBroker at construction time.
///
/// The set of explicitly-approved local origins (Broker.ApproveLocalOrigin) is carried across rebuilds
/// via one stable <see cref="ApprovedLocalOrigins"/> instance - a proxy change must never silently
/// un-approve Ollama/AnkiConnect.
/// </summary>
public sealed class NetworkBrokerProvider : IDisposable
{
    private readonly ISettingsStore settings;
    private readonly ISecretStore secretStore;
    private readonly ApprovedLocalOrigins localOrigins = new();
    private readonly Lock gate = new();
    private NetworkBroker current;

    public NetworkBrokerProvider(ISettingsStore settings, ISecretStore secretStore)
    {
        this.settings = settings;
        this.secretStore = secretStore;
        current = Build(settings.State.Effective.Network);
        settings.Changed += OnSettingsChanged;
    }

    public NetworkBroker Current { get { lock (gate) return current; } }

    private void OnSettingsChanged(SettingsState state)
    {
        var next = Build(state.Effective.Network);
        NetworkBroker previous;
        lock (gate) { previous = current; current = next; }
        previous.Dispose(); // safe: in-flight requests on the old HttpClient already hold their own reference chain
    }

    private NetworkBroker Build(NetworkSettings network)
    {
        // Loopback targets bypass the proxy (CFG05); system mode is the OS default proxy wrapped the same way.
        return new NetworkBroker(new NetworkBrokerOptions
        {
            Proxy = ProxyFactory.ForBroker(network, secretStore),
            Timeout = TimeSpan.FromSeconds(Math.Max(1, network.AiTimeoutSeconds)),
            LocalOrigins = localOrigins,
        });
    }

    public void Dispose()
    {
        settings.Changed -= OnSettingsChanged;
        current.Dispose();
    }
}
