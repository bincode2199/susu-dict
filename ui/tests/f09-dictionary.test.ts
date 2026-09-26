import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import type { CardSnapshot, DictionaryEntryView } from '@protocol/ui';
import ResultCard from '../src/components/ResultCard.vue';
import { entryPlainText, entryVocabProjection } from '../src/components/dictionary';
import { setLocale, t } from '../src/locales/i18n';

setLocale('zh-Hans');

const entry = (extra: Partial<DictionaryEntryView> = {}): DictionaryEntryView => ({
  word: 'run',
  phonetics: [{ accent: 'uk', ipa: 'rʌn', audioId: 'a1' }, { accent: 'us', ipa: 'rʌn', audioId: 'a2' }],
  parts: [{ pos: 'v.', means: ['跑', '经营'] }, { pos: 'n.', means: ['跑步'] }],
  forms: [{ name: '过去式', value: 'ran' }, { name: '过去分词', value: 'run' }],
  examples: [{ src: 'I run every day.', dst: '我每天跑步。' }],
  ...extra,
});
const dictCard = (e: DictionaryEntryView, state: CardSnapshot['state'] = 'Ready'): CardSnapshot =>
  ({ serviceId: 'youdao/dictionary', displayName: '有道词典', state, collapsed: false, text: '', dictionary: true, entry: e });
const mountCard = (c: CardSnapshot) => mount(ResultCard, { props: { card: c, from: 'en', to: 'zh-Hans' }, attachTo: document.body });

describe('F09.3 dictionary card rendering (DICT03)', () => {
  it('renders word, phonetics, parts of speech, forms and examples as structured text', () => {
    const w = mountCard(dictCard(entry()));
    expect(w.find('.word').text()).toBe('run');
    const phon = w.findAll('.phonetic');
    expect(phon.map((p) => p.find('.accent').text())).toEqual(['英', '美']);
    expect(phon[0].find('.ipa').text()).toBe('/rʌn/');
    expect(w.findAll('.part').map((p) => [p.find('.pos').text(), p.find('.means').text()])).toEqual([['v.', '跑；经营'], ['n.', '跑步']]);
    expect(w.find('.forms').text()).toContain('过去式 ran');
    expect(w.find('.example .src').text()).toBe('I run every day.');
    expect(w.find('.example .dst').text()).toBe('我每天跑步。');
    expect(w.find('p.text').exists()).toBe(false);
    w.unmount();
  });

  it('leaves empty groups out and shows the entry only once the card is Ready', async () => {
    const w = mountCard(dictCard(entry({ phonetics: [], forms: [], examples: [] })));
    expect(w.find('.phonetics').exists()).toBe(false);
    expect(w.find('.forms').exists()).toBe(false);
    expect(w.find('.examples').exists()).toBe(false);
    await w.setProps({ card: dictCard(entry(), 'Loading') });
    expect(w.find('.entry').exists()).toBe(false);
    expect(w.find('.skeleton').exists()).toBe(true);
    w.unmount();
  });

  it('keeps hostile HTML, scripts, SVG and javascript: links inert text (S08)', () => {
    const hostile = [
      '<img src=x onerror="window.__pwned=1">',
      '<script>window.__pwned=1</script>',
      '<svg onload="window.__pwned=1"><use href="data:image/svg+xml,x"/></svg>',
      '<a href="javascript:window.__pwned=1">x</a>',
      'javascript:alert(1)',
    ];
    const e = entry({
      word: hostile[0],
      phonetics: [{ accent: '<b>us</b>', ipa: hostile[1], audioId: 'javascript:alert(1)' }],
      parts: [{ pos: hostile[2], means: [hostile[3], hostile[4]] }],
      forms: [{ name: hostile[1], value: hostile[2] }],
      examples: [{ src: hostile[3], dst: hostile[0] }],
    });
    const w = mountCard(dictCard(e));
    const body = w.find('.entry');
    expect(body.findAll('img, script, svg:not([aria-hidden]), a, use, iframe, object').length).toBe(0);
    // Only the component's own icons are SVG, and none carries service data.
    for (const svg of body.findAll('svg')) expect(svg.html()).not.toContain('pwned');
    expect(body.find('.word').text()).toBe(hostile[0]);
    expect(body.find('.accent').text()).toBe('<b>us</b>');
    expect(body.find('.pos').text()).toBe(hostile[2]);
    expect(body.find('.means').text()).toBe(`${hostile[3]}；${hostile[4]}`);
    expect(body.html()).not.toContain('javascript:alert(1)"');
    expect(document.querySelectorAll('[onerror], [onload], [href^="javascript"]').length).toBe(0);
    expect((window as unknown as { __pwned?: number }).__pwned).toBeUndefined();
    w.unmount();
  });

  it('without a pronunciation service: read-aloud and favorite unavailable, phonetic keys with dictionary audio still play', async () => {
    const w = mountCard(dictCard(entry({ phonetics: [{ accent: 'uk', ipa: 'rʌn', audioId: 'a1' }, { accent: 'us', ipa: 'rʌn' }] })));
    const actions = w.findAll('.actions button').map((b) => b.attributes('data-action'));
    expect(actions).toEqual(['pronounce', 'copy', 'favorite']);
    const pronounce = w.find('[data-action="pronounce"]');
    const favorite = w.find('[data-action="favorite"]');
    expect(pronounce.attributes('disabled')).toBeDefined();
    expect(pronounce.attributes('title')).toBe(t('card.needsSpeech'));
    expect(favorite.attributes('disabled')).toBeDefined();
    expect(favorite.attributes('title')).toBe(t('card.needsVocab'));
    const speak = w.findAll('.speak');
    expect(speak).toHaveLength(2);
    expect(speak[0].attributes('disabled')).toBeUndefined(); // TTS03: the entry's own audio needs no TTS service
    expect(speak[1].attributes('disabled')).toBeDefined();
    expect(speak[1].attributes('title')).toBe(t('card.needsSpeech'));
    await speak[0].trigger('click');
    await pronounce.trigger('click');
    await favorite.trigger('click');
    expect(w.emitted('speak')).toEqual([[0]]);
    expect(Object.keys(w.emitted())).not.toContain('copy');
    w.unmount();
  });

  it('a plain translation card has read-aloud and copy (F10.2)', () => {
    const w = mountCard({ serviceId: 's', displayName: 's', state: 'Ready', collapsed: false, text: '你好' });
    expect(w.findAll('.actions button').map((b) => b.attributes('data-action'))).toEqual(['pronounce', 'copy']);
    w.unmount();
  });
});

