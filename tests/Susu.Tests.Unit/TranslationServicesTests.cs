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

/// <summary>F06.3a: translation services built from instance settings and account bindings.</summary>
public class TranslationServicesTests
{
    private static readonly Func<string, string, bool> NoSecrets = (_, _) => false;

    private static AppSettings Enable(AppSettings s, string serviceId, bool enabled = true)
        => s with { Services = [.. s.Services.Select(x => x.ServiceId == serviceId ? x with { Enabled = enabled } : x)] };

    /// <summary>Binds and grants every credential target of <paramref name="instanceId"/> to an account of the same id.</summary>
    private static AppSettings WithGrantedKey(AppSettings s, string instanceId, IReadOnlyDictionary<string, string>? config = null)
    {
        var package = TranslationPackages.Find(instanceId)!;
        var instance = s.Instances.Single(i => i.Id == instanceId);
        instance = instance with { Config = config ?? instance.Config, AccountBindings = package.SecretNames.ToDictionary(n => n, _ => instanceId) };
        var account = CredentialAuthorizer.Confirm(new AccountSettings(instanceId, instanceId, package.SecretNames, []), package.RequiredGrants(instance.Config));
        return s with { Accounts = [.. s.Accounts, account], Instances = [.. s.Instances.Select(i => i.Id == instanceId ? instance : i)] };
    }

    private static Supervisor<HostSession> NeverLaunched() => new(() => throw new InvalidOperationException("launched"), new ManualClock(), TimeSpan.FromMinutes(10));

    [Fact] // fresh config works with no key: MyMemory only; OpenAI is not enabled by default
    public void Fresh_settings_resolve_to_mymemory_only()
    {
        var plans = TranslationPackages.Resolve(BuiltInCatalog.Defaults(), NoSecrets);
        Assert.Equal(["mymemory/translate"], plans.Select(p => p.Service.ServiceId));
        Assert.False(BuiltInCatalog.Defaults().Services.Single(x => x.ServiceId == "openai/translate").Enabled);
    }

    [Fact] // an enabled keyed service needs a saved key and a grant for its exact origin and use
    public void Keyed_services_need_saved_and_granted_credentials()
    {
        var s = Enable(BuiltInCatalog.Defaults(), "deepl/translate");
        Assert.Equal(Availability.MissingCredential, TranslationPackages.AvailabilityOf(s, s.Services.Single(x => x.ServiceId == "deepl/translate"), NoSecrets));
        Assert.DoesNotContain(TranslationPackages.Resolve(s, NoSecrets), p => p.Package.InstanceId == "deepl");

        // Bound and saved, but no grant yet: still not usable.
        var bound = s with
        {
            Accounts = [new AccountSettings("deepl", "deepl", ["apiKey"], [])],
            Instances = [.. s.Instances.Select(i => i.Id == "deepl" ? i with { AccountBindings = new Dictionary<string, string> { ["apiKey"] = "deepl" } } : i)],
        };
        Assert.DoesNotContain(TranslationPackages.Resolve(bound, (_, _) => true), p => p.Package.InstanceId == "deepl");

        var granted = WithGrantedKey(s, "deepl");
        Assert.DoesNotContain(TranslationPackages.Resolve(granted, NoSecrets), p => p.Package.InstanceId == "deepl"); // key not saved
        Assert.Equal(["mymemory/translate", "deepl/translate"], TranslationPackages.Resolve(granted, (_, _) => true).Select(p => p.Service.ServiceId));
    }

    [Fact] // enable, disable and reorder take effect on the next build, no restart
    public void Providers_follow_order_and_enabled_flags()
    {
        var s = WithGrantedKey(WithGrantedKey(WithGrantedKey(BuiltInCatalog.Defaults(), "deepl"), "openai"), "tencent-translate");
        s = Enable(Enable(Enable(s, "deepl/translate"), "openai/translate"), "tencent-translate/translate");
        var supervisor = NeverLaunched();
        Assert.Equal(["mymemory/translate", "tencent-translate/translate", "deepl/translate", "openai/translate"],
            PluginTranslationProviders.Build(s, (_, _) => true, supervisor).Select(p => p.ServiceId));

        var reordered = s with { TranslationOrder = ["openai/translate", "deepl/translate", .. s.TranslationOrder.Where(id => id is not ("openai/translate" or "deepl/translate"))] };
        Assert.Equal(["openai/translate", "deepl/translate", "mymemory/translate", "tencent-translate/translate"],
            PluginTranslationProviders.Build(reordered, (_, _) => true, supervisor).Select(p => p.ServiceId));

        var fewer = Enable(reordered, "mymemory/translate", false);
        Assert.Equal(["openai/translate", "deepl/translate", "tencent-translate/translate"], PluginTranslationProviders.Build(fewer, (_, _) => true, supervisor).Select(p => p.ServiceId));
        Assert.False(supervisor.TryGetCurrent(out _)); // building providers launches nothing (lazy start, PER02)
    }

