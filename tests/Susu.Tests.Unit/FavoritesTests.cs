using System.Diagnostics;
using Susu.Abstractions;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>F15.1 (DATA05/DATA06 storage side): local entries, per-target outbox, revision and dedupe, no network needed.</summary>
public sealed class FavoritesTests : IDisposable
{
    private readonly TempRoot root = new();
    private readonly ManualClock clock = new();
    private int ids;

    private (Database Db, FavoritesRepository Fav) Open(IFaultPoint? faults = null)
    {
        var db = Database.Open(root.Paths.Database);
        return (db, new FavoritesRepository(db, clock, faults, () => $"id{++ids}"));
    }

    private static FavoriteCard Card(string word = "Apple", string content = """{"meanings":["fruit"]}""", string lang = "en") => new(lang, word, content);

    public void Dispose() => root.Dispose();

    [Fact]
    public void Favorite_without_any_target_stores_the_entry_only()
    {
        var (db, fav) = Open();
        using (db)
        {
            var r = fav.Favorite(Card(), []);
            Assert.True(r.Created);
            Assert.Equal(1, r.Revision);
            Assert.Empty(r.QueuedTargets);
            Assert.True(fav.IsFavorite("en", "apple"));
            Assert.Empty(fav.Deliveries(r.EntryId));
            Assert.Equal("Apple", fav.Get(r.EntryId)!.DisplayText);
        }
    }

    [Fact] // DATA05: repeat / different case / NFC form are one entry that keeps the original text
    public void Duplicates_dedupe_keep_original_text_and_do_not_bump_revision()
    {
        var (db, fav) = Open();
        using (db)
        {
            var first = fav.Favorite(Card("Apple"), ["anki"]);
            var again = fav.Favorite(Card("APPLE", """{ "meanings": [ "fruit" ] }"""), ["anki"]);
            Assert.Equal(first.EntryId, again.EntryId);
            Assert.Equal(1, again.Revision);
            Assert.False(again.ContentChanged);
            Assert.Empty(again.QueuedTargets);
            Assert.Single(fav.List());
            Assert.Equal("Apple", fav.List()[0].DisplayText);
            Assert.Single(fav.Deliveries(first.EntryId));
            var nfc = fav.Favorite(Card("Café", "{}", "fr"), []);
            Assert.Equal(nfc.EntryId, fav.Favorite(Card("Café", "{}", "fr"), []).EntryId);
            Assert.NotEqual(nfc.EntryId, fav.Favorite(Card("café", "{}", "fr"), []).EntryId); // only English folds case
        }
    }

    [Fact]
    public void Content_change_bumps_revision_and_queues_new_rows_with_new_operation_ids()
    {
        var (db, fav) = Open();
        using (db)
        {
            var one = fav.Favorite(Card(), ["anki", "eudic"]);
            var claimed = fav.Claim("anki")!;
            fav.Complete(claimed.EntryId, "anki", 1, DeliveryState.Succeeded, "r1");
            var two = fav.Favorite(Card(content: """{"meanings":["fruit","tree"]}"""), ["anki", "eudic"]);
            Assert.Equal(2, two.Revision);
            Assert.True(two.ContentChanged);
            Assert.Equal(["anki", "eudic"], two.QueuedTargets);
            var rows = fav.Deliveries(one.EntryId);
            Assert.Equal(4, rows.Count);
            Assert.Equal(4, rows.Select(r => r.OperationId).Distinct().Count());
            Assert.Equal(DeliveryState.Succeeded, rows.Single(r => r is { TargetInstanceId: "anki", EntryRevision: 1 }).State);
            Assert.Equal(DeliveryState.Cancelled, rows.Single(r => r is { TargetInstanceId: "eudic", EntryRevision: 1 }).State); // unsent older revision superseded
            Assert.Equal(2, fav.Get(one.EntryId)!.Revision);
        }
    }

    [Fact] // repeated click on the same revision/target must not create a new operationId
    public void Repeated_favorite_keeps_operation_id_and_adds_new_targets_independently()
    {
        var (db, fav) = Open();
        using (db)
        {
            var r = fav.Favorite(Card(), ["anki"]);
            string op = fav.Deliveries(r.EntryId)[0].OperationId;
            fav.Favorite(Card(), ["anki"]);
            Assert.Equal(op, fav.Deliveries(r.EntryId).Single().OperationId);
            var added = fav.Favorite(Card(), ["anki", "eudic"]);
            Assert.Equal(["eudic"], added.QueuedTargets);
            Assert.Equal(2, fav.Deliveries(r.EntryId).Count);
        }
    }

    [Theory] // entry and outbox are all-or-nothing wherever it crashes
    [InlineData("favorite:entry")]
    [InlineData("favorite:outbox")]
    public void Crash_between_steps_leaves_nothing_behind(string stage)
    {
        var (db, fav) = Open(new FaultAt(stage));
        using (db)
        {
            Assert.Throws<SimulatedCrash>(() => fav.Favorite(Card(), ["anki", "eudic"]));
            Assert.Empty(fav.List(includeDeleted: true));
            Assert.Equal(0L, db.Read(c => Database.Scalar(c, null, "SELECT count(*) FROM vocab_deliveries;")));
        }
        var (db2, fav2) = Open();
        using (db2) Assert.False(fav2.IsFavorite("en", "apple")); // and survives reopen
    }

