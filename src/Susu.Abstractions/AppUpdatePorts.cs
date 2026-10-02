namespace Susu.Abstractions;

/// <summary>
/// What a verified update manifest offers (F18.2). Everything here comes from a manifest whose signature was verified before it was parsed.
/// Sequence is the release counter the anti-rollback rule compares; Size and Sha256 describe the package file; Notes is plain text from the manifest.
/// </summary>
public sealed record AppUpdateOffer(string Version, long Sequence, string FileName, long Size, string Sha256, string Notes);

public enum AppUpdateStatus { UpToDate, Available, Failed }

/// <summary>Result of an update check. Failed carries a stable ErrorCode; a failed or unverified response is never reported as UpToDate or as a newer version.</summary>
public sealed record AppUpdateCheck(AppUpdateStatus Status, AppUpdateOffer? Offer, string? ErrorCode)
{
    public static AppUpdateCheck UpToDate() => new(AppUpdateStatus.UpToDate, null, null);
    public static AppUpdateCheck Available(AppUpdateOffer offer) => new(AppUpdateStatus.Available, offer, null);
    public static AppUpdateCheck Failed(string code) => new(AppUpdateStatus.Failed, null, code);
}

/// <summary>
/// Where application updates come from (ARCHITECTURE 10). The product registers a source only when a manifest URL is configured (none is by default).
/// A source verifies its manifest signature before it reports anything. FetchAsync downloads the offered package into the folder and returns its path;
/// the updater re-checks size and hash itself.
/// </summary>
public interface IAppUpdateSource
{
    Task<AppUpdateCheck> CheckAsync(string currentVersion, CancellationToken cancellationToken);

    Task<string> FetchAsync(AppUpdateOffer offer, string folder, CancellationToken cancellationToken);
}

/// <summary>
/// The update page's state. State: none | checking | upToDate | available | downloading | ready | installing | failed | rolledBack | committed.
/// Error is a stable key (see the page's texts); Offer is set for available/downloading/ready/installing.
/// </summary>
public sealed record AppUpdateInfo(string State, string CurrentVersion, AppUpdateOffer? Offer, string? Error, bool SourceConfigured, bool AutoCheck,
    DateTimeOffset? LastCheck, int InFlight, bool KeyringEmbedded, string? Detail = null);

/// <summary>Result of starting the install. Error: not-ready | in-flight-needs-confirmation | helper-failed | busy | none.</summary>
public sealed record AppUpdateInstallOutcome(bool Ok, string? Error);

/// <summary>The application update as the shell sees it (F18.2). Checks may run on a schedule; download and install only on the user's command.</summary>
public interface IAppUpdateService
{
    /// <summary>False when no update source is configured: the page says so and offers no check.</summary>
    bool Available { get; }

    AppUpdateInfo Status();

    /// <summary>Checks the source. <paramref name="automatic"/> only changes what is logged and never starts a download.</summary>
    Task<AppUpdateInfo> CheckAsync(bool automatic, CancellationToken cancellationToken);

    /// <summary>User command: downloads and stages the offered package (size, hash, safe extraction). Nothing is installed.</summary>
    Task<AppUpdateInfo> DownloadAsync(CancellationToken cancellationToken);

    /// <summary>User command: starts the installer helper that waits for this app to exit, replaces the files and migrates. Needs <paramref name="acknowledgedInFlight"/> when tasks are running.</summary>
    AppUpdateInstallOutcome Install(bool acknowledgedInFlight);

    /// <summary>Drops the staged package and any finished result.</summary>
    void Discard();

    void SetAutoCheck(bool enabled);
}
