using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Susu.Abstractions;
using Susu.Domain;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>A protector with its own key: what a second Windows user's DPAPI looks like (cannot open the first user's blobs).</summary>
public sealed class KeyedProtector(byte key) : ISecretProtector
{
    public byte[] Protect(ReadOnlySpan<byte> plaintext) => [key, .. plaintext.ToArray().Select(b => (byte)(b ^ key))];
    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => ciphertext.Length > 0 && ciphertext[0] == key ? [.. ciphertext[1..].ToArray().Select(b => (byte)(b ^ key))] : throw new System.Security.Cryptography.CryptographicException("another user");
}

/// <summary>One installation: data root, settings, secrets and the backup service, with a restart that reopens them from disk.</summary>
internal sealed class BackupRig : IDisposable
{
    public readonly TempRoot Root = new();
    public readonly ManualClock Clock = new();
    public readonly ISecretProtector Protector;
    public SettingsStore Store = null!;
    public SecretStore Secrets = null!;
    public BackupService Service = null!;
    public Dictionary<string, string?> Available = BuiltInCatalog.Packages.ToDictionary(p => p.PackageId, p => (string?)null);
    public List<BackupPluginRef> UserPlugins = [];
    public BackupLocalData Local = new(3, 1);

    public BackupRig(ISecretProtector? protector = null)
    {
        Protector = protector ?? new XorProtector();
        Available["native.els"] = null;
        Available["native.sapi"] = null;
        Open();
    }

    public AppPaths Paths => Root.Paths;

    public void Open()
    {
        Store?.Dispose();
        Store = new SettingsStore(Paths, Clock);
        Secrets = new SecretStore(Paths.Secrets, Protector);
        Service = new BackupService(Paths, Store, Secrets, Protector, Clock, new BackupHost("1.2.3", () => Available, () => UserPlugins, () => Local));
    }

    public void Seed(string label = "DeepL", string key = "sk-test-SECRET-1234", string? account = null)
    {
        account ??= "acct-deepl";
        var grant = new CredentialGrant("app.susu.deepl", "builtin", "apiKey", "https://api-free.deepl.com:443", "header:Authorization");
        var state = Store.State;
        var next = state.Effective with
        {
            Accounts = [new AccountSettings(account, label, ["apiKey"], [grant])],
            Prompts = [new PromptProfile("p1", "Polite " + label, "Translate politely: {text}")],
        };
        var saved = Store.Save(next, state.Revision, state.FileHash);
        Assert.Equal(SaveStatus.Saved, saved.Status);
        Secrets.Write(account, "apiKey", key);
    }

    public string SettingsHash => AtomicFile.HashOf(Paths.Settings);
    public string SecretsHash => AtomicFile.HashOf(Paths.Secrets);

    /// <summary>Everything a real restart does before the config is loaded: recover the journal, switch a Ready import, reopen from disk.</summary>
    public BackupApplyResult? Restart(IFaultPoint? faults = null, bool recover = true)
    {
        Store.Dispose();
        if (recover) new ConfigTransaction(Paths.Transactions).Recover();
        var result = BackupImport.ApplyPending(Paths, Clock, faults);
        Open();
        return result;
    }

    public string Export(string name, BackupExportOptions options)
    {
        string path = Path.Combine(Root.Root, name);
        var outcome = Service.Export(path, options);
        Assert.True(outcome.Ok, outcome.Error);
        return path;
    }

    public void Dispose() { Store.Dispose(); Root.Dispose(); }
}

public class F17BackupTests
{
    private const string Password = "correct horse battery";
    private static readonly BackupExportOptions WithKeys = new(true, Password);

    private static string CodeOf(Action action) => Assert.Throws<BackupException>(action).Code;

    // ---------- archive crafting ----------

    private static byte[] ValidSettings(Func<string, string>? edit = null)
    {
        string text = SettingsYaml.Write(BuiltInCatalog.Defaults() with { Revision = 3 });
        return Encoding.UTF8.GetBytes(edit is null ? text : edit(text));
    }

    private static byte[] ManifestFor(IDictionary<string, byte[]> files, int formatVersion = 1, int schema = 1, bool? includesSecrets = null, string format = "susubak")
    {
        var hashes = string.Join(",", files.Where(f => f.Key != "manifest.json").Select(f => $"\"{f.Key}\":\"{AtomicFile.Hash(f.Value)}\""));
        bool secrets = includesSecrets ?? files.ContainsKey("secrets.json");
        return Encoding.UTF8.GetBytes($"{{\"format\":\"{format}\",\"formatVersion\":{formatVersion},\"app\":\"Su-Su\",\"appVersion\":\"9.9\",\"schemaVersion\":{schema},\"createdUtc\":\"2026-01-01T00:00:00.0000000Z\",\"includesSecrets\":{secrets.ToString().ToLowerInvariant()},\"files\":{{{hashes}}}}}");
    }

