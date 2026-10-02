using System.Text;
using Susu.Abstractions;
using Susu.Jobs;
using Susu.Plugins;
using Susu.Plugins.Install;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F16.2 (UPD03, UPD04): update staging and held changes, API/minHost compatibility, KV across update and uninstall, the host restart and in-flight
/// feedback, with fakes for the plugin host and the update source (no network). The real sandbox is in <see cref="PluginUpdateSandboxTests"/>.
/// </summary>
public sealed class PluginUpdateTests : IDisposable
{
    private const string Id = "com.example.echo";
    private readonly string temp = TestTemp.NewDir("susu-update");
    private readonly Database db;
    private readonly PluginInstallationRepository store;
    private readonly PluginKvRepository kv;
    private readonly string userRoot;
    private readonly FakeHostControl host = new();

    public PluginUpdateTests()
    {
        db = Database.Open(Path.Combine(temp, "susu.db"));
        store = new PluginInstallationRepository(db);
        kv = new PluginKvRepository(db);
        userRoot = Path.Combine(temp, "plugins");
    }

    public void Dispose() => db.Dispose();

    private sealed class FakeHostControl : IPluginHostControl
    {
        public readonly List<string> Restarts = [];
        public int Cancelled;
        public bool Throw;
        public Action<string>? OnRestart;
        public IReadOnlyList<PluginTaskInfo> InFlight(string packageId) => [];
        public int Restart(string packageId)
        {
            Restarts.Add(packageId);
            OnRestart?.Invoke(packageId);
            if (Throw) throw new InvalidOperationException("host");
            return Cancelled;
        }
    }

    private PluginInstaller Installer(Func<HealthRequest, HealthResult>? health = null)
        => new(userRoot, store, new BuiltInPackages([]),
            new HostKeyring([new KeyValuePair<string, byte[]>(PackageFactory.HostKeyId, Ed25519.PublicKey(PackageFactory.SeedA))]), health, host: host, removeData: id => kv.DeletePackage(id));

    private string Pkg(string name, string version, string hosts = "", string id = Id, byte[]? seed = null, string? keyId = null, string secrets = "")
        => PackageFactory.Zip(temp, name, PackageFactory.Files(PackageFactory.Manifest(id, version, "translate", hosts, secrets)), seed, keyId);

    private void InstallV1(PluginInstaller installer, byte[]? seed = null, string hosts = "")
        => Assert.True(installer.Install(installer.Preview(Pkg("v1.susuext", "1.0.0", hosts, seed: seed)).Token!, acknowledged: true).Ok);

    private int StagingDirs() => Directory.Exists(Path.Combine(userRoot, ".staging")) ? Directory.EnumerateDirectories(Path.Combine(userRoot, ".staging")).Count() : 0;

    // ---------- KV namespace ----------

    [Fact] // data belongs to the package id: it survives an update and an uninstall, and goes only when the user chose to remove it; other packages are untouched
    public void Stored_data_survives_update_and_uninstall_and_is_removed_only_by_choice()
    {
        var installer = Installer();
        InstallV1(installer);
        kv.Set(Id, "state", "{\"n\":1}");
        kv.Set("com.example.other", "state", "{\"n\":9}");

        Assert.True(installer.Install(installer.Preview(Pkg("v11.susuext", "1.1.0")).Token!).Ok);
        Assert.Equal("{\"n\":1}", kv.Get(Id, "state"));

        Assert.True(installer.Uninstall(Id).Ok);
        Assert.Equal("{\"n\":1}", kv.Get(Id, "state")); // kept by default
        InstallV1(installer);
        Assert.Equal("{\"n\":1}", kv.Get(Id, "state"));

        Assert.True(installer.Uninstall(Id, removeData: true).Ok);
        Assert.Null(kv.Get(Id, "state"));
        Assert.Equal("{\"n\":9}", kv.Get("com.example.other", "state"));
    }

    // ---------- compatibility ----------

