using System.IO.Compression;
using System.Text;
using Susu.Abstractions;
using Susu.Plugins;
using Susu.Plugins.Install;
using Susu.Storage;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>Builds .susuext zips for the installer tests, optionally signed with a seed this test owns.</summary>
internal static class PackageFactory
{
    public static readonly byte[] SeedA = Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
    public static readonly byte[] SeedB = Convert.FromHexString("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb");
    public const string HostKeyId = "host-test-2026";

    public static string ThirdPartyKeyId(byte[] seed) => Convert.ToHexStringLower(Ed25519.PublicKey(seed));

    public static string Manifest(string id = "com.example.echo", string version = "1.0.0", string capabilities = "translate", string hosts = "", string secrets = "", string name = "Echo")
        => $"id: {id}\nname: {name}\nversion: {version}\napiVersion: 1\nminHost: 1\nentry: main.js\ncapabilities:\n  - {capabilities.Replace(",", "\n  - ")}\n"
         + (hosts.Length > 0 ? "hosts:\n  - " + hosts.Replace(",", "\n  - ") + "\n" : "")
         + (secrets.Length > 0 ? "credentialUse:\n  - " + secrets.Replace(",", "\n  - ") + "\n" : "");

    public const string Main = "export default { async translate(req) { return { text: `echo:${req.text}` }; } };\n";

    /// <summary>Files by entry name. When <paramref name="signWith"/> is set, a signature entry over manifest.yaml and the other files is added.</summary>
    public static string Zip(string directory, string fileName, IDictionary<string, byte[]> files, byte[]? signWith = null, string? keyId = null, IEnumerable<string>? extraEntries = null)
    {
        string path = Path.Combine(directory, fileName);
        var all = new Dictionary<string, byte[]>(files);
        if (signWith is not null && files.TryGetValue("manifest.yaml", out var manifest))
        {
            var hashes = files.Where(f => f.Key != "signature").Select(f => (f.Key, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(f.Value)))).OrderBy(h => h.Key, StringComparer.Ordinal).ToList();
            byte[] sig = Ed25519.Sign(signWith, PackageTrust.SignedBytes(manifest, hashes));
            all["signature"] = Encoding.UTF8.GetBytes($"keyId: {keyId ?? ThirdPartyKeyId(signWith)}\nalgorithm: ed25519\nsignature: {Convert.ToBase64String(sig)}\n");
        }
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (name, bytes) in all)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = entry.Open();
                s.Write(bytes);
            }
            foreach (string extra in extraEntries ?? []) zip.CreateEntry(extra);
        }
        return path;
    }

    public static Dictionary<string, byte[]> Files(string manifest, string main = Main)
        => new() { ["manifest.yaml"] = Encoding.UTF8.GetBytes(manifest), ["main.js"] = Encoding.UTF8.GetBytes(main) };
}

public class Ed25519Tests
{
    [Fact]
    public void Rfc8032_test_vectors_one_and_two_match()
    {
        byte[] seed1 = Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        Assert.Equal("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", Convert.ToHexStringLower(Ed25519.PublicKey(seed1)));
        Assert.Equal("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b",
            Convert.ToHexStringLower(Ed25519.Sign(seed1, [])));
        byte[] seed2 = Convert.FromHexString("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb");
        Assert.Equal("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", Convert.ToHexStringLower(Ed25519.PublicKey(seed2)));
        Assert.Equal("92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00",
            Convert.ToHexStringLower(Ed25519.Sign(seed2, [0x72])));
    }

    [Fact]
    public void Verify_accepts_a_good_signature_and_rejects_any_change()
    {
        byte[] seed = PackageFactory.SeedA, pub = Ed25519.PublicKey(seed), message = Encoding.UTF8.GetBytes("hello");
        byte[] sig = Ed25519.Sign(seed, message);
        Assert.True(Ed25519.Verify(pub, message, sig));
        Assert.False(Ed25519.Verify(pub, Encoding.UTF8.GetBytes("hellp"), sig));
        var bad = (byte[])sig.Clone();
        bad[10] ^= 1;
        Assert.False(Ed25519.Verify(pub, message, bad));
        Assert.False(Ed25519.Verify(Ed25519.PublicKey(PackageFactory.SeedB), message, sig));
        Assert.False(Ed25519.Verify(pub, message, sig[..63]));
    }
}

/// <summary>F16.1 installer on real temp folders and a real SQLite database; no sandbox (the sandbox end-to-end is <see cref="PluginInstallSandboxTests"/>).</summary>
public sealed class PluginInstallTests : IDisposable
{
    private readonly string temp = TestTemp.NewDir("susu-install");
    private readonly Database db;
    private readonly PluginInstallationRepository store;
    private readonly string userRoot;

