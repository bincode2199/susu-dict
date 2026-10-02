using System.Text;

namespace Susu.Plugins.Install;

public enum SignerKind { Unsigned, Host, ThirdParty }

/// <summary>
/// The identity a package proved: Unsigned (Signer empty), Host (a key of the host keyring) or ThirdParty (a self-certifying key: the key id is the
/// 64 hex characters of its Ed25519 public key, so the signature file need not carry the key). A third-party key is only the identity the user
/// accepted at install; it is never presented as endorsed by the host (PLAN 4.8).
/// </summary>
public sealed record PackageIdentity(SignerKind Kind, string Signer)
{
    public static readonly PackageIdentity Unsigned = new(SignerKind.Unsigned, "");
    public string Key => Kind switch { SignerKind.Host => "host:" + Signer, SignerKind.ThirdParty => "key:" + Signer, _ => "unsigned" };

    public static PackageIdentity FromKey(string key)
        => key.StartsWith("host:", StringComparison.Ordinal) ? new(SignerKind.Host, key[5..])
         : key.StartsWith("key:", StringComparison.Ordinal) ? new(SignerKind.ThirdParty, key[4..]) : Unsigned;
}

/// <summary>The host's trusted public keys (ARCHITECTURE 10: the root is offline; the real keys arrive with the release build, F18).</summary>
public interface IHostKeyring
{
    byte[]? Find(string keyId);
}

public sealed class HostKeyring : IHostKeyring
{
    private readonly Dictionary<string, byte[]> keys = new(StringComparer.Ordinal);

    public static readonly HostKeyring Empty = new([]);

    /// <summary>Key ids that look like a self-certifying third-party id (64 hex characters) are refused so a host key can never be confused with one.</summary>
    public HostKeyring(IEnumerable<KeyValuePair<string, byte[]>> entries)
    {
        foreach (var (id, key) in entries)
        {
            if (PackageTrust.IsThirdPartyKeyId(id)) throw new ArgumentException($"host key id '{id}' looks like a third-party key id");
            if (key.Length != Ed25519.PublicKeyBytes) throw new ArgumentException("an Ed25519 public key is 32 bytes");
            keys[id] = key;
        }
    }

    public byte[]? Find(string keyId) => keys.GetValueOrDefault(keyId);
}

/// <summary>
/// Package signature (PROTOCOL 10, ARCHITECTURE 10): the detached file "signature" (keyId, algorithm ed25519, base64 signature) covers the domain tag,
/// the raw bytes of manifest.yaml, and the sorted "path\tsha256" list of every other file. Verified before the manifest is trusted; any extra,
/// missing or changed file breaks it. A package without the file is unsigned.
/// </summary>
public static class PackageTrust
{
    public const string Domain = "susu-plugin-v1";
    public const string SignatureFileName = "signature";

    public static bool IsThirdPartyKeyId(string id) => id.Length == 64 && id.All(c => char.IsAsciiHexDigitLower(c));

    public static byte[] SignedBytes(byte[] manifestBytes, IReadOnlyList<(string Path, string Sha256Hex)> otherFiles)
    {
        byte[] head = SignatureFile.ManifestSignedBytes(Domain, manifestBytes);
        byte[] list = SignatureFile.FileListSignedBytes(otherFiles);
        var all = new byte[head.Length + 1 + list.Length];
        head.CopyTo(all, 0);
        list.CopyTo(all, head.Length + 1); // a 0x00 separator stays between the two parts
        return all;
    }

    /// <summary>Verifies the signature of an unpacked package directory and says who signed it. An invalid signature is an issue, never "unsigned".</summary>
    public static (PackageIdentity? Identity, IReadOnlyList<ManifestIssue> Issues) Verify(string directory, IHostKeyring keyring)
    {
        string signaturePath = Path.Combine(directory, SignatureFileName);
        if (!File.Exists(signaturePath)) return (PackageIdentity.Unsigned, []);
        string text;
        try
        {
            var info = new FileInfo(signaturePath);
            if (info.Length > 4096) return (null, [new ManifestIssue(SignatureFileName, "signature-invalid", "the signature file is too large")]);
            text = File.ReadAllText(signaturePath, Encoding.UTF8);
        }
        catch (IOException e) { return (null, [new ManifestIssue(SignatureFileName, "signature-invalid", e.Message)]); }
        var (file, parseIssues) = SignatureFile.Parse(text);
        if (file is null) return (null, [.. parseIssues.Select(i => new ManifestIssue(SignatureFileName, "signature-invalid", i.ToString()))]);
        if (file.Signature.Length != Ed25519.SignatureBytes) return (null, [new ManifestIssue(SignatureFileName, "signature-invalid", "an Ed25519 signature is 64 bytes")]);

        PackageIdentity identity;
        byte[]? publicKey = keyring.Find(file.KeyId);
        if (publicKey is not null) identity = new PackageIdentity(SignerKind.Host, file.KeyId);
        else if (IsThirdPartyKeyId(file.KeyId))
        {
            publicKey = Convert.FromHexString(file.KeyId);
            identity = new PackageIdentity(SignerKind.ThirdParty, file.KeyId);
        }
        else return (null, [new ManifestIssue(SignatureFileName, "unknown-key", "the signing key is not trusted by this host")]);

        string manifestPath = Path.Combine(directory, "manifest.yaml");
        if (!File.Exists(manifestPath)) return (null, [new ManifestIssue("manifest.yaml", "missing", "manifest.yaml is required")]);
        byte[] manifestBytes = File.ReadAllBytes(manifestPath);
        var others = SafePackage.CanonicalHashes(directory).Where(h => !string.Equals(h.Path, SignatureFileName, StringComparison.Ordinal)).ToList();
        return Ed25519.Verify(publicKey, SignedBytes(manifestBytes, others), file.Signature)
            ? (identity, [])
            : (null, [new ManifestIssue(SignatureFileName, "signature-invalid", "the signature does not match the manifest and files")]);
    }

    /// <summary>Package hash recorded in plugin_installations: sha256 over the sorted file hash list (the signature file included).</summary>
    public static string PackageHash(string directory)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(SignatureFile.FileListSignedBytes(SafePackage.CanonicalHashes(directory))));
}
