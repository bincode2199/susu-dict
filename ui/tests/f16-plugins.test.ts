// F16.1/F16.2: the Plugins settings page. Fake bridge only (the host side is covered by PluginWindowTests and the sandbox tests): empty list and pick,
// the permission diff for a first install / an upgrade / a built-in override, the unsigned and third-party notes, confirm and discard sending
// only the token, rejection reasons, install and uninstall results (with the built-in restored note), the uninstall confirmation step, and a
// build without a file dialog.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import type { CommandResult, InstalledPluginView, PluginOutcomeView, PluginPreviewView, PluginsView, SettingsView } from '@protocol/ui';
import SettingsWindow from '../src/windows/SettingsWindow.vue';
import { createStore } from '../src/bridge/store';
import { setLocale, t } from '../src/locales/i18n';

setLocale('zh-Hans');
afterEach(() => { document.body.innerHTML = ''; });

function fakeBridge(answer: (name: string, payload?: any) => CommandResult = () => ({ ok: true })) {
  const calls: { name: string; payload?: any }[] = [];
  return { calls, command: vi.fn(async (name: string, payload?: object) => { calls.push({ name, payload }); return answer(name, payload); }) };
}
const installed = (over: Partial<InstalledPluginView> = {}): InstalledPluginView => ({
  id: 'acme.dict', name: 'Acme', version: '1.0.0', signerKind: 'thirdParty', signer: 'a1b2c3d4e5f60718', capabilities: ['dictionary'], origins: ['https://dict.example'], secrets: [], overridesBuiltIn: undefined, ...over,
});
const pending = (over: Partial<PluginPreviewView> = {}): PluginPreviewView => ({
  token: 'tok1', id: 'acme.dict', name: 'Acme', version: '1.1.0', signerKind: 'thirdParty', signer: 'a1b2c3d4e5f60718', overridesBuiltIn: undefined, replacesVersion: undefined,
  against: 'none', baseVersion: undefined, addedCapabilities: ['dictionary'], removedCapabilities: [], addedOrigins: ['https://dict.example'], removedOrigins: [], addedSecrets: [], removedSecrets: [], ...over,
});
const plugins = (over: Partial<PluginsView> = {}): PluginsView => ({ installed: [], pending: undefined, last: undefined, canPick: true, ...over });
const settings = (p: PluginsView): SettingsView => ({
  revision: 4, fileHash: 'h', issues: [], hotkeys: [], accounts: [], services: [], plugins: p,
  general: { uiLanguage: 'zh-Hans', sourceLanguage: 'en', targetLanguage: 'zh-Hans', defaultExpandedCards: 2, allowClipboardBorrowing: false, closeAction: 'hide', launchAtStartup: false },
  network: { proxyMode: 'system', proxyHost: '', proxyPort: 0, proxyUsername: '', proxyPasswordSaved: false, aiTimeoutSeconds: 30 },
});
const withView = (p: PluginsView) => ({ ok: true, value: settings(p) }) as CommandResult;

async function mountPage(p: PluginsView, answer?: (name: string, payload?: any) => CommandResult) {
  const { state } = createStore();
  state.window = { kind: 'Settings', uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features: [] };
  state.settings = settings(p);
  const bridge = fakeBridge(answer);
  const wrapper = mount(SettingsWindow, { props: { bridge: bridge as any, state }, attachTo: document.body });
  await wrapper.findAll('.nav-item').find((n) => n.text() === t('nav.plugins'))!.trigger('click');
  return { wrapper, bridge, state };
}
const lines = (w: Awaited<ReturnType<typeof mountPage>>['wrapper']) => w.findAll('[data-diff]').map((l) => l.attributes('data-diff'));

