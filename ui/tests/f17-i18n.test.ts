// F17.2 full Chinese/English check (TEST-PLAN UI05). Every message key the UI source uses exists in both catalogs, no message is empty, the two catalogs
// have the same keys and the same {placeholders}, English messages hold no Chinese text and Chinese messages are not untranslated copies, and no window's
// template carries visible literal text outside the catalog. Dynamic keys (`t(`prefix.${code}`)`) must have at least one catalog entry under the prefix.
import { describe, expect, it } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { catalogs } from '../src/locales/i18n';

const root = join(__dirname, '..', 'src');
function walk(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const full = join(dir, name);
    return statSync(full).isDirectory() ? walk(full) : /\.(vue|ts)$/.test(name) ? [full] : [];
  });
}
const files = walk(root).filter((f) => !f.endsWith(join('locales', 'i18n.ts')) && !f.includes(`${join('bridge', 'devHost')}`));
const zh = catalogs['zh-Hans'];
const en = catalogs.en;
const cjk = /[㐀-鿿＀-￯　-〿]/;
const placeholders = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();

interface Use { file: string; key: string; dynamic: boolean }
const uses: Use[] = [];
for (const file of files) {
  const text = readFileSync(file, 'utf8');
  for (const m of text.matchAll(/(?<![\w.$])t\(\s*(['"`])((?:\\.|(?!\1)[^\\])*)\1/g)) {
    const raw = m[2];
    uses.push(m[1] === '`' && raw.includes('${') ? { file, key: raw.slice(0, raw.indexOf('${')), dynamic: true } : { file, key: raw, dynamic: false });
  }
}
// Keys referenced through data (nav ids, error code tables, `:title-key="..."`): any string literal shaped like an existing namespace.key that is not in the catalog.
const namespaces = new Set(Object.keys(zh).map((k) => k.split('.')[0]));
const looksLikeKey = /^([a-z][A-Za-z0-9]*)(\.[A-Za-z0-9_-]+)+$/;
// Dotted names that are not messages: bridge events, protocol error codes and manifest-localized prefixes (config.title.<instance>.<field>).
const notMessages = new Set(["transcribe.cue", "window.hidden", "video.noAsr", "video.noTranslation", "config.title", "config.help"]);

describe('catalog', () => {
  it('has the same keys in Chinese and English, none empty', () => {
    expect(Object.keys(en).filter((k) => !(k in zh))).toEqual([]);
    expect(Object.keys(zh).filter((k) => !(k in en))).toEqual([]);
    for (const [name, table] of Object.entries(catalogs)) {
      const empty = Object.entries(table).filter(([, v]) => typeof v !== 'string' || v.trim().length === 0).map(([k]) => `${name}:${k}`);
      expect(empty).toEqual([]);
    }
  });

  it('uses the same placeholders in both languages', () => {
    const mismatched = Object.keys(zh).filter((k) => JSON.stringify(placeholders(zh[k])) !== JSON.stringify(placeholders(en[k])));
    expect(mismatched).toEqual([]);
  });

  it('keeps Chinese out of English messages and does not leave Chinese messages untranslated', () => {
    expect(Object.keys(en).filter((k) => cjk.test(en[k]))).toEqual([]);
    // A Chinese message with no Chinese text is allowed only when it is the same text on purpose (brand, protocol and technical names such as API Key).
    const untranslated = Object.keys(zh).filter((k) => !cjk.test(zh[k]) && /[A-Za-z]{3,}/.test(zh[k].replace(/\{\w+\}/g, '')) && zh[k].toLowerCase() !== en[k].toLowerCase());
    expect(untranslated).toEqual([]);
  });
});

describe('source usage', () => {
  it('finds a meaningful number of t() calls (the scan itself works)', () => {
    expect(uses.length).toBeGreaterThan(300);
  });

  it('every literal key used in the UI exists in both languages', () => {
    const missing = uses.filter((u) => !u.dynamic && !(u.key in zh && u.key in en)).map((u) => `${u.file.slice(root.length + 1)}: ${u.key}`);
    expect(missing).toEqual([]);
  });

  it('every dynamic key prefix has entries in both languages', () => {
    const bad = uses.filter((u) => u.dynamic && !(Object.keys(zh).some((k) => k.startsWith(u.key)) && Object.keys(en).some((k) => k.startsWith(u.key)))).map((u) => `${u.file.slice(root.length + 1)}: ${u.key}`);
    expect(bad).toEqual([]);
  });

  it('string literals shaped like message keys under a catalog namespace exist', () => {
    const missing: string[] = [];
    for (const file of files) {
      const text = readFileSync(file, 'utf8').replace(/\/\/[^\n]*|\/\*[\s\S]*?\*\//g, '');
      for (const m of text.matchAll(/(')([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9_-]+)+)\1/g)) { // single quotes only: double quotes in a template are expressions
        const key = m[2];
        if (!looksLikeKey.test(key) || !namespaces.has(key.split('.')[0]) || key in zh) continue;
        if (/\.(ts|vue|js|css|json|png|svg|ico)$/.test(key)) continue;
        if (notMessages.has(key)) continue;
        missing.push(`${file.slice(root.length + 1)}: ${key}`);
      }
    }
    expect(missing).toEqual([]);
  });
});

describe('code', () => {
  it('has no hard-coded Chinese text or Chinese punctuation outside the catalog (language self-names excepted)', () => {
    const selfNames = ['简体中文'];
    const offenders: string[] = [];
    for (const file of files) {
      const code = readFileSync(file, 'utf8').replace(/<!--[\s\S]*?-->/g, '').replace(/\/\/[^\n]*|\/\*[\s\S]*?\*\//g, '');
      for (const m of code.matchAll(/[^\n]*[㐀-鿿＀-￯　-〿][^\n]*/g)) {
        const line = selfNames.reduce((acc, name) => acc.split(name).join(''), m[0]);
        if (cjk.test(line)) offenders.push(`${file.slice(root.length + 1)}: ${m[0].trim().slice(0, 80)}`);
      }
    }
    expect(offenders).toEqual([]);
  });
});

describe('templates', () => {
  it('no window or component template shows literal text outside the catalog', () => {
    const offenders: string[] = [];
    for (const file of files.filter((f) => f.endsWith('.vue'))) {
      const source = readFileSync(file, 'utf8');
      const template = source.match(/<template>([\s\S]*)<\/template>\s*(?:<style|$)/)?.[1] ?? '';
      const stripped = template.replace(/<!--[\s\S]*?-->/g, '').replace(/\{\{[\s\S]*?\}\}/g, ' ');
      for (const m of stripped.matchAll(/>([^<>]+)</g)) {
        const text = m[1].trim();
        if (!text) continue;
        if (text === '简体中文') continue; // the language's own name, shown as is in the language picker
        if (cjk.test(text) || /[A-Za-z]{3,}\s+[A-Za-z]{2,}/.test(text)) offenders.push(`${file.slice(root.length + 1)}: ${text.slice(0, 60)}`);
      }
      for (const m of stripped.matchAll(/\s(?:title|aria-label|placeholder|alt)="([^"]+)"/g)) {
        if (cjk.test(m[1]) || /^[A-Za-z]{3,}(\s+[A-Za-z]+)*$/.test(m[1])) offenders.push(`${file.slice(root.length + 1)}: attr ${m[1].slice(0, 60)}`);
      }
    }
    expect(offenders).toEqual([]);
  });
});
