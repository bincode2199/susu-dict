using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Susu.Plugins;

/// <summary>One problem found while validating a package (manifest, file list or signature).</summary>
public sealed record ManifestIssue(string Path, string Code, string Message)
{
    public override string ToString() => $"{Path}: {Code}: {Message}";
}

/// <summary>
/// Plugin manifest (PLAN 4.6/4.7, PROTOCOL/generated/susu-plugin.d.ts). F04.1 scope: schema and safe
/// path validation, and structural signature reading. Cryptographic verification against a trusted
/// keyring, staged activation and the install UI are F16/F18.
/// </summary>
public sealed record PackageManifest(
    string Id,
    string Name,
    int ApiVersion,
    int MinHost,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Hosts,
    IReadOnlyList<string> CredentialUse,
    string Entry)
{
    /// <summary>Reads and validates one manifest.yaml. Never throws; problems come back as issues.</summary>
    public static (PackageManifest? Manifest, IReadOnlyList<ManifestIssue> Issues) Parse(string yaml)
    {
        var issues = new List<ManifestIssue>();
        YMap? root;
        try { root = ReadRoot(yaml); }
        catch (YamlException e) { return (null, [new ManifestIssue("$", "syntax", e.Message.Split('\n')[0])]); }
        if (root is null) return (null, [new ManifestIssue("$", "empty", "manifest is empty")]);

        string? id = Scalar(root, "id", issues);
        string? name = Scalar(root, "name", issues);
        string? entry = Scalar(root, "entry", issues) ?? "main.js";
        int apiVersion = IntScalar(root, "apiVersion", issues);
        int minHost = IntScalar(root, "minHost", issues);
        var capabilities = StringList(root, "capabilities", issues);
        var hosts = StringList(root, "hosts", issues, required: false);
        var credentialUse = StringList(root, "credentialUse", issues, required: false);

        if (id is not null && !Susu.Domain.PackageId.IsValid(id)) issues.Add(new ManifestIssue("id", "invalid", $"'{id}' is not a reverse-domain package id"));
        if (string.IsNullOrWhiteSpace(name)) issues.Add(new ManifestIssue("name", "missing", "name is required"));
        if (!Susu.Contracts.ProtocolVersions.SupportedPluginApi.Contains(apiVersion)) issues.Add(new ManifestIssue("apiVersion", "unsupported", $"apiVersion {apiVersion} not in {Susu.Contracts.ProtocolVersions.SupportedPluginApi}"));
        if (minHost <= 0) issues.Add(new ManifestIssue("minHost", "missing", "minHost is required"));
        if (capabilities.Count == 0) issues.Add(new ManifestIssue("capabilities", "empty", "at least one capability is required"));
        foreach (var c in capabilities)
            if (!Enum.GetNames<Susu.Contracts.Capability>().Any(n => string.Equals(n, c, StringComparison.OrdinalIgnoreCase)))
                issues.Add(new ManifestIssue("capabilities", "unknown", $"'{c}' is not a published capability"));
        if (!LooksLikeSafeRelativePath(entry)) issues.Add(new ManifestIssue("entry", "invalid", $"'{entry}' is not a safe relative path"));

        if (issues.Count > 0 || id is null || name is null) return (null, issues);
        return (new PackageManifest(id, name, apiVersion, minHost, capabilities, hosts, credentialUse, entry), issues);
    }

    private static bool LooksLikeSafeRelativePath(string name)
        => name.Length > 0 && !name.Contains('\\') && !name.Contains(':') && !Path.IsPathRooted(name) && !name.Split('/').Contains("..");

    private static YMap? ReadRoot(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > 1 << 16) throw new YamlException("manifest exceeds 64 KiB");
        var parser = new Parser(new StringReader(text));
        parser.Consume<StreamStart>();
        if (parser.Accept<StreamEnd>(out _)) return null;
        parser.Consume<DocumentStart>();
        var node = ReadNode(parser);
        parser.Consume<DocumentEnd>();
        if (!parser.Accept<StreamEnd>(out _)) throw new YamlException("multiple documents are not allowed");
        return node as YMap ?? throw new YamlException("manifest root must be a mapping");
    }

    private static YNode ReadNode(IParser parser)
    {
        var current = parser.Current ?? throw new YamlException("unexpected end of input");
        if (current is AnchorAlias) throw new YamlException("aliases are not allowed");
        if (current is NodeEvent node && !node.Anchor.IsEmpty) throw new YamlException("anchors are not allowed");
        switch (current)
        {
            case Scalar scalar: parser.MoveNext(); return new YScalar(scalar.Value);
            case SequenceStart:
            {
                parser.MoveNext();
                var items = new List<YNode>();
                while (!parser.TryConsume<SequenceEnd>(out _)) items.Add(ReadNode(parser));
                return new YSeq(items);
            }
            case MappingStart:
            {
                parser.MoveNext();
                var entries = new List<KeyValuePair<string, YNode>>();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                while (!parser.TryConsume<MappingEnd>(out _))
                {
                    var keyNode = ReadNode(parser);
                    string key = keyNode is YScalar s ? s.Value : throw new YamlException("mapping keys must be scalars");
                    if (key == "<<") throw new YamlException("merge keys are not allowed");
                    if (!keys.Add(key)) throw new YamlException($"duplicate key '{key}'");
                    entries.Add(new(key, ReadNode(parser)));
                }
                return new YMap(entries);
            }
            default: throw new YamlException($"unexpected {current.GetType().Name}");
        }
    }

    private abstract record YNode;
    private sealed record YScalar(string Value) : YNode;
    private sealed record YSeq(IReadOnlyList<YNode> Items) : YNode;
    private sealed record YMap(IReadOnlyList<KeyValuePair<string, YNode>> Entries) : YNode
    {
        public YNode? Get(string key) => Entries.FirstOrDefault(e => e.Key == key).Value;
    }

    private static string? Scalar(YMap map, string key, List<ManifestIssue> issues)
    {
        var node = map.Get(key);
        if (node is null) { issues.Add(new ManifestIssue(key, "missing", $"'{key}' is required")); return null; }
        if (node is not YScalar s) { issues.Add(new ManifestIssue(key, "type", $"'{key}' must be a scalar")); return null; }
        return s.Value;
    }

    private static int IntScalar(YMap map, string key, List<ManifestIssue> issues)
        => Scalar(map, key, issues) is { } s && int.TryParse(s, out int v) ? v : 0;

    private static IReadOnlyList<string> StringList(YMap map, string key, List<ManifestIssue> issues, bool required = true)
    {
        var node = map.Get(key);
        if (node is null) { if (required) issues.Add(new ManifestIssue(key, "missing", $"'{key}' is required")); return []; }
        if (node is not YSeq seq) { issues.Add(new ManifestIssue(key, "type", $"'{key}' must be a sequence")); return []; }
        var result = new List<string>();
        foreach (var item in seq.Items)
            if (item is YScalar s) result.Add(s.Value);
            else issues.Add(new ManifestIssue(key, "type", $"'{key}' items must be scalars"));
        return result;
    }
}

