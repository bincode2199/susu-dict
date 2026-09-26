import { describe, expect, it, vi } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { flushPromises, mount } from '@vue/test-utils';
import type { CardSnapshot, CommandResult, DictionaryEntryView, SpeechBarView, SpeechStateView, TranslationSnapshot, WindowView } from '@protocol/ui';
import SpeechBar from '../src/windows/SpeechBar.vue';
import MainWindow from '../src/windows/MainWindow.vue';
import SelectionWindow from '../src/windows/SelectionWindow.vue';
import ResultCard from '../src/components/ResultCard.vue';
import App from '../src/App.vue';
import { createStore } from '../src/bridge/store';
import { windowFromQuery } from '../src/bridge/bridge';
import { setLocale, t } from '../src/locales/i18n';

setLocale('zh-Hans');

function fakeBridge(answer: (name: string, payload?: any) => CommandResult = () => ({ ok: true })) {
  const calls: { name: string; payload?: any }[] = [];
  return { calls, command: vi.fn(async (name: string, payload?: object) => { calls.push({ name, payload }); return answer(name, payload); }) };
}
const view = (kind: WindowView['kind'], features: string[] = ['input-translation', 'pronunciation']): WindowView =>
  ({ kind, uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features });
const bar = (active = 'native-sapi'): SpeechBarView => ({ id: 1, active, services: [{ instance: 'native-sapi', default: true }, { instance: 'microsoft-tts', default: false }] });
const speech = (phase: string, target = 'bar', extra: Partial<SpeechStateView> = {}): SpeechStateView => ({ generation: 1, phase, target, ...extra });

