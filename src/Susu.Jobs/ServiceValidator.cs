using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;

namespace Susu.Jobs;

public enum CredentialValidity { Valid, Invalid, Unknown }

/// <summary>
/// Result of a key-configuration "validate" action (F06.2a): separates whether the credential itself is
/// good from whether the service can currently be reached with it. A quota/rate-limit response proves
/// the key authenticated - the UI must not tell the user their key is wrong when the real problem is
/// "try again later" or "upgrade your plan".
/// </summary>
public sealed record ServiceValidation(CredentialValidity Credential, bool ServiceAvailable, ErrorKind? Error, string? Detail)
{
    public static readonly ServiceValidation Ok = new(CredentialValidity.Valid, true, null, null);
}

/// <summary>
/// "Validate" action for a keyed service instance (F06.2a, DEV-PLAN F06.2): calls the configured
/// provider with a minimal real request and classifies auth/quota/network errors instead of the UI
/// treating "the key is wrong" and "the service is temporarily unreachable" as the same failure.
///
/// Runs through the exact <see cref="ITranslationProvider.TranslateAsync"/> path a real translation
/// would - for a plugin-backed provider that is <c>PluginProvider</c>'s own Acquire/secrets/config
/// wiring (Broker's S02 account-binding check included) - so a passing validation is proof the account
/// binding and the plugin's own request shape both work, not just that a key string was typed in.
/// The credential's actual value is never read or logged here; it flows to the vendor the same way any
/// other call's does, through <see cref="ISecretStore"/> inside the network layer (S07).
/// </summary>
public static class ServiceValidator
{
    /// <summary>Short, deterministic probe text - real language codes so a language-pair-picky vendor
    /// does not itself report unsupported_language for the probe alone.</summary>
    public const string ProbeText = "hello";

    public static async Task<ServiceValidation> ValidateAsync(ITranslationProvider provider, string from, string to, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!provider.SupportsLanguagePair(from, to))
            return new ServiceValidation(CredentialValidity.Unknown, false, ErrorKind.UnsupportedLanguage, "probe language pair not supported");
        var call = new TranslateCall(ProbeText, from, to, Guid.NewGuid().ToString("N"), new ConfigSnapshot(0, 0, 0, 0, timeout), timeout);
        ProviderOutcome outcome;
        try { outcome = await provider.TranslateAsync(call, static _ => ValueTask.CompletedTask, cancellationToken); }
        catch (OperationCanceledException) { return new ServiceValidation(CredentialValidity.Unknown, false, ErrorKind.Cancelled, null); }
        return outcome switch
        {
            ProviderOutcome.Success => ServiceValidation.Ok,
            ProviderOutcome.Failure f => Classify(f.Error),
            _ => new ServiceValidation(CredentialValidity.Unknown, false, ErrorKind.BadResponse, "unrecognized provider outcome"),
        };
    }

    private static ServiceValidation Classify(ProviderError error) => error.Kind switch
    {
        // The vendor rejected the credential itself: "credential valid" must be false, not merely unknown.
        ErrorKind.Auth => new ServiceValidation(CredentialValidity.Invalid, false, error.Kind, error.Detail),
        // Quota/rate-limit responses prove the credential authenticated: the key is good, the *service*
        // just cannot serve this call right now.
        ErrorKind.Quota or ErrorKind.RateLimited => new ServiceValidation(CredentialValidity.Valid, false, error.Kind, error.Detail),
        // Network/timeout/unavailable/bad_response/unsupported_language say nothing about the
        // credential either way - the probe simply did not get a definitive answer.
        _ => new ServiceValidation(CredentialValidity.Unknown, false, error.Kind, error.Detail),
    };
}
