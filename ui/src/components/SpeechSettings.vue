<script setup lang="ts">
import { computed, ref } from 'vue';
import { UI_COMMANDS, type CommandResult, type SettingsView, type SpeechChoiceView, type SpeechSlotView } from '@protocol/ui';
import SettingRow from './SettingRow.vue';
import Icon from './Icon.vue';
import { t, serviceName } from '../locales/i18n';

// SetSpeech (pronunciation service) and SetSpeechB (recording/audio transcription and video transcription chosen
// separately), F07.4. Each choice is saved alone through Settings.SelectSpeech, so changing one never changes
// another or the AI translation model (A02). Only services whose package declares the capability are listed; a
// text-only ASR is shown disabled under video with the timecode prerequisite (A03). Keys and shared accounts are
// entered in the service rows below (Settings.BindAccount with explicit grants). Nothing here starts recording or
// playback: those features arrive in F10/F12, and the status line says why a selection is not usable yet.
interface Commands { command(name: string, payload?: object): Promise<CommandResult> }
const props = defineProps<{ settings: SettingsView; bridge: Commands }>();
const emit = defineEmits<{ settings: [SettingsView] }>();

const speech = computed(() => props.settings.speech!);
const slots = computed(() => [
  { slot: speech.value.tts, group: 'speech.tts' },
  { slot: speech.value.asr, group: 'speech.asr' },
  { slot: speech.value.videoAsr, group: 'speech.videoAsr' },
]);
const error = ref<{ slot: string; text: string } | null>(null);
const busy = ref(false);

const choiceOf = (slot: SpeechSlotView, instance = slot.instance) => slot.choices.find((c) => c.instanceId === instance);
const choiceLabel = (choice: SpeechChoiceView) => {
  const notes: string[] = [];
  if (!choice.selectable && choice.reasonKey) notes.push(t(`speech.choice.${choice.reasonKey}`));
  else if (!choice.installed) notes.push(t('speech.choice.notInstalled'));
  return notes.length ? `${serviceName(choice.instanceId)}（${notes.join('，')}）` : serviceName(choice.instanceId);
};
const modelLabel = (id: string, timecodes: boolean) => `${id} · ${t(timecodes ? 'speech.model.timecodes' : 'speech.model.textOnly')}`;

/** Status of the selected service: why it cannot be used yet (not installed, not built, key missing, ...). */
function status(slot: SpeechSlotView): string {
  if (slot.ready) return t('speech.reason.ready');
  const choice = choiceOf(slot);
  return t(`speech.reason.${slot.reasonKey ?? 'none-selected'}`, { plan: choice?.plan ?? '' });
}

// F14.4: the translation service of video transcription (SetSpeechB), saved alone like the other selections.
const videoTranslator = computed(() => speech.value.videoTranslator ?? '');
const videoTranslatorChoices = computed(() => speech.value.videoTranslatorChoices ?? []);
async function selectVideoTranslator(event: Event): Promise<void> {
  if (busy.value) return;
  busy.value = true;
  error.value = null;
  try {
    const instance = (event.target as HTMLSelectElement).value;
    const result = await props.bridge.command(UI_COMMANDS.SelectSpeech, { expectedRevision: props.settings.revision, expectedFileHash: props.settings.fileHash, slot: 'videoTranslator', instance, model: '' });
    if (result.value && (result.ok || result.error === 'conflict')) emit('settings', result.value as SettingsView);
    if (!result.ok) error.value = { slot: 'videoTranslator', text: t(`speech.error.${result.error === 'range' ? 'range' : result.error === 'conflict' ? 'conflict' : 'failed'}`) };
  } finally { busy.value = false; }
}