describe('F10.2 pronunciation bar (DESIGN 9 "发音浮条")', () => {
  function mountBar(state: SpeechStateView | null, speechBar: SpeechBarView = bar()) {
    const { state: ui, inbound } = createStore();
    inbound.onSnapshot({ window: view('Speech'), speech: state ?? undefined, speechBar });
    const bridge = fakeBridge();
    const wrapper = mount(SpeechBar, { props: { bridge: bridge as any, state: ui } });
    return { wrapper, bridge, inbound };
  }

  it('shows the status key, a divider and one square per service with the default first', () => {
    const { wrapper } = mountBar(speech('loading'));
    const squares = wrapper.findAll('button.service');
    expect(squares.map((s) => s.attributes('data-instance'))).toEqual(['native-sapi', 'microsoft-tts']);
    expect(squares.map((s) => s.text())).toEqual(['系', '微']);
    expect(squares[0].classes()).toContain('default');
    expect(squares[1].classes()).not.toContain('default');
    expect(wrapper.find('.divider').exists()).toBe(true);
    expect(wrapper.find('[role="toolbar"]').attributes('aria-label')).toBe(t('speech.bar'));
  });

  it('takes every colour from theme tokens, so the dark theme applies (F10.3)', () => {
    const source = readFileSync(join(__dirname, '../src/windows/SpeechBar.vue'), 'utf8');
    const style = source.slice(source.indexOf('<style'));
    expect(style).not.toMatch(/#[0-9a-f]{3,8}\b/i);
    expect(style).toContain('.service.default { border-color: var(--line-accent);');
  });

  it('loading and playing: the status key stops; the active square is pressed', async () => {
    const { wrapper, bridge, inbound } = mountBar(speech('loading'));
    expect(wrapper.attributes('data-phase')).toBe('loading');
    expect(wrapper.find('[data-action="status"]').attributes('aria-label')).toBe(t('speech.stop'));
    expect(wrapper.find('[role="status"]').text()).toBe(t('speech.phase.loading'));
    inbound.onEvent('speech', speech('playing'));
    await flushPromises();
    expect(wrapper.attributes('data-phase')).toBe('playing');
    expect(wrapper.find('[role="status"]').text()).toBe(t('speech.phase.playing'));
    expect(wrapper.find('[data-instance="native-sapi"]').attributes('aria-pressed')).toBe('true');
    await wrapper.find('[data-action="status"]').trigger('click');
    expect(bridge.calls).toEqual([{ name: 'Speech.Stop', payload: {} }]);
  });

  it('stopped: the status key reads again with the active service; a square reads with that service', async () => {
    const { wrapper, bridge } = mountBar(speech('stopped'), bar('microsoft-tts'));
    expect(wrapper.find('[data-action="status"]').attributes('aria-label')).toBe(t('speech.replay'));
    await wrapper.find('[data-action="status"]').trigger('click');
    await wrapper.find('[data-instance="native-sapi"]').trigger('click');
    expect(bridge.calls).toEqual([{ name: 'Speech.Play', payload: { instance: 'microsoft-tts' } }, { name: 'Speech.Play', payload: { instance: 'native-sapi' } }]);
  });

  it('error: the service or device failure is the status text', async () => {
    const { wrapper, inbound } = mountBar(speech('error', 'bar', { error: 'auth' }));
    expect(wrapper.attributes('data-phase')).toBe('error');
    expect(wrapper.find('[data-action="status"]').attributes('title')).toBe(t('error.auth'));
    inbound.onEvent('speech', speech('error', 'bar', { generation: 2, device: 'no-device' }));
    await flushPromises();
    expect(wrapper.find('[role="status"]').text()).toBe(t('speech.device.no-device'));
    // An older request never overwrites a newer one.
    inbound.onEvent('speech', speech('playing', 'bar', { generation: 1 }));
    await flushPromises();
    expect(wrapper.attributes('data-phase')).toBe('error');
  });

  it('a card read aloud meanwhile leaves the bar stopped', () => {
    const { wrapper } = mountBar(speech('playing', 'card:svc'));
    expect(wrapper.attributes('data-phase')).toBe('stopped');
    expect(wrapper.find('[data-instance="native-sapi"]').attributes('aria-pressed')).toBe('false');
  });

  it('the Speech window kind routes to the bar', async () => {
    expect(windowFromQuery('?w=speech&s=x').kind).toBe('Speech');
    const { state, inbound } = createStore();
    inbound.onSnapshot({ window: view('Speech'), speech: speech('playing'), speechBar: bar() });
    const wrapper = mount(App, { props: { kind: 'Speech', bridge: fakeBridge() as any, state } });
    await vi.dynamicImportSettled();
    await flushPromises();
    expect(wrapper.find('button.service').exists()).toBe(true);
  });
});

const entry: DictionaryEntryView = {
  word: 'good',
  phonetics: [{ accent: 'us', ipa: 'ɡʊd', audioId: 'audio-1' }, { accent: 'uk', ipa: 'ɡʊd' }],
  parts: [{ pos: 'adj.', means: ['好的'] }],
  forms: [],
  examples: [],
};
const plain = (state: CardSnapshot['state'] = 'Ready'): CardSnapshot => ({ serviceId: 'svc', displayName: 'svc', state, collapsed: false, text: '你好' });
const dict: CardSnapshot = { serviceId: 'youdao', displayName: 'youdao', state: 'Ready', collapsed: false, text: '', dictionary: true, entry };

describe('F10.2 card read-aloud keys (DESIGN 8, TTS03)', () => {
  const mountCard = (card: CardSnapshot, extra: { speech?: SpeechStateView | null; canSpeak?: boolean } = {}) =>
    mount(ResultCard, { props: { card, from: 'en', to: 'zh-Hans', canSpeak: true, ...extra } });

  it('every finished card has an enabled read-aloud key that asks for the card', async () => {
    const w = mountCard(plain());
    const key = w.find('[data-action="pronounce"]');
    expect(key.attributes('disabled')).toBeUndefined();
    expect(key.attributes('title')).toBe(t('card.pronounce'));
    await key.trigger('click');
    expect(w.emitted('speak')).toEqual([[]]);
  });

  it('no key while the card is loading, collapsed or failed', () => {
    for (const card of [plain('Loading'), plain('Failed'), { ...plain('CollapsedIdle'), collapsed: true }])
      expect(mountCard(card).find('[data-action="pronounce"]').exists()).toBe(false);
  });

  it('the key shows the playback it started and stops it', async () => {
    const w = mountCard(plain(), { speech: speech('playing', 'card:svc') });
    const key = w.find('[data-action="pronounce"]');
    expect(key.attributes('aria-pressed')).toBe('true');
    expect(key.attributes('aria-label')).toBe(t('speech.stop'));
    await key.trigger('click');
    expect(w.emitted('stopSpeech')).toHaveLength(1);
    expect(w.emitted('speak')).toBeUndefined();
    const other = mountCard(plain(), { speech: speech('playing', 'card:other') });
    expect(other.find('[data-action="pronounce"]').attributes('aria-pressed')).toBe('false');
  });

  it('phonetic keys ask for their index and show their own playback', async () => {
    const w = mountCard(dict, { speech: speech('loading', 'card:youdao:1') });
    const keys = w.findAll('.speak');
    expect(keys.map((k) => k.attributes('aria-pressed'))).toEqual(['false', 'true']);
    await keys[0].trigger('click');
    await keys[1].trigger('click');
    expect(w.emitted('speak')).toEqual([[0]]);
    expect(w.emitted('stopSpeech')).toHaveLength(1);
  });
});

describe('F10.2 result windows send the speech commands', () => {
  const translation = (cards: CardSnapshot[]): TranslationSnapshot => ({ revision: 1, generation: 1, sourceText: 'good', from: 'en', to: 'zh-Hans', cards });

  it('main window: card key, phonetic key and stop', async () => {
    const { state, inbound } = createStore();
    inbound.onSnapshot({ window: view('Main'), translation: translation([plain(), dict]) });
    const bridge = fakeBridge();
    const wrapper = mount(MainWindow, { props: { bridge: bridge as any, state } });
    const cards = wrapper.findAllComponents(ResultCard);
    await cards[0].find('[data-action="pronounce"]').trigger('click');
    await cards[1].findAll('.speak')[0].trigger('click');
    inbound.onEvent('speech', speech('playing', 'card:svc', { generation: 3 }));
    await flushPromises();
    await cards[0].find('[data-action="pronounce"]').trigger('click');
    expect(bridge.calls.filter((c) => c.name.startsWith('Speech.'))).toEqual([
      { name: 'Speech.SpeakCard', payload: { serviceId: 'svc' } },
      { name: 'Speech.SpeakCard', payload: { serviceId: 'youdao', phonetic: 0 } },
      { name: 'Speech.Stop', payload: {} },
    ]);
  });

  it('without the pronunciation feature the card key is unavailable but dictionary audio stays playable', async () => {
    const { state, inbound } = createStore();
    inbound.onSnapshot({ window: view('Selection', ['selection']), translation: translation([dict]), capture: { id: 1, origin: 'selection', empty: false } });
    const bridge = fakeBridge();
    const wrapper = mount(SelectionWindow, { props: { bridge: bridge as any, state } });
    const card = wrapper.findComponent(ResultCard);
    expect(card.find('[data-action="pronounce"]').attributes('disabled')).toBeDefined();
    expect(card.find('[data-action="pronounce"]').attributes('title')).toBe(t('card.needsSpeech'));
    const keys = card.findAll('.speak');
    expect(keys.map((k) => k.attributes('disabled') === undefined)).toEqual([true, false]);
    await keys[0].trigger('click');
    expect(bridge.calls.filter((c) => c.name.startsWith('Speech.'))).toEqual([{ name: 'Speech.SpeakCard', payload: { serviceId: 'youdao', phonetic: 0 } }]);
  });
});
