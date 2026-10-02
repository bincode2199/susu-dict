using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Susu.Abstractions;

namespace Susu.Storage;

public enum VocabExportFormat { EudicTxt, Csv, Apkg }

/// <summary>The one "export content" choice shared by every format (PLAN 6.6): definitions, phonetics, examples.</summary>
[Flags]
public enum VocabContent { None = 0, Definitions = 1, Phonetics = 2, Examples = 4, All = 7 }

/// <summary>Text of one card after parsing the vocab projection (F09.3) and cleaning; every field is plain text, newlines are LF.</summary>
public sealed record VocabCardText(string EntryId, string Word, string Lang, string Source, string Phonetic, string Definition, string Example);

/// <summary>
/// Pure generators for the three export files (F15.2). They only see frozen <see cref="VocabEntrySnapshot"/> values, never the live rows.
/// Decisions (the specs name none of these): Eudic txt is a plain word list, one word per line, UTF-8 without BOM, LF; CSV is RFC 4180
/// with CRLF rows and a UTF-8 BOM so Excel detects the encoding; apkg is a legacy schema 11 collection.anki2 (read by every Anki 2.1.x
/// and newer desktop that imports .apkg), media-less.
/// </summary>
public static class VocabFormats
{
    /// <summary>Longest text kept per field (UTF-16 code units); the rest is cut on a code point boundary and reported.</summary>
    public const int MaxFieldChars = 20_000;
    public const string DefaultDeck = "Su-Su";
    private const char FieldSeparator = '\u001f';

    // ---- parsing ---------------------------------------------------------------------------------------------------------------

    public static VocabCardText Parse(VocabEntrySnapshot s, VocabContent content, List<string>? issues = null)
    {
        string source = "Su-Su", phonetic = "", definition = "", example = "";
        try
        {
            using var doc = JsonDocument.Parse(s.ContentJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (Str(root, "source") is { Length: > 0 } src) source = src;
                if (content.HasFlag(VocabContent.Phonetics) && root.TryGetProperty("phonetics", out var ph) && ph.ValueKind == JsonValueKind.Array)
                    phonetic = string.Join("  ", ph.EnumerateArray().Select(p =>
                    {
                        if (p.ValueKind == JsonValueKind.String) return p.GetString() ?? "";
                        string accent = Str(p, "accent"), ipa = Str(p, "ipa");
                        if (ipa.Length == 0) return "";
                        return (accent.Length > 0 ? accent + " " : "") + (ipa.StartsWith('/') || ipa.StartsWith('[') ? ipa : "/" + ipa + "/");
                    }).Where(x => x.Length > 0));
                if (content.HasFlag(VocabContent.Definitions) && root.TryGetProperty("meanings", out var mn) && mn.ValueKind == JsonValueKind.Array)
                    definition = string.Join("\n", mn.EnumerateArray().Select(m =>
                    {
                        if (m.ValueKind == JsonValueKind.String) return m.GetString() ?? "";
                        string pos = Str(m, "pos");
                        string means = m.TryGetProperty("means", out var arr) && arr.ValueKind == JsonValueKind.Array
                            ? string.Join("; ", arr.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").Where(x => x.Length > 0)) : "";
                        return means.Length == 0 ? "" : (pos.Length > 0 ? pos + " " : "") + means;
                    }).Where(x => x.Length > 0));
                if (content.HasFlag(VocabContent.Examples) && root.TryGetProperty("examples", out var ex) && ex.ValueKind == JsonValueKind.Array)
                    example = string.Join("\n", ex.EnumerateArray().Select(e =>
                    {
                        if (e.ValueKind == JsonValueKind.String) return e.GetString() ?? "";
                        string a = Str(e, "src"), b = Str(e, "dst");
                        return a.Length > 0 && b.Length > 0 ? a + " - " + b : a + b;
                    }).Where(x => x.Length > 0));
            }
            else issues?.Add($"{s.EntryId}: content is not an object");
        }
        catch (JsonException) { issues?.Add($"{s.EntryId}: content is not valid JSON"); }

        string Cap(string v, string name)
        {
            string t = Sanitize(v, multiline: true);
            if (t.Length <= MaxFieldChars) return t;
            int cut = MaxFieldChars;
            if (char.IsHighSurrogate(t[cut - 1])) cut--;
            issues?.Add($"{s.EntryId}: {name} truncated");
            return t[..cut];
        }
        return new VocabCardText(s.EntryId, Sanitize(s.DisplayText, multiline: false), s.Lang, Cap(source, "source"), Cap(phonetic, "phonetic"), Cap(definition, "definition"), Cap(example, "example"));
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>LF line ends, no control characters or BOM, lone surrogates become U+FFFD, lines and the whole text trimmed. Single-line text joins lines with a space.</summary>
    public static string Sanitize(string? text, bool multiline)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { sb.Append(c).Append(text[++i]); continue; }
            if (char.IsSurrogate(c)) { sb.Append('\uFFFD'); continue; }
            if (c == '\r') { sb.Append('\n'); if (i + 1 < text.Length && text[i + 1] == '\n') i++; continue; }
            if (c is '\n' or '\u2028' or '\u2029' or '\u0085' or '\v' or '\f') { sb.Append('\n'); continue; }
            if (c == '\t') { sb.Append(' '); continue; }
            if (char.IsControl(c) || c == '\uFEFF') continue;
            sb.Append(c);
        }
        var lines = sb.ToString().Split('\n').Select(l => l.Trim()).ToList();
        if (!multiline) return string.Join(" ", lines.Where(l => l.Length > 0));
        return string.Join("\n", lines).Trim('\n');
    }

