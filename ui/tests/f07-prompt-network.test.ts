// F07.3: SetPrompt (levels, templates, scope, host-rendered preview; CFG04) and the SetNetwork test (CFG05).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import type { CommandResult, NetworkTestView, PromptView, SettingsView } from '@protocol/ui';
import PromptSettings from '../src/components/PromptSettings.vue';
import SettingsWindow from '../src/windows/SettingsWindow.vue';
import { createStore } from '../src/bridge/store';
import { setLocale, t } from '../src/locales/i18n';

setLocale('zh-Hans');
beforeEach(() => { vi.useFakeTimers(); });
afterEach(() => { vi.useRealTimers(); document.body.innerHTML = ''; });

type Call = { name: string; payload?: any };
function fakeBridge(answer: (name: string, payload?: any) => CommandResult | Promise<CommandResult> = () => ({ ok: true })) {
  const calls: Call[] = [];
  return { calls, command: vi.fn(async (name: string, payload?: object) => { calls.push({ name, payload }); return answer(name, payload); }) };
}
const DEFAULT = '把下面的原文从 {{from}} 译成 {{to}}，按 {{level}} 书写。\n\n原文：\n{{text}}';
const prompt = (extra: Partial<PromptView> = {}): PromptView => ({
  level: '', profile: '', scope: ['openai', 'glm', 'gemini', 'claude', 'ollama'],
  levels: ['literal', 'free', 'ielts-6.5', 'ielts-7.0', 'toefl-100', 'cet-6', 'academic'], aiServices: ['openai', 'glm', 'gemini', 'claude', 'ollama'],
  profiles: [], defaultTemplate: DEFAULT, variables: ['text', 'from', 'to', 'level'], ...extra,
});
const settings = (p: PromptView = prompt()): SettingsView => ({
  revision: 5, fileHash: 'h5', issues: [], hotkeys: [], accounts: [], services: [],
  general: { uiLanguage: 'zh-Hans', sourceLanguage: 'en', targetLanguage: 'zh-Hans', defaultExpandedCards: 2, allowClipboardBorrowing: false, closeAction: 'hide', launchAtStartup: false },
  network: { proxyMode: 'system', proxyHost: '', proxyPort: 0, proxyUsername: '', proxyPasswordSaved: false, aiTimeoutSeconds: 30 },
  prompt: p,
});
const previews = (calls: Call[]) => calls.filter((c) => c.name === 'Settings.PreviewPrompt').map((c) => c.payload);
const settle = async () => { await vi.advanceTimersByTimeAsync(250); await flushPromises(); };

describe('SetPrompt (CFG04)', () => {
  it('lists the seven built-in levels and previews the chosen one through the host', async () => {
    const bridge = fakeBridge((name, payload) => name === 'Settings.PreviewPrompt'
      ? { ok: true, value: { rendered: `rendered at ${payload.level || '-'}`, unknown: [] } } : { ok: true });
    const wrapper = mount(PromptSettings, { props: { settings: settings(), bridge } });
    await settle();
    expect(wrapper.findAll('[data-level]').map((b) => b.text())).toEqual(['直译', '意译', '雅思 6.5', '雅思 7.0', '托福 100', 'CET-6', '学术论文']);
    expect(previews(bridge.calls).at(-1)).toMatchObject({ template: '', level: '', from: 'en', to: 'zh-Hans' }); // "" = the built-in default
    await wrapper.find('[data-level="ielts-7.0"]').trigger('click');
    await settle();
    expect(previews(bridge.calls).at(-1).level).toBe('ielts-7.0');
    expect(wrapper.find('[data-preview]').text()).toBe('rendered at ielts-7.0');
    expect(wrapper.find('[data-level="ielts-7.0"]').attributes('aria-pressed')).toBe('true');
  });

  it('shows the rendered text literally, lists unknown variables and a template problem', async () => {
    const bridge = fakeBridge(() => ({ ok: true, value: { rendered: 'Say {{to}} {{secret.apiKey}} <b>x</b>', unknown: ['tone'], problem: 'text-repeated' } }));
    const wrapper = mount(PromptSettings, { props: { settings: settings(), bridge } });
    await settle();
    expect(wrapper.find('[data-preview]').text()).toBe('Say {{to}} {{secret.apiKey}} <b>x</b>');
    expect(wrapper.find('[data-preview] b').exists()).toBe(false); // text, never HTML
    expect(wrapper.find('[data-unknown]').text()).toContain('{{tone}}');
    expect(wrapper.find('[data-problem]').text()).toBe(t('prompt.issue.text-repeated'));
  });

  it('a slower, older preview answer never replaces a newer one', async () => {
    let release!: (r: CommandResult) => void;
    let first = true;
    const bridge = fakeBridge((name, payload) => {
      if (name !== 'Settings.PreviewPrompt') return { ok: true };
      if (first) { first = false; return new Promise<CommandResult>((resolve) => { release = resolve; }); }
      return { ok: true, value: { rendered: `new ${payload.level}`, unknown: [] } };
    });
    const wrapper = mount(PromptSettings, { props: { settings: settings(), bridge } });
    await settle(); // first request pending
    await wrapper.find('[data-level="cet-6"]').trigger('click');
    await settle();
    release({ ok: true, value: { rendered: 'old', unknown: [] } });
    await flushPromises();
    expect(wrapper.find('[data-preview]').text()).toBe('new cet-6');
  });

  it('editing the default starts a custom template; variables insert as {{name}}; save sends the whole page', async () => {
    const bridge = fakeBridge((name) => name === 'Settings.PreviewPrompt' ? { ok: true, value: { rendered: '', unknown: [] } } : { ok: true });
    const wrapper = mount(PromptSettings, { props: { settings: settings(), bridge } });
    await settle();
    const area = wrapper.find('textarea');
    expect((area.element as HTMLTextAreaElement).value).toBe(DEFAULT);
    await area.setValue('Translate to {{to}}: ');
    (area.element as HTMLTextAreaElement).setSelectionRange(21, 21);
    await wrapper.find('[data-var="text"]').trigger('click');
    expect((area.element as HTMLTextAreaElement).value).toBe('Translate to {{to}}: {{text}}');
    await wrapper.find('[data-scope="glm"]').setValue(false);
    await wrapper.find('[data-level="academic"]').trigger('click');
    await wrapper.find('[data-save-prompt]').trigger('click');
    await flushPromises();
    const save = bridge.calls.find((c) => c.name === 'Settings.SavePrompt')!.payload;
    expect(save).toEqual({
      expectedRevision: 5, expectedFileHash: 'h5', level: 'academic', profile: 'custom-1', scope: ['openai', 'gemini', 'claude', 'ollama'],
      profiles: [{ id: 'custom-1', name: '自定义', template: 'Translate to {{to}}: {{text}}' }],
    });
    expect(wrapper.text()).toContain(t('prompt.saved'));
  });

  it('restore default selects the built-in template; an invalid save shows the reason', async () => {
    const bridge = fakeBridge((name) => name === 'Settings.SavePrompt'
      ? { ok: false, error: 'invalid', value: [{ path: 'prompts.mine.template', code: 'too-long', message: '', line: 0 }] }
      : { ok: true, value: { rendered: '', unknown: [] } });
    const wrapper = mount(PromptSettings, { props: { settings: settings(prompt({ profile: 'mine', profiles: [{ id: 'mine', name: 'Mine', template: 'X {{text}}' }] })), bridge } });
    await settle();
    expect((wrapper.find('textarea').element as HTMLTextAreaElement).value).toBe('X {{text}}');
    await wrapper.find('textarea').setValue('Y {{text}}');
    await wrapper.find('[data-save-prompt]').trigger('click');
    await flushPromises();
    expect(wrapper.find('.issues').text()).toContain(t('prompt.issue.too-long'));
    await wrapper.findAll('button').find((b) => b.text() === t('prompt.restore'))!.trigger('click');
    expect((wrapper.find('textarea').element as HTMLTextAreaElement).value).toBe(DEFAULT);
    expect((wrapper.find('#p-profile').element as HTMLSelectElement).value).toBe('');
  });
});

