import { reactive } from 'vue';
import type { CardPatch, SettingsView, TranslationSnapshot, TrayView, UiSnapshot, WindowView } from '@protocol/ui';
import type { Inbound } from './bridge';

/**
 * Projection of host state for one window. The page never owns task truth: it applies snapshots and patches
 * and keeps only unsaved form input locally (ARCHITECTURE 6).
 */
export interface UiState {
  window: WindowView | null;
  translation: TranslationSnapshot | null;
  settings: SettingsView | null;
  tray: TrayView | null;
  hiddenCount: number;
}

export function createStore(onWindow?: (view: WindowView) => void): { state: UiState; inbound: Inbound } {
  const state = reactive<UiState>({ window: null, translation: null, settings: null, tray: null, hiddenCount: 0 });
  const cardRevision = new Map<string, number>();
  let early: CardPatch[] = []; // patches of a newer generation that arrived before its snapshot event

  const adoptTranslation = (snapshot: TranslationSnapshot | null) => {
    state.translation = snapshot;
    cardRevision.clear();
    if (!snapshot) return;
    for (const card of snapshot.cards) cardRevision.set(card.serviceId, snapshot.revision);
    const replay = early.filter((p) => p.generation === snapshot.generation).sort((a, b) => a.revision - b.revision);
    early = early.filter((p) => p.generation > snapshot.generation);
    for (const patch of replay) applyCard(patch);
  };

  const applyCard = (patch: CardPatch) => {
    if (!state.translation) return;
    if (patch.generation > state.translation.generation) { early.push(patch); return; }
    if (patch.generation < state.translation.generation) return; // late result of an older job
    if ((cardRevision.get(patch.card.serviceId) ?? -1) >= patch.revision) return;
    cardRevision.set(patch.card.serviceId, patch.revision);
    const index = state.translation.cards.findIndex((card) => card.serviceId === patch.card.serviceId);
    if (index >= 0) state.translation.cards[index] = patch.card;
  };

  const inbound: Inbound = {
    onSnapshot(payload) {
      const snapshot = payload as UiSnapshot;
      state.window = snapshot.window;
      state.settings = snapshot.settings ?? null;
      state.tray = snapshot.tray ?? null;
      early = [];
      adoptTranslation(snapshot.translation ?? null);
      onWindow?.(snapshot.window);
    },
    onPatch(name, payload) {
      if (name === 'card') applyCard(payload as CardPatch);
    },
    onEvent(name, payload) {
      if (name === 'settings') state.settings = payload as SettingsView;
      else if (name === 'translation') adoptTranslation(payload as TranslationSnapshot);
      else if (name === 'window') {
        state.window = payload as WindowView;
        onWindow?.(state.window);
      } else if (name === 'window.hidden') state.hiddenCount++;
    },
  };
  return { state, inbound };
}
