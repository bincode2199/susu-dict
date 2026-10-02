using System.Security.Cryptography;
using Susu.Abstractions;

namespace Susu.Storage;

public sealed record VocabExportOptions(VocabExportFormat Format, VocabContent Content = VocabContent.All, bool OnlyNew = false, string DeckName = VocabFormats.DefaultDeck);

/// <summary>Outcome of an export. <see cref="Path"/> is set only when a file was written. <see cref="Skipped"/> counts entries left out by "only new".</summary>
public sealed record VocabExportResult(bool Ok, string? Error, bool Retryable, string? Path, string? ExportId, int Exported, int Skipped, string? FileHash, IReadOnlyList<string> Issues);

public static class VocabExportErrors
{
    public const string NothingToExport = "export.nothingToExport";
    public const string PathInvalid = "export.pathInvalid";
    public const string DiskFull = "export.diskFull";
    public const string WriteFailed = "export.writeFailed";
    public const string Cancelled = "export.cancelled";
}

/// <summary>What start-up recovery did with one export that was interrupted.</summary>
public sealed record VocabExportRecovery(string ExportId, string? Path, bool Recovered);

/// <summary>
/// Exports favorites to a file (F15.2, ARCHITECTURE 8.3, DATA07/DATA08). One frozen snapshot (a single <see cref="IFavorites.List"/>) feeds
/// the chosen generator. The record is written first as Pending together with its (entry, revision) items and the file hash; the file goes
/// to a flushed temp file next to the target and is moved over it; only then the record becomes Succeeded. A crash between the move and the
/// DB commit leaves a Pending row; <see cref="Recover"/> finds the file by its hash and marks it Succeeded instead of exporting a second
/// copy, or marks the row Failed when the file is not there. "Only new" is per format and compares (entry, revision) with Succeeded exports.
/// </summary>
public sealed class VocabExporter(Database db, IFavorites favorites, IClock clock, IFaultPoint? faults = null, Func<string, Stream>? createTemp = null, Func<string>? newId = null)
{
    private readonly Func<string> id = newId ?? (() => Guid.NewGuid().ToString("N"));
    private readonly object gate = new();

    public VocabExportResult Export(string path, VocabExportOptions options, CancellationToken cancellationToken = default)
    {
        lock (gate) return ExportCore(path, options, cancellationToken);
    }

