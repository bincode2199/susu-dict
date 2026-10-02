using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Susu.Plugins.Install;

/// <summary>
/// Extracts a plugin package (.susuext, a zip) into a fresh directory only after every entry passed the checks (PLAN 4.8, TEST-PLAN UPD01):
/// no absolute or rooted names, no ".." segments, no backslashes, no ':' (drive letters, alternate data streams), no reserved device names
/// (also with an extension, "CON.txt"), no trailing dot or space, no names equal except for case, no symlink/reparse entries, no nested archives
/// (they are rejected, never extracted), at most 256 files, 4 MiB per file, 16 MiB total, and a bounded expansion ratio. Sizes written in the
/// zip header are not trusted: the copy itself is capped. Nothing is written when an entry fails the name checks; a failure while copying
/// deletes what was written. <see cref="SafePackage.Validate"/> re-checks the result on disk.
/// </summary>
public static partial class SafeUnzip
{
    public const int MaxEntries = 512; // directory entries count too
    public const long MaxZipBytes = 20 * 1024 * 1024;
    public const int MaxRatio = 100;
    public const int MaxPathChars = 200;

    private static readonly string[] DeviceNames =
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"];

    private static readonly string[] ArchiveExtensions = [".zip", ".susuext", ".7z", ".rar", ".gz", ".tgz", ".tar", ".bz2", ".xz", ".cab", ".jar", ".nupkg", ".msi", ".iso"];

    [GeneratedRegex(@"~\d")]
    private static partial Regex ShortNameTilde();

    /// <summary>Checks the entry names and sizes without writing anything. Empty list: safe to extract.</summary>
    public static IReadOnlyList<ManifestIssue> Inspect(ZipArchive zip, long compressedBytes)
    {
        var issues = new List<ManifestIssue>();
        if (zip.Entries.Count > MaxEntries) { issues.Add(new ManifestIssue("$", "too-many-files", $"{zip.Entries.Count} entries exceeds the {MaxEntries} limit")); return issues; }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int files = 0;
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName;
            bool directory = name.EndsWith('/') && entry.Length == 0;
            string shown = Shown(name);
            string key = directory ? name.TrimEnd('/') : name;
            string? problem = NameProblem(key);
            if (problem is not null) { issues.Add(new ManifestIssue(shown, problem, ProblemText(problem))); continue; }
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            { issues.Add(new ManifestIssue(shown, "link", "symbolic links and reparse points are rejected")); continue; }
            if (unixType is not (0 or 0x8000 or 0x4000))
            { issues.Add(new ManifestIssue(shown, "special-file", "only regular files are accepted")); continue; }
            if (!seen.Add(key)) { issues.Add(new ManifestIssue(shown, "case-duplicate", "path duplicates another entry (names are compared without case)")); continue; }
            if (directory) continue;
            files++;
            if (ArchiveExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            { issues.Add(new ManifestIssue(shown, "nested-archive", "an archive inside a package is rejected, never extracted")); continue; }
            if (entry.Length > SafePackage.MaxFileBytes) issues.Add(new ManifestIssue(shown, "too-large", $"{entry.Length} bytes exceeds the {SafePackage.MaxFileBytes} limit"));
            if (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxRatio && entry.Length > 64 * 1024)
                issues.Add(new ManifestIssue(shown, "ratio", "compression ratio is too high"));
            total += entry.Length;
        }
        if (files > SafePackage.MaxFiles) issues.Add(new ManifestIssue("$", "too-many-files", $"{files} files exceeds the {SafePackage.MaxFiles} limit"));
        if (total > SafePackage.MaxTotalBytes) issues.Add(new ManifestIssue("$", "too-large", $"total {total} bytes exceeds the {SafePackage.MaxTotalBytes} limit"));
        if (compressedBytes > 0 && total / Math.Max(1, compressedBytes) > MaxRatio && total > 256 * 1024) issues.Add(new ManifestIssue("$", "ratio", "compression ratio is too high"));
        return issues;
    }

