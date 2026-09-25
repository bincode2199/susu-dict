using Susu.Contracts;

namespace Susu.Domain;

/// <summary>One remote path the SetNetwork test checks: an exact origin and the enabled services that call it.</summary>
public sealed record NetworkProbeTarget(string Origin, IReadOnlyList<string> Services);

/// <summary>
/// The outcome of one path (CFG05): errors are reported per origin, never as one global verdict. Route: proxy,
/// direct or local (loopback, never proxied). Ok: the origin answered at all (any HTTP status).
/// </summary>
public sealed record NetworkProbeResult(string Origin, IReadOnlyList<string> Services, string Route, bool Ok, ErrorKind? Error, int? Status, long ElapsedMs);

public static class NetworkPaths
{
    public const int MaxTargets = 16;

    /// <summary>
    /// The origins of the enabled wired translation services under their current config, one entry per origin.
    /// With none enabled, the keyless default (MyMemory) is tested so the button still says something useful.
    /// </summary>
    public static IReadOnlyList<NetworkProbeTarget> For(AppSettings settings)
    {
        var byOrigin = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var service in settings.Services.Where(s => s.Enabled && s.Capability == Capability.Translate))
        {
            var package = TranslationPackages.Find(service.Instance);
            var instance = settings.Instances.FirstOrDefault(i => i.Id == service.Instance);
            if (package is null || instance is null || instance.Package != package.PackageId) continue;
            string origin = package.Origin(instance.Config);
            if (!byOrigin.TryGetValue(origin, out var list)) byOrigin[origin] = list = [];
            list.Add(service.ServiceId);
        }
        if (byOrigin.Count == 0 && TranslationPackages.Find(TranslationPackages.MyMemory) is { } fallback)
            byOrigin[fallback.Origin(new Dictionary<string, string>())] = [$"{TranslationPackages.MyMemory}/translate"];
        return [.. byOrigin.Take(MaxTargets).Select(kv => new NetworkProbeTarget(kv.Key, kv.Value))];
    }
}
