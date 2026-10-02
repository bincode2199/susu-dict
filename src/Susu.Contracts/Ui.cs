using System.Text.Json;
using System.Text.Json.Serialization;

namespace Susu.Contracts;

[TsExport("ui")]
[JsonConverter(typeof(JsonStringEnumConverter<WindowKind>))]
public enum WindowKind { Main, Selection, Clipboard, Ocr, Voice, Transcribe, Settings, Error, Tray, Speech }

[TsExport("ui")]
[JsonConverter(typeof(JsonStringEnumConverter<UiMessageKind>))]
public enum UiMessageKind { Ready, Command, Result, Snapshot, Patch, Event }

/// <summary>
/// UI bridge envelope (ARCHITECTURE 6). Grants, secrets and real file paths never appear in it.
/// Host → page: Snapshot(revision) then Patch(sequence); page → host: Ready, Command(correlationId).
/// </summary>
[TsExport("ui")]
public sealed record UiEnvelope(
    int UiVersion,
    UiMessageKind Kind,
    string WindowSessionId,
    long Sequence = 0,
    string? Name = null,
    string? CorrelationId = null,
    JsonElement? Payload = null);

[TsExport("ui")]
[JsonConverter(typeof(JsonStringEnumConverter<CardState>))]
public enum CardState { CollapsedIdle, Queued, Loading, Streaming, Ready, Failed, Cancelled, Unsupported }

[TsExport("ui")]
public sealed record CardSnapshot(
    string ServiceId,
    string DisplayName,
    CardState State,
    bool Collapsed,
    string Text,
    ErrorKind? Error = null,
    bool Chunked = false,
    bool Dictionary = false,
    DictionaryEntryView? Entry = null);

/// <summary>
/// A dictionary card body (PLAN 6.1, F09.2): <see cref="CardSnapshot.Dictionary"/> is true and <see cref="CardSnapshot.Entry"/>
/// is set only when the card shows a non-empty dictionary entry; a legal empty entry falls back to the plain
/// translation in <see cref="CardSnapshot.Text"/>. Structured text only: every string is plain text the page must
/// render as text, never as HTML. Audio links never reach the page: <see cref="DictionaryPhoneticView.AudioId"/> is
/// an opaque, session-scoped id the host resolves (F09.3 authorizes playback).
/// </summary>
[TsExport("ui")]
public sealed record DictionaryEntryView(string Word, DictionaryPhoneticView[] Phonetics, DictionaryPartView[] Parts, DictionaryFormView[] Forms, DictionaryExampleView[] Examples);

[TsExport("ui")] public sealed record DictionaryPhoneticView(string Accent, string Ipa, string? AudioId = null);
[TsExport("ui")] public sealed record DictionaryPartView(string Pos, string[] Means);
[TsExport("ui")] public sealed record DictionaryFormView(string Name, string Value);
[TsExport("ui")] public sealed record DictionaryExampleView(string Src, string Dst);

[TsExport("ui")]
public sealed record TranslationSnapshot(
    long Revision,
    long Generation,
    string SourceText,
    string From,
    string To,
    CardSnapshot[] Cards,
    bool Offline = false);

/// <summary>
/// Offline (ARCHITECTURE 13, DESIGN "译文 · 离线"): true only when every card that requested in this
/// generation failed with a retryable network error. Each card keeps its own error either way; the page
/// shows the shared notice on top of, never instead of, cards that already hold text.
/// </summary>
[TsExport("ui")]
public sealed record CardPatch(long Revision, long Generation, CardSnapshot Card, bool Offline = false);

[TsExport("ui")]
public sealed record CommandResult(bool Ok, string? Error = null, JsonElement? Value = null);

