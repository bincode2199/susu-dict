using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Susu.Abstractions;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>F15.2 (DATA07/DATA08 file side): three generators, one snapshot, file transaction, recovery record.</summary>
public sealed class VocabExportTests : IDisposable
{
    private static VocabExportResult Run(VocabExporter e, string path, VocabExportOptions o) => e.Export(path, o, TestContext.Current.CancellationToken);
    private readonly TempRoot root = new();
    private readonly ManualClock clock = new();
    private int ids;

    private (Database Db, FavoritesRepository Fav, VocabExporter Exp) Open(IFaultPoint? faults = null, Func<string, Stream>? temp = null)
    {
        var db = Database.Open(root.Paths.Database);
        var fav = new FavoritesRepository(db, clock, null, () => $"e{++ids}");
        return (db, fav, new VocabExporter(db, fav, clock, faults, temp, () => $"x{++ids}"));
    }

    private string Out(string name) => Path.Combine(root.Root, name);
    public void Dispose() => root.Dispose();

    private static string Content(string mean = "fruit", string ex = "An apple a day") =>
        JsonSerializer.Serialize(new { source = "Test", phonetics = new[] { new { accent = "US", ipa = "ˈæpl" } }, meanings = new[] { new { pos = "n.", means = new[] { mean } } }, examples = new[] { new { src = ex, dst = "每天一苹果" } } });

    private static void Add(FavoritesRepository f, string word, string? content = null) => f.Favorite(new FavoriteCard("en", word, content ?? Content()), []);

