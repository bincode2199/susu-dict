// F15.4: the card favorite star (state, local-only note, toggle, failure, unavailable) and the SetVocab page (no-target note, per-target state and
// counts, last error, sync / retry failed / queue existing, Uncertain and Failed rows with the manual check, export with path and error, the
// unfavorite note). Fake bridge only: no Anki, no Eudic and no save dialog on this machine; those are host ports (see VocabWindowTests).
import { afterEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import type { CardSnapshot, CommandResult, SettingsView, VocabSettingsView, VocabTargetView } from '@protocol/ui';
import ResultCard from '../src/components/ResultCard.vue';
import SettingsWindow from '../src/windows/SettingsWindow.vue';
import { createStore } from '../src/bridge/store';
import { setLocale, t } from '../src/locales/i18n';

setLocale('zh-Hans');

function fakeBridge(answer: (name: string, payload?: any) => CommandResult = () => ({ ok: true })) {
  const calls: { name: string; payload?: any }[] = [];
  return { calls, command: vi.fn(async (name: string, payload?: object) => { calls.push({ name, payload }); return answer(name, payload); }) };
}
afterEach(() => { document.body.innerHTML = ''; });

const card = (word = 'run'): CardSnapshot => ({
  serviceId: 'youdao/dictionary', displayName: '有道词典', state: 'Ready', collapsed: false, text: '', dictionary: true,
  entry: { word, phonetics: [], parts: [{ pos: 'v.', means: ['跑'] }], forms: [], examples: [] },
});
const collectAnswer = (state: { favorited: boolean; targets: number }) => (name: string, payload?: any): CommandResult => {
  if (name !== 'Vocab.Collect') return { ok: true };
  if (payload?.favorite === true) state.favorited = true;
  if (payload?.favorite === false) state.favorited = false;
  return { ok: true, value: { favorited: state.favorited, targets: state.targets } };
};
const mountCard = (bridge: ReturnType<typeof fakeBridge> | undefined, c = card()) => mount(ResultCard, { props: { card: c, from: 'en', to: 'zh-Hans', ...(bridge ? { bridge: bridge as any } : {}) }, attachTo: document.body });
const star = (w: ReturnType<typeof mountCard>) => w.find('[data-action="favorite"]');

describe('card favorite star', () => {
  it('asks the host for the state when the entry shows and is enabled once the host answers', async () => {
    const bridge = fakeBridge(collectAnswer({ favorited: false, targets: 0 }));
    const w = mountCard(bridge);
    await flushPromises();
    expect(bridge.calls[0]).toEqual({ name: 'Vocab.Collect', payload: { serviceId: 'youdao/dictionary' } });
    expect(star(w).attributes('disabled')).toBeUndefined();
    expect(star(w).attributes('aria-pressed')).toBe('false');
    expect(star(w).attributes('title')).toBe(t('card.favorite'));
    w.unmount();
  });

  it('favorite: the star turns on, and with no sync target it says the word is saved on this computer only; unfavorite turns it off', async () => {
    const bridge = fakeBridge(collectAnswer({ favorited: false, targets: 0 }));
    const w = mountCard(bridge);
    await flushPromises();
    await star(w).trigger('click');
    await flushPromises();
    expect(bridge.calls.at(-1)).toEqual({ name: 'Vocab.Collect', payload: { serviceId: 'youdao/dictionary', favorite: true } });
    expect(star(w).attributes('aria-pressed')).toBe('true');
    expect(star(w).attributes('title')).toBe(t('card.favoritedLocal'));
    await star(w).trigger('click');
    await flushPromises();
    expect(bridge.calls.at(-1)!.payload.favorite).toBe(false);
    expect(star(w).attributes('aria-pressed')).toBe('false');
    w.unmount();
  });

  it('with a target the favorited title promises sync but not a remote delete', async () => {
    const w = mountCard(fakeBridge(collectAnswer({ favorited: true, targets: 2 })));
    await flushPromises();
    expect(star(w).attributes('aria-pressed')).toBe('true');
    expect(star(w).attributes('title')).toBe(t('card.favorited'));
    expect(t('card.favorited')).toContain('不会删除');
    w.unmount();
  });

  it('a failed save keeps the old state and offers a retry; a new entry asks again', async () => {
    let fail = true;
    const state = { favorited: false, targets: 0 };
    const bridge = fakeBridge((name, payload) => (payload?.favorite !== undefined && fail ? { ok: false, error: 'storage' } : collectAnswer(state)(name, payload)));
    const w = mountCard(bridge);
    await flushPromises();
    await star(w).trigger('click');
    await flushPromises();
    expect(star(w).attributes('aria-pressed')).toBe('false');
    expect(star(w).attributes('title')).toBe(t('card.favoriteFailed'));
    fail = false;
    await star(w).trigger('click');
    await flushPromises();
    expect(star(w).attributes('aria-pressed')).toBe('true');
    const before = bridge.calls.length;
    await w.setProps({ card: card('walk') });
    await flushPromises();
    expect(bridge.calls.length).toBe(before + 1); // the state of the new word is asked
    w.unmount();
  });

  it('stays greyed with the unavailable title when the host has no vocabulary service or there is no bridge', async () => {
    const refused = fakeBridge(() => ({ ok: false, error: 'unavailable' }));
    const w = mountCard(refused);
    await flushPromises();
    expect(star(w).attributes('disabled')).toBeDefined();
    expect(star(w).attributes('title')).toBe(t('card.needsVocab'));
    await star(w).trigger('click');
    expect(refused.calls).toHaveLength(1); // no toggle was sent
    w.unmount();
    const bare = mountCard(undefined);
    await flushPromises();
    expect(star(bare).attributes('disabled')).toBeDefined();
    bare.unmount();
  });
});

const target = (over: Partial<VocabTargetView> = {}): VocabTargetView => ({
  instanceId: 'ankiconnect', enabled: true, availability: 'Ready', usable: true, reasonKey: undefined, origin: 'http://127.0.0.1:8765', local: true, lookup: true,
  pending: 0, retrying: 0, failed: 0, uncertain: 0, succeeded: 0, lastError: undefined, passFailed: false, ...over,
});
const vocab = (over: Partial<VocabSettingsView> = {}): VocabSettingsView => ({ favorites: 3, targets: [target(), target({ instanceId: 'eudic', enabled: false, availability: 'Disabled', usable: false, reasonKey: 'vocab.reason.disabled', origin: 'https://api.frdic.com:443', local: false })], rows: [], export: undefined, canExport: true, ...over });
const settings = (v: VocabSettingsView): SettingsView => ({
  revision: 4, fileHash: 'h', issues: [], hotkeys: [], accounts: [], services: [], vocab: v,
  general: { uiLanguage: 'zh-Hans', sourceLanguage: 'en', targetLanguage: 'zh-Hans', defaultExpandedCards: 2, allowClipboardBorrowing: false, closeAction: 'hide', launchAtStartup: false },
  network: { proxyMode: 'system', proxyHost: '', proxyPort: 0, proxyUsername: '', proxyPasswordSaved: false, aiTimeoutSeconds: 30 },
});
async function mountPage(v: VocabSettingsView, answer?: (name: string, payload?: any) => CommandResult) {
  const { state } = createStore();
  state.window = { kind: 'Settings', uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features: [] };
  state.settings = settings(v);
  const bridge = fakeBridge(answer);
  const wrapper = mount(SettingsWindow, { props: { bridge: bridge as any, state }, attachTo: document.body });
  await wrapper.findAll('.nav-item').find((n) => n.text() === t('nav.vocab'))!.trigger('click');
  return { wrapper, bridge, state };
}
const withView = (next: VocabSettingsView) => ({ ok: true, value: settings(next) }) as CommandResult;

describe('SetVocab page', () => {
  it('lists each target with its state, address and counts, the favorites count and the unfavorite note', async () => {
    const { wrapper } = await mountPage(vocab({ targets: [target({ pending: 2, failed: 1, uncertain: 1, succeeded: 5 }), vocab().targets[1]] }));
    expect(wrapper.find('[data-vocab-count]').text()).toBe(t('vocab.favorites', { n: 3 }));
    expect(wrapper.find('[data-vocab-note]').text()).toBe(t('vocab.unfavoriteNote'));
    expect(wrapper.find('[data-vocab-no-targets]').exists()).toBe(false);
    const anki = wrapper.find('[data-vocab-target="ankiconnect"]');
    expect(anki.find('.hint').text()).toContain(t('vocab.target.usable'));
    expect(anki.find('[data-vocab-address]').text()).toContain('http://127.0.0.1:8765');
    expect(anki.find('[data-vocab-address]').text()).toContain(t('vocab.target.local'));
    expect(anki.find('[data-vocab-counts]').text()).toContain(t('vocab.count.pending', { n: 2 }));
    expect(anki.find('[data-vocab-counts]').text()).toContain(t('vocab.count.uncertain', { n: 1 }));
    expect(anki.find('[data-vocab-counts]').text()).toContain(t('vocab.count.succeeded', { n: 5 }));
    const eudic = wrapper.find('[data-vocab-target="eudic"]');
    expect(eudic.find('.hint').text()).toContain(t('vocab.reason.disabled'));
    expect(eudic.find('[data-vocab-sync]').attributes('disabled')).toBeDefined(); // nothing to sync to a target that cannot run
  });

  it('says plainly when no target is usable: the favorites stay on this computer and can be exported', async () => {
    const { wrapper } = await mountPage(vocab({ targets: vocab().targets.map((x) => ({ ...x, usable: false })) }));
    expect(wrapper.find('[data-vocab-no-targets]').text()).toBe(t('vocab.noTargets'));
  });

  it('every reason a target cannot run has its own text, not the key', async () => {
    for (const reason of ['vocab.reason.missingKey', 'vocab.reason.notGranted', 'vocab.reason.originNotLocal', 'vocab.reason.noRuntime']) {
      expect(t(reason)).not.toBe(reason);
      const { wrapper } = await mountPage(vocab({ targets: [target({ usable: false, reasonKey: reason })] }));
      expect(wrapper.find('[data-vocab-target="ankiconnect"] .hint').text()).toContain(t(reason));
      wrapper.unmount();
    }
  });

  it('shows the last error code of a target and a pass that did not finish', async () => {
    const { wrapper } = await mountPage(vocab({ targets: [target({ failed: 2, lastError: 'auth', passFailed: true })] }));
    expect(wrapper.find('[data-vocab-last-error]').text()).toBe(t('vocab.lastError', { error: t('error.auth') }));
    expect(wrapper.find('[data-vocab-pass-failed]').text()).toBe(t('vocab.passFailed'));
  });

  it('sync now, retry failed and queue existing send their command for that target and adopt the answer', async () => {
    const next = vocab({ favorites: 9, targets: [target({ failed: 1 }), vocab().targets[1]] });
    const { wrapper, bridge, state } = await mountPage(vocab({ targets: [target({ failed: 1 }), vocab().targets[1]] }), () => withView(next));
    expect(wrapper.find('[data-vocab-target="eudic"] [data-vocab-retry]').exists()).toBe(false); // nothing failed there
    await wrapper.find('[data-vocab-target="ankiconnect"] [data-vocab-sync]').trigger('click');
    await flushPromises();
    expect(bridge.calls.at(-1)).toEqual({ name: 'Vocab.Sync', payload: { action: 'sync', target: 'ankiconnect' } });
    await wrapper.find('[data-vocab-retry]').trigger('click');
    await flushPromises();
    expect(bridge.calls.at(-1)).toEqual({ name: 'Vocab.Sync', payload: { action: 'retryFailed', target: 'ankiconnect' } });
    await wrapper.find('[data-vocab-queue]').trigger('click');
    await flushPromises();
    expect(bridge.calls.at(-1)).toEqual({ name: 'Vocab.Sync', payload: { action: 'queueExisting', target: 'ankiconnect' } });
    expect(state.settings!.vocab!.favorites).toBe(9);
  });

  it('Uncertain and Failed rows: the user says delivered or not delivered, with the same entry, target and revision', async () => {
    const rows = [{ entryId: 'e1', word: 'apple', target: 'eudic', revision: 2, state: 'Uncertain', attempts: 1 }, { entryId: 'e2', word: 'pear', target: 'ankiconnect', revision: 1, state: 'Failed', attempts: 6 }];
    const { wrapper, bridge } = await mountPage(vocab({ rows }), () => withView(vocab({ rows: [rows[1]] })));
    const list = wrapper.findAll('[data-vocab-rows] li');
    expect(list).toHaveLength(2);
    expect(list[0].text()).toContain('apple');
    expect(list[0].text()).toContain(t('vocab.row.state.Uncertain'));
    expect(list[1].text()).toContain(t('vocab.row.state.Failed'));
    expect(wrapper.text()).toContain(t('vocab.rows.hint'));
    await list[0].find('[data-vocab-delivered]').trigger('click');
    await flushPromises();
    expect(bridge.calls.at(-1)).toEqual({ name: 'Vocab.Resolve', payload: { entryId: 'e1', target: 'eudic', revision: 2, delivered: true } });
    expect(wrapper.findAll('[data-vocab-rows] li')).toHaveLength(1); // the answer removed the row
    await wrapper.find('[data-vocab-not-delivered]').trigger('click');
    await flushPromises();
    expect(bridge.calls.at(-1)).toEqual({ name: 'Vocab.Resolve', payload: { entryId: 'e2', target: 'ankiconnect', revision: 1, delivered: false } });
  });

  it('no rows: no manual-check list at all', async () => {
    const { wrapper } = await mountPage(vocab());
    expect(wrapper.find('[data-vocab-rows]').exists()).toBe(false);
  });

  it('export: the chosen format and content go to the host; the field choice and deck apply per format; the saved path is shown', async () => {
    const saved = vocab({ export: { format: 'csv', path: 'C:\\Docs\\words.csv', retryable: false, exported: 3, skipped: 1, issues: ['x'] } });
    const { wrapper, bridge } = await mountPage(vocab(), () => withView(saved));
    expect(wrapper.find('[data-vocab-definitions]').attributes('disabled')).toBeDefined(); // txt holds the words only
    await wrapper.find('[data-vocab-format]').setValue('csv');
    expect(wrapper.find('[data-vocab-definitions]').attributes('disabled')).toBeUndefined();
    await wrapper.find('[data-vocab-examples]').setValue(false);
    await wrapper.find('[data-vocab-only-new]').setValue(true);
    expect(wrapper.find('[data-vocab-deck]').exists()).toBe(false);
    await wrapper.find('[data-vocab-export]').trigger('click');
    await flushPromises();
    expect(bridge.calls.at(-1)).toEqual({ name: 'Vocab.Export', payload: { format: 'csv', definitions: true, phonetics: true, examples: false, onlyNew: true } });
    expect(wrapper.find('[data-vocab-export-path]').text()).toBe(t('vocab.export.saved', { path: 'C:\\Docs\\words.csv', n: 3 }));
    expect(wrapper.find('[data-vocab-export-result]').text()).toContain(t('vocab.export.skipped', { n: 1 }));
    await wrapper.find('[data-vocab-format]').setValue('apkg');
    await wrapper.find('[data-vocab-deck]').setValue('My deck');
    await wrapper.find('[data-vocab-export]').trigger('click');
    await flushPromises();
    expect(bridge.calls.at(-1)!.payload).toMatchObject({ format: 'apkg', deck: 'My deck' });
  });

  it('export error: the code gets its own message; no content column chosen disables CSV; no save dialog disables export with the reason', async () => {
    const failed = vocab({ export: { format: 'txt', path: undefined, error: 'export.diskFull', retryable: true, exported: 0, skipped: 0, issues: [] } });
    const { wrapper } = await mountPage(failed);
    expect(wrapper.find('[data-vocab-export-error]').text()).toBe(t('vocab.export.error.export.diskFull'));
    expect(wrapper.find('[data-vocab-export-result]').classes()).toContain('error-text');
    await wrapper.find('[data-vocab-format]').setValue('csv');
    for (const id of ['definitions', 'phonetics', 'examples']) await wrapper.find(`[data-vocab-${id}]`).setValue(false);
    expect(wrapper.find('[data-vocab-export]').attributes('disabled')).toBeDefined();
    wrapper.unmount();

    const none = await mountPage(vocab({ canExport: false }));
    expect(none.wrapper.find('[data-vocab-export]').attributes('disabled')).toBeDefined();
    expect(none.wrapper.text()).toContain(t('vocab.export.noDialog'));
  });

  it('a refused command shows one error line and does not change the page', async () => {
    const { wrapper } = await mountPage(vocab(), () => ({ ok: false, error: 'not-usable' }));
    await wrapper.find('[data-vocab-sync]').trigger('click');
    await flushPromises();
    expect(wrapper.find('[data-vocab-error]').text()).toBe(t('vocab.failed'));
  });
});
