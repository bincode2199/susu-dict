using Susu.Abstractions;
using Susu.Contracts;
using Susu.Jobs;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F15.4 host vocabulary service (DATA05 and DATA06 host side, CFG02 sync status): favorite and unfavorite with and without targets, the
/// loop (start, favorite, timer, stop), per-target isolation, Uncertain/Failed rows with the manual check, retry failed, queueing favorites
/// that predate a target, and a bounded stop that never resends. Real <see cref="FavoritesRepository"/>, scripted targets, manual clock.
/// </summary>
public sealed class VocabServiceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempRoot root = new();
    private readonly ManualClock clock = new();
    private readonly Database db;
    private readonly FavoritesRepository fav;
    private int ids;

    public VocabServiceTests()
    {
        db = Database.Open(root.Paths.Database);
        fav = new FavoritesRepository(db, clock, null, () => $"id{++ids}");
    }

    public void Dispose() { db.Dispose(); root.Dispose(); }

    private sealed class Target(string id, Func<VocabSyncRequest, CancellationToken, Task<VocabSyncOutcome>> handler, bool lookup = false) : IVocabSyncTarget
    {
        private int calls;
        public List<VocabSyncRequest> Requests { get; } = [];
        public int Calls => Volatile.Read(ref calls);
        public string InstanceId => id;
        public bool SupportsLookup => lookup;
        public Task<VocabSyncOutcome> SendAsync(VocabSyncRequest request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            Interlocked.Increment(ref calls);
            return handler(request, cancellationToken);
        }
    }

    private static Task<VocabSyncOutcome> Done(VocabSyncOutcome o) => Task.FromResult(o);
    private static Task<VocabSyncOutcome> Applied(VocabSyncRequest r, CancellationToken _) => Done(new VocabSyncOutcome.Applied("remote-" + r.EntryId));
    private static FavoriteCard Card(string word = "apple") => new("en", word, """{"meanings":[{"pos":"n.","means":["fruit"]}]}""");

    private VocabService Service(List<string> usable, IEnumerable<Target> targets, IVocabFileExporter? exporter = null, VocabSyncPolicy? policy = null)
    {
        var list = targets.ToList();
        return new VocabService(fav, new VocabSyncWorker(fav, id => list.FirstOrDefault(t => t.InstanceId == id), clock, policy), () => [.. usable], clock, exporter, policy);
    }

    private static DeliveryState State(IFavorites f, string entry, string target) => f.Deliveries(entry).Single(d => d.TargetInstanceId == target).State;

    [Fact] // DATA05: a favorite works with no target at all; nothing is queued and nothing is called
    public async Task Favorite_without_any_target_is_local_only()
    {
        var anki = new Target("ankiconnect", Applied);
        await using var service = Service([], [anki]);
        service.Start();

        var result = service.Favorite(Card());

        Assert.Empty(result.QueuedTargets);
        Assert.True(service.IsFavorite("en", "Apple"));
        Assert.Equal(1, service.Count());
        Assert.Empty(fav.Deliveries(result.EntryId));
        Assert.Empty(await service.SyncNowAsync(Ct));
        Assert.Equal(0, anki.Calls);
        Assert.Empty(service.Targets());
    }

    [Fact] // the loop starts at launch and again on every favorite; only usable targets get rows
    public async Task Favorite_queues_only_usable_targets_and_a_pass_delivers_it()
    {
        var anki = new Target("ankiconnect", Applied);
        var eudic = new Target("eudic", Applied);
        await using var service = Service(["ankiconnect"], [anki, eudic]);
        service.Start();
        await Task.Delay(50, Ct); // the first pass at start finds nothing

        var result = service.Favorite(Card());

        Assert.Equal(["ankiconnect"], result.QueuedTargets);
        Assert.True(await Eventually.WaitAsync(() => State(fav, result.EntryId, "ankiconnect") == DeliveryState.Succeeded));
        Assert.Equal(0, eudic.Calls);
        Assert.Equal(1, service.Status(["ankiconnect"])[0].Succeeded);
    }

    [Fact] // the timer: a row waiting for its backoff is picked up by the next poll without any user action
    public async Task Timer_pass_retries_a_row_when_its_backoff_has_elapsed()
    {
        int attempt = 0;
        var anki = new Target("ankiconnect", (r, ct) => Interlocked.Increment(ref attempt) == 1 ? Done(new VocabSyncOutcome.Retry(new ProviderError(ErrorKind.Network, "refused"))) : Applied(r, ct));
        await using var service = Service(["ankiconnect"], [anki]);
        service.Start();
        var result = service.Favorite(Card());
        Assert.True(await Eventually.WaitAsync(() => State(fav, result.EntryId, "ankiconnect") == DeliveryState.RetryWait));
        Assert.Equal(ErrorKind.Network, service.Status(["ankiconnect"])[0].LastError);

        // past the 30 s backoff and the 30 s poll (advanced until the loop has armed its timer)

        Assert.True(await Eventually.WaitAsync(() => { clock.Advance(TimeSpan.FromSeconds(31)); return State(fav, result.EntryId, "ankiconnect") == DeliveryState.Succeeded; }));
        Assert.Null(service.Status(["ankiconnect"])[0].LastError); // cleared once a pass delivered something
    }

    [Fact] // per-target isolation (DATA05 remote half): a target failing with auth leaves the other one delivering
    public async Task One_failing_target_does_not_stop_the_other_and_its_last_error_is_shown()
    {
        var anki = new Target("ankiconnect", Applied);
        var eudic = new Target("eudic", (_, _) => Done(new VocabSyncOutcome.Failure(new ProviderError(ErrorKind.Auth, "401"))));
        await using var service = Service(["ankiconnect", "eudic"], [anki, eudic]);
        var result = service.Favorite(Card());

        await service.SyncNowAsync(Ct);

        Assert.Equal(DeliveryState.Succeeded, State(fav, result.EntryId, "ankiconnect"));
        Assert.Equal(DeliveryState.Failed, State(fav, result.EntryId, "eudic"));
        var status = service.Status(["ankiconnect", "eudic"]);
        Assert.Equal((1, 0, null), (status[0].Succeeded, status[0].Failed, status[0].LastError));
        Assert.Equal((0, 1, ErrorKind.Auth), (status[1].Succeeded, status[1].Failed, status[1].LastError));
        var row = Assert.Single(service.Problems());
        Assert.Equal(("apple", "eudic", DeliveryState.Failed), (row.Word, row.Target, row.State));
    }

    [Fact] // DATA06: Failed rows are retried on request with the same operationId; the error clears when a pass delivers
    public async Task Retry_failed_resends_the_same_operation_and_clears_the_error_after_success()
    {
        bool authOk = false;
        var eudic = new Target("eudic", (r, ct) => authOk ? Applied(r, ct) : Done(new VocabSyncOutcome.Failure(new ProviderError(ErrorKind.Auth, "401"))));
        await using var service = Service(["eudic"], [eudic]);
        var result = service.Favorite(Card());
        string op = fav.Deliveries(result.EntryId).Single().OperationId;
        await service.SyncNowAsync(Ct);
        Assert.Equal(DeliveryState.Failed, State(fav, result.EntryId, "eudic"));

        authOk = true;
        Assert.Equal(1, service.RetryFailed("eudic"));
        await service.SyncNowAsync(Ct);

        Assert.Equal(DeliveryState.Succeeded, State(fav, result.EntryId, "eudic"));
        Assert.All(eudic.Requests, r => Assert.Equal(op, r.OperationId));
        Assert.Equal(2, eudic.Calls);
        Assert.Null(service.Status(["eudic"])[0].LastError);
        Assert.Empty(service.Problems());
    }

    [Fact] // DATA06: a lost answer stays Uncertain, is listed, and is never resent until the user says so
    public async Task Uncertain_rows_are_listed_and_resolve_as_delivered_or_not_delivered_with_the_same_operation()
    {
        bool lost = true;
        var eudic = new Target("eudic", (r, ct) => lost ? Done(new VocabSyncOutcome.Unknown("answer lost")) : Applied(r, ct)); // no lookup: waits for the user
        await using var service = Service(["eudic"], [eudic]);
        var a = service.Favorite(Card("apple"));
        var b = service.Favorite(Card("pear"));
        await service.SyncNowAsync(Ct);
        await service.SyncNowAsync(Ct);
        Assert.Equal(2, eudic.Calls); // each row was tried once; the second pass did not resend (a target with no lookup keeps Uncertain)
        var rows = service.Problems();
        Assert.Contains(rows, r => r.EntryId == a.EntryId && r.State == DeliveryState.Uncertain);

        Assert.True(service.Resolve(a.EntryId, "eudic", 1, delivered: true));
        Assert.Equal(DeliveryState.Succeeded, State(fav, a.EntryId, "eudic"));
        Assert.Equal(2, eudic.Calls);

        lost = false;
        string op = fav.Deliveries(b.EntryId).Single().OperationId;
        Assert.True(service.Resolve(b.EntryId, "eudic", 1, delivered: false));
        await service.SyncNowAsync(Ct);
        Assert.Equal(DeliveryState.Succeeded, State(fav, b.EntryId, "eudic"));
        Assert.Equal(op, eudic.Requests.Last().OperationId);
        Assert.False(service.Resolve(b.EntryId, "eudic", 1, delivered: true)); // not Uncertain/Failed any more
    }

    [Fact] // unfavorite never deletes remotely: the target is not called, a delivered row stays, an unsent one is cancelled
    public async Task Unfavorite_cancels_unsent_rows_and_never_calls_a_target()
    {
        var anki = new Target("ankiconnect", Applied);
        await using var service = Service(["ankiconnect"], [anki]);
        var sent = service.Favorite(Card("apple"));
        await service.SyncNowAsync(Ct);
        var unsent = service.Favorite(Card("pear"));
        int before = anki.Calls;

        Assert.True(service.Unfavorite("en", "apple"));
        Assert.True(service.Unfavorite("en", "pear"));
        await service.SyncNowAsync(Ct);

        Assert.Equal(before, anki.Calls);
        Assert.Equal(DeliveryState.Succeeded, State(fav, sent.EntryId, "ankiconnect"));
        Assert.Equal(DeliveryState.Cancelled, State(fav, unsent.EntryId, "ankiconnect"));
        Assert.Equal(0, service.Count());
    }

    [Fact] // "not delivered" on a row whose word was unfavorited meanwhile is cancelled, never resent
    public async Task Resolving_not_delivered_for_an_unfavorited_entry_cancels_it()
    {
        var eudic = new Target("eudic", (_, _) => Done(new VocabSyncOutcome.Unknown("lost")));
        await using var service = Service(["eudic"], [eudic]);
        var entry = service.Favorite(Card());
        await service.SyncNowAsync(Ct);
        service.Unfavorite("en", "apple");

        Assert.True(service.Resolve(entry.EntryId, "eudic", 1, delivered: false));

        Assert.Equal(DeliveryState.Cancelled, State(fav, entry.EntryId, "eudic"));
        await service.SyncNowAsync(Ct);
        Assert.Equal(1, eudic.Calls);
    }

    [Fact] // a target enabled later takes the favorites that predate it, without touching the rows another target already has
    public async Task Queue_existing_adds_rows_only_for_the_new_target()
    {
        var anki = new Target("ankiconnect", Applied);
        var eudic = new Target("eudic", Applied);
        var usable = new List<string> { "ankiconnect" };
        await using var service = Service(usable, [anki, eudic]);
        var e1 = service.Favorite(Card("apple"));
        var e2 = service.Favorite(Card("pear"));
        await service.SyncNowAsync(Ct);
        usable.Add("eudic");
        Assert.Equal(0, service.Status(["eudic"])[0].Pending);

        Assert.Equal(2, service.QueueExisting("eudic"));
        Assert.Equal(2, service.Status(["eudic"])[0].Pending);
        await service.SyncNowAsync(Ct);

        Assert.Equal(DeliveryState.Succeeded, State(fav, e1.EntryId, "eudic"));
        Assert.Equal(DeliveryState.Succeeded, State(fav, e2.EntryId, "eudic"));
        Assert.Equal(2, anki.Calls); // no second delivery to the first target
        service.QueueExisting("eudic"); // asking again changes nothing for rows that exist
        await service.SyncNowAsync(Ct);
        Assert.Equal(2, eudic.Calls);
    }

    [Fact] // a clean shutdown: the loop stops within the bound; a call in flight is cancelled and its row is Uncertain, not resent
    public async Task Stop_cancels_a_call_in_flight_and_leaves_the_row_uncertain()
    {
        var entered = new TaskCompletionSource();
        var anki = new Target("ankiconnect", async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new VocabSyncOutcome.Absent(); });
        await using var service = Service(["ankiconnect"], [anki]);
        service.Start();
        var result = service.Favorite(Card());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await service.StopAsync(TimeSpan.FromSeconds(5));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4));
        Assert.Equal(DeliveryState.Uncertain, State(fav, result.EntryId, "ankiconnect"));
        Assert.Equal(1, anki.Calls);
        await service.StopAsync(); // idempotent
    }

    [Fact] // the page's export is the exporter over the same snapshot; an unavailable exporter says so
    public void Export_goes_through_the_file_exporter_or_answers_unavailable()
    {
        var plain = Service([], []);
        Assert.False(plain.HasExporter);
        Assert.Equal("unavailable", plain.Export(new VocabExportRequest("txt", "x", true, true, true, false, ""), Ct).Error);

        var exporter = new VocabFileExporter(new VocabExporter(db, fav, clock));
        var service = Service([], [], exporter);
        service.Favorite(Card());
        string path = Path.Combine(root.Root, "out.txt");

        var outcome = service.Export(new VocabExportRequest("txt", path, true, true, true, false, ""), Ct);

        Assert.True(outcome.Ok);
        Assert.Equal((path, 1), (outcome.Path, outcome.Exported));
        Assert.Equal("apple", File.ReadAllText(path).Trim());
    }
}
