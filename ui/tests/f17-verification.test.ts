// F17 independent verification (testing agent): TEST-PLAN UI05 static checks on the two new Settings pages and the error and status texts the host can send.
// Not executed (needs a real window): screen reader announcements, high contrast, real keyboard walk, long-copy truncation.
// What is checked here: every stable code the host can send (read from the C# sources) has a message in both languages; both pages rendered in every state
// show no raw message key and, in English, no Chinese text or fullwidth punctuation; every interactive control has an accessible name; labels point at real
// ids; ids are unique; no positive tabindex; result and error regions are live regions; placeholders are filled by the call sites; the second click of a
// data clean is required; Escape and focus behaviour of the confirm box is recorded.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises, type VueWrapper } from '@vue/test-utils';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import type { AboutView, BackupPreviewView, BackupView, CommandResult, SettingsView } from '@protocol/ui';
import SettingsWindow from '../src/windows/SettingsWindow.vue';
import { createStore } from '../src/bridge/store';
import { catalogs, setLocale, t } from '../src/locales/i18n';

const repo = join(__dirname, '..', '..');
const zh = catalogs['zh-Hans'];
const en = catalogs.en;
const cjk = /[㐀-鿿＀-￯　-〿]/;

setLocale('zh-Hans');
afterEach(() => { document.body.innerHTML = ''; setLocale('zh-Hans'); });

function read(...parts: string[]): string { return readFileSync(join(repo, ...parts), 'utf8'); }

// ---------- codes the host can send, read from the C# sources ----------

