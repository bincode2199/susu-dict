<script setup lang="ts">
import { defineAsyncComponent, onBeforeUnmount, onMounted } from 'vue';
import { UI_COMMANDS, type WindowKind } from '@protocol/ui';
import type { Bridge } from './bridge/bridge';
import type { UiState } from './bridge/store';

// One root per window kind, each loaded on demand (ARCHITECTURE 9: per-window chunks).
const props = defineProps<{ kind: WindowKind; bridge: Bridge; state: UiState }>();
const roots: Partial<Record<WindowKind, ReturnType<typeof defineAsyncComponent>>> = {
  Main: defineAsyncComponent(() => import('./windows/MainWindow.vue')),
  Settings: defineAsyncComponent(() => import('./windows/SettingsWindow.vue')),
  Tray: defineAsyncComponent(() => import('./windows/TrayMenu.vue')),
  // Selection and clipboard translation share one floating window shell (DESIGN 9).
  Selection: defineAsyncComponent(() => import('./windows/SelectionWindow.vue')),
  Clipboard: defineAsyncComponent(() => import('./windows/SelectionWindow.vue')),
  Error: defineAsyncComponent(() => import('./windows/ErrorBar.vue')),
  // F11.3: the OCR result window (DESIGN 9 "结果窗", Ocr artboard).
  Ocr: defineAsyncComponent(() => import('./windows/OcrWindow.vue')),
  // F12.3: the voice window (DESIGN 9, Voice artboard).
  Voice: defineAsyncComponent(() => import('./windows/VoiceWindow.vue')),
  // F14.4: the video transcription window (DESIGN 9, Transcribe artboard).
  Transcribe: defineAsyncComponent(() => import('./windows/TranscribeWindow.vue')),
  // F10.2: the pronunciation bar next to the selection (DESIGN 9).
  Speech: defineAsyncComponent(() => import('./windows/SpeechBar.vue')),
};

// Esc: an open popup or a field handling Esc itself (hotkey recorder, select) goes first; otherwise the window
// closes by its own rules (ARCHITECTURE 9, UI03). IME composition never closes anything.
function onKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Escape' || event.defaultPrevented || event.isComposing) return;
  const active = document.activeElement;
  if (active instanceof HTMLSelectElement) return;
  event.preventDefault();
  void props.bridge.command(UI_COMMANDS.Close);
}
onMounted(() => window.addEventListener('keydown', onKeydown));
onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown));
</script>

<template>
  <component :is="roots[kind]" v-if="roots[kind] && state.window" :bridge="bridge" :state="state" />
</template>
