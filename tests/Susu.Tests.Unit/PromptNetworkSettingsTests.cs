using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Net;
using Susu.Plugins;
using Susu.Storage;
using Susu.Testing;
using Susu.Ui;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F07.3 through the settings commands: SetPrompt save/preview (CFG04), SetNetwork test through the broker with
/// per-path results and hotkey registration failures (CFG05).
/// </summary>
public class PromptNetworkSettingsTests
{
    internal sealed class Rig : IDisposable
    {
        public readonly TempRoot Root = new();
        public readonly FakePlatform Platform = new();
        public readonly SettingsStore Settings;
        public readonly ConfigService Config;
        public readonly ShellCoordinator Shell;
        public readonly List<(NetworkSettings Network, IReadOnlyList<NetworkProbeTarget> Targets)> Tests = [];
        private long counter;
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        public Rig(Action<FakePlatform>? before = null)
        {
            before?.Invoke(Platform);
            Settings = new SettingsStore(Root.Paths, new ManualClock());
            Config = new ConfigService(Settings, new SecretStore(Root.Paths.Secrets, new XorProtector()));
            var features = new FeatureRegistry();
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Selection, FeatureState.Available, null, [Capability.Translate]));
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Clipboard, FeatureState.Available, null, [Capability.Translate]));
            Shell = new ShellCoordinator(Platform, Config, features, c => c == Capability.Translate, new ShellOptions(false, false), _ => null, null,
                new TranslationBackend(true, (_, _) => null, TestNetwork: (network, targets, _) =>
                {
                    lock (Tests) Tests.Add((network, targets));
                    IReadOnlyList<NetworkProbeResult> results = [.. targets.Select(t => new NetworkProbeResult(t.Origin, t.Services, "proxy", false, ErrorKind.Network, null, 12))];
                    return Task.FromResult(results);
                }));
            Shell.Start();
            Shell.Open(WindowKind.Settings);
            Shell.OnPageMessage(WindowKind.Settings, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Ready", windowSessionId = Platform.Session(WindowKind.Settings) }));
        }

        public CommandResult Run(string name, object payload)
        {
            string id = $"c{++counter}";
            Shell.OnPageMessage(WindowKind.Settings, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Command", windowSessionId = Platform.Session(WindowKind.Settings), name, correlationId = id, payload }, Web));
            for (var poll = System.Diagnostics.Stopwatch.StartNew(); poll.Elapsed < Eventually.DefaultTimeout;)
            {
                var hit = Platform.Posted.FirstOrDefault(p => p.Envelope.CorrelationId == id);
                if (hit.Envelope is not null) return hit.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.CommandResult)!;
                Thread.Sleep(5);
            }
            throw new TimeoutException(id);
        }

        public SettingsView View => Shell.ProjectSettings(Settings.State);

        public CommandResult SavePrompt(string level, string profile, string[] scope, params PromptProfileView[] profiles)
            => Run(UiCommands.SavePrompt, new PromptSaveRequest(View.Revision, View.FileHash, level, profile, scope, profiles));

        public PromptPreviewView Preview(string template, string level, string text)
        {
            var result = Run(UiCommands.PreviewPrompt, new PromptPreviewRequest(template, level, text, "en", "zh-Hans"));
            Assert.True(result.Ok, result.Error);
            return result.Value!.Value.Deserialize(ContractsJson.Default.PromptPreviewView)!;
        }

        public void Dispose() { Settings.Dispose(); Root.Dispose(); }
    }

    [Fact]
    public void SetPrompt_is_projected_with_levels_scope_and_the_default_template()
    {
        using var rig = new Rig();
        var prompt = rig.View.Prompt!;
        Assert.Equal(("", ""), (prompt.Level, prompt.Profile));
        Assert.Equal(PromptCatalog.AiInstances, prompt.Scope);
        Assert.Equal(PromptCatalog.AiInstances, prompt.AiServices);
        Assert.Equal(7, prompt.Levels.Length);
        Assert.Equal(["text", "from", "to", "level"], prompt.Variables);
        Assert.Contains("{{text}}", prompt.DefaultTemplate);
    }

    [Fact]
    public void SetPrompt_saves_level_template_and_scope_and_projects_them_back()
    {
        using var rig = new Rig();
        var saved = rig.SavePrompt("ielts-7.0", "exam", ["claude", "openai"], new PromptProfileView("exam", " Exam ", "{{level}} {{text}}"));
        Assert.True(saved.Ok, saved.Error);
        var s = rig.Settings.State.Effective;
        Assert.Equal(new PromptProfile("exam", "Exam", "{{level}} {{text}}"), s.Prompts.Single());
        Assert.Equal(["openai", "claude"], s.Prompt.Scope); // stored in catalog order
        Assert.Equal(("ielts-7.0", "exam"), (rig.View.Prompt!.Level, rig.View.Prompt.Profile));
    }

    [Fact]
    public void SetPrompt_rejects_bad_levels_scopes_and_templates()
    {
        using var rig = new Rig();
        Assert.Equal("level", rig.SavePrompt("jlpt-n2", "", []).Error);
        Assert.Equal("scope", rig.SavePrompt("", "", ["deepl"]).Error); // a translation engine is never in scope
        Assert.Equal("profile", rig.SavePrompt("", "missing", []).Error);
        Assert.Equal("profile-id", rig.SavePrompt("", "", [], new PromptProfileView("Bad Id", "x", "t")).Error);
        var invalid = rig.SavePrompt("", "", [], new PromptProfileView("a", "A", "{{text}} {{text}}"), new PromptProfileView("b", "", "ok"));
        Assert.Equal("invalid", invalid.Error);
        string issues = invalid.Value!.Value.GetRawText();
        Assert.Contains("prompts.a.template", issues);
        Assert.Contains("text-repeated", issues);
        Assert.Contains("prompts.b.name", issues);
        Assert.Empty(rig.Settings.State.Effective.Prompts); // nothing was written
    }

    [Fact]
    public void Preview_renders_once_and_lists_unknown_variables()
    {
        using var rig = new Rig();
        var view = rig.Preview("{{from}}>{{to}} [{{level}}] {{style}}: {{text}}", "toefl-100", "say {{to}} {{secret.apiKey}}");
        Assert.Equal("English>Chinese (Simplified) [托福 100] {{style}}: say {{to}} {{secret.apiKey}}", view.Rendered);
        Assert.Equal(["style"], view.Unknown);
        Assert.Null(view.Problem);
        var defaults = rig.Preview("", "", "hello");
        Assert.EndsWith("hello", defaults.Rendered);
        Assert.Contains(PromptCatalog.NoLevel, defaults.Rendered);
        Assert.Equal("text-repeated", rig.Preview("{{text}}{{text}}", "", "x").Problem);
    }

    [Fact]
    public void Network_test_uses_the_proxy_being_edited_and_reports_each_path()
    {
        using var rig = new Rig();
        var edited = rig.View.Network with { ProxyMode = "http", ProxyHost = " 127.0.0.1 ", ProxyPort = 7890, ProxyUsername = "susu" };
        var result = rig.Run(UiCommands.TestNetwork, new NetworkTestRequest(edited));
        Assert.True(result.Ok, result.Error);
        var view = result.Value!.Value.Deserialize(ContractsJson.Default.NetworkTestView)!;
        var (network, targets) = rig.Tests.Single();
        Assert.Equal((ProxyMode.Http, "127.0.0.1", 7890, "susu"), (network.ProxyMode, network.ProxyHost, network.ProxyPort, network.ProxyUsername));
        Assert.Equal("https://api.mymemory.translated.net:443", targets.Single().Origin); // the enabled default service
        var path = view.Paths.Single();
        Assert.Equal((false, ErrorKind.Network, "proxy"), (path.Ok, path.Error, path.Route));
        Assert.Equal(["mymemory/translate"], path.Services);
        // Testing saves nothing and a failed test disables nothing.
        Assert.Equal(ProxyMode.System, rig.Settings.State.Effective.Network.ProxyMode);
        Assert.True(rig.Settings.State.Effective.Services.Single(s => s.ServiceId == "mymemory/translate").Enabled);
    }

    [Fact]
    public void Network_test_refuses_a_manual_proxy_without_an_address()
    {
        using var rig = new Rig();
        Assert.Equal("proxy-address", rig.Run(UiCommands.TestNetwork, new NetworkTestRequest(rig.View.Network with { ProxyMode = "http", ProxyHost = "", ProxyPort = 0 })).Error);
        Assert.Equal("proxy-address", rig.Run(UiCommands.TestNetwork, new NetworkTestRequest(rig.View.Network with { ProxyMode = "socks5", ProxyHost = "a b", ProxyPort = 1080 })).Error);
        Assert.Equal("proxy-mode", rig.Run(UiCommands.TestNetwork, new NetworkTestRequest(rig.View.Network with { ProxyMode = "pac" })).Error);
        Assert.Empty(rig.Tests);
    }

    [Fact] // CFG05: clipboard shares selection's registration, so it shows selection's RegisterHotKey failure
    public void A_failed_shared_registration_is_visible_on_both_selection_and_clipboard()
    {
        using var rig = new Rig(p => p.HotkeyOutcome["selectionTranslate"] = false);
        Assert.DoesNotContain("clipboardTranslate", rig.Platform.Registered.Keys); // one registration for the shared chord
        var hotkeys = rig.View.Hotkeys;
        Assert.Equal("failed", hotkeys.Single(h => h.Action == "selectionTranslate").State);
        Assert.Equal("failed", hotkeys.Single(h => h.Action == "clipboardTranslate").State);
        Assert.Equal("ok", hotkeys.Single(h => h.Action == "inputTranslate").State);
    }

    [Fact] // CFG05: any other shared chord is a conflict, and a conflicting chord is not registered at all
    public void Only_selection_and_clipboard_may_share_a_chord()
    {
        using var rig = new Rig();
        var view = rig.View;
        var hotkeys = view.Hotkeys.Select(h => h.Action == "inputTranslate" ? h with { Chord = "Alt+D" } : h).ToArray();
        var saved = rig.Run(UiCommands.SettingsSave, new SettingsSaveRequest(view.Revision, view.FileHash, view.General, hotkeys, view.Network, []));
        Assert.False(saved.Ok);
        Assert.Equal("invalid", saved.Error);
        Assert.Contains("conflict", saved.Value!.Value.GetRawText());
    }
}

