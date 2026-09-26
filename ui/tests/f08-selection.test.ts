// F08.3: the floating Selection window (Selection artboard), the failure bar (Error artboard 01), Esc on the top
// layer (UI03) and the SetHotkeys page (CFG05, C07 note).
import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import type { CardSnapshot, CommandResult, HotkeyView, SettingsView, TranslationSnapshot, UiSnapshot, WindowView } from '@protocol/ui';
import SelectionWindow from '../src/windows/SelectionWindow.vue';
import ErrorBar from '../src/windows/ErrorBar.vue';
import SettingsWindow from '../src/windows/SettingsWindow.vue';
import App from '../src/App.vue';
import { createStore } from '../src/bridge/store';
import { setLocale, t } from '../src/locales/i18n';

setLocale('zh-Hans');

function fakeBridge(answer: (name: string, payload?: any) => CommandResult = () => ({ ok: true })) {
  const calls: { name: string; payload?: any }[] = [];
  return { calls, command: vi.fn(async (name: string, payload?: object) => { calls.push({ name, payload }); return answer(name, payload); }) };
}
const view = (kind: WindowView['kind'], pinned = false): WindowView => ({ kind, uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned, devPreview: false, features: ['selection', 'clipboard'] });
const card = (serviceId: string, state: CardSnapshot['state'], text = ''): CardSnapshot => ({ serviceId, displayName: serviceId, state, collapsed: false, text });
const translation = (sourceText: string, cards: CardSnapshot[], generation = 1): TranslationSnapshot => ({ revision: 3, generation, sourceText, from: 'en', to: 'zh-Hans', cards });

function mountFloat(snapshot: Omit<UiSnapshot, 'window'>, pinned = false) {
  const { state, inbound } = createStore();
  inbound.onSnapshot({ window: view('Selection', pinned), ...snapshot });
  const bridge = fakeBridge();
  const wrapper = mount(SelectionWindow, { props: { bridge: bridge as any, state }, attachTo: document.body });
  return { state, inbound, bridge, wrapper };
}

afterEach(() => { document.body.innerHTML = ''; });

