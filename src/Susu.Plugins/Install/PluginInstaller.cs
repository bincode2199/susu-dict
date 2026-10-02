using System.Text.Json;
using Susu.Abstractions;
using Susu.Storage;

namespace Susu.Plugins.Install;

/// <summary>The authority a package declares: what a diff compares.</summary>
public sealed record PermissionSet(IReadOnlyList<string> Capabilities, IReadOnlyList<string> Origins, IReadOnlyList<string> Secrets)
{
    public static PermissionSet Of(PackageManifest m) => new(m.Capabilities, m.Hosts, m.CredentialUse);
    public static readonly PermissionSet Empty = new([], [], []);

    public PluginPermissionDiff DiffFrom(PermissionSet? previous, string against, string? baseVersion)
    {
        previous ??= Empty;
        static string[] Minus(IReadOnlyList<string> a, IReadOnlyList<string> b, StringComparer c) => [.. a.Where(x => !b.Contains(x, c)).Distinct(c).Order(StringComparer.Ordinal)];
        var c = StringComparer.OrdinalIgnoreCase;
        return new PluginPermissionDiff(against, baseVersion,
            Minus(Capabilities, previous.Capabilities, c), Minus(previous.Capabilities, Capabilities, c),
            Minus(Origins, previous.Origins, StringComparer.Ordinal), Minus(previous.Origins, Origins, StringComparer.Ordinal),
            Minus(Secrets, previous.Secrets, StringComparer.Ordinal), Minus(previous.Secrets, Secrets, StringComparer.Ordinal));
    }
}

/// <summary>A package the application ships. Its version is what an override must beat.</summary>
public sealed record BuiltInPackage(string Id, string Version, PermissionSet Permissions);

public interface IBuiltInPackages
{
    BuiltInPackage? Find(string id);
}

public sealed class BuiltInPackages(IEnumerable<BuiltInPackage> packages) : IBuiltInPackages
{
    public const string DefaultVersion = "1.0.0";
    private readonly Dictionary<string, BuiltInPackage> byId = packages.ToDictionary(p => p.Id, StringComparer.Ordinal);

    public BuiltInPackage? Find(string id) => byId.GetValueOrDefault(id);

    /// <summary>Reads every plugins/&lt;dir&gt;/manifest.yaml of the program folder. A shipped manifest without a version counts as <see cref="DefaultVersion"/>.</summary>
    public static BuiltInPackages FromDirectory(string root)
    {
        var list = new List<BuiltInPackage>();
        if (Directory.Exists(root))
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                string path = Path.Combine(dir, "manifest.yaml");
                try
                {
                    if (!File.Exists(path) || PackageManifest.Parse(File.ReadAllText(path)).Manifest is not { } m) continue;
                    list.Add(new BuiltInPackage(m.Id, m.Version ?? DefaultVersion, PermissionSet.Of(m)));
                }
                catch (IOException) { }
            }
        return new BuiltInPackages(list);
    }
}

/// <summary>Input of the post-switch health check: the activated directory, relative to nothing the page can influence.</summary>
public sealed record HealthRequest(string PackageId, string Version, string Directory, PackageManifest Manifest);

public sealed record HealthResult(bool Ok, string? Detail = null);

/// <summary>
/// F16.1 package installation (PLAN 4.8/4.9, ARCHITECTURE 8.1/10). Stage: unzip safely into staging, parse and validate the manifest, verify the signature
/// identity, apply the built-in override and signer-continuity rules, and compute the permission diff. Commit (the user confirmed the diff): re-check the
/// staged bytes, move them to packages/&lt;id&gt;/&lt;version&gt;, switch the active row in one database transaction, run the health check, and only then
/// drop the journal; any failure restores the previous active version (or none) and removes the new directory. Uninstall deactivates the row and deletes
/// the directories, which hands the id back to the built-in package when there is one. The previous version stays on disk until the new one is committed.
/// </summary>
public sealed class PluginInstaller : IPluginInstallService
{
    public const int HostLevel = 1;
    private readonly string root;
    private readonly PluginInstallationRepository store;
    private readonly IBuiltInPackages builtIns;
    private readonly IHostKeyring keyring;
    private readonly Func<HealthRequest, HealthResult> health;
    private readonly Action<string>? fault;
    private readonly IPluginHostControl? host;
    private readonly Action<string>? removeData;
    private readonly object gate = new();
    private Pending? manual;
    private readonly Dictionary<string, Pending> updates = new(StringComparer.Ordinal);

