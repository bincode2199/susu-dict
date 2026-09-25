<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue';
import { UI_COMMANDS, type CommandResult, type PromptPreviewView, type PromptProfileView, type SettingsIssueView, type SettingsView } from '@protocol/ui';
import SettingRow from './SettingRow.vue';
import { t, serviceName } from '../locales/i18n';

// SetPrompt (F07.3, DESIGN SetPrompt + ARTBOARD-REVISIONS P1/P2): built-in level, custom templates, the AI services
// the prompt applies to and a live preview. The preview is rendered by the host with the same single-pass
// substitution a task uses (Settings.PreviewPrompt); saving (Settings.SavePrompt) affects later tasks only.
interface Commands { command(name: string, payload?: object): Promise<CommandResult> }
const props = defineProps<{ settings: SettingsView; bridge: Commands }>();
const emit = defineEmits<{ settings: [SettingsView] }>();

const prompt = computed(() => props.settings.prompt!);
const draft = reactive({ level: '', profile: '', scope: [] as string[], profiles: [] as PromptProfileView[] });
const sample = ref(t('prompt.sampleText'));
const preview = ref<PromptPreviewView | null>(null);
const message = ref<{ kind: 'ok' | 'error'; text: string } | null>(null);
const issues = ref<SettingsIssueView[]>([]);
const editor = ref<HTMLTextAreaElement | null>(null);

const baseline = ref('');
const current = () => JSON.stringify({ l: draft.level, p: draft.profile, s: draft.scope, t: draft.profiles });
const dirty = computed(() => current() !== baseline.value);
function load(): void {
  draft.level = prompt.value.level;
  draft.profile = prompt.value.profile;
  draft.scope = [...prompt.value.scope];
  draft.profiles = prompt.value.profiles.map((p) => ({ ...p }));
  baseline.value = current();
}
// Adopt host changes (a save, a hand edit of settings.yaml) unless there are unsaved edits here.
let force = true;
watch(() => props.settings, () => { if (force || !dirty.value) load(); force = false; }, { immediate: true });

const active = computed(() => draft.profiles.find((p) => p.id === draft.profile) ?? null);
/** The template text shown: the chosen custom template, or the built-in default. */
const templateText = computed({
  get: () => active.value?.template ?? prompt.value.defaultTemplate,
  set: (value: string) => {
    if (active.value) { active.value.template = value; return; }
    // Editing the built-in default starts a custom template from it.
    const created = newProfile(value);
    draft.profile = created.id;
  },
});

function newProfile(template: string): PromptProfileView {
  let n = 1;
  while (draft.profiles.some((p) => p.id === `custom-${n}`)) n++;
  const profile = { id: `custom-${n}`, name: n === 1 ? t('prompt.customName') : `${t('prompt.customName')} ${n}`, template };
  draft.profiles.push(profile);
  return profile;
}
function addProfile(): void { draft.profile = newProfile(templateText.value).id; }
function removeProfile(): void {
  if (!active.value) return;
  draft.profiles = draft.profiles.filter((p) => p.id !== draft.profile);
  draft.profile = '';
}
function restoreDefault(): void { draft.profile = ''; }
function toggleLevel(id: string): void { draft.level = draft.level === id ? '' : id; }
function toggleScope(id: string, on: boolean): void {
  draft.scope = prompt.value.aiServices.filter((s) => (s === id ? on : draft.scope.includes(s)));
}

/** Inserts {{name}} at the cursor (the chips under the editor). */
function insertVariable(name: string): void {
  const token = `{{${name}}}`;
  const area = editor.value;
  const text = templateText.value;
  const start = area?.selectionStart ?? text.length;
  const end = area?.selectionEnd ?? text.length;
  templateText.value = text.slice(0, start) + token + text.slice(end);
  requestAnimationFrame(() => { area?.focus(); area?.setSelectionRange(start + token.length, start + token.length); });
}

let timer: ReturnType<typeof setTimeout> | undefined;
let sequence = 0;
async function renderPreview(): Promise<void> {
  const mine = ++sequence;
  const result = await props.bridge.command(UI_COMMANDS.PreviewPrompt, { template: active.value?.template ?? '', level: draft.level, text: sample.value.slice(0, 2000), from: 'en', to: 'zh-Hans' });
  if (mine !== sequence) return; // an older answer never overwrites a newer preview
  preview.value = result.ok ? (result.value as PromptPreviewView) : null;
}
watch([() => active.value?.template, () => draft.level, sample], () => { clearTimeout(timer); timer = setTimeout(renderPreview, 200); }, { immediate: true });
onBeforeUnmount(() => clearTimeout(timer));

async function save(): Promise<void> {
  issues.value = [];
  const result = await props.bridge.command(UI_COMMANDS.SavePrompt, {
    expectedRevision: props.settings.revision, expectedFileHash: props.settings.fileHash,
    level: draft.level, profile: draft.profile, scope: draft.scope, profiles: draft.profiles,
  });
  if (result.ok) {
    message.value = { kind: 'ok', text: t('prompt.saved') };
    force = true;
    if (result.value) emit('settings', result.value as SettingsView);
    return;
  }
  if (result.error === 'conflict' && result.value) {
    force = true;
    emit('settings', result.value as SettingsView);
    message.value = { kind: 'error', text: t('save.conflict') };
  } else if (result.error === 'invalid') {
    issues.value = (result.value as SettingsIssueView[]) ?? [];
    message.value = { kind: 'error', text: t('save.invalid') };
  } else message.value = { kind: 'error', text: t('save.failed') };
}
const unknownText = computed(() => t('prompt.unknown', { names: (preview.value?.unknown ?? []).map((n) => '{{' + n + '}}').join(' ') }));
const issueText = (issue: SettingsIssueView) => t(`prompt.issue.${issue.code}`);
</script>