async function select(slot: SpeechSlotView, instance: string, model: string): Promise<void> {
  if (busy.value) return;
  busy.value = true;
  error.value = null;
  try {
    const result = await props.bridge.command(UI_COMMANDS.SelectSpeech, { expectedRevision: props.settings.revision, expectedFileHash: props.settings.fileHash, slot: slot.slot, instance, model });
    if (result.value && (result.ok || result.error === 'conflict')) emit('settings', result.value as SettingsView);
    if (!result.ok) error.value = { slot: slot.slot, text: t(`speech.error.${['needs-timecodes', 'capability', 'model', 'conflict'].includes(result.error ?? '') ? result.error : 'failed'}`) };
  } finally { busy.value = false; }
}
function onService(slot: SpeechSlotView, event: Event): void {
  const instance = (event.target as HTMLSelectElement).value;
  const choice = choiceOf(slot, instance);
  // A new service starts on its first model this slot accepts (video: the first one with timecodes).
  const model = choice?.models.find((m) => m.selectable)?.id ?? '';
  void select(slot, instance, model);
}
function onModel(slot: SpeechSlotView, event: Event): void {
  void select(slot, slot.instance, (event.target as HTMLSelectElement).value);
}
</script>

<template>
  <div class="speech">
    <section v-for="{ slot, group } in slots" :key="slot.slot" class="group" :data-slot="slot.slot">
      <h2>{{ t(group) }}</h2>
      <p class="hint-text">{{ t(`${group}Hint`) }}</p>
      <SettingRow :title="t('speech.service')" :for-id="`speech-${slot.slot}`">
        <select :id="`speech-${slot.slot}`" class="field" :value="slot.instance" :disabled="busy" data-service @change="onService(slot, $event)">
          <option value="">{{ t('speech.none') }}</option>
          <option v-for="choice in slot.choices" :key="choice.instanceId" :value="choice.instanceId" :disabled="!choice.selectable" :data-choice="choice.instanceId">
            {{ choiceLabel(choice) }}
          </option>
        </select>
      </SettingRow>
      <SettingRow v-if="choiceOf(slot)?.models.length" :title="t('speech.model')" :for-id="`speech-${slot.slot}-model`">
        <select :id="`speech-${slot.slot}-model`" class="field" :value="slot.model" :disabled="busy" data-model @change="onModel(slot, $event)">
          <option v-for="m in choiceOf(slot)!.models" :key="m.id" :value="m.id" :disabled="!m.selectable" :data-model-id="m.id">{{ modelLabel(m.id, m.timecodes) }}</option>
        </select>
      </SettingRow>
      <SettingRow v-if="choiceOf(slot)" :title="t('speech.account')">
        <span class="hint-text" data-account>{{ choiceOf(slot)!.native ? t('speech.account.native') : t(`speech.account.${choiceOf(slot)!.availability}`) }}</span>
      </SettingRow>
      <p class="status" :class="{ ready: slot.ready }" role="status" data-status>
        <Icon :name="slot.ready ? 'check' : 'warning'" :size="13" />{{ status(slot) }}
      </p>
      <p v-if="slot.slot === 'videoAsr' && slot.choices.some((c) => !c.selectable)" class="hint-text small" data-timecode-note>{{ t('speech.videoAsrTimecodes') }}</p>
      <p v-if="error?.slot === slot.slot" class="error-text small" role="alert">{{ error.text }}</p>
      <template v-if="slot.slot === 'videoAsr'">
        <SettingRow :title="t('speech.videoTranslation')" for-id="speech-videoTranslator">
          <select id="speech-videoTranslator" class="field" :value="videoTranslator" :disabled="busy" data-video-translator @change="selectVideoTranslator">
            <option value="">{{ t('speech.videoTranslationAuto') }}</option>
            <option v-for="id in videoTranslatorChoices" :key="id" :value="id">{{ serviceName(id) }}</option>
          </select>
        </SettingRow>
        <p class="hint-text small">{{ t('speech.videoTranslationHint') }}</p>
        <p v-if="error?.slot === 'videoTranslator'" class="error-text small" role="alert">{{ error.text }}</p>
      </template>
    </section>
  </div>
</template>

<style scoped>
.group { margin-bottom: 22px; }
.group h2 { font-size: 12.5px; font-weight: 600; margin: 0 0 4px; }
.status { display: flex; align-items: center; gap: 6px; margin: 6px 0 0; font-size: 11px; color: var(--hint); }
.status.ready { color: var(--ink-secondary); }
.small { font-size: 11px; margin: 4px 0 0; }
</style>
