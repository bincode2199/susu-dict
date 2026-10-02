// F17.2: the About page. Fake bridge only (the host side is covered by F17AboutTests): facts and the log location without a path, opening the folder and
// the diagnostics export send no path, the result shows a file name only, errors read in both languages, the license list, and every data-clean entry says
// what it deletes and keeps, needs a second click, and sends the kind with confirm = true only after it (never on the first click).
import { afterEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import type { AboutView, CommandResult, SettingsView } from '@protocol/ui';
import SettingsWindow from '../src/windows/SettingsWindow.vue';
import { createStore } from '../src/bridge/store';
import { setLocale, t } from '../src/locales/i18n';

setLocale('zh-Hans');
afterEach(() => { document.body.innerHTML = ''; setLocale('zh-Hans'); });

function fakeBridge(answer: (name: string, payload?: any) => CommandResult = () => ({ ok: true })) {
  const calls: { name: string; payload?: any }[] = [];
  return { calls, command: vi.fn(async (name: string, payload?: object) => { calls.push({ name, payload }); return answer(name, payload); }) };
}
const kinds = ['caches', 'logs', 'screenshots', 'favorites', 'settings', 'accounts'];
const about = (over: Partial<AboutView> = {}): AboutView => ({
  version: '1.2.3.0', build: '1.2.3', os: 'Windows 10.0.26200', runtime: '.NET 10.0.0', logLocation: '%LOCALAPPDATA%\\Su-Su\\logs', logFiles: 3, logBytes: 2048,
  canOpenLogs: true, canExport: true, diagnostics: undefined,
  licenses: [{ name: 'QuickJS-NG', version: 'v0.17.0', license: 'MIT', kind: 'native', ships: 'susu_quickjs.dll' }, { name: 'YamlDotNet', version: '18.1.0', license: 'MIT', kind: 'nuget', ships: 'compiled in' }],
  data: kinds.map((kind) => ({ kind, count: kind === 'settings' ? 0 : 4, bytes: kind === 'caches' ? 4096 : 0, available: true })), cleaned: undefined, ...over,
});
const settings = (a: AboutView | undefined): SettingsView => ({
  revision: 4, fileHash: 'h', issues: [], hotkeys: [], accounts: [], services: [], about: a,
  general: { uiLanguage: 'zh-Hans', sourceLanguage: 'en', targetLanguage: 'zh-Hans', defaultExpandedCards: 2, allowClipboardBorrowing: false, closeAction: 'hide', launchAtStartup: false },
  network: { proxyMode: 'system', proxyHost: '', proxyPort: 0, proxyUsername: '', proxyPasswordSaved: false, aiTimeoutSeconds: 30 },
});
const withView = (a: AboutView) => ({ ok: true, value: settings(a) }) as CommandResult;

async function mountPage(a: AboutView | undefined, answer?: (name: string, payload?: any) => CommandResult) {
  const { state } = createStore();
  state.window = { kind: 'Settings', uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features: [] };
  state.settings = settings(a);
  const bridge = fakeBridge(answer);
  const wrapper = mount(SettingsWindow, { props: { bridge: bridge as any, state }, attachTo: document.body });
  await wrapper.findAll('.nav-item').find((n) => n.text() === t('nav.about'))!.trigger('click');
  return { wrapper, bridge, state };
}

describe('About page', () => {
  it('shows version, build, platform, the log location in environment form and the log usage', async () => {
    const { wrapper } = await mountPage(about());
    expect(wrapper.find('[data-about-version]').text()).toBe('1.2.3.0');
    expect(wrapper.find('[data-about-build]').text()).toBe('1.2.3');
    expect(wrapper.find('[data-about-platform]').text()).toContain('Windows 10.0.26200');
    expect(wrapper.find('[data-about-log-location]').text()).toBe('%LOCALAPPDATA%\\Su-Su\\logs');
    expect(wrapper.text()).toContain(t('about.logs.hint', { files: 3, size: t('about.size.kb', { n: '2.0' }) }));
  });

  it('opens the log folder without a path, and says so when it cannot', async () => {
    const { wrapper, bridge } = await mountPage(about(), () => ({ ok: false, error: 'open-failed' }));
    await wrapper.find('[data-about-open-logs]').trigger('click');
    await flushPromises();
    expect(bridge.calls).toEqual([{ name: 'About.OpenLogs', payload: undefined }]);
    expect(wrapper.find('[data-about-failed]').text()).toBe(t('about.logs.openFailed'));
  });

  it('exports diagnostics with no path in the command and shows the file name only', async () => {
    const done = about({ diagnostics: { fileName: 'su-su-diagnostics.zip', logFiles: 2, logLines: 40, droppedLines: 3, bytes: 5000 } });
    const { wrapper, bridge } = await mountPage(about(), () => withView(done));
    await wrapper.find('[data-about-export]').trigger('click');
    await flushPromises();
    expect(bridge.calls).toEqual([{ name: 'About.ExportDiagnostics', payload: undefined }]);
    const text = wrapper.find('[data-about-exported]').text();
    expect(text).toContain('su-su-diagnostics.zip');
    expect(text).toContain(t('about.diagnostics.done', { file: 'su-su-diagnostics.zip', files: 2, lines: 40, dropped: 3, size: t('about.size.kb', { n: '4.9' }) }));
  });

  it('shows an export error as a message in the current language, never the raw code', async () => {
    for (const locale of ['zh-Hans', 'en']) {
      setLocale(locale);
      for (const code of ['disk-full', 'write-failed', 'leak-detected', 'picker', 'something-new']) {
        const { wrapper } = await mountPage(about({ diagnostics: { error: code, logFiles: 0, logLines: 0, droppedLines: 0, bytes: 0 } }));
        const shown = wrapper.find('[data-about-export-error]').text();
        expect(shown).not.toContain(code === 'something-new' ? 'something-new' : 'about.diagnostics');
        expect(shown).toBe(t(code === 'something-new' ? 'about.diagnostics.error.other' : `about.diagnostics.error.${code}`));
        wrapper.unmount();
      }
    }
  });

  it('a build with no export port has the button off', async () => {
    const { wrapper } = await mountPage(about({ canExport: false }));
    expect(wrapper.find('[data-about-export]').attributes('disabled')).toBeDefined();
  });

  it('lists the licenses on demand', async () => {
    const { wrapper } = await mountPage(about());
    expect(wrapper.find('[data-about-licenses]').exists()).toBe(false);
    expect(wrapper.text()).toContain(t('about.licenses.hint', { n: 2 }));
    await wrapper.find('[data-about-licenses-toggle]').trigger('click');
    const list = wrapper.find('[data-about-licenses]').text();
    expect(list).toContain('QuickJS-NG');
    expect(list).toContain('YamlDotNet');
    expect(list).toContain('MIT');
  });

  it('every clean entry says what it deletes and keeps, and a first click only asks', async () => {
    const { wrapper, bridge } = await mountPage(about());
    for (const kind of kinds) {
      const row = wrapper.find(`[data-clean="${kind}"]`);
      expect(row.text()).toContain(t(`about.clean.${kind}.deletes`));
      expect(row.text()).toContain(t(`about.clean.${kind}.keeps`));
      await wrapper.find(`[data-clean-button="${kind}"]`).trigger('click');
      expect(wrapper.find(`[data-clean-confirm="${kind}"]`).exists()).toBe(true);
      await wrapper.find(`[data-clean-cancel="${kind}"]`).trigger('click');
      expect(wrapper.find(`[data-clean-confirm="${kind}"]`).exists()).toBe(false);
    }
    expect(bridge.calls).toEqual([]);
  });

  it('confirming sends the kind with confirm = true, once', async () => {
    const { wrapper, bridge } = await mountPage(about(), () => withView(about({ cleaned: { kind: 'accounts', removed: 4, bytes: 0, skipped: 0 } })));
    await wrapper.find('[data-clean-button="accounts"]').trigger('click');
    await wrapper.find('[data-clean-confirm-button="accounts"]').trigger('click');
    await flushPromises();
    expect(bridge.calls).toEqual([{ name: 'Data.Clear', payload: { kind: 'accounts', confirm: true } }]);
    expect(wrapper.find('[data-clean-confirm="accounts"]').exists()).toBe(false);
    expect(wrapper.find('[data-clean-done]').text()).toContain(t('about.clean.done', { what: t('about.clean.accounts'), n: 4 }));
  });

  it('reports skipped items and clean errors', async () => {
    const skipped = await mountPage(about({ cleaned: { kind: 'caches', removed: 2, bytes: 10, skipped: 3 } }));
    expect(skipped.wrapper.find('[data-clean-done]').text()).toContain(t('about.clean.skipped', { n: 3 }));
    skipped.wrapper.unmount();
    const failed = await mountPage(about({ cleaned: { kind: 'settings', error: 'conflict', removed: 0, bytes: 0, skipped: 0 } }));
    expect(failed.wrapper.find('[data-clean-error]').text()).toBe(t('about.clean.error.conflict'));
  });

  it('a build without an About service shows no section', async () => {
    const { wrapper } = await mountPage(undefined);
    expect(wrapper.find('[data-about]').exists()).toBe(false);
  });

  it('renders in English with the same structure', async () => {
    setLocale('en');
    const { wrapper } = await mountPage(about());
    expect(wrapper.find('[data-about] h2').text()).toBe('About Su-Su');
    expect(wrapper.find('[data-clean="logs"]').text()).toContain('Deletes all log files.');
  });
});
