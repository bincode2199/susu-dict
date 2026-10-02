// F17.1: the Backup page. Fake bridge only (the host side is covered by BackupWindowTests and F17BackupTests): export without keys sends no password,
// keys need a password before the button works, the password is not kept after use, the password step and a wrong password, the restore preview
// (what is replaced, accounts needing authorization, missing plugins listed as information, local keys removed, favorites kept), confirm sends only
// the token, the scheduled and applied states, and a build without a service. English and Chinese both resolve every key the page uses.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import type { BackupPreviewView, BackupView, CommandResult, SettingsView } from '@protocol/ui';
import SettingsWindow from '../src/windows/SettingsWindow.vue';
import { createStore } from '../src/bridge/store';
import { setLocale, t } from '../src/locales/i18n';

setLocale('zh-Hans');
afterEach(() => { document.body.innerHTML = ''; setLocale('zh-Hans'); });

function fakeBridge(answer: (name: string, payload?: any) => CommandResult = () => ({ ok: true })) {
  const calls: { name: string; payload?: any }[] = [];
  return { calls, command: vi.fn(async (name: string, payload?: object) => { calls.push({ name, payload }); return answer(name, payload); }) };
}
const preview = (over: Partial<BackupPreviewView> = {}): BackupPreviewView => ({
  token: 'tok1', created: '2026-01-01T00:00:00.0000000Z', appVersion: '1.2.3', encrypted: false, includesSecrets: false, schemaOlder: false,
  deltas: [{ area: 'settings', current: 0, backup: 0, differs: true }, { area: 'accounts', current: 1, backup: 2, differs: true }, { area: 'prompts', current: 1, backup: 1, differs: false }],
  plugins: [{ id: 'acme.dict', backupVersion: '1.0.0', status: 'missing' }, { id: 'acme.tts', backupVersion: '1.0.0', status: 'older', installedVersion: '1.5.0' }],
  missingPackages: ['acme.dict'], disabledInstances: ['acme'], accounts: [{ id: 'a1', label: 'DeepL', missingSecrets: ['apiKey'] }, { id: 'a2', label: 'Tencent', missingSecrets: [] }],
  backupSecrets: 1, keysRemoved: 2, keptFavorites: 7, keptOutbox: 3, conflicts: ['plugins-missing'], ...over,
});
const backup = (over: Partial<BackupView> = {}): BackupView => ({ canExport: true, canImport: true, step: 'idle', fileName: undefined, preview: undefined, last: undefined, scheduled: false, scheduledSource: undefined, applied: undefined, canUndo: false, ...over });
const settings = (b: BackupView | undefined): SettingsView => ({
  revision: 4, fileHash: 'h', issues: [], hotkeys: [], accounts: [], services: [], backup: b,
  general: { uiLanguage: 'zh-Hans', sourceLanguage: 'en', targetLanguage: 'zh-Hans', defaultExpandedCards: 2, allowClipboardBorrowing: false, closeAction: 'hide', launchAtStartup: false },
  network: { proxyMode: 'system', proxyHost: '', proxyPort: 0, proxyUsername: '', proxyPasswordSaved: false, aiTimeoutSeconds: 30 },
});
const withView = (b: BackupView) => ({ ok: true, value: settings(b) }) as CommandResult;

async function mountPage(b: BackupView | undefined, answer?: (name: string, payload?: any) => CommandResult) {
  const { state } = createStore();
  state.window = { kind: 'Settings', uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features: [] };
  state.settings = settings(b);
  const bridge = fakeBridge(answer);
  const wrapper = mount(SettingsWindow, { props: { bridge: bridge as any, state }, attachTo: document.body });
  await wrapper.findAll('.nav-item').find((n) => n.text() === t('nav.backup'))!.trigger('click');
  return { wrapper, bridge, state };
}

