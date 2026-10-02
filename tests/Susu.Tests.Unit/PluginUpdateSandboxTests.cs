using Susu.Abstractions;
using Susu.Jobs;
using Susu.Plugins;
using Susu.Plugins.Install;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F16.2 in the real sandbox (UPD03, UPD04, X05): a package installed under a folder OUTSIDE the host folder is loaded by the sandboxed plugin host
/// (read grant per session, revoked afterwards), a bad update rolls back by the real load probe, a good update restarts the host and cancels the
/// package's in-flight call cleanly, KV written by one version is read by the next and survives uninstall, and nothing is left running or granted.
/// </summary>
public sealed class PluginUpdateSandboxTests : IDisposable
{
    private const string Id = "com.example.echo";
    private readonly string temp = TestTemp.NewDir("susu-update-sbx");
    private Database? db;

    public void Dispose() => db?.Dispose();

    private static string? FindPublish()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish");
            if (File.Exists(Path.Combine(candidate, "susu.exe"))) return candidate;
        }
        return null;
    }

    private static string Source(string tag) => "export default {\n" +
        $"  async translate(req, ctx) {{ await ctx.$store.set('counter', 'from-{tag}'); return {{ text: '{tag}:' + req.text }}; }},\n" +
        $"  async read(req, ctx) {{ const v = await ctx.$store.get('counter'); return {{ text: '{tag} sees ' + v }}; }},\n" +
        "  async hang() { await new Promise(() => {}); },\n};\n";

    private string Zip(string name, string version, string main)
        => PackageFactory.Zip(temp, name, PackageFactory.Files(PackageFactory.Manifest(Id, version), main), PackageFactory.SeedB);

    [Fact]
    public async Task A_user_folder_package_loads_updates_keeps_its_data_and_cancels_in_flight_calls_in_the_real_sandbox()
    {
        string? publish = FindPublish();
        if (publish is null) return;
        string staged = Path.Combine(temp, "host");
        Directory.CreateDirectory(staged);
        foreach (string file in Directory.EnumerateFiles(publish))
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(staged, Path.GetFileName(file)));
        string userRoot = Path.Combine(temp, "user-plugins"); // deliberately outside the host folder: the sandbox reaches it only through the session grant
        db = Database.Open(Path.Combine(temp, "susu.db"));
        var store = new PluginInstallationRepository(db);
        var kv = new PluginKvRepository(db);
        var ct = TestContext.Current.CancellationToken;

        PluginInstaller installer = null!;
        var options = new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", MakeBroker: () => new Broker { PluginKv = kv });
        HostSession.Options Granted() => options with { ExtraReadRoots = Directory.Exists(installer.PackagesFolder) ? [installer.PackagesFolder] : null };
        using var supervisor = new Supervisor<HostSession>(() =>
        {
            var session = HostSession.Start(Granted());
            foreach (var package in installer.ActivePackages())
            {
                var loaded = session.Load(package.Id, package.Directory, entry: package.Entry);
                Assert.True(loaded.Ok, loaded.Error);
            }
            return session;
        }, SystemClock.Instance);
        using var controller = new PluginHostController<HostSession>(supervisor, TimeSpan.FromSeconds(5));
        installer = new PluginInstaller(userRoot, store, new BuiltInPackages([]), HostKeyring.Empty,
            PluginLoadProbe.Create(() => options with { ExtraReadRoots = [installer.PackagesFolder] }), host: controller, removeData: id => kv.DeletePackage(id));

        async Task<(Susu.Contracts.IpcMessageType Type, string Text)> Call(string capability, string request = "{\"text\":\"hi\"}")
        {
            var session = supervisor.Acquire() ?? throw new InvalidOperationException("no host");
            try
            {
                var (_, _, task) = session.Invoke(Id, capability, request, "job-" + capability, origins: []);
                var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                return (envelope.Type, envelope.Payload?.GetRawText() ?? "");
            }
            finally { supervisor.Release(); }
        }

        // 1. install v1 from a folder the sandbox could not read by itself: the probe loads it for real, then the host serves it
        var preview = installer.Preview(Zip("v1.susuext", "1.0.0", Source("v1")));
        Assert.True(preview.Ok, string.Join(",", preview.Issues.Select(i => i.Code)));
        var installed = installer.Install(preview.Token!);
        Assert.True(installed.Ok, installed.Error);
        var (type1, text1) = await Call("translate");
        Assert.Equal(Susu.Contracts.IpcMessageType.Completed, type1);
        Assert.Contains("v1:hi", text1);
        Assert.Equal("\"from-v1\"", kv.Get(Id, "counter")); // $store went to plugin_kv under the package id

        // 2. a call stays in flight; the page can see it before an update would cancel it
        var running = supervisor.Acquire()!;
        var (_, _, hang) = running.Invoke(Id, "hang", "{}", "job-hang", origins: []);
        Assert.True(await Eventually.WaitAsync(() => controller.InFlight(Id).Count > 0));
        Assert.Equal([new PluginTaskInfo("hang", 1)], controller.InFlight(Id));
        int oldPid = running.ChildPid;

        // 3. a bad update (cannot load) is refused by the real load probe; v1 stays active, the in-flight call is untouched, nothing restarted
        var broken = installer.Preview(Zip("v11.susuext", "1.1.0", "export default { async translate( {{{ ;\n"));
        Assert.True(broken.Ok);
        var failed = installer.Install(broken.Token!);
        Assert.Equal("install.healthFailed", failed.Error);
        Assert.Equal("1.0.0", store.Active(Id)!.Version);
        Assert.False(hang.IsCompleted);
        Assert.Equal(1, controller.InFlight(Id).Sum(t => t.Count));
        Assert.Contains("v1 sees from-v1", (await Call("read")).Text);

        // 4. a good update through the staging path: the host restarts, the in-flight call fails cleanly as cancelled, the new version runs, KV carried over
        var update = installer.StageUpdate(Zip("v12.susuext", "1.2.0", Source("v12")));
        Assert.True(update.Ok);
        var applied = installer.Install(update.Token!);
        Assert.True(applied.Ok, applied.Error);
        Assert.Equal(1, applied.Interrupted);
        var hangResult = await hang.WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(Susu.Contracts.IpcMessageType.Failed, hangResult.Type);
        Assert.Contains("cancelled", hangResult.Payload!.Value.GetRawText());
        supervisor.Release(); // the Acquire taken for the hang call
        var (typeNew, textNew) = await Call("read");
        Assert.Equal(Susu.Contracts.IpcMessageType.Completed, typeNew);
        Assert.Contains("v12 sees from-v1", textNew);

        // 5. the old plugin-host process is gone and the new one is a different process (a restart, not a reuse)
        await controller.Retired().WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.True(await GoneAsync(oldPid, ct), "the replaced plugin host process is still running");
        Assert.NotEqual(oldPid, supervisor.Acquire()!.ChildPid);
        supervisor.Release();

        // 6. uninstall keeps the data by default; the host no longer has the package; reinstalling sees the old data; removal is by explicit choice
        Assert.True(installer.Uninstall(Id).Ok);
        Assert.Equal("\"from-v1\"", kv.Get(Id, "counter"));
        Assert.Equal(Susu.Contracts.IpcMessageType.Failed, (await Call("read")).Type);
        Assert.True(installer.Install(installer.Preview(Zip("v13.susuext", "1.3.0", Source("v13"))).Token!).Ok);
        Assert.Contains("v13 sees from-v1", (await Call("read")).Text);
        Assert.True(installer.Uninstall(Id, removeData: true).Ok);
        Assert.Null(kv.Get(Id, "counter"));

        // 7. no sandbox grant is left on the packages folder once the sessions are gone (X05: the container gets only what its session asked for)
        await controller.Retired().WaitAsync(TimeSpan.FromSeconds(20), ct);
        supervisor.Dispose();
        foreach (string folder in new[] { installer.PackagesFolder, Path.Combine(userRoot, ".staging") })
            foreach (System.Security.AccessControl.AuthorizationRule rule in new DirectoryInfo(folder).GetAccessControl().GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)))
                Assert.DoesNotContain("S-1-15-2-", rule.IdentityReference.Value); // staging was never granted either
    }

    private static async Task<bool> GoneAsync(int pid, CancellationToken ct)
    {
        for (var until = DateTime.UtcNow.AddSeconds(15); DateTime.UtcNow < until; await Task.Delay(100, ct))
        {
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); if (p.HasExited) return true; }
            catch (ArgumentException) { return true; }
        }
        return false;
    }
}
