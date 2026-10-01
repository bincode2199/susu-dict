// F12.3: the voice window (microphone ready, recording with level and limit, paused, transcribing, transcript, interrupted with
// "transcribe recorded / re-record", start and save errors, service outcomes), the tray recording state, and SetSpeechB with a text-only
// ASR (A03: voice available, video greyed with the timecode reason). Fake bridge only: no microphone on this machine.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import type { CardSnapshot, CommandResult, ServiceView, SettingsView, SpeechSlotView, SpeechView, TranslationSnapshot, UiSnapshot, VoiceView, WindowView } from '@protocol/ui';
import VoiceWindow from '../src/windows/VoiceWindow.vue';
import TrayMenu from '../src/windows/TrayMenu.vue';
import SpeechSettings from '../src/components/SpeechSettings.vue';
import { createStore } from '../src/bridge/store';
import { setLocale, t } from '../src/locales/i18n';

setLocale('zh-Hans');

function fakeBridge(answer: (name: string, payload?: any) => CommandResult = () => ({ ok: true })) {
  const calls: { name: string; payload?: any }[] = [];
  return { calls, command: vi.fn(async (name: string, payload?: object) => { calls.push({ name, payload }); return answer(name, payload); }) };
}
const view = (kind: WindowView['kind']): WindowView => ({ kind, uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features: ['voice'] });
const card = (serviceId: string, state: CardSnapshot['state'], text = ''): CardSnapshot => ({ serviceId, displayName: serviceId, state, collapsed: false, text });
const translation = (sourceText: string, cards: CardSnapshot[], generation = 1): TranslationSnapshot => ({ revision: 3, generation, sourceText, from: 'en', to: 'zh-Hans', cards });
const voice = (over: Partial<VoiceView> = {}): VoiceView => ({
  id: 1, phase: 'idle', elapsedMs: 0, limitMs: 600000, level: 0, silent: false, translated: false, hotkey: 'Alt+V', canTranscribe: false, ...over,
});

function mountVoice(snapshot: Omit<UiSnapshot, 'window'>) {
  const { state, inbound } = createStore();
  inbound.onSnapshot({ window: view('Voice'), ...snapshot });
  const bridge = fakeBridge();
  const wrapper = mount(VoiceWindow, { props: { bridge: bridge as any, state }, attachTo: document.body });
  return { state, inbound, bridge, wrapper };
}

afterEach(() => { document.body.innerHTML = ''; });