    [Fact] // an unsupported API version or a newer minHost is refused with a stable reason, as a first install and as an update; the old version stays
    public void An_incompatible_package_is_refused_with_a_reason_and_the_old_version_stays()
    {
        var installer = Installer();
        InstallV1(installer);
        string good = PackageFactory.Manifest(Id, "1.1.0");
        var cases = new[]
        {
            (good.Replace("apiVersion: 1", "apiVersion: 99"), "api-unsupported", "apiVersion"),
            (good.Replace("minHost: 1", "minHost: 7"), "host-too-old", "minHost"),
        };
        foreach (var (manifest, code, path) in cases)
        {
            string zip = PackageFactory.Zip(temp, code + ".susuext", new Dictionary<string, byte[]> { ["manifest.yaml"] = Encoding.UTF8.GetBytes(manifest), ["main.js"] = Encoding.UTF8.GetBytes(PackageFactory.Main) });
            foreach (var staged in new[] { installer.Preview(zip), installer.StageUpdate(zip) })
            {
                Assert.False(staged.Ok);
                Assert.Contains(staged.Issues, i => i.Code == code && i.Path == path);
            }
        }
        Assert.Equal("1.0.0", Assert.Single(installer.Installed()).Version);
        Assert.Empty(installer.StagedUpdates());
        Assert.Equal(0, StagingDirs());
        Assert.Single(host.Restarts); // only the first install restarted the host
    }

    // ---------- update staging ----------

    [Fact] // UPD03: a valid update is kept aside; the installed version keeps running; confirming applies it and restarts the host
    public void A_staged_update_waits_beside_the_running_version_and_confirm_applies_it()
    {
        var installer = Installer();
        InstallV1(installer);
        string oldDir = installer.ActiveDirectory(Id)!;
        host.Cancelled = 3;

        var staged = installer.StageUpdate(Pkg("v11.susuext", "1.1.0"));
        Assert.True(staged.Ok);
        Assert.True(staged.IsUpdate);
        Assert.Equal("1.0.0", staged.ReplacesVersion);
        Assert.Empty(staged.Reasons);
        Assert.Equal(oldDir, installer.ActiveDirectory(Id));
        Assert.Equal("1.0.0", Assert.Single(installer.Installed()).Version);
        Assert.Equal("1.1.0", Assert.Single(installer.StagedUpdates()).Version);
        Assert.Single(host.Restarts); // nothing restarted by staging

        var outcome = installer.Install(staged.Token!);
        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal(3, outcome.Interrupted);
        Assert.Equal("1.1.0", Assert.Single(installer.Installed()).Version);
        Assert.Empty(installer.StagedUpdates());
        Assert.Equal([Id, Id], host.Restarts);
    }

    [Fact] // UPD04: a package that fails validation is discarded as it is staged and the installed version is untouched
    public void A_bad_staged_package_is_discarded_and_the_old_version_remains()
    {
        var installer = Installer();
        InstallV1(installer, PackageFactory.SeedB);
        string oldDir = installer.ActiveDirectory(Id)!;
        string notZip = Path.Combine(temp, "notzip.susuext");
        File.WriteAllText(notZip, "not a zip");
        var bad = new[]
        {
            installer.StageUpdate(notZip),                                                                                      // unreadable
            installer.StageUpdate(Pkg("same.susuext", "1.0.0", seed: PackageFactory.SeedB)),                                    // not newer
            installer.StageUpdate(Pkg("other.susuext", "1.1.0", id: "com.example.other", seed: PackageFactory.SeedB)),          // an update only applies to an installed package
            installer.StageUpdate(Pkg("unknownkey.susuext", "1.1.0", seed: PackageFactory.SeedB, keyId: "not-a-trusted-key")),  // key not trusted
        };
        Assert.All(bad, b => { Assert.False(b.Ok); Assert.NotEmpty(b.Issues); });
        Assert.Contains(bad[2].Issues, i => i.Code == "not-installed");
        Assert.Empty(installer.StagedUpdates());
        Assert.Equal(0, StagingDirs());
        Assert.Equal(oldDir, installer.ActiveDirectory(Id));
        Assert.Equal("1.0.0", store.Active(Id)!.Version);
        Assert.Single(host.Restarts);
    }

    [Fact] // a staged package whose bytes change on disk before confirmation is refused at apply and discarded; the old version remains
    public void A_staged_update_tampered_with_before_apply_is_refused()
    {
        var installer = Installer();
        InstallV1(installer, PackageFactory.SeedB);
        var staged = installer.StageUpdate(Pkg("v11.susuext", "1.1.0", seed: PackageFactory.SeedB));
        Assert.True(staged.Ok);
        File.AppendAllText(Path.Combine(userRoot, ".staging", staged.Token!, "main.js"), "// changed after staging\n");
        Assert.Equal("install.changed", installer.Install(staged.Token!).Error);
        Assert.Equal("1.0.0", store.Active(Id)!.Version);
        Assert.Empty(installer.StagedUpdates());
        Assert.Equal(0, StagingDirs());
    }

