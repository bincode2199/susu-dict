<script setup lang="ts">
import { computed } from 'vue';
import { UI_COMMANDS } from '@protocol/ui';
import type { Bridge } from '../bridge/bridge';
import type { UiState } from '../bridge/store';
import Icon from '../components/Icon.vue';
import { serviceName, t } from '../locales/i18n';

// Pronunciation bar (DESIGN 9 "发音浮条", PLAN 6.5): height 34, radius 6, padding 0 5. Left the "reading" status key
// (ink), a 1 px divider, then one 26×26 square per pronunciation service (radius 5, 10.5 px abbreviation). The first
// square is the default service (border #C9CDD3, ink text); the others use the hairline border and secondary text.
// The host places it next to the selection without taking focus; the status key stops playback while it runs and reads
// again afterwards; a square reads the same text with that service. Nothing here holds the text.
const props = defineProps<{ bridge: Bridge; state: UiState }>();
const bar = computed(() => props.state.speechBar);
// Only the bar's own playback drives it; a card read aloud meanwhile leaves it stopped.
const phase = computed(() => (props.state.speech?.target === 'bar' ? props.state.speech.phase : 'stopped'));
const busy = computed(() => phase.value === 'loading' || phase.value === 'playing');
const errorText = computed(() => {
  const speech = props.state.speech;
  if (speech?.target !== 'bar' || speech.phase !== 'error') return '';
  return speech.device ? t(`speech.device.${speech.device}`) : t(`error.${speech.error ?? 'unavailable'}`);
});
const statusLabel = computed(() => (busy.value ? t('speech.stop') : errorText.value || t('speech.replay')));
const abbr = (instance: string) => {
  const key = `speech.abbr.${instance}`;
  const text = t(key);
  return text === key ? serviceName(instance).slice(0, 2) : text;
};

function status(): void {
  if (busy.value) void props.bridge.command(UI_COMMANDS.SpeechStop, {});
  else if (bar.value) void props.bridge.command(UI_COMMANDS.SpeechPlay, { instance: bar.value.active });
}
function play(instance: string): void {
  void props.bridge.command(UI_COMMANDS.SpeechPlay, { instance });
}
</script>

<template>
  <div class="window bar" role="toolbar" :aria-label="t('speech.bar')" :data-phase="phase">
    <button type="button" class="status" :class="{ busy, error: phase === 'error' }" data-action="status"
      :aria-label="statusLabel" :title="statusLabel" @click="status">
      <Icon :name="phase === 'error' ? 'warning' : busy ? (phase === 'playing' ? 'stop' : 'audio') : 'audio'" :size="15" />
    </button>
    <span class="divider" aria-hidden="true" />
    <button v-for="service in bar?.services ?? []" :key="service.instance" type="button" class="service"
      :class="{ default: service.default, active: service.instance === bar?.active && busy }" :data-instance="service.instance"
      :aria-pressed="service.instance === bar?.active && busy" :aria-label="t('speech.playWith', { name: serviceName(service.instance) })"
      :title="serviceName(service.instance)" @click="play(service.instance)">
      {{ abbr(service.instance) }}
    </button>
    <span class="sr-only" role="status" aria-live="polite">{{ busy ? t(`speech.phase.${phase}`) : errorText }}</span>
  </div>
</template>

<style scoped>
.bar { height: 34px; display: flex; align-items: center; gap: 4px; padding: 0 5px; border-radius: 6px; overflow: hidden; }
.status, .service { width: 26px; height: 26px; flex: none; display: grid; place-items: center; padding: 0; background: transparent; cursor: pointer; }
.status { border: none; border-radius: 5px; color: var(--ink); }
.status.error { color: var(--error); }
.divider { width: 1px; height: 18px; background: var(--line); flex: none; }
.service { border: 1px solid var(--line); border-radius: 5px; font-size: 10.5px; line-height: 1; color: var(--ink-secondary); }
.service.default { border-color: #c9cdd3; color: var(--ink); }
.service.active { color: var(--ink); border-color: var(--ink-secondary); }
.service:hover { color: var(--ink); }
.sr-only { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; }
</style>
