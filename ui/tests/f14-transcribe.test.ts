// F14.4: the Transcribe window (no file, picked, unusable file, upload confirmation, stage progress with incremental cues, paused,
// quota stop with a service switch, failed, done with the export buttons, drag target) and the SetSpeechB video translation field.
// Fake bridge only: no desktop, no media and no vendor on this machine; the native drop and the save dialog are host ports.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import type { CommandResult, SettingsView, SpeechSlotView, SpeechView, TranscribeView, UiSnapshot, WindowView } from '@protocol/ui';
import TranscribeWindow from '../src/windows/TranscribeWindow.vue';
import SpeechSettings from '../src/components/SpeechSettings.vue';
import { createStore } from '../src/bridge/store';
import { setLocale, t } from '../src/locales/i18n';

setLocale('zh-Hans');

function fakeBridge(answer: (name: string, payload?: any) => CommandResult = () => ({ ok: true })) {
  const calls: { name: string; payload?: any }[] = [];
  return { calls, command: vi.fn(async (name: string, payload?: object) => { calls.push({ name, payload }); return answer(name, payload); }) };
}
const window_ = (): WindowView => ({ kind: 'Transcribe', uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features: ['transcription'] });
const view = (over: Partial<TranscribeView> = {}): TranscribeView => ({
  id: 1, phase: 'idle', jobId: undefined, fileName: undefined, durationMs: undefined, hasVideo: false, streams: [], errorCode: undefined, errorKind: undefined,
  stage: 'none', slicesDone: 0, slicesTotal: 0, cueCount: 0, translated: 0, failedCues: 0, asrService: undefined, translationService: undefined, quotaSide: undefined,
  upload: undefined, choices: [], cues: [], export: undefined, hotkey: 'Alt+T', ...over,
});
const stream = { codec: 'aac', sampleRate: 44100, channels: 2, decodable: true, selected: true };
const picked = (over: Partial<TranscribeView> = {}) => view({ phase: 'picked', fileName: 'movie.mp4', durationMs: 2_520_000, hasVideo: true, streams: [stream], ...over });
const job = (phase: string, over: Partial<TranscribeView> = {}) => picked({ phase, jobId: 'job-1', stage: 'asr', slicesDone: 2, slicesTotal: 9, cueCount: 2, translated: 1, ...over });
const cue = (id: string, start: number, end: number, original: string, translation?: string, translationError?: string) => ({ id, start, end, original, translation, translationError });

function mountWindow(transcribe: TranscribeView) {
  const { state, inbound } = createStore();
  inbound.onSnapshot({ window: window_(), transcribe } as UiSnapshot);
  const bridge = fakeBridge();
  const wrapper = mount(TranscribeWindow, { props: { bridge: bridge as any, state }, attachTo: document.body });
  return { state, inbound, bridge, wrapper };
}

afterEach(() => { document.body.innerHTML = ''; });

describe('Transcribe window: file', () => {
  it('idle: choose a file; title and hotkey hint; drag target explains drag-in', async () => {
    const { wrapper, bridge } = mountWindow(view());
    expect(wrapper.find('.titlebar .title').text()).toBe(`Su-Su · ${t('transcribe.title')}`);
    expect(wrapper.find('[data-stage="idle"] p').text()).toBe(t('transcribe.idleHint'));
    expect(wrapper.find('[data-status]').text()).toBe('Alt+T 打开 · Esc 关闭');
    await wrapper.find('[data-pick]').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Transcription.PickMedia');
  });

  it('picked: name, length, kind and audio tracks are listed; start sends the command', async () => {
    const { wrapper, bridge } = mountWindow(picked());
    expect(wrapper.find('[data-file-name]').text()).toBe('movie.mp4');
    expect(wrapper.find('[data-duration]').text()).toBe(`42:00 · ${t('transcribe.kind.video')}`);
    expect(wrapper.find('[data-stream]').text()).toContain('aac · 44100 Hz · 2 声道');
    expect(wrapper.find('[data-stream]').text()).toContain(t('transcribe.stream.selected'));
    await wrapper.find('[data-start]').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Transcription.Start');
  });

  it('unsupported encoding: the error is explained, the undecodable track is marked, and there is no start button', () => {
    const { wrapper } = mountWindow(view({ phase: 'pickError', fileName: 'film.mkv', durationMs: 60_000, streams: [{ ...stream, codec: 'ac3', decodable: false, selected: false }], errorCode: 'media.unsupportedEncoding' }));
    expect(wrapper.find('[data-error]').text()).toContain(t('transcribe.error.media.unsupportedEncoding'));
    expect(wrapper.find('[data-stream]').text()).toContain(t('transcribe.stream.unsupported'));
    expect(wrapper.find('[data-start]').exists()).toBe(false);
    expect(wrapper.find('[data-pick]').exists()).toBe(true);
    expect(wrapper.find('[data-status]').text()).toBe(t('transcribe.error.media.unsupportedEncoding'));
  });

  it('a refusal at start (no timecodes) shows its reason with a settings link and keeps the file', async () => {
    const { wrapper, bridge } = mountWindow(picked({ errorCode: 'asr.noTimecodes' }));
    expect(wrapper.find('[data-error]').text()).toContain(t('transcribe.error.asr.noTimecodes'));
    await wrapper.find('[data-error] .link').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Window.OpenSettings');
    expect(wrapper.find('[data-start]').exists()).toBe(true);
  });

  it('dropping a file shows the target but sends nothing: the host receives the path, the page never does', async () => {
    const { wrapper, bridge } = mountWindow(view());
    await wrapper.find('.window').trigger('dragover');
    expect(wrapper.find('.window').classes()).toContain('dragging');
    await wrapper.find('.window').trigger('drop');
    expect(wrapper.find('.window').classes()).not.toContain('dragging');
    expect(bridge.calls).toEqual([]);
  });
});

