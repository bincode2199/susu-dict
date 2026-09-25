// F06 independent verification: UI03 focus order and Esc, UI04 kept results, S07 on the SetEngines and
// SetAI pages through the real SettingsWindow, S08 hostile service output in the main window.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import type { CardSnapshot, CommandResult, ServiceView, SettingsView, TranslationSnapshot, UiSnapshot } from '@protocol/ui';
import MainWindow from '../src/windows/MainWindow.vue';
import SettingsWindow from '../src/windows/SettingsWindow.vue';
import ServiceDetails from '../src/components/ServiceDetails.vue';
import { createStore } from '../src/bridge/store';
import { setLocale, t } from '../src/locales/i18n';

setLocale('zh-Hans');

const card = (serviceId: string, state: CardSnapshot['state'], text = '', extra: Partial<CardSnapshot> = {}): CardSnapshot => ({ serviceId, displayName: serviceId, state, collapsed: false, text, ...extra });

function fakeBridge(answer: (name: string, payload?: any) => CommandResult | Promise<CommandResult> = () => ({ ok: true })) {
  const calls: { name: string; payload?: any }[] = [];
  return { calls, command: vi.fn(async (name: string, payload?: object) => { calls.push({ name, payload }); return answer(name, payload); }) };
}

const mainWindow = { kind: 'Main' as const, uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features: ['input-translation'] };
const snapshot = (cards: CardSnapshot[]): TranslationSnapshot => ({ revision: 10, generation: 1, sourceText: 'hello', from: 'en', to: 'zh-Hans', cards });

function mountMain(cards: CardSnapshot[]) {
  const { state, inbound } = createStore();
  inbound.onSnapshot({ window: mainWindow, translation: snapshot(cards) } satisfies UiSnapshot);
  const bridge = fakeBridge();
  const wrapper = mount(MainWindow, { props: { bridge: bridge as any, state }, attachTo: document.body });
  return { state, inbound, bridge, wrapper };
}

const FOCUSABLE = 'button:not([disabled]), a[href], a.retry, input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]';

afterEach(() => { document.body.innerHTML = ''; });

describe('UI03: focus order and Esc', () => {
  it('main window: no positive tabindex, so Tab follows reading order: languages, input, then each card', () => {
    const { wrapper } = mountMain([card('mymemory/translate', 'Ready', '你好'), card('openai/translate', 'Failed', '', { error: 'network' })]);
    const all = Array.from((wrapper.element as HTMLElement).querySelectorAll<HTMLElement>(FOCUSABLE));
    expect(all.filter((el) => Number(el.getAttribute('tabindex') ?? '0') > 0)).toEqual([]);
    const textarea = all.findIndex((el) => el.tagName === 'TEXTAREA');
    const language = all.findIndex((el) => el.id === 'target-language');
    const toggles = all.map((el, i) => (el.matches('button.toggle') ? i : -1)).filter((i) => i >= 0);
    expect(textarea).toBeGreaterThanOrEqual(0);
    expect(textarea).toBeGreaterThan(language);
    expect(toggles).toHaveLength(2);
    expect(toggles[0]).toBeGreaterThan(textarea);
    // Every card control is reachable by keyboard and named for assistive technology.
    for (const el of all.filter((el) => el.tagName === 'BUTTON')) expect(el.getAttribute('aria-label') ?? el.textContent?.trim()).toBeTruthy();
  });

  it('the input has focus when the main window opens', async () => {
    const { wrapper } = mountMain([]);
    await flushPromises();
    expect(document.activeElement).toBe(wrapper.find('textarea').element);
  });

  it('Esc in the input neither submits, clears nor closes anything (the main window has no layer of its own)', async () => {
    const { bridge, wrapper } = mountMain([card('mymemory/translate', 'Ready', '你好')]);
    const area = wrapper.find('textarea');
    await area.setValue('hello');
    await area.trigger('keydown', { key: 'Escape' });
    expect(bridge.calls).toEqual([]);
    expect((area.element as HTMLTextAreaElement).value).toBe('hello');
  });

  // The key confirmation bar is inline, not a popup layer; since F07.1 Esc also dismisses it (f07-services.test.ts).
  it('the key confirmation bar is dismissed by its Cancel button and sends nothing', async () => {
    const deepl: ServiceView = {
      serviceId: 'deepl/translate', instanceId: 'deepl', capability: 'translate', page: 'engines', enabled: true, availability: 'MissingCredential',
      implemented: true, secretNames: ['apiKey'], plan: 'free', order: 1,
      credentialTargets: [{ secret: 'apiKey', origin: 'https://api-free.deepl.com:443', use: 'header:Authorization', saved: false, granted: false }],
    };
    const bridge = fakeBridge();
    const wrapper = mount(ServiceDetails, { props: { service: deepl, accounts: [], bridge, clearToken: 0 }, attachTo: document.body });
    await wrapper.find('input').setValue('dl-key:fx');
    await wrapper.find('button.primary').trigger('click');
    expect(wrapper.find('.confirm').exists()).toBe(true);
    await wrapper.find('.confirm button:not(.primary)').trigger('click');
    expect(wrapper.find('.confirm').exists()).toBe(false);
    expect(bridge.calls).toHaveLength(0);
  });
});

