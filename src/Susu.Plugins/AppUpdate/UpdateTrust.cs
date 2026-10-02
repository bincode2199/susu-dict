using System.Globalization;
using System.Text;
using Susu.Plugins.Install;
using Susu.Storage;

namespace Susu.Plugins.AppUpdate;

/// <summary>One Ed25519 public key the updater trusts. Purposes: CanRelease signs update manifests, CanRotate signs key rotation records.</summary>
public sealed record UpdateKey(string Id, byte[] PublicKey, bool CanRelease, bool CanRotate)
{
    public static bool IsValidId(string id) => id.Length is >= 1 and <= 32 && char.IsAsciiLetterOrDigit(id[0]) && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}

/// <summary>
/// The set of keys trusted right now (ARCHITECTURE 10). The embedded roots come from the build (<see cref="Production"/> is the production slot: it is
/// EMPTY in this repository because no release key has been generated, so an unsigned build cannot verify any update and says so). Rotation records
/// derive a new keyring from a current one; they never come from an unsigned response.
/// </summary>
public sealed class UpdateKeyring
{
    private readonly Dictionary<string, UpdateKey> keys;

    /// <summary>"production-slot-empty" for the unsigned build, "test" for a test keyring, or the build's own label.</summary>
    public string Marker { get; }

    public UpdateKeyring(IEnumerable<UpdateKey> entries, string marker)
    {
        keys = new Dictionary<string, UpdateKey>(StringComparer.Ordinal);
        foreach (var key in entries)
        {
            if (!UpdateKey.IsValidId(key.Id) || key.PublicKey.Length != Ed25519.PublicKeyBytes) throw new ArgumentException("invalid update key");
            keys[key.Id] = key;
        }
        Marker = marker;
    }

    /// <summary>The production slot. Empty and marked: nothing is invented here. A release build embeds the offline root's public key and the keys it signs.</summary>
    public static UpdateKeyring Production { get; } = new([], "production-slot-empty");

    public bool IsEmpty => keys.Count == 0;

    public IReadOnlyCollection<UpdateKey> Keys => keys.Values;

    public UpdateKey? Find(string id) => keys.GetValueOrDefault(id);

    internal UpdateKeyring With(IEnumerable<UpdateKey> add, IEnumerable<string> revoke)
    {
        var next = new Dictionary<string, UpdateKey>(keys, StringComparer.Ordinal);
        foreach (string id in revoke) next.Remove(id);
        foreach (var key in add) next[key.Id] = key;
        return new UpdateKeyring(next.Values, Marker);
    }
}

public sealed record SignedBlob(byte[] Bytes, byte[] Signature);

/// <summary>The detached signature file: three lines (key-id, algorithm, signature as base64). It is not YAML, so nothing signed is parsed before it verifies.</summary>
public static class UpdateSignature
{
    public const string ManifestDomain = "susu-update-v1", KeyringDomain = "susu-keyring-v1";
    public const int MaxSignatureFileBytes = 1024;

    public static byte[] Signed(string domain, ReadOnlySpan<byte> bytes) => [.. Encoding.UTF8.GetBytes(domain + "\n"), .. bytes];

    public static byte[] Format(string keyId, byte[] signature)
        => Encoding.UTF8.GetBytes($"key-id: {keyId}\nalgorithm: ed25519\nsignature: {Convert.ToBase64String(signature)}\n");

    public static bool TryParse(byte[] file, out string keyId, out byte[] signature)
    {
        keyId = "";
        signature = [];
        if (file.Length is 0 or > MaxSignatureFileBytes) return false;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(file); } catch (ArgumentException) { return false; }
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 3) return false;
        string[] prefixes = ["key-id: ", "algorithm: ", "signature: "];
        for (int i = 0; i < 3; i++) if (!lines[i].StartsWith(prefixes[i], StringComparison.Ordinal)) return false;
        string id = lines[0][prefixes[0].Length..].Trim();
        if (!UpdateKey.IsValidId(id) || lines[1][prefixes[1].Length..].Trim() != "ed25519") return false;
        try { signature = Convert.FromBase64String(lines[2][prefixes[2].Length..].Trim()); } catch (FormatException) { return false; }
        if (signature.Length != Ed25519.SignatureBytes) return false;
        if (!file.AsSpan().SequenceEqual(Format(id, signature))) return false; // exactly the canonical form: no whitespace, padding or case variants
        keyId = id;
        return true;
    }
}

/// <summary>
/// Key rotation (ARCHITECTURE 10): a record names keys to add and keys to revoke and must be signed by a key the CURRENT keyring trusts for rotation.
/// Records are numbered 1, 2, ... and applied strictly in order, so a replayed or skipped record is refused. A rotation can never empty the keyring
/// of release keys or rotation keys. The record is parsed only after its signature verified.
/// </summary>
public static class KeyRotation
{
    public const int MaxRecordBytes = 16 * 1024;