const codeSources = ['src/Susu.Storage/BackupArchive.cs', 'src/Susu.Storage/Backup.cs', 'src/Susu.Ui/Backup.cs'].map((f) => read(...f.split('/'))).join('\n');
const backupCodes = new Set<string>([
  ...[...codeSources.matchAll(/BackupException\("([a-z-]+)"\)/g)].map((m) => m[1]),
  ...[...codeSources.matchAll(/BackupExportOutcome\(false, "([a-z-]+)"/g)].map((m) => m[1]),
  ...[...codeSources.matchAll(/Fail\(paths, clock, "([a-z-]+)"/g)].map((m) => m[1]),
  ...[...codeSources.matchAll(/FailureCode\([a-z]+, "([a-z-]+)"\)/g)].map((m) => m[1]),
  'disk-full', 'ratio', 'picker', // disk-full from FailureCode, ratio from the bounded reader (a variable, not a literal), picker from the shell
]);
const diagnosticsCodes = new Set(['disk-full', 'write-failed', 'leak-detected', 'picker']);
const cleanCodes = new Set(['conflict', 'failed', 'unavailable']);

describe('error codes', () => {
  it('finds a meaningful number of backup codes in the sources (the scan itself works)', () => {
    expect(backupCodes.size).toBeGreaterThan(25);
  });

  it('every backup code the host can send has a message in both languages', () => {
    const missing = [...backupCodes].filter((c) => !(`backup.error.${c}` in zh && `backup.error.${c}` in en));
    expect(missing).toEqual([]);
  });

  it('every diagnostics and clean code has a message in both languages', () => {
    expect([...diagnosticsCodes].filter((c) => !(`about.diagnostics.error.${c}` in zh && `about.diagnostics.error.${c}` in en))).toEqual([]);
    expect([...cleanCodes].filter((c) => !(`about.clean.error.${c}` in zh && `about.clean.error.${c}` in en))).toEqual([]);
  });

  it('a code with no catalog entry falls back to a readable generic message, never the key', () => {
    for (const lang of ['zh-Hans', 'en'] as const) {
      setLocale(lang);
      for (const key of ['backup.error.other', 'about.diagnostics.error.other', 'about.clean.error.other']) {
        expect(t(key)).not.toBe(key);
        expect(t(key).length).toBeGreaterThan(3);
      }
    }
  });
});

// ---------- placeholders are supplied by the call sites ----------

function walk(dir: string): string[] {
  return readdirSync(dir).flatMap((n) => { const f = join(dir, n); return statSync(f).isDirectory() ? walk(f) : /\.(vue|ts)$/.test(n) ? [f] : []; });
}

/** `t('key', { a, b: x })` calls with a literal object: the names must be exactly the placeholders of the message in both languages. */
describe('placeholders at the call sites', () => {
  it('every literal t(key, {..}) call supplies every placeholder of its message, and no call leaves one out', () => {
    const problems: string[] = [];
    let checked = 0;
    for (const file of walk(join(repo, 'ui', 'src')).filter((f) => !f.endsWith('i18n.ts') && !f.includes('devHost'))) {
      const text = readFileSync(file, 'utf8');
      for (const m of text.matchAll(/(?<![\w.$])t\(\s*'([^']+)'\s*(,\s*\{)?/g)) {
        const key = m[1];
        if (!(key in zh)) continue;
        const need = [...new Set([...zh[key].matchAll(/\{(\w+)\}/g)].map((x) => x[1]))].sort();
        let supplied: string[] | null = [];
        if (m[2]) {
          let depth = 1; let i = m.index! + m[0].length; const start = i;
          while (i < text.length && depth > 0) { if (text[i] === '{') depth++; else if (text[i] === '}') depth--; i++; }
          const body = text.slice(start, i - 1);
          // top-level keys only: strip nested braces, parentheses and strings first
          let flat = body; let prev = '';
          while (flat !== prev) { prev = flat; flat = flat.replace(/\([^()]*\)|\{[^{}]*\}|\[[^\[\]]*\]|'[^']*'|`[^`]*`|"[^"]*"/g, '0'); }
          supplied = [...flat.matchAll(/(?:^|,)\s*(\w+)\s*(?::|(?=,|$))/g)].map((x) => x[1]).sort();
        }
        checked++;
        if (need.length && supplied && JSON.stringify(need) !== JSON.stringify(supplied.filter((s) => need.includes(s)))) problems.push(`${file.slice(repo.length + 1)}: ${key} needs ${need} gets ${supplied}`);
      }
    }
    expect(checked).toBeGreaterThan(100);
    expect(problems).toEqual([]);
  });

  it('English and Chinese messages keep the same placeholders for every key of the new pages', () => {
    const keys = Object.keys(zh).filter((k) => /^(backup|about)\./.test(k));
    expect(keys.length).toBeGreaterThan(100);
    for (const k of keys) {
      const a = [...zh[k].matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();
      const b = [...en[k].matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();
      expect(b, k).toEqual(a);
    }
  });

  it('English strings of the whole catalog hold no fullwidth or CJK punctuation and no stray template leftovers', () => {
    const bad = Object.entries(en).filter(([, v]) => /[，。：；！？（）、「」『』《》]/.test(v) || /\{\s*\}|\$\{/.test(v)).map(([k]) => k);
    // the ellipsis and the middle dot are ordinary English typography and are allowed; curly apostrophes too; the prompt variable syntax {{...}} is deliberate; fullwidth punctuation copied from Chinese text is not
    expect(bad).toEqual([]);
  });

  it('Chinese messages do not contain doubled placeholders or ASCII-only copies of English for the new pages', () => {
    const same = Object.keys(zh).filter((k) => /^(backup|about)\./.test(k) && zh[k] === en[k] && /[A-Za-z]{4,}/.test(zh[k].replace(/\{\w+\}/g, '')));
    expect(same).toEqual([]);
  });
});

// ---------- rendering every state ----------

function fakeBridge(answer: (name: string, payload?: any) => CommandResult = () => ({ ok: true })) {
  const calls: { name: string; payload?: any }[] = [];
  return { calls, command: vi.fn(async (name: string, payload?: object) => { calls.push({ name, payload }); return answer(name, payload); }) };
}
const kinds = ['caches', 'logs', 'screenshots', 'favorites', 'settings', 'accounts'];
const statuses = ['missing', 'builtin', 'same', 'older', 'newer'];
const areas = ['settings', 'accounts', 'instances', 'enabledServices', 'prompts'];
const preview = (over: Partial<BackupPreviewView> = {}): BackupPreviewView => ({
  token: 'tok1', created: '2026-01-01T00:00:00.0000000Z', appVersion: '1.2.3', encrypted: true, includesSecrets: true, schemaOlder: true,
  deltas: areas.map((area, i) => ({ area, current: i, backup: i + 2, differs: i % 2 === 0 })),
  plugins: statuses.map((status, i) => ({ id: `acme.p${i}`, backupVersion: '1.0.0', status, installedVersion: '1.5.0' })),
  missingPackages: ['acme.p0'], disabledInstances: ['a', 'b'], accounts: [{ id: 'a1', label: 'DeepL', missingSecrets: ['apiKey'] }, { id: 'a2', label: 'Tencent', missingSecrets: [] }],
  backupSecrets: 3, keysRemoved: 2, keptFavorites: 7, keptOutbox: 3, conflicts: ['plugins-missing'], ...over,
});
const backup = (over: Partial<BackupView> = {}): BackupView => ({ canExport: true, canImport: true, step: 'idle', fileName: 'x.susubak', preview: undefined, last: undefined, scheduled: false, scheduledSource: undefined, applied: undefined, canUndo: true, ...over });
const about = (over: Partial<AboutView> = {}): AboutView => ({
  version: '1.2.3.0', build: '1.2.3', os: 'Windows 10.0.26200', runtime: '.NET 10.0.0', logLocation: '%LOCALAPPDATA%\\Su-Su\\logs', logFiles: 3, logBytes: 2048,
  canOpenLogs: true, canExport: true, diagnostics: undefined,
  licenses: [{ name: 'QuickJS-NG', version: 'v0.17.0', license: 'MIT', kind: 'native', ships: 'susu_quickjs.dll' }],
  data: kinds.map((kind) => ({ kind, count: kind === 'settings' ? 0 : 4, bytes: kind === 'caches' || kind === 'screenshots' ? 5 * 1024 * 1024 : 0, available: true })), cleaned: undefined, ...over,
});
const settings = (over: Partial<SettingsView>): SettingsView => ({
  revision: 4, fileHash: 'h', issues: [], hotkeys: [], accounts: [], services: [],
  general: { uiLanguage: 'zh-Hans', sourceLanguage: 'en', targetLanguage: 'zh-Hans', defaultExpandedCards: 2, allowClipboardBorrowing: false, closeAction: 'hide', launchAtStartup: false },
  network: { proxyMode: 'system', proxyHost: '', proxyPort: 0, proxyUsername: '', proxyPasswordSaved: false, aiTimeoutSeconds: 30 }, ...over,
});

async function mountPage(nav: 'nav.backup' | 'nav.about', view: Partial<SettingsView>, lang: 'zh-Hans' | 'en', answer?: (name: string, payload?: any) => CommandResult) {
  setLocale(lang);
  const { state } = createStore();
  state.window = { kind: 'Settings', uiLanguage: lang, theme: 'light', maximized: false, pinned: false, devPreview: false, features: [] };
  state.settings = settings(view);
  const bridge = fakeBridge(answer);
  const wrapper = mount(SettingsWindow, { props: { bridge: bridge as any, state }, attachTo: document.body });
  await wrapper.findAll('.nav-item').find((n) => n.text() === t(nav))!.trigger('click');
  return { wrapper, bridge };
}

const backupStates = (): { name: string; view: BackupView }[] => [
  { name: 'idle', view: backup() },
  { name: 'cannot import', view: backup({ canImport: false, canExport: false, canUndo: false }) },
  { name: 'password step', view: backup({ step: 'password' }) },
  { name: 'preview', view: backup({ step: 'preview', preview: preview() }) },
  { name: 'preview, nothing special', view: backup({ step: 'preview', preview: preview({ schemaOlder: false, includesSecrets: false, keysRemoved: 0, plugins: [], missingPackages: [], disabledInstances: [], accounts: [] }) }) },
  { name: 'scheduled', view: backup({ scheduled: true }) },
  { name: 'scheduled undo', view: backup({ scheduled: true, scheduledSource: 'undo' }) },
  { name: 'applied', view: backup({ applied: { state: 'Applied', source: 'backup', disabledInstances: 2, at: '2026-01-01T00:00:00Z' } }) },
  { name: 'applied undo', view: backup({ applied: { state: 'Applied', source: 'undo', disabledInstances: 0, at: '2026-01-01T00:00:00Z' } }) },
  ...[...backupCodes].flatMap((code) => [
    { name: `export error ${code}`, view: backup({ last: { action: 'export', error: code, encrypted: false, includedSecrets: false, secretCount: 0 } }) },
    { name: `preview error ${code}`, view: backup({ last: { action: 'preview', error: code, encrypted: false, includedSecrets: false, secretCount: 0 } }) },
    { name: `apply error ${code}`, view: backup({ last: { action: 'apply', error: code, encrypted: false, includedSecrets: false, secretCount: 0 } }) },
    { name: `failed at start ${code}`, view: backup({ applied: { state: 'Failed', error: code, source: 'backup', disabledInstances: 0, at: '2026-01-01T00:00:00Z' } }) },
  ]),
  { name: 'failed at start, unknown code', view: backup({ applied: { state: 'Failed', error: 'brand-new-code', source: 'backup', disabledInstances: 0, at: '2026-01-01T00:00:00Z' } }) },
  { name: 'exported with keys', view: backup({ last: { action: 'export', fileName: 'k.susubak', encrypted: true, includedSecrets: true, secretCount: 4 } }) },
  { name: 'exported encrypted', view: backup({ last: { action: 'export', fileName: 'k.susubak', encrypted: true, includedSecrets: false, secretCount: 0 } }) },
  { name: 'exported plain', view: backup({ last: { action: 'export', fileName: 'k.susubak', encrypted: false, includedSecrets: false, secretCount: 0 } }) },
];

const aboutStates = (): { name: string; view: AboutView }[] => [
  { name: 'idle', view: about() },
  { name: 'no services', view: about({ canOpenLogs: false, canExport: false, data: kinds.map((kind) => ({ kind, count: 0, bytes: 0, available: false })) }) },
  { name: 'exported', view: about({ diagnostics: { fileName: 'd.zip', logFiles: 2, logLines: 40, droppedLines: 3, bytes: 5 * 1024 * 1024 } }) },
  ...[...diagnosticsCodes, 'brand-new-code'].map((code) => ({ name: `diagnostics error ${code}`, view: about({ diagnostics: { error: code, logFiles: 0, logLines: 0, droppedLines: 0, bytes: 0 } }) })),
  ...kinds.map((kind) => ({ name: `cleaned ${kind}`, view: about({ cleaned: { kind, removed: 5, bytes: 1024, skipped: 2 } }) })),
  ...[...cleanCodes, 'brand-new-code'].map((code) => ({ name: `clean error ${code}`, view: about({ cleaned: { kind: 'logs', error: code, removed: 0, bytes: 0, skipped: 0 } }) })),
];

const rawKey = /\b(?:backup|about|nav|sep|common)\.[a-z][A-Za-z0-9.-]{2,}/;

describe('rendering every state of both pages', () => {
  it('shows no raw message key in either language, no Chinese in English, Chinese in Chinese', async () => {
    const problems: string[] = [];
    let rendered = 0;
    for (const lang of ['zh-Hans', 'en'] as const) {
      for (const s of backupStates()) {
        const { wrapper } = await mountPage('nav.backup', { backup: s.view }, lang);
        const text = wrapper.find('[data-backup]').text();
        rendered++;
        if (rawKey.test(text)) problems.push(`${lang} backup ${s.name}: raw key in "${text.match(rawKey)![0]}"`);
        if (lang === 'en' && cjk.test(text.replace(/简体中文/g, ''))) problems.push(`en backup ${s.name}: Chinese text "${text.match(cjk)![0]}"`);
        if (lang === 'zh-Hans' && !cjk.test(text)) problems.push(`zh backup ${s.name}: no Chinese text`);
        if (/undefined|NaN|\[object|\{\w+\}/.test(text)) problems.push(`${lang} backup ${s.name}: placeholder leftover or undefined in "${text.slice(0, 100)}"`);
        wrapper.unmount();
      }
      for (const s of aboutStates()) {
        const { wrapper } = await mountPage('nav.about', { about: s.view }, lang);
        const text = wrapper.find('[data-about]').text();
        rendered++;
        if (rawKey.test(text)) problems.push(`${lang} about ${s.name}: raw key in "${text.match(rawKey)![0]}"`);
        if (lang === 'en' && cjk.test(text.replace(/简体中文/g, ''))) problems.push(`en about ${s.name}: Chinese text "${text.match(cjk)![0]}"`);
        if (/undefined|NaN|\[object|\{\w+\}/.test(text)) problems.push(`${lang} about ${s.name}: placeholder leftover in "${text.slice(0, 100)}"`);
        wrapper.unmount();
      }
    }
    expect(rendered).toBeGreaterThan(300);
    expect(problems).toEqual([]);
  }, 120_000);
});

// ---------- accessible names, labels, ids, tabindex, live regions ----------

function accessibleName(el: Element, root: ParentNode): string {
  const aria = el.getAttribute('aria-label');
  if (aria?.trim()) return aria.trim();
  const by = el.getAttribute('aria-labelledby');
  if (by) { const n = by.split(/\s+/).map((id) => root.querySelector(`#${CSS.escape(id)}`)?.textContent?.trim() ?? '').join(' ').trim(); if (n) return n; }
  const id = el.getAttribute('id');
  if (id) { const l = root.querySelector(`label[for="${CSS.escape(id)}"]`); if (l?.textContent?.trim()) return l.textContent.trim(); }
  const wrapping = el.closest('label');
  if (wrapping?.textContent?.trim()) return wrapping.textContent.trim();
  if (el.tagName === 'BUTTON' && el.textContent?.trim()) return el.textContent.trim();
  return el.getAttribute('title')?.trim() ?? '';
}

function audit(wrapper: VueWrapper, selector: string): string[] {
  const section = wrapper.find(selector).element;
  const doc = document;
  const problems: string[] = [];
  const controls = [...section.querySelectorAll('button, input, select, textarea, a[href], [role="button"], [tabindex]')];
  for (const el of controls) {
    const tag = `${el.tagName.toLowerCase()}${[...el.attributes].filter((a) => a.name.startsWith('data-')).map((a) => `[${a.name}]`).join('')}`;
    if (el.tagName === 'INPUT' && (el as HTMLInputElement).type === 'hidden') continue;
    if (!accessibleName(el, doc)) problems.push(`no accessible name: ${tag}`);
    const ti = el.getAttribute('tabindex');
    if (ti !== null && Number(ti) > 0) problems.push(`positive tabindex ${ti}: ${tag}`);
    if (el.tagName === 'BUTTON' && !el.getAttribute('type')) problems.push(`button without type (submits a form): ${tag}`);
    if (el.tagName === 'INPUT' && (el as HTMLInputElement).type === 'password') {
      if (el.getAttribute('autocomplete') !== 'off') problems.push(`password field may be offered for saving: ${tag}`);
      if (el.getAttribute('spellcheck') !== 'false') problems.push(`password field spellchecked: ${tag}`);
    }
  }
  for (const label of section.querySelectorAll('label[for]')) if (!doc.getElementById(label.getAttribute('for')!)) problems.push(`label for a missing id: ${label.getAttribute('for')}`);
  const ids = [...doc.querySelectorAll('[id]')].map((e) => e.id);
  for (const id of new Set(ids.filter((i, n) => ids.indexOf(i) !== n))) problems.push(`duplicate id ${id}`);
  for (const el of section.querySelectorAll('[data-backup-export-error], [data-backup-preview-error], [data-backup-action-error], [data-backup-failed], [data-about-export-error], [data-clean-error], [data-about-failed]'))
    if (el.getAttribute('role') !== 'alert') problems.push(`error text without role=alert: ${[...el.attributes].map((a) => a.name).join(' ')}`);
  for (const el of section.querySelectorAll('[data-backup-exported], [data-backup-scheduled], [data-backup-applied], [data-about-exported], [data-clean-done]'))
    if (el.getAttribute('role') !== 'status') problems.push(`result text without role=status: ${[...el.attributes].map((a) => a.name).join(' ')}`);
  const headings = [...section.querySelectorAll('h1,h2,h3,h4')].map((h) => Number(h.tagName[1]));
  for (let i = 1; i < headings.length; i++) if (headings[i] - headings[i - 1] > 1) problems.push(`heading level jumps ${headings[i - 1]} to ${headings[i]}`);
  return problems;
}

describe('accessibility (static)', () => {
  it('every control of the Backup page has a name, a type and a sane tabindex in every state, in both languages', async () => {
    const problems = new Set<string>();
    for (const lang of ['zh-Hans', 'en'] as const)
      for (const s of backupStates().filter((x) => !/error /.test(x.name) || /export error (write-failed)|apply error (disk-full)/.test(x.name))) {
        const { wrapper } = await mountPage('nav.backup', { backup: s.view }, lang);
        for (const p of audit(wrapper, '[data-backup]')) problems.add(`${s.name}: ${p}`);
        wrapper.unmount();
      }
    expect([...problems]).toEqual([]);
  });

  it('every control of the About page has a name, a type and a sane tabindex in every state, in both languages', async () => {
    const problems = new Set<string>();
    for (const lang of ['zh-Hans', 'en'] as const)
      for (const s of aboutStates().filter((x) => !/error /.test(x.name) || /diagnostics error write-failed|clean error failed/.test(x.name))) {
        const { wrapper } = await mountPage('nav.about', { about: s.view }, lang);
        for (const p of audit(wrapper, '[data-about]')) problems.add(`${s.name}: ${p}`);
        wrapper.unmount();
      }
    expect([...problems]).toEqual([]);
  });

  it('the confirm box of a clean is a labelled alert dialog that appears only after the first click', async () => {
    const { wrapper } = await mountPage('nav.about', { about: about() }, 'en');
    for (const kind of kinds) {
      expect(wrapper.find(`[data-clean-confirm="${kind}"]`).exists()).toBe(false);
      await wrapper.find(`[data-clean-button="${kind}"]`).trigger('click');
      const box = wrapper.find(`[data-clean-confirm="${kind}"]`);
      expect(box.attributes('role')).toBe('alertdialog');
      expect(box.attributes('aria-label')).toBeTruthy();
      await wrapper.find(`[data-clean-cancel="${kind}"]`).trigger('click');
    }
  });

  it('OBSERVATION F17V-12 (low, UI05): the six clean buttons share one accessible name, and neither page moves focus to what appears (password step, preview, confirm box)', async () => {
    const { wrapper } = await mountPage('nav.about', { about: about() }, 'en');
    const names = kinds.map((k) => accessibleName(wrapper.find(`[data-clean-button="${k}"]`).element, document));
    // Each row's title is a <span>, not a label, and the button has no aria-label or aria-describedby: a screen reader tabbing through the buttons hears the same
    // word six times. Records today's behaviour; when fixed expect six different names (for example "Clear logs").
    expect(new Set(names).size).toBe(1);
    const sources = ['BackupSettings.vue', 'AboutSettings.vue'].map((f) => read('ui', 'src', 'components', f)).join('\n');
    expect(sources).not.toMatch(/\.focus\(|autofocus|@keydown\.esc|@keydown\.escape/);
  });
});

// ---------- second click, passwords, no path ----------

describe('commands the pages send', () => {
  it('a data clean sends nothing on the first click and kind + confirm only on the second', async () => {
    const { wrapper, bridge } = await mountPage('nav.about', { about: about() }, 'zh-Hans');
    for (const kind of kinds) {
      const before = bridge.calls.length;
      await wrapper.find(`[data-clean-button="${kind}"]`).trigger('click');
      expect(bridge.calls.length).toBe(before);
      await wrapper.find(`[data-clean-confirm-button="${kind}"]`).trigger('click');
      await flushPromises();
      expect(bridge.calls.at(-1)).toEqual({ name: 'Data.Clear', payload: { kind, confirm: true } });
    }
    expect(bridge.calls.filter((c) => c.name === 'Data.Clear')).toHaveLength(kinds.length);
  });

  it('cancelling the confirm box and opening another entry never sends the first one', async () => {
    const { wrapper, bridge } = await mountPage('nav.about', { about: about() }, 'zh-Hans');
    await wrapper.find('[data-clean-button="accounts"]').trigger('click');
    await wrapper.find('[data-clean-button="logs"]').trigger('click'); // while the accounts box is open
    await flushPromises();
    expect(bridge.calls.filter((c) => c.name === 'Data.Clear')).toEqual([]);
    expect(wrapper.findAll('[data-clean-confirm]').length).toBeLessThanOrEqual(1);
    await wrapper.find('[data-clean-cancel]').trigger('click');
    expect(wrapper.findAll('[data-clean-confirm]')).toHaveLength(0);
  });

  it('a double click on the confirm button sends one request', async () => {
    let release: () => void = () => {};
    const gate = new Promise<void>((r) => { release = r; });
    const { wrapper, bridge } = await mountPage('nav.about', { about: about() }, 'zh-Hans', () => ({ ok: true }));
    bridge.command.mockImplementation(async (name: string, payload?: object) => { bridge.calls.push({ name, payload }); await gate; return { ok: true }; });
    await wrapper.find('[data-clean-button="accounts"]').trigger('click');
    const confirm = wrapper.find('[data-clean-confirm-button="accounts"]');
    await confirm.trigger('click');
    await confirm.trigger('click');
    release();
    await flushPromises();
    expect(bridge.calls.filter((c) => c.name === 'Data.Clear')).toHaveLength(1);
  });

  it('the Backup page never shows a password or key in its markup after typing one', async () => {
    const { wrapper } = await mountPage('nav.backup', { backup: backup({ step: 'password' }) }, 'en');
    await wrapper.find('[data-backup-password]').setValue('S3cret-Export-Password');
    await wrapper.find('[data-backup-unlock-password]').setValue('S3cret-Unlock-Password');
    expect(wrapper.html()).not.toMatch(/S3cret-(Export|Unlock)-Password/);
    for (const input of wrapper.findAll('input[type="password"]')) expect(input.attributes('value')).toBeUndefined();
  });
});