    [Fact] // UPD03: a new signer or wider hosts keep the old version until the user confirms; cancelling leaves it; confirming applies it
    public void A_signer_change_or_wider_hosts_keep_the_old_version_until_confirmed()
    {
        var installer = Installer();
        InstallV1(installer, PackageFactory.SeedB, hosts: "https://a.example:443");
        string oldDir = installer.ActiveDirectory(Id)!;
        string update = Pkg("v11.susuext", "1.1.0", "https://a.example:443,https://b.example:443", seed: PackageFactory.SeedA, keyId: PackageFactory.ThirdPartyKeyId(PackageFactory.SeedA));

        var staged = installer.StageUpdate(update);
        Assert.True(staged.Ok);
        Assert.Equal(["permissions-expanded", "signer-changed"], staged.Reasons.Order().ToArray());
        Assert.Equal(["https://b.example:443"], staged.Diff!.AddedOrigins);

        Assert.Equal("install.needsConfirmation", installer.Install(staged.Token!).Error);
        Assert.Equal(oldDir, installer.ActiveDirectory(Id));
        Assert.Equal("key:" + PackageFactory.ThirdPartyKeyId(PackageFactory.SeedB), store.Active(Id)!.Signer);
        Assert.Single(installer.StagedUpdates()); // still waiting, not silently dropped or applied
        Assert.Single(host.Restarts);

        installer.Discard(staged.Token!); // the user cancels the confirmation
        Assert.Empty(installer.StagedUpdates());
        Assert.Equal("1.0.0", store.Active(Id)!.Version);

        var again = installer.StageUpdate(update);
        Assert.True(installer.Install(again.Token!, acknowledged: true).Ok);
        Assert.Equal(("1.1.0", "key:" + PackageFactory.ThirdPartyKeyId(PackageFactory.SeedA)), (store.Active(Id)!.Version, store.Active(Id)!.Signer));
    }

    [Fact] // narrowing permissions needs no acknowledgement
    public void Reduced_permissions_apply_without_a_held_change()
    {
        var installer = Installer();
        InstallV1(installer, PackageFactory.SeedB, hosts: "https://a.example:443,https://b.example:443");
        var staged = installer.StageUpdate(Pkg("v11.susuext", "1.1.0", "https://a.example:443", seed: PackageFactory.SeedB));
        Assert.Empty(staged.Reasons);
        Assert.Equal(["https://b.example:443"], staged.Diff!.RemovedOrigins);
        Assert.True(installer.Install(staged.Token!).Ok);
    }

    [Fact] // what was staged was judged against the version running then: if that changed, it must be staged again
    public void A_staged_update_goes_stale_when_the_installed_version_changed_meanwhile()
    {
        var installer = Installer();
        InstallV1(installer);
        var staged = installer.StageUpdate(Pkg("v11.susuext", "1.1.0"));
        Assert.True(installer.Install(installer.Preview(Pkg("v12.susuext", "1.2.0")).Token!).Ok); // a manual install in between
        Assert.Equal("install.stale", installer.Install(staged.Token!).Error);
        Assert.Equal("1.2.0", store.Active(Id)!.Version);
        Assert.Equal(0, StagingDirs());
    }

    [Fact] // one staged update per package: a newer one replaces the older and the older's staging folder is removed
    public void Staging_a_newer_update_replaces_the_older_staged_one()
    {
        var installer = Installer();
        InstallV1(installer);
        var first = installer.StageUpdate(Pkg("v11.susuext", "1.1.0"));
        var second = installer.StageUpdate(Pkg("v12.susuext", "1.2.0"));
        Assert.Equal("1.2.0", Assert.Single(installer.StagedUpdates()).Version);
        Assert.False(Directory.Exists(Path.Combine(userRoot, ".staging", first.Token!)));
        Assert.True(Directory.Exists(Path.Combine(userRoot, ".staging", second.Token!)));
        Assert.Equal("install.noPending", installer.Install(first.Token!).Error);
    }

