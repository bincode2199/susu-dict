using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Susu.Abstractions;
using Susu.Domain;

namespace Susu.Storage;


/// <summary>A user-installed package listed in a backup. Only the identity is kept: the code is never part of a backup and is never installed by an import.</summary>
public sealed record BackupPluginRef(string Id, string Version, string Signer, string Hash);

/// <summary>Bounds applied to every backup file before and while it is read (ARCHITECTURE 8.1: KDF 600k-2M, 32 MiB total, 8 MiB per entry).</summary>
public static class BackupLimits
{
    public const long MaxFileBytes = 16L << 20;
    public const long MaxEntryBytes = 8L << 20;
    public const long MaxTotalBytes = 32L << 20;
    public const int MaxEntries = 16;
    /// <summary>An entry may expand at most this many times its compressed size once it is larger than <see cref="RatioFloorBytes"/>.</summary>
    public const int MaxRatio = 100;
    public const long RatioFloorBytes = 1L << 20;
    public const int MinIterations = 600_000;
    public const int MaxIterations = 2_000_000;
    public const int MinPasswordChars = BackupPasswordPolicy.Min;
    public const int MaxPasswordChars = BackupPasswordPolicy.Max;
    public const int MaxListEntries = 1000;
}

internal sealed record BackupManifest(string Format, int FormatVersion, string App, string AppVersion, int SchemaVersion, string CreatedUtc, bool IncludesSecrets, Dictionary<string, string> Files);
internal sealed record BackupEncHeader(string Kdf, int Iterations, string Salt, string Nonce, string Cipher);
internal sealed record BackupPluginsFile(BackupPluginRef[] Plugins);
internal sealed record BackupSecretValue(string Account, string Name, string Value);
internal sealed record BackupSecretsFile(BackupSecretValue[] Entries);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BackupManifest))]
[JsonSerializable(typeof(BackupEncHeader))]
[JsonSerializable(typeof(BackupPluginsFile))]
[JsonSerializable(typeof(BackupSecretsFile))]
[JsonSerializable(typeof(BackupPending))]
[JsonSerializable(typeof(BackupResult))]
[JsonSerializable(typeof(BackupRestorePointMeta))]
internal sealed partial class BackupJson : JsonSerializerContext;

/// <summary>What a verified backup holds. Secrets are plaintext only here, in memory, and only when the file was password-encrypted.</summary>
internal sealed record BackupContents(BackupManifest Manifest, byte[] SettingsBytes, AppSettings Settings, BackupPluginRef[] Plugins,
    IReadOnlyList<(string Account, string Name, string Value)> Secrets, bool Encrypted);

/// <summary>
/// The <c>.susubak</c> file (F17.1). Plain: a zip of manifest.json, settings.yaml, plugins.json. Encrypted: <c>SUSUBAK\x01</c>, a length-prefixed
/// JSON header (PBKDF2-SHA256, iterations, salt, nonce) and AES-256-GCM over the same zip (with secrets.json when keys are included), the header
/// being the associated data. Nothing else is accepted: no code, caches, database or logs, and no entry name outside the four fixed ones.
/// </summary>
internal static class BackupArchive
{
    public const string ManifestName = "manifest.json", SettingsName = "settings.yaml", PluginsName = "plugins.json", SecretsName = "secrets.json";
    public const int FormatVersion = 1;
    private static readonly byte[] Magic = [(byte)'S', (byte)'U', (byte)'S', (byte)'U', (byte)'B', (byte)'A', (byte)'K', 1];
    private static readonly string[] AllowedNames = [ManifestName, SettingsName, PluginsName, SecretsName];
    private const int MaxHeaderBytes = 1024, TagBytes = 16, NonceBytes = 12, KeyBytes = 32;

    public static bool IsEncrypted(ReadOnlySpan<byte> file) => file.StartsWith(Magic);

    public static byte[] ReadFile(string path)
    {
        FileInfo info;
        try { info = new FileInfo(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { throw new BackupException("unreadable"); }
        if (!info.Exists) throw new BackupException("unreadable");
        if (info.Length > BackupLimits.MaxFileBytes) throw new BackupException("too-large");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                if (ms.Length + read > BackupLimits.MaxFileBytes) throw new BackupException("too-large"); // the file grew after the length check
                ms.Write(buffer, 0, read);
            }
            return ms.ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new BackupException("unreadable"); }
    }

