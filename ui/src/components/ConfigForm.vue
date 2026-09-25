<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue';
import { UI_COMMANDS, type CommandResult, type ConfigFieldView, type OptionItem, type OptionsView, type ServiceView, type SettingsView } from '@protocol/ui';
import SettingRow from './SettingRow.vue';
import Toggle from './Toggle.vue';
import { t } from '../locales/i18n';

// F07.2: settings controls generated from the package's manifest config schema (PLAN 4.6), not per vendor.
// Dynamic fields (x-susu.optionsSource) load their choices through Settings.LoadOptions for the field's
// dependency revision (CFG02): a list is shown only while that revision is current, a stale answer is
// dropped, and a failed load keeps the saved selection. The host checks every saved value again.
interface Commands { command(name: string, payload?: object): Promise<CommandResult> }
const props = defineProps<{ service: ServiceView; bridge: Commands }>();
const emit = defineEmits<{ settings: [SettingsView]; error: [] }>();

const fields = computed<ConfigFieldView[]>(() => props.service.config ?? []);
const draft = reactive<Record<string, string>>({});
function resetDraft(): void {
  for (const key of Object.keys(draft)) delete draft[key];
  for (const f of fields.value) draft[f.name] = f.value ?? '';
}
watch(() => `${props.service.instanceId}#${props.service.instanceRevision}`, resetDraft, { immediate: true });
const dirty = computed(() => fields.value.some((f) => (draft[f.name] ?? '') !== (f.value ?? '')));
const effective = (f: ConfigFieldView) => draft[f.name] || f.default || '';
const visible = (f: ConfigFieldView) => {
  if (!f.showWhenField) return true;
  const other = fields.value.find((x) => x.name === f.showWhenField);
  return !!other && effective(other) === (f.showWhenEquals ?? '');
};
const basic = computed(() => fields.value.filter((f) => f.group !== 'advanced' && visible(f)));
const advanced = computed(() => fields.value.filter((f) => f.group === 'advanced' && visible(f)));

function localized(prefix: string, f: ConfigFieldView, fallback?: string | null): string | undefined {
  const key = `${prefix}.${f.name}`;
  const text = t(key);
  return text !== key ? text : fallback ?? undefined;
}
const title = (f: ConfigFieldView) => localized('config.title', f, f.title) ?? f.name;
const help = (f: ConfigFieldView) => localized('config.help', f, f.help);
const controlId = (f: ConfigFieldView) => `${props.service.instanceId}-config-${f.name}`;

// ---------- dynamic options ----------
type FieldOptions = { revision: number; items: OptionItem[]; next?: string; loading: boolean; error?: string };
const options = reactive<Record<string, FieldOptions>>({});
const credentialsReady = computed(() => (props.service.credentialTargets ?? []).every((c) => c.saved && c.granted));
const dynamicFields = computed(() => fields.value.filter((f) => f.dynamic));

async function load(f: ConfigFieldView, cursor?: string, refresh = false): Promise<void> {
  const revision = f.optionsRevision ?? 0;
  const state = options[f.name];
  if (!state || state.revision !== revision || state.loading) return;
  state.loading = true;
  state.error = undefined;
  const result = await props.bridge.command(UI_COMMANDS.LoadOptions, { instanceId: props.service.instanceId, field: f.name, dependsOnRevision: revision, cursor, refresh });
  const current = options[f.name];
  // The dependencies changed while this was loading: the answer belongs to an old revision and is dropped.
  if (current !== state || current.revision !== revision) return;
  state.loading = false;
  if (!result.ok) {
    state.error = result.error === 'missing-credential' ? t('options.needsCredential') : t('options.failed', { reason: t('error.unavailable') });
    return;
  }
  const view = result.value as OptionsView;
  if (view.stale || view.dependsOnRevision !== revision) return; // the settings event with the new revision starts a fresh load
  if (view.error) { state.error = t('options.failed', { reason: t(`error.${view.error}`) }); return; }
  state.items = cursor ? [...state.items, ...view.items.filter((i) => !state.items.some((x) => x.value === i.value))] : view.items;
  state.next = view.nextCursor ?? undefined;
}

// A new dependency revision (address, account or key changed) discards the old list before anything else.
watch(() => dynamicFields.value.map((f) => `${f.name}:${f.optionsRevision}`).join('|'), () => {
  for (const f of dynamicFields.value) {
    const revision = f.optionsRevision ?? 0;
    if (options[f.name]?.revision === revision) continue;
    options[f.name] = { revision, items: [], loading: false };
    if (credentialsReady.value) void load(f);
    else options[f.name].error = t('options.needsCredential');
  }
}, { immediate: true });
watch(credentialsReady, (ready) => {
  if (!ready) return;
  for (const f of dynamicFields.value) if (!options[f.name]?.items.length) void load(f);
});

/** The choices shown: the loaded list, plus the saved value when the list does not (or no longer) contain it. */
function choices(f: ConfigFieldView): OptionItem[] {
  const items = options[f.name]?.items ?? [];
  const value = draft[f.name];
  return value && !items.some((i) => i.value === value) ? [{ value, label: value }, ...items] : items;
}

