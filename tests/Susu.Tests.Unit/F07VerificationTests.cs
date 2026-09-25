using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Net;
using Susu.Plugins;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F07 independent verification: adversarial cases for CFG01, CFG02, CFG04, CFG05 and A02 that the
/// sub-item tests did not cover (racing loads, paging through the shell, rebinding, loopback bypass under a
/// proxy, hand-edited templates, the AI model vs the ASR selections).
/// </summary>
public class F07VerificationTests
{
    private static OptionsView AsOptions(CommandResult result)
    {
        Assert.True(result.Ok, result.Error);
        return result.Value!.Value.Deserialize(ContractsJson.Default.OptionsView)!;
    }

    private static void WaitForLoad(ServiceConfigTests.Rig rig, int count)
    {
        for (int i = 0; i < 400; i++) { lock (rig.Loads) if (rig.Loads.Count >= count) return; Thread.Sleep(5); }
        throw new TimeoutException("the options loader was never called");
    }

    // ---------- CFG02 ----------

    [Fact] // CFG02: the address changes while the model list is loading; the old list never reaches the page or the cache
    public void CFG02_a_list_in_flight_when_the_address_changes_never_writes_back()
    {
        using var rig = new ServiceConfigTests.Rig();
        rig.GrantKey();
        long revision = rig.Field("model").OptionsRevision;
        var gate = new TaskCompletionSource<OptionsLoad>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Answer = _ => gate.Task;
        string pending = rig.Send(UiCommands.LoadOptions, new LoadOptionsRequest("openai", "model", revision));
        WaitForLoad(rig, 1);
        Assert.True(rig.SaveConfig(("baseUrl", "https://llm.example.com")).Ok);
        gate.SetResult(new OptionsLoad([new OptionItem("old-address-model", "old-address-model")]));
        var view = AsOptions(rig.Wait(pending));
        Assert.True(view.Stale);
        Assert.Empty(view.Items);
        long current = rig.Field("model").OptionsRevision;
        Assert.True(current > revision);
        Assert.Equal(current, view.DependsOnRevision);

        // The new address needs its grant first; after that a fresh load runs (nothing came from the cache).
        Assert.Equal("missing-credential", rig.Run(UiCommands.LoadOptions, new LoadOptionsRequest("openai", "model", current)).Error);
        Assert.True(rig.Run(UiCommands.BindAccount, new BindAccountRequest("openai")).Ok);
        current = rig.Field("model").OptionsRevision;
        rig.Answer = _ => Task.FromResult(new OptionsLoad([new OptionItem("new-address-model", "new-address-model")]));
        var fresh = rig.Options(current);
        Assert.Equal(["new-address-model"], fresh.Items.Select(i => i.Value));
        Assert.False(fresh.Cached);
    }

    [Fact] // CFG02: switching the instance to another saved account while loading makes the answer stale
    public void CFG02_a_list_in_flight_when_the_account_is_rebound_never_writes_back()
    {
        using var rig = new ServiceConfigTests.Rig();
        rig.GrantKey("sk-account-A");
        // A second account holding an apiKey (the Claude instance's own account).
        Assert.True(rig.Run(UiCommands.SecretWriteNew, new SecretWriteRequest("claude", "apiKey", "sk-account-B", ConfirmGrants: true)).Ok);
        string otherAccount = rig.Settings.State.Effective.Instances.Single(i => i.Id == "claude").AccountBindings["apiKey"];
        Assert.NotEqual(rig.OpenAI.AccountId, otherAccount);

        long revision = rig.Field("model").OptionsRevision;
        var gate = new TaskCompletionSource<OptionsLoad>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Answer = _ => gate.Task;
        string pending = rig.Send(UiCommands.LoadOptions, new LoadOptionsRequest("openai", "model", revision));
        WaitForLoad(rig, 1);
        Assert.True(rig.Run(UiCommands.BindAccount, new BindAccountRequest("openai", otherAccount, ConfirmGrants: true)).Ok);
        gate.SetResult(new OptionsLoad([new OptionItem("model-of-A", "model-of-A")]));

        var view = AsOptions(rig.Wait(pending));
        Assert.True(view.Stale);
        Assert.Empty(view.Items);
        Assert.Equal(otherAccount, rig.OpenAI.AccountId);
        long current = rig.Field("model").OptionsRevision;
        Assert.True(current > revision);
        rig.Answer = _ => Task.FromResult(new OptionsLoad([new OptionItem("model-of-B", "model-of-B")]));
        Assert.Equal(["model-of-B"], rig.Options(current).Items.Select(i => i.Value));
        Assert.DoesNotContain("sk-account-", rig.Platform.AllJson(), StringComparison.Ordinal);
    }