    private sealed record Pending(string Token, string Directory, PackageManifest Manifest, string Version, PackageIdentity Identity, string Hash, string? BaseVersion, PluginPreviewInfo Info);

    public event Action? Changed;

    /// <param name="root">The user plugin folder (%APPDATA%\Su-Su\plugins); staging and the journal live under it too.</param>
    /// <param name="health">Runs after the switch. The default checks the activated files again; a composition root can add a sandbox load.</param>
    /// <param name="fault">Test seam: called with the step name before each step and may throw to simulate a failure there.</param>
    /// <param name="host">The running plugin host: told to restart after a committed install or uninstall, and asked what is in flight. Null: nothing is running.</param>
    /// <param name="removeData">Deletes a package's stored data; called only when the user chose that at uninstall.</param>
    public PluginInstaller(string root, PluginInstallationRepository store, IBuiltInPackages builtIns, IHostKeyring keyring, Func<HealthRequest, HealthResult>? health = null, Action<string>? fault = null,
        IPluginHostControl? host = null, Action<string>? removeData = null)
    {
        this.root = Path.GetFullPath(root);
        this.store = store;
        this.builtIns = builtIns;
        this.keyring = keyring;
        this.health = health ?? Structural;
        this.fault = fault;
        this.host = host;
        this.removeData = removeData;
        Directory.CreateDirectory(this.root);
    }

    private string StagingRoot => Path.Combine(root, ".staging");
    private string PackagesRoot => Path.Combine(root, "packages");
    private string JournalPath => Path.Combine(root, "journal.json");
    public string DirectoryOf(string id, string version) => Path.Combine(PackagesRoot, id, version);

    /// <summary>The directory of the active user version of a package, or null when the built-in one (or nothing) is in effect.</summary>
    public string? ActiveDirectory(string id)
    {
        lock (gate) return store.Active(id) is { } r ? DirectoryOf(r.PackageId, r.Version) : null; // waits for a switch in progress, so a launch never loads a version still being health-checked
    }

    /// <summary>The folder that holds every installed version (packages/&lt;id&gt;/&lt;version&gt;); the sandbox is granted read access to this one only, never to staging.</summary>
    public string PackagesFolder => PackagesRoot;

    /// <summary>Every active user package with its directory and manifest entry, for the plugin host to load (F16.2).</summary>
    public IReadOnlyList<InstalledPackage> ActivePackages()
    {
        lock (gate)
        {
            var list = new List<InstalledPackage>();
            foreach (var row in store.ActiveRecords())
            {
                string dir = DirectoryOf(row.PackageId, row.Version);
                string entry = "main.js";
                try
                {
                    string path = Path.Combine(dir, "manifest.yaml");
                    if (File.Exists(path) && PackageManifest.Parse(File.ReadAllText(path)).Manifest is { } m) entry = m.Entry;
                }
                catch (IOException) { }
                list.Add(new InstalledPackage(row.PackageId, dir, entry));
            }
            return list;
        }
    }

    /// <summary>The default health check: manifest still parses to the same identity, the entry file is there, and the files hash as recorded.</summary>
    public static HealthResult Structural(HealthRequest request)
    {
        string manifestPath = Path.Combine(request.Directory, "manifest.yaml");
        if (!File.Exists(manifestPath)) return new(false, "manifest missing");
        var (m, issues) = PackageManifest.Parse(File.ReadAllText(manifestPath));
        if (m is null || issues.Count > 0 || m.Id != request.PackageId || m.Version != request.Version) return new(false, "manifest changed");
        if (!File.Exists(Path.Combine(request.Directory, m.Entry))) return new(false, "entry missing");
        return new(true);
    }

