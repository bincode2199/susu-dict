using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Susu.Abstractions;

namespace Susu.Storage;

internal sealed record SecretFile(int Version, SecretEntry[] Entries);
internal sealed record SecretEntry(string Account, string Name, string Blob);

/// <summary>
/// secrets.dat (PLAN 5.4): account id and secret name are readable so presence can be shown without
/// decrypting; each value is a separate DPAPI (CurrentUser) blob, decrypted only when the network layer
/// needs it. Values are never logged, never returned to UI code and never placed in settings.yaml.
/// </summary>
public sealed class SecretStore : ISecretStore
{
    public const int MaxValueChars = 8192;
    private readonly string path;
    private readonly ISecretProtector protector;
    private readonly IFaultPoint? faults;
    private readonly object gate = new();
    private List<SecretEntry> entries;

    public SecretStore(string path, ISecretProtector protector, IFaultPoint? faults = null)
    {
        this.path = path;
        this.protector = protector;
        this.faults = faults;
        entries = Load(path);
    }

    /// <summary>Raised with the plaintext when a value is prepared for storage, so loggers can mask it from then on (never persisted by the subscriber).</summary>
    public event Action<string>? ValueStored;

    public bool Has(string accountId, string secretName) { lock (gate) return entries.Any(e => e.Account == accountId && e.Name == secretName); }

    /// <summary>Every stored (account, name) pair, for a backup that includes keys (F17.1). Values are read one by one with <see cref="TryRead"/>.</summary>
    public IReadOnlyList<(string Account, string Name)> Entries() { lock (gate) return [.. entries.Select(e => (e.Account, e.Name))]; }

    /// <summary>
    /// Builds the bytes of a secrets.dat from plaintext values, protecting each with <paramref name="protector"/> (the importing user's DPAPI,
    /// F17.1), without touching any store. Same validation as <see cref="Write"/>.
    /// </summary>
    public static byte[] BuildFile(ISecretProtector protector, IEnumerable<(string Account, string Name, string Value)> values)
    {
        var list = new List<SecretEntry>();
        foreach (var (account, name, value) in values)
        {
            if (value.Length == 0 || value.Length > MaxValueChars) throw new ArgumentException($"secret must be 1..{MaxValueChars} characters");
            if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(name)) throw new ArgumentException("account and secret name are required");
            byte[] plain = Encoding.UTF8.GetBytes(value);
            try { list.RemoveAll(e => e.Account == account && e.Name == name); list.Add(new SecretEntry(account, name, Convert.ToBase64String(protector.Protect(plain)))); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        return Serialize(list);
    }

    public IReadOnlyList<string> Names(string accountId) { lock (gate) return entries.Where(e => e.Account == accountId).Select(e => e.Name).Order(StringComparer.Ordinal).ToList(); }

    public void Write(string accountId, string secretName, ReadOnlySpan<char> value)
    {
        lock (gate)
        {
            var next = WithValue(entries, accountId, secretName, value);
            AtomicFile.Write(path, Serialize(next), faults, "secrets");
            entries = next;
        }
    }

    public bool Delete(string accountId, string secretName)
    {
        lock (gate)
        {
            var next = entries.Where(e => !(e.Account == accountId && e.Name == secretName)).ToList();
            if (next.Count == entries.Count) return false;
            AtomicFile.Write(path, Serialize(next), faults, "secrets");
            entries = next;
            return true;
        }
    }

    public bool TryRead(string accountId, string secretName, out string value)
    {
        SecretEntry? entry;
        lock (gate) entry = entries.FirstOrDefault(e => e.Account == accountId && e.Name == secretName);
        value = "";
        if (entry is null) return false;
        byte[] plain = protector.Unprotect(Convert.FromBase64String(entry.Blob));
        try { value = Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        return true;
    }

    /// <summary>Bytes of secrets.dat after a change, for a two-file <see cref="ConfigTransaction"/>; call <see cref="Reload"/> after commit.</summary>
    public byte[] Prepare(IEnumerable<(string Account, string Name, string? Value)> changes)
    {
        lock (gate)
        {
            var next = entries;
            foreach (var (account, name, value) in changes)
                next = value is null ? next.Where(e => !(e.Account == account && e.Name == name)).ToList() : WithValue(next, account, name, value);
            return Serialize(next);
        }
    }

    public void Reload() { lock (gate) entries = Load(path); }

    /// <summary>Removes every secret of an account (account deleted in settings).</summary>
    public byte[] PrepareDeleteAccount(string accountId) { lock (gate) return Serialize(entries.Where(e => e.Account != accountId).ToList()); }

    private List<SecretEntry> WithValue(List<SecretEntry> current, string accountId, string secretName, ReadOnlySpan<char> value)
    {
        if (value.Length == 0 || value.Length > MaxValueChars) throw new ArgumentException($"secret must be 1..{MaxValueChars} characters");
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(secretName)) throw new ArgumentException("account and secret name are required");
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(value.Length));
        try
        {
            int length = Encoding.UTF8.GetBytes(value, buffer);
            string blob = Convert.ToBase64String(protector.Protect(buffer.AsSpan(0, length)));
            ValueStored?.Invoke(new string(value));
            var next = current.Where(e => !(e.Account == accountId && e.Name == secretName)).ToList();
            next.Add(new SecretEntry(accountId, secretName, blob));
            return next;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static byte[] Serialize(List<SecretEntry> entries)
        => JsonSerializer.SerializeToUtf8Bytes(new SecretFile(1, [.. entries.OrderBy(e => e.Account, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.Ordinal)]), StorageJson.Default.SecretFile);

    private static List<SecretEntry> Load(string path)
    {
        if (!File.Exists(path)) return [];
        var file = JsonSerializer.Deserialize(File.ReadAllBytes(path), StorageJson.Default.SecretFile)
            ?? throw new InvalidDataException("secrets.dat is empty");
        if (file.Version != 1) throw new InvalidDataException($"secrets.dat version {file.Version} is not supported");
        return [.. file.Entries];
    }
}
