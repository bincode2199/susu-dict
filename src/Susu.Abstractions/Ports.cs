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

public sealed record DictionaryCall(string Word, string From, string To, string AttemptId, ConfigSnapshot Config, TimeSpan Timeout);

public abstract record DictionaryOutcome
{
    /// <summary>A successful lookup. It may be a legal empty entry (<see cref="DictionaryEntries.IsEmpty"/>).</summary>
    public sealed record Entry(DictionaryResult Result) : DictionaryOutcome;
    public sealed record Failure(ProviderError Error) : DictionaryOutcome;
}

/// <summary>
/// The <c>dictionary</c> capability of the same provider instance as a translation card (PLAN 6.1, F09.2). A
/// translation provider that also implements this is a dictionary source only while <see cref="DictionaryEnabled"/>
/// (the instance's dictionary service is enabled in settings); the session then looks a word form up first on
/// that card and falls back to <see cref="ITranslationProvider.TranslateAsync"/> only on a legal empty entry.
/// </summary>
public interface IDictionaryProvider
{
    bool DictionaryEnabled { get; }
    Task<DictionaryOutcome> LookupAsync(DictionaryCall call, CancellationToken cancellationToken);
}

/// <summary>An audio link of a shown dictionary entry and the service whose card shows it (F09.3). Host-only: the UI sees only the opaque audio id.</summary>
public sealed record DictionaryAudioLink(string ServiceId, string Url);

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
