using System.Security.Cryptography;
using System.Text;
using Susu.Abstractions;
using Susu.Plugins.AppUpdate;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>F18.2 update source, the shared background schedule (check only), and the service the Settings page talks to.</summary>
public class F18UpdateServiceTests
{
    private static readonly byte[] Zip = Encoding.UTF8.GetBytes("package bytes");

    private sealed class FakeTransport : IUpdateTransport
    {
        public readonly Dictionary<string, byte[]> Files = [];
        public Exception? Fail;
        public readonly List<string> Requests = [];
        public Task<byte[]?> GetAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
        {
            Requests.Add(uri.AbsoluteUri);
            if (Fail is not null) throw Fail;
            return Task.FromResult(Files.TryGetValue(uri.AbsoluteUri, out var b) ? b : null);
        }
        public Task DownloadAsync(Uri uri, string destination, long maxBytes, CancellationToken cancellationToken)
        {
            Requests.Add(uri.AbsoluteUri);
            if (Fail is not null) throw Fail;
            File.WriteAllBytes(destination, Files[uri.AbsoluteUri]);
            return Task.CompletedTask;
        }
    }

    private const string Base = "https://updates.example.test/susu/";

    private static (SignedManifestAppUpdateSource Source, FakeTransport Transport, UpdateTrustStore Store) Source(string version = "1.2.0", long sequence = 5)
    {
        var transport = new FakeTransport();
        var bundle = UpdateKeys.Bundle(UpdateKeys.ManifestText(version, sequence, Zip));
        transport.Files[Base + "update.yaml"] = bundle.Manifest;
        transport.Files[Base + "update.yaml.sig"] = bundle.Signature;
        transport.Files[Base + $"susu-{version}.zip"] = Zip;
        var store = new UpdateTrustStore(Path.Combine(TestTemp.NewDir("susu-f18-source"), "trust.json"), UpdateKeys.Test());
        return (new SignedManifestAppUpdateSource(new Uri(Base + "update.yaml"), transport, new UpdateVerifier(store), store), transport, store);
    }