    public PluginInstallTests()
    {
        db = Database.Open(Path.Combine(temp, "susu.db"));
        store = new PluginInstallationRepository(db);
        userRoot = Path.Combine(temp, "plugins");
    }

    public void Dispose() => db.Dispose();

    private static HostKeyring Keyring() => new([new KeyValuePair<string, byte[]>(PackageFactory.HostKeyId, Ed25519.PublicKey(PackageFactory.SeedA))]);

    private static BuiltInPackages BuiltIns() => new([new BuiltInPackage("app.susu.deepl", "1.2.0", new PermissionSet(["translate"], ["https://api.deepl.com:443"], ["apiKey"]))]);

    private PluginInstaller Installer(Func<HealthRequest, HealthResult>? health = null, Action<string>? fault = null)
        => new(userRoot, store, BuiltIns(), Keyring(), health, fault);

    private string Zip(string name, IDictionary<string, byte[]> files, byte[]? seed = null, string? keyId = null, IEnumerable<string>? extra = null)
        => PackageFactory.Zip(temp, name, files, seed, keyId, extra);

    private string Basic(string name = "echo.susuext", string version = "1.0.0", byte[]? seed = null, string? keyId = null, string id = "com.example.echo", string capabilities = "translate", string hosts = "", string secrets = "")
        => Zip(name, PackageFactory.Files(PackageFactory.Manifest(id, version, capabilities, hosts, secrets)), seed, keyId);

    // ---------- install, diff, identity ----------

