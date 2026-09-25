using System.Text.Json;
using System.Text.Json.Nodes;
using Susu.Abstractions;
using Susu.Domain;
using Susu.Jobs;
using Susu.Plugins;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F07.3 CFG04 through the real sandbox: the shipped OpenAI package receives the prompt the host rendered from
/// SetPrompt (single pass, source text literal) and the temperature its schema declares, via the production
/// provider factory (<see cref="PluginTranslationProviders.Create"/>). A provider built before a save keeps its
/// snapshot. Needs susu.exe published; skips itself otherwise (like <see cref="OpenAIPluginTests"/>).
/// </summary>
public class PromptSandboxTests
{
    private static readonly ConfigSnapshot Config = new(1, 1, 1, 2, TimeSpan.FromSeconds(30));

    private static IReadOnlyDictionary<string, IReadOnlyList<ConfigField>> Schemas()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Susu.slnx"))) root = root.Parent!;
        return PluginTranslationProviders.LoadSchemas(Path.Combine(root.FullName, "src", "Susu.Host"));
    }

    private static AppSettings Settings(string origin, string template, string? temperature)
    {
        var s = BuiltInCatalog.Defaults();
        var config = new Dictionary<string, string> { ["baseUrl"] = origin };
        if (temperature is not null) config["temperature"] = temperature;
        return s with
        {
            Revision = 3,
            Instances = [.. s.Instances.Select(i => i.Id == "openai" ? i with { Config = config } : i)],
            Prompts = [new PromptProfile("mine", "Mine", template)],
            Prompt = new PromptSettings("ielts-6.5", "mine", ["openai"]),
        };
    }

    private static PluginProvider Provider(AppSettings settings, Supervisor<HostSession> supervisor, IReadOnlyDictionary<string, IReadOnlyList<ConfigField>>? schemas)
    {
        var instance = settings.Instances.Single(i => i.Id == "openai");
        var service = settings.Services.Single(x => x.ServiceId == "openai/translate");
        return PluginTranslationProviders.Create(new TranslationServicePlan(TranslationPackages.Find("openai")!, instance, service), supervisor, settings, schemas);
    }

    [Fact]
    public async Task OpenAI_receives_the_rendered_prompt_and_its_declared_temperature()
    {
        string? staged = OpenAIPluginTests.StageHost();
        if (staged is null) return;
        var bodies = new System.Collections.Concurrent.ConcurrentQueue<JsonNode>();
        using var server = new LoopbackHttpServer(async (req, stream, ct) =>
        {
            bodies.Enqueue(JsonNode.Parse(req.Body)!);
            await LoopbackHttpServer.WriteSseHeadAsync(stream, ct);
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(OpenAIPluginTests.Sse("ok")), ct);
        });
        var options = OpenAIPluginTests.Options(staged, server.Origin);
        using var supervisor = new Supervisor<HostSession>(() =>
        {
            var s = HostSession.Start(options);
            s.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(s.Load(OpenAIPluginTests.PackageId, "plugins/openai").Ok);
            return s;
        }, SystemClock.Instance, TimeSpan.FromMinutes(10));
        var schemas = Schemas();

        async Task<JsonNode> Translate(PluginProvider provider, string text)
        {
            var outcome = await provider.TranslateAsync(new TranslateCall(text, "en", "zh-Hans", Guid.NewGuid().ToString("N"), Config, TimeSpan.FromSeconds(20)),
                _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
            Assert.IsType<ProviderOutcome.Success>(outcome);
            Assert.True(bodies.TryDequeue(out var body));
            return body!;
        }

        // Hostile source text: template syntax and a secret reference must arrive literally.
        const string text = "Say {{to}} and {{secret.apiKey}} {{level}}";
        var before = Provider(Settings(server.Origin, "[{{level}}] {{from}} -> {{to}} {{tone}}\n{{text}}", "0.3"), supervisor, schemas);
        var body = await Translate(before, text);
        var messages = body["messages"]!.AsArray();
        Assert.Single(messages);
        Assert.Equal("user", (string?)messages[0]!["role"]);
        Assert.Equal($"[雅思 6.5] English -> Chinese (Simplified) {{{{tone}}}}\n{text}", (string?)messages[0]!["content"]);
        Assert.Equal(0.3, body["temperature"]!.GetValue<double>());
        Assert.DoesNotContain("sk-test-secret-value", body.ToJsonString());

        // A save after the task started: the running task's provider keeps its snapshot, a new one uses the edit.
        var after = Provider(Settings(server.Origin, "NEW {{text}}", null), supervisor, schemas);
        Assert.StartsWith("[雅思 6.5]", (string?)(await Translate(before, "again"))["messages"]![0]!["content"]);
        var newBody = await Translate(after, "again");
        Assert.Equal("NEW again", (string?)newBody["messages"]![0]!["content"]);
        Assert.Null(newBody["temperature"]); // not set: the vendor default applies

        // A package config without the schema declaration never carries the temperature.
        var undeclared = Provider(Settings(server.Origin, "{{text}}", "0.3"), supervisor, schemas: null);
        Assert.Null((await Translate(undeclared, "x"))["temperature"]);
    }

    [Fact]
    public async Task Outside_the_scope_the_package_keeps_its_own_instruction()
    {
        string? staged = OpenAIPluginTests.StageHost();
        if (staged is null) return;
        JsonNode? seen = null;
        using var server = new LoopbackHttpServer(async (req, stream, ct) =>
        {
            seen = JsonNode.Parse(req.Body);
            await LoopbackHttpServer.WriteSseHeadAsync(stream, ct);
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(OpenAIPluginTests.Sse("ok")), ct);
        });
        var options = OpenAIPluginTests.Options(staged, server.Origin);
        using var supervisor = new Supervisor<HostSession>(() =>
        {
            var s = HostSession.Start(options);
            s.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(s.Load(OpenAIPluginTests.PackageId, "plugins/openai").Ok);
            return s;
        }, SystemClock.Instance, TimeSpan.FromMinutes(10));
        var settings = Settings(server.Origin, "CUSTOM {{text}}", null) with { Prompt = new PromptSettings("", "mine", ["claude"]) };
        var outcome = await Provider(settings, supervisor, Schemas()).TranslateAsync(new TranslateCall("hi {{to}}", "en", "fr", "a1", Config, TimeSpan.FromSeconds(20)),
            _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        Assert.IsType<ProviderOutcome.Success>(outcome);
        var messages = seen!["messages"]!.AsArray();
        Assert.Equal(["system", "user"], messages.Select(m => (string?)m!["role"]));
        Assert.Equal("hi {{to}}", (string?)messages[1]!["content"]);
        Assert.DoesNotContain("CUSTOM", seen.ToJsonString());
    }
}
