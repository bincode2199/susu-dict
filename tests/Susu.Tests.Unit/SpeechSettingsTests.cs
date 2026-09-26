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
/// F07.4 SetSpeech/SetSpeechB: three independent selections (A02), capability filtering, the video timecode rule for
/// text-only ASR (A03), shared accounts only through explicit grants, and no speech provider running before F10/F12.
/// </summary>
public class SpeechSettingsTests
{
    internal sealed class Rig : IDisposable
    {
        public readonly TempRoot Root = new();
        public readonly FakePlatform Platform = new();
        public readonly SettingsStore Settings;
        public readonly ConfigService Config;
        public readonly ShellCoordinator Shell;
        private long counter;
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        public Rig()
        {
            Settings = new SettingsStore(Root.Paths, new ManualClock());
            Config = new ConfigService(Settings, new SecretStore(Root.Paths.Secrets, new XorProtector()));
            var features = new FeatureRegistry();
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.InputTranslation, FeatureState.Available, null, [Capability.Translate]));
            Shell = new ShellCoordinator(Platform, Config, features, c => c == Capability.Translate, new ShellOptions(false, false), _ => null, null,
                new TranslationBackend(true, (_, _) => null));
            Shell.Start();
            Shell.Open(WindowKind.Settings);
            Shell.OnPageMessage(WindowKind.Settings, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Ready", windowSessionId = Platform.Session(WindowKind.Settings) }));
        }

        public CommandResult Run(string name, object payload)
        {
            string id = $"c{++counter}";
            Shell.OnPageMessage(WindowKind.Settings, JsonSerializer.Serialize(new { uiVersion = 1, kind = "Command", windowSessionId = Platform.Session(WindowKind.Settings), name, correlationId = id, payload }, Web));
            for (var poll = System.Diagnostics.Stopwatch.StartNew(); poll.Elapsed < Eventually.DefaultTimeout;)
            {
                var hit = Platform.Posted.FirstOrDefault(p => p.Envelope.CorrelationId == id);
                if (hit.Envelope is not null) return hit.Envelope.Payload!.Value.Deserialize(ContractsJson.Default.CommandResult)!;
                Thread.Sleep(5);
            }
            throw new TimeoutException(id);
        }

        public SettingsView View => Shell.ProjectSettings(Settings.State);
        public SpeechView Speech => View.Speech!;

        public CommandResult Select(string slot, string instance, string model)
            => Run(UiCommands.SelectSpeech, new SpeechSelectRequest(View.Revision, View.FileHash, slot, instance, model));

