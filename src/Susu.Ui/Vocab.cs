using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;

namespace Susu.Ui;

/// <summary>
/// F15.4 vocabulary: the card star (Vocab.Collect from a result window) and the SetVocab page (targets, status, manual check, export).
/// The host builds what a favorite keeps from the entry it shows, never from page text. Every Settings-window command answers with the
/// fresh settings view, like the other settings commands; the page never sees a secret, a grant or a remote id.
/// </summary>
public sealed partial class ShellCoordinator
{
    private VocabService? vocab;
    private VocabExportView? lastVocabExport;
    private int vocabExporting;

    /// <summary>The save-file dialog for "export now". Null: the export command answers unavailable.</summary>
    public IVocabSavePicker? VocabSavePicker { get; set; }

    /// <summary>The favorites, sync and export service. Null: no star on the cards and no export on SetVocab (the page says so).</summary>
    public VocabService? Vocab
    {
        get => vocab;
        set
        {
            if (vocab is not null) vocab.Changed -= OnVocabChanged;
            vocab = value;
            if (vocab is not null) vocab.Changed += OnVocabChanged;
        }
    }

    private void OnVocabChanged() => platform.StartTimer(TimeSpan.Zero, () =>
        Broadcast(UiMessageKind.Event, "settings", JsonSerializer.SerializeToElement(ProjectSettings(config.State), ContractsJson.Default.SettingsView), WindowKind.Settings));

    // ---------- the card star ----------

