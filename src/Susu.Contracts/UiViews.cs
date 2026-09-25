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
/// <remarks>
/// CredentialTargets (F06.3a): where each secret will be written under the current config - the service
/// and target shown when the user first enters a key (PLAN 4.5.4). Plan: DeepL's endpoint, "free" or "pro",
/// set by the host from the key's ":fx" suffix. Order: position in the merged translation order (translation and AI
/// services, the order result cards follow), -1 otherwise. UsageThisMonth: local count of characters sent this
/// month (DATA04; not a vendor balance), null when the host does not track the service.
/// </remarks>
[TsExport("ui")]
public sealed record ServiceView(string ServiceId, string InstanceId, string Capability, string Page, bool Enabled, string Availability, bool Implemented, string[] SecretNames, string? AccountId,
    CredentialTargetView[]? CredentialTargets = null, string? Plan = null, int Order = -1, long? UsageThisMonth = null);

/// <summary>Use: header:Authorization, signer:tencent-tc3, ... Granted: the user confirmed this package, origin and use for the secret.</summary>
[TsExport("ui")]
public sealed record CredentialTargetView(string Secret, string Origin, string Use, bool Saved, bool Granted);

[TsExport("ui")]
public sealed record AccountView(string Id, string Label, SecretSlotView[] Secrets, string[] UsedBy);

/// <summary>Saved is the only thing a page learns about a stored secret.</summary>
[TsExport("ui")]
public sealed record SecretSlotView(string Name, bool Saved);

[TsExport("ui")]
public sealed record ServiceToggle(string ServiceId, bool Enabled);

[TsExport("ui")]
public sealed record SettingsSaveRequest(long ExpectedRevision, string ExpectedFileHash, GeneralView General, HotkeyView[] Hotkeys, NetworkView Network, ServiceToggle[] Services);

/// <summary>
/// One-way credential write from the Settings window (PLAN 5.4). The value is never echoed back.
/// ConfirmGrants: the user confirmed the service and targets shown with the key field
/// (<see cref="ServiceView.CredentialTargets"/>); the host then grants this secret to the package for the
/// origin/use its config needs after the write (DeepL: the plan follows the key). Without it the key is
/// saved and bound but not usable until <c>Settings.BindAccount</c> confirms.
/// </summary>
[TsExport("ui")]
public sealed record SecretWriteRequest(string InstanceId, string SecretName, string Value, bool ConfirmGrants = false);

/// <summary>
/// Settings.BindAccount: binds the instance's secrets to <paramref name="AccountId"/> (an existing account
/// holding the same secret names, e.g. a shared Tencent Cloud account; null keeps the current binding) and,
/// with ConfirmGrants, grants every credential target the instance currently needs (a changed plan or address).
/// </summary>
[TsExport("ui")]
public sealed record BindAccountRequest(string InstanceId, string? AccountId = null, bool ConfirmGrants = true);

/// <summary>Settings.ValidateProvider: one minimal real call through the configured service, enabled or not.</summary>
[TsExport("ui")]
public sealed record ValidateProviderRequest(string ServiceId);

/// <summary>Credential: valid | invalid | unknown, kept apart from whether the service can be used right now (F06.2a).</summary>
[TsExport("ui")]
public sealed record ServiceValidationView(string ServiceId, string Credential, bool ServiceAvailable, ErrorKind? Error = null);

/// <summary>
/// Settings.ReorderService: moves a service to Index among the services of its own settings page, leaving the
/// other page's slots in place (CFG03). Merged: Index is a position in the merged list of the General page.
/// </summary>
[TsExport("ui")]
public sealed record ReorderServiceRequest(string ServiceId, int Index, bool Merged = false);

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