    [Fact] // a manual pick does not discard staged updates, and a restart clears staging
    public void A_manual_pick_leaves_staged_updates_alone_and_recovery_clears_them()
    {
        var installer = Installer();
        InstallV1(installer);
        installer.StageUpdate(Pkg("v11.susuext", "1.1.0"));
        installer.Preview(Pkg("x.susuext", "1.0.5", id: "com.example.x"));
        Assert.Single(installer.StagedUpdates());
        installer.Recover();
        Assert.Empty(installer.StagedUpdates());
        Assert.Equal(0, StagingDirs());
        Assert.Equal("1.0.0", store.Active(Id)!.Version);
    }

    // ---------- host restart and in-flight feedback ----------

    [Fact] // UPD04: a failed activation restores the old version and never restarts the host (nothing was cancelled)
    public void A_failed_switch_does_not_restart_the_host()
    {
        bool fail = false;
        var installer = Installer(r => fail ? new HealthResult(false, "boom") : PluginInstaller.Structural(r));
        InstallV1(installer);
        fail = true;
        host.Cancelled = 5;
        var outcome = installer.Install(installer.Preview(Pkg("v11.susuext", "1.1.0")).Token!);
        Assert.Equal("install.healthFailed", outcome.Error);
        Assert.Equal(0, outcome.Interrupted);
        Assert.Equal("1.0.0", store.Active(Id)!.Version);
        Assert.Single(host.Restarts);
    }

    [Fact] // a host that cannot restart does not undo a committed switch
    public void A_failing_host_restart_does_not_fail_a_committed_install()
    {
        var installer = Installer();
        host.Throw = true;
        var outcome = installer.Install(installer.Preview(Pkg("v1.susuext", "1.0.0")).Token!);
        Assert.True(outcome.Ok);
        Assert.Equal(0, outcome.Interrupted);
        Assert.Equal("1.0.0", store.Active(Id)!.Version);
    }

    [Fact] // uninstall: the pointer is switched, then the host is told (calls cancelled), and only then do the files go
    public void Uninstall_restarts_the_host_after_the_switch_and_before_the_files_are_removed()
    {
        var installer = Installer();
        InstallV1(installer);
        bool? activeAtRestart = null, filesAtRestart = null;
        host.OnRestart = _ => { activeAtRestart = store.Active(Id) is not null; filesAtRestart = Directory.Exists(Path.Combine(userRoot, "packages", Id)); };
        host.Cancelled = 2;
        var outcome = installer.Uninstall(Id);
        Assert.Equal(2, outcome.Interrupted);
        Assert.Equal((false, true), (activeAtRestart, filesAtRestart));
        Assert.False(Directory.Exists(Path.Combine(userRoot, "packages", Id)));
    }

    [Fact] // the installed packages the host loads: active directory and manifest entry, nothing from staging
    public void Active_packages_list_what_the_plugin_host_should_load()
    {
        var installer = Installer();
        InstallV1(installer);
        installer.StageUpdate(Pkg("v11.susuext", "1.1.0"));
        var active = Assert.Single(installer.ActivePackages());
        Assert.Equal((Id, "main.js", installer.ActiveDirectory(Id)), (active.Id, active.Entry, active.Directory));
        Assert.DoesNotContain(".staging", active.Directory);
    }
}

/// <summary>F16.2 host controller against the supervisor with an in-memory session double: the in-flight listing and the restart.</summary>
public class PluginHostControllerTests
{
    private static InFlightCall Call(string plugin, string capability, int id) => new(plugin, capability, $"r{id}", $"j{id}", id);

    [Fact] // UPD04: restart detaches the session, cancels the changed package's calls only, lets others finish, then disposes it; the next call gets a fresh session
    public async Task Restart_cancels_the_packages_calls_lets_others_drain_and_the_next_acquire_gets_a_fresh_session()
    {
        var clock = new ManualClock();
        var sessions = new List<FakeSession>();
        var supervisor = new Supervisor<FakeSession>(() => { var s = new FakeSession(); sessions.Add(s); return s; }, clock);
        using var controller = new PluginHostController<FakeSession>(supervisor, TimeSpan.FromSeconds(10));
        var first = supervisor.Start();
        first.Calls.AddRange([Call("pkg.a", "translate", 1), Call("pkg.a", "translate", 2), Call("pkg.a", "dictionary", 3), Call("pkg.b", "translate", 4)]);

        Assert.Equal([new PluginTaskInfo("dictionary", 1), new PluginTaskInfo("translate", 2)], controller.InFlight("pkg.a"));
        Assert.Equal(3, controller.Restart("pkg.a"));
        Assert.Equal(["pkg.a"], first.Interrupted);
        Assert.False(first.Disposed); // pkg.b still has a call running
        Assert.Equal([Call("pkg.b", "translate", 4)], first.InFlightCalls());
        Assert.Equal([new PluginTaskInfo("translate", 1)], controller.InFlight("pkg.b")); // a detached session still reports what it runs

        var fresh = supervisor.Acquire();
        Assert.NotNull(fresh);
        Assert.NotSame(first, fresh);
        Assert.Equal(2, sessions.Count);
        Assert.Empty(controller.InFlight("pkg.a")); // the new session has nothing of the old package

        first.Calls.Clear(); // pkg.b's call completes
        await controller.Retired().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(first.Disposed);
        Assert.False(fresh.Disposed);
        supervisor.Release();
    }

