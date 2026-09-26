<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue';
import { UI_COMMANDS } from '@protocol/ui';
import type { Bridge } from '../bridge/bridge';
import type { UiState } from '../bridge/store';
import TitleBar from '../components/TitleBar.vue';
import SourceCard from '../components/SourceCard.vue';
import ResultCard from '../components/ResultCard.vue';
import Icon from '../components/Icon.vue';
import { t } from '../locales/i18n';

// Main window 520×700 (DESIGN 9): title bar, source card and one result card per service, 32 px status bar.
const props = defineProps<{ bridge: Bridge; state: UiState }>();
// F10.2: card read-aloud keys (DESIGN 8) need a pronunciation service; a dictionary phonetic may still play its own audio.
const canSpeak = computed(() => props.state.window?.features.includes('pronunciation') ?? false);
function speakCard(serviceId: string, phonetic?: number): void {
  void props.bridge.command(UI_COMMANDS.SpeakCard, phonetic === undefined ? { serviceId } : { serviceId, phonetic });
}
const text = ref('');
const source = ref<InstanceType<typeof SourceCard> | null>(null);
// Typing starts right away when the window opens or is shown again (each show sends a fresh snapshot).
const focusInput = () => nextTick(() => source.value?.focus());
onMounted(focusInput);
watch(() => props.state.window, focusInput);
const available = computed(() => props.state.window?.features.includes('input-translation') ?? false);
const from = ref('en');
const to = ref('zh-Hans');
let generation = -1;
watch(() => props.state.translation, (snapshot) => {
  if (!snapshot) return;
  from.value = snapshot.from || from.value;
  to.value = snapshot.to || to.value;
  // A new generation started by the host (e.g. the floating window's "open in main window") brings its source text.
  if (snapshot.generation !== generation && snapshot.sourceText && snapshot.sourceText !== text.value) text.value = snapshot.sourceText;
  generation = snapshot.generation;
}, { immediate: true });

function submit(): void {
  if (!available.value || !text.value.trim()) return;
  void props.bridge.command(UI_COMMANDS.SubmitText, { text: text.value });
}
/** Changing the language pair re-requests the current text (a new generation) once the host accepts it. */
async function setLanguages(source: string, target: string): Promise<void> {
  from.value = source;
  to.value = target;
  const result = await props.bridge.command(UI_COMMANDS.SelectLanguage, { from: source, to: target });
  if (result.ok && (props.state.translation?.generation ?? 0) > 0 && text.value.trim()) submit();
}
// DESIGN 13 "部分离线": per-card errors stay on their cards; the shared notice appears only when the host
// says every remote path that was asked has failed on the network.
const offline = computed(() => props.state.translation?.offline === true);
</script>

<template>
  <div class="window">
    <TitleBar :title="t('app.name')" show-pin show-settings maximizable :pinned="state.window?.pinned"
      @minimize="bridge.command(UI_COMMANDS.Minimize)" @maximize="bridge.command(UI_COMMANDS.Maximize)" @close="bridge.command(UI_COMMANDS.Close)"
      @pin="bridge.command(UI_COMMANDS.Pin)" @settings="bridge.command(UI_COMMANDS.OpenSettings)" />
    <main class="content">
      <p v-if="state.window?.devPreview" class="preview-note" role="note">{{ t('app.devPreview') }}</p>
      <p v-if="!available" class="preview-note" role="status">{{ t('main.unavailable') }}</p>
      <SourceCard ref="source" v-model="text" :from="from" :to="to" :disabled="!available" @submit="submit" @swap="setLanguages(to, from)" @language="setLanguages" />
      <ResultCard v-for="card in state.translation?.cards ?? []" :key="card.serviceId" :card="card" :from="from" :to="to"
        @toggle="bridge.command(UI_COMMANDS.ToggleCard, { serviceId: card.serviceId })"
        @retry="bridge.command(UI_COMMANDS.RetryCard, { serviceId: card.serviceId })"
        @copy="(text: string) => bridge.command(UI_COMMANDS.CopyText, { text })"
        @settings="bridge.command(UI_COMMANDS.OpenSettings)"
        :speech="state.speech" :can-speak="canSpeak" @speak="(phonetic?: number) => speakCard(card.serviceId, phonetic)"
        @stop-speech="bridge.command(UI_COMMANDS.SpeechStop, {})" />
    </main>
    <footer class="statusbar" :class="{ offline }">
      <span v-if="offline" class="offline-note" role="status"><Icon name="warning" :size="13" />{{ t('main.offline') }}</span>
      <span v-else>{{ t('main.statusHint') }}</span>
    </footer>
  </div>
</template>

<style scoped>
.window { height: 100%; display: flex; flex-direction: column; }
.content { flex: 1; overflow-y: auto; padding: 14px; display: flex; flex-direction: column; gap: 10px; }
.preview-note { margin: 0; font-size: 11px; color: var(--hint); border: 1px dashed var(--line-accent); border-radius: var(--radius-tag); padding: 4px 8px; }
.offline-note { display: inline-flex; align-items: center; gap: 6px; color: var(--error); }
.statusbar { height: 32px; flex: none; display: flex; align-items: center; padding: 0 14px; border-top: 1px solid var(--line); font-size: 11px; color: var(--hint); }
</style>
