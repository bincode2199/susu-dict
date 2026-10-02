// F18.2: the application update section of the About page. Fake bridge only (the host side is covered by F18Update*Tests): no source configured, check and
// download send no URL or path, install needs a second click that names the tasks it would interrupt and sends the acknowledgement only after it, every error key
// reads in both languages, a failed or unsigned response is never shown as a version or as "up to date", the unsigned-build marker and the automatic-check switch.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import type { CommandResult, SettingsView, UpdateView } from '@protocol/ui';
import SettingsWindow from '../src/windows/SettingsWindow.vue';
import { createStore } from '../src/bridge/store';
import { setLocale, t } from '../src/locales/i18n';

setLocale('zh-Hans');
afterEach(() => { document.body.innerHTML = ''; setLocale('zh-Hans'); });

function fakeBridge(answer: (name: string, payload?: any) => CommandResult = () => ({ ok: true })) {
  const calls: { name: string; payload?: any }[] = [];
  return { calls, command: vi.fn(async (name: string, payload?: object) => { calls.push({ name, payload }); return answer(name, payload); }) };
}
const view = (over: Partial<UpdateView> = {}): UpdateView => ({
  state: 'none', currentVersion: '1.1.0', error: undefined, offer: undefined, canCheck: true, autoCheck: true, lastCheck: undefined, inFlight: 0, keyringEmbedded: true, ...over,
});
const offer = { version: '1.2.0', size: 22_400_000, notes: 'Fixes and polish' };
const settings = (u: UpdateView | undefined): SettingsView => ({
  revision: 4, fileHash: 'h', issues: [], hotkeys: [], accounts: [], services: [], update: u,
  about: { version: '1.1.0.0', build: '1.1.0', os: 'Windows', runtime: '.NET', logLocation: '%LOCALAPPDATA%\\Su-Su\\logs', logFiles: 0, logBytes: 0, canOpenLogs: true, canExport: true, licenses: [], data: [] },
  general: { uiLanguage: 'zh-Hans', sourceLanguage: 'en', targetLanguage: 'zh-Hans', defaultExpandedCards: 2, allowClipboardBorrowing: false, closeAction: 'hide', launchAtStartup: false },
  network: { proxyMode: 'system', proxyHost: '', proxyPort: 0, proxyUsername: '', proxyPasswordSaved: false, aiTimeoutSeconds: 30 },
});
const answerWith = (u: UpdateView) => ({ ok: true, value: settings(u) }) as CommandResult;

async function mountPage(u: UpdateView | undefined, answer?: (name: string, payload?: any) => CommandResult) {
  const { state } = createStore();
  state.window = { kind: 'Settings', uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features: [] };
  state.settings = settings(u);
  const bridge = fakeBridge(answer);
  const wrapper = mount(SettingsWindow, { props: { bridge: bridge as any, state }, attachTo: document.body });
  await wrapper.findAll('.nav-item').find((n) => n.text() === t('nav.about'))!.trigger('click');
  return { wrapper, bridge, state };
}

