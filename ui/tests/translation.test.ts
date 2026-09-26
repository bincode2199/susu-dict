import { describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import type { CardSnapshot, CommandResult, ServiceView, SettingsView, TranslationSnapshot, UiSnapshot } from '@protocol/ui';
import ResultCard from '../src/components/ResultCard.vue';
import ServiceDetails from '../src/components/ServiceDetails.vue';
import MainWindow from '../src/windows/MainWindow.vue';
import { createStore } from '../src/bridge/store';
import { setLocale, t } from '../src/locales/i18n';

setLocale('zh-Hans');

const card = (serviceId: string, state: CardSnapshot['state'], text = '', extra: Partial<CardSnapshot> = {}): CardSnapshot => ({ serviceId, displayName: serviceId, state, collapsed: false, text, ...extra });
const mountCard = (c: CardSnapshot) => mount(ResultCard, { props: { card: c, from: 'en', to: 'zh-Hans' } });

function fakeBridge(answer: (name: string, payload?: any) => CommandResult | Promise<CommandResult> = () => ({ ok: true })) {
  const calls: { name: string; payload?: any }[] = [];
  return { calls, command: vi.fn(async (name: string, payload?: object) => { calls.push({ name, payload }); return answer(name, payload); }) };
}

describe('F06.3b result cards', () => {
  it('appends streaming text in place and offers copy only once the result is complete', async () => {
    const wrapper = mountCard(card('openai/translate', 'Streaming', '你好'));
    expect(wrapper.find('.text').text()).toBe('你好');
    expect(wrapper.find('.status').text()).toBe(t('card.translating'));
    expect(wrapper.find('.actions').exists()).toBe(false);
    await wrapper.setProps({ card: card('openai/translate', 'Streaming', '你好，世界') });
    expect(wrapper.find('.text').text()).toBe('你好，世界');
    await wrapper.setProps({ card: card('openai/translate', 'Ready', '你好，世界。') });
    expect(wrapper.find('.status').text()).toBe('');
    await wrapper.find('[data-action="copy"]').trigger('click');
    expect(wrapper.emitted('copy')).toHaveLength(1);
    expect(wrapper.find('.status').text()).toBe(t('card.copied'));
  });

  it('offers a manual retry for network-type failures and a cancelled attempt, settings for auth/quota', async () => {
    for (const error of ['network', 'timeout', 'rate_limited'] as const) {
      const wrapper = mountCard(card('s', 'Failed', '', { error }));
      await wrapper.find('a.retry').trigger('click');
      expect(wrapper.emitted('retry')).toHaveLength(1);
    }
    const cancelled = mountCard(card('s', 'Cancelled'));
    expect(cancelled.text()).toContain(t('card.cancelled'));
    expect(cancelled.find('a.retry').exists()).toBe(true);
    const auth = mountCard(card('s', 'Failed', '', { error: 'auth' }));
    expect(auth.find('a.retry').exists()).toBe(false);
    await auth.find('a').trigger('click');
    expect(auth.emitted('settings')).toHaveLength(1);
  });

  it('collapses through the toggle and shows no body while collapsed', async () => {
    const wrapper = mountCard(card('s', 'Streaming', 'partial'));
    await wrapper.find('button.toggle').trigger('click');
    expect(wrapper.emitted('toggle')).toHaveLength(1);
    await wrapper.setProps({ card: card('s', 'Cancelled', '', { collapsed: true }) });
    expect(wrapper.find('.body').exists()).toBe(false);
    expect(wrapper.find('button.toggle').attributes('aria-expanded')).toBe('false');
  });
});

describe('F06.3b main window', () => {
  const window = { kind: 'Main' as const, uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features: ['input-translation'] };
  const snapshot = (cards: CardSnapshot[], generation = 1): TranslationSnapshot => ({ revision: 10, generation, sourceText: 'hello', from: 'en', to: 'zh-Hans', cards });

  function setup(cards: CardSnapshot[]) {
    const { state, inbound } = createStore();
    inbound.onSnapshot({ window, translation: snapshot(cards) } satisfies UiSnapshot);
    const bridge = fakeBridge();
    const wrapper = mount(MainWindow, { props: { bridge: bridge as any, state } });
    return { state, inbound, bridge, wrapper };
  }

  it('sends collapse (which the host turns into a cancel), retry and copy for the right card', async () => {
    const { bridge, wrapper } = setup([card('mymemory/translate', 'Ready', '你好'), card('deepl/translate', 'Failed', '', { error: 'network' }), card('openai/translate', 'Streaming', '部分')]);
    const cards = wrapper.findAllComponents(ResultCard);
    await cards[2].find('button.toggle').trigger('click');
    await cards[1].find('a.retry').trigger('click');
    await cards[0].find('[data-action="copy"]').trigger('click');
    expect(bridge.calls).toEqual(expect.arrayContaining([
      { name: 'Translation.ToggleCard', payload: { serviceId: 'openai/translate' } },
      { name: 'Translation.RetryCard', payload: { serviceId: 'deepl/translate' } },
      { name: 'Window.CopyText', payload: { text: '你好' } },
    ]));
  });

  it('keeps cards in the host order, and a failing card leaves the others and the offline notice alone (UI04)', async () => {
    const { inbound, wrapper } = setup([card('b', 'Ready', 'B 的译文'), card('a', 'Loading')]);
    expect(wrapper.findAll('.name').map((n) => n.text())).toEqual(['b', 'a']);
    inbound.onPatch('card', { revision: 11, generation: 1, card: card('a', 'Failed', '', { error: 'network' }), offline: false });
    await flushPromises();
    expect(wrapper.findAll('.text').map((n) => n.text())).toEqual(['B 的译文']);
    expect(wrapper.text()).not.toContain(t('main.offline'));
    // Only when the host says every requested remote path failed does the shared notice appear.
    inbound.onPatch('card', { revision: 12, generation: 1, card: card('b', 'Failed', '', { error: 'timeout' }), offline: true });
    await flushPromises();
    expect(wrapper.find('.statusbar').text()).toContain(t('main.offline'));
    expect(wrapper.findAll('.card').length).toBe(2);
    inbound.onPatch('card', { revision: 13, generation: 1, card: card('a', 'Ready', 'A'), offline: false });
    await flushPromises();
    expect(wrapper.find('.statusbar').text()).not.toContain(t('main.offline'));
  });

  it('changing the target language re-requests the current text', async () => {
    const { bridge, wrapper } = setup([card('a', 'Ready', 'x')]);
    await wrapper.find('textarea').setValue('hello');
    await wrapper.find('#target-language').setValue('en');
    await flushPromises();
    const names = bridge.calls.map((c) => c.name);
    expect(names).toContain('Translation.SelectLanguage');
    expect(names.indexOf('Translation.SubmitText')).toBeGreaterThan(names.indexOf('Translation.SelectLanguage'));
    expect(bridge.calls.find((c) => c.name === 'Translation.SelectLanguage')!.payload).toEqual({ from: 'zh-Hans', to: 'en' });
  });

  it('UI03: Enter alone and Ctrl+Enter during IME composition never submit', async () => {
    const { bridge, wrapper } = setup([]);
    const area = wrapper.find('textarea');
    await area.setValue('中文');
    await area.trigger('keydown', { key: 'Enter' });
    await area.trigger('compositionstart');
    await area.trigger('keydown', { key: 'Enter', ctrlKey: true, isComposing: true });
    expect(bridge.calls.filter((c) => c.name === 'Translation.SubmitText')).toHaveLength(0);
    await area.trigger('compositionend');
    await area.trigger('keydown', { key: 'Enter', ctrlKey: true });
    expect(bridge.calls.filter((c) => c.name === 'Translation.SubmitText')).toEqual([{ name: 'Translation.SubmitText', payload: { text: '中文' } }]);
  });
});

describe('F06.3b service settings', () => {
  const KEY = 'dl-SECRET-4711:fx';
  const deepl = (saved: boolean, granted: boolean, plan = 'free'): ServiceView => ({
    serviceId: 'deepl/translate', instanceId: 'deepl', capability: 'translate', page: 'engines', enabled: true, availability: saved && granted ? 'Ready' : 'MissingCredential',
    implemented: true, secretNames: ['apiKey'], accountId: saved ? 'deepl' : undefined, plan, order: 1,
    credentialTargets: [{ secret: 'apiKey', origin: plan === 'free' ? 'https://api-free.deepl.com:443' : 'https://api.deepl.com:443', use: 'header:Authorization', saved, granted }],
  });
  const view = (service: ServiceView) => ({ services: [service], accounts: [] }) as unknown as SettingsView;
  const mountDetails = (service: ServiceView, bridge = fakeBridge()) =>
    ({ bridge, wrapper: mount(ServiceDetails, { props: { service, accounts: [], bridge, clearToken: 0 } }) });

  it('asks to confirm the exact address before sending a key, then never shows the key again (S07)', async () => {
    const { bridge, wrapper } = mountDetails(deepl(false, false, 'pro'), fakeBridge(() => ({ ok: true, value: view(deepl(true, true)) })));
    await wrapper.find('input').setValue(KEY);
    await wrapper.find('button.primary').trigger('click');
    // ":fx" means the Free endpoint, even though the current plan is Pro.
    expect(wrapper.find('.confirm').text()).toContain('https://api-free.deepl.com:443');
    expect(bridge.calls).toHaveLength(0);
    await wrapper.find('.confirm button.primary').trigger('click');
    await flushPromises();
    expect(bridge.calls).toEqual([{ name: 'Secret.WriteNew', payload: { instanceId: 'deepl', secretName: 'apiKey', value: KEY, confirmGrants: true } }]);
    const next = (wrapper.emitted('settings')![0] as [SettingsView])[0];
    await wrapper.setProps({ service: next.services[0] });
    expect(wrapper.find('input').exists()).toBe(false);
    expect(wrapper.html()).not.toContain('SECRET');
    expect(wrapper.text()).toContain(t('secret.saved'));
    expect(wrapper.text()).toContain(t('plan.free'));
  });

  it('cancelling the confirmation sends nothing, and a page change drops the pending value', async () => {
    const { bridge, wrapper } = mountDetails(deepl(false, false));
    await wrapper.find('input').setValue(KEY);
    await wrapper.find('button.primary').trigger('click');
    await wrapper.find('.confirm button:not(.primary)').trigger('click');
    expect(wrapper.find('.confirm').exists()).toBe(false);
    await wrapper.find('button.primary').trigger('click');
    expect(wrapper.find('.confirm').exists()).toBe(true);
    await wrapper.setProps({ clearToken: 1 });
    expect(wrapper.find('.confirm').exists()).toBe(false);
    expect((wrapper.find('input').element as HTMLInputElement).value).toBe('');
    expect(bridge.calls).toHaveLength(0);
  });

  it('a saved but unauthorized key is authorized through Settings.BindAccount after confirmation', async () => {
    const { bridge, wrapper } = mountDetails(deepl(true, false, 'pro'));
    expect(wrapper.text()).toContain(t('grant.notGranted'));
    await wrapper.find('.authorize button').trigger('click');
    expect(wrapper.find('.confirm').text()).toContain('https://api.deepl.com:443');
    await wrapper.find('.confirm button.primary').trigger('click');
    await flushPromises();
    expect(bridge.calls).toEqual([{ name: 'Settings.BindAccount', payload: { instanceId: 'deepl', confirmGrants: true } }]);
  });

  it('lists engines in the configured order and moves them with Settings.ReorderService', async () => {
    const mymemory: ServiceView = { serviceId: 'mymemory/translate', instanceId: 'mymemory', capability: 'translate', page: 'engines', enabled: true, availability: 'Ready', implemented: true, secretNames: [], credentialTargets: [], order: 2 };
    const settings = {
      revision: 1, fileHash: 'h', issues: [], hotkeys: [], accounts: [], services: [mymemory, deepl(true, false)],
      general: { uiLanguage: 'zh-Hans', sourceLanguage: 'en', targetLanguage: 'zh-Hans', defaultExpandedCards: 2, allowClipboardBorrowing: false, closeAction: 'hide', launchAtStartup: false },
      network: { proxyMode: 'system', proxyHost: '', proxyPort: 0, proxyUsername: '', proxyPasswordSaved: false, aiTimeoutSeconds: 30 },
    } satisfies SettingsView;
    const { state } = createStore();
    state.window = { kind: 'Settings', uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features: [] };
    state.settings = settings;
    const bridge = fakeBridge();
    const { default: SettingsWindow } = await import('../src/windows/SettingsWindow.vue');
    const wrapper = mount(SettingsWindow, { props: { bridge: bridge as any, state } });
    await wrapper.findAll('.nav-item')[2].trigger('click');
    expect(wrapper.findAll('.service .title').map((n) => n.text())).toEqual(['DeepL', 'MyMemory']);
    expect(wrapper.find('.service .hint').text()).toContain(t('services.state.NotGranted'));
    await wrapper.find(`button[aria-label="${t('services.moveUp', { name: 'MyMemory' })}"]`).trigger('click');
    expect(bridge.calls).toEqual([{ name: 'Settings.ReorderService', payload: { serviceId: 'mymemory/translate', index: 0 } }]);
  });

  it('shows validation as credential validity and service availability, "not validated" before a real call', async () => {
    let answer: CommandResult = { ok: true, value: { serviceId: 'deepl/translate', credential: 'valid', serviceAvailable: false, error: 'quota' } };
    const { wrapper } = mountDetails(deepl(true, true), fakeBridge(() => answer));
    expect(wrapper.find('.validation').text()).toBe(t('validate.never'));
    await wrapper.find('.status-line button').trigger('click');
    await flushPromises();
    const text = wrapper.find('.validation').text();
    expect(text).toContain(t('validate.credential.valid'));
    expect(text).toContain(t('validate.unavailable', { reason: t('error.quota') }));
    answer = { ok: true, value: { serviceId: 'deepl/translate', credential: 'invalid', serviceAvailable: false, error: 'auth' } };
    await wrapper.find('.status-line button').trigger('click');
    await flushPromises();
    expect(wrapper.find('.validation').text()).toContain(t('validate.credential.invalid'));
    answer = { ok: false, error: 'missing-credential' };
    await wrapper.find('.status-line button').trigger('click');
    await flushPromises();
    expect(wrapper.find('.validation').text()).toBe(t('validate.missing-credential'));
  });
});