    [Fact] // per-service limits; longer text goes through the shared F01 chunking path
    public void Each_package_declares_its_limits()
    {
        Assert.All(TranslationPackages.All, p => Assert.Null(p.Limits.Validate()));
        Assert.Equal((InputUnit.Utf8Bytes, 500), (TranslationPackages.Find("mymemory")!.Limits.Unit, TranslationPackages.Find("mymemory")!.Limits.MaxInput));
        Assert.Equal((InputUnit.UnicodeScalars, 5999), (TranslationPackages.Find("tencent-translate")!.Limits.Unit, TranslationPackages.Find("tencent-translate")!.Limits.MaxInput));
        Assert.Equal((InputUnit.Utf8Bytes, 120_000), (TranslationPackages.Find("deepl")!.Limits.Unit, TranslationPackages.Find("deepl")!.Limits.MaxInput));
        Assert.Equal(InputUnit.UnicodeScalars, TranslationPackages.Find("openai")!.Limits.Unit);
        var chunks = TextChunker.Split(new string('字', 7000), TranslationPackages.Find("tencent-translate")!.Limits);
        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 5999));
    }

    [Theory] // DeepL: the key's ":fx" suffix selects the free endpoint, anything else pro
    [InlineData("00000000-0000-0000-0000-000000000000:fx", "free", "https://api-free.deepl.com:443")]
    [InlineData("00000000-0000-0000-0000-000000000000", "pro", "https://api.deepl.com:443")]
    public void DeepL_plan_follows_the_key_suffix(string key, string plan, string origin)
    {
        var deepl = TranslationPackages.Find("deepl")!;
        var config = deepl.ConfigAfterSecret(new Dictionary<string, string>(), "apiKey", key);
        Assert.Equal(plan, config["plan"]);
        Assert.Equal(origin, deepl.Origin(config));
        var grant = Assert.Single(deepl.RequiredGrants(config));
        Assert.Equal(new CredentialGrant("app.susu.deepl", "unsigned:app.susu.deepl", "apiKey", origin, "header:Authorization"), grant);
    }

    [Fact] // PLAN 4.5.3: Tencent secrets go only to the named signer; OpenAI/DeepL to the Authorization header
    public void Required_grants_name_the_exact_use()
    {
        var tencent = TranslationPackages.Find("tencent-translate")!.RequiredGrants(new Dictionary<string, string>());
        Assert.Equal(["secretId", "secretKey"], tencent.Select(g => g.Secret));
        Assert.All(tencent, g => Assert.Equal(("signer:tencent-tc3", "https://tmt.tencentcloudapi.com:443"), (g.Use, g.Origin)));
        var openai = Assert.Single(TranslationPackages.Find("openai")!.RequiredGrants(new Dictionary<string, string> { ["baseUrl"] = "https://llm.example.com/api" }));
        Assert.Equal(("header:Authorization", "https://llm.example.com:443"), (openai.Use, openai.Origin));
    }

    [Fact] // the provider carries instance id, signer, secret names and config for S02 and the plugin
    public void Built_provider_passes_limits_and_display_name()
    {
        var s = Enable(WithGrantedKey(BuiltInCatalog.Defaults(), "tencent-translate"), "tencent-translate/translate");
        var provider = PluginTranslationProviders.Build(s, (_, _) => true, NeverLaunched()).Single(p => p.ServiceId == "tencent-translate/translate");
        Assert.Equal("Tencent Translator", provider.DisplayName);
        Assert.Equal(5999, provider.Limits.MaxInput);
        Assert.Equal("plugin:app.susu.tencent-translate", provider.LimiterKey);
        Assert.NotNull(PluginTranslationProviders.ForValidation(Enable(s, "tencent-translate/translate", false), "tencent-translate/translate", NeverLaunched()));
        Assert.Null(PluginTranslationProviders.ForValidation(s, "google-translate/translate", NeverLaunched())); // not wired yet
    }
}

