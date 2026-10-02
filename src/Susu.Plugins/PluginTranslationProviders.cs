using Susu.Abstractions;
using Susu.Domain;

namespace Susu.Plugins;

/// <summary>
/// Builds <see cref="PluginProvider"/>s from the current settings (F06.3a): one per enabled, ready built-in
/// translation service, in the user's order, all sharing the one plugin-host <see cref="Supervisor{HostSession}"/>.
/// Construction is cheap and launches nothing, so the host calls this for every new translation session;
/// enabling, disabling, reordering or binding a key needs no restart.
/// </summary>
/// <summary>A package the plugin host loads, with the credential/origin rules of its catalog entry.</summary>
public sealed record WiredPackage(string InstanceId, string PackageId, string Directory, ICredentialPackage Credentials)
{
    public string Signer => Credentials.Signer;
    public IReadOnlyList<string> SecretNames => Credentials.SecretNames;
    public string Origin(IReadOnlyDictionary<string, string> config) => Credentials.Origin(config);
}

public static class PluginTranslationProviders
{
    /// <summary>Every package the plugin host loads: the wired translation packages and the installed speech packages (F10.1 P-S01–P-S03).</summary>
    public static IReadOnlyList<WiredPackage> WiredPackages { get; } =
    [
        .. TranslationPackages.All.Select(p => new WiredPackage(p.InstanceId, p.PackageId, p.Directory, p)),
        .. SpeechCatalog.InstalledPlugins.Select(p => new WiredPackage(p.InstanceId, p.PackageId, p.Directory, p)),
        .. OcrCatalog.All.Select(p => new WiredPackage(p.InstanceId, p.PackageId, p.Directory, p)),
        .. VocabCatalog.All.Select(p => new WiredPackage(p.InstanceId, p.PackageId, p.Directory, p)),
    ];

    public static IReadOnlyList<ITranslationProvider> Build(AppSettings settings, Func<string, string, bool> hasSecret, Supervisor<HostSession> supervisor,
        IReadOnlyDictionary<string, IReadOnlyList<ConfigField>>? schemas = null)
        => [.. TranslationPackages.Resolve(settings, hasSecret).Select(plan => Create(plan, supervisor, settings, schemas))];

    /// <summary>A provider for one instance regardless of its enabled flag (the settings "validate" action runs before enabling).</summary>
    public static ITranslationProvider? ForValidation(AppSettings settings, string serviceId, Supervisor<HostSession> supervisor,
        IReadOnlyDictionary<string, IReadOnlyList<ConfigField>>? schemas = null)
    {
        var service = settings.Services.FirstOrDefault(s => s.ServiceId == serviceId);
        var package = service is null ? null : TranslationPackages.Find(service.Instance);
        var instance = service is null ? null : settings.Instances.FirstOrDefault(i => i.Id == service.Instance);
        if (service is null || package is null || instance is null || instance.Package != package.PackageId) return null;
        return Create(new TranslationServicePlan(package, instance, service), supervisor, settings, schemas);
    }

    /// <summary>
    /// F07.3: the provider carries the SetPrompt snapshot of <paramref name="settings"/> (AI services in scope only)
    /// and a config where model parameters such as temperature survive only if the package's schema declares them.
    /// </summary>
    public static PluginProvider Create(TranslationServicePlan plan, Supervisor<HostSession> supervisor, AppSettings? settings = null,
        IReadOnlyDictionary<string, IReadOnlyList<ConfigField>>? schemas = null)
    {
        var package = plan.Package;
        IReadOnlyList<ConfigField>? schema = schemas is not null && schemas.TryGetValue(plan.Instance.Id, out var fields) ? fields : null;
        return new PluginProvider(package.PackageId, plan.Service.ServiceId, package.DisplayName, package.Limits, supervisor, [package.Origin(plan.Instance.Config)],
            plan.Instance.Id, package.Signer, package.SecretNames, ConfigSchema.ForPlugin(plan.Instance.Config, schema),
            settings is null ? null : PromptCatalog.SnapshotFor(settings, plan.Instance.Id), plan.Dictionary);
    }

    /// <summary>
    /// F07.2: the config schema of each wired package, read from its shipped manifest.yaml under
    /// <paramref name="root"/> (the install folder). A missing or invalid manifest leaves that package without
    /// generated controls (reported through <paramref name="invalid"/>) rather than failing the settings page.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ConfigField>> LoadSchemas(string root, Action<string>? invalid = null)
    {
        var result = new Dictionary<string, IReadOnlyList<ConfigField>>(StringComparer.Ordinal);
        foreach (var package in WiredPackages)
        {
            string path = Path.Combine(root, package.Directory, "manifest.yaml");
            PackageManifest? manifest;
            try { manifest = File.Exists(path) ? PackageManifest.Parse(File.ReadAllText(path)).Manifest : null; }
            catch (IOException) { manifest = null; }
            if (manifest is null || manifest.Id != package.PackageId) { invalid?.Invoke(package.PackageId); continue; }
            if (manifest.ConfigFields.Count > 0) result[package.InstanceId] = manifest.ConfigFields;
        }
        return result;
    }

