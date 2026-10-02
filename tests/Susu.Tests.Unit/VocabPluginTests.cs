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
/// F15.3 P-V01 AnkiConnect and P-V02 Eudic: the real shipped packages through the real AppContainer sandbox + QuickJS + IPC + Broker/$http
/// and <see cref="PluginVocabTarget"/>, driven by <see cref="VocabSyncWorker"/> over the real outbox, against loopback fakes of the two
/// vendor protocols (DATA05/DATA06 remote halves, CFG02 plugin parts). Needs susu.exe published and skips itself otherwise. Real vendor
/// runs: not executed (no Anki, no Eudic account); the fakes follow the public AnkiConnect v6 and Eudic OpenAPI descriptions.
/// </summary>
public sealed class VocabPluginTests : IDisposable
{
    private const string Key = "anki-key-0123456789abcdef";
    private const string EudicAuth = "NIS eudic-token-0123456789abcdef";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempRoot root = new();
    private readonly ManualClock clock = new();
    private readonly List<IDisposable> disposables = [];
    private int ids;

    public void Dispose() { foreach (var d in disposables) d.Dispose(); root.Dispose(); }

    // ---------------- staging (as AsrPluginTests) ----------------

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
        string dir = TestTemp.NewDir("susu-vocab-it");
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

    private sealed class Rig(Supervisor<HostSession> supervisor, PluginVocabTarget target, WiredPackage package, string configJson, string origin) : IDisposable
    {
        public PluginVocabTarget Target => target;
        public Supervisor<HostSession> Supervisor => supervisor;
        public WiredPackage Package => package;
        public string ConfigJson => configJson;
        public string Origin => origin;
        public void Dispose() => supervisor.Dispose();
    }

