using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Net;
using Susu.Storage;
using Susu.Testing;
using Susu.Windows;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>Isolated data root per test; never the real profile (F02.4).</summary>
public sealed class TempRoot : IDisposable
{
    public TempRoot() { Root = Path.Combine(Path.GetTempPath(), "susu-tests", Guid.NewGuid().ToString("N")); Paths = AppPaths.UnderRoot(Root).EnsureCreated(); }
    public string Root { get; }
    public AppPaths Paths { get; }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Reversible test protector: output never contains the plaintext bytes.</summary>
public sealed class XorProtector : ISecretProtector
{
    public byte[] Protect(ReadOnlySpan<byte> plaintext) => [0x53, .. plaintext.ToArray().Select(b => (byte)(b ^ 0xA5))];
    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => ciphertext[0] == 0x53 ? [.. ciphertext[1..].ToArray().Select(b => (byte)(b ^ 0xA5))] : throw new InvalidDataException("tampered");
}

public sealed class SimulatedCrash(string stage) : Exception($"crash at {stage}");

public sealed class FaultAt(string stage, Func<string, Exception>? make = null) : IFaultPoint
{
    public void Hit(string at) { if (at == stage) throw (make ?? (s => new SimulatedCrash(s)))(at); }
}

public class SettingsYamlTests
{
    private static string Valid(Func<string, string>? edit = null)
    {
        string text = SettingsYaml.Write(BuiltInCatalog.Defaults() with { Revision = 3 });
        return edit is null ? text : edit(text);
    }