/// <summary>F06.3a/F06.2: usage de-duplication and per-service network state in a translation session.</summary>
public class TranslationSessionServiceStateTests
{
    private static readonly ConfigSnapshot Config = new(1, 1, 1, 3, TimeSpan.FromSeconds(30));

    private static (TranslationSession Session, RecordingUsage Usage, List<CardPatch> Patches, ManualClock Clock) Create(params ITranslationProvider[] providers)
    {
        var usage = new RecordingUsage();
        var clock = new ManualClock();
        int ids = 0;
        var session = new TranslationSession(providers, new TranslationSessionOptions(Config, 3, () => $"a{Interlocked.Increment(ref ids)}"), new InvocationScheduler(new SchedulerLimits()), clock, new FixedJitter(0), usage);
        var patches = new List<CardPatch>();
        session.CardChanged += p => { lock (patches) patches.Add(p); };
        return (session, usage, patches, clock);
    }

    private static async Task Settle(TranslationSession session, ManualClock clock, Func<TranslationSnapshot, bool> done)
    {
        for (int i = 0; i < 400; i++)
        {
            if (done(await session.SnapshotAsync())) { await session.IdleAsync(); return; }
            clock.Advance(TimeSpan.FromMilliseconds(200));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException();
    }

    [Fact] // DATA04/F06.2: chunks of one attempt are one usage event carrying all accepted characters
    public async Task Chunked_text_is_counted_once_per_attempt_with_every_chunk()
    {
        var small = new TranslationLimits(InputUnit.Utf8Bytes, 20, BatchMode.Single, 1, 20);
        var provider = new ScriptedProvider("svc", small, new Step.Echo(""));
        var (session, usage, _, clock) = Create(provider);
        string text = "One sentence here. Another sentence there. And a third one.";
        await session.SubmitAsync(text, "en", "zh-Hans");
        await Settle(session, clock, s => s.Cards[0].State == CardState.Ready);
        Assert.True(provider.Calls.Count > 1);
        var record = Assert.Single(usage.Records);
        Assert.Equal(("svc", "chars", "ok"), (record.Service, record.Metric, record.Outcome));
        Assert.Equal(provider.Calls.Sum(c => c.Text.Length), record.Units);
    }

    [Fact] // a retry is a new attempt: one event per attempt, the failed one counts only accepted chunks
    public async Task Retry_after_a_failure_is_a_separate_attempt_without_double_counting()
    {
        var provider = new ScriptedProvider("svc", ScriptedProvider.Generous, new Step.Fail(new ProviderError(ErrorKind.Network)), new Step.Succeed("ok"));
        var (session, usage, _, clock) = Create(provider);
        await session.SubmitAsync("hello", "en", "zh-Hans");
        await Settle(session, clock, s => s.Cards[0].State == CardState.Ready);
        var records = usage.Records.ToArray();
        Assert.Equal(2, records.Length);
        Assert.Equal(2, records.Select(r => r.Attempt).Distinct().Count());
        Assert.Equal((0L, "Network"), (records[0].Units, records[0].Outcome));
        Assert.Equal((5L, "ok"), (records[1].Units, records[1].Outcome));
    }

    [Fact] // UI04: one service's network failure leaves the others and their text alone; no shared offline notice
    public async Task One_failing_service_does_not_affect_the_others()
    {
        var good = new ScriptedProvider("good", ScriptedProvider.Generous, new Step.Succeed("你好"));
        var bad = new ScriptedProvider("bad", ScriptedProvider.Generous, new Step.Fail(new ProviderError(ErrorKind.Network)));
        var (session, _, patches, clock) = Create(good, bad);
        await session.SubmitAsync("hello", "en", "zh-Hans");
        await Settle(session, clock, s => s.Cards.All(c => c.State is CardState.Ready or CardState.Failed));
        var snapshot = await session.SnapshotAsync();
        Assert.Equal((CardState.Ready, "你好", (ErrorKind?)null), (snapshot.Cards[0].State, snapshot.Cards[0].Text, snapshot.Cards[0].Error));
        Assert.Equal((CardState.Failed, (ErrorKind?)ErrorKind.Network), (snapshot.Cards[1].State, snapshot.Cards[1].Error));
        Assert.False(snapshot.Offline);
        lock (patches) Assert.DoesNotContain(patches, p => p.Offline);
    }

    [Fact] // the shared offline notice only when every requested card failed on the network
    public async Task Offline_only_when_every_requested_service_failed_on_the_network()
    {
        var a = new ScriptedProvider("a", ScriptedProvider.Generous, new Step.Fail(new ProviderError(ErrorKind.Network)));
        var b = new ScriptedProvider("b", ScriptedProvider.Generous, new Step.Fail(new ProviderError(ErrorKind.Timeout)));
        var (session, _, patches, clock) = Create(a, b);
        await session.SubmitAsync("hello", "en", "zh-Hans");
        await Settle(session, clock, s => s.Cards.All(c => c.State == CardState.Failed));
        Assert.True((await session.SnapshotAsync()).Offline);
        lock (patches) Assert.True(patches[^1].Offline);

        var auth = new ScriptedProvider("c", ScriptedProvider.Generous, new Step.Fail(new ProviderError(ErrorKind.Auth)));
        var (other, _, _, otherClock) = Create(a, auth);
        await other.SubmitAsync("hello", "en", "zh-Hans");
        await Settle(other, otherClock, s => s.Cards.All(c => c.State == CardState.Failed));
        Assert.False((await other.SnapshotAsync()).Offline); // an auth failure is not a network outage
    }
}

/// <summary>F06.3a settings commands: key entry with grants, DeepL plan, account binding, reorder, validate.</summary>
public class TranslationSettingsCommandTests
{
    private sealed class Rig : IDisposable
    {
        public readonly TempRoot Root = new();
        public readonly FakePlatform Platform = new();
        public readonly SettingsStore Settings;
        public readonly SecretStore Secrets;
        public readonly ConfigService Config;
        public readonly ShellCoordinator Shell;
        public readonly List<string> Diagnostics = [];
        public int SessionsBuilt;
        private long counter;
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        public Rig(Func<AppSettings, string, ITranslationProvider?>? validation = null, Func<AppSettings, IReadOnlyList<ITranslationProvider>>? providers = null, Func<string, long>? usage = null)
        {
            Settings = new SettingsStore(Root.Paths, new ManualClock());
            Secrets = new SecretStore(Root.Paths.Secrets, new XorProtector());
            Config = new ConfigService(Settings, Secrets);
            var features = new FeatureRegistry();
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
            Func<AppSettings, TranslationSession?> sessions = s =>
            {
                Interlocked.Increment(ref SessionsBuilt);
                var list = providers?.Invoke(s) ?? [];
                return list.Count == 0 ? null : new TranslationSession(list, new TranslationSessionOptions(s.Snapshot(), s.General.DefaultExpandedCards),
                    new InvocationScheduler(new SchedulerLimits()), new ManualClock(), new FixedJitter(), new RecordingUsage());
            };
            Shell = new ShellCoordinator(Platform, Config, features, c => c == Capability.Translate, new ShellOptions(false, false), sessions, null,
                new TranslationBackend(true, validation ?? ((_, _) => null), usage));
            Shell.Diagnostic += d => { lock (Diagnostics) Diagnostics.Add(d); };
            Shell.Start();
            Shell.Open(WindowKind.Settings);
            Shell.OnPageMessage(WindowKind.Settings, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Ready", windowSessionId = Platform.Session(WindowKind.Settings) }));
        }

        public CommandResult Run(WindowKind kind, string name, object? payload = null)
        {
            string id = $"c{++counter}";
            Shell.OnPageMessage(kind, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Command", windowSessionId = Platform.Session(kind), name, correlationId = id, payload }, Web));
            for (int i = 0; i < 1000; i++)
            {
                var hit = Platform.Posted.FirstOrDefault(p => p.Envelope.CorrelationId == id);
                if (hit.Envelope is not null) return hit.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.CommandResult)!;
                Thread.Sleep(5);
            }
            throw new TimeoutException(name);
        }

        public SettingsView View => Shell.ProjectSettings(Settings.State);
        public ServiceView Service(string id) => View.Services.Single(s => s.ServiceId == id);

        public void Enable(string serviceId)
        {
            var view = View;
            var result = Run(WindowKind.Settings, UiCommands.SettingsSave, new SettingsSaveRequest(view.Revision, view.FileHash, view.General, view.Hotkeys, view.Network, [new ServiceToggle(serviceId, true)]));
            Assert.True(result.Ok, result.Error);
        }

        public void Dispose() { Settings.Dispose(); Root.Dispose(); }
    }