describe('Voice window', () => {
  it('idle: a microphone button starts recording; nothing is transcribed live and no text box yet', async () => {
    const { wrapper, bridge } = mountVoice({ voice: voice() });
    expect(wrapper.find('.titlebar .title').text()).toBe(`Su-Su · ${t('voice.title')}`);
    expect(wrapper.find('[data-stage="idle"] p').text()).toBe(t('voice.idle'));
    expect(wrapper.find('textarea').exists()).toBe(false);
    expect(wrapper.find('[data-status]').text()).toBe('Alt+V 打开 · Esc 关闭');
    await wrapper.find('[data-start]').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Audio.StartRecording');
  });

  it('recording: elapsed time against the 10-minute limit and the level meter, no live-transcription row (VO1/VO2)', async () => {
    const { wrapper, bridge, inbound } = mountVoice({ voice: voice({ phase: 'recording', elapsedMs: 65_000, level: 0.42 }) });
    expect(wrapper.find('[data-elapsed]').text()).toBe('1:05 / 10:00');
    expect(wrapper.find('[data-meter]').attributes('aria-valuenow')).toBe('42');
    expect((wrapper.find('[data-meter] .fill').element as HTMLElement).style.width).toBe('42%');
    expect(wrapper.find('[data-limit]').text()).toBe(t('voice.limitHint', { remaining: '8:55', n: 10 }));
    expect(wrapper.find('[data-status]').text()).toBe(t('voice.status.recording', { remaining: '8:55', limit: 10 }));
    expect(wrapper.find('textarea').exists()).toBe(false);
    expect(wrapper.text()).not.toContain('实时转写');
    inbound.onEvent('voice', voice({ phase: 'recording', elapsedMs: 66_000, level: 0.9 }));
    await flushPromises();
    expect(wrapper.find('[data-elapsed]').text()).toBe('1:06 / 10:00');
    expect(wrapper.find('[data-meter]').attributes('aria-valuenow')).toBe('90');
    await wrapper.find('[data-pause]').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Audio.PauseRecording');
    await wrapper.find('[data-stop]').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Audio.StopRecording');
    await wrapper.find('[data-cancel]').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Audio.CancelRecording');
  });

  it('no voice: the notice and the status line say nothing was heard', () => {
    const { wrapper } = mountVoice({ voice: voice({ phase: 'recording', elapsedMs: 4000, silent: true }) });
    expect(wrapper.find('[data-silent]').text()).toBe(t('voice.silent'));
    expect(wrapper.find('[data-status]').text()).toBe(t('voice.status.silent'));
  });

  it('paused: the meter is empty, the button resumes, the pause does not count', () => {
    const { wrapper } = mountVoice({ voice: voice({ phase: 'paused', elapsedMs: 12_000, level: 0.7 }) });
    expect(wrapper.find('[data-elapsed]').text()).toBe('0:12 / 10:00');
    expect(wrapper.find('[data-meter]').attributes('aria-valuenow')).toBe('0');
    expect(wrapper.find('[data-paused]').text()).toBe(t('voice.pausedHint'));
    expect(wrapper.find('[data-pause]').text()).toBe(t('voice.resume'));
    expect(wrapper.find('[data-status]').text()).toBe(t('voice.status.paused', { elapsed: '0:12' }));
  });

  it('transcribing: two skeleton lines and the service, no spinner, cancel offered; the limit note when the recording was cut', async () => {
    const { wrapper, bridge } = mountVoice({ voice: voice({ phase: 'transcribing', elapsedMs: 600_000, serviceId: 'openai-asr', notice: 'limit', chunk: 1, chunks: 3 }) });
    expect(wrapper.findAll('.skeleton .line')).toHaveLength(2);
    expect(wrapper.find('.label-row .tag').text()).toBe(`${t('voice.status.transcribing')} · OpenAI 转写`);
    expect(wrapper.find('[data-limit-note]').text()).toBe(t('voice.status.limit', { n: 10 }));
    expect(wrapper.find('[data-status]').text()).toBe(`${t('voice.status.transcribing')} · OpenAI 转写 · 2/3`);
    expect(wrapper.find('.source').attributes('aria-busy')).toBe('true');
    expect(wrapper.find('textarea').exists()).toBe(false);
    await wrapper.find('[data-cancel]').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Audio.CancelRecording');
  });

  it('transcript: editable text, cards below, copy, record again and translate (Ctrl+Enter, not while composing)', async () => {
    const { wrapper, bridge } = mountVoice({
      voice: voice({ phase: 'transcribed', serviceId: 'openai-asr', text: 'Hello from the microphone', translated: true, transcribeMs: 1200 }),
      translation: translation('Hello from the microphone', [card('deepl', 'Ready', '来自麦克风的问候'), card('claude', 'CollapsedIdle')]),
    });
    await flushPromises();
    expect((wrapper.find('#voice-source-text').element as HTMLTextAreaElement).value).toBe('Hello from the microphone');
    expect(wrapper.find('.label-row').text()).toContain(t('voice.editHint'));
    expect(wrapper.find('.meta').text()).toBe(t('voice.meta', { service: 'OpenAI 转写', seconds: '1.2', n: 25 }));
    expect(wrapper.text()).toContain('来自麦克风的问候');
    expect(wrapper.find('[data-status]').text()).toBe('Alt+V 重新录音 · Esc 关闭');
    await wrapper.find(`.foot button[aria-label="${t('voice.copy')}"]`).trigger('click');
    expect(bridge.calls.at(-1)).toEqual({ name: 'Window.CopyText', payload: { text: 'Hello from the microphone' } });
    await wrapper.find('.foot [data-rerecord]').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Audio.StartRecording');
    const area = wrapper.find('#voice-source-text');
    await area.setValue('Hello again');
    await wrapper.find('button.translate').trigger('click');
    expect(bridge.calls.at(-1)).toEqual({ name: 'Translation.SubmitText', payload: { text: 'Hello again' } });
    const count = bridge.calls.length;
    await area.trigger('keydown', { key: 'Enter', ctrlKey: true, isComposing: true });
    expect(bridge.calls.length).toBe(count);
    await area.trigger('keydown', { key: 'Enter', ctrlKey: true });
    expect(bridge.calls.at(-1)!.name).toBe('Translation.SubmitText');
  });

  it('a new recording clears the old transcript; the new one replaces it', async () => {
    const { wrapper, inbound } = mountVoice({ voice: voice({ phase: 'transcribed', text: 'first' }) });
    inbound.onEvent('voice', voice({ id: 2, phase: 'recording' }));
    await flushPromises();
    expect(wrapper.find('textarea').exists()).toBe(false);
    inbound.onEvent('voice', voice({ id: 2, phase: 'transcribed', text: 'second' }));
    await flushPromises();
    expect((wrapper.find('textarea').element as HTMLTextAreaElement).value).toBe('second');
  });

  it.each([
    ['deviceRemoved', 'voice.reason.deviceRemoved'],
    ['defaultChanged', 'voice.reason.defaultChanged'],
    ['sleep', 'voice.reason.sleep'],
  ])('interrupted by %s: says why in the status line and offers transcribe recorded / record again (REC03)', async (reason, key) => {
    const { wrapper, bridge } = mountVoice({ voice: voice({ phase: 'interrupted', elapsedMs: 83_000, reason, canTranscribe: true }) });
    expect(wrapper.find('[data-status]').text()).toBe(t(key));
    expect(wrapper.find('[data-stage="interrupted"] .failure-title').text()).toBe(t('voice.interrupted'));
    expect(wrapper.find('[data-stage="interrupted"]').text()).toContain(t('voice.keptTime', { time: '1:23' }));
    await wrapper.find('[data-transcribe-recorded]').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Audio.TranscribeRecorded');
    await wrapper.find('[data-rerecord]').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Audio.StartRecording');
  });

  it('interrupted with nothing kept: only record again', () => {
    const { wrapper } = mountVoice({ voice: voice({ phase: 'interrupted', reason: 'sleep', canTranscribe: false }) });
    expect(wrapper.find('[data-transcribe-recorded]').exists()).toBe(false);
    expect(wrapper.find('[data-rerecord]').exists()).toBe(true);
    expect(wrapper.find('[data-stage="interrupted"]').text()).toContain(t('voice.nothingKept'));
  });

  it.each(['mic.denied', 'mic.noDevice', 'mic.failed', 'mic.busy', 'record.diskFull', 'record.writeFailed'])('start or save error %s has its own text and a record-again button', async (code) => {
    const { wrapper, bridge } = mountVoice({ voice: voice({ phase: 'error', errorCode: code }) });
    expect(wrapper.find('[data-stage="error"] .failure-title').text()).toBe(t('voice.errorTitle'));
    expect(wrapper.find('[data-stage="error"]').text()).toContain(t(`voice.error.${code}`));
    expect(wrapper.find('[data-status]').text()).toBe(t(`voice.error.${code}`));
    await wrapper.find('[data-rerecord]').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Audio.StartRecording');
  });

  it('no speech, a failed service and no service: each says so; auth and quota link to settings, no service has no record button', async () => {
    const none = mountVoice({ voice: voice({ phase: 'noSpeech' }) });
    expect(none.wrapper.find('.failure-title').text()).toBe(t('voice.noSpeech'));
    expect(none.wrapper.find('[data-rerecord]').exists()).toBe(true);
    const failed = mountVoice({ voice: voice({ phase: 'failed', errorKind: 'auth', serviceId: 'openai-asr' }) });
    expect(failed.wrapper.find('.failure').text()).toContain(t('error.auth'));
    await failed.wrapper.find('.failure .link').trigger('click');
    expect(failed.bridge.calls.at(-1)!.name).toBe('Window.OpenSettings');
    const network = mountVoice({ voice: voice({ phase: 'failed', errorKind: 'network' }) });
    expect(network.wrapper.find('.failure .link').exists()).toBe(false);
    const unavailable = mountVoice({ voice: voice({ phase: 'unavailable' }) });
    expect(unavailable.wrapper.find('.failure-title').text()).toBe(t('voice.unavailable'));
    expect(unavailable.wrapper.find('[data-rerecord]').exists()).toBe(false);
    expect(unavailable.wrapper.find('.failure .link').exists()).toBe(true);
  });

  it('title bar: minimize keeps the task, close cancels it', async () => {
    const { wrapper, bridge } = mountVoice({ voice: voice({ phase: 'recording' }) });
    await wrapper.find(`button[aria-label="${t('window.minimize')}"]`).trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Window.Minimize');
    await wrapper.find(`button[aria-label="${t('window.close')}"]`).trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Window.Close');
  });

  it('only a view from the host is shown: audio never reaches the page, no file path appears anywhere', () => {
    const { wrapper } = mountVoice({ voice: voice({ phase: 'transcribed', text: 'ok', serviceId: 'openai-asr' }) });
    expect(wrapper.html()).not.toMatch(/\.wav|file:|<audio|<video|blob:/i);
  });
});

