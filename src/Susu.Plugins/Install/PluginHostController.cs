using Susu.Abstractions;

namespace Susu.Plugins.Install;

/// <summary>
/// F16.2 restart of the plugin host after an install or uninstall (ARCHITECTURE 10: "重启插件宿主", without restarting the application). The live
/// session is detached from the supervisor, so the next call launches a fresh one that loads the packages as they are now. In the detached session the
/// changed package's in-flight calls are cancelled (each completes as a clean "cancelled" failure and is never replayed); calls of other packages are
/// left to finish, and the session is disposed once it has drained or after <c>drainTimeout</c>.
/// </summary>
public sealed class PluginHostController<TSession>(Supervisor<TSession> supervisor, TimeSpan? drainTimeout = null, Action<string>? diagnostic = null) : IPluginHostControl, IDisposable
    where TSession : class, IHostSessionHandle
{
    private readonly TimeSpan timeout = drainTimeout ?? TimeSpan.FromSeconds(30);
    private readonly object gate = new();
    private readonly List<TSession> retiring = [];
    private readonly List<Task> drains = [];
    private bool disposed;

    public IReadOnlyList<PluginTaskInfo> InFlight(string packageId)
    {
        var calls = new List<InFlightCall>();
        if (supervisor.TryGetCurrent(out var current) && current is not null) calls.AddRange(current.InFlightCalls(packageId));
        lock (gate) foreach (var old in retiring) calls.AddRange(old.InFlightCalls(packageId));
        return [.. calls.GroupBy(c => c.Capability, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new PluginTaskInfo(g.Key, g.Count()))];
    }

    public int Restart(string packageId)
    {
        var old = supervisor.Recycle();
        if (old is null) return 0;
        int cancelled = 0;
        try { cancelled = old.Interrupt(packageId); }
        catch (Exception e) when (e is not OutOfMemoryException) { diagnostic?.Invoke("plugin-host.interrupt-failed " + e.GetType().Name); }
        lock (gate)
        {
            if (disposed) { SafeDispose(old); return cancelled; }
            retiring.Add(old);
            drains.Add(Task.Run(() => DrainAndDispose(old)));
        }
        return cancelled;
    }

    /// <summary>Completes when every detached session has been disposed (tests, shutdown).</summary>
    public Task Retired()
    {
        lock (gate) return Task.WhenAll(drains);
    }

    private async Task DrainAndDispose(TSession old)
    {
        var until = DateTime.UtcNow + timeout;
        try
        {
            while (DateTime.UtcNow < until && old.InFlightCalls().Count > 0) await Task.Delay(20).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        SafeDispose(old);
        lock (gate) retiring.Remove(old);
    }

    private void SafeDispose(TSession session)
    {
        try { session.Dispose(); }
        catch (Exception e) when (e is not OutOfMemoryException) { diagnostic?.Invoke("plugin-host.retire-failed " + e.GetType().Name); }
    }

    public void Dispose()
    {
        List<TSession> rest;
        lock (gate) { disposed = true; rest = [.. retiring]; retiring.Clear(); }
        foreach (var session in rest) SafeDispose(session);
    }
}

/// <summary>
/// F16.2 update check: asks the configured <see cref="IPluginUpdateSource"/> about every user-installed package, fetches what it offers into a download
/// folder, and hands each file to the installer's <see cref="IPluginInstallService.StageUpdate"/>. Nothing is applied here: the installed version keeps
/// running and a staged update waits for the user. A source error is a failure for that package, never "up to date".
/// </summary>
public sealed class PluginUpdateService(IPluginInstallService installer, IPluginUpdateSource? source, string downloadFolder) : IPluginUpdateService
{
    private readonly SemaphoreSlim serial = new(1, 1);

    public bool Available => source is not null;

    public async Task<PluginUpdateCheckOutcome> CheckAsync(CancellationToken cancellationToken)
    {
        if (source is null) return new PluginUpdateCheckOutcome(0, 0, [], []);
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false); // one check at a time (ARCHITECTURE 10: serial low-priority schedule)
        try
        {
            int staged = 0;
            var upToDate = new List<string>();
            var failures = new List<PluginUpdateFailure>();
            var installed = installer.Installed();
            foreach (var plugin in installed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PluginUpdateCheck check;
                try { check = await source.CheckAsync(plugin.Id, plugin.Version, cancellationToken).ConfigureAwait(false); }
                catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { failures.Add(new(plugin.Id, "check-failed")); continue; }
                if (check.Status == PluginUpdateStatus.Failed) { failures.Add(new(plugin.Id, check.ErrorCode ?? "check-failed")); continue; }
                if (check.Status == PluginUpdateStatus.UpToDate || check.Offer is null) { upToDate.Add(plugin.Id); continue; }
                var offer = check.Offer;
                if (offer.PackageId != plugin.Id || !PackageVersion.TryParse(offer.Version, out var offered) || !PackageVersion.TryParse(plugin.Version, out var current) || !(offered > current))
                { failures.Add(new(plugin.Id, "offer-invalid")); continue; }
                if (installer.StagedUpdates().Any(s => s.Id == plugin.Id && s.Version == offer.Version)) { staged++; continue; } // already waiting
                string? file = null;
                Directory.CreateDirectory(downloadFolder);
                string folder = Path.Combine(downloadFolder, Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(folder);
                    file = await source.FetchAsync(offer, folder, cancellationToken).ConfigureAwait(false);
                    var preview = installer.StageUpdate(file);
                    if (preview.Ok && preview.Id == plugin.Id) staged++;
                    else
                    {
                        if (preview.Ok && preview.Token is { } token) installer.Discard(token); // a package that names another id is not the update that was offered
                        failures.Add(new(plugin.Id, preview.Ok ? "offer-invalid" : "rejected:" + (preview.Issues.FirstOrDefault()?.Code ?? "unknown")));
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { failures.Add(new(plugin.Id, "fetch-failed")); }
                finally { Susu.Plugins.Install.SafeUnzip.TryDelete(folder); }
            }
            return new PluginUpdateCheckOutcome(installed.Count, staged, [.. upToDate], [.. failures]);
        }
        finally { serial.Release(); }
    }
}
