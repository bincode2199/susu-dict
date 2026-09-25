using Susu.Contracts;

namespace Susu.Domain;

/// <summary>
/// Non-sensitive user settings (PLAN 5.2, ARCHITECTURE 8.1). Secrets never appear here: accounts carry
/// only labels, secret names and grants; the values live in secrets.dat.
/// </summary>
public sealed record AppSettings(
    int SchemaVersion,
    long Revision,
    GeneralSettings General,
    HotkeySettings Hotkeys,
    NetworkSettings Network,
    IReadOnlyList<AccountSettings> Accounts,
    IReadOnlyList<InstanceSettings> Instances,
    IReadOnlyList<ServiceSettings> Services,
    IReadOnlyList<string> TranslationOrder,
    IReadOnlyList<PromptProfile> Prompts,
    PromptSettings Prompt)
{
    public const int CurrentSchemaVersion = 1;

    public ConfigSnapshot Snapshot() => new(Revision, Revision, Revision, General.DefaultExpandedCards, TimeSpan.FromSeconds(Network.AiTimeoutSeconds));

    /// <summary>Structural equality (records compare list references, which is not useful for settings).</summary>
    public bool ContentEquals(AppSettings other) => SettingsText.Canonical(this with { Revision = 0 }) == SettingsText.Canonical(other with { Revision = 0 });
}

public enum CloseAction { Hide, Exit }

public sealed record GeneralSettings(
    string UiLanguage,
    string SourceLanguage,
    string TargetLanguage,
    string DetectionService,
    int DefaultExpandedCards,
    bool AllowClipboardBorrowing,
    CloseAction CloseAction,
    bool LaunchAtStartup,
    string Theme);

/// <summary>Hotkey chords such as <c>Alt+A</c>; empty means unassigned.</summary>
public sealed record HotkeySettings(IReadOnlyDictionary<string, string> Chords)
{
    public static readonly string[] Actions = ["inputTranslate", "selectionTranslate", "clipboardTranslate", "ocrTranslate", "voiceTranslate", "audioTranslate", "videoTranscribe", "pronounce"];

    /// <summary>Only selection and clipboard translation may share a chord (PLAN 1.2, CFG05).</summary>
    public IReadOnlyList<(string Chord, string[] Actions)> Conflicts()
        => Chords.Where(kv => kv.Value.Length > 0)
            .GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToArray()))
            .Where(g => g.Item2.Length > 1 && !(g.Item2.Length == 2 && g.Item2.Contains("selectionTranslate") && g.Item2.Contains("clipboardTranslate")))
            .ToList();
}

public enum ProxyMode { System, None, Http, Socks5 }

/// <summary>Proxy password is a secret (account <see cref="NetworkSettings.ProxyAccountId"/>), never a field here.</summary>
public sealed record NetworkSettings(ProxyMode ProxyMode, string ProxyHost, int ProxyPort, string ProxyUsername, int AiTimeoutSeconds)
{
    public const string ProxyAccountId = "proxy";
}

/// <summary>A credential account: which secret names exist and which plugin identities may use them where.</summary>
public sealed record AccountSettings(string Id, string Label, IReadOnlyList<string> Secrets, IReadOnlyList<CredentialGrant> Grants);

public sealed record InstanceSettings(
    string Id,
    string Package,
    long Revision,
    IReadOnlyDictionary<string, string> Config,
    IReadOnlyDictionary<string, string> AccountBindings);

public sealed record ServiceSettings(string Instance, Capability Capability, bool Enabled)
{
    public string ServiceId => $"{Instance}/{Capability.ToString().ToLowerInvariant()}";
}

/// <summary>A custom SetPrompt template (F07.3); variables are substituted in one pass (<see cref="PromptTemplate"/>).</summary>
public sealed record PromptProfile(string Id, string Name, string Template);

/// <summary>Deterministic text form used for equality and hashing (not the YAML file format).</summary>
public static class SettingsText
{
    public static string Canonical(AppSettings s)
    {
        var b = new System.Text.StringBuilder();
        b.Append(s.SchemaVersion).Append('|').Append(s.Revision).Append('|').Append(s.General).Append('|');
        foreach (var kv in s.Hotkeys.Chords.OrderBy(k => k.Key, StringComparer.Ordinal)) b.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
        b.Append('|').Append(s.Network).Append('|');
        foreach (var a in s.Accounts) b.Append(a.Id).Append(',').Append(a.Label).Append(',').AppendJoin(',', a.Secrets).Append(',').AppendJoin(',', a.Grants).Append(';');
        b.Append('|');
        foreach (var i in s.Instances)
        {
            b.Append(i.Id).Append(',').Append(i.Package).Append(',').Append(i.Revision).Append(',');
            foreach (var kv in i.Config.OrderBy(k => k.Key, StringComparer.Ordinal)) b.Append(kv.Key).Append('=').Append(kv.Value).Append(',');
            foreach (var kv in i.AccountBindings.OrderBy(k => k.Key, StringComparer.Ordinal)) b.Append(kv.Key).Append("->").Append(kv.Value).Append(',');
            b.Append(';');
        }
        b.Append('|').AppendJoin(';', s.Services).Append('|').AppendJoin(';', s.TranslationOrder).Append('|').AppendJoin(';', s.Prompts)
            .Append('|').Append(s.Prompt.Level).Append(',').Append(s.Prompt.Profile).Append(',').AppendJoin(';', s.Prompt.Scope);
        return b.ToString();
    }
}