    [Fact] // S07 + key entry: confirmed grants make the service ready; the key never reaches the page, files or diagnostics
    public void Confirmed_key_entry_grants_and_never_echoes_the_secret()
    {
        using var rig = new Rig();
        const string key = "sk-SECRET-openai-4711";
        Assert.False(rig.Service("openai/translate").Enabled);
        var target = Assert.Single(rig.Service("openai/translate").CredentialTargets!);
        Assert.Equal(new CredentialTargetView("apiKey", "https://api.openai.com:443", "header:Authorization", false, false), target);

        var result = rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("openai", "apiKey", key, ConfirmGrants: true));
        Assert.True(result.Ok, result.Error);
        Assert.Equal("Disabled", rig.Service("openai/translate").Availability);
        rig.Enable("openai/translate");
        var view = rig.Service("openai/translate");
        Assert.Equal("Ready", view.Availability);
        Assert.True(view.Implemented);
        Assert.Equal(new CredentialTargetView("apiKey", "https://api.openai.com:443", "header:Authorization", true, true), Assert.Single(view.CredentialTargets!));

        Assert.DoesNotContain("SECRET-openai", rig.Platform.AllJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-openai", File.ReadAllText(rig.Root.Paths.Settings), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-openai", File.ReadAllText(rig.Root.Paths.Secrets), StringComparison.Ordinal);
        lock (rig.Diagnostics) Assert.DoesNotContain(rig.Diagnostics, d => d.Contains("SECRET-openai", StringComparison.Ordinal));
        if (Directory.Exists(rig.Root.Paths.Logs))
            foreach (var file in Directory.EnumerateFiles(rig.Root.Paths.Logs, "*", SearchOption.AllDirectories))
                Assert.DoesNotContain("SECRET-openai", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact] // PLAN 4.5.4: without confirmation the key is saved and bound but grants nothing
    public void Unconfirmed_key_is_not_usable_until_bind_account_confirms()
    {
        using var rig = new Rig();
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("openai", "apiKey", "sk-x")).Ok);
        rig.Enable("openai/translate");
        Assert.Equal("MissingCredential", rig.Service("openai/translate").Availability);
        Assert.Equal((true, false), (rig.Service("openai/translate").CredentialTargets![0].Saved, rig.Service("openai/translate").CredentialTargets![0].Granted));

