// F11.3: the OCR result window (Ocr artboard 02–04: recognizing, recognized, no text / failed, recapture, copy) and the
// SetOcr page (service readiness, default service, auto-translate, keep screenshots and retention, the screenshot hotkey).
import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import type { CardSnapshot, CommandResult, HotkeyView, OcrSettingsView, OcrView, ServiceView, SettingsView, TranslationSnapshot, UiSnapshot, WindowView } from '@protocol/ui';
import OcrWindow from '../src/windows/OcrWindow.vue';
import SettingsWindow from '../src/windows/SettingsWindow.vue';
import App from '../src/App.vue';
import { createStore } from '../src/bridge/store';
import { setLocale, t } from '../src/locales/i18n';

setLocale('zh-Hans');

function fakeBridge(answer: (name: string, payload?: any) => CommandResult = () => ({ ok: true })) {
  const calls: { name: string; payload?: any }[] = [];
  return { calls, command: vi.fn(async (name: string, payload?: object) => { calls.push({ name, payload }); return answer(name, payload); }) };
}
const view = (kind: WindowView['kind']): WindowView => ({ kind, uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features: ['ocr'] });
const card = (serviceId: string, state: CardSnapshot['state'], text = ''): CardSnapshot => ({ serviceId, displayName: serviceId, state, collapsed: false, text });
const translation = (sourceText: string, cards: CardSnapshot[], generation = 1): TranslationSnapshot => ({ revision: 3, generation, sourceText, from: 'en', to: 'zh-Hans', cards });
const PNG = 'data:image/png;base64,iVBORw0KGgo=';
const ocr = (over: Partial<OcrView> = {}): OcrView => ({
  id: 1, phase: 'recognized', serviceId: 'tencent-ocr', text: 'Rendering is the art of failing better.\n\\frac{a}{b}',
  blocks: [{ text: 'Rendering is the art of failing better.', kind: 'text' }, { text: '\\frac{a}{b}', kind: 'formula' }],
  preview: PNG, width: 832, height: 264, translated: true, autoTranslate: true, hotkey: 'Alt+S', elapsedMs: 800, ...over,
});

function mountOcr(snapshot: Omit<UiSnapshot, 'window'>) {
  const { state, inbound } = createStore();
  inbound.onSnapshot({ window: view('Ocr'), ...snapshot });
  const bridge = fakeBridge();
  const wrapper = mount(OcrWindow, { props: { bridge: bridge as any, state }, attachTo: document.body });
  return { state, inbound, bridge, wrapper };
}

afterEach(() => { document.body.innerHTML = ''; });

