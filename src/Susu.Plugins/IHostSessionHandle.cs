namespace Susu.Plugins;

/// <summary>
/// The slice of <see cref="HostSession"/> that <see cref="Supervisor"/> needs to manage a plugin-host
/// child's lifecycle. Pulled out so Supervisor's restart/backoff/threshold-stop timing (F04.3) can be
/// unit-tested against a fast in-memory double instead of a real AppContainer process.
/// </summary>
public interface IHostSessionHandle : IDisposable
{
    /// <summary>Raised when the reader loop ends (child disconnected, crashed or was told to shut down).</summary>
    event Action? Disconnected;
}