    [Fact]
    public async Task The_source_fetches_manifest_and_signature_verifies_first_and_downloads_by_the_verified_file_name()
    {
        var (source, transport, _) = Source();
        var check = await source.CheckAsync("1.1.0", TestContext.Current.CancellationToken);
        Assert.Equal(AppUpdateStatus.Available, check.Status);
        Assert.Equal(Base + "update.yaml", transport.Requests[0]);
        Assert.Equal(Base + "update.yaml.sig", transport.Requests[1]);
        string folder = TestTemp.NewDir("susu-f18-fetch");
        string path = await source.FetchAsync(check.Offer!, folder, TestContext.Current.CancellationToken);
        Assert.Equal(Zip, File.ReadAllBytes(path));
        Assert.Contains(Base + "susu-1.2.0.zip", transport.Requests);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.FetchAsync(check.Offer! with { FileName = "../x.zip" }, folder, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_missing_signature_a_network_failure_and_a_bad_response_are_failures_never_up_to_date_or_a_version()
    {
        var (source, transport, _) = Source();
        transport.Files.Remove(Base + "update.yaml.sig");
        Assert.Equal(AppUpdateCheck.Failed("signature-missing"), await source.CheckAsync("1.1.0", TestContext.Current.CancellationToken));
        transport.Files.Remove(Base + "update.yaml");
        Assert.Equal(AppUpdateCheck.Failed("manifest-unavailable"), await source.CheckAsync("1.1.0", TestContext.Current.CancellationToken));
        transport.Fail = new HttpRequestException("offline");
        Assert.Equal(AppUpdateCheck.Failed("network"), await source.CheckAsync("1.1.0", TestContext.Current.CancellationToken));
        // An unsigned manifest served as if it were signed (signature of different bytes).
        var (s2, t2, _) = Source();
        t2.Files[Base + "update.yaml"] = Encoding.UTF8.GetBytes(UpdateKeys.ManifestText("9.9.9", 99, Zip));
        var check = await s2.CheckAsync("1.1.0", TestContext.Current.CancellationToken);
        Assert.Equal(AppUpdateCheck.Failed("signature-invalid"), check);
        Assert.Null(check.Offer);
    }

    [Fact]
    public async Task The_source_reads_key_rotation_records_next_to_the_manifest_in_order_and_stops_at_the_first_missing_one()
    {
        var (source, transport, store) = Source();
        var rotation = UpdateKeys.Rotation(1, UpdateKeys.AddKey("rel-2", UpdateKeys.NextSeed), "");
        var bundle = UpdateKeys.Bundle(UpdateKeys.ManifestText("1.2.0", 5, Zip), "rel-2", UpdateKeys.NextSeed);
        transport.Files[Base + "update.yaml"] = bundle.Manifest;
        transport.Files[Base + "update.yaml.sig"] = bundle.Signature;
        transport.Files[Base + "keyring-1.yaml"] = rotation.Bytes;
        transport.Files[Base + "keyring-1.yaml.sig"] = rotation.Signature;
        Assert.Equal(AppUpdateStatus.Available, (await source.CheckAsync("1.1.0", TestContext.Current.CancellationToken)).Status);
        Assert.Single(store.Chain);
        Assert.Contains(Base + "keyring-2.yaml", transport.Requests);
        Assert.DoesNotContain(Base + "keyring-3.yaml", transport.Requests);
        // A rotation record without its signature file is a failure, not "no rotation".
        transport.Files.Remove(Base + "keyring-2.yaml.sig");
        transport.Files[Base + "keyring-2.yaml"] = rotation.Bytes;
        Assert.Equal(AppUpdateCheck.Failed("rotation-invalid"), await source.CheckAsync("1.1.0", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void No_source_is_configured_by_default_and_only_an_https_url_without_credentials_that_names_a_file_is_accepted()
    {
        string folder = TestTemp.NewDir("susu-f18-config");
        Assert.Null(UpdateSourceConfig.Load(folder));
        File.WriteAllText(Path.Combine(folder, UpdateSourceConfig.FileName), "{\"manifestUrl\":\"https://updates.example.test/susu/update.yaml\"}");
        Assert.Equal("https://updates.example.test/susu/update.yaml", UpdateSourceConfig.Load(folder)!.AbsoluteUri);
        foreach (string bad in new[] { "http://updates.example.test/update.yaml", "https://user:pw@updates.example.test/update.yaml", "ftp://x/y.yaml", "file:///c:/x.yaml",
            "https://updates.example.test/", "update.yaml", "", "https://updates.example.test/u.yaml#frag" })
            Assert.Null(UpdateSourceConfig.Parse(bad));
        File.WriteAllText(Path.Combine(folder, UpdateSourceConfig.FileName), "not json");
        Assert.Null(UpdateSourceConfig.Load(folder));
    }

    // ---------- schedule ----------

    private sealed class CountingSource : IAppUpdateSource
    {
        public int Checks, Fetches;
        public AppUpdateCheck Result = AppUpdateCheck.UpToDate();
        public Task<AppUpdateCheck> CheckAsync(string currentVersion, CancellationToken cancellationToken) { Checks++; return Task.FromResult(Result); }
        public Task<string> FetchAsync(AppUpdateOffer offer, string folder, CancellationToken cancellationToken) { Fetches++; throw new IOException("must not be called"); }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 400 && !condition(); i++) await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    [Fact]
    public async Task The_schedule_checks_once_15_seconds_after_start_then_only_after_24_hours_and_never_downloads()
    {
        var clock = new ManualClock();
        var prefs = new UpdatePrefs(Path.Combine(TestTemp.NewDir("susu-f18-sched"), "schedule.json"));
        var source = new CountingSource { Result = AppUpdateCheck.Available(new AppUpdateOffer("1.2.0", 5, "susu-1.2.0.zip", 10, new string('a', 64), "")) };
        var rig = ServiceRig(source, clock, prefs);
        int pluginChecks = 0;
        var schedule = new UpdateSchedule(clock, prefs, [ct => rig.Service.CheckAsync(true, ct), ct => { pluginChecks++; return Task.CompletedTask; }]);
        using var cts = new CancellationTokenSource();
        var run = schedule.RunAsync(cts.Token);
        clock.Advance(TimeSpan.FromSeconds(14));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(0, source.Checks);
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitFor(() => source.Checks == 1 && pluginChecks == 1); // the plugin check shares the schedule
        await WaitFor(() => clock.PendingTimers == 1);
        Assert.Equal("available", rig.Service.Status().State);
        Assert.Equal(0, source.Fetches); // automatic means check only
        Assert.Null(rig.Updater.Read());
        clock.Advance(TimeSpan.FromHours(23));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, source.Checks);
        clock.Advance(TimeSpan.FromHours(1));
        await WaitFor(() => source.Checks == 2 && pluginChecks == 2);
        Assert.Equal(0, source.Fetches);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task A_restart_within_24_hours_does_not_check_again_and_turning_automatic_checks_off_stops_them()
    {
        string folder = TestTemp.NewDir("susu-f18-sched2");
        string file = Path.Combine(folder, "schedule.json");
        var clock = new ManualClock();
        var prefs = new UpdatePrefs(file);
        int checks = 0;
        using var cts = new CancellationTokenSource();
        var run = new UpdateSchedule(clock, prefs, [_ => { checks++; return Task.CompletedTask; }]).RunAsync(cts.Token);
        clock.Advance(UpdateSchedule.StartDelay);
        await WaitFor(() => checks == 1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // The next start (a new process) reads the last attempt from disk.
        var prefs2 = new UpdatePrefs(file);
        Assert.NotNull(prefs2.LastAttempt);
        clock.Advance(TimeSpan.FromHours(2));
        using var cts2 = new CancellationTokenSource();
        var run2 = new UpdateSchedule(clock, prefs2, [_ => { checks++; return Task.CompletedTask; }]).RunAsync(cts2.Token);
        clock.Advance(UpdateSchedule.StartDelay);
        await Task.Delay(80, TestContext.Current.CancellationToken);
        Assert.Equal(1, checks);
        // Off: even after more than 24 h nothing runs.
        prefs2.Update(autoCheck: false);
        clock.Advance(TimeSpan.FromHours(30));
        await Task.Delay(80, TestContext.Current.CancellationToken);
        Assert.Equal(1, checks);
        Assert.False(new UpdatePrefs(file).AutoCheck);
        cts2.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run2);
    }

    [Fact]
    public async Task A_failing_check_does_not_stop_the_schedule_and_still_counts_as_an_attempt()
    {
        var clock = new ManualClock();
        var prefs = new UpdatePrefs(Path.Combine(TestTemp.NewDir("susu-f18-sched3"), "schedule.json"));
        int second = 0;
        using var cts = new CancellationTokenSource();
        var run = new UpdateSchedule(clock, prefs, [_ => throw new InvalidOperationException("boom"), _ => { second++; return Task.CompletedTask; }]).RunAsync(cts.Token);
        clock.Advance(UpdateSchedule.StartDelay);
        await WaitFor(() => second == 1);
        Assert.NotNull(prefs.LastAttempt);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    // ---------- service ----------

    private sealed class Launcher : IUpdateLauncher
    {
        public int Launches;
        public bool Ok = true;
        public bool LaunchHelper() { Launches++; return Ok; }
    }

    private sealed class ServiceRigRecord(AppUpdateService service, AppUpdater updater, Launcher launcher, string root)
    {
        public AppUpdateService Service => service;
        public AppUpdater Updater => updater;
        public Launcher Launcher => launcher;
        public string Root => root;
    }

    private sealed class NullEnv : IUpdateEnvironment
    {
        public UpdateExitOutcome StopApp(TimeSpan timeout) => UpdateExitOutcome.Exited;
        public bool Migrate(string i, string d, out string? detail) { detail = null; return true; }
        public bool HealthCheck(string i, string d, string v, out string? detail) { detail = null; return true; }
        public long FreeBytes(string path) => long.MaxValue;
    }

    private static ServiceRigRecord ServiceRig(IAppUpdateSource? source, IClock? clock = null, UpdatePrefs? prefs = null, int inFlight = 0)
    {
        string root = TestTemp.NewDir("susu-f18-service");
        var updater = new AppUpdater(Path.Combine(root, "app"), Path.Combine(root, "susu.db"), Path.Combine(root, "updates"), new NullEnv());
        var launcher = new Launcher();
        var service = new AppUpdateService("1.1.0", source, updater, prefs ?? new UpdatePrefs(Path.Combine(root, "schedule.json")), clock ?? new ManualClock(), () => inFlight, launcher, keyringEmbedded: true);
        return new ServiceRigRecord(service, updater, launcher, root);
    }

    private sealed class ZipSource(byte[] zip, string version = "1.2.0") : IAppUpdateSource
    {
        public Task<AppUpdateCheck> CheckAsync(string currentVersion, CancellationToken cancellationToken)
            => Task.FromResult(AppUpdateCheck.Available(new AppUpdateOffer(version, 5, $"susu-{version}.zip", zip.Length, Convert.ToHexStringLower(SHA256.HashData(zip)), "notes")));
        public Task<string> FetchAsync(AppUpdateOffer offer, string folder, CancellationToken cancellationToken)
        {
            string path = Path.Combine(folder, offer.FileName);
            File.WriteAllBytes(path, zip);
            return Task.FromResult(path);
        }
    }

    private static byte[] ZipOf(params (string, string)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, true))
            foreach (var (name, text) in files) { using var s = zip.CreateEntry(name).Open(); s.Write(Encoding.UTF8.GetBytes(text)); }
        return stream.ToArray();
    }

    [Fact]
    public async Task Without_a_source_the_service_says_so_and_a_failed_check_is_a_failure_never_up_to_date()
    {
        var none = ServiceRig(null);
        Assert.False(none.Service.Available);
        Assert.Equal("none", (await none.Service.CheckAsync(false, TestContext.Current.CancellationToken)).State);

        var source = new CountingSource { Result = AppUpdateCheck.Failed("signature-invalid") };
        var rig = ServiceRig(source);
        var info = await rig.Service.CheckAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(("failed", "signature-invalid", null), (info.State, info.Error, info.Offer));
        // An exception from the source is a failure too.
        var throwing = ServiceRig(new ThrowingSource());
        Assert.Equal(("failed", "check-failed"), ((await throwing.Service.CheckAsync(false, TestContext.Current.CancellationToken)) is var i ? (i.State, i.Error) : default));
    }

    private sealed class ThrowingSource : IAppUpdateSource
    {
        public Task<AppUpdateCheck> CheckAsync(string currentVersion, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
        public Task<string> FetchAsync(AppUpdateOffer offer, string folder, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    [Fact]
    public async Task Download_and_install_run_only_on_the_users_commands_and_install_needs_a_staged_package_and_a_confirmation_when_tasks_run()
    {
        var rig = ServiceRig(new ZipSource(ZipOf(("susu.exe", "x"))), inFlight: 2);
        Assert.Equal(new AppUpdateInstallOutcome(false, "not-ready"), rig.Service.Install(true));
        Assert.Equal("none", (await rig.Service.DownloadAsync(TestContext.Current.CancellationToken)).State); // nothing offered yet: no download
        Assert.Equal("available", (await rig.Service.CheckAsync(false, TestContext.Current.CancellationToken)).State);
        Assert.Null(rig.Updater.Read()); // a check downloads nothing
        Assert.Equal("ready", (await rig.Service.DownloadAsync(TestContext.Current.CancellationToken)).State);
        Assert.Equal(UpdateStages.Staged, rig.Updater.Read()!.Stage);
        Assert.Equal(new AppUpdateInstallOutcome(false, "in-flight-needs-confirmation"), rig.Service.Install(false));
        Assert.Equal(0, rig.Launcher.Launches);
        // A scheduled check while a package is staged does not disturb it.
        Assert.Equal("ready", (await rig.Service.CheckAsync(true, TestContext.Current.CancellationToken)).State);
        Assert.Equal(new AppUpdateInstallOutcome(true, null), rig.Service.Install(true));
        Assert.Equal(1, rig.Launcher.Launches);
        rig.Launcher.Ok = false;
        Assert.Equal(new AppUpdateInstallOutcome(false, "helper-failed"), rig.Service.Install(true));
        rig.Service.Discard();
        Assert.Null(rig.Updater.Read());
        Assert.Equal("none", rig.Service.Status().State);
    }

    [Fact]
    public async Task A_package_that_fails_staging_is_reported_and_nothing_stays_on_disk()
    {
        var rig = ServiceRig(new ZipSource(ZipOf(("other.txt", "no exe"))));
        await rig.Service.CheckAsync(false, TestContext.Current.CancellationToken);
        var info = await rig.Service.DownloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(("failed", "package-invalid"), (info.State, info.Error));
        Assert.Null(rig.Updater.Read());
        Assert.Equal(new AppUpdateInstallOutcome(false, "not-ready"), rig.Service.Install(true));
    }

    [Fact]
    public async Task The_page_state_comes_from_the_journal_so_a_rolled_back_update_is_shown_after_a_restart()
    {
        var rig = ServiceRig(new ZipSource(ZipOf(("susu.exe", "x"))));
        await rig.Service.CheckAsync(false, TestContext.Current.CancellationToken);
        await rig.Service.DownloadAsync(TestContext.Current.CancellationToken);
        // Simulate an update that was rolled back by a helper: the journal says so.
        string journal = Path.Combine(rig.Root, "updates", "journal.json");
        File.WriteAllText(journal, File.ReadAllText(journal).Replace("\"Staged\"", "\"RolledBack\"").Replace("\"error\":null", "\"error\":\"health-check-failed\""));
        var restarted = new AppUpdateService("1.1.0", new CountingSource(), rig.Updater, new UpdatePrefs(Path.Combine(rig.Root, "schedule.json")), new ManualClock(), () => 0, new Launcher(), true);
        var status = restarted.Status();
        Assert.Equal(("rolledBack", "health-check-failed", "1.2.0"), (status.State, status.Error, status.Offer!.Version));
        restarted.Discard();
        Assert.Equal("none", restarted.Status().State);
    }
}