    public static (UpdateKeyring? Keyring, string? Error) Apply(UpdateKeyring current, long expectedSequence, SignedBlob record)
    {
        if (record.Bytes.Length is 0 or > MaxRecordBytes) return (null, "rotation-invalid");
        if (!UpdateSignature.TryParse(record.Signature, out string signerId, out byte[] signature)) return (null, "rotation-invalid");
        var signer = current.Find(signerId);
        if (signer is not { CanRotate: true }) return (null, "rotation-invalid"); // not a key the current chain trusts for rotation
        if (!Ed25519.Verify(signer.PublicKey, UpdateSignature.Signed(UpdateSignature.KeyringDomain, record.Bytes), signature)) return (null, "rotation-invalid");
        // Verified: only now is the record read.
        string text;
        try { text = new UTF8Encoding(false, true).GetString(record.Bytes); } catch (ArgumentException) { return (null, "rotation-invalid"); }
        var (root, issues) = YamlSubset.Parse(text);
        if (issues.Count > 0 || root is not YMap map) return (null, "rotation-invalid");
        foreach (var entry in map.Entries) if (entry.Key is not ("schema" or "sequence" or "add" or "revoke")) return (null, "rotation-invalid");
        if (Scalar(map, "schema") != "1" || !long.TryParse(Scalar(map, "sequence"), NumberStyles.None, CultureInfo.InvariantCulture, out long sequence) || sequence != expectedSequence) return (null, "rotation-invalid");
        var add = new List<UpdateKey>();
        if (map.Get("add") is YSeq adds)
        {
            foreach (var item in adds.Items)
            {
                if (item is not YMap k) return (null, "rotation-invalid");
                foreach (var e in k.Entries) if (e.Key is not ("id" or "key" or "purposes")) return (null, "rotation-invalid");
                string id = Scalar(k, "id") ?? "", hex = Scalar(k, "key") ?? "", purposes = Scalar(k, "purposes") ?? "";
                if (!UpdateKey.IsValidId(id) || PackageTrust.IsThirdPartyKeyId(id) || hex.Length != 64 || !hex.All(char.IsAsciiHexDigitLower)) return (null, "rotation-invalid");
                var parts = purposes.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length == 0 || parts.Any(p => p is not ("release" or "rotate"))) return (null, "rotation-invalid");
                add.Add(new UpdateKey(id, Convert.FromHexString(hex), parts.Contains("release"), parts.Contains("rotate")));
            }
        }
        else if (map.Get("add") is not null) return (null, "rotation-invalid");
        var revoke = new List<string>();
        if (map.Get("revoke") is YSeq revokes)
        {
            foreach (var item in revokes.Items) { if (item is not YScalar s || !UpdateKey.IsValidId(s.Value)) return (null, "rotation-invalid"); revoke.Add(s.Value); }
        }
        else if (map.Get("revoke") is not null) return (null, "rotation-invalid");
        if (add.Count + revoke.Count == 0) return (null, "rotation-invalid");
        UpdateKeyring next;
        try { next = current.With(add, revoke); } catch (ArgumentException) { return (null, "rotation-invalid"); }
        if (!next.Keys.Any(k => k.CanRelease) || !next.Keys.Any(k => k.CanRotate)) return (null, "rotation-invalid");
        return (next, null);
    }

    private static string? Scalar(YMap map, string key) => map.Get(key) is YScalar s ? s.Value : null;
}

/// <summary>
/// What the updater remembers between checks (F18.2): the accepted key rotation records (raw bytes and signatures, re-verified from the embedded roots on
/// every load, so editing this file can never add a key) and the highest manifest sequence and version it has verified (anti-rollback). The high-water
/// marks are not signed: a user who edits them only weakens the protection for themselves.
/// </summary>
public sealed class UpdateTrustStore
{
    private readonly string path;

    public UpdateKeyring Roots { get; }
    public UpdateKeyring Keyring { get; private set; }
    public IReadOnlyList<SignedBlob> Chain { get; private set; } = [];
    public long HighSequence { get; private set; }
    public PackageVersion HighVersion { get; private set; }

    public UpdateTrustStore(string path, UpdateKeyring roots)
    {
        this.path = path;
        Roots = roots;
        Keyring = roots;
        Load();
    }

    private void Load()
    {
        TrustFile? file = null;
        try { if (File.Exists(path)) file = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllBytes(path), AppUpdateJson.Default.TrustFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { file = null; }
        if (file is null) return;
        var keyring = Roots;
        var chain = new List<SignedBlob>();
        try
        {
            foreach (var r in file.Rotations ?? [])
            {
                var blob = new SignedBlob(Convert.FromBase64String(r.Record), Convert.FromBase64String(r.Signature));
                var (next, error) = KeyRotation.Apply(keyring, chain.Count + 1, blob);
                if (next is null) { chain.Clear(); keyring = Roots; break; } // a stored chain that does not verify from the roots is dropped whole
                keyring = next;
                chain.Add(blob);
            }
        }
        catch (FormatException) { chain.Clear(); keyring = Roots; }
        Keyring = keyring;
        Chain = chain;
        HighSequence = Math.Max(0, file.HighSequence);
        if (PackageVersion.TryParse(file.HighVersion, out var v)) HighVersion = v;
    }

    public void Commit(IReadOnlyList<SignedBlob> chain, UpdateKeyring keyring, long sequence, PackageVersion version)
    {
        var file = new TrustFile([.. chain.Select(b => new RotationFile(Convert.ToBase64String(b.Bytes), Convert.ToBase64String(b.Signature)))], Math.Max(HighSequence, sequence),
            (version > HighVersion ? version : HighVersion).ToString());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicFile.Write(path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(file, AppUpdateJson.Default.TrustFile));
        Chain = chain;
        Keyring = keyring;
        HighSequence = file.HighSequence;
        HighVersion = version > HighVersion ? version : HighVersion;
    }
}

public sealed record RotationFile(string Record, string Signature);

public sealed record TrustFile(RotationFile[]? Rotations, long HighSequence, string? HighVersion);