/// <summary>
/// Per-window command whitelist (ARCHITECTURE 6, PLAN 4.5.5). Credential commands are accepted only
/// from the Settings window; nothing offers a generic HTTP/file/code proxy.
/// </summary>
public static class UiCommands
{
    public const string SubmitText = "Translation.SubmitText", ToggleCard = "Translation.ToggleCard", RetryCard = "Translation.RetryCard",
        SelectLanguage = "Translation.SelectLanguage", CopyText = "Window.CopyText", Close = "Window.Close", Pin = "Window.Pin",
        Minimize = "Window.Minimize", Maximize = "Window.Maximize",
        SettingsRead = "Settings.Read", SettingsSave = "Settings.Save", ValidateProvider = "Settings.ValidateProvider",
        LoadOptions = "Settings.LoadOptions", BindAccount = "Settings.BindAccount", ReorderService = "Settings.ReorderService",
        SaveServiceConfig = "Settings.SaveServiceConfig", SavePrompt = "Settings.SavePrompt", PreviewPrompt = "Settings.PreviewPrompt",
        TestNetwork = "Settings.TestNetwork", SelectSpeech = "Settings.SelectSpeech", SaveOcr = "Settings.SaveOcr", VocabExport = "Vocab.Export", VocabResolve = "Vocab.Resolve", VocabSync = "Vocab.Sync",
        PluginPick = "Plugin.Pick", PluginConfirm = "Plugin.Confirm", PluginDiscard = "Plugin.Discard", PluginUninstall = "Plugin.Uninstall", PluginCheckUpdates = "Plugin.CheckUpdates",
        BackupExport = "Backup.Export", BackupPick = "Backup.Pick", BackupUnlock = "Backup.Unlock", BackupApply = "Backup.Apply", BackupDiscard = "Backup.Discard", BackupUndo = "Backup.Undo", BackupDismiss = "Backup.Dismiss",
        AboutOpenLogs = "About.OpenLogs", AboutExportDiagnostics = "About.ExportDiagnostics", AboutDismiss = "About.Dismiss", DataClear = "Data.Clear",
        UpdateCheck = "Update.Check", UpdateDownload = "Update.Download", UpdateInstall = "Update.Install", UpdateDiscard = "Update.Discard", UpdateAutoCheck = "Update.AutoCheck",
        SecretWriteNew = "Secret.WriteNew", SecretDelete = "Secret.Delete", SecretExportEncrypted = "Secret.ExportEncrypted", SecretImportEncrypted = "Secret.ImportEncrypted",
        BeginCapture = "Capture.BeginCapture", StartRecording = "Audio.StartRecording", PauseRecording = "Audio.PauseRecording", StopRecording = "Audio.StopRecording",
        CancelRecording = "Audio.CancelRecording", TranscribeRecorded = "Audio.TranscribeRecorded",
        PickMedia = "Transcription.PickMedia", StartTranscription = "Transcription.Start", PauseTranscription = "Transcription.Pause",
        ChangeTranslator = "Transcription.ChangeTranslator", Export = "Transcription.Export",
        ConfirmTranscription = "Transcription.Confirm", ResumeTranscription = "Transcription.Resume", CancelTranscription = "Transcription.Cancel",
        Collect = "Vocab.Collect", Speak = "Vocab.Speak", TrayOpen = "Tray.Open", TrayExit = "Tray.Exit", OpenSettings = "Window.OpenSettings", Painted = "Window.Painted",
        OpenInMain = "Window.OpenInMain", FitContent = "Window.FitContent",
        SpeakCard = "Speech.SpeakCard", SpeechPlay = "Speech.Play", SpeechStop = "Speech.Stop";

    private static readonly WindowKind[] resultWindows = [WindowKind.Main, WindowKind.Selection, WindowKind.Clipboard, WindowKind.Ocr, WindowKind.Voice];
    private static readonly WindowKind[] allWindows = Enum.GetValues<WindowKind>();