describe('Selection window', () => {
  it('with a result: source under the 34 px language row, one card per service, origin in the footer, status bar hint', async () => {
    const { wrapper } = mountFloat({ capture: { id: 1, origin: 'selection', empty: false }, translation: translation('A good translation', [card('deepl', 'Ready', '好的译文'), card('google', 'CollapsedIdle')]) });
    await flushPromises();
    expect((wrapper.find('textarea').element as HTMLTextAreaElement).value).toBe('A good translation');
    expect(wrapper.find('.count').text()).toBe(`${t('main.chars', { n: 18 })} · ${t('float.origin.selection')}`);
    expect(wrapper.findAll('.cards > *')).toHaveLength(2);
    expect(wrapper.find('.cards').text()).toContain('好的译文');
    expect(wrapper.find('.statusbar').text()).toContain(t('float.statusHint'));
    expect(wrapper.find('.statusbar .link').text()).toBe(t('float.pin'));
    // Nothing to resubmit: the Translate button appears only when the source was changed.
    expect(wrapper.find('button.translate').exists()).toBe(false);
    // Header: language row plus pin, open in main window and close; no system buttons.
    const labels = wrapper.findAll('.float > .head button').map((b) => b.attributes('aria-label'));
    expect(labels).toEqual([t('lang.swap'), t('window.pin'), t('float.openInMain'), t('window.close')]);
  });

  it('clipboard origin is labelled as such', () => {
    const { wrapper } = mountFloat({ capture: { id: 2, origin: 'clipboard', empty: false }, translation: translation('copied', []) });
    expect(wrapper.find('.count').text()).toContain(t('float.origin.clipboard'));
  });

  it('while the captured text is on its way, shows a status line instead of the empty input', () => {
    const { wrapper } = mountFloat({ capture: { id: 1, origin: 'selection', empty: false }, translation: translation('', [], 0) });
    expect(wrapper.find('.waiting').text()).toBe(t('float.waiting'));
    expect(wrapper.find('textarea').exists()).toBe(false);
  });

  it('empty state (shared hotkey, nothing on the clipboard): focused input, Translate submits the typed text', async () => {
    const { wrapper, bridge } = mountFloat({ capture: { id: 3, origin: 'clipboard', empty: true }, translation: translation('', [card('deepl', 'CollapsedIdle')], 0) });
    await flushPromises();
    const area = wrapper.find('textarea');
    expect(document.activeElement).toBe(area.element);
    expect(area.attributes('placeholder')).toBe(t('main.placeholder'));
    expect(wrapper.find('.count').text()).toBe(t('main.chars', { n: 0 })); // no origin for an empty capture
    expect(wrapper.find('button.translate').attributes('disabled')).toBeDefined();
    await area.setValue('hello');
    await wrapper.find('button.translate').trigger('click');
    expect(bridge.calls.at(-1)).toEqual({ name: 'Translation.SubmitText', payload: { text: 'hello' } });
  });

  it('a new capture replaces the text; Ctrl+Enter submits but not during IME composition (UI03)', async () => {
    const { wrapper, bridge, inbound } = mountFloat({ capture: { id: 1, origin: 'selection', empty: false }, translation: translation('first', []) });
    inbound.onEvent('capture', { id: 2, origin: 'selection', empty: false });
    inbound.onEvent('translation', translation('second', [], 2));
    await flushPromises();
    const area = wrapper.find('textarea');
    expect((area.element as HTMLTextAreaElement).value).toBe('second');
    await area.setValue('second edited');
    await area.trigger('compositionstart');
    await area.trigger('keydown', { key: 'Enter', ctrlKey: true });
    expect(bridge.calls.some((c) => c.name === 'Translation.SubmitText')).toBe(false);
    await area.trigger('compositionend');
    await area.trigger('keydown', { key: 'Enter', ctrlKey: true });
    expect(bridge.calls.at(-1)).toEqual({ name: 'Translation.SubmitText', payload: { text: 'second edited' } });
  });

  it('open in main window hands over the current text; pin toggles through the host', async () => {
    const { wrapper, bridge } = mountFloat({ capture: { id: 1, origin: 'selection', empty: false }, translation: translation('hello', []) }, true);
    await wrapper.findAll('.float > .head button')[2].trigger('click');
    expect(bridge.calls.at(-1)).toEqual({ name: 'Window.OpenInMain', payload: { text: 'hello' } });
    expect(wrapper.find('.statusbar .link').text()).toBe(t('float.unpin'));
    await wrapper.find('.statusbar .link').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Window.Pin');
  });

  it('reports its natural content height, counting the whole card list, so the host can fit or scroll it (UI01)', async () => {
    // jsdom has no layout: give every block 34 px and the card list a 600 px scroll height.
    const offset = vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(34);
    const scroll = vi.spyOn(HTMLElement.prototype, 'scrollHeight', 'get').mockReturnValue(600);
    try {
      const { bridge } = mountFloat({ capture: { id: 1, origin: 'selection', empty: false }, translation: translation('x', [card('a', 'Ready', 'y')]) });
      await vi.waitFor(() => expect(bridge.calls.some((c) => c.name === 'Window.FitContent')).toBe(true));
      // frame 2 + header 34 + source 34 + cards 600 (scroll height, not the clipped box) + status bar 34
      expect(bridge.calls.find((c) => c.name === 'Window.FitContent')!.payload).toEqual({ heightDip: 704 });
    } finally { offset.mockRestore(); scroll.mockRestore(); }
  });
});

