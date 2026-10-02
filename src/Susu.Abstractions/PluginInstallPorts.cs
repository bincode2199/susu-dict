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
/// ConfirmReasons (F16.2) are the changes the user must acknowledge explicitly before it replaces the running version: signer-changed,
/// signature-removed, permissions-expanded. IsUpdate: it came from an update check, not from the file dialog.
/// </summary>
public sealed record PluginPreviewInfo(bool Ok, string? Token, string? Id, string? Name, string? Version, string SignerKind, string Signer,
    string? OverridesBuiltIn, string? ReplacesVersion, PluginPermissionDiff? Diff, PluginIssue[] Issues, string[]? ConfirmReasons = null, bool IsUpdate = false)
{
    public string[] Reasons => ConfirmReasons ?? [];
}

/// <summary>Result of an install or uninstall. Error is a stable key (install.*); Issues carries detail when a re-check failed. Interrupted: in-flight calls of the package that were cancelled by the switch.</summary>
public sealed record PluginInstallOutcome(bool Ok, string? Error, string? Id, string? Version, PluginIssue[] Issues, string? RestoredBuiltIn = null, int Interrupted = 0);

/// <summary>A user-installed package. Active is always true for listed rows; OverridesBuiltIn is the shipped version it overrides.</summary>
public sealed record InstalledPluginInfo(string Id, string Name, string Version, string SignerKind, string Signer, string[] Capabilities, string[] Origins, string[] Secrets,
    string? OverridesBuiltIn, string? BuiltInVersion);

/// <summary>What a package has running right now: calls in flight for one capability.</summary>
public sealed record PluginTaskInfo(string Capability, int Count);

/// <summary>
/// The running plugin host as the installer needs it (F16.2). InFlight tells the user what a switch would cancel. Restart makes the running host
/// load the packages as they are now: the package's in-flight calls are cancelled cleanly (never replayed) and the next call uses a fresh host.
/// Returns how many calls were cancelled.
/// </summary>
public interface IPluginHostControl
{
    IReadOnlyList<PluginTaskInfo> InFlight(string packageId);

    int Restart(string packageId);
}

/// <summary>The plugin installer as the shell sees it (F16.1/F16.2). The shell never sees paths of staged files or package contents beyond these records.</summary>
public interface IPluginInstallService
{
    /// <summary>Unpacks and validates a package into staging. A new preview replaces the pending one.</summary>
    PluginPreviewInfo Preview(string packagePath);

    /// <summary>
    /// F16.2: stages a package an update check fetched for an installed package. It is validated like any package and kept aside, one per package id; the
    /// installed version keeps running and nothing changes until <see cref="Install"/> is called for its token. A package that fails validation is
    /// discarded (Ok false) and the installed version is untouched.
    /// </summary>
    PluginPreviewInfo StageUpdate(string packagePath);

    /// <summary>Staged updates waiting for the user, one per package.</summary>
    IReadOnlyList<PluginPreviewInfo> StagedUpdates();

    /// <summary>
    /// Runs the activation transaction for the pending or staged package named by <paramref name="token"/>. When the package carries ConfirmReasons it
    /// is applied only with <paramref name="acknowledged"/> true (install.needsConfirmation otherwise, and it stays staged).
    /// </summary>
    PluginInstallOutcome Install(string token, bool acknowledged = false);

    void Discard(string token);

    /// <summary>Uninstalls the user version (the built-in one, when there is one, takes over again). The package's stored data stays unless <paramref name="removeData"/>.</summary>
    PluginInstallOutcome Uninstall(string packageId, bool removeData = false);

    IReadOnlyList<InstalledPluginInfo> Installed();

    /// <summary>Calls of the package running now, which an install or uninstall would cancel.</summary>
    IReadOnlyList<PluginTaskInfo> InFlight(string packageId);

    /// <summary>Raised after an install, uninstall or recovery changed what is active.</summary>
    event Action? Changed;
}

/// <summary>What an update check found for one installed package.</summary>
public enum PluginUpdateStatus { UpToDate, Available, Failed }

/// <summary>An update the source offers: the package id, the version, and the source's own handle for fetching it.</summary>
public sealed record PluginUpdateOffer(string PackageId, string Version, string Handle);

public sealed record PluginUpdateCheck(PluginUpdateStatus Status, PluginUpdateOffer? Offer = null, string? ErrorCode = null);

/// <summary>
/// Where plugin updates come from. The product has no update feed yet (DEV-PLAN F16.2: the manifest <c>update</c> field and the signed update manifest
/// schema are not specified), so production registers none and the page says updates are unavailable. A source must verify its own manifest signature
/// before it reports anything: a verification failure is <c>Failed</c>, never <c>UpToDate</c> (PLAN security rule).
/// </summary>
public interface IPluginUpdateSource
{
    Task<PluginUpdateCheck> CheckAsync(string packageId, string installedVersion, CancellationToken cancellationToken);

    /// <summary>Downloads the offered package into <paramref name="folder"/> and returns the file path. The installer re-verifies everything.</summary>
    Task<string> FetchAsync(PluginUpdateOffer offer, string folder, CancellationToken cancellationToken);
}

public sealed record PluginUpdateCheckOutcome(int Checked, int Staged, string[] UpToDate, PluginUpdateFailure[] Failures);

public sealed record PluginUpdateFailure(string PackageId, string Code);

/// <summary>The update check as the shell sees it. <see cref="Available"/> is false when no source is configured.</summary>
public interface IPluginUpdateService
{
    bool Available { get; }

    Task<PluginUpdateCheckOutcome> CheckAsync(CancellationToken cancellationToken);
}
