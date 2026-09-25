using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Plugins;
using Susu.Storage;
using Susu.Testing;
using Susu.Ui;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F07.2 through the settings commands: controls generated from the shipped manifests, host-side config checks,
/// and Settings.LoadOptions with dependency revisions (CFG02: an old revision never writes back, a failed
/// load keeps the saved selection and carries no credential).
/// </summary>
public class ServiceConfigTests
{
    private static readonly string RepoRoot = FindRoot();
    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Susu.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    internal sealed class Rig : IDisposable
    {
        public readonly TempRoot Root = new();
        public readonly FakePlatform Platform = new();
        public readonly SettingsStore Settings;
        public readonly ConfigService Config;
        public readonly ShellCoordinator Shell;
        public readonly ManualClock Clock = new();
        public readonly OptionsBroker Broker;
        public readonly List<OptionsQuery> Loads = [];
        public Func<OptionsQuery, Task<OptionsLoad>> Answer = q => Task.FromResult(new OptionsLoad([new OptionItem("gpt-a", "gpt-a"), new OptionItem("gpt-b", "gpt-b")]));
        private long counter;
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        public Rig()
        {
            Settings = new SettingsStore(Root.Paths, new ManualClock());
            Config = new ConfigService(Settings, new SecretStore(Root.Paths.Secrets, new XorProtector()));
            Broker = new OptionsBroker((q, _) => { lock (Loads) Loads.Add(q); return Answer(q); }, Clock);
            var schemas = PluginTranslationProviders.LoadSchemas(Path.Combine(RepoRoot, "src", "Susu.Host"));
            var features = new FeatureRegistry();
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
            Shell = new ShellCoordinator(Platform, Config, features, c => c == Capability.Translate, new ShellOptions(false, false), _ => null, null,
                new TranslationBackend(true, (_, _) => null, null, schemas, Broker));
            Shell.Start();
            Shell.Open(WindowKind.Settings);
            Shell.OnPageMessage(WindowKind.Settings, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Ready", windowSessionId = Platform.Session(WindowKind.Settings) }));
        }

        public string Send(string name, object payload)
        {
            string id = $"c{++counter}";
            Shell.OnPageMessage(WindowKind.Settings, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Command", windowSessionId = Platform.Session(WindowKind.Settings), name, correlationId = id, payload }, Web));
            return id;
        }

        public CommandResult Wait(string id)
        {
            for (int i = 0; i < 1000; i++)
            {
                var hit = Platform.Posted.FirstOrDefault(p => p.Envelope.CorrelationId == id);
                if (hit.Envelope is not null) return hit.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.CommandResult)!;
                Thread.Sleep(5);
            }
            throw new TimeoutException(id);
        }

        public CommandResult Run(string name, object payload) => Wait(Send(name, payload));

        public ServiceView OpenAI => Shell.ProjectSettings(Settings.State).Services.Single(s => s.ServiceId == "openai/translate");
        public ConfigFieldView Field(string name) => OpenAI.Config!.Single(f => f.Name == name);

        public void GrantKey(string value = "sk-first")
            => Assert.True(Run(UiCommands.SecretWriteNew, new SecretWriteRequest("openai", "apiKey", value, ConfirmGrants: true)).Ok);

        public CommandResult SaveConfig(params (string Name, string Value)[] values)
            => Run(UiCommands.SaveServiceConfig, new ServiceConfigRequest("openai", OpenAI.InstanceRevision, [.. values.Select(v => new ConfigValueView(v.Name, v.Value))]));

        public OptionsView Options(long revision, string? cursor = null, bool refresh = false)
        {
            var result = Run(UiCommands.LoadOptions, new LoadOptionsRequest("openai", "model", revision, cursor, refresh));
            Assert.True(result.Ok, result.Error);
            return result.Value!.Value.Deserialize(ContractsJson.Default.OptionsView)!;
        }

        public void Dispose() { Settings.Dispose(); Root.Dispose(); }
    }

    [Fact]
    public void Controls_are_generated_from_the_shipped_manifest_schema_not_hard_coded()
    {
        using var rig = new Rig();
        var model = rig.Field("model");
        Assert.Equal(("string", "gpt-4o-mini", true), (model.Type, model.Default, model.Dynamic));
        Assert.Null(model.Value);
        Assert.True(model.OptionsRevision > 0);
        var baseUrl = rig.Field("baseUrl");
        Assert.Equal(("uri", "advanced", false), (baseUrl.Format, baseUrl.Group, baseUrl.Dynamic));
        // Packages whose manifest declares no config get no generated controls.
        var views = rig.Shell.ProjectSettings(rig.Settings.State).Services;
        Assert.Null(views.Single(s => s.ServiceId == "deepl/translate").Config);
        Assert.Null(views.Single(s => s.ServiceId == "mymemory/translate").Config);
    }

    [Fact]
    public void Config_values_are_checked_by_the_host_and_saved_on_the_instance()
    {
        using var rig = new Rig();
        long before = rig.OpenAI.InstanceRevision;
        Assert.True(rig.SaveConfig(("model", "gpt-b")).Ok);
        Assert.Equal("gpt-b", rig.Field("model").Value);
        Assert.Equal(before + 1, rig.OpenAI.InstanceRevision);
        Assert.Equal("gpt-b", rig.Settings.State.Effective.Instances.Single(i => i.Id == "openai").Config["model"]);

        var invalid = rig.SaveConfig(("baseUrl", "http://llm.example.com/v1"));
        Assert.Equal("invalid", invalid.Error);
        Assert.Contains("instances.openai.config.baseUrl", invalid.Value!.Value.GetRawText());
        Assert.Equal("unknown-field", rig.SaveConfig(("prompt", "x")).Error); // not declared by the schema
        Assert.Equal("conflict", rig.Run(UiCommands.SaveServiceConfig, new ServiceConfigRequest("openai", before, [new ConfigValueView("model", "gpt-a")])).Error);
        Assert.Equal("unknown-instance", rig.Run(UiCommands.SaveServiceConfig, new ServiceConfigRequest("deepl", 1, [])).Error);

        Assert.True(rig.SaveConfig(("model", "")).Ok); // empty = back to the default
        Assert.Null(rig.Field("model").Value);
    }

    [Fact]
    public void A_new_address_drops_the_old_origin_grant_and_bumps_the_model_revision()
    {
        using var rig = new Rig();
        rig.GrantKey();
        Assert.All(rig.OpenAI.CredentialTargets!, t => Assert.True(t.Granted));
        long revision = rig.Field("model").OptionsRevision;
        Assert.True(rig.SaveConfig(("baseUrl", "https://llm.example.com")).Ok);
        var target = Assert.Single(rig.OpenAI.CredentialTargets!);
        Assert.Equal(("https://llm.example.com:443", true, false), (target.Origin, target.Saved, target.Granted));
        Assert.True(rig.Field("model").OptionsRevision > revision);
        // A field the model does not depend on leaves its revision alone.
        long afterAddress = rig.Field("model").OptionsRevision;
        Assert.True(rig.SaveConfig(("model", "gpt-b")).Ok);
        Assert.Equal(afterAddress, rig.Field("model").OptionsRevision);
    }

    [Fact]
    public void Options_need_a_granted_key_then_load_and_come_from_the_cache()
    {
        using var rig = new Rig();
        long revision = rig.Field("model").OptionsRevision;
        Assert.Equal("missing-credential", rig.Run(UiCommands.LoadOptions, new LoadOptionsRequest("openai", "model", revision)).Error);
        Assert.Empty(rig.Loads);
        rig.GrantKey();
        revision = rig.Field("model").OptionsRevision;
        var first = rig.Options(revision);
        Assert.Equal(["gpt-a", "gpt-b"], first.Items.Select(i => i.Value));
        Assert.False(first.Cached);
        Assert.Equal(("options", "model", revision), (rig.Loads.Single().Method, rig.Loads.Single().Field, rig.Loads.Single().Revision));
        Assert.True(rig.Options(revision).Cached);
        Assert.Single(rig.Loads);
        Assert.False(rig.Options(revision, refresh: true).Cached);
        Assert.Equal("unknown-field", rig.Run(UiCommands.LoadOptions, new LoadOptionsRequest("openai", "baseUrl", revision)).Error); // not dynamic
    }

    [Fact]
    public void A_request_for_an_old_revision_is_answered_stale_without_calling_the_plugin()
    {
        using var rig = new Rig();
        rig.GrantKey();
        long old = rig.Field("model").OptionsRevision;
        Assert.NotEmpty(rig.Options(old).Items);
        Assert.True(rig.SaveConfig(("baseUrl", "https://llm.example.com")).Ok);
        Assert.True(rig.Run(UiCommands.BindAccount, new BindAccountRequest("openai")).Ok); // confirm the new origin
        long current = rig.Field("model").OptionsRevision;
        Assert.True(current > old);
        var stale = rig.Options(old);
        Assert.True(stale.Stale);
        Assert.Equal(current, stale.DependsOnRevision);
        Assert.Empty(stale.Items);
        Assert.Single(rig.Loads);
        Assert.False(rig.Options(current).Cached); // the old list is not reused for the new address
    }

    [Fact]
    public void A_list_in_flight_when_the_key_changes_never_writes_back()
    {
        using var rig = new Rig();
        rig.GrantKey("sk-account-A");
        long revision = rig.Field("model").OptionsRevision;
        var gate = new TaskCompletionSource<OptionsLoad>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Answer = _ => gate.Task;
        string pending = rig.Send(UiCommands.LoadOptions, new LoadOptionsRequest("openai", "model", revision));
        for (int i = 0; i < 400 && rig.Loads.Count == 0; i++) Thread.Sleep(5);
        rig.GrantKey("sk-account-B"); // switched account while the list was loading
        gate.SetResult(new OptionsLoad([new OptionItem("model-of-A", "model-of-A")]));
        var view = rig.Wait(pending).Value!.Value.Deserialize(ContractsJson.Default.OptionsView)!;
        Assert.True(view.Stale);
        Assert.Empty(view.Items);
        long current = rig.Field("model").OptionsRevision;
        Assert.Equal(current, view.DependsOnRevision);
        rig.Answer = _ => Task.FromResult(new OptionsLoad([new OptionItem("model-of-B", "model-of-B")]));
        var fresh = rig.Options(current);
        Assert.Equal(["model-of-B"], fresh.Items.Select(i => i.Value));
        Assert.False(fresh.Cached);
    }

    [Fact]
    public void A_failed_load_keeps_the_saved_selection_and_reports_only_the_kind()
    {
        using var rig = new Rig();
        rig.GrantKey("sk-SECRET-value");
        Assert.True(rig.SaveConfig(("model", "gpt-chosen")).Ok);
        rig.Answer = _ => Task.FromResult(new OptionsLoad(null, null, ErrorKind.Auth));
        var view = rig.Options(rig.Field("model").OptionsRevision);
        Assert.Equal(ErrorKind.Auth, view.Error);
        Assert.Empty(view.Items);
        Assert.Equal("gpt-chosen", rig.Field("model").Value);
        Assert.DoesNotContain("sk-SECRET-value", rig.Platform.AllJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Save_service_config_and_load_options_are_settings_window_only()
    {
        Assert.True(UiCommands.IsAllowed(WindowKind.Settings, UiCommands.SaveServiceConfig));
        Assert.True(UiCommands.IsAllowed(WindowKind.Settings, UiCommands.LoadOptions));
        Assert.False(UiCommands.IsAllowed(WindowKind.Main, UiCommands.SaveServiceConfig));
        Assert.False(UiCommands.IsAllowed(WindowKind.Main, UiCommands.LoadOptions));
    }
}
