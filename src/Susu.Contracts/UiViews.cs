namespace Susu.Contracts;

// ---- UI view DTOs (ARCHITECTURE 6). Projections only: no secrets, grants or real file paths. ----

/// <summary>First message to a page after Ready; later changes arrive as Patch/Event.</summary>
[TsExport("ui")]
public sealed record UiSnapshot(WindowView Window, TranslationSnapshot? Translation = null, SettingsView? Settings = null, TrayView? Tray = null);

[TsExport("ui")]
public sealed record WindowView(WindowKind Kind, string UiLanguage, string Theme, bool Maximized, bool Pinned, bool DevPreview, string[] Features);

[TsExport("ui")]
public sealed record TrayView(TrayItemView[] Items);

/// <summary>A tray menu entry; unimplemented or unconfigured features are listed disabled with a reason, never as a dead link.</summary>
[TsExport("ui")]
public sealed record TrayItemView(string Id, string Chord, bool Enabled, string? ReasonKey = null, bool SeparatorBefore = false);

[TsExport("ui")]
public sealed record SettingsView(
    long Revision,
    string FileHash,
    SettingsIssueView[] Issues,
    GeneralView General,
    HotkeyView[] Hotkeys,
    NetworkView Network,
    ServiceView[] Services,
    AccountView[] Accounts);

[TsExport("ui")]
public sealed record SettingsIssueView(string Path, string Code, string Message, int Line);

[TsExport("ui")]
public sealed record GeneralView(string UiLanguage, string SourceLanguage, string TargetLanguage, int DefaultExpandedCards, bool AllowClipboardBorrowing, string CloseAction, bool LaunchAtStartup);

/// <summary>State: ok, conflict (in-app), failed (RegisterHotKey refused), unassigned, unavailable (feature not provided).</summary>
[TsExport("ui")]
public sealed record HotkeyView(string Action, string Chord, string State, string? ReasonKey = null);

/// <summary>The proxy password is never sent to a page; only whether one is saved.</summary>
[TsExport("ui")]
public sealed record NetworkView(string ProxyMode, string ProxyHost, int ProxyPort, string ProxyUsername, bool ProxyPasswordSaved, int AiTimeoutSeconds);

/// <summary>Availability: Disabled, MissingCredential, UnsupportedCapability, Ready, TemporarilyUnavailable. Implemented=false: adapter not built yet.</summary>
[TsExport("ui")]
public sealed record ServiceView(string ServiceId, string InstanceId, string Capability, string Page, bool Enabled, string Availability, bool Implemented, string[] SecretNames, string? AccountId);

[TsExport("ui")]
public sealed record AccountView(string Id, string Label, SecretSlotView[] Secrets, string[] UsedBy);

/// <summary>Saved is the only thing a page learns about a stored secret.</summary>
[TsExport("ui")]
public sealed record SecretSlotView(string Name, bool Saved);

[TsExport("ui")]
public sealed record ServiceToggle(string ServiceId, bool Enabled);

[TsExport("ui")]
public sealed record SettingsSaveRequest(long ExpectedRevision, string ExpectedFileHash, GeneralView General, HotkeyView[] Hotkeys, NetworkView Network, ServiceToggle[] Services);

/// <summary>One-way credential write from the Settings window (PLAN 5.4). The value is never echoed back.</summary>
[TsExport("ui")]
public sealed record SecretWriteRequest(string InstanceId, string SecretName, string Value);

[TsExport("ui")]
public sealed record SecretDeleteRequest(string InstanceId, string SecretName);

[TsExport("ui")]
public sealed record SubmitTextRequest(string Text);

[TsExport("ui")]
public sealed record ToggleCardRequest(string ServiceId);

[TsExport("ui")]
public sealed record SelectLanguageRequest(string From, string To);

[TsExport("ui")]
public sealed record TrayOpenRequest(string Id);