/// <summary>
/// File-list safety for one unpacked package directory (PROTOCOL §10): at most 256 files, size caps,
/// no path traversal, absolute paths, device names, alternate data streams, symlinks/hardlinks,
/// reparse points, or paths equal except for case.
/// </summary>
public static class SafePackage
{
    public const int MaxFiles = 256;
    public const long MaxTotalBytes = 16 * 1024 * 1024;
    public const long MaxFileBytes = 4 * 1024 * 1024;

    private static readonly string[] DeviceNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    public static IReadOnlyList<ManifestIssue> Validate(string root)
    {
        var issues = new List<ManifestIssue>();
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = new List<FileInfo>();
        try { files = new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).ToList(); }
        catch (IOException e) { issues.Add(new ManifestIssue("$", "io", e.Message)); return issues; }

        if (files.Count > MaxFiles) issues.Add(new ManifestIssue("$", "too-many-files", $"{files.Count} files exceeds the {MaxFiles} limit"));
        long total = 0;
        var seenLower = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            string full = Path.GetFullPath(file.FullName);
            string relative = full[prefix.Length..].Replace('\\', '/');
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { issues.Add(new ManifestIssue(relative, "path-escape", "resolves outside the package")); continue; }
            if (relative.Split('/').Any(segment => DeviceNames.Contains(segment.Split(':')[0], StringComparer.OrdinalIgnoreCase)))
                issues.Add(new ManifestIssue(relative, "device-name", "reserved device name in path"));
            if (relative.Contains(':')) issues.Add(new ManifestIssue(relative, "alternate-stream", "alternate data streams are rejected"));
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) issues.Add(new ManifestIssue(relative, "reparse-point", "symlinks/junctions/hardlinked reparse points are rejected"));
            if (!seenLower.Add(relative)) issues.Add(new ManifestIssue(relative, "case-duplicate", "path duplicates another entry except for case"));
            if (file.Length > MaxFileBytes) issues.Add(new ManifestIssue(relative, "too-large", $"{file.Length} bytes exceeds the {MaxFileBytes} limit"));
            total += file.Length;
        }
        if (total > MaxTotalBytes) issues.Add(new ManifestIssue("$", "too-large", $"total {total} bytes exceeds the {MaxTotalBytes} limit"));
        return issues;
    }

    /// <summary>Canonical (path, sha256) list sorted by path, ordinal — the list a package signature covers.</summary>
    public static IReadOnlyList<(string Path, string Sha256Hex)> CanonicalHashes(string root)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = new List<(string, string)>();
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            string relative = Path.GetFullPath(file.FullName)[prefix.Length..].Replace('\\', '/');
            using var stream = File.OpenRead(file.FullName);
            result.Add((relative, Convert.ToHexStringLower(SHA256.HashData(stream))));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return result;
    }
}