    // ---------- writing ----------

    public static byte[] Build(string appVersion, DateTimeOffset created, byte[] settingsBytes, BackupPluginRef[] plugins,
        IReadOnlyList<(string Account, string Name, string Value)>? secrets, string? password)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [SettingsName] = settingsBytes,
            [PluginsName] = JsonSerializer.SerializeToUtf8Bytes(new BackupPluginsFile(plugins), BackupJson.Default.BackupPluginsFile),
        };
        if (secrets is not null)
            files[SecretsName] = JsonSerializer.SerializeToUtf8Bytes(new BackupSecretsFile([.. secrets.Select(s => new BackupSecretValue(s.Account, s.Name, s.Value))]), BackupJson.Default.BackupSecretsFile);
        var manifest = new BackupManifest("susubak", FormatVersion, "Su-Su", appVersion, AppSettings.CurrentSchemaVersion, created.UtcDateTime.ToString("O"), secrets is not null,
            files.ToDictionary(f => f.Key, f => AtomicFile.Hash(f.Value), StringComparer.Ordinal));
        files[ManifestName] = JsonSerializer.SerializeToUtf8Bytes(manifest, BackupJson.Default.BackupManifest);

        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in AllowedNames.Where(files.ContainsKey))
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var s = entry.Open();
                s.Write(files[name]);
            }
        }
        if (files.TryGetValue(SecretsName, out var secretsJson)) CryptographicOperations.ZeroMemory(secretsJson);
        byte[] zipBytes = zipStream.ToArray();
        byte[] result = password is null ? zipBytes : Encrypt(zipBytes, password);
        if (result.Length > BackupLimits.MaxFileBytes) throw new BackupException("too-large");
        return result;
    }

    private static byte[] Encrypt(byte[] plain, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16), nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var header = new BackupEncHeader("pbkdf2-sha256", BackupLimits.MinIterations, Convert.ToBase64String(salt), Convert.ToBase64String(nonce), "aes-256-gcm");
        byte[] headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, BackupJson.Default.BackupEncHeader);
        byte[] prefix = new byte[Magic.Length + 4 + headerBytes.Length];
        Magic.CopyTo(prefix, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(Magic.Length), (uint)headerBytes.Length);
        headerBytes.CopyTo(prefix, Magic.Length + 4);
        byte[] key = DeriveKey(password, salt, header.Iterations);
        try
        {
            byte[] output = new byte[prefix.Length + plain.Length + TagBytes];
            prefix.CopyTo(output, 0);
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plain, output.AsSpan(prefix.Length, plain.Length), output.AsSpan(prefix.Length + plain.Length, TagBytes), prefix);
            return output;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        byte[] pw = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
        try { return Rfc2898DeriveBytes.Pbkdf2(pw, salt, iterations, HashAlgorithmName.SHA256, KeyBytes); }
        finally { CryptographicOperations.ZeroMemory(pw); }
    }

    // ---------- reading ----------

    /// <summary>Verifies and decodes a backup. Writes nothing. Throws <see cref="BackupException"/> with a stable code.</summary>
    public static BackupContents Open(byte[] file, string? password)
    {
        if (file.Length > BackupLimits.MaxFileBytes) throw new BackupException("too-large");
        bool encrypted = IsEncrypted(file);
        byte[] zipBytes;
        if (encrypted)
        {
            if (string.IsNullOrEmpty(password)) throw new BackupException("password-required");
            if (password.Length > BackupLimits.MaxPasswordChars) throw new BackupException("decrypt-failed");
            zipBytes = Decrypt(file, password);
        }
        else if (file.Length >= 4 && file[0] == 'P' && file[1] == 'K') zipBytes = file;
        else throw new BackupException("not-a-backup");

        var entries = ReadEntries(zipBytes);
        if (!entries.TryGetValue(ManifestName, out var manifestBytes) || !entries.ContainsKey(SettingsName)) throw new BackupException("incomplete");
        BackupManifest manifest;
        try { manifest = JsonSerializer.Deserialize(manifestBytes, BackupJson.Default.BackupManifest) ?? throw new BackupException("manifest-invalid"); }
        catch (JsonException) { throw new BackupException("manifest-invalid"); }
        if (manifest.Format != "susubak" || manifest.Files is null) throw new BackupException("manifest-invalid");
        if (manifest.FormatVersion > FormatVersion) throw new BackupException("format-newer");
        if (manifest.FormatVersion < 1) throw new BackupException("manifest-invalid");
        if (manifest.SchemaVersion > AppSettings.CurrentSchemaVersion) throw new BackupException("schema-newer");
        if (manifest.SchemaVersion < 0) throw new BackupException("manifest-invalid");

        // Every entry but the manifest is listed with its hash, and nothing is listed that is not there.
        var listed = new HashSet<string>(manifest.Files.Keys, StringComparer.Ordinal);
        var present = new HashSet<string>(entries.Keys.Where(n => n != ManifestName), StringComparer.Ordinal);
        if (!listed.SetEquals(present)) throw new BackupException("manifest-mismatch");
        foreach (var (name, hash) in manifest.Files)
            if (!string.Equals(hash, AtomicFile.Hash(entries[name]), StringComparison.OrdinalIgnoreCase)) throw new BackupException("hash-mismatch");
        if (manifest.IncludesSecrets != entries.ContainsKey(SecretsName)) throw new BackupException("manifest-mismatch");
        // Keys are never accepted in a file that is not encrypted: a plaintext secrets.json means a tampered or foreign file.
        if (entries.ContainsKey(SecretsName) && !encrypted) throw new BackupException("secrets-unencrypted");

        byte[] settingsBytes = entries[SettingsName];
        var (settings, issues) = SettingsYaml.Read(DecodeUtf8(settingsBytes));
        if (settings is null)
            throw new BackupException(issues.Any(i => i.Code == "newer-schema") ? "schema-newer" : "settings-invalid");

        var plugins = Array.Empty<BackupPluginRef>();
        if (entries.TryGetValue(PluginsName, out var pluginBytes))
        {
            try { plugins = JsonSerializer.Deserialize(pluginBytes, BackupJson.Default.BackupPluginsFile)?.Plugins ?? throw new BackupException("plugins-invalid"); }
            catch (JsonException) { throw new BackupException("plugins-invalid"); }
            if (plugins.Length > BackupLimits.MaxListEntries || plugins.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || p.Version is null)) throw new BackupException("plugins-invalid");
        }

        var secrets = new List<(string, string, string)>();
        if (entries.TryGetValue(SecretsName, out var secretBytes))
        {
            try
            {
                var file2 = JsonSerializer.Deserialize(secretBytes, BackupJson.Default.BackupSecretsFile) ?? throw new BackupException("secrets-invalid");
                if (file2.Entries is null || file2.Entries.Length > BackupLimits.MaxListEntries) throw new BackupException("secrets-invalid");
                foreach (var e in file2.Entries)
                {
                    if (e is null || string.IsNullOrWhiteSpace(e.Account) || string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrEmpty(e.Value) || e.Value.Length > SecretStore.MaxValueChars)
                        throw new BackupException("secrets-invalid");
                    secrets.Add((e.Account, e.Name, e.Value));
                }
            }
            catch (JsonException) { throw new BackupException("secrets-invalid"); }
            finally { CryptographicOperations.ZeroMemory(secretBytes); }
        }
        return new BackupContents(manifest, settingsBytes, settings, plugins, secrets, encrypted);
    }

    private static byte[] Decrypt(byte[] file, string password)
    {
        int fixedPrefix = Magic.Length + 4;
        if (file.Length < fixedPrefix + 2 + TagBytes) throw new BackupException("corrupt");
        uint headerLength = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(Magic.Length, 4));
        if (headerLength == 0 || headerLength > MaxHeaderBytes || (long)fixedPrefix + headerLength + TagBytes > file.Length) throw new BackupException("corrupt");
        int prefixLength = fixedPrefix + (int)headerLength;
        BackupEncHeader? header;
        try { header = JsonSerializer.Deserialize(file.AsSpan(fixedPrefix, (int)headerLength), BackupJson.Default.BackupEncHeader); }
        catch (JsonException) { throw new BackupException("corrupt"); }
        if (header is null || header.Salt is null || header.Nonce is null) throw new BackupException("corrupt");
        if (header.Kdf != "pbkdf2-sha256" || header.Cipher != "aes-256-gcm") throw new BackupException("unsupported-kdf");
        // Bounds first: a hostile iteration count must never reach the KDF (DoS), and a low one is refused as too weak.
        if (header.Iterations < BackupLimits.MinIterations || header.Iterations > BackupLimits.MaxIterations) throw new BackupException("kdf-params");
        byte[] salt, nonce;
        try { salt = Convert.FromBase64String(header.Salt); nonce = Convert.FromBase64String(header.Nonce); }
        catch (FormatException) { throw new BackupException("corrupt"); }
        if (salt.Length is < 16 or > 64 || nonce.Length != NonceBytes) throw new BackupException("corrupt");

        byte[] key = DeriveKey(password, salt, header.Iterations);
        try
        {
            int cipherLength = file.Length - prefixLength - TagBytes;
            byte[] plain = new byte[cipherLength];
            using var aes = new AesGcm(key, TagBytes);
            try { aes.Decrypt(nonce, file.AsSpan(prefixLength, cipherLength), file.AsSpan(prefixLength + cipherLength, TagBytes), plain, file.AsSpan(0, prefixLength)); }
            catch (CryptographicException) { throw new BackupException("decrypt-failed"); } // wrong password and tampering cannot be told apart
            return plain;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static Dictionary<string, byte[]> ReadEntries(byte[] zipBytes)
    {
        if (PeekEntryCount(zipBytes) is not { } count) throw new BackupException("corrupt");
        if (count > BackupLimits.MaxEntries) throw new BackupException("too-many-entries");
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(zipBytes, writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > BackupLimits.MaxEntries) throw new BackupException("too-many-entries");
            long declared = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                string name = entry.FullName;
                if (!IsPlainName(name)) throw new BackupException("path-escape");
                if (!AllowedNames.Contains(name, StringComparer.Ordinal)) throw new BackupException("unexpected-entry");
                if (!seen.Add(name)) throw new BackupException("duplicate-entry");
                declared += entry.Length;
                if (entry.Length > BackupLimits.MaxEntryBytes || declared > BackupLimits.MaxTotalBytes) throw new BackupException("too-large");
            }
            foreach (var entry in zip.Entries)
            {
                long allowed = Math.Min(BackupLimits.MaxEntryBytes, Math.Max(BackupLimits.RatioFloorBytes, entry.CompressedLength * BackupLimits.MaxRatio));
                result[entry.FullName] = ReadBounded(entry, allowed, ref total, allowed < BackupLimits.MaxEntryBytes);
            }
        }
        catch (InvalidDataException) { throw new BackupException("corrupt"); }
        catch (EndOfStreamException) { throw new BackupException("corrupt"); }
        catch (NotSupportedException) { throw new BackupException("corrupt"); }
        return result;
    }

    private static byte[] ReadBounded(ZipArchiveEntry entry, long limit, ref long total, bool ratioBound)
    {
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            total += read;
            if (ms.Length + read > limit) throw new BackupException(ratioBound ? "ratio" : "too-large");
            if (total > BackupLimits.MaxTotalBytes) throw new BackupException("too-large");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    /// <summary>A bare file name: no separators, drive, parent or stream parts, no control characters, no trailing dot or space.</summary>
    internal static bool IsPlainName(string name)
        => name.Length is > 0 and <= 64
           && !name.Any(c => c < 0x20 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
           && name != "." && name != ".." && !name.EndsWith('.') && !name.EndsWith(' ');

    /// <summary>The entry count from the end-of-central-directory record, read before any zip parsing allocates per entry.</summary>
    private static int? PeekEntryCount(byte[] zip)
    {
        int start = Math.Max(0, zip.Length - 22 - 65535);
        for (int i = zip.Length - 22; i >= start; i--)
        {
            if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x05 && zip[i + 3] == 0x06)
            {
                int count = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(i + 10, 2));
                return count == 0xFFFF ? int.MaxValue : count;
            }
        }
        return null;
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.StartsWith(Encoding.UTF8.Preamble)) span = span[3..];
        return Encoding.UTF8.GetString(span);
    }
}