describe('OCR window', () => {
  it('recognizing: two skeleton lines and the service name, no spinner and no editable text yet', () => {
    const { wrapper } = mountOcr({ ocr: ocr({ phase: 'recognizing', text: undefined, blocks: [], elapsedMs: undefined, translated: false }) });
    expect(wrapper.find('.titlebar .title').text()).toBe('Su-Su · OCR');
    expect(wrapper.findAll('.skeleton .line')).toHaveLength(2);
    expect(wrapper.find('.label-row .tag').text()).toBe(`${t('ocr.recognizing')} · 腾讯 OCR`);
    expect(wrapper.find('.source').attributes('aria-busy')).toBe('true');
    expect(wrapper.find('textarea').exists()).toBe(false);
    expect(wrapper.find('.preview img').attributes('src')).toBe(PNG);
    expect(wrapper.find('.preview figcaption').text()).toBe('832 × 264');
  });

  it('recognized: editable text, formulas as LaTeX source, meta line, cards and the status bar hint', async () => {
    const { wrapper } = mountOcr({ ocr: ocr(), translation: translation('Rendering is the art of failing better.\n\\frac{a}{b}', [card('deepl', 'Ready', '翻译是一门手艺。'), card('claude', 'CollapsedIdle')]) });
    await flushPromises();
    expect((wrapper.find('#ocr-source-text').element as HTMLTextAreaElement).value).toBe('Rendering is the art of failing better.\n\\frac{a}{b}');
    expect(wrapper.find('.label-row').text()).toContain(t('ocr.editHint'));
    expect(wrapper.find('.formulas code').text()).toBe('\\frac{a}{b}');
    expect(wrapper.find('.meta').text()).toBe(t('ocr.meta', { service: '腾讯 OCR', seconds: '0.8', n: 51 }));
    expect(wrapper.text()).toContain('翻译是一门手艺。');
    expect(wrapper.find('.statusbar').text()).toBe('Alt+S 重新截图 · Esc 关闭');
  });

  it('copy, copy LaTeX, recapture and translate (the edited text, also with Ctrl+Enter but not while composing)', async () => {
    const { wrapper, bridge } = mountOcr({ ocr: ocr({ translated: false, autoTranslate: false }) });
    await wrapper.find(`.foot button[aria-label="${t('ocr.copy')}"]`).trigger('click');
    expect(bridge.calls.at(-1)).toEqual({ name: 'Window.CopyText', payload: { text: 'Rendering is the art of failing better.\n\\frac{a}{b}' } });
    await wrapper.find(`.formulas button[aria-label="${t('ocr.copyFormula')}"]`).trigger('click');
    expect(bridge.calls.at(-1)).toEqual({ name: 'Window.CopyText', payload: { text: '\\frac{a}{b}' } });
    await wrapper.find(`.foot button[aria-label="${t('ocr.recapture')}"]`).trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Capture.BeginCapture');
    const area = wrapper.find('#ocr-source-text');
    await area.setValue('Rendering is the art of failing better');
    await wrapper.find('button.translate').trigger('click');
    expect(bridge.calls.at(-1)).toEqual({ name: 'Translation.SubmitText', payload: { text: 'Rendering is the art of failing better' } });
    const count = bridge.calls.length;
    await area.trigger('keydown', { key: 'Enter', ctrlKey: true, isComposing: true });
    expect(bridge.calls.length).toBe(count);
    await area.trigger('keydown', { key: 'Enter', ctrlKey: true });
    expect(bridge.calls.at(-1)!.name).toBe('Translation.SubmitText');
  });

  it('a new capture clears the old text while recognizing, then shows the new result', async () => {
    const { wrapper, inbound } = mountOcr({ ocr: ocr() });
    inbound.onEvent('ocr', ocr({ id: 2, phase: 'recognizing', text: undefined, blocks: [] }));
    await flushPromises();
    expect(wrapper.find('textarea').exists()).toBe(false);
    inbound.onEvent('ocr', ocr({ id: 2, phase: 'recognized', text: 'second', blocks: [{ text: 'second', kind: 'text' }] }));
    await flushPromises();
    expect((wrapper.find('textarea').element as HTMLTextAreaElement).value).toBe('second');
    expect(wrapper.find('.formulas').exists()).toBe(false);
  });

  it('no text: the card keeps its shape and offers "重新截图"', async () => {
    const { wrapper, bridge } = mountOcr({ ocr: ocr({ phase: 'noText', text: undefined, blocks: [] }) });
    expect(wrapper.find('.failure-title').text()).toBe('未识别到文字');
    expect(wrapper.find('.failure').text()).toContain('所选区域没有可识别的文字');
    expect(wrapper.find('.failure').attributes('role')).toBe('alert');
    expect(wrapper.find('.failure .link').exists()).toBe(false);
    await wrapper.find('.failure .recapture').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Capture.BeginCapture');
    expect(wrapper.find('.foot').exists()).toBe(false);
  });

  it('a vendor failure shows its class with host text; credentials problems link to Settings', async () => {
    const { wrapper, bridge } = mountOcr({ ocr: ocr({ phase: 'failed', text: undefined, blocks: [], errorKind: 'auth' }) });
    expect(wrapper.find('.failure-title').text()).toBe(t('ocr.failed'));
    expect(wrapper.find('.failure').text()).toContain(`${t('error.auth')} · 腾讯 OCR`);
    await wrapper.find('.failure .link').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Window.OpenSettings');
    const network = mountOcr({ ocr: ocr({ phase: 'failed', text: undefined, blocks: [], errorKind: 'network' }) });
    expect(network.wrapper.find('.failure').text()).toContain(t('error.network'));
    expect(network.wrapper.find('.failure .link').exists()).toBe(false);
  });

  it('no usable service: says so and links to Settings', async () => {
    const { wrapper, bridge } = mountOcr({ ocr: ocr({ phase: 'unavailable', serviceId: undefined, text: undefined, blocks: [] }) });
    expect(wrapper.find('.failure-title').text()).toBe(t('ocr.unavailable'));
    await wrapper.find('.failure .link').trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Window.OpenSettings');
  });

  it('only a PNG data URL is shown as the preview; a path or file URL never becomes an image', () => {
    for (const preview of ['file:///C:/Users/x/AppData/Local/Temp/a.png', 'C:\\temp\\a.png', 'https://example.com/a.png', 'data:image/svg+xml;base64,PHN2Zz4=']) {
      const { wrapper } = mountOcr({ ocr: ocr({ preview }) });
      expect(wrapper.find('.preview').exists()).toBe(false);
      expect(wrapper.html()).not.toContain(preview);
    }
  });

  it('a kept copy that could not be written is a notice; no chord means only "Esc 关闭"', () => {
    const { wrapper } = mountOcr({ ocr: ocr({ notice: 'ocr.keep.diskFull', hotkey: '' }) });
    expect(wrapper.find('.notice').text()).toBe(t('ocr.keep.diskFull'));
    expect(wrapper.find('.statusbar').text()).toBe(t('ocr.statusHintNoChord'));
  });

  it('title bar: settings, minimize and close (no pin or maximize)', async () => {
    const { wrapper, bridge } = mountOcr({ ocr: ocr() });
    const labels = wrapper.findAll('.titlebar button').map((b) => b.attributes('aria-label'));
    expect(labels).toEqual([t('window.settings'), t('window.minimize'), t('window.close')]);
    await wrapper.find(`.titlebar button[aria-label="${t('window.close')}"]`).trigger('click');
    expect(bridge.calls.at(-1)!.name).toBe('Window.Close');
  });

  it('the App routes the Ocr window kind to the OCR page', async () => {
    const { state, inbound } = createStore();
    inbound.onSnapshot({ window: view('Ocr'), ocr: ocr() });
    const wrapper = mount(App, { props: { kind: 'Ocr', bridge: fakeBridge() as any, state }, attachTo: document.body });
    for (let i = 0; i < 20 && !wrapper.find('#ocr-source-text').exists(); i++) { await flushPromises(); await new Promise((r) => setTimeout(r, 5)); }
    expect(wrapper.find('#ocr-source-text').exists()).toBe(true);
  });
});