    [Fact]
    public void Crash_during_a_revision_bump_keeps_the_previous_revision_and_its_rows()
    {
        var (db, fav) = Open();
        using (db) fav.Favorite(Card(), ["anki"]);
        var (db2, fav2) = Open(new FaultAt("favorite:outbox"));
        using (db2)
        {
            Assert.Throws<SimulatedCrash>(() => fav2.Favorite(Card(content: """{"x":1}"""), ["anki"]));
            var e = fav2.List().Single();
            Assert.Equal(1, e.Revision);
            Assert.Equal(DeliveryState.Pending, fav2.Deliveries(e.EntryId).Single().State);
        }
    }

    [Fact] // unfavorite stops unsent work, never touches sent/uncertain rows, restore revives the same operation
    public void Unfavorite_cancels_unsent_rows_only_and_refavorite_restores_them()
    {
        var (db, fav) = Open();
        using (db)
        {
            var r = fav.Favorite(Card(), ["anki", "eudic", "other"]);
            var a = fav.Claim("anki")!; fav.Complete(a.EntryId, "anki", 1, DeliveryState.Succeeded, "remote-1");
            var e = fav.Claim("eudic")!; // left Sending
            string otherOp = fav.Deliveries(r.EntryId).Single(d => d.TargetInstanceId == "other").OperationId;

            Assert.True(fav.Unfavorite("en", "APPLE"));
            Assert.False(fav.Unfavorite("en", "apple"));
            Assert.False(fav.IsFavorite("en", "apple"));
            Assert.Empty(fav.List());
            Assert.True(fav.Get(r.EntryId)!.Deleted);
            var rows = fav.Deliveries(r.EntryId).ToDictionary(d => d.TargetInstanceId);
            Assert.Equal(DeliveryState.Succeeded, rows["anki"].State);
            Assert.Equal("remote-1", rows["anki"].RemoteId);
            Assert.Equal(DeliveryState.Sending, rows["eudic"].State);
            Assert.Equal(DeliveryState.Cancelled, rows["other"].State);
            Assert.Null(fav.Claim("other")); // cancelled rows are not claimable

            var back = fav.Favorite(Card(), ["anki", "eudic", "other"]);
            Assert.True(back.Restored);
            Assert.Equal(1, back.Revision);
            Assert.Equal(["other"], back.QueuedTargets);
            Assert.Equal(otherOp, fav.Deliveries(r.EntryId).Single(d => d.TargetInstanceId == "other").OperationId);
            Assert.Single(fav.List());
        }
    }

    [Fact] // DATA06 storage side: restart turns Sending into Uncertain, which is never claimed again
    public void Recover_interrupted_marks_sending_uncertain_and_not_claimable()
    {
        var (db, fav) = Open();
        using (db)
        {
            fav.Favorite(Card("a"), ["anki"]);
            fav.Favorite(Card("b"), ["anki"]);
            var sent = fav.Claim("anki")!;
            Assert.Equal(DeliveryState.Sending, sent.State);
            Assert.Equal(1, sent.Attempts);
        }
        var (db2, fav2) = Open();
        using (db2)
        {
            Assert.Equal(1, fav2.RecoverInterrupted());
            Assert.Equal(DeliveryState.Uncertain, fav2.Deliveries(sent_id(fav2, "a")).Single().State);
            Assert.NotNull(fav2.Claim("anki")); // only the other, still Pending, row
            Assert.Null(fav2.Claim("anki"));
            Assert.False(fav2.Complete("x", "anki", 1, DeliveryState.Succeeded)); // not Sending: no effect
        }
    }

    private static string sent_id(FavoritesRepository fav, string word) => fav.List().Single(e => e.DisplayText == word).EntryId;

    [Fact] // retry waits for its time on the fake clock; targets are independent
    public void Retry_wait_is_due_only_after_the_delay_and_targets_do_not_block_each_other()
    {
        var (db, fav) = Open();
        using (db)
        {
            var r = fav.Favorite(Card(), ["anki", "eudic"]);
            var c = fav.Claim("anki")!;
            Assert.True(fav.Complete(c.EntryId, "anki", c.EntryRevision, DeliveryState.RetryWait, retryAfter: TimeSpan.FromSeconds(30)));
            Assert.Null(fav.Claim("anki"));
            var other = fav.Claim("eudic")!;
            Assert.True(fav.Complete(other.EntryId, "eudic", 1, DeliveryState.Failed));
            clock.Advance(TimeSpan.FromSeconds(29));
            Assert.Null(fav.Claim("anki"));
            clock.Advance(TimeSpan.FromSeconds(2));
            var again = fav.Claim("anki")!;
            Assert.Equal(2, again.Attempts);
            Assert.Equal(c.OperationId, again.OperationId);
            Assert.Equal(DeliveryState.Failed, fav.Deliveries(r.EntryId).Single(d => d.TargetInstanceId == "eudic").State);
        }
    }

    [Fact]
    public void Empty_word_is_refused()
    {
        var (db, fav) = Open();
        using (db) Assert.Throws<ArgumentException>(() => fav.Favorite(Card("  "), []));
    }

    [Fact]
    public void Many_entries_stay_fast()
    {
        var (db, fav) = Open();
        using (db)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 2000; i++) fav.Favorite(Card($"word{i}", $$"""{"n":{{i}}}"""), ["anki", "eudic"]);
            long writeMs = sw.ElapsedMilliseconds;
            sw.Restart();
            Assert.Equal(2000, fav.List().Count);
            int claimed = 0;
            while (fav.Claim("anki") is { } d) { fav.Complete(d.EntryId, "anki", d.EntryRevision, DeliveryState.Succeeded); claimed++; }
            Assert.Equal(2000, claimed);
            Assert.True(writeMs < 20_000, $"2000 favorites took {writeMs} ms");
            Assert.True(sw.ElapsedMilliseconds < 20_000, $"list+claim took {sw.ElapsedMilliseconds} ms");
        }
    }
}