    /// <summary>
    /// F07.2: one page of a dynamic settings field through the package's optional <c>options</c> method (or
    /// <c>voices</c>, mapped to value/label pairs), with the instance's current config, secrets and origin, the
    /// same authority its translate call has (ARCHITECTURE 3.1: only that provider's network grant). The error
    /// detail is dropped by the caller; only the kind reaches a page.
    /// </summary>
    public static async Task<CapabilityOutcome<Susu.Contracts.OptionsResult>> LoadOptionsAsync(Supervisor<HostSession> supervisor, AppSettings settings,
        string instanceId, string method, string field, long revision, string? cursor, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var package = WiredPackages.FirstOrDefault(p => p.InstanceId == instanceId);
        var instance = settings.Instances.FirstOrDefault(i => i.Id == instanceId);
        if (package is null || instance is null || instance.Package != package.PackageId) return new(false, null, Susu.Contracts.ErrorKind.Unavailable, "no such package");
        if (supervisor.Stopped) return new(false, null, Susu.Contracts.ErrorKind.Unavailable, "plugin host stopped");
        HostSession? host;
        try { host = supervisor.Acquire(); }
        catch (Exception error) { return new(false, null, Susu.Contracts.ErrorKind.Unavailable, error.Message); }
        if (host is null) return new(false, null, Susu.Contracts.ErrorKind.Unavailable, "plugin host is restarting");
        try
        {
            string configJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>(instance.Config), Susu.Contracts.ContractsJson.Default.DictionaryStringString);
            string jobId = $"options-{Guid.NewGuid():N}";
            string[] origins = [package.Origin(instance.Config)];
            // F15.4: the AnkiConnect address the user typed on SetVocab is approved here as it is for a sync call, but only when it is a loopback address.
            if (package.Credentials is VocabPackage { Local: true } && VocabPackage.IsLoopback(origins[0])) host.Broker.ApproveLocalOrigin(origins[0]);
            if (method == OptionsSource.VoicesMethod)
            {
                var voices = await CapabilityClient.InvokeAsync(host, package.PackageId, "voices", "{}", jobId, origins, Susu.Contracts.ContractsJson.Default.VoiceArray,
                    package.SecretNames, configJson: configJson, timeout: timeout, cancellationToken: cancellationToken, instanceId: instance.Id, signer: package.Signer);
                if (!voices.Ok) return new(false, null, voices.ErrorKind, voices.ErrorDetail);
                var list = voices.Result ?? [];
                return new(true, new Susu.Contracts.OptionsResult([.. list.Select(v => new Susu.Contracts.OptionItem(v.Id, string.IsNullOrEmpty(v.Lang) ? v.Name : $"{v.Name} ({v.Lang})"))]), null, null);
            }
            string requestJson = System.Text.Json.JsonSerializer.Serialize(new Susu.Contracts.OptionsRequest(field, revision, cursor), Susu.Contracts.ContractsJson.Default.OptionsRequest);
            return await CapabilityClient.InvokeAsync(host, package.PackageId, OptionsSource.OptionsMethod, requestJson, jobId, origins, Susu.Contracts.ContractsJson.Default.OptionsResult,
                package.SecretNames, configJson: configJson, timeout: timeout, cancellationToken: cancellationToken, instanceId: instance.Id, signer: package.Signer);
        }
        finally { supervisor.Release(); }
    }

    /// <summary>
    /// Launch function body: loads every wired package into a fresh session. One package failing to load
    /// is reported and skipped (its calls then fail on their own card) instead of taking the others down;
    /// only a session where nothing loaded is a launch failure.
    /// F16.2: <paramref name="activeDirectory"/> names the directory of a user-installed version of a wired package (a host-signed override), which is
    /// loaded in place of the shipped one; <paramref name="installed"/> lists every user-installed package, and those the product does not wire
    /// are loaded too so they are present in the host the moment they are installed.
    /// </summary>
    public static HostSession LoadAll(HostSession session, Action<string, string?>? loadFailed = null, Func<string, string?>? activeDirectory = null,
        Func<IReadOnlyList<InstalledPackage>>? installed = null)
    {
        int loaded = 0;
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in WiredPackages)
        {
            if (!done.Add(package.PackageId)) continue;
            string directory = activeDirectory?.Invoke(package.PackageId) ?? package.Directory;
            var result = session.Load(package.PackageId, directory);
            if (result.Ok) loaded++;
            else loadFailed?.Invoke(package.PackageId, result.Error);
        }
        foreach (var package in installed?.Invoke() ?? [])
        {
            if (!done.Add(package.Id)) continue;
            var result = session.Load(package.Id, package.Directory, entry: package.Entry);
            if (result.Ok) loaded++;
            else loadFailed?.Invoke(package.Id, result.Error);
        }
        if (loaded == 0)
        {
            session.Shutdown(2000);
            throw new InvalidOperationException("no translation package could be loaded");
        }
        return session;
    }
}

/// <summary>A user-installed package the plugin host loads: its id, absolute directory and manifest entry file.</summary>
public sealed record InstalledPackage(string Id, string Directory, string Entry);