    [Fact] // CFG02: pages go through the shell one at a time, each page cached on its own cursor; a bad cursor is refused
    public void CFG02_paging_passes_the_cursor_and_caches_each_page()
    {
        using var rig = new ServiceConfigTests.Rig();
        rig.GrantKey();
        rig.Answer = q => Task.FromResult(q.Cursor is null
            ? new OptionsLoad([.. Enumerable.Range(0, OptionsBroker.MaxItems).Select(i => new OptionItem($"m{i}", $"m{i}"))], "page-2")
            : new OptionsLoad([new OptionItem("last", "last")]));
        long revision = rig.Field("model").OptionsRevision;
        var first = rig.Options(revision);
        Assert.Equal(OptionsBroker.MaxItems, first.Items.Length);
        Assert.Equal("page-2", first.NextCursor);
        var second = rig.Options(revision, "page-2");
        Assert.Equal(["last"], second.Items.Select(i => i.Value));
        Assert.Null(second.NextCursor);
        lock (rig.Loads) Assert.Equal([null, "page-2"], rig.Loads.Select(l => l.Cursor));
        Assert.True(rig.Options(revision).Cached);
        Assert.True(rig.Options(revision, "page-2").Cached);
        lock (rig.Loads) Assert.Equal(2, rig.Loads.Count);

        Assert.False(rig.Run(UiCommands.LoadOptions, new LoadOptionsRequest("openai", "model", revision, new string('c', OptionsBroker.MaxCursorLength + 1))).Ok);
        // A page over the limit is an error, not a truncated list.
        rig.Answer = _ => Task.FromResult(new OptionsLoad([.. Enumerable.Range(0, OptionsBroker.MaxItems + 1).Select(i => new OptionItem($"x{i}", $"x{i}"))]));
        var tooMany = rig.Options(revision, refresh: true);
        Assert.Equal(ErrorKind.BadResponse, tooMany.Error);
        Assert.Empty(tooMany.Items);
    }

