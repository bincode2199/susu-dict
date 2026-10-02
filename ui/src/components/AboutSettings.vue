<script setup lang="ts">
import { computed, nextTick, ref } from 'vue';
import { UI_COMMANDS, type CommandResult, type DataCleanView, type SettingsView } from '@protocol/ui';
import SettingRow from './SettingRow.vue';
import UpdateSettings from './UpdateSettings.vue';
import { t } from '../locales/i18n';

// About, diagnostics and data clean (F17.2). The page never receives a path: the host opens the log folder and the save dialog itself. The diagnostics
// file holds version facts and allow-listed log fields only (no keys, texts, names, paths or addresses); the licenses come from the generated notices.
// Each data-clean entry says what it deletes and what it keeps, asks for a second click to confirm, and the host refuses a request without the confirmation.
interface Commands { command(name: string, payload?: object): Promise<CommandResult> }
const props = defineProps<{ settings: SettingsView; bridge: Commands }>();
const emit = defineEmits<{ settings: [SettingsView] }>();
const about = computed(() => props.settings.about!);
const busy = ref<string | null>(null);
const error = ref<string | null>(null);
const confirming = ref<string | null>(null);
const showLicenses = ref(false);

async function run(key: string, name: string, payload?: object): Promise<boolean> {
  if (busy.value) return false;
  busy.value = key;
  error.value = null;
  try {
    const result = await props.bridge.command(name, payload);
    if (result.ok && result.value) emit('settings', result.value as SettingsView);
    else if (!result.ok) error.value = t(result.error === 'open-failed' ? 'about.logs.openFailed' : result.error === 'busy' ? 'about.busy' : result.error === 'unavailable' ? 'about.unavailable' : 'about.failedGeneric');
    return result.ok;
  } finally { busy.value = null; }
}

const openLogs = () => run('logs', UI_COMMANDS.AboutOpenLogs);
const exportDiagnostics = () => run('diagnostics', UI_COMMANDS.AboutExportDiagnostics);
const dismiss = () => run('dismiss', UI_COMMANDS.AboutDismiss);
// F17V-12: opening a confirm box focuses its safe button (cancel); closing it by cancel, Escape or after the clear returns focus to the entry's button.
const root = ref<HTMLElement | null>(null);
const focusIn = async (selector: string) => { await nextTick(); root.value?.querySelector<HTMLElement>(selector)?.focus(); };
async function ask(kind: string): Promise<void> {
  confirming.value = kind;
  await focusIn(`[data-clean-cancel="${kind}"]`);
}
async function cancel(kind: string): Promise<void> {
  if (busy.value) return;
  confirming.value = null;
  await focusIn(`[data-clean-button="${kind}"]`);
}
async function clearNow(kind: string): Promise<void> {
  confirming.value = null;
  await run(`clear-${kind}`, UI_COMMANDS.DataClear, { kind, confirm: true });
  await focusIn(`[data-clean-button="${kind}"]`);
}

const sizeText = (bytes: number) => (bytes < 1024 ? t('about.size.bytes', { n: bytes }) : bytes < 1024 * 1024 ? t('about.size.kb', { n: (bytes / 1024).toFixed(1) }) : t('about.size.mb', { n: (bytes / 1024 / 1024).toFixed(1) }));
const diagnosticsError = (code: string) => { const key = `about.diagnostics.error.${code}`; const text = t(key); return text === key ? t('about.diagnostics.error.other') : text; };
const cleanError = (code: string) => { const key = `about.clean.error.${code}`; const text = t(key); return text === key ? t('about.clean.error.other') : text; };
const usage = (d: DataCleanView) => (d.kind === 'settings' ? '' : d.kind === 'accounts' ? t('about.clean.usage.accounts', { n: d.count }) : d.bytes > 0 ? t('about.clean.usage.countBytes', { n: d.count, size: sizeText(d.bytes) }) : t('about.clean.usage.count', { n: d.count }));
const kindName = (kind: string) => t(`about.clean.${kind}`);
const cleanLabel = (key: string, kind: string) => `${t(key).replace('…', '')} ${kindName(kind)}`.trim(); // a distinct accessible name per entry (F17V-12)
</script>