describe('Transcribe window: confirmation', () => {
  const upload = {
    fileName: 'movie.mp4', durationMs: 2_520_000, hasVideo: true, asrService: 'openai-asr', asrModel: 'whisper-1', uploadBytesEstimate: 80_640_000,
    chunkSecondsLimit: 300, chunkBytesLimit: 24_000_000, translationService: 'mymemory', translationServiceName: 'MyMemory', translationMaxInput: 500, translationMaxItems: 1,
    quotaNoteKey: 'video.quota.sharedDaily', estimatedCharacters: undefined, estimatedPrice: undefined,
  };

  it('shows both services, the limits and the upload size; unknown characters and price are said to be unknown, never invented', async () => {
    const { wrapper, bridge } = mountWindow(job('confirm', { stage: 'probe', upload }));
    const dialog = wrapper.find('[role="dialog"]');
    expect(dialog.exists()).toBe(true);
    expect(dialog.find('[data-asr]').text()).toContain('whisper-1');
    expect(dialog.find('[data-translation]').text()).toBe('MyMemory');
    expect(dialog.find('[data-size]').text()).toContain('76.9 MB');
    expect(dialog.find('[data-chunk]').text()).toContain('300');
    expect(dialog.find('[data-limit]').text()).toContain('500');
    expect(dialog.find('[data-chars]').text()).toBe(t('transcribe.confirm.charsUnknown'));
    expect(dialog.find('[data-price]').text()).toBe(t('transcribe.confirm.priceUnknown'));
    expect(dialog.find('[data-quota-note]').text()).toBe(t('video.quota.sharedDaily'));
    await dialog.find('[data-confirm-yes]').trigger('click');
    expect(bridge.calls.at(-1)).toEqual({ name: 'Transcription.Confirm', payload: { accept: true } });
    await dialog.find('[data-confirm-no]').trigger('click');
    expect(bridge.calls.at(-1)).toEqual({ name: 'Transcription.Confirm', payload: { accept: false } });
  });
});