describe('Tray recording state', () => {
  it('a minimized recording is marked on the Voice item (and stays clickable)', async () => {
    const { state, inbound } = createStore();
    const items = ['input-translation', 'clipboard', 'ocr', 'voice', 'system-audio', 'transcription', 'settings', 'check-update', 'exit'].map((id) => ({
      id, chord: id === 'voice' ? 'Alt+V' : '', enabled: id !== 'transcription' && id !== 'system-audio', reasonKey: id === 'transcription' ? 'feature.noService.videoAsr' : undefined,
      separatorBefore: id === 'settings', status: id === 'voice' ? 'recording' : undefined,
    }));
    inbound.onSnapshot({ window: view('Tray'), tray: { items } });
    const bridge = fakeBridge();
    const wrapper = mount(TrayMenu, { props: { bridge: bridge as any, state }, attachTo: document.body });
    const voiceItem = wrapper.findAll('button.item').find((b) => b.text().includes(t('tray.voice')))!;
    expect(voiceItem.find('.status').text()).toBe(t('tray.status.recording'));
    expect(voiceItem.find('.shortcut').exists()).toBe(false);
    expect(voiceItem.attributes('disabled')).toBeUndefined();
    await voiceItem.trigger('click');
    expect(bridge.calls.at(-1)).toEqual({ name: 'Tray.Open', payload: { id: 'voice' } });
    // The greyed video entry says it needs a service with timecodes.
    const video = wrapper.findAll('button.item').find((b) => b.text().includes(t('tray.transcription')))!;
    expect(video.attributes('disabled')).toBeDefined();
    expect(video.find('.shortcut').text()).toBe(t('feature.noService.videoAsr'));
    expect(t('feature.noService.videoAsr')).toBe('需配置支持时间码的转写服务');
  });
});