describe('Plugins page', () => {
  it('shows the empty list and sends only Plugin.Pick, then renders the staged package', async () => {
    const { wrapper, bridge } = await mountPage(plugins(), (name) => (name === 'Plugin.Pick' ? withView(plugins({ pending: pending() })) : { ok: true }));
    expect(wrapper.find('[data-plugin-none]').exists()).toBe(true);
    await wrapper.find('[data-plugin-pick]').trigger('click');
    await flushPromises();
    expect(bridge.calls).toEqual([{ name: 'Plugin.Pick', payload: undefined }]);
    expect(wrapper.find('[data-plugin-pending]').exists()).toBe(true);
    expect(wrapper.find('[data-plugin-id]').text()).toBe('acme.dict');
  });

  it('lists everything a first install asks for and does not call it a widening', async () => {
    const { wrapper } = await mountPage(plugins({ pending: pending({ addedSecrets: ['apiKey'] }) }));
    expect(lines(wrapper)).toEqual(['add-capability', 'add-origin', 'add-secret']);
    expect(wrapper.find('[data-plugin-against]').text()).toBe(t('plugins.diff.first'));
    expect(wrapper.find('[data-plugin-wider]').exists()).toBe(false);
    expect(wrapper.find('[data-plugin-signer]').text()).toContain('a1b2c3d4e5f60718');
  });

  it('shows added and removed permissions against the installed version and warns when it widens', async () => {
    const p = pending({ against: 'installed', baseVersion: '1.0.0', replacesVersion: '1.0.0', addedOrigins: ['https://new.example'], removedOrigins: ['https://dict.example'], addedCapabilities: [] });
    const { wrapper } = await mountPage(plugins({ installed: [installed()], pending: p }));
    expect(lines(wrapper)).toEqual(['add-origin', 'remove-origin']);
    expect(wrapper.find('[data-plugin-against]').text()).toBe(t('plugins.diff.installed', { v: '1.0.0' }));
    expect(wrapper.find('[data-plugin-replaces]').text()).toBe(t('plugins.replaces', { v: '1.0.0' }));
    expect(wrapper.find('[data-plugin-wider]').exists()).toBe(true);
  });

  it('says so when nothing changed and when the package overrides a built-in version', async () => {
    const p = pending({ against: 'builtin', baseVersion: '1.0.0', overridesBuiltIn: '1.0.0', signerKind: 'host', signer: 'host-key', addedCapabilities: [], addedOrigins: [] });
    const { wrapper } = await mountPage(plugins({ pending: p }));
    expect(wrapper.find('[data-plugin-nochange]').exists()).toBe(true);
    expect(wrapper.find('[data-plugin-override]').text()).toBe(t('plugins.overrides', { v: '1.0.0' }));
    expect(wrapper.find('[data-plugin-signer]').text()).toBe(t('plugins.signer.host', { id: 'host-key' }));
  });

  it('marks an unsigned package and notes that a third-party signature is only an identity', async () => {
    const un = await mountPage(plugins({ pending: pending({ signerKind: 'unsigned', signer: '' }) }));
    expect(un.wrapper.find('[data-plugin-signer]').text()).toBe(t('plugins.signer.unsigned'));
    expect(un.wrapper.find('[data-plugin-signer]').classes()).toContain('warn-text');
    un.wrapper.unmount();
    const third = await mountPage(plugins({ pending: pending() }));
    expect(third.wrapper.text()).toContain(t('plugins.signer.thirdPartyNote'));
  });

  it('confirm and discard send only the token', async () => {
    const answer = (name: string) => (name === 'Plugin.Confirm' ? withView(plugins({ installed: [installed({ version: '1.1.0' })], last: { action: 'install', id: 'acme.dict', version: '1.1.0', issues: [] } as PluginOutcomeView })) : withView(plugins()));
    const { wrapper, bridge } = await mountPage(plugins({ pending: pending() }), answer);
    await wrapper.find('[data-plugin-confirm]').trigger('click');
    await flushPromises();
    expect(bridge.calls[0]).toEqual({ name: 'Plugin.Confirm', payload: { token: 'tok1' } });
    expect(wrapper.find('[data-plugin-installed]').text()).toBe(t('plugins.installed', { id: 'acme.dict', version: '1.1.0' }));
    expect(wrapper.find('[data-plugin-item="acme.dict"]').exists()).toBe(true);
    wrapper.unmount();
    const second = await mountPage(plugins({ pending: pending() }), answer);
    await second.wrapper.find('[data-plugin-discard]').trigger('click');
    await flushPromises();
    expect(second.bridge.calls[0]).toEqual({ name: 'Plugin.Discard', payload: { token: 'tok1' } });
    expect(second.wrapper.find('[data-plugin-pending]').exists()).toBe(false);
  });

  it('lists why a package was refused, translating known codes', async () => {
    const last: PluginOutcomeView = { action: 'preview', error: 'install.rejected', issues: [{ path: '../evil.js', code: 'traversal' }, { path: 'x', code: 'weird-new-code' }] };
    const { wrapper } = await mountPage(plugins({ last }));
    expect(wrapper.find('[data-plugin-error]').text()).toBe(t('plugins.rejected'));
    const issues = wrapper.findAll('[data-plugin-issue]').map((i) => i.text());
    expect(issues[0]).toContain(t('plugins.issue.traversal'));
    expect(issues[0]).toContain('../evil.js');
    expect(issues[1]).toContain(t('plugins.issue.other', { code: 'weird-new-code' }));
  });

  it('shows a translated install failure (activation rolled back)', async () => {
    const { wrapper } = await mountPage(plugins({ last: { action: 'install', id: 'acme.dict', error: 'install.activationFailed', issues: [] } }));
    expect(wrapper.find('[data-plugin-error]').text()).toBe(t('plugins.error.install.activationFailed'));
  });

  it('needs a second click to uninstall, then reports the restored built-in version', async () => {
    const answer = () => withView(plugins({ last: { action: 'uninstall', id: 'acme.dict', issues: [], restoredBuiltIn: '1.0.0' } }));
    const { wrapper, bridge } = await mountPage(plugins({ installed: [installed({ overridesBuiltIn: '1.0.0', signerKind: 'host', signer: 'host-key' })] }), answer);
    expect(wrapper.find('[data-plugin-override-line]').text()).toContain(t('plugins.uninstall.restores'));
    await wrapper.find('[data-plugin-uninstall]').trigger('click');
    expect(bridge.calls).toHaveLength(0);
    await wrapper.find('[data-plugin-uninstall-confirm]').trigger('click');
    await flushPromises();
    expect(bridge.calls[0]).toEqual({ name: 'Plugin.Uninstall', payload: { id: 'acme.dict' } });
    expect(wrapper.find('[data-plugin-uninstalled]').text()).toBe(t('plugins.uninstalled.restored', { id: 'acme.dict', v: '1.0.0' }));
  });

  it('disables choosing when the build has no file dialog and shows a failed command', async () => {
    const none = await mountPage(plugins({ canPick: false }));
    expect(none.wrapper.find('[data-plugin-pick]').attributes('disabled')).toBeDefined();
    expect(none.wrapper.text()).toContain(t('plugins.noDialog'));
    none.wrapper.unmount();
    const busy = await mountPage(plugins(), () => ({ ok: false, error: 'busy' }));
    await busy.wrapper.find('[data-plugin-pick]').trigger('click');
    await flushPromises();
    expect(busy.wrapper.find('[data-plugin-failed]').text()).toBe(t('plugins.busy'));
  });

  // ---------- F16.2 ----------

  const heldUpdate = (over: Partial<PluginPreviewView> = {}) => pending({
    token: 'upd1', version: '1.2.0', against: 'installed', baseVersion: '1.0.0', replacesVersion: '1.0.0', isUpdate: true,
    reasons: ['signer-changed', 'permissions-expanded'], addedCapabilities: [], addedOrigins: ['https://new.example'], ...over,
  });

  it('lists staged updates, names the held changes, and keeps Apply disabled until they are acknowledged', async () => {
    const { wrapper, bridge } = await mountPage(plugins({ installed: [installed()], updates: [heldUpdate()] }), () => withView(plugins()));
    expect(wrapper.find('[data-plugin-update="true"]').exists()).toBe(true);
    expect(wrapper.findAll('[data-plugin-reason]').map((r) => r.attributes('data-plugin-reason'))).toEqual(['signer-changed', 'permissions-expanded']);
    expect(wrapper.find('[data-plugin-old-runs]').text()).toBe(t('plugins.held.oldRuns', { v: '1.0.0' }));
    const apply = wrapper.find('[data-plugin-confirm]');
    expect(apply.attributes('disabled')).toBeDefined();
    await apply.trigger('click');
    expect(bridge.calls).toHaveLength(0);
    await wrapper.find('[data-plugin-ack]').setValue(true);
    expect(wrapper.find('[data-plugin-confirm]').attributes('disabled')).toBeUndefined();
    await wrapper.find('[data-plugin-confirm]').trigger('click');
    await flushPromises();
    expect(bridge.calls[0]).toEqual({ name: 'Plugin.Confirm', payload: { token: 'upd1', acknowledged: true } });
  });

  it('a staged update without held changes applies with the token only and can be discarded', async () => {
    const { wrapper, bridge } = await mountPage(plugins({ installed: [installed()], updates: [heldUpdate({ reasons: [] })] }), () => withView(plugins()));
    expect(wrapper.find('[data-plugin-ack]').exists()).toBe(false);
    await wrapper.find('[data-plugin-confirm]').trigger('click');
    await flushPromises();
    expect(bridge.calls[0]).toEqual({ name: 'Plugin.Confirm', payload: { token: 'upd1' } });
  });

  it('names the calls an install or uninstall would cancel and counts them afterwards', async () => {
    const tasks = [{ capability: 'translate', count: 2 }];
    const { wrapper } = await mountPage(plugins({ installed: [installed({ inFlight: tasks })], pending: pending({ inFlight: tasks }) }));
    expect(wrapper.find('[data-plugin-pending] [data-plugin-inflight]').text()).toBe(t('plugins.inflight', { tasks: 'translate × 2' }));
    await wrapper.find('[data-plugin-uninstall]').trigger('click');
    expect(wrapper.find('[data-plugin-item] [data-plugin-inflight]').text()).toBe(t('plugins.inflight', { tasks: 'translate × 2' }));
    wrapper.unmount();
    const done = await mountPage(plugins({ last: { action: 'install', id: 'acme.dict', version: '1.1.0', issues: [], interrupted: 2 } }));
    expect(done.wrapper.find('[data-plugin-interrupted]').text()).toBe(t('plugins.interrupted', { n: 2 }));
  });

  it('uninstall asks whether to delete the stored data and sends removeData only when ticked', async () => {
    const answer = () => withView(plugins());
    const keep = await mountPage(plugins({ installed: [installed()] }), answer);
    await keep.wrapper.find('[data-plugin-uninstall]').trigger('click');
    expect(keep.wrapper.text()).toContain(t('plugins.dataKept'));
    await keep.wrapper.find('[data-plugin-uninstall-confirm]').trigger('click');
    await flushPromises();
    expect(keep.bridge.calls[0]).toEqual({ name: 'Plugin.Uninstall', payload: { id: 'acme.dict' } });
    keep.wrapper.unmount();
    const drop = await mountPage(plugins({ installed: [installed()] }), answer);
    await drop.wrapper.find('[data-plugin-uninstall]').trigger('click');
    await drop.wrapper.find('[data-plugin-remove-data]').setValue(true);
    await drop.wrapper.find('[data-plugin-uninstall-confirm]').trigger('click');
    await flushPromises();
    expect(drop.bridge.calls[0]).toEqual({ name: 'Plugin.Uninstall', payload: { id: 'acme.dict', removeData: true } });
  });

  it('offers a check only with an update source, and a failed check is shown as a failure, never as up to date', async () => {
    const none = await mountPage(plugins());
    expect(none.wrapper.find('[data-plugin-check]').attributes('disabled')).toBeDefined();
    expect(none.wrapper.text()).toContain(t('plugins.updates.none'));
    none.wrapper.unmount();
    const answer = () => withView(plugins({ canCheckUpdates: true, check: { checked: 1, staged: 0, failures: [{ id: 'acme.dict', code: 'signature-invalid' }] } }));
    const { wrapper, bridge } = await mountPage(plugins({ canCheckUpdates: true }), answer);
    await wrapper.find('[data-plugin-check]').trigger('click');
    await flushPromises();
    expect(bridge.calls[0]).toEqual({ name: 'Plugin.CheckUpdates', payload: undefined });
    expect(wrapper.find('[data-plugin-check-ok]').exists()).toBe(false);
    expect(wrapper.find('[data-plugin-check-failed]').text()).toBe(t('plugins.check.failed'));
    expect(wrapper.find('[data-plugin-check-failure]').text()).toContain(t('plugins.check.failure.signature-invalid'));
    wrapper.unmount();
    const ok = await mountPage(plugins({ canCheckUpdates: true, check: { checked: 3, staged: 1, failures: [] } }));
    expect(ok.wrapper.find('[data-plugin-check-ok]').text()).toBe(t('plugins.check.result', { n: 3, staged: 1 }));
  });

  it('translates the held-change refusal and the compatibility reasons', async () => {
    const { wrapper } = await mountPage(plugins({ last: { action: 'install', id: 'acme.dict', error: 'install.needsConfirmation', issues: [] } }));
    expect(wrapper.find('[data-plugin-error]').text()).toBe(t('plugins.error.install.needsConfirmation'));
    wrapper.unmount();
    const rejected = await mountPage(plugins({ last: { action: 'preview', error: 'install.rejected', issues: [{ path: 'apiVersion', code: 'api-unsupported' }, { path: 'minHost', code: 'host-too-old' }] } }));
    const texts = rejected.wrapper.findAll('[data-plugin-issue]').map((i) => i.text());
    expect(texts[0]).toContain(t('plugins.issue.api-unsupported'));
    expect(texts[1]).toContain(t('plugins.issue.host-too-old'));
  });
});
