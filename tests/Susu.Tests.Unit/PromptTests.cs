using System.Text.Json;
using Susu.Contracts;
using Susu.Domain;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F07.3 CFG04: single-pass prompt substitution, the per-task prompt snapshot, and temperature reaching only a
/// package whose schema declares it.
/// </summary>
public class PromptTemplateTests
{
    private static readonly Dictionary<string, string> Values = new() { ["from"] = "English", ["to"] = "Chinese (Simplified)", ["level"] = "IELTS 7.0" };

    [Fact]
    public void Known_variables_are_replaced_once_and_unknown_ones_stay_literal()
    {
        string rendered = PromptTemplate.Render("{{from}}->{{to}} at {{level}}; {{tone}} {{ secret.apiKey }}\n{{text}}", Values, "hello");
        Assert.Equal("English->Chinese (Simplified) at IELTS 7.0; {{tone}} {{ secret.apiKey }}\nhello", rendered);
    }

    [Theory]
    [InlineData("{{to}}")]
    [InlineData("{{secret.apiKey}}")]
    [InlineData("{{text}}{{text}}")]
    [InlineData("{{{{to}}}}")]
    [InlineData("{ \"secret\": \"apiKey\" } {{level}}\r\n{{from}")]
    public void Source_text_is_inserted_literally_and_never_expanded(string text)
    {
        string rendered = PromptTemplate.Render("Translate to {{to}}:\n{{text}}\nEnd {{level}}", Values, text);
        Assert.Equal($"Translate to Chinese (Simplified):\n{text}\nEnd IELTS 7.0", rendered);
    }

    [Fact]
    public void Values_are_not_rescanned_even_when_they_look_like_variables()
    {
        var hostile = new Dictionary<string, string> { ["from"] = "{{to}}", ["to"] = "{{text}}", ["level"] = "{{level}}" };
        Assert.Equal("{{to}} {{text}} {{level}} x", PromptTemplate.Render("{{from}} {{to}} {{level}} {{text}}", hostile, "x"));
    }

    [Fact]
    public void The_text_goes_in_once_at_its_variable_or_after_the_template()
    {
        Assert.Equal("A x B {{text}}", PromptTemplate.Render("A {{text}} B {{text}}", Values, "x"));
        Assert.Equal("Translate.\n\nx", PromptTemplate.Render("Translate.", Values, "x"));
        Assert.Equal("x", PromptTemplate.Render("", Values, "x"));
        Assert.Equal("open {{to x", PromptTemplate.Render("open {{to", Values, "x").Replace("\n\n", " "));
    }

    [Fact]
    public void Template_checks_limit_length_and_a_repeated_text_variable_only()
    {
        Assert.Null(PromptCatalog.CheckTemplate("{{unknown}} {{text}}"));
        Assert.Equal("text-repeated", PromptCatalog.CheckTemplate("{{text}} and {{ text }}"));
        Assert.Equal("too-long", PromptCatalog.CheckTemplate(new string('a', PromptCatalog.MaxTemplateLength + 1)));
        Assert.Equal(["from", "x.y", "text"], PromptTemplate.Names("{{from}} {{x.y}} {{from}} {{text}}"));
    }

    [Fact]
    public void Built_in_levels_follow_the_revised_artboard()
    {
        Assert.Equal(["literal", "free", "ielts-6.5", "ielts-7.0", "toefl-100", "cet-6", "academic"], PromptCatalog.Levels.Select(l => l.Id));
        Assert.Contains("claude", PromptCatalog.AiInstances); // SetPrompt P2
        Assert.Equal(1, PromptTemplate.Count(PromptCatalog.DefaultTemplate, PromptTemplate.Text));
    }
}

public class PromptSnapshotTests
{
    private static AppSettings With(string level, string profile, string[] scope, params PromptProfile[] profiles)
        => BuiltInCatalog.Defaults() with { Revision = 7, Prompts = profiles, Prompt = new PromptSettings(level, profile, scope) };

    [Fact]
    public void Only_AI_services_in_scope_get_a_prompt()
    {
        var s = With("ielts-7.0", "", ["openai", "claude"]);
        Assert.NotNull(PromptCatalog.SnapshotFor(s, "openai"));
        Assert.Null(PromptCatalog.SnapshotFor(s, "glm")); // AI, but outside the scope
        Assert.Null(PromptCatalog.SnapshotFor(s, "deepl")); // translation engines never receive a prompt
        Assert.Null(PromptCatalog.SnapshotFor(s, "mymemory"));
    }