<template>
  <section ref="root" class="group about" data-about>
    <h2>{{ t('about.title') }}</h2>
    <SettingRow :title="t('about.version')"><span data-about-version>{{ about.version }}</span></SettingRow>
    <SettingRow :title="t('about.build')"><span data-about-build>{{ about.build }}</span></SettingRow>
    <SettingRow :title="t('about.platform')"><span class="small" data-about-platform>{{ about.os }} · {{ about.runtime }}</span></SettingRow>

    <UpdateSettings v-if="settings.update" :settings="settings" :bridge="bridge" @settings="(next) => emit('settings', next)" />

    <h3>{{ t('about.logs.title') }}</h3>
    <SettingRow :title="t('about.logs.location')" :hint="t('about.logs.hint', { files: about.logFiles, size: sizeText(about.logBytes) })">
      <code class="path" data-about-log-location>{{ about.logLocation }}</code>
      <button type="button" class="btn" :disabled="!about.canOpenLogs || !!busy" data-about-open-logs @click="openLogs">{{ t('about.logs.open') }}</button>
    </SettingRow>

    <h3>{{ t('about.diagnostics.title') }}</h3>
    <p class="hint-text small">{{ t('about.diagnostics.hint') }}</p>
    <div class="actions">
      <button type="button" class="btn primary" :disabled="!about.canExport || !!busy" data-about-export @click="exportDiagnostics">{{ busy === 'diagnostics' ? t('about.diagnostics.exporting') : t('about.diagnostics.export') }}</button>
    </div>
    <p v-if="about.diagnostics && !about.diagnostics.error" class="result small hint-text" role="status" data-about-exported>
      {{ t('about.diagnostics.done', { file: about.diagnostics.fileName ?? '', files: about.diagnostics.logFiles, lines: about.diagnostics.logLines, dropped: about.diagnostics.droppedLines, size: sizeText(about.diagnostics.bytes) }) }}
      <button type="button" class="btn" :disabled="!!busy" data-about-dismiss @click="dismiss">{{ t('about.dismiss') }}</button>
    </p>
    <p v-if="about.diagnostics?.error" class="result small error-text" role="alert" data-about-export-error>{{ diagnosticsError(about.diagnostics.error) }}</p>

    <h3>{{ t('about.licenses.title') }}</h3>
    <p class="hint-text small">{{ t('about.licenses.hint', { n: about.licenses.length }) }}</p>
    <button type="button" class="btn" :aria-expanded="showLicenses" data-about-licenses-toggle @click="showLicenses = !showLicenses">{{ showLicenses ? t('about.licenses.hide') : t('about.licenses.show') }}</button>
    <ul v-if="showLicenses" class="licenses" data-about-licenses>
      <li v-for="l in about.licenses" :key="l.kind + l.name + l.version"><strong>{{ l.name }}</strong> {{ l.version }} · {{ l.license }}</li>
    </ul>

    <h3>{{ t('about.clean.title') }}</h3>
    <p class="hint-text small">{{ t('about.clean.hint') }}</p>
    <div v-for="d in about.data" :key="d.kind" class="clean" :data-clean="d.kind">
      <SettingRow :title="kindName(d.kind)" :hint="t(`about.clean.${d.kind}.deletes`) + ' ' + t(`about.clean.${d.kind}.keeps`)">
        <span v-if="usage(d)" class="small hint-text" :data-clean-usage="d.kind">{{ usage(d) }}</span>
        <button v-if="confirming !== d.kind" type="button" class="btn" :disabled="!d.available || !!busy" :data-clean-button="d.kind" :aria-label="cleanLabel('about.clean.run', d.kind)" @click="ask(d.kind)">{{ t('about.clean.run') }}</button>
      </SettingRow>
      <div v-if="confirming === d.kind" class="pending" role="alertdialog" :aria-label="kindName(d.kind)" :data-clean-confirm="d.kind" @keydown.esc.stop="cancel(d.kind)">
        <p class="small">{{ t('about.clean.confirm', { what: kindName(d.kind) }) }}</p>
        <p class="small hint-text">{{ t(`about.clean.${d.kind}.deletes`) }} {{ t(`about.clean.${d.kind}.keeps`) }}</p>
        <div class="actions">
          <button type="button" class="btn danger" :disabled="!!busy" :aria-label="cleanLabel('about.clean.confirmRun', d.kind)" :data-clean-confirm-button="d.kind" @click="clearNow(d.kind)">{{ t('about.clean.confirmRun') }}</button>
          <button type="button" class="btn" :disabled="!!busy" :data-clean-cancel="d.kind" @click="cancel(d.kind)">{{ t('about.clean.cancel') }}</button>
        </div>
      </div>
    </div>
    <p v-if="about.cleaned && !about.cleaned.error" class="result small hint-text" role="status" data-clean-done>
      {{ t('about.clean.done', { what: kindName(about.cleaned.kind), n: about.cleaned.removed }) }}<template v-if="about.cleaned.skipped > 0"> {{ t('about.clean.skipped', { n: about.cleaned.skipped }) }}</template>
      <button type="button" class="btn" :disabled="!!busy" data-about-dismiss-clean @click="dismiss">{{ t('about.dismiss') }}</button>
    </p>
    <p v-if="about.cleaned?.error" class="result small error-text" role="alert" data-clean-error>{{ cleanError(about.cleaned.error) }}</p>
    <p v-if="error" class="error-text small" role="alert" data-about-failed>{{ error }}</p>
  </section>
</template>

<style scoped>
.group { margin-bottom: 22px; }
.group h2 { font-size: 12.5px; font-weight: 600; margin: 0 0 4px; }
.group h3 { font-size: 12px; font-weight: 600; margin: 16px 0 4px; }
.small { font-size: 11px; margin: 2px 0; word-break: break-word; }
.pending { border: 1px solid var(--line); border-radius: 6px; padding: 10px 12px; margin: 6px 0 10px; }
.actions { display: flex; gap: 8px; margin-top: 8px; align-items: center; }
.result { margin: 8px 0; display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.path { font-size: 11px; word-break: break-all; }
.licenses { list-style: none; margin: 8px 0; padding: 8px 10px; font-size: 11.5px; max-height: 220px; overflow: auto; border: 1px solid var(--line); border-radius: 6px; }
.danger { color: var(--error, #c0392b); }
.licenses li { padding: 2px 0; word-break: break-word; }
</style>