        var bound = rig.Run(WindowKind.Settings, UiCommands.BindAccount, new BindAccountRequest("openai"));
        Assert.True(bound.Ok, bound.Error);
        Assert.Equal("Ready", rig.Service("openai/translate").Availability);
    }

    [Theory] // DeepL: ":fx" sets plan=free (api-free), any other key plan=pro (api)
    [InlineData("dl-key:fx", "free", "https://api-free.deepl.com:443")]
    [InlineData("dl-key", "pro", "https://api.deepl.com:443")]
    public void DeepL_key_sets_the_plan_and_the_granted_origin(string key, string plan, string origin)
    {
        using var rig = new Rig();
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("deepl", "apiKey", key, true)).Ok);
        Assert.Equal(plan, rig.Settings.State.Effective.Instances.Single(i => i.Id == "deepl").Config["plan"]);
        var view = rig.Service("deepl/translate");
        Assert.Equal(plan, view.Plan);
        Assert.Equal((origin, true, true), (view.CredentialTargets![0].Origin, view.CredentialTargets[0].Saved, view.CredentialTargets[0].Granted));
    }

    [Fact] // a plan change moves the origin: the old grant is not widened until confirmed again
    public void Changing_the_DeepL_plan_needs_a_new_confirmation()
    {
        using var rig = new Rig();
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("deepl", "apiKey", "dl-key:fx", true)).Ok);
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("deepl", "apiKey", "dl-pro-key")).Ok);
        rig.Enable("deepl/translate");
        Assert.Equal("pro", rig.Service("deepl/translate").Plan);
        Assert.Equal("MissingCredential", rig.Service("deepl/translate").Availability);
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.BindAccount, new BindAccountRequest("deepl")).Ok);
        Assert.Equal("Ready", rig.Service("deepl/translate").Availability);
    }

    [Fact] // F06.3b: the free origin's grant goes with the free key; switching back needs a new confirmation
    public void Changing_the_DeepL_plan_revokes_the_old_origin_grant()
    {
        using var rig = new Rig();
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("deepl", "apiKey", "dl-key:fx", true)).Ok);
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("deepl", "apiKey", "dl-pro-key", true)).Ok);
        var grants = rig.Settings.State.Effective.Accounts.Single(a => a.Id == "deepl").Grants;
        Assert.Equal("https://api.deepl.com:443", Assert.Single(grants).Origin);

        // Same plan again: nothing is revoked.
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("deepl", "apiKey", "dl-pro-key-2")).Ok);
        Assert.Single(rig.Settings.State.Effective.Accounts.Single(a => a.Id == "deepl").Grants);

        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("deepl", "apiKey", "dl-key-2:fx")).Ok);
        Assert.Empty(rig.Settings.State.Effective.Accounts.Single(a => a.Id == "deepl").Grants);
        rig.Enable("deepl/translate");
        Assert.Equal("MissingCredential", rig.Service("deepl/translate").Availability);
    }

    [Fact] // F06.3b: deleting a secret withdraws its grants; a new key is confirmed afresh
    public void Deleting_a_secret_revokes_its_grants()
    {
        using var rig = new Rig();
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("tencent-translate", "secretId", "AKID-x", true)).Ok);
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("tencent-translate", "secretKey", "SK-x", true)).Ok);
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretDelete, new SecretDeleteRequest("tencent-translate", "secretKey")).Ok);
        var grant = Assert.Single(rig.Settings.State.Effective.Accounts.Single(a => a.Id == "tencent-translate").Grants);
        Assert.Equal("secretId", grant.Secret);

        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("tencent-translate", "secretKey", "SK-y")).Ok);
        rig.Enable("tencent-translate/translate");
        var targets = rig.Service("tencent-translate/translate").CredentialTargets!;
        Assert.Equal((true, false), (targets.Single(t => t.Secret == "secretKey").Saved, targets.Single(t => t.Secret == "secretKey").Granted));
        Assert.Equal("MissingCredential", rig.Service("tencent-translate/translate").Availability);
    }

    [Fact] // Tencent: both secrets granted to signer:tencent-tc3, one account, ready once both are in
    public void Tencent_needs_both_secrets_granted_to_the_signer()
    {
        using var rig = new Rig();
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("tencent-translate", "secretId", "AKID-x", true)).Ok);
        rig.Enable("tencent-translate/translate");
        Assert.Equal("MissingCredential", rig.Service("tencent-translate/translate").Availability);
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("tencent-translate", "secretKey", "SK-x", true)).Ok);
        Assert.Equal("Ready", rig.Service("tencent-translate/translate").Availability);
        var account = rig.Settings.State.Effective.Accounts.Single(a => a.Id == "tencent-translate");
        Assert.Equal(2, account.Grants.Count);
        Assert.All(account.Grants, g => Assert.Equal(("app.susu.tencent-translate", "signer:tencent-tc3", "https://tmt.tencentcloudapi.com:443"), (g.Package, g.Use, g.Origin)));
    }

    [Fact] // BindAccount: an instance can be bound to an existing account holding the same secret names
    public void Bind_account_rebinds_to_an_existing_account()
    {
        using var rig = new Rig();
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("openai", "apiKey", "sk-x", true)).Ok);
        Assert.Equal("account-secrets", rig.Run(WindowKind.Settings, UiCommands.BindAccount, new BindAccountRequest("tencent-translate", "openai")).Error);
        Assert.Equal("unknown-account", rig.Run(WindowKind.Settings, UiCommands.BindAccount, new BindAccountRequest("deepl", "nope")).Error);
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.BindAccount, new BindAccountRequest("deepl", "openai")).Ok);
        Assert.Equal("openai", rig.Settings.State.Effective.Instances.Single(i => i.Id == "deepl").AccountBindings["apiKey"]);
        Assert.Contains(rig.Settings.State.Effective.Accounts.Single(a => a.Id == "openai").Grants, g => g.Package == "app.susu.deepl");
    }

    [Fact] // CFG03: reordering is page-local and changes the order of the next session
    public void Reorder_moves_a_service_within_its_page()
    {
        using var rig = new Rig();
        var before = rig.Settings.State.Effective.TranslationOrder;
        var result = rig.Run(WindowKind.Settings, UiCommands.ReorderService, new ReorderServiceRequest("deepl/translate", 0));
        Assert.True(result.Ok, result.Error);
        var after = rig.Settings.State.Effective.TranslationOrder;
        Assert.Equal("deepl/translate", after[0]);
        Assert.Equal(before.ToList().IndexOf("openai/translate"), after.ToList().IndexOf("openai/translate")); // the AI page's slots are untouched
        Assert.Equal(0, rig.Service("deepl/translate").Order);
        Assert.Equal("unknown-service", rig.Run(WindowKind.Settings, UiCommands.ReorderService, new ReorderServiceRequest("native-els/detect", 0)).Error);
    }

    [Fact] // CFG03: the General page moves across categories; a later page-local move keeps the other page's slots
    public void Merged_reorder_crosses_pages_and_page_moves_keep_the_other_category_in_place()
    {
        using var rig = new Rig();
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.ReorderService, new ReorderServiceRequest("openai/translate", 0, Merged: true)).Ok);
        var merged = rig.Settings.State.Effective.TranslationOrder;
        Assert.Equal("openai/translate", merged[0]);
        Assert.Equal(0, rig.Service("openai/translate").Order);

        // Engines page: DeepL to the top of its own page takes the first engines slot (1), not the AI slot 0.
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.ReorderService, new ReorderServiceRequest("deepl/translate", 0)).Ok);
        var after = rig.Settings.State.Effective.TranslationOrder;
        Assert.Equal(("openai/translate", "deepl/translate"), (after[0], after[1]));
        var aiSlots = merged.Select((id, i) => (id, i)).Where(x => BuiltInCatalog.Find(x.id.Split('/')[0])!.Page == "ai").ToList();
        Assert.All(aiSlots, x => Assert.Equal(x.id, after[x.i]));

        // AI page: moving Claude to the top of the AI page only permutes AI slots.
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.ReorderService, new ReorderServiceRequest("claude/translate", 0)).Ok);
        var ai = rig.Settings.State.Effective.TranslationOrder;
        Assert.Equal(("claude/translate", "deepl/translate"), (ai[0], ai[1]));
        Assert.Equal(after.Where(id => BuiltInCatalog.Find(id.Split('/')[0])!.Page == "engines"), ai.Where(id => BuiltInCatalog.Find(id.Split('/')[0])!.Page == "engines"));
        // Views report the merged position the cards follow.
        Assert.Equal(ai.Select((id, i) => (id, i)), rig.View.Services.Where(s => s.Order >= 0).OrderBy(s => s.Order).Select(s => (s.ServiceId, s.Order)));
    }

    [Fact] // CFG03: cards follow the merged order, so the default expanded count counts across both categories
    public void Resolved_cards_follow_the_merged_order()
    {
        using var rig = new Rig();
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("openai", "apiKey", "sk-x", true)).Ok);
        rig.Enable("openai/translate");
        Assert.Equal(["mymemory/translate", "openai/translate"], TranslationPackages.Resolve(rig.Settings.State.Effective, rig.Secrets.Has).Select(p => p.Service.ServiceId));
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.ReorderService, new ReorderServiceRequest("openai/translate", 0, Merged: true)).Ok);
        // TranslationSession expands the first DefaultExpanded providers of this list.
        Assert.Equal(["openai/translate", "mymemory/translate"], TranslationPackages.Resolve(rig.Settings.State.Effective, rig.Secrets.Has).Select(p => p.Service.ServiceId));
    }

    [Fact] // CFG01: a shared Tencent account is bound, but not usable until the binding's grants are confirmed
    public void Shared_account_needs_a_binding_grant_and_identity_stays_stable()
    {
        using var rig = new Rig();
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("tencent-ocr", "secretId", "AKID-x")).Ok);
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("tencent-ocr", "secretKey", "SK-x")).Ok);
        rig.Enable("tencent-translate/translate");
        var before = rig.Service("tencent-translate/translate");

        Assert.True(rig.Run(WindowKind.Settings, UiCommands.BindAccount, new BindAccountRequest("tencent-translate", "tencent-ocr", ConfirmGrants: false)).Ok);
        var bound = rig.Service("tencent-translate/translate");
        Assert.Equal("tencent-ocr", bound.AccountId);
        Assert.Equal("MissingCredential", bound.Availability); // complete config is not usable without the grant
        Assert.All(bound.CredentialTargets!, t => Assert.Equal((true, false), (t.Saved, t.Granted)));
        Assert.Contains("tencent-translate", rig.View.Accounts.Single(a => a.Id == "tencent-ocr").UsedBy);

        Assert.True(rig.Run(WindowKind.Settings, UiCommands.BindAccount, new BindAccountRequest("tencent-translate")).Ok);
        var ready = rig.Service("tencent-translate/translate");
        Assert.Equal("Ready", ready.Availability);
        Assert.Equal((before.ServiceId, before.InstanceId, before.Capability, before.Page, before.Order), (ready.ServiceId, ready.InstanceId, ready.Capability, ready.Page, ready.Order));
        Assert.All(rig.Settings.State.Effective.Accounts.Single(a => a.Id == "tencent-ocr").Grants, g => Assert.Equal("app.susu.tencent-translate", g.Package));

        // Disabling and re-enabling keeps the same service identity, binding and grants.
        var view = rig.View;
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SettingsSave, new SettingsSaveRequest(view.Revision, view.FileHash, view.General, view.Hotkeys, view.Network, [new ServiceToggle("tencent-translate/translate", false)])).Ok);
        Assert.Equal("Disabled", rig.Service("tencent-translate/translate").Availability);
        rig.Enable("tencent-translate/translate");
        Assert.Equal(("Ready", "tencent-ocr", before.Order), (rig.Service("tencent-translate/translate").Availability, rig.Service("tencent-translate/translate").AccountId, rig.Service("tencent-translate/translate").Order));
    }

    [Fact] // usage is the local monthly count, shown only for services the host tracks
    public void Service_views_carry_local_monthly_usage()
    {
        using var rig = new Rig(usage: id => id == "mymemory/translate" ? 1234 : 0);
        Assert.Equal(1234, rig.Service("mymemory/translate").UsageThisMonth);
        Assert.Equal(0, rig.Service("deepl/translate").UsageThisMonth);
        Assert.Null(rig.Service("tencent-ocr/ocr").UsageThisMonth);
        using var plain = new Rig();
        Assert.Null(plain.Service("mymemory/translate").UsageThisMonth);
    }

    [Fact] // validate runs through the configured provider and keeps credential validity apart from availability
    public void Validate_reports_credential_validity_without_detail()
    {
        var provider = new ScriptedProvider("openai/translate", ScriptedProvider.Generous, new Step.Fail(new ProviderError(ErrorKind.Quota, "vendor said: sk-SECRET")));
        using var rig = new Rig(validation: (_, id) => id == "openai/translate" ? provider : null);
        Assert.Equal("missing-credential", rig.Run(WindowKind.Settings, UiCommands.ValidateProvider, new ValidateProviderRequest("openai/translate")).Error);
        Assert.True(rig.Run(WindowKind.Settings, UiCommands.SecretWriteNew, new SecretWriteRequest("openai", "apiKey", "sk-x", true)).Ok);
        var result = rig.Run(WindowKind.Settings, UiCommands.ValidateProvider, new ValidateProviderRequest("openai/translate")); // still disabled: validate first
        Assert.True(result.Ok, result.Error);
        var view = result.Value!.Value.Deserialize(ContractsJson.Default.ServiceValidationView)!;
        Assert.Equal(new ServiceValidationView("openai/translate", "valid", false, ErrorKind.Quota), view);
        Assert.DoesNotContain("vendor said", rig.Platform.AllJson(), StringComparison.Ordinal);
        Assert.Equal("unavailable", rig.Run(WindowKind.Settings, UiCommands.ValidateProvider, new ValidateProviderRequest("mymemory/translate")).Error);    }

    [Fact] // settings changes rebuild the session on the next submit, without a restart
    public void A_settings_change_rebuilds_the_session_on_the_next_submit()
    {
        var mymemory = new ScriptedProvider("mymemory/translate", ScriptedProvider.Generous, new Step.Echo("M:"));
        var deepl = new ScriptedProvider("deepl/translate", ScriptedProvider.Generous, new Step.Echo("D:"));
        using var rig = new Rig(providers: s => [.. new ITranslationProvider[] { mymemory, deepl }.Where(p => s.Services.Single(x => x.ServiceId == p.ServiceId).Enabled)]);
        rig.Shell.Open(WindowKind.Main);
        rig.Shell.OnPageMessage(WindowKind.Main, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Ready", windowSessionId = rig.Platform.Session(WindowKind.Main) }));
        Assert.True(rig.Run(WindowKind.Main, UiCommands.SubmitText, new SubmitTextRequest("hello")).Ok);
        int built = rig.SessionsBuilt;
        Assert.True(rig.Run(WindowKind.Main, UiCommands.SubmitText, new SubmitTextRequest("again")).Ok);
        Assert.Equal(built, rig.SessionsBuilt); // unchanged settings keep the session

        rig.Enable("deepl/translate");
        Assert.True(rig.Run(WindowKind.Main, UiCommands.SubmitText, new SubmitTextRequest("hello")).Ok);
        Assert.Equal(built + 1, rig.SessionsBuilt);
        var snapshot = rig.Platform.Posted.Last(p => p.Envelope.Name == "translation").Envelope.Payload!.Value.Deserialize(ContractsJson.Default.TranslationSnapshot)!;
        Assert.Equal(["mymemory/translate", "deepl/translate"], snapshot.Cards.Select(c => c.ServiceId));
    }
}