    [Fact]
    public void An_unsigned_package_stages_installs_and_lists_with_the_full_permission_set_on_first_install()
    {
        var installer = Installer();
        var preview = installer.Preview(Basic(hosts: "https://api.example.com:443", secrets: "apiKey"));
        Assert.True(preview.Ok);
        Assert.Equal("unsigned", preview.SignerKind);
        Assert.Equal("none", preview.Diff!.Against);
        Assert.Equal(["translate"], preview.Diff.AddedCapabilities);
        Assert.Equal(["https://api.example.com:443"], preview.Diff.AddedOrigins);
        Assert.Equal(["apiKey"], preview.Diff.AddedSecrets);
        Assert.True(Directory.Exists(Path.Combine(userRoot, ".staging", preview.Token!)));
        Assert.False(Directory.Exists(Path.Combine(userRoot, "packages")), "nothing is live before the user confirms");

        var outcome = installer.Install(preview.Token!);
        Assert.True(outcome.Ok, outcome.Error);
        Assert.True(File.Exists(Path.Combine(userRoot, "packages", "com.example.echo", "1.0.0", "main.js")));
        var listed = Assert.Single(installer.Installed());
        Assert.Equal(("com.example.echo", "1.0.0", "unsigned"), (listed.Id, listed.Version, listed.SignerKind));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(userRoot, ".staging")));
        Assert.False(File.Exists(Path.Combine(userRoot, "journal.json")));
    }

    [Fact]
    public void A_third_party_signature_is_verified_and_recorded_but_not_presented_as_host_trust()
    {
        var installer = Installer();
        var preview = installer.Preview(Basic(seed: PackageFactory.SeedB));
        Assert.True(preview.Ok);
        Assert.Equal("thirdParty", preview.SignerKind);
        Assert.Equal(PackageFactory.ThirdPartyKeyId(PackageFactory.SeedB), preview.Signer);
        Assert.True(installer.Install(preview.Token!).Ok);
        Assert.Equal("key:" + PackageFactory.ThirdPartyKeyId(PackageFactory.SeedB), store.Active("com.example.echo")!.Signer);
    }

    [Fact]
    public void An_update_shows_only_what_changed_and_flags_additions()
    {
        var installer = Installer();
        Assert.True(installer.Install(installer.Preview(Basic(hosts: "https://a.example.com:443", secrets: "apiKey")).Token!).Ok);
        var preview = installer.Preview(Basic(name: "echo2.susuext", version: "1.1.0", capabilities: "translate,dictionary", hosts: "https://b.example.com:443", secrets: "apiKey,token"));
        Assert.True(preview.Ok);
        Assert.Equal("1.0.0", preview.ReplacesVersion);
        var diff = preview.Diff!;
        Assert.Equal(("installed", "1.0.0"), (diff.Against, diff.BaseVersion));
        Assert.Equal(["dictionary"], diff.AddedCapabilities);
        Assert.Equal(["https://b.example.com:443"], diff.AddedOrigins);
        Assert.Equal(["https://a.example.com:443"], diff.RemovedOrigins);
        Assert.Equal(["token"], diff.AddedSecrets);
        Assert.True(diff.HasAdditions);
        Assert.True(installer.Install(preview.Token!).Ok);
        Assert.Equal("1.1.0", Assert.Single(installer.Installed()).Version);
        // The replaced version stays until a later install prunes it (rollback material); the active row is the new one.
        Assert.True(Directory.Exists(Path.Combine(userRoot, "packages", "com.example.echo", "1.0.0")));
    }

    [Fact]
    public void A_signed_package_cannot_be_replaced_by_another_signer_or_an_unsigned_one_and_versions_must_increase()
    {
        var installer = Installer();
        Assert.True(installer.Install(installer.Preview(Basic(seed: PackageFactory.SeedB)).Token!).Ok);
        Assert.Contains(installer.Preview(Basic(version: "1.1.0", seed: PackageFactory.SeedA, keyId: PackageFactory.ThirdPartyKeyId(PackageFactory.SeedA))).Issues, i => i.Code == "signer-changed");
        Assert.Contains(installer.Preview(Basic(version: "1.1.0")).Issues, i => i.Code == "signature-removed");
        Assert.Contains(installer.Preview(Basic(version: "1.0.0", seed: PackageFactory.SeedB)).Issues, i => i.Code == "not-newer");
        Assert.Contains(installer.Preview(Basic(version: "0.9.0", seed: PackageFactory.SeedB)).Issues, i => i.Code == "not-newer");
        Assert.True(installer.Preview(Basic(version: "1.0.1", seed: PackageFactory.SeedB)).Ok);
    }

    // ---------- UPD02: built-in override ----------

    [Fact]
    public void A_built_in_id_is_taken_only_by_a_host_signed_newer_package_and_uninstall_restores_the_built_in()
    {
        var installer = Installer();
        string id = "app.susu.deepl";
        var hostFiles = (string version, string hosts) => PackageFactory.Files(PackageFactory.Manifest(id, version, "translate", hosts, "apiKey"));

        // a third party cannot impersonate a built-in, signed or not
        Assert.Contains(installer.Preview(Zip("a.susuext", hostFiles("2.0.0", ""))).Issues, i => i.Code == "builtin-id-not-host-signed");
        Assert.Contains(installer.Preview(Zip("b.susuext", hostFiles("2.0.0", ""), PackageFactory.SeedB)).Issues, i => i.Code == "builtin-id-not-host-signed");
        // the host key but not newer than the built-in 1.2.0
        Assert.Contains(installer.Preview(Zip("c.susuext", hostFiles("1.2.0", ""), PackageFactory.SeedA, PackageFactory.HostKeyId)).Issues, i => i.Code == "not-newer-than-builtin");
        Assert.Contains(installer.Preview(Zip("d.susuext", hostFiles("1.1.9", ""), PackageFactory.SeedA, PackageFactory.HostKeyId)).Issues, i => i.Code == "not-newer-than-builtin");
        // a host-signed newer one is accepted, diffed against the built-in, listed as an override
        var preview = installer.Preview(Zip("e.susuext", hostFiles("1.3.0", "https://api.deepl.com:443,https://extra.example.com:443"), PackageFactory.SeedA, PackageFactory.HostKeyId));
        Assert.True(preview.Ok);
        Assert.Equal(("builtin", "1.2.0"), (preview.Diff!.Against, preview.Diff.BaseVersion));
        Assert.Equal("1.2.0", preview.OverridesBuiltIn);
        Assert.Equal(["https://extra.example.com:443"], preview.Diff.AddedOrigins);
        Assert.Equal("host", preview.SignerKind);
        Assert.True(installer.Install(preview.Token!).Ok);
        var listed = Assert.Single(installer.Installed());
        Assert.Equal("1.2.0", listed.OverridesBuiltIn);
        Assert.NotNull(installer.ActiveDirectory(id));

        var uninstalled = installer.Uninstall(id);
        Assert.True(uninstalled.Ok);
        Assert.Equal("1.2.0", uninstalled.RestoredBuiltIn);
        Assert.Null(installer.ActiveDirectory(id));
        Assert.Empty(installer.Installed());
        Assert.False(Directory.Exists(Path.Combine(userRoot, "packages", id)));
    }

    [Fact]
    public void A_signature_must_cover_every_file_and_the_manifest_bytes()
    {
        var installer = Installer();
        var files = PackageFactory.Files(PackageFactory.Manifest("app.susu.deepl", "1.5.0", "translate", "", "apiKey"));
        // signed, then a file was added
        string good = Zip("good.susuext", files, PackageFactory.SeedA, PackageFactory.HostKeyId);
        string added = Zip("added.susuext", new Dictionary<string, byte[]>(files) { ["extra.js"] = [1] }.Concat(ReadSignature(good)).ToDictionary(), keyId: null);
        Assert.Contains(installer.Preview(added).Issues, i => i.Code == "signature-invalid");
        // signed, then main.js changed
        var tampered = new Dictionary<string, byte[]>(files) { ["main.js"] = Encoding.UTF8.GetBytes("export default {};") };
        Assert.Contains(installer.Preview(Zip("tampered.susuext", tampered.Concat(ReadSignature(good)).ToDictionary())).Issues, i => i.Code == "signature-invalid");
        // signed, then the manifest was edited (a wider origin)
        var wider = PackageFactory.Files(PackageFactory.Manifest("app.susu.deepl", "1.5.0", "translate", "https://evil.example.com:443", "apiKey"));
        Assert.Contains(installer.Preview(Zip("wider.susuext", wider.Concat(ReadSignature(good)).ToDictionary())).Issues, i => i.Code == "signature-invalid");
        // an unknown key id and a malformed signature
        Assert.Contains(installer.Preview(Zip("unknown.susuext", files, PackageFactory.SeedB, "some-unknown-key")).Issues, i => i.Code == "unknown-key");
        var broken = new Dictionary<string, byte[]>(files) { ["signature"] = Encoding.UTF8.GetBytes("keyId: x\nalgorithm: ed25519\nsignature: AAAA\n") };
        Assert.Contains(installer.Preview(Zip("broken.susuext", broken)).Issues, i => i.Code is "signature-invalid" or "unknown-key");
        // the untouched one still passes
        Assert.True(installer.Preview(good).Ok);
    }

    private static IEnumerable<KeyValuePair<string, byte[]>> ReadSignature(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        using var s = zip.GetEntry("signature")!.Open();
        using var m = new MemoryStream();
        s.CopyTo(m);
        return [new("signature", m.ToArray())];
    }

    [Fact]
    public void A_broken_manifest_or_missing_pieces_are_rejected_before_anything_is_staged_for_install()
    {
        var installer = Installer();
        Assert.Contains(installer.Preview(Zip("nomanifest.susuext", new Dictionary<string, byte[]> { ["main.js"] = [1] })).Issues, i => i.Code == "missing");
        Assert.False(installer.Preview(Zip("garbage.susuext", new Dictionary<string, byte[]> { ["manifest.yaml"] = Encoding.UTF8.GetBytes("id: [unclosed"), ["main.js"] = [1] })).Ok);
        Assert.Contains(installer.Preview(Zip("noversion.susuext", new Dictionary<string, byte[]> { ["manifest.yaml"] = Encoding.UTF8.GetBytes("id: com.example.x\nname: X\napiVersion: 1\nminHost: 1\ncapabilities:\n  - translate\n"), ["main.js"] = [1] })).Issues, i => i.Code == "missing");
        Assert.Contains(installer.Preview(Zip("badversion.susuext", PackageFactory.Files(PackageFactory.Manifest(version: "1.0")))).Issues, i => i.Code == "invalid");
        Assert.Contains(installer.Preview(Zip("noentry.susuext", new Dictionary<string, byte[]> { ["manifest.yaml"] = Encoding.UTF8.GetBytes(PackageFactory.Manifest()) })).Issues, i => i.Path == "entry");
        Assert.Contains(installer.Preview(Zip("futurehost.susuext", new Dictionary<string, byte[]> { ["manifest.yaml"] = Encoding.UTF8.GetBytes(PackageFactory.Manifest().Replace("minHost: 1", "minHost: 9")), ["main.js"] = [1] })).Issues, i => i.Code == "host-too-old");
        Assert.False(installer.Preview(Path.Combine(temp, "does-not-exist.susuext")).Ok);
        string notZip = Path.Combine(temp, "notzip.susuext");
        File.WriteAllText(notZip, "this is not a zip");
        Assert.Contains(installer.Preview(notZip).Issues, i => i.Code == "unreadable");
        Assert.Empty(store.All());
        Assert.False(Directory.Exists(Path.Combine(userRoot, "packages")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(userRoot, ".staging")));
    }

    // ---------- activation transaction ----------

    [Theory]
    [InlineData("recheck")]
    [InlineData("move")]
    [InlineData("switch")]
    [InlineData("health")]
    public void A_failure_at_any_step_of_a_first_install_leaves_nothing_active_and_nothing_on_disk(string step)
    {
        var installer = Installer(fault: s => { if (s == step) throw new IOException("injected " + s); });
        var preview = installer.Preview(Basic());
        var outcome = installer.Install(preview.Token!);
        Assert.False(outcome.Ok);
        Assert.Null(store.Active("com.example.echo"));
        Assert.Empty(installer.Installed());
        Assert.False(Directory.Exists(Path.Combine(userRoot, "packages", "com.example.echo", "1.0.0")));
        Assert.False(File.Exists(Path.Combine(userRoot, "journal.json")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(userRoot, ".staging")));
    }

    [Fact]
    public void A_failed_health_check_restores_the_previous_version_and_keeps_its_files()
    {
        bool failNext = false;
        var installer = Installer(health: r => failNext ? new HealthResult(false, "does not start") : PluginInstaller.Structural(r));
        Assert.True(installer.Install(installer.Preview(Basic(seed: PackageFactory.SeedB)).Token!).Ok);
        string oldHash = store.Active("com.example.echo")!.Hash;

        failNext = true;
        var preview = installer.Preview(Basic(version: "1.1.0", seed: PackageFactory.SeedB, name: "second.susuext"));
        Assert.True(preview.Ok);
        var outcome = installer.Install(preview.Token!);
        Assert.Equal("install.healthFailed", outcome.Error);
        var active = store.Active("com.example.echo")!;
        Assert.Equal(("1.0.0", oldHash), (active.Version, active.Hash));
        Assert.True(File.Exists(Path.Combine(userRoot, "packages", "com.example.echo", "1.0.0", "main.js")));
        Assert.False(Directory.Exists(Path.Combine(userRoot, "packages", "com.example.echo", "1.1.0")));
        Assert.Equal("1.0.0", Assert.Single(installer.Installed()).Version);
    }

    [Fact]
    public void A_crash_after_the_switch_is_rolled_back_at_the_next_start()
    {
        var crashing = Installer(fault: s => { if (s == "health") throw new OutOfMemoryException("simulated process death"); });
        var first = Installer();
        Assert.True(first.Install(first.Preview(Basic(seed: PackageFactory.SeedB)).Token!).Ok);
        var preview = crashing.Preview(Basic(version: "1.1.0", seed: PackageFactory.SeedB, name: "crash.susuext"));
        Assert.Throws<OutOfMemoryException>(() => crashing.Install(preview.Token!));
        // the process "died" mid-transaction: the new row is active, the journal exists
        Assert.Equal("1.1.0", store.Active("com.example.echo")!.Version);
        Assert.True(File.Exists(Path.Combine(userRoot, "journal.json")));

        var restarted = Installer();
        restarted.Recover();
        Assert.Equal("1.0.0", store.Active("com.example.echo")!.Version);
        Assert.False(File.Exists(Path.Combine(userRoot, "journal.json")));
        Assert.False(Directory.Exists(Path.Combine(userRoot, "packages", "com.example.echo", "1.1.0")));
        Assert.True(File.Exists(Path.Combine(userRoot, "packages", "com.example.echo", "1.0.0", "main.js")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(userRoot, ".staging")));
    }

    [Fact]
    public void A_crash_during_a_first_install_leaves_no_active_row_after_recovery()
    {
        var crashing = Installer(fault: s => { if (s == "health") throw new OutOfMemoryException("simulated"); });
        var preview = crashing.Preview(Basic());
        Assert.Throws<OutOfMemoryException>(() => crashing.Install(preview.Token!));
        Assert.NotNull(store.Active("com.example.echo"));
        Installer().Recover();
        Assert.Null(store.Active("com.example.echo"));
        Assert.False(Directory.Exists(Path.Combine(userRoot, "packages", "com.example.echo")));
    }

    [Fact]
    public void Bytes_changed_between_confirmation_and_activation_are_refused()
    {
        var installer = Installer();
        var preview = installer.Preview(Basic());
        File.AppendAllText(Path.Combine(userRoot, ".staging", preview.Token!, "main.js"), "// changed after the preview\n");
        var outcome = installer.Install(preview.Token!);
        Assert.Equal("install.changed", outcome.Error);
        Assert.Empty(installer.Installed());
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(userRoot, ".staging")));
    }

    [Fact]
    public void A_wrong_or_replaced_token_and_discard_install_nothing()
    {
        var installer = Installer();
        var first = installer.Preview(Basic());
        var second = installer.Preview(Basic(name: "two.susuext"));
        Assert.Equal("install.noPending", installer.Install(first.Token!).Error); // a new preview replaced it
        Assert.Equal("install.noPending", installer.Install("nonsense").Error);
        installer.Discard(second.Token!);
        Assert.Equal("install.noPending", installer.Install(second.Token!).Error);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(userRoot, ".staging")));
        Assert.Equal("uninstall.notInstalled", installer.Uninstall("com.example.echo").Error);
    }

    [Fact]
    public void Uninstalling_a_plain_package_removes_it_and_a_reinstall_works()
    {
        var installer = Installer();
        Assert.True(installer.Install(installer.Preview(Basic()).Token!).Ok);
        var outcome = installer.Uninstall("com.example.echo");
        Assert.True(outcome.Ok);
        Assert.Null(outcome.RestoredBuiltIn);
        Assert.Empty(installer.Installed());
        Assert.False(Directory.Exists(Path.Combine(userRoot, "packages", "com.example.echo")));
        Assert.True(installer.Install(installer.Preview(Basic(name: "again.susuext")).Token!).Ok);
    }

    // ---------- UPD01: adversarial archives ----------

    public static IEnumerable<object[]> BadEntries() =>
    [
        ["../escape.js", "traversal"],
        ["lib/../../escape.js", "traversal"],
        ["/abs.js", "absolute"],
        ["C:/abs.js", "absolute"],
        ["C:\\abs.js", "backslash"],
        ["lib\\main.js", "backslash"],
        ["main.js:stream", "alternate-stream"],
        ["lib/a.js::$DATA", "alternate-stream"],
        ["CON", "device-name"],
        ["lib/nul.txt", "device-name"],
        ["Aux.js", "device-name"],
        ["COM1.js", "device-name"],
        ["lpt9", "device-name"],
        ["trailing.", "trailing-dot"],
        ["space.js ", "trailing-dot"],
        ["PROGRA~1/a.js", "short-name"],
        ["a|b.js", "bad-character"],
        ["lib//double.js", "empty-segment"],
        ["data.zip", "nested-archive"],
        ["lib/inner.SUSUEXT", "nested-archive"],
        ["x.7z", "nested-archive"],
    ];

    [Theory]
    [MemberData(nameof(BadEntries))]
    public void A_bad_entry_name_is_rejected_before_anything_is_written(string entry, string code)
    {
        var files = PackageFactory.Files(PackageFactory.Manifest());
        files[entry] = [1, 2, 3];
        string zip = RawZip("bad.susuext", files);
        string destination = Path.Combine(temp, "dest-" + Guid.NewGuid().ToString("N"));
        var issues = SafeUnzip.Extract(zip, destination);
        Assert.Contains(issues, i => i.Code == code);
        Assert.False(Directory.Exists(destination), "no half-extracted package");
        Assert.False(File.Exists(Path.Combine(temp, "escape.js")));
        Assert.False(installerAccepts(zip));
    }

    private bool installerAccepts(string zip) => Installer().Preview(zip).Ok;

    /// <summary>Writes entry names verbatim (the factory's CreateEntry does not alter them either); here only to keep the intent obvious.</summary>
    private string RawZip(string name, IDictionary<string, byte[]> files) => PackageFactory.Zip(temp, name, files);

    [Fact]
    public void Names_equal_except_for_case_are_rejected()
    {
        var files = PackageFactory.Files(PackageFactory.Manifest());
        files["lib/Util.js"] = [1];
        files["lib/util.js"] = [2];
        var issues = SafeUnzip.Extract(RawZip("case.susuext", files), Path.Combine(temp, "case-dest"));
        Assert.Contains(issues, i => i.Code == "case-duplicate");
        Assert.False(Directory.Exists(Path.Combine(temp, "case-dest")));
    }

    [Fact]
    public void A_symbolic_link_or_reparse_entry_is_rejected()
    {
        string path = Path.Combine(temp, "link.susuext");
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (name, bytes) in PackageFactory.Files(PackageFactory.Manifest()))
            { using var s = zip.CreateEntry(name).Open(); s.Write(bytes); }
            var unixLink = zip.CreateEntry("lib-link");
            unixLink.ExternalAttributes = unchecked((int)(0xA1FFu << 16)); // S_IFLNK | 0777
            using (var s = unixLink.Open()) s.Write("main.js"u8);
            var winLink = zip.CreateEntry("junction.js");
            winLink.ExternalAttributes = (int)FileAttributes.ReparsePoint;
            using (var s = winLink.Open()) s.Write([1]);
        }
        var issues = SafeUnzip.Extract(path, Path.Combine(temp, "link-dest"));
        Assert.Equal(2, issues.Count(i => i.Code == "link"));
        Assert.False(Directory.Exists(Path.Combine(temp, "link-dest")));
    }

    [Fact]
    public void Entry_count_total_size_file_size_and_expansion_ratio_are_bounded()
    {
        // more than 256 files
        var many = PackageFactory.Files(PackageFactory.Manifest());
        for (int i = 0; i < 260; i++) many[$"lib/f{i}.js"] = [1];
        Assert.Contains(SafeUnzip.Extract(RawZip("many.susuext", many), Path.Combine(temp, "many-dest")), i => i.Code == "too-many-files");
        // one file over 4 MiB (incompressible would exceed the zip cap, so repeat bytes: the ratio rule also fires)
        var big = PackageFactory.Files(PackageFactory.Manifest());
        big["lib/big.bin"] = new byte[5 * 1024 * 1024];
        var bigIssues = SafeUnzip.Extract(RawZip("big.susuext", big), Path.Combine(temp, "big-dest"));
        Assert.Contains(bigIssues, i => i.Code == "too-large");
        Assert.Contains(bigIssues, i => i.Code == "ratio");
        // many 4 MiB zero files: each under the file cap, together over 16 MiB, and a huge ratio
        var bomb = PackageFactory.Files(PackageFactory.Manifest());
        for (int i = 0; i < 6; i++) bomb[$"lib/z{i}.bin"] = new byte[3 * 1024 * 1024];
        var bombIssues = SafeUnzip.Extract(RawZip("bomb.susuext", bomb), Path.Combine(temp, "bomb-dest"));
        Assert.Contains(bombIssues, i => i.Code == "ratio");
        Assert.False(Directory.Exists(Path.Combine(temp, "bomb-dest")));
    }

    [Fact]
    public void A_header_that_understates_the_size_is_stopped_while_copying()
    {
        // Build a valid zip, then rewrite the uncompressed size field of the big entry to a small number in both headers.
        var files = PackageFactory.Files(PackageFactory.Manifest());
        files["lib/liar.bin"] = new byte[SafePackage.MaxFileBytes + 1024];
        string path = RawZip("liar.susuext", files);
        byte[] bytes = File.ReadAllBytes(path);
        PatchUncompressedSize(bytes, "lib/liar.bin", 1000);
        File.WriteAllBytes(path, bytes);
        var destination = Path.Combine(temp, "liar-dest");
        var issues = SafeUnzip.Extract(path, destination);
        if (issues.Count == 0)
            foreach (string file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories))
                Assert.True(new FileInfo(file).Length <= SafePackage.MaxFileBytes, "the copy is bounded by the declared size or the cap");
        else Assert.False(Directory.Exists(destination));
    }

    private static void PatchUncompressedSize(byte[] zip, string name, uint size)
    {
        byte[] needle = Encoding.ASCII.GetBytes(name);
        for (int i = 0; i + needle.Length < zip.Length; i++)
        {
            if (!zip.AsSpan(i, needle.Length).SequenceEqual(needle)) continue;
            // local header: signature at i-30, uncompressed size at +22; central header: signature at i-46, size at +24
            if (i >= 30 && zip[i - 30] == 0x50 && zip[i - 29] == 0x4b && zip[i - 28] == 3 && zip[i - 27] == 4) BitConverter.GetBytes(size).CopyTo(zip, i - 30 + 22);
            if (i >= 46 && zip[i - 46] == 0x50 && zip[i - 45] == 0x4b && zip[i - 44] == 1 && zip[i - 43] == 2) BitConverter.GetBytes(size).CopyTo(zip, i - 46 + 24);
        }
    }

    [Fact]
    public void A_good_package_passes_the_same_unzip()
    {
        var files = PackageFactory.Files(PackageFactory.Manifest());
        files["lib/util.js"] = [1];
        files["icon.svg"] = [2];
        string destination = Path.Combine(temp, "ok-dest");
        Assert.Empty(SafeUnzip.Extract(RawZip("ok.susuext", files), destination));
        Assert.True(File.Exists(Path.Combine(destination, "lib", "util.js")));
    }

    [Fact]
    public void Plugin_versions_order_numerically_and_reject_loose_forms()
    {
        Assert.True(PackageVersion.TryParse("1.10.0", out var a) && PackageVersion.TryParse("1.9.9", out var b) && a > b);
        foreach (string bad in new[] { "1.0", "1.0.0.0", "1.0.0-beta", "01.0.0", "v1.0.0", "", "1.a.0", "1.0.1000000" })
            Assert.False(PackageVersion.TryParse(bad, out _), bad);
    }
}

