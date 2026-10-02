using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;

namespace Susu.Ui;

/// <summary>
/// F16.1 SetPlugins: pick (or drop) a package, review what it would change, confirm, uninstall. The page never sends a path: the host opens the
/// dialog (<see cref="PluginPicker"/>) or receives the dropped path from native code (<see cref="PreviewPluginPackageAsync"/>), stages the package
/// behind a token, and only the token comes back to confirm. Unzipping, signature and rule checks live in the installer service; this part projects
/// its records and keeps the one pending preview and the last outcome for the page. Every Settings-window command answers with the fresh settings view.
/// </summary>
public sealed partial class ShellCoordinator
{
    private IPluginInstallService? plugins;
    private PluginPreviewInfo? pendingPlugin;
    private PluginOutcomeView? lastPlugin;
    private int pluginBusy;

    /// <summary>The file-open dialog. Null: the page offers no "choose a package" (drop still works when native code calls <see cref="PreviewPluginPackageAsync"/>).</summary>
    public IPluginPackagePicker? PluginPicker { get; set; }

    /// <summary>The package installer. Null: the Plugins page is hidden (the view has no plugins section).</summary>
    public IPluginInstallService? PluginInstaller
    {
        get => plugins;
        set
        {
            if (plugins is not null) plugins.Changed -= OnPluginsChanged;
            plugins = value;
            if (plugins is not null) plugins.Changed += OnPluginsChanged;
        }
    }

    private void OnPluginsChanged() => platform.StartTimer(TimeSpan.Zero, () =>
        Broadcast(UiMessageKind.Event, "settings", JsonSerializer.SerializeToElement(ProjectSettings(config.State), ContractsJson.Default.SettingsView), WindowKind.Settings));

    private PluginsView? ProjectPlugins()
    {
        if (plugins is not { } service) return null;
        var installed = service.Installed().Select(p => new InstalledPluginView(p.Id, p.Name, p.Version, p.SignerKind, p.Signer, p.Capabilities, p.Origins, p.Secrets, p.OverridesBuiltIn)).ToArray();
        PluginPreviewView? pending = null;
        if (pendingPlugin is { Ok: true, Token: { } token, Diff: { } diff } p)
            pending = new PluginPreviewView(token, p.Id!, p.Name!, p.Version!, p.SignerKind, p.Signer, p.OverridesBuiltIn, p.ReplacesVersion, diff.Against, diff.BaseVersion,
                diff.AddedCapabilities, diff.RemovedCapabilities, diff.AddedOrigins, diff.RemovedOrigins, diff.AddedSecrets, diff.RemovedSecrets);
        return new PluginsView(installed, pending, lastPlugin, PluginPicker is not null);
    }

    private async Task<CommandResult> PickPluginAsync()
    {
        if (plugins is null || PluginPicker is not { } picker) return new CommandResult(false, "unavailable");
        if (Interlocked.Exchange(ref pluginBusy, 1) == 1) return new CommandResult(false, "busy");
        try
        {
            string? path;
            try { path = await picker.PickAsync(CancellationToken.None); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Diagnostic?.Invoke("plugin.picker-failed " + e.GetType().Name);
                return new CommandResult(false, "picker");
            }
            if (path is null) return Ok(SettingsElement()); // cancelled
            await PreviewAsync(path);
            return Ok(SettingsElement());
        }
        finally { Interlocked.Exchange(ref pluginBusy, 0); }
    }

    /// <summary>
    /// Entry for a package dropped on the Settings window. The native drop handler is not wired yet (needs the WebView host's drag-drop hook);
    /// the method does what the dialog path does after choosing a file, and tells the Settings window with a fresh settings event.
    /// </summary>
    public async Task<CommandResult> PreviewPluginPackageAsync(string path)
    {
        if (plugins is null) return new CommandResult(false, "unavailable");
        if (Interlocked.Exchange(ref pluginBusy, 1) == 1) return new CommandResult(false, "busy");
        try
        {
            await PreviewAsync(path);
            OnPluginsChanged();
            return Ok(SettingsElement());
        }
        finally { Interlocked.Exchange(ref pluginBusy, 0); }
    }

    private async Task PreviewAsync(string path)
    {
        var service = plugins!;
        string ext = Path.GetExtension(path);
        PluginPreviewInfo info;
        if (!ext.Equals(".susuext", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            info = new PluginPreviewInfo(false, null, null, null, null, "unsigned", "", null, null, null, [new PluginIssue("$", "not-a-package")]);
        else
        {
            try { info = await Task.Run(() => service.Preview(path)); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Diagnostic?.Invoke("plugin.preview-failed " + e.GetType().Name);
                info = new PluginPreviewInfo(false, null, null, null, null, "unsigned", "", null, null, null, [new PluginIssue("$", "unreadable")]);
            }
        }
        pendingPlugin = info.Ok ? info : null;
        lastPlugin = info.Ok ? null : new PluginOutcomeView("preview", null, null, "install.rejected", [.. info.Issues.Select(i => new PluginIssueView(i.Path, i.Code))], null);
    }

    private async Task<CommandResult> ConfirmPluginAsync(PluginTokenRequest request)
    {
        if (plugins is not { } service) return new CommandResult(false, "unavailable");
        if (pendingPlugin?.Token != request.Token) return new CommandResult(false, "not-found");
        if (Interlocked.Exchange(ref pluginBusy, 1) == 1) return new CommandResult(false, "busy");
        try
        {
            PluginInstallOutcome outcome;
            try { outcome = await Task.Run(() => service.Install(request.Token)); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Diagnostic?.Invoke("plugin.install-failed " + e.GetType().Name);
                outcome = new PluginInstallOutcome(false, "install.activationFailed", null, null, []);
            }
            lastPlugin = new PluginOutcomeView("install", outcome.Id ?? pendingPlugin.Id, outcome.Version ?? pendingPlugin.Version, outcome.Error,
                [.. outcome.Issues.Select(i => new PluginIssueView(i.Path, i.Code))], null);
            pendingPlugin = null;
            return Ok(SettingsElement());
        }
        finally { Interlocked.Exchange(ref pluginBusy, 0); }
    }

    private CommandResult DiscardPlugin(PluginTokenRequest request)
    {
        if (plugins is not { } service) return new CommandResult(false, "unavailable");
        if (pendingPlugin?.Token == request.Token) { service.Discard(request.Token); pendingPlugin = null; }
        return Ok(SettingsElement());
    }

    private CommandResult UninstallPlugin(PluginUninstallRequest request)
    {
        if (plugins is not { } service) return new CommandResult(false, "unavailable");
        if (Interlocked.Exchange(ref pluginBusy, 1) == 1) return new CommandResult(false, "busy");
        try
        {
            PluginInstallOutcome outcome;
            try { outcome = service.Uninstall(request.Id); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Diagnostic?.Invoke("plugin.uninstall-failed " + e.GetType().Name);
                outcome = new PluginInstallOutcome(false, "uninstall.failed", request.Id, null, []);
            }
            lastPlugin = new PluginOutcomeView("uninstall", request.Id, outcome.Version, outcome.Error, [], outcome.RestoredBuiltIn);
            return Ok(SettingsElement());
        }
        finally { Interlocked.Exchange(ref pluginBusy, 0); }
    }
}
