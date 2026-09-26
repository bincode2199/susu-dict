<script setup lang="ts">
import { computed, ref } from 'vue';
import { UI_COMMANDS, type CommandResult, type OcrSettingsView, type SettingsView } from '@protocol/ui';
import SettingRow from './SettingRow.vue';
import Toggle from './Toggle.vue';
import Stepper from './Stepper.vue';
import { t } from '../locales/i18n';

// SetOcr "截图" group (F11.3, SetOcr artboard): translate after recognition (on by default), keep screenshots in Pictures
// (off by default, OCR03) with the retention days, and the screenshot hotkey shown as the same item as SetHotkeys. Each
// change is saved at once through Settings.SaveOcr, which touches only these fields and the default service.
interface Commands { command(name: string, payload?: object): Promise<CommandResult> }
const props = defineProps<{ settings: SettingsView; bridge: Commands }>();
const emit = defineEmits<{ settings: [SettingsView] }>();
const ocr = computed(() => props.settings.ocr!);
const error = ref<string | null>(null);
const busy = ref(false);

async function save(patch: Partial<Pick<OcrSettingsView, 'service' | 'autoTranslate' | 'keepScreenshots' | 'retentionDays'>>): Promise<void> {
  if (busy.value) return;
  busy.value = true;
  error.value = null;
  try {
    const current = ocr.value;
    const result = await props.bridge.command(UI_COMMANDS.SaveOcr, {
      expectedRevision: props.settings.revision, expectedFileHash: props.settings.fileHash,
      service: patch.service ?? current.service, autoTranslate: patch.autoTranslate ?? current.autoTranslate,
      keepScreenshots: patch.keepScreenshots ?? current.keepScreenshots, retentionDays: patch.retentionDays ?? current.retentionDays,
    });
    if (result.value && (result.ok || result.error === 'conflict')) emit('settings', result.value as SettingsView);
    if (!result.ok) error.value = t(result.error === 'conflict' ? 'save.conflict' : 'ocr.settings.saveFailed');
  } finally { busy.value = false; }
}
defineExpose({ save });
</script>

<template>
  <section class="group ocr-capture">
    <h2>{{ t('ocr.settings.capture') }}</h2>
    <SettingRow :title="t('ocr.settings.autoTranslate')" :hint="t('ocr.settings.autoTranslateHint')">
      <Toggle :model-value="ocr.autoTranslate" :label="t('ocr.settings.autoTranslate')" data-ocr-auto @update:model-value="(v: boolean) => save({ autoTranslate: v })" />
    </SettingRow>
    <SettingRow :title="t('ocr.settings.keep')" :hint="t('ocr.settings.keepHint')">
      <span v-if="ocr.keepScreenshots" class="hint-text small">{{ t('ocr.settings.retention', { n: ocr.retentionDays }) }}</span>
      <Stepper v-if="ocr.keepScreenshots" :model-value="ocr.retentionDays" :min="ocr.minRetentionDays" :max="ocr.maxRetentionDays" :label="t('ocr.settings.retentionLabel')"
        data-ocr-retention @update:model-value="(v: number) => save({ retentionDays: v })" />
      <Toggle :model-value="ocr.keepScreenshots" :label="t('ocr.settings.keep')" data-ocr-keep @update:model-value="(v: boolean) => save({ keepScreenshots: v })" />
    </SettingRow>
    <SettingRow :title="t('ocr.settings.hotkey')" :hint="t('ocr.settings.hotkeyHint')">
      <kbd class="chord" data-ocr-hotkey>{{ ocr.hotkey ? ocr.hotkey.split('+').join(' + ') : t('ocr.settings.unassigned') }}</kbd>
    </SettingRow>
    <p v-if="error" class="error-text small" role="alert">{{ error }}</p>
  </section>
</template>

<style scoped>
.group { margin-bottom: 22px; }
.group h2 { font-size: 12.5px; font-weight: 600; margin: 0 0 4px; }
.small { font-size: 11px; }
.chord { font-family: inherit; font-size: 12px; padding: 3px 8px; border: 1px solid var(--line-accent); border-radius: var(--radius-icon); }
</style>
