using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Storage;
using Susu.Testing;
using Susu.Ui;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F17.1 Settings backup section, host side, with the real backup service on a temp data root and fake file dialogs: export with and without keys,
/// the password step, the preview of what a restore replaces, confirm by token, discard, the result of an import at start, undo, a failing dialog,
/// the section being absent without a service, and the Settings-only whitelist (DATA01/DATA09/S07 host part).
/// </summary>
public sealed class BackupWindowTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Password = "correct horse battery";

    private sealed class Dialogs : IBackupFilePicker
    {
        public string? SavePath, OpenPath;
        public int SaveCalls, OpenCalls;
        public bool Throw;
        public Task<string?> PickSaveAsync(string suggestedFileName, CancellationToken ct)
        {
            SaveCalls++;
            Assert.EndsWith(".susubak", suggestedFileName, StringComparison.Ordinal);
            return Throw ? Task.FromException<string?>(new InvalidOperationException("dialog")) : Task.FromResult(SavePath);
        }
        public Task<string?> PickOpenAsync(CancellationToken ct)
        {
            OpenCalls++;
            return Throw ? Task.FromException<string?>(new InvalidOperationException("dialog")) : Task.FromResult(OpenPath);
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly BackupRig Backup = new();
        public readonly FakePlatform Platform = new();
        public readonly ConfigService Config;
        public readonly ShellCoordinator Shell;
        public readonly Dialogs Dialog = new();
        private long counter;

        public Rig(bool withService = true)
        {
            Config = new ConfigService(Backup.Store, Backup.Secrets);
            var features = new FeatureRegistry();
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
            Shell = new ShellCoordinator(Platform, Config, features, c => c is Capability.Translate, new ShellOptions(false, false), _ => null, null,
                new TranslationBackend(true, (_, _) => null), null);
            if (withService)
            {
                Shell.Backup = Backup.Service;
                Shell.BackupPicker = Dialog;
            }
            Shell.Start();
            Shell.Open(WindowKind.Settings);
            Shell.OnPageMessage(WindowKind.Settings, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Ready", windowSessionId = Platform.Session(WindowKind.Settings) }));
        }

        public string LastJson = "";

        public CommandResult Run(string name, object? payload = null)
        {
            string id = $"c{++counter}";
            Shell.OnPageMessage(WindowKind.Settings, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Command", windowSessionId = Platform.Session(WindowKind.Settings), name, correlationId = id, payload = payload ?? new { } }, Web));
            for (var poll = System.Diagnostics.Stopwatch.StartNew(); poll.Elapsed < Eventually.DefaultTimeout;)
            {
                var hit = Platform.Posted.FirstOrDefault(p => p.Envelope.CorrelationId == id);
                if (hit.Envelope is not null)
                {
                    LastJson = hit.Envelope.Payload!.Value.GetRawText();
                    return hit.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.CommandResult)!;
                }
                Thread.Sleep(5);
            }
            throw new TimeoutException(id);
        }

        public BackupView View(CommandResult r)
        {
            Assert.True(r.Ok, r.Error);
            return r.Value!.Value.Deserialize(ContractsJson.Default.SettingsView)!.Backup!;
        }

        public BackupView Read() => View(Run(UiCommands.SettingsRead));

        public void Dispose() { Backup.Dispose(); }
    }

    [Fact] // the default export holds no key; the page gets the file name only
    public void Export_without_keys_writes_a_plain_file_and_reports_the_name_only()
    {
        using var rig = new Rig();
        rig.Backup.Seed();
        Assert.True(rig.Read().CanExport);
        rig.Dialog.SavePath = Path.Combine(rig.Backup.Root.Root, "out.susubak");
        var view = rig.View(rig.Run(UiCommands.BackupExport, new { includeSecrets = false }));
        Assert.Equal(("export", null, "out.susubak", false, false), (view.Last!.Action, view.Last.Error, view.Last.FileName, view.Last.Encrypted, view.Last.IncludedSecrets));
        Assert.True(File.Exists(rig.Dialog.SavePath));
        Assert.DoesNotContain(rig.Backup.Root.Root.Replace("\\", "\\\\"), rig.LastJson, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-test-SECRET-1234", rig.LastJson, StringComparison.Ordinal);
    }

    [Fact] // keys need a password, and the dialog is not even opened without one
    public void Export_with_keys_needs_a_password_before_any_dialog()
    {
        using var rig = new Rig();
        rig.Backup.Seed();
        rig.Dialog.SavePath = Path.Combine(rig.Backup.Root.Root, "k.susubak");
        Assert.Equal("password-required", rig.View(rig.Run(UiCommands.BackupExport, new { includeSecrets = true })).Last!.Error);
        Assert.Equal("password-length", rig.View(rig.Run(UiCommands.BackupExport, new { includeSecrets = true, password = "short" })).Last!.Error);
        Assert.Equal(0, rig.Dialog.SaveCalls);
        var ok = rig.View(rig.Run(UiCommands.BackupExport, new { includeSecrets = true, password = Password })).Last!;
        Assert.Equal((null, true, true, 1), (ok.Error, ok.Encrypted, ok.IncludedSecrets, ok.SecretCount));
        Assert.DoesNotContain(Password, rig.LastJson, StringComparison.Ordinal);
        Assert.StartsWith("SUSUBAK", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(rig.Dialog.SavePath)), StringComparison.Ordinal);
    }

    [Fact] // a cancelled dialog changes nothing; a failing dialog reports it
    public void Cancelled_and_failing_dialogs_are_handled()
    {
        using var rig = new Rig();
        var cancelled = rig.View(rig.Run(UiCommands.BackupExport, new { includeSecrets = false }));
        Assert.Null(cancelled.Last);
        Assert.Equal("idle", rig.View(rig.Run(UiCommands.BackupPick)).Step);
        rig.Dialog.Throw = true;
        Assert.Equal("picker", rig.View(rig.Run(UiCommands.BackupExport, new { includeSecrets = false })).Last!.Error);
        Assert.Equal("picker", rig.View(rig.Run(UiCommands.BackupPick)).Last!.Error);
    }

    [Fact] // pick, see what would be replaced, confirm by token; nothing changes before the restart
    public void Pick_previews_then_confirm_schedules_the_import_for_the_next_start()
    {
        using var source = new BackupRig();
        source.Seed("Source", "sk-source-KEY");
        string file = source.Export("plain.susubak", new BackupExportOptions(false, null));
        using var rig = new Rig();
        rig.Backup.Seed("Local", "sk-local-KEY", account: "acct-local");
        string settings = rig.Backup.SettingsHash, secrets = rig.Backup.SecretsHash;

        rig.Dialog.OpenPath = file;
        var view = rig.View(rig.Run(UiCommands.BackupPick));
        Assert.Equal(("preview", "plain.susubak"), (view.Step, view.FileName));
        var preview = view.Preview!;
        Assert.False(preview.IncludesSecrets);
        Assert.Equal("acct-deepl", preview.Accounts.Single().Id);
        Assert.Equal(["apiKey"], preview.Accounts.Single().MissingSecrets);
        Assert.Contains("accounts-need-authorization", preview.Conflicts);
        Assert.Contains("keys-removed", preview.Conflicts);
        Assert.Equal((3, 1), (preview.KeptFavorites, preview.KeptOutbox));
        Assert.Contains(preview.Deltas, d => d.Area == "accounts" && d is { Current: 1, Backup: 1, Differs: true });
        Assert.DoesNotContain("sk-source-KEY", rig.LastJson, StringComparison.Ordinal);
        Assert.Equal(settings, rig.Backup.SettingsHash);
        Assert.Equal(secrets, rig.Backup.SecretsHash);

        Assert.Equal("not-found", rig.Run(UiCommands.BackupApply, new { token = "wrong" }).Error);
        var applied = rig.View(rig.Run(UiCommands.BackupApply, new { token = preview.Token }));
        Assert.True(applied.Scheduled);
        Assert.Equal(("idle", "backup", "apply"), (applied.Step, applied.ScheduledSource, applied.Last!.Action));
        Assert.Equal(settings, rig.Backup.SettingsHash);
        Assert.Equal(secrets, rig.Backup.SecretsHash);

        // The restart: the storage layer switches it, the next view reports the result and offers undo.
        rig.Backup.Restart();
        var after = rig.Backup.Service.Status();
        Assert.Equal("Applied", after.Result!.State);
        Assert.True(after.CanUndo);
        Assert.Equal("Source", rig.Backup.Store.State.Effective.Accounts.Single().Label);
    }

    [Fact] // an encrypted file waits for its password; a wrong one keeps the step and the right one previews
    public void Encrypted_file_asks_for_the_password_and_a_wrong_one_changes_nothing()
    {
        using var source = new BackupRig();
        source.Seed("Source", "sk-source-KEY");
        string file = source.Export("k.susubak", new BackupExportOptions(true, Password));
        using var rig = new Rig();
        rig.Backup.Seed("Local");
        string settings = rig.Backup.SettingsHash, secrets = rig.Backup.SecretsHash;
        rig.Dialog.OpenPath = file;
        var locked = rig.View(rig.Run(UiCommands.BackupPick));
        Assert.Equal(("password", "k.susubak", null), (locked.Step, locked.FileName, locked.Preview));
        var wrong = rig.View(rig.Run(UiCommands.BackupUnlock, new { password = "wrong password!" }));
        Assert.Equal(("password", "decrypt-failed"), (wrong.Step, wrong.Last!.Error));
        Assert.DoesNotContain("wrong password!", rig.LastJson, StringComparison.Ordinal);
        Assert.Equal(settings, rig.Backup.SettingsHash);
        Assert.Equal(secrets, rig.Backup.SecretsHash);
        var ok = rig.View(rig.Run(UiCommands.BackupUnlock, new { password = Password }));
        Assert.Equal("preview", ok.Step);
        Assert.True(ok.Preview!.Encrypted);
        Assert.True(ok.Preview.IncludesSecrets);
        Assert.Equal(1, ok.Preview.BackupSecrets);
        Assert.DoesNotContain(Password, rig.LastJson, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-source-KEY", rig.LastJson, StringComparison.Ordinal);
    }

    [Fact] // no chosen file means unlock and apply have nothing to act on
    public void Unlock_and_apply_without_a_chosen_file_are_refused()
    {
        using var rig = new Rig();
        Assert.Equal("not-found", rig.Run(UiCommands.BackupUnlock, new { password = Password }).Error);
        Assert.Equal("not-found", rig.Run(UiCommands.BackupApply, new { token = "x" }).Error);
    }

    [Fact] // a refused file ends the import with a reason key and keeps the page idle
    public void A_file_that_is_not_a_backup_is_refused_with_a_reason()
    {
        using var rig = new Rig();
        string junk = Path.Combine(rig.Backup.Root.Root, "junk.susubak");
        File.WriteAllText(junk, "not a backup at all");
        rig.Dialog.OpenPath = junk;
        var view = rig.View(rig.Run(UiCommands.BackupPick));
        Assert.Equal(("idle", "preview", "not-a-backup"), (view.Step, view.Last!.Action, view.Last.Error));
        Assert.Null(view.Preview);
        Assert.Equal("None", rig.Backup.Service.Status().State);
    }

    [Fact] // discard drops the preview and a scheduled import
    public void Discard_drops_the_preview_and_a_scheduled_import()
    {
        using var source = new BackupRig();
        source.Seed("Source");
        string file = source.Export("plain.susubak", new BackupExportOptions(false, null));
        using var rig = new Rig();
        rig.Dialog.OpenPath = file;
        var token = rig.View(rig.Run(UiCommands.BackupPick)).Preview!.Token;
        Assert.True(rig.View(rig.Run(UiCommands.BackupApply, new { token })).Scheduled);
        var view = rig.View(rig.Run(UiCommands.BackupDiscard));
        Assert.False(view.Scheduled);
        Assert.Equal("idle", view.Step);
        Assert.Equal("None", rig.Backup.Service.Status().State);
    }

    [Fact] // the result of an import at start is shown until dismissed; undo is scheduled through the same path
    public void The_import_result_is_shown_dismissed_and_undo_is_scheduled()
    {
        using var source = new BackupRig();
        source.Seed("Source");
        string file = source.Export("plain.susubak", new BackupExportOptions(false, null));
        using var rig = new Rig();
        rig.Backup.Seed("Local");
        Assert.Equal("unavailable", rig.Run(UiCommands.BackupUndo).Error);
        rig.Dialog.OpenPath = file;
        rig.Run(UiCommands.BackupApply, new { token = rig.View(rig.Run(UiCommands.BackupPick)).Preview!.Token });
        BackupImport.ApplyPending(rig.Backup.Paths, rig.Backup.Clock);
        var view = rig.Read();
        Assert.Equal(("Applied", null, "backup"), (view.Applied!.State, view.Applied.Error, view.Applied.Source));
        Assert.True(view.CanUndo);
        Assert.Null(rig.View(rig.Run(UiCommands.BackupDismiss)).Applied);
        var undo = rig.View(rig.Run(UiCommands.BackupUndo));
        Assert.Equal(("undo", true, "undo"), (undo.Last!.Action, undo.Scheduled, undo.ScheduledSource));
    }

    [Fact] // F17.3: a full disk while preparing, confirming or scheduling an undo reaches the page as the disk-full reason, with nothing scheduled
    public void A_full_disk_while_preparing_confirming_or_scheduling_undo_reaches_the_page_as_disk_full()
    {
        using var source = new BackupRig();
        source.Seed("Source");
        string file = source.Export("plain.susubak", new BackupExportOptions(false, null));
        using var rig = new Rig();
        rig.Backup.Seed("Local");
        BackupService Faulty(string stage) => new(rig.Backup.Paths, rig.Backup.Store, rig.Backup.Secrets, rig.Backup.Protector, rig.Backup.Clock,
            new BackupHost("1.2.3", () => rig.Backup.Available, () => rig.Backup.UserPlugins, () => rig.Backup.Local), new FaultAt(stage, _ => new IOException("full", unchecked((int)0x80070070))));
        rig.Dialog.OpenPath = file;

        rig.Shell.Backup = Faulty("import-stage:secrets"); // preparing
        var preparing = rig.View(rig.Run(UiCommands.BackupPick));
        Assert.Equal(("idle", "preview", "disk-full"), (preparing.Step, preparing.Last!.Action, preparing.Last.Error));
        Assert.Null(preparing.Preview);

        rig.Shell.Backup = rig.Backup.Service;
        var preview = rig.View(rig.Run(UiCommands.BackupPick)).Preview!;
        rig.Shell.Backup = Faulty("import-apply:pending"); // confirming
        var confirming = rig.View(rig.Run(UiCommands.BackupApply, new { token = preview.Token }));
        Assert.Equal(("idle", "apply", "disk-full", false), (confirming.Step, confirming.Last!.Action, confirming.Last.Error, confirming.Scheduled));
        Assert.Null(confirming.Preview);

        rig.Shell.Backup = rig.Backup.Service; // a real import, so there is something to undo
        rig.Run(UiCommands.BackupApply, new { token = rig.View(rig.Run(UiCommands.BackupPick)).Preview!.Token });
        rig.Backup.Restart();
        rig.Shell.Backup = Faulty("import-stage:secrets"); // scheduling the undo
        var undo = rig.View(rig.Run(UiCommands.BackupUndo));
        Assert.Equal(("undo", "disk-full", false, true), (undo.Last!.Action, undo.Last.Error, undo.Scheduled, undo.CanUndo));
    }

    [Fact] // without a service there is no section and every command is unavailable
    public void Without_a_service_the_section_is_absent()
    {
        using var rig = new Rig(withService: false);
        Assert.Null(rig.Run(UiCommands.SettingsRead).Value!.Value.Deserialize(ContractsJson.Default.SettingsView)!.Backup);
        var payloads = new Dictionary<string, object>
        {
            [UiCommands.BackupExport] = new { includeSecrets = false }, [UiCommands.BackupPick] = new { }, [UiCommands.BackupUnlock] = new { password = "x" },
            [UiCommands.BackupApply] = new { token = "x" }, [UiCommands.BackupDiscard] = new { }, [UiCommands.BackupUndo] = new { }, [UiCommands.BackupDismiss] = new { },
        };
        foreach (var (name, payload) in payloads)
            Assert.Equal("unavailable", rig.Run(name, payload).Error);
    }

    [Fact] // S07: the backup commands exist only in the Settings window
    public void Backup_commands_are_allowed_only_from_the_Settings_window()
    {
        foreach (var name in new[] { UiCommands.BackupExport, UiCommands.BackupPick, UiCommands.BackupUnlock, UiCommands.BackupApply, UiCommands.BackupDiscard, UiCommands.BackupUndo, UiCommands.BackupDismiss })
            foreach (var window in Enum.GetValues<WindowKind>())
                Assert.Equal(window == WindowKind.Settings, UiCommands.IsAllowed(window, name));
    }
}
