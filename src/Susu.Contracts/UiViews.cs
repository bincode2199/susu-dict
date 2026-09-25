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
    AccountView[] Accounts,
    PromptView? Prompt = null);

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
/// <remarks>
/// Config (F07.2): the package's manifest config schema with the instance's current values, the controls the
/// page generates; InstanceRevision is what Settings.SaveServiceConfig expects back.
/// </remarks>
[TsExport("ui")]
public sealed record ServiceView(string ServiceId, string InstanceId, string Capability, string Page, bool Enabled, string Availability, bool Implemented, string[] SecretNames, string? AccountId,
    CredentialTargetView[]? CredentialTargets = null, string? Plan = null, int Order = -1, long? UsageThisMonth = null, ConfigFieldView[]? Config = null, long InstanceRevision = 0);

/// <summary>
/// One generated settings control (PLAN 4.6 config schema + x-susu). Type: string | integer | number | boolean.
/// Value: the saved value (absent = the default applies). Dynamic: choices come from Settings.LoadOptions;
/// OptionsRevision is the field's dependency revision, which changes whenever a field, account or address it
/// depends on changes, and a loaded list is only shown while it still matches (CFG02).
/// </summary>
[TsExport("ui")]
public sealed record ConfigFieldView(string Name, string Type, string? Value = null, string? Default = null, string[]? Enum = null, string? Title = null,
    string? Format = null, double? Minimum = null, double? Maximum = null, string? Group = null, string? Placeholder = null, string? Help = null,
    string? ShowWhenField = null, string? ShowWhenEquals = null, bool Dynamic = false, long OptionsRevision = 0);

[TsExport("ui")]
public sealed record ConfigValueView(string Name, string Value);

/// <summary>Settings.SaveServiceConfig: an empty Value clears the field back to its default. Checked against the schema by the host.</summary>
[TsExport("ui")]
public sealed record ServiceConfigRequest(string InstanceId, long ExpectedInstanceRevision, ConfigValueView[] Values);

/// <summary>Settings.LoadOptions: one page of a dynamic field for the dependency revision the page shows.</summary>
[TsExport("ui")]
public sealed record LoadOptionsRequest(string InstanceId, string Field, long DependsOnRevision, string? Cursor = null, bool Refresh = false);

/// <summary>
/// Stale: the field's dependencies changed since DependsOnRevision; the page drops the result. Error: the kind
/// only; the page keeps the current selection. Cached: served from the 5-minute cache.
/// </summary>
[TsExport("ui")]
public sealed record OptionsView(string InstanceId, string Field, long DependsOnRevision, OptionItem[] Items, string? NextCursor = null, bool Cached = false,
    bool Stale = false, ErrorKind? Error = null);

/// <summary>Use: header:Authorization, signer:tencent-tc3, ... Granted: the user confirmed this package, origin and use for the secret.</summary>
[TsExport("ui")]
public sealed record CredentialTargetView(string Secret, string Origin, string Use, bool Saved, bool Granted);

[TsExport("ui")]
public sealed record AccountView(string Id, string Label, SecretSlotView[] Secrets, string[] UsedBy);

/// <summary>Saved is the only thing a page learns about a stored secret.</summary>
[TsExport("ui")]
public sealed record SecretSlotView(string Name, bool Saved);

/// <summary>
/// SetPrompt (F07.3). Level: a built-in level id or "" (none). Profile: the custom template in use or "" (the
/// built-in default). Scope: AI instance ids the prompt is sent to; AiServices lists the choices in display order.
/// Translation engines never receive a prompt.
/// </summary>
[TsExport("ui")]
public sealed record PromptView(string Level, string Profile, string[] Scope, string[] Levels, string[] AiServices, PromptProfileView[] Profiles, string DefaultTemplate, string[] Variables);

[TsExport("ui")]
public sealed record PromptProfileView(string Id, string Name, string Template);

/// <summary>Settings.SavePrompt: the whole SetPrompt page; ExpectedRevision/FileHash guard like Settings.Save.</summary>
[TsExport("ui")]
public sealed record PromptSaveRequest(long ExpectedRevision, string ExpectedFileHash, string Level, string Profile, string[] Scope, PromptProfileView[] Profiles);

/// <summary>Settings.PreviewPrompt: renders Template ("" = the built-in default) exactly as a task would, with a sample text.</summary>
[TsExport("ui")]
public sealed record PromptPreviewRequest(string Template, string Level, string Text, string From, string To);

/// <summary>Unknown: variable names that are not substituted and stay literal. Problem: why the template cannot be saved, or null.</summary>
[TsExport("ui")]
public sealed record PromptPreviewView(string Rendered, string[] Unknown, string? Problem = null);

/// <summary>Settings.TestNetwork: tests the proxy settings being edited (the saved password is used) through the network broker.</summary>
[TsExport("ui")]
public sealed record NetworkTestRequest(NetworkView Network);

/// <summary>
/// Result per path (CFG05). Route: proxy | direct | local (loopback services never use the proxy). Ok: the
/// origin answered (any HTTP status). Error: the kind only (network, timeout, ...). Services: the enabled
/// services on that origin.
/// </summary>
[TsExport("ui")]
public sealed record NetworkPathView(string Origin, string[] Services, string Route, bool Ok, ErrorKind? Error = null, int? Status = null, long ElapsedMs = 0);

[TsExport("ui")]
public sealed record NetworkTestView(NetworkPathView[] Paths, string TestedAt);

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
