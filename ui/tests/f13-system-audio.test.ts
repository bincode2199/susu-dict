// F13.2: the voice window in system-audio mode (same page, source systemAudio): its own title and wording, the "includes Su-Su's own
// sound" notice while recording and on the result, the loopback error codes, the interruption choices, and the tray entry marking.
// Fake bridge only: no output device on this test run.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import type { CommandResult, TrayView, UiSnapshot, VoiceView, WindowView } from '@protocol/ui';
import VoiceWindow from '../src/windows/VoiceWindow.vue';
import TrayMenu from '../src/windows/TrayMenu.vue';
import { createStore } from '../src/bridge/store';
import { setLocale, t } from '../src/locales/i18n';

setLocale('zh-Hans');

function fakeBridge(answer: (name: string, payload?: any) => CommandResult = () => ({ ok: true })) {
  const calls: { name: string; payload?: any }[] = [];
  return { calls, command: vi.fn(async (name: string, payload?: object) => { calls.push({ name, payload }); return answer(name, payload); }) };
}
const view = (kind: WindowView['kind']): WindowView => ({ kind, uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features: ['system-audio'] });
const sys = (over: Partial<VoiceView> = {}): VoiceView => ({
  id: 1, phase: 'idle', elapsedMs: 0, limitMs: 600000, level: 0, silent: false, translated: false, hotkey: 'Alt+B', canTranscribe: false, source: 'systemAudio', ownPlayback: true, ...over,
});
function mountVoice(snapshot: Omit<UiSnapshot, 'window'>) {
  const { state, inbound } = createStore();
  inbound.onSnapshot({ window: view('Voice'), ...snapshot });
  const bridge = fakeBridge();
  const wrapper = mount(VoiceWindow, { props: { bridge: bridge as any, state }, attachTo: document.body });
  return { state, inbound, bridge, wrapper };
}
afterEach(() => { document.body.innerHTML = ''; });

describe('System audio in the voice window', () => {
  it('idle: its own title and wording, the own-sound notice, start records', async () => {
    const { wrapper, bridge } = mountVoice({ voice: sys() });
    expect(wrapper.find('.titlebar .title').text()).toBe(`Su-Su · ${t('voice.sys.title')}`);
    expect(wrapper.find('[data-stage="idle"] p').text()).toBe(t('voice.sys.idle'));
    expect(wrapper.find('[data-own-playback]').text()).toBe(t('voice.sys.ownPlayback'));
    expect(wrapper.find('[data-status]').text()).toBe('Alt+B 打开 · Esc 关闭');
    await wrapper.find('[data-start]').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Audio.StartRecording');
  });

  it('the microphone window has neither the system title nor the notice', () => {
    const { wrapper } = mountVoice({ voice: sys({ source: 'microphone', ownPlayback: false, hotkey: 'Alt+V' }) });
    expect(wrapper.find('.titlebar .title').text()).toBe(`Su-Su · ${t('voice.title')}`);
    expect(wrapper.find('[data-own-playback]').exists()).toBe(false);
  });

  it('recording and the result keep the notice; silence says no system sound', () => {
    const rec = mountVoice({ voice: sys({ phase: 'recording', elapsedMs: 5000, level: 0.2 }) });
    expect(rec.wrapper.find('[data-own-playback]').exists()).toBe(true);
    expect(rec.wrapper.find('[data-meter]').exists()).toBe(true);
    const silent = mountVoice({ voice: sys({ phase: 'recording', elapsedMs: 5000, silent: true }) });
    expect(silent.wrapper.find('[data-silent]').text()).toBe(t('voice.sys.silent'));
    expect(silent.wrapper.find('[data-status]').text()).toBe(t('voice.sys.status.silent'));
    const done = mountVoice({ voice: sys({ phase: 'transcribed', text: 'hello', translated: true, serviceId: 'openai-asr', transcribeMs: 900 }) });
    expect(done.wrapper.find('[data-own-playback]').exists()).toBe(true);
  });

  it.each(['loopback.noDevice', 'loopback.denied', 'loopback.failed'])('start error %s has its own text and offers record again', async (code) => {
    const { wrapper, bridge } = mountVoice({ voice: sys({ phase: 'error', errorCode: code }) });
    expect(wrapper.find('[data-status]').text()).toBe(t(`voice.error.${code}`));
    expect(wrapper.text()).toContain(t(`voice.error.${code}`));
    expect(wrapper.text()).not.toContain('麦克风');
    await wrapper.find('[data-rerecord]').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Audio.StartRecording');
  });

  it('output switch: says the output device changed, keeps the part, transcribe recorded / record again', async () => {
    const { wrapper, bridge } = mountVoice({ voice: sys({ phase: 'interrupted', reason: 'defaultChanged', canTranscribe: true, elapsedMs: 12_000 }) });
    expect(wrapper.text()).toContain(t('voice.sys.reason.defaultChanged'));
    expect(wrapper.text()).not.toContain('麦克风');
    expect(wrapper.text()).toContain(t('voice.keptTime', { time: '0:12' }));
    await wrapper.find('[data-transcribe-recorded]').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Audio.TranscribeRecorded');
    await wrapper.find('[data-rerecord]').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Audio.StartRecording');
  });

  it('sleep falls back to the shared wording (no microphone in it)', () => {
    const { wrapper } = mountVoice({ voice: sys({ phase: 'interrupted', reason: 'sleep', canTranscribe: false }) });
    expect(wrapper.text()).toContain(t('voice.reason.sleep'));
  });

  it('English wording exists for every system-audio key', () => {
    setLocale('en');
    const { wrapper } = mountVoice({ voice: sys() });
    expect(wrapper.find('.titlebar .title').text()).toBe('Su-Su · System audio translation');
    expect(wrapper.find('[data-own-playback]').text()).toContain('own read-aloud');
    setLocale('zh-Hans');
  });
});

describe('Tray entry', () => {
  it('a recording system-audio entry is marked and the voice entry is not', () => {
    const tray: TrayView = { items: [
      { id: 'voice', chord: 'Alt+V', enabled: true, separatorBefore: false },
      { id: 'system-audio', chord: 'Alt+B', enabled: true, separatorBefore: false, status: 'recording' },
    ] };
    const { state, inbound } = createStore();
    inbound.onSnapshot({ window: view('Tray'), tray });
    const wrapper = mount(TrayMenu, { props: { bridge: fakeBridge() as any, state }, attachTo: document.body });
    expect(wrapper.findAll('[data-status]')).toHaveLength(1);
    expect(wrapper.find('[data-status]').text()).toContain(t('tray.status.recording'));
    expect(wrapper.text()).toContain(t('tray.system-audio'));
  });
});