/// <summary>
/// F16.1 through the real AppContainer sandbox: a package installed from a real zip is loaded and called in the real plugin host, the
/// health check is a real sandbox load plus call, and a package that cannot load rolls back to the previous version, which still works.
/// Needs the NativeAOT susu.exe publish like the other sandbox tests and skips itself without it.
/// </summary>
public sealed class PluginInstallSandboxTests : IDisposable
{
    private readonly string temp = TestTemp.NewDir("susu-install-sbx");
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

    private static string RelativeSlash(string root, string full) => Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');

    private static async Task<(bool Loaded, string? Text)> CallAsync(string stagedRoot, string pluginId, string directory, CancellationToken ct)
    {
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(stagedRoot, "susu.exe"), stagedRoot, "quickjs", KeepProfile: false));
        try
        {
            var loaded = session.Load(pluginId, RelativeSlash(stagedRoot, directory));
            if (!loaded.Ok) return (false, loaded.Error);
            var (_, _, task) = session.Invoke(pluginId, "translate", "{\"text\":\"hi\"}", jobId: "install-check", origins: []);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            return (envelope.Type == Susu.Contracts.IpcMessageType.Completed, envelope.Payload?.GetRawText());
        }
        finally { session.Shutdown(2000); }
    }

    [Fact]
    public async Task A_package_installed_from_a_real_zip_runs_in_the_real_sandbox_and_a_package_that_cannot_load_rolls_back()
    {
        string? publish = FindPublish();
        if (publish is null) return;
        string staged = Path.Combine(temp, "host");
        Directory.CreateDirectory(staged);
        foreach (string file in Directory.EnumerateFiles(publish))
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(staged, Path.GetFileName(file)));
        string userRoot = Path.Combine(staged, "plugins", "user"); // inside the sandbox's read scope; the product's %APPDATA% folder is not yet (see F16.md)
        db = Database.Open(Path.Combine(temp, "susu.db"));
        var store = new PluginInstallationRepository(db);
        var ct = TestContext.Current.CancellationToken;

        var healthRuns = 0;
        var installer = new PluginInstaller(userRoot, store, new BuiltInPackages([]), HostKeyring.Empty, request =>
        {
            Interlocked.Increment(ref healthRuns);
            var (ok, text) = CallAsync(staged, request.PackageId, request.Directory, ct).GetAwaiter().GetResult();
            return new HealthResult(ok, ok ? null : text);
        });

        // install v1 from a real zip, signed by a third party
        string v1 = PackageFactory.Zip(temp, "v1.susuext", PackageFactory.Files(PackageFactory.Manifest(version: "1.0.0")), PackageFactory.SeedB);
        var preview = installer.Preview(v1);
        Assert.True(preview.Ok, string.Join(",", preview.Issues.Select(i => i.Code)));
        var installed = installer.Install(preview.Token!);
        Assert.True(installed.Ok, installed.Error);
        Assert.Equal(1, healthRuns);

        // enable it and call it through the real sandbox from the active directory
        string active = installer.ActiveDirectory("com.example.echo")!;
        var (okCall, text) = await CallAsync(staged, "com.example.echo", active, ct);
        Assert.True(okCall, text);
        Assert.Contains("echo:hi", text);

        // v1.1.0 parses but cannot run (syntax error): the sandbox health check fails and v1 stays active and working
        var broken = PackageFactory.Files(PackageFactory.Manifest(version: "1.1.0"), "export default { async translate( {{{ ;\n");
        var brokenPreview = installer.Preview(PackageFactory.Zip(temp, "v11.susuext", broken, PackageFactory.SeedB));
        Assert.True(brokenPreview.Ok);
        var failed = installer.Install(brokenPreview.Token!);
        Assert.Equal("install.healthFailed", failed.Error);
        Assert.Equal(2, healthRuns);
        var row = store.Active("com.example.echo")!;
        Assert.Equal("1.0.0", row.Version);
        Assert.False(Directory.Exists(Path.Combine(userRoot, "packages", "com.example.echo", "1.1.0")));
        var (stillOk, stillText) = await CallAsync(staged, "com.example.echo", installer.ActiveDirectory("com.example.echo")!, ct);
        Assert.True(stillOk, stillText);
        Assert.Contains("echo:hi", stillText);

        // a good update goes through the sandbox check and is the one that runs afterwards
        string newer = Path.Combine(temp, "v12.susuext");
        var good2 = PackageFactory.Files(PackageFactory.Manifest(version: "1.2.0"), "export default { async translate(req) { return { text: `v12:${req.text}` }; } };\n");
        var p2 = installer.Preview(PackageFactory.Zip(temp, "v12.susuext", good2, PackageFactory.SeedB));
        Assert.True(installer.Install(p2.Token!).Ok);
        var (ok12, text12) = await CallAsync(staged, "com.example.echo", installer.ActiveDirectory("com.example.echo")!, ct);
        Assert.True(ok12, text12);
        Assert.Contains("v12:hi", text12);
        _ = newer;

        Assert.True(installer.Uninstall("com.example.echo").Ok);
        Assert.Null(installer.ActiveDirectory("com.example.echo"));
    }
}