describe('Failure bar', () => {
  function mountBar(lines: { key: string; link?: string }[]) {
    const { state, inbound } = createStore();
    inbound.onSnapshot({ window: view('Error'), errorBar: { id: 1, lines } });
    const bridge = fakeBridge();
    const wrapper = mount(ErrorBar, { props: { bridge: bridge as any, state } });
    return { state, inbound, bridge, wrapper };
  }

  it('borrow off: the lossless-capture text with "go to settings"; the height fits one row', async () => {
    const { wrapper, bridge } = mountBar([{ key: 'capture.notSupportedBorrowOff', link: 'settings' }]);
    expect(wrapper.find('[role="alert"]').exists()).toBe(true);
    expect(wrapper.find('.text').text()).toBe('当前程序不支持无损取词 —— 可在设置中允许借用剪贴板取词');
    expect(wrapper.find('.link').text()).toBe('去设置');
    expect(bridge.calls[0]).toEqual({ name: 'Window.FitContent', payload: { heightDip: 34 } });
    await wrapper.find('.link').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Window.OpenSettings');
  });

  it('borrow on but still unreadable: the other text, without a link (the two never mix)', () => {
    const { wrapper } = mountBar([{ key: 'capture.notSupported' }]);
    expect(wrapper.find('.text').text()).toBe('当前程序不支持取词');
    expect(wrapper.find('.link').exists()).toBe(false);
  });

  it('a failure plus the restore notice: two rows, 68 DIP, the notice links to capture settings', async () => {
    const { wrapper, bridge, inbound } = mountBar([{ key: 'capture.notSupported' }, { key: 'capture.restoreFailed', link: 'settings' }]);
    expect(wrapper.findAll('.line')).toHaveLength(2);
    expect(wrapper.findAll('.line')[1].text()).toContain('未能恢复剪贴板，当前内容已保留');
    expect(wrapper.find('.link').text()).toBe('取词设置');
    expect(bridge.calls[0].payload).toEqual({ heightDip: 68 });
    inbound.onEvent('errorbar', { id: 2, lines: [{ key: 'capture.notSupported' }] });
    await flushPromises();
    expect(wrapper.findAll('.line')).toHaveLength(1);
    expect(bridge.calls.at(-1)!.payload).toEqual({ heightDip: 34 });
  });

  it('English UI shows the host texts in English', () => {
    setLocale('en');
    try {
      const { wrapper } = mountBar([{ key: 'capture.notSupportedBorrowOff', link: 'settings' }]);
      expect(wrapper.find('.text').text()).toContain('lossless capture');
    } finally { setLocale('zh-Hans'); }
  });
});

describe('Esc takes the top layer first (UI03)', () => {
  async function mountApp() {
    const { state, inbound } = createStore();
    inbound.onSnapshot({ window: view('Selection'), capture: { id: 1, origin: 'selection', empty: false }, translation: translation('hello', []) });
    const bridge = fakeBridge();
    const wrapper = mount(App, { props: { kind: 'Selection', bridge: bridge as any, state }, attachTo: document.body });
    for (let i = 0; i < 20 && !wrapper.find('.float').exists(); i++) { await flushPromises(); await new Promise((r) => setTimeout(r, 5)); }
    return { bridge, wrapper };
  }

  it('Esc hides the floating window', async () => {
    const { bridge, wrapper } = await mountApp();
    expect(wrapper.find('.float').exists()).toBe(true);
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', cancelable: true }));
    expect(bridge.calls.at(-1)!.name).toBe('Window.Close');
  });

  it('an open language list or an IME composition keeps Esc for itself', async () => {
    const { bridge, wrapper } = await mountApp();
    (wrapper.find('#float-target-language').element as HTMLSelectElement).focus();
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', cancelable: true }));
    (wrapper.find('textarea').element as HTMLTextAreaElement).focus();
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', cancelable: true, isComposing: true }));
    expect(bridge.calls.some((c) => c.name === 'Window.Close')).toBe(false);
  });
});

