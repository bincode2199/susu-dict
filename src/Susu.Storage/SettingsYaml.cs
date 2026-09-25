using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Susu.Contracts;
using Susu.Domain;

namespace Susu.Storage;

/// <summary>
/// settings.yaml schema v1 (PLAN 5.2, ARCHITECTURE 8.1): strict binding with exact error paths, forward-only
/// migrations, and a host-generated file with a fixed key order and regenerated comments.
/// </summary>
public static partial class SettingsYaml
{
    // ---------- reading ----------

    public static (AppSettings? Settings, IReadOnlyList<FileIssue> Issues) Read(string text)
    {
        var (root, issues) = YamlSubset.Parse(text);
        if (root is null) return (null, issues);
        if (root is not YMap map) return (null, [new FileIssue("$", "type", "top level must be a mapping", root.Line, root.Column)]);
        var list = new List<FileIssue>();
        int version = map.Get("schemaVersion") is YScalar v && int.TryParse(v.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : -1;
        if (version < 0) return (null, [new FileIssue("schemaVersion", "missing", "schemaVersion is required", map.Line, map.Column)]);
        if (version > AppSettings.CurrentSchemaVersion)
            return (null, [new FileIssue("schemaVersion", "newer-schema", $"file was written by a newer Su-Su (schema {version}); this version supports {AppSettings.CurrentSchemaVersion}")]);
        for (int from = version; from < AppSettings.CurrentSchemaVersion; from++) map = Migrations[from](map);
        var settings = new Binder(list).Bind(map);
        return list.Count == 0 ? (settings, list) : (null, list);
    }

    /// <summary>Migration from schema N to N+1, on the node tree so unknown-field checks still apply afterwards.</summary>
    public static readonly IReadOnlyDictionary<int, Func<YMap, YMap>> Migrations = new Dictionary<int, Func<YMap, YMap>>
    {
        // Schema 0 (pre-release test layout): general.language was the UI language; no AI timeout.
        [0] = MigrateV0,
    };

    private static YMap MigrateV0(YMap map)
    {
        map = Rewrite(map, "general", general => general is YMap g
            ? g with { Entries = g.Entries.Select(e => e.Key == "language" ? new KeyValuePair<string, YNode>("uiLanguage", e.Value) : e).ToList() }
            : general);
        return Rewrite(map, "schemaVersion", _ => new YScalar("1", false, 0, 0));
    }

    private static YMap Rewrite(YMap map, string key, Func<YNode, YNode> change)
        => map with { Entries = map.Entries.Select(e => e.Key == key ? new KeyValuePair<string, YNode>(key, change(e.Value)) : e).ToList() };

    private sealed partial class Binder(List<FileIssue> issues)
    {
        private readonly AppSettings defaults = BuiltInCatalog.Defaults();

        public AppSettings Bind(YMap root)
        {
            Keys(root, "", "schemaVersion", "revision", "general", "hotkeys", "network", "accounts", "instances", "services", "translationOrder", "prompts", "prompt");
            long revision = Long(root, "", "revision", 0, 0, long.MaxValue);
            var general = General(root.Get("general") as YMap ?? Expect<YMap>(root, "general"));
            var hotkeys = Hotkeys(root.Get("hotkeys"));
            var network = Network(root.Get("network"));
            var accounts = List(root, "accounts", Account);
            var instances = List(root, "instances", Instance);
            var services = List(root, "services", Service);
            var order = List(root, "translationOrder", (node, path) => Scalar(node, path));
            var prompts = List(root, "prompts", Prompt);
            var prompt = PromptSelection(root.Get("prompt"), prompts);
            var settings = new AppSettings(AppSettings.CurrentSchemaVersion, revision, general, hotkeys, network,
                root.Get("accounts") is null ? defaults.Accounts : accounts,
                root.Get("instances") is null ? defaults.Instances : instances,
                root.Get("services") is null ? defaults.Services : services,
                root.Get("translationOrder") is null ? defaults.TranslationOrder : order, prompts, prompt);
            CrossCheck(settings);
            return settings;
        }

        private GeneralSettings General(YMap? g)
        {
            var d = defaults.General;
            if (g is null) return d;
            const string p = "general";
            Keys(g, p, "uiLanguage", "sourceLanguage", "targetLanguage", "detectionService", "defaultExpandedCards", "allowClipboardBorrowing", "closeAction", "launchAtStartup", "theme");
            string ui = OneOf(g, p, "uiLanguage", d.UiLanguage, "zh-Hans", "en");
            string source = Lang(g, p, "sourceLanguage", d.SourceLanguage), target = Lang(g, p, "targetLanguage", d.TargetLanguage);
            if (source == target) Issue($"{p}.targetLanguage", "range", "source and target languages must differ", g.Get("targetLanguage"));
            return new GeneralSettings(ui, source, target,
                Str(g, p, "detectionService", d.DetectionService),
                (int)Long(g, p, "defaultExpandedCards", d.DefaultExpandedCards, 0, 32),
                Bool(g, p, "allowClipboardBorrowing", d.AllowClipboardBorrowing),
                OneOf(g, p, "closeAction", "hide", "hide", "exit") == "exit" ? CloseAction.Exit : CloseAction.Hide,
                Bool(g, p, "launchAtStartup", d.LaunchAtStartup),
                OneOf(g, p, "theme", d.Theme, "light"));
        }

        private HotkeySettings Hotkeys(YNode? node)
        {
            if (node is null) return defaults.Hotkeys;
            if (node is not YMap map) { Issue("hotkeys", "type", "expected a mapping", node); return defaults.Hotkeys; }
            Keys(map, "hotkeys", HotkeySettings.Actions);
            var chords = new Dictionary<string, string>(defaults.Hotkeys.Chords, StringComparer.Ordinal);
            foreach (var action in HotkeySettings.Actions)
            {
                string chord = Str(map, "hotkeys", action, chords[action]);
                if (chord.Length > 0 && !Chord().IsMatch(chord)) Issue($"hotkeys.{action}", "format", $"'{chord}' is not a chord such as Ctrl+Alt+T", map.Get(action));
                chords[action] = chord;
            }
            var hotkeys = new HotkeySettings(chords);
            foreach (var (chord, actions) in hotkeys.Conflicts()) Issue($"hotkeys.{actions[0]}", "conflict", $"{chord} is assigned to {string.Join(", ", actions)}", map.Get(actions[0]));
            return hotkeys;
        }

        [GeneratedRegex("^((Ctrl|Alt|Shift|Win)\\+){1,4}([A-Z0-9]|F([1-9]|1[0-9]|2[0-4])|Space|Insert|Delete|Home|End|PageUp|PageDown|Up|Down|Left|Right|Oem[1-8]|OemComma|OemPeriod|OemMinus|OemPlus)$")]
        private static partial Regex Chord();

        private NetworkSettings Network(YNode? node)
        {
            var d = defaults.Network;
            if (node is null) return d;
            if (node is not YMap map) { Issue("network", "type", "expected a mapping", node); return d; }
            Keys(map, "network", "proxy", "aiTimeoutSeconds");
            var mode = d.ProxyMode; string host = d.ProxyHost, user = d.ProxyUsername; int port = d.ProxyPort;
            if (map.Get("proxy") is YMap proxy)
            {
                Keys(proxy, "network.proxy", "mode", "host", "port", "username");
                mode = OneOf(proxy, "network.proxy", "mode", "system", "system", "none", "http", "socks5") switch { "none" => ProxyMode.None, "http" => ProxyMode.Http, "socks5" => ProxyMode.Socks5, _ => ProxyMode.System };
                host = Str(proxy, "network.proxy", "host", "");
                port = (int)Long(proxy, "network.proxy", "port", 0, 0, 65535);
                user = Str(proxy, "network.proxy", "username", "");
                if (mode is ProxyMode.Http or ProxyMode.Socks5 && (host.Length == 0 || port == 0)) Issue("network.proxy", "missing", "http/socks5 proxy needs host and port", proxy);
            }
            else if (map.Get("proxy") is { } bad) Issue("network.proxy", "type", "expected a mapping", bad);
            return new NetworkSettings(mode, host, port, user, (int)Long(map, "network", "aiTimeoutSeconds", d.AiTimeoutSeconds, 1, 600));
        }

        private AccountSettings Account(YNode node, string path)
        {
            if (node is not YMap map) { Issue(path, "type", "expected a mapping", node); return new("", "", [], []); }
            Keys(map, path, "id", "label", "secrets", "grants");
            string id = Id(map, path, "id");
            var secrets = List(map, "secrets", (n, p) => Scalar(n, p), path);
            var grants = List(map, "grants", (n, p) =>
            {
                if (n is not YMap g) { Issue(p, "type", "expected a mapping", n); return new CredentialGrant("", "", "", "", ""); }
                Keys(g, p, "package", "signer", "secret", "origin", "use");
                string origin = Str(g, p, "origin", "");
                if (!Origin.TryNormalize(origin, out var normalized) || normalized != origin) Issue($"{p}.origin", "format", $"'{origin}' must be an exact normalized origin like https://host:443", g.Get("origin"));
                string secret = Str(g, p, "secret", "");
                if (!secrets.Contains(secret)) Issue($"{p}.secret", "reference", $"account has no secret '{secret}'", g.Get("secret"));
                string use = Str(g, p, "use", "");
                if (!UseFormat().IsMatch(use)) Issue($"{p}.use", "format", $"'{use}' must be header:<name>, query:<name>, json:/<pointer> or signer:<scheme>", g.Get("use"));
                return new CredentialGrant(Str(g, p, "package", ""), Str(g, p, "signer", ""), secret, origin, use);
            }, path);
            return new AccountSettings(id, Str(map, path, "label", ""), secrets, grants);
        }

        [GeneratedRegex("^(header:[A-Za-z0-9-]+|query:[^&=\\s]+|json:/\\S*|signer:(tencent-tc3|aws-sigv4|bearer|digest|hmac))$")]
        private static partial Regex UseFormat();

        private InstanceSettings Instance(YNode node, string path)
        {
            if (node is not YMap map) { Issue(path, "type", "expected a mapping", node); return new("", "", 0, Empty, Empty); }
            Keys(map, path, "id", "package", "revision", "config", "accounts");
            return new InstanceSettings(Id(map, path, "id"), Str(map, path, "package", ""), Long(map, path, "revision", 1, 1, long.MaxValue),
                ScalarMap(map.Get("config"), $"{path}.config"), ScalarMap(map.Get("accounts"), $"{path}.accounts"));
        }

        private ServiceSettings Service(YNode node, string path)
        {
            if (node is not YMap map) { Issue(path, "type", "expected a mapping", node); return new("", Capability.Translate, false); }
            Keys(map, path, "instance", "capability", "enabled");
            string capability = Str(map, path, "capability", "");
            if (!Enum.TryParse<Capability>(capability, true, out var parsed) || capability != capability.ToLowerInvariant())
                Issue($"{path}.capability", "range", $"unknown capability '{capability}'", map.Get("capability"));
            return new ServiceSettings(Str(map, path, "instance", ""), parsed, Bool(map, path, "enabled", false));
        }

        private PromptProfile Prompt(YNode node, string path)
        {
            if (node is not YMap map) { Issue(path, "type", "expected a mapping", node); return new("", "", ""); }
            Keys(map, path, "id", "name", "template");
            return new PromptProfile(Id(map, path, "id"), Str(map, path, "name", ""), Str(map, path, "template", ""));
        }

        private PromptSettings PromptSelection(YNode? node, IReadOnlyList<PromptProfile> profiles)
        {
            var d = defaults.Prompt;
            if (node is null) return d;
            const string p = "prompt";
            if (node is not YMap map) { Issue(p, "type", "expected a mapping", node); return d; }
            Keys(map, p, "level", "profile", "scope");
            string level = Str(map, p, "level", "");
            if (level.Length > 0 && PromptCatalog.FindLevel(level) is null) Issue($"{p}.level", "range", $"unknown level '{level}'", map.Get("level"));
            string profile = Str(map, p, "profile", "");
            if (profile.Length > 0 && !profiles.Any(x => x.Id == profile)) Issue($"{p}.profile", "reference", $"unknown prompt '{profile}'", map.Get("profile"));
            var scope = map.Get("scope") is null ? d.Scope : List(map, "scope", (n, path) => Scalar(n, path), p);
            foreach (var id in scope)
                if (!PromptCatalog.AiInstances.Contains(id)) Issue($"{p}.scope", "range", $"'{id}' is not an AI service", map.Get("scope"));
            return new PromptSettings(level, profile, scope);
        }

        private void CrossCheck(AppSettings s)
        {
            Unique(s.Accounts.Select(a => a.Id), "accounts");
            Unique(s.Instances.Select(i => i.Id), "instances");
            Unique(s.Prompts.Select(p => p.Id), "prompts");
            Unique(s.Services.Select(x => x.ServiceId), "services");
            var instanceIds = s.Instances.Select(i => i.Id).ToHashSet();
            var accountIds = s.Accounts.Select(a => a.Id).ToHashSet();
            for (int i = 0; i < s.Instances.Count; i++)
            {
                if (!PackageId.IsValid(s.Instances[i].Package) && !s.Instances[i].Package.StartsWith("native.", StringComparison.Ordinal))
                    issues.Add(new FileIssue($"instances[{i}].package", "format", $"'{s.Instances[i].Package}' is not a package id"));
                foreach (var (secret, account) in s.Instances[i].AccountBindings)
                    if (!accountIds.Contains(account)) issues.Add(new FileIssue($"instances[{i}].accounts.{secret}", "reference", $"unknown account '{account}'"));
            }
            for (int i = 0; i < s.Services.Count; i++)
                if (!instanceIds.Contains(s.Services[i].Instance)) issues.Add(new FileIssue($"services[{i}].instance", "reference", $"unknown instance '{s.Services[i].Instance}'"));
            var translate = s.Services.Where(x => x.Capability == Capability.Translate).Select(x => x.ServiceId).ToHashSet();
            for (int i = 0; i < s.TranslationOrder.Count; i++)
                if (!translate.Contains(s.TranslationOrder[i])) issues.Add(new FileIssue($"translationOrder[{i}]", "reference", $"'{s.TranslationOrder[i]}' is not a translate service"));
            Unique(s.TranslationOrder, "translationOrder");
            if (!translate.SetEquals(s.TranslationOrder)) issues.Add(new FileIssue("translationOrder", "missing", "every translate service must appear exactly once"));
            if (!s.Services.Any(x => x.ServiceId == s.General.DetectionService && x.Capability == Capability.Detect))
                issues.Add(new FileIssue("general.detectionService", "reference", $"'{s.General.DetectionService}' is not a detect service"));
        }

        private void Unique(IEnumerable<string> ids, string path)
        {
            foreach (var dup in ids.GroupBy(x => x, StringComparer.Ordinal).Where(g => g.Count() > 1))
                issues.Add(new FileIssue(path, "duplicate-id", $"'{dup.Key}' appears more than once"));
        }

        // ---------- primitives ----------

        private void Keys(YMap map, string path, params string[] allowed)
        {
            foreach (var entry in map.Entries)
                if (Array.IndexOf(allowed, entry.Key) < 0)
                    issues.Add(new FileIssue(path.Length == 0 ? entry.Key : $"{path}.{entry.Key}", "unknown-field", $"unknown field '{entry.Key}'", entry.Value.Line, entry.Value.Column));
        }

        private T? Expect<T>(YMap parent, string key) where T : YNode
        {
            var node = parent.Get(key);
            if (node is not null and not T) Issue(key, "type", $"expected a {(typeof(T) == typeof(YMap) ? "mapping" : "sequence")}", node);
            return null;
        }

        private List<T> List<T>(YMap parent, string key, Func<YNode, string, T> item, string prefix = "")
        {
            string path = prefix.Length == 0 ? key : $"{prefix}.{key}";
            var node = parent.Get(key);
            if (node is null) return [];
            if (node is not YSeq seq) { Issue(path, "type", "expected a sequence", node); return []; }
            return seq.Items.Select((n, i) => item(n, $"{path}[{i}]")).ToList();
        }

        private IReadOnlyDictionary<string, string> ScalarMap(YNode? node, string path)
        {
            if (node is null) return Empty;
            if (node is not YMap map) { Issue(path, "type", "expected a mapping", node); return Empty; }
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in map.Entries) result[entry.Key] = Scalar(entry.Value, $"{path}.{entry.Key}");
            return result;
        }

        private string Scalar(YNode node, string path)
        {
            if (node is YScalar s) return s.Value;
            Issue(path, "type", "expected a scalar", node);
            return "";
        }

        private string Str(YMap map, string path, string key, string fallback)
            => map.Get(key) is { } node ? Scalar(node, Join(path, key)) : fallback;

        private string Id(YMap map, string path, string key)
        {
            string id = Str(map, path, key, "");
            if (!IdFormat().IsMatch(id)) Issue(Join(path, key), "format", $"'{id}' must be 1-64 characters of a-z, 0-9, '-', '_' or '.'", map.Get(key) ?? map);
            return id;
        }

        [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
        private static partial Regex IdFormat();

        private long Long(YMap map, string path, string key, long fallback, long min, long max)
        {
            if (map.Get(key) is not { } node) return fallback;
            if (node is YScalar { Quoted: false } s && long.TryParse(s.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
            {
                if (value >= min && value <= max) return value;
                Issue(Join(path, key), "range", $"{value} is outside {min}..{max}", node);
                return fallback;
            }
            Issue(Join(path, key), "type", "expected an integer", node);
            return fallback;
        }

        private bool Bool(YMap map, string path, string key, bool fallback)
        {
            if (map.Get(key) is not { } node) return fallback;
            if (node is YScalar { Quoted: false, Value: "true" or "false" } s) return s.Value == "true";
            Issue(Join(path, key), "type", "expected true or false", node);
            return fallback;
        }

        private string OneOf(YMap map, string path, string key, string fallback, params string[] values)
        {
            string value = Str(map, path, key, fallback);
            if (Array.IndexOf(values, value) >= 0) return value;
            Issue(Join(path, key), "range", $"'{value}' must be one of {string.Join(", ", values)}", map.Get(key));
            return fallback;
        }

        private string Lang(YMap map, string path, string key, string fallback)
        {
            string value = Str(map, path, key, fallback);
            if (value is "zh-Hans" or "en") return value;
            Issue(Join(path, key), "range", $"'{value}' must be zh-Hans or en", map.Get(key));
            return fallback;
        }

        private void Issue(string path, string code, string message, YNode? node)
            => issues.Add(new FileIssue(path, code, message, node?.Line ?? 0, node?.Column ?? 0));

        private static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    // ---------- writing ----------

    /// <summary>Host-generated file: fixed key order, comments from the resource table in the UI language.</summary>
    public static string Write(AppSettings s)
    {
        var c = s.General.UiLanguage == "en" ? CommentsEn : CommentsZh;
        var w = new StringBuilder();
        foreach (var line in c["header"].Split('\n')) w.Append("# ").Append(line).Append('\n');
        w.Append("schemaVersion: ").Append(s.SchemaVersion).Append('\n');
        w.Append("revision: ").Append(s.Revision).Append('\n');

        Section(w, c, "general");
        var g = s.General;
        Pair(w, 1, "uiLanguage", Q(g.UiLanguage)); Pair(w, 1, "sourceLanguage", Q(g.SourceLanguage)); Pair(w, 1, "targetLanguage", Q(g.TargetLanguage));
        Pair(w, 1, "detectionService", Q(g.DetectionService)); Pair(w, 1, "defaultExpandedCards", g.DefaultExpandedCards.ToString(CultureInfo.InvariantCulture));
        Pair(w, 1, "allowClipboardBorrowing", B(g.AllowClipboardBorrowing)); Pair(w, 1, "closeAction", Q(g.CloseAction == CloseAction.Exit ? "exit" : "hide"));
        Pair(w, 1, "launchAtStartup", B(g.LaunchAtStartup)); Pair(w, 1, "theme", Q(g.Theme));

        Section(w, c, "hotkeys");
        foreach (var action in HotkeySettings.Actions) Pair(w, 1, action, Q(s.Hotkeys.Chords.TryGetValue(action, out var chord) ? chord : ""));

        Section(w, c, "network");
        w.Append("  proxy:\n");
        Pair(w, 2, "mode", Q(s.Network.ProxyMode.ToString().ToLowerInvariant())); Pair(w, 2, "host", Q(s.Network.ProxyHost));
        Pair(w, 2, "port", s.Network.ProxyPort.ToString(CultureInfo.InvariantCulture)); Pair(w, 2, "username", Q(s.Network.ProxyUsername));
        Pair(w, 1, "aiTimeoutSeconds", s.Network.AiTimeoutSeconds.ToString(CultureInfo.InvariantCulture));

        Seq(w, c, "accounts", s.Accounts, (a, b) =>
        {
            b.Append("  - id: ").Append(Q(a.Id)).Append('\n');
            Pair(b, 2, "label", Q(a.Label));
            Pair(b, 2, "secrets", Flow(a.Secrets));
            if (a.Grants.Count == 0) Pair(b, 2, "grants", "[]");
            else
            {
                b.Append("    grants:\n");
                foreach (var grant in a.Grants)
                {
                    b.Append("      - package: ").Append(Q(grant.Package)).Append('\n');
                    Pair(b, 4, "signer", Q(grant.Signer)); Pair(b, 4, "secret", Q(grant.Secret)); Pair(b, 4, "origin", Q(grant.Origin)); Pair(b, 4, "use", Q(grant.Use));
                }
            }
        });
        Seq(w, c, "instances", s.Instances, (i, b) =>
        {
            b.Append("  - id: ").Append(Q(i.Id)).Append('\n');
            Pair(b, 2, "package", Q(i.Package)); Pair(b, 2, "revision", i.Revision.ToString(CultureInfo.InvariantCulture));
            Map(b, "config", i.Config); Map(b, "accounts", i.AccountBindings);
        });
        Seq(w, c, "services", s.Services, (x, b) =>
        {
            b.Append("  - instance: ").Append(Q(x.Instance)).Append('\n');
            Pair(b, 2, "capability", Q(x.Capability.ToString().ToLowerInvariant())); Pair(b, 2, "enabled", B(x.Enabled));
        });
        Section(w, c, "translationOrder", inline: s.TranslationOrder.Count == 0 ? "[]" : null);
        foreach (var id in s.TranslationOrder) w.Append("  - ").Append(Q(id)).Append('\n');
        Seq(w, c, "prompts", s.Prompts, (p, b) =>
        {
            b.Append("  - id: ").Append(Q(p.Id)).Append('\n');
            Pair(b, 2, "name", Q(p.Name)); Pair(b, 2, "template", Q(p.Template));
        });
        Section(w, c, "prompt");
        Pair(w, 1, "level", Q(s.Prompt.Level)); Pair(w, 1, "profile", Q(s.Prompt.Profile)); Pair(w, 1, "scope", Flow(s.Prompt.Scope));
        return w.ToString();
    }

    private static void Section(StringBuilder w, IReadOnlyDictionary<string, string> c, string key, string? inline = null)
    {
        w.Append('\n');
        if (c.TryGetValue(key, out var comment)) w.Append("# ").Append(comment).Append('\n');
        w.Append(key).Append(':').Append(inline is null ? "" : " " + inline).Append('\n');
    }

    private static void Seq<T>(StringBuilder w, IReadOnlyDictionary<string, string> c, string key, IReadOnlyList<T> items, Action<T, StringBuilder> write)
    {
        Section(w, c, key, items.Count == 0 ? "[]" : null);
        foreach (var item in items) write(item, w);
    }

    private static void Map(StringBuilder w, string key, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0) { Pair(w, 2, key, "{}"); return; }
        w.Append("    ").Append(key).Append(":\n");
        foreach (var kv in map.OrderBy(k => k.Key, StringComparer.Ordinal)) w.Append("      ").Append(Q(kv.Key)).Append(": ").Append(Q(kv.Value)).Append('\n');
    }

    private static void Pair(StringBuilder w, int indent, string key, string value) => w.Append(' ', indent * 2).Append(key).Append(": ").Append(value).Append('\n');
    private static string Q(string value) => YamlSubset.Quote(value);
    private static string B(bool value) => value ? "true" : "false";
    private static string Flow(IReadOnlyList<string> items) => $"[{string.Join(", ", items.Select(Q))}]";

    private static readonly IReadOnlyDictionary<string, string> CommentsZh = new Dictionary<string, string>
    {
        ["header"] = "苏苏设置文件。由苏苏生成并在每次保存时重写：键顺序固定，说明注释会重新生成，你自己添加的注释不会保留。\n可以手动修改值；保存后约 0.3 秒生效。文件有误时苏苏继续使用上一次有效设置，不会用默认值覆盖本文件。\n凭据（API Key、代理密码）不在此文件中。",
        ["general"] = "通用：界面语言、翻译语言、检测服务、默认展开卡片数、剪贴板借用、关闭行为、开机自启、主题",
        ["hotkeys"] = "快捷键：只有划词翻译与剪贴板翻译可以共用同一组合键；留空表示不设置",
        ["network"] = "网络：代理（密码单独加密保存）与 AI 服务默认超时（秒）",
        ["accounts"] = "账户：只记录名称、密钥名与授权范围（插件身份、精确 origin、写入位置）；密钥本身在 secrets.dat",
        ["instances"] = "服务实例：插件包、配置值与账户绑定",
        ["services"] = "服务能力：每个实例的各项能力可单独启用",
        ["translationOrder"] = "翻译结果卡片的顺序（翻译引擎与 AI 合并排序）",
        ["prompts"] = "提示语：自定义模板；{{text}} {{from}} {{to}} {{level}} 只替换一次",
        ["prompt"] = "当前提示语：内置水平、自定义模板与应用范围",
    };

    private static readonly IReadOnlyDictionary<string, string> CommentsEn = new Dictionary<string, string>
    {
        ["header"] = "Su-Su settings. Generated by Su-Su and rewritten on every save: key order is fixed, these comments are regenerated,\nand comments you add are not kept. You may edit values by hand; changes apply about 0.3 s after saving. If the file is\ninvalid Su-Su keeps using the last valid settings and never overwrites this file with defaults. Credentials are not stored here.",
        ["general"] = "General: interface language, translation languages, detection service, cards expanded by default, clipboard borrowing, close action, start with Windows, theme",
        ["hotkeys"] = "Hotkeys: only selection and clipboard translation may share a chord; empty means unassigned",
        ["network"] = "Network: proxy (its password is stored encrypted elsewhere) and the default AI timeout in seconds",
        ["accounts"] = "Accounts: names, secret names and grants (plugin identity, exact origin, write location) only; secret values live in secrets.dat",
        ["instances"] = "Service instances: plugin package, configuration values and account bindings",
        ["services"] = "Service capabilities: each capability of an instance is enabled separately",
        ["translationOrder"] = "Order of translation result cards (engines and AI services in one list)",
        ["prompts"] = "Prompts: custom templates; {{text}} {{from}} {{to}} {{level}} are replaced once, other text stays as written",
        ["prompt"] = "Prompt in use: built-in level (empty = none), custom template id (empty = built-in default) and the AI services it applies to",
    };
}
