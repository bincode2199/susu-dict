namespace Susu.Plugins.AppUpdate;

/// <summary>
/// F18.3 (TEST-PLAN UPD08): what the update helper accepts from its command line. Every path is untrusted text. A folder is accepted only as an absolute
/// local drive path of plain names: no relative or rooted-without-drive form, no <c>..</c> or <c>.</c>, no UNC, device (<c>\\?\</c>, <c>\\.\</c>) or alternate-data-stream
/// syntax, no reserved device names, no control characters, no drive that is a network drive, and the folder itself is not a reparse point (junction or symlink).
/// </summary>
public static class UpdatePathRules
{
    public const int MaxLength = 240;

    private static readonly string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>The canonical absolute form of <paramref name="path"/>, or null when it is not an acceptable plain local path.</summary>
    public static string? PlainAbsolute(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxLength) return null;
        if (path.Length > 3) path = path.TrimEnd('\\', '/');
        foreach (char c in path) if (c < ' ' || c == '"' || c == '<' || c == '>' || c == '|' || c == '*' || c == '?') return null;
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')) return null; // "C:\..." only: not "C:rel", "\x", "\\server", "\\?\", "\\.\"
        if (path.IndexOf(':', 2) >= 0) return null; // alternate data stream or a second drive
        foreach (string part in path[3..].Split('\\', '/'))
        {
            if (part.Length == 0) { if (path.AsSpan(3).Length == 0) continue; return null; } // empty segments ("a\\b") are never produced by a caller that knows what it means
            if (part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')) return null;
            if (reserved.Contains(part.Split('.')[0].ToUpperInvariant())) return null;
        }
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        full = full.Length > 3 ? full.TrimEnd('\\') : full;
        try { if (new DriveInfo(full[..1]).DriveType is DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown) return null; }
        catch (Exception e) when (e is ArgumentException or IOException) { return null; }
        return full;
    }

    public static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; } // absent
    }

    /// <summary>An existing plain folder: acceptable path, exists, not a reparse point.</summary>
    public static string? ExistingFolder(string? path)
    {
        string? full = PlainAbsolute(path);
        return full is not null && Directory.Exists(full) && !IsReparsePoint(full) ? full : null;
    }

    /// <summary>True when <paramref name="inner"/> is <paramref name="outer"/> or below it (both canonical).</summary>
    public static bool IsUnder(string inner, string outer)
    {
        string o = outer.TrimEnd('\\') + '\\';
        return (inner.TrimEnd('\\') + '\\').StartsWith(o, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The install folder the helper may replace files in: an existing plain folder with a regular susu.exe in it, never a drive root or a Windows folder, and never overlapping the
    /// data or updates folder (the backup would copy itself). Returns the canonical path or null.
    /// </summary>
    public static string? InstallFolder(string? path, string updatesFolder, string mainExecutable = "susu.exe")
    {
        string? full = ExistingFolder(path);
        if (full is null || full.Length <= 3) return null;
        string exe = Path.Combine(full, mainExecutable);
        if (!File.Exists(exe) || IsReparsePoint(exe)) return null;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0 && IsUnder(full, windows)) return null;
        string updates = Path.GetFullPath(updatesFolder);
        if (IsUnder(full, updates) || IsUnder(updates, full)) return null;
        return full;
    }
}
