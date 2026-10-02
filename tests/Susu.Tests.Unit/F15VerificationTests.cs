using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// Independent F15 verification (testing agent), part 1: storage, export and the loopback rule. Gaps the coding agents' tests leave: exports
/// parsed back by code written here (RFC 4180 reader, zip + sqlite for apkg) with hostile text and formula cells, stable guids across
/// re-export and revision change, "only new" across revisions and formats, disk full and recovery (hash match and mismatch), crash between the
/// entry and outbox writes, unfavorite and re-favorite, Resolve both ways, the address rule for AnkiConnect (IPv6, 127.0.0.2, userinfo, DNS
/// names), secrets in serialized status, 5000-entry timing. The sandbox end-to-end half is in <c>F15SyncVerificationTests</c>.
/// Real Anki, Eudic, Excel and real dialogs: not executed.
/// </summary>
public sealed class F15VerificationTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempRoot root = new();
    private readonly ManualClock clock = new();
    private int ids;

    public void Dispose() => root.Dispose();
    private string Out(string name) => Path.Combine(root.Root, name);

    private (Database Db, FavoritesRepository Fav, VocabExporter Exp) Open(IFaultPoint? faults = null, Func<string, Stream>? temp = null)
    {
        var db = Database.Open(root.Paths.Database);
        var fav = new FavoritesRepository(db, clock, faults, () => $"e{++ids}");
        return (db, fav, new VocabExporter(db, fav, clock, faults, temp, () => $"x{++ids}"));
    }

    private static string Content(string mean = "fruit", string ex = "An apple a day", string ipa = "ipa1", string source = "Test") =>
        JsonSerializer.Serialize(new { source, phonetics = new[] { new { accent = "US", ipa } }, meanings = new[] { new { pos = "n.", means = new[] { mean } } }, examples = new[] { new { src = ex, dst = "dst" } } });

    private static FavoriteResult Add(FavoritesRepository f, string word, string? content = null, string lang = "en", params string[] targets) => f.Favorite(new FavoriteCard(lang, word, content ?? Content()), targets);

    private static VocabExportResult Run(VocabExporter e, string path, VocabExportFormat f, bool onlyNew = false, VocabContent c = VocabContent.All)
        => e.Export(path, new VocabExportOptions(f, c, onlyNew), Ct);

    // ---------------- independent readers ----------------

    /// <summary>RFC 4180 reader written for these tests: BOM tolerant, quoted fields with "" and embedded CR/LF, CRLF or LF rows.</summary>
    private static List<List<string>> ParseCsv(byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else quoted = false; }
                else cell.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"': quoted = true; any = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); any = true; break;
                case '\r': break;
                case '\n': row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = []; any = false; break;
                default: cell.Append(ch); any = true; break;
            }
        }
        Assert.False(quoted, "unterminated quote");
        if (any || cell.Length > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    private sealed record Apkg(long ColVer, Dictionary<string, JsonNode?> Models, Dictionary<string, JsonNode?> Decks, List<(long Id, string Guid, long Mid, string[] Fields, string Sfld, long Csum)> Notes, List<(long Id, long Nid, long Did)> Cards);

    private static Apkg ReadApkg(string path)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "susu-f15v-" + Guid.NewGuid().ToString("N") + ".anki2");
        try
        {
            using (var zip = ZipFile.OpenRead(path)) zip.GetEntry("collection.anki2")!.ExtractToFile(tmp);
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            using var q = c.CreateCommand();
            q.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", (string)q.ExecuteScalar()!);
            q.CommandText = "SELECT ver, models, decks FROM col;";
            long ver; string models, decks;
            using (var r = q.ExecuteReader()) { Assert.True(r.Read()); ver = r.GetInt64(0); models = r.GetString(1); decks = r.GetString(2); }
            var notes = new List<(long, string, long, string[], string, long)>();
            q.CommandText = "SELECT id, guid, mid, flds, sfld, csum FROM notes ORDER BY id;";
            using (var r = q.ExecuteReader()) while (r.Read()) notes.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3).Split('\u001f'), r.GetValue(4).ToString()!, r.GetInt64(5)));
            var cards = new List<(long, long, long)>();
            q.CommandText = "SELECT id, nid, did FROM cards ORDER BY id;";
            using (var r = q.ExecuteReader()) while (r.Read()) cards.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)));
            return new Apkg(ver, JsonNode.Parse(models)!.AsObject().ToDictionary(k => k.Key, k => k.Value), JsonNode.Parse(decks)!.AsObject().ToDictionary(k => k.Key, k => k.Value), notes, cards);
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(tmp); } catch (IOException) { } }
    }

    private static string Unhtml(string s) => s.Replace("<br>", "\n").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");

    private sealed class FullDiskStream : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => 0; public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => 0; public override long Seek(long o, SeekOrigin s) => 0; public override void SetLength(long v) { }
        public override void Write(byte[] b, int o, int c) => throw new IOException("disk full", unchecked((int)0x80070070));
    }

    // ================= address rule =================

    private static AppSettings WithAnki(string? baseUrl, bool enabled = true, bool useKey = false)
    {
        var s = BuiltInCatalog.Defaults();
        return s with
        {
            Services = [.. s.Services.Select(x => x.Instance == VocabCatalog.AnkiConnect && x.Capability == Capability.Vocab ? x with { Enabled = enabled } : x)],
            Instances = [.. s.Instances.Select(i =>
            {
                if (i.Id != VocabCatalog.AnkiConnect) return i;
                var cfg = new Dictionary<string, string>(i.Config);
                if (baseUrl is not null) cfg["baseUrl"] = baseUrl;
                if (useKey) cfg["useApiKey"] = "true";
                return i with { Config = cfg };
            })],
        };
    }

    [Theory] // only a literal loopback address is ever usable; whatever is usable must really call loopback
    [InlineData("http://127.0.0.1:8765", true)]
    [InlineData("http://[::1]:8765", true)]
    [InlineData("http://127.0.0.2:8765", true)]
    [InlineData("http://localhost:8765", true)]
    [InlineData("http://evil.example:8765", false)]
    [InlineData("http://127.0.0.1.evil.example:8765", false)]
    [InlineData("http://localhost.evil.example:8765", false)]
    [InlineData("http://localtest.me:8765", false)] // a public name that resolves to 127.0.0.1
    [InlineData("http://192.168.1.5:8765", false)]
    [InlineData("http://0.0.0.0:8765", false)]
    [InlineData("http://[2001:db8::1]:8765", false)]
    [InlineData("http://example.invalid", false)]
    [InlineData("https://api.frdic.com", false)]
    public void Anki_address_is_usable_only_when_it_is_loopback(string baseUrl, bool usable)
    {
        var state = VocabTargets.Evaluate(WithAnki(baseUrl), VocabCatalog.AnkiConnect, (_, _) => false)!;
        Assert.Equal(usable, state.Usable);
        Assert.Equal(usable, VocabTargets.Usable(WithAnki(baseUrl), (_, _) => false).Contains(VocabCatalog.AnkiConnect));
        if (!usable) Assert.Equal(VocabTargets.ReasonOriginNotLocal, state.ReasonKey);
        if (state.Usable) Assert.True(Uri.TryCreate(state.Origin, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "localhost"), state.Origin);
    }

    [Theory] // userinfo, backslash and port tricks: whatever happens, the reported origin must be loopback when usable and never name the evil host
    [InlineData("http://127.0.0.1@evil.example:8765")]
    [InlineData("http://127.0.0.1:8765@evil.example")]
    [InlineData("http://evil.example\\@127.0.0.1:8765")]
    [InlineData("http://user:pw@127.0.0.1:8765")]
    [InlineData("http://127.0.0.1:8765#@evil.example")]
    [InlineData("http://127.0.0.1%2eevil.example:8765")]
    [InlineData("http://[::ffff:8.8.8.8]:8765")]
    [InlineData("http://2130706433:8765")]
    [InlineData("ftp://127.0.0.1:8765")]
    [InlineData("127.0.0.1:8765")]
    public void Anki_address_tricks_never_reach_a_non_loopback_host(string baseUrl)
    {
        var state = VocabTargets.Evaluate(WithAnki(baseUrl), VocabCatalog.AnkiConnect, (_, _) => false)!;
        TestContext.Current.SendDiagnosticMessage($"F15 origin '{baseUrl}' -> usable={state.Usable} origin={state.Origin}");
        if (state.Origin.Contains("evil")) Assert.False(state.Usable); // the user's text names the evil host as the real host: it is called only if usable, and it is not
        if (state.Usable)
        {
            Assert.True(Uri.TryCreate(state.Origin, UriKind.Absolute, out var u), state.Origin);
            Assert.True(u!.IsLoopback || u.Host == "localhost", state.Origin);
            Assert.True(u.UserInfo.Length == 0, "userinfo must not survive: " + state.Origin);
        }
    }

    [Fact] // an address that is not loopback queues nothing: the favorite is local only
    public async Task Non_loopback_anki_address_queues_no_rows_and_sends_nothing()
    {
        var (db, fav, _) = Open();
        using (db)
        {
            var settings = WithAnki("http://evil.example:8765");
            int calls = 0;
            var target = new ScriptTarget(VocabCatalog.AnkiConnect, (_, _) => { calls++; return Task.FromResult<VocabSyncOutcome>(new VocabSyncOutcome.Applied("x")); });
            await using var service = new VocabService(fav, new VocabSyncWorker(fav, _ => target, clock), () => VocabTargets.Usable(settings, (_, _) => true), clock);
            var r = service.Favorite(new FavoriteCard("en", "apple", Content()));
            Assert.Empty(r.QueuedTargets);
            Assert.Empty(fav.Deliveries(r.EntryId));
            Assert.Empty(await service.SyncNowAsync(Ct));
            Assert.Equal(0, calls);
            // The service itself queues rows for an unusable target when asked (the shell refuses first); the sync pass must still never call it.
            service.QueueExisting(VocabCatalog.AnkiConnect);
            Assert.Empty(await service.SyncNowAsync(Ct));
            Assert.Equal(0, calls);
        }
    }

    [Fact] // SetVocab states without a key: Eudic needs it saved and granted; AnkiConnect does not unless asked
    public void Target_states_without_keys_give_the_right_reason()
    {
        var s = BuiltInCatalog.Defaults();
        s = s with { Services = [.. s.Services.Select(x => x.Capability == Capability.Vocab ? x with { Enabled = true } : x)] };
        var anki = VocabTargets.Evaluate(s, VocabCatalog.AnkiConnect, (_, _) => false)!;
        var eudic = VocabTargets.Evaluate(s, VocabCatalog.Eudic, (_, _) => false)!;
        Assert.True(anki.Usable); // no key needed by default
        Assert.False(eudic.Usable);
        Assert.Equal(VocabTargets.ReasonMissingKey, eudic.ReasonKey);
        var needKey = VocabTargets.Evaluate(WithAnki("http://127.0.0.1:8765", useKey: true), VocabCatalog.AnkiConnect, (_, _) => false)!;
        Assert.False(needKey.Usable);
        Assert.Equal(VocabTargets.ReasonMissingKey, needKey.ReasonKey);
        var off = VocabTargets.Evaluate(WithAnki("http://127.0.0.1:8765", enabled: false), VocabCatalog.AnkiConnect, (_, _) => true)!;
        Assert.Equal((false, false, VocabTargets.ReasonDisabled), (off.Enabled, off.Usable, off.ReasonKey));
        Assert.Null(VocabTargets.Evaluate(s, "not-a-vocab-instance", (_, _) => true));
    }

    // ================= scripted target =================

    private sealed class ScriptTarget(string id, Func<VocabSyncRequest, CancellationToken, Task<VocabSyncOutcome>> handler, bool lookup = false) : IVocabSyncTarget
    {
        public List<VocabSyncRequest> Requests { get; } = [];
        public string InstanceId => id;
        public bool SupportsLookup => lookup;
        public Task<VocabSyncOutcome> SendAsync(VocabSyncRequest r, CancellationToken ct) { lock (Requests) Requests.Add(r); return handler(r, ct); }
    }

    // ================= favorites store =================

    [Fact] // a crash after the entry write but before the outbox write leaves nothing; after restart the same favorite works and queues rows once
    public void Crash_between_entry_and_outbox_then_restart_and_favorite_again()
    {
        var (db, fav, _) = Open(new FaultAt("favorite:outbox"));
        using (db) Assert.Throws<SimulatedCrash>(() => Add(fav, "apple", null, "en", "ankiconnect", "eudic"));
        var (db2, fav2, _) = Open();
        using (db2)
        {
            Assert.Empty(fav2.List(includeDeleted: true));
            Assert.False(fav2.IsFavorite("en", "apple"));
            var r = Add(fav2, "Apple", null, "en", "ankiconnect", "eudic");
            Assert.True(r.Created);
            Assert.Equal(2, fav2.Deliveries(r.EntryId).Count);
            Assert.All(fav2.Deliveries(r.EntryId), d => Assert.Equal(DeliveryState.Pending, d.State));
            Assert.Equal(2, fav2.Deliveries(r.EntryId).Select(d => d.OperationId).Distinct().Count());
        }
    }

    [Fact] // same operationId across a restart and across Resolve(resend); a new revision gets a new one; unsent old revision is cancelled
    public void Operation_ids_are_stable_across_restart_and_change_with_the_revision()
    {
        string entry, op1;
        var (db, fav, _) = Open();
        using (db)
        {
            var r = Add(fav, "apple", null, "en", "t");
            entry = r.EntryId;
            op1 = fav.Deliveries(entry).Single().OperationId;
            Assert.NotNull(fav.Claim("t"));
            Assert.Equal(1, fav.RecoverInterrupted());
        }
        var (db2, fav2, _) = Open();
        using (db2)
        {
            var d = fav2.Deliveries(entry).Single();
            Assert.Equal((DeliveryState.Uncertain, op1), (d.State, d.OperationId)); // Uncertain survives the restart
            Assert.True(fav2.Resolve(entry, "t", 1, delivered: false));
            Assert.Equal(op1, fav2.Deliveries(entry).Single().OperationId);
            Assert.Equal(DeliveryState.Pending, fav2.Deliveries(entry).Single().State);
            var r2 = Add(fav2, "apple", Content("changed"), "en", "t"); // revision 2 while rev 1 is still unsent
            Assert.Equal((2L, true), (r2.Revision, r2.ContentChanged));
            var rows = fav2.Deliveries(entry).OrderBy(x => x.EntryRevision).ToList();
            Assert.Equal(DeliveryState.Cancelled, rows[0].State);
            Assert.Equal(DeliveryState.Pending, rows[1].State);
            Assert.NotEqual(op1, rows[1].OperationId);
            Assert.Equal(op1, rows[0].OperationId);
        }
    }

    [Fact] // unfavorite, re-favorite unchanged: the delivered row is not queued again; changed: new revision row
    public void Unfavorite_then_refavorite_does_not_duplicate_delivered_rows()
    {
        var (db, fav, _) = Open();
        using (db)
        {
            var r = Add(fav, "apple", null, "en", "t");
            Assert.NotNull(fav.Claim("t"));
            Assert.True(fav.Complete(r.EntryId, "t", 1, DeliveryState.Succeeded, "r1"));
            Assert.True(fav.Unfavorite("en", "APPLE"));
            Assert.False(fav.IsFavorite("en", "apple"));
            Assert.False(fav.Unfavorite("en", "apple")); // a second unfavorite is a no-op
            var again = Add(fav, "apple", null, "en", "t");
            Assert.True(again.Restored);
            Assert.Equal(r.EntryId, again.EntryId);
            Assert.Single(fav.Deliveries(r.EntryId));
            Assert.Null(fav.Claim("t")); // nothing to send; the word is already remote
            Assert.True(fav.Unfavorite("en", "apple"));
            var edited = Add(fav, "apple", Content("new"), "en", "t");
            Assert.Equal(2L, edited.Revision);
            Assert.Equal([DeliveryState.Succeeded, DeliveryState.Pending], fav.Deliveries(r.EntryId).OrderBy(d => d.EntryRevision).Select(d => d.State).ToArray());
        }
    }

    [Fact] // Resolve both ways, through the service, with a target that has no lookup: delivered=true never calls the target; false resends once with the same operationId
    public async Task Resolve_both_ways_never_resends_blindly()
    {
        var (db, fav, _) = Open();
        using (db)
        {
            int n = 0;
            var target = new ScriptTarget("t", (_, _) => Task.FromResult<VocabSyncOutcome>(Interlocked.Increment(ref n) == 1 ? new VocabSyncOutcome.Unknown("lost") : new VocabSyncOutcome.Applied("ok")));
            await using var service = new VocabService(fav, new VocabSyncWorker(fav, _ => target, clock), () => ["t"], clock);
            var a = service.Favorite(new FavoriteCard("en", "apple", Content()));
            var b = service.Favorite(new FavoriteCard("en", "pear", Content()));
            await service.SyncNowAsync(Ct);
            await service.SyncNowAsync(Ct);
            await service.SyncNowAsync(Ct);
            Assert.Equal(DeliveryState.Uncertain, fav.Deliveries(a.EntryId).Single().State);
            Assert.All(target.Requests, r => Assert.Equal(VocabAction.Upsert, r.Action)); // a lookup-less target is never asked to look up
            Assert.Equal(1, target.Requests.Count(r => r.EntryId == a.EntryId)); // never resent by a later pass

            Assert.True(service.Resolve(a.EntryId, "t", 1, delivered: true));
            Assert.Equal(DeliveryState.Succeeded, fav.Deliveries(a.EntryId).Single().State);
            await service.SyncNowAsync(Ct);
            Assert.Equal(1, target.Requests.Count(r => r.EntryId == a.EntryId));
            Assert.False(service.Resolve(a.EntryId, "t", 1, delivered: false)); // Succeeded is final: cannot be flipped to a resend
            Assert.Equal(DeliveryState.Succeeded, fav.Deliveries(b.EntryId).Single().State); // the second word went out normally

            // a fresh uncertain word marked as not delivered is resent once, with the same operation id
            n = 0;
            var c = service.Favorite(new FavoriteCard("en", "plum", Content()));
            await service.SyncNowAsync(Ct);
            Assert.Equal(DeliveryState.Uncertain, fav.Deliveries(c.EntryId).Single().State);
            Assert.True(service.Resolve(c.EntryId, "t", 1, delivered: false));
            await service.SyncNowAsync(Ct);
            var sent = target.Requests.Where(r => r.EntryId == c.EntryId).ToList();
            Assert.Equal(2, sent.Count);
            Assert.Equal(sent[0].OperationId, sent[1].OperationId);
            Assert.Equal(DeliveryState.Succeeded, fav.Deliveries(c.EntryId).Single().State);
        }
    }

    // ================= CSV / txt / apkg round trips =================

    private static readonly string[] Hostile =
    [
        "=cmd|' /C calc'!A0", "+SUM(1,1)", "-2+3", "@SUM(A1)", " =1+1", "\t=1+1", "\r=1+1", "\n=1+1", "=\"x\"", "plain, comma", "say \"hi\"", "line1\r\nline2",
        "مرحبا‮", "\U0001F469‍\U0001F469‍\U0001F467 family", "a\u0000b\u0007c", "﻿bom", "emoji\U0001F600", "日本語",
        "x\u2028y", "'single", "<script>alert(1)</script>", "a&b", "trail ", "\"", ",", "\\",
    ];

    [Fact] // CSV: parse back with an independent RFC 4180 reader; every cell is data (no live formula), content survives, row/column counts hold
    public void Csv_round_trips_hostile_cells_and_no_cell_is_a_live_formula()
    {
        var (db, fav, exp) = Open();
        using (db)
        {
            foreach (string h in Hostile)
                Add(fav, "w" + h, Content(h, h, "ipa" + h, h)); // hostile text in every column
            foreach (string h in Hostile) Add(fav, h.Trim().Length == 0 ? "blank" : h.Trim() + "Z", Content(h, h)); // and a hostile leading character in the word
            string path = Out("h.csv");
            var r = Run(exp, path, VocabExportFormat.Csv);
            Assert.True(r.Ok, r.Error);
            var rows = ParseCsv(File.ReadAllBytes(path));
            Assert.Equal(["Word", "Language", "Phonetic", "Definition", "Example", "Source"], rows[0]);
            Assert.Equal(1 + fav.List().Count, rows.Count);
            Assert.All(rows, row => Assert.Equal(6, row.Count));
            foreach (var row in rows.Skip(1))
                foreach (string cell in row)
                {
                    if (cell.Length == 0) continue;
                    // after the apostrophe guard, no cell may begin with a character a spreadsheet treats as a formula or DDE
                    Assert.DoesNotContain(cell[0], "=+-@\t\r");
                    Assert.DoesNotContain('\0', cell);
                    Assert.DoesNotContain('\u0007', cell);
                }
            var comma = rows.Single(x => x[0] == "wplain, comma");
            Assert.Contains("plain, comma", comma[3]);
            var quote = rows.Single(x => x[0] == "wsay \"hi\"");
            Assert.Contains("say \"hi\"", quote[3]);
            var multi = rows.Single(x => x[0] == "wline1 line2"); // word is single line
            Assert.Contains("line1\nline2", multi[3]);
            var arabic = rows.Single(x => x[0].StartsWith("wمرح", StringComparison.Ordinal));
            Assert.Contains("مرح", arabic[3]);
            Assert.Contains(rows, x => x[0].StartsWith("'=cmd", StringComparison.Ordinal));
            Assert.Contains(rows, x => x[0].StartsWith("'+SUM", StringComparison.Ordinal));
            Assert.Contains(rows, x => x[0].StartsWith("'-2+3", StringComparison.Ordinal));
            Assert.Contains(rows, x => x[0].StartsWith("'@SUM", StringComparison.Ordinal));
        }
    }

    [Fact] // txt: UTF-8 without BOM, LF, one word per line, no control characters, case-insensitive dedupe, parsed back by lines
    public void Txt_round_trips_words_and_dedupes()
    {
        var (db, fav, exp) = Open();
        using (db)
        {
            foreach (string w in new[] { "Apple", "apple ", "BANANA", "multi\r\nline", "tab\tsep", "nul\0char", "\U0001F600", "日本語", "=1+1" })
                Add(fav, w);
            var r = Run(exp, Out("w.txt"), VocabExportFormat.EudicTxt);
            Assert.True(r.Ok, r.Error);
            byte[] b = File.ReadAllBytes(Out("w.txt"));
            Assert.False(b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB);
            string text = new UTF8Encoding(false, true).GetString(b); // strict decode: valid UTF-8
            Assert.DoesNotContain('\r', text);
            var lines = text.Split('\n');
            Assert.Equal("", lines[^1]);
            var words = lines[..^1];
            Assert.Equal(words.Length, words.Select(x => x.ToLowerInvariant()).Distinct().Count());
            Assert.All(words, w => { Assert.NotEqual("", w); Assert.DoesNotContain(w, c => char.IsControl(c)); });
            Assert.Contains("multi line", words);
            Assert.Contains("\U0001F600", words);
            Assert.Contains("nulchar", words);
            Assert.Equal(1, words.Count(w => string.Equals(w, "apple", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact] // apkg: parsed by zip + sqlite here; stable note identity on re-export, content edit keeps guid and note id, field escaping, csum, deck
    public void Apkg_is_valid_has_stable_guids_and_escapes_html()
    {
        var (db, fav, exp) = Open();
        using (db)
        {
            Add(fav, "apple", Content("fruit<b>", "x & y <i>"));
            Add(fav, "<b>bold</b> & co");
            var r1 = exp.Export(Out("a.apkg"), new VocabExportOptions(VocabExportFormat.Apkg, VocabContent.All, false, "My \"Deck\"\u001f"), Ct);
            Assert.True(r1.Ok, r1.Error);
            var one = ReadApkg(Out("a.apkg"));
            Assert.Equal(11, one.ColVer);
            Assert.Equal(2, one.Notes.Count);
            Assert.Equal(2, one.Cards.Count);
            Assert.Single(one.Models);
            var model = one.Models.Single().Value!;
            Assert.All(one.Notes, n => Assert.Equal(model["flds"]!.AsArray().Count, n.Fields.Length));
            Assert.All(one.Notes, n => Assert.Equal(long.Parse(one.Models.Keys.Single()), n.Mid));
            Assert.Equal(one.Notes.Count, one.Notes.Select(n => n.Guid).Distinct().Count());
            Assert.Equal(one.Notes.Count, one.Notes.Select(n => n.Id).Distinct().Count());
            Assert.All(one.Cards, c => Assert.Contains(one.Decks.Keys, k => k == c.Did.ToString()));
            Assert.All(one.Cards, c => Assert.Contains(one.Notes, n => n.Id == c.Nid));
            foreach (var n in one.Notes)
            {
                Assert.Equal(Unhtml(n.Fields[0]), n.Sfld); // sort field is the plain first field
                Assert.Equal(Convert.ToInt64(Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(n.Sfld)))[..8], 16), n.Csum);
                Assert.DoesNotContain("<b>", n.Fields[0]); // raw markup must be escaped inside the HTML field
                Assert.False(n.Sfld.Contains((char)0x1f), "sfld hex " + Convert.ToHexString(Encoding.UTF8.GetBytes(n.Sfld)));
            }
            var bold = one.Notes.Single(n => n.Sfld.Contains("bold"));
            Assert.Equal("&lt;b&gt;bold&lt;/b&gt; &amp; co", bold.Fields[0]);
            string deckName = one.Decks.Values.Select(d => d!["name"]!.GetValue<string>()).Single(x => x != "Default");
            Assert.DoesNotContain('"', deckName);
            Assert.False(deckName.Contains((char)0x1f), deckName);

            // re-export after an edit: same note id and guid for every entry, the edit lands in the same note
            Add(fav, "apple", Content("edited"));
            var r2 = Run(exp, Out("b.apkg"), VocabExportFormat.Apkg);
            Assert.True(r2.Ok);
            var two = ReadApkg(Out("b.apkg"));
            Assert.Equal(one.Notes.Select(n => (n.Id, n.Guid)).OrderBy(x => x.Id), two.Notes.Select(n => (n.Id, n.Guid)).OrderBy(x => x.Id));
            var apple = two.Notes.Single(n => n.Fields[0] == "apple");
            Assert.Contains("edited", apple.Fields[2]);
        }
    }

    [Fact]
    public void Note_guid_is_a_pure_function_of_the_entry_id()
    {
        Assert.Equal(VocabFormats.NoteGuid("e1"), VocabFormats.NoteGuid("e1"));
        Assert.NotEqual(VocabFormats.NoteGuid("e1"), VocabFormats.NoteGuid("e2"));
        Assert.Matches("^[A-Za-z0-9_-]{12}$", VocabFormats.NoteGuid("e1"));
    }

    [Fact] // only-new across revisions: new entry, edited entry, and formats do not influence each other; a deleted entry is never exported
    public void Only_new_tracks_entry_revisions_per_format()
    {
        var (db, fav, exp) = Open();
        using (db)
        {
            Add(fav, "alpha"); Add(fav, "beta");
            var first = Run(exp, Out("1.txt"), VocabExportFormat.EudicTxt, onlyNew: true);
            Assert.Equal(2, first.Exported);
            Assert.Equal(VocabExportErrors.NothingToExport, Run(exp, Out("2.txt"), VocabExportFormat.EudicTxt, onlyNew: true).Error);
            Assert.Equal(2, Run(exp, Out("1.csv"), VocabExportFormat.Csv, onlyNew: true).Exported); // csv has its own history

            Add(fav, "gamma");
            var third = Run(exp, Out("3.txt"), VocabExportFormat.EudicTxt, onlyNew: true);
            Assert.Equal((1, 2), (third.Exported, third.Skipped));
            Assert.Equal("gamma\n", File.ReadAllText(Out("3.txt")));

            Add(fav, "alpha", Content("revised")); // revision 2 of alpha is new again
            var fourth = Run(exp, Out("4.csv"), VocabExportFormat.Csv, onlyNew: true);
            Assert.Equal(2, fourth.Exported); // csv never saw gamma and alpha rev 2
            Assert.Equal(["alpha", "gamma"], ParseCsv(File.ReadAllBytes(Out("4.csv"))).Skip(1).Select(x => x[0]).OrderBy(x => x));

            Assert.True(fav.Unfavorite("en", "beta"));
            var all = Run(exp, Out("all.txt"), VocabExportFormat.EudicTxt);
            Assert.DoesNotContain("beta", File.ReadAllText(Out("all.txt")));
            Assert.Equal(2, all.Exported);
            Add(fav, "beta"); // restored with the same content: revision unchanged, so not new for a format that already exported it
            Assert.Equal(VocabExportErrors.NothingToExport, Run(exp, Out("5.csv"), VocabExportFormat.Csv, onlyNew: true).Error);
        }
    }

    [Fact] // an export cancelled before the write, or whose move fails (locked target), never marks anything as exported and leaves no temp file
    public void Failed_and_cancelled_exports_do_not_mark_entries()
    {
        var (db, fav, exp) = Open();
        using (db)
        {
            Add(fav, "alpha");
            using var cts = new CancellationTokenSource(); cts.Cancel();
            var cancelled = exp.Export(Out("c.csv"), new VocabExportOptions(VocabExportFormat.Csv, VocabContent.All, true), cts.Token);
            Assert.Equal(VocabExportErrors.Cancelled, cancelled.Error);
            Assert.False(File.Exists(Out("c.csv")));
            File.WriteAllText(Out("lock.csv"), "old");
            using (new FileStream(Out("lock.csv"), FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.False(Run(exp, Out("lock.csv"), VocabExportFormat.Csv, onlyNew: true).Ok);
            Assert.Empty(Directory.GetFiles(root.Root, "*.tmp"));
            Assert.Equal("old", File.ReadAllText(Out("lock.csv")));
            Assert.Equal(1, Run(exp, Out("ok.csv"), VocabExportFormat.Csv, onlyNew: true).Exported);
        }
    }

    [Fact] // disk full on an overwrite: the old file is intact, record Failed, retryable, and on a retry the rows are exported once
    public void Disk_full_during_apkg_overwrite_keeps_old_file_and_is_retryable()
    {
        bool full = true;
        var (db, fav, exp) = Open(temp: p => full ? new FullDiskStream() : new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.None));
        using (db)
        {
            Add(fav, "alpha");
            File.WriteAllBytes(Out("t.apkg"), [1, 2, 3]);
            var r = Run(exp, Out("t.apkg"), VocabExportFormat.Apkg, onlyNew: true);
            Assert.Equal((false, VocabExportErrors.DiskFull, true), (r.Ok, r.Error, r.Retryable));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(Out("t.apkg")));
            Assert.Empty(Directory.GetFiles(root.Root, "*.tmp"));
            full = false;
            var again = Run(exp, Out("t.apkg"), VocabExportFormat.Apkg, onlyNew: true);
            Assert.True(again.Ok, again.Error);
            Assert.Single(ReadApkg(Out("t.apkg")).Notes);
        }
    }

    [Theory] // recovery at startup: moved but record still Pending. Hash matches -> Succeeded (counts as exported); user edited the file -> Failed (rows are new again)
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_by_hash_match_and_mismatch(bool tamper)
    {
        string path = Out("rec.csv");
        var (db, fav, _) = Open();
        using (db) Add(fav, "alpha");
        var (db1, _, exp1) = Open(new FaultAt("export:moved"));
        using (db1) Assert.Throws<SimulatedCrash>(() => Run(exp1, path, VocabExportFormat.Csv, onlyNew: true));
        if (tamper) File.AppendAllText(path, "user edit\r\n");
        var (db2, _, exp2) = Open();
        using (db2)
        {
            var rec = Assert.Single(exp2.Recover(), x => x.Path == path);
            Assert.Equal(!tamper, rec.Recovered);
            var again = Run(exp2, Out("again.csv"), VocabExportFormat.Csv, onlyNew: true);
            if (tamper) Assert.True(again.Ok, "a mismatched file must not count as exported");
            else Assert.Equal(VocabExportErrors.NothingToExport, again.Error);
        }
    }

    [Fact] // a Pending record whose folder vanished does not throw at startup
    public void Recovery_tolerates_missing_folder()
    {
        string dir = Path.Combine(root.Root, "gone"); Directory.CreateDirectory(dir);
        var (db, fav, _) = Open();
        using (db) Add(fav, "alpha");
        var (db1, _, exp1) = Open(new FaultAt("export:moved"));
        using (db1) Assert.Throws<SimulatedCrash>(() => Run(exp1, Path.Combine(dir, "x.csv"), VocabExportFormat.Csv));
        Directory.Delete(dir, true);
        var (db2, _, exp2) = Open();
        using (db2) Assert.False(Assert.Single(exp2.Recover()).Recovered);
    }

    // ================= scale =================

    [Fact] // 5000 entries x 2 targets: favorite, drain the outbox, and all three exports, within generous bounds (timings are in the record)
    public void Five_thousand_entries_favorite_drain_and_export_in_time()
    {
        var (db, fav, exp) = Open();
        using (db)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 5000; i++) Add(fav, "word" + i, Content("m" + i), "en", "ankiconnect", "eudic");
            long favMs = sw.ElapsedMilliseconds; sw.Restart();
            Assert.Equal(5000, fav.ActiveCount());
            int drained = 0;
            while (fav.Claim("ankiconnect") is { } d) { Assert.True(fav.Complete(d.EntryId, d.TargetInstanceId, d.EntryRevision, DeliveryState.Succeeded, "r")); drained++; }
            long drainMs = sw.ElapsedMilliseconds;
            Assert.Equal(5000, drained);
            Assert.Equal(5000, fav.DeliveryCounts().Single(c => c.TargetInstanceId == "ankiconnect" && c.State == DeliveryState.Succeeded).Count);
            var times = new List<long>();
            foreach (var (f, name) in new[] { (VocabExportFormat.EudicTxt, "b.txt"), (VocabExportFormat.Csv, "b.csv"), (VocabExportFormat.Apkg, "b.apkg") })
            {
                sw.Restart();
                var r = Run(exp, Out(name), f);
                times.Add(sw.ElapsedMilliseconds);
                Assert.True(r.Ok, r.Error);
                Assert.Equal(5000, r.Exported);
            }
            Assert.Equal(5000, ReadApkg(Out("b.apkg")).Notes.Count);
            Assert.Equal(5001, ParseCsv(File.ReadAllBytes(Out("b.csv"))).Count);
            TestContext.Current.SendDiagnosticMessage($"F15 5000 entries: favorite {favMs} ms, drain {drainMs} ms, txt/csv/apkg {string.Join("/", times)} ms");
            Assert.True(favMs < 60_000 && drainMs < 60_000 && times.All(t => t < 30_000), $"favorite {favMs} drain {drainMs} export {string.Join("/", times)}");
        }
    }

    // ================= secrets =================

    [Fact] // the service's status/problem rows are plain data: none can contain a key, even when a target reports one in its detail
    public async Task Status_and_problem_rows_never_carry_a_key_from_an_error_detail()
    {
        const string Secret = "NIS secret-token-0123456789abcdef";
        var (db, fav, _) = Open();
        using (db)
        {
            var target = new ScriptTarget("t", (_, _) => Task.FromResult<VocabSyncOutcome>(new VocabSyncOutcome.Failure(new ProviderError(ErrorKind.Auth, "401 " + Secret))));
            await using var service = new VocabService(fav, new VocabSyncWorker(fav, _ => target, clock), () => ["t"], clock);
            service.Favorite(new FavoriteCard("en", "apple", Content()));
            var reports = await service.SyncNowAsync(Ct);
            string all = JsonSerializer.Serialize(new { s = service.Status(["t"]), p = service.Problems() });
            Assert.DoesNotContain("secret-token", all);
            Assert.DoesNotContain("secret-token", string.Join("|", reports.Select(r => r.Error ?? "")));
        }
    }
}