    private Rig? Build(string instanceId, string origin, Dictionary<string, string> extra, string? secret, TimeSpan? timeout = null)
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
            session.Broker.ApproveLocalOrigin(origin); // the loopback fake (Eudic is not a local package, so the target never approves it)
            var loaded = session.Load(package.PackageId, package.Directory);
            if (!loaded.Ok) { session.Shutdown(2000); throw new InvalidOperationException($"{package.PackageId} failed to load: {loaded.Error}"); }
            return session;
        }, SystemClock.Instance, TimeSpan.FromMinutes(10));
        var rig = new Rig(supervisor, new PluginVocabTarget(package, instance, supervisor, null, timeout ?? TimeSpan.FromSeconds(30)), package, JsonSerializer.Serialize(cfg), package.Origin(cfg));
        disposables.Add(rig);
        return rig;
    }

    // ---------------- fake vendors ----------------

    public enum Fault { None, DropAfterApply, DropBeforeApply, ServerErrorAfterApply, ServerErrorBeforeApply, RateLimited }

    private static async Task Reply(NetworkStream stream, int status, string json, string? retryAfter = null, CancellationToken ct = default)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        string head = $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n{(retryAfter is null ? "" : $"Retry-After: {retryAfter}\r\n")}Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(body, ct);
    }

    private sealed record Note(long Id, string Deck, string Model, Dictionary<string, string> Fields, string[] Tags);

    /// <summary>AnkiConnect v6 as far as the package uses it. Faults apply to the next note-changing call (addNote/updateNoteFields).</summary>
    private sealed class FakeAnki : IDisposable
    {
        private readonly object gate = new();
        private long nextId = 1000;
        public List<Note> Notes { get; } = [];
        public HashSet<string> Models { get; } = [];
        public HashSet<string> Decks { get; } = ["Default"];
        public List<(string Action, JsonObject Body)> Calls { get; } = [];
        public Queue<Fault> Faults { get; } = new();
        public string? RequiredKey { get; set; }
        public LoopbackHttpServer Server { get; }

        public FakeAnki() => Server = new LoopbackHttpServer(Handle);

        public int Count(string action) { lock (gate) return Calls.Count(c => c.Action == action); }
        private bool disposed;
        public void Dispose() { if (disposed) return; disposed = true; Server.Dispose(); }

        private async Task Handle(LoopbackHttpRequest req, NetworkStream stream, CancellationToken ct)
        {
            var body = (JsonObject)JsonNode.Parse(req.Body)!;
            string action = body["action"]!.GetValue<string>();
            var p = body["params"] as JsonObject;
            Fault fault = Fault.None;
            string json;
            lock (gate)
            {
                Calls.Add((action, body));
                if (RequiredKey is not null && body["key"]?.GetValue<string>() != RequiredKey)
                { json = Answer(null, "valid api key must be provided"); goto reply; }
                if (action is "addNote" or "updateNoteFields" && Faults.Count > 0) fault = Faults.Dequeue();
                if (fault == Fault.RateLimited) { json = ""; goto reply; }
                if (fault is Fault.DropBeforeApply or Fault.ServerErrorBeforeApply) { json = ""; goto reply; }
                json = Apply(action, p);
            }
        reply:
            switch (fault)
            {
                case Fault.DropAfterApply or Fault.DropBeforeApply: return; // the connection closes with no answer
                case Fault.ServerErrorAfterApply or Fault.ServerErrorBeforeApply: await Reply(stream, 500, "{}", ct: ct); return;
                case Fault.RateLimited: await Reply(stream, 429, "{}", "7", ct); return;
            }
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
                    string query = p!["query"]!.GetValue<string>();
                    var m = Regex.Match(query, @"SusuId:(\S+)");
                    return Answer(new JsonArray([.. Notes.Where(n => m.Success && n.Fields.GetValueOrDefault("SusuId") == m.Groups[1].Value).Select(n => (JsonNode)n.Id)]), null);
                }
                case "addNote":
                {
                    var n = p!["note"]!;
                    var fields = n["fields"]!.AsObject().ToDictionary(f => f.Key, f => f.Value!.GetValue<string>());
                    string model = n["modelName"]!.GetValue<string>(), deck = n["deckName"]!.GetValue<string>();
                    if (!Decks.Contains(deck)) return Answer(null, "deck was not found: " + deck);
                    if (!Models.Contains(model)) return Answer(null, "model was not found: " + model);
                    bool allowDuplicate = n["options"]?["allowDuplicate"]?.GetValue<bool>() ?? false;
                    if (!allowDuplicate && Notes.Any(x => x.Model == model && x.Fields["Word"] == fields["Word"])) return Answer(null, "cannot create note because it is a duplicate");
                    var note = new Note(++nextId, deck, model, fields, [.. n["tags"]!.AsArray().Select(t => t!.GetValue<string>())]);
                    Notes.Add(note);
                    return Answer(note.Id, null);
                }
                case "updateNoteFields":
                {
                    long id = p!["note"]!["id"]!.GetValue<long>();
                    var note = Notes.FirstOrDefault(x => x.Id == id);
                    if (note is null) return Answer(null, "Note was not found: " + id);
                    foreach (var f in p["note"]!["fields"]!.AsObject()) note.Fields[f.Key] = f.Value!.GetValue<string>();
                    return Answer(null, null);
                }
                default: return Answer(null, "unsupported action: " + action);
            }
        }
    }

    /// <summary>Eudic OpenAPI study list as far as the package uses it.</summary>
    private sealed class FakeEudic : IDisposable
    {
        private readonly object gate = new();
        public Dictionary<string, long[]> Words { get; } = [];
        public List<LoopbackHttpRequest> Requests { get; } = [];
        public Queue<Fault> Faults { get; } = new();
        public string? RequiredAuth { get; set; } = EudicAuth;
        public LoopbackHttpServer Server { get; }

        public FakeEudic() => Server = new LoopbackHttpServer(Handle);
        public int Count(string method) { lock (gate) return Requests.Count(r => r.Method == method); }
        private bool disposed;
        public void Dispose() { if (disposed) return; disposed = true; Server.Dispose(); }

        private async Task Handle(LoopbackHttpRequest req, NetworkStream stream, CancellationToken ct)
        {
            Fault fault = Fault.None;
            int status; string json;
            lock (gate)
            {
                Requests.Add(req);
                string? auth = req.Headers.FirstOrDefault(h => string.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase)).Value;
                if (RequiredAuth is not null && auth != RequiredAuth) { status = 401; json = """{"message":"unauthorized"}"""; goto reply; }
                if (req.Method == "POST" && Faults.Count > 0) fault = Faults.Dequeue();
                (status, json) = fault is Fault.DropBeforeApply or Fault.ServerErrorBeforeApply or Fault.RateLimited ? (0, "") : Apply(req);
            }
        reply:
            switch (fault)
            {
                case Fault.DropAfterApply or Fault.DropBeforeApply: return;
                case Fault.ServerErrorAfterApply or Fault.ServerErrorBeforeApply: await Reply(stream, 500, "{}", ct: ct); return;
                case Fault.RateLimited: await Reply(stream, 429, "{}", "7", ct); return;
            }
            await Reply(stream, status, json, ct: ct);
        }

        private (int, string) Apply(LoopbackHttpRequest req)
        {
            string path = req.Path.Split('?')[0];
            var query = System.Web.HttpUtility.ParseQueryString(req.Path.Contains('?') ? req.Path[(req.Path.IndexOf('?') + 1)..] : "");
            if (req.Method == "GET" && path.StartsWith("/api/open/v1/studylist/word/", StringComparison.Ordinal))
            {
                string word = Uri.UnescapeDataString(path["/api/open/v1/studylist/word/".Length..]);
                return Words.ContainsKey($"{query["language"]}:{word}")
                    ? (200, new JsonObject { ["data"] = new JsonObject { ["word"] = word }, ["message"] = "ok" }.ToJsonString())
                    : (404, """{"message":"not found"}""");
            }
            if (req.Method == "GET" && path == "/api/open/v1/studylist/category")
                return (200, """{"data":[{"id":"111","language":"en","name":"Reading"},{"id":"222","language":"en","name":"Exam"}],"message":"ok"}""");
            if (req.Method == "POST" && path == "/api/open/v1/studylist/word")
            {
                var body = (JsonObject)JsonNode.Parse(req.Body)!;
                Words[$"{body["language"]!.GetValue<string>()}:{body["word"]!.GetValue<string>()}"] = [.. (body["category_ids"] as JsonArray ?? []).Select(c => c!.GetValue<long>())];
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

    private static string Content(string meaning = "fruit") => $$"""{"phonetics":[{"accent":"US","ipa":"ˈæp.əl"}],"meanings":[{"pos":"n.","means":["{{meaning}}"]}],"examples":[{"src":"An apple <a day>.","dst":"一天一苹果"}],"source":"Youdao"}""";

    private static VocabSyncRequest Req(VocabAction action, string entryId = "e1", long revision = 1, string word = "apple", string lang = "en", string? content = null, string op = "op-1")
        => new(op, action, entryId, revision, word, lang, content ?? Content());

    private static VocabSyncOutcome.Applied Applied(VocabSyncOutcome o) => Assert.IsType<VocabSyncOutcome.Applied>(o);
    private static ProviderError RetryOf(VocabSyncOutcome o) => Assert.IsType<VocabSyncOutcome.Retry>(o).Error;
    private static ProviderError FailureOf(VocabSyncOutcome o) => Assert.IsType<VocabSyncOutcome.Failure>(o).Error;
    private static DeliveryState State(IFavorites fav, string entry, string target) => fav.Deliveries(entry).Single(d => d.TargetInstanceId == target).State;

    private static readonly Dictionary<string, string> NoExtra = [];

    // ---------------- P-V01 AnkiConnect ----------------

    [Fact] // add: model + deck created, note found by its Su-Su id, fields rendered and escaped, operationId tag, no key without the option
    public async Task Anki_upsert_adds_one_note_with_the_stable_id_and_escaped_fields()
    {
        using var anki = new FakeAnki();
        using var rig = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, new() { ["deck"] = "Reading" }, null);
        if (rig is null) return;

        var outcome = Applied(await rig.Target.SendAsync(Req(VocabAction.Upsert), Ct));

        var note = Assert.Single(anki.Notes);
        Assert.Equal(note.Id.ToString(), outcome.RemoteId);
        Assert.Equal(("Reading", "Su-Su"), (note.Deck, note.Model));
        Assert.Contains("Su-Su", anki.Models);
        Assert.Equal("e1", note.Fields["SusuId"]);
        Assert.Equal("apple", note.Fields["Word"]);
        Assert.Equal("US /ˈæp.əl/", note.Fields["Phonetic"]);
        Assert.Equal("n. fruit", note.Fields["Definition"]);
        Assert.Equal("An apple &lt;a day&gt;. - 一天一苹果", note.Fields["Example"]);
        Assert.Contains("susu-op-op-1", note.Tags);
        Assert.All(anki.Calls, c => Assert.Null(c.Body["key"]));
        Assert.All(anki.Calls, c => Assert.Equal(6, c.Body["version"]!.GetValue<int>()));
    }

    [Fact] // lookup then update: a new revision changes the same note, never a second one
    public async Task Anki_upsert_of_a_new_revision_updates_the_same_note()
    {
        using var anki = new FakeAnki();
        using var rig = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, NoExtra, null);
        if (rig is null) return;
        var first = Applied(await rig.Target.SendAsync(Req(VocabAction.Upsert), Ct));
        var second = Applied(await rig.Target.SendAsync(Req(VocabAction.Upsert, revision: 2, content: Content("red fruit"), op: "op-2"), Ct));
        Assert.Equal(first.RemoteId, second.RemoteId);
        Assert.Equal("n. red fruit", Assert.Single(anki.Notes).Fields["Definition"]);
        Assert.Equal(1, anki.Count("addNote"));
        Assert.Equal(1, anki.Count("updateNoteFields"));
    }

    [Fact]
    public async Task Anki_lookup_reports_found_or_absent_and_never_writes()
    {
        using var anki = new FakeAnki();
        using var rig = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, NoExtra, null);
        if (rig is null) return;
        Assert.IsType<VocabSyncOutcome.Absent>(await rig.Target.SendAsync(Req(VocabAction.Lookup), Ct));
        Assert.Equal((0, 0, 0), (anki.Count("createModel"), anki.Count("createDeck"), anki.Count("addNote"))); // the lookup alone changed nothing
        var added = Applied(await rig.Target.SendAsync(Req(VocabAction.Upsert), Ct));
        var found = Assert.IsType<VocabSyncOutcome.Found>(await rig.Target.SendAsync(Req(VocabAction.Lookup), Ct));
        Assert.Equal(added.RemoteId, found.RemoteId);
        Assert.Equal(1, anki.Count("addNote"));
    }

    [Fact] // the API key goes in the request's key field only when the option is on; a wrong key is an auth failure that changes nothing
    public async Task Anki_api_key_is_written_by_the_host_and_a_wrong_key_is_an_auth_failure()
    {
        using var anki = new FakeAnki { RequiredKey = Key };
        using var good = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, new() { ["useApiKey"] = "true" }, Key);
        if (good is null) return;
        Applied(await good.Target.SendAsync(Req(VocabAction.Upsert), Ct));
        Assert.All(anki.Calls, c => Assert.Equal(Key, c.Body["key"]!.GetValue<string>()));

        using var bad = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, new() { ["useApiKey"] = "true" }, "wrong-key-0123456789abcdef");
        var error = FailureOf(await bad!.Target.SendAsync(Req(VocabAction.Upsert, "e2", word: "pear"), Ct));
        Assert.Equal(ErrorKind.Auth, error.Kind);
        Assert.DoesNotContain("wrong-key", error.Detail ?? "");
        Assert.Single(anki.Notes);

        using var missing = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, NoExtra, null); // the option is off but the add-on wants a key
        Assert.Equal(ErrorKind.Auth, FailureOf(await missing!.Target.SendAsync(Req(VocabAction.Upsert, "e2", word: "pear"), Ct)).Kind);
    }

    [Fact] // offline: nothing could have been written, so it is a plain retry, not Uncertain
    public async Task Anki_not_running_is_a_retry()
    {
        var anki = new FakeAnki();
        string origin = anki.Server.Origin;
        anki.Dispose();
        using var rig = Build(VocabCatalog.AnkiConnect, origin, NoExtra, null);
        if (rig is null) return;
        Assert.Equal(ErrorKind.Network, RetryOf(await rig.Target.SendAsync(Req(VocabAction.Upsert), Ct)).Kind);
        Assert.Equal(ErrorKind.Network, RetryOf(await rig.Target.SendAsync(Req(VocabAction.Lookup), Ct)).Kind);
    }

    [Fact] // duplicate detect: the same word is already in the deck (added by hand): no second note, reported as found
    public async Task Anki_duplicate_word_is_not_added_again()
    {
        using var anki = new FakeAnki();
        anki.Models.Add("Su-Su");
        anki.Notes.Add(new Note(1, "Default", "Su-Su", new() { ["Word"] = "apple", ["SusuId"] = "manual" }, []));
        using var rig = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, NoExtra, null);
        if (rig is null) return;
        var found = Assert.IsType<VocabSyncOutcome.Found>(await rig.Target.SendAsync(Req(VocabAction.Upsert), Ct));
        Assert.Null(found.RemoteId);
        Assert.Single(anki.Notes);
    }

    [Theory] // a write answered with a 5xx, or whose answer is lost, may have landed: Unknown. A 429 never did: retry.
    [InlineData(Fault.DropAfterApply, true)]
    [InlineData(Fault.DropBeforeApply, true)]
    [InlineData(Fault.ServerErrorAfterApply, true)]
    [InlineData(Fault.RateLimited, false)]
    public async Task Anki_write_that_loses_its_answer_is_unknown_and_a_429_is_a_retry(Fault fault, bool unknown)
    {
        using var anki = new FakeAnki();
        using var rig = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, NoExtra, null);
        if (rig is null) return;
        anki.Faults.Enqueue(fault);
        var outcome = await rig.Target.SendAsync(Req(VocabAction.Upsert), Ct);
        if (unknown) Assert.IsType<VocabSyncOutcome.Unknown>(outcome);
        else Assert.Equal(7, RetryOf(outcome).RetryAfter!.Value.TotalSeconds);
        Assert.Equal(1, anki.Count("addNote"));
    }

    [Fact] // DATA06: answer lost after the note landed, app restarts: lookup finds it, no duplicate, no second addNote
    public async Task Anki_lost_response_then_restart_finds_the_note_and_does_not_duplicate()
    {
        using var anki = new FakeAnki();
        using var rig = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, NoExtra, null);
        if (rig is null) return;
        string entryId;
        {
            var (db, fav) = Open();
            using var _ = db;
            entryId = fav.Favorite(new FavoriteCard("en", "apple", Content()), [VocabCatalog.AnkiConnect]).EntryId;
            anki.Faults.Enqueue(Fault.DropAfterApply);
            var worker = new VocabSyncWorker(fav, _ => rig.Target, clock);
            Assert.Equal(1, (await worker.RunTargetAsync(VocabCatalog.AnkiConnect, Ct)).Uncertain);
            Assert.Equal(DeliveryState.Uncertain, State(fav, entryId, VocabCatalog.AnkiConnect));
        }
        {
            var (db, fav) = Open(); // restart
            using var _ = db;
            Assert.Equal(0, fav.RecoverInterrupted());
            var report = await new VocabSyncWorker(fav, _ => rig.Target, clock).RunTargetAsync(VocabCatalog.AnkiConnect, Ct);
            Assert.Equal((1, 1), (report.Succeeded, report.Confirmed));
            var row = fav.Deliveries(entryId).Single();
            Assert.Equal(DeliveryState.Succeeded, row.State);
            Assert.Equal(anki.Notes.Single().Id.ToString(), row.RemoteId);
        }
        Assert.Equal(1, anki.Count("addNote"));
        Assert.Single(anki.Notes);
    }

    [Fact] // DATA06: the app dies while the request is in flight (row left Sending) and the note never landed: back-filled once
    public async Task Anki_crash_before_the_write_landed_is_back_filled_exactly_once()
    {
        using var anki = new FakeAnki();
        using var rig = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, NoExtra, null);
        if (rig is null) return;
        var (db, fav) = Open();
        using var _ = db;
        var entry = fav.Favorite(new FavoriteCard("en", "apple", Content()), [VocabCatalog.AnkiConnect]);
        Assert.NotNull(fav.Claim(VocabCatalog.AnkiConnect));
        Assert.Equal(1, fav.RecoverInterrupted());
        var worker = new VocabSyncWorker(fav, _ => rig.Target, clock);
        await worker.RunTargetAsync(VocabCatalog.AnkiConnect, Ct);
        await worker.RunTargetAsync(VocabCatalog.AnkiConnect, Ct);
        Assert.Equal(DeliveryState.Succeeded, State(fav, entry.EntryId, VocabCatalog.AnkiConnect));
        Assert.Single(anki.Notes);
        Assert.Equal(1, anki.Count("addNote"));
    }

    [Fact] // a host timeout on the write leaves it Unknown (the plugin call is cancelled), and lookup then settles it
    public async Task Anki_host_timeout_on_a_write_is_unknown()
    {
        using var slow = new LoopbackHttpServer(async (req, stream, ct) =>
        {
            var body = (JsonObject)JsonNode.Parse(req.Body)!;
            string action = body["action"]!.GetValue<string>();
            if (action == "addNote") { await Task.Delay(TimeSpan.FromSeconds(30), ct); return; }
            await Reply(stream, 200, action switch { "modelNames" => """{"result":["Su-Su"],"error":null}""", "findNotes" => """{"result":[],"error":null}""", _ => """{"result":1,"error":null}""" }, ct: ct);
        });
        using var rig = Build(VocabCatalog.AnkiConnect, slow.Origin, NoExtra, null, timeout: TimeSpan.FromSeconds(3));
        if (rig is null) return;
        Assert.IsType<VocabSyncOutcome.Unknown>(await rig.Target.SendAsync(Req(VocabAction.Upsert), Ct));
        Assert.IsType<VocabSyncOutcome.Absent>(await rig.Target.SendAsync(Req(VocabAction.Lookup), Ct)); // reads still work
    }

    [Fact] // CFG02 (plugin part): the deck list comes from deckNames through the options method with the instance's own authority
    public async Task Anki_options_lists_the_decks_and_rejects_other_fields()
    {
        using var anki = new FakeAnki { RequiredKey = Key };
        anki.Decks.Add("Zeta"); anki.Decks.Add("Alpha");
        using var rig = Build(VocabCatalog.AnkiConnect, anki.Server.Origin, new() { ["useApiKey"] = "true" }, Key);
        if (rig is null) return;
        var host = rig.Supervisor.Acquire()!;
        try
        {
            var ok = await CapabilityClient.InvokeAsync(host, rig.Package.PackageId, "options", """{"field":"deck","dependsOnRevision":1}""", "job-opt", [rig.Origin],
                ContractsJson.Default.OptionsResult, rig.Package.SecretNames, configJson: rig.ConfigJson, instanceId: rig.Package.InstanceId, signer: rig.Package.Signer, cancellationToken: Ct);
            Assert.True(ok.Ok, ok.ErrorDetail);
            Assert.Equal(["Alpha", "Default", "Zeta"], ok.Result!.Items.Select(i => i.Value));
            var other = await CapabilityClient.InvokeAsync(host, rig.Package.PackageId, "options", """{"field":"nope","dependsOnRevision":1}""", "job-opt2", [rig.Origin],
                ContractsJson.Default.OptionsResult, rig.Package.SecretNames, configJson: rig.ConfigJson, instanceId: rig.Package.InstanceId, signer: rig.Package.Signer, cancellationToken: Ct);
            Assert.False(other.Ok);
        }
        finally { rig.Supervisor.Release(); }
    }

    // ---------------- P-V02 Eudic ----------------

    [Fact] // lookup then upsert; Authorization written by the host; the chosen word list travels as category_ids
    public async Task Eudic_upsert_looks_up_then_adds_to_the_chosen_list()
    {
        using var eudic = new FakeEudic();
        using var rig = Build(VocabCatalog.Eudic, eudic.Server.Origin, new() { ["vocabList"] = "222" }, EudicAuth);
        if (rig is null) return;
        var outcome = Applied(await rig.Target.SendAsync(Req(VocabAction.Upsert), Ct));
        Assert.Equal("en:apple", outcome.RemoteId);
        Assert.Equal([222L], eudic.Words["en:apple"]);
        Assert.Equal(["GET", "POST"], eudic.Requests.Select(r => r.Method));
        Assert.StartsWith("/api/open/v1/studylist/word/apple?language=en", eudic.Requests[0].Path);
        Assert.Equal("/api/open/v1/studylist/word", eudic.Requests[1].Path);
    }

    [Fact] // a word already in the list and no list chosen: found, nothing written
    public async Task Eudic_found_word_without_a_list_choice_is_not_written_again()
    {
        using var eudic = new FakeEudic();
        eudic.Words["en:apple"] = [];
        using var rig = Build(VocabCatalog.Eudic, eudic.Server.Origin, NoExtra, EudicAuth);
        if (rig is null) return;
        Assert.IsType<VocabSyncOutcome.Found>(await rig.Target.SendAsync(Req(VocabAction.Upsert), Ct));
        Assert.Equal(0, eudic.Count("POST"));
        Assert.IsType<VocabSyncOutcome.Absent>(await rig.Target.SendAsync(Req(VocabAction.Lookup, word: "pear", entryId: "e2"), Ct));
    }

    [Fact] // CFG02 (plugin part): word lists from the category call; the key is never in the answer or the error
    public async Task Eudic_options_lists_the_word_lists_and_auth_failure_hides_the_key()
    {
        using var eudic = new FakeEudic();
        using var rig = Build(VocabCatalog.Eudic, eudic.Server.Origin, NoExtra, EudicAuth);
        if (rig is null) return;
        var host = rig.Supervisor.Acquire()!;
        try
        {
            var ok = await CapabilityClient.InvokeAsync(host, rig.Package.PackageId, "options", """{"field":"vocabList","dependsOnRevision":1}""", "job-opt", [rig.Origin],
                ContractsJson.Default.OptionsResult, rig.Package.SecretNames, configJson: rig.ConfigJson, instanceId: rig.Package.InstanceId, signer: rig.Package.Signer, cancellationToken: Ct);
            Assert.True(ok.Ok, ok.ErrorDetail);
            Assert.Equal([("111", "Reading"), ("222", "Exam")], ok.Result!.Items.Select(i => (i.Value, i.Label)));
        }
        finally { rig.Supervisor.Release(); }
        eudic.RequiredAuth = "NIS something-else";
        using var rejected = Build(VocabCatalog.Eudic, eudic.Server.Origin, NoExtra, EudicAuth);
        var host2 = rejected!.Supervisor.Acquire()!;
        try
        {
            var bad = await CapabilityClient.InvokeAsync(host2, rejected.Package.PackageId, "options", """{"field":"vocabList","dependsOnRevision":1}""", "job-opt3", [rejected.Origin],
                ContractsJson.Default.OptionsResult, rejected.Package.SecretNames, configJson: rejected.ConfigJson, instanceId: rejected.Package.InstanceId, signer: rejected.Package.Signer, cancellationToken: Ct);
            Assert.Equal(ErrorKind.Auth, bad.ErrorKind);
            Assert.DoesNotContain("eudic-token", bad.ErrorDetail ?? "");
        }
        finally { rejected.Supervisor.Release(); }
    }

    [Fact]
    public async Task Eudic_error_classes_auth_rate_limit_language_offline()
    {
        using var eudic = new FakeEudic { RequiredAuth = "NIS other" };
        using var rig = Build(VocabCatalog.Eudic, eudic.Server.Origin, NoExtra, EudicAuth);
        if (rig is null) return;
        Assert.Equal(ErrorKind.Auth, FailureOf(await rig.Target.SendAsync(Req(VocabAction.Upsert), Ct)).Kind);
        Assert.Equal(0, eudic.Count("POST")); // the lookup was refused first, nothing was written

        eudic.RequiredAuth = EudicAuth;
        Assert.Equal(ErrorKind.UnsupportedLanguage, FailureOf(await rig.Target.SendAsync(Req(VocabAction.Upsert, lang: "ja", word: "猫"), Ct)).Kind);
        eudic.Faults.Enqueue(Fault.RateLimited);
        Assert.Equal(7, RetryOf(await rig.Target.SendAsync(Req(VocabAction.Upsert), Ct)).RetryAfter!.Value.TotalSeconds);
        Assert.Empty(eudic.Words);

        string origin = eudic.Server.Origin;
        eudic.Dispose();
        using var offline = Build(VocabCatalog.Eudic, origin, NoExtra, EudicAuth);
        Assert.Equal(ErrorKind.Network, RetryOf(await offline!.Target.SendAsync(Req(VocabAction.Upsert), Ct)).Kind);
    }

    [Theory]
    [InlineData(Fault.DropAfterApply)]
    [InlineData(Fault.ServerErrorAfterApply)]
    public async Task Eudic_lost_answer_is_unknown_then_settled_by_lookup_without_a_second_write(Fault fault)
    {
        using var eudic = new FakeEudic();
        using var rig = Build(VocabCatalog.Eudic, eudic.Server.Origin, NoExtra, EudicAuth);
        if (rig is null) return;
        var (db, fav) = Open();
        using var _ = db;
        var entry = fav.Favorite(new FavoriteCard("en", "apple", Content()), [VocabCatalog.Eudic]);
        eudic.Faults.Enqueue(fault);
        var worker = new VocabSyncWorker(fav, _ => rig.Target, clock);
        await worker.RunTargetAsync(VocabCatalog.Eudic, Ct);
        Assert.Equal(DeliveryState.Uncertain, State(fav, entry.EntryId, VocabCatalog.Eudic));
        var report = await worker.RunTargetAsync(VocabCatalog.Eudic, Ct);
        Assert.Equal(1, report.Confirmed);
        Assert.Equal(DeliveryState.Succeeded, State(fav, entry.EntryId, VocabCatalog.Eudic));
        Assert.Equal(1, eudic.Count("POST"));
        Assert.Single(eudic.Words);
    }

    // ---------------- both vendors through the worker (DATA05 remote half) ----------------

    [Fact] // one target offline while the other works: independent states, and the offline one catches up after the backoff
    public async Task One_vendor_offline_does_not_block_the_other_and_catches_up_later()
    {
        var deadAnki = new FakeAnki();
        string deadOrigin = deadAnki.Server.Origin;
        deadAnki.Dispose();
        using var eudic = new FakeEudic();
        using var ankiRig = Build(VocabCatalog.AnkiConnect, deadOrigin, NoExtra, null);
        using var eudicRig = Build(VocabCatalog.Eudic, eudic.Server.Origin, NoExtra, EudicAuth);
        if (ankiRig is null || eudicRig is null) return;
        var (db, fav) = Open();
        using var _ = db;
        var entry = fav.Favorite(new FavoriteCard("en", "apple", Content()), [VocabCatalog.AnkiConnect, VocabCatalog.Eudic]);
        var worker = new VocabSyncWorker(fav, id => id == VocabCatalog.Eudic ? eudicRig.Target : ankiRig.Target, clock);

        var reports = await worker.RunAllAsync([VocabCatalog.AnkiConnect, VocabCatalog.Eudic], Ct);
        Assert.Equal(1, reports.Single(r => r.Target == VocabCatalog.AnkiConnect).Retrying);
        Assert.Equal(DeliveryState.RetryWait, State(fav, entry.EntryId, VocabCatalog.AnkiConnect));
        Assert.Equal(DeliveryState.Succeeded, State(fav, entry.EntryId, VocabCatalog.Eudic));

        // Anki comes back at the same address after the backoff
        using var revived = new FakeAnki();
        using var back = Build(VocabCatalog.AnkiConnect, revived.Server.Origin, NoExtra, null);
        var worker2 = new VocabSyncWorker(fav, id => id == VocabCatalog.Eudic ? eudicRig.Target : back!.Target, clock);
        await worker2.RunAllAsync([VocabCatalog.AnkiConnect, VocabCatalog.Eudic], Ct);
        Assert.Equal(DeliveryState.RetryWait, State(fav, entry.EntryId, VocabCatalog.AnkiConnect)); // not due yet
        clock.Advance(TimeSpan.FromSeconds(31));
        await worker2.RunAllAsync([VocabCatalog.AnkiConnect, VocabCatalog.Eudic], Ct);
        Assert.Equal(DeliveryState.Succeeded, State(fav, entry.EntryId, VocabCatalog.AnkiConnect));
        Assert.Single(revived.Notes);
        Assert.Equal(1, eudic.Count("POST"));
    }

    [Fact] // the packages ship with the plugin host's package list and their manifests parse
    public void Packages_are_wired_and_manifests_are_valid()
    {
        string source = FindRoot(d => Directory.Exists(Path.Combine(d, "src", "Susu.Host", "plugins")))!;
        foreach (var package in VocabCatalog.All)
        {
            Assert.Contains(PluginTranslationProviders.WiredPackages, p => p.PackageId == package.PackageId);
            var (manifest, issues) = PackageManifest.Parse(File.ReadAllText(Path.Combine(source, "src", "Susu.Host", package.Directory, "manifest.yaml")));
            Assert.Empty(issues);
            Assert.Equal(package.PackageId, manifest!.Id);
            Assert.Equal(["vocab"], manifest.Capabilities);
            Assert.Contains(manifest.ConfigFields, f => f.Options is not null); // deck / vocabList come from the options method
        }
        Assert.True(VocabCatalog.All.All(p => p.Lookup));
        Assert.Equal("http://127.0.0.1:8765", VocabCatalog.Find(VocabCatalog.AnkiConnect)!.Origin(new Dictionary<string, string>()));
        var grant = Assert.Single(VocabCatalog.Find(VocabCatalog.AnkiConnect)!.RequiredGrants(new Dictionary<string, string>()));
        Assert.Equal(("apiKey", "http://127.0.0.1:8765", "json:/key"), (grant.Secret, grant.Origin, grant.Use));
    }
}
