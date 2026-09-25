using System.Text;

namespace Susu.Domain;

/// <summary>
/// SetPrompt selection (F07.3, ARCHITECTURE 3.1): the built-in writing level ("" = none), the custom template
/// in use ("" = the built-in default template) and the AI instances the prompt applies to. Translation engines
/// never receive a prompt, whatever the scope says.
/// </summary>
public sealed record PromptSettings(string Level, string Profile, IReadOnlyList<string> Scope)
{
    public static PromptSettings Default => new("", "", [.. PromptCatalog.AiInstances]);
}

/// <summary>A built-in writing level; <see cref="Value"/> is what <c>{{level}}</c> renders to.</summary>
public sealed record PromptLevel(string Id, string Value);

public static class PromptCatalog
{
    public const int MaxTemplateLength = 4000, MaxProfiles = 20, MaxNameLength = 40;

    /// <summary>SetPrompt P1 (ARTBOARD-REVISIONS): no JLPT; the seven levels in display order.</summary>
    public static readonly IReadOnlyList<PromptLevel> Levels =
    [
        new("literal", "直译（贴近原文结构）"),
        new("free", "意译（通顺自然）"),
        new("ielts-6.5", "雅思 6.5"),
        new("ielts-7.0", "雅思 7.0"),
        new("toefl-100", "托福 100"),
        new("cet-6", "大学英语六级（CET-6）"),
        new("academic", "学术论文"),
    ];

    /// <summary>Value of <c>{{level}}</c> when no level is selected.</summary>
    public const string NoLevel = "通用";

    /// <summary>SetPrompt "应用到" (P2 adds Claude): the AI platforms, in display order.</summary>
    public static readonly IReadOnlyList<string> AiInstances = ["openai", "glm", "gemini", "claude", "ollama"];

    public const string DefaultTemplate =
        "你是一名专业译者。把下面的原文从 {{from}} 译成 {{to}}。按 {{level}} 对应的书写水平选词与造句：句式自然，不堆砌生僻词，不解释、不加注，只输出译文。\n\n原文：\n{{text}}";

    public static PromptLevel? FindLevel(string id) => Levels.FirstOrDefault(l => l.Id == id);

    /// <summary>
    /// The template an AI instance uses under <paramref name="settings"/>, or null when the prompt does not apply
    /// (not an AI instance, or outside the scope): that service then keeps its own built-in instruction.
    /// </summary>
    public static PromptSnapshot? SnapshotFor(AppSettings settings, string instanceId)
    {
        if (!AiInstances.Contains(instanceId) || !settings.Prompt.Scope.Contains(instanceId)) return null;
        string template = settings.Prompts.FirstOrDefault(p => p.Id == settings.Prompt.Profile)?.Template is { Length: > 0 } custom ? custom : DefaultTemplate;
        string level = FindLevel(settings.Prompt.Level)?.Value ?? NoLevel;
        return new PromptSnapshot(settings.Revision, template, level);
    }

    /// <summary>Problems with a custom template, or null. Unknown variables are allowed and stay literal (CFG04).</summary>
    public static string? CheckTemplate(string template)
    {
        if (template.Length > MaxTemplateLength) return "too-long";
        if (PromptTemplate.Count(template, PromptTemplate.Text) > 1) return "text-repeated";
        return null;
    }
}

/// <summary>
/// The prompt a task was started with (ARCHITECTURE 3.1: tasks fix their PromptRevision). It is captured when the
/// provider is built, so saving SetPrompt affects later tasks only.
/// </summary>
public sealed record PromptSnapshot(long Revision, string Template, string Level)
{
    public string Render(string text, string from, string to)
        => PromptTemplate.Render(Template, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PromptTemplate.From] = PromptTemplate.LanguageName(from),
            [PromptTemplate.To] = PromptTemplate.LanguageName(to),
            [PromptTemplate.Level] = Level,
        }, text);
}

/// <summary>
/// Single-pass variable substitution (CFG04, ARCHITECTURE 3.1). The template is scanned once, left to right;
/// a known <c>{{name}}</c> is replaced by its value and the value is never scanned again, so source text or a
/// language name containing <c>{{to}}</c> or <c>{{secret.apiKey}}</c> stays literal. Unknown names stay as
/// written. The source text is one separate piece inserted once: at <c>{{text}}</c>, or after the template
/// when it has no such variable.
/// </summary>
public static class PromptTemplate
{
    public const string Text = "text", From = "from", To = "to", Level = "level";
    public static readonly IReadOnlyList<string> Variables = [Text, From, To, Level];

    public static string Render(string template, IReadOnlyDictionary<string, string> values, string text)
    {
        var output = new StringBuilder(template.Length + text.Length + 64);
        bool textInserted = false;
        int at = 0;
        while (at < template.Length)
        {
            int open = template.IndexOf("{{", at, StringComparison.Ordinal);
            if (open < 0) { output.Append(template, at, template.Length - at); break; }
            output.Append(template, at, open - at);
            int close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) { output.Append(template, open, template.Length - open); break; }
            string name = template.Substring(open + 2, close - open - 2).Trim();
            if (name == Text && !textInserted) { output.Append(text); textInserted = true; }
            else if (name != Text && values.TryGetValue(name, out var value)) output.Append(value);
            else output.Append(template, open, close + 2 - open); // unknown (or a second {{text}}): literal
            at = close + 2;
        }
        if (!textInserted) output.Append(output.Length > 0 ? "\n\n" : "").Append(text);
        return output.ToString();
    }

    /// <summary>Known and unknown variable names used in <paramref name="template"/>, in order of first use.</summary>
    public static IReadOnlyList<string> Names(string template)
    {
        var names = new List<string>();
        int at = 0;
        while (true)
        {
            int open = template.IndexOf("{{", at, StringComparison.Ordinal);
            if (open < 0) break;
            int close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) break;
            string name = template.Substring(open + 2, close - open - 2).Trim();
            if (!names.Contains(name)) names.Add(name);
            at = close + 2;
        }
        return names;
    }

    public static int Count(string template, string name)
    {
        int count = 0, at = 0;
        while (true)
        {
            int open = template.IndexOf("{{", at, StringComparison.Ordinal);
            if (open < 0) return count;
            int close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) return count;
            if (template.Substring(open + 2, close - open - 2).Trim() == name) count++;
            at = close + 2;
        }
    }

    /// <summary>English language name for a canonical code; any other code is passed through.</summary>
    public static string LanguageName(string code) => Languages.IsCanonical(code) ? Languages.Get(code).EnglishName : code;
}
