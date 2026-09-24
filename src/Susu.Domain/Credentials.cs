using System.Globalization;

namespace Susu.Domain;

/// <summary>
/// Exact origin (PLAN 4.6): scheme + host + explicit port. No bare domains, wildcards, paths, queries or
/// userinfo. IDN hosts are compared in their ASCII form; <c>https://example.com</c> equals <c>:443</c>.
/// </summary>
public static class Origin
{
    private static readonly IdnMapping idn = new() { AllowUnassigned = false, UseStd3AsciiRules = true };

    public static bool TryNormalize(string? text, out string origin)
    {
        origin = "";
        if (string.IsNullOrWhiteSpace(text) || text.Contains('*') || text != text.Trim()) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("https" or "http" or "wss" or "ws")) return false;
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        if (uri.AbsolutePath != "/" || text.TrimEnd('/').Count(c => c == '/') > 2) return false;
        string host;
        if (uri.HostNameType == UriHostNameType.IPv6) host = $"[{uri.IdnHost.Trim('[', ']').ToLowerInvariant()}]";
        else if (uri.HostNameType == UriHostNameType.IPv4) host = uri.Host;
        else
        {
            try { host = idn.GetAscii(uri.Host.TrimEnd('.')).ToLowerInvariant(); }
            catch (ArgumentException) { return false; }
        }
        origin = $"{uri.Scheme}://{host}:{uri.Port}";
        return true;
    }

    public static string Normalize(string text) => TryNormalize(text, out var origin) ? origin : throw new ArgumentException($"'{text}' is not an exact origin.", nameof(text));
}

/// <summary>Package id plus confirmed signing identity, or <c>unsigned:&lt;installationId&gt;</c> for unsigned installs.</summary>
public sealed record PluginIdentity(string PackageId, string Signer);

/// <summary>
/// Where a credential may be written (PLAN 4.5.2/4.5.3): <c>header:Authorization</c>, <c>query:key</c>,
/// <c>json:/auth/key</c> or <c>signer:tencent-tc3</c>.
/// </summary>
public sealed record CredentialGrant(string Package, string Signer, string Secret, string Origin, string Use)
{
    public bool Covers(PluginIdentity identity, string secret, string origin, string use)
        => Package == identity.PackageId && Signer == identity.Signer && Secret == secret && Origin == origin && Use == use;
}

public enum CredentialDecision { Allowed, NotBound, UnknownAccount, IdentityChanged, OriginNotGranted, UseNotGranted, InvalidOrigin }

/// <summary>
/// Credential authorization (PLAN 4.5.4, S02): only an explicit user binding of instance secret name →
/// account, plus a grant for the exact plugin identity, origin and write location, releases a secret.
/// Declaring a same-named secret, replacing the package identity or changing origin/use never inherits
/// an existing account.
/// </summary>
public static class CredentialAuthorizer
{
    public static (CredentialDecision Decision, string? AccountId) Authorize(
        IReadOnlyList<AccountSettings> accounts, InstanceSettings instance, PluginIdentity identity, string secret, string origin, string use)
    {
        if (!Origin.TryNormalize(origin, out var normalized)) return (CredentialDecision.InvalidOrigin, null);
        if (instance.Package != identity.PackageId) return (CredentialDecision.IdentityChanged, null);
        if (!instance.AccountBindings.TryGetValue(secret, out var accountId)) return (CredentialDecision.NotBound, null);
        var account = accounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null || !account.Secrets.Contains(secret)) return (CredentialDecision.UnknownAccount, null);
        var sameSecret = account.Grants.Where(g => g.Secret == secret).ToList();
        if (sameSecret.Count == 0 || !sameSecret.Any(g => g.Package == identity.PackageId && g.Signer == identity.Signer)) return (CredentialDecision.IdentityChanged, null);
        var mine = sameSecret.Where(g => g.Package == identity.PackageId && g.Signer == identity.Signer).ToList();
        if (!mine.Any(g => g.Origin == normalized)) return (CredentialDecision.OriginNotGranted, null);
        return mine.Any(g => g.Covers(identity, secret, normalized, use)) ? (CredentialDecision.Allowed, accountId) : (CredentialDecision.UseNotGranted, null);
    }

    /// <summary>
    /// Grants a manifest's credentialUse would need that the user has not confirmed yet. The caller shows
    /// them for confirmation; nothing here extends an existing grant (PLAN 4.6 "待确认差异").
    /// </summary>
    public static IReadOnlyList<CredentialGrant> PendingConfirmation(AccountSettings account, IEnumerable<CredentialGrant> requested)
        => requested.Where(r => !account.Grants.Contains(r)).Distinct().ToList();

    /// <summary>Applies grants the user explicitly confirmed; returns a new account (settings are immutable).</summary>
    public static AccountSettings Confirm(AccountSettings account, IEnumerable<CredentialGrant> confirmed)
    {
        var grants = account.Grants.ToList();
        foreach (var grant in confirmed)
        {
            if (!Origin.TryNormalize(grant.Origin, out var normalized) || normalized != grant.Origin) throw new ArgumentException($"grant origin '{grant.Origin}' is not normalized");
            if (!account.Secrets.Contains(grant.Secret)) throw new ArgumentException($"account '{account.Id}' has no secret '{grant.Secret}'");
            if (!grants.Contains(grant)) grants.Add(grant);
        }
        return account with { Grants = grants };
    }
}
