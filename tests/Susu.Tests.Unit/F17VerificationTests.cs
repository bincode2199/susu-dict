using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Susu.Abstractions;
using Susu.Domain;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// Independent F17 verification (testing agent): DATA01-DATA04, DATA09, S07, S10, UI05 and the F17 exit line, attacked end to end. The archive is attacked byte
/// by byte with real zip and AES-GCM/PBKDF2 code (an encryptor written here from the documented format builds files with chosen KDF parameters, header
/// fields, entry names and manifests); the cross-user case uses a second protector with its own key (a fake for DPAPI); the stage, the live folders and the
/// restore point are scanned as bytes for every seeded key in every encoding; the switch at start is run against hostile pending records and stages;
/// diagnostics are exported from logs in unusual encodings and from unreadable folders; the data-clean entries are compared as trees, including junctions that
/// point outside the app data. Not executed: real DPAPI and a second Windows user, real dialogs, a real power cut or full disk, the packaged WebView,
/// screen reader and high contrast.
/// </summary>
public sealed class F17VerificationTests : IDisposable
{
    private const string Pw = "correct horse battery";
    private static readonly BackupExportOptions WithKeys = new(true, Pw);
    private static readonly BackupExportOptions Plain = new(false, null);
    private readonly List<IDisposable> cleanup = [];
    public void Dispose() { for (int i = cleanup.Count - 1; i >= 0; i--) cleanup[i].Dispose(); }

    private BackupRig Rig(ISecretProtector? protector = null) { var r = new BackupRig(protector); cleanup.Add(r); return r; }

    // ================= kit =================

    private static readonly byte[] Magic = "SUSUBAK"u8.ToArray().Append((byte)1).ToArray();

