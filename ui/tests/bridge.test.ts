import { describe, expect, it, vi } from 'vitest';
import type { CardPatch, TranslationSnapshot, UiSnapshot } from '@protocol/ui';
import { Bridge, windowFromQuery, type HostPort } from '../src/bridge/bridge';
import { createStore } from '../src/bridge/store';

function fakeHost() {
  const sent: any[] = [];
  let listener: ((event: MessageEvent) => void) | null = null;
  const host: HostPort = { postMessage: (m) => sent.push(JSON.parse(m)), addEventListener: (_t, l) => (listener = l) };
  const deliver = (data: object) => listener!(new MessageEvent('message', { data }));
  return { host, sent, deliver };
}

const envelope = (session: string, sequence: number, kind: string, extra: object = {}) => ({ uiVersion: 1, windowSessionId: session, sequence, kind, ...extra });
const card = (serviceId: string, state: any, text = '') => ({ serviceId, displayName: serviceId, state, collapsed: false, text });
const translation = (generation: number, revision: number): TranslationSnapshot => ({ revision, generation, sourceText: 's', from: 'en', to: 'zh-Hans', cards: [card('a', 'Loading'), card('b', 'Loading')] });
const snapshot = (t: TranslationSnapshot | null): UiSnapshot => ({ window: { kind: 'Main', uiLanguage: 'zh-Hans', theme: 'light', maximized: false, pinned: false, devPreview: false, features: [] }, translation: t ?? undefined });

describe('Bridge', () => {
  it('sends Ready and correlated whitelisted commands for its own session', async () => {
    const { host, sent, deliver } = fakeHost();
    const bridge = new Bridge(host, 's1', { onSnapshot() {}, onPatch() {}, onEvent() {} });
    bridge.ready();
    expect(sent[0]).toEqual({ uiVersion: 1, kind: 'Ready', windowSessionId: 's1' });
    const pending = bridge.command('Settings.Read');
    const command = sent[1];
    expect(command).toMatchObject({ kind: 'Command', name: 'Settings.Read', windowSessionId: 's1' });
    deliver(envelope('s1', 1, 'Result', { correlationId: command.correlationId, payload: { ok: true } }));
    await expect(pending).resolves.toEqual({ ok: true });
  });

  it('ignores other sessions and versions, drops duplicates and resyncs on a sequence gap', () => {
    const { host, sent, deliver } = fakeHost();
    const events: string[] = [];
    const bridge = new Bridge(host, 's1', { onSnapshot: () => events.push('snapshot'), onPatch: (n) => events.push(`patch:${n}`), onEvent: (n) => events.push(`event:${n}`) });
    deliver(envelope('other', 1, 'Snapshot'));
    deliver({ ...envelope('s1', 1, 'Snapshot'), uiVersion: 2 });
    expect(events).toEqual([]);
    deliver(envelope('s1', 5, 'Snapshot'));
    deliver(envelope('s1', 6, 'Patch', { name: 'card' }));
    deliver(envelope('s1', 6, 'Patch', { name: 'card' })); // duplicate
    deliver(envelope('s1', 9, 'Event', { name: 'settings' })); // gap 7-8
    expect(events).toEqual(['snapshot', 'patch:card']);
    expect(sent.at(-1)).toMatchObject({ kind: 'Ready' }); // asks for a fresh snapshot
    void bridge;
  });

  it('times out a command instead of waiting forever', async () => {
    vi.useFakeTimers();
    const { host } = fakeHost();
    const bridge = new Bridge(host, 's1', { onSnapshot() {}, onPatch() {}, onEvent() {} }, 1000);
    const pending = bridge.command('Window.Close');
    vi.advanceTimersByTime(1001);
    await expect(pending).resolves.toEqual({ ok: false, error: 'timeout' });
    vi.useRealTimers();
  });

  it('reads the window kind, session and language from the page address', () => {
    expect(windowFromQuery('?w=settings&s=abc&l=en')).toEqual({ kind: 'Settings', session: 'abc', language: 'en' });
    expect(windowFromQuery('?w=../../evil')).toMatchObject({ kind: 'Main', session: '' });
  });
});

describe('store', () => {
  const patch = (generation: number, revision: number, id: string, state: any, text: string): CardPatch => ({ generation, revision, card: card(id, state, text) });

  it('applies patches in revision order and ignores stale ones', () => {
    const { state, inbound } = createStore();
    inbound.onSnapshot(snapshot(translation(1, 10)));
    inbound.onPatch('card', patch(1, 12, 'a', 'Ready', 'new'));
    inbound.onPatch('card', patch(1, 11, 'a', 'Streaming', 'old')); // late
    inbound.onPatch('card', patch(0, 20, 'b', 'Ready', 'previous job')); // older generation (J01)
    expect(state.translation!.cards.map((c) => [c.state, c.text])).toEqual([['Ready', 'new'], ['Loading', '']]);
  });

  it('buffers patches of a new generation until its snapshot and replays only newer ones', () => {
    const { state, inbound } = createStore();
    inbound.onSnapshot(snapshot(translation(1, 10)));
    inbound.onPatch('card', patch(2, 21, 'a', 'Streaming', 'par'));
    inbound.onPatch('card', patch(2, 23, 'b', 'Ready', 'done'));
    inbound.onEvent('translation', translation(2, 22));
    expect(state.translation!.generation).toBe(2);
    expect(state.translation!.cards.map((c) => c.state)).toEqual(['Loading', 'Ready']); // rev 21 <= snapshot 22 dropped
  });

  it('counts hidden notices so pages can drop unsaved secret input', () => {
    const { state, inbound } = createStore();
    inbound.onEvent('window.hidden', undefined);
    expect(state.hiddenCount).toBe(1);
  });
});