/// <summary>Built-in packages (DEV-PLAN 5: 21 plugins) and native providers, with first-run defaults.</summary>
public static class BuiltInCatalog
{
    /// <summary>Secrets: local secret names the package declares (PLAN 4.5.3 table); values live only in secrets.dat.</summary>
    public sealed record Package(string InstanceId, string PackageId, Capability[] Capabilities, string Page, string[] Secrets);

    public static readonly IReadOnlyList<Package> Packages =
    [
        new("mymemory", "app.susu.mymemory", [Capability.Translate], "engines", []),
        new("tencent-translate", "app.susu.tencent-translate", [Capability.Translate], "engines", ["secretId", "secretKey"]),
        new("deepl", "app.susu.deepl", [Capability.Translate], "engines", ["apiKey"]),
        new("google-translate", "app.susu.google-translate", [Capability.Translate], "engines", ["apiKey"]),
        new("microsoft-translate", "app.susu.microsoft-translate", [Capability.Translate], "engines", ["apiKey"]),
        new("amazon-translate", "app.susu.amazon-translate", [Capability.Translate], "engines", ["accessKeyId", "secretAccessKey"]),
        new("youdao", "app.susu.youdao", [Capability.Translate, Capability.Dictionary], "engines", ["appKey", "appSecret"]),
        new("openai", "app.susu.openai", [Capability.Translate], "ai", ["apiKey"]),
        new("glm", "app.susu.glm", [Capability.Translate], "ai", ["apiKey"]),
        new("gemini", "app.susu.gemini", [Capability.Translate], "ai", ["apiKey"]),
        new("claude", "app.susu.claude", [Capability.Translate], "ai", ["apiKey"]),
        new("ollama", "app.susu.ollama", [Capability.Translate], "ai", []),
        new("tencent-ocr", "app.susu.tencent-ocr", [Capability.Ocr], "ocr", ["secretId", "secretKey"]),
        new("simple-latex", "app.susu.simple-latex", [Capability.Ocr], "ocr", ["apiKey"]),
        new("microsoft-tts", "app.susu.microsoft-tts", [Capability.Tts], "speech", ["apiKey"]),
        new("google-tts", "app.susu.google-tts", [Capability.Tts], "speech", ["apiKey"]),
        new("tencent-tts", "app.susu.tencent-tts", [Capability.Tts], "speech", ["secretId", "secretKey"]),
        new("openai-asr", "app.susu.openai-asr", [Capability.Asr], "speech", ["apiKey"]),
        new("gemini-asr", "app.susu.gemini-asr", [Capability.Asr], "speech", ["apiKey"]),
        new("ankiconnect", "app.susu.ankiconnect", [Capability.Vocab], "vocab", []),
        new("eudic", "app.susu.eudic", [Capability.Vocab], "vocab", ["apiKey"]),
    ];

    public static Package? Find(string instanceId) => Packages.FirstOrDefault(p => p.InstanceId == instanceId);

    public const string NativeDetect = "native-els", NativeTts = "native-sapi";

    public static AppSettings Defaults()
    {
        var instances = new List<InstanceSettings>
        {
            new(NativeDetect, "native.els", 1, Empty, Empty),
            new(NativeTts, "native.sapi", 1, Empty, Empty),
        };
        var services = new List<ServiceSettings> { new(NativeDetect, Capability.Detect, true), new(NativeTts, Capability.Tts, true) };
        foreach (var package in Packages)
        {
            instances.Add(new(package.InstanceId, package.PackageId, 1, Empty, Empty));
            foreach (var capability in package.Capabilities)
                services.Add(new(package.InstanceId, capability, package.InstanceId == "mymemory"));
        }
        var order = services.Where(s => s.Capability == Capability.Translate).Select(s => s.ServiceId).ToList();
        var hotkeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["inputTranslate"] = "Alt+A", ["selectionTranslate"] = "Alt+D", ["clipboardTranslate"] = "Alt+D",
            ["ocrTranslate"] = "Alt+S", ["voiceTranslate"] = "Alt+V", ["audioTranslate"] = "Alt+B",
            ["videoTranscribe"] = "", ["pronounce"] = "",
        };
        return new AppSettings(
            AppSettings.CurrentSchemaVersion, 0,
            new GeneralSettings("zh-Hans", "en", "zh-Hans", $"{NativeDetect}/detect", 2, false, CloseAction.Hide, false, "light"),
            new HotkeySettings(hotkeys),
            new NetworkSettings(ProxyMode.System, "", 0, "", 30),
            [], instances, services, order, [], PromptSettings.Default);
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();
}