describe('SetNetwork test (CFG05)', () => {
  function mountNetwork(answer: (name: string, payload?: any) => CommandResult) {
    const { state } = createStore();
    state.window = { kind: 'Settings', uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features: [] };
    state.settings = settings();
    const bridge = fakeBridge(answer);
    const wrapper = mount(SettingsWindow, { props: { bridge: bridge as any, state } });
    return { bridge, wrapper };
  }
  const go = async (wrapper: any, page: string) => wrapper.findAll('.nav-item').find((n: any) => n.text() === t(`nav.${page}`))!.trigger('click');

  it('tests the proxy as edited and shows one line per path with its own error', async () => {
    const result: NetworkTestView = {
      testedAt: '2026-09-25T14:02:00+08:00',
      paths: [
        { origin: 'https://api.openai.com:443', services: ['openai/translate'], route: 'proxy', ok: false, error: 'timeout', elapsedMs: 10000 },
        { origin: 'http://127.0.0.1:11434', services: ['openai/translate'], route: 'local', ok: true, status: 404, elapsedMs: 3 },
      ],
    };
    const { bridge, wrapper } = mountNetwork((name) => name === 'Settings.TestNetwork' ? { ok: true, value: result } : { ok: true });
    await go(wrapper, 'network');
    await wrapper.find('select').setValue('http');
    await wrapper.find('#n-host').setValue('127.0.0.1');
    await wrapper.find('#n-port').setValue('7890');
    await wrapper.find('[data-test-network]').trigger('click');
    await flushPromises();
    const call = bridge.calls.find((c) => c.name === 'Settings.TestNetwork')!;
    expect(call.payload.network).toMatchObject({ proxyMode: 'http', proxyHost: '127.0.0.1', proxyPort: 7890 });
    const rows = wrapper.findAll('.paths li');
    expect(rows).toHaveLength(2);
    expect(rows[0].classes()).toContain('failed');
    expect(rows[0].text()).toContain(t('error.timeout'));
    expect(rows[0].text()).toContain(t('network.route.proxy'));
    expect(rows[1].classes()).toContain('ok');
    expect(rows[1].text()).toContain(t('network.route.local'));
    expect(bridge.calls.some((c) => c.name === 'Settings.Save')).toBe(false); // testing saves nothing
  });

  it('a manual proxy without an address is reported, not tested', async () => {
    const { wrapper } = mountNetwork((name) => name === 'Settings.TestNetwork' ? { ok: false, error: 'proxy-address' } : { ok: true });
    await go(wrapper, 'network');
    await wrapper.find('[data-test-network]').trigger('click');
    await flushPromises();
    expect(wrapper.find('[role="alert"]').text()).toBe(t('network.test.address'));
  });

  it('the prompt page is in the navigation after AI models', async () => {
    const { wrapper } = mountNetwork(() => ({ ok: true, value: { rendered: '', unknown: [] } }));
    const names = wrapper.findAll('.nav-item').map((n) => n.text());
    expect(names.indexOf(t('nav.prompt'))).toBe(names.indexOf(t('nav.ai')) + 1);
    await go(wrapper, 'prompt');
    expect(wrapper.find('textarea').exists()).toBe(true);
  });
});