    [Fact]
    public void The_custom_template_and_level_are_captured_with_the_settings_revision()
    {
        var s = With("academic", "mine", ["openai"], new PromptProfile("mine", "Mine", "[{{level}}] {{from}}>{{to}}: {{text}}"));
        var snapshot = PromptCatalog.SnapshotFor(s, "openai")!;
        Assert.Equal(7, snapshot.Revision);
        Assert.Equal("[学术论文] English>Chinese (Simplified): hi", snapshot.Render("hi", "en", "zh-Hans"));

        // A later save builds a new snapshot; the one a running task holds is unchanged.
        var later = s with { Revision = 8, Prompts = [new PromptProfile("mine", "Mine", "NEW {{text}}")] };
        Assert.Equal("NEW hi", PromptCatalog.SnapshotFor(later, "openai")!.Render("hi", "en", "zh-Hans"));
        Assert.Equal("[学术论文] English>Chinese (Simplified): hi", snapshot.Render("hi", "en", "zh-Hans"));
    }

    [Fact]
    public void No_level_and_no_profile_use_the_built_in_default()
    {
        var snapshot = PromptCatalog.SnapshotFor(With("", "", ["openai"]), "openai")!;
        Assert.Equal(PromptCatalog.DefaultTemplate, snapshot.Template);
        Assert.Equal(PromptCatalog.NoLevel, snapshot.Level);
        Assert.EndsWith("原文：\nhello", snapshot.Render("hello", "en", "zh-Hans"));
    }

    [Fact]
    public void Prompt_settings_round_trip_through_the_settings_file()
    {
        var settings = With("cet-6", "p1", ["openai", "ollama"], new PromptProfile("p1", "One", "{{to}} {{text}}"));
        string yaml = SettingsYaml.Write(settings);
        var (back, issues) = SettingsYaml.Read(yaml);
        Assert.Empty(issues);
        Assert.Equal(settings.Prompt.Level, back!.Prompt.Level);
        Assert.Equal(settings.Prompt.Profile, back.Prompt.Profile);
        Assert.Equal(settings.Prompt.Scope, back.Prompt.Scope);
        Assert.True(back.ContentEquals(settings with { Revision = back.Revision }));

        var (_, problems) = SettingsYaml.Read(yaml.Replace("level: \"cet-6\"", "level: \"jlpt-n2\"").Replace("profile: \"p1\"", "profile: \"gone\""));
        Assert.Contains(problems, i => i.Path == "prompt.level");
        Assert.Contains(problems, i => i.Path == "prompt.profile");
    }

    [Fact]
    public void A_file_without_a_prompt_section_gets_the_default_selection()
    {
        string yaml = SettingsYaml.Write(BuiltInCatalog.Defaults() with { Prompt = new PromptSettings("free", "", ["openai"]) });
        int at = yaml.IndexOf("\nprompt:", StringComparison.Ordinal);
        Assert.True(at > 0);
        var (back, issues) = SettingsYaml.Read(yaml[..at] + "\n");
        Assert.Empty(issues);
        Assert.Equal(PromptCatalog.AiInstances, back!.Prompt.Scope);
    }
}

public class ModelParameterGatingTests
{
    private static readonly ConfigField Temperature = new("temperature", ConfigFieldType.Number, Minimum: 0, Maximum: 2);

    [Fact]
    public void Temperature_reaches_only_a_package_whose_schema_declares_it()
    {
        var config = new Dictionary<string, string> { ["temperature"] = "0.4", ["plan"] = "pro" };
        Assert.Equal("0.4", ConfigSchema.ForPlugin(config, [Temperature])["temperature"]);
        var undeclared = ConfigSchema.ForPlugin(config, [new ConfigField("model", ConfigFieldType.String)]);
        Assert.False(undeclared.ContainsKey("temperature"));
        Assert.Equal("pro", undeclared["plan"]); // other keys are untouched
        Assert.False(ConfigSchema.ForPlugin(config, null).ContainsKey("temperature"));
    }

    [Theory]
    [InlineData("2.5")]
    [InlineData("-1")]
    [InlineData("hot")]
    [InlineData("NaN")]
    public void A_hand_edited_out_of_range_temperature_is_dropped(string value)
        => Assert.False(ConfigSchema.ForPlugin(new Dictionary<string, string> { ["temperature"] = value }, [Temperature]).ContainsKey("temperature"));

    [Fact]
    public void The_shipped_OpenAI_schema_declares_temperature_and_no_other_package_does()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Susu.slnx"))) root = root.Parent!;
        var schemas = Susu.Plugins.PluginTranslationProviders.LoadSchemas(Path.Combine(root.FullName, "src", "Susu.Host"));
        var field = schemas["openai"].Single(f => f.Name == "temperature");
        Assert.Equal((ConfigFieldType.Number, 0d, 2d, "advanced"), (field.Type, field.Minimum!.Value, field.Maximum!.Value, field.Group));
        Assert.All(schemas.Where(kv => kv.Key != "openai"), kv => Assert.DoesNotContain(kv.Value, f => f.Name == "temperature"));
    }
}
