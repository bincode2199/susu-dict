using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Plugins;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F06.3a through the real AppContainer sandbox: one plugin host loads all four wired packages, and
/// providers built from settings (grants from <see cref="TranslationPackages.RequiredGrants"/>, bindings,
/// secrets) reach a local stand-in vendor. Needs susu.exe published
/// (`dotnet publish src/Susu.Host -c Release -r win-x64`); skips itself otherwise.
/// </summary>
public class TranslationRuntimeIntegrationTests
{
    private static string? StageHost()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? output = null;
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish");
            if (File.Exists(Path.Combine(candidate, "susu.exe"))) { output = candidate; break; }
        }
        if (output is null || TranslationPackages.All.Any(p => !File.Exists(Path.Combine(output, p.Directory, "main.js")))) return null;
        string staged = TestTemp.NewDir("susu-runtime-it");
        foreach (string file in Directory.EnumerateFiles(output))
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(staged, Path.GetFileName(file)));
        foreach (var package in TranslationPackages.All)
        {
            string target = Path.Combine(staged, package.Directory);
            Directory.CreateDirectory(target);
            foreach (string file in Directory.EnumerateFiles(Path.Combine(output, package.Directory)))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        return staged;
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<(string, string), string> values = [];
        public bool Has(string a, string n) => values.ContainsKey((a, n));
        public IReadOnlyList<string> Names(string a) => [.. values.Keys.Where(k => k.Item1 == a).Select(k => k.Item2)];
        public void Write(string a, string n, ReadOnlySpan<char> v) => values[(a, n)] = v.ToString();
        public bool Delete(string a, string n) => values.Remove((a, n));
        public bool TryRead(string a, string n, out string v) => values.TryGetValue((a, n), out v!);
    }

    [Fact] // all four packages load into one host; settings-built MyMemory and DeepL providers call the vendor with the bound key
    public async Task Settings_built_providers_run_in_the_real_sandbox()
    {
        string? staged = StageHost();
        if (staged is null) return;
        const string key = "0000-runtime-test:fx";
        using var server = new LoopbackHttpServer(req => req.Path.StartsWith("/v2/translate", StringComparison.Ordinal)
            ? LoopbackHttpResponse.Json(200, req.Headers.Any(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && h.Value == $"DeepL-Auth-Key {key}")
                ? "{\"translations\":[{\"detected_source_language\":\"EN\",\"text\":\"DeepL 你好\"}]}" : "{}")
            : LoopbackHttpResponse.Json(200, "{\"responseData\":{\"translatedText\":\"MyMemory 你好\"},\"responseStatus\":200}"));

        // Settings as the settings commands leave them: DeepL enabled, key bound to its own account and granted.
        var s = BuiltInCatalog.Defaults();
        var deepl = TranslationPackages.Find("deepl")!;
        var local = new Dictionary<string, string> { ["baseUrl"] = server.Origin };
        var deeplConfig = new Dictionary<string, string>(deepl.ConfigAfterSecret(local, "apiKey", key));
        var account = CredentialAuthorizer.Confirm(new AccountSettings("deepl", "deepl", ["apiKey"], []), deepl.RequiredGrants(deeplConfig));
        s = s with
        {
            Accounts = [account],
            Instances = [.. s.Instances.Select(i => i.Id switch
            {
                "deepl" => i with { Config = deeplConfig, AccountBindings = new Dictionary<string, string> { ["apiKey"] = "deepl" } },
                "mymemory" => i with { Config = local },
                _ => i,
            })],
            Services = [.. s.Services.Select(x => x.ServiceId == "deepl/translate" ? x with { Enabled = true } : x)],
        };
        var secrets = new FakeSecretStore();
        secrets.Write("deepl", "apiKey", key);
        var accounts = new AccountAuthorization(() => (s.Accounts, s.Instances));
        var options = new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false,
            MakeBroker: () => new Broker(secretStore: secrets, accounts: accounts));
        var failed = new List<string>();
        using var supervisor = new Supervisor<HostSession>(() =>
        {
            var session = HostSession.Start(options);
            session.Broker.ApproveLocalOrigin(server.Origin);
            return PluginTranslationProviders.LoadAll(session, (package, _) => failed.Add(package));
        }, new ManualClock(), TimeSpan.FromMinutes(10));

        var providers = PluginTranslationProviders.Build(s, secrets.Has, supervisor);
        Assert.Equal(["mymemory/translate", "deepl/translate"], providers.Select(p => p.ServiceId));
        var results = new List<string>();
        foreach (var provider in providers)
        {
            var call = new TranslateCall("hello", "en", "zh-Hans", Guid.NewGuid().ToString("N"), new ConfigSnapshot(0, 0, 0, 0, TimeSpan.FromSeconds(20)), TimeSpan.FromSeconds(20));
            var outcome = await provider.TranslateAsync(call, _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
            var success = Assert.IsType<ProviderOutcome.Success>(outcome);
            results.Add(success.Text);
        }
        Assert.Empty(failed); // every wired package loaded
        Assert.Equal(["MyMemory 你好", "DeepL 你好"], results);
    }
}
