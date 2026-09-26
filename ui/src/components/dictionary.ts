import type { DictionaryEntryView } from '@protocol/ui';
import { t } from '../locales/i18n';

// F09.3 data projections of a dictionary entry (DICT03). Both are built from the structured view only, so a
// hostile service value stays a string: nothing here parses or interprets markup.

/** Localized accent label (英/美, UK/US); an unknown accent is shown as sent, as text. */
export function accentLabel(accent: string): string {
  return accent === 'uk' || accent === 'us' ? t(`dict.accent.${accent}`) : clean(accent);
}

// Control characters plus U+2028/U+2029, built from escapes so the source holds no raw separators.
const FIELD_BREAKS = new RegExp('[\u0000-\u001f\u007f\u2028\u2029]+', 'g');

/** Trims, and folds control characters and line breaks inside one field to a single space. */
function clean(value: string | undefined): string {
  return (value ?? '').replace(FIELD_BREAKS, ' ').replace(/ {2,}/g, ' ').trim();
}

/**
 * Plain-text projection for the copy action. One item per line, blank line between groups:
 *
 *   word
 *   英 /ipa/  美 /ipa/
 *   (blank)
 *   n. mean1；mean2
 *   (blank)
 *   复数: value；过去式: value
 *   (blank)
 *   example source
 *   example translation
 *
 * Empty groups are left out. Audio ids never appear.
 */
export function entryPlainText(entry: DictionaryEntryView): string {
  const groups: string[][] = [];
  const head = [clean(entry.word)];
  const phonetics = entry.phonetics
    .filter((p) => clean(p.ipa))
    .map((p) => `${accentLabel(p.accent)} /${clean(p.ipa)}/`.trim());
  if (phonetics.length) head.push(phonetics.join('  '));
  groups.push(head.filter(Boolean));
  const parts = entry.parts
    .map((p) => ({ pos: clean(p.pos), means: p.means.map(clean).filter(Boolean) }))
    .filter((p) => p.means.length)
    .map((p) => (p.pos ? `${p.pos} ${p.means.join('；')}` : p.means.join('；')));
  if (parts.length) groups.push(parts);
  const forms = entry.forms.filter((f) => clean(f.value)).map((f) => `${clean(f.name)}: ${clean(f.value)}`);
  if (forms.length) groups.push([forms.join('；')]);
  const examples = entry.examples.flatMap((e) => [clean(e.src), clean(e.dst)].filter(Boolean));
  if (examples.length) groups.push(examples);
  return groups.filter((g) => g.length).map((g) => g.join('\n')).join('\n\n');
}

/**
 * Favorite (vocab) projection prepared for F15. Shape follows the export content choices (PLAN 6.6: meanings,
 * phonetics, examples) and feeds `VocabRequest.word/lang/content` (ARCHITECTURE 8.3). Audio ids are
 * session-scoped and are not stored.
 */
export interface VocabProjection {
  word: string;
  lang: string;
  source: string;
  phonetics: { accent: string; ipa: string }[];
  meanings: { pos: string; means: string[] }[];
  forms: { name: string; value: string }[];
  examples: { src: string; dst: string }[];
}

export function entryVocabProjection(entry: DictionaryEntryView, lang: string, source: string): VocabProjection {
  return {
    word: clean(entry.word),
    lang,
    source,
    phonetics: entry.phonetics.filter((p) => clean(p.ipa)).map((p) => ({ accent: clean(p.accent), ipa: clean(p.ipa) })),
    meanings: entry.parts
      .map((p) => ({ pos: clean(p.pos), means: p.means.map(clean).filter(Boolean) }))
      .filter((p) => p.means.length),
    forms: entry.forms.filter((f) => clean(f.value)).map((f) => ({ name: clean(f.name), value: clean(f.value) })),
    examples: entry.examples
      .map((e) => ({ src: clean(e.src), dst: clean(e.dst) }))
      .filter((e) => e.src || e.dst),
  };
}
