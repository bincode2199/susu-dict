namespace Susu.Abstractions;

/// <summary>The file-open dialog for a plugin package: the chosen .susuext path, or null when the user cancelled.</summary>
public interface IPluginPackagePicker
{
    Task<string?> PickAsync(CancellationToken cancellationToken);
}

/// <summary>One reason a package was rejected. Code is a stable key (zip and manifest codes); Path names the offending entry or field.</summary>
public sealed record PluginIssue(string Path, string Code);

/// <summary>
/// What installing would change in the package's authority, against Against: none (first install: Added is the full set), installed (the active
/// user version, BaseVersion) or builtin (the shipped package it overrides). Origins are exact origins from hosts; Secrets are credentialUse names.
/// </summary>
public sealed record PluginPermissionDiff(string Against, string? BaseVersion,
    string[] AddedCapabilities, string[] RemovedCapabilities, string[] AddedOrigins, string[] RemovedOrigins, string[] AddedSecrets, string[] RemovedSecrets)
{
    public bool HasAdditions => AddedCapabilities.Length + AddedOrigins.Length + AddedSecrets.Length > 0;
}

/// <summary>
/// A package that passed staging and waits for the user's confirmation. SignerKind: unsigned | host | thirdParty; Signer is the verified identity
/// (a key id), empty for unsigned. OverridesBuiltIn: the shipped version this package would replace. ReplacesVersion: the active user version it updates.
/// </summary>
public sealed record PluginPreviewInfo(bool Ok, string? Token, string? Id, string? Name, string? Version, string SignerKind, string Signer,
    string? OverridesBuiltIn, string? ReplacesVersion, PluginPermissionDiff? Diff, PluginIssue[] Issues);

/// <summary>Result of an install or uninstall. Error is a stable key (install.*); Issues carries detail when a re-check failed.</summary>
public sealed record PluginInstallOutcome(bool Ok, string? Error, string? Id, string? Version, PluginIssue[] Issues, string? RestoredBuiltIn = null);

/// <summary>A user-installed package. Active is always true for listed rows; OverridesBuiltIn is the shipped version it overrides.</summary>
public sealed record InstalledPluginInfo(string Id, string Name, string Version, string SignerKind, string Signer, string[] Capabilities, string[] Origins, string[] Secrets,
    string? OverridesBuiltIn, string? BuiltInVersion);

/// <summary>The plugin installer as the shell sees it (F16.1). The shell never sees paths of staged files or package contents beyond these records.</summary>
public interface IPluginInstallService
{
    /// <summary>Unpacks and validates a package into staging. A new preview replaces the pending one.</summary>
    PluginPreviewInfo Preview(string packagePath);

    /// <summary>Runs the activation transaction for the pending package named by <paramref name="token"/>.</summary>
    PluginInstallOutcome Install(string token);

    void Discard(string token);

    PluginInstallOutcome Uninstall(string packageId);

    IReadOnlyList<InstalledPluginInfo> Installed();

    /// <summary>Raised after an install, uninstall or recovery changed what is active.</summary>
    event Action? Changed;
}