describe('Transcribe window: progress', () => {
  it('stages, slice progress and counts follow the host; cues arrive one by one and a view event keeps them', async () => {
    const { wrapper, inbound, state } = mountWindow(job('running', { cueCount: 0, translated: 0 }));
    expect(wrapper.find('[data-stage-item="probe"]').classes()).toContain('past');
    expect(wrapper.find('[data-stage-item="asr"]').classes()).toContain('now');
    expect(wrapper.find('[data-progress]').text()).toBe('音频片段 2/9');
    expect(wrapper.find('[data-empty]').exists()).toBe(true);
    expect(wrapper.find('[data-pause]').exists()).toBe(true);

    inbound.onEvent('transcribe.cue', cue('c1', 0.5, 2, 'Hello there'));
    inbound.onEvent('transcribe.cue', cue('c2', 3, 65, 'Second line'));
    await wrapper.vm.$nextTick();
    expect(wrapper.findAll('[data-cue]').map((c) => c.attributes('data-cue'))).toEqual(['c1', 'c2']);
    expect(wrapper.find('[data-cue="c2"] .time').text()).toBe('00:03 → 01:05');
    expect(wrapper.find('[data-cue="c1"]').text()).toContain(t('transcribe.cue.pending'));

    // A translated update replaces the cue in place; a view event of the same job with no cues keeps the list.
    inbound.onEvent('transcribe.cue', cue('c1', 0.5, 2, 'Hello there', '你好'));
    inbound.onEvent('transcribe', job('running', { stage: 'translate', cueCount: 2, translated: 1, cues: [] }));
    await wrapper.vm.$nextTick();
    expect(state.transcribe!.cues).toHaveLength(2);
    expect(wrapper.find('[data-cue="c1"]').text()).toContain('你好');
    expect(wrapper.find('[data-stage-item="translate"]').classes()).toContain('now');
    expect(wrapper.find('[data-counts]').text()).toBe(t('transcribe.counts', { cues: 2, translated: 1 }));

    // A cue that failed on its own is marked; the list goes on.
    inbound.onEvent('transcribe.cue', cue('c2', 3, 65, 'Second line', undefined, 'bad_response'));
    await wrapper.vm.$nextTick();
    expect(wrapper.find('[data-cue="c2"] [data-cue-failed]').text()).toBe(t('transcribe.cue.failed'));

    // A new job starts with an empty list.
    inbound.onEvent('transcribe', job('running', { jobId: 'job-2', cues: [], cueCount: 0 }));
    await wrapper.vm.$nextTick();
    expect(state.transcribe!.cues).toEqual([]);
  });

  it('pause and cancel send their commands; paused offers resume', async () => {
    const { wrapper, bridge, inbound } = mountWindow(job('running'));
    await wrapper.find('[data-pause]').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Transcription.Pause');
    await wrapper.find('[data-cancel]').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Transcription.Cancel');
    inbound.onEvent('transcribe', job('paused', { cues: [] }));
    await wrapper.vm.$nextTick();
    expect(wrapper.find('[data-status]').text()).toBe(t('transcribe.status.paused'));
    await wrapper.find('[data-resume]').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Transcription.Resume');
  });
});

describe('Transcribe window: quota, failure, done', () => {
  it('translation quota keeps the recognized text, offers another service, and switching resumes', async () => {
    const choices = [{ kind: 'translation', id: 'mymemory', model: '', current: true }, { kind: 'translation', id: 'deepl', model: '', current: false }];
    const { wrapper, bridge } = mountWindow(job('quota', { quotaSide: 'translation', choices, cues: [cue('c1', 1, 2, 'Hello')] }));
    const panel = wrapper.find('[data-quota]');
    expect(panel.attributes('data-side')).toBe('translation');
    expect(panel.text()).toContain(t('transcribe.quota.translation'));
    expect(panel.text()).toContain(t('transcribe.quota.keep'));
    expect(panel.findAll('[data-choice]')).toHaveLength(1); // the exhausted service itself is not offered
    expect(wrapper.find('[data-cue="c1"]').exists()).toBe(true);
    await panel.find('[data-choice] button').trigger('click');
    await new Promise((r) => setTimeout(r, 0));
    expect(bridge.calls.slice(-2).map((c) => c.name)).toEqual(['Transcription.ChangeTranslator', 'Transcription.Resume']);
    expect(bridge.calls.at(-2)!.payload).toEqual({ side: 'translation', id: 'deepl', model: '' });
    expect(wrapper.find('[data-export-panel]').exists()).toBe(true); // what exists can be exported while stopped
  });

  it('asr quota offers ASR services with their model; with nothing else to switch to it says so', async () => {
    const asr = mountWindow(job('quota', { quotaSide: 'asr', choices: [{ kind: 'asr', id: 'openai-asr', model: 'whisper-1', current: true }, { kind: 'asr', id: 'tencent-asr', model: 'x', current: false }] }));
    expect(asr.wrapper.find('[data-choice]').text()).toContain('x');
    await asr.wrapper.find('[data-choice] button').trigger('click');
    await new Promise((r) => setTimeout(r, 0));
    expect(asr.bridge.calls.at(-2)!.payload).toEqual({ side: 'asr', id: 'tencent-asr', model: 'x' });
    const none = mountWindow(job('quota', { quotaSide: 'asr', choices: [{ kind: 'asr', id: 'openai-asr', model: 'whisper-1', current: true }] }));
    expect(none.wrapper.find('[data-no-choice]').text()).toBe(t('transcribe.quota.noChoice'));
  });

  it('failed shows the error with a settings link for auth and keeps the finished cues exportable', async () => {
    const { wrapper, bridge } = mountWindow(job('failed', { errorCode: 'video.translationFailed', errorKind: 'auth', cues: [cue('c1', 1, 2, 'Hello')] }));
    expect(wrapper.find('[data-failed] [data-error]').text()).toContain(t('transcribe.error.video.translationFailed'));
    await wrapper.find('[data-failed] .link').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Window.OpenSettings');
    expect(wrapper.find('[data-export-panel]').text()).toContain(t('transcribe.export.partialNote'));
  });

  it('done: SRT, VTT and TXT for original, translation and both bilingual orders; the saved path and left-out cues are reported', async () => {
    const { wrapper, bridge, inbound } = mountWindow(job('done', { stage: 'translate', slicesDone: 9, cues: [cue('c1', 1, 2, 'Hello', '你好')], cueCount: 1, translated: 1 }));
    expect(wrapper.find('[data-export-panel]').text()).not.toContain(t('transcribe.export.partialNote'));
    expect(wrapper.findAll('[data-export]')).toHaveLength(12);
    await wrapper.find('[data-export="vtt:bilingual"]').trigger('click');
    expect(bridge.calls.at(-1)).toEqual({ name: 'Transcription.Export', payload: { format: 'vtt', mode: 'bilingual' } });
    await wrapper.find('[data-export="srt:translation"]').trigger('click');
    expect(bridge.calls.at(-1)!.payload).toEqual({ format: 'srt', mode: 'translation' });
    await wrapper.find('[data-export="txt:bilingualTranslationFirst"]').trigger('click');
    expect(bridge.calls.at(-1)!.payload).toEqual({ format: 'txt', mode: 'bilingualTranslationFirst' });

    inbound.onEvent('transcribe', job('done', { cues: [], export: { format: 'srt', mode: 'original', path: 'D:\\subs\\movie.original.srt', error: undefined, retryable: false, exported: 41, overlaps: 2,
      issues: [{ cueId: 'c7', code: 'time.range' }, { cueId: 'c9', code: 'translation.missing' }] } }));
    await wrapper.vm.$nextTick();
    expect(wrapper.find('[data-saved]').text()).toBe(t('transcribe.export.saved', { path: 'D:\\subs\\movie.original.srt', n: 41 }));
    const issues = wrapper.find('[data-issues]').text();
    expect(issues).toContain(`c7 · ${t('transcribe.issue.time.range')}`);
    expect(issues).toContain(`c9 · ${t('transcribe.issue.translation.missing')}`);

    inbound.onEvent('transcribe', job('done', { cues: [], export: { format: 'srt', mode: 'original', path: undefined, error: 'export.diskFull', retryable: true, exported: 0, overlaps: 0, issues: [] } }));
    await wrapper.vm.$nextTick();
    expect(wrapper.find('[data-export-error]').text()).toBe(t('transcribe.export.error.export.diskFull'));
    expect(wrapper.find('[data-saved]').exists()).toBe(false);
  });
});