    // ---------- stage ----------

    public PluginPreviewInfo Preview(string packagePath) => Stage(packagePath, update: false);

    public PluginPreviewInfo StageUpdate(string packagePath) => Stage(packagePath, update: true);

    public IReadOnlyList<PluginPreviewInfo> StagedUpdates()
    {
        lock (gate) return [.. updates.Values.Select(p => p.Info).OrderBy(i => i.Id, StringComparer.Ordinal)];
    }

    public IReadOnlyList<PluginTaskInfo> InFlight(string packageId) => host?.InFlight(packageId) ?? [];

    private PluginPreviewInfo Stage(string packagePath, bool update)
    {
        lock (gate)
        {
            if (!update) DiscardManualLocked();
            string token = Guid.NewGuid().ToString("N");
            string dir = Path.Combine(StagingRoot, token);
            Directory.CreateDirectory(StagingRoot);
            try
            {
                var issues = new List<ManifestIssue>(SafeUnzip.Extract(packagePath, dir));
                if (issues.Count > 0) return Rejected(issues, dir);

                string manifestPath = Path.Combine(dir, "manifest.yaml");
                if (!File.Exists(manifestPath)) return Rejected([new ManifestIssue("manifest.yaml", "missing", "manifest.yaml is required at the package root")], dir);
                var (manifest, manifestIssues) = PackageManifest.Parse(File.ReadAllText(manifestPath));
                if (manifest is null || manifestIssues.Count > 0)
                    return Rejected(manifestIssues.Select(i => i.Path == "apiVersion" && i.Code == "unsupported" ? i with { Code = "api-unsupported" } : i), dir);
                if (manifest.Version is null) return Rejected([new ManifestIssue("version", "missing", "'version' is required (major.minor.patch)")], dir);
                if (manifest.MinHost > HostLevel) return Rejected([new ManifestIssue("minHost", "host-too-old", $"needs host level {manifest.MinHost}; this host is {HostLevel}")], dir);
                if (!File.Exists(Path.Combine(dir, manifest.Entry))) return Rejected([new ManifestIssue("entry", "missing", $"'{manifest.Entry}' is not in the package")], dir);

                var (identity, signatureIssues) = PackageTrust.Verify(dir, keyring);
                if (identity is null) return Rejected(signatureIssues, dir);

                PackageVersion.TryParse(manifest.Version, out var version);
                var builtIn = builtIns.Find(manifest.Id);
                var installed = store.Active(manifest.Id);
                var reasons = new List<string>();
                if (update && installed is null) issues.Add(new ManifestIssue("id", "not-installed", "an update applies only to an installed package"));
                if (builtIn is not null)
                {
                    if (identity.Kind != SignerKind.Host) issues.Add(new ManifestIssue("id", "builtin-id-not-host-signed", "this id belongs to a built-in package; only a package signed by the host may use it"));
                    else if (PackageVersion.TryParse(builtIn.Version, out var shipped) && !(version > shipped))
                        issues.Add(new ManifestIssue("version", "not-newer-than-builtin", $"must be newer than the built-in {builtIn.Version}"));
                }
                if (installed is not null)
                {
                    if (PackageVersion.TryParse(installed.Version, out var current) && !(version > current))
                        issues.Add(new ManifestIssue("version", "not-newer", $"must be newer than the installed {installed.Version}"));
                    // A signer change or dropping the signature is not silent and not refused: it is held for the user's explicit acknowledgement (UPD03).
                    var previous = PackageIdentity.FromKey(installed.Signer);
                    if (previous.Kind != SignerKind.Unsigned && identity.Key != installed.Signer)
                        reasons.Add(identity.Kind == SignerKind.Unsigned ? "signature-removed" : "signer-changed");
                }
                if (issues.Count > 0) return Rejected(issues, dir);

                var (baseSet, against, baseVersion) = BaseOf(installed, builtIn);
                var diff = PermissionSet.Of(manifest).DiffFrom(baseSet, against, baseVersion);
                if (diff.HasAdditions && against != "none") reasons.Add("permissions-expanded");
                string hash = PackageTrust.PackageHash(dir);
                var info = new PluginPreviewInfo(true, token, manifest.Id, manifest.Name, manifest.Version, KindName(identity.Kind), identity.Signer,
                    builtIn?.Version, installed?.Version, diff, [], [.. reasons], update);
                var staged = new Pending(token, dir, manifest, manifest.Version, identity, hash, installed?.Version, info);
                if (update)
                {
                    if (updates.Remove(manifest.Id, out var older)) SafeUnzip.TryDelete(older.Directory);
                    updates[manifest.Id] = staged;
                }
                else manual = staged;
                return info;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Rejected([new ManifestIssue("$", "io", e.GetType().Name)], dir);
            }
        }
    }

