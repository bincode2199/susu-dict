import { reactive } from 'vue';
import type { CaptureView, CardPatch, ErrorBarView, OcrView, SettingsView, SpeechBarView, SpeechStateView, TranscribeCueView, TranscribeView, TranslationSnapshot, TrayView, UiSnapshot, VoiceView, WindowView } from '@protocol/ui';
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
  /** Floating Selection window: the current capture (origin, empty). */
  capture: CaptureView | null;
  /** Failure bar: the lines to show. */
  errorBar: ErrorBarView | null;
  /** F10.2: the one player's state (result windows and the pronunciation bar). */
  speech: SpeechStateView | null;
  /** Pronunciation bar: its service squares (default first) and the active one. */
  speechBar: SpeechBarView | null;
  /** F11.3 OCR window: the recognition state, text blocks and preview of the current capture. */
  ocr: OcrView | null;
  /** F12.3 voice window: the recording / transcription state (phases and numbers only, never audio). */
  voice: VoiceView | null;
  /** F14.4 Transcribe window: the file, job phase, stage progress and the incremental cue list (never a path or a media token). */
  transcribe: TranscribeView | null;
  hiddenCount: number;
}

export function createStore(onWindow?: (view: WindowView) => void): { state: UiState; inbound: Inbound } {
  const state = reactive<UiState>({ window: null, translation: null, settings: null, tray: null, capture: null, errorBar: null, speech: null, speechBar: null, ocr: null, voice: null, transcribe: null, hiddenCount: 0 });
  const cardRevision = new Map<string, number>();
  let latestRevision = 0;
  let early: CardPatch[] = []; // patches of a newer generation that arrived before its snapshot event

  const adoptTranslation = (snapshot: TranslationSnapshot | null) => {
    state.translation = snapshot;
    cardRevision.clear();
    latestRevision = snapshot?.revision ?? 0;
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
    // Only this card changes: other cards keep their text and expanded state (UI04). The shared offline
    // notice follows the host's session-wide flag carried by the newest patch.
    const index = state.translation.cards.findIndex((card) => card.serviceId === patch.card.serviceId);
    if (index >= 0) state.translation.cards[index] = patch.card;
    if (patch.revision >= latestRevision) {
      latestRevision = patch.revision;
      state.translation.offline = patch.offline ?? false;
    }
  };

  // F14.4: view events carry no cues (they arrive one by one as transcribe.cue); an empty list of the same job keeps what the page has.
  const adoptTranscribe = (view: TranscribeView) => {
    const same = state.transcribe !== null && state.transcribe.jobId !== undefined && state.transcribe.jobId !== null && state.transcribe.jobId === view.jobId;
    state.transcribe = view.cues.length === 0 && same ? { ...view, cues: state.transcribe!.cues } : view;
  };
  const upsertCue = (cue: TranscribeCueView) => {
    if (!state.transcribe) return;
    const cues = state.transcribe.cues;
    const index = cues.findIndex((c) => c.id === cue.id);
    if (index >= 0) cues[index] = cue; else cues.push(cue);
  };

  const inbound: Inbound = {
    onSnapshot(payload) {
      const snapshot = payload as UiSnapshot;
      state.window = snapshot.window;
      state.settings = snapshot.settings ?? null;
      state.tray = snapshot.tray ?? null;
      state.capture = snapshot.capture ?? null;
      state.errorBar = snapshot.errorBar ?? null;
      state.speech = snapshot.speech ?? null;
      state.speechBar = snapshot.speechBar ?? null;
      state.ocr = snapshot.ocr ?? null;
      state.voice = snapshot.voice ?? null;
      state.transcribe = snapshot.transcribe ?? null;
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
      else if (name === 'capture') state.capture = payload as CaptureView;
      else if (name === 'errorbar') state.errorBar = payload as ErrorBarView;
      else if (name === 'ocr') state.ocr = payload as OcrView;
      else if (name === 'voice') state.voice = payload as VoiceView;
      else if (name === 'transcribe') adoptTranscribe(payload as TranscribeView);
      else if (name === 'transcribe.cue') upsertCue(payload as TranscribeCueView);
      else if (name === 'speech') {
        const next = payload as SpeechStateView;
        if (!state.speech || next.generation >= state.speech.generation) state.speech = next; // never step back to an older request
      } else if (name === 'speechbar') state.speechBar = payload as SpeechBarView;
      else if (name === 'window') {
        state.window = payload as WindowView;
        onWindow?.(state.window);
      } else if (name === 'window.hidden') state.hiddenCount++;
    },
  };
  return { state, inbound };
}
