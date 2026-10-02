using System.Security.Cryptography;
using System.Text;
using Susu.Abstractions;
using Susu.Plugins.AppUpdate;
using Susu.Plugins.Install;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>Test keys and builders for F18.2. The seeds are PUBLIC test values: they live only in the test assembly, never in src, and the production key slot is empty.</summary>
internal static class UpdateKeys
{
    public static readonly byte[] RootSeed = Convert.FromHexString("c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7");
    public static readonly byte[] ReleaseSeed = Convert.FromHexString("fc51cd8e6218a0a46f4e4f3b63a8e23e1bd4c4d63a30b9a3f1c1f1b8b1c9f0a1");
    public static readonly byte[] AttackerSeed = Convert.FromHexString("0d4a05c7a14e5e7a0c8f2d6d3b1f9b2c5e8a7d6c4b3a291807f6e5d4c3b2a190");
    public static readonly byte[] NextSeed = Convert.FromHexString("a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90");

    public static UpdateKey Key(string id, byte[] seed, bool release, bool rotate) => new(id, Ed25519.PublicKey(seed), release, rotate);

    /// <summary>Roots: one offline root (rotate and release) is enough for the tests; "rel-1" is a release-only key.</summary>
    public static UpdateKeyring Test() => new([Key("root-test", RootSeed, true, true), Key("rel-1", ReleaseSeed, true, false)], "test");

    public static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    public static byte[] SignFile(string keyId, byte[] seed, string domain, byte[] bytes)
        => UpdateSignature.Format(keyId, Ed25519.Sign(seed, UpdateSignature.Signed(domain, bytes)));

    public static string ManifestText(string version, long sequence, byte[] zip, string? file = null, string? sha = null, long? size = null, string extra = "")
        => $"schema: 1\nproduct: susu\nchannel: stable\nversion: {version}\nsequence: {sequence}\npublished: 2026-10-02\npackage:\n  file: {file ?? $"susu-{version}.zip"}\n  size: {size ?? zip.Length}\n  sha256: {sha ?? Convert.ToHexStringLower(SHA256.HashData(zip))}\nnotes: \"Fixes and polish\"\n{extra}";

    public static UpdateBundle Bundle(string manifest, string keyId = "rel-1", byte[]? seed = null, params SignedBlob[] rotations)
    {
        byte[] raw = Encoding.UTF8.GetBytes(manifest);
        return new UpdateBundle(raw, SignFile(keyId, seed ?? ReleaseSeed, UpdateSignature.ManifestDomain, raw), rotations);
    }

    public static SignedBlob Rotation(long sequence, string add, string revoke, string signerId = "root-test", byte[]? signerSeed = null)
    {
        string text = $"schema: 1\nsequence: {sequence}\n{(add.Length > 0 ? "add:\n" + add : "")}{(revoke.Length > 0 ? "revoke:\n" + revoke : "")}";
        byte[] raw = Encoding.UTF8.GetBytes(text);
        return new SignedBlob(raw, SignFile(signerId, signerSeed ?? RootSeed, UpdateSignature.KeyringDomain, raw));
    }

    public static string AddKey(string id, byte[] seed, string purposes = "release") => $"  - id: {id}\n    key: {Hex(Ed25519.PublicKey(seed))}\n    purposes: {purposes}\n";
}

/// <summary>
/// F18.2 signed manifest, keyring, rotation and anti-rollback (TEST-PLAN UPD05). Signature verification happens over the raw bytes before any parsing; a
/// failed or unverified response never yields an update offer or "up to date".
/// </summary>
public class F18UpdateTrustTests
{
    private static readonly byte[] Zip = Encoding.UTF8.GetBytes("pretend package bytes");

    private static (UpdateVerifier Verifier, UpdateTrustStore Store, string Path) Rig(UpdateKeyring? roots = null)
    {
        string path = Path.Combine(TestTemp.NewDir("susu-f18-trust"), "trust.json");
        var store = new UpdateTrustStore(path, roots ?? UpdateKeys.Test());
        return (new UpdateVerifier(store), store, path);
    }

    private static string Code(AppUpdateCheck check) => check.Status == AppUpdateStatus.Failed ? check.ErrorCode! : check.Status.ToString();

