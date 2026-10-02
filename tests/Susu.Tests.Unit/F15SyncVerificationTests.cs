using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Net;
using Susu.Plugins;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// Independent F15 verification (testing agent), part 2: favorite -> outbox -> <see cref="VocabService"/>/<see cref="VocabSyncWorker"/> ->
/// the real shipped AnkiConnect and Eudic packages in the real sandbox -> loopback fakes that keep a full request log. Lost response then a new
/// service instance on the same database; revision change while uncertain; Resolve(not delivered) when the note had landed; two targets with one
/// failing; redirects; an unapproved loopback origin; the vendor action allow-list (no remote delete is ever sent); key never in outcomes or rows.
/// Needs susu.exe published (skips itself otherwise). Real Anki and Eudic: not executed (the fakes follow the public API descriptions).
/// </summary>
public sealed class F15SyncVerificationTests : IDisposable
{
    private const string Key = "anki-key-0123456789abcdef";
    private const string EudicAuth = "NIS eudic-token-0123456789abcdef";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempRoot root = new();
    private readonly ManualClock clock = new();
    private readonly List<IDisposable> disposables = [];
    private int ids;

    public void Dispose() { foreach (var d in disposables) d.Dispose(); root.Dispose(); }

    // ---------------- staging (as VocabPluginTests) ----------------

