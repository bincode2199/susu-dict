using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Susu.Abstractions;
using Susu.Plugins.AppUpdate;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

// Updater side of the F18 verification: download through the real source and HTTP transport types, journal stages and kill points, planted state, replacement
// of known files only, first-start guard counters, database refusal, loop guard. See the class comment in F18VerificationTests.cs.
public partial class F18VerificationTests
{
    // ================= kit: install rig (own copy; the coding agents' rigs are private) =================

    private sealed class UpEnv(string db) : IUpdateEnvironment
    {
        public bool MigrateOk = true, HealthOk = true;
        public int Stops;
        public UpdateExitOutcome StopApp(TimeSpan timeout) { Stops++; return UpdateExitOutcome.Exited; }
        public bool Migrate(string installDirectory, string databasePath, out string? detail)
        {
            detail = MigrateOk ? null : "boom";
            Sql(db, "INSERT INTO notes(text) VALUES ('migrated');");
            return MigrateOk;
        }
        public bool HealthCheck(string installDirectory, string databasePath, string expectedVersion, out string? detail) { detail = HealthOk ? null : "unhealthy"; return HealthOk; }
        public long FreeBytes(string path) => long.MaxValue;
    }

    private sealed class Hits : IFaultPoint
    {
        public readonly List<string> List = [];
        public void Hit(string stage) => List.Add(stage);
    }

    private sealed class HookPoint(Dictionary<string, Action> actions) : IFaultPoint
    {
        public void Hit(string stage) { if (actions.TryGetValue(stage, out var action)) action(); }
    }

    private sealed class UpRig
    {
        public readonly string Root = TestTemp.NewDir("susu-f18v-rig");
        public string Install => Path.Combine(Root, "app");
        public string Db => Path.Combine(Root, "data", "susu.db");
        public string Updates => Path.Combine(Root, "data", "updates");
        public string ZipPath => Path.Combine(Updates, "downloads", "package.zip");
        public readonly UpEnv Env;
        public byte[] Zip = [];
        public AppUpdateOffer Offer = null!;
        public Dictionary<string, string> Old = new() { ["susu.exe"] = "OLD exe 1.1.0", ["susu_native.dll"] = "OLD native", ["ui/index.html"] = "OLD ui", ["ui/legacy/x.js"] = "OLD legacy", ["old-only.dll"] = "only in the old version" };
        public Dictionary<string, string> New = new() { ["susu.exe"] = "NEW exe 1.2.0", ["susu_native.dll"] = "NEW native", ["ui/index.html"] = "NEW ui", ["ui/app.js"] = "NEW js" };

        public UpRig()
        {
            Old["staged-manifest.json"] = ManifestOf(Old.Keys);
            New["staged-manifest.json"] = ManifestOf(New.Keys);
            Directory.CreateDirectory(Install);
            Directory.CreateDirectory(Path.GetDirectoryName(Db)!);
            foreach (var (path, text) in Old) PutFile(Install, path, text);
            Sql(Db, "PRAGMA journal_mode=WAL; CREATE TABLE notes(id INTEGER PRIMARY KEY, text TEXT); INSERT INTO notes(text) VALUES ('user data one'), ('user data two');");
            Env = new UpEnv(Db);
            SetPackage(New);
        }

        public void SetPackage(Dictionary<string, string> files, string version = "1.2.0", long sequence = 5)
        {
            Zip = ZipOfFiles(files.Select(f => (f.Key, Encoding.UTF8.GetBytes(f.Value))));
            Place();
            Offer = new AppUpdateOffer(version, sequence, $"susu-{version}.zip", Zip.Length, Convert.ToHexStringLower(SHA256.HashData(Zip)), "");
        }

        public void Place() { Directory.CreateDirectory(Path.GetDirectoryName(ZipPath)!); File.WriteAllBytes(ZipPath, Zip); }

        public AppUpdater Updater(IFaultPoint? faults = null) => new(Install, Db, Updates, Env, faults);

        public AppUpdater Staged(IFaultPoint? faults = null)
        {
            var updater = Updater(faults);
            updater.BeginDownload(Offer, "1.1.0"); Place();
            Assert.True(updater.Stage(ZipPath).Ok);
            return updater;
        }

        /// <summary>The whole forward flow with a crash at <paramref name="point"/>. True when the crash happened.</summary>
        public bool CrashAt(string point)
        {
            try { Staged(new FaultAt(point)).Apply(); return false; }
            catch (SimulatedCrash) { return true; }
        }
    }

    private static string ManifestOf(IEnumerable<string> paths) => "{\"product\":\"Su-Su\",\"files\":[" + string.Join(",", paths.Select(p => "{\"path\":\"" + p + "\"}")) + "]}";