    [Fact]
    public void A_valid_signed_manifest_offers_the_new_version_with_its_hash_and_size()
    {
        var (verifier, store, _) = Rig();
        var check = verifier.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("1.2.0", 5, Zip)), "1.1.0");
        Assert.Equal(AppUpdateStatus.Available, check.Status);
        Assert.Equal(("1.2.0", 5L, "susu-1.2.0.zip", (long)Zip.Length), (check.Offer!.Version, check.Offer.Sequence, check.Offer.FileName, check.Offer.Size));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Zip)), check.Offer.Sha256);
        Assert.Equal(5, store.HighSequence);
        Assert.Equal(AppUpdateStatus.UpToDate, verifier.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("1.2.0", 5, Zip)), "1.2.0").Status);
    }

    [Fact]
    public void The_production_key_slot_is_empty_so_an_unsigned_build_cannot_verify_anything()
    {
        Assert.True(UpdateKeyring.Production.IsEmpty);
        Assert.Equal("production-slot-empty", UpdateKeyring.Production.Marker);
        var (verifier, _, _) = Rig(UpdateKeyring.Production);
        Assert.Equal("no-trusted-keys", Code(verifier.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("1.2.0", 5, Zip)), "1.1.0")));
        Assert.DoesNotContain(UpdateKeys.Test().Keys, k => UpdateKeyring.Production.Find(k.Id) is not null);
    }

    [Fact]
    public void Flipping_a_byte_in_every_region_of_the_manifest_is_a_signature_failure_before_any_parsing()
    {
        var (verifier, store, _) = Rig();
        string text = UpdateKeys.ManifestText("1.2.0", 5, Zip);
        var good = UpdateKeys.Bundle(text);
        // One byte in each line (first, middle, last character) plus the first and last byte of the file: header keys, values, hash, size, notes.
        var offsets = new SortedSet<int> { 0, good.Manifest.Length - 1 };
        int at = 0;
        foreach (string line in text.Split('\n'))
        {
            if (line.Length > 0) { offsets.Add(at); offsets.Add(at + line.Length / 2); offsets.Add(at + line.Length - 1); }
            at += line.Length + 1;
        }
        Assert.True(offsets.Count > 25);
        foreach (int offset in offsets)
        {
            var bytes = (byte[])good.Manifest.Clone();
            bytes[offset] ^= 0x01;
            // Even a flip that makes the YAML or UTF-8 invalid is reported as a signature failure: nothing was parsed.
            Assert.Equal("signature-invalid", Code(verifier.Check(good with { Manifest = bytes }, "1.1.0")));
        }
        Assert.Equal(0, store.HighSequence); // nothing was remembered from any of them
        Assert.Equal(AppUpdateStatus.Available, verifier.Check(good, "1.1.0").Status); // the untouched one still verifies
    }

    [Fact]
    public void Appending_reformatting_or_truncating_the_manifest_breaks_the_signature_it_is_the_raw_bytes_that_are_signed()
    {
        var (verifier, _, _) = Rig();
        string text = UpdateKeys.ManifestText("1.2.0", 5, Zip);
        var good = UpdateKeys.Bundle(text);
        foreach (var changed in new[]
        {
            Encoding.UTF8.GetBytes(text + "\n"), Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n")), Encoding.UTF8.GetBytes(text + "# extra\n"),
            good.Manifest[..^1], [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. good.Manifest], Encoding.UTF8.GetBytes(text.Replace("version: 1.2.0", "version:  1.2.0")),
        })
            Assert.Equal("signature-invalid", Code(verifier.Check(good with { Manifest = changed }, "1.1.0")));
        Assert.Equal("manifest-too-large", Code(verifier.Check(good with { Manifest = new byte[UpdateVerifier.MaxManifestBytes + 1] }, "1.1.0")));
        Assert.Equal("manifest-too-large", Code(verifier.Check(good with { Manifest = [] }, "1.1.0")));
    }

    [Fact]
    public void Every_byte_of_the_signature_file_is_checked_and_a_wrong_or_missing_key_is_refused()
    {
        var (verifier, _, _) = Rig();
        var good = UpdateKeys.Bundle(UpdateKeys.ManifestText("1.2.0", 5, Zip));
        for (int i = 0; i < good.Signature.Length; i++)
        {
            var bytes = (byte[])good.Signature.Clone();
            bytes[i] ^= 0x01;
            var check = verifier.Check(good with { Signature = bytes }, "1.1.0");
            Assert.Equal(AppUpdateStatus.Failed, check.Status);
            Assert.Null(check.Offer);
        }
        Assert.Equal("signature-invalid", Code(verifier.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("1.2.0", 5, Zip), "rel-1", UpdateKeys.AttackerSeed), "1.1.0"))); // signed by someone else under a trusted id
        Assert.Equal("unknown-key", Code(verifier.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("1.2.0", 5, Zip), "attacker", UpdateKeys.AttackerSeed), "1.1.0")));
        Assert.Equal("signature-malformed", Code(verifier.Check(good with { Signature = [] }, "1.1.0")));
        Assert.Equal("signature-malformed", Code(verifier.Check(good with { Signature = Encoding.UTF8.GetBytes("key-id: rel-1\nalgorithm: rsa\nsignature: AAAA\n") }, "1.1.0")));
        // A manifest signed in the plugin domain (a plugin signature replayed) is not an update signature.
        byte[] raw = good.Manifest;
        var crossDomain = new UpdateBundle(raw, UpdateKeys.SignFile("rel-1", UpdateKeys.ReleaseSeed, "susu-plugin-v1", raw), []);
        Assert.Equal("signature-invalid", Code(verifier.Check(crossDomain, "1.1.0")));
        // The release-only key cannot sign key rotation, and a rotate-only key cannot sign releases.
        var rotateOnly = new UpdateKeyring([UpdateKeys.Key("r", UpdateKeys.RootSeed, false, true), UpdateKeys.Key("x", UpdateKeys.ReleaseSeed, true, false)], "test");
        var (v2, _, _) = Rig(rotateOnly);
        Assert.Equal("key-not-allowed", Code(v2.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("1.2.0", 5, Zip), "r", UpdateKeys.RootSeed), "1.1.0")));
    }

    [Theory]
    [InlineData("schema: 2", "manifest-schema-unsupported")]
    [InlineData("product: other", "manifest-wrong-product")]
    [InlineData("channel: beta", "manifest-wrong-product")]
    [InlineData("version: 1.2", "manifest-invalid")]
    [InlineData("sequence: -3", "manifest-invalid")]
    public void A_signed_but_malformed_manifest_is_rejected_after_the_signature_with_a_clear_code(string replace, string expected)
    {
        var (verifier, store, _) = Rig();
        string text = UpdateKeys.ManifestText("1.2.0", 5, Zip);
        string key = replace[..replace.IndexOf(':')];
        string edited = string.Join('\n', text.Split('\n').Select(l => l.StartsWith(key + ":", StringComparison.Ordinal) ? replace : l));
        Assert.Equal(expected, Code(verifier.Check(UpdateKeys.Bundle(edited), "1.1.0")));
        Assert.Equal(0, store.HighSequence);
    }

    [Fact]
    public void Unknown_keys_duplicate_keys_aliases_a_path_in_the_file_name_and_an_oversized_package_are_rejected()
    {
        var (verifier, _, _) = Rig();
        string ok = UpdateKeys.ManifestText("1.2.0", 5, Zip);
        foreach (var (text, why) in new (string, string)[]
        {
            (ok + "extra: 1\n", "unknown top-level key"),
            (ok + "version: 9.9.9\n", "duplicate key"),
            (ok.Replace("notes: \"Fixes and polish\"", "notes: &a x\nother: *a"), "anchor and alias"),
            (UpdateKeys.ManifestText("1.2.0", 5, Zip, file: "../evil.zip"), "path in file name"),
            (UpdateKeys.ManifestText("1.2.0", 5, Zip, file: "a/b.zip"), "folder in file name"),
            (UpdateKeys.ManifestText("1.2.0", 5, Zip, file: "run.exe"), "not a zip"),
            (UpdateKeys.ManifestText("1.2.0", 5, Zip, sha: "ABC"), "short hash"),
            (UpdateKeys.ManifestText("1.2.0", 5, Zip, size: UpdateVerifier.MaxPackageBytes + 1), "huge size"),
            (UpdateKeys.ManifestText("1.2.0", 5, Zip, size: 0), "zero size"),
            (ok.Replace("package:\n  file:", "package:\n  url: https://x/y.zip\n  file:"), "url field"),
        })
            Assert.True(verifier.Check(UpdateKeys.Bundle(text), "1.1.0").Status == AppUpdateStatus.Failed, why);
    }

    // ---------- rotation ----------

    [Fact]
    public void A_rotation_signed_by_the_current_root_adds_a_key_that_then_signs_releases_and_the_chain_survives_a_restart()
    {
        var (verifier, store, path) = Rig();
        var rotation = UpdateKeys.Rotation(1, UpdateKeys.AddKey("rel-2", UpdateKeys.NextSeed), revoke: "  - rel-1\n");
        string manifest = UpdateKeys.ManifestText("1.2.0", 5, Zip);
        // Signed by the new key, with its rotation record: accepted.
        Assert.Equal(AppUpdateStatus.Available, verifier.Check(UpdateKeys.Bundle(manifest, "rel-2", UpdateKeys.NextSeed, rotation), "1.1.0").Status);
        Assert.Single(store.Chain);
        Assert.Null(store.Keyring.Find("rel-1")); // revoked
        // After a restart the chain is re-verified from the embedded roots and rel-2 still works without being sent again.
        var again = new UpdateVerifier(new UpdateTrustStore(path, UpdateKeys.Test()));
        Assert.Equal(AppUpdateStatus.UpToDate, again.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("1.2.0", 5, Zip), "rel-2", UpdateKeys.NextSeed), "1.2.0").Status);
        // The revoked key no longer signs releases.
        Assert.Equal("unknown-key", Code(again.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("1.3.0", 6, Zip), "rel-1", UpdateKeys.ReleaseSeed), "1.2.0")));
        // The root itself was never replaced.
        Assert.NotNull(store.Keyring.Find("root-test"));
    }

    [Fact]
    public void A_forged_rotation_is_refused_whoever_signed_it_and_changes_nothing()
    {
        var (verifier, store, _) = Rig();
        string manifest = UpdateKeys.ManifestText("1.2.0", 5, Zip);
        string attackerAdd = UpdateKeys.AddKey("evil", UpdateKeys.AttackerSeed, "release,rotate");
        var forged = new Dictionary<string, SignedBlob>
        {
            ["signed by an unknown key"] = UpdateKeys.Rotation(1, attackerAdd, "", "attacker", UpdateKeys.AttackerSeed),
            ["trusted id, wrong private key"] = UpdateKeys.Rotation(1, attackerAdd, "", "root-test", UpdateKeys.AttackerSeed),
            ["signed by a release-only key"] = UpdateKeys.Rotation(1, attackerAdd, "", "rel-1", UpdateKeys.ReleaseSeed),
            ["no signature"] = new SignedBlob(Encoding.UTF8.GetBytes("schema: 1\nsequence: 1\nadd:\n" + attackerAdd), []),
            ["wrong domain"] = Retag(UpdateKeys.Rotation(1, attackerAdd, "")),
            ["skipped sequence"] = UpdateKeys.Rotation(2, attackerAdd, ""),
            ["replayed sequence zero"] = UpdateKeys.Rotation(0, attackerAdd, ""),
            ["empty record"] = UpdateKeys.Rotation(1, "", ""),
            ["revokes every key"] = UpdateKeys.Rotation(1, "", "  - root-test\n  - rel-1\n"),
            ["looks like a third-party id"] = UpdateKeys.Rotation(1, UpdateKeys.AddKey(UpdateKeys.Hex(Ed25519.PublicKey(UpdateKeys.AttackerSeed)), UpdateKeys.AttackerSeed), ""),
            ["unknown purpose"] = UpdateKeys.Rotation(1, UpdateKeys.AddKey("p", UpdateKeys.NextSeed, "root"), ""),
        };
        foreach (var (why, record) in forged)
        {
            // The attacker also signs the manifest with the key the forged rotation would add.
            var check = verifier.Check(UpdateKeys.Bundle(manifest, "evil", UpdateKeys.AttackerSeed, record), "1.1.0");
            Assert.True(check.Status == AppUpdateStatus.Failed && check.ErrorCode == "rotation-invalid", why + " -> " + Code(check));
            Assert.Null(check.Offer);
        }
        Assert.Empty(store.Chain);
        Assert.Null(store.Keyring.Find("evil"));
        Assert.Equal(0, store.HighSequence);

        static SignedBlob Retag(SignedBlob r) => new(r.Bytes, UpdateKeys.SignFile("root-test", UpdateKeys.RootSeed, UpdateSignature.ManifestDomain, r.Bytes));
    }

    [Fact]
    public void Editing_the_stored_trust_file_cannot_add_a_key_and_a_damaged_file_falls_back_to_the_embedded_roots()
    {
        var (verifier, store, path) = Rig();
        Assert.Equal(AppUpdateStatus.Available, verifier.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("1.2.0", 5, Zip), "rel-2", UpdateKeys.NextSeed,
            UpdateKeys.Rotation(1, UpdateKeys.AddKey("rel-2", UpdateKeys.NextSeed), "")), "1.1.0").Status);
        // Plant an unsigned rotation record in the file next to the valid one.
        var evil = UpdateKeys.Rotation(2, UpdateKeys.AddKey("evil", UpdateKeys.AttackerSeed, "release,rotate"), "", "evil", UpdateKeys.AttackerSeed);
        string json = File.ReadAllText(path).Replace("\"rotations\":[", $"\"rotations\":[{{\"record\":\"{Convert.ToBase64String(evil.Bytes)}\",\"signature\":\"{Convert.ToBase64String(evil.Signature)}\"}},");
        File.WriteAllText(path, json);
        var tampered = new UpdateTrustStore(path, UpdateKeys.Test());
        Assert.Null(tampered.Keyring.Find("evil"));
        Assert.Null(tampered.Keyring.Find("rel-2")); // a chain that does not verify is dropped whole
        File.WriteAllText(path, "{ not json");
        Assert.Equal(2, new UpdateTrustStore(path, UpdateKeys.Test()).Keyring.Keys.Count);
    }

    [Fact]
    public void A_rotation_in_an_unsigned_or_failed_response_never_replaces_the_root()
    {
        var (verifier, store, _) = Rig();
        // A valid rotation but a manifest whose signature fails: the rotation is not remembered either (all or nothing).
        var rotation = UpdateKeys.Rotation(1, UpdateKeys.AddKey("rel-2", UpdateKeys.NextSeed), "");
        var bad = UpdateKeys.Bundle(UpdateKeys.ManifestText("1.2.0", 5, Zip), "rel-2", UpdateKeys.AttackerSeed, rotation);
        Assert.Equal("signature-invalid", Code(verifier.Check(bad, "1.1.0")));
        Assert.Empty(store.Chain);
        Assert.Null(store.Keyring.Find("rel-2"));
    }

    // ---------- downgrade and replay ----------

    [Fact]
    public void A_replayed_older_signed_manifest_is_refused_and_versions_and_sequences_only_move_forward()
    {
        var (verifier, store, path) = Rig();
        var v1 = UpdateKeys.Bundle(UpdateKeys.ManifestText("1.1.0", 4, Zip));
        var v2 = UpdateKeys.Bundle(UpdateKeys.ManifestText("1.2.0", 5, Zip));
        Assert.Equal(AppUpdateStatus.Available, verifier.Check(v1, "1.0.0").Status);
        Assert.Equal(AppUpdateStatus.Available, verifier.Check(v2, "1.0.0").Status);
        Assert.Equal("rollback", Code(verifier.Check(v1, "1.0.0"))); // the older, genuinely signed manifest is replayed
        // The high-water mark survives a restart.
        var restarted = new UpdateVerifier(new UpdateTrustStore(path, UpdateKeys.Test()));
        Assert.Equal("rollback", Code(restarted.Check(v1, "1.0.0")));
        // A signed manifest that offers an older version than the installed one is a downgrade.
        Assert.Equal("rollback", Code(restarted.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("1.2.0", 6, Zip)), "1.3.0")));
        // Sequence goes forward but the version goes back: refused (version monotonicity).
        Assert.Equal("rollback", Code(restarted.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("1.1.5", 9, Zip)), "1.0.0")));
        // The same manifest again is fine (same sequence, same version).
        Assert.Equal(AppUpdateStatus.Available, restarted.Check(v2, "1.0.0").Status);
        Assert.Equal(5, store.HighSequence);
    }
}
