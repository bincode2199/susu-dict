using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Susu.Abstractions;

namespace Susu.Storage;

/// <summary>
/// vocab_entries + vocab_deliveries (ARCHITECTURE 8.2/8.3, DATA05/DATA06). Everything a favorite changes happens in one
/// writer transaction; fault stages "favorite:entry" and "favorite:outbox" let tests prove all-or-nothing.
/// </summary>
public sealed class FavoritesRepository(Database db, IClock clock, IFaultPoint? faults = null, Func<string>? newId = null) : IFavorites
{
    private readonly Func<string> id = newId ?? (() => Guid.NewGuid().ToString("N"));

    /// <summary>NFC; case-folded for English (the original text stays in display_text).</summary>
    public static string Normalize(string lang, string word)
    {
        string t = word.Trim().Normalize(NormalizationForm.FormC);
        return lang.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? t.ToLowerInvariant() : t;
    }

    private static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Key order and whitespace do not change content identity.</summary>
    public static string Canonical(string json) => Sort(JsonNode.Parse(json))?.ToJsonString(Compact) ?? "null";

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sort(p.Value?.DeepClone())))),
        JsonArray a => new JsonArray(a.Select(x => Sort(x?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };

    public FavoriteResult Favorite(FavoriteCard card, IReadOnlyList<string> targetInstanceIds)
    {
        string norm = Normalize(card.Lang, card.Word);
        if (norm.Length == 0) throw new ArgumentException("word is empty", nameof(card));
        string content = Canonical(card.ContentJson);
        long now = clock.UtcNow.ToUnixTimeMilliseconds();
        var targets = targetInstanceIds.Distinct(StringComparer.Ordinal).ToArray();
        return db.Write(w =>
        {
            string? entryId = null; long revision = 1; string? oldContent = null; bool wasDeleted = false;
            using (var cmd = w.Connection.CreateCommand())
            {
                cmd.Transaction = w.Transaction;
                cmd.CommandText = "SELECT entry_id, revision, content_json, deleted_at IS NOT NULL FROM vocab_entries WHERE lang=$l AND normalized_text=$n;";
                cmd.Parameters.AddWithValue("$l", card.Lang); cmd.Parameters.AddWithValue("$n", norm);
                using var r = cmd.ExecuteReader();
                if (r.Read()) { entryId = r.GetString(0); revision = r.GetInt64(1); oldContent = r.GetString(2); wasDeleted = r.GetInt64(3) == 1; }
            }
            bool created = entryId is null, changed = false;
            if (created)
            {
                entryId = id();
                w.Exec("INSERT INTO vocab_entries(entry_id, lang, normalized_text, display_text, content_json, revision, created_at, updated_at, deleted_at) VALUES ($e, $l, $n, $d, $c, 1, $t, $t, NULL);",
                    ("$e", entryId), ("$l", card.Lang), ("$n", norm), ("$d", card.Word.Trim()), ("$c", content), ("$t", now));
            }
            else
            {
                changed = oldContent != content;
                if (changed) revision++;
                w.Exec("UPDATE vocab_entries SET content_json=$c, revision=$r, updated_at=$t, deleted_at=NULL WHERE entry_id=$e;",
                    ("$c", content), ("$r", revision), ("$t", now), ("$e", entryId));
                if (changed) // unsent rows of older revisions are superseded
                    w.Exec("UPDATE vocab_deliveries SET state='Cancelled' WHERE entry_id=$e AND entry_revision<$r AND state IN ('Pending','RetryWait');", ("$e", entryId), ("$r", revision));
            }
            faults?.Hit("favorite:entry");
            var queued = new List<string>();
            foreach (string target in targets)
            {
                // same (entry, revision, target) keeps its operationId; a cancelled row is revived, a sent one is left alone
                int ins = w.Exec("INSERT OR IGNORE INTO vocab_deliveries(entry_id, target_instance_id, entry_revision, operation_id, state, remote_id, attempts, next_at) VALUES ($e, $t, $r, $o, 'Pending', NULL, 0, 0);",
                    ("$e", entryId), ("$t", target), ("$r", revision), ("$o", id()));
                int revived = w.Exec("UPDATE vocab_deliveries SET state='Pending', next_at=0 WHERE entry_id=$e AND target_instance_id=$t AND entry_revision=$r AND state='Cancelled';", ("$e", entryId), ("$t", target), ("$r", revision));
                if (ins + revived > 0) queued.Add(target);
            }
            faults?.Hit("favorite:outbox");
            return new FavoriteResult(entryId!, revision, created, changed, wasDeleted, queued);
        });
    }

    public bool Unfavorite(string lang, string word)
    {
        string norm = Normalize(lang, word);
        long now = clock.UtcNow.ToUnixTimeMilliseconds();
        return db.Write(w =>
        {
            if (w.Scalar("SELECT entry_id FROM vocab_entries WHERE lang=$l AND normalized_text=$n AND deleted_at IS NULL;", ("$l", lang), ("$n", norm)) is not string e) return false;
            w.Exec("UPDATE vocab_entries SET deleted_at=$t WHERE entry_id=$e;", ("$t", now), ("$e", e));
            // Sending/Succeeded/Uncertain stay as they are; nothing remote is deleted
            w.Exec("UPDATE vocab_deliveries SET state='Cancelled' WHERE entry_id=$e AND state IN ('Pending','RetryWait');", ("$e", e));
            return true;
        });
    }

    public bool IsFavorite(string lang, string word) => db.Read(c =>
        Database.Scalar(c, null, "SELECT 1 FROM vocab_entries WHERE lang=$l AND normalized_text=$n AND deleted_at IS NULL;", ("$l", lang), ("$n", Normalize(lang, word))) is not null);

    private static VocabEntrySnapshot Entry(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5) == 1);
    private const string EntryColumns = "SELECT entry_id, lang, display_text, content_json, revision, deleted_at IS NOT NULL FROM vocab_entries";

    public VocabEntrySnapshot? Get(string entryId) => db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = EntryColumns + " WHERE entry_id=$e;";
        cmd.Parameters.AddWithValue("$e", entryId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Entry(r) : null;
    });

    public IReadOnlyList<VocabEntrySnapshot> List(bool includeDeleted = false) => db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = EntryColumns + (includeDeleted ? "" : " WHERE deleted_at IS NULL") + " ORDER BY created_at, entry_id;";
        using var r = cmd.ExecuteReader();
        var list = new List<VocabEntrySnapshot>();
        while (r.Read()) list.Add(Entry(r));
        return list;
    });

    private const string DeliveryColumns = "SELECT entry_id, target_instance_id, entry_revision, operation_id, state, remote_id, attempts, next_at FROM vocab_deliveries";

    private static VocabDelivery Delivery(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3),
        Enum.Parse<DeliveryState>(r.GetString(4)), r.IsDBNull(5) ? null : r.GetString(5), r.GetInt32(6), r.GetInt64(7));

    public IReadOnlyList<VocabDelivery> Deliveries(string entryId) => db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = DeliveryColumns + " WHERE entry_id=$e ORDER BY entry_revision, target_instance_id;";
        cmd.Parameters.AddWithValue("$e", entryId);
        using var r = cmd.ExecuteReader();
        var list = new List<VocabDelivery>();
        while (r.Read()) list.Add(Delivery(r));
        return list;
    });

    public VocabDelivery? Claim(string targetInstanceId)
    {
        long now = clock.UtcNow.ToUnixTimeMilliseconds();
        return db.Write(w =>
        {
            VocabDelivery? d;
            using (var cmd = w.Connection.CreateCommand())
            {
                cmd.Transaction = w.Transaction;
                cmd.CommandText = DeliveryColumns + " WHERE target_instance_id=$t AND state IN ('Pending','RetryWait') AND next_at<=$n ORDER BY next_at, rowid LIMIT 1;";
                cmd.Parameters.AddWithValue("$t", targetInstanceId); cmd.Parameters.AddWithValue("$n", now);
                using var r = cmd.ExecuteReader();
                d = r.Read() ? Delivery(r) : null;
            }
            if (d is null) return null;
            w.Exec("UPDATE vocab_deliveries SET state='Sending', attempts=attempts+1 WHERE entry_id=$e AND target_instance_id=$t AND entry_revision=$r;", ("$e", d.EntryId), ("$t", d.TargetInstanceId), ("$r", d.EntryRevision));
            return d with { State = DeliveryState.Sending, Attempts = d.Attempts + 1 };
        });
    }

    public bool Complete(string entryId, string targetInstanceId, long entryRevision, DeliveryState outcome, string? remoteId = null, TimeSpan? retryAfter = null)
    {
        if (outcome is DeliveryState.Pending or DeliveryState.Sending) throw new ArgumentException("not a result state", nameof(outcome));
        long next = outcome == DeliveryState.RetryWait ? clock.UtcNow.ToUnixTimeMilliseconds() + (long)(retryAfter ?? TimeSpan.Zero).TotalMilliseconds : 0;
        return db.Write(w => w.Exec("UPDATE vocab_deliveries SET state=$s, remote_id=coalesce($rid, remote_id), next_at=$n WHERE entry_id=$e AND target_instance_id=$t AND entry_revision=$r AND state='Sending';",
            ("$s", outcome.ToString()), ("$rid", remoteId), ("$n", next), ("$e", entryId), ("$t", targetInstanceId), ("$r", entryRevision)) == 1);
    }

    public VocabDelivery? ClaimUncertain(string targetInstanceId) => db.Write(w =>
    {
        VocabDelivery? d;
        using (var cmd = w.Connection.CreateCommand())
        {
            cmd.Transaction = w.Transaction;
            cmd.CommandText = DeliveryColumns + " WHERE target_instance_id=$t AND state='Uncertain' ORDER BY rowid LIMIT 1;";
            cmd.Parameters.AddWithValue("$t", targetInstanceId);
            using var r = cmd.ExecuteReader();
            d = r.Read() ? Delivery(r) : null;
        }
        if (d is null) return null;
        w.Exec("UPDATE vocab_deliveries SET state='Sending' WHERE entry_id=$e AND target_instance_id=$t AND entry_revision=$r;", ("$e", d.EntryId), ("$t", d.TargetInstanceId), ("$r", d.EntryRevision));
        return d with { State = DeliveryState.Sending };
    });

    public bool Resolve(string entryId, string targetInstanceId, long entryRevision, bool delivered) => db.Write(w => w.Exec(
        delivered
            ? "UPDATE vocab_deliveries SET state='Succeeded', next_at=0 WHERE entry_id=$e AND target_instance_id=$t AND entry_revision=$r AND state IN ('Uncertain','Failed');"
            // "Not delivered" resends with the same operationId, except for an entry the user has since unfavorited: that row is cancelled.
            : "UPDATE vocab_deliveries SET state=CASE WHEN EXISTS (SELECT 1 FROM vocab_entries v WHERE v.entry_id=vocab_deliveries.entry_id AND v.deleted_at IS NOT NULL) THEN 'Cancelled' ELSE 'Pending' END, attempts=0, next_at=0 WHERE entry_id=$e AND target_instance_id=$t AND entry_revision=$r AND state IN ('Uncertain','Failed');",
        ("$e", entryId), ("$t", targetInstanceId), ("$r", entryRevision)) == 1);

    public IReadOnlyList<VocabDeliveryCount> DeliveryCounts() => db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT target_instance_id, state, count(*) FROM vocab_deliveries GROUP BY target_instance_id, state;";
        using var r = cmd.ExecuteReader();
        var list = new List<VocabDeliveryCount>();
        while (r.Read()) list.Add(new VocabDeliveryCount(r.GetString(0), Enum.Parse<DeliveryState>(r.GetString(1)), r.GetInt32(2)));
        return list;
    });

    public IReadOnlyList<VocabDelivery> DeliveriesIn(IReadOnlyCollection<DeliveryState> states, int limit) => states.Count == 0 ? [] : db.Read(c =>
    {
        using var cmd = c.CreateCommand();
        var names = states.Select((s, i) => { cmd.Parameters.AddWithValue($"$s{i}", s.ToString()); return $"$s{i}"; }).ToList();
        cmd.CommandText = DeliveryColumns + $" WHERE state IN ({string.Join(',', names)}) ORDER BY rowid LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", Math.Max(0, limit));
        using var r = cmd.ExecuteReader();
        var list = new List<VocabDelivery>();
        while (r.Read()) list.Add(Delivery(r));
        return list;
    });

    public int ActiveCount() => db.Read(c => Database.Scalar(c, null, "SELECT count(*) FROM vocab_entries WHERE deleted_at IS NULL;") is long n ? (int)n : 0);

    /// <summary>F17.2 data clean: removes every favorite, its deliveries and the export records. Returns the number of entries removed. Nothing outside the database is touched.</summary>
    public int ClearAll() => db.Write(w =>
    {
        int count = Convert.ToInt32(w.Scalar("SELECT count(*) FROM vocab_entries;"), System.Globalization.CultureInfo.InvariantCulture);
        w.Exec("DELETE FROM vocab_export_items;");
        w.Exec("DELETE FROM vocab_exports;");
        w.Exec("DELETE FROM vocab_deliveries;");
        w.Exec("DELETE FROM vocab_entries;");
        return count;
    });

    public int RecoverInterrupted() => db.Write(w => w.Exec("UPDATE vocab_deliveries SET state='Uncertain' WHERE state='Sending';"));
}