    private (PermissionSet? Set, string Against, string? Version) BaseOf(InstallationRecord? installed, BuiltInPackage? builtIn)
    {
        if (installed is not null)
        {
            string path = Path.Combine(DirectoryOf(installed.PackageId, installed.Version), "manifest.yaml");
            try
            {
                if (File.Exists(path) && PackageManifest.Parse(File.ReadAllText(path)).Manifest is { } m) return (PermissionSet.Of(m), "installed", installed.Version);
            }
            catch (IOException) { }
            return (PermissionSet.Empty, "installed", installed.Version); // unreadable installed manifest: show everything as new
        }
        return builtIn is not null ? (builtIn.Permissions, "builtin", builtIn.Version) : (null, "none", null);
    }

    private static string KindName(SignerKind kind) => kind switch { SignerKind.Host => "host", SignerKind.ThirdParty => "thirdParty", _ => "unsigned" };

    private static PluginPreviewInfo Rejected(IEnumerable<ManifestIssue> issues, string stagingDirectory)
    {
        SafeUnzip.TryDelete(stagingDirectory);
        return new PluginPreviewInfo(false, null, null, null, null, "unsigned", "", null, null, null, [.. issues.Take(20).Select(i => new PluginIssue(i.Path, i.Code))]);
    }

    public void Discard(string token)
    {
        lock (gate)
        {
            if (manual?.Token == token) DiscardManualLocked();
            else if (updates.FirstOrDefault(u => u.Value.Token == token) is { Value: { } staged }) { SafeUnzip.TryDelete(staged.Directory); updates.Remove(staged.Manifest.Id); }
        }
    }

    private void DiscardManualLocked()
    {
        if (manual is null) return;
        SafeUnzip.TryDelete(manual.Directory);
        manual = null;
    }

    // ---------- activate ----------

    public PluginInstallOutcome Install(string token, bool acknowledged = false)
    {
        PluginInstallOutcome outcome;
        string? changedId = null;
        lock (gate)
        {
            Pending? staged = manual?.Token == token ? manual : updates.Values.FirstOrDefault(u => u.Token == token);
            if (staged is null) return Fail("install.noPending");
            // Held changes (new signer, dropped signature, wider permissions) apply only on explicit acknowledgement; until then the package stays staged and the old version runs.
            if (staged.Info.Reasons.Length > 0 && !acknowledged) return Fail("install.needsConfirmation");
            if (manual == staged) manual = null; else updates.Remove(staged.Manifest.Id);
            outcome = Activate(staged);
            if (outcome.Ok) changedId = staged.Manifest.Id;
        }
        if (changedId is null) return outcome;
        int interrupted = RestartHost(changedId);
        Changed?.Invoke();
        return outcome with { Interrupted = interrupted };
    }