    [Fact] // CFG02: a loader that throws with the key in its message reports a kind only and keeps the saved model
    public void CFG02_a_throwing_load_keeps_the_selection_and_leaks_no_credential()
    {
        using var rig = new ServiceConfigTests.Rig();
        rig.GrantKey("sk-LEAK-CHECK-123");
        Assert.True(rig.SaveConfig(("model", "gpt-kept")).Ok);
        rig.Answer = _ => throw new InvalidOperationException("401 for key sk-LEAK-CHECK-123 at https://api.openai.com");
        var view = rig.Options(rig.Field("model").OptionsRevision);
        Assert.Equal(ErrorKind.Unavailable, view.Error);
        Assert.Empty(view.Items);
        Assert.Equal("gpt-kept", rig.Field("model").Value);
        Assert.DoesNotContain("sk-LEAK-CHECK-123", rig.Platform.AllJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("401 for key", rig.Platform.AllJson(), StringComparison.Ordinal);
    }

    // ---------- A02 ----------

    [Fact] // A02: changing the AI translation model (openai config) leaves both ASR selections alone
    public void A02_changing_the_ai_translation_model_does_not_change_the_asr_selections()
    {
        using var rig = new ServiceConfigTests.Rig();
        var before = rig.Shell.ProjectSettings(rig.Settings.State).Speech!;
        Assert.True(rig.SaveConfig(("model", "gpt-4.1-mini")).Ok);
        Assert.True(rig.SaveConfig(("model", "")).Ok);
        Assert.True(rig.SaveConfig(("model", "o4-mini")).Ok);
        var after = rig.Shell.ProjectSettings(rig.Settings.State).Speech!;
        Assert.Equal((before.Asr.Instance, before.Asr.Model), (after.Asr.Instance, after.Asr.Model));
        Assert.Equal((before.VideoAsr.Instance, before.VideoAsr.Model), (after.VideoAsr.Instance, after.VideoAsr.Model));
        Assert.Equal(before.Tts.Instance, after.Tts.Instance);
        Assert.Equal("whisper-1", after.Asr.Model);
        Assert.Equal("o4-mini", rig.Field("model").Value);
    }

    // ---------- CFG01 ----------

    [Fact] // CFG01: a failed validation reports the credential as invalid but neither disables nor re-identifies the service
    public void CFG01_an_auth_failure_marks_the_credential_invalid_without_touching_availability_or_identity()
    {
        var provider = new ScriptedProvider("openai/translate", ScriptedProvider.Generous, new Step.Fail(new ProviderError(ErrorKind.Auth, "bad key sk-VALIDATE")));
        using var rig = new TranslationSettingsCommandTests.Rig(validation: (_, id) => id == "openai/translate" ? provider : null);
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("openai", "apiKey", "sk-VALIDATE", true)).Ok);
        rig.Enable("openai/translate");
        var before = rig.Service("openai/translate");
        Assert.Equal("Ready", before.Availability); // complete config: usable, which says nothing about validity

