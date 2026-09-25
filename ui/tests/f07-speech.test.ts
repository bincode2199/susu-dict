// F07.4: SetSpeech/SetSpeechB - three independent selections (A02), capability-filtered choices, the video timecode
// rule for text-only ASR (A03), shared-account grants in the speech service rows, and no capture entry point.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import type { CommandResult, ServiceView, SettingsView, SpeechSlotView, SpeechView } from '@protocol/ui';
import SpeechSettings from '../src/components/SpeechSettings.vue';
import SettingsWindow from '../src/windows/SettingsWindow.vue';
import { createStore } from '../src/bridge/store';
import { setLocale, t } from '../src/locales/i18n';

setLocale('zh-Hans');
afterEach(() => { document.body.innerHTML = ''; });

type Call = { name: string; payload?: any };
function fakeBridge(answer: (name: string, payload?: any) => CommandResult = () => ({ ok: true })) {
  const calls: Call[] = [];
  return { calls, command: vi.fn(async (name: string, payload?: object) => { calls.push({ name, payload }); return answer(name, payload); }) };
}

const TTS = [['native-sapi', true, true, 'F10.1'], ['microsoft-tts', false, false, 'F10.1 P-S01'], ['google-tts', false, false, 'F10.1 P-S02'], ['tencent-tts', false, false, 'F10.1 P-S03']] as const;
const ASR: [string, string, [string, boolean][]][] = [
  ['openai-asr', 'F12.2 P-R01', [['whisper-1', true], ['gpt-4o-transcribe', false], ['gpt-4o-mini-transcribe', false]]],
  ['gemini-asr', 'F12.2 P-R02', [['gemini-2.5-flash', false]]],
];
function slot(name: 'tts' | 'asr' | 'videoAsr', instance: string, model: string, reasonKey = 'not-installed', availability = 'MissingCredential'): SpeechSlotView {
  const video = name === 'videoAsr';
  const choices = name === 'tts'
    ? TTS.map(([id, native, installed, plan]) => ({ instanceId: id, native, installed, plan, timecodes: false, selectable: true, availability: native ? 'Ready' : availability, models: [] }))
    : ASR.map(([id, plan, models]) => {
      const timecodes = models.some(([, tc]) => tc);
      return { instanceId: id, native: false, installed: false, plan, timecodes, selectable: !video || timecodes, availability,
        models: models.map(([m, tc]) => ({ id: m, timecodes: tc, selectable: !video || tc })), reasonKey: video && !timecodes ? 'needs-timecodes' : undefined };
    });
  return { slot: name, instance, model, choices, ready: false, reasonKey };
}
const speech = (over: Partial<SpeechView> = {}): SpeechView => ({
  tts: slot('tts', 'native-sapi', '', 'not-built'), asr: slot('asr', 'openai-asr', 'whisper-1'), videoAsr: slot('videoAsr', 'openai-asr', 'whisper-1'), ...over,
});
const settings = (s: SpeechView = speech(), services: ServiceView[] = []): SettingsView => ({
  revision: 7, fileHash: 'h7', issues: [], hotkeys: [], accounts: [], services,
  general: { uiLanguage: 'zh-Hans', sourceLanguage: 'en', targetLanguage: 'zh-Hans', defaultExpandedCards: 2, allowClipboardBorrowing: false, closeAction: 'hide', launchAtStartup: false },
  network: { proxyMode: 'system', proxyHost: '', proxyPort: 0, proxyUsername: '', proxyPasswordSaved: false, aiTimeoutSeconds: 30 },
  speech: s,
});
const group = (wrapper: any, name: string) => wrapper.find(`[data-slot="${name}"]`);