    private int RestartHost(string id)
    {
        try { return host?.Restart(id) ?? 0; }
        catch (Exception e) when (e is not OutOfMemoryException) { return 0; } // the switch is committed; a host that cannot restart now loads it on its next launch
    }

    private PluginInstallOutcome Activate(Pending staged)
    {
        {
            string id = staged.Manifest.Id, version = staged.Version;
            string finalDir = DirectoryOf(id, version);
            bool moved = false, switched = false;
            InstallationRecord? previous = null;
            try
            {
                fault?.Invoke("recheck");
                // The bytes the user confirmed must be the bytes that go live: hash again, and the file rules again.
                if (!Directory.Exists(staged.Directory) || PackageTrust.PackageHash(staged.Directory) != staged.Hash || SafePackage.Validate(staged.Directory).Count > 0)
                { SafeUnzip.TryDelete(staged.Directory); return Fail("install.changed"); }
                // What was staged was judged against the version that ran then; if that changed since (another install, an uninstall), stage again.
                if (store.Active(id)?.Version != staged.BaseVersion) { SafeUnzip.TryDelete(staged.Directory); return Fail("install.stale"); }

                previous = store.Active(id);
                WriteJournal(new InstallJournal(id, version, previous?.Version, previous?.Signer, previous?.Hash));

                fault?.Invoke("move");
                Directory.CreateDirectory(Path.GetDirectoryName(finalDir)!);
                if (Directory.Exists(finalDir)) SafeUnzip.TryDelete(finalDir); // leftover of an interrupted run; never an active version (it must be newer)
                Directory.Move(staged.Directory, finalDir);
                moved = true;

                fault?.Invoke("switch");
                store.Activate(id, version, staged.Identity.Key, staged.Hash);
                switched = true;

                fault?.Invoke("health");
                var result = health(new HealthRequest(id, version, finalDir, staged.Manifest));
                if (!result.Ok) throw new HealthFailedException(result.Detail);

                fault?.Invoke("commit");
                DeleteJournal();
                PruneOldVersions(id, keep: [version, previous?.Version]);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                bool restored = Rollback(id, previous, finalDir, moved, switched);
                SafeUnzip.TryDelete(staged.Directory);
                return Fail(e is HealthFailedException ? "install.healthFailed" : restored ? "install.activationFailed" : "install.rollbackFailed");
            }
            return new PluginInstallOutcome(true, null, id, version, []);
        }
    }

    private sealed class HealthFailedException(string? detail) : Exception(detail);

    private bool Rollback(string id, InstallationRecord? previous, string finalDir, bool moved, bool switched)
    {
        bool ok = true;
        if (switched)
        {
            try
            {
                if (previous is not null) store.Activate(previous.PackageId, previous.Version, previous.Signer, previous.Hash);
                else store.Deactivate(id);
            }
            catch (Exception) { ok = false; }
        }
        if (ok && moved) SafeUnzip.TryDelete(finalDir);
        if (ok) { try { DeleteJournal(); } catch (IOException) { ok = false; } }
        return ok;
    }

    private void PruneOldVersions(string id, string?[] keep)
    {
        string dir = Path.Combine(PackagesRoot, id);
        if (!Directory.Exists(dir)) return;
        foreach (string sub in Directory.EnumerateDirectories(dir))
            if (!keep.Contains(Path.GetFileName(sub), StringComparer.Ordinal)) SafeUnzip.TryDelete(sub);
    }

    // ---------- uninstall ----------

    public PluginInstallOutcome Uninstall(string packageId, bool removeData = false)
    {
        InstallationRecord active;
        lock (gate)
        {
            active = store.Active(packageId)!;
            if (active is null) return Fail("uninstall.notInstalled");
            try { store.Deactivate(packageId); }
            catch (Exception) { return Fail("uninstall.failed"); }
            if (updates.Remove(packageId, out var staged)) SafeUnzip.TryDelete(staged.Directory);
        }
        // The host stops using the package before its files go: in-flight calls are cancelled, and the next call loads the built-in package or none.
        int interrupted = RestartHost(packageId);
        lock (gate)
        {
            SafeUnzip.TryDelete(Path.Combine(PackagesRoot, packageId)); // best effort; Recover removes what a running host still held
            if (removeData) { try { this.removeData?.Invoke(packageId); } catch (Exception e) when (e is not OutOfMemoryException) { } }
        }
        Changed?.Invoke();
        return new PluginInstallOutcome(true, null, packageId, active.Version, [], builtIns.Find(packageId)?.Version, interrupted);
    }