describe('SetOcr', () => {
  const ocrService = (instanceId: string, enabled: boolean, availability: string): ServiceView => ({
    serviceId: `${instanceId}/ocr`, instanceId, capability: 'ocr', page: 'ocr', enabled, availability, implemented: true,
    secretNames: instanceId === 'tencent-ocr' ? ['secretId', 'secretKey'] : ['apiKey'], order: -1,
    credentialTargets: instanceId === 'tencent-ocr'
      ? [{ secret: 'secretId', origin: 'https://ocr.tencentcloudapi.com:443', use: 'signer:tencent-tc3', saved: true, granted: false }, { secret: 'secretKey', origin: 'https://ocr.tencentcloudapi.com:443', use: 'signer:tencent-tc3', saved: true, granted: false }]
      : [{ secret: 'apiKey', origin: 'https://server.simpletex.cn:443', use: 'header:token', saved: false, granted: false }],
  });
  const ocrSettings = (over: Partial<OcrSettingsView> = {}): OcrSettingsView => ({
    service: 'tencent-ocr', autoTranslate: true, keepScreenshots: false, retentionDays: 7, minRetentionDays: 1, maxRetentionDays: 365, hotkey: 'Alt+S',
    ready: false, reasonKey: 'feature.noService.ocr',
    choices: [{ instanceId: 'tencent-ocr', serviceId: 'tencent-ocr/ocr', enabled: true, availability: 'MissingCredential', usable: false }, { instanceId: 'simple-latex', serviceId: 'simple-latex/ocr', enabled: false, availability: 'Disabled', usable: false }],
    ...over,
  });
  const settings = (ocr: OcrSettingsView, hotkeys: HotkeyView[] = []): SettingsView => ({
    revision: 4, fileHash: 'h', issues: [], hotkeys, accounts: [], services: [ocrService('tencent-ocr', true, 'MissingCredential'), ocrService('simple-latex', false, 'Disabled')], ocr,
    general: { uiLanguage: 'zh-Hans', sourceLanguage: 'en', targetLanguage: 'zh-Hans', defaultExpandedCards: 2, allowClipboardBorrowing: false, closeAction: 'hide', launchAtStartup: false },
    network: { proxyMode: 'system', proxyHost: '', proxyPort: 0, proxyUsername: '', proxyPasswordSaved: false, aiTimeoutSeconds: 30 },
  });
  async function mountSettings(view: SettingsView, answer?: (name: string, payload?: any) => CommandResult, page = 'nav.ocr') {
    const { state } = createStore();
    state.window = { kind: 'Settings', uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features: [] };
    state.settings = view;
    const bridge = fakeBridge(answer);
    const wrapper = mount(SettingsWindow, { props: { bridge: bridge as any, state } });
    await wrapper.findAll('.nav-item').find((n) => n.text() === t(page))!.trigger('click');
    return { wrapper, bridge, state };
  }

  it('says why OCR is unavailable, marks the default service and lists both services with their credential state', async () => {
    const { wrapper } = await mountSettings(settings(ocrSettings()));
    expect(wrapper.find('[data-ocr-status]').text()).toBe(t('ocr.settings.notReady', { reason: t('feature.noService.ocr') }));
    expect(wrapper.find('[data-ocr-status]').classes()).toContain('error-text');
    expect(wrapper.text()).toContain(t('ocr.settings.hint'));
    const rows = wrapper.findAll('.service');
    expect(rows).toHaveLength(2);
    expect(rows[0].find('[data-ocr-default]').text()).toBe('默认');
    expect(rows[0].text()).toContain('腾讯 OCR');
    expect(rows[0].find('.hint').text()).toContain(t('services.state.NotGranted'));
    expect(rows[1].find('[data-ocr-make-default]').text()).toBe(t('ocr.settings.useDefault'));
    expect(wrapper.find('[data-ocr-hotkey]').text()).toBe('Alt + S');
  });

  it('a ready service says OCR is available', async () => {
    const { wrapper } = await mountSettings(settings(ocrSettings({ ready: true, reasonKey: undefined })));
    expect(wrapper.find('[data-ocr-status]').text()).toBe(t('ocr.settings.ready'));
  });

  it('make default, auto-translate, keep screenshots and retention each save through Settings.SaveOcr', async () => {
    let current = ocrSettings();
    const answer = (name: string, payload?: any): CommandResult => {
      if (name !== 'Settings.SaveOcr') return { ok: true };
      current = { ...current, service: payload.service, autoTranslate: payload.autoTranslate, keepScreenshots: payload.keepScreenshots, retentionDays: payload.retentionDays };
      return { ok: true, value: settings(current) };
    };
    const { wrapper, bridge } = await mountSettings(settings(current), answer);
    await wrapper.find('[data-ocr-make-default="simple-latex"]').trigger('click');
    await flushPromises();
    expect(bridge.calls.at(-1)).toEqual({ name: 'Settings.SaveOcr', payload: { expectedRevision: 4, expectedFileHash: 'h', service: 'simple-latex', autoTranslate: true, keepScreenshots: false, retentionDays: 7 } });
    expect(wrapper.findAll('.service')[1].find('[data-ocr-default]').exists()).toBe(true);
    await wrapper.find('[data-ocr-auto]').trigger('click');
    await flushPromises();
    expect(bridge.calls.at(-1)!.payload).toMatchObject({ autoTranslate: false, service: 'simple-latex' });
    expect(wrapper.find('[data-ocr-retention]').exists()).toBe(false); // retention appears only when keeping screenshots
    await wrapper.find('[data-ocr-keep]').trigger('click');
    await flushPromises();
    expect(bridge.calls.at(-1)!.payload).toMatchObject({ keepScreenshots: true });
    expect(wrapper.text()).toContain('保留 7 天');
    await wrapper.find(`[data-ocr-retention] button[aria-label="${t('stepper.increase')}"]`).trigger('click');
    await flushPromises();
    expect(bridge.calls.at(-1)!.payload).toMatchObject({ retentionDays: 8, keepScreenshots: true });
  });

  it('a refused save shows an error line', async () => {
    const { wrapper } = await mountSettings(settings(ocrSettings()), (name) => (name === 'Settings.SaveOcr' ? { ok: false, error: 'retention' } : { ok: true }));
    await wrapper.find('[data-ocr-auto]').trigger('click');
    await flushPromises();
    expect(wrapper.find('.ocr-capture .error-text').text()).toBe(t('ocr.settings.saveFailed'));
  });

  it('SetHotkeys greys the OCR chord with the service reason, not "not available yet"', async () => {
    const view = settings(ocrSettings(), [{ action: 'ocrTranslate', chord: 'Alt+S', state: 'unavailable', reasonKey: 'feature.noService.ocr' }, { action: 'voiceTranslate', chord: 'Alt+V', state: 'unavailable', reasonKey: 'feature.inDevelopment' }]);
    const { wrapper } = await mountSettings(view, undefined, 'nav.hotkeys');
    const row = (action: string) => wrapper.findAll('.row').find((r) => r.find('.title').text() === t(`hotkeys.${action}`))!;
    expect(row('ocrTranslate').find('.hint').text()).toBe(t('feature.noService.ocr'));
    expect(row('voiceTranslate').find('.hint').text()).toBe(t('hotkeys.state.unavailable'));
  });
});