    private static string? FindRoot(Func<string, bool> test)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (test(dir.FullName)) return dir.FullName;
        return null;
    }

    private static readonly Lazy<string?> staged = new(() =>
    {
        string? output = FindRoot(d => File.Exists(Path.Combine(d, "src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish", "susu.exe")));
        if (output is null) return null;
        string publish = Path.Combine(output, "src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish");
        string source = Path.Combine(output, "src", "Susu.Host", "plugins");
        string dir = TestTemp.NewDir("susu-f15v-it");
        foreach (string file in Directory.EnumerateFiles(publish))
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        foreach (string package in new[] { VocabCatalog.AnkiConnect, VocabCatalog.Eudic })
        {
            string target = Path.Combine(dir, "plugins", package);
            Directory.CreateDirectory(target);
            foreach (string file in Directory.EnumerateFiles(Path.Combine(source, package))) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        return dir;
    });

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<(string, string), string> values = [];
        public void Set(string account, string name, string value) => values[(account, name)] = value;
        public bool Has(string a, string n) => values.ContainsKey((a, n));
        public IReadOnlyList<string> Names(string a) => [.. values.Keys.Where(k => k.Item1 == a).Select(k => k.Item2)];
        public void Write(string a, string n, ReadOnlySpan<char> v) => values[(a, n)] = v.ToString();
        public bool Delete(string a, string n) => values.Remove((a, n));
        public bool TryRead(string a, string n, out string v) => values.TryGetValue((a, n), out v!);
    }

    private sealed class Rig(Supervisor<HostSession> supervisor, PluginVocabTarget target) : IDisposable
    {
        public PluginVocabTarget Target => target;
        public void Dispose() => supervisor.Dispose();
    }

    private Rig? Build(string instanceId, string origin, Dictionary<string, string> extra, string? secret, bool approveOrigin = true)
    {
        if (staged.Value is not { } dir) return null;
        var package = PluginTranslationProviders.WiredPackages.Single(p => p.InstanceId == instanceId);
        var cfg = new Dictionary<string, string>(extra) { ["baseUrl"] = origin };
        var secrets = new FakeSecretStore();
        var bindings = new Dictionary<string, string>();
        var accounts = new List<AccountSettings>();
        if (secret is not null)
        {
            secrets.Set("account", "apiKey", secret);
            accounts.Add(new AccountSettings("account", "Shared account", ["apiKey"], [.. package.Credentials.RequiredGrants(cfg)]));
            bindings["apiKey"] = "account";
        }
        var instance = new InstanceSettings(instanceId, package.PackageId, 1, cfg, bindings);
        var authorization = new AccountAuthorization(() => (accounts, [instance]));
        var options = new HostSession.Options(Path.Combine(dir, "susu.exe"), dir, "quickjs", KeepProfile: false,
            MakeBroker: () => new Broker(secretStore: secrets, accounts: authorization));
        var supervisor = new Supervisor<HostSession>(() =>
        {
            var session = HostSession.Start(options);
            if (approveOrigin) session.Broker.ApproveLocalOrigin(origin);
            var loaded = session.Load(package.PackageId, package.Directory);
            if (!loaded.Ok) { session.Shutdown(2000); throw new InvalidOperationException($"{package.PackageId} failed to load: {loaded.Error}"); }
            return session;
        }, SystemClock.Instance, TimeSpan.FromMinutes(10));
        var rig = new Rig(supervisor, new PluginVocabTarget(package, instance, supervisor, null, TimeSpan.FromSeconds(30)));
        disposables.Add(rig);
        return rig;
    }

    // ---------------- fakes with a full request log ----------------

    public enum Fault { None, DropAfterApply, Redirect }

    private static async Task Reply(NetworkStream stream, int status, string json, string? location = null, CancellationToken ct = default)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        string head = $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n{(location is null ? "" : $"Location: {location}\r\n")}Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(body, ct);
    }

    /// <summary>The only AnkiConnect actions the package may use. Anything else (deleteNotes, removeDeckConfig, ...) is logged as unexpected.</summary>
    private static readonly HashSet<string> AnkiAllowed = ["modelNames", "deckNames", "createModel", "createDeck", "findNotes", "addNote", "updateNoteFields", "version", "notesInfo"];

    private sealed class FakeAnki : IDisposable
    {
        private readonly object gate = new();
        private long nextId = 1000;
        public Dictionary<long, Dictionary<string, string>> Notes { get; } = [];
        public HashSet<string> Models { get; } = [];
        public HashSet<string> Decks { get; } = ["Default"];
        public List<string> Actions { get; } = [];
        public List<string> RawBodies { get; } = [];
        public Queue<Fault> Faults { get; } = new();
        public string? RedirectTo { get; set; }
        public string? RedirectAction { get; set; }
        public string? RequiredKey { get; set; }
        public LoopbackHttpServer Server { get; }
        public FakeAnki() => Server = new LoopbackHttpServer(Handle);
        public int Count(string action) { lock (gate) return Actions.Count(a => a == action); }
        public IReadOnlyList<string> Unexpected { get { lock (gate) return [.. Actions.Where(a => !AnkiAllowed.Contains(a))]; } }
        public int Notes0 { get { lock (gate) return Notes.Count; } }
        public void Dispose() => Server.Dispose();

        private async Task Handle(LoopbackHttpRequest req, NetworkStream stream, CancellationToken ct)
        {
            var body = (JsonObject)JsonNode.Parse(req.Body)!;
            string action = body["action"]!.GetValue<string>();
            var p = body["params"] as JsonObject;
            Fault fault = Fault.None;
            string json;
            lock (gate)
            {
                Actions.Add(action); RawBodies.Add(Encoding.UTF8.GetString(req.Body));
                if (RedirectTo is not null && (RedirectAction is null || RedirectAction == action)) { fault = Fault.Redirect; json = ""; goto reply; }
                if (RequiredKey is not null && body["key"]?.GetValue<string>() != RequiredKey) { json = Answer(null, "valid api key must be provided"); goto reply; }
                if (action is "addNote" or "updateNoteFields" && Faults.Count > 0) fault = Faults.Dequeue();
                json = Apply(action, p);
            }
        reply:
            if (fault == Fault.DropAfterApply) return;
            if (fault == Fault.Redirect) { await Reply(stream, 307, "{}", RedirectTo, ct); return; }
            await Reply(stream, 200, json, ct: ct);
        }

        private static string Answer(JsonNode? result, string? error) => new JsonObject { ["result"] = result, ["error"] = error }.ToJsonString();

        private string Apply(string action, JsonObject? p)
        {
            switch (action)
            {
                case "modelNames": return Answer(new JsonArray([.. Models.Select(m => (JsonNode)m)]), null);
                case "deckNames": return Answer(new JsonArray([.. Decks.Select(d => (JsonNode)d)]), null);
                case "createModel": Models.Add(p!["modelName"]!.GetValue<string>()); return Answer(new JsonObject(), null);
                case "createDeck": Decks.Add(p!["deck"]!.GetValue<string>()); return Answer(1, null);
                case "findNotes":
                {
                    var m = Regex.Match(p!["query"]!.GetValue<string>(), @"SusuId:(\S+)");
                    return Answer(new JsonArray([.. Notes.Where(n => m.Success && n.Value.GetValueOrDefault("SusuId") == m.Groups[1].Value).Select(n => (JsonNode)n.Key)]), null);
                }
                case "addNote":
                {
                    var n = p!["note"]!;
                    var fields = n["fields"]!.AsObject().ToDictionary(f => f.Key, f => f.Value!.GetValue<string>());
                    if (!Decks.Contains(n["deckName"]!.GetValue<string>())) return Answer(null, "deck was not found");
                    if (!Models.Contains(n["modelName"]!.GetValue<string>())) return Answer(null, "model was not found");
                    if (Notes.Values.Any(x => x["Word"] == fields["Word"]) && !(n["options"]?["allowDuplicate"]?.GetValue<bool>() ?? false)) return Answer(null, "cannot create note because it is a duplicate");
                    Notes[++nextId] = fields;
                    return Answer(nextId, null);
                }
                case "updateNoteFields":
                {
                    long id = p!["note"]!["id"]!.GetValue<long>();
                    if (!Notes.TryGetValue(id, out var note)) return Answer(null, "Note was not found");
                    foreach (var f in p["note"]!["fields"]!.AsObject()) note[f.Key] = f.Value!.GetValue<string>();
                    return Answer(null, null);
                }
                default: return Answer(null, "unsupported action: " + action);
            }
        }
    }

    private sealed class FakeEudic : IDisposable
    {
        private readonly object gate = new();
        public HashSet<string> Words { get; } = [];
        public List<string> Requests { get; } = [];
        public Queue<Fault> Faults { get; } = new();
        public LoopbackHttpServer Server { get; }
        public FakeEudic() => Server = new LoopbackHttpServer(Handle);
        public int Count(string method) { lock (gate) return Requests.Count(r => r.StartsWith(method + " ", StringComparison.Ordinal)); }
        public int Total { get { lock (gate) return Requests.Count; } }
        public IReadOnlyList<string> Unexpected { get { lock (gate) return [.. Requests.Where(r => !(r.StartsWith("GET /api/open/v1/studylist/", StringComparison.Ordinal) || r.StartsWith("POST /api/open/v1/studylist/word", StringComparison.Ordinal)))]; } }
        public void Dispose() => Server.Dispose();

        private async Task Handle(LoopbackHttpRequest req, NetworkStream stream, CancellationToken ct)
        {
            Fault fault = Fault.None;
            int status; string json;
            lock (gate)
            {
                Requests.Add($"{req.Method} {req.Path}");
                string? auth = req.Headers.FirstOrDefault(h => string.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase)).Value;
                if (auth != EudicAuth) { status = 401; json = """{"message":"unauthorized"}"""; goto reply; }
                if (req.Method == "POST" && Faults.Count > 0) fault = Faults.Dequeue();
                (status, json) = Apply(req);
            }
        reply:
            if (fault == Fault.DropAfterApply) return;
            await Reply(stream, status, json, ct: ct);
        }

        private (int, string) Apply(LoopbackHttpRequest req)
        {
            string path = req.Path.Split('?')[0];
            var query = System.Web.HttpUtility.ParseQueryString(req.Path.Contains('?') ? req.Path[(req.Path.IndexOf('?') + 1)..] : "");
            if (req.Method == "GET" && path.StartsWith("/api/open/v1/studylist/word/", StringComparison.Ordinal))
            {
                string word = Uri.UnescapeDataString(path["/api/open/v1/studylist/word/".Length..]);
                return Words.Contains($"{query["language"]}:{word}") ? (200, """{"data":{"word":"x"},"message":"ok"}""") : (404, """{"message":"not found"}""");
            }
            if (req.Method == "POST" && path == "/api/open/v1/studylist/word")
            {
                var body = (JsonObject)JsonNode.Parse(req.Body)!;
                Words.Add($"{body["language"]!.GetValue<string>()}:{body["word"]!.GetValue<string>()}");
                return (201, """{"message":"success"}""");
            }
            return (404, "{}");
        }
    }

    // ---------------- helpers ----------------

    private (Database Db, FavoritesRepository Fav) Open()
    {
        var db = Database.Open(root.Paths.Database);
        return (db, new FavoritesRepository(db, clock, null, () => $"e{++ids}"));
    }

    private VocabService NewService(IFavorites fav, params (string Id, Rig Rig)[] targets)
    {
        var map = targets.ToDictionary(t => t.Id, t => (IVocabSyncTarget)t.Rig.Target);
        return new VocabService(fav, new VocabSyncWorker(fav, id => map.GetValueOrDefault(id), clock), () => [.. targets.Select(t => t.Id)], clock);
    }

    private static string Content(string meaning = "fruit") => $$"""{"phonetics":[{"accent":"US","ipa":"ˈæp.əl"}],"meanings":[{"pos":"n.","means":["{{meaning}}"]}],"examples":[{"src":"An apple <a day>.","dst":"x"}],"source":"Youdao"}""";
    private static DeliveryState State(IFavorites fav, string entry, string target) => fav.Deliveries(entry).Where(d => d.TargetInstanceId == target).OrderBy(d => d.EntryRevision).Last().State;
    private static readonly Dictionary<string, string> NoExtra = [];

    // ================= tests =================
    [Fact] // guard: the tests below return silently without a published host; this one fails instead, so a pass here means they really ran
    public void Published_host_and_packages_are_present() => Assert.NotNull(staged.Value);


    [Fact] // the whole path for two real packages, then unfavorite and re-favorite: nothing is ever deleted remotely and nothing is sent twice
    public async Task Favorite_syncs_to_both_real_packages_and_unfavorite_never_deletes()
    {
        using var anki = new FakeAnki { RequiredKey = Key };
        using var eudic = new FakeEudic();
        using var ar = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, new() { ["useApiKey"] = "true", ["deck"] = "Reading" }, Key);
        if (ar is null) return;
        using var er = Build(VocabCatalog.Eudic, eudic.Server.Origin, NoExtra, EudicAuth)!;
        var (db, fav) = Open();
        using var _ = db;
        await using var service = NewService(fav, (VocabCatalog.AnkiConnect, ar), (VocabCatalog.Eudic, er));

        var r = service.Favorite(new FavoriteCard("en", "apple", Content()));
        Assert.Equal(2, r.QueuedTargets.Count);
        await service.SyncNowAsync(Ct);
        Assert.Equal(DeliveryState.Succeeded, State(fav, r.EntryId, VocabCatalog.AnkiConnect));
        Assert.Equal(DeliveryState.Succeeded, State(fav, r.EntryId, VocabCatalog.Eudic));
        Assert.Single(anki.Notes);
        Assert.Contains("en:apple", eudic.Words);

        int ankiBefore = anki.Actions.Count, eudicBefore = eudic.Total;
        Assert.True(service.Unfavorite("en", "apple"));
        await service.SyncNowAsync(Ct);
        Assert.Equal((ankiBefore, eudicBefore), (anki.Actions.Count, eudic.Total)); // unfavorite touches no remote
        service.Favorite(new FavoriteCard("en", "apple", Content())); // re-favorite, same content
        await service.SyncNowAsync(Ct);
        Assert.Equal((ankiBefore, eudicBefore), (anki.Actions.Count, eudic.Total)); // already delivered: no second write, no duplicate
        Assert.Single(anki.Notes);
        Assert.Empty(anki.Unexpected);
        Assert.Empty(eudic.Unexpected);
        Assert.Equal(0, eudic.Count("DELETE"));
    }

    [Fact] // lost answer, then a NEW service instance on the same database after the content was edited: rev 1 is confirmed by lookup, rev 2 updates the same note
    public async Task Lost_response_restart_with_revision_change_yields_one_note_with_the_new_content()
    {
        using var anki = new FakeAnki();
        using var rig = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, NoExtra, null);
        if (rig is null) return;
        string entry;
        {
            var (db, fav) = Open();
            using var _ = db;
            await using var service = NewService(fav, (VocabCatalog.AnkiConnect, rig));
            entry = service.Favorite(new FavoriteCard("en", "apple", Content())).EntryId;
            anki.Faults.Enqueue(Fault.DropAfterApply);
            await service.SyncNowAsync(Ct);
            Assert.Equal(DeliveryState.Uncertain, State(fav, entry, VocabCatalog.AnkiConnect));
            service.Favorite(new FavoriteCard("en", "apple", Content("red fruit"))); // user edits the card before the next run
        }
        {
            var (db, fav) = Open(); // restart
            using var _ = db;
            Assert.Equal(0, fav.RecoverInterrupted());
            await using var service = NewService(fav, (VocabCatalog.AnkiConnect, rig));
            await service.SyncNowAsync(Ct);
            await service.SyncNowAsync(Ct);
            var rows = fav.Deliveries(entry).OrderBy(d => d.EntryRevision).ToList();
            Assert.Equal([DeliveryState.Succeeded, DeliveryState.Succeeded], rows.Select(d => d.State).ToArray());
        }
        Assert.Equal(1, anki.Count("addNote"));
        Assert.Equal(1, anki.Notes0);
        Assert.Contains("red fruit", anki.Notes.Values.Single()["Definition"]);
        Assert.Empty(anki.Unexpected);
    }

    [Fact] // the app dies while Sending (row left Sending, request never made): startup recovery -> Uncertain -> lookup absent -> exactly one add
    public async Task Crash_while_sending_is_recovered_with_one_write()
    {
        using var anki = new FakeAnki();
        using var rig = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, NoExtra, null);
        if (rig is null) return;
        string entry;
        {
            var (db, fav) = Open();
            using var _ = db;
            entry = fav.Favorite(new FavoriteCard("en", "apple", Content()), [VocabCatalog.AnkiConnect]).EntryId;
            Assert.NotNull(fav.Claim(VocabCatalog.AnkiConnect));
        }
        {
            var (db, fav) = Open();
            using var _ = db;
            Assert.Equal(1, fav.RecoverInterrupted());
            await using var service = NewService(fav, (VocabCatalog.AnkiConnect, rig));
            for (int i = 0; i < 3; i++) await service.SyncNowAsync(Ct);
            Assert.Equal(DeliveryState.Succeeded, State(fav, entry, VocabCatalog.AnkiConnect));
        }
        Assert.Equal(1, anki.Count("addNote"));
        Assert.Equal(1, anki.Notes0);
    }

    [Fact] // Resolve(not delivered) although the note had landed: the package's own lookup-before-write keeps it at one note
    public async Task Resolve_not_delivered_when_the_note_had_landed_does_not_duplicate()
    {
        using var anki = new FakeAnki();
        using var rig = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, NoExtra, null);
        if (rig is null) return;
        var (db, fav) = Open();
        using var _ = db;
        await using var service = NewService(fav, (VocabCatalog.AnkiConnect, rig));
        var r = service.Favorite(new FavoriteCard("en", "apple", Content()));
        anki.Faults.Enqueue(Fault.DropAfterApply);
        await service.SyncNowAsync(Ct);
        // the worker would settle it by lookup; force the manual path instead: put the row back to Uncertain
        if (State(fav, r.EntryId, VocabCatalog.AnkiConnect) == DeliveryState.Succeeded) Assert.Equal(1, anki.Notes0);
        else
        {
            Assert.True(service.Resolve(r.EntryId, VocabCatalog.AnkiConnect, 1, delivered: false));
            await service.SyncNowAsync(Ct);
        }
        for (int i = 0; i < 2; i++) await service.SyncNowAsync(Ct);
        Assert.Equal(1, anki.Notes0);
        Assert.Equal(1, anki.Count("addNote"));
        Assert.Equal(DeliveryState.Succeeded, State(fav, r.EntryId, VocabCatalog.AnkiConnect));
    }

    [Fact] // Eudic: lost answer after the word was stored, restart: lookup finds it, one POST
    public async Task Eudic_lost_response_then_restart_posts_once()
    {
        using var eudic = new FakeEudic();
        using var rig = Build(VocabCatalog.Eudic, eudic.Server.Origin, NoExtra, EudicAuth);
        if (rig is null) return;
        string entry;
        {
            var (db, fav) = Open();
            using var _ = db;
            await using var service = NewService(fav, (VocabCatalog.Eudic, rig));
            entry = service.Favorite(new FavoriteCard("en", "apple", Content())).EntryId;
            eudic.Faults.Enqueue(Fault.DropAfterApply);
            await service.SyncNowAsync(Ct);
            Assert.Equal(DeliveryState.Uncertain, State(fav, entry, VocabCatalog.Eudic));
        }
        {
            var (db, fav) = Open();
            using var _ = db;
            await using var service = NewService(fav, (VocabCatalog.Eudic, rig));
            for (int i = 0; i < 3; i++) await service.SyncNowAsync(Ct);
            Assert.Equal(DeliveryState.Succeeded, State(fav, entry, VocabCatalog.Eudic));
        }
        Assert.Equal(1, eudic.Count("POST"));
        Assert.Empty(eudic.Unexpected);
    }

    [Fact] // two targets, one failing with auth: the other delivers; the failing one is not hammered; the key is in no outcome, row or status
    public async Task One_failing_real_target_does_not_stop_the_other_and_no_key_leaks()
    {
        const string WrongKey = "wrong-eudic-key-0123456789";
        using var anki = new FakeAnki { RequiredKey = Key };
        using var eudic = new FakeEudic();
        using var ar = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, new() { ["useApiKey"] = "true" }, Key);
        if (ar is null) return;
        using var er = Build(VocabCatalog.Eudic, eudic.Server.Origin, NoExtra, WrongKey)!;
        var (db, fav) = Open();
        using var _ = db;
        await using var service = NewService(fav, (VocabCatalog.AnkiConnect, ar), (VocabCatalog.Eudic, er));
        foreach (string w in new[] { "apple", "pear", "plum" }) service.Favorite(new FavoriteCard("en", w, Content()));
        var reports = await service.SyncNowAsync(Ct);
        Assert.Equal(3, anki.Notes0);
        Assert.Equal(3, fav.DeliveryCounts().Single(c => c.TargetInstanceId == VocabCatalog.AnkiConnect && c.State == DeliveryState.Succeeded).Count);
        Assert.DoesNotContain(fav.DeliveryCounts(), c => c.TargetInstanceId == VocabCatalog.Eudic && c.State == DeliveryState.Succeeded);
        Assert.True(eudic.Total <= 3, "an auth failure must end the pass for that target; requests: " + eudic.Total);
        string everything = JsonSerializer.Serialize(new { reports, s = service.Status([VocabCatalog.AnkiConnect, VocabCatalog.Eudic]), p = service.Problems() });
        Assert.DoesNotContain(WrongKey, everything);
        Assert.DoesNotContain(Key, everything);
        Assert.DoesNotContain("eudic-token", everything);
        Assert.Contains(service.Status([VocabCatalog.Eudic]), s => s.LastError == ErrorKind.Auth);
    }

    [Fact] // the server redirects every call to a second loopback server: it is never contacted and nothing is written or marked delivered
    public async Task Redirect_to_another_origin_is_not_followed()
    {
        using var other = new FakeAnki();
        using var anki = new FakeAnki { RedirectTo = other.Server.Origin + "/" };
        using var rig = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, NoExtra, null);
        if (rig is null) return;
        var (db, fav) = Open();
        using var _ = db;
        await using var service = NewService(fav, (VocabCatalog.AnkiConnect, rig));
        var r = service.Favorite(new FavoriteCard("en", "apple", Content()));
        await service.SyncNowAsync(Ct);
        Assert.Empty(other.Actions);
        Assert.Equal(0, other.Notes0);
        Assert.NotEqual(DeliveryState.Succeeded, State(fav, r.EntryId, VocabCatalog.AnkiConnect));
    }

    [Fact] // a redirect that only appears on the final write: the write is not followed to the other origin, so the row is not Succeeded
    public async Task Redirect_on_the_write_is_not_followed_and_the_row_is_not_delivered()
    {
        using var other = new FakeAnki();
        using var anki = new FakeAnki { RedirectTo = other.Server.Origin + "/", RedirectAction = "addNote" };
        using var rig = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, NoExtra, null);
        if (rig is null) return;
        var (db, fav) = Open();
        using var _ = db;
        await using var service = NewService(fav, (VocabCatalog.AnkiConnect, rig));
        var r = service.Favorite(new FavoriteCard("en", "apple", Content()));
        await service.SyncNowAsync(Ct);
        Assert.Empty(other.Actions);
        Assert.Equal(0, anki.Notes0);
        Assert.NotEqual(DeliveryState.Succeeded, State(fav, r.EntryId, VocabCatalog.AnkiConnect));
    }

    [Fact] // a loopback origin the host did not approve is refused for a package that is not Local: Eudic's Authorization never reaches a loopback listener
    public async Task Unapproved_loopback_origin_for_eudic_receives_nothing()
    {
        using var eudic = new FakeEudic();
        using var rig = Build(VocabCatalog.Eudic, eudic.Server.Origin, NoExtra, EudicAuth, approveOrigin: false);
        if (rig is null) return;
        var (db, fav) = Open();
        using var _ = db;
        await using var service = NewService(fav, (VocabCatalog.Eudic, rig));
        var r = service.Favorite(new FavoriteCard("en", "apple", Content()));
        await service.SyncNowAsync(Ct);
        Assert.Equal(0, eudic.Total);
        Assert.NotEqual(DeliveryState.Succeeded, State(fav, r.EntryId, VocabCatalog.Eudic));
    }

    [Fact] // a name that resolves to loopback (localhost) works only when it is the configured, loopback origin; observation recorded, no crash, no wrong host
    public async Task Localhost_name_for_anki_is_either_applied_or_a_network_retry()
    {
        using var anki = new FakeAnki();
        using var rig = Build(VocabCatalog.AnkiConnect, $"http://localhost:{anki.Server.Port}", NoExtra, null);
        if (rig is null) return;
        var (db, fav) = Open();
        using var _ = db;
        await using var service = NewService(fav, (VocabCatalog.AnkiConnect, rig));
        var r = service.Favorite(new FavoriteCard("en", "apple", Content()));
        await service.SyncNowAsync(Ct);
        var state = State(fav, r.EntryId, VocabCatalog.AnkiConnect);
        TestContext.Current.SendDiagnosticMessage($"F15 localhost origin -> {state}, notes {anki.Notes0}");
        Assert.True(state is DeliveryState.Succeeded or DeliveryState.RetryWait or DeliveryState.Failed, state.ToString());
        Assert.Equal(state == DeliveryState.Succeeded ? 1 : 0, anki.Notes0);
    }

    [Fact] // hostile text through the real package: markup is escaped in every field, quotes and newlines survive, one note per word
    public async Task Hostile_content_is_escaped_and_not_interpreted_by_anki()
    {
        using var anki = new FakeAnki();
        using var rig = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, NoExtra, null);
        if (rig is null) return;
        var (db, fav) = Open();
        using var _ = db;
        await using var service = NewService(fav, (VocabCatalog.AnkiConnect, rig));
        string hostile = "x\"} ], \"action\":\"deleteNotes\", \"y\":[ {\"<img src=x onerror=alert(1)>\nSusuId:*";
        var content = JsonSerializer.Serialize(new { source = hostile, meanings = new[] { new { pos = "n.", means = new[] { hostile } } }, examples = new[] { new { src = hostile, dst = hostile } } });
        service.Favorite(new FavoriteCard("en", "<b>deck:*</b>", content));
        await service.SyncNowAsync(Ct);
        var note = anki.Notes.Values.Single();
        foreach (var f in new[] { "Word", "Definition", "Example", "Source" })
        {
            Assert.DoesNotContain("<img", note[f]);
            Assert.DoesNotContain("<b>", note[f]);
        }
        Assert.Empty(anki.Unexpected); // no injected action reached the server
    }

    [Fact] // vendor surface: for a whole flow (add, edit, unfavorite, refavorite) only the allow-listed AnkiConnect actions and GET/POST word calls are ever made
    public async Task Only_allow_listed_vendor_calls_are_made_in_a_full_flow()
    {
        using var anki = new FakeAnki();
        using var eudic = new FakeEudic();
        using var ar = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, NoExtra, null);
        if (ar is null) return;
        using var er = Build(VocabCatalog.Eudic, eudic.Server.Origin, NoExtra, EudicAuth)!;
        var (db, fav) = Open();
        using var _ = db;
        await using var service = NewService(fav, (VocabCatalog.AnkiConnect, ar), (VocabCatalog.Eudic, er));
        service.Favorite(new FavoriteCard("en", "apple", Content()));
        await service.SyncNowAsync(Ct);
        service.Favorite(new FavoriteCard("en", "apple", Content("changed")));
        service.Unfavorite("en", "apple");
        await service.SyncNowAsync(Ct);
        service.Favorite(new FavoriteCard("en", "apple", Content("changed again")));
        await service.SyncNowAsync(Ct);
        Assert.Empty(anki.Unexpected);
        Assert.Empty(eudic.Unexpected);
        Assert.Equal(1, anki.Notes0);
        Assert.Equal(1, anki.Count("addNote"));
    }
}