    private async Task<CommandResult> CollectAsync(WindowKind kind, CollectRequest request)
    {
        if (vocab is not { } service || TranslationOf(kind) is not { } session) return new CommandResult(false, "unavailable");
        var snapshot = await session.SnapshotAsync();
        var card = snapshot.Cards.FirstOrDefault(c => c.ServiceId == request.ServiceId);
        if (card is not { State: CardState.Ready, Entry: { } entry } || card.Collapsed) return new CommandResult(false, "not-ready");
        string word = entry.Word.Trim();
        if (word.Length == 0) return new CommandResult(false, "empty");
        string lang = snapshot.From;
        int targets = service.Targets().Count;
        bool favorited;
        try
        {
            switch (request.Favorite)
            {
                case true:
                    var result = service.Favorite(new FavoriteCard(lang, word, VocabContentOf(card.DisplayName, entry)));
                    favorited = true;
                    targets = result.QueuedTargets.Count;
                    break;
                case false:
                    service.Unfavorite(lang, word);
                    favorited = false;
                    break;
                default:
                    favorited = service.IsFavorite(lang, word);
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Diagnostic?.Invoke($"vocab.collect-failed {e.GetType().Name}");
            return new CommandResult(false, "storage");
        }
        return Ok(JsonSerializer.SerializeToElement(new CollectView(favorited, targets), ContractsJson.Default.CollectView));
    }

    /// <summary>The saved content: the F09.3 projection (source, phonetics, meanings, examples) of the entry the card shows.</summary>
    internal static string VocabContentOf(string source, DictionaryEntryView entry)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("source", source);
            w.WriteStartArray("phonetics");
            foreach (var p in entry.Phonetics) { w.WriteStartObject(); w.WriteString("accent", p.Accent); w.WriteString("ipa", p.Ipa); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteStartArray("meanings");
            foreach (var part in entry.Parts)
            {
                w.WriteStartObject(); w.WriteString("pos", part.Pos);
                w.WriteStartArray("means"); foreach (var m in part.Means) w.WriteStringValue(m); w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("examples");
            foreach (var e in entry.Examples) { w.WriteStartObject(); w.WriteString("src", e.Src); w.WriteString("dst", e.Dst); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    // ---------- SetVocab ----------

    private VocabSettingsView? ProjectVocab(AppSettings s)
    {
        if (vocab is not { } service) return null;
        bool runtime = capabilityReady(Capability.Vocab);
        var present = VocabCatalog.All.Select(p => (Package: p, State: VocabTargets.Evaluate(s, p.InstanceId, config.Secrets.Has))).Where(x => x.State is not null).ToList();
        var status = service.Status([.. present.Select(x => x.Package.InstanceId)]).ToDictionary(x => x.Target);
        var targets = present.Select(x =>
        {
            var st = x.State!;
            var counts = status[x.Package.InstanceId];
            bool usable = st.Usable && runtime;
            string? reason = st.Usable && !runtime ? "vocab.reason.noRuntime" : st.ReasonKey;
            return new VocabTargetView(x.Package.InstanceId, st.Enabled, st.Availability.ToString(), usable, reason, st.Origin, x.Package.Local, x.Package.Lookup,
                counts.Pending, counts.Retrying, counts.Failed, counts.Uncertain, counts.Succeeded, counts.LastError, counts.LastPassError is not null);
        }).ToArray();
        var rows = service.Problems().Select(r => new VocabRowView(r.EntryId, r.Word, r.Target, r.Revision, r.State.ToString(), r.Attempts)).ToArray();
        return new VocabSettingsView(service.Count(), targets, rows, lastVocabExport, service.HasExporter && VocabSavePicker is not null);
    }

    private async Task<CommandResult> ExportVocabAsync(VocabExportCommand request)
    {
        if (vocab is not { HasExporter: true } service || VocabSavePicker is not { } picker) return new CommandResult(false, "unavailable");
        if (request.Format is not ("txt" or "csv" or "apkg")) return new CommandResult(false, "range");
        if (Interlocked.Exchange(ref vocabExporting, 1) == 1) return new CommandResult(false, "busy");
        try
        {
            string? path;
            try { path = await picker.PickAsync("su-su-vocabulary." + request.Format, request.Format, CancellationToken.None); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Diagnostic?.Invoke("vocab.picker-failed " + e.GetType().Name);
                path = "";
            }
            if (path is null) return Ok(SettingsElement()); // cancelled: nothing written, nothing to report
            VocabExportOutcome outcome;
            try
            {
                string full = path;
                outcome = path.Length == 0 ? new VocabExportOutcome(false, "export.pathInvalid", false, null, 0, 0, [])
                    : await Task.Run(() => service.Export(new VocabExportRequest(request.Format, full, request.Definitions, request.Phonetics, request.Examples, request.OnlyNew, request.Deck ?? ""), CancellationToken.None));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Diagnostic?.Invoke("vocab.export-failed " + e.GetType().Name);
                outcome = new VocabExportOutcome(false, "export.writeFailed", false, null, 0, 0, []);
            }
            lastVocabExport = new VocabExportView(request.Format, outcome.Ok ? outcome.Path : null, outcome.Ok ? null : outcome.Error, outcome.Retryable, outcome.Exported, outcome.Skipped, [.. outcome.Issues]);
            return Ok(SettingsElement());
        }
        finally { Interlocked.Exchange(ref vocabExporting, 0); }
    }

    private CommandResult ResolveVocab(VocabResolveRequest request)
    {
        if (vocab is not { } service) return new CommandResult(false, "unavailable");
        if (!service.Resolve(request.EntryId, request.Target, request.Revision, request.Delivered)) return new CommandResult(false, "not-found");
        return Ok(SettingsElement());
    }

    private async Task<CommandResult> SyncVocabAsync(VocabSyncCommand request)
    {
        if (vocab is not { } service) return new CommandResult(false, "unavailable");
        string? target = request.Target;
        if (request.Action != "sync" && (target is null || VocabCatalog.Find(target) is null)) return new CommandResult(false, "unknown-target");
        switch (request.Action)
        {
            case "sync": service.Kick(); break;
            case "retryFailed": service.RetryFailed(target!); break;
            case "queueExisting":
                if (!service.Targets().Contains(target!)) return new CommandResult(false, "not-usable");
                await Task.Run(() => service.QueueExisting(target!));
                break;
            default: return new CommandResult(false, "range");
        }
        return Ok(SettingsElement());
    }
}
