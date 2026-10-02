using Susu.Contracts;

namespace Susu.Domain;

/// <summary>
/// A built-in vocabulary sync package (DEV-PLAN 5: P-V01 AnkiConnect, P-V02 Eudic; F15.3). <see cref="Local"/>: the vendor is an
/// application on this machine (AnkiConnect on 127.0.0.1:8765), so its origin is a loopback origin the user approves by configuring
/// it, and its key is optional. <see cref="Lookup"/>: the manifest declares a lookup the host may use to confirm an uncertain write
/// (ARCHITECTURE 8.3).
/// </summary>
public sealed record VocabPackage(string InstanceId, string PackageId, string Plan, string DefaultOrigin, IReadOnlyList<CredentialTarget> Credentials, bool Local, bool Lookup)
    : ICredentialPackage
{
    public string Signer => $"unsigned:{PackageId}";
    public IReadOnlyList<string> SecretNames => [.. Credentials.Select(c => c.Secret).Distinct()];
    public string Directory => $"plugins/{InstanceId}";

    /// <summary>The exact origin this package calls: an explicit <c>baseUrl</c> (the user's AnkiConnect address, or a test server), else the vendor default.</summary>
    public string Origin(IReadOnlyDictionary<string, string> config)
        => config.TryGetValue("baseUrl", out var baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && Domain.Origin.TryNormalize($"{uri.Scheme}://{uri.Authority}", out var custom) ? custom : Domain.Origin.Normalize(DefaultOrigin);

    public IReadOnlyList<CredentialGrant> RequiredGrants(IReadOnlyDictionary<string, string> config)
    {
        string origin = Origin(config);
        return [.. Credentials.Select(c => new CredentialGrant(PackageId, Signer, c.Secret, origin, c.Use))];
    }

    /// <summary>True for a loopback origin (127.0.0.0/8, ::1, localhost); only such origins of a <see cref="Local"/> package are approved without a prompt.</summary>
    public static bool IsLoopback(string origin)
        => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
}

public static class VocabCatalog
{
    public const string AnkiConnect = "ankiconnect", Eudic = "eudic";

    public static readonly IReadOnlyList<VocabPackage> All =
    [
        // P-V01: AnkiConnect API v6 on the local Anki. The optional key goes into the request's `key` field (json:/key); it is
        // sent only when the instance enables it. Notes carry a stable Su-Su id field, so lookup and update are by id.
        new(AnkiConnect, "app.susu.ankiconnect", "F15.3 P-V01", "http://127.0.0.1:8765", [new("apiKey", "json:/key")], Local: true, Lookup: true),
        // P-V02: Eudic OpenAPI study list; the whole Authorization value is the secret. Adding a word is idempotent by word, and
        // a word lookup confirms it, but the vendor gives no idempotency key and no operation id.
        new(Eudic, "app.susu.eudic", "F15.3 P-V02", "https://api.frdic.com", [new("apiKey", "header:Authorization")], Local: false, Lookup: true),
    ];

    public static VocabPackage? Find(string instanceId) => All.FirstOrDefault(p => p.InstanceId == instanceId);
}
