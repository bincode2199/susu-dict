using Susu.Abstractions;
using Susu.Contracts;
using Susu.Jobs;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F15.3 host sync worker (DATA05 remote half, DATA06): outbox consumer over the real <see cref="FavoritesRepository"/> with a scripted
/// <see cref="IVocabSyncTarget"/> and a manual clock. The vendor protocols are in <c>VocabPluginTests</c>.
/// </summary>
public sealed class VocabSyncWorkerTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempRoot root = new();
    private readonly ManualClock clock = new();
    private int ids;

    public void Dispose() => root.Dispose();

    private (Database Db, FavoritesRepository Fav) Open()
    {
        var db = Database.Open(root.Paths.Database);
        return (db, new FavoritesRepository(db, clock, null, () => $"id{++ids}"));
    }

    private sealed class Target(string id, bool lookup, Func<VocabSyncRequest, CancellationToken, Task<VocabSyncOutcome>> handler) : IVocabSyncTarget
    {
        public List<VocabSyncRequest> Requests { get; } = [];
        public string InstanceId => id;
        public bool SupportsLookup => lookup;
        public Task<VocabSyncOutcome> SendAsync(VocabSyncRequest request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            return handler(request, cancellationToken);
        }
        public int Count(VocabAction action) { lock (Requests) return Requests.Count(r => r.Action == action); }
    }

    private static Task<VocabSyncOutcome> Done(VocabSyncOutcome outcome) => Task.FromResult(outcome);
    private static VocabSyncOutcome.Retry Offline() => new(new ProviderError(ErrorKind.Network, "refused"));
    private static FavoriteCard Card(string word = "apple") => new("en", word, """{"meanings":["fruit"]}""");

    private VocabSyncWorker Worker(IFavorites fav, VocabSyncPolicy? policy = null, params Target[] targets)
        => new(fav, id => targets.FirstOrDefault(t => t.InstanceId == id), clock, policy);

    private static DeliveryState State(IFavorites fav, string entry, string target) => fav.Deliveries(entry).Single(d => d.TargetInstanceId == target).State;

    [Fact]
    public async Task Success_sends_the_row_operation_id_and_stores_the_remote_id()
    {
        var (db, fav) = Open();
        using var _ = db;
        var entry = fav.Favorite(Card(), ["anki"]);
        var op = fav.Deliveries(entry.EntryId).Single().OperationId;
        var anki = new Target("anki", true, (_, _) => Done(new VocabSyncOutcome.Applied("note-1")));

        var report = await Worker(fav, null, anki).RunTargetAsync("anki", Ct);

        Assert.Equal(1, report.Succeeded);
        var request = Assert.Single(anki.Requests);
        Assert.Equal((op, VocabAction.Upsert, entry.EntryId, 1L, "apple", "en"), (request.OperationId, request.Action, request.EntryId, request.EntryRevision, request.Word, request.Lang));
        var row = fav.Deliveries(entry.EntryId).Single();
        Assert.Equal((DeliveryState.Succeeded, "note-1", 1), (row.State, row.RemoteId, row.Attempts));
        Assert.Equal(0, (await Worker(fav, null, anki).RunTargetAsync("anki", Ct)).Succeeded); // nothing left to send
        Assert.Single(anki.Requests);
    }

    [Fact] // retry boundary and backoff on the fake clock
    public async Task Transient_failure_waits_with_exponential_backoff_then_gives_up_at_the_attempt_limit()
    {
        var (db, fav) = Open();
        using var _ = db;
        var entry = fav.Favorite(Card(), ["anki"]);
        var anki = new Target("anki", true, (_, _) => Done(Offline()));
        var worker = Worker(fav, new VocabSyncPolicy(MaxAttempts: 3), anki);

        Assert.Equal(1, (await worker.RunTargetAsync("anki", Ct)).Retrying);
        var row = fav.Deliveries(entry.EntryId).Single();
        Assert.Equal((DeliveryState.RetryWait, 1), (row.State, row.Attempts));
        Assert.Equal(30_000, row.NextAtMs - clock.UtcNow.ToUnixTimeMilliseconds());

        await worker.RunTargetAsync("anki", Ct); // not due yet
        Assert.Single(anki.Requests);
        clock.Advance(TimeSpan.FromSeconds(29));
        await worker.RunTargetAsync("anki", Ct);
        Assert.Single(anki.Requests);

        clock.Advance(TimeSpan.FromSeconds(1));
        await worker.RunTargetAsync("anki", Ct); // attempt 2: waits 60 s
        row = fav.Deliveries(entry.EntryId).Single();
        Assert.Equal((DeliveryState.RetryWait, 2), (row.State, row.Attempts));
        Assert.Equal(60_000, row.NextAtMs - clock.UtcNow.ToUnixTimeMilliseconds());

        clock.Advance(TimeSpan.FromSeconds(60));
        var last = await worker.RunTargetAsync("anki", Ct); // attempt 3 is the limit
        Assert.Equal(1, last.Failed);
        row = fav.Deliveries(entry.EntryId).Single();
        Assert.Equal((DeliveryState.Failed, 3), (row.State, row.Attempts));
        clock.Advance(TimeSpan.FromDays(1));
        await worker.RunTargetAsync("anki", Ct);
        Assert.Equal(3, anki.Requests.Count);
    }

    [Fact]
    public async Task Vendor_retry_after_is_honoured_up_to_the_cap()
    {
        var (db, fav) = Open();
        using var _ = db;
        var entry = fav.Favorite(Card(), ["anki"]);
        var anki = new Target("anki", true, (_, _) => Done(new VocabSyncOutcome.Retry(new ProviderError(ErrorKind.RateLimited, "429", TimeSpan.FromMinutes(5)))));
        await Worker(fav, null, anki).RunTargetAsync("anki", Ct);
        Assert.Equal(300_000, fav.Deliveries(entry.EntryId).Single().NextAtMs - clock.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(TimeSpan.FromHours(1), new VocabSyncPolicy().Backoff(1, TimeSpan.FromDays(2)));
        Assert.Equal(TimeSpan.FromHours(1), new VocabSyncPolicy().Backoff(25, null));
    }

    [Fact] // a target-wide outage ends the pass: the remaining rows are not burned one attempt each
    public async Task Offline_target_stops_the_pass_and_leaves_the_other_rows_untouched()
    {
        var (db, fav) = Open();
        using var _ = db;
        var a = fav.Favorite(Card("apple"), ["anki"]); fav.Favorite(Card("pear"), ["anki"]); fav.Favorite(Card("plum"), ["anki"]);
        var anki = new Target("anki", true, (_, _) => Done(Offline()));
        var report = await Worker(fav, null, anki).RunTargetAsync("anki", Ct);
        Assert.Equal(1, report.Retrying);
        Assert.Single(anki.Requests);
        Assert.Equal(2, fav.List().Count(e => fav.Deliveries(e.EntryId).Single().State == DeliveryState.Pending));
        Assert.Equal(DeliveryState.RetryWait, State(fav, a.EntryId, "anki"));
    }

    [Fact] // DATA05: one target failing does not block the other
    public async Task Targets_are_isolated_failure_throw_and_hang_do_not_block_the_other()
    {
        var (db, fav) = Open();
        using var _ = db;
        var entry = fav.Favorite(Card(), ["anki", "eudic", "third"]);
        var anki = new Target("anki", true, (_, _) => Done(new VocabSyncOutcome.Failure(new ProviderError(ErrorKind.Auth, "401"))));
        var eudic = new Target("eudic", false, (_, _) => Done(new VocabSyncOutcome.Applied("apple")));
        var third = new Target("third", false, (_, _) => throw new InvalidOperationException("boom"));
        var reports = await Worker(fav, null, anki, eudic, third).RunAllAsync(["anki", "eudic", "third", "missing"], Ct);

        Assert.Equal(DeliveryState.Failed, State(fav, entry.EntryId, "anki"));
        Assert.Equal(DeliveryState.Succeeded, State(fav, entry.EntryId, "eudic"));
        Assert.Equal(DeliveryState.Uncertain, State(fav, entry.EntryId, "third")); // threw mid-call: nobody knows
        Assert.Equal("no such target", reports.Single(r => r.Target == "missing").Error);
        Assert.Equal(1, reports.Single(r => r.Target == "eudic").Succeeded);
    }

    [Fact]
    public async Task A_hung_target_does_not_stop_the_other_and_cancel_leaves_its_row_uncertain()
    {
        var (db, fav) = Open();
        using var _ = db;
        var entry = fav.Favorite(Card(), ["anki", "eudic"]);
        var entered = new TaskCompletionSource();
        var anki = new Target("anki", true, async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return new VocabSyncOutcome.Applied(null); });
        var eudic = new Target("eudic", false, (_, _) => Done(new VocabSyncOutcome.Applied("x")));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = Worker(fav, null, anki, eudic).RunAllAsync(["anki", "eudic"], cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        for (int i = 0; i < 200 && State(fav, entry.EntryId, "eudic") != DeliveryState.Succeeded; i++) await Task.Delay(10, Ct);
        Assert.Equal(DeliveryState.Succeeded, State(fav, entry.EntryId, "eudic"));
        Assert.Equal(DeliveryState.Sending, State(fav, entry.EntryId, "anki"));
        cts.Cancel();
        await run;
        Assert.Equal(DeliveryState.Uncertain, State(fav, entry.EntryId, "anki"));
    }

    [Fact] // DATA06 for a target without a reliable lookup: Uncertain is shown to the user and never resent
    public async Task Lost_response_without_lookup_stays_uncertain_and_is_never_resent()
    {
        var (db, fav) = Open();
        using var _ = db;
        var entry = fav.Favorite(Card(), ["eudic"]);
        var eudic = new Target("eudic", false, (_, _) => Done(new VocabSyncOutcome.Unknown("answer lost")));
        var worker = Worker(fav, null, eudic);
        Assert.Equal(1, (await worker.RunTargetAsync("eudic", Ct)).Uncertain);
        for (int i = 0; i < 3; i++) { clock.Advance(TimeSpan.FromHours(2)); await worker.RunTargetAsync("eudic", Ct); }
        Assert.Single(eudic.Requests);
        Assert.Equal(DeliveryState.Uncertain, State(fav, entry.EntryId, "eudic"));

        // manual check: the user says it is there, or asks for a resend (same operationId)
        var op = fav.Deliveries(entry.EntryId).Single().OperationId;
        Assert.True(fav.Resolve(entry.EntryId, "eudic", 1, delivered: false));
        var okay = new Target("eudic", false, (_, _) => Done(new VocabSyncOutcome.Applied("r")));
        await Worker(fav, null, okay).RunTargetAsync("eudic", Ct);
        Assert.Equal(op, okay.Requests.Single().OperationId);
        Assert.Equal(DeliveryState.Succeeded, State(fav, entry.EntryId, "eudic"));
        Assert.False(fav.Resolve(entry.EntryId, "eudic", 1, delivered: true)); // not a row that needs a check
    }

    [Fact] // DATA06 with lookup: the row left Sending by a crash is confirmed (found) without any second write
    public async Task Restart_after_a_crash_confirms_by_lookup_and_does_not_write_again()
    {
        VocabSyncOutcome.Found found = new("note-7");
        string id;
        {
            var (db, fav) = Open();
            using var _ = db;
            id = fav.Favorite(Card(), ["anki"]).EntryId;
            Assert.NotNull(fav.Claim("anki")); // the app dies while the request is in flight
        }
        {
            var (db, fav) = Open();
            using var _ = db;
            Assert.Equal(1, fav.RecoverInterrupted());
            Assert.Equal(DeliveryState.Uncertain, State(fav, id, "anki"));
            var anki = new Target("anki", true, (r, _) => Done(r.Action == VocabAction.Lookup ? found : throw new InvalidOperationException("must not write")));
            var report = await Worker(fav, null, anki).RunTargetAsync("anki", Ct);
            Assert.Equal((1, 1), (report.Succeeded, report.Confirmed));
            Assert.Equal([VocabAction.Lookup], anki.Requests.Select(r => r.Action));
            var row = fav.Deliveries(id).Single();
            Assert.Equal((DeliveryState.Succeeded, "note-7", 1), (row.State, row.RemoteId, row.Attempts));
        }
    }

    [Fact] // DATA06 with lookup: absent means the write never landed, so it is back-filled exactly once
    public async Task Restart_with_absent_lookup_back_fills_once()
    {
        var (db, fav) = Open();
        using var _ = db;
        var entry = fav.Favorite(Card(), ["anki"]);
        fav.Claim("anki");
        fav.RecoverInterrupted();
        var anki = new Target("anki", true, (r, _) => Done(r.Action == VocabAction.Lookup ? new VocabSyncOutcome.Absent() : new VocabSyncOutcome.Applied("n1")));
        var worker = Worker(fav, null, anki);
        await worker.RunTargetAsync("anki", Ct);
        await worker.RunTargetAsync("anki", Ct);
        Assert.Equal([VocabAction.Lookup, VocabAction.Upsert], anki.Requests.Select(r => r.Action));
        Assert.Equal(DeliveryState.Succeeded, State(fav, entry.EntryId, "anki"));
    }

    [Fact] // a lookup that cannot be answered keeps the row Uncertain and the pass short
    public async Task Lookup_that_cannot_be_answered_leaves_the_row_uncertain()
    {
        var (db, fav) = Open();
        using var _ = db;
        var entry = fav.Favorite(Card(), ["anki"]);
        fav.Claim("anki");
        fav.RecoverInterrupted();
        fav.Favorite(Card("pear"), ["anki"]);
        var anki = new Target("anki", true, (_, _) => Done(Offline()));
        var report = await Worker(fav, null, anki).RunTargetAsync("anki", Ct);
        Assert.Single(anki.Requests); // the pear is not tried either: the target is down
        Assert.Equal(1, report.Uncertain);
        Assert.Equal(DeliveryState.Uncertain, State(fav, entry.EntryId, "anki"));
    }

    [Fact] // in-process lost answer: the next pass settles it by lookup before anything is resent
    public async Task Unknown_then_next_pass_found_by_lookup_has_one_write()
    {
        var (db, fav) = Open();
        using var _ = db;
        var entry = fav.Favorite(Card(), ["anki"]);
        bool landed = false;
        var anki = new Target("anki", true, (r, _) =>
        {
            if (r.Action == VocabAction.Upsert) { landed = true; return Done(new VocabSyncOutcome.Unknown("answer lost")); }
            return Done(landed ? new VocabSyncOutcome.Found("n9") : new VocabSyncOutcome.Absent());
        });
        var worker = Worker(fav, null, anki);
        await worker.RunTargetAsync("anki", Ct);
        Assert.Equal(DeliveryState.Uncertain, State(fav, entry.EntryId, "anki"));
        await worker.RunTargetAsync("anki", Ct);
        Assert.Equal(DeliveryState.Succeeded, State(fav, entry.EntryId, "anki"));
        Assert.Equal(1, anki.Count(VocabAction.Upsert));
    }

    [Fact] // unfavorite never reaches the remote: the worker has no delete, cancelled rows are not sent, sent ones stay
    public async Task Unfavorite_cancels_unsent_rows_and_the_worker_never_deletes_remotely()
    {
        var (db, fav) = Open();
        using var _ = db;
        var sent = fav.Favorite(Card("apple"), ["anki"]);
        var unsent = fav.Favorite(Card("pear"), ["anki"]);
        var anki = new Target("anki", true, (_, _) => Done(new VocabSyncOutcome.Applied("n")));
        fav.Claim("anki"); fav.Complete(sent.EntryId, "anki", 1, DeliveryState.Succeeded, "n"); // apple went out earlier
        Assert.True(fav.Unfavorite("en", "apple"));
        Assert.True(fav.Unfavorite("en", "pear"));
        await Worker(fav, null, anki).RunTargetAsync("anki", Ct);
        Assert.Empty(anki.Requests);
        Assert.Equal(DeliveryState.Succeeded, State(fav, sent.EntryId, "anki"));
        Assert.Equal(DeliveryState.Cancelled, State(fav, unsent.EntryId, "anki"));
        Assert.Equal(2, Enum.GetValues<VocabAction>().Length); // upsert and lookup are the whole port: no delete action exists
    }

    [Fact]
    public async Task The_loop_polls_on_the_clock_and_stops_when_cancelled()
    {
        var (db, fav) = Open();
        using var _ = db;
        var anki = new Target("anki", true, (_, _) => Done(new VocabSyncOutcome.Applied("n")));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loop = Worker(fav, null, anki).RunAsync(() => ["anki"], cts.Token);
        for (int i = 0; i < 200 && clock.PendingTimers == 0; i++) await Task.Delay(10, Ct);
        var entry = fav.Favorite(Card(), ["anki"]);
        Assert.Empty(anki.Requests); // nothing before the next poll
        clock.Advance(TimeSpan.FromSeconds(30));
        for (int i = 0; i < 200 && State(fav, entry.EntryId, "anki") != DeliveryState.Succeeded; i++) await Task.Delay(10, Ct);
        Assert.Equal(DeliveryState.Succeeded, State(fav, entry.EntryId, "anki"));
        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }
}
