<script setup lang="ts">
import { computed, reactive, ref } from 'vue';
import { UI_COMMANDS, type CommandResult, type SettingsView, type VocabTargetView } from '@protocol/ui';
import SettingRow from './SettingRow.vue';
import { t, serviceName } from '../locales/i18n';

// SetVocab (F15.4): the favorites status, one block per sync target (state, outbox counts, last error, sync now / retry failed / queue the
// favorites that predate it), the Uncertain and Failed rows for the manual check, and the file export. Turning a target on, its key, deck or word list
// are the service rows above (the same list as the other pages); there is no separate vocabulary window. Unfavorite never deletes a remote entry, and
// the page says so. Every command answers with the fresh settings view.
interface Commands { command(name: string, payload?: object): Promise<CommandResult> }
const props = defineProps<{ settings: SettingsView; bridge: Commands }>();
const emit = defineEmits<{ settings: [SettingsView] }>();
const vocab = computed(() => props.settings.vocab!);
const busy = ref<string | null>(null);
const error = ref<string | null>(null);
const options = reactive({ format: 'txt', definitions: true, phonetics: true, examples: true, onlyNew: false, deck: 'Su-Su' });
const anyUsable = computed(() => vocab.value.targets.some((x) => x.usable));
const contentless = computed(() => options.format !== 'txt' && !options.definitions && !options.phonetics && !options.examples);

async function run(key: string, name: string, payload: object): Promise<void> {
  if (busy.value) return;
  busy.value = key;
  error.value = null;
  try {
    const result = await props.bridge.command(name, payload);
    if (result.ok && result.value) emit('settings', result.value as SettingsView);
    else if (!result.ok) error.value = t(result.error === 'unavailable' ? 'vocab.unavailable' : 'vocab.failed');
  } finally { busy.value = null; }
}
const sync = (action: string, target?: string) => run(`${action}:${target ?? ''}`, UI_COMMANDS.VocabSync, target ? { action, target } : { action });
const resolve = (row: NonNullable<SettingsView['vocab']>['rows'][number], delivered: boolean) =>
  run(`resolve:${row.entryId}:${row.target}:${row.revision}`, UI_COMMANDS.VocabResolve, { entryId: row.entryId, target: row.target, revision: row.revision, delivered });
const exportNow = () => run('export', UI_COMMANDS.VocabExport, {
  format: options.format, definitions: options.definitions, phonetics: options.phonetics, examples: options.examples, onlyNew: options.onlyNew,
  ...(options.format === 'apkg' && options.deck.trim() ? { deck: options.deck.trim() } : {}),
});

const stateLine = (x: VocabTargetView) => (x.usable ? t('vocab.target.usable') : t(x.reasonKey ?? 'vocab.reason.disabled'));
const exportError = (code: string) => { const key = `vocab.export.error.${code}`; const text = t(key); return text === key ? t('vocab.failed') : text; };
</script>