describe('SetSpeech/SetSpeechB selections', () => {
  it('shows three groups, each listing only services with the needed capability', () => {
    const wrapper = mount(SpeechSettings, { props: { settings: settings(), bridge: fakeBridge() } });
    expect(wrapper.findAll('[data-slot]').map((g) => g.attributes('data-slot'))).toEqual(['tts', 'asr', 'videoAsr']);
    expect(group(wrapper, 'tts').findAll('[data-choice]').map((o: any) => o.attributes('data-choice'))).toEqual(['native-sapi', 'microsoft-tts', 'google-tts', 'tencent-tts']);
    expect(group(wrapper, 'asr').findAll('[data-choice]').map((o: any) => o.attributes('data-choice'))).toEqual(['openai-asr', 'gemini-asr']);
    expect(group(wrapper, 'tts').find('[data-model]').exists()).toBe(false); // TTS has no model choice; voices live in the service details
    expect((group(wrapper, 'asr').find('[data-model]').element as HTMLSelectElement).value).toBe('whisper-1');
    // Planned packages are labelled not installed; nothing is usable before F10/F12.
    expect(group(wrapper, 'asr').find('[data-choice="gemini-asr"]').text()).toContain(t('speech.choice.notInstalled'));
    expect(group(wrapper, 'tts').find('[data-status]').text()).toBe(t('speech.reason.not-built', { plan: 'F10.1' }));
    expect(group(wrapper, 'asr').find('[data-status]').text()).toBe(t('speech.reason.not-installed', { plan: 'F12.2 P-R01' }));
    expect(group(wrapper, 'tts').find('[data-account]').text()).toBe(t('speech.account.native'));
    expect(wrapper.text()).not.toMatch(/开始录音|开始朗读/);
  });

  it('A02: changing the recording service sends that slot only', async () => {
    const next = settings(speech({ asr: slot('asr', 'gemini-asr', 'gemini-2.5-flash') }));
    const bridge = fakeBridge(() => ({ ok: true, value: next }));
    const wrapper = mount(SpeechSettings, { props: { settings: settings(), bridge } });
    const select = group(wrapper, 'asr').find('[data-service]');
    (select.element as HTMLSelectElement).value = 'gemini-asr';
    await select.trigger('change');
    await flushPromises();
    expect(bridge.calls).toEqual([{ name: 'Settings.SelectSpeech', payload: { expectedRevision: 7, expectedFileHash: 'h7', slot: 'asr', instance: 'gemini-asr', model: 'gemini-2.5-flash' } }]);
    expect(wrapper.emitted('settings')?.[0]).toEqual([next]);

    const model = group(wrapper, 'videoAsr').find('[data-model]');
    (model.element as HTMLSelectElement).value = 'whisper-1';
    await model.trigger('change');
    await flushPromises();
    expect(bridge.calls[1].payload).toMatchObject({ slot: 'videoAsr', instance: 'openai-asr', model: 'whisper-1' });
  });

  it('A03: a text-only ASR is disabled for video with the timecode explanation, and only timecoded models are offered', () => {
    const wrapper = mount(SpeechSettings, { props: { settings: settings(speech({ asr: slot('asr', 'gemini-asr', 'gemini-2.5-flash') })), bridge: fakeBridge() } });
    const videoGemini = group(wrapper, 'videoAsr').find('[data-choice="gemini-asr"]');
    expect(videoGemini.attributes('disabled')).toBeDefined();
    expect(videoGemini.text()).toContain(t('speech.choice.needs-timecodes'));
    expect(group(wrapper, 'asr').find('[data-choice="gemini-asr"]').attributes('disabled')).toBeUndefined();
    expect(group(wrapper, 'videoAsr').find('[data-timecode-note]').text()).toBe(t('speech.videoAsrTimecodes'));
    const models = group(wrapper, 'videoAsr').findAll('[data-model-id]');
    expect(models.filter((o: any) => o.attributes('disabled') === undefined).map((o: any) => o.attributes('data-model-id'))).toEqual(['whisper-1']);
    expect(group(wrapper, 'asr').findAll('[data-model-id]').every((o: any) => o.attributes('disabled') === undefined)).toBe(true);
  });

  it('A03: with no timecoded service selected, video says it needs one; a refused save shows the reason', async () => {
    const bridge = fakeBridge(() => ({ ok: false, error: 'needs-timecodes' }));
    const wrapper = mount(SpeechSettings, { props: { settings: settings(speech({ videoAsr: slot('videoAsr', '', '', 'needs-timecodes') })), bridge } });
    expect(group(wrapper, 'videoAsr').find('[data-status]').text()).toBe(t('speech.reason.needs-timecodes'));
    const model = group(wrapper, 'asr').find('[data-model]');
    (model.element as HTMLSelectElement).value = 'gpt-4o-transcribe';
    await model.trigger('change');
    await flushPromises();
    expect(group(wrapper, 'asr').find('[role="alert"]').text()).toBe(t('speech.error.needs-timecodes'));
    expect(wrapper.emitted('settings')).toBeUndefined();
  });
});

describe('Speech page in the settings window', () => {
  const asrService = (granted: boolean): ServiceView => ({
    serviceId: 'openai-asr/asr', instanceId: 'openai-asr', capability: 'asr', page: 'speech', enabled: false, availability: 'Disabled', implemented: false,
    secretNames: ['apiKey'], accountId: 'openai-asr', credentialTargets: [{ secret: 'apiKey', origin: 'https://api.openai.com:443', use: 'header:Authorization', saved: false, granted }],
    order: -1, instanceRevision: 1,
  });

  it('shows the selections above the speech service rows; sharing the OpenAI account asks for the grant first', async () => {
    const { state } = createStore();
    state.window = { kind: 'Settings', uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features: [] };
    state.settings = { ...settings(speech(), [asrService(false)]), accounts: [{ id: 'openai', label: 'openai', secrets: [{ name: 'apiKey', saved: true }], usedBy: ['openai'] }] };
    const bridge = fakeBridge(() => ({ ok: true, value: state.settings }));
    const wrapper = mount(SettingsWindow, { props: { bridge: bridge as any, state } });
    await wrapper.findAll('.nav-item').find((n) => n.text() === t('nav.speech'))!.trigger('click');
    expect(wrapper.findAll('[data-slot]')).toHaveLength(3);
    await wrapper.find(`[aria-label="${t('services.details', { name: 'OpenAI 转写' })}"]`).trigger('click');
    // No validation button: the package is not installed, so there is nothing to call.
    expect(wrapper.text()).not.toContain(t('validate.run'));
    const account = wrapper.find('#openai-asr-account');
    (account.element as HTMLSelectElement).value = 'openai';
    await account.trigger('change');
    expect(bridge.calls.some((c) => c.name === 'Settings.BindAccount')).toBe(false);
    const confirm = wrapper.find('[role="alertdialog"]');
    expect(confirm.text()).toContain('https://api.openai.com:443');
    await confirm.find('.btn.primary').trigger('click');
    await flushPromises();
    expect(bridge.calls.find((c) => c.name === 'Settings.BindAccount')?.payload).toEqual({ instanceId: 'openai-asr', accountId: 'openai', confirmGrants: true });
  });
});