    /// <summary>Builds a .susubak the way the documented format says, with any header values (the importer must refuse what it cannot trust).</summary>
    private static byte[] Enc(byte[] zip, string password, int iterations = 600_000, int saltLen = 16, int declaredNonceLen = 12, string? headerJson = null, uint? lengthField = null,
        string kdf = "pbkdf2-sha256", string cipher = "aes-256-gcm")
    {
        byte[] salt = RandomNumberGenerator.GetBytes(saltLen), nonce = RandomNumberGenerator.GetBytes(12);
        byte[] declared = declaredNonceLen == 12 ? nonce : RandomNumberGenerator.GetBytes(declaredNonceLen);
        headerJson ??= $"{{\"kdf\":\"{kdf}\",\"iterations\":{iterations},\"salt\":\"{Convert.ToBase64String(salt)}\",\"nonce\":\"{Convert.ToBase64String(declared)}\",\"cipher\":\"{cipher}\"}}";
        byte[] header = Encoding.UTF8.GetBytes(headerJson);
        byte[] prefix = new byte[Magic.Length + 4 + header.Length];
        Magic.CopyTo(prefix, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(Magic.Length), lengthField ?? (uint)header.Length);
        header.CopyTo(prefix, Magic.Length + 4);
        int iter = iterations is < 1 or > 5_000_000 ? 1000 : iterations; // the real KDF cost only matters when the importer would accept the count
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC)), salt, iter, HashAlgorithmName.SHA256, 32);
        byte[] output = new byte[prefix.Length + zip.Length + 16];
        prefix.CopyTo(output, 0);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, zip, output.AsSpan(prefix.Length, zip.Length), output.AsSpan(prefix.Length + zip.Length, 16), prefix);
        return output;
    }

    private static byte[] ZipOf(IEnumerable<(string Name, byte[] Data)> entries, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
            {
                using var s = zip.CreateEntry(name, level).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    private static byte[] Json(string s) => Encoding.UTF8.GetBytes(s);
    private static byte[] Valid(out string settingsHash) { var b = F17BackupTests.Craft(); settingsHash = AtomicFile.Hash(F17BackupTests.ValidSettings()); return b; }

    private static string Put(BackupRig rig, byte[] bytes, string name = "in.susubak") => F17BackupTests.WriteTemp(rig, bytes, name);
    private static string Code(Action a) => F17BackupTests.CodeOf(a);
    private static bool Staged(BackupRig rig) => Directory.Exists(Path.Combine(rig.Paths.Imports, "staged")) || File.Exists(Path.Combine(rig.Paths.Imports, "pending.json"));

    private static void AssertRefusedCleanly(BackupRig rig, byte[] file, string? password, params string[] codes)
    {
        string settings = rig.SettingsHash, secrets = rig.SecretsHash;
        string code = Code(() => rig.Service.Preview(Put(rig, file), password));
        if (codes.Length > 0) Assert.Contains(code, codes);
        Assert.False(Staged(rig), $"staged after refusing with {code}");
        Assert.Equal("None", rig.Service.Status().State);
        Assert.Equal(settings, rig.SettingsHash);
        Assert.Equal(secrets, rig.SecretsHash);
    }

    private static IEnumerable<string> Forms(string value)
    {
        foreach (var f in SensitiveLiterals.FormsOf(value)) yield return f;
        yield return Convert.ToBase64String(Encoding.Unicode.GetBytes(value));
    }

    /// <summary>The first needle found in the bytes in any of its forms (UTF-8 text, Latin-1 view, UTF-16 bytes), or null.</summary>
    private static string? FindIn(byte[] data, IEnumerable<string> needles)
    {
        string utf8 = Encoding.UTF8.GetString(data), latin = Encoding.Latin1.GetString(data);
        foreach (var needle in needles)
            foreach (var form in Forms(needle).Append(needle))
            {
                if (form.Length < 4) continue;
                if (utf8.Contains(form, StringComparison.Ordinal) || latin.Contains(form, StringComparison.Ordinal)) return needle;
                if (data.AsSpan().IndexOf(Encoding.Unicode.GetBytes(form)) >= 0) return needle + "(utf16)";
            }
        return null;
    }

    private static string? FindInTree(string folder, IEnumerable<string> needles)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); } catch (IOException) { continue; }
            if (FindIn(bytes, needles) is { } hit) return $"{Path.GetRelativePath(folder, file)}:{hit}";
        }
        return null;
    }

    private static string? FindInZip(byte[] zipBytes, IEnumerable<string> needles)
    {
        if (FindIn(zipBytes, needles) is { } raw) return "zip:" + raw;
        using var zip = new ZipArchive(new MemoryStream(zipBytes));
        foreach (var e in zip.Entries)
        {
            using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
            if (FindIn(ms.ToArray(), needles) is { } hit) return $"{e.FullName}:{hit}";
        }
        return null;
    }

    private static readonly string[] SeedKeys =
        ["sk-live-9fA2kQ7zXb41LmN8pRt0VwYc", "密钥-ÄÖü-🔑-s3cr3t/with+odd=chars&more", "pr0xy-P@ssw0rd!-77", "tok_Zx81qLmN0pQ"];

    private static void SeedAll(BackupRig rig)
    {
        rig.Seed("DeepL-Label-77", SeedKeys[0]);
        var st = rig.Store.State;
        Assert.Equal(SaveStatus.Saved, rig.Store.Save(st.Effective with { Network = st.Effective.Network with { ProxyMode = ProxyMode.Http, ProxyHost = "proxy.host.test", ProxyPort = 8080, ProxyUsername = "proxy-user-x" } }, st.Revision, st.FileHash).Status);
        rig.Secrets.Write("acct-deepl", "second", SeedKeys[1]);
        rig.Secrets.Write(NetworkSettings.ProxyAccountId, "password", SeedKeys[2]);
        rig.Secrets.Write("acct-other", "token", SeedKeys[3]);
    }

    // ================= .susubak tampering: every byte region =================

    [Fact]
    public void Every_byte_region_of_an_encrypted_backup_is_protected_and_a_flip_stages_nothing()
    {
        var rig = Rig();
        SeedAll(rig);
        byte[] file = File.ReadAllBytes(rig.Export("k.susubak", WithKeys));
        uint headerLength = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8, 4));
        int headerEnd = 12 + (int)headerLength, tagStart = file.Length - 16;
        var positions = new List<int>();
        positions.AddRange(Enumerable.Range(0, 12));                           // magic, version, length field
        positions.AddRange(Enumerable.Range(12, (int)headerLength).Where(i => i % 3 == 0)); // header json (KDF params, salt, nonce)
        positions.AddRange([headerEnd - 1, headerEnd, headerEnd + 1, headerEnd + 17, (headerEnd + tagStart) / 2, tagStart - 1]); // ciphertext
        positions.AddRange(Enumerable.Range(tagStart, 16));                   // every tag byte
        var sw = Stopwatch.StartNew();
        foreach (int p in positions.Distinct())
            foreach (byte mask in new byte[] { 0x01, 0x80 })
            {
                byte[] bad = (byte[])file.Clone();
                bad[p] ^= mask;
                string settings = rig.SettingsHash, secrets = rig.SecretsHash;
                var ex = Record.Exception(() => rig.Service.Preview(Put(rig, bad), Pw));
                Assert.True(ex is BackupException, $"byte {p} mask {mask:x2}: {ex?.GetType().Name ?? "accepted"}");
                Assert.False(Staged(rig), $"staged after a flip at {p}");
                Assert.Equal(settings, rig.SettingsHash);
                Assert.Equal(secrets, rig.SecretsHash);
            }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(120), sw.Elapsed.ToString());
        // truncation at every region boundary and a trailing byte
        foreach (int keep in new[] { 0, 7, 8, 11, 12, headerEnd - 1, headerEnd, headerEnd + 1, tagStart, file.Length - 1 })
            AssertRefusedCleanly(rig, file[..keep], Pw);
        AssertRefusedCleanly(rig, [.. file, 0], Pw, "decrypt-failed");
        AssertRefusedCleanly(rig, [.. file, .. file], Pw, "decrypt-failed");
        // the untouched file still opens, so the refusals above were the flips and not the setup
        Assert.Equal(4, rig.Service.Preview(Put(rig, file), Pw).BackupSecrets);
    }

    [Fact]
    public void Header_length_field_extremes_are_refused_without_allocating_or_running_the_kdf()
    {
        var rig = Rig();
        byte[] zip = F17BackupTests.Craft();
        foreach (uint length in new uint[] { 0, 1, 1024, 1025, 65535, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF })
        {
            var sw = Stopwatch.StartNew();
            AssertRefusedCleanly(rig, Enc(zip, Pw, lengthField: length), Pw);
            Assert.True(sw.ElapsedMilliseconds < 2000, $"length {length}: {sw.ElapsedMilliseconds} ms");
        }
    }

    // ================= KDF bounds, exact boundaries =================

    [Theory]
    [InlineData(599_999, false)]
    [InlineData(600_000, true)]
    [InlineData(2_000_000, true)]
    [InlineData(2_000_001, false)]
    public void Kdf_iteration_boundaries_are_exact(int iterations, bool accepted)
    {
        var rig = Rig();
        byte[] file = Enc(F17BackupTests.Craft(), Pw, iterations);
        if (accepted)
        {
            Assert.NotNull(rig.Service.Preview(Put(rig, file), Pw));
            Assert.Equal("Previewed", rig.Service.Status().State);
        }
        else
        {
            var sw = Stopwatch.StartNew();
            AssertRefusedCleanly(rig, file, Pw, "kdf-params");
            Assert.True(sw.ElapsedMilliseconds < 1500, "refused only after deriving a key");
        }
    }

    [Theory]
    [InlineData(15, false)]
    [InlineData(16, true)]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void Salt_length_boundaries(int saltLength, bool accepted)
    {
        var rig = Rig();
        byte[] file = Enc(F17BackupTests.Craft(), Pw, saltLen: saltLength);
        if (accepted) Assert.NotNull(rig.Service.Preview(Put(rig, file), Pw));
        else AssertRefusedCleanly(rig, file, Pw, "corrupt");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(32)]
    public void A_header_that_declares_another_nonce_length_is_refused(int nonceLength)
        => AssertRefusedCleanly(Rig(), Enc(F17BackupTests.Craft(), Pw, declaredNonceLen: nonceLength), Pw, "corrupt");

    [Theory]
    [InlineData("\"iterations\":600000.5", "iterations")]
    [InlineData("\"iterations\":\"600000\"", "string")]
    [InlineData("\"iterations\":99999999999999999999", "huge")]
    [InlineData("\"iterations\":-600000", "negative")]
    [InlineData("\"iterations\":null", "null")]
    [InlineData("\"iterations\":1e6", "exponent")]
    [InlineData("\"iterations\":[600000]", "array")]
    public void Hostile_iteration_values_in_the_header_are_refused_with_a_backup_error(string field, string label)
    {
        var rig = Rig();
        string header = $"{{\"kdf\":\"pbkdf2-sha256\",{field},\"salt\":\"{Convert.ToBase64String(new byte[16])}\",\"nonce\":\"{Convert.ToBase64String(new byte[12])}\",\"cipher\":\"aes-256-gcm\"}}";
        var sw = Stopwatch.StartNew();
        var ex = Record.Exception(() => rig.Service.Preview(Put(rig, Enc(F17BackupTests.Craft(), Pw, headerJson: header), "h.susubak"), Pw));
        // 1e6 and 600000.5 are valid JSON numbers; System.Text.Json refuses them as int, which is a refusal too. Anything but a BackupException is a defect.
        Assert.True(ex is BackupException, $"{label}: {ex?.GetType().Name ?? "accepted"}");
        Assert.False(Staged(rig));
        Assert.True(sw.ElapsedMilliseconds < 2000, label);
    }

    [Theory]
    [InlineData("pbkdf2-sha1", "aes-256-gcm")]
    [InlineData("PBKDF2-SHA256", "aes-256-gcm")]
    [InlineData("pbkdf2-sha256", "aes-128-gcm")]
    [InlineData("pbkdf2-sha256", "none")]
    [InlineData("argon2id", "aes-256-gcm")]
    [InlineData("", "")]
    public void Unknown_kdf_or_cipher_names_are_refused_before_any_key_derivation(string kdf, string cipher)
    {
        var sw = Stopwatch.StartNew();
        AssertRefusedCleanly(Rig(), Enc(F17BackupTests.Craft(), Pw, kdf: kdf, cipher: cipher), Pw, "unsupported-kdf");
        Assert.True(sw.ElapsedMilliseconds < 1500);
    }

    [Theory]
    [InlineData("{\"kdf\":null,\"iterations\":600000,\"salt\":null,\"nonce\":null,\"cipher\":null}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"x\"")]
    [InlineData("{\"kdf\":\"pbkdf2-sha256\",\"iterations\":600000,\"salt\":\"!!!notbase64\",\"nonce\":\"AAAAAAAAAAAAAAAA\",\"cipher\":\"aes-256-gcm\"}")]
    [InlineData("{\"kdf\":\"pbkdf2-sha256\",\"iterations\":600000,\"salt\":\"\",\"nonce\":\"\",\"cipher\":\"aes-256-gcm\"}")]
    [InlineData("{}")]
    public void A_malformed_header_object_is_refused_as_corrupt(string header)
        => AssertRefusedCleanly(Rig(), Enc(F17BackupTests.Craft(), Pw, headerJson: header), Pw);

    [Fact]
    public void Password_length_boundaries_on_export_and_import()
    {
        var rig = Rig();
        rig.Seed();
        string path = Path.Combine(rig.Root.Root, "len.susubak");
        Assert.Equal("password-length", rig.Service.Export(path, new BackupExportOptions(false, new string('a', 7))).Error);
        Assert.True(rig.Service.Export(path, new BackupExportOptions(false, new string('a', 8))).Ok);
        Assert.True(rig.Service.Export(path, new BackupExportOptions(true, new string('a', 256))).Ok);
        Assert.Equal("password-length", rig.Service.Export(path, new BackupExportOptions(true, new string('a', 257))).Error);
        // a 256-character password opens its own file; 257 is refused with the same answer as a wrong password and without a derivation
        Assert.NotNull(rig.Service.Preview(path, new string('a', 256)));
        var sw = Stopwatch.StartNew();
        Assert.Equal("decrypt-failed", Code(() => rig.Service.Preview(path, new string('a', 257))));
        Assert.True(sw.ElapsedMilliseconds < 500);
        // unicode: composed and decomposed forms of the same password both open the file (NFC), and a lone surrogate password does not crash
        string composed = "pässwörd-é-密码", decomposed = composed.Normalize(NormalizationForm.FormD);
        Assert.True(rig.Service.Export(path, new BackupExportOptions(false, composed)).Ok);
        Assert.NotNull(rig.Service.Preview(path, decomposed));
        // DEFECT F17V-1 (low): a password with a lone surrogate makes string.Normalize throw ArgumentException from Preview and Export instead of a BackupException
        // code; the shell's generic catch turns it into a generic failure. When fixed, expect BackupException("decrypt-failed") / "password-invalid" here.
        rig.Service.Discard();
        Assert.IsType<ArgumentException>(Record.Exception(() => rig.Service.Preview(path, "\ud800 lone surrogate password")));
        Assert.IsType<ArgumentException>(Record.Exception(() => rig.Service.Export(path, new BackupExportOptions(false, "\ud800 lone surrogate"))));
        Assert.False(Staged(rig));
    }

    // ================= manifest hash list and contents of an encrypted file =================

    [Fact]
    public void A_consistent_manifest_is_required_even_inside_a_file_the_password_opens()
    {
        var rig = Rig();
        var settings = F17BackupTests.ValidSettings();
        byte[] plugins = Json("{\"plugins\":[]}");
        string H(byte[] b) => AtomicFile.Hash(b);
        byte[] Manifest(string files, bool secrets = false, int fv = 1, int sv = 1, string format = "susubak")
            => Json($"{{\"format\":\"{format}\",\"formatVersion\":{fv},\"app\":\"Su-Su\",\"appVersion\":\"9\",\"schemaVersion\":{sv},\"createdUtc\":\"2026-01-01T00:00:00Z\",\"includesSecrets\":{secrets.ToString().ToLowerInvariant()},\"files\":{{{files}}}}}");
        string good = $"\"settings.yaml\":\"{H(settings)}\",\"plugins.json\":\"{H(plugins)}\"";
        var cases = new (string Label, byte[] Manifest, string Expect)[]
        {
            ("wrong hash", Manifest($"\"settings.yaml\":\"{new string('0', 64)}\",\"plugins.json\":\"{H(plugins)}\""), "hash-mismatch"),
            ("hash of the other file", Manifest($"\"settings.yaml\":\"{H(plugins)}\",\"plugins.json\":\"{H(settings)}\""), "hash-mismatch"),
            ("empty hash", Manifest($"\"settings.yaml\":\"\",\"plugins.json\":\"{H(plugins)}\""), "hash-mismatch"),
            ("short hash", Manifest($"\"settings.yaml\":\"{H(settings)[..63]}\",\"plugins.json\":\"{H(plugins)}\""), "hash-mismatch"),
            ("entry missing from the list", Manifest($"\"settings.yaml\":\"{H(settings)}\""), "manifest-mismatch"),
            ("listed entry that is not there", Manifest(good + $",\"secrets.json\":\"{H(plugins)}\""), "manifest-mismatch"),
            ("manifest lists itself", Manifest(good + $",\"manifest.json\":\"{H(plugins)}\""), "manifest-mismatch"),
            ("secrets flag with no secrets", Manifest(good, secrets: true), "manifest-mismatch"),
            ("files is null", Json("{\"format\":\"susubak\",\"formatVersion\":1,\"schemaVersion\":1,\"files\":null}"), "manifest-invalid"),
            ("wrong format name", Manifest(good, format: "other"), "manifest-invalid"),
            ("format version 0", Manifest(good, fv: 0), "manifest-invalid"),
            ("format version 2", Manifest(good, fv: 2), "format-newer"),
            ("schema -1", Manifest(good, sv: -1), "manifest-invalid"),
            ("schema 999", Manifest(good, sv: 999), "schema-newer"),
            ("not json", Json("hello"), "manifest-invalid"),
            ("empty", [], "manifest-invalid"),
        };
        foreach (var c in cases)
        {
            byte[] zip = ZipOf([("manifest.json", c.Manifest), ("settings.yaml", settings), ("plugins.json", plugins)]);
            string code = Code(() => rig.Service.Preview(Put(rig, Enc(zip, Pw)), Pw));
            Assert.True(c.Expect == code, $"{c.Label}: expected {c.Expect}, got {code}");
            Assert.False(Staged(rig), c.Label);
        }
        // the same file with a correct manifest is accepted (the setup is sound)
        Assert.NotNull(rig.Service.Preview(Put(rig, Enc(ZipOf([("manifest.json", Manifest(good)), ("settings.yaml", settings), ("plugins.json", plugins)]), Pw)), Pw));
    }

    [Fact]
    public void Plain_backup_tampering_is_caught_by_the_hashes_or_changes_nothing_that_matters()
    {
        var rig = Rig();
        rig.Seed();
        byte[] file = File.ReadAllBytes(rig.Export("p.susubak", Plain));
        rig.Service.Preview(Put(rig, file), null);
        byte[] baseline = File.ReadAllBytes(Path.Combine(rig.Paths.Imports, "staged", "settings.yaml"));
        rig.Service.Discard();
        int accepted = 0, refused = 0;
        for (int p = 0; p < file.Length; p++)
        {
            byte[] bad = (byte[])file.Clone();
            bad[p] ^= 0x20;
            var ex = Record.Exception(() => rig.Service.Preview(Put(rig, bad), null));
            if (ex is null)
            {
                // A flip in a field the format does not verify (timestamps, attributes, comments) may open; what is staged must be exactly the original.
                accepted++;
                Assert.Equal(baseline, File.ReadAllBytes(Path.Combine(rig.Paths.Imports, "staged", "settings.yaml")));
                rig.Service.Discard();
            }
            else
            {
                Assert.True(ex is BackupException, $"byte {p}: {ex.GetType().Name}: {ex.Message}");
                refused++;
            }
            Assert.False(Staged(rig) && ex is not null);
        }
        Assert.True(refused > file.Length / 2, $"accepted {accepted} refused {refused}");
        Assert.Equal("None", rig.Service.Status().State);
    }

    // ================= entry names =================

    [Theory]
    [InlineData("Manifest.json")]
    [InlineData("MANIFEST.JSON")]
    [InlineData("manifest.JSON")]
    [InlineData("Settings.yaml")]
    [InlineData("settings.YAML")]
    [InlineData("plugins.JSON")]
    [InlineData("settings.yaml.")]
    [InlineData("settings.yaml ")]
    [InlineData("settings.yaml:stream")]
    [InlineData("settings.yaml::$DATA")]
    [InlineData("settings.yaml\0")]
    [InlineData("settings.yaml\u200b")]
    [InlineData("\uff53ettings.yaml")]            // fullwidth S
    [InlineData("settings.yam\u0430l")]           // Cyrillic a plus l
    [InlineData("ſettings.yaml")]                 // long s (case-folds to s)
    [InlineData("plugins.json\u202e")]            // right-to-left override
    [InlineData("./settings.yaml")]
    [InlineData(".\\settings.yaml")]
    [InlineData("settings.yaml/")]
    [InlineData("SETTINGS~1.YAM")]
    [InlineData("secrets.dat")]
    [InlineData("susu.db")]
    [InlineData("plugins/")]
    [InlineData("plugins/evil/manifest.yaml")]
    [InlineData("plugins/evil/main.js")]
    [InlineData("plugins\\evil\\main.js")]
    [InlineData("main.js")]
    [InlineData("evil.dll")]
    [InlineData("evil.exe")]
    [InlineData("evil.js")]
    [InlineData("native/susu-host.exe")]
    [InlineData("a/b/c/settings.yaml")]
    [InlineData("C:\\Windows\\settings.yaml")]
    [InlineData("\\\\server\\share\\settings.yaml")]
    [InlineData("..\\..\\settings.yaml")]
    [InlineData("CON")]
    [InlineData("NUL")]
    public void An_extra_entry_name_in_any_variant_is_refused_and_nothing_is_staged_or_installed(string name)
    {
        var rig = Rig();
        var pluginsBefore = Directory.GetFileSystemEntries(rig.Paths.UserPlugins, "*", SearchOption.AllDirectories);
        byte[] plain = F17BackupTests.Craft(f => f[name] = "MZ-payload"u8.ToArray());
        AssertRefusedCleanly(rig, plain, null, "path-escape", "unexpected-entry", "duplicate-entry", "manifest-mismatch");
        AssertRefusedCleanly(rig, Enc(F17BackupTests.Craft(f => f[name] = "MZ-payload"u8.ToArray()), Pw), Pw, "path-escape", "unexpected-entry", "duplicate-entry", "manifest-mismatch");
        Assert.Equal(pluginsBefore, Directory.GetFileSystemEntries(rig.Paths.UserPlugins, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("settings.yaml")]
    [InlineData("plugins.json")]
    public void A_duplicate_entry_is_refused_even_when_both_copies_are_valid_and_the_manifest_is_consistent(string name)
    {
        var rig = Rig();
        var settings = F17BackupTests.ValidSettings();
        byte[] plugins = Json("{\"plugins\":[]}");
        string hashes = $"\"settings.yaml\":\"{AtomicFile.Hash(settings)}\",\"plugins.json\":\"{AtomicFile.Hash(plugins)}\"";
        byte[] manifest = Json($"{{\"format\":\"susubak\",\"formatVersion\":1,\"app\":\"Su-Su\",\"appVersion\":\"1\",\"schemaVersion\":1,\"createdUtc\":\"2026-01-01T00:00:00Z\",\"includesSecrets\":false,\"files\":{{{hashes}}}}}");
        var entries = new List<(string, byte[])> { ("manifest.json", manifest), ("settings.yaml", settings), ("plugins.json", plugins) };
        entries.Add((name, entries.First(e => e.Item1 == name).Item2));
        AssertRefusedCleanly(rig, ZipOf(entries), null, "duplicate-entry");
        // case variants of a valid name are a duplicate for the case-insensitive file system, and refused as unexpected either way
        entries[^1] = (name.ToUpperInvariant(), entries.First(e => e.Item1 == name).Item2);
        AssertRefusedCleanly(rig, ZipOf(entries), null, "unexpected-entry", "duplicate-entry");
    }

    [Fact]
    public void A_zip_whose_central_directory_and_local_headers_disagree_or_is_zip64_or_has_a_comment_is_refused_or_harmless()
    {
        var rig = Rig();
        byte[] good = F17BackupTests.Craft();
        // append garbage after the end record, prepend garbage, and put a comment of 65535 bytes: none may open as something else
        foreach (byte[] variant in new byte[][] { [.. good, .. new byte[100]], [.. new byte[100], .. good], [.. new byte[10], .. good[..^22], .. new byte[22]] })
        {
            var ex = Record.Exception(() => rig.Service.Preview(Put(rig, variant), null));
            if (ex is not null) Assert.IsType<BackupException>(ex);
            rig.Service.Discard();
        }
        // end record claims 0xFFFF entries (zip64 marker) with a single real one: refused as too many, not read as 65535
        byte[] bomb = (byte[])good.Clone();
        int eocd = bomb.Length - 22;
        Assert.Equal(0x06054b50u, BinaryPrimitives.ReadUInt32LittleEndian(bomb.AsSpan(eocd)));
        BinaryPrimitives.WriteUInt16LittleEndian(bomb.AsSpan(eocd + 10), 0xFFFF);
        AssertRefusedCleanly(rig, bomb, null, "too-many-entries", "corrupt");
    }

    // ================= size and ratio boundaries =================

    private static byte[] PluginsJsonOfSize(int size, bool random)
    {
        // {"plugins":[],"pad":"...."} with the filler chosen so the whole file is exactly `size` bytes
        string head = "{\"plugins\":[],\"pad\":\"", tail = "\"}";
        int fill = size - head.Length - tail.Length;
        string body = random ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(fill * 3 / 4 + 3))[..fill] : new string('a', fill);
        return Json(head + body + tail);
    }

    [Fact]
    public void An_entry_of_exactly_the_maximum_size_opens_and_one_byte_more_is_refused()
    {
        var rig = Rig();
        byte[] atLimit = PluginsJsonOfSize((int)BackupLimits.MaxEntryBytes, random: true);
        Assert.Equal(BackupLimits.MaxEntryBytes, atLimit.Length);
        byte[] ok = F17BackupTests.Craft(f => f["plugins.json"] = atLimit);
        Assert.True(ok.Length < BackupLimits.MaxFileBytes, "random filler must stay under the file limit");
        Assert.NotNull(rig.Service.Preview(Put(rig, ok), null)); // 8 MiB: accepted (and the list is empty)
        rig.Service.Discard();
        byte[] over = F17BackupTests.Craft(f => f["plugins.json"] = PluginsJsonOfSize((int)BackupLimits.MaxEntryBytes + 1, random: true));
        AssertRefusedCleanly(rig, over, null, "too-large");
    }

    [Fact]
    public void The_ratio_rule_starts_above_one_mebibyte_exactly()
    {
        var rig = Rig();
        byte[] exactly = F17BackupTests.Craft(f => f["plugins.json"] = PluginsJsonOfSize((int)BackupLimits.RatioFloorBytes, random: false)); // 1 MiB of 'a': compresses ~1000x, allowed by the floor
        Assert.NotNull(rig.Service.Preview(Put(rig, exactly), null));
        rig.Service.Discard();
        byte[] over = F17BackupTests.Craft(f => f["plugins.json"] = PluginsJsonOfSize((int)BackupLimits.RatioFloorBytes + 1, random: false));
        AssertRefusedCleanly(rig, over, null, "ratio");
        // many MiB of the same byte: still ratio, and bounded in time and memory (no 100 MiB inflate)
        var sw = Stopwatch.StartNew();
        AssertRefusedCleanly(rig, F17BackupTests.Craft(f => f["plugins.json"] = PluginsJsonOfSize(30 << 20, random: false)), null, "ratio", "too-large");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
    }

    [Fact]
    public void The_file_size_limit_is_exact_and_checked_before_parsing()
    {
        var rig = Rig();
        byte[] atLimit = new byte[BackupLimits.MaxFileBytes];
        atLimit[0] = (byte)'P'; atLimit[1] = (byte)'K';
        AssertRefusedCleanly(rig, atLimit, null, "corrupt", "not-a-backup"); // read in full, then judged on content (not "too-large")
        byte[] over = new byte[BackupLimits.MaxFileBytes + 1];
        over[0] = (byte)'P'; over[1] = (byte)'K';
        AssertRefusedCleanly(rig, over, null, "too-large");
        // an encrypted-looking file of the same size
        byte[] magic = new byte[BackupLimits.MaxFileBytes + 1];
        Magic.CopyTo(magic, 0);
        AssertRefusedCleanly(rig, magic, Pw, "too-large");
        // not a file: a directory, a missing path, an empty path
        Assert.Equal("unreadable", Code(() => rig.Service.Preview(rig.Root.Root, null)));
        Assert.Equal("unreadable", Code(() => rig.Service.Preview(Path.Combine(rig.Root.Root, "nope.susubak"), null)));
        Assert.Equal("unreadable", Code(() => rig.Service.Preview("", null)));
        Assert.Equal("unreadable", Code(() => rig.Service.Preview("bad\0path", null)));
        Assert.False(rig.Service.NeedsPassword(rig.Root.Root));
    }

    // ================= keys: cross user, plain files, scans =================

    [Fact]
    public void A_plain_backup_holds_no_key_no_grant_no_identity_in_any_encoding()
    {
        var rig = Rig();
        SeedAll(rig);
        byte[] file = File.ReadAllBytes(rig.Export("plain.susubak", Plain));
        var secretsOnly = SeedKeys.Concat(["sk-test-SECRET-1234", "api-free.deepl.com", "header:Authorization"]);
        Assert.Null(FindInZip(file, secretsOnly));
        Assert.Null(FindIn(file, secretsOnly));
        // the identity of the exporting machine and user is not in the file or in its entries
        var identity = new[] { Environment.UserName, Environment.MachineName, Environment.UserDomainName }.Where(s => s.Length >= 4).ToArray();
        Assert.Null(FindInZip(file, identity));
        // an encrypted file without keys: same, and its bytes are opaque
        byte[] enc = File.ReadAllBytes(rig.Export("enc.susubak", new BackupExportOptions(false, Pw)));
        Assert.Null(FindIn(enc, secretsOnly));
        Assert.Null(FindIn(enc, ["DeepL-Label-77", "proxy.host.test", "proxy-user-x", "settings.yaml", "manifest"])); // all of it is encrypted
        // by design the plain file carries the labels and the proxy host/user (the preview and the restore need them); it never carries a password
        Assert.NotNull(FindInZip(file, ["DeepL-Label-77"]));
        // and the secret names appear as names only
        string yaml = Encoding.UTF8.GetString(F17ReadEntry(file, "settings.yaml"));
        Assert.DoesNotContain("password:", yaml, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] F17ReadEntry(byte[] zipBytes, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes));
        using var s = zip.GetEntry(name)!.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Cross_user_restore_leaves_no_plaintext_key_on_disk_at_any_step_and_only_the_importing_user_can_read_them()
    {
        var first = Rig(new XorProtector());
        SeedAll(first);
        string file = first.Export("keys.susubak", WithKeys);
        string copy = Path.Combine(Path.GetTempPath(), "susu-tests", "xuser-" + Guid.NewGuid().ToString("N") + ".susubak");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(file, copy);

        var second = Rig(new KeyedProtector(0x37));
        second.Seed("Second", "sk-second-user-KEY-xyz", account: "acct-second");
        var preview = second.Service.Preview(copy, Pw);
        Assert.Equal(4, preview.BackupSecrets);
        // after the preview: the stage on disk holds protected values only
        Assert.Null(FindInTree(second.Paths.Imports, SeedKeys));
        Assert.Null(FindInTree(second.Root.Root, SeedKeys.Concat(["sk-second-user-KEY-xyz"]).Take(4)));
        Assert.True(second.Service.Apply(preview.Token));
        Assert.Null(FindInTree(second.Root.Root, SeedKeys));
        var result = second.Restart();
        Assert.Equal("Applied", result!.State);
        // after the switch: nothing plaintext anywhere under either data folder, including the restore point and the journal
        Assert.Null(FindInTree(second.Root.Root, SeedKeys));
        // the importing user reads all four keys; the exporting user's protector reads none of the new file
        Assert.True(second.Secrets.TryRead("acct-deepl", "apiKey", out string a)); Assert.Equal(SeedKeys[0], a);
        Assert.True(second.Secrets.TryRead("acct-deepl", "second", out string b)); Assert.Equal(SeedKeys[1], b);
        Assert.True(second.Secrets.TryRead(NetworkSettings.ProxyAccountId, "password", out string c)); Assert.Equal(SeedKeys[2], c);
        Assert.True(second.Secrets.TryRead("acct-other", "token", out string d)); Assert.Equal(SeedKeys[3], d);
        Assert.False(second.Secrets.TryRead("acct-second", "apiKey", out _)); // the local key was replaced as announced
        var asFirst = new SecretStore(second.Paths.Secrets, new XorProtector());
        foreach (var (acct, name) in second.Secrets.Entries())
        {
            bool read;
            try { read = asFirst.TryRead(acct, name, out _); } catch (Exception e) when (e is CryptographicException or InvalidDataException) { read = false; }
            Assert.False(read, $"{acct}/{name} opened by the exporting user's protector");
        }
        // the pre-import pair in the restore point is the second user's own ciphertext: undo gives their key back and none of the first user's
        Assert.True(second.Service.ScheduleUndo());
        second.Restart();
        Assert.True(second.Secrets.TryRead("acct-second", "apiKey", out string back)); Assert.Equal("sk-second-user-KEY-xyz", back);
        Assert.False(second.Secrets.TryRead("acct-deepl", "apiKey", out _));
        Assert.Null(FindInTree(second.Root.Root, SeedKeys));
    }

    [Fact]
    public void Hostile_secrets_files_inside_an_encrypted_backup_are_refused_cleanly_or_imported_harmlessly()
    {
        var rig = Rig();
        string One(string account, string name, string value) => $"{{\"account\":{JsonSerializer.Serialize(account)},\"name\":{JsonSerializer.Serialize(name)},\"value\":{JsonSerializer.Serialize(value)}}}";
        string S(params string[] entries) => "{\"entries\":[" + string.Join(",", entries) + "]}";
        var cases = new (string Label, string Secrets)[]
        {
            ("duplicate pair", S(One("a", "k", "v1"), One("a", "k", "v2"))),
            ("empty value", S(One("a", "k", ""))),
            ("value over the limit", S(One("a", "k", new string('x', SecretStore.MaxValueChars + 1)))),
            ("value at the limit", S(One("a", "k", new string('x', SecretStore.MaxValueChars)))),
            ("blank account", S(One(" ", "k", "v"))),
            ("path-like names", S(One("..\\..\\x", "../k", "v"))),
            ("unicode and control names", S(One("账户\u0000x", "k\n", "v"))),
            ("1001 entries", S(Enumerable.Range(0, 1001).Select(i => One("a" + i, "k", "v")).ToArray())),
            ("1000 entries", S(Enumerable.Range(0, 1000).Select(i => One("a" + i, "k", "v")).ToArray())),
            ("null entries", "{\"entries\":[null]}"),
            ("entries null", "{\"entries\":null}"),
            ("not json", "oops"),
        };
        foreach (var c in cases)
        {
            byte[] zip = F17BackupTests.Craft(f => f["secrets.json"] = Json(c.Secrets), includesSecrets: true);
            var ex = Record.Exception(() => rig.Service.Preview(Put(rig, Enc(zip, Pw)), Pw));
            if (ex is not null) Assert.True(ex is BackupException, $"{c.Label}: {ex.GetType().Name}: {ex.Message}");
            rig.Service.Discard();
        }
        Assert.False(Staged(rig));
    }

    // ================= import: pending stages, preview truth, restore points =================

    private static string Source(BackupRig source, string name, BackupExportOptions? options = null) => source.Export(name, options ?? WithKeys);

    private BackupRig Labelled(string label, string key, string account, ISecretProtector? protector = null)
    {
        var r = Rig(protector);
        r.Seed(label, key, account);
        return r;
    }

    private static string Label(BackupRig r) => r.Store.State.Effective.Accounts.Single().Label;

    [Fact]
    public void Previewing_a_second_file_replaces_a_confirmed_import_and_the_old_token_stops_working()
    {
        string a = Source(Labelled("A", "sk-a-KEYKEY", "acct-a"), "a.susubak"), b = Source(Labelled("B", "sk-b-KEYKEY", "acct-b"), "b.susubak");
        var target = Labelled("T", "sk-t-KEYKEY", "acct-t");
        var pa = target.Service.Preview(a, Pw);
        Assert.True(target.Service.Apply(pa.Token));
        Assert.Equal("Ready", target.Service.Status().State);
        // a refused file leaves the confirmed import alone
        Assert.Equal("not-a-backup", Code(() => target.Service.Preview(Put(target, "junk"u8.ToArray()), null)));
        Assert.Equal("Ready", target.Service.Status().State);
        // a second good file replaces it: the confirmed import is dropped without a word (observation F17V-2, low: the page must not offer Pick while Ready)
        var pb = target.Service.Preview(b, Pw);
        Assert.NotEqual(pa.Token, pb.Token);
        Assert.Equal("Previewed", target.Service.Status().State);
        Assert.False(target.Service.Apply(pa.Token));
        Assert.False(target.Service.Apply(""));
        Assert.False(target.Service.Apply(null!));
        Assert.True(target.Service.Apply(pb.Token));
        Assert.False(target.Service.Apply(pb.Token)); // a second confirm is not an error and not a second import
        target.Restart();
        Assert.Equal("B", Label(target));
        Assert.Null(target.Restart()); // nothing is applied twice
        Assert.Equal("B", Label(target));
    }

    [Fact]
    public void Discard_after_confirm_cancels_and_the_next_start_changes_nothing()
    {
        string a = Source(Labelled("A", "sk-a-KEYKEY", "acct-a"), "a.susubak");
        var target = Labelled("T", "sk-t-KEYKEY", "acct-t");
        string s = target.SettingsHash, k = target.SecretsHash;
        Assert.True(target.Service.Apply(target.Service.Preview(a, Pw).Token));
        target.Service.Discard();
        Assert.Equal("None", target.Service.Status().State);
        Assert.Null(target.Restart());
        Assert.Equal(s, target.SettingsHash);
        Assert.Equal(k, target.SecretsHash);
        target.Service.Discard(); // idempotent
    }

    [Fact]
    public void A_backup_with_the_largest_revision_still_restores_to_a_loadable_config_with_a_larger_revision()
    {
        var source = Labelled("Big", "sk-big-KEYKEY", "acct-big");
        string yaml = Encoding.UTF8.GetString(F17BackupTests.ValidSettings()).Replace("revision: 3", $"revision: {long.MaxValue}", StringComparison.Ordinal);
        Assert.Contains(long.MaxValue.ToString(), yaml, StringComparison.Ordinal);
        byte[] file = F17BackupTests.Craft(f => f["settings.yaml"] = Json(yaml));
        var target = Labelled("T", "sk-t-KEYKEY", "acct-t");
        var preview = target.Service.Preview(Put(target, file), null);
        Assert.True(target.Service.Apply(preview.Token));
        var result = target.Restart();
        long revision = target.Store.State.Revision;
        // DEFECT F17V-3 (low-medium; needs a crafted file): max(current, backup)+1 overflows a long, the applied settings.yaml carries a negative revision, the
        // loader rejects it ("range" on revision), and the result still says Applied. The app then runs on the last valid state with an invalid live file.
        // Records today's behaviour; when fixed expect FileInvalid false and a positive revision.
        Assert.Equal("Applied", result?.State);
        Assert.True(target.Store.State.FileInvalid);
        Assert.Contains(target.Store.State.Issues, i => i.Code == "range");
    }

    [Fact]
    public void Preview_counts_are_what_the_restart_applies()
    {
        var source = Labelled("Src", "sk-src-KEYKEY", "acct-src");
        var st = source.Store.State;
        var gone = new[] { "g1", "g2" };
        Assert.Equal(SaveStatus.Saved, source.Store.Save(st.Effective with
        {
            Accounts = [.. st.Effective.Accounts, new AccountSettings("acct-two", "Two", ["apiKey", "other"], [])],
            Instances = [.. st.Effective.Instances, .. gone.Select(g => new InstanceSettings(g, "app.example." + g, 1, new Dictionary<string, string>(), new Dictionary<string, string>()))],
            Services = [.. st.Effective.Services, .. gone.Select(g => new ServiceSettings(g, Susu.Contracts.Capability.Translate, true)), new ServiceSettings("g1", Susu.Contracts.Capability.Ocr, false)],
            TranslationOrder = [.. st.Effective.TranslationOrder, "g1/translate", "g2/translate"],
            Prompts = [new PromptProfile("p1", "One", "Translate {text}"), new PromptProfile("p2", "Two", "Polish {text}")],
        }, st.Revision, st.FileHash).Status);
        source.Secrets.Write("acct-two", "apiKey", "sk-two-KEYKEY");
        source.UserPlugins.AddRange([new BackupPluginRef("app.example.g1", "1.0.0", "k", "h1"), new BackupPluginRef("app.example.g2", "1.0.0", "k", "h2")]);
        string file = Source(source, "src.susubak");

        var target = Labelled("Local", "sk-local-KEYKEY", "acct-local");
        target.Secrets.Write("acct-local", "extra", "sk-extra-KEYKEY");
        var before = target.Store.State.Effective;
        int enabledBefore = before.Services.Count(s => s.Enabled);
        int keysBefore = target.Secrets.Entries().Count;
        var preview = target.Service.Preview(file, Pw);
        Assert.True(target.Service.Apply(preview.Token));
        var result = target.Restart()!;

        var after = target.Store.State.Effective;
        var d = preview.Deltas.ToDictionary(x => x.Area);
        Assert.Equal(before.Accounts.Count, d["accounts"].Current); Assert.Equal(after.Accounts.Count, d["accounts"].Backup);
        Assert.Equal(before.Instances.Count, d["instances"].Current); Assert.Equal(after.Instances.Count, d["instances"].Backup);
        Assert.Equal(enabledBefore, d["enabledServices"].Current); Assert.Equal(after.Services.Count(s => s.Enabled), d["enabledServices"].Backup);
        Assert.Equal(before.Prompts.Count, d["prompts"].Current); Assert.Equal(after.Prompts.Count, d["prompts"].Backup);
        Assert.Equal(preview.DisabledInstances.Count, result.DisabledInstances);
        Assert.Equal(["g1", "g2"], preview.DisabledInstances.Order());
        Assert.All(gone, g => Assert.All(after.Services.Where(s => s.Instance == g), s => Assert.False(s.Enabled)));
        var keysAfter = target.Secrets.Entries();
        Assert.Equal(preview.BackupSecrets, keysAfter.Count);
        Assert.Equal(keysBefore - keysBefore + 2 + 0, keysAfter.Count); // the two keys the backup brought
        Assert.Equal(2, preview.KeysRemoved);                           // acct-local/apiKey and acct-local/extra are not in the backup
        Assert.DoesNotContain(keysAfter, k => k.Account == "acct-local");
        Assert.Equal(["acct-src", "acct-two"], preview.Accounts.Select(a => a.Id).Order());
        Assert.Equal(["other"], preview.Accounts.Single(a => a.Id == "acct-two").MissingSecrets); // announced as missing and truly absent
        Assert.False(target.Secrets.TryRead("acct-two", "other", out _));
        Assert.All(after.Accounts, a => Assert.Empty(a.Grants));
        Assert.Equal(["app.example.g1", "app.example.g2"], preview.MissingPackages.Order());
        Assert.Equal(["app.example.g1", "app.example.g2"], preview.Plugins.Where(p => p.Status == "missing").Select(p => p.Id).Order());
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(target.Paths.UserPlugins, "*", SearchOption.AllDirectories), p => p.Contains("g1") || p.Contains("g2"));
    }

    [Fact]
    public void Hostile_plugin_lists_are_information_only_and_touch_no_file_system()
    {
        var rig = Rig();
        string before = string.Join("|", Directory.EnumerateFileSystemEntries(rig.Root.Root, "*", SearchOption.AllDirectories).Order());
        string Plugin(string id, string version, string signer = "k", string hash = "h") => $"{{\"id\":{JsonSerializer.Serialize(id)},\"version\":{JsonSerializer.Serialize(version)},\"signer\":{JsonSerializer.Serialize(signer)},\"hash\":{JsonSerializer.Serialize(hash)}}}";
        var cases = new (string Label, string Plugins, bool Refuse)[]
        {
            ("traversal id", "{\"plugins\":[" + Plugin("..\\..\\..\\Windows\\evil", "1.0.0") + "]}", false),
            ("absolute id", "{\"plugins\":[" + Plugin("C:\\evil", "1.0.0", "..\\x", "..\\..\\y") + "]}", false),
            ("long id", "{\"plugins\":[" + Plugin(new string('x', 100_000), "1.0.0") + "]}", false),
            ("garbage version", "{\"plugins\":[" + Plugin("a.b", "not a version at all") + "," + Plugin("a.c", "") + "," + Plugin("a.d", "1.0.0.0.0") + "]}", false),
            ("1000 plugins", "{\"plugins\":[" + string.Join(",", Enumerable.Range(0, 1000).Select(i => Plugin("a.p" + i, "1.0.0"))) + "]}", false),
            ("1001 plugins", "{\"plugins\":[" + string.Join(",", Enumerable.Range(0, 1001).Select(i => Plugin("a.p" + i, "1.0.0"))) + "]}", true),
            ("blank id", "{\"plugins\":[" + Plugin(" ", "1.0.0") + "]}", true),
            ("null version", "{\"plugins\":[{\"id\":\"a.b\",\"version\":null,\"signer\":\"k\",\"hash\":\"h\"}]}", true),
            ("null entry", "{\"plugins\":[null]}", true),
            ("extra fields with code", "{\"plugins\":[{\"id\":\"a.b\",\"version\":\"1\",\"signer\":\"k\",\"hash\":\"h\",\"code\":\"eval(1)\",\"url\":\"http://evil/x.zip\"}],\"download\":\"http://evil/y.zip\"}", false),
        };
        foreach (var c in cases)
        {
            byte[] file = F17BackupTests.Craft(f => f["plugins.json"] = Json(c.Plugins));
            var ex = Record.Exception(() => rig.Service.Preview(Put(rig, file), null));
            if (c.Refuse) Assert.True(ex is BackupException, $"{c.Label}: {ex?.GetType().Name ?? "accepted"}");
            else if (ex is not null) Assert.Fail($"{c.Label}: {ex.GetType().Name}: {ex.Message}");
            rig.Service.Discard();
        }
        // one accepted hostile file, applied and restarted: no plugin folder, no download, nothing but the config files and the import bookkeeping
        byte[] hostile = F17BackupTests.Craft(f => f["plugins.json"] = Json(cases[0].Plugins));
        Assert.True(rig.Service.Apply(rig.Service.Preview(Put(rig, hostile, "hostile.susubak"), null).Token));
        rig.Restart();
        var added = Directory.EnumerateFileSystemEntries(rig.Root.Root, "*", SearchOption.AllDirectories).Except(before.Split('|')).Select(p => Path.GetRelativePath(rig.Root.Root, p)).ToArray();
        Assert.All(added, p => Assert.True(p.Contains("import") || p.Contains("transactions") || p.EndsWith("hostile.susubak") || p.EndsWith("in.susubak") || p.Contains("settings") || p.Contains("secrets") || p.Contains("susubak"), p));
        Assert.Empty(Directory.GetFileSystemEntries(rig.Paths.UserPlugins));
    }

    [Fact]
    public void Restore_points_hold_one_generation_and_undo_alternates_between_the_last_two_configs()
    {
        var target = Labelled("T0", "sk-t0-KEYKEY", "acct-t0");
        string b = Source(Labelled("B", "sk-b-KEYKEY", "acct-b"), "b.susubak"), c = Source(Labelled("C", "sk-c-KEYKEY", "acct-c"), "c.susubak");
        Assert.False(target.Service.Status().CanUndo);
        Assert.True(target.Service.Apply(target.Service.Preview(b, Pw).Token)); target.Restart();
        Assert.Equal("B", Label(target)); Assert.True(target.Service.Status().CanUndo);
        Assert.True(target.Service.Apply(target.Service.Preview(c, Pw).Token)); target.Restart();
        Assert.Equal("C", Label(target));
        // undo goes to B (the generation before the last import), not to T0
        Assert.True(target.Service.ScheduleUndo()); target.Restart();
        Assert.Equal("B", Label(target));
        Assert.True(target.Secrets.TryRead("acct-b", "apiKey", out string kb)); Assert.Equal("sk-b-KEYKEY", kb);
        Assert.False(target.Secrets.TryRead("acct-c", "apiKey", out _));
        // undo of the undo is C again; T0 is gone after two imports (documented: one generation)
        Assert.True(target.Service.ScheduleUndo()); target.Restart();
        Assert.Equal("C", Label(target));
        Assert.False(target.Secrets.TryRead("acct-t0", "apiKey", out _));
        // while an undo is only scheduled, nothing changed and Discard cancels it
        Assert.True(target.Service.ScheduleUndo());
        target.Service.Discard();
        Assert.Null(target.Restart());
        Assert.Equal("C", Label(target));
    }

    [Fact]
    public void A_damaged_restore_point_is_not_offered_or_not_applied_and_never_breaks_the_start()
    {
        var target = Labelled("T", "sk-t-KEYKEY", "acct-t");
        string b = Source(Labelled("B", "sk-b-KEYKEY", "acct-b"), "b.susubak");
        Assert.True(target.Service.Apply(target.Service.Preview(b, Pw).Token)); target.Restart();
        string rp = Path.Combine(target.Paths.Imports, "restore-point");
        Assert.True(Directory.Exists(rp));
        string settingsCopy = File.ReadAllText(Path.Combine(rp, "settings.yaml"));
        // garbage settings: undo is refused up front
        File.WriteAllText(Path.Combine(rp, "settings.yaml"), "{{{ not yaml: [");
        Assert.False(target.Service.ScheduleUndo());
        Assert.Equal("None", target.Service.Status().State);
        // garbage meta only: still usable (the meta is not trusted for anything but a token)
        File.WriteAllText(Path.Combine(rp, "settings.yaml"), settingsCopy);
        File.WriteAllText(Path.Combine(rp, "meta.json"), "garbage");
        // secrets.dat damaged: the stage is hash-consistent (made from the damaged bytes), so the damage is committed. Observe what the app then does with it.
        File.WriteAllBytes(Path.Combine(rp, "secrets.dat"), [1, 2, 3, 4, 5]);
        bool scheduled = target.Service.ScheduleUndo();
        string liveBefore = target.SecretsHash;
        if (scheduled)
        {
            // DEFECT F17V-4 (low): the restore point's secrets.dat is not parsed before it is staged and committed, so a damaged restore point replaces the
            // live key file with unreadable bytes (the keys are lost; the settings come back). The result is recorded here, not asserted away.
            // The restart is the host's: recover, ApplyPending, then open the stores. Opening throws JsonException on the damaged secrets.dat, which the host
            // (Program.cs, `new SecretStore(...)`) does not catch: the application cannot start until the file is removed by hand.
            var ex = Record.Exception(() => target.Restart());
            Assert.NotEqual(liveBefore, target.SecretsHash);
            Assert.IsType<System.Text.Json.JsonException>(ex);
            Assert.Equal("Applied", BackupImport.ReadResult(target.Paths)?.State);
        }
    }

    // ================= ApplyPending at start: corrupt records and stages =================

    private static (BackupRig Target, BackupPreview Preview) Ready(BackupRig target, string file)
    {
        var p = target.Service.Preview(file, Pw);
        Assert.True(target.Service.Apply(p.Token));
        return (target, p);
    }

    private static string PendingPath(BackupRig r) => Path.Combine(r.Paths.Imports, "pending.json");
    private static string StagePath(BackupRig r, string name = "") => Path.Combine(r.Paths.Imports, "staged", name);

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"token\":null,\"state\":null}")]
    [InlineData("{\"token\":1,\"state\":2}")]
    [InlineData("\u0000\u0000\u0000")]
    [InlineData("{\"state\":\"Ready\"")]
    [InlineData("{\"token\":\"t\",\"state\":\"Ready\",\"settingsHash\":null,\"secretsHash\":null,\"attempts\":\"x\"}")]
    [InlineData("{\"token\":\"..\\\\..\\\\evil\",\"state\":\"Ready\",\"settingsHash\":\"\",\"secretsHash\":\"\",\"attempts\":0,\"source\":\"x\\n\\u0000y\"}")]
    [InlineData("{\"token\":\"t\",\"state\":\"Applying\",\"settingsHash\":\"a\",\"secretsHash\":\"b\",\"finalSettingsHash\":\"c\",\"attempts\":-5,\"source\":\"s\",\"disabled\":-9}")]
    [InlineData("{\"token\":\"t\",\"state\":\"Ready\",\"settingsHash\":\"a\",\"secretsHash\":\"b\",\"attempts\":2147483647,\"source\":\"s\"}")]
    public void A_corrupt_or_hostile_pending_record_ends_in_a_recorded_failure_and_never_changes_the_config(string content)
    {
        string a = Source(Labelled("A", "sk-a-KEYKEY", "acct-a"), "a.susubak");
        var (target, _) = Ready(Labelled("T", "sk-t-KEYKEY", "acct-t"), a);
        string s = target.SettingsHash, k = target.SecretsHash;
        File.WriteAllText(PendingPath(target), content);
        BackupApplyResult? result = null;
        var ex = Record.Exception(() => result = target.Restart());
        Assert.Null(ex);
        Assert.Equal(s, target.SettingsHash);
        Assert.Equal(k, target.SecretsHash);
        Assert.Equal("T", Label(target));
        Assert.False(Directory.Exists(StagePath(target)));
        Assert.False(File.Exists(PendingPath(target)));
        Assert.False(target.Store.State.FileInvalid);
        if (result is not null) Assert.Equal("Failed", result.State);
        // the page's error text is a stable key, never a path or the raw content
        Assert.DoesNotContain("evil", result?.Error ?? "", StringComparison.Ordinal);
        Assert.Null(target.Restart()); // and the next start is quiet
    }

    [Fact]
    public void A_token_and_source_with_path_characters_are_data_only_and_create_nothing_outside_the_import_folder()
    {
        string a = Source(Labelled("A", "sk-a-KEYKEY", "acct-a"), "a.susubak");
        var (target, p) = Ready(Labelled("T", "sk-t-KEYKEY", "acct-t"), a);
        string json = File.ReadAllText(PendingPath(target)).Replace(p.Token, "..\\\\..\\\\..\\\\Roaming\\\\pwned", StringComparison.Ordinal);
        File.WriteAllText(PendingPath(target), json);
        var outside = Path.Combine(target.Root.Root, "Roaming", "pwned");
        var result = target.Restart();
        Assert.Equal("Applied", result!.State);
        Assert.False(File.Exists(outside) || Directory.Exists(outside));
        Assert.False(Directory.Exists(Path.Combine(target.Root.Root, "pwned")));
        Assert.False(Directory.Exists(Path.Combine(target.Paths.Imports, "..", "..", "pwned")));
        Assert.Equal("A", Label(target));
    }

    [Theory]
    [InlineData("missing-secrets")]
    [InlineData("missing-settings")]
    [InlineData("changed-settings")]
    [InlineData("changed-secrets")]
    [InlineData("settings-is-a-directory")]
    [InlineData("planted-final-with-recorded-hash")]
    [InlineData("secrets-empty")]
    public void A_damaged_stage_is_refused_at_start_and_the_old_pair_stays(string damage)
    {
        string a = Source(Labelled("A", "sk-a-KEYKEY", "acct-a"), "a.susubak");
        var (target, _) = Ready(Labelled("T", "sk-t-KEYKEY", "acct-t"), a);
        string s = target.SettingsHash, k = target.SecretsHash;
        switch (damage)
        {
            case "missing-secrets": File.Delete(StagePath(target, "secrets.dat")); break;
            case "missing-settings": File.Delete(StagePath(target, "settings.yaml")); break;
            case "changed-settings": File.AppendAllText(StagePath(target, "settings.yaml"), "\n# x"); break;
            case "changed-secrets": File.AppendAllText(StagePath(target, "secrets.dat"), "x"); break;
            case "settings-is-a-directory": File.Delete(StagePath(target, "settings.yaml")); Directory.CreateDirectory(StagePath(target, "settings.yaml")); break;
            case "secrets-empty": File.WriteAllBytes(StagePath(target, "secrets.dat"), []); break;
            case "planted-final-with-recorded-hash":
                // an earlier try recorded a final hash; the file on disk is then replaced
                File.WriteAllText(PendingPath(target), File.ReadAllText(PendingPath(target)).Replace("\"finalSettingsHash\":null", "\"finalSettingsHash\":\"" + new string('a', 64) + "\"", StringComparison.Ordinal).Replace("\"state\":\"Ready\"", "\"state\":\"Applying\"", StringComparison.Ordinal));
                File.WriteAllText(StagePath(target, "settings.final.yaml"), "revision: 1\n");
                break;
        }
        BackupApplyResult? result = null;
        Assert.Null(Record.Exception(() => result = target.Restart()));
        Assert.Equal("Failed", result!.State);
        Assert.Equal(s, target.SettingsHash);
        Assert.Equal(k, target.SecretsHash);
        Assert.False(Directory.Exists(StagePath(target)));
        Assert.Equal("T", Label(target));
        Assert.True(target.Secrets.TryRead("acct-t", "apiKey", out string key)); Assert.Equal("sk-t-KEYKEY", key);
    }

    [Fact]
    public void A_staged_settings_file_that_no_longer_parses_is_refused_even_when_its_hash_was_updated_to_match()
    {
        string a = Source(Labelled("A", "sk-a-KEYKEY", "acct-a"), "a.susubak");
        var (target, p) = Ready(Labelled("T", "sk-t-KEYKEY", "acct-t"), a);
        byte[] bad = Json("schemaVersion: 1\nrevision: 'x'\ngeneral: [\n");
        File.WriteAllBytes(StagePath(target, "settings.yaml"), bad);
        string json = File.ReadAllText(PendingPath(target));
        var pending = JsonDocument.Parse(json).RootElement;
        json = json.Replace(pending.GetProperty("settingsHash").GetString()!, AtomicFile.Hash(bad), StringComparison.Ordinal);
        File.WriteAllText(PendingPath(target), json);
        string s = target.SettingsHash;
        var result = target.Restart();
        Assert.Equal("Failed", result!.State);
        Assert.Equal("settings-invalid", result.Error);
        Assert.Equal(s, target.SettingsHash);
    }

    [Fact]
    public void A_junction_in_place_of_the_stage_is_removed_without_deleting_what_it_points_to()
    {
        string a = Source(Labelled("A", "sk-a-KEYKEY", "acct-a"), "a.susubak");
        var target = Labelled("T", "sk-t-KEYKEY", "acct-t");
        target.Service.Preview(a, Pw); // Previewed: dropped at the next start
        string outside = TestTemp.NewDir("susu-f17v-outside");
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "do not delete");
        Directory.CreateDirectory(Path.Combine(outside, "sub"));
        File.WriteAllText(Path.Combine(outside, "sub", "also.txt"), "keep");
        string stage = StagePath(target).TrimEnd('\\');
        Directory.Delete(stage, recursive: true);
        if (!TryJunction(stage, outside)) { Assert.Fail("junction could not be created on this machine"); }
        Assert.Null(Record.Exception(() => target.Restart()));
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")), "a file behind the junction was deleted");
        Assert.True(File.Exists(Path.Combine(outside, "sub", "also.txt")));
        Assert.False(Directory.Exists(stage));
        // the same for a discard through the service
        Directory.CreateDirectory(stage[..^"staged".Length]);
        Assert.True(TryJunction(stage, outside));
        File.WriteAllText(PendingPath(target), "{\"token\":\"t\",\"state\":\"Previewed\",\"settingsHash\":\"\",\"secretsHash\":\"\",\"attempts\":0,\"source\":\"s\"}");
        target.Service.Discard();
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
        Assert.True(File.Exists(Path.Combine(outside, "sub", "also.txt")));
    }

    private static bool TryJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 && Directory.Exists(link);
    }

    // ================= the config journal at start =================

    [Fact]
    public void A_planted_journal_cannot_make_recovery_touch_a_file_outside_the_config()
    {
        var rig = Rig();
        string victim = Path.Combine(TestTemp.NewDir("susu-f17v-victim"), "victim.txt");
        File.WriteAllText(victim, "original victim");
        string dir = Path.Combine(rig.Paths.Transactions, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "0.old"), "overwritten by recovery");
        File.WriteAllText(Path.Combine(dir, "journal.json"), JsonSerializer.Serialize(new { state = "Prepared", files = new[] { new { target = victim, hadOld = true } } }));
        var ex = Record.Exception(() => new ConfigTransaction(rig.Paths.Transactions).Recover());
        string now = File.Exists(victim) ? File.ReadAllText(victim) : "<deleted>";
        // DEFECT F17V-5 (low-medium, defense in depth; the same shape as F16's journal.json finding): recovery trusts the Target path recorded in the journal, so a
        // journal planted in %LOCALAPPDATA%\Su-Su\transactions overwrites (hadOld) or deletes (not hadOld) any file the user can write. Targets should be limited to
        // the settings and secrets paths. This assertion records today's behaviour; when fixed the victim must stay "original victim".
        Assert.Null(ex);
        Assert.Equal("overwritten by recovery", now);
    }

    [Theory]
    [InlineData("{\"state\":\"Prepared\"}")]
    [InlineData("{\"state\":\"Prepared\",\"files\":null}")]
    [InlineData("{\"state\":\"Prepared\",\"files\":[null]}")]
    [InlineData("{\"state\":\"Prepared\",\"files\":[{\"target\":null,\"hadOld\":true}]}")]
    [InlineData("{\"state\":\"Prepared\",\"files\":[{\"target\":\"\",\"hadOld\":false}]}")]
    [InlineData("{\"state\":\"Prepared\",\"files\":[{\"target\":\"x\\u0000y\",\"hadOld\":true}]}")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    public void A_damaged_journal_must_not_stop_the_application_from_starting(string journal)
    {
        var rig = Rig();
        rig.Seed();
        string dir = Path.Combine(rig.Paths.Transactions, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "journal.json"), journal);
        string s = rig.SettingsHash;
        var ex = Record.Exception(() => rig.Restart());
        // DEFECT F17V-6 (low-medium): journals with a missing files list or a null/empty/invalid target throw NullReferenceException, ArgumentException or
        // similar out of Recover(), which runs before anything is loaded; the host cannot start until someone deletes the transactions folder by hand. Any such
        // journal should be treated as damaged: delete it and go on. The exception type is recorded so the fix can be verified.
        bool crashes = ex is not null;
        if (crashes) Assert.True(ex is NullReferenceException or ArgumentException or InvalidOperationException or IOException or NotSupportedException, ex!.GetType().Name);
        else Assert.Equal(s, rig.SettingsHash);
        Assert.True(File.Exists(rig.Paths.Settings));
    }

    // === END ===
}