    private static byte[] ZipOf(IEnumerable<(string Name, byte[] Data)> entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
            {
                using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    /// <summary>A structurally valid plain backup with a consistent manifest, for the cases that change one thing.</summary>
    private static byte[] Craft(Action<Dictionary<string, byte[]>>? edit = null, int formatVersion = 1, int schema = 1, bool? includesSecrets = null)
    {
        var files = new Dictionary<string, byte[]> { ["settings.yaml"] = ValidSettings(), ["plugins.json"] = Encoding.UTF8.GetBytes("{\"plugins\":[]}") };
        edit?.Invoke(files);
        files["manifest.json"] = ManifestFor(files, formatVersion, schema, includesSecrets);
        return ZipOf(files.Select(f => (f.Key, f.Value)));
    }

    private static string WriteTemp(BackupRig rig, byte[] bytes, string name = "in.susubak")
    {
        string path = Path.Combine(rig.Root.Root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ---------- export ----------

    [Fact]
    public void Export_default_has_no_keys_no_grants_and_only_the_fixed_entries()
    {
        using var rig = new BackupRig();
        rig.Seed();
        string path = rig.Export("plain.susubak", new BackupExportOptions(false, null));
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal((byte)'P', bytes[0]);
        Assert.DoesNotContain("sk-test-SECRET-1234", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(["manifest.json", "plugins.json", "settings.yaml"], zip.Entries.Select(e => e.FullName).Order().ToArray());
        string yaml; using (var r = new StreamReader(zip.GetEntry("settings.yaml")!.Open())) yaml = r.ReadToEnd();
        var (settings, issues) = SettingsYaml.Read(yaml);
        Assert.Empty(issues);
        Assert.Single(settings!.Accounts);
        Assert.Empty(settings.Accounts[0].Grants); // authorization is not exported
        Assert.DoesNotContain("api-free.deepl.com", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_with_keys_needs_a_password_and_encrypts_everything()
    {
        using var rig = new BackupRig();
        rig.Seed();
        string path = Path.Combine(rig.Root.Root, "k.susubak");
        Assert.Equal("password-required", rig.Service.Export(path, new BackupExportOptions(true, null)).Error);
        Assert.False(File.Exists(path));
        Assert.Equal("password-length", rig.Service.Export(path, new BackupExportOptions(true, "short")).Error);
        var outcome = rig.Service.Export(path, WithKeys);
        Assert.True(outcome.Ok);
        Assert.True(outcome.Encrypted);
        Assert.True(outcome.IncludedSecrets);
        Assert.Equal(1, outcome.SecretCount);
        string text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        Assert.StartsWith("SUSUBAK", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-test-SECRET-1234", text, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.yaml", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Polite", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_can_encrypt_settings_without_keys_and_failure_leaves_the_target_alone()
    {
        using var rig = new BackupRig();
        rig.Seed();
        string path = rig.Export("e.susubak", new BackupExportOptions(false, Password));
        Assert.StartsWith("SUSUBAK", Encoding.Latin1.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);
        Assert.Equal(0, rig.Service.Preview(path, Password).BackupSecrets);
        Assert.Equal("write-failed", rig.Service.Export(rig.Root.Root, new BackupExportOptions(false, null)).Error); // a directory is not a file
        Assert.Equal("path-invalid", rig.Service.Export("", new BackupExportOptions(false, null)).Error);
    }

    // ---------- passwords, tampering, truncation ----------

    [Fact]
    public void Wrong_password_or_none_changes_nothing()
    {
        using var rig = new BackupRig();
        rig.Seed();
        string path = rig.Export("k.susubak", WithKeys);
        string settings = rig.SettingsHash, secrets = rig.SecretsHash;
        Assert.Equal("decrypt-failed", CodeOf(() => rig.Service.Preview(path, "wrong password!!")));
        Assert.Equal("password-required", CodeOf(() => rig.Service.Preview(path, null)));
        Assert.Equal("decrypt-failed", CodeOf(() => rig.Service.Preview(path, new string('x', 100_000)))); // no KDF run for an absurd password
        Assert.Equal(settings, rig.SettingsHash);
        Assert.Equal(secrets, rig.SecretsHash);
        Assert.False(Directory.Exists(Path.Combine(rig.Paths.Imports, "staged")));
        Assert.Equal("None", rig.Service.Status().State);
    }

    [Theory]
    [InlineData(-1)] // last byte: the GCM tag
    [InlineData(40)] // header JSON (associated data)
    [InlineData(200)] // ciphertext
    public void Tampered_encrypted_file_is_refused(int position)
    {
        using var rig = new BackupRig();
        rig.Seed();
        byte[] bytes = File.ReadAllBytes(rig.Export("k.susubak", WithKeys));
        bytes[position < 0 ? bytes.Length + position : position] ^= 0x01;
        string code = CodeOf(() => rig.Service.Preview(WriteTemp(rig, bytes), Password));
        Assert.Contains(code, new[] { "decrypt-failed", "corrupt", "kdf-params", "unsupported-kdf" });
        Assert.False(Directory.Exists(Path.Combine(rig.Paths.Imports, "staged")));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(11)]
    [InlineData(300)]
    public void Truncated_files_are_refused(int keep)
    {
        using var rig = new BackupRig();
        rig.Seed();
        foreach (var options in new[] { WithKeys, new BackupExportOptions(false, null) })
        {
            byte[] bytes = File.ReadAllBytes(rig.Export("t.susubak", options));
            byte[] cut = bytes[..Math.Min(keep, bytes.Length - 1)];
            string code = CodeOf(() => rig.Service.Preview(WriteTemp(rig, cut), Password));
            Assert.Contains(code, new[] { "corrupt", "decrypt-failed", "not-a-backup" });
            byte[] half = bytes[..(bytes.Length / 2)];
            code = CodeOf(() => rig.Service.Preview(WriteTemp(rig, half), Password));
            Assert.Contains(code, new[] { "corrupt", "decrypt-failed" });
        }
        Assert.Equal("not-a-backup", CodeOf(() => rig.Service.Preview(WriteTemp(rig, "hello, not a backup"u8.ToArray()), null)));
        Assert.Equal("unreadable", CodeOf(() => rig.Service.Preview(Path.Combine(rig.Root.Root, "nope.susubak"), null)));
    }

    [Fact]
    public void Plain_archive_changed_after_export_fails_its_manifest_hash()
    {
        using var rig = new BackupRig();
        var files = new Dictionary<string, byte[]> { ["settings.yaml"] = ValidSettings(), ["plugins.json"] = "{\"plugins\":[]}"u8.ToArray() };
        byte[] manifest = ManifestFor(files);
        files["settings.yaml"] = ValidSettings(t => t.Replace("theme: \"light\"", "theme: \"light\" ")); // different bytes, same manifest
        files["manifest.json"] = manifest;
        Assert.Equal("hash-mismatch", CodeOf(() => rig.Service.Preview(WriteTemp(rig, ZipOf(files.Select(f => (f.Key, f.Value)))), null)));
        // An entry listed but absent, and an entry present but unlisted.
        files.Remove("plugins.json");
        Assert.Equal("manifest-mismatch", CodeOf(() => rig.Service.Preview(WriteTemp(rig, ZipOf(files.Select(f => (f.Key, f.Value)))), null)));
    }

    // ---------- hostile archives ----------

    [Theory]
    [InlineData("../evil.yaml")]
    [InlineData("..\\evil.yaml")]
    [InlineData("/etc/settings.yaml")]
    [InlineData("C:\\Windows\\x.yaml")]
    [InlineData("sub/settings.yaml")]
    [InlineData("settings.yaml:stream")]
    [InlineData("..")]
    public void Path_escape_names_are_refused_and_nothing_is_written_outside(string name)
    {
        using var rig = new BackupRig();
        byte[] bytes = Craft(f => f[name] = "x"u8.ToArray());
        Assert.Equal("path-escape", CodeOf(() => rig.Service.Preview(WriteTemp(rig, bytes), null)));
        Assert.False(File.Exists(Path.Combine(rig.Root.Root, "evil.yaml")));
        Assert.False(Directory.Exists(Path.Combine(rig.Paths.Imports, "staged")));
    }

    [Theory] // never import code or caches: only the four fixed names exist
    [InlineData("plugins/app.example.x/main.js")]
    [InlineData("susu.db")]
    [InlineData("cache.bin")]
    [InlineData("logs.txt")]
    [InlineData("secrets.dat")]
    public void Code_caches_database_and_unknown_entries_are_refused(string name)
    {
        using var rig = new BackupRig();
        byte[] bytes = Craft(f => f[name] = "x"u8.ToArray());
        string code = CodeOf(() => rig.Service.Preview(WriteTemp(rig, bytes), null));
        Assert.Contains(code, new[] { "unexpected-entry", "path-escape" });
    }

    [Fact]
    public void Duplicate_entries_are_refused()
    {
        using var rig = new BackupRig();
        var files = new Dictionary<string, byte[]> { ["settings.yaml"] = ValidSettings(), ["plugins.json"] = "{\"plugins\":[]}"u8.ToArray() };
        byte[] manifest = ManifestFor(files);
        var entries = new List<(string, byte[])> { ("manifest.json", manifest), ("settings.yaml", files["settings.yaml"]), ("settings.yaml", ValidSettings()), ("plugins.json", files["plugins.json"]) };
        string code = CodeOf(() => rig.Service.Preview(WriteTemp(rig, ZipOf(entries)), null));
        Assert.Contains(code, new[] { "duplicate-entry", "corrupt" });
    }

    [Theory]
    [InlineData(17)]
    [InlineData(2000)]
    public void Entry_count_bombs_are_refused_before_entries_are_read(int count)
    {
        using var rig = new BackupRig();
        byte[] bytes = ZipOf(Enumerable.Range(0, count).Select(i => ($"f{i}.txt", new byte[1])));
        Assert.Equal("too-many-entries", CodeOf(() => rig.Service.Preview(WriteTemp(rig, bytes), null)));
    }

    [Fact]
    public void Size_and_ratio_bombs_are_refused_with_bounded_reading()
    {
        using var rig = new BackupRig();
        // One entry over 8 MiB (declared size), a 5 MiB entry that compresses far beyond the ratio bound, and a total over 32 MiB.
        byte[] huge = Craft(f => f["plugins.json"] = new byte[9 << 20]);
        Assert.Equal("too-large", CodeOf(() => rig.Service.Preview(WriteTemp(rig, huge), null)));
        byte[] ratio = Craft(f => f["plugins.json"] = new byte[5 << 20]);
        Assert.Equal("ratio", CodeOf(() => rig.Service.Preview(WriteTemp(rig, ratio), null)));
        byte[] big = new byte[(int)(BackupLimits.MaxFileBytes + 1)];
        Assert.Equal("too-large", CodeOf(() => rig.Service.Preview(WriteTemp(rig, big), null)));
        Assert.False(Directory.Exists(Path.Combine(rig.Paths.Imports, "staged")));
    }

    [Fact]
    public void Plain_secrets_are_never_accepted()
    {
        using var rig = new BackupRig();
        byte[] bytes = Craft(f => f["secrets.json"] = "{\"entries\":[{\"account\":\"a\",\"name\":\"apiKey\",\"value\":\"v\"}]}"u8.ToArray());
        Assert.Equal("secrets-unencrypted", CodeOf(() => rig.Service.Preview(WriteTemp(rig, bytes), null)));
    }

    [Fact]
    public void Settings_that_do_not_validate_are_refused_by_code_not_by_text()
    {
        using var rig = new BackupRig();
        byte[] bad = Craft(f => f["settings.yaml"] = ValidSettings(t => t.Replace("  theme: \"light\"\n", "  theme: \"light\"\n  colour: \"red\"\n")));
        Assert.Equal("settings-invalid", CodeOf(() => rig.Service.Preview(WriteTemp(rig, bad), null)));
    }

    // ---------- KDF bounds ----------

    private static byte[] WithIterations(byte[] file, int iterations)
    {
        int headerLength = BitConverter.ToInt32(file, 8);
        string json = Encoding.UTF8.GetString(file, 12, headerLength);
        json = System.Text.RegularExpressions.Regex.Replace(json, "\"iterations\":\\d+", $"\"iterations\":{iterations}");
        byte[] header = Encoding.UTF8.GetBytes(json);
        return [.. file[..8], .. BitConverter.GetBytes(header.Length), .. header, .. file[(12 + headerLength)..]];
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(2_000_001)]
    [InlineData(100_000_000)]
    [InlineData(1000)]
    [InlineData(0)]
    [InlineData(-5)]
    public void Kdf_parameters_outside_the_bounds_are_refused_without_running_the_kdf(int iterations)
    {
        using var rig = new BackupRig();
        rig.Seed();
        byte[] hostile = WithIterations(File.ReadAllBytes(rig.Export("k.susubak", WithKeys)), iterations);
        var timer = Stopwatch.StartNew();
        Assert.Equal("kdf-params", CodeOf(() => rig.Service.Preview(WriteTemp(rig, hostile), Password)));
        Assert.True(timer.ElapsedMilliseconds < 1500, "a hostile iteration count must be refused before key derivation");
    }

    [Fact]
    public void Kdf_at_the_upper_bound_is_accepted_to_the_authentication_check()
    {
        using var rig = new BackupRig();
        rig.Seed();
        byte[] edge = WithIterations(File.ReadAllBytes(rig.Export("k.susubak", WithKeys)), BackupLimits.MaxIterations);
        // Within bounds the KDF runs and the tag no longer matches: decrypt-failed, never kdf-params.
        Assert.Equal("decrypt-failed", CodeOf(() => rig.Service.Preview(WriteTemp(rig, edge), Password)));
    }

    // ---------- versions ----------

    [Fact]
    public void Newer_schema_or_format_is_refused_and_older_schema_is_migrated()
    {
        using var rig = new BackupRig();
        Assert.Equal("schema-newer", CodeOf(() => rig.Service.Preview(WriteTemp(rig, Craft(schema: 99)), null)));
        Assert.Equal("schema-newer", CodeOf(() => rig.Service.Preview(WriteTemp(rig, Craft(f => f["settings.yaml"] = ValidSettings(t => t.Replace("schemaVersion: 1", "schemaVersion: 2")))), null)));
        Assert.Equal("format-newer", CodeOf(() => rig.Service.Preview(WriteTemp(rig, Craft(formatVersion: 2)), null)));
        Assert.Equal("manifest-invalid", CodeOf(() => rig.Service.Preview(WriteTemp(rig, Craft(formatVersion: 0)), null)));

        byte[] older = Craft(f => f["settings.yaml"] = ValidSettings(t => t.Replace("schemaVersion: 1", "schemaVersion: 0").Replace("uiLanguage:", "language:")), schema: 0);
        var preview = rig.Service.Preview(WriteTemp(rig, older), null);
        Assert.True(preview.SchemaOlder);
        Assert.Equal(1, new StagedView(rig.Paths).Settings!.SchemaVersion);
    }

    private sealed class StagedView(AppPaths paths)
    {
        public AppSettings? Settings => SettingsYaml.Read(File.ReadAllText(Path.Combine(paths.Imports, "staged", "settings.yaml"))).Settings;
    }

    // ---------- preview ----------

    [Fact]
    public void Preview_stages_without_touching_the_live_config_and_reports_conflicts()
    {
        using var source = new BackupRig();
        source.Seed("Source", "sk-source-KEY");
        var instance = source.Store.State.Effective.Instances.First(i => i.Package == "app.susu.deepl");
        // A package that this install does not have, with a service the backup had enabled.
        var state = source.Store.State;
        var withGone = state.Effective with
        {
            Instances = [.. state.Effective.Instances, new InstanceSettings("gone", "app.example.gone", 1, new Dictionary<string, string>(), new Dictionary<string, string>())],
            Services = [.. state.Effective.Services, new ServiceSettings("gone", Susu.Contracts.Capability.Translate, true)], TranslationOrder = [.. state.Effective.TranslationOrder, "gone/translate"],
        };
        Assert.Equal(SaveStatus.Saved, source.Store.Save(withGone, state.Revision, state.FileHash).Status);
        source.UserPlugins.Add(new BackupPluginRef("app.example.gone", "2.0.0", "key1", "abc"));
        source.UserPlugins.Add(new BackupPluginRef("app.example.here", "1.0.0", "key1", "def"));
        string file = source.Export("k.susubak", WithKeys);
        Assert.NotNull(instance);

        using var target = new BackupRig();
        target.Seed("Local", "sk-local-KEY", account: "acct-local");
        target.Available["app.example.here"] = "1.5.0";
        string settings = target.SettingsHash, secrets = target.SecretsHash;
        var preview = target.Service.Preview(file, Password);

        Assert.Equal(settings, target.SettingsHash);
        Assert.Equal(secrets, target.SecretsHash);
        Assert.Equal("Previewed", target.Service.Status().State);
        Assert.True(preview.Encrypted);
        Assert.True(preview.IncludesSecrets);
        Assert.Contains("app.example.gone", preview.MissingPackages);
        Assert.Contains("gone", preview.DisabledInstances);
        Assert.Equal("missing", preview.Plugins.Single(p => p.Id == "app.example.gone").Status);
        Assert.Equal("older", preview.Plugins.Single(p => p.Id == "app.example.here").Status);
        Assert.Equal(1, preview.KeysRemoved); // the local key of acct-local is not in the backup
        Assert.Equal(1, preview.BackupSecrets);
        Assert.Equal(new BackupLocalData(3, 1), preview.Kept);
        Assert.Equal("acct-deepl", preview.Accounts.Single().Id);
        Assert.Empty(preview.Accounts.Single().MissingSecrets);
        Assert.Contains("plugins-missing", preview.Conflicts);
        Assert.Contains("accounts-need-authorization", preview.Conflicts);
        Assert.Contains("keys-removed", preview.Conflicts);
        Assert.True(preview.Deltas.Single(d => d.Area == "settings").Differs);
        var staged = new StagedView(target.Paths).Settings!;
        Assert.False(staged.Services.Single(s => s.Instance == "gone").Enabled);
        Assert.Empty(staged.Accounts.Single().Grants);
        Assert.DoesNotContain("sk-source-KEY", Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(target.Paths.Imports, "staged", "secrets.dat"))), StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_preview_supersedes_the_old_token_and_discard_drops_the_stage()
    {
        using var rig = new BackupRig();
        rig.Seed();
        string file = rig.Export("p.susubak", new BackupExportOptions(false, null));
        var first = rig.Service.Preview(file, null);
        var second = rig.Service.Preview(file, null);
        Assert.False(rig.Service.Apply(first.Token));
        Assert.False(rig.Service.Apply("0000"));
        Assert.True(rig.Service.Apply(second.Token));
        Assert.Equal("Ready", rig.Service.Status().State);
        rig.Service.Discard();
        Assert.Equal("None", rig.Service.Status().State);
        Assert.False(Directory.Exists(Path.Combine(rig.Paths.Imports, "staged")));
        Assert.Null(rig.Restart()); // nothing left to apply
    }

    // ---------- apply at start ----------

    private static (BackupRig Target, string File) Pair()
    {
        using var source = new BackupRig();
        source.Seed("Source", "sk-source-KEY");
        string file = Path.Combine(Path.GetTempPath(), "susu-tests", Guid.NewGuid().ToString("N") + ".susubak");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        Assert.True(source.Service.Export(file, WithKeys).Ok);
        var target = new BackupRig();
        target.Seed("Target", "sk-target-KEY", account: "acct-target");
        return (target, file);
    }

    [Fact]
    public void Import_replaces_settings_and_keys_only_at_the_next_start_and_keeps_a_restore_point()
    {
        var (target, file) = Pair();
        using var _ = target;
        string oldSettings = target.SettingsHash, oldSecrets = target.SecretsHash;
        var preview = target.Service.Preview(file, Password);
        Assert.True(target.Service.Apply(preview.Token));
        Assert.Equal(oldSettings, target.SettingsHash); // nothing changes until the restart
        Assert.Equal(oldSecrets, target.SecretsHash);

        var result = target.Restart();
        Assert.Equal("Applied", result!.State);
        var effective = target.Store.State.Effective;
        Assert.Equal("Source", effective.Accounts.Single().Label);
        Assert.Empty(effective.Accounts.Single().Grants);
        Assert.True(target.Store.State.Revision > 2);
        Assert.True(target.Secrets.TryRead("acct-deepl", "apiKey", out string key));
        Assert.Equal("sk-source-KEY", key);
        Assert.False(target.Secrets.Has("acct-target", "apiKey"));
        Assert.False(Directory.Exists(Path.Combine(target.Paths.Imports, "staged")));
        Assert.False(File.Exists(Path.Combine(target.Paths.Imports, "pending.json")));
        Assert.Empty(Directory.GetDirectories(target.Paths.Transactions));
        Assert.Equal("Applied", target.Service.Status().Result!.State);
        Assert.True(target.Service.Status().CanUndo);
        Assert.Equal(oldSettings, AtomicFile.Hash(File.ReadAllBytes(Path.Combine(target.Paths.Imports, "restore-point", "settings.yaml"))));
        Assert.Equal(oldSecrets, AtomicFile.Hash(File.ReadAllBytes(Path.Combine(target.Paths.Imports, "restore-point", "secrets.dat"))));
        target.Service.DismissResult();
        Assert.Null(target.Service.Status().Result);
    }

    [Fact]
    public void A_keyless_backup_brings_no_keys_and_every_account_must_be_authorized_again()
    {
        using var source = new BackupRig();
        source.Seed("Source", "sk-source-KEY");
        string file = source.Export("plain.susubak", new BackupExportOptions(false, null));
        using var target = new BackupRig();
        var preview = target.Service.Preview(file, null);
        Assert.False(preview.IncludesSecrets);
        Assert.Equal(["apiKey"], preview.Accounts.Single().MissingSecrets);
        Assert.Contains("keys-missing", preview.Conflicts);
        Assert.True(target.Service.Apply(preview.Token));
        target.Restart();
        Assert.Single(target.Store.State.Effective.Accounts);
        Assert.False(target.Secrets.Has("acct-deepl", "apiKey"));
        Assert.Empty(target.Store.State.Effective.Accounts[0].Grants);
    }

    [Fact]
    public void Backup_restored_into_another_profile_is_protected_for_the_new_user()
    {
        var userA = new XorProtector();
        var userB = new KeyedProtector(0x42);
        using var source = new BackupRig(userA);
        source.Seed("Source", "sk-source-KEY");
        string file = source.Export("k.susubak", WithKeys);
        using var target = new BackupRig(userB);
        var preview = target.Service.Preview(file, Password);
        byte[] stagedSecrets = File.ReadAllBytes(Path.Combine(target.Paths.Imports, "staged", "secrets.dat"));
        Assert.DoesNotContain("sk-source-KEY", Encoding.Latin1.GetString(stagedSecrets), StringComparison.Ordinal);
        Assert.True(target.Service.Apply(preview.Token));
        target.Restart();
        Assert.True(target.Secrets.TryRead("acct-deepl", "apiKey", out string key));
        Assert.Equal("sk-source-KEY", key);
        var asUserA = new SecretStore(target.Paths.Secrets, userA);
        Assert.ThrowsAny<Exception>(() => asUserA.TryRead("acct-deepl", "apiKey", out _)); // user A's DPAPI cannot open it
        // The source machine's own blob does not open for user B either: nothing was copied across.
        Assert.ThrowsAny<Exception>(() => new SecretStore(source.Paths.Secrets, userB).TryRead("acct-deepl", "apiKey", out _));
    }

    [Fact]
    public void Missing_plugins_are_disabled_not_installed_and_the_settings_still_load()
    {
        using var source = new BackupRig();
        source.Seed();
        var state = source.Store.State;
        Assert.Equal(SaveStatus.Saved, source.Store.Save(state.Effective with
        {
            Instances = [.. state.Effective.Instances, new InstanceSettings("gone", "app.example.gone", 1, new Dictionary<string, string>(), new Dictionary<string, string>())],
            Services = [.. state.Effective.Services, new ServiceSettings("gone", Susu.Contracts.Capability.Translate, true)], TranslationOrder = [.. state.Effective.TranslationOrder, "gone/translate"],
        }, state.Revision, state.FileHash).Status);
        source.UserPlugins.Add(new BackupPluginRef("app.example.gone", "2.0.0", "key1", "abc"));
        string file = source.Export("p.susubak", new BackupExportOptions(false, null));

        using var target = new BackupRig();
        var before = Directory.GetFileSystemEntries(target.Paths.UserPlugins);
        Assert.True(target.Service.Apply(target.Service.Preview(file, null).Token));
        target.Restart();
        var gone = target.Store.State.Effective.Services.Single(s => s.Instance == "gone");
        Assert.False(gone.Enabled);
        Assert.False(target.Store.State.FileInvalid);
        Assert.Equal(before, Directory.GetFileSystemEntries(target.Paths.UserPlugins)); // no code was installed
        Assert.Equal(1, target.Service.Status().Result!.DisabledInstances);
    }

    [Fact]
    public void Undo_brings_the_old_config_back_at_the_next_start()
    {
        var (target, file) = Pair();
        using var _ = target;
        string oldSettings = target.SettingsHash, oldSecrets = target.SecretsHash;
        Assert.False(target.Service.ScheduleUndo()); // no restore point yet
        Assert.True(target.Service.Apply(target.Service.Preview(file, Password).Token));
        target.Restart();
        Assert.NotEqual(oldSettings, target.SettingsHash);
        Assert.True(target.Service.ScheduleUndo());
        Assert.Equal("Ready", target.Service.Status().State);
        Assert.Equal("undo", target.Service.Status().Source);
        Assert.Equal("Applied", target.Restart()!.State);
        Assert.Equal("Target", target.Store.State.Effective.Accounts.Single().Label);
        Assert.Equal(oldSecrets, target.SecretsHash);
        Assert.Single(target.Store.State.Effective.Accounts.Single().Grants); // the old authorization comes back with the old config
        Assert.True(target.Secrets.TryRead("acct-target", "apiKey", out string key));
        Assert.Equal("sk-target-KEY", key);
    }

    [Fact]
    public void A_previewed_but_unconfirmed_stage_is_dropped_at_start()
    {
        var (target, file) = Pair();
        using var _ = target;
        string oldSettings = target.SettingsHash;
        target.Service.Preview(file, Password);
        Assert.Null(target.Restart());
        Assert.Equal(oldSettings, target.SettingsHash);
        Assert.False(Directory.Exists(Path.Combine(target.Paths.Imports, "staged")));
    }

    [Fact]
    public void A_staged_file_changed_after_confirmation_is_not_applied()
    {
        var (target, file) = Pair();
        using var _ = target;
        string oldSettings = target.SettingsHash, oldSecrets = target.SecretsHash;
        Assert.True(target.Service.Apply(target.Service.Preview(file, Password).Token));
        File.AppendAllText(Path.Combine(target.Paths.Imports, "staged", "settings.yaml"), "# edited\n");
        var result = target.Restart();
        Assert.Equal("Failed", result!.State);
        Assert.Equal("staged-tampered", result.Error);
        Assert.Equal(oldSettings, target.SettingsHash);
        Assert.Equal(oldSecrets, target.SecretsHash);
    }

    [Fact]
    public void A_full_disk_at_the_switch_leaves_the_old_config_and_drops_the_import()
    {
        var (target, file) = Pair();
        using var _ = target;
        string oldSettings = target.SettingsHash, oldSecrets = target.SecretsHash;
        Assert.True(target.Service.Apply(target.Service.Preview(file, Password).Token));
        var result = target.Restart(new FaultAt("replace:1:flushed", s => new IOException("disk full")));
        Assert.Equal("Failed", result!.State);
        Assert.Equal("io", result.Error);
        Assert.Equal(oldSettings, target.SettingsHash); // the first replace was rolled back with the pair
        Assert.Equal(oldSecrets, target.SecretsHash);
        Assert.Empty(Directory.GetDirectories(target.Paths.Transactions));
        Assert.False(File.Exists(Path.Combine(target.Paths.Imports, "pending.json")));
        Assert.Equal("Target", target.Store.State.Effective.Accounts.Single().Label);
    }

    [Theory] // DATA09 power loss: every kill point of the import and of the two-file commit
    [InlineData("import:begin")]
    [InlineData("import:restore-point-written")]
    [InlineData("import:before-commit")]
    [InlineData("stage:0")]
    [InlineData("stage:1")]
    [InlineData("prepared")]
    [InlineData("replace:0:flushed")]
    [InlineData("replace:0:replaced")]
    [InlineData("replace:1:flushed")]
    [InlineData("replace:1:replaced")]
    [InlineData("committed")]
    [InlineData("import:committed")]
    [InlineData("import:done")]
    public void Power_loss_at_any_kill_point_never_leaves_a_half_config_and_the_next_start_finishes(string stage)
    {
        var (target, file) = Pair();
        using var _ = target;
        string oldSettings = target.SettingsHash, oldSecrets = target.SecretsHash;
        Assert.True(target.Service.Apply(target.Service.Preview(file, Password).Token));

        target.Store.Dispose();
        Assert.Throws<SimulatedCrash>(() => BackupImport.ApplyPending(target.Paths, target.Clock, new FaultAt(stage)));
        // Restart step 1: the journal rolls an unfinished switch back.
        new ConfigTransaction(target.Paths.Transactions).Recover();
        bool oldPair = target.SettingsHash == oldSettings && target.SecretsHash == oldSecrets;
        string? newSettings = oldPair ? null : target.SettingsHash;
        bool newPair = !oldPair && target.SecretsHash != oldSecrets && target.SettingsHash != oldSettings;
        Assert.True(oldPair || newPair, $"after a crash at {stage} the pair must be entirely old or entirely new");

        // Restart step 2: the import is finished (or recognised as finished) and the restore point still holds the OLD pair.
        var result = BackupImport.ApplyPending(target.Paths, target.Clock);
        target.Open();
        if (stage != "import:done") Assert.Equal("Applied", result!.State);
        Assert.Equal("Source", target.Store.State.Effective.Accounts.Single().Label);
        Assert.True(target.Secrets.TryRead("acct-deepl", "apiKey", out string key));
        Assert.Equal("sk-source-KEY", key);
        Assert.Equal(oldSettings, AtomicFile.Hash(File.ReadAllBytes(Path.Combine(target.Paths.Imports, "restore-point", "settings.yaml"))));
        Assert.Equal(oldSecrets, AtomicFile.Hash(File.ReadAllBytes(Path.Combine(target.Paths.Imports, "restore-point", "secrets.dat"))));
        Assert.False(File.Exists(Path.Combine(target.Paths.Imports, "pending.json")));
        Assert.Empty(Directory.GetDirectories(target.Paths.Transactions));
        if (newSettings is not null) Assert.Equal(newSettings, target.SettingsHash); // a commit that already happened is not repeated with a new revision
    }

    [Fact]
    public void An_import_that_keeps_crashing_is_abandoned_after_three_tries_and_the_old_config_stays()
    {
        var (target, file) = Pair();
        using var _ = target;
        string oldSettings = target.SettingsHash, oldSecrets = target.SecretsHash;
        Assert.True(target.Service.Apply(target.Service.Preview(file, Password).Token));
        target.Store.Dispose();
        for (int i = 0; i < 3; i++)
        {
            Assert.Throws<SimulatedCrash>(() => BackupImport.ApplyPending(target.Paths, target.Clock, new FaultAt("import:begin")));
            new ConfigTransaction(target.Paths.Transactions).Recover();
        }
        var result = BackupImport.ApplyPending(target.Paths, target.Clock);
        Assert.Equal("Failed", result!.State);
        Assert.Equal("too-many-attempts", result.Error);
        Assert.Equal(oldSettings, target.SettingsHash);
        Assert.Equal(oldSecrets, target.SecretsHash);
        Assert.Null(BackupImport.ApplyPending(target.Paths, target.Clock));
    }
}
