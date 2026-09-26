import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import type { CardSnapshot, DictionaryEntryView } from '@protocol/ui';
import ResultCard from '../src/components/ResultCard.vue';
import { entryPlainText, entryVocabProjection } from '../src/components/dictionary';
import { setLocale } from '../src/locales/i18n';

// F09 independent verification (DICT03, S08): adversarial dictionary entries in the real card component.
setLocale('zh-Hans');

const base = (extra: Partial<DictionaryEntryView> = {}): DictionaryEntryView => ({
  word: 'run',
  phonetics: [{ accent: 'us', ipa: 'rʌn', audioId: 'aud-secret-1' }],
  parts: [{ pos: 'v.', means: ['跑'] }],
  forms: [],
  examples: [],
  ...extra,
});
const card = (entry: DictionaryEntryView | undefined, extra: Partial<CardSnapshot> = {}): CardSnapshot =>
  ({ serviceId: 'youdao/translate', displayName: '有道', state: 'Ready', collapsed: false, text: '', dictionary: !!entry, entry, ...extra });
const mountCard = (c: CardSnapshot) => mount(ResultCard, { props: { card: c, from: 'en', to: 'zh-Hans' }, attachTo: document.body });

describe('F09 verification: dictionary card under hostile data', () => {
  it('renders very large entries without dropping or interpreting items, and copy still works', async () => {
    const n = 500;
    const e = base({
      parts: Array.from({ length: n }, (_, i) => ({ pos: 'n.', means: [`<b>m${i}</b>`] })),
      examples: Array.from({ length: n }, (_, i) => ({ src: `s${i}`, dst: `<i>d${i}</i>` })),
      forms: Array.from({ length: n }, (_, i) => ({ name: 'f', value: `v${i}` })),
    });
    const w = mountCard(card(e));
    expect(w.findAll('.part')).toHaveLength(n);
    expect(w.findAll('.example')).toHaveLength(n);
    expect(w.find('.entry').findAll('b, i').length).toBe(0);
    await w.find('[data-action="copy"]').trigger('click');
    const copied = w.emitted('copy')?.[0]?.[0] as string;
    expect(copied).toContain('n. <b>m499</b>');
    expect(copied).toContain('<i>d499</i>');
    w.unmount();
  });

  it('never puts an audio id into the DOM, even a hostile one', () => {
    const hostileId = 'javascript:alert(1)//aud';
    const w = mountCard(card(base({ phonetics: [{ accent: 'uk', ipa: 'x', audioId: hostileId }, { accent: 'us', ipa: 'y', audioId: 'aud-secret-2' }] })));
    const html = w.html();
    expect(html).not.toContain(hostileId);
    expect(html).not.toContain('aud-secret-2');
    for (const el of Array.from(document.body.querySelectorAll('*')))
      for (const attr of Array.from(el.attributes)) expect(attr.value).not.toMatch(/aud-secret|javascript:/);
    w.unmount();
  });

  it('keeps bidi overrides, entities and zero-width characters as literal text', () => {
    const word = '‮gnp.exe‬';
    const e = base({ word, parts: [{ pos: '&lt;b&gt;', means: ['&amp;nbsp;', 'a​b'] }] });
    const w = mountCard(card(e));
    expect(w.find('.word').text()).toBe(word);
    expect(w.find('.pos').text()).toBe('&lt;b&gt;');
    expect(w.find('.means').text()).toBe('&amp;nbsp;；a​b');
    expect(entryPlainText(e)).toContain('&lt;b&gt; &amp;nbsp;；a​b');
    w.unmount();
  });

  it('a fallback (legal empty entry) card is an ordinary translation card', () => {
    const w = mountCard(card(undefined, { text: '<script>window.__f09=1</script>未知词' }));
    expect(w.find('.entry').exists()).toBe(false);
    expect(w.find('p.text').text()).toBe('<script>window.__f09=1</script>未知词');
    expect(w.findAll('script').length).toBe(0);
    expect(w.findAll('.actions button').map((b) => b.attributes('data-action'))).toEqual(['pronounce', 'copy']); // F10.2 read-aloud key
    expect((window as unknown as { __f09?: number }).__f09).toBeUndefined();
    w.unmount();
  });

  it('an entry is not shown on a failed or cancelled card', () => {
    for (const state of ['Failed', 'Cancelled'] as const) {
      const w = mountCard(card(base(), { state }));
      expect(w.find('.entry').exists()).toBe(false);
      w.unmount();
    }
  });
});

describe('F09 verification: copy and vocab projections', () => {
  it('copy folds line/paragraph separators and control characters inside fields', () => {
    const text = entryPlainText(base({ word: 'a b c\u0007d', phonetics: [], parts: [], forms: [], examples: [{ src: 'x\ny', dst: '' }] }));
    expect(text).toBe('a b c d\n\nx y');
  });

  it('vocab projection keeps hostile values literal, drops blank items and never carries audio ids', () => {
    const v = entryVocabProjection(base({
      word: ' <img src=x onerror=1> ',
      phonetics: [{ accent: 'uk', ipa: '  ', audioId: 'aud-1' }, { accent: '<b>us</b>', ipa: 'ɪ', audioId: 'aud-2' }],
      parts: [{ pos: 'n.', means: ['', '  '] }, { pos: '', means: ['javascript:x'] }],
      forms: [{ name: 'x', value: ' ' }],
      examples: [{ src: ' ', dst: '' }, { src: '', dst: '<svg/onload=1>' }],
    }), 'en', 'youdao/translate');
    expect(v).toEqual({
      word: '<img src=x onerror=1>',
      lang: 'en',
      source: 'youdao/translate',
      phonetics: [{ accent: '<b>us</b>', ipa: 'ɪ' }],
      meanings: [{ pos: '', means: ['javascript:x'] }],
      forms: [],
      examples: [{ src: '', dst: '<svg/onload=1>' }],
    });
    expect(JSON.stringify(v)).not.toContain('aud-');
  });

  it('copy of an entry with every group empty is just the word', () => {
    expect(entryPlainText(base({ phonetics: [], parts: [], forms: [], examples: [] }))).toBe('run');
  });
});