// ---------- save ----------
const busy = ref(false);
const message = ref<string | null>(null);
async function save(): Promise<void> {
  if (!dirty.value || busy.value) return;
  busy.value = true;
  message.value = null;
  const values = fields.value.filter((f) => (draft[f.name] ?? '') !== (f.value ?? '')).map((f) => ({ name: f.name, value: draft[f.name] ?? '' }));
  const result = await props.bridge.command(UI_COMMANDS.SaveServiceConfig, { instanceId: props.service.instanceId, expectedInstanceRevision: props.service.instanceRevision ?? 0, values });
  busy.value = false;
  if (result.ok) { if (result.value) emit('settings', result.value as SettingsView); return; }
  if (result.error === 'invalid') {
    const issues = (result.value as { path: string }[] | undefined) ?? [];
    const names = issues.map((i) => i.path.split('.').pop() ?? '').map((name) => { const f = fields.value.find((x) => x.name === name); return f ? title(f) : name; });
    message.value = t('config.invalid', { field: names.join('、') });
    return;
  }
  if (result.error === 'conflict' && result.value) { message.value = t('config.conflict'); emit('settings', result.value as SettingsView); return; }
  emit('error');
}
</script>

<template>
  <div v-if="fields.length" class="config">
    <template v-for="(group, index) in [basic, advanced]" :key="index">
      <div v-if="index === 1 && group.length" class="group">{{ t('config.advanced') }}</div>
      <SettingRow v-for="f in group" :key="f.name" :title="title(f)" :hint="help(f)" :for-id="controlId(f)">
        <template v-if="f.dynamic">
          <select :id="controlId(f)" v-model="draft[f.name]" class="field" :data-field="f.name">
            <option value="">{{ f.default ? t('config.default', { value: f.default }) : '—' }}</option>
            <option v-for="item in choices(f)" :key="item.value" :value="item.value">{{ item.label }}</option>
          </select>
          <button type="button" class="btn icon-btn" :disabled="options[f.name]?.loading" :aria-label="t('options.refresh')" :title="t('options.refresh')"
            data-action="refresh" @click="load(f, undefined, true)">↻</button>
        </template>
        <select v-else-if="f.enum?.length" :id="controlId(f)" v-model="draft[f.name]" class="field" :data-field="f.name">
          <option value="">{{ f.default ? t('config.default', { value: f.default }) : '—' }}</option>
          <option v-for="value in f.enum" :key="value" :value="value">{{ value }}</option>
        </select>
        <Toggle v-else-if="f.type === 'boolean'" :model-value="effective(f) === 'true'" :label="title(f)"
          @update:model-value="(on) => (draft[f.name] = on ? 'true' : 'false')" />
        <input v-else-if="f.type === 'integer' || f.type === 'number'" :id="controlId(f)" v-model="draft[f.name]" class="field" type="number"
          :step="f.type === 'integer' ? 1 : 'any'" :min="f.minimum ?? undefined" :max="f.maximum ?? undefined" :placeholder="f.default ?? undefined" :data-field="f.name" />
        <input v-else :id="controlId(f)" v-model.trim="draft[f.name]" class="field" :type="f.format === 'uri' ? 'url' : 'text'" spellcheck="false"
          :placeholder="f.placeholder ?? f.default ?? undefined" :data-field="f.name" />
        <template v-if="f.dynamic" #below>
          <span v-if="options[f.name]?.loading" class="status" role="status">{{ t('options.loading') }}</span>
          <span v-else-if="options[f.name]?.error" class="status error-text" role="status">{{ options[f.name].error }}</span>
          <span v-else-if="options[f.name]?.items.length" class="status">
            {{ t('options.count', { n: options[f.name].items.length }) }}
            <button v-if="options[f.name].next" type="button" class="link" data-action="more" @click="load(f, options[f.name].next)">{{ t('options.more') }}</button>
          </span>
        </template>
      </SettingRow>
    </template>
    <div v-if="dirty || message" class="actions">
      <span v-if="message" class="status error-text" role="status">{{ message }}</span>
      <button v-if="dirty" type="button" class="btn primary" :disabled="busy" data-action="save-config" @click="save">{{ t('config.save') }}</button>
    </div>
  </div>
</template>

<style scoped>
.group { font-size: 11px; color: var(--hint); padding: 12px 0 2px; }
.status { font-size: 11px; color: var(--hint); margin-top: 3px; }
.status.error-text { color: var(--error); }
.icon-btn { min-width: 28px; padding: 0 6px; }
.link { border: none; background: none; padding: 0 0 0 6px; color: var(--accent); font-size: 11px; cursor: pointer; }
.actions { display: flex; align-items: center; justify-content: flex-end; gap: 8px; padding: 8px 0; }
.actions .status { flex: 1; margin-top: 0; }
select.field { max-width: 220px; }
input.field { width: 220px; }
</style>
