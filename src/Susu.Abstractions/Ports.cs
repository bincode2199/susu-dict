using Susu.Contracts;
using Susu.Domain;

namespace Susu.Abstractions;

/// <summary>Monotonic time source for jobs (deterministic in tests).</summary>
public interface IClock
{
    /// <summary>Monotonic milliseconds; only differences are meaningful.</summary>
    long NowMilliseconds { get; }
    DateTimeOffset UtcNow { get; }
    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>Uniform random value in [0, 1) for bounded retry jitter.</summary>
public interface IJitter
{
    double Next();
}

public sealed record TranslateCall(string Text, string From, string To, string AttemptId, ConfigSnapshot Config, TimeSpan Timeout);

public abstract record ProviderOutcome
{
    public sealed record Success(string Text, string? DetectedFrom = null) : ProviderOutcome;
    public sealed record Failure(ProviderError Error) : ProviderOutcome;
}

/// <summary>
/// A translation provider as seen by jobs: the plugin provider (F04/F05) and native providers implement
/// it; test doubles live only in test assemblies. Streaming providers call <c>onChunk</c> with text pieces.
/// </summary>
public interface ITranslationProvider
{
    string ServiceId { get; }
    string DisplayName { get; }
    /// <summary>Limiter identity shared by providers using the same account and origin (ARCHITECTURE 5.1).</summary>
    string LimiterKey { get; }
    TranslationLimits Limits { get; }
    bool SupportsLanguagePair(string from, string to);
    Task<ProviderOutcome> TranslateAsync(TranslateCall call, Func<string, ValueTask> onChunk, CancellationToken cancellationToken);
}

/// <summary>Ranked language candidates; null or empty when unknown (PLAN 3.2).</summary>
public interface ILanguageDetector
{
    Task<IReadOnlyList<string>> DetectAsync(string text, CancellationToken cancellationToken);
}

/// <summary>Usage is recorded per attempt and deduplicated by (attemptId, metric) (DATA04).</summary>
public interface IUsageSink
{
    void Record(string serviceId, string attemptId, string metric, long units, string outcome);
}

/// <summary>A feature entry point and whether it may be offered (ARCHITECTURE 12, UI06).</summary>
public enum FeatureState { Available, InDevelopment, Unavailable }

public sealed record FeatureDescriptor(string Id, FeatureState State, string? UnavailableReasonKey, IReadOnlyList<Capability> RequiredCapabilities);