        public void Dispose() { Settings.Dispose(); Root.Dispose(); }
    }

    [Fact]
    public void Defaults_pronounce_with_sapi_and_transcribe_with_whisper_and_only_pronunciation_is_ready_before_F12()
    {
        using var rig = new Rig();
        var speech = rig.Speech;
        Assert.Equal(("native-sapi", ""), (speech.Tts.Instance, speech.Tts.Model));
        Assert.Equal(("openai-asr", "whisper-1"), (speech.Asr.Instance, speech.Asr.Model));
        Assert.Equal(("openai-asr", "whisper-1"), (speech.VideoAsr.Instance, speech.VideoAsr.Model));
        Assert.True(speech.Tts.Ready); // F10.2: native SAPI is installed and needs no credentials
        Assert.Null(speech.Tts.ReasonKey);
        Assert.Equal("not-installed", speech.Asr.ReasonKey);
        Assert.Equal("not-installed", speech.VideoAsr.ReasonKey);
        // Planned packages are catalog entries only: listed, not installed, never implemented.
        Assert.All(rig.View.Services.Where(s => s.Capability is "tts" or "asr"), s => Assert.False(s.Implemented));
        Assert.All(speech.Asr.Choices, c => Assert.False(c.Installed));
    }

    [Fact]
    public void Choices_are_filtered_by_the_declared_capability()
    {
        using var rig = new Rig();
        var speech = rig.Speech;
        Assert.Equal(["native-sapi", "microsoft-tts", "google-tts", "tencent-tts"], speech.Tts.Choices.Select(c => c.InstanceId));
        Assert.True(speech.Tts.Choices[0].Native);
        Assert.Equal(["openai-asr", "gemini-asr"], speech.Asr.Choices.Select(c => c.InstanceId));
        Assert.Equal(["openai-asr", "gemini-asr"], speech.VideoAsr.Choices.Select(c => c.InstanceId));
        Assert.All(speech.Asr.Choices, c => Assert.True(c.Selectable));

        // A TTS package cannot transcribe and an ASR package cannot pronounce; OCR/translation are never speech.
        Assert.Equal("capability", rig.Select("asr", "microsoft-tts", "").Error);
        Assert.Equal("capability", rig.Select("tts", "openai-asr", "whisper-1").Error);
        Assert.Equal("capability", rig.Select("tts", "tencent-ocr", "").Error);
        Assert.Equal("capability", rig.Select("asr", "openai", "whisper-1").Error);
        Assert.Equal("model", rig.Select("asr", "openai-asr", "gpt-4o").Error);
        Assert.Equal("model", rig.Select("tts", "google-tts", "whisper-1").Error);
        Assert.Equal("openai-asr", rig.Speech.Asr.Instance);
    }

    [Fact]
    public void A02_each_selection_changes_alone_and_never_the_ai_translation_model()
    {
        using var rig = new Rig();
        // The AI translation model is the openai instance's config.
        var s = rig.Config.State.Effective;
        var withModel = s with { Instances = [.. s.Instances.Select(i => i.Id == "openai" ? i with { Config = new Dictionary<string, string> { ["model"] = "gpt-4.1-mini" } } : i)] };
        Assert.Equal(SaveStatus.Saved, rig.Config.Save(withModel, rig.Config.State.Revision, rig.Config.State.FileHash).Status);
        var openaiBefore = rig.Config.State.Effective.Instances.First(i => i.Id == "openai");

        Assert.True(rig.Select("asr", "openai-asr", "gpt-4o-transcribe").Ok);
        var speech = rig.Speech;
        Assert.Equal(("openai-asr", "gpt-4o-transcribe"), (speech.Asr.Instance, speech.Asr.Model));
        Assert.Equal(("openai-asr", "whisper-1"), (speech.VideoAsr.Instance, speech.VideoAsr.Model));
        Assert.Equal("native-sapi", speech.Tts.Instance);

        Assert.True(rig.Select("tts", "tencent-tts", "").Ok);
        Assert.True(rig.Select("asr", "gemini-asr", "gemini-2.5-flash").Ok);
        speech = rig.Speech;
        Assert.Equal("tencent-tts", speech.Tts.Instance);
        Assert.Equal(("gemini-asr", "gemini-2.5-flash"), (speech.Asr.Instance, speech.Asr.Model));
        Assert.Equal(("openai-asr", "whisper-1"), (speech.VideoAsr.Instance, speech.VideoAsr.Model));

        var openaiAfter = rig.Config.State.Effective.Instances.First(i => i.Id == "openai");
        Assert.Equal("gpt-4.1-mini", openaiAfter.Config["model"]);
        Assert.Equal(openaiBefore.Revision, openaiAfter.Revision);
        Assert.Equal(rig.Config.State.Effective.Services, withModel.Services);
    }

    [Fact]
    public void A03_text_only_asr_is_offered_for_recording_but_disabled_for_video_and_never_saved_there()
    {
        using var rig = new Rig();
        Assert.True(rig.Select("asr", "gemini-asr", "gemini-2.5-flash").Ok);
        var speech = rig.Speech;
        var gemini = speech.VideoAsr.Choices.Single(c => c.InstanceId == "gemini-asr");
        Assert.False(gemini.Selectable);
        Assert.False(gemini.Timecodes);
        Assert.Equal("needs-timecodes", gemini.ReasonKey);
        Assert.True(speech.Asr.Choices.Single(c => c.InstanceId == "gemini-asr").Selectable);
        // OpenAI: whisper-1 returns segments; the gpt-4o transcribe models are text only.
        var openai = speech.VideoAsr.Choices.Single(c => c.InstanceId == "openai-asr");
        Assert.True(openai.Selectable);
        Assert.Equal(["whisper-1"], openai.Models.Where(m => m.Selectable).Select(m => m.Id));

        long revision = rig.View.Revision;
        Assert.Equal("needs-timecodes", rig.Select("videoAsr", "gemini-asr", "gemini-2.5-flash").Error);
        Assert.Equal("needs-timecodes", rig.Select("videoAsr", "openai-asr", "gpt-4o-transcribe").Error);
        Assert.Equal(revision, rig.View.Revision);
        Assert.Equal(("openai-asr", "whisper-1"), (rig.Speech.VideoAsr.Instance, rig.Speech.VideoAsr.Model));

        // Clearing the video selection leaves video unavailable with the timecode explanation.
        Assert.True(rig.Select("videoAsr", "", "").Ok);
        Assert.False(rig.Speech.VideoAsr.Ready);
        Assert.Equal("needs-timecodes", rig.Speech.VideoAsr.ReasonKey);
        Assert.Equal("gemini-asr", rig.Speech.Asr.Instance);
    }

    [Fact]
    public void A02_shared_openai_account_needs_its_own_grant_for_asr()
    {
        using var rig = new Rig();
        Assert.True(rig.Run(UiCommands.SecretWriteNew, new SecretWriteRequest("openai", "apiKey", "sk-test-shared", ConfirmGrants: true)).Ok);
        Assert.True(rig.View.Services.Single(s => s.ServiceId == "openai/translate").CredentialTargets!.Single().Granted);

        // Binding without confirming: the account is bound, but no grant for the ASR package exists.
        Assert.True(rig.Run(UiCommands.BindAccount, new BindAccountRequest("openai-asr", "openai", ConfirmGrants: false)).Ok);
        var asr = rig.Speech.Asr.Choices.Single(c => c.InstanceId == "openai-asr");
        Assert.Equal("MissingCredential", asr.Availability);
        var target = rig.View.Services.Single(s => s.ServiceId == "openai-asr/asr").CredentialTargets!.Single();
        Assert.Equal(("https://api.openai.com:443", "header:Authorization", true, false), (target.Origin, target.Use, target.Saved, target.Granted));
        var instance = rig.Config.State.Effective.Instances.First(i => i.Id == "openai-asr");
        Assert.Equal(CredentialDecision.IdentityChanged,
            CredentialAuthorizer.Authorize(rig.Config.State.Effective.Accounts, instance, new PluginIdentity("app.susu.openai-asr", "unsigned:app.susu.openai-asr"), "apiKey", "https://api.openai.com:443", "header:Authorization").Decision);

        // Explicit confirmation grants exactly this package, origin and use on the shared account.
        Assert.True(rig.Run(UiCommands.BindAccount, new BindAccountRequest("openai-asr", null, ConfirmGrants: true)).Ok);
        Assert.Equal("Ready", rig.Speech.Asr.Choices.Single(c => c.InstanceId == "openai-asr").Availability);
        var grants = rig.Config.State.Effective.Accounts.Single(a => a.Id == "openai").Grants;
        Assert.Contains(grants, g => g.Package == "app.susu.openai-asr" && g.Origin == "https://api.openai.com:443" && g.Use == "header:Authorization");
        Assert.Contains(grants, g => g.Package == "app.susu.openai");
        // Still not ready to record: the package is not installed until F12.
        Assert.Equal("not-installed", rig.Speech.Asr.ReasonKey);
    }

    [Fact]
    public void Shared_tencent_account_grants_tts_per_origin()
    {
        using var rig = new Rig();
        Assert.True(rig.Run(UiCommands.SecretWriteNew, new SecretWriteRequest("tencent-translate", "secretId", "AKIDtest", ConfirmGrants: true)).Ok);
        Assert.True(rig.Run(UiCommands.SecretWriteNew, new SecretWriteRequest("tencent-translate", "secretKey", "keytest", ConfirmGrants: true)).Ok);
        Assert.True(rig.Run(UiCommands.BindAccount, new BindAccountRequest("tencent-tts", "tencent-translate", ConfirmGrants: false)).Ok);
        var targets = rig.View.Services.Single(s => s.ServiceId == "tencent-tts/tts").CredentialTargets!;
        Assert.All(targets, t => Assert.Equal(("https://tts.tencentcloudapi.com:443", "signer:tencent-tc3", true, false), (t.Origin, t.Use, t.Saved, t.Granted)));
        Assert.True(rig.Run(UiCommands.BindAccount, new BindAccountRequest("tencent-tts", null, ConfirmGrants: true)).Ok);
        Assert.All(rig.View.Services.Single(s => s.ServiceId == "tencent-tts/tts").CredentialTargets!, t => Assert.True(t.Granted));
        // The translation grant was for tmt.tencentcloudapi.com only; the TTS grant does not widen it.
        var grants = rig.Config.State.Effective.Accounts.Single(a => a.Id == "tencent-translate").Grants;
        Assert.All(grants.Where(g => g.Package == "app.susu.tencent-translate"), g => Assert.Equal("https://tmt.tencentcloudapi.com:443", g.Origin));
    }

    [Fact]
    public void Selections_round_trip_through_settings_yaml_and_a_hand_edited_text_only_video_model_is_an_issue()
    {
        var settings = BuiltInCatalog.Defaults() with
        {
            Speech = new SpeechSettings(new("google-tts", ""), new("gemini-asr", "gemini-2.5-flash"), new("openai-asr", "whisper-1")),
        };
        string yaml = SettingsYaml.Write(settings);
        var (back, issues) = SettingsYaml.Read(yaml);
        Assert.Empty(issues);
        Assert.Equal(settings.Speech, back!.Speech);

        var (_, problems) = SettingsYaml.Read(yaml.Replace("instance: \"openai-asr\"\n    model: \"whisper-1\"", "instance: \"gemini-asr\"\n    model: \"gemini-2.5-flash\""));
        Assert.Contains(problems, p => p.Path == "speech.videoAsr" && p.Code == "timecodes");
    }

    [Fact]
    public void Catalog_metadata_covers_the_planned_speech_packages()
    {
        Assert.All(SpeechCatalog.All.Where(p => !p.Native), p =>
        {
            Assert.Equal(p.Capability == Capability.Tts, p.Installed); // F10.1 installed P-S01-P-S03; ASR comes in F12
            Assert.NotNull(BuiltInCatalog.Find(p.InstanceId));
            Assert.Equal(BuiltInCatalog.Find(p.InstanceId)!.Secrets, p.SecretNames);
            Assert.Contains(p.Capability, BuiltInCatalog.Find(p.InstanceId)!.Capabilities);
        });
        Assert.True(SpeechCatalog.Find("openai-asr")!.Timecodes);
        Assert.False(SpeechCatalog.Find("gemini-asr")!.Timecodes);
        Assert.Null(SpeechCatalog.Check(SpeechSlot.Asr, new("gemini-asr", "gemini-2.5-flash")));
        Assert.Equal(SpeechCatalog.NeedsTimecodes, SpeechCatalog.Check(SpeechSlot.VideoAsr, new("gemini-asr", "gemini-2.5-flash")));
    }
}