    [Fact]
    public void Defaults_round_trip_with_fixed_order_and_comments()
    {
        var defaults = BuiltInCatalog.Defaults() with { Revision = 7 };
        string text = SettingsYaml.Write(defaults);
        var (back, issues) = SettingsYaml.Read(text);
        Assert.Empty(issues);
        Assert.True(back!.ContentEquals(defaults));
        Assert.Equal(7, back.Revision);
        Assert.Equal(text, SettingsYaml.Write(back)); // deterministic
        Assert.StartsWith("# 苏苏设置文件", text, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", text, StringComparison.Ordinal);
        Assert.Equal(21 + 2, back.Instances.Count);
        Assert.Equal("mymemory/translate", back.TranslationOrder[0]);
        Assert.True(back.Services.Single(s => s.ServiceId == "mymemory/translate").Enabled);
        Assert.False(back.Services.Single(s => s.ServiceId == "deepl/translate").Enabled);
    }

    [Theory] // DATA01: exact paths for unknown fields, duplicate keys and forbidden YAML features
    [InlineData("  theme: \"light\"\n", "  theme: \"light\"\n  colour: \"red\"\n", "general.colour", "unknown-field")]
    [InlineData("  theme: \"light\"\n", "  theme: \"light\"\n  theme: \"light\"\n", "general.theme", "duplicate-key")]
    [InlineData("general:\n", "general: &g\n", "general", "forbidden-feature")]
    [InlineData("  theme: \"light\"\n", "  theme: !!str light\n", "general.theme", "forbidden-feature")]
    [InlineData("  defaultExpandedCards: 2\n", "  defaultExpandedCards: \"2\"\n", "general.defaultExpandedCards", "type")]
    [InlineData("  defaultExpandedCards: 2\n", "  defaultExpandedCards: 99\n", "general.defaultExpandedCards", "range")]
    [InlineData("  targetLanguage: \"zh-Hans\"\n", "  targetLanguage: \"ja\"\n", "general.targetLanguage", "range")]
    [InlineData("  ocrTranslate: \"Alt+S\"\n", "  ocrTranslate: \"Alt+A\"\n", "hotkeys.inputTranslate", "conflict")]
    [InlineData("  ocrTranslate: \"Alt+S\"\n", "  ocrTranslate: \"Alt+\"\n", "hotkeys.ocrTranslate", "format")]
    public void Invalid_files_report_exact_paths(string find, string replace, string path, string code)
    {
        var (settings, issues) = SettingsYaml.Read(Valid(t => ReplaceFirst(t, find, replace)));
        Assert.Null(settings);
        Assert.Contains(issues, i => i.Path == path && i.Code == code);
        Assert.All(issues.Where(i => i.Code != "conflict" && i.Code != "reference"), i => Assert.True(i.Line > 0, i.ToString()));
    }

    [Fact]
    public void Aliases_merge_keys_multiple_documents_and_syntax_errors_are_rejected()
    {
        Assert.Contains(SettingsYaml.Read("schemaVersion: 1\na: &x 1\nb: *x\n").Issues, i => i.Code == "forbidden-feature");
        Assert.Contains(SettingsYaml.Read("schemaVersion: 1\n<<: {a: 1}\n").Issues, i => i.Code == "forbidden-feature");
        Assert.Contains(SettingsYaml.Read(Valid() + "---\nschemaVersion: 1\n").Issues, i => i.Code == "forbidden-feature");
        var syntax = SettingsYaml.Read("schemaVersion: 1\ngeneral: [\n").Issues;
        Assert.Contains(syntax, i => i.Code == "syntax" && i.Line > 0);
        Assert.Contains(SettingsYaml.Read("revision: 1\n").Issues, i => i.Path == "schemaVersion" && i.Code == "missing");
    }

    [Fact]
    public void Newer_schema_is_refused_and_older_schema_is_migrated()
    {
        Assert.Contains(SettingsYaml.Read("schemaVersion: 9\n").Issues, i => i.Code == "newer-schema");
        var (migrated, issues) = SettingsYaml.Read("schemaVersion: 0\nrevision: 4\ngeneral:\n  language: \"en\"\n");
        Assert.Empty(issues);
        Assert.Equal("en", migrated!.General.UiLanguage);
        Assert.Equal(AppSettings.CurrentSchemaVersion, migrated.SchemaVersion);
    }

    [Fact]
    public void Cross_references_are_checked()
    {
        var bad = BuiltInCatalog.Defaults() with { TranslationOrder = ["nope/translate"] };
        var issues = SettingsYaml.Read(SettingsYaml.Write(bad)).Issues;
        Assert.Contains(issues, i => i.Path == "translationOrder[0]" && i.Code == "reference");
        var unboundAccount = BuiltInCatalog.Defaults();
        unboundAccount = unboundAccount with { Instances = [.. unboundAccount.Instances.Select(i => i.Id == "deepl" ? i with { AccountBindings = new Dictionary<string, string> { ["apiKey"] = "ghost" } } : i)] };
        Assert.Contains(SettingsYaml.Read(SettingsYaml.Write(unboundAccount)).Issues, i => i.Path == "instances[4].accounts.apiKey");
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("quote \" backslash \\ colon: # hash")]
    [InlineData("CRLF\r\nTab\tNUL\u0000 LS\u2028 emoji 😀 中文")]
    [InlineData("{{secret.apiKey}} {\"secret\": \"apiKey\"}")]
    public void Any_string_round_trips_through_quoting(string value)
    {
        var settings = BuiltInCatalog.Defaults() with { Prompts = [new PromptProfile("p1", value, value)] };
        var (back, issues) = SettingsYaml.Read(SettingsYaml.Write(settings));
        Assert.Empty(issues);
        Assert.Equal(value, back!.Prompts[0].Template);
    }

    private static string ReplaceFirst(string text, string find, string replace)
    {
        int index = text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(index >= 0, $"fixture text not found: {find}");
        return string.Concat(text.AsSpan(0, index), replace, text.AsSpan(index + find.Length));
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void First_start_writes_defaults_and_save_bumps_revision_atomically()
    {
        using var root = new TempRoot();
        using var store = new SettingsStore(root.Paths, new ManualClock());
        Assert.Equal(1, store.State.Revision);
        var changed = store.State.Effective with { General = store.State.Effective.General with { UiLanguage = "en" } };
        var result = store.Save(changed, store.State.Revision, store.State.FileHash);
        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(2, store.State.Revision);
        Assert.Equal(AtomicFile.HashOf(root.Paths.Settings), store.State.FileHash);
        Assert.StartsWith("# Su-Su settings", File.ReadAllText(root.Paths.Settings), StringComparison.Ordinal);
        Assert.True(File.Exists(root.Paths.Settings + ".prev"));
        Assert.Empty(Directory.GetFiles(root.Paths.Roaming, "*.tmp"));
    }

    [Fact] // DATA01: external edit and UI save at the same time
    public void Stale_revision_or_changed_file_is_a_conflict_not_an_overwrite()
    {
        using var root = new TempRoot();
        using var store = new SettingsStore(root.Paths, new ManualClock());
        var view = store.State;
        string edited = File.ReadAllText(root.Paths.Settings).Replace("defaultExpandedCards: 2", "defaultExpandedCards: 3");
        File.WriteAllText(root.Paths.Settings, edited);
        var result = store.Save(view.Effective with { General = view.Effective.General with { Theme = "light", DefaultExpandedCards = 5 } }, view.Revision, view.FileHash);
        Assert.Equal(SaveStatus.Conflict, result.Status);
        Assert.Equal(edited, File.ReadAllText(root.Paths.Settings)); // hand edit kept
        Assert.True(store.Reload());
        Assert.Equal(3, store.State.Effective.General.DefaultExpandedCards);
        Assert.Equal(SaveStatus.Conflict, store.Save(store.State.Effective, view.Revision, store.State.FileHash).Status);
        Assert.Equal(SaveStatus.Saved, store.Save(store.State.Effective, store.State.Revision, store.State.FileHash).Status);
    }

    [Fact] // DATA01: an invalid file keeps the last valid state and is never replaced by defaults
    public void Invalid_hand_edit_keeps_last_valid_settings_and_the_file()
    {
        using var root = new TempRoot();
        var clock = new ManualClock();
        using var store = new SettingsStore(root.Paths, clock);
        store.Save(store.State.Effective with { General = store.State.Effective.General with { UiLanguage = "en" } }, store.State.Revision, store.State.FileHash);
        string broken = File.ReadAllText(root.Paths.Settings).Replace("theme: \"light\"", "theme: \"light\"\n  theme: \"dark\"");
        File.WriteAllText(root.Paths.Settings, broken);
        Assert.True(store.Reload());
        Assert.True(store.State.FileInvalid);
        Assert.Contains(store.State.Issues, i => i.Path == "general.theme" && i.Code == "duplicate-key");
        Assert.Equal("en", store.State.Effective.General.UiLanguage); // last valid, not defaults
        Assert.Equal(broken, File.ReadAllText(root.Paths.Settings));

        // Restart with the broken file: still not overwritten; falls back to the valid .prev.
        using var restarted = new SettingsStore(root.Paths, clock);
        Assert.True(restarted.State.FileInvalid);
        Assert.Equal(broken, File.ReadAllText(root.Paths.Settings));

        // A later UI save keeps a copy of the broken input.
        var saved = restarted.Save(restarted.State.Effective, restarted.State.Revision, restarted.State.FileHash);
        Assert.Equal(SaveStatus.Saved, saved.Status);
        var aside = Assert.Single(Directory.GetFiles(root.Paths.Roaming, "settings.invalid-*.yaml"));
        Assert.Equal(broken, File.ReadAllText(aside));
    }

    [Fact] // file events: 300 ms debounce, a burst causes one reload
    public async Task File_events_are_debounced()
    {
        using var root = new TempRoot();
        var clock = new ManualClock();
        using var store = new SettingsStore(root.Paths, clock);
        int changes = 0;
        store.Changed += _ => Interlocked.Increment(ref changes);
        File.WriteAllText(root.Paths.Settings, File.ReadAllText(root.Paths.Settings).Replace("launchAtStartup: false", "launchAtStartup: true"));
        for (int i = 0; i < 5; i++) { store.NotifyFileChanged(); clock.Advance(TimeSpan.FromMilliseconds(100)); }
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(0, changes);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        await Eventually.WaitAsync(() => Volatile.Read(ref changes) != 0);
        Assert.Equal(1, changes);
        Assert.True(store.State.Effective.General.LaunchAtStartup);
        Assert.Equal(2, store.State.Revision); // content changed without a revision bump: effective revision still advances
    }

    [Fact]
    public void Invalid_proposal_is_rejected_before_writing()
    {
        using var root = new TempRoot();
        using var store = new SettingsStore(root.Paths, new ManualClock());
        string before = File.ReadAllText(root.Paths.Settings);
        var hotkeys = new Dictionary<string, string>(store.State.Effective.Hotkeys.Chords) { ["ocrTranslate"] = "Alt+A" };
        var result = store.Save(store.State.Effective with { Hotkeys = new HotkeySettings(hotkeys) }, store.State.Revision, store.State.FileHash);
        Assert.Equal(SaveStatus.Invalid, result.Status);
        Assert.Contains(result.Issues, i => i.Code == "conflict");
        Assert.Equal(before, File.ReadAllText(root.Paths.Settings));
    }
}

public class ConfigTransactionTests
{
    private static (TempRoot Root, SettingsStore Store, SecretStore Secrets) Setup(IFaultPoint? faults = null)
    {
        var root = new TempRoot();
        var protector = new XorProtector();
        new SecretStore(root.Paths.Secrets, protector).Write("tencent", "secretKey", "OLD-SECRET-VALUE");
        var store = new SettingsStore(root.Paths, new ManualClock(), faults);
        return (root, store, new SecretStore(root.Paths.Secrets, protector, faults));
    }

    private static AppSettings WithAccount(AppSettings s) => s with
    {
        Accounts = [new AccountSettings("tencent", "Tencent Cloud", ["secretKey"], [new CredentialGrant("app.susu.tencent-translate", "builtin", "secretKey", "https://tmt.tencentcloudapi.com:443", "signer:tencent-tc3")])],
    };

    public static TheoryData<string, bool> Stages => new()
    {
        { "stage:0", false }, { "stage:1", false }, { "prepared", false },
        { "replace:0:flushed", false }, { "replace:0:replaced", false }, { "replace:1:flushed", false }, { "replace:1:replaced", false },
        { "committed", true },
    };

    [Theory] // DATA02: crash at every stage leaves both files old or both new after recovery
    [MemberData(nameof(Stages))]
    public void Crash_at_any_stage_recovers_to_a_matching_pair(string stage, bool expectNew)
    {
        var (root, store, secrets) = Setup(new FaultAt(stage));
        using (root)
        using (store)
        {
            string oldSettings = File.ReadAllText(root.Paths.Settings), oldSecrets = File.ReadAllText(root.Paths.Secrets);
            byte[] newSecrets = secrets.Prepare([("tencent", "secretKey", "NEW-SECRET-VALUE")]);
            Assert.Throws<SimulatedCrash>(() => store.SaveWithSecrets(WithAccount(store.State.Effective), store.State.Revision, store.State.FileHash, secrets, newSecrets));

            int rolledBack = new ConfigTransaction(root.Paths).Recover(); // next start
            Assert.Empty(Directory.GetDirectories(root.Paths.Transactions));
            var restarted = new SecretStore(root.Paths.Secrets, new XorProtector());
            Assert.True(restarted.TryRead("tencent", "secretKey", out var value));
            using var reloaded = new SettingsStore(root.Paths, new ManualClock());
            bool settingsNew = reloaded.State.Effective.Accounts.Count == 1;
            Assert.Equal(expectNew, settingsNew);
            Assert.Equal(expectNew ? "NEW-SECRET-VALUE" : "OLD-SECRET-VALUE", value);
            if (!expectNew)
            {
                Assert.Equal(oldSettings, File.ReadAllText(root.Paths.Settings));
                Assert.Equal(oldSecrets, File.ReadAllText(root.Paths.Secrets));
                Assert.Equal(stage is "stage:0" or "stage:1" ? 0 : 1, rolledBack);
            }
        }
    }

    [Fact] // DATA02: disk full during the second replace is rolled back in-process
    public void Disk_full_mid_commit_rolls_back_both_files()
    {
        var (root, store, secrets) = Setup(new FaultAt("replace:1:flushed", s => new IOException("There is not enough space on the disk.", unchecked((int)0x80070070))));
        using (root)
        using (store)
        {
            string oldSettings = File.ReadAllText(root.Paths.Settings), oldSecrets = File.ReadAllText(root.Paths.Secrets);
            var result = store.SaveWithSecrets(WithAccount(store.State.Effective), store.State.Revision, store.State.FileHash, secrets, secrets.Prepare([("tencent", "secretKey", "NEW-SECRET-VALUE")]));
            Assert.Equal(SaveStatus.Failed, result.Status);
            Assert.Equal(oldSettings, File.ReadAllText(root.Paths.Settings));
            Assert.Equal(oldSecrets, File.ReadAllText(root.Paths.Secrets));
            Assert.Empty(store.State.Effective.Accounts);
            Assert.True(secrets.TryRead("tencent", "secretKey", out var value) && value == "OLD-SECRET-VALUE");
            Assert.Empty(Directory.GetDirectories(root.Paths.Transactions));
        }
    }

    [Fact] // backups in transactions/ stay ciphertext
    public void Journal_backups_never_contain_plaintext_secrets()
    {
        var (root, store, secrets) = Setup(new FaultAt("prepared"));
        using (root)
        using (store)
        {
            Assert.Throws<SimulatedCrash>(() => store.SaveWithSecrets(WithAccount(store.State.Effective), store.State.Revision, store.State.FileHash, secrets, secrets.Prepare([("tencent", "secretKey", "NEW-SECRET-VALUE")])));
            foreach (var file in Directory.GetFiles(root.Paths.Transactions, "*", SearchOption.AllDirectories).Append(root.Paths.Secrets).Append(root.Paths.Settings))
            {
                string text = File.ReadAllText(file);
                Assert.DoesNotContain("SECRET-VALUE", text, StringComparison.Ordinal);
            }
        }
    }
}

public class SecretStoreTests
{
    [Fact] // write-only from the UI's point of view: presence and names only, replace and delete
    public void Write_replace_delete_and_presence()
    {
        using var root = new TempRoot();
        var store = new SecretStore(root.Paths.Secrets, new XorProtector());
        store.Write("deepl", "apiKey", "k-111");
        store.Write("deepl", "apiKey", "k-222");
        store.Write(NetworkSettings.ProxyAccountId, "password", "proxy-pass"); // proxy password: same path
        Assert.True(store.Has("deepl", "apiKey"));
        Assert.Equal(["apiKey"], store.Names("deepl"));
        Assert.True(new SecretStore(root.Paths.Secrets, new XorProtector()).TryRead("deepl", "apiKey", out var value));
        Assert.Equal("k-222", value);
        Assert.DoesNotContain("k-222", File.ReadAllText(root.Paths.Secrets), StringComparison.Ordinal);
        Assert.DoesNotContain("proxy-pass", File.ReadAllText(root.Paths.Secrets), StringComparison.Ordinal);
        Assert.True(store.Delete("deepl", "apiKey"));
        Assert.False(store.Has("deepl", "apiKey"));
        Assert.False(store.Delete("deepl", "apiKey"));
        Assert.Throws<ArgumentException>(() => store.Write("deepl", "apiKey", ""));
    }

    [Fact] // real DPAPI (CurrentUser) on Windows
    public void Dpapi_protector_round_trips_and_detects_tampering()
    {
        using var root = new TempRoot();
        var dpapi = new DpapiProtector();
        var store = new SecretStore(root.Paths.Secrets, dpapi);
        store.Write("openai", "apiKey", "sk-test-中文-value");
        Assert.True(new SecretStore(root.Paths.Secrets, dpapi).TryRead("openai", "apiKey", out var value));
        Assert.Equal("sk-test-中文-value", value);
        Assert.DoesNotContain("sk-test", File.ReadAllText(root.Paths.Secrets), StringComparison.Ordinal);
        byte[] blob = dpapi.Protect("x"u8);
        blob[^1] ^= 0xFF;
        Assert.ThrowsAny<Exception>(() => dpapi.Unprotect(blob));
    }
}

public class CredentialAuthorizationTests
{
    private static readonly PluginIdentity Translate = new("app.susu.tencent-translate", "builtin");
    private const string TmtOrigin = "https://tmt.tencentcloudapi.com:443";

    private static (IReadOnlyList<AccountSettings> Accounts, InstanceSettings Instance) Bound()
    {
        var account = new AccountSettings("tencent", "Tencent", ["secretId", "secretKey"],
            [new CredentialGrant(Translate.PackageId, Translate.Signer, "secretKey", TmtOrigin, "signer:tencent-tc3")]);
        var instance = new InstanceSettings("tencent-translate", Translate.PackageId, 1, new Dictionary<string, string>(), new Dictionary<string, string> { ["secretKey"] = "tencent" });
        return ([account], instance);
    }

    [Fact]
    public void Exact_binding_identity_origin_and_use_is_allowed()
    {
        var (accounts, instance) = Bound();
        Assert.Equal((CredentialDecision.Allowed, "tencent"), CredentialAuthorizer.Authorize(accounts, instance, Translate, "secretKey", "https://TMT.tencentcloudapi.com", "signer:tencent-tc3"));
    }

    [Theory] // S02
    [InlineData("com.evil.tencent", "builtin", "secretKey", "https://tmt.tencentcloudapi.com", "signer:tencent-tc3", CredentialDecision.IdentityChanged)] // third party, same secret name
    [InlineData("app.susu.tencent-translate", "unsigned:abc", "secretKey", "https://tmt.tencentcloudapi.com", "signer:tencent-tc3", CredentialDecision.IdentityChanged)] // replaced package identity
    [InlineData("app.susu.tencent-translate", "builtin", "secretKey", "https://evil.example.com", "signer:tencent-tc3", CredentialDecision.OriginNotGranted)]
    [InlineData("app.susu.tencent-translate", "builtin", "secretKey", "http://tmt.tencentcloudapi.com", "signer:tencent-tc3", CredentialDecision.OriginNotGranted)] // downgrade
    [InlineData("app.susu.tencent-translate", "builtin", "secretKey", "https://tmt.tencentcloudapi.com", "header:Authorization", CredentialDecision.UseNotGranted)] // changed purpose
    [InlineData("app.susu.tencent-translate", "builtin", "secretId", "https://tmt.tencentcloudapi.com", "signer:tencent-tc3", CredentialDecision.NotBound)]
    [InlineData("app.susu.tencent-translate", "builtin", "secretKey", "https://user@tmt.tencentcloudapi.com", "signer:tencent-tc3", CredentialDecision.InvalidOrigin)]
    public void Changed_identity_origin_or_use_does_not_inherit_the_account(string package, string signer, string secret, string origin, string use, CredentialDecision expected)
    {
        var (accounts, instance) = Bound();
        if (package != instance.Package) instance = instance with { Package = package }; // third-party instance declaring the same secret name
        var (decision, account) = CredentialAuthorizer.Authorize(accounts, instance, new PluginIdentity(package, signer), secret, origin, use);
        Assert.Equal(expected, decision);
        Assert.Null(account);
    }

    [Fact]
    public void A_third_party_instance_without_an_explicit_binding_gets_nothing()
    {
        var (accounts, _) = Bound();
        var thirdParty = new InstanceSettings("x", "com.other.tencent", 1, new Dictionary<string, string>(), new Dictionary<string, string>());
        Assert.Equal(CredentialDecision.NotBound, CredentialAuthorizer.Authorize(accounts, thirdParty, new PluginIdentity("com.other.tencent", "builtin"), "secretKey", TmtOrigin, "signer:tencent-tc3").Decision);
    }

    [Fact] // declared changes become pending confirmations, never automatic grants
    public void New_origins_need_confirmation_and_confirmed_grants_apply()
    {
        var (accounts, instance) = Bound();
        var ocr = new CredentialGrant("app.susu.tencent-ocr", "builtin", "secretKey", "https://ocr.tencentcloudapi.com:443", "signer:tencent-tc3");
        var pending = CredentialAuthorizer.PendingConfirmation(accounts[0], [accounts[0].Grants[0], ocr]);
        Assert.Equal([ocr], pending);
        var confirmed = CredentialAuthorizer.Confirm(accounts[0], pending);
        var ocrInstance = instance with { Id = "tencent-ocr", Package = "app.susu.tencent-ocr" };
        Assert.Equal(CredentialDecision.Allowed, CredentialAuthorizer.Authorize([confirmed], ocrInstance, new PluginIdentity("app.susu.tencent-ocr", "builtin"), "secretKey", "https://ocr.tencentcloudapi.com", "signer:tencent-tc3").Decision);
        Assert.Throws<ArgumentException>(() => CredentialAuthorizer.Confirm(accounts[0], [ocr with { Origin = "https://ocr.tencentcloudapi.com" }])); // must be normalized
    }

    [Theory]
    [InlineData("https://Example.COM", "https://example.com:443")]
    [InlineData("https://example.com:8443/", "https://example.com:8443")]
    [InlineData("http://127.0.0.1:11434", "http://127.0.0.1:11434")]
    [InlineData("https://[::1]:8765", "https://[::1]:8765")]
    [InlineData("https://bücher.example", "https://xn--bcher-kva.example:443")]
    public void Origins_normalize(string input, string expected) => Assert.Equal(expected, Origin.Normalize(input));

    [Theory]
    [InlineData("example.com")]
    [InlineData("https://*.example.com")]
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com?q=1")]
    [InlineData("https://u:p@example.com")]
    [InlineData("ftp://example.com")]
    public void Non_origins_are_rejected(string input) => Assert.False(Origin.TryNormalize(input, out _));
}

public class CredentialInjectionTests
{
    private static string Secret(CredentialSpec spec, string name) => name == "apiKey" ? "K\"ey&=/+ 中" : throw new InvalidOperationException("unauthorized");

    [Fact] // S01: literal text is never interpolated; only the control field carries secrets
    public void Secret_syntax_in_ordinary_data_stays_literal()
    {
        var body = JsonNode.Parse("""{"text":"{{secret.apiKey}}","nested":{"secret":"apiKey"},"auth":null}""");
        var request = new RequestDescriptor("POST", "https://api.example.com/v1?q=%7B%7Bsecret.apiKey%7D%7D&key=", [new("Authorization", ""), new("X-Note", "{{secret.apiKey}}")], body);
        var filled = CredentialInjector.Apply(request,
            [new CredentialSpec(CredentialArea.Header, "Authorization", [CredentialPart.Text("DeepL-Auth-Key "), CredentialPart.Ref("apiKey")]),
             new CredentialSpec(CredentialArea.Query, "key", [CredentialPart.Ref("apiKey")]),
             new CredentialSpec(CredentialArea.Json, "/auth", [CredentialPart.Ref("apiKey")])], Secret);
        Assert.Equal("DeepL-Auth-Key K\"ey&=/+ 中", filled.Headers.Single(h => h.Key == "Authorization").Value);
        Assert.Equal("{{secret.apiKey}}", filled.Headers.Single(h => h.Key == "X-Note").Value);
        Assert.Contains("q=%7B%7Bsecret.apiKey%7D%7D", filled.Url, StringComparison.Ordinal);
        Assert.Contains("key=K%22ey%26%3D%2F%2B%20%E4%B8%AD", filled.Url, StringComparison.Ordinal); // one encoder
        Assert.Equal("{{secret.apiKey}}", filled.Body!["text"]!.GetValue<string>());
        Assert.Equal("apiKey", filled.Body["nested"]!["secret"]!.GetValue<string>());
        Assert.Equal("{\"text\":\"{{secret.apiKey}}\",\"nested\":{\"secret\":\"apiKey\"},\"auth\":\"K\\u0022ey\\u0026=/\\u002B \\u4E2D\"}", filled.Body.ToJsonString());
        Assert.Null(request.Body!["auth"]); // the plugin's descriptor is not mutated
    }

    [Theory]
    [InlineData(CredentialArea.Header, "Host")]
    [InlineData(CredentialArea.Header, "Content-Length")]
    [InlineData(CredentialArea.Header, "Proxy-Authorization")]
    [InlineData(CredentialArea.Header, "X-Missing")]
    [InlineData(CredentialArea.Header, "X-Filled")]
    [InlineData(CredentialArea.Query, "missing")]
    [InlineData(CredentialArea.Json, "/filled")]
    [InlineData(CredentialArea.Json, "/absent")]
    public void Targets_must_be_reserved_and_never_routing_fields(CredentialArea area, string target)
    {
        var request = new RequestDescriptor("POST", "https://api.example.com/", [new("Host", ""), new("Content-Length", ""), new("Proxy-Authorization", ""), new("X-Filled", "v")], JsonNode.Parse("""{"filled":"x"}"""));
        Assert.Throws<CredentialRejectedException>(() => CredentialInjector.Apply(request, [new CredentialSpec(area, target, [CredentialPart.Ref("apiKey")])], Secret));
    }

    [Fact]
    public void Crlf_in_a_header_value_is_rejected()
    {
        var request = new RequestDescriptor("GET", "https://api.example.com/", [new("Authorization", "")], null);
        Assert.Throws<CredentialRejectedException>(() => CredentialInjector.Apply(request, [new CredentialSpec(CredentialArea.Header, "Authorization", [CredentialPart.Text("Bearer x\r\nX-Evil: 1")])], Secret));
        Assert.Throws<CredentialRejectedException>(() => CredentialInjector.Apply(request, [new CredentialSpec(CredentialArea.Header, "Authorization", [new CredentialPart("a", "apiKey")])], Secret));
    }
}

public class DatabaseTests
{
    [Fact]
    public void Opens_with_wal_foreign_keys_and_current_schema()
    {
        using var root = new TempRoot();
        using var db = Database.Open(root.Paths.Database);
        Assert.Equal(Database.SchemaVersion, db.Version);
        Assert.Equal("wal", db.Read(c => Database.Scalar(c, null, "PRAGMA journal_mode;")));
        Assert.Equal(1L, db.Read(c => Database.Scalar(c, null, "PRAGMA foreign_keys;")));
    }

    [Fact] // DATA03: consistent backup while the WAL holds uncheckpointed pages
    public async Task Online_backup_is_consistent_with_an_active_wal()
    {
        using var root = new TempRoot();
        using var db = Database.Open(root.Paths.Database);
        var windows = new WindowStateRepository(db);
        for (int i = 0; i < 50; i++) windows.Save(new WindowPlacement($"w{i}", "m", i, -i, 144));
        await db.FlushAsync();
        Assert.True(new FileInfo(root.Paths.Database + "-wal").Length > 0);
        string backup = Path.Combine(root.Root, "copy.db");
        await db.BackupToAsync(backup);
        using var copy = new SqliteConnection($"Data Source={backup};Pooling=False");
        copy.Open();
        Assert.Equal(50L, Database.Scalar(copy, null, "SELECT count(*) FROM window_state;"));
        Assert.Equal(new WindowPlacement("w7", "m", 7, -7, 144), windows.Get("w7"));
    }

    [Fact] // DATA03: failed migration keeps the old version and data; older app refuses newer data and can restore its backup
    public async Task Failed_migration_and_rollback_to_an_older_app()
    {
        using var root = new TempRoot();
        int current = Database.SchemaVersion, next = current + 1;
        var migrations = new Dictionary<int, string>(Database.Migrations) { [next] = "CREATE TABLE f99_probe (id TEXT PRIMARY KEY);" };
        using (var v1 = Database.Open(root.Paths.Database)) { new WindowStateRepository(v1).Save(new WindowPlacement("main", "m", 1, 2, 96)); await v1.FlushAsync(); }

        Assert.Throws<SimulatedCrash>(() => Database.Open(root.Paths.Database, new FaultAt($"migrate:{next}"), next, migrations));
        using (var still = Database.Open(root.Paths.Database))
        {
            Assert.Equal(current, still.Version);
            Assert.Equal(0L, still.Read(c => Database.Scalar(c, null, "SELECT count(*) FROM sqlite_master WHERE name='f99_probe';")));
            Assert.NotNull(new WindowStateRepository(still).Get("main"));
        }

        using (var v2 = Database.Open(root.Paths.Database, null, next, migrations))
        {
            Assert.Equal(Database.BackupPath(root.Paths.Database, current), v2.MigrationBackup);
            new WindowStateRepository(v2).Save(new WindowPlacement("main", "m", 9, 9, 96));
            await v2.FlushAsync();
        }
        var refused = Assert.Throws<DatabaseVersionException>(() => Database.Open(root.Paths.Database));
        Assert.Equal((next, current), (refused.Found, refused.Supported));
        Assert.NotNull(refused.CompatibleBackup);
        Database.RestoreBackup(refused.CompatibleBackup!, root.Paths.Database);
        using var old = Database.Open(root.Paths.Database);
        Assert.Equal(current, old.Version);
        Assert.Equal(1, new WindowStateRepository(old).Get("main")!.X); // data matching the program version
    }

    [Fact] // no half-committed write survives an exception
    public async Task Failed_write_rolls_back()
    {
        using var root = new TempRoot();
        using var db = Database.Open(root.Paths.Database);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.WriteAsync<int>(w =>
        {
            w.Exec("INSERT INTO meta(key, value) VALUES ('x', 'y');");
            throw new InvalidOperationException("boom");
        }));
        Assert.Equal(0L, db.Read(c => Database.Scalar(c, null, "SELECT count(*) FROM meta WHERE key='x';")));
    }

    [Fact] // DATA04: duplicate usage events count once
    public async Task Usage_is_deduplicated_per_attempt_and_metric()
    {
        using var root = new TempRoot();
        using var db = Database.Open(root.Paths.Database);
        var clock = new ManualClock();
        var usage = new UsageRepository(db, clock);
        Assert.True(await usage.RecordAsync("deepl/translate", "a1", "chars", 100, "ok"));
        Assert.False(await usage.RecordAsync("deepl/translate", "a1", "chars", 100, "ok"));
        Assert.True(await usage.RecordAsync("deepl/translate", "a1", "requests", 1, "ok"));
        Assert.True(await usage.RecordAsync("deepl/translate", "a2", "chars", 50, "ok"));
        string period = clock.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(150, usage.Count("deepl/translate", "chars", period));
        Assert.Equal(1, usage.Count("deepl/translate", "requests", period));
        clock.Advance(TimeSpan.FromDays(8));
        Assert.Equal(3, await usage.PurgeEventsAsync());
        Assert.Equal(150, usage.Count("deepl/translate", "chars", period)); // aggregate kept
    }

    [Fact] // DATA04: namespaces are host-bound and quotas enforced
    public void Plugin_kv_isolation_and_quota()
    {
        using var root = new TempRoot();
        using var db = Database.Open(root.Paths.Database);
        var kv = new PluginKvRepository(db, perInstallationBytes: 100, totalBytes: 150);
        const string a = "app.susu.deepl", b = "com.other.plugin";
        kv.Set(a, "token", "\"a-value\"");
        Assert.Null(kv.Get(b, "token"));
        Assert.Equal("\"a-value\"", kv.Get(a, "token"));
        Assert.Throws<PluginKvQuotaException>(() => kv.Set(a, "big", new string('x', 100)));
        kv.Set(a, "token", new string('y', 80)); // replacing a key counts only the new size
        kv.Set(b, "k", new string('z', 40));
        Assert.Throws<PluginKvQuotaException>(() => kv.Set(b, "k2", new string('z', 40))); // total 150
        Assert.Equal(85, kv.BytesUsed(a));
        Assert.True(kv.Delete(a, "token"));
        Assert.False(kv.Delete(a, "token"));
    }

    [Fact] // F16.2: data is per package id, survives version changes and an uninstall, and goes only when the user chose to remove it
    public void Plugin_kv_is_keyed_by_package_id_and_removed_only_on_request()
    {
        using var root = new TempRoot();
        using var db = Database.Open(root.Paths.Database);
        var installs = new PluginInstallationRepository(db);
        var kv = new PluginKvRepository(db);
        installs.Activate("com.example.echo", "1.0.0", "unsigned:x", "h1");
        kv.Set("com.example.echo", "state", "{\"n\":1}");
        kv.Set("com.other.plugin", "state", "{\"n\":2}");
        installs.Activate("com.example.echo", "1.1.0", "unsigned:x", "h2"); // an update is a new installation row
        Assert.Equal("{\"n\":1}", kv.Get("com.example.echo", "state"));
        installs.Deactivate("com.example.echo"); // uninstall keeps the data
        Assert.Equal("{\"n\":1}", kv.Get("com.example.echo", "state"));
        installs.Activate("com.example.echo", "1.2.0", "unsigned:x", "h3");
        Assert.Equal("{\"n\":1}", kv.Get("com.example.echo", "state"));
        Assert.Equal(1, kv.DeletePackage("com.example.echo"));
        Assert.Null(kv.Get("com.example.echo", "state"));
        Assert.Equal("{\"n\":2}", kv.Get("com.other.plugin", "state")); // another package is untouched
    }

    [Fact] // F16.2: migrating schema 3 to 4 moves existing plugin_kv rows under their package id
    public void Plugin_kv_rows_survive_the_schema_4_migration()
    {
        using var root = new TempRoot();
        using (var old = Database.Open(root.Paths.Database, targetVersion: 3))
        {
            old.Write(w => w.Exec("INSERT INTO plugin_installations(installation_id, package_id, version, signer, hash, active) VALUES ('p@1', 'p', '1', 's', 'h', 0), ('p@2', 'p', '2', 's', 'h', 1);"));
            old.Write(w => w.Exec("INSERT INTO plugin_kv(installation_id, namespace, key, value_json, bytes) VALUES ('p@1', 'store', 'old', '1', 4), ('p@2', 'store', 'new', '2', 4), ('p@1', 'store', 'both', '\"v1\"', 9), ('p@2', 'store', 'both', '\"v2\"', 9);"));
        }
        using var db = Database.Open(root.Paths.Database);
        var kv = new PluginKvRepository(db);
        Assert.Equal("1", kv.Get("p", "old"));
        Assert.Equal("2", kv.Get("p", "new"));
        Assert.Equal("\"v2\"", kv.Get("p", "both")); // the active version wins a clash
    }
}

public class LeaseAndLogTests
{
    [Fact]
    public void Leases_delete_on_last_release_and_stale_sessions_are_cleaned()
    {
        using var root = new TempRoot();
        string stale = Path.Combine(root.Paths.Cache, "1-12345");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "old.wav"), "x");
        string unrelated = Path.Combine(root.Root, "user-picture.png");
        File.WriteAllText(unrelated, "keep");
        string sessionDir;
        using (var leases = new FileLeases(root.Paths.Cache))
        {
            Assert.Equal(1, leases.CleanupStaleSessions());
            Assert.False(Directory.Exists(stale));
            var lease = leases.Create("ocr", "png");
            string path = leases.PathOf(lease);
            sessionDir = Path.GetDirectoryName(path)!;
            File.WriteAllText(path, "img");
            Assert.Same(lease, leases.AddReference(lease.Id));
            Assert.Null(leases.AddReference("guess"));
            leases.Release(lease);
            Assert.True(File.Exists(path)); // second holder still active
            leases.Release(lease);
            Assert.False(File.Exists(path));
            Assert.Equal(32, lease.Id.Length);
            leases.Create("asr", "wav");
            Assert.Equal(1, leases.CleanupStaleSessions() + leases.ActiveCount); // own session never removed
        }
        Assert.False(Directory.Exists(sessionDir)); // exit cleanup
        Assert.True(File.Exists(unrelated));
    }

