using Susu.Contracts;

namespace Susu.Domain;

/// <summary>
/// A package whose secrets are released under grants (PLAN 4.5.4): the wired translation packages and the
/// catalog entries of the speech packages (F07.4). Grants are always computed from the instance's current config.
/// </summary>
public interface ICredentialPackage
{
    string InstanceId { get; }
    string PackageId { get; }
    string Signer { get; }
    IReadOnlyList<string> SecretNames { get; }
    string Origin(IReadOnlyDictionary<string, string> config);
    IReadOnlyList<CredentialGrant> RequiredGrants(IReadOnlyDictionary<string, string> config);
}

public static class CredentialPackages
{
    public static ICredentialPackage? Find(string instanceId)
        => (ICredentialPackage?)TranslationPackages.Find(instanceId) ?? (SpeechCatalog.Find(instanceId) is { Credentials.Count: > 0 } speech ? speech : OcrCatalog.Find(instanceId));

    /// <summary>Saved/granted state of each credential target of <paramref name="instance"/> under its current config.</summary>
    public static IReadOnlyList<CredentialTargetState> States(AppSettings settings, ICredentialPackage package, InstanceSettings instance, Func<string, string, bool> hasSecret)
        => [.. package.RequiredGrants(instance.Config).Select(g =>
        {
            bool saved = instance.AccountBindings.TryGetValue(g.Secret, out var account) && hasSecret(account, g.Secret);
            var (decision, _) = CredentialAuthorizer.Authorize(settings.Accounts, instance, new PluginIdentity(package.PackageId, package.Signer), g.Secret, g.Origin, g.Use);
            return new CredentialTargetState(g.Secret, g.Origin, g.Use, saved, decision == CredentialDecision.Allowed);
        })];
}

/// <summary>The three independent speech selections of SetSpeech/SetSpeechB (F07.4, A02).</summary>
public enum SpeechSlot { Tts, Asr, VideoAsr }

/// <summary>A selected service instance and model; <c>""</c> instance = nothing selected, <c>""</c> model = the package has no model choice.</summary>
public sealed record SpeechSelection(string Instance, string Model)
{
    public static readonly SpeechSelection None = new("", "");
}

/// <summary>
/// SetSpeech (pronunciation service) and SetSpeechB (recording/audio transcription and video transcription, chosen
/// separately). Each is its own field: changing one never touches the others, nor the AI translation model, which is
/// the <c>openai</c> instance's config (A02).
/// </summary>
public sealed record SpeechSettings(SpeechSelection Tts, SpeechSelection Asr, SpeechSelection VideoAsr)
{
    /// <summary>DESIGN SetSpeechB: OpenAI whisper-1 for both transcription groups; native SAPI pronounces.</summary>
    public static SpeechSettings Default => new(new(BuiltInCatalog.NativeTts, ""), new(SpeechCatalog.OpenAiAsr, "whisper-1"), new(SpeechCatalog.OpenAiAsr, "whisper-1"));

    public SpeechSelection this[SpeechSlot slot] => slot switch { SpeechSlot.Tts => Tts, SpeechSlot.Asr => Asr, _ => VideoAsr };

    public SpeechSettings With(SpeechSlot slot, SpeechSelection selection) => slot switch
    {
        SpeechSlot.Tts => this with { Tts = selection },
        SpeechSlot.Asr => this with { Asr = selection },
        _ => this with { VideoAsr = selection },
    };
}

/// <summary>Timecodes: the model returns segments with start/end (verbose_json). A text-only model never gets invented times (A03).</summary>
public sealed record SpeechModel(string Id, bool Timecodes);