    /// <summary>
    /// Opens the zip, inspects it and extracts into <paramref name="destination"/> (created here; must not exist). On any issue the directory
    /// is removed again and the issues are returned. Never throws for a bad archive.
    /// </summary>
    public static IReadOnlyList<ManifestIssue> Extract(string zipPath, string destination)
    {
        try
        {
            var info = new FileInfo(zipPath);
            if (!info.Exists) return [new ManifestIssue("$", "missing", "the package file does not exist")];
            if (info.Length > MaxZipBytes) return [new ManifestIssue("$", "too-large", $"the package is {info.Length} bytes; the limit is {MaxZipBytes}")];
            using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var issues = Inspect(zip, info.Length);
            if (issues.Count > 0) return issues;
            return ExtractChecked(zip, destination);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            TryDelete(destination);
            return [new ManifestIssue("$", "unreadable", e is InvalidDataException ? "the file is not a valid zip package" : "the package could not be read: " + e.GetType().Name)];
        }
    }

    private static IReadOnlyList<ManifestIssue> ExtractChecked(ZipArchive zip, string destination)
    {
        if (Directory.Exists(destination)) return [new ManifestIssue("$", "exists", "the staging directory already exists")];
        Directory.CreateDirectory(destination);
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long written = 0;
        try
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue; // directories appear implicitly with their files
                string target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { TryDelete(destination); return [new ManifestIssue(Shown(entry.FullName), "path-escape", "resolves outside the package")]; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[16 * 1024];
                long entryBytes = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    entryBytes += read;
                    written += read;
                    if (entryBytes > SafePackage.MaxFileBytes || written > SafePackage.MaxTotalBytes)
                    {
                        output.Dispose();
                        TryDelete(destination);
                        return [new ManifestIssue(Shown(entry.FullName), "too-large", "the file expands past the size limit (its header understated its size)")];
                    }
                    output.Write(buffer, 0, read);
                }
            }
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
        var onDisk = SafePackage.Validate(destination);
        if (onDisk.Count > 0) TryDelete(destination);
        return onDisk;
    }

    private static string? NameProblem(string name)
    {
        if (name.Length == 0) return "empty-name";
        if (name.Length > MaxPathChars) return "name-too-long";
        if (name.Contains('\\')) return "backslash";
        if (name.StartsWith('/') || (name.Length >= 2 && name[1] == ':')) return "absolute";
        if (name.Contains(':')) return "alternate-stream";
        foreach (char c in name) if (c < ' ' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') return "bad-character";
        foreach (string segment in name.Split('/'))
        {
            if (segment.Length == 0) return "empty-segment";
            if (segment == ".." || segment == ".") return "traversal";
            if (segment.EndsWith('.') || segment.EndsWith(' ')) return "trailing-dot";
            string stem = segment.Split('.')[0].TrimEnd(' ');
            if (DeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) return "device-name";
            if (segment.Length <= 12 && ShortNameTilde().IsMatch(segment)) return "short-name";
        }
        return null;
    }

    private static string ProblemText(string code) => code switch
    {
        "traversal" => "path traversal (. or ..) is rejected",
        "absolute" => "absolute and drive-qualified paths are rejected",
        "alternate-stream" => "alternate data streams (name:stream) are rejected",
        "device-name" => "reserved device name in path",
        "backslash" => "backslashes are not allowed in entry names",
        "trailing-dot" => "names ending in a dot or space are rejected",
        "short-name" => "8.3 short names are rejected",
        "name-too-long" => "the entry name is too long",
        "bad-character" => "the entry name has a character Windows does not allow",
        _ => "the entry name is not valid",
    };

    private static string Shown(string name)
    {
        var cleaned = new string([.. name.Where(c => c >= ' ')]);
        return cleaned.Length > 80 ? cleaned[..80] + "..." : cleaned;
    }

    internal static void TryDelete(string directory)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); return; }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }
}
