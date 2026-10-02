using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Susu.Abstractions;
using Susu.Plugins.AppUpdate;
using Susu.Plugins.Install;
using Susu.Storage;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// Independent F18 verification (testing agent): UPD05-UPD08, X01, X06, DATA03 and the DEV-PLAN F18 exit line, attacked where the coding agents' own tests stop.
/// Real Ed25519, zip, SQLite, file system, HTTP client types and (where the F18 tests already use it) the published susu.exe; temp folders are named
/// susu-f18v-*. A test that cannot run says why with Assert.Skip. Tests named Known_gap_* pin the CURRENT behaviour of a defect recorded in
/// docs/evidence/F18/F18.md (verification section) so that fixing it is a deliberate change. Not executed: a real NSIS compile, a clean Windows 11 user,
/// a real power cut, a real update of an installed running app.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class F18VerificationTests
{
    private static readonly byte[] Zip = Encoding.UTF8.GetBytes("pretend package bytes for the verification tests");

    private static (UpdateVerifier Verifier, UpdateTrustStore Store, string Path) TrustRig(UpdateKeyring? roots = null)
    {
        string path = Path.Combine(TestTemp.NewDir("susu-f18v-trust"), "trust.json");
        var store = new UpdateTrustStore(path, roots ?? UpdateKeys.Test());
        return (new UpdateVerifier(store), store, path);
    }

    private static string Code(AppUpdateCheck check) => check.Status == AppUpdateStatus.Failed ? check.ErrorCode! : check.Status.ToString();

    private static void AssertRefused(AppUpdateCheck check, string why)
    {
        Assert.True(check.Status == AppUpdateStatus.Failed && check.Offer is null, why + " -> " + Code(check));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "build-installer.ps1"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    // ================= signed manifest: byte-level attacks =================

    [Fact] // UPD05: every byte, flipped, deleted or duplicated, is refused as a signature failure: nothing was parsed, nothing remembered
    public void Every_byte_of_a_signed_manifest_changed_four_ways_is_refused_before_any_parsing()
    {
        var (verifier, store, path) = TrustRig();
        var good = UpdateKeys.Bundle("schema: 1\nproduct: susu\nchannel: stable\nversion: 1.2.0\nsequence: 5\npackage:\n  file: susu-1.2.0.zip\n  size: 10\n  sha256: " + new string('a', 64) + "\n");
        int checks = 0;
        for (int i = 0; i < good.Manifest.Length; i++)
        {
            var variants = new List<byte[]>();
            var flipLow = (byte[])good.Manifest.Clone(); flipLow[i] ^= 0x01; variants.Add(flipLow);
            var flipAll = (byte[])good.Manifest.Clone(); flipAll[i] ^= 0xFF; variants.Add(flipAll); // mostly invalid UTF-8: a parser would choke, the verifier must not get that far
            variants.Add([.. good.Manifest[..i], .. good.Manifest[(i + 1)..]]);
            variants.Add([.. good.Manifest[..(i + 1)], .. good.Manifest[i..]]);
            foreach (var bytes in variants)
            {
                var check = verifier.Check(good with { Manifest = bytes }, "1.1.0");
                Assert.True(Code(check) == "signature-invalid", $"byte {i}: {Code(check)}");
                checks++;
            }
        }
        Assert.True(checks > 600);
        Assert.False(File.Exists(path)); // not even the trust file was written
        Assert.Equal(0, store.HighSequence);
        Assert.Equal(AppUpdateStatus.Available, verifier.Check(good, "1.1.0").Status); // control: the untouched bundle verifies (a refusal above is not a broken rig)
    }

    [Fact] // UPD05: the signature file is checked as exact bytes: every byte changed, deleted or duplicated, plus line-ending, BOM, case and base64 variants
    public void Every_byte_of_the_signature_file_and_every_normalisation_variant_of_it_is_refused()
    {
        var (verifier, store, _) = TrustRig();
        var good = UpdateKeys.Bundle(UpdateKeys.ManifestText("1.2.0", 5, Zip));
        byte[] sig = good.Signature;
        for (int i = 0; i < sig.Length; i++)
        {
            foreach (var bytes in new[] { Flip(sig, i, 0xFF), Flip(sig, i, 0x20), [.. sig[..i], .. sig[(i + 1)..]], [.. sig[..(i + 1)], .. sig[i..]] })
                AssertRefused(verifier.Check(good with { Signature = bytes }, "1.1.0"), $"signature byte {i}");
        }
        string text = Encoding.UTF8.GetString(sig);
        string b64 = text.Split('\n')[2]["signature: ".Length..];
        string urlSafe = b64.Replace('+', '-').Replace('/', '_');
        // The last base64 character before the padding carries two unused bits: another character decodes to the SAME 64 bytes, so only the canonical check refuses it.
        int lastIndex = b64.IndexOf('=') - 1;
        char alt = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"[("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/".IndexOf(b64[lastIndex]) ^ 1)];
        string sameBytesOtherText = b64[..lastIndex] + alt + b64[(lastIndex + 1)..];
        Assert.Equal(Convert.FromBase64String(b64), Convert.FromBase64String(sameBytesOtherText));
        var variants = new Dictionary<string, byte[]>
        {
            ["CRLF"] = Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n")),
            ["BOM"] = [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. sig],
            ["trailing blank line"] = Encoding.UTF8.GetBytes(text + "\n"),
            ["no final newline"] = sig[..^1],
            ["trailing space"] = Encoding.UTF8.GetBytes(text.Replace("\nalgorithm", " \nalgorithm")),
            ["tab"] = Encoding.UTF8.GetBytes(text.Replace("key-id: ", "key-id:\t")),
            ["upper-case algorithm"] = Encoding.UTF8.GetBytes(text.Replace("ed25519", "ED25519")),
            ["upper-case key id"] = Encoding.UTF8.GetBytes(text.Replace("rel-1", "REL-1")),
            ["url-safe base64"] = Encoding.UTF8.GetBytes(text.Replace(b64, urlSafe)),
            ["unpadded base64"] = Encoding.UTF8.GetBytes(text.Replace(b64, b64.TrimEnd('='))),
            ["base64 with a space"] = Encoding.UTF8.GetBytes(text.Replace(b64, b64[..10] + " " + b64[10..])),
            ["same bytes, other last base64 character"] = Encoding.UTF8.GetBytes(text.Replace(b64, sameBytesOtherText)),
            ["lines reordered"] = Encoding.UTF8.GetBytes(string.Join('\n', text.TrimEnd('\n').Split('\n').Reverse()) + "\n"),
            ["a second signature line"] = Encoding.UTF8.GetBytes(text + text),
            ["algorithm line twice"] = Encoding.UTF8.GetBytes(text.Replace("algorithm: ed25519\n", "algorithm: ed25519\nalgorithm: ed25519\n")),
            ["NUL at the end"] = [.. sig, 0],
            ["key id with a path"] = Encoding.UTF8.GetBytes(text.Replace("rel-1", "..\\rel-1")),
            ["oversize file"] = new byte[UpdateSignature.MaxSignatureFileBytes + 1],
        };
        foreach (var (why, bytes) in variants) AssertRefused(verifier.Check(good with { Signature = bytes }, "1.1.0"), why);
        Assert.Equal(0, store.HighSequence);
        Assert.Equal(AppUpdateStatus.Available, verifier.Check(good, "1.1.0").Status);

        static byte[] Flip(byte[] source, int index, byte mask) { var copy = (byte[])source.Clone(); copy[index] ^= mask; return copy; }
    }

    [Fact] // UPD05: signature text normalisation tricks on the MANIFEST: any variant of the signed bytes is refused; a signed odd variant never yields a different offer
    public void Trailing_data_BOM_CRLF_and_look_alike_characters_never_verify_and_a_signed_odd_manifest_never_changes_the_offer()
    {
        var (verifier, _, _) = TrustRig();
        string text = UpdateKeys.ManifestText("1.2.0", 5, Zip);
        var good = UpdateKeys.Bundle(text);
        var unsignedVariants = new List<(string, byte[])>
        {
            ("NUL appended", [.. good.Manifest, 0]),
            ("spaces appended", [.. good.Manifest, .. "   "u8.ToArray()]),
            ("--- document end appended", [.. good.Manifest, .. "---\nschema: 9\n"u8.ToArray()]),
            ("... end marker", [.. good.Manifest, .. "...\n"u8.ToArray()]),
            ("second manifest appended", [.. good.Manifest, .. Encoding.UTF8.GetBytes(UpdateKeys.ManifestText("9.9.9", 99, Zip))]),
            ("CRLF", Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n"))),
            ("BOM", [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. good.Manifest]),
            ("UTF-16", Encoding.Unicode.GetBytes(text)),
            ("Cyrillic e in a key", Encoding.UTF8.GetBytes(text.Replace("sequence", "sequеnce"))),
            ("zero-width space in the version", Encoding.UTF8.GetBytes(text.Replace("1.2.0", "1.2.​0"))),
            ("RTL override in the notes", Encoding.UTF8.GetBytes(text.Replace("Fixes", "‮Fixes"))),
        };
        foreach (var (why, bytes) in unsignedVariants) AssertRefused(verifier.Check(good with { Manifest = bytes }, "1.1.0"), why);

        // Signed oddities: the signer signed these bytes, so a verdict is allowed, but an accepted one must be exactly the intended offer.
        var expected = verifier.Check(good, "1.1.0").Offer!;
        foreach (var (why, bytes) in unsignedVariants.Where(v => v.Item1 is "CRLF" or "BOM" or "spaces appended" or "NUL appended" or "UTF-16" or "second manifest appended"))
        {
            var (v2, _, _) = TrustRig();
            var raw = bytes;
            var signed = new UpdateBundle(raw, UpdateKeys.SignFile("rel-1", UpdateKeys.ReleaseSeed, UpdateSignature.ManifestDomain, raw), []);
            var result = v2.Check(signed, "1.1.0");
            if (result.Status == AppUpdateStatus.Available) Assert.Equal(expected, result.Offer);
            else Assert.True(result.Status == AppUpdateStatus.Failed, why);
        }
    }

    [Fact] // UPD05: a valid signature does not make the content trustworthy: nested duplicate keys, anchors, tags, multi-document and typed oddities are refused
    public void A_validly_signed_manifest_with_hostile_yaml_or_values_is_refused_and_nothing_is_remembered()
    {
        string ok = UpdateKeys.ManifestText("1.2.0", 5, Zip);
        string sha = Convert.ToHexStringLower(SHA256.HashData(Zip));
        var bad = new Dictionary<string, string>
        {
            ["duplicate nested key"] = ok.Replace("  size:", "  size: 5\n  size:"),
            ["duplicate top key, different value first"] = "sequence: 1\n" + ok,
            ["anchor and merge key"] = ok.Replace("package:", "base: &b\n  size: 1\npackage:\n  <<: *b"),
            ["billion laughs"] = "a: &a [\"x\",\"x\"]\nb: &b [*a,*a]\nc: &c [*b,*b]\n" + ok,
            ["tag"] = ok.Replace("version: 1.2.0", "version: !!str 1.2.0"),
            ["python object tag"] = ok.Replace("notes:", "notes: !!python/object/apply:os.system [calc]\nx:"),
            ["second document"] = ok + "---\n" + ok,
            ["version as a list"] = ok.Replace("version: 1.2.0", "version: [1, 2, 0]"),
            ["package as a scalar"] = ok.Replace("package:\n  file: susu-1.2.0.zip\n  size: " + Zip.Length + "\n  sha256: " + sha, "package: nothing"),
            ["notes with an escaped NUL"] = ok.Replace("\"Fixes and polish\"", "\"Fixes\\u0000 and polish\""),
            ["notes 2001 characters"] = ok.Replace("Fixes and polish", new string('n', 2001)),
            ["upper-case hash"] = ok.Replace(sha, sha.ToUpperInvariant()),
            ["hash with trailing space inside quotes"] = ok.Replace(sha, "\"" + sha + " \""),
            ["size in exponent form"] = ok.Replace("size: " + Zip.Length, "size: 1e3"),
            ["size with a plus"] = ok.Replace("size: " + Zip.Length, "size: +" + Zip.Length),
            ["size hex"] = ok.Replace("size: " + Zip.Length, "size: 0x20"),
            ["size Arabic-Indic digits"] = ok.Replace("size: " + Zip.Length, "size: ٥٦"),
            ["sequence Arabic-Indic digits"] = ok.Replace("sequence: 5", "sequence: ٥"),
            ["sequence one past long.MaxValue"] = ok.Replace("sequence: 5", "sequence: 9223372036854775808"),
            ["sequence with a decimal"] = ok.Replace("sequence: 5", "sequence: 5.0"),
            ["sequence zero"] = ok.Replace("sequence: 5", "sequence: 0"),
            ["version with a prerelease"] = ok.Replace("version: 1.2.0", "version: 1.2.0-beta"),
            ["version with a leading zero"] = ok.Replace("version: 1.2.0", "version: 01.2.0"),
            ["version component too large"] = ok.Replace("version: 1.2.0", "version: 1.2.9999999"),
            ["version with a fourth part"] = ok.Replace("version: 1.2.0", "version: 1.2.0.1"),
            ["file name with a drive"] = ok.Replace("susu-1.2.0.zip", "C:evil.zip"),
            ["file name with an alternate stream"] = ok.Replace("susu-1.2.0.zip", "a.zip:s.zip"),
            ["file name with a space"] = ok.Replace("susu-1.2.0.zip", "\"a b.zip\""),
            ["file name hidden file"] = ok.Replace("susu-1.2.0.zip", ".zip.zip"),
            ["file name upper-case extension"] = ok.Replace("susu-1.2.0.zip", "susu.ZIP"),
            ["file name 81 characters"] = ok.Replace("susu-1.2.0.zip", new string('a', 77) + ".zip"),
            ["unknown nested key"] = ok.Replace("  size:", "  mirror: https://evil.example/x.zip\n  size:"),
            ["tab indentation"] = ok.Replace("  size:", "\tsize:"),
            ["flow mapping package with a url"] = ok.Replace(ok[ok.IndexOf("package:", StringComparison.Ordinal)..ok.IndexOf("notes:", StringComparison.Ordinal)], "package: {file: susu-1.2.0.zip, size: 5, sha256: " + sha + ", url: https://evil.example/}\n"),
        };
        var (verifier, store, path) = TrustRig();
        foreach (var (why, text) in bad)
        {
            var check = verifier.Check(UpdateKeys.Bundle(text), "1.1.0");
            Assert.True(check.Status == AppUpdateStatus.Failed && check.Offer is null, why + " -> " + Code(check) + " " + check.Offer);
        }
        Assert.False(File.Exists(path));
        Assert.Equal(0, store.HighSequence);
        Assert.Equal(AppUpdateStatus.Available, verifier.Check(UpdateKeys.Bundle(ok), "1.1.0").Status);
    }

    [Fact] // UPD05: the manifest cap is exact, and a parser bomb at the cap is cheap whether or not it is signed
    public void The_manifest_size_cap_is_exact_and_parser_bombs_at_the_cap_are_refused_quickly()
    {
        var (verifier, store, _) = TrustRig();
        string ok = UpdateKeys.ManifestText("1.2.0", 5, Zip);
        // Exactly at the cap with comment padding (valid YAML): verifies. One byte more: refused by size before anything else.
        string padded = ok + "#" + new string('p', UpdateVerifier.MaxManifestBytes - ok.Length - 2) + "\n";
        Assert.Equal(UpdateVerifier.MaxManifestBytes, Encoding.UTF8.GetByteCount(padded));
        Assert.Equal(AppUpdateStatus.Available, verifier.Check(UpdateKeys.Bundle(padded), "1.1.0").Status);
        Assert.Equal("manifest-too-large", Code(verifier.Check(UpdateKeys.Bundle(padded + "\n"), "1.1.0")));

        var clock = Stopwatch.StartNew();
        string[] bombs =
        [
            "notes: " + new string('[', 150) + "\n",
            "notes: " + new string('{', 150) + "\n",
            string.Concat(Enumerable.Range(0, 4000).Select(i => new string(' ', Math.Min(i, 120)) + "k" + i + ":\n")),
            string.Concat(Enumerable.Range(0, 1500).Select(i => $"a{i}: &a{i} [{string.Join(",", Enumerable.Range(0, 8).Select(_ => "*a" + Math.Max(0, i - 1)))}]\n")),
            "notes: \"" + new string('\\', 60000) + "\"\n",
            string.Concat(Enumerable.Repeat("- ", 30000)),
        ];
        foreach (string bomb in bombs)
        {
            string text = ok + bomb;
            if (Encoding.UTF8.GetByteCount(text) > UpdateVerifier.MaxManifestBytes) text = text[..(UpdateVerifier.MaxManifestBytes - 1)];
            // unsigned bytes: never parsed
            byte[] raw = Encoding.UTF8.GetBytes(text);
            AssertRefused(verifier.Check(new UpdateBundle(raw, UpdateKeys.Bundle(ok).Signature, []), "1.1.0"), "unsigned bomb");
            // signed bytes: parsed, must be refused in bounded time and without a crash
            AssertRefused(verifier.Check(UpdateKeys.Bundle(text), "1.1.0"), "signed bomb");
        }
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), clock.Elapsed.ToString());
        Assert.Equal(5, store.HighSequence); // only the one padded, valid manifest was remembered
    }

    private const string BombChildVariable = "SUSU_F18V_BOMB_CHILD";

    [Fact] // UPD05: Known_gap (DEFECT D1): deeply nested flow YAML in a SIGNED manifest overflows the parser's stack, which ends the process (not catchable)
    public async Task Known_gap_deeply_nested_flow_yaml_in_a_signed_manifest_ends_the_process_with_a_stack_overflow()
    {
        string manifest = UpdateKeys.ManifestText("1.2.0", 5, Zip) + "notes2: " + new string('[', 30000) + "\n";
        if (Environment.GetEnvironmentVariable(BombChildVariable) == "1")
        {
            var (verifier, _, _) = TrustRig();
            verifier.Check(UpdateKeys.Bundle(manifest), "1.1.0"); // the child is expected to die here
            return;
        }
        string exe = Path.Combine(AppContext.BaseDirectory, "Susu.Tests.Unit.exe");
        if (!File.Exists(exe)) Assert.Skip("The test host executable is not beside the test assembly, so the crash cannot be isolated in a child process.");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-method");
        info.ArgumentList.Add("Susu.Tests.Unit.F18VerificationTests.Known_gap_deeply_nested_flow_yaml_in_a_signed_manifest_ends_the_process_with_a_stack_overflow");
        info.Environment[BombChildVariable] = "1";
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.True(process.WaitForExit(120_000));
        // Pinned current behaviour: 0xC00000FD (STATUS_STACK_OVERFLOW). When the parser gets a depth limit this becomes exit 0 and the test must be rewritten to expect a refusal.
        Assert.True(process.ExitCode == unchecked((int)0xC00000FD), $"exit {process.ExitCode}: {await stdout}{await stderr}");
    }

    // ================= sequence and version edge cases =================

    [Fact] // UPD05: monotonicity at the edges (equal, maximum, ordering by number not by text, downgrade with a higher sequence)
    public void Sequence_and_version_edges_move_only_forward_and_compare_numbers_not_text()
    {
        var (verifier, store, path) = TrustRig();
        AppUpdateCheck Offer(string version, long sequence, string installed = "1.0.0") => verifier.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText(version, sequence, Zip)), installed);
        Assert.Equal(AppUpdateStatus.Available, Offer("1.9.0", 10).Status);
        Assert.Equal(AppUpdateStatus.Available, Offer("1.10.0", 11).Status); // 1.10.0 is newer than 1.9.0
        Assert.Equal("rollback", Code(Offer("1.9.0", 12))); // text order would call 1.9.0 newer; the number order refuses it even with a higher sequence
        Assert.Equal("rollback", Code(Offer("1.10.0", 10))); // same version, older sequence
        Assert.Equal(AppUpdateStatus.Available, Offer("1.10.0", 11).Status); // equal sequence and equal version is the same manifest again
        Assert.Equal(AppUpdateStatus.Available, Offer("1.10.1", 11).Status); // equal sequence, higher version: allowed (the signer signed it)
        Assert.Equal(AppUpdateStatus.Available, Offer("1.10.1", 13).Status); // higher sequence, equal version
        Assert.Equal("rollback", Code(Offer("0.9.9", long.MaxValue))); // a huge sequence does not make a downgrade acceptable
        Assert.Equal("rollback", Code(Offer("1.10.1", 5, installed: "2.0.0"))); // older than installed
        Assert.Equal(AppUpdateStatus.UpToDate, Offer("1.10.1", 14, installed: "1.10.1").Status);
        Assert.Equal(14, store.HighSequence);
        // Persisted high-water marks survive a restart and are not lowered by a manifest that is only "up to date".
        var restarted = new UpdateVerifier(new UpdateTrustStore(path, UpdateKeys.Test()));
        Assert.Equal("rollback", Code(restarted.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("1.10.1", 13, Zip)), "1.0.0")));
    }

    [Fact] // UPD05: Known_gap: a signed manifest with sequence long.MaxValue is accepted and then no later release can ever be accepted (recorded as a low defect)
    public void Known_gap_a_signed_maximum_sequence_locks_out_every_later_release_until_the_trust_file_is_removed()
    {
        var (verifier, store, path) = TrustRig();
        Assert.Equal(AppUpdateStatus.Available, verifier.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("1.2.0", long.MaxValue, Zip)), "1.1.0").Status);
        Assert.Equal(long.MaxValue, store.HighSequence);
        Assert.Equal("rollback", Code(verifier.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("1.3.0", long.MaxValue - 1, Zip)), "1.1.0")));
        Assert.Equal("rollback", Code(verifier.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("9.0.0", 7, Zip)), "1.1.0")));
        File.Delete(path); // the documented way out: the marks are the user's own and unsigned
        Assert.Equal(AppUpdateStatus.Available, new UpdateVerifier(new UpdateTrustStore(path, UpdateKeys.Test())).Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("9.0.0", 7, Zip)), "1.1.0").Status);
    }

    [Fact] // UPD05: Known_gap: reserved device names are accepted as the package file name (a download would be written to a device path on some Windows builds)
    public void Known_gap_reserved_device_names_pass_the_package_file_name_rule()
    {
        foreach (string name in new[] { "NUL.zip", "con.zip", "COM1.zip", "aux.zip", "lpt9.zip" })
            Assert.True(UpdateVerifier.IsSafeFileName(name), name); // pinned: see the defect list; the staging step still refuses what is not a real file (next test)
    }

    // ================= key rotation =================

    private static string AddRotate(string id, byte[] seed) => UpdateKeys.AddKey(id, seed, "release,rotate");

    [Fact] // UPD05: a revoked key cannot sign a rotation link, whether the links arrive together or one by one, and a rotation chain cannot skip, reorder or be spliced
    public void A_rotation_chain_with_a_link_signed_by_a_revoked_key_is_refused_and_chains_cannot_be_reordered_or_spliced()
    {
        string manifest = UpdateKeys.ManifestText("1.2.0", 5, Zip);
        // link 1 hands the rotation right to "mid" and revokes the root; link 2 signed by the (now revoked) root is a forgery.
        var link1 = UpdateKeys.Rotation(1, AddRotate("mid", UpdateKeys.NextSeed), revoke: "  - root-test\n");
        var link2ByRoot = UpdateKeys.Rotation(2, UpdateKeys.AddKey("late", UpdateKeys.AttackerSeed), "", "root-test", UpdateKeys.RootSeed);
        var link2ByMid = UpdateKeys.Rotation(2, UpdateKeys.AddKey("late", UpdateKeys.AttackerSeed), "", "mid", UpdateKeys.NextSeed);

        var (together, storeTogether, _) = TrustRig();
        var bad = together.Check(UpdateKeys.Bundle(manifest, "late", UpdateKeys.AttackerSeed, link1, link2ByRoot), "1.1.0");
        Assert.True(Code(bad) == "rotation-invalid", Code(bad));
        Assert.Empty(storeTogether.Chain); // all or nothing: even the valid link 1 is not remembered when the batch fails

        var (oneByOne, storeOne, _) = TrustRig();
        Assert.Equal(AppUpdateStatus.Available, oneByOne.Check(UpdateKeys.Bundle(manifest, "mid", UpdateKeys.NextSeed, link1), "1.1.0").Status);
        Assert.Equal("rotation-invalid", Code(oneByOne.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("1.3.0", 6, Zip), "late", UpdateKeys.AttackerSeed, link2ByRoot), "1.1.0")));
        Assert.Null(storeOne.Keyring.Find("late"));
        Assert.Null(storeOne.Keyring.Find("root-test")); // the revocation stuck
        Assert.Equal(AppUpdateStatus.Available, oneByOne.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("1.3.0", 6, Zip), "late", UpdateKeys.AttackerSeed, link2ByMid), "1.1.0").Status);

        // Order and splicing: [2, 1], [2] alone on an empty chain, a record repeated with its own number, and a link numbered for a later slot.
        foreach (var (why, links) in new (string, SignedBlob[])[]
        {
            ("reversed", [link2ByMid, link1]),
            ("link 2 without link 1", [link2ByMid]),
            ("link 1 twice", [link1, link1]),
            ("link 3 in slot 2", [link1, UpdateKeys.Rotation(3, UpdateKeys.AddKey("late", UpdateKeys.AttackerSeed), "", "mid", UpdateKeys.NextSeed)]),
        })
        {
            var (v, s, _) = TrustRig();
            var check = v.Check(UpdateKeys.Bundle(manifest, "late", UpdateKeys.AttackerSeed, links), "1.1.0");
            Assert.True(check.Status == AppUpdateStatus.Failed && check.Offer is null, why + " -> " + Code(check));
            Assert.Empty(s.Chain);
        }
    }

    [Fact] // UPD05: after a rotation, a genuinely signed OLD manifest from the old (revoked) key is refused, and so is anything replayed once the new key has moved the marks
    public void Rotating_to_a_new_key_then_replaying_an_older_manifest_of_the_old_key_is_refused_both_ways()
    {
        var (verifier, store, path) = TrustRig();
        var oldManifest = UpdateKeys.Bundle(UpdateKeys.ManifestText("1.1.0", 4, Zip)); // signed by rel-1 in the past
        var rotation = UpdateKeys.Rotation(1, UpdateKeys.AddKey("rel-2", UpdateKeys.NextSeed), revoke: "  - rel-1\n");
        Assert.Equal(AppUpdateStatus.Available, verifier.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("1.2.0", 5, Zip), "rel-2", UpdateKeys.NextSeed, rotation), "1.0.0").Status);
        Assert.Equal("unknown-key", Code(verifier.Check(oldManifest, "1.0.0"))); // old key: gone
        // Even the same old bytes with the rotation attached do not bring the key back.
        Assert.Equal("unknown-key", Code(verifier.Check(oldManifest with { Rotations = [rotation] }, "1.0.0")));
        // A new-key manifest with an old sequence is a replay too.
        Assert.Equal("rollback", Code(verifier.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("1.1.0", 4, Zip), "rel-2", UpdateKeys.NextSeed), "1.0.0")));
        // A fresh install (no trust file) that only knows the embedded roots would accept the old key again: the replay window is bounded by the trust file, which is
        // why the marks live in the user's profile; pinned here so the behaviour is explicit.
        var fresh = new UpdateVerifier(new UpdateTrustStore(Path.Combine(TestTemp.NewDir("susu-f18v-fresh"), "trust.json"), UpdateKeys.Test()));
        Assert.Equal(AppUpdateStatus.Available, fresh.Check(oldManifest, "1.0.0").Status);
        Assert.Equal(5, store.HighSequence);
        Assert.True(File.Exists(path));
    }

    [Fact] // UPD05: the record schema has no expiry, key-id reuse or purpose widening hole
    public void Rotation_records_with_expiry_fields_unknown_fields_or_a_reused_id_for_a_different_key_are_handled_strictly()
    {
        string manifest = UpdateKeys.ManifestText("1.2.0", 5, Zip);
        var (verifier, store, _) = TrustRig();
        // An "expires" field is not part of the schema: a record that carries one is refused rather than silently ignored.
        string text = $"schema: 1\nsequence: 1\nexpires: 2020-01-01\nadd:\n{UpdateKeys.AddKey("rel-2", UpdateKeys.NextSeed)}";
        byte[] raw = Encoding.UTF8.GetBytes(text);
        var withExpiry = new SignedBlob(raw, UpdateKeys.SignFile("root-test", UpdateKeys.RootSeed, UpdateSignature.KeyringDomain, raw));
        Assert.Equal("rotation-invalid", Code(verifier.Check(UpdateKeys.Bundle(manifest, "rel-2", UpdateKeys.NextSeed, withExpiry), "1.1.0")));
        // A record that re-adds an existing id with a different public key replaces the key (signed by the root: allowed), and the old private key stops working.
        var replace = UpdateKeys.Rotation(1, UpdateKeys.AddKey("rel-1", UpdateKeys.NextSeed), "");
        Assert.Equal(AppUpdateStatus.Available, verifier.Check(UpdateKeys.Bundle(manifest, "rel-1", UpdateKeys.NextSeed, replace), "1.1.0").Status);
        Assert.Equal("signature-invalid", Code(verifier.Check(UpdateKeys.Bundle(UpdateKeys.ManifestText("1.3.0", 6, Zip), "rel-1", UpdateKeys.ReleaseSeed), "1.1.0")));
        Assert.Single(store.Chain);
        // A release-only key cannot be widened by a record signed by itself.
        var (v2, s2, _) = TrustRig();
        var selfWiden = UpdateKeys.Rotation(1, AddRotate("rel-1", UpdateKeys.ReleaseSeed), "", "rel-1", UpdateKeys.ReleaseSeed);
        Assert.Equal("rotation-invalid", Code(v2.Check(UpdateKeys.Bundle(manifest, "rel-1", UpdateKeys.ReleaseSeed, selfWiden), "1.1.0")));
        Assert.Empty(s2.Chain);
    }
}
