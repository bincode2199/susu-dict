using Susu.Abstractions;
using Susu.Domain;

namespace Susu.Plugins;

/// <summary>
/// Builds <see cref="PluginProvider"/>s from the current settings (F06.3a): one per enabled, ready built-in
/// translation service, in the user's order, all sharing the one plugin-host <see cref="Supervisor{HostSession}"/>.
/// Construction is cheap and launches nothing, so the host calls this for every new translation session;
/// enabling, disabling, reordering or binding a key needs no restart.
/// </summary>
public static class PluginTranslationProviders
{
    public static IReadOnlyList<ITranslationProvider> Build(AppSettings settings, Func<string, string, bool> hasSecret, Supervisor<HostSession> supervisor)
        => [.. TranslationPackages.Resolve(settings, hasSecret).Select(plan => Create(plan, supervisor))];

    /// <summary>A provider for one instance regardless of its enabled flag (the settings "validate" action runs before enabling).</summary>
    public static ITranslationProvider? ForValidation(AppSettings settings, string serviceId, Supervisor<HostSession> supervisor)
    {
        var service = settings.Services.FirstOrDefault(s => s.ServiceId == serviceId);
        var package = service is null ? null : TranslationPackages.Find(service.Instance);
        var instance = service is null ? null : settings.Instances.FirstOrDefault(i => i.Id == service.Instance);
        if (service is null || package is null || instance is null || instance.Package != package.PackageId) return null;
        return Create(new TranslationServicePlan(package, instance, service), supervisor);
    }

    public static PluginProvider Create(TranslationServicePlan plan, Supervisor<HostSession> supervisor)
    {
        var package = plan.Package;
        return new PluginProvider(package.PackageId, plan.Service.ServiceId, package.DisplayName, package.Limits, supervisor, [package.Origin(plan.Instance.Config)],
            plan.Instance.Id, package.Signer, package.SecretNames, plan.Instance.Config);
    }

    /// <summary>
    /// Launch function body: loads every wired package into a fresh session. One package failing to load
    /// is reported and skipped (its calls then fail on their own card) instead of taking the others down;
    /// only a session where nothing loaded is a launch failure.
    /// </summary>
    public static HostSession LoadAll(HostSession session, Action<string, string?>? loadFailed = null)
    {
        int loaded = 0;
        foreach (var package in TranslationPackages.All)
        {
            var result = session.Load(package.PackageId, package.Directory);
            if (result.Ok) loaded++;
            else loadFailed?.Invoke(package.PackageId, result.Error);
        }
        if (loaded == 0)
        {
            session.Shutdown(2000);
            throw new InvalidOperationException("no translation package could be loaded");
        }
        return session;
    }
}