    private VocabExportResult ExportCore(string path, VocabExportOptions options, CancellationToken ct)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return Fail(VocabExportErrors.PathInvalid); }
        string? dir = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || Directory.Exists(full)) return Fail(VocabExportErrors.PathInvalid);

        var snapshot = favorites.List(includeDeleted: false)
            .Where(e => !e.Deleted)
            .OrderBy(e => e.DisplayText, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.EntryId, StringComparer.Ordinal).ToList();
        int skipped = 0;
        if (options.OnlyNew)
        {
            var done = ExportedItems(options.Format);
            int before = snapshot.Count;
            snapshot = snapshot.Where(e => !done.Contains((e.EntryId, e.Revision))).ToList();
            skipped = before - snapshot.Count;
        }
        if (snapshot.Count == 0) return new VocabExportResult(false, VocabExportErrors.NothingToExport, false, null, null, 0, skipped, null, []);
        if (ct.IsCancellationRequested) return Fail(VocabExportErrors.Cancelled);

        var issues = new List<string>();
        var cards = snapshot.Select(e => VocabFormats.Parse(e, options.Content, issues)).ToList();
        byte[] bytes = options.Format switch
        {
            VocabExportFormat.EudicTxt => VocabFormats.EudicTxt(cards),
            VocabExportFormat.Csv => VocabFormats.Csv(cards, options.Content),
            _ => VocabFormats.Apkg(cards, options.DeckName, clock.UtcNow),
        };
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string exportId = id();
        long now = clock.UtcNow.ToUnixTimeMilliseconds();

        db.Write(w =>
        {
            w.Exec("INSERT INTO vocab_exports(export_id, format, file_hash, created_at, outcome, path) VALUES ($i, $f, $h, $t, 'Pending', $p);",
                ("$i", exportId), ("$f", options.Format.ToString()), ("$h", hash), ("$t", now), ("$p", full));
            foreach (var e in snapshot)
                w.Exec("INSERT INTO vocab_export_items(export_id, entry_id, entry_revision) VALUES ($i, $e, $r);", ("$i", exportId), ("$e", e.EntryId), ("$r", e.Revision));
            return true;
        });
        faults?.Hit("export:recorded");

        string temp = $"{full}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = createTemp?.Invoke(temp) ?? new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush();
                if (stream is FileStream fs) fs.Flush(flushToDisk: true);
            }
            faults?.Hit("export:temp-written");
            File.Move(temp, full, overwrite: true);
            faults?.Hit("export:moved");
            Finish(exportId, "Succeeded");
            return new VocabExportResult(true, null, false, full, exportId, snapshot.Count, skipped, hash, issues);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            bool diskFull = e is IOException io && (io.HResult & 0xFFFF) is 0x27 or 0x70;
            try { Finish(exportId, "Failed"); } catch (Exception) { /* the Pending row is cleaned at the next start-up */ }
            return new VocabExportResult(false, diskFull ? VocabExportErrors.DiskFull : VocabExportErrors.WriteFailed, diskFull, null, exportId, 0, skipped, hash, issues);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Succeeded sets the outcome; Failed also drops the items so a failed attempt marks nothing as exported.</summary>
    private void Finish(string exportId, string outcome) => db.Write(w =>
    {
        w.Exec("UPDATE vocab_exports SET outcome=$o WHERE export_id=$i AND outcome='Pending';", ("$o", outcome), ("$i", exportId));
        if (outcome != "Succeeded") w.Exec("DELETE FROM vocab_export_items WHERE export_id=$i;", ("$i", exportId));
        return true;
    });

    private HashSet<(string, long)> ExportedItems(VocabExportFormat format) => db.Read(c =>
    {
        var set = new HashSet<(string, long)>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT i.entry_id, i.entry_revision FROM vocab_export_items i JOIN vocab_exports e ON e.export_id=i.export_id WHERE e.format=$f AND e.outcome='Succeeded';";
        cmd.Parameters.AddWithValue("$f", format.ToString());
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add((r.GetString(0), r.GetInt64(1)));
        return set;
    });

    /// <summary>(entry, revision) pairs of one export, for tests and the later queue projection.</summary>
    public IReadOnlyList<(string EntryId, long Revision)> Items(string exportId) => db.Read(c =>
    {
        var list = new List<(string, long)>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT entry_id, entry_revision FROM vocab_export_items WHERE export_id=$i ORDER BY entry_id;";
        cmd.Parameters.AddWithValue("$i", exportId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt64(1)));
        return list;
    });

    public string? Outcome(string exportId) => db.Read(c => Database.Scalar(c, null, "SELECT outcome FROM vocab_exports WHERE export_id=$i;", ("$i", exportId)) as string);

    /// <summary>
    /// At start-up: each Pending export is resolved. When the target file exists and has the recorded hash, the export is marked Succeeded
    /// (no second copy is made); otherwise it is marked Failed, its items are dropped and its leftover temp files are deleted.
    /// </summary>
    public IReadOnlyList<VocabExportRecovery> Recover()
    {
        var pending = db.Read(c =>
        {
            var list = new List<(string Id, string? Path, string Hash)>();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT export_id, path, file_hash FROM vocab_exports WHERE outcome='Pending' ORDER BY created_at;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2)));
            return list;
        });
        var result = new List<VocabExportRecovery>();
        foreach (var (exportId, path, hash) in pending)
        {
            bool ok = false;
            if (path is not null)
            {
                try { ok = File.Exists(path) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == hash; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ok = false; }
                CleanTemps(path);
            }
            Finish(exportId, ok ? "Succeeded" : "Failed");
            result.Add(new VocabExportRecovery(exportId, path, ok));
        }
        return result;
    }

    private static void CleanTemps(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir is null || !Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, Path.GetFileName(path) + ".*.tmp")) File.Delete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static VocabExportResult Fail(string code) => new(false, code, false, null, null, 0, 0, null, []);
}

/// <summary>F15.4: the SetVocab "export now" over <see cref="VocabExporter"/>: maps the page's format and content choices to the F15.2 options.</summary>
public sealed class VocabFileExporter(VocabExporter exporter) : IVocabFileExporter
{
    public VocabExportOutcome Export(VocabExportRequest request, CancellationToken cancellationToken)
    {
        var format = request.Format switch { "txt" => VocabExportFormat.EudicTxt, "csv" => VocabExportFormat.Csv, "apkg" => VocabExportFormat.Apkg, _ => (VocabExportFormat?)null };
        if (format is null) return new VocabExportOutcome(false, VocabExportErrors.PathInvalid, false, null, 0, 0, []);
        var content = (request.Definitions ? VocabContent.Definitions : 0) | (request.Phonetics ? VocabContent.Phonetics : 0) | (request.Examples ? VocabContent.Examples : 0);
        string deck = string.IsNullOrWhiteSpace(request.Deck) ? VocabFormats.DefaultDeck : request.Deck.Trim();
        var r = exporter.Export(request.Path, new VocabExportOptions(format.Value, content, request.OnlyNew, deck), cancellationToken);
        return new VocabExportOutcome(r.Ok, r.Error, r.Retryable, r.Ok ? r.Path : null, r.Exported, r.Skipped, r.Issues);
    }
}
