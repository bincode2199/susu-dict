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

/// <summary>
/// F15.4: whether a vocabulary service instance can take deliveries now. <see cref="ReasonKey"/> is the page's i18n key of the first
/// thing missing (vocab.reason.*); <see cref="Origin"/> is the exact address it calls.
/// </summary>
public sealed record VocabTargetState(bool Enabled, bool Usable, Availability Availability, string? ReasonKey, string Origin);

public static class VocabTargets
{
    public const string ReasonDisabled = "vocab.reason.disabled", ReasonMissingKey = "vocab.reason.missingKey", ReasonNotGranted = "vocab.reason.notGranted",
        ReasonOriginNotLocal = "vocab.reason.originNotLocal";

    /// <summary>The state of <paramref name="instanceId"/> under <paramref name="settings"/>; null when it is not a vocabulary package instance.</summary>
    /// <remarks>
    /// AnkiConnect (a <see cref="VocabPackage.Local"/> package): its address is approved by the user typing it into this page, but only a loopback
    /// address is ever approved without another step, so any other address makes the target unusable (the origin is the user's own AnkiConnect,
    /// never a remote host). Its key is optional: it counts only when "use an API key" is on. Eudic needs its Authorization value saved and granted.
    /// </remarks>
    public static VocabTargetState? Evaluate(AppSettings settings, string instanceId, Func<string, string, bool> hasSecret)
    {
        if (VocabCatalog.Find(instanceId) is not { } package) return null;
        var instance = settings.Instances.FirstOrDefault(i => i.Id == instanceId);
        if (instance is null || instance.Package != package.PackageId) return null;
        bool enabled = settings.Services.Any(s => s.Instance == instanceId && s.Capability == Capability.Vocab && s.Enabled);
        string origin = package.Origin(instance.Config);
        if (!enabled) return new VocabTargetState(false, false, Availability.Disabled, ReasonDisabled, origin);
        if (package.Local && !VocabPackage.IsLoopback(origin)) return new VocabTargetState(true, false, Availability.TemporarilyUnavailable, ReasonOriginNotLocal, origin);
        bool needsKey = !package.Local || (instance.Config.TryGetValue("useApiKey", out var use) && use is "true" or "True");
        if (needsKey)
        {
            var states = CredentialPackages.States(settings, package, instance, hasSecret);
            if (!states.All(t => t.Saved)) return new VocabTargetState(true, false, Availability.MissingCredential, ReasonMissingKey, origin);
            if (!states.All(t => t.Granted)) return new VocabTargetState(true, false, Availability.MissingCredential, ReasonNotGranted, origin);
        }
        return new VocabTargetState(true, true, Availability.Ready, null, origin);
    }

    /// <summary>The instance ids of every vocabulary service that is enabled and has what it needs (key saved and granted, a local address), in catalog order.</summary>
    public static IReadOnlyList<string> Usable(AppSettings settings, Func<string, string, bool> hasSecret)
        => [.. VocabCatalog.All.Where(p => Evaluate(settings, p.InstanceId, hasSecret) is { Usable: true }).Select(p => p.InstanceId)];
}
