// F17.3: user-facing recovery texts. Every error code the backup code can end with has a Chinese and an English message (the page falls back to a
// generic "no settings changed" line for an unknown code, which would hide a missing text), and the failures that matter for recovery say that the
// current settings are intact. The drills that inject each failure are in tests/Susu.Tests.Unit/F17RecoveryTests.cs.
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { setLocale, t } from '../src/locales/i18n';

afterEach(() => setLocale('zh-Hans'));

const source = (file: string) => readFileSync(resolve(__dirname, '../../src', file), 'utf8');

/** The codes the host can put in a backup outcome: archive refusals, service refusals and the switch at start. */
function emittedCodes(): string[] {
  const codes = new Set<string>();
  const patterns = [
    /BackupException\("([a-z-]+)"/g,
    /new BackupExportOutcome\(false, "([a-z-]+)"/g,
    /Fail\(paths, clock, "([a-z-]+)"/g,
    /DiskOr\("([a-z-]+)"/g,
  ];
  for (const file of ['Susu.Storage/Backup.cs', 'Susu.Storage/BackupArchive.cs', 'Susu.Ui/Backup.cs']) {
    const text = source(file);
    for (const re of patterns) for (const m of text.matchAll(re)) codes.add(m[1]);
  }
  codes.add('disk-full').add('stage-failed').add('io'); // chosen from the exception at run time
  return [...codes].filter((c) => c !== 'not-found').sort();
}

describe('Backup recovery messages', () => {
  it('finds the codes in the source (guards the scan itself)', () => {
    const codes = emittedCodes();
    expect(codes.length).toBeGreaterThan(30);
    for (const c of ['decrypt-failed', 'schema-newer', 'format-newer', 'write-failed', 'disk-full', 'io', 'staged-tampered', 'too-many-attempts']) expect(codes).toContain(c);
  });

  it('every code has its own message in both languages', () => {
    for (const locale of ['zh-Hans', 'en'] as const) {
      setLocale(locale);
      for (const code of emittedCodes()) {
        const key = `backup.error.${code}`;
        const text = t(key);
        expect(text, `${locale} ${key}`).not.toBe(key);
        expect(text, `${locale} ${key} is the generic fallback`).not.toBe(t('backup.error.other'));
      }
    }
  });

  // what happened, whether the old configuration is intact, what to do: the intact part is checked in words, per language
  const intact = {
    'zh-Hans': /没有改变|没有被改动|没有改动|原样/,
    en: /did not change|not changed|unchanged|nothing (was|half)/i,
  } as const;
  const recoveryCodes = ['decrypt-failed', 'disk-full', 'write-failed', 'stage-failed', 'io', 'format-newer', 'schema-newer', 'kdf-params', 'hash-mismatch', 'staged-tampered', 'pending-corrupt', 'too-many-attempts'];

  it('each recovery failure says the current settings are intact', () => {
    for (const locale of ['zh-Hans', 'en'] as const) {
      setLocale(locale);
      for (const code of recoveryCodes) expect(t(`backup.error.${code}`), `${locale} ${code}`).toMatch(intact[locale]);
    }
  });

  it('version and disk messages say what to do', () => {
    setLocale('en');
    expect(t('backup.error.schema-newer')).toMatch(/update su-su/i);
    expect(t('backup.error.format-newer')).toMatch(/update su-su/i);
    expect(t('backup.error.disk-full')).toMatch(/free some space/i);
    expect(t('backup.error.io')).toMatch(/free some space/i);
    expect(t('backup.error.decrypt-failed')).toMatch(/enter the password again/i);
    setLocale('zh-Hans');
    expect(t('backup.error.schema-newer')).toContain('升级');
    expect(t('backup.error.format-newer')).toContain('升级');
    expect(t('backup.error.disk-full')).toMatch(/清理空间/);
    expect(t('backup.error.decrypt-failed')).toContain('重新输入密码');
  });
});
