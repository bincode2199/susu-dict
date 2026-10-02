using Susu.Abstractions;

namespace Susu.Plugins.AppUpdate;

/// <summary>Starts the installer helper: a copy of the application outside the install folder that waits for this app to exit and runs <see cref="AppUpdater.Apply"/>.</summary>
public interface IUpdateLauncher
{
    bool LaunchHelper();

    /// <summary>F18.3: starts the helper that restores the previous version from the backup pair. False when no helper could be started.</summary>
    bool LaunchRollback() => false;
}

/// <summary>
/// The application update as the shell sees it (F18.2). Checks (manual or scheduled) only ask the source; the package is downloaded and staged by the
/// user's command and installed by the user's command. The page state is derived from the journal first (so it survives a restart) and from the last
/// check second. A failed or unverified check is "failed", never "up to date".
/// </summary>
public sealed class AppUpdateService(string currentVersion, IAppUpdateSource? source, AppUpdater updater, UpdatePrefs prefs, IClock clock, Func<int> inFlight,
    IUpdateLauncher launcher, bool keyringEmbedded) : IAppUpdateService
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private string state = "none";
    private AppUpdateOffer? offer;
    private string? error;

    public bool Available => source is not null;

    public AppUpdateInfo Status()
    {
        var journal = updater.Read();
        string shown = state;
        AppUpdateOffer? shownOffer = offer;
        string? shownError = error;
        if (journal is not null)
        {
            var fromJournal = new AppUpdateOffer(journal.ToVersion, journal.Sequence, journal.PackageFile, journal.PackageSize, journal.PackageSha256, "");
            switch (journal.Stage)
            {
                case UpdateStages.Downloading: shown = "downloading"; shownOffer = fromJournal; break;
                case UpdateStages.Staged: shown = "ready"; shownOffer = fromJournal; shownError = journal.Error is null ? error : journal.Error; break;
                case UpdateStages.Committed: shown = "committed"; shownOffer = fromJournal; shownError = null; break;
                case UpdateStages.RolledBack: shown = "rolledBack"; shownOffer = fromJournal; shownError = journal.Error ?? "rolled-back"; break;
                default: shown = "installing"; shownOffer = fromJournal; break;
            }
        }
        return new AppUpdateInfo(shown, currentVersion, shownOffer, shownError, Available, prefs.AutoCheck, prefs.LastAttempt, inFlight(), keyringEmbedded);
    }

    public async Task<AppUpdateInfo> CheckAsync(bool automatic, CancellationToken cancellationToken)
    {
        if (source is null) return Status();
        if (updater.Read() is { Stage: not (UpdateStages.RolledBack or UpdateStages.Committed) }) return Status(); // a staged or running install is not disturbed by a check
        if (!await serial.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return Status();
        try
        {
            state = "checking";
            error = null;
            prefs.Update(attempt: clock.UtcNow);
            AppUpdateCheck check;
            try { check = await source.CheckAsync(currentVersion, cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { check = AppUpdateCheck.Failed("check-failed"); }
            catch (OperationCanceledException) { state = "none"; throw; }
            switch (check.Status)
            {
                case AppUpdateStatus.Available when check.Offer is not null && updater.Guard.IsFailed(check.Offer.Version): state = "failed"; offer = null; error = "version-failed-before"; break; // loop guard (F18.3)
                case AppUpdateStatus.Available when check.Offer is not null: state = "available"; offer = check.Offer; break;
                case AppUpdateStatus.UpToDate: state = "upToDate"; offer = null; break;
                default: state = "failed"; offer = null; error = check.ErrorCode ?? "check-failed"; break;
            }
            return Status();
        }
        finally { serial.Release(); }
    }

    public async Task<AppUpdateInfo> DownloadAsync(CancellationToken cancellationToken)
    {
        if (source is null || state != "available" || offer is not { } wanted || updater.Read() is not null) return Status();
        if (updater.Guard.IsFailed(wanted.Version)) return Failed("version-failed-before");
        if (!await serial.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return Status();
        try
        {
            string folder = updater.BeginDownload(wanted, currentVersion);
            state = "downloading";
            string? file = null;
            try { file = await source.FetchAsync(wanted, folder, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { updater.Discard(); state = "available"; throw; }
            catch (Exception e) when (e is not OutOfMemoryException) { updater.Discard(); return Failed(e is IOException io && Susu.Storage.StorageErrors.IsDiskFull(io) ? "disk-full" : "download-failed"); }
            var staged = updater.Stage(file);
            if (!staged.Ok) return Failed(staged.Error ?? "stage-failed");
            state = "ready";
            error = null;
            return Status();
        }
        finally { serial.Release(); }
    }

    private AppUpdateInfo Failed(string code)
    {
        state = "failed";
        error = code;
        return Status();
    }

    public AppUpdateInstallOutcome Install(bool acknowledgedInFlight)
    {
        if (updater.Read() is not { Stage: UpdateStages.Staged }) return new AppUpdateInstallOutcome(false, "not-ready");
        if (inFlight() > 0 && !acknowledgedInFlight) return new AppUpdateInstallOutcome(false, "in-flight-needs-confirmation");
        if (!serial.Wait(0)) return new AppUpdateInstallOutcome(false, "busy");
        try
        {
            if (!launcher.LaunchHelper()) return new AppUpdateInstallOutcome(false, "helper-failed");
            state = "installing";
            return new AppUpdateInstallOutcome(true, null);
        }
        finally { serial.Release(); }
    }

    /// <summary>F18.3: the previous version can be restored from the backup pair of the last committed update (the pair is kept until the next update).</summary>
    public bool CanRollBack => updater.CanRollBackToPrevious();

    /// <summary>Starts the rollback helper on the user's command; names the interrupted tasks first like an install does.</summary>
    public AppUpdateInstallOutcome RollBack(bool acknowledgedInFlight)
    {
        if (!updater.CanRollBackToPrevious()) return new AppUpdateInstallOutcome(false, "no-backup");
        if (inFlight() > 0 && !acknowledgedInFlight) return new AppUpdateInstallOutcome(false, "in-flight-needs-confirmation");
        if (!serial.Wait(0)) return new AppUpdateInstallOutcome(false, "busy");
        try
        {
            if (!launcher.LaunchRollback()) return new AppUpdateInstallOutcome(false, "helper-failed");
            state = "installing";
            return new AppUpdateInstallOutcome(true, null);
        }
        finally { serial.Release(); }
    }

    public void Discard()
    {
        if (updater.Read() is { Stage: UpdateStages.Staged or UpdateStages.RolledBack or UpdateStages.Committed })
        {
            updater.Discard();
            state = "none";
            offer = null;
            error = null;
        }
    }

    public void SetAutoCheck(bool enabled) => prefs.Update(autoCheck: enabled);
}
