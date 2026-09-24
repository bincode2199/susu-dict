using System.Text.RegularExpressions;
using Susu.Contracts;

namespace Susu.Domain;

/// <summary>Reverse-domain package id (PLAN 4.6), e.g. <c>com.example.deepl</c>.</summary>
public readonly partial record struct PackageId
{
    public string Value { get; }
    public PackageId(string value)
    {
        if (!IsValid(value)) throw new ArgumentException($"'{value}' is not a reverse-domain package id.", nameof(value));
        Value = value;
    }
    public static bool IsValid(string? value) => value is { Length: <= 128 } && Pattern().IsMatch(value);
    [GeneratedRegex("^[a-z][a-z0-9-]*(\\.[a-z0-9][a-z0-9-]*){2,}$")]
    private static partial Regex Pattern();
    public override string ToString() => Value;
}

public enum ProviderKind { Plugin, Native }

/// <summary>Availability is separate from validation state (ARCHITECTURE 3).</summary>
public enum Availability { Disabled, MissingCredential, UnsupportedCapability, Ready, TemporarilyUnavailable }

/// <summary>Result of the last explicit validation; having a key never implies success.</summary>
public sealed record ValidationState(bool Validated, DateTimeOffset? SucceededAt, ErrorKind? LastFailure);

/// <summary>A configured provider: a plugin package instance or a native provider (ELS, SAPI).</summary>
public sealed record ProviderInstance(
    string InstanceId,
    ProviderKind Kind,
    string PackageOrNativeId,
    IReadOnlySet<Capability> DeclaredCapabilities,
    IReadOnlyList<string> RequiredSecrets,
    IReadOnlyDictionary<string, string> AccountBindings,
    long Revision);

/// <summary>One capability of one instance in the user's ordered service list.</summary>
public sealed record ServiceBinding(string InstanceId, Capability Capability, bool Enabled, string OrderKey)
{
    public string ServiceId => $"{InstanceId}/{Capability.ToString().ToLowerInvariant()}";
}

public sealed record ResolvedService(ServiceBinding Binding, ProviderInstance Instance, Availability Availability);

/// <summary>
/// Resolves which services serve a capability and in what order. Feature entry points ask by
/// capability, never by settings page (ARCHITECTURE 3).
/// </summary>
public static class CapabilityResolver
{
    public static IReadOnlyList<ResolvedService> Resolve(Capability capability, IEnumerable<ServiceBinding> bindings, IReadOnlyDictionary<string, ProviderInstance> instances,
        Func<string, string, bool> hasSecret, Func<string, bool>? temporarilyUnavailable = null)
    {
        var result = new List<ResolvedService>();
        foreach (var binding in bindings.Where(b => b.Capability == capability).OrderBy(b => b.OrderKey, StringComparer.Ordinal))
        {
            if (!instances.TryGetValue(binding.InstanceId, out var instance)) continue;
            Availability availability;
            if (!binding.Enabled) availability = Availability.Disabled;
            else if (!instance.DeclaredCapabilities.Contains(capability)) availability = Availability.UnsupportedCapability;
            else if (instance.RequiredSecrets.Any(secret => !instance.AccountBindings.TryGetValue(secret, out var account) || !hasSecret(account, secret))) availability = Availability.MissingCredential;
            else if (temporarilyUnavailable?.Invoke(instance.InstanceId) == true) availability = Availability.TemporarilyUnavailable;
            else availability = Availability.Ready;
            result.Add(new ResolvedService(binding, instance, availability));
        }
        return result;
    }

    /// <summary>
    /// Page-local reordering (translation vs AI pages share one translationOrder): moves an item within
    /// the slots the page occupies; items of the other page keep their positions (ARCHITECTURE 3, CFG03).
    /// </summary>
    public static IReadOnlyList<string> ReorderWithinPage(IReadOnlyList<string> order, Func<string, bool> onPage, string moved, int newPageIndex)
    {
        var slots = order.Select((id, index) => (id, index)).Where(x => onPage(x.id)).Select(x => x.index).ToArray();
        var pageItems = slots.Select(i => order[i]).ToList();
        if (!pageItems.Remove(moved)) throw new ArgumentException("item is not on this page", nameof(moved));
        pageItems.Insert(Math.Clamp(newPageIndex, 0, pageItems.Count), moved);
        var result = order.ToArray();
        for (int k = 0; k < slots.Length; k++) result[slots[k]] = pageItems[k];
        return result;
    }
}

/// <summary>Per-task immutable configuration (ARCHITECTURE 3.1): later saves affect only later calls.</summary>
public sealed record ConfigSnapshot(long SettingsRevision, long ProviderRevision, long PromptRevision, int DefaultExpandedCards, TimeSpan AiTimeout)
{
    public static readonly TimeSpan HostCallCeiling = TimeSpan.FromSeconds(600);

    /// <summary>Effective call timeout: explicit service value, else capability default, bounded by the host ceiling.</summary>
    public static TimeSpan EffectiveTimeout(Capability capability, TimeSpan? serviceOverride, TimeSpan? aiPageDefault = null)
    {
        TimeSpan value = serviceOverride ?? aiPageDefault ?? capability switch
        {
            Capability.Ocr => TimeSpan.FromSeconds(60),
            Capability.Asr => TimeSpan.FromSeconds(300),
            _ => TimeSpan.FromSeconds(30),
        };
        return value <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : value > HostCallCeiling ? HostCallCeiling : value;
    }
}
