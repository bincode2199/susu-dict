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
    // Failure evidence for CI: whether the main process sees the entry file and which DACL entries the version folder and entry carry.
    private static string Describe(HealthRequest request)
    {
        try
        {
            string entry = Path.Combine(request.Directory, request.Manifest.Entry);
            string Acl(FileSystemInfo i) => i is DirectoryInfo d ? d.GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access)
                : ((FileInfo)i).GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);
            return $" [dir={request.Directory} exists={Directory.Exists(request.Directory)} entry={request.Manifest.Entry} entryExists={File.Exists(entry)} dirAcl={Acl(new DirectoryInfo(request.Directory))} entryAcl={(File.Exists(entry) ? Acl(new FileInfo(entry)) : "-")}]";
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return " [describe failed: " + e.GetType().Name + "]"; }
    }

    public static Func<HealthRequest, HealthResult> Create(Func<HostSession.Options> options, TimeSpan? loadTimeout = null)
        => request =>
        {
            try
            {
                using var session = HostSession.Start(options());
                try
                {
                    var loaded = session.Load(request.PackageId, request.Directory, timeoutMs: (int)(loadTimeout ?? TimeSpan.FromSeconds(8)).TotalMilliseconds, entry: request.Manifest.Entry);
                    return loaded.Ok ? new HealthResult(true) : new HealthResult(false, (loaded.Error ?? "load failed") + Describe(request));
                }
                finally { session.Shutdown(2000); }
            }
            catch (Exception e) when (e is not OutOfMemoryException) { return new HealthResult(false, e.GetType().Name + ": " + e.Message); }
        };
}