/// <summary>F07.3 CFG05 through the real network broker: per-path results, proxy routing and loopback bypass.</summary>
public class NetworkProbeTests
{
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task A_dead_proxy_fails_remote_paths_one_by_one_while_local_services_stay_reachable()
    {
        using var local = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(404, "{}"));
        var network = new NetworkSettings(ProxyMode.Http, "127.0.0.1", ClosedPort(), "", 30);
        var results = await NetworkProbe.RunAsync(network, new InMemorySecretStore(),
            [new NetworkProbeTarget(local.Origin, ["ollama/translate"]), new NetworkProbeTarget("https://api.example.test:443", ["openai/translate"])],
            TestContext.Current.CancellationToken, TimeSpan.FromSeconds(5));

        var localPath = results.Single(r => r.Origin == local.Origin);
        Assert.Equal(("local", true, 404), (localPath.Route, localPath.Ok, localPath.Status)); // any answer counts as reachable
        var remote = results.Single(r => r.Origin == "https://api.example.test:443");
        Assert.Equal("proxy", remote.Route);
        Assert.False(remote.Ok);
        Assert.Equal(ErrorKind.Network, remote.Error);
        Assert.Equal(["openai/translate"], remote.Services);
        Assert.Equal(1, local.RequestCount);
    }

    [Fact]
    public async Task A_cloud_origin_is_sent_to_a_proxy_on_loopback()
    {
        // Common setup (a local proxy client on 127.0.0.1:7890): the broker must connect to the proxy even though
        // loopback is never a valid address for a cloud origin itself. This test proxy cannot tunnel TLS, so the
        // call fails after the CONNECT; what matters is that the CONNECT reached the proxy.
        using var proxy = new LoopbackProxyServer();
        var network = new NetworkSettings(ProxyMode.Http, "127.0.0.1", proxy.Port, "", 30);
        var results = await NetworkProbe.RunAsync(network, new InMemorySecretStore(), [new NetworkProbeTarget("https://api.example.test:443", ["openai/translate"])],
            TestContext.Current.CancellationToken, TimeSpan.FromSeconds(5));
        Assert.Equal("proxy", results.Single().Route);
        Assert.Contains(proxy.Received, r => r.Method == "CONNECT" && r.AbsoluteUri.StartsWith("api.example.test:443", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_proxy_goes_direct_and_the_cloud_address_check_still_applies()
    {
        var results = await NetworkProbe.RunAsync(new NetworkSettings(ProxyMode.None, "", 0, "", 30), new InMemorySecretStore(),
            [new NetworkProbeTarget("https://localhost.:443", ["x/translate"])], TestContext.Current.CancellationToken, TimeSpan.FromSeconds(5));
        var path = results.Single();
        Assert.Equal("direct", path.Route);
        Assert.False(path.Ok); // a cloud origin that resolves to loopback is refused without a proxy
    }

    [Fact]
    public async Task The_production_broker_sends_local_services_direct_when_the_proxy_is_down()
    {
        using var target = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"ok":true}"""));
        var store = new FakeSettingsStore(BuiltInCatalog.Defaults() with { Network = new NetworkSettings(ProxyMode.Http, "127.0.0.1", ClosedPort(), "", 30) });
        using var provider = new NetworkBrokerProvider(store, new InMemorySecretStore());
        provider.Current.LocalOrigins.Approve(target.Origin);
        var request = new BrokerHttpRequest("GET", new Uri(target.Origin + "/api/tags"), [], RequestBody.None, [], [],
            (_, _) => throw new InvalidOperationException(), null, ResponseKind.Json, [], null, LocalOriginApproved: true);
        var outcome = await provider.Current.ExecuteAsync(request, TestContext.Current.CancellationToken);
        Assert.IsType<BrokerSuccess>(outcome);
    }

    [Fact] // the proxy-tunnel exemption keys on CONNECT, so a caller can never send CONNECT itself
    public async Task The_broker_refuses_a_CONNECT_request()
    {
        using var target = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, "{}"));
        var options = new NetworkBrokerOptions { Proxy = new WebProxy("http://127.0.0.1:" + ClosedPort()), Timeout = TimeSpan.FromSeconds(5) };
        options.LocalOrigins.Approve(target.Origin);
        using var broker = new NetworkBroker(options);
        var request = new BrokerHttpRequest("CONNECT", new Uri(target.Origin + "/"), [], RequestBody.None, [], [],
            (_, _) => throw new InvalidOperationException(), null, ResponseKind.Text, [], null, LocalOriginApproved: true);
        var failure = Assert.IsType<BrokerFailure>(await broker.ExecuteAsync(request, TestContext.Current.CancellationToken));
        Assert.Contains("not allowed", failure.Detail);
        Assert.Equal(0, target.RequestCount);
    }

    [Fact]
    public void Probe_targets_are_the_enabled_services_grouped_by_origin()
    {
        var s = BuiltInCatalog.Defaults();
        s = s with
        {
            Services = [.. s.Services.Select(x => x.Instance is "openai" or "deepl" ? x with { Enabled = true } : x)],
            Instances = [.. s.Instances.Select(i => i.Id == "openai" ? i with { Config = new Dictionary<string, string> { ["baseUrl"] = "http://127.0.0.1:11434" } } : i)],
        };
        var targets = NetworkPaths.For(s);
        Assert.Equal(["https://api.mymemory.translated.net:443", "https://api-free.deepl.com:443", "http://127.0.0.1:11434"], targets.Select(t => t.Origin));
        Assert.Equal(["openai/translate"], targets.Last().Services);
    }
}
