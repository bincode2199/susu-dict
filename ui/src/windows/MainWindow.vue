<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { UI_COMMANDS } from '@protocol/ui';
import type { Bridge } from '../bridge/bridge';
import type { UiState } from '../bridge/store';
import TitleBar from '../components/TitleBar.vue';
import SourceCard from '../components/SourceCard.vue';
import ResultCard from '../components/ResultCard.vue';
import { t } from '../locales/i18n';

// Main window 520×700 (DESIGN 9): title bar, source card and one result card per service, 32 px status bar.
const props = defineProps<{ bridge: Bridge; state: UiState }>();
const text = ref('');
const available = computed(() => props.state.window?.features.includes('input-translation') ?? false);
const from = ref('en');
const to = ref('zh-Hans');
watch(() => props.state.translation, (snapshot) => {
  if (!snapshot) return;
  from.value = snapshot.from || from.value;
  to.value = snapshot.to || to.value;
}, { immediate: true });

function submit(): void {
  if (!available.value || !text.value.trim()) return;
  void props.bridge.command(UI_COMMANDS.SubmitText, { text: text.value });
}
function setLanguages(source: string, target: string): void {
  from.value = source;
  to.value = target;
  void props.bridge.command(UI_COMMANDS.SelectLanguage, { from: source, to: target });
}
</script>

<template>
  <div class="window">
    <TitleBar :title="t('app.name')" show-pin show-settings maximizable :pinned="state.window?.pinned"
      @minimize="bridge.command(UI_COMMANDS.Minimize)" @maximize="bridge.command(UI_COMMANDS.Maximize)" @close="bridge.command(UI_COMMANDS.Close)"
      @pin="bridge.command(UI_COMMANDS.Pin)" @settings="bridge.command(UI_COMMANDS.OpenSettings)" />
    <main class="content">
      <p v-if="state.window?.devPreview" class="preview-note" role="note">{{ t('app.devPreview') }}</p>
      <p v-if="!available" class="preview-note" role="status">{{ t('main.unavailable') }}</p>
      <SourceCard v-model="text" :from="from" :to="to" :disabled="!available" @submit="submit" @swap="setLanguages(to, from)" @language="setLanguages" />
      <ResultCard v-for="card in state.translation?.cards ?? []" :key="card.serviceId" :card="card" :from="from" :to="to"
        @toggle="bridge.command(UI_COMMANDS.ToggleCard, { serviceId: card.serviceId })"
        @retry="bridge.command(UI_COMMANDS.RetryCard, { serviceId: card.serviceId })"
        @copy="bridge.command(UI_COMMANDS.CopyText, { text: card.text })"
        @settings="bridge.command(UI_COMMANDS.OpenSettings)" />
    </main>
    <footer class="statusbar"><span>{{ t('main.statusHint') }}</span></footer>
  </div>
</template>

<style scoped>
.window { height: 100%; display: flex; flex-direction: column; }
.content { flex: 1; overflow-y: auto; padding: 14px; display: flex; flex-direction: column; gap: 10px; }
.preview-note { margin: 0; font-size: 11px; color: var(--hint); border: 1px dashed var(--line-accent); border-radius: var(--radius-tag); padding: 4px 8px; }
.statusbar { height: 32px; flex: none; display: flex; align-items: center; padding: 0 14px; border-top: 1px solid var(--line); font-size: 11px; color: var(--hint); }
</style>