describe('SetSpeechB: video translation service', () => {
  const slot = (name: 'tts' | 'asr' | 'videoAsr'): SpeechSlotView => ({ slot: name, instance: '', model: '', ready: false, reasonKey: 'none-selected', choices: [] });
  const settings = (speech: SpeechView): SettingsView => ({
    revision: 7, fileHash: 'h7', issues: [], hotkeys: [], accounts: [], services: [],
    general: { uiLanguage: 'zh-Hans', sourceLanguage: 'en', targetLanguage: 'zh-Hans', defaultExpandedCards: 2, allowClipboardBorrowing: false, closeAction: 'hide', launchAtStartup: false },
    network: { proxyMode: 'system', proxyHost: '', proxyPort: 0, proxyUsername: '', proxyPasswordSaved: false, aiTimeoutSeconds: 30 },
    speech,
  });

  it('lists the enabled translation services next to the video ASR and saves the choice on its own', async () => {
    const bridge = fakeBridge(() => ({ ok: true }));
    const wrapper = mount(SpeechSettings, { props: { settings: settings({ tts: slot('tts'), asr: slot('asr'), videoAsr: slot('videoAsr'), videoTranslator: 'mymemory', videoTranslatorChoices: ['mymemory', 'deepl'] }), bridge } });
    const field = wrapper.find('[data-video-translator]');
    expect((field.element as HTMLSelectElement).value).toBe('mymemory');
    expect(field.findAll('option').map((o) => o.attributes('value'))).toEqual(['', 'mymemory', 'deepl']);
    expect(wrapper.find('[data-slot="videoAsr"]').text()).toContain(t('speech.videoTranslationHint'));
    await field.setValue('deepl');
    expect(bridge.calls.at(-1)).toEqual({ name: 'Settings.SelectSpeech', payload: { expectedRevision: 7, expectedFileHash: 'h7', slot: 'videoTranslator', instance: 'deepl', model: '' } });
    // The other groups have no such field.
    expect(wrapper.find('[data-slot="asr"] [data-video-translator]').exists()).toBe(false);
  });
});
