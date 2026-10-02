namespace Susu.Abstractions;

/// <summary>The save dialog for the diagnostics file (F17.2). Null: the user cancelled. The page never sends a path.</summary>
public interface IDiagnosticsFilePicker
{
    Task<string?> PickSaveAsync(string suggestedFileName, CancellationToken cancellationToken);
}

/// <summary>What the About page shows. <see cref="LogLocation"/> is for display only and uses environment-variable form (never the user name).</summary>
public sealed record AboutInfo(string Version, string Build, string Os, string Runtime, string LogLocation);

public sealed record LogUsage(int Files, long Bytes);

/// <summary>Error is a stable key (disk-full, write-failed, leak-detected) or null. Counts describe what the file holds.</summary>
public sealed record DiagnosticsOutcome(bool Ok, string? Error, int LogFiles, int LogLines, int DroppedLines, long Bytes);

/// <summary>The About page's port (F17.2): facts, the log folder and the diagnostics export. Implemented in Susu.Storage.</summary>
public interface IAboutService
{
    AboutInfo Info();
    LogUsage Logs();
    /// <summary>Opens the log folder in the shell; false when it could not be opened. The folder is the host's own, never a page value.</summary>
    bool OpenLogFolder();
    DiagnosticsOutcome ExportDiagnostics(string path);
}

/// <summary>
/// One thing the data-clean section can delete. Kind: caches | logs | screenshots | favorites | settings | accounts. Count and Bytes describe what would go
/// (items and bytes; settings has none). Available is false when the host has no store for it.
/// </summary>
public sealed record DataCleanItem(string Kind, int Count, long Bytes, bool Available);

/// <summary>Error is a stable key (in-use, failed, unavailable, conflict) or null. Removed counts items; some may have been skipped because a task holds them.</summary>
public sealed record DataCleanOutcome(string Kind, bool Ok, string? Error, int Removed, long Bytes, int Skipped);

/// <summary>The data-clean port (F17.2). Only the app's own data is touched; user files outside the data folders and the plugin packages are never listed or deleted.</summary>
public interface IDataCleanService
{
    IReadOnlyList<DataCleanItem> Items();
    DataCleanOutcome Clear(string kind);
}

public static class DataCleanKinds
{
    public const string Caches = "caches", Logs = "logs", Screenshots = "screenshots", Favorites = "favorites", Settings = "settings", Accounts = "accounts";
    public static readonly string[] All = [Caches, Logs, Screenshots, Favorites, Settings, Accounts];
}
