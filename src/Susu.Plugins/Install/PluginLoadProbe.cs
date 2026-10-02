using Susu.Abstractions;

namespace Susu.Plugins.Install;

/// <summary>
/// The activation transaction's load probe (ARCHITECTURE 10: "重启插件宿主→加载探针"): starts a throwaway sandboxed plugin host that may read the installed
/// packages folder, loads the just-activated package from its real directory, and shuts it down. A package that parses but cannot load (syntax error,
/// over budget, missing entry) fails here, so the installer restores the previous version. It never invokes a capability: that would need a request
/// the host cannot invent for an arbitrary package.
/// </summary>
public static class PluginLoadProbe
{
    public static Func<HealthRequest, HealthResult> Create(Func<HostSession.Options> options, TimeSpan? loadTimeout = null)
        => request =>
        {
            try
            {
                using var session = HostSession.Start(options());
                try
                {
                    var loaded = session.Load(request.PackageId, request.Directory, timeoutMs: (int)(loadTimeout ?? TimeSpan.FromSeconds(8)).TotalMilliseconds, entry: request.Manifest.Entry);
                    return loaded.Ok ? new HealthResult(true) : new HealthResult(false, loaded.Error ?? "load failed");
                }
                finally { session.Shutdown(2000); }
            }
            catch (Exception e) when (e is not OutOfMemoryException) { return new HealthResult(false, e.GetType().Name); }
        };
}