    [Fact]
    public void Eudic_txt_is_one_word_per_line_deduped_without_bom()
    {
        var (db, fav, exp) = Open();
        using (db)
        {
            Add(fav, "beta"); Add(fav, "Alpha"); Add(fav, "multi\r\nline");
            var r = Run(exp, Out("w.txt"), new VocabExportOptions(VocabExportFormat.EudicTxt));
            Assert.True(r.Ok);
            byte[] b = File.ReadAllBytes(Out("w.txt"));
            Assert.NotEqual(0xEF, b[0]);
            Assert.Equal("Alpha\nbeta\nmulti line\n", Encoding.UTF8.GetString(b));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(b)), r.FileHash);
        }
    }

    [Fact]
    public void Csv_quotes_per_rfc4180_has_bom_and_blocks_formula_injection()
    {
        Assert.Equal("a", VocabFormats.CsvCell("a"));
        Assert.Equal("\"a,b\"", VocabFormats.CsvCell("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", VocabFormats.CsvCell("say \"hi\""));
        Assert.Equal("\"l1\nl2\"", VocabFormats.CsvCell("l1\nl2"));
        Assert.Equal("\" pad\"", VocabFormats.CsvCell(" pad"));
        foreach (string lead in new[] { "=", "+", "-", "@" }) Assert.Equal("'" + lead + "1+1", VocabFormats.CsvCell(lead + "1+1"));

        var (db, fav, exp) = Open();
        using (db)
        {
            Add(fav, "=cmd|' /C calc'!A0", Content("a, \"b\"\nc"));
            Add(fav, "مرحبا", Content("שלום"));
            Add(fav, "😀 emoji", Content());
            var r = Run(exp, Out("w.csv"), new VocabExportOptions(VocabExportFormat.Csv));
            Assert.True(r.Ok);
            byte[] b = File.ReadAllBytes(Out("w.csv"));
            Assert.Equal([0xEF, 0xBB, 0xBF], b[..3]);
            string text = Encoding.UTF8.GetString(b, 3, b.Length - 3);
            Assert.StartsWith("Word,Language,Phonetic,Definition,Example,Source\r\n", text);
            Assert.DoesNotContain("\r\n=", text);
            Assert.Contains("مرحبا", text);
            Assert.Contains("😀 emoji", text);
        }
    }

    [Fact]
    public void Csv_content_choice_drops_columns()
    {
        var cards = new[] { new VocabCardText("e", "w", "en", "S", "p", "d", "x") };
        string t = Encoding.UTF8.GetString(VocabFormats.Csv(cards, VocabContent.Definitions)).TrimStart('﻿');
        Assert.StartsWith("Word,Language,Definition,Source\r\nw,en,d,S\r\n", t);
    }

    // ---- apkg ------------------------------------------------------------------------------------------------------------------

    private static (SqliteConnection Conn, string Path) OpenApkg(string apkg)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "susu-tests", "apkg-" + Guid.NewGuid().ToString("N") + ".anki2");
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        using (var zip = ZipFile.OpenRead(apkg))
        {
            Assert.Equal(["collection.anki2", "media"], zip.Entries.Select(e => e.FullName).OrderBy(x => x).ToArray());
            zip.GetEntry("collection.anki2")!.ExtractToFile(tmp);
            using var media = new StreamReader(zip.GetEntry("media")!.Open());
            Assert.Equal("{}", media.ReadToEnd());
        }
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        return (c, tmp);
    }

    private static object? Scalar(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar(); }

    [Fact]
    public void Apkg_reopens_with_valid_col_notes_cards_checksums_and_sort_field()
    {
        var (db, fav, exp) = Open();
        using (db)
        {
            Add(fav, "apple"); Add(fav, "Banana <b>&"); Add(fav, "😀");
            var r = Run(exp, Out("w.apkg"), new VocabExportOptions(VocabExportFormat.Apkg, DeckName: "My::Deck"));
            Assert.True(r.Ok);
            var (c, tmp) = OpenApkg(Out("w.apkg"));
            using (c)
            {
                Assert.Equal("ok", Scalar(c, "PRAGMA integrity_check;"));
                Assert.Equal(1L, Scalar(c, "SELECT count(*) FROM col;"));
                Assert.Equal(11L, Scalar(c, "SELECT ver FROM col;"));
                Assert.Equal(3L, Scalar(c, "SELECT count(*) FROM notes;"));
                Assert.Equal(3L, Scalar(c, "SELECT count(*) FROM cards WHERE nid IN (SELECT id FROM notes);"));
                var models = JsonDocument.Parse((string)Scalar(c, "SELECT models FROM col;")!).RootElement;
                var model = models.GetProperty(VocabFormats.ModelId.ToString());
                Assert.Equal(5, model.GetProperty("flds").GetArrayLength());
                Assert.Equal(VocabFormats.ModelId, (long)Scalar(c, "SELECT DISTINCT mid FROM notes;")!);
                var decks = JsonDocument.Parse((string)Scalar(c, "SELECT decks FROM col;")!).RootElement;
                Assert.Contains(decks.EnumerateObject(), d => d.Value.GetProperty("name").GetString() == "My::Deck");
                long did = (long)Scalar(c, "SELECT DISTINCT did FROM cards;")!;
                Assert.True(decks.TryGetProperty(did.ToString(), out _));
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT guid, flds, sfld, csum FROM notes ORDER BY sfld;";
                using var rd = cmd.ExecuteReader();
                var rows = new List<(string Guid, string Flds, string Sfld, long Csum)>();
                while (rd.Read()) rows.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetInt64(3)));
                Assert.Equal(3, rows.Count);
                foreach (var row in rows)
                {
                    Assert.Equal(5, row.Flds.Split('\u001f').Length);
                    Assert.Equal(row.Sfld.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"), row.Flds.Split('\u001f')[0]);
                    Assert.Equal(Convert.ToInt64(Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(row.Sfld)))[..8], 16), row.Csum);
                    Assert.Equal(12, row.Guid.Length);
                }
                Assert.Contains(rows, x => x.Sfld == "Banana <b>&" && x.Flds.StartsWith("Banana &lt;b&gt;&amp;\u001f"));
            }
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }

    [Fact]
    public void Apkg_guids_are_stable_so_a_re_export_updates_instead_of_duplicating()
    {
        var (db, fav, exp) = Open();
        using (db)
        {
            Add(fav, "apple");
            Assert.True(Run(exp, Out("a1.apkg"), new VocabExportOptions(VocabExportFormat.Apkg)).Ok);
            Add(fav, "apple", Content("changed meaning"));
            clock.Advance(TimeSpan.FromHours(1));
            Assert.True(Run(exp, Out("a2.apkg"), new VocabExportOptions(VocabExportFormat.Apkg)).Ok);
            string G(string f)
            {
                var (c, tmp) = OpenApkg(f);
                using (c) { var g = (string)Scalar(c, "SELECT guid FROM notes;")!; c.Close(); SqliteConnection.ClearAllPools(); try { File.Delete(tmp); } catch (IOException) { } return g; }
            }
            Assert.Equal(G(Out("a1.apkg")), G(Out("a2.apkg")));
            Assert.Equal(VocabFormats.NoteGuid("e1"), G(Out("a1.apkg")));
        }
    }

    // ---- revision and "only new" ----------------------------------------------------------------------------------------------

    [Fact]
    public void Only_new_skips_unchanged_entries_and_exports_changed_revisions_per_format()
    {
        var (db, fav, exp) = Open();
        using (db)
        {
            Add(fav, "apple"); Add(fav, "pear");
            var first = Run(exp, Out("n1.csv"), new VocabExportOptions(VocabExportFormat.Csv, OnlyNew: true));
            Assert.Equal((2, 0), (first.Exported, first.Skipped));
            Assert.Equal(2, exp.Items(first.ExportId!).Count);
            Assert.Equal(VocabExportErrors.NothingToExport, Run(exp, Out("n2.csv"), new VocabExportOptions(VocabExportFormat.Csv, OnlyNew: true)).Error);
            Assert.False(File.Exists(Out("n2.csv")));
            Add(fav, "pear", Content("changed")); // revision 2
            var third = Run(exp, Out("n3.csv"), new VocabExportOptions(VocabExportFormat.Csv, OnlyNew: true));
            Assert.Equal((1, 1), (third.Exported, third.Skipped));
            Assert.Contains(exp.Items(third.ExportId!), i => i.Revision == 2);
            // another format has its own history
            Assert.Equal(2, Run(exp, Out("n4.txt"), new VocabExportOptions(VocabExportFormat.EudicTxt, OnlyNew: true)).Exported);
            // not "only new": repeated export records another snapshot
            Assert.True(Run(exp, Out("n5.csv"), new VocabExportOptions(VocabExportFormat.Csv)).Ok);
        }
    }

    [Fact]
    public void Deleted_entries_are_not_exported()
    {
        var (db, fav, exp) = Open();
        using (db)
        {
            Add(fav, "apple"); Add(fav, "pear");
            fav.Unfavorite("en", "pear");
            Assert.Equal("apple\n", Encoding.UTF8.GetString(File.ReadAllBytes(WriteTxt(exp))));
        }
    }

    private string WriteTxt(VocabExporter exp) { Run(exp, Out("d.txt"), new VocabExportOptions(VocabExportFormat.EudicTxt)); return Out("d.txt"); }

    // ---- adversarial, size, failures ---------------------------------------------------------------------------------------------

    [Fact]
    public void Adversarial_text_survives_all_three_formats()
    {
        var (db, fav, exp) = Open();
        using (db)
        {
            string huge = new('x', 100_000);
            string lone = "bad\uD800pair";
            Add(fav, "q\"uote, comma", Content("line1\r\nline2\u0000\u0007 \"q\"", huge));
            Add(fav, "rtl ‮evil", Content(lone));
            Add(fav, "👩‍👩‍👧 family", "{}");
            foreach (var fmt in Enum.GetValues<VocabExportFormat>())
            {
                var r = Run(exp, Out("adv." + fmt), new VocabExportOptions(fmt));
                Assert.True(r.Ok, fmt.ToString());
                Assert.Equal(3, r.Exported);
                Assert.Contains(r.Issues, i => i.Contains("example truncated"));
            }
            string csv = Encoding.UTF8.GetString(File.ReadAllBytes(Out("adv.Csv")));
            Assert.False(csv.Contains('\0') || csv.Contains('\u0007'));
            Assert.Contains("�", csv);
            Assert.Contains("\"line1\nline2 \"\"q\"\"\"", csv.Replace("n. line1", "line1").Replace("\r\nline2", "\nline2"));
            Assert.DoesNotContain(new string('x', VocabFormats.MaxFieldChars + 1), csv);
            Assert.Contains(Run(exp, Out("adv2.csv"), new VocabExportOptions(VocabExportFormat.Csv)).Issues, i => i.Contains("example truncated"));
            var (c, tmp) = OpenApkg(Out("adv.Apkg"));
            using (c) Assert.Equal(3L, Scalar(c, "SELECT count(*) FROM notes;"));
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }

    [Fact]
    public void Five_thousand_entries_export_quickly_in_every_format()
    {
        var (db, fav, exp) = Open();
        using (db)
        {
            for (int i = 0; i < 5000; i++) Add(fav, $"word{i:0000}", Content($"meaning {i}", $"example {i}"));
            foreach (var fmt in Enum.GetValues<VocabExportFormat>())
            {
                var sw = Stopwatch.StartNew();
                var r = Run(exp, Out("big." + fmt), new VocabExportOptions(fmt));
                Assert.True(r.Ok);
                Assert.Equal(5000, r.Exported);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"{fmt} took {sw.Elapsed}");
            }
            var (c, tmp) = OpenApkg(Out("big.Apkg"));
            using (c) { Assert.Equal(5000L, Scalar(c, "SELECT count(*) FROM notes;")); Assert.Equal(5000L, Scalar(c, "SELECT count(DISTINCT id) FROM notes;")); }
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }

    private sealed class FullDiskStream : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => 0; public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => 0;
        public override long Seek(long o, SeekOrigin s) => 0;
        public override void SetLength(long v) { }
        public override void Write(byte[] b, int o, int c) => throw new IOException("disk full", unchecked((int)0x80070070));
    }

    [Fact]
    public void Disk_full_leaves_no_file_keeps_old_target_marks_nothing_exported_and_is_retryable()
    {
        bool full = true;
        var (db, fav, exp) = Open(temp: p =>
        {
            if (!full) return new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            File.WriteAllBytes(p, [1]); // a partial temp must be cleaned up
            return new FullDiskStream();
        });
        using (db)
        {
            Add(fav, "apple");
            File.WriteAllText(Out("t.csv"), "old");
            var r = Run(exp, Out("t.csv"), new VocabExportOptions(VocabExportFormat.Csv, OnlyNew: true));
            Assert.False(r.Ok);
            Assert.Equal(VocabExportErrors.DiskFull, r.Error);
            Assert.True(r.Retryable);
            Assert.Equal("old", File.ReadAllText(Out("t.csv")));
            Assert.Empty(Directory.GetFiles(root.Root, "*.tmp"));
            Assert.Equal("Failed", exp.Outcome(r.ExportId!));
            Assert.Empty(exp.Items(r.ExportId!));
            full = false;
            var again = Run(exp, Out("t.csv"), new VocabExportOptions(VocabExportFormat.Csv, OnlyNew: true));
            Assert.True(again.Ok); // the failed attempt did not mark the entry as exported
            Assert.Equal(1, again.Exported);
        }
    }

    [Fact]
    public void Invalid_target_path_writes_nothing_and_records_nothing()
    {
        var (db, fav, exp) = Open();
        using (db)
        {
            Add(fav, "apple");
            Assert.Equal(VocabExportErrors.PathInvalid, Run(exp, Path.Combine(root.Root, "nodir", "x.csv"), new VocabExportOptions(VocabExportFormat.Csv)).Error);
            Assert.Equal(VocabExportErrors.PathInvalid, Run(exp, root.Root, new VocabExportOptions(VocabExportFormat.Csv)).Error);
            Assert.Equal(0L, db.Read(c => Scalar(c, "SELECT count(*) FROM vocab_exports;")));
        }
    }

    // ---- crash recovery (DATA07) ------------------------------------------------------------------------------------------------

    [Fact]
    public void Crash_after_rename_before_db_commit_is_recovered_by_hash_without_a_second_copy()
    {
        string target = Out("crash.csv");
        string exportId;
        var (db, fav, exp) = Open(new FaultAt("export:moved"));
        using (db)
        {
            Add(fav, "apple");
            Assert.Throws<SimulatedCrash>(() => Run(exp, target, new VocabExportOptions(VocabExportFormat.Csv)));
            Assert.True(File.Exists(target));
            exportId = db.Read(c => (string)Scalar(c, "SELECT export_id FROM vocab_exports;")!);
            Assert.Equal("Pending", exp.Outcome(exportId));
            Assert.Single(exp.Items(exportId));
        }
        byte[] before = File.ReadAllBytes(target);
        var (db2, _, exp2) = Open();
        using (db2)
        {
            var rec = Assert.Single(exp2.Recover());
            Assert.True(rec.Recovered);
            Assert.Equal("Succeeded", exp2.Outcome(exportId));
            Assert.Equal(before, File.ReadAllBytes(target));
            Assert.Equal(1L, db2.Read(c => Scalar(c, "SELECT count(*) FROM vocab_exports;")));
            Assert.Empty(exp2.Recover());
            // the recovered export now counts for "only new"
            Assert.Equal(VocabExportErrors.NothingToExport, Run(exp2, Out("again.csv"), new VocabExportOptions(VocabExportFormat.Csv, OnlyNew: true)).Error);
        }
    }

    [Fact]
    public void Crash_before_rename_is_reported_failed_cleans_temp_and_keeps_old_target()
    {
        string target = Out("crash2.csv");
        File.WriteAllText(target, "old");
        var (db, fav, exp) = Open(new FaultAt("export:temp-written"));
        using (db)
        {
            Add(fav, "apple");
            Assert.Throws<SimulatedCrash>(() => Run(exp, target, new VocabExportOptions(VocabExportFormat.Csv)));
            File.WriteAllText(target + ".abc.tmp", "partial"); // a real crash leaves the temp file behind; the in-process fault unwinds through finally
        }
        var (db2, _, exp2) = Open();
        using (db2)
        {
            var rec = Assert.Single(exp2.Recover());
            Assert.False(rec.Recovered);
            Assert.Empty(Directory.GetFiles(root.Root, "*.tmp"));
            Assert.Equal("old", File.ReadAllText(target));
            Assert.Equal(0L, db2.Read(c => Scalar(c, "SELECT count(*) FROM vocab_export_items;")));
            Assert.Equal(1, Run(exp2, Out("retry.csv"), new VocabExportOptions(VocabExportFormat.Csv, OnlyNew: true)).Exported);
        }
    }

    [Fact]
    public void Crash_right_after_the_record_is_written_without_a_file_is_reported_failed()
    {
        var (db, fav, exp) = Open(new FaultAt("export:recorded"));
        using (db)
        {
            Add(fav, "apple");
            Assert.Throws<SimulatedCrash>(() => Run(exp, Out("none.csv"), new VocabExportOptions(VocabExportFormat.Csv)));
        }
        var (db2, _, exp2) = Open();
        using (db2)
        {
            Assert.False(Assert.Single(exp2.Recover()).Recovered);
            Assert.False(File.Exists(Out("none.csv")));
        }
    }

    [Fact]
    public void Edits_after_the_snapshot_do_not_leak_into_the_export_and_stay_new()
    {
        var (db, fav, exp) = Open();
        using (db)
        {
            Add(fav, "apple");
            var r = Run(exp, Out("s.csv"), new VocabExportOptions(VocabExportFormat.Csv, OnlyNew: true));
            Assert.Equal(1L, Assert.Single(exp.Items(r.ExportId!)).Revision);
            Add(fav, "apple", Content("edited"));
            Assert.Equal(1, Run(exp, Out("s2.csv"), new VocabExportOptions(VocabExportFormat.Csv, OnlyNew: true)).Exported);
        }
    }
}
