// F07.1: full service list in settings (CFG01, CFG03, UI04) and Esc on the key confirmation bar.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import type { AccountView, CommandResult, ServiceView, SettingsView } from '@protocol/ui';
import SettingsWindow from '../src/windows/SettingsWindow.vue';
import ServiceDetails from '../src/components/ServiceDetails.vue';
import { createStore } from '../src/bridge/store';
import { setLocale, t } from '../src/locales/i18n';

setLocale('zh-Hans');
afterEach(() => { document.body.innerHTML = ''; });

function fakeBridge(answer: (name: string, payload?: any) => CommandResult | Promise<CommandResult> = () => ({ ok: true })) {
  const calls: { name: string; payload?: any }[] = [];
  return { calls, command: vi.fn(async (name: string, payload?: object) => { calls.push({ name, payload }); return answer(name, payload); }) };
}

const svc = (instanceId: string, page: string, order: number, extra: Partial<ServiceView> = {}): ServiceView => ({
  serviceId: `${instanceId}/translate`, instanceId, capability: 'translate', page, enabled: true, availability: 'Ready', implemented: true,
  secretNames: [], order, ...extra,
});
const TENCENT = 'https://tmt.tencentcloudapi.com:443';
const tencent = svc('tencent-translate', 'engines', 1, {
  availability: 'MissingCredential', secretNames: ['secretId', 'secretKey'], accountId: 'tencent-translate',
  credentialTargets: [
    { secret: 'secretId', origin: TENCENT, use: 'signer:tencent-tc3', saved: false, granted: false },
    { secret: 'secretKey', origin: TENCENT, use: 'signer:tencent-tc3', saved: false, granted: false },
  ],
});
const ocrAccount: AccountView = { id: 'tencent-ocr', label: 'tencent-ocr', secrets: [{ name: 'secretId', saved: true }, { name: 'secretKey', saved: true }], usedBy: ['tencent-ocr'] };

// Merged order: mymemory 0, openai 1, deepl 2, claude 3 (AI slots 1 and 3).
const services = (): ServiceView[] => [
  svc('mymemory', 'engines', 0, { usageThisMonth: 1234 }),
  svc('deepl', 'engines', 2, { secretNames: ['apiKey'], availability: 'Ready', credentialTargets: [{ secret: 'apiKey', origin: 'https://api-free.deepl.com:443', use: 'header:Authorization', saved: true, granted: true }], usageThisMonth: 0 }),
  svc('openai', 'ai', 1, { enabled: false, availability: 'Disabled', secretNames: ['apiKey'] }),
  svc('claude', 'ai', 3, { enabled: false, availability: 'Disabled', secretNames: ['apiKey'] }),
  { ...svc('tencent-ocr', 'ocr', -1), serviceId: 'tencent-ocr/ocr', capability: 'ocr', enabled: false, availability: 'Disabled' },
];
const settings = (list: ServiceView[]): SettingsView => ({
  revision: 1, fileHash: 'h', issues: [], hotkeys: [], accounts: [], services: list,
  general: { uiLanguage: 'zh-Hans', sourceLanguage: 'en', targetLanguage: 'zh-Hans', defaultExpandedCards: 2, allowClipboardBorrowing: false, closeAction: 'hide', launchAtStartup: false },
  network: { proxyMode: 'system', proxyHost: '', proxyPort: 0, proxyUsername: '', proxyPasswordSaved: false, aiTimeoutSeconds: 30 },
});
function mountSettings(list: ServiceView[] = services()) {
  const { state } = createStore();
  state.window = { kind: 'Settings', uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features: [] };
  state.settings = settings(list);
  const bridge = fakeBridge(() => ({ ok: true, value: state.settings }));
  const wrapper = mount(SettingsWindow, { props: { bridge: bridge as any, state } });
  const go = async (page: string) => wrapper.findAll('.nav-item').find((n) => n.text() === t(`nav.${page}`))!.trigger('click');
  return { state, bridge, wrapper, go };
}

describe('Esc closes the inline key confirmation bar (F06 carry-in)', () => {
  it('Esc drops a typed key waiting for its address, and sends nothing', async () => {
    const bridge = fakeBridge();
    const wrapper = mount(ServiceDetails, { props: { service: tencent, accounts: [], bridge, clearToken: 0 }, attachTo: document.body });
    await wrapper.find('input').setValue('AKID-typed');
    await wrapper.find('button.primary').trigger('click');
    expect(wrapper.find('.confirm').exists()).toBe(true);
    await wrapper.find('.confirm button.primary').trigger('keydown', { key: 'Escape' });
    expect(wrapper.find('.confirm').exists()).toBe(false);
    expect(bridge.calls).toEqual([]);
  });
});

