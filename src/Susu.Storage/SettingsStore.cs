using System.Text;
using Susu.Abstractions;
using Susu.Domain;

namespace Susu.Storage;

/// <summary>
/// settings.yaml owner (ARCHITECTURE 8.1). Save checks the caller's revision and the file hash, validates by
/// round-tripping the generated text, writes a flushed temp file and replaces atomically. Hand edits are
/// picked up after a 300 ms debounce; an invalid file keeps the last valid settings in effect and is never
/// replaced by defaults. A later save from the UI first copies the invalid file aside.
/// </summary>
public sealed class SettingsStore : ISettingsStore, IDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly AppPaths paths;
    private readonly IClock clock;
    private readonly IFaultPoint? faults;
    private readonly object gate = new();
    private SettingsState state;
    private FileSystemWatcher? watcher;
    private CancellationTokenSource? pending;

    public SettingsStore(AppPaths paths, IClock clock, IFaultPoint? faults = null)
    {
        this.paths = paths;
        this.clock = clock;
        this.faults = faults;
        state = LoadInitial();
    }

    public SettingsState State { get { lock (gate) return state; } }
    public event Action<SettingsState>? Changed;

    public SaveResult Save(AppSettings proposed, long expectedRevision, string expectedFileHash)
    {
        lock (gate)
        {
            var check = Prepare(proposed, expectedRevision, expectedFileHash);
            if (check.Result is { } rejected) return rejected;
            try
            {
                PreserveInvalidFile();
                AtomicFile.Write(paths.Settings, check.Bytes!, faults, "settings");
            }
            catch (IOException e)
            {
                return new SaveResult(SaveStatus.Failed, state, [], e.Message);
            }
            Adopt(check.Next!, AtomicFile.Hash(check.Bytes));
        }
        Changed?.Invoke(State);
        return new SaveResult(SaveStatus.Saved, State, []);
    }

    /// <summary>
    /// Saves settings together with secret changes (e.g. a new account and its key) through a journaled
    /// two-file transaction. Secrets are only ever passed as values to <see cref="SecretStore.Prepare"/>.
    /// </summary>
    public SaveResult SaveWithSecrets(AppSettings proposed, long expectedRevision, string expectedFileHash, SecretStore secrets, byte[] secretBytes)
    {
        lock (gate)
        {
            var check = Prepare(proposed, expectedRevision, expectedFileHash);
            if (check.Result is { } rejected) return rejected;
            try
            {
                PreserveInvalidFile();
                new ConfigTransaction(paths.Transactions, faults).Commit([(paths.Settings, check.Bytes!), (paths.Secrets, secretBytes)]);
            }
            catch (IOException e)
            {
                secrets.Reload();
                return new SaveResult(SaveStatus.Failed, state, [], e.Message);
            }
            secrets.Reload();
            Adopt(check.Next!, AtomicFile.Hash(check.Bytes));
        }
        Changed?.Invoke(State);
        return new SaveResult(SaveStatus.Saved, State, []);
    }

    private (SaveResult? Result, byte[]? Bytes, AppSettings? Next) Prepare(AppSettings proposed, long expectedRevision, string expectedFileHash)
    {
        string diskHash = AtomicFile.HashOf(paths.Settings);
        if (expectedRevision != state.Revision || expectedFileHash != state.FileHash || diskHash != state.FileHash)
            return (new SaveResult(SaveStatus.Conflict, state, []), null, null);
        var next = proposed with { SchemaVersion = AppSettings.CurrentSchemaVersion, Revision = state.Revision + 1 };
        string text = SettingsYaml.Write(next);
        var (parsed, issues) = SettingsYaml.Read(text);
        if (parsed is null) return (new SaveResult(SaveStatus.Invalid, state, Map(issues)), null, null);
        return (null, Encoding.UTF8.GetBytes(text), parsed);
    }

    private void PreserveInvalidFile()
    {
        if (!state.FileInvalid || !File.Exists(paths.Settings)) return;
        string aside = Path.Combine(paths.Roaming, $"settings.invalid-{clock.UtcNow:yyyyMMdd-HHmmss}.yaml");
        File.Copy(paths.Settings, aside, overwrite: true);
    }

    private void Adopt(AppSettings next, string hash) => state = new SettingsState(next, next.Revision, hash, []);

    // ---------- loading and file events ----------

    private SettingsState LoadInitial()
    {
        if (!File.Exists(paths.Settings))
        {
            var defaults = BuiltInCatalog.Defaults() with { Revision = 1 };
            byte[] bytes = Encoding.UTF8.GetBytes(SettingsYaml.Write(defaults));
            Directory.CreateDirectory(paths.Roaming);
            AtomicFile.Write(paths.Settings, bytes, faults, "settings");
            return new SettingsState(defaults, 1, AtomicFile.Hash(bytes), []);
        }
        byte[] current = File.ReadAllBytes(paths.Settings);
        var (settings, issues) = SettingsYaml.Read(Decode(current));
        if (settings is not null) return new SettingsState(settings, settings.Revision, AtomicFile.Hash(current), []);
        // Invalid file: fall back to the previous version if it is valid, else defaults - in memory only.
        string previous = $"{paths.Settings}.prev";
        var fallback = File.Exists(previous) ? SettingsYaml.Read(Decode(File.ReadAllBytes(previous))).Settings : null;
        fallback ??= BuiltInCatalog.Defaults();
        return new SettingsState(fallback, fallback.Revision, AtomicFile.Hash(current), Map(issues));
    }

    /// <summary>Re-reads the file now (also used by the debounced watcher). Returns true when the effective state changed.</summary>
    public bool Reload()
    {
        SettingsState changed;
        lock (gate)
        {
            if (!File.Exists(paths.Settings)) return false;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(paths.Settings); }
            catch (IOException) { return false; } // editor still writing; the next event retries
            string hash = AtomicFile.Hash(bytes);
            if (hash == state.FileHash) return false;
            var (settings, issues) = SettingsYaml.Read(Decode(bytes));
            if (settings is null) state = state with { FileHash = hash, Issues = Map(issues) };
            else
            {
                long revision = Math.Max(settings.Revision, state.Revision + 1);
                state = new SettingsState(settings with { Revision = revision }, revision, hash, []);
            }
            changed = state;
        }
        Changed?.Invoke(changed);
        return true;
    }

    public void StartWatching()
    {
        watcher = new FileSystemWatcher(paths.Roaming, "settings.yaml") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        watcher.Changed += (_, _) => NotifyFileChanged();
        watcher.Created += (_, _) => NotifyFileChanged();
        watcher.Renamed += (_, _) => NotifyFileChanged();
        watcher.EnableRaisingEvents = true;
    }

    /// <summary>Debounced: bursts of events within 300 ms cause one reload.</summary>
    public void NotifyFileChanged()
    {
        CancellationTokenSource cts;
        lock (gate)
        {
            pending?.Cancel();
            pending = cts = new CancellationTokenSource();
        }
        _ = clock.Delay(Debounce, cts.Token).ContinueWith(t => { if (t.IsCompletedSuccessfully) Reload(); }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        watcher?.Dispose();
        lock (gate) pending?.Cancel();
    }

    private static string Decode(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.StartsWith(Encoding.UTF8.Preamble)) span = span[3..];
        return Encoding.UTF8.GetString(span);
    }

    private static IReadOnlyList<SettingsIssue> Map(IReadOnlyList<FileIssue> issues)
        => issues.Select(i => new SettingsIssue(i.Path, i.Code, i.Message, i.Line, i.Column)).ToList();
}
