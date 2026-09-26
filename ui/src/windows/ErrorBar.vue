<script setup lang="ts">
import { computed, watch } from 'vue';
import { UI_COMMANDS } from '@protocol/ui';
import type { Bridge } from '../bridge/bridge';
import type { UiState } from '../bridge/store';
import Icon from '../components/Icon.vue';
import { t } from '../locales/i18n';

// Failure bar (DESIGN 9 "悬浮条", Error artboard 01): the same paper as a popup, one 34 px row per line with a 15 px
// warning icon, 12 px text and an optional 11 px link. It appears next to the pointer without taking focus and the
// host hides it after 4 s. The texts are host resources keyed by the shell, so the two capture failures never mix.
const props = defineProps<{ bridge: Bridge; state: UiState }>();
const ROW = 34;
const lines = computed(() => props.state.errorBar?.lines ?? []);
// The restore notice's link reads "capture settings"; the others read "go to settings" (Error artboard 01).
const linkLabel = (key: string) => t(key === 'capture.restoreFailed' ? 'capture.link.captureSettings' : 'capture.link.settings');

watch(() => props.state.errorBar?.id, () => {
  if (lines.value.length > 0) void props.bridge.command(UI_COMMANDS.FitContent, { heightDip: ROW * lines.value.length });
}, { immediate: true });
</script>

<template>
  <div class="window bar" role="alert">
    <p v-for="(line, index) in lines" :key="`${state.errorBar?.id}-${index}`" class="line">
      <Icon name="warning" :size="15" />
      <span class="text">{{ t(line.key) }}</span>
      <button v-if="line.link === 'settings'" type="button" class="link" @click="bridge.command(UI_COMMANDS.OpenSettings)">{{ linkLabel(line.key) }}</button>
    </p>
  </div>
</template>

<style scoped>
.bar { height: 100%; display: flex; flex-direction: column; overflow: hidden; }
.line { height: 34px; flex: none; margin: 0; display: flex; align-items: center; gap: 8px; padding: 0 10px; color: var(--ink); }
.line + .line { border-top: 1px solid var(--line); }
.text { font-size: 12px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.link { margin-left: auto; flex: none; border: none; background: transparent; padding: 0; font-size: 11px; color: var(--accent); cursor: pointer; }
.link:hover { color: var(--accent-hover); }
</style>
