namespace Susu.Plugins;

/// <summary>A call that has been sent to the plugin host and has not completed. Capability is the plugin-API capability name; JobId is the host job that owns it.</summary>
public sealed record InFlightCall(string PluginId, string Capability, string RequestId, string JobId, int CallId);

/// <summary>
/// The slice of <see cref="HostSession"/> that <see cref="Supervisor"/> needs to manage a plugin-host
/// child's lifecycle. Pulled out so Supervisor's restart/backoff/threshold-stop timing (F04.3) can be
/// unit-tested against a fast in-memory double instead of a real AppContainer process.
/// </summary>
public interface IHostSessionHandle : IDisposable
{
    /// <summary>Raised when the reader loop ends (child disconnected, crashed or was told to shut down).</summary>
    event Action? Disconnected;

    /// <summary>Calls sent and not yet completed, optionally only those of one package (F16.2: what an install or uninstall would cancel).</summary>
    IReadOnlyList<InFlightCall> InFlightCalls(string? pluginId = null);

    /// <summary>Cancels every in-flight call of one package. Each call's result completes as a clean "cancelled" failure; nothing is replayed. Returns how many were cancelled.</summary>
    int Interrupt(string pluginId);
}