describe('SetHotkeys (CFG05)', () => {
  const hotkey = (action: string, chord: string, state = 'ok'): HotkeyView => ({ action, chord, state });
  const settings = (hotkeys: HotkeyView[], borrow = false): SettingsView => ({
    revision: 1, fileHash: 'h', issues: [], hotkeys, accounts: [], services: [],
    general: { uiLanguage: 'zh-Hans', sourceLanguage: 'en', targetLanguage: 'zh-Hans', defaultExpandedCards: 2, allowClipboardBorrowing: borrow, closeAction: 'hide', launchAtStartup: false },
    network: { proxyMode: 'system', proxyHost: '', proxyPort: 0, proxyUsername: '', proxyPasswordSaved: false, aiTimeoutSeconds: 30 },
  });
  async function mountHotkeys(hotkeys: HotkeyView[]) {
    const { state } = createStore();
    state.window = { kind: 'Settings', uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features: [] };
    state.settings = settings(hotkeys);
    const bridge = fakeBridge();
    const wrapper = mount(SettingsWindow, { props: { bridge: bridge as any, state } });
    await wrapper.findAll('.nav-item').find((n) => n.text() === t('nav.hotkeys'))!.trigger('click');
    const row = (action: string) => wrapper.findAll('.row').find((r) => r.find('.title').text() === t(`hotkeys.${action}`))!;
    return { wrapper, bridge, row };
  }

  it('selection and clipboard are marked shareable and may use one chord; selection explains lossless capture first', async () => {
    const { row } = await mountHotkeys([hotkey('inputTranslate', 'Alt+A'), hotkey('selectionTranslate', 'Alt+D'), hotkey('clipboardTranslate', 'Alt+D'), hotkey('pronounce', 'Alt+R')]);
    expect(row('selectionTranslate').find('.shared-tag').text()).toBe(t('hotkeys.shared'));
    expect(row('clipboardTranslate').find('.shared-tag').exists()).toBe(true);
    expect(row('inputTranslate').find('.shared-tag').exists()).toBe(false);
    expect(row('selectionTranslate').find('.hotkey-error').exists()).toBe(false);
    expect(row('clipboardTranslate').find('.hotkey-error').exists()).toBe(false);
    expect(row('selectionTranslate').find('.hint').text()).toContain('优先无损取词');
    expect(row('pronounce').find('.hint').text()).toBe(t('hotkeys.note.pronounce'));
  });

  it('any other shared chord names the action that already uses it', async () => {
    const { row } = await mountHotkeys([hotkey('selectionTranslate', 'Alt+S', 'conflict'), hotkey('clipboardTranslate', 'Alt+D'), hotkey('ocrTranslate', 'Alt+S', 'conflict')]);
    expect(row('ocrTranslate').find('.hotkey-error').text()).toBe('Alt+S 已被「划词翻译」占用');
    expect(row('selectionTranslate').find('.hotkey-error').text()).toBe('Alt+S 已被「OCR 识别翻译」占用');
    expect(row('clipboardTranslate').find('.hotkey-error').exists()).toBe(false);
  });

  it('a RegisterHotKey refusal is one visible error line on the row', async () => {
    const { row } = await mountHotkeys([hotkey('selectionTranslate', 'Alt+R', 'failed'), hotkey('clipboardTranslate', 'Alt+R', 'failed')]);
    expect(row('selectionTranslate').find('.hotkey-error').text()).toBe('系统拒绝注册 Alt+R，可能已被其他程序占用 —— 换一个组合键');
    expect(row('clipboardTranslate').find('.hotkey-error').attributes('role')).toBe('alert');
  });

  it('the borrow switch sits with the capture hotkeys and says honestly that the copy lands in Win+V history (C07)', async () => {
    const { wrapper } = await mountHotkeys([hotkey('selectionTranslate', 'Alt+D')]);
    const borrow = wrapper.findAll('.row').find((r) => r.find('.title').text() === t('general.allowBorrow'));
    expect(borrow).toBeTruthy();
    expect(borrow!.find('[role="switch"], input[type="checkbox"], button').exists()).toBe(true);
    const hint = borrow!.find('.hint').text();
    expect(hint).toContain('Win+V');
    expect(hint).not.toMatch(/不会进入|不进入|排除/); // never promises to keep it out of history
  });
});