    [Fact] // a call that never drains does not keep the old session alive forever
    public async Task A_detached_session_is_disposed_after_the_drain_timeout()
    {
        var supervisor = new Supervisor<FakeSession>(() => new FakeSession(), new ManualClock());
        using var controller = new PluginHostController<FakeSession>(supervisor, TimeSpan.FromMilliseconds(80));
        var first = supervisor.Start();
        first.Calls.Add(Call("pkg.b", "translate", 1));
        Assert.Equal(0, controller.Restart("pkg.a"));
        await controller.Retired().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(first.Disposed);
    }

    [Fact] // detaching is not a crash: no restart backoff, no failure counted, and the old session's disconnect is ignored
    public void Restart_is_not_counted_as_a_crash()
    {
        int launches = 0;
        var supervisor = new Supervisor<FakeSession>(() => { launches++; return new FakeSession(); }, new ManualClock());
        using var controller = new PluginHostController<FakeSession>(supervisor, TimeSpan.Zero);
        var first = supervisor.Start();
        controller.Restart("pkg.a");
        first.Crash(); // the disposed old session disconnects: must not relaunch or count
        Assert.Equal(1, launches);
        Assert.Equal(3, supervisor.AttemptsRemaining);
        Assert.False(supervisor.Stopped);
        Assert.NotNull(supervisor.Acquire());
        Assert.Equal(2, launches);
    }

    [Fact] // without a running session there is nothing to cancel
    public void Restart_without_a_session_cancels_nothing()
    {
        var supervisor = new Supervisor<FakeSession>(() => new FakeSession(), new ManualClock());
        using var controller = new PluginHostController<FakeSession>(supervisor);
        Assert.Equal(0, controller.Restart("pkg.a"));
        Assert.Empty(controller.InFlight("pkg.a"));
    }
}

/// <summary>F16.2 update check against a fake source: nothing is fetched except through the port, so no network is used.</summary>
public sealed class PluginUpdateServiceTests : IDisposable
{
    private const string Id = "com.example.echo";
    private readonly string temp = TestTemp.NewDir("susu-updsvc");
    private readonly Database db;
    private readonly PluginInstaller installer;
    private readonly PluginInstallationRepository store;

    public PluginUpdateServiceTests()
    {
        db = Database.Open(Path.Combine(temp, "susu.db"));
        store = new PluginInstallationRepository(db);
        installer = new PluginInstaller(Path.Combine(temp, "plugins"), store, new BuiltInPackages([]), HostKeyring.Empty);
        Assert.True(installer.Install(installer.Preview(PackageFactory.Zip(temp, "v1.susuext", PackageFactory.Files(PackageFactory.Manifest(Id, "1.0.0")), PackageFactory.SeedB)).Token!).Ok);
    }

    public void Dispose() => db.Dispose();

    private sealed class FakeSource : IPluginUpdateSource
    {
        public Func<string, string, PluginUpdateCheck> Check = (_, _) => new(PluginUpdateStatus.UpToDate);
        public Func<PluginUpdateOffer, string, string>? Fetch;
        public int Fetches;
        public Task<PluginUpdateCheck> CheckAsync(string packageId, string installedVersion, CancellationToken cancellationToken) => Task.FromResult(Check(packageId, installedVersion));
        public Task<string> FetchAsync(PluginUpdateOffer offer, string folder, CancellationToken cancellationToken)
        {
            Fetches++;
            return Task.FromResult(Fetch!(offer, folder));
        }
    }

    private PluginUpdateService Service(FakeSource? source) => new(installer, source, Path.Combine(temp, "downloads"));