describe('CFG01: shared account and availability apart from validation', () => {
  it('another saved Tencent account is offered; binding needs the address confirmation before anything is sent', async () => {
    const bridge = fakeBridge();
    const wrapper = mount(ServiceDetails, { props: { service: tencent, accounts: [ocrAccount], bridge, clearToken: 0 }, attachTo: document.body });
    const select = wrapper.find('select');
    expect(select.findAll('option').map((o) => o.attributes('value'))).toEqual(['', 'tencent-ocr']);
    await select.setValue('tencent-ocr');
    const bar = wrapper.find('.confirm');
    expect(bar.text()).toContain(t('grant.target', { origin: TENCENT }));
    expect(bridge.calls).toEqual([]);
    // Esc cancels the binding too.
    await bar.trigger('keydown', { key: 'Escape' });
    expect(wrapper.find('.confirm').exists()).toBe(false);
    await select.setValue('tencent-ocr');
    await wrapper.find('.confirm button.primary').trigger('click');
    await flushPromises();
    expect(bridge.calls).toEqual([{ name: 'Settings.BindAccount', payload: { instanceId: 'tencent-translate', accountId: 'tencent-ocr', confirmGrants: true } }]);
  });

  it('accounts missing a secret, and the current account, are not offered', () => {
    const partial: AccountView = { ...ocrAccount, id: 'half', secrets: [{ name: 'secretId', saved: true }, { name: 'secretKey', saved: false }] };
    const own: AccountView = { ...ocrAccount, id: 'tencent-translate' };
    const wrapper = mount(ServiceDetails, { props: { service: tencent, accounts: [partial, own], bridge: fakeBridge(), clearToken: 0 } });
    expect(wrapper.find('select').exists()).toBe(false);
  });

  it('a Ready service with saved, granted credentials still reads "not validated" until a real call', async () => {
    const { wrapper, go } = mountSettings();
    await go('engines');
    const deepl = wrapper.findAll('.service').find((n) => n.text().includes('DeepL'))!;
    expect(deepl.text()).toContain(t('services.state.Ready'));
    await deepl.find(`button[aria-label="${t('services.details', { name: 'DeepL' })}"]`).trigger('click');
    expect(wrapper.find('.validation').text()).toBe(t('validate.never'));
    expect(wrapper.text()).not.toContain(t('validate.credential.valid'));
  });

  it('local monthly usage and MyMemory limits are written apart', async () => {
    const { wrapper, go } = mountSettings();
    await go('engines');
    const mymemory = wrapper.findAll('.service').find((n) => n.text().includes('MyMemory'))!;
    expect(mymemory.text()).toContain(t('services.usage', { n: '1234' }));
    expect(mymemory.text()).toContain(t('services.mymemoryLimits'));
  });
});

describe('CFG03: category pages and the merged order', () => {
  it('the engines and AI pages each list only their category, in merged order, and move within their own slots', async () => {
    const { wrapper, go, bridge } = mountSettings();
    await go('engines');
    const engineRows = wrapper.findAll('.service').map((n) => n.text());
    expect(engineRows).toHaveLength(2);
    expect(engineRows[0]).toContain('MyMemory');
    expect(engineRows[1]).toContain('DeepL');
    await wrapper.find(`button[aria-label="${t('services.moveUp', { name: 'DeepL' })}"]`).trigger('click');
    await flushPromises();
    expect(bridge.calls.at(-1)).toEqual({ name: 'Settings.ReorderService', payload: { serviceId: 'deepl/translate', index: 0 } });

    await go('ai');
    const aiRows = wrapper.findAll('.service').map((n) => n.text());
    expect(aiRows).toHaveLength(2);
    expect(aiRows[0]).toContain('OpenAI');
    expect(aiRows[1]).toContain('Claude');
    await wrapper.find(`button[aria-label="${t('services.moveUp', { name: 'Claude' })}"]`).trigger('click');
    await flushPromises();
    expect(bridge.calls.at(-1)).toEqual({ name: 'Settings.ReorderService', payload: { serviceId: 'claude/translate', index: 0 } });
  });

  it('the General page shows both categories in the merged order and moves across them', async () => {
    const { wrapper, bridge } = mountSettings();
    expect(wrapper.find('.result-order').exists()).toBe(false);
    await wrapper.find(`button[aria-label="${t('general.resultOrderEdit')}"]`).trigger('click');
    const rows = wrapper.findAll('.result-order li');
    expect(rows.map((r) => r.attributes('data-service'))).toEqual(['mymemory/translate', 'openai/translate', 'deepl/translate', 'claude/translate']);
    expect(rows[1].text()).toContain(t('services.ai'));
    expect(rows[2].text()).toContain(t('services.engines'));
    await rows[1].find(`button[aria-label="${t('services.moveDown', { name: 'OpenAI' })}"]`).trigger('click');
    await flushPromises();
    expect(bridge.calls.at(-1)).toEqual({ name: 'Settings.ReorderService', payload: { serviceId: 'openai/translate', index: 2, merged: true } });
    expect(rows[0].find(`button[aria-label="${t('services.moveUp', { name: 'MyMemory' })}"]`).attributes('disabled')).toBeDefined();
  });
});

describe('UI04: a remote service error stays on that service', () => {
  it('a network failure while validating one service does not mark another, and a local service stays available', async () => {
    const remote = { ...tencent, availability: 'Ready', credentialTargets: tencent.credentialTargets!.map((c) => ({ ...c, saved: true, granted: true })) };
    const local = svc('ollama', 'ai', 4, { credentialTargets: [] });
    const bridge = fakeBridge((name, payload) => (name === 'Settings.ValidateProvider' && payload.serviceId === remote.serviceId
      ? { ok: true, value: { serviceId: remote.serviceId, credential: 'unknown', serviceAvailable: false, error: 'network' } } : { ok: true }));
    const a = mount(ServiceDetails, { props: { service: remote, accounts: [], bridge, clearToken: 0 } });
    const b = mount(ServiceDetails, { props: { service: local, accounts: [], bridge, clearToken: 0 } });
    await a.find('.status-line button').trigger('click');
    await flushPromises();
    expect(a.find('.validation').text()).toContain(t('error.network'));
    expect(a.find('.validation').classes()).toContain('error-text');
    expect(b.find('.validation').text()).toBe(t('validate.never'));
    expect(b.find('.validation').classes()).not.toContain('error-text');
  });
});