describe('SetSpeechB with a text-only ASR (A03)', () => {
  const slot = (name: 'asr' | 'videoAsr', instance: string, model: string, ready: boolean, reasonKey?: string): SpeechSlotView => {
    const video = name === 'videoAsr';
    const choice = (id: string, models: [string, boolean][]) => {
      const timecodes = models.some(([, tc]) => tc);
      return { instanceId: id, native: false, installed: true, plan: 'F12.2', timecodes, selectable: !video || timecodes, availability: 'Ready',
        models: models.map(([m, tc]) => ({ id: m, timecodes: tc, selectable: !video || tc })), reasonKey: video && !timecodes ? 'needs-timecodes' : undefined };
    };
    return { slot: name, instance, model, ready, reasonKey, choices: [choice('openai-asr', [['whisper-1', true]]), choice('gemini-asr', [['gemini-2.5-flash', false]])] };
  };
  const ttsSlot: SpeechSlotView = { slot: 'tts', instance: 'native-sapi', model: '', choices: [], ready: true };
  const settings = (s: SpeechView): SettingsView => ({
    revision: 7, fileHash: 'h7', issues: [], hotkeys: [], accounts: [], services: [] as ServiceView[],
    general: { uiLanguage: 'zh-Hans', sourceLanguage: 'en', targetLanguage: 'zh-Hans', defaultExpandedCards: 2, allowClipboardBorrowing: false, closeAction: 'hide', launchAtStartup: false },
    network: { proxyMode: 'system', proxyHost: '', proxyPort: 0, proxyUsername: '', proxyPasswordSaved: false, aiTimeoutSeconds: 30 },
    speech: s,
  });

  it('voice/audio says it can be used with Gemini; video has no timecoded service and says so; video transcription is not built yet when one is chosen', () => {
    const gemini = mount(SpeechSettings, {
      props: { settings: settings({ tts: ttsSlot, asr: slot('asr', 'gemini-asr', 'gemini-2.5-flash', true), videoAsr: slot('videoAsr', '', '', false, 'needs-timecodes') }), bridge: fakeBridge() },
    });
    const asr = gemini.find('[data-slot="asr"]');
    const video = gemini.find('[data-slot="videoAsr"]');
    expect(asr.find('[data-status]').text()).toBe(t('speech.reason.ready'));
    expect((asr.find('[data-model]').element as HTMLSelectElement).value).toBe('gemini-2.5-flash');
    expect(video.find('[data-status]').text()).toBe(t('speech.reason.needs-timecodes'));
    expect(video.find('[data-choice="gemini-asr"]').attributes('disabled')).toBeDefined();
    expect(video.find('[data-timecode-note]').text()).toBe(t('speech.videoAsrTimecodes'));
    const chosen = mount(SpeechSettings, {
      props: { settings: settings({ tts: ttsSlot, asr: slot('asr', 'gemini-asr', 'gemini-2.5-flash', true), videoAsr: slot('videoAsr', 'openai-asr', 'whisper-1', false, 'not-built') }), bridge: fakeBridge() },
    });
    expect(chosen.find('[data-slot="videoAsr"] [data-status]').text()).toBe(t('speech.reason.videoNotBuilt'));
    expect(chosen.find('[data-slot="asr"] [data-status]').text()).toBe(t('speech.reason.ready'));
  });
});