    private string Package(string folder, string version, byte[]? seed = null) => PackageFactory.Zip(folder, "update-" + version + ".susuext", PackageFactory.Files(PackageFactory.Manifest(Id, version)), seed ?? PackageFactory.SeedB);

    [Fact]
    public async Task Without_a_source_updates_are_unavailable_and_nothing_happens()
    {
        var service = Service(null);
        Assert.False(service.Available);
        var outcome = await service.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal((0, 0), (outcome.Checked, outcome.Staged));
        Assert.Empty(installer.StagedUpdates());
    }

    [Fact] // an update the source offers is fetched, validated, staged beside the running version, and the download is removed
    public async Task An_offered_update_is_staged_and_not_applied()
    {
        var source = new FakeSource
        {
            Check = (id, version) => new(PluginUpdateStatus.Available, new PluginUpdateOffer(id, "1.1.0", "handle-1")),
            Fetch = (offer, folder) => Package(folder, offer.Version),
        };
        var outcome = await Service(source).CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal((1, 1, 0), (outcome.Checked, outcome.Staged, outcome.Failures.Length));
        Assert.Equal("1.1.0", Assert.Single(installer.StagedUpdates()).Version);
        Assert.Equal("1.0.0", store.Active(Id)!.Version);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(temp, "downloads"))); // the downloaded file is gone once staged

        await Service(source).CheckAsync(TestContext.Current.CancellationToken); // an update already waiting is not fetched again
        Assert.Equal(1, source.Fetches);
    }

    [Fact] // a source that reports up to date, and one that reports an error: the error is a failure, never "up to date"
    public async Task A_failed_check_is_reported_as_a_failure_and_not_as_up_to_date()
    {
        var source = new FakeSource();
        var ok = await Service(source).CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal([Id], ok.UpToDate);
        Assert.Empty(ok.Failures);

        source.Check = (_, _) => new(PluginUpdateStatus.Failed, null, "signature-invalid");
        var failed = await Service(source).CheckAsync(TestContext.Current.CancellationToken);
        Assert.Empty(failed.UpToDate);
        Assert.Equal(new PluginUpdateFailure(Id, "signature-invalid"), Assert.Single(failed.Failures));

        source.Check = (_, _) => throw new InvalidOperationException("network");
        var thrown = await Service(source).CheckAsync(TestContext.Current.CancellationToken);
        Assert.Empty(thrown.UpToDate);
        Assert.Equal("check-failed", Assert.Single(thrown.Failures).Code);
        Assert.Empty(installer.StagedUpdates());
    }

    [Fact] // an offer for the wrong id or a version that is not newer, a failed download, and a package the installer rejects: failures, nothing staged, old version stays
    public async Task Bad_offers_downloads_and_packages_stage_nothing()
    {
        var source = new FakeSource();
        var service = Service(source);

        source.Check = (_, _) => new(PluginUpdateStatus.Available, new PluginUpdateOffer("com.example.other", "1.1.0", "h"));
        Assert.Equal("offer-invalid", Assert.Single((await service.CheckAsync(TestContext.Current.CancellationToken)).Failures).Code);
        source.Check = (id, _) => new(PluginUpdateStatus.Available, new PluginUpdateOffer(id, "1.0.0", "h"));
        Assert.Equal("offer-invalid", Assert.Single((await service.CheckAsync(TestContext.Current.CancellationToken)).Failures).Code);

        source.Check = (id, _) => new(PluginUpdateStatus.Available, new PluginUpdateOffer(id, "1.1.0", "h"));
        source.Fetch = (_, _) => throw new IOException("download");
        Assert.Equal("fetch-failed", Assert.Single((await service.CheckAsync(TestContext.Current.CancellationToken)).Failures).Code);

        source.Fetch = (_, folder) => { string p = Path.Combine(folder, "bad.susuext"); File.WriteAllText(p, "not a zip"); return p; };
        Assert.Equal("rejected:unreadable", Assert.Single((await service.CheckAsync(TestContext.Current.CancellationToken)).Failures).Code);

        source.Fetch = (_, folder) => Package(folder, "1.1.0", PackageFactory.SeedA); // a different signer than the installed one: staged, held (not a failure)
        var held = await service.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal((1, 0), (held.Staged, held.Failures.Length));
        Assert.Contains("signer-changed", Assert.Single(installer.StagedUpdates()).Reasons);
        Assert.Equal("1.0.0", store.Active(Id)!.Version);
    }
}
