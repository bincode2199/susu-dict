using Susu.Abstractions;
using Susu.Contracts;

namespace Susu.Jobs;

/// <summary>
/// Feature modules register here; entry points (tray, hotkeys, windows) are offered only for
/// <see cref="FeatureState.Available"/> features (ARCHITECTURE 12, UI06). Unimplemented modules are
/// either absent or InDevelopment and never shown as working entry points in a release build.
/// </summary>
public sealed class FeatureRegistry
{
    public static class Ids
    {
        public const string InputTranslation = "input-translation", Selection = "selection", Clipboard = "clipboard", Ocr = "ocr",
            Voice = "voice", SystemAudio = "system-audio", Transcription = "transcription", Pronunciation = "pronunciation", Vocabulary = "vocabulary";
    }

    private readonly Dictionary<string, FeatureDescriptor> features = new(StringComparer.Ordinal);

    public void Register(FeatureDescriptor feature)
    {
        if (!features.TryAdd(feature.Id, feature)) throw new InvalidOperationException($"feature {feature.Id} registered twice");
    }

    public IReadOnlyCollection<FeatureDescriptor> All => features.Values;

    /// <summary>
    /// Available only when registered as implemented and every required capability has a ready service;
    /// otherwise returns the reason key for the disabled entry (e.g. no OCR service configured).
    /// </summary>
    public (FeatureState State, string? ReasonKey) Resolve(string id, Func<Capability, bool> capabilityReady)
    {
        if (!features.TryGetValue(id, out var feature)) return (FeatureState.Unavailable, "feature.notRegistered");
        if (feature.State != FeatureState.Available) return (feature.State, feature.UnavailableReasonKey ?? "feature.inDevelopment");
        var missing = feature.RequiredCapabilities.FirstOrDefault(c => !capabilityReady(c), (Capability)(-1));
        return (int)missing >= 0 ? (FeatureState.Unavailable, $"feature.noService.{missing.ToString().ToLowerInvariant()}") : (FeatureState.Available, null);
    }

    /// <summary>Entry points a release build may show: only resolved-available features.</summary>
    public IReadOnlyList<string> EntryPoints(Func<Capability, bool> capabilityReady, bool developmentBuild)
        => [.. features.Values.Where(f => Resolve(f.Id, capabilityReady).State == FeatureState.Available || (developmentBuild && f.State == FeatureState.InDevelopment)).Select(f => f.Id)];
}