describe('Application update section', () => {
  it('is absent when the host has no update service', async () => {
    const { wrapper } = await mountPage(undefined);
    expect(wrapper.find('[data-update]').exists()).toBe(false);
  });

  it('with no source configured says so, offers no check, and shows the current version', async () => {
    const { wrapper } = await mountPage(view({ canCheck: false }));
    expect(wrapper.find('[data-update-current]').text()).toBe(t('update.current', { version: '1.1.0' }));
    expect(wrapper.find('[data-update-nosource]').text()).toBe(t('update.noSource'));
    expect(wrapper.find('[data-update-check]').exists()).toBe(false);
  });

  it('marks an unsigned build: no embedded key means no update can verify', async () => {
    const { wrapper } = await mountPage(view({ keyringEmbedded: false }));
    expect(wrapper.find('[data-update-unsigned]').text()).toBe(t('update.unsignedBuild'));
    const embedded = await mountPage(view());
    expect(embedded.wrapper.find('[data-update-unsigned]').exists()).toBe(false);
  });

  it('checks for updates without a payload and shows the offered version from the host view', async () => {
    const { wrapper, bridge } = await mountPage(view(), () => answerWith(view({ state: 'available', offer })));
    await wrapper.find('[data-update-check]').trigger('click');
    await flushPromises();
    expect(bridge.calls).toEqual([{ name: 'Update.Check', payload: undefined }]);
    expect(wrapper.find('[data-update-state]').text()).toBe(t('update.state.available', { version: '1.2.0', size: t('update.size.mb', { n: '21.4' }) }));
    expect(wrapper.find('[data-update-notes]').text()).toContain('Fixes and polish');
  });

  it('never shows a version or "up to date" for a failed check and names the reason in both languages', async () => {
    const codes = ['signature-invalid', 'signature-malformed', 'signature-missing', 'unknown-key', 'key-not-allowed', 'rotation-invalid', 'manifest-invalid', 'manifest-wrong-product',
      'manifest-too-large', 'no-trusted-keys', 'rollback', 'network', 'hash-mismatch', 'size-mismatch', 'download-truncated', 'package-invalid', 'disk-full', 'something-new'];
    for (const locale of ['zh-Hans', 'en']) {
      setLocale(locale);
      for (const code of codes) {
        const { wrapper } = await mountPage(view({ state: 'failed', error: code, offer: undefined }));
        const text = wrapper.find('[data-update-error]').text();
        expect(text).not.toContain('update.error.');
        expect(text).not.toBe(code);
        expect(text).toBe(t(codes.slice(0, -1).includes(code) ? `update.error.${code}` : 'update.error.other'));
        expect(wrapper.find('[data-update-state]').text()).not.toBe(t('update.state.upToDate'));
        expect(wrapper.text()).not.toContain('1.2.0');
        wrapper.unmount();
      }
    }
  });

  it('downloads only on the button, with no payload', async () => {
    const { wrapper, bridge } = await mountPage(view({ state: 'available', offer }), () => answerWith(view({ state: 'ready', offer })));
    expect(wrapper.find('[data-update-install]').exists()).toBe(false); // nothing to install before a download
    await wrapper.find('[data-update-download]').trigger('click');
    await flushPromises();
    expect(bridge.calls).toEqual([{ name: 'Update.Download', payload: undefined }]);
    expect(wrapper.find('[data-update-state]').text()).toBe(t('update.state.ready', { version: '1.2.0' }));
  });

  it('install needs a second click, names the interrupted tasks, and sends the acknowledgement only after it', async () => {
    const { wrapper, bridge } = await mountPage(view({ state: 'ready', offer, inFlight: 2 }), () => answerWith(view({ state: 'installing', offer })));
    await wrapper.find('[data-update-install]').trigger('click');
    expect(bridge.calls).toEqual([]); // the first click only asks
    expect(wrapper.find('[data-update-in-flight]').text()).toBe(t('update.inFlight', { n: 2 }));
    expect(document.activeElement?.hasAttribute('data-update-cancel')).toBe(true); // focus starts on the safe button
    await wrapper.find('[data-update-cancel]').trigger('click');
    expect(bridge.calls).toEqual([]);
    expect(wrapper.find('[data-update-confirm]').exists()).toBe(false);
    await wrapper.find('[data-update-install]').trigger('click');
    await wrapper.find('[data-update-install-now]').trigger('click');
    await flushPromises();
    expect(bridge.calls).toEqual([{ name: 'Update.Install', payload: { acknowledgedInFlight: true } }]);
    expect(wrapper.find('[data-update-state]').text()).toBe(t('update.state.installing', { version: '1.2.0' }));
  });

  it('install with nothing in flight acknowledges nothing, and Escape cancels the confirmation', async () => {
    const { wrapper, bridge } = await mountPage(view({ state: 'ready', offer, inFlight: 0 }));
    await wrapper.find('[data-update-install]').trigger('click');
    expect(wrapper.find('[data-update-in-flight]').exists()).toBe(false);
    await wrapper.find('[data-update-confirm]').trigger('keydown', { key: 'Escape' });
    expect(wrapper.find('[data-update-confirm]').exists()).toBe(false);
    await wrapper.find('[data-update-install]').trigger('click');
    await wrapper.find('[data-update-install-now]').trigger('click');
    await flushPromises();
    expect(bridge.calls).toEqual([{ name: 'Update.Install', payload: { acknowledgedInFlight: false } }]);
  });

  it('shows a command error in the current language and closes the confirmation', async () => {
    for (const locale of ['zh-Hans', 'en']) {
      setLocale(locale);
      const { wrapper } = await mountPage(view({ state: 'ready', offer }), () => ({ ok: false, error: 'helper-failed' }));
      await wrapper.find('[data-update-install]').trigger('click');
      await wrapper.find('[data-update-install-now]').trigger('click');
      await flushPromises();
      expect(wrapper.find('[data-update-failed]').text()).toBe(t('update.helperFailed'));
      expect(wrapper.find('[data-update-confirm]').exists()).toBe(false);
      wrapper.unmount();
    }
  });

  it('says a rolled back update was rolled back with the reason, and the dismiss button clears it', async () => {
    const { wrapper, bridge } = await mountPage(view({ state: 'rolledBack', offer, error: 'health-check-failed' }), () => answerWith(view()));
    expect(wrapper.find('[data-update-rolled-back]').text()).toBe(t('update.error.health-check-failed'));
    expect(wrapper.find('[data-update-state]').text()).toBe(t('update.state.rolledBack'));
    await wrapper.find('[data-update-discard]').trigger('click');
    await flushPromises();
    expect(bridge.calls).toEqual([{ name: 'Update.Discard', payload: undefined }]);
  });

  it('the automatic-check switch sends only the boolean and says the schedule checks but never installs', async () => {
    const { wrapper, bridge } = await mountPage(view({ lastCheck: '2026-10-02T08:00:00.0000000+00:00' }), () => answerWith(view({ autoCheck: false })));
    expect(wrapper.text()).toContain(t('update.auto'));
    expect(t('update.auto')).toContain('不会自动安装');
    setLocale('en');
    expect(t('update.auto')).toContain('never installs');
    setLocale('zh-Hans');
    await wrapper.find('[data-update-auto]').trigger('click');
    await flushPromises();
    expect(bridge.calls).toEqual([{ name: 'Update.AutoCheck', payload: { enabled: false } }]);
  });

  it('has no hard-coded text: every update key exists in both languages', () => {
    const keys = ['update.title', 'update.current', 'update.check', 'update.download', 'update.install', 'update.installNow', 'update.cancel', 'update.inFlight', 'update.error.other'];
    for (const key of keys) {
      setLocale('zh-Hans'); const zh = t(key, { n: 1, version: '1', size: '1' });
      setLocale('en'); const en = t(key, { n: 1, version: '1', size: '1' });
      expect(zh).not.toBe(key);
      expect(en).not.toBe(key);
      expect(zh).not.toBe(en);
    }
  });
});