describe('F09.3 copy and favorite projections (DICT03)', () => {
  it('copies the plain-text projection of the entry, not the card text', async () => {
    const w = mountCard(dictCard(entry()));
    await w.find('[data-action="copy"]').trigger('click');
    expect(w.emitted('copy')?.[0]).toEqual([
      'run\n英 /rʌn/  美 /rʌn/\n\nv. 跑；经营\nn. 跑步\n\n过去式: ran；过去分词: run\n\nI run every day.\n我每天跑步。',
    ]);
    w.unmount();
  });

  it('plain-text projection drops empty groups, audio ids and in-field line breaks', () => {
    const text = entryPlainText(entry({ word: ' run\n', phonetics: [{ accent: 'us', ipa: '', audioId: 'a' }], parts: [{ pos: '', means: ['跑\r\n步', ''] }], forms: [], examples: [] }));
    expect(text).toBe('run\n\n跑 步');
    expect(entryPlainText(entry())).not.toContain('a1');
  });

  it('plain-text projection keeps hostile markup as literal text', () => {
    expect(entryPlainText(entry({ word: '<script>x</script>', phonetics: [], parts: [], forms: [], examples: [] }))).toBe('<script>x</script>');
  });

  it('vocab projection carries word, language, source, phonetics, meanings, forms and examples without audio ids', () => {
    const v = entryVocabProjection(entry(), 'en', 'youdao/dictionary');
    expect(v).toEqual({
      word: 'run',
      lang: 'en',
      source: 'youdao/dictionary',
      phonetics: [{ accent: 'uk', ipa: 'rʌn' }, { accent: 'us', ipa: 'rʌn' }],
      meanings: [{ pos: 'v.', means: ['跑', '经营'] }, { pos: 'n.', means: ['跑步'] }],
      forms: [{ name: '过去式', value: 'ran' }, { name: '过去分词', value: 'run' }],
      examples: [{ src: 'I run every day.', dst: '我每天跑步。' }],
    });
    expect(JSON.stringify(v)).not.toContain('audio');
  });
});
