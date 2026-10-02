using Susu.Abstractions;
using Susu.Contracts;

namespace Susu.Ui;

/// <summary>
/// F18.2 application update on the Settings About page. The page sends no URL, path or file name: the host knows its own source. Check, download and install
/// are separate commands, each only from the Settings window; the scheduled background check calls <see cref="IAppUpdateService.CheckAsync"/> directly and
/// never downloads or installs. Every command answers with the fresh settings view.
/// </summary>
public sealed partial class ShellCoordinator
{
    /// <summary>The application update service. Null: the page has no update section.</summary>
    public IAppUpdateService? AppUpdates { get; set; }

    private UpdateView? ProjectUpdate()
    {
        if (AppUpdates is not { } service) return null;
        var info = service.Status();
        return new UpdateView(info.State, info.CurrentVersion, info.Offer is { } o ? new UpdateOfferView(o.Version, o.Size, o.Notes) : null, info.Error, info.SourceConfigured, info.AutoCheck,
            info.LastCheck?.ToString("O", System.Globalization.CultureInfo.InvariantCulture), info.InFlight, info.KeyringEmbedded);
    }

    private async Task<CommandResult> CheckAppUpdateAsync()
    {
        if (AppUpdates is not { Available: true } service) return new CommandResult(false, "unavailable");
        if (Interlocked.Exchange(ref aboutBusy, 1) == 1) return new CommandResult(false, "busy");
        try
        {
            await service.CheckAsync(automatic: false, CancellationToken.None);
            return Ok(SettingsElement());
        }
        finally { Interlocked.Exchange(ref aboutBusy, 0); }
    }

    private async Task<CommandResult> DownloadAppUpdateAsync()
    {
        if (AppUpdates is not { Available: true } service) return new CommandResult(false, "unavailable");
        if (Interlocked.Exchange(ref aboutBusy, 1) == 1) return new CommandResult(false, "busy");
        try
        {
            await service.DownloadAsync(CancellationToken.None);
            return Ok(SettingsElement());
        }
        finally { Interlocked.Exchange(ref aboutBusy, 0); }
    }

    private CommandResult InstallAppUpdate(UpdateInstallRequest request)
    {
        if (AppUpdates is not { Available: true } service) return new CommandResult(false, "unavailable");
        var outcome = service.Install(request.AcknowledgedInFlight);
        return outcome.Ok ? Ok(SettingsElement()) : new CommandResult(false, outcome.Error ?? "failed");
    }

    private CommandResult DiscardAppUpdate()
    {
        if (AppUpdates is not { } service) return new CommandResult(false, "unavailable");
        service.Discard();
        return Ok(SettingsElement());
    }

    private CommandResult SetAppUpdateAutoCheck(UpdateAutoCheckRequest request)
    {
        if (AppUpdates is not { } service) return new CommandResult(false, "unavailable");
        service.SetAutoCheck(request.Enabled);
        return Ok(SettingsElement());
    }
}