    // ---------- state ----------

    public IReadOnlyList<InstalledPluginInfo> Installed()
    {
        var result = new List<InstalledPluginInfo>();
        foreach (var row in store.ActiveRecords())
        {
            string path = Path.Combine(DirectoryOf(row.PackageId, row.Version), "manifest.yaml");
            PackageManifest? m = null;
            try { if (File.Exists(path)) m = PackageManifest.Parse(File.ReadAllText(path)).Manifest; }
            catch (IOException) { }
            var identity = PackageIdentity.FromKey(row.Signer);
            var builtIn = builtIns.Find(row.PackageId);
            result.Add(new InstalledPluginInfo(row.PackageId, m?.Name ?? row.PackageId, row.Version, KindName(identity.Kind), identity.Signer,
                [.. m?.Capabilities ?? []], [.. m?.Hosts ?? []], [.. m?.CredentialUse ?? []], builtIn?.Version, builtIn?.Version));
        }
        return result;
    }

    /// <summary>
    /// Start-up recovery: a journal means a switch never reached its commit, so the previous version (or none) is restored and the half-installed
    /// directory removed; staging is emptied; version directories no row names are deleted.
    /// </summary>
    public void Recover()
    {
        lock (gate)
        {
            manual = null;
            updates.Clear();
            SafeUnzip.TryDelete(StagingRoot);
            Directory.CreateDirectory(StagingRoot);
            bool changed = false;
            if (File.Exists(JournalPath))
            {
                InstallJournal? journal = null;
                try { journal = JsonSerializer.Deserialize(File.ReadAllText(JournalPath), InstallJson.Default.InstallJournal); }
                catch (Exception e) when (e is IOException or JsonException) { }
                if (journal is not null)
                {
                    var active = store.Active(journal.PackageId);
                    if (active is not null && active.Version == journal.Version)
                    {
                        if (journal.PreviousVersion is not null && journal.PreviousSigner is not null && journal.PreviousHash is not null)
                            store.Activate(journal.PackageId, journal.PreviousVersion, journal.PreviousSigner, journal.PreviousHash);
                        else store.Deactivate(journal.PackageId);
                        changed = true;
                    }
                    SafeUnzip.TryDelete(DirectoryOf(journal.PackageId, journal.Version));
                }
                DeleteJournal();
            }
            if (Directory.Exists(PackagesRoot))
                foreach (string idDir in Directory.EnumerateDirectories(PackagesRoot))
                {
                    string id = Path.GetFileName(idDir);
                    if (store.Active(id) is null) SafeUnzip.TryDelete(idDir); // uninstalled (or rolled back) leftovers
                }
            if (changed) Changed?.Invoke();
        }
    }

    private void WriteJournal(InstallJournal journal)
        => AtomicFile.WriteFlushed(JournalPath, JsonSerializer.SerializeToUtf8Bytes(journal, InstallJson.Default.InstallJournal));

    private void DeleteJournal() { if (File.Exists(JournalPath)) File.Delete(JournalPath); }

    private static PluginInstallOutcome Fail(string error) => new(false, error, null, null, []);
}

internal sealed record InstallJournal(string PackageId, string Version, string? PreviousVersion, string? PreviousSigner, string? PreviousHash);

[System.Text.Json.Serialization.JsonSerializable(typeof(InstallJournal))]
internal sealed partial class InstallJson : System.Text.Json.Serialization.JsonSerializerContext;