describe('Backup page', () => {
  it('exports without keys: no password in the command, and the button works', async () => {
    const { wrapper, bridge } = await mountPage(backup(), () => withView(backup({ last: { action: 'export', fileName: 'x.susubak', encrypted: false, includedSecrets: false, secretCount: 0 } })));
    expect(wrapper.find('[data-backup-export]').attributes('disabled')).toBeUndefined();
    await wrapper.find('[data-backup-export]').trigger('click');
    await flushPromises();
    expect(bridge.calls).toEqual([{ name: 'Backup.Export', payload: { includeSecrets: false } }]);
    expect(wrapper.find('[data-backup-exported]').text()).toContain('x.susubak');
    expect(wrapper.find('[data-backup-exported]').text()).toContain(t('backup.exported.plain'));
  });

  it('keys need a password: the button stays off until a valid one is typed, and it is cleared after use', async () => {
    const { wrapper, bridge } = await mountPage(backup(), () => withView(backup({ last: { action: 'export', fileName: 'k.susubak', encrypted: true, includedSecrets: true, secretCount: 2 } })));
    await wrapper.find('[data-backup-keys]').setValue(true);
    expect(wrapper.find('[data-backup-export]').attributes('disabled')).toBeDefined();
    await wrapper.find('[data-backup-password]').setValue('short');
    expect(wrapper.find('[data-backup-export]').attributes('disabled')).toBeDefined();
    await wrapper.find('[data-backup-password]').setValue('long enough password');
    expect(wrapper.find('[data-backup-export]').attributes('disabled')).toBeUndefined();
    await wrapper.find('[data-backup-export]').trigger('click');
    await flushPromises();
    expect(bridge.calls).toEqual([{ name: 'Backup.Export', payload: { includeSecrets: true, password: 'long enough password' } }]);
    expect((wrapper.find('[data-backup-password]').element as HTMLInputElement).value).toBe('');
    expect(wrapper.find('[data-backup-exported]').text()).toContain(t('backup.exported.keys', { n: 2 }));
    expect(wrapper.html()).not.toContain('long enough password');
  });

  it('an export error shows its reason', async () => {
    const { wrapper } = await mountPage(backup(), () => withView(backup({ last: { action: 'export', error: 'write-failed', encrypted: false, includedSecrets: false, secretCount: 0 } })));
    await wrapper.find('[data-backup-export]').trigger('click');
    await flushPromises();
    expect(wrapper.find('[data-backup-export-error]').text()).toBe(t('backup.error.write-failed'));
  });

  it('pick sends no path; an encrypted file asks for its password and a wrong one stays on the step', async () => {
    let n = 0;
    const { wrapper, bridge } = await mountPage(backup(), (name) => {
      if (name === 'Backup.Pick') return withView(backup({ step: 'password', fileName: 'k.susubak' }));
      n++;
      return withView(backup({ step: 'password', fileName: 'k.susubak', last: { action: 'preview', error: 'decrypt-failed', fileName: 'k.susubak', encrypted: false, includedSecrets: false, secretCount: 0 } }));
    });
    await wrapper.find('[data-backup-pick]').trigger('click');
    await flushPromises();
    expect(bridge.calls[0]).toEqual({ name: 'Backup.Pick', payload: undefined });
    expect(wrapper.find('[data-backup-unlock]').text()).toContain('k.susubak');
    expect(wrapper.find('[data-backup-unlock-button]').attributes('disabled')).toBeDefined();
    await wrapper.find('[data-backup-unlock-password]').setValue('wrong password');
    await wrapper.find('[data-backup-unlock-button]').trigger('click');
    await flushPromises();
    expect(bridge.calls[1]).toEqual({ name: 'Backup.Unlock', payload: { password: 'wrong password' } });
    expect(n).toBe(1);
    expect(wrapper.find('[data-backup-preview-error]').text()).toBe(t('backup.error.decrypt-failed'));
    expect(wrapper.find('[data-backup-unlock]').exists()).toBe(true);
    expect((wrapper.find('[data-backup-unlock-password]').element as HTMLInputElement).value).toBe('');
    expect(wrapper.html()).not.toContain('wrong password');
  });

  it('shows what the restore replaces, the authorization it needs, missing plugins as information, removed keys and kept data', async () => {
    const { wrapper } = await mountPage(backup({ step: 'preview', fileName: 'p.susubak', preview: preview() }));
    expect(wrapper.find('[data-backup-preview]').text()).toContain('p.susubak');
    expect(wrapper.find('[data-delta="settings"]').text()).toBe(t('backup.delta.settings', { area: t('backup.area.settings') }));
    expect(wrapper.find('[data-delta="accounts"]').text()).toBe(t('backup.delta.changed', { area: t('backup.area.accounts'), current: 1, backup: 2 }));
    expect(wrapper.find('[data-delta="prompts"]').text()).toBe(t('backup.delta.same', { area: t('backup.area.prompts') }));
    expect(wrapper.find('[data-account="a1"]').text()).toBe(t('backup.account.keysMissing', { label: 'DeepL', names: 'apiKey' }));
    expect(wrapper.find('[data-account="a2"]').text()).toBe(t('backup.account.keysIncluded', { label: 'Tencent' }));
    expect(wrapper.find('[data-plugin="acme.dict"]').attributes('data-status')).toBe('missing');
    expect(wrapper.find('[data-plugin="acme.dict"]').text()).toContain(t('backup.plugin.missing'));
    expect(wrapper.find('[data-backup-missing]').text()).toBe(t('backup.missing', { ids: 'acme.dict' }));
    expect(wrapper.find('[data-backup-disabled]').text()).toBe(t('backup.disabled', { n: 1 }));
    expect(wrapper.find('[data-backup-keys-removed]').text()).toBe(t('backup.keysRemoved', { n: 2 }));
    expect(wrapper.find('[data-backup-kept]').text()).toBe(t('backup.kept', { fav: 7, outbox: 3 }));
    expect(wrapper.find('[data-backup-keys-included]').exists()).toBe(false);
    expect(wrapper.find('[data-backup-older]').exists()).toBe(false);
  });

  it('notes included keys and an older backup version', async () => {
    const { wrapper } = await mountPage(backup({ step: 'preview', fileName: 'p.susubak', preview: preview({ includesSecrets: true, schemaOlder: true, keysRemoved: 0, missingPackages: [], disabledInstances: [], plugins: [] }) }));
    expect(wrapper.find('[data-backup-keys-included]').text()).toBe(t('backup.keysIncluded', { n: 1 }));
    expect(wrapper.find('[data-backup-older]').exists()).toBe(true);
    expect(wrapper.find('[data-backup-keys-removed]').exists()).toBe(false);
    expect(wrapper.find('[data-backup-plugins]').exists()).toBe(false);
  });

  it('confirm sends only the token and the page then shows the schedule; cancel discards', async () => {
    const { wrapper, bridge } = await mountPage(backup({ step: 'preview', fileName: 'p.susubak', preview: preview() }), (name) => withView(name === 'Backup.Apply'
      ? backup({ scheduled: true, scheduledSource: 'backup', last: { action: 'apply', encrypted: false, includedSecrets: false, secretCount: 0 } }) : backup()));
    await wrapper.find('[data-backup-apply]').trigger('click');
    await flushPromises();
    expect(bridge.calls).toEqual([{ name: 'Backup.Apply', payload: { token: 'tok1' } }]);
    expect(wrapper.find('[data-backup-scheduled]').text()).toContain(t('backup.scheduled'));
    await wrapper.find('[data-backup-unschedule]').trigger('click');
    await flushPromises();
    expect(bridge.calls[1]).toEqual({ name: 'Backup.Discard', payload: undefined });
    expect(wrapper.find('[data-backup-scheduled]').exists()).toBe(false);
  });

  it('reports how the last import ended, with the dismiss and the undo', async () => {
    const applied = backup({ applied: { state: 'Applied', source: 'backup', disabledInstances: 2, at: '2026-01-01T00:00:00Z' }, canUndo: true });
    const { wrapper, bridge } = await mountPage(applied, () => withView(backup({ canUndo: true, scheduled: true, scheduledSource: 'undo' })));
    expect(wrapper.find('[data-backup-applied-ok]').text()).toBe(t('backup.applied'));
    expect(wrapper.find('[data-backup-applied-disabled]').text()).toContain(t('backup.applied.disabled', { n: 2 }));
    await wrapper.find('[data-backup-undo]').trigger('click');
    await flushPromises();
    expect(bridge.calls).toEqual([{ name: 'Backup.Undo', payload: undefined }]);
    expect(wrapper.find('[data-backup-scheduled]').text()).toContain(t('backup.scheduled.undo'));
    expect(wrapper.find('[data-backup-undo]').exists()).toBe(false); // not offered while one is scheduled
  });

  it('a failed import says the current settings did not change, with the reason', async () => {
    const { wrapper } = await mountPage(backup({ applied: { state: 'Failed', error: 'io', source: 'backup', disabledInstances: 0, at: '2026-01-01T00:00:00Z' } }));
    expect(wrapper.find('[data-backup-applied-failed]').text()).toBe(t('backup.failed', { reason: t('backup.error.io') }));
  });

  it('a failed confirm or undo shows its reason (disk full) next to the restore controls', async () => {
    for (const action of ['apply', 'undo'] as const) {
      const { wrapper } = await mountPage(backup({ canUndo: true, last: { action, error: 'disk-full', encrypted: false, includedSecrets: false, secretCount: 0 } }));
      expect(wrapper.find('[data-backup-action-error]').text()).toBe(t('backup.error.disk-full'));
      wrapper.unmount();
    }
  });

  it('shows host errors for an unavailable or busy backup and has no page content without a view', async () => {
    const { wrapper } = await mountPage(backup(), () => ({ ok: false, error: 'busy' }));
    await wrapper.find('[data-backup-pick]').trigger('click');
    await flushPromises();
    expect(wrapper.find('[data-backup-failed]').text()).toBe(t('backup.busy'));
    const none = await mountPage(undefined);
    expect(none.wrapper.find('[data-backup]').exists()).toBe(false);
  });

  it('a build without dialogs disables import and export', async () => {
    const { wrapper } = await mountPage(backup({ canExport: false, canImport: false }));
    expect(wrapper.find('[data-backup-export]').attributes('disabled')).toBeDefined();
    expect(wrapper.find('[data-backup-pick]').attributes('disabled')).toBeDefined();
  });

  it('every backup key resolves in English and Chinese (no key shown raw)', async () => {
    for (const locale of ['en', 'zh-Hans'] as const) {
      setLocale(locale);
      const { wrapper } = await mountPage(backup({ step: 'preview', fileName: 'p.susubak', preview: preview({ includesSecrets: true, schemaOlder: true }), canUndo: true,
        applied: { state: 'Failed', error: 'too-many-attempts', source: 'undo', disabledInstances: 1, at: '2026-01-01T00:00:00Z' }, last: { action: 'preview', error: 'kdf-params', encrypted: false, includedSecrets: false, secretCount: 0 } }));
      const text = wrapper.find('[data-backup]').text();
      expect(text).not.toMatch(/\bbackup\.[a-z][a-zA-Z.-]*/); // a raw key has a lower-case letter after the dot; a sentence ending "backup." runs into the next element's capital
      expect(text).not.toMatch(/\bnav\.backup\b/);
      expect(text).toContain(t('backup.error.kdf-params'));
      wrapper.unmount();
    }
  });
});