/// <summary>
/// Catalog metadata of a planned speech service (DEV-PLAN 5: P-S01–P-S03, P-R01, P-R02, and native SAPI): what it
/// can do, where its key goes, and whether it exists yet. Installed=false: the package ships in <see cref="Plan"/>;
/// it can be selected and given a shared account now, but nothing is called until it is installed. Installed plugin
/// entries (P-S01–P-S03, F10.1) ship under <see cref="Directory"/> and load into the plugin host.
/// </summary>
public sealed record SpeechPackage(string InstanceId, string PackageId, string Plan, Capability Capability, bool Native, bool Installed,
    string DefaultOrigin, IReadOnlyList<CredentialTarget> Credentials, IReadOnlyList<SpeechModel> Models) : ICredentialPackage
{
    public string Signer => $"unsigned:{PackageId}";
    public IReadOnlyList<string> SecretNames => [.. Credentials.Select(c => c.Secret).Distinct()];

    /// <summary>At least one model returns timecodes, so the package may serve video transcription.</summary>
    public bool Timecodes => Models.Any(m => m.Timecodes);

    /// <summary>Where the shipped package lives under the install folder (plugin packages only).</summary>
    public string Directory => $"plugins/{InstanceId}";

    /// <summary>
    /// The exact origin this package calls with <paramref name="config"/>: an explicit <c>baseUrl</c>, else for P-S01 the
    /// configured Azure Speech region (<c>https://{region}.tts.speech.microsoft.com</c>), else the default.
    /// </summary>
    public string Origin(IReadOnlyDictionary<string, string> config)
    {
        if (config.TryGetValue("baseUrl", out var baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && Domain.Origin.TryNormalize($"{uri.Scheme}://{uri.Authority}", out var custom)) return custom;
        if (InstanceId == SpeechCatalog.MicrosoftTts && config.TryGetValue("region", out var region) && SpeechCatalog.IsAzureRegion(region))
            return Domain.Origin.Normalize($"https://{region}.tts.speech.microsoft.com");
        return Domain.Origin.Normalize(DefaultOrigin);
    }

    public IReadOnlyList<CredentialGrant> RequiredGrants(IReadOnlyDictionary<string, string> config)
    {
        string origin = Origin(config);
        return [.. Credentials.Select(c => new CredentialGrant(PackageId, Signer, c.Secret, origin, c.Use))];
    }
}

public static class SpeechCatalog
{
    public const string OpenAiAsr = "openai-asr", GeminiAsr = "gemini-asr";
    public const string MicrosoftTts = "microsoft-tts", GoogleTts = "google-tts", TencentTts = "tencent-tts";

    /// <summary>An Azure region name as used in the Speech endpoint host (e.g. <c>eastus</c>, <c>westeurope</c>).</summary>
    public static bool IsAzureRegion(string? region) => region is { Length: > 0 and <= 32 } && region.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');

    /// <summary>Installed plugin packages (not native) that ship under <c>plugins/</c> and load into the plugin host.</summary>
    public static IEnumerable<SpeechPackage> InstalledPlugins => All.Where(p => p.Installed && !p.Native);

    /// <summary>Reason codes of <see cref="Check"/>; the page shows the matching explanation.</summary>
    public const string WrongCapability = "capability", NeedsTimecodes = "needs-timecodes", UnknownModel = "model";

    public static readonly IReadOnlyList<SpeechPackage> All =
    [
        new(BuiltInCatalog.NativeTts, "native.sapi", "F10.1", Capability.Tts, Native: true, Installed: true, "", [], []),
        new(MicrosoftTts, "app.susu.microsoft-tts", "F10.1 P-S01", Capability.Tts, false, true,
            "https://eastus.tts.speech.microsoft.com", [new("apiKey", "header:Ocp-Apim-Subscription-Key")], []),
        new(GoogleTts, "app.susu.google-tts", "F10.1 P-S02", Capability.Tts, false, true,
            "https://texttospeech.googleapis.com", [new("apiKey", "header:X-Goog-Api-Key")], []),
        new(TencentTts, "app.susu.tencent-tts", "F10.1 P-S03", Capability.Tts, false, true,
            "https://tts.tencentcloudapi.com", [new("secretId", "signer:tencent-tc3"), new("secretKey", "signer:tencent-tc3")], []),
        // P-R01: whisper-1 returns segments (verbose_json); the gpt-4o transcribe models return text only.
        new(OpenAiAsr, "app.susu.openai-asr", "F12.2 P-R01", Capability.Asr, false, false,
            "https://api.openai.com", [new("apiKey", "header:Authorization")],
            [new("whisper-1", true), new("gpt-4o-transcribe", false), new("gpt-4o-mini-transcribe", false)]),
        // P-R02: inlineData in, text out; no timecodes, so never offered for video.
        new(GeminiAsr, "app.susu.gemini-asr", "F12.2 P-R02", Capability.Asr, false, false,
            "https://generativelanguage.googleapis.com", [new("apiKey", "header:X-Goog-Api-Key")],
            [new("gemini-2.5-flash", false)]),
    ];

    public static SpeechPackage? Find(string instanceId) => All.FirstOrDefault(p => p.InstanceId == instanceId);

    public static Capability CapabilityOf(SpeechSlot slot) => slot == SpeechSlot.Tts ? Capability.Tts : Capability.Asr;

    /// <summary>The packages listed for a slot: those declaring the slot's capability, in catalog order.</summary>
    public static IReadOnlyList<SpeechPackage> Choices(SpeechSlot slot) => [.. All.Where(p => p.Capability == CapabilityOf(slot))];

    /// <summary>
    /// Why <paramref name="selection"/> cannot fill <paramref name="slot"/>, or null. Video transcription needs a
    /// model with timecodes (A03); an empty instance means nothing is selected and is always allowed.
    /// </summary>
    public static string? Check(SpeechSlot slot, SpeechSelection selection)
    {
        if (selection.Instance.Length == 0) return selection.Model.Length == 0 ? null : UnknownModel;
        if (Find(selection.Instance) is not { } package || package.Capability != CapabilityOf(slot)) return WrongCapability;
        if (package.Models.Count == 0) return selection.Model.Length == 0 ? null : UnknownModel;
        if (package.Models.FirstOrDefault(m => m.Id == selection.Model) is not { } model) return UnknownModel;
        return slot == SpeechSlot.VideoAsr && !model.Timecodes ? NeedsTimecodes : null;
    }

    /// <summary>Why a whole package cannot be chosen for a slot (a text-only ASR for video), or null.</summary>
    public static string? CheckPackage(SpeechSlot slot, SpeechPackage package)
        => package.Capability != CapabilityOf(slot) ? WrongCapability : slot == SpeechSlot.VideoAsr && !package.Timecodes ? NeedsTimecodes : null;
}