/// <summary>
/// Structural reading of a detached package/app signature file (PROTOCOL §10: domain-separated raw
/// bytes signed, file list sorted by canonical path). This is F04's "read" half only: parsing the
/// structure and recomputing the bytes a signature covers. Verifying against a trusted keyring, key
/// rotation and the transitional keyring are F16/F18.
/// </summary>
public sealed record SignatureFile(string KeyId, string Algorithm, byte[] Signature)
{
    /// <summary>Domain-separated bytes a manifest signature covers: tag || 0x00 || raw manifest bytes.</summary>
    public static byte[] ManifestSignedBytes(string domainTag, byte[] rawManifestBytes)
    {
        var tag = Encoding.UTF8.GetBytes(domainTag);
        var result = new byte[tag.Length + 1 + rawManifestBytes.Length];
        tag.CopyTo(result, 0);
        result[tag.Length] = 0;
        rawManifestBytes.CopyTo(result, tag.Length + 1);
        return result;
    }

    /// <summary>Canonical bytes a package signature covers: the sorted "path\tsha256\n" hash list.</summary>
    public static byte[] FileListSignedBytes(IReadOnlyList<(string Path, string Sha256Hex)> hashes)
    {
        var b = new StringBuilder();
        foreach (var (path, hash) in hashes) b.Append(path).Append('\t').Append(hash).Append('\n');
        return Encoding.UTF8.GetBytes(b.ToString());
    }

    /// <summary>Parses a small "key: value" signature file (keyId, algorithm, signature as base64). No crypto check.</summary>
    public static (SignatureFile? File, IReadOnlyList<ManifestIssue> Issues) Parse(string text)
    {
        var issues = new List<ManifestIssue>();
        string? keyId = null, algorithm = null, signatureBase64 = null;
        foreach (var line in text.Split('\n'))
        {
            string trimmed = line.Trim('\r', ' ', '\t');
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            int colon = trimmed.IndexOf(':');
            if (colon < 0) { issues.Add(new ManifestIssue("$", "syntax", $"malformed line '{trimmed}'")); continue; }
            string key = trimmed[..colon].Trim();
            string value = trimmed[(colon + 1)..].Trim();
            switch (key)
            {
                case "keyId": keyId = value; break;
                case "algorithm": algorithm = value; break;
                case "signature": signatureBase64 = value; break;
                default: issues.Add(new ManifestIssue(key, "unknown-field", "schema rejects unsigned extra fields")); break;
            }
        }
        if (keyId is null) issues.Add(new ManifestIssue("keyId", "missing", "keyId is required"));
        if (algorithm != "ed25519") issues.Add(new ManifestIssue("algorithm", "unsupported", "only ed25519 is accepted"));
        byte[] signature = [];
        if (signatureBase64 is null) issues.Add(new ManifestIssue("signature", "missing", "signature is required"));
        else { try { signature = Convert.FromBase64String(signatureBase64); } catch (FormatException) { issues.Add(new ManifestIssue("signature", "invalid", "signature is not valid base64")); } }
        if (issues.Count > 0 || keyId is null) return (null, issues);
        return (new SignatureFile(keyId, algorithm!, signature), issues);
    }
}
