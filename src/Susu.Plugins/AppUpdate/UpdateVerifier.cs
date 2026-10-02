using System.Globalization;
using System.Text;
using Susu.Abstractions;
using Susu.Plugins.Install;
using Susu.Storage;

namespace Susu.Plugins.AppUpdate;

/// <summary>What a source fetched: the manifest's raw bytes, its detached signature file, and any key rotation records (in order) the source holds.</summary>
public sealed record UpdateBundle(byte[] Manifest, byte[] Signature, IReadOnlyList<SignedBlob> Rotations);

/// <summary>
/// Verifies an application update manifest (ARCHITECTURE 10, TEST-PLAN UPD05). Order, which the tests pin: size limits, then key rotation records, then the
/// signature over the exact raw bytes (domain-tagged), and only then the YAML is parsed and checked. Nothing is read from an unverified byte. After a
/// valid signature the manifest must be for this product and channel, must not be older than anything already verified (sequence and version only
/// move forward: a replayed old manifest is refused), and must not be older than the installed version. Error codes are stable keys for the page.
/// </summary>
public sealed class UpdateVerifier(UpdateTrustStore store, string product = "susu", string channel = "stable")
{
    public const int MaxManifestBytes = 64 * 1024, MaxRotations = 16;
    public const long MaxPackageBytes = 1L << 30;
    private static readonly string[] topKeys = ["schema", "product", "channel", "version", "sequence", "published", "package", "notes"];

    public AppUpdateCheck Check(UpdateBundle bundle, string currentVersion)
    {
        if (!PackageVersion.TryParse(currentVersion, out var current)) return AppUpdateCheck.Failed("current-version-invalid");
        if (store.Roots.IsEmpty) return AppUpdateCheck.Failed("no-trusted-keys"); // an unsigned build has no key slot filled: nothing can verify
        if (bundle.Manifest.Length is 0 or > MaxManifestBytes) return AppUpdateCheck.Failed("manifest-too-large");
        if (bundle.Rotations.Count > MaxRotations) return AppUpdateCheck.Failed("rotation-invalid");

        // 1. Rotation records: each must be signed by a key the chain so far trusts. Known (already accepted) records must match what the store holds.
        var keyring = store.Keyring;
        var chain = new List<SignedBlob>(store.Chain);
        foreach (var record in bundle.Rotations)
        {
            int known = chain.Count;
            int index = chain.FindIndex(b => b.Bytes.AsSpan().SequenceEqual(record.Bytes));
            if (index >= 0) continue; // already part of the accepted chain
            var (next, error) = KeyRotation.Apply(keyring, known + 1, record);
            if (next is null) return AppUpdateCheck.Failed(error ?? "rotation-invalid");
            keyring = next;
            chain.Add(record);
        }

        // 2. The signature over the raw bytes, before any parsing.
        if (!UpdateSignature.TryParse(bundle.Signature, out string keyId, out byte[] signature)) return AppUpdateCheck.Failed("signature-malformed");
        var key = keyring.Find(keyId);
        if (key is null) return AppUpdateCheck.Failed("unknown-key");
        if (!key.CanRelease) return AppUpdateCheck.Failed("key-not-allowed");
        if (!Ed25519.Verify(key.PublicKey, UpdateSignature.Signed(UpdateSignature.ManifestDomain, bundle.Manifest), signature)) return AppUpdateCheck.Failed("signature-invalid");

        // 3. Only now parse.
        var parsed = Parse(bundle.Manifest);
        if (parsed.Error is not null) return AppUpdateCheck.Failed(parsed.Error);
        var (offer, version) = (parsed.Offer!, parsed.Version);

        // 4. Anti-rollback: monotonic sequence and version, never older than what is installed.
        if (offer.Sequence < store.HighSequence || version < store.HighVersion || version < current) return AppUpdateCheck.Failed("rollback");

        // Everything verified: remember the rotations and the high-water marks (one atomic write), then answer.
        try { store.Commit(chain, keyring, offer.Sequence, version); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return AppUpdateCheck.Failed("state-write-failed"); }
        return version > current ? AppUpdateCheck.Available(offer) : AppUpdateCheck.UpToDate();
    }

    private (AppUpdateOffer? Offer, PackageVersion Version, string? Error) Parse(byte[] raw)
    {
        const string bad = "manifest-invalid";
        string text;
        try { text = new UTF8Encoding(false, true).GetString(raw); } catch (ArgumentException) { return (null, default, bad); }
        var (root, issues) = YamlSubset.Parse(text);
        if (issues.Count > 0 || root is not YMap map) return (null, default, bad);
        foreach (var entry in map.Entries) if (!topKeys.Contains(entry.Key)) return (null, default, bad);
        if (S(map, "schema") != "1") return (null, default, "manifest-schema-unsupported");
        if (S(map, "product") != product || S(map, "channel") != channel) return (null, default, "manifest-wrong-product");
        if (!PackageVersion.TryParse(S(map, "version"), out var version)) return (null, default, bad);
        if (!long.TryParse(S(map, "sequence"), NumberStyles.None, CultureInfo.InvariantCulture, out long sequence) || sequence <= 0) return (null, default, bad);
        if (map.Get("package") is not YMap package) return (null, default, bad);
        foreach (var entry in package.Entries) if (entry.Key is not ("file" or "size" or "sha256")) return (null, default, bad);
        string file = S(package, "file") ?? "", hash = S(package, "sha256") ?? "";
        if (!IsSafeFileName(file) || hash.Length != 64 || !hash.All(char.IsAsciiHexDigitLower)) return (null, default, bad);
        if (!long.TryParse(S(package, "size"), NumberStyles.None, CultureInfo.InvariantCulture, out long size) || size is <= 0 or > MaxPackageBytes) return (null, default, bad);
        string notes = S(map, "notes") ?? "";
        if (notes.Length > 2000 || notes.Any(c => char.IsControl(c) && c is not ('\n' or '\t'))) return (null, default, bad);
        return (new AppUpdateOffer(version.ToString(), sequence, file, size, hash, notes), version, null);
    }

    /// <summary>The package file name is a bare name next to the manifest: the manifest cannot point the download anywhere else.</summary>
    public static bool IsSafeFileName(string name)
        => name.Length is >= 5 and <= 80 && name.EndsWith(".zip", StringComparison.Ordinal) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') && !name.StartsWith('.') && !name.Contains("..", StringComparison.Ordinal);

    private static string? S(YMap map, string key) => map.Get(key) is YScalar s ? s.Value : null;
}