describe('UI04: a failing service keeps the other results', () => {
  it('a new failure on one card never clears or collapses the finished text of another, and nothing is forced collapsed', async () => {
    const { inbound, wrapper } = mountMain([card('mymemory/translate', 'Ready', '已有译文'), card('deepl/translate', 'Streaming', '部分')]);
    inbound.onPatch('card', { revision: 11, generation: 1, card: card('deepl/translate', 'Failed', '', { error: 'network' }), offline: false });
    await flushPromises();
    const texts = wrapper.findAll('.text').map((n) => n.text());
    expect(texts).toContain('已有译文');
    expect(wrapper.findAll('button.toggle').map((b) => b.attributes('aria-expanded'))).toEqual(['true', 'true']);
    expect(wrapper.text()).not.toContain(t('main.offline'));
    // The failed card offers its own retry; the ready card still offers copy.
    expect(wrapper.findAll('a.retry')).toHaveLength(1);
  });
});

describe('S07: secret entry on the SetEngines and SetAI pages', () => {
  const openai = (saved: boolean): ServiceView => ({
    serviceId: 'openai/translate', instanceId: 'openai', capability: 'translate', page: 'ai', enabled: false, availability: saved ? 'Ready' : 'MissingCredential',
    implemented: true, secretNames: ['apiKey'], accountId: saved ? 'openai' : undefined, order: 3,
    credentialTargets: [{ secret: 'apiKey', origin: 'https://api.openai.com:443', use: 'header:Authorization', saved, granted: saved }],
  });
  const tencent: ServiceView = {
    serviceId: 'tencent-translate/translate', instanceId: 'tencent-translate', capability: 'translate', page: 'engines', enabled: false, availability: 'MissingCredential',
    implemented: true, secretNames: ['secretId', 'secretKey'], order: 1,
    credentialTargets: [
      { secret: 'secretId', origin: 'https://tmt.tencentcloudapi.com:443', use: 'signer:tencent-tc3', saved: false, granted: false },
      { secret: 'secretKey', origin: 'https://tmt.tencentcloudapi.com:443', use: 'signer:tencent-tc3', saved: false, granted: false },
    ],
  };
  const settings = (services: ServiceView[]): SettingsView => ({
    revision: 1, fileHash: 'h', issues: [], hotkeys: [], accounts: [], services,
    general: { uiLanguage: 'zh-Hans', sourceLanguage: 'en', targetLanguage: 'zh-Hans', defaultExpandedCards: 2, allowClipboardBorrowing: false, closeAction: 'hide', launchAtStartup: false },
    network: { proxyMode: 'system', proxyHost: '', proxyPort: 0, proxyUsername: '', proxyPasswordSaved: false, aiTimeoutSeconds: 30 },
  });

  async function mountSettings(services: ServiceView[], answer?: (name: string, payload?: any) => CommandResult) {
    const { state, inbound } = createStore();
    state.window = { kind: 'Settings', uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features: [] };
    state.settings = settings(services);
    const bridge = fakeBridge(answer);
    const wrapper = mount(SettingsWindow, { props: { bridge: bridge as any, state } });
    const go = async (page: string) => {
      const item = wrapper.findAll('.nav-item').find((n) => n.text() === t(`nav.${page}`))!;
      await item.trigger('click');
    };
    return { state, inbound, bridge, wrapper, go };
  }

  it('SetAI: OpenAI key typed, then a page change drops it; nothing reaches the host or browser storage', async () => {
    const { bridge, wrapper, go } = await mountSettings([openai(false)]);
    await go('ai');
    expect(wrapper.find('h2').text()).toBe(t('services.ai'));
    await wrapper.find(`button[aria-label="${t('services.details', { name: 'OpenAI' })}"]`).trigger('click');
    await wrapper.find('.expanded input').setValue('sk-TYPED-SECRET');
    await go('engines');
    await go('ai');
    const input = wrapper.find('.expanded input');
    if (input.exists()) expect((input.element as HTMLInputElement).value).toBe('');
    expect(wrapper.html()).not.toContain('sk-TYPED-SECRET');
    expect(bridge.calls.filter((c) => c.name.startsWith('Secret.'))).toEqual([]);
    expect(localStorage.length + sessionStorage.length).toBe(0);
  });

  it('SetAI: hiding the window drops a typed key; a saved key has no input and never comes back', async () => {
    const { inbound, wrapper, go, state } = await mountSettings([openai(false)]);
    await go('ai');
    await wrapper.find(`button[aria-label="${t('services.details', { name: 'OpenAI' })}"]`).trigger('click');
    await wrapper.find('.expanded input').setValue('sk-TYPED-SECRET');
    inbound.onEvent('window.hidden', undefined);
    await flushPromises();
    expect((wrapper.find('.expanded input').element as HTMLInputElement).value).toBe('');
    state.settings = settings([openai(true)]);
    await flushPromises();
    expect(wrapper.find('.expanded input').exists()).toBe(false);
    expect(wrapper.text()).toContain(t('secret.saved'));
  });

  it('SetEngines: Tencent keys are confirmed against the exact signer address before anything is sent', async () => {
    const { bridge, wrapper, go } = await mountSettings([tencent]);
    await go('engines');
    await wrapper.find(`button[aria-label="${t('services.details', { name: '腾讯翻译君' })}"]`).trigger('click');
    const details = wrapper.find('.expanded');
    expect(details.exists()).toBe(true);
    const inputs = details.findAll('input');
    expect(inputs.length).toBe(2);
    for (const input of inputs) expect(input.attributes('type')).toBe('password');
    await inputs[0].setValue('AKID-SECRET-ID');
    await details.findAll('button.primary')[0].trigger('click');
    expect(details.find('.confirm').text()).toContain('https://tmt.tencentcloudapi.com:443');
    expect(bridge.calls).toEqual([]);
  });
});

describe('S08: hostile service output in the main window', () => {
  it('script, event-handler and SVG payloads in streamed and final text stay text', async () => {
    const payload = '<img src=x onerror="window.__pwned=1"><svg onload="window.__pwned=2"><script>window.__pwned=3</script></svg>';
    const { inbound, wrapper } = mountMain([card('openai/translate', 'Streaming', payload)]);
    inbound.onPatch('card', { revision: 11, generation: 1, card: card('openai/translate', 'Ready', payload + '<a href="javascript:alert(1)">x</a>'), offline: false });
    await flushPromises();
    const body = wrapper.find('.card .text');
    expect(body.text()).toContain('<svg onload=');
    expect((body.element as HTMLElement).querySelectorAll('img, svg, script, a, iframe')).toHaveLength(0);
    expect((window as any).__pwned).toBeUndefined();
  });
});