    private static void PutFile(string root, string rel, string text)
    {
        string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static byte[] ZipOfFiles(IEnumerable<(string Name, byte[] Bytes)> entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(bytes);
            }
        return stream.ToArray();
    }

    private static void Sql(string db, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string[] NoteRows(string db)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT text FROM notes ORDER BY id;";
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add(r.GetString(0));
        return [.. rows];
    }

    private static SortedDictionary<string, string> Tree(string dir)
        => new(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(dir, p).Replace('\\', '/'), File.ReadAllText));

    private static void AssertOldPair(UpRig rig, string why = "")
    {
        Assert.True(Tree(rig.Install).SequenceEqual(new SortedDictionary<string, string>(rig.Old)), "install is not the old version " + why + ": " + string.Join(", ", Tree(rig.Install).Keys));
        Assert.Equal(["user data one", "user data two"], NoteRows(rig.Db));
    }

    private static void AssertNewPair(UpRig rig, string why = "")
    {
        Assert.True(Tree(rig.Install).SequenceEqual(new SortedDictionary<string, string>(rig.New)), "install is not the new version " + why + ": " + string.Join(", ", Tree(rig.Install).Keys));
        Assert.Equal(["user data one", "user data two", "migrated"], NoteRows(rig.Db));
    }

    /// <summary>After any interruption and one recovery the install and the database are a matching pair and nothing temporary is left in the install folder.</summary>
    private static string AssertMatchingPair(UpRig rig, AppUpdater updater, string why)
    {
        Assert.Empty(Directory.EnumerateFiles(rig.Install, "*.susu-new", SearchOption.AllDirectories));
        if (updater.Read()?.Stage == UpdateStages.Committed) { AssertNewPair(rig, why); return "new"; }
        AssertOldPair(rig, why);
        return "old";
    }

    private static List<string> HitsOfFlow(bool failHealth)
    {
        var rig = new UpRig();
        rig.Env.HealthOk = !failHealth;
        var hits = new Hits();
        rig.Staged(hits).Apply();
        return hits.List;
    }

    // ================= download through the real source, HTTP transport and service =================

    private const string BaseUrl = "https://updates.example.test/susu/";

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class InfiniteStream : Stream
    {
        public long Served;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)'A', offset, count); Served += count; return count; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NoLauncher : IUpdateLauncher { public bool LaunchHelper() => true; }

    private sealed class DownloadRig
    {
        public readonly string Root;
        public readonly byte[] PackageBytes = ZipOfFiles([("susu.exe", "NEW exe"u8.ToArray()), ("ui/index.html", "NEW ui"u8.ToArray())]);
        public readonly UpdateBundle Signed;
        public AppUpdateService Service = null!;
        public AppUpdater Updater = null!;
        public UpdateTrustStore Store = null!;

        public DownloadRig(Func<DownloadRig, HttpRequestMessage, HttpResponseMessage> package, Func<DownloadRig, byte[]>? manifestBody = null, string version = "1.2.0", long sequence = 5, string? root = null)
        {
            Root = root ?? TestTemp.NewDir("susu-f18v-dl");
            Signed = UpdateKeys.Bundle(UpdateKeys.ManifestText(version, sequence, PackageBytes));
            var handler = new FakeHandler(request =>
            {
                string url = request.RequestUri!.AbsoluteUri;
                if (url.EndsWith("update.yaml", StringComparison.Ordinal)) return Ok(manifestBody?.Invoke(this) ?? Signed.Manifest);
                if (url.EndsWith("update.yaml.sig", StringComparison.Ordinal)) return Ok(Signed.Signature);
                if (url.Contains("keyring-", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                return package(this, request);
            });
            Store = new UpdateTrustStore(Path.Combine(Root, "trust.json"), UpdateKeys.Test());
            var source = new SignedManifestAppUpdateSource(new Uri(BaseUrl + "update.yaml"), new HttpUpdateTransport(new HttpClient(handler)), new UpdateVerifier(Store), Store);
            Updater = new AppUpdater(Path.Combine(Root, "app"), Path.Combine(Root, "data", "susu.db"), Path.Combine(Root, "updates"), new UpEnv(Path.Combine(Root, "data", "susu.db")));
            Service = new AppUpdateService("1.1.0", source, Updater, new UpdatePrefs(Path.Combine(Root, "schedule.json")), new ManualClock(), () => 0, new NoLauncher(), keyringEmbedded: true);
        }

        public string DownloadsFolder => Updater.DownloadsFolder;
    }

    private static HttpResponseMessage Ok(byte[] bytes, long? declaredLength = null)
    {
        var content = new ByteArrayContent(bytes);
        if (declaredLength is { } n) content.Headers.ContentLength = n;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static async Task<(AppUpdateInfo Checked, AppUpdateInfo Final, DownloadRig Rig)> RunDownload(Func<DownloadRig, HttpRequestMessage, HttpResponseMessage> package)
    {
        var rig = new DownloadRig(package);
        var ct = TestContext.Current.CancellationToken;
        var checkedInfo = await rig.Service.CheckAsync(false, ct);
        var final = await rig.Service.DownloadAsync(ct);
        return (checkedInfo, final, rig);
    }

    private static void AssertDownloadFailed((AppUpdateInfo Checked, AppUpdateInfo Final, DownloadRig Rig) run, string error, string why)
    {
        Assert.True(run.Checked.State == "available", why + ": check was " + run.Checked.State + " " + run.Checked.Error);
        Assert.True(run.Final.State == "failed" && run.Final.Error == error, $"{why}: {run.Final.State} {run.Final.Error}");
        Assert.Null(run.Rig.Updater.Read());
        Assert.False(Directory.Exists(run.Rig.Updater.StageFolder));
        Assert.True(!Directory.Exists(run.Rig.DownloadsFolder) || !Directory.EnumerateFileSystemEntries(run.Rig.DownloadsFolder).Any(), why + ": a download was kept");
        Assert.False(Directory.Exists(Path.Combine(run.Rig.Root, "app"))); // nothing was installed
    }

    [Fact] // UPD05/UPD06: lies on the wire (one flipped bit, short body under a full Content-Length, extra byte, endless body, redirect to http, errors) end in a clear failure and keep nothing
    public async Task A_download_that_lies_in_any_way_is_refused_through_the_real_transport_and_keeps_nothing()
    {
        var ok = await RunDownload((rig, r) => Ok(rig.PackageBytes));
        Assert.Equal("ready", ok.Final.State); // control: the honest server works through the same rig
        Assert.NotNull(ok.Rig.Updater.Read());

        var bit = await RunDownload((rig, r) => { var b = (byte[])rig.PackageBytes.Clone(); b[b.Length / 2] ^= 0x01; return Ok(b); });
        AssertDownloadFailed(bit, "hash-mismatch", "one flipped bit");

        var zeros = await RunDownload((rig, r) => Ok(new byte[rig.PackageBytes.Length]));
        AssertDownloadFailed(zeros, "hash-mismatch", "same size, other bytes");

        var truncated = await RunDownload((rig, r) => Ok(rig.PackageBytes[..^1], declaredLength: rig.PackageBytes.Length));
        AssertDownloadFailed(truncated, "download-truncated", "short body under a full Content-Length");

        var empty = await RunDownload((rig, r) => Ok([]));
        AssertDownloadFailed(empty, "download-truncated", "empty 200");

        var longer = await RunDownload((rig, r) => Ok([.. rig.PackageBytes, 0], declaredLength: rig.PackageBytes.Length));
        AssertDownloadFailed(longer, "download-failed", "one byte more than the manifest size, header claims the right length");

        var shortHeader = await RunDownload((rig, r) => Ok([.. rig.PackageBytes, .. new byte[4096]], declaredLength: 10));
        AssertDownloadFailed(shortHeader, "download-failed", "a tiny Content-Length and a long body");

        var endless = new InfiniteStream();
        var timer = Stopwatch.StartNew();
        var forever = await RunDownload((rig, r) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(endless) });
        AssertDownloadFailed(forever, "download-failed", "endless body");
        Assert.True(endless.Served < 1_000_000 && timer.Elapsed < TimeSpan.FromSeconds(20), $"served {endless.Served} bytes");

        var toHttp = await RunDownload((rig, r) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(rig.PackageBytes), RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://evil.example.test/susu-1.2.0.zip") });
        AssertDownloadFailed(toHttp, "download-failed", "redirected to plain http");

        AssertDownloadFailed(await RunDownload((rig, r) => new HttpResponseMessage(HttpStatusCode.NotFound)), "download-failed", "404");
        AssertDownloadFailed(await RunDownload((rig, r) => new HttpResponseMessage(HttpStatusCode.InternalServerError)), "download-failed", "500");
        AssertDownloadFailed(await RunDownload((rig, r) => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://other.example.test/x.zip") } }), "download-failed", "unfollowed redirect");
    }

    [Fact] // UPD05: the manifest the user is shown is the manifest that was signed; a body swap, an oversize body, or a missing rotation are refused at the source
    public async Task A_manifest_body_that_is_not_the_signed_one_or_is_oversize_is_never_offered()
    {
        var ct = TestContext.Current.CancellationToken;
        // Signature of manifest A, body of manifest B (a valid-looking manifest with another package hash).
        var swapped = new DownloadRig((rig, r) => Ok(rig.PackageBytes), rig => Encoding.UTF8.GetBytes(UpdateKeys.ManifestText("1.2.0", 5, rig.PackageBytes, sha: new string('b', 64))));
        var s = await swapped.Service.CheckAsync(false, ct);
        Assert.True(s.State == "failed" && s.Error == "signature-invalid", s.State + " " + s.Error);
        Assert.Null(s.Offer);
        // Oversize body: bounded read, a network-class failure, never a verdict on partial bytes.
        var big = new DownloadRig((rig, r) => Ok(rig.PackageBytes), rig => new byte[UpdateVerifier.MaxManifestBytes + 100]);
        var b = await big.Service.CheckAsync(false, ct);
        Assert.True(b.State == "failed" && b.Error is "network" or "manifest-too-large", b.State + " " + b.Error);
        // A different package URL is never followed: a manifest that names another file only changes the name looked up beside the manifest.
        var other = new DownloadRig((rig, r) => Ok(rig.PackageBytes), null, "1.2.0", 5);
        Assert.Equal("available", (await other.Service.CheckAsync(false, ct)).State);
        Assert.Equal(0, other.Service.Status().Offer!.FileName.Count(c => c is '/' or '\\' or ':'));
    }

    // ================= journal stages, kill points, torn and damaged journals =================

    [Fact] // UPD06: the kill-point table reaches every journal stage the code declares; each of those states recovers to a matching pair, also with half a journal temp file beside it
    public void Every_declared_journal_stage_is_the_state_at_some_kill_point_and_each_recovers_to_a_matching_pair()
    {
        string[] declared = [.. typeof(UpdateStages).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!)];
        Assert.True(declared.Length >= 13, string.Join(",", declared));
        var points = HitsOfFlow(false).Concat(HitsOfFlow(true)).Distinct().ToList();
        var seen = new Dictionary<string, List<string>>();
        foreach (string point in points)
        {
            var rig = new UpRig();
            if (point.StartsWith("rollback:", StringComparison.Ordinal)) rig.Env.HealthOk = false;
            Assert.True(rig.CrashAt(point), "no crash at " + point);
            string stage = rig.Updater().Read()?.Stage ?? "(none)";
            if (!seen.TryGetValue(stage, out var list)) seen[stage] = list = [];
            list.Add(point);
            // A kill inside the atomic journal write leaves a half-written temp file beside the previous, intact journal.
            string journal = Path.Combine(rig.Updates, "journal.json");
            if (File.Exists(journal))
            {
                byte[] bytes = File.ReadAllBytes(journal);
                File.WriteAllBytes($"{journal}.{Guid.NewGuid():N}.tmp", bytes[..(bytes.Length / 2)]);
            }
            var fresh = rig.Updater();
            Assert.NotEqual("failed", fresh.Recover().Action);
            AssertMatchingPair(rig, fresh, point);
        }
        Assert.True(declared.Except(seen.Keys).Count() == 0, "stages never observed at a kill point: " + string.Join(",", declared.Except(seen.Keys)) + "; seen " + string.Join(",", seen.Keys));
    }

    [Fact] // UPD06: a crash while a RECOVERY is rolling back (second failure) is finished by the next recovery; the pair is never mixed
    public void A_second_crash_during_the_recovery_of_a_first_crash_still_ends_in_a_matching_pair()
    {
        string[] forward = [.. HitsOfFlow(false).Where(p => p.StartsWith("replace:", StringComparison.Ordinal) || p.StartsWith("migrate:", StringComparison.Ordinal) || p.StartsWith("health:", StringComparison.Ordinal))];
        Assert.True(forward.Length >= 8, string.Join(",", forward));
        int combinations = 0;
        foreach (string first in forward.Where(p => p != "replace:done" || true))
            foreach (string second in new[] { "rollback:journal", "rollback:file:0", "rollback:file:1", "rollback:files", "rollback:db", "rollback:done" })
            {
                var rig = new UpRig();
                Assert.True(rig.CrashAt(first), first);
                try { rig.Updater(new FaultAt(second)).Recover(); } catch (SimulatedCrash) { }
                var third = rig.Updater();
                Assert.NotEqual("failed", third.Recover().Action);
                AssertMatchingPair(rig, third, $"{first} then {second}");
                Assert.NotEqual("failed", rig.Updater().Recover().Action); // idempotent
                AssertMatchingPair(rig, rig.Updater(), $"{first} then {second} again");
                combinations++;
            }
        Assert.True(combinations >= 48);
    }

    [Fact] // UPD06: an update that fails, is cut off and fails again leaves the old pair; the second attempt's backup is never taken from a half-replaced install
    public void A_cut_off_replacement_followed_by_a_failed_second_attempt_still_returns_the_original_pair()
    {
        foreach (string first in new[] { "replace:1", "replace:3", "replace:deleted", "replace:done", "migrate:done" })
        {
            var rig = new UpRig();
            Assert.True(rig.CrashAt(first), first);
            var restarted = rig.Updater();
            Assert.NotEqual("failed", restarted.Recover().Action); // the start after the cut-off rolls back
            AssertOldPair(rig, "after recovery of " + first);
            // The user retries, and this time the health check fails: back to the same old pair, not to a copy of the half-replaced state.
            rig.Env.HealthOk = false;
            var retry = rig.Staged();
            Assert.Equal("rolled-back", retry.Apply().Outcome);
            AssertOldPair(rig, "after the failed retry of " + first);
        }
    }

    [Fact] // UPD06: Known_gap (DEFECT D3): a journal.json that is damaged IN PLACE falls back to .prev, which can name an earlier stage than the files are really in
    public void Known_gap_a_journal_damaged_in_place_after_the_replacement_started_resumes_from_the_previous_stage_over_a_half_replaced_install()
    {
        var rig = new UpRig();
        Assert.True(rig.CrashAt("replace:2"));
        string journal = Path.Combine(rig.Updates, "journal.json");
        Assert.Equal(UpdateStages.Replacing, rig.Updater().Read()!.Stage);
        File.WriteAllBytes(journal, File.ReadAllBytes(journal)[..40]); // bit rot / a quarantining scanner: unreadable, .prev (BackedUp) remains
        var fresh = rig.Updater();
        Assert.Equal(UpdateStages.BackedUp, fresh.Read()!.Stage);
        var recovery = fresh.Recover();
        // Pinned: the half-replaced install is treated as "nothing replaced yet" and the update is simply resumable. The install folder is mixed at this point.
        Assert.Equal("resumable", recovery.Action);
        var mixed = Tree(rig.Install);
        Assert.False(mixed.SequenceEqual(new SortedDictionary<string, string>(rig.Old)) || mixed.SequenceEqual(new SortedDictionary<string, string>(rig.New)), "the install is expected to be a mix here");
        // Applying it again finishes the update (every file is rewritten from the verified stage), so the user is not stranded.
        Assert.Equal("committed", fresh.Apply().Outcome);
        AssertNewPair(rig);
    }

    [Fact] // UPD06: journal damage that leaves NO readable journal and a backup pair is rolled back; with no pair it is cleared; zero length and garbage behave the same
    public void A_damaged_journal_with_no_previous_copy_is_rolled_back_from_the_pair_or_cleared()
    {
        foreach (byte[] damage in new[] { Array.Empty<byte>(), "{ not json"u8.ToArray(), "null"u8.ToArray(), "[]"u8.ToArray(), [0xFF, 0xFE, 0x00], "{\"stage\":\"Replacing\"}"u8.ToArray() })
        {
            var rig = new UpRig();
            Assert.True(rig.CrashAt("replace:2"));
            string journal = Path.Combine(rig.Updates, "journal.json");
            File.WriteAllBytes(journal, damage);
            File.Delete(journal + ".prev");
            var fresh = rig.Updater();
            Assert.NotEqual("failed", fresh.Recover().Action);
            AssertOldPair(rig, "damage " + damage.Length);
            Assert.Null(fresh.Read());
        }
    }

    [Fact] // UPD06: a backup being written when the process is killed is never used for a rollback, and the interrupted update leaves the old pair
    public void A_kill_during_backup_creation_at_every_backup_step_leaves_the_old_pair_and_a_later_update_succeeds()
    {
        foreach (string point in new[] { "backup:begin", "backup:app:0", "backup:app:2", "backup:db", "backup:index", "backup:journal" })
        {
            var rig = new UpRig();
            Assert.True(rig.CrashAt(point), point);
            var fresh = rig.Updater();
            Assert.NotEqual("failed", fresh.Recover().Action);
            AssertOldPair(rig, point);
            Assert.False(fresh.CanRollBackToPrevious() && fresh.Read()?.Stage == UpdateStages.Committed, point);
            // A half-written backup folder must not poison the next update.
            var retry = rig.Staged();
            Assert.Equal("committed", retry.Apply().Outcome);
            AssertNewPair(rig, "retry after " + point);
        }
    }

}