<template>
  <section class="group vocab" data-vocab>
    <h2>{{ t('vocab.title') }}</h2>
    <p class="hint-text small">{{ t('vocab.hint') }}</p>
    <p class="small" data-vocab-count>{{ t('vocab.favorites', { n: vocab.favorites }) }}</p>
    <p v-if="!anyUsable" class="hint-text small" role="status" data-vocab-no-targets>{{ t('vocab.noTargets') }}</p>
    <p class="hint-text small" data-vocab-note>{{ t('vocab.unfavoriteNote') }}</p>

    <h3>{{ t('vocab.targets') }}</h3>
    <div v-for="target in vocab.targets" :key="target.instanceId" class="target" :data-vocab-target="target.instanceId">
      <SettingRow :title="serviceName(target.instanceId)" :hint="stateLine(target)">
        <button type="button" class="btn" :disabled="!target.usable || !!busy" data-vocab-sync @click="sync('sync', target.instanceId)">{{ t('vocab.syncNow') }}</button>
        <button v-if="target.failed > 0" type="button" class="btn" :disabled="!!busy" data-vocab-retry @click="sync('retryFailed', target.instanceId)">{{ t('vocab.retryFailed') }}</button>
        <button v-if="target.usable && vocab.favorites > 0" type="button" class="btn" :disabled="!!busy" :title="t('vocab.queueExistingHint')" data-vocab-queue @click="sync('queueExisting', target.instanceId)">{{ t('vocab.queueExisting') }}</button>
      </SettingRow>
      <p class="counts small hint-text" data-vocab-counts>
        <span>{{ t('vocab.count.pending', { n: target.pending }) }}</span>
        <span>{{ t('vocab.count.retrying', { n: target.retrying }) }}</span>
        <span :class="{ 'error-text': target.failed > 0 }">{{ t('vocab.count.failed', { n: target.failed }) }}</span>
        <span :class="{ 'error-text': target.uncertain > 0 }">{{ t('vocab.count.uncertain', { n: target.uncertain }) }}</span>
        <span>{{ t('vocab.count.succeeded', { n: target.succeeded }) }}</span>
      </p>
      <p class="small hint-text" data-vocab-address>{{ t('vocab.target.address', { origin: target.origin }) }}{{ target.local ? ' · ' + t('vocab.target.local') : '' }} · {{ target.lookup ? t('vocab.target.lookup') : t('vocab.target.noLookup') }}</p>
      <p v-if="target.lastError" class="small error-text" role="status" data-vocab-last-error>{{ t('vocab.lastError', { error: t(`error.${target.lastError}`) }) }}</p>
      <p v-if="target.passFailed" class="small error-text" role="status" data-vocab-pass-failed>{{ t('vocab.passFailed') }}</p>
    </div>

    <template v-if="vocab.rows.length">
      <h3>{{ t('vocab.rows') }}</h3>
      <p class="hint-text small">{{ t('vocab.rows.hint') }}</p>
      <ul class="rows" data-vocab-rows>
        <li v-for="row in vocab.rows" :key="`${row.entryId}:${row.target}:${row.revision}`" class="row" :data-vocab-row-state="row.state">
          <span class="word">{{ row.word }}</span>
          <span class="tag">{{ serviceName(row.target) }}</span>
          <span :class="row.state === 'Failed' ? 'error-text' : 'hint-text'" class="small">{{ t(`vocab.row.state.${row.state}`) }}</span>
          <span class="spacer" />
          <button type="button" class="btn" :disabled="!!busy" data-vocab-delivered @click="resolve(row, true)">{{ t('vocab.row.delivered') }}</button>
          <button type="button" class="btn" :disabled="!!busy" data-vocab-not-delivered @click="resolve(row, false)">{{ t('vocab.row.notDelivered') }}</button>
        </li>
      </ul>
    </template>

    <h3>{{ t('vocab.export') }}</h3>
    <p class="hint-text small">{{ t('vocab.export.hint') }}</p>
    <SettingRow :title="t('vocab.export.format')">
      <select v-model="options.format" class="select" :aria-label="t('vocab.export.format')" data-vocab-format>
        <option v-for="f in ['txt', 'csv', 'apkg']" :key="f" :value="f">{{ t(`vocab.export.format.${f}`) }}</option>
      </select>
    </SettingRow>
    <SettingRow :title="t('vocab.export.fields')" :hint="t('vocab.export.fieldsNote')">
      <label class="check"><input v-model="options.definitions" type="checkbox" :disabled="options.format === 'txt'" data-vocab-definitions /> {{ t('vocab.export.definitions') }}</label>
      <label class="check"><input v-model="options.phonetics" type="checkbox" :disabled="options.format === 'txt'" data-vocab-phonetics /> {{ t('vocab.export.phonetics') }}</label>
      <label class="check"><input v-model="options.examples" type="checkbox" :disabled="options.format === 'txt'" data-vocab-examples /> {{ t('vocab.export.examples') }}</label>
    </SettingRow>
    <SettingRow v-if="options.format === 'apkg'" :title="t('vocab.export.deck')">
      <input v-model="options.deck" type="text" class="input" maxlength="64" :aria-label="t('vocab.export.deck')" data-vocab-deck />
    </SettingRow>
    <SettingRow :title="t('vocab.export.onlyNew')">
      <input v-model="options.onlyNew" type="checkbox" :aria-label="t('vocab.export.onlyNew')" data-vocab-only-new />
    </SettingRow>
    <SettingRow :title="t('vocab.export.run')" :hint="vocab.canExport ? undefined : t('vocab.export.noDialog')">
      <button type="button" class="btn primary" :disabled="!vocab.canExport || !!busy || contentless" data-vocab-export @click="exportNow">{{ busy === 'export' ? t('vocab.export.running') : t('vocab.export.run') }}</button>
    </SettingRow>
    <div v-if="vocab.export" class="result small" data-vocab-export-result :class="vocab.export.path ? 'hint-text' : 'error-text'" role="status">
      <template v-if="vocab.export.path">
        <span class="path" data-vocab-export-path>{{ t('vocab.export.saved', { path: vocab.export.path, n: vocab.export.exported }) }}</span>
        <span v-if="vocab.export.skipped > 0"> · {{ t('vocab.export.skipped', { n: vocab.export.skipped }) }}</span>
        <span v-if="vocab.export.issues.length > 0"> · {{ t('vocab.export.issues', { n: vocab.export.issues.length }) }}</span>
      </template>
      <span v-else data-vocab-export-error>{{ exportError(vocab.export.error ?? '') }}</span>
    </div>
    <p v-if="error" class="error-text small" role="alert" data-vocab-error>{{ error }}</p>
  </section>
</template>

<style scoped>
.group { margin-bottom: 22px; }
.group h2 { font-size: 12.5px; font-weight: 600; margin: 0 0 4px; }
.group h3 { font-size: 12px; font-weight: 600; margin: 16px 0 4px; }
.small { font-size: 11px; margin: 2px 0; }
.counts { display: flex; gap: 12px; flex-wrap: wrap; }
.rows { list-style: none; margin: 4px 0; padding: 0; }
.row { display: flex; align-items: center; gap: 8px; padding: 5px 0; border-top: 1px solid var(--line); }
.row .word { font-size: 12.5px; word-break: break-word; }
.spacer { flex: 1; }
.check { display: inline-flex; gap: 4px; align-items: center; font-size: 12px; margin-right: 10px; }
.result { margin: 6px 0; word-break: break-all; }
</style>