    [Fact] // PLAN 4.5.4 item 5: whitelist, secret masking in raw/URL/Base64 forms, URL query removal
    public void Log_redacts_secrets_urls_and_unlisted_fields()
    {
        using var root = new TempRoot();
        var log = new RedactingLog(root.Paths.Logs, new ManualClock());
        log.RegisterSecret("sk-live-ABC/123+=");
        log.Event("http.done", ("requestId", "r1"), ("status", 200), ("url", "https://api.example.com/v1/translate?key=sk-live-ABC%2F123%2B%3D&q=hello"),
            ("code", "echo sk-live-ABC/123+= and c2stbGl2ZS1BQkMvMTIzKz0= and sk-live-ABC%2F123%2B%3D"), ("body", "private text"), ("authorization", "Bearer x"));
        string text = File.ReadAllText(log.CurrentFile);
        Assert.DoesNotContain("sk-live", text, StringComparison.Ordinal);
        Assert.DoesNotContain("c2stbGl2ZS1BQkMvMTIzKz0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hello", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private text", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", text, StringComparison.Ordinal);
        Assert.Contains("https://api.example.com/v1/translate", text, StringComparison.Ordinal);
        Assert.Equal(2, log.DroppedFields);
    }

    [Fact]
    public void Plugin_logs_are_rate_limited_and_truncated()
    {
        using var root = new TempRoot();
        var clock = new ManualClock();
        var log = new RedactingLog(root.Paths.Logs, clock);
        int accepted = Enumerable.Range(0, 30).Count(i => log.Plugin("inst-a", "info", $"m{i}"));
        Assert.Equal(RedactingLog.PluginPerSecond, accepted);
        Assert.True(log.Plugin("inst-b", "info", "other plugin has its own budget"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(log.Plugin("inst-a", "warn", new string('字', 3000)));
        var lines = File.ReadAllLines(log.CurrentFile);
        Assert.Contains(lines, l => l.Contains("\"dropped\":10", StringComparison.Ordinal));
        string longLine = lines.Last(l => l.Contains("字", StringComparison.Ordinal));
        Assert.True(Encoding.UTF8.GetByteCount(longLine) < RedactingLog.PluginMessageBytes + 300);
    }

    [Fact] // 7 days and 10 MiB total
    public void Log_retention_by_age_and_size()
    {
        using var root = new TempRoot();
        var clock = new ManualClock();
        var log = new RedactingLog(root.Paths.Logs, clock, maxTotalBytes: 4096);
        string old = Path.Combine(root.Paths.Logs, $"susu-{clock.UtcNow.AddDays(-8):yyyyMMdd}.jsonl");
        string recent = Path.Combine(root.Paths.Logs, $"susu-{clock.UtcNow.AddDays(-1):yyyyMMdd}.jsonl");
        File.WriteAllText(old, "{}\n");
        File.WriteAllText(recent, new string('x', 3000) + "\n");
        for (int i = 0; i < 40; i++) log.Event("tick", ("count", i));
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(recent)); // oldest removed to fit the total
        Assert.True(new DirectoryInfo(root.Paths.Logs).GetFiles().Sum(f => f.Length) <= 4096);
    }

    [Fact] // F02.4: tests and development never resolve to the real profile
    public void Data_root_override_and_development_isolation()
    {
        string root = Path.Combine(Path.GetTempPath(), "susu-override");
        Assert.StartsWith(root, AppPaths.Resolve(root, development: false).Settings, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Su-Su-Dev", AppPaths.Resolve(null, development: true).Database, StringComparison.Ordinal);
        Assert.DoesNotContain("Su-Su-Dev", AppPaths.Resolve(null, development: false).Database, StringComparison.Ordinal);
    }
}