<template>
  <div class="prompt-page">
    <section class="group">
      <h2>{{ t('prompt.levels') }}</h2>
      <p class="hint-text">{{ t('prompt.levelsHint') }}</p>
      <div class="chips" role="group" :aria-label="t('prompt.levels')">
        <button v-for="id in prompt.levels" :key="id" type="button" class="chip" :class="{ on: draft.level === id }" :aria-pressed="draft.level === id" :data-level="id" @click="toggleLevel(id)">
          {{ t(`prompt.level.${id}`) }}
        </button>
      </div>
    </section>

    <section class="group">
      <h2>{{ t('prompt.custom') }}</h2>
      <p class="hint-text">{{ t('prompt.customHint') }}</p>
      <SettingRow :title="t('prompt.template')" for-id="p-profile">
        <select id="p-profile" v-model="draft.profile" class="field">
          <option value="">{{ t('prompt.defaultTemplate') }}</option>
          <option v-for="p in draft.profiles" :key="p.id" :value="p.id">{{ p.name || p.id }}</option>
        </select>
        <button type="button" class="btn" @click="addProfile">{{ t('prompt.newTemplate') }}</button>
        <button type="button" class="btn" :disabled="!active" @click="removeProfile">{{ t('prompt.deleteTemplate') }}</button>
      </SettingRow>
      <SettingRow v-if="active" :title="t('prompt.templateName')" for-id="p-name">
        <input id="p-name" v-model="active.name" class="field" maxlength="40" />
      </SettingRow>
      <textarea id="p-template" ref="editor" v-model="templateText" class="editor" rows="6" spellcheck="false" :aria-label="t('prompt.custom')" />
      <div class="vars">
        <span class="hint-text">{{ t('prompt.insert') }}</span>
        <button v-for="name in prompt.variables" :key="name" type="button" class="tag var" :data-var="name" @click="insertVariable(name)">{{ t(`prompt.var.${name}`) }}</button>
      </div>
      <p class="hint-text">{{ t('prompt.varsHint') }}</p>
    </section>

    <section class="group">
      <h2>{{ t('prompt.scope') }}</h2>
      <div class="scope" role="group" :aria-label="t('prompt.scope')">
        <label v-for="id in prompt.aiServices" :key="id" class="check">
          <input type="checkbox" :checked="draft.scope.includes(id)" :data-scope="id" @change="toggleScope(id, ($event.target as HTMLInputElement).checked)" /> {{ serviceName(id) }}
        </label>
      </div>
    </section>

    <section class="group">
      <h2>{{ t('prompt.preview') }}</h2>
      <SettingRow :title="t('prompt.sample')" for-id="p-sample"><input id="p-sample" v-model="sample" class="field wide" maxlength="2000" /></SettingRow>
      <pre class="preview" aria-live="polite" data-preview>{{ preview?.rendered ?? '' }}</pre>
      <p v-if="preview?.unknown.length" class="hint-text" data-unknown>{{ unknownText }}</p>
      <p v-if="preview?.problem" class="error-text" data-problem>{{ t(`prompt.issue.${preview.problem}`) }}</p>
    </section>

    <ul v-if="issues.length" class="issues error-text">
      <li v-for="issue in issues" :key="issue.path + issue.code">{{ issue.path }}: {{ issueText(issue) }}</li>
    </ul>
    <div class="actions">
      <span v-if="message" :class="message.kind === 'error' ? 'error-text' : 'hint-text'" role="status">{{ message.text }}</span>
      <span class="spacer" />
      <button type="button" class="btn" @click="restoreDefault">{{ t('prompt.restore') }}</button>
      <button type="button" class="btn primary" :disabled="!dirty" data-save-prompt @click="save">{{ t('save.save') }}</button>
    </div>
  </div>
</template>

<style scoped>
.group { margin-bottom: 22px; }
.group h2 { font-size: 12.5px; font-weight: 600; margin: 0 0 4px; }
.chips { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 8px; }
.chip { height: 26px; padding: 0 10px; border: 1px solid var(--line); border-radius: 13px; background: transparent; color: var(--ink-secondary); font-size: 12px; }
.chip.on { border-color: var(--accent); color: var(--ink); font-weight: 600; }
.editor { width: 100%; box-sizing: border-box; margin-top: 8px; padding: 8px 10px; border: 1px solid var(--line); border-radius: 5px; font: inherit; font-size: 12px; line-height: 1.6; resize: vertical; background: transparent; color: var(--ink); }
.editor:focus { outline: none; border-color: var(--accent); }
.vars { display: flex; align-items: center; flex-wrap: wrap; gap: 6px; margin-top: 6px; font-size: 11px; }
.var { cursor: pointer; border: none; }
.scope { display: flex; flex-wrap: wrap; gap: 14px; margin-top: 6px; }
.check { display: inline-flex; align-items: center; gap: 6px; font-size: 12px; }
.check input { accent-color: var(--accent); margin: 0; }
.wide { width: 320px; }
.preview { margin: 6px 0 0; padding: 8px 10px; min-height: 48px; border: 1px dashed var(--line); border-radius: 5px; white-space: pre-wrap; word-break: break-word; font: inherit; font-size: 12px; line-height: 1.6; }
.issues { font-size: 11px; padding-left: 18px; }
.actions { display: flex; align-items: center; gap: 8px; font-size: 11px; }
.spacer { flex: 1; }
</style>