        var result = rig.Run(WindowKind.Settings, UiCommands.ValidateProvider, new ValidateProviderRequest("openai/translate"));
        Assert.True(result.Ok, result.Error);
        var view = result.Value!.Value.Deserialize(ContractsJson.Default.ServiceValidationView)!;
        Assert.Equal(("invalid", ErrorKind.Auth), (view.Credential, view.Error));
        var after = rig.Service("openai/translate");
        Assert.Equal((before.ServiceId, before.InstanceId, before.Capability, before.Page, before.Order, before.Enabled, before.AccountId),
            (after.ServiceId, after.InstanceId, after.Capability, after.Page, after.Order, after.Enabled, after.AccountId));
        Assert.DoesNotContain("sk-VALIDATE", rig.Platform.AllJson(), StringComparison.Ordinal);
    }

    // ---------- CFG04 ----------

    [Fact] // CFG04: templates that only a hand edit could produce still render the source text exactly once, literally
    public void CFG04_hand_edited_and_malformed_templates_never_insert_the_text_twice_or_expand_it()
    {
        var snapshot = new PromptSnapshot(1, "A {{text}} B {{ text }} C", "L");
        Assert.Equal("A {{to}} B {{ text }} C", snapshot.Render("{{to}}", "en", "fr"));
        Assert.Equal("text-repeated", PromptCatalog.CheckTemplate("{{text}}{{ text }}"));

        // Nested and unbalanced braces: unknown names stay literal; the text is appended once.
        Assert.Equal("{{ {{text}} }}\n\nSRC", PromptTemplate.Render("{{ {{text}} }}", new Dictionary<string, string>(), "SRC"));
        Assert.Equal("{{Text}} {{to\n\nSRC", PromptTemplate.Render("{{Text}} {{to", new Dictionary<string, string> { ["to"] = "X" }, "SRC"));
        // A language code that is not canonical is passed through but never re-scanned.
        Assert.Equal("{{text}}->{{level}}: S", new PromptSnapshot(1, "{{from}}->{{to}}: {{text}}", "L").Render("S", "{{text}}", "{{level}}"));
        // A secret reference in the template itself is literal text.
        Assert.Equal("key={{secret.apiKey}} S", new PromptSnapshot(1, "key={{secret.apiKey}} {{text}}", "L").Render("S", "en", "fr"));
    }

    [Fact] // CFG04: the preview never resolves a secret reference, even with a key saved
    public void CFG04_preview_with_a_saved_key_keeps_secret_references_literal()
    {
        using var rig = new PromptNetworkSettingsTests.Rig();
        Assert.True(rig.Run(UiCommands.SecretWriteNew, new SecretWriteRequest("openai", "apiKey", "sk-PREVIEW-SECRET", true)).Ok);
        var preview = rig.Preview("{{secret.apiKey}} {{text}}", "", "{{secret.apiKey}} {{text}}");
        Assert.Equal("{{secret.apiKey}} {{secret.apiKey}} {{text}}", preview.Rendered);
        Assert.Contains("secret.apiKey", preview.Unknown);
        Assert.DoesNotContain("sk-PREVIEW-SECRET", rig.Platform.AllJson(), StringComparison.Ordinal);
    }

    // ---------- CFG05 ----------

    [Fact] // CFG05: a RegisterHotKey failure on a non-shared chord is visible on that action only
    public void CFG05_a_failed_registration_shows_on_its_own_action_only()
    {
        using var rig = new PromptNetworkSettingsTests.Rig(p => p.HotkeyOutcome["inputTranslate"] = false);
        var hotkeys = rig.View.Hotkeys;
        Assert.Equal("failed", hotkeys.Single(h => h.Action == "inputTranslate").State);
        Assert.Equal("ok", hotkeys.Single(h => h.Action == "selectionTranslate").State);
        Assert.Equal("ok", hotkeys.Single(h => h.Action == "clipboardTranslate").State);
    }

    private static BrokerHttpRequest Get(string uri, bool approved = false)
        => new("GET", new Uri(uri), [], RequestBody.None, [], [], (_, _) => throw new InvalidOperationException(), null, ResponseKind.Json, [], null, LocalOriginApproved: approved);

    private static TcpListener? Listen(IPAddress address)
    {
        try { var l = new TcpListener(address, 0); l.Start(); return l; }
        catch (SocketException) { return null; }
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Theory] // CFG05 / F07.3 broker change: with a proxy set, loopback is bypassed and the direct connection is still checked
    [InlineData("127.0.0.1", true)]
    [InlineData("localhost", true)]
    [InlineData("[::1]", false)]
    public async Task CFG05_a_bypassed_loopback_target_that_is_not_an_approved_local_origin_is_refused(string host, bool v4)
    {
        using var listener = Listen(v4 ? IPAddress.Loopback : IPAddress.IPv6Loopback);
        if (listener is null) return; // no IPv6 loopback on this machine
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var proxy = new LoopbackProxyServer();
        foreach (var network in new[] { new NetworkSettings(ProxyMode.Http, "127.0.0.1", proxy.Port, "", 30), new NetworkSettings(ProxyMode.Http, "127.0.0.1", ClosedPort(), "", 30) })
        {
            var store = new FakeSettingsStore(BuiltInCatalog.Defaults() with { Network = network });
            using var provider = new NetworkBrokerProvider(store, new InMemorySecretStore());
            var outcome = await provider.Current.ExecuteAsync(Get($"http://{host}:{port}/v1/models"), TestContext.Current.CancellationToken);
            Assert.IsType<BrokerFailure>(outcome);
        }
        Assert.False(listener.Pending()); // no connection reached the target
        Assert.Empty(proxy.Received);      // and nothing was handed to the proxy either
    }

    [Fact] // CFG05: an approved local service is reached directly under a working proxy; the proxy sees nothing
    public async Task CFG05_an_approved_local_service_bypasses_a_working_proxy()
    {
        using var target = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"ok":true}"""));
        using var proxy = new LoopbackProxyServer();
        var store = new FakeSettingsStore(BuiltInCatalog.Defaults() with { Network = new NetworkSettings(ProxyMode.Http, "127.0.0.1", proxy.Port, "", 30) });
        using var provider = new NetworkBrokerProvider(store, new InMemorySecretStore());
        provider.Current.LocalOrigins.Approve(target.Origin);
        Assert.IsType<BrokerSuccess>(await provider.Current.ExecuteAsync(Get(target.Origin + "/api/tags", approved: true), TestContext.Current.CancellationToken));
        Assert.Equal(1, target.RequestCount);
        Assert.Empty(proxy.Received);
    }
}
