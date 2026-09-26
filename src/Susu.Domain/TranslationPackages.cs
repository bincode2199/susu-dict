using Susu.Contracts;

namespace Susu.Domain;

/// <summary>Where a package writes one secret (PLAN 4.5.2/4.5.3), e.g. <c>header:Authorization</c> or <c>signer:tencent-tc3</c>.</summary>
public sealed record CredentialTarget(string Secret, string Use);

/// <summary>
/// A built-in translation package that ships wired into the host (F06.3a): where it lives, how it is
/// shown, its per-call limits (PLAN 4.7.1; the shared F01 chunking path splits longer text) and which
/// secrets it writes where. The origin a keyed call goes to depends on the instance config (DeepL
/// plan, an explicit <c>baseUrl</c>), so grants are always computed from the current config.
/// </summary>
public sealed record TranslationPackage(string InstanceId, string PackageId, string Directory, string DisplayName, TranslationLimits Limits,
    string DefaultOrigin, IReadOnlyList<CredentialTarget> Credentials) : ICredentialPackage
{
    /// <summary>Built-in packages are unsigned installs until F16 tracks signatures; the same default Broker.Issue uses.</summary>
    public string Signer => $"unsigned:{PackageId}";

    public IReadOnlyList<string> SecretNames => [.. Credentials.Select(c => c.Secret).Distinct()];

    public string ServiceId => $"{InstanceId}/translate";

    /// <summary>The exact origin (PLAN 4.6) this package calls with <paramref name="config"/>.</summary>
    public string Origin(IReadOnlyDictionary<string, string> config)
    {
        if (config.TryGetValue("baseUrl", out var baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && Domain.Origin.TryNormalize($"{uri.Scheme}://{uri.Authority}", out var custom)) return custom;
        if (InstanceId == TranslationPackages.DeepL)
            return Domain.Origin.Normalize(config.TryGetValue("plan", out var plan) && plan == "pro" ? TranslationPackages.DeepLProOrigin : TranslationPackages.DeepLFreeOrigin);
        return Domain.Origin.Normalize(DefaultOrigin);
    }

    /// <summary>The grants the user confirms when entering this package's credentials with <paramref name="config"/>.</summary>
    public IReadOnlyList<CredentialGrant> RequiredGrants(IReadOnlyDictionary<string, string> config)
    {
        string origin = Origin(config);
        return [.. Credentials.Select(c => new CredentialGrant(PackageId, Signer, c.Secret, origin, c.Use))];
    }

    /// <summary>
    /// Config that follows from a newly entered secret. DeepL: the plugin never sees the key, so a key
    /// ending in <c>:fx</c> selects the free endpoint (<c>plan=free</c>), any other key the pro one.
    /// </summary>
    public IReadOnlyDictionary<string, string> ConfigAfterSecret(IReadOnlyDictionary<string, string> config, string secretName, string value)
    {
        if (InstanceId != TranslationPackages.DeepL || secretName != "apiKey") return config;
        return new Dictionary<string, string>(config, StringComparer.Ordinal) { ["plan"] = TranslationPackages.DeepLPlanFor(value) };
    }
}

/// <summary>The state of one credential target of a configured instance, for the settings view.</summary>
public sealed record CredentialTargetState(string Secret, string Origin, string Use, bool Saved, bool Granted);

/// <summary>An enabled, credential-complete translation service, in the user's order. <c>Dictionary</c> (F09.2, PLAN 6.1):
/// the same instance's <c>dictionary</c> service is enabled and ready too, so its card looks word forms up first.</summary>
public sealed record TranslationServicePlan(TranslationPackage Package, InstanceSettings Instance, ServiceSettings Service, bool Dictionary = false);

/// <summary>
/// The wired built-in translation packages (P-T01 MyMemory, P-T02 Tencent, P-T03 DeepL, P-T07 Youdao, P-A01 OpenAI) and
/// the resolution from settings to the ordered list of services a translation session uses (F06.3a).
/// Resolution runs on every new session, so enabling, disabling, reordering or binding a key takes
/// effect without a restart.
/// </summary>
public static class TranslationPackages
{
    public const string MyMemory = "mymemory", Tencent = "tencent-translate", DeepL = "deepl", OpenAI = "openai", Youdao = "youdao";
    public const string DeepLFreeOrigin = "https://api-free.deepl.com", DeepLProOrigin = "https://api.deepl.com";

    public static readonly IReadOnlyList<TranslationPackage> All =
    [
        // MyMemory's GET q= parameter: 500 bytes per request (P-T01).
        new(MyMemory, "app.susu.mymemory", "plugins/mymemory", "MyMemory", new TranslationLimits(InputUnit.Utf8Bytes, 500, BatchMode.Single, 1, 500),
            "https://api.mymemory.translated.net", []),
        // TextTranslate: under 6000 characters per request; the plugin refuses 6000+ before sending.
        new(Tencent, "app.susu.tencent-translate", "plugins/tencent-translate", "Tencent Translator", new TranslationLimits(InputUnit.UnicodeScalars, 5999, BatchMode.Single, 1, 5999),
            "https://tmt.tencentcloudapi.com", [new("secretId", "signer:tencent-tc3"), new("secretKey", "signer:tencent-tc3")]),
        // The plugin refuses JSON bodies over ~127 KiB; 120000 bytes of text leaves room for escapes.
        new(DeepL, "app.susu.deepl", "plugins/deepl", "DeepL", new TranslationLimits(InputUnit.Utf8Bytes, 120_000, BatchMode.Single, 1, 120_000),
            DeepLFreeOrigin, [new("apiKey", "header:Authorization")]),
        // Youdao text translation (P-T07): 5000 characters per query. appKey is injected into the reserved query
        // field and, with appSecret, is a secret part of the digest the host writes into the `sign` query field.
        new(Youdao, "app.susu.youdao", "plugins/youdao", "Youdao", new TranslationLimits(InputUnit.UnicodeScalars, 5000, BatchMode.Single, 1, 5000),
            "https://openapi.youdao.com", [new("appKey", "query:appKey"), new("appKey", "query:sign"), new("appSecret", "query:sign")]),
        // One chat completion per chunk; 6000 characters keeps the reply well inside a small model's output budget.
        new(OpenAI, "app.susu.openai", "plugins/openai", "OpenAI", new TranslationLimits(InputUnit.UnicodeScalars, 6000, BatchMode.Single, 1, 6000),
            "https://api.openai.com", [new("apiKey", "header:Authorization")]),
    ];

    public static TranslationPackage? Find(string instanceId) => All.FirstOrDefault(p => p.InstanceId == instanceId);

    /// <summary>Whether a wired package also offers the <c>dictionary</c> capability (only Youdao, PLAN 6.1). Its dictionary
    /// service uses the package's own credentials and grants, so it is ready exactly when they are.</summary>
    public static bool SupportsDictionary(string instanceId)
        => Find(instanceId) is not null && BuiltInCatalog.Find(instanceId) is { } catalog && catalog.Capabilities.Contains(Capability.Dictionary);

    public static string DeepLPlanFor(string key) => key.Trim().EndsWith(":fx", StringComparison.Ordinal) ? "free" : "pro";

    /// <summary>Saved/granted state of each credential target of <paramref name="instance"/> under its current config.</summary>
    public static IReadOnlyList<CredentialTargetState> CredentialStates(AppSettings settings, TranslationPackage package, InstanceSettings instance, Func<string, string, bool> hasSecret)
        => CredentialPackages.States(settings, package, instance, hasSecret);

    /// <summary>Ready only when every secret is saved and granted for the package, origin and use it will be written to.</summary>
    public static Availability AvailabilityOf(AppSettings settings, ServiceSettings service, Func<string, string, bool> hasSecret)
    {
        if (!service.Enabled) return Availability.Disabled;
        if (service.Capability != Capability.Translate && !(service.Capability == Capability.Dictionary && SupportsDictionary(service.Instance))) return Availability.UnsupportedCapability;
        var package = Find(service.Instance);
        var instance = settings.Instances.FirstOrDefault(i => i.Id == service.Instance);
        if (package is null || instance is null || instance.Package != package.PackageId) return Availability.UnsupportedCapability;
        return CredentialStates(settings, package, instance, hasSecret).All(c => c.Saved && c.Granted) ? Availability.Ready : Availability.MissingCredential;
    }

    /// <summary>
    /// Enabled, ready translation services in <see cref="AppSettings.TranslationOrder"/> (services missing
    /// from the order follow in settings order). Unimplemented packages and services without a usable
    /// credential are left out rather than shown as cards that can only fail.
    /// </summary>
    public static IReadOnlyList<TranslationServicePlan> Resolve(AppSettings settings, Func<string, string, bool> hasSecret)
    {
        var translate = settings.Services.Where(s => s.Capability == Capability.Translate).ToList();
        var ordered = settings.TranslationOrder.Select(id => translate.FirstOrDefault(s => s.ServiceId == id)).OfType<ServiceSettings>()
            .Concat(translate.Where(s => !settings.TranslationOrder.Contains(s.ServiceId))).Distinct();
        var result = new List<TranslationServicePlan>();
        foreach (var service in ordered)
        {
            if (AvailabilityOf(settings, service, hasSecret) != Availability.Ready) continue;
            var dictionary = settings.Services.FirstOrDefault(s => s.Instance == service.Instance && s.Capability == Capability.Dictionary);
            bool lookup = dictionary is not null && AvailabilityOf(settings, dictionary, hasSecret) == Availability.Ready;
            result.Add(new TranslationServicePlan(Find(service.Instance)!, settings.Instances.First(i => i.Id == service.Instance), service, lookup));
        }
        return result;
    }
}