    private static readonly Dictionary<string, WindowKind[]> allowed = new(StringComparer.Ordinal)
    {
        [SubmitText] = resultWindows, [ToggleCard] = resultWindows, [RetryCard] = resultWindows, [SelectLanguage] = resultWindows,
        [CopyText] = [.. resultWindows, WindowKind.Transcribe],
        [Collect] = resultWindows, [Speak] = resultWindows,
        [Close] = allWindows, [Pin] = [WindowKind.Main, WindowKind.Selection, WindowKind.Clipboard],
        [Minimize] = [WindowKind.Main, WindowKind.Settings, WindowKind.Ocr, WindowKind.Voice, WindowKind.Transcribe], [Maximize] = [WindowKind.Main, WindowKind.Settings, WindowKind.Transcribe],
        [SettingsRead] = [WindowKind.Settings], [SettingsSave] = [WindowKind.Settings], [ValidateProvider] = [WindowKind.Settings],
        [LoadOptions] = [WindowKind.Settings], [BindAccount] = [WindowKind.Settings], [ReorderService] = [WindowKind.Settings],
        [SaveServiceConfig] = [WindowKind.Settings], [SavePrompt] = [WindowKind.Settings], [PreviewPrompt] = [WindowKind.Settings],
        [TestNetwork] = [WindowKind.Settings], [SelectSpeech] = [WindowKind.Settings], [SaveOcr] = [WindowKind.Settings], [VocabExport] = [WindowKind.Settings], [VocabResolve] = [WindowKind.Settings], [VocabSync] = [WindowKind.Settings],
        [PluginPick] = [WindowKind.Settings], [PluginConfirm] = [WindowKind.Settings], [PluginDiscard] = [WindowKind.Settings], [PluginUninstall] = [WindowKind.Settings], [PluginCheckUpdates] = [WindowKind.Settings],
        [BackupExport] = [WindowKind.Settings], [BackupPick] = [WindowKind.Settings], [BackupUnlock] = [WindowKind.Settings], [BackupApply] = [WindowKind.Settings],
        [BackupDiscard] = [WindowKind.Settings], [BackupUndo] = [WindowKind.Settings], [BackupDismiss] = [WindowKind.Settings],
        [AboutOpenLogs] = [WindowKind.Settings], [AboutExportDiagnostics] = [WindowKind.Settings], [AboutDismiss] = [WindowKind.Settings], [DataClear] = [WindowKind.Settings],
        [UpdateCheck] = [WindowKind.Settings], [UpdateDownload] = [WindowKind.Settings], [UpdateInstall] = [WindowKind.Settings], [UpdateDiscard] = [WindowKind.Settings], [UpdateAutoCheck] = [WindowKind.Settings],
        [SecretWriteNew] = [WindowKind.Settings], [SecretDelete] = [WindowKind.Settings],
        [SecretExportEncrypted] = [WindowKind.Settings], [SecretImportEncrypted] = [WindowKind.Settings],
        [BeginCapture] = [WindowKind.Ocr], [StartRecording] = [WindowKind.Voice], [PauseRecording] = [WindowKind.Voice], [StopRecording] = [WindowKind.Voice],
        [CancelRecording] = [WindowKind.Voice], [TranscribeRecorded] = [WindowKind.Voice],
        [PickMedia] = [WindowKind.Transcribe], [StartTranscription] = [WindowKind.Transcribe], [PauseTranscription] = [WindowKind.Transcribe],
        [ChangeTranslator] = [WindowKind.Transcribe], [Export] = [WindowKind.Transcribe],
        [ConfirmTranscription] = [WindowKind.Transcribe], [ResumeTranscription] = [WindowKind.Transcribe], [CancelTranscription] = [WindowKind.Transcribe],
        [TrayOpen] = [WindowKind.Tray], [TrayExit] = [WindowKind.Tray],
        [OpenSettings] = [.. resultWindows, WindowKind.Transcribe, WindowKind.Error],
        // F08.3: the floating window hands its text to the main window; auto-height windows report their content height.
        [OpenInMain] = [WindowKind.Selection, WindowKind.Clipboard],
        [FitContent] = [WindowKind.Selection, WindowKind.Clipboard, WindowKind.Voice, WindowKind.Error],
        // Local timing signal only (ARCHITECTURE 11 UiReady/first frame): no payload, never leaves the machine.
        [Painted] = allWindows,
        // F10.2: card read-aloud keys in result windows; the pronunciation bar replays with one of its services; both can stop.
        [SpeakCard] = resultWindows, [SpeechPlay] = [WindowKind.Speech], [SpeechStop] = [.. resultWindows, WindowKind.Speech],
    };

    public static IReadOnlyCollection<string> All => allowed.Keys;

    /// <summary>True only for a known command sent from a window kind that may issue it.</summary>
    public static bool IsAllowed(WindowKind window, string? command)
        => command is not null && allowed.TryGetValue(command, out var kinds) && Array.IndexOf(kinds, window) >= 0;
}
