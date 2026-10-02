using System.Reflection;
using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;

namespace Susu.Ui;

/// <summary>
/// F17.2 About page and data clean. The page never sends or receives a path: the host opens the log folder itself and the save dialog for the diagnostics
/// file, and only a file name comes back. Data.Clear needs <c>Confirm = true</c> and a known kind; what each kind deletes is documented on
/// <see cref="IDataCleanService"/> and listed with counts in the view. Every Settings-window command answers with the fresh settings view.
/// </summary>
public sealed partial class ShellCoordinator
{
    private int aboutBusy;
    private DiagnosticsResultView? lastDiagnostics;
    private DataCleanResultView? lastCleaned;
    private static LicenseView[]? licenses;

    /// <summary>Facts, log folder and diagnostics. Null: the page has no About section.</summary>
    public IAboutService? About { get; set; }

    /// <summary>The save dialog for the diagnostics file. Null: no export button.</summary>
    public IDiagnosticsFilePicker? DiagnosticsPicker { get; set; }

    /// <summary>The data-clean port. Null: no clean entries.</summary>
    public IDataCleanService? DataClean { get; set; }

    private AboutView? ProjectAbout()
    {
        if (About is not { } service) return null;
        var info = service.Info();
        var logs = service.Logs();
        DataCleanView[] data = DataClean is { } clean ? [.. clean.Items().Select(i => new DataCleanView(i.Kind, i.Count, i.Bytes, i.Available))] : [];
        return new AboutView(info.Version, info.Build, info.Os, info.Runtime, info.LogLocation, logs.Files, logs.Bytes, true, DiagnosticsPicker is not null,
            lastDiagnostics, Licenses(), data, lastCleaned);
    }

    /// <summary>The licenses of what ships, from the generated LICENSES/third-party.json embedded in this assembly (tools/generate-notices.mjs).</summary>
    public static LicenseView[] Licenses()
    {
        if (licenses is not null) return licenses;
        using var stream = typeof(ShellCoordinator).Assembly.GetManifestResourceStream("third-party.json");
        if (stream is null) return licenses = [];
        using var doc = JsonDocument.Parse(stream);
        string Text(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        return licenses = [.. doc.RootElement.GetProperty("components").EnumerateArray()
            .Select(c => new LicenseView(Text(c, "name"), Text(c, "version"), Text(c, "license"), Text(c, "kind"), Text(c, "ships")))];
    }

    private CommandResult OpenLogs()
    {
        if (About is not { } service) return new CommandResult(false, "unavailable");
        return service.OpenLogFolder() ? Ok(SettingsElement()) : new CommandResult(false, "open-failed");
    }

    private async Task<CommandResult> ExportDiagnosticsAsync()
    {
        if (About is not { } service || DiagnosticsPicker is not { } picker) return new CommandResult(false, "unavailable");
        if (Interlocked.Exchange(ref aboutBusy, 1) == 1) return new CommandResult(false, "busy");
        try
        {
            string? path;
            try { path = await picker.PickSaveAsync($"su-su-diagnostics-{DateTime.Now:yyyyMMdd}.zip", CancellationToken.None); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Diagnostic?.Invoke("diagnostics.picker-failed " + e.GetType().Name);
                lastDiagnostics = new DiagnosticsResultView("picker", null, 0, 0, 0, 0);
                return Ok(SettingsElement());
            }
            if (path is null) return Ok(SettingsElement()); // cancelled
            DiagnosticsOutcome outcome;
            try { outcome = await Task.Run(() => service.ExportDiagnostics(path)); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Diagnostic?.Invoke("diagnostics.export-failed " + e.GetType().Name);
                outcome = new DiagnosticsOutcome(false, "write-failed", 0, 0, 0, 0);
            }
            lastDiagnostics = new DiagnosticsResultView(outcome.Error, outcome.Ok ? Path.GetFileName(path) : null, outcome.LogFiles, outcome.LogLines, outcome.DroppedLines, outcome.Bytes);
            return Ok(SettingsElement());
        }
        finally { Interlocked.Exchange(ref aboutBusy, 0); }
    }

    private CommandResult DismissAbout()
    {
        lastDiagnostics = null;
        lastCleaned = null;
        return Ok(SettingsElement());
    }

    private async Task<CommandResult> ClearDataAsync(DataCleanRequest request)
    {
        if (DataClean is not { } service) return new CommandResult(false, "unavailable");
        if (!request.Confirm) return new CommandResult(false, "confirm-required");
        if (!DataCleanKinds.All.Contains(request.Kind, StringComparer.Ordinal)) return new CommandResult(false, "unknown-kind");
        if (Interlocked.Exchange(ref aboutBusy, 1) == 1) return new CommandResult(false, "busy");
        try
        {
            if (request.Kind == DataCleanKinds.Accounts)
                foreach (var account in config.State.Effective.Accounts) InvalidateOptions(account.Id);
            var outcome = await Task.Run(() => service.Clear(request.Kind));
            lastCleaned = new DataCleanResultView(outcome.Kind, outcome.Error, outcome.Removed, outcome.Bytes, outcome.Skipped);
            return Ok(SettingsElement());
        }
        finally { Interlocked.Exchange(ref aboutBusy, 0); }
    }
}