    // ---- Eudic txt ------------------------------------------------------------------------------------------------------------

    /// <summary>One word per line; duplicates (ordinal, ignoring case) are written once.</summary>
    public static byte[] EudicTxt(IReadOnlyList<VocabCardText> cards)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var c in cards)
            if (c.Word.Length > 0 && seen.Add(c.Word)) sb.Append(c.Word).Append('\n');
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    // ---- CSV ------------------------------------------------------------------------------------------------------------------

    public static byte[] Csv(IReadOnlyList<VocabCardText> cards, VocabContent content)
    {
        var header = new List<string> { "Word", "Language" };
        if (content.HasFlag(VocabContent.Phonetics)) header.Add("Phonetic");
        if (content.HasFlag(VocabContent.Definitions)) header.Add("Definition");
        if (content.HasFlag(VocabContent.Examples)) header.Add("Example");
        header.Add("Source");
        var sb = new StringBuilder();
        sb.Append(string.Join(',', header)).Append("\r\n");
        foreach (var c in cards)
        {
            var row = new List<string> { CsvCell(c.Word), CsvCell(c.Lang) };
            if (content.HasFlag(VocabContent.Phonetics)) row.Add(CsvCell(c.Phonetic));
            if (content.HasFlag(VocabContent.Definitions)) row.Add(CsvCell(c.Definition));
            if (content.HasFlag(VocabContent.Examples)) row.Add(CsvCell(c.Example));
            row.Add(CsvCell(c.Source));
            sb.Append(string.Join(',', row)).Append("\r\n");
        }
        var utf8 = new UTF8Encoding(false);
        return [0xEF, 0xBB, 0xBF, .. utf8.GetBytes(sb.ToString())];
    }

    /// <summary>RFC 4180 quoting plus spreadsheet formula protection: a cell starting with = + - @ gets a leading apostrophe.</summary>
    public static string CsvCell(string value)
    {
        string v = value;
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@') v = "'" + v;
        bool quote = v.Length > 0 && (v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r') || v[0] == ' ' || v[^1] == ' ');
        return quote ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    // ---- Anki apkg ------------------------------------------------------------------------------------------------------------

    /// <summary>Stable note guid: derived from the entry id only, so exporting a changed entry again updates the note instead of adding another.</summary>
    public static string NoteGuid(string entryId)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes("susu-note:" + entryId));
        return Convert.ToBase64String(h, 0, 9).Replace('+', '-').Replace('/', '_');
    }

    private static long StableId(string key, long baseValue)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        long n = 0;
        for (int i = 0; i < 5; i++) n = (n << 8) | h[i];
        return baseValue + n % 1_000_000_000_000L;
    }

    public const long ModelId = 1_700_000_000_123L;
    private static readonly string[] FieldNames = ["Word", "Phonetic", "Definition", "Example", "Source"];

    private static string Html(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\n", "<br>");

    /// <summary>Anki's checksum: first 8 hex digits of SHA-1 of the sort field, as an integer.</summary>
    public static long Checksum(string sortField) => Convert.ToInt64(Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sortField)))[..8], 16);

    public static string CleanDeckName(string? name)
    {
        string n = Sanitize(name, multiline: false).Replace(FieldSeparator.ToString(), "").Replace("\"", "");
        return n.Length == 0 ? DefaultDeck : n.Length > 200 ? n[..200] : n;
    }

    public static byte[] Apkg(IReadOnlyList<VocabCardText> cards, string deckName, DateTimeOffset now)
    {
        string deck = CleanDeckName(deckName);
        long deckId = StableId("susu-deck:" + deck, 2_000_000_000_000L);
        long secs = now.ToUnixTimeSeconds(), ms = now.ToUnixTimeMilliseconds();
        string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"susu-apkg-{Guid.NewGuid():N}.anki2");
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = tmp, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
            using (var c = new SqliteConnection(cs))
            {
                c.Open();
                Database.Exec(c, "PRAGMA journal_mode=DELETE; PRAGMA synchronous=OFF;");
                Database.Exec(c, AnkiSchema);
                using var tx = c.BeginTransaction();
                Database.Exec(c, "INSERT INTO col VALUES (1, $crt, $mod, $scm, 11, 0, 0, 0, $conf, $models, $decks, $dconf, '{}');", tx,
                    ("$crt", secs - secs % 86400), ("$mod", ms), ("$scm", ms), ("$conf", ConfJson()), ("$models", ModelsJson(deckId, secs)), ("$decks", DecksJson(deck, deckId, secs)), ("$dconf", DconfJson()));
                var usedNotes = new HashSet<long>();
                int due = 0;
                foreach (var card in cards)
                {
                    long nid = StableId("susu-nid:" + card.EntryId, 1_000_000_000_000L);
                    while (!usedNotes.Add(nid)) nid++;
                    string[] fields = [card.Word, card.Phonetic, card.Definition, card.Example, card.Source];
                    string flds = string.Join(FieldSeparator, fields.Select(Html));
                    Database.Exec(c, "INSERT INTO notes VALUES ($id, $guid, $mid, $mod, -1, ' ', $flds, $sfld, $csum, 0, '');", tx,
                        ("$id", nid), ("$guid", NoteGuid(card.EntryId)), ("$mid", ModelId), ("$mod", secs), ("$flds", flds), ("$sfld", card.Word), ("$csum", Checksum(card.Word)));
                    Database.Exec(c, "INSERT INTO cards VALUES ($id, $nid, $did, 0, $mod, -1, 0, 0, $due, 0, 0, 0, 0, 0, 0, 0, 0, '');", tx,
                        ("$id", nid), ("$nid", nid), ("$did", deckId), ("$mod", secs), ("$due", ++due));
                }
                tx.Commit();
            }
            byte[] db = File.ReadAllBytes(tmp);
            using var ms2 = new MemoryStream();
            using (var zip = new ZipArchive(ms2, ZipArchiveMode.Create, leaveOpen: true))
            {
                Add(zip, "collection.anki2", db, now);
                Add(zip, "media", "{}"u8.ToArray(), now);
            }
            return ms2.ToArray();
        }
        finally
        {
            foreach (var f in new[] { tmp, tmp + "-journal", tmp + "-wal", tmp + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Add(ZipArchive zip, string name, byte[] data, DateTimeOffset now)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        e.LastWriteTime = now;
        using var s = e.Open();
        s.Write(data);
    }

    private const string AnkiSchema = """
        CREATE TABLE col (id integer primary key, crt integer not null, mod integer not null, scm integer not null, ver integer not null, dty integer not null, usn integer not null, ls integer not null, conf text not null, models text not null, decks text not null, dconf text not null, tags text not null);
        CREATE TABLE notes (id integer primary key, guid text not null, mid integer not null, mod integer not null, usn integer not null, tags text not null, flds text not null, sfld integer not null, csum integer not null, flags integer not null, data text not null);
        CREATE TABLE cards (id integer primary key, nid integer not null, did integer not null, ord integer not null, mod integer not null, usn integer not null, type integer not null, queue integer not null, due integer not null, ivl integer not null, factor integer not null, reps integer not null, lapses integer not null, left integer not null, odue integer not null, odid integer not null, flags integer not null, data text not null);
        CREATE TABLE revlog (id integer primary key, cid integer not null, usn integer not null, ease integer not null, ivl integer not null, lastIvl integer not null, factor integer not null, time integer not null, type integer not null);
        CREATE TABLE graves (usn integer not null, oid integer not null, type integer not null);
        CREATE INDEX ix_notes_usn ON notes (usn);
        CREATE INDEX ix_cards_usn ON cards (usn);
        CREATE INDEX ix_revlog_usn ON revlog (usn);
        CREATE INDEX ix_cards_nid ON cards (nid);
        CREATE INDEX ix_cards_sched ON cards (did, queue, due);
        CREATE INDEX ix_revlog_cid ON revlog (cid);
        CREATE INDEX ix_notes_csum ON notes (csum);
        """;

    private static string ConfJson() => new JsonObject
    {
        ["nextPos"] = 1, ["estTimes"] = true, ["activeDecks"] = new JsonArray(1), ["sortType"] = "noteFld", ["timeLim"] = 0, ["sortBackwards"] = false,
        ["addToCur"] = true, ["curDeck"] = 1, ["newBury"] = true, ["newSpread"] = 0, ["dueCounts"] = true, ["curModel"] = ModelId.ToString(System.Globalization.CultureInfo.InvariantCulture), ["collapseTime"] = 1200,
    }.ToJsonString();

    private static JsonObject DeckObject(long id, string name, long secs) => new()
    {
        ["id"] = id, ["name"] = name, ["mod"] = secs, ["usn"] = -1, ["lrnToday"] = new JsonArray(0, 0), ["revToday"] = new JsonArray(0, 0),
        ["newToday"] = new JsonArray(0, 0), ["timeToday"] = new JsonArray(0, 0), ["collapsed"] = false, ["browserCollapsed"] = false,
        ["desc"] = "", ["dyn"] = 0, ["conf"] = 1, ["extendNew"] = 10, ["extendRev"] = 50,
    };

    private static string DecksJson(string deck, long deckId, long secs) => new JsonObject
    {
        ["1"] = DeckObject(1, "Default", secs),
        [deckId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = DeckObject(deckId, deck, secs),
    }.ToJsonString();

    private static string DconfJson() => JsonNode.Parse("""
        {"1":{"id":1,"mod":0,"name":"Default","usn":0,"maxTaken":60,"autoplay":true,"timer":0,"replayq":true,
        "new":{"bury":true,"delays":[1,10],"initialFactor":2500,"ints":[1,4,7],"order":1,"perDay":20,"separate":true},
        "rev":{"bury":true,"ease4":1.3,"fuzz":0.05,"ivlFct":1,"maxIvl":36500,"minSpace":1,"perDay":100},
        "lapse":{"delays":[10],"leechAction":0,"leechFails":8,"minInt":1,"mult":0},"dyn":false}}
        """)!.ToJsonString();

    private static string ModelsJson(long deckId, long secs)
    {
        var flds = new JsonArray();
        for (int i = 0; i < FieldNames.Length; i++)
            flds.Add((JsonNode?)new JsonObject { ["name"] = FieldNames[i], ["ord"] = i, ["sticky"] = false, ["rtl"] = false, ["font"] = "Arial", ["size"] = 20, ["media"] = new JsonArray() });
        var model = new JsonObject
        {
            ["id"] = ModelId, ["name"] = "Su-Su Word", ["type"] = 0, ["mod"] = secs, ["usn"] = -1, ["sortf"] = 0, ["did"] = deckId,
            ["tmpls"] = new JsonArray(new JsonObject
            {
                ["name"] = "Card 1", ["ord"] = 0, ["qfmt"] = "<div class=\"w\">{{Word}}</div>{{#Phonetic}}<div class=\"p\">{{Phonetic}}</div>{{/Phonetic}}",
                ["afmt"] = "{{FrontSide}}<hr id=answer>{{Definition}}{{#Example}}<div class=\"e\">{{Example}}</div>{{/Example}}<div class=\"s\">{{Source}}</div>",
                ["bqfmt"] = "", ["bafmt"] = "", ["did"] = null, ["bfont"] = "", ["bsize"] = 0,
            }),
            ["flds"] = flds,
            ["css"] = ".card { font-family: arial; font-size: 20px; text-align: center; color: black; background-color: white; } .w { font-size: 32px; } .p, .s { color: gray; font-size: 16px; } .e { font-style: italic; }",
            ["latexPre"] = "\\documentclass[12pt]{article}\n\\special{papersize=3in,5in}\n\\usepackage[utf8]{inputenc}\n\\usepackage{amssymb,amsmath}\n\\pagestyle{empty}\n\\setlength{\\parindent}{0in}\n\\begin{document}\n",
            ["latexPost"] = "\\end{document}", ["latexsvg"] = false,
            ["req"] = new JsonArray(new JsonArray(0, "any", new JsonArray(0))), ["tags"] = new JsonArray(), ["vers"] = new JsonArray(),
        };
        return new JsonObject { [ModelId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = model }.ToJsonString();
    }
}
