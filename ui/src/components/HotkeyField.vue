<script setup lang="ts">
import { ref } from 'vue';
import { t } from '../locales/i18n';
import { chordFromEvent } from './chords';

// DESIGN 7 hotkey box: 26 px, 5 px radius, 11 px; accent border while recording, error border on conflict.
// Records Ctrl/Alt/Shift/Win + one key into the chord format the host parses (e.g. "Ctrl+Alt+T").
const props = defineProps<{ modelValue: string; label: string; invalid?: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [string] }>();
const recording = ref(false);

function onKeydown(event: KeyboardEvent): void {
  if (!recording.value) return;
  event.preventDefault();
  if (event.key === 'Escape') { event.stopPropagation(); recording.value = false; return; }
  const chord = chordFromEvent(event);
  if (chord) {
    emit('update:modelValue', chord);
    recording.value = false;
  }
}
</script>

<template>
  <button type="button" class="hotkey" :class="{ recording, invalid }" :aria-label="t('hotkeys.edit', { name: label })" @click="recording = !recording" @keydown="onKeydown" @blur="recording = false">
    {{ recording ? t('hotkeys.recording') : modelValue || '—' }}
  </button>
  <button v-if="props.modelValue" type="button" class="btn clear" @click="emit('update:modelValue', '')">{{ t('hotkeys.clear') }}</button>
</template>

<style scoped>
.hotkey { min-width: 120px; height: 26px; border: 1px solid var(--button-line); border-radius: var(--radius-icon); background: var(--paper); font-size: 11px; padding: 0 10px; text-align: center; }
.hotkey.recording { border-color: var(--accent); }
.hotkey.invalid { border-color: var(--error); }
.clear { height: 26px; }
</style>
