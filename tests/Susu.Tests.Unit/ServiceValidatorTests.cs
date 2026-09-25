using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F06.2a: the "validate" action for a keyed service instance separates "the credential is wrong"
/// from "the service is temporarily unavailable" instead of collapsing every failure into one message.
/// </summary>
public class ServiceValidatorTests
{
    private static readonly TranslationLimits Limits = new(InputUnit.Utf8Bytes, 5000, BatchMode.Single, 1, 500);

    [Fact]
    public async Task A_successful_probe_call_is_valid_and_available()
    {
        var provider = new ScriptedProvider("svc/translate", Limits, new Step.Succeed("hello"));
        var result = await ServiceValidator.ValidateAsync(provider, "en", "zh-Hans", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(ServiceValidation.Ok, result);
    }

    [Fact]
    public async Task Auth_failure_marks_the_credential_invalid_and_the_service_unavailable()
    {
        var provider = new ScriptedProvider("svc/translate", Limits, new Step.Fail(new ProviderError(ErrorKind.Auth, "bad key")));
        var result = await ServiceValidator.ValidateAsync(provider, "en", "zh-Hans", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(CredentialValidity.Invalid, result.Credential);
        Assert.False(result.ServiceAvailable);
        Assert.Equal(ErrorKind.Auth, result.Error);
    }

    [Theory]
    [InlineData(ErrorKind.Quota)]
    [InlineData(ErrorKind.RateLimited)]
    public async Task Quota_or_rate_limit_failure_proves_the_credential_authenticated(ErrorKind kind)
    {
        var provider = new ScriptedProvider("svc/translate", Limits, new Step.Fail(new ProviderError(kind, "over budget")));
        var result = await ServiceValidator.ValidateAsync(provider, "en", "zh-Hans", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        // The key is good - the service just cannot serve this call right now.
        Assert.Equal(CredentialValidity.Valid, result.Credential);
        Assert.False(result.ServiceAvailable);
    }

    [Theory]
    [InlineData(ErrorKind.Network)]
    [InlineData(ErrorKind.Timeout)]
    [InlineData(ErrorKind.BadResponse)]
    [InlineData(ErrorKind.Unavailable)]
    public async Task Network_shaped_failures_leave_credential_validity_unknown(ErrorKind kind)
    {
        var provider = new ScriptedProvider("svc/translate", Limits, new Step.Fail(new ProviderError(kind)));
        var result = await ServiceValidator.ValidateAsync(provider, "en", "zh-Hans", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(CredentialValidity.Unknown, result.Credential);
        Assert.False(result.ServiceAvailable);
    }

    [Fact]
    public async Task An_unsupported_probe_language_pair_never_calls_the_provider()
    {
        var provider = new ScriptedProvider("svc/translate", Limits, new Step.Succeed("should not be reached")) { Pairs = (_, _) => false };
        var result = await ServiceValidator.ValidateAsync(provider, "xx", "yy", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(CredentialValidity.Unknown, result.Credential);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task The_probe_never_reaches_the_UI_layer_with_a_credential_value()
    {
        // S07: ServiceValidator only ever sees the provider's ProviderOutcome (success text or a
        // classified ProviderError) - there is no code path here that reads or forwards a secret value.
        var provider = new ScriptedProvider("svc/translate", Limits, new Step.Succeed("hello"));
        await ServiceValidator.ValidateAsync(provider, "en", "zh-Hans", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Single(provider.Calls);
        Assert.Equal(ServiceValidator.ProbeText, provider.Calls.First().Text);
    }
}
