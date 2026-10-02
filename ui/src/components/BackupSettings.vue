<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { UI_COMMANDS, type BackupPreviewView, type CommandResult, type SettingsView } from '@protocol/ui';
import SettingRow from './SettingRow.vue';
import { t } from '../locales/i18n';

// Backup and restore (F17.1). Export: the host opens the save dialog (the page never sees a path); keys are left out unless the box is ticked, and then a
// password is required. Restore: the host opens the file dialog, an encrypted file asks for its password, then the page shows what the restore would
// replace (settings, accounts and their authorization, prompts, the plugin list as information only, keys) and what it keeps (favorites, outbox).
// Confirming only schedules the switch for the next start; nothing is changed now. Passwords live in this component only while it is open: they are
// cleared after use, when the window hides (clearToken) and never put in browser storage or echoed back (S07). Every command answers with the fresh view.
interface Commands { command(name: string, payload?: object): Promise<CommandResult> }
const props = defineProps<{ settings: SettingsView; bridge: Commands; clearToken: number }>();
const emit = defineEmits<{ settings: [SettingsView] }>();
const backup = computed(() => props.settings.backup!);
const busy = ref<string | null>(null);
const error = ref<string | null>(null);
const includeKeys = ref(false);
const exportPassword = ref('');
const unlockPassword = ref('');

watch(() => props.clearToken, () => { exportPassword.value = ''; unlockPassword.value = ''; });

async function run(key: string, name: string, payload?: object): Promise<boolean> {
  if (busy.value) return false;
  busy.value = key;
  error.value = null;
  try {
    const result = await props.bridge.command(name, payload);
    if (result.ok && result.value) emit('settings', result.value as SettingsView);
    else if (!result.ok) error.value = t(result.error === 'unavailable' ? 'backup.unavailable' : result.error === 'busy' ? 'backup.busy' : 'backup.failedGeneric');
    return result.ok;
  } finally { busy.value = null; }
}

const passwordOk = computed(() => exportPassword.value.length === 0 || (exportPassword.value.length >= 8 && exportPassword.value.length <= 256));
const canExport = computed(() => backup.value.canExport && passwordOk.value && (!includeKeys.value || exportPassword.value.length > 0));
async function exportNow(): Promise<void> {
  const payload = exportPassword.value ? { includeSecrets: includeKeys.value, password: exportPassword.value } : { includeSecrets: includeKeys.value };
  const ok = await run('export', UI_COMMANDS.BackupExport, payload);
  exportPassword.value = ''; // used once; the host keeps nothing
  if (ok && !backup.value.last?.error) includeKeys.value = false;
}
const pick = () => run('pick', UI_COMMANDS.BackupPick);
async function unlock(): Promise<void> {
  const password = unlockPassword.value;
  unlockPassword.value = '';
  await run('unlock', UI_COMMANDS.BackupUnlock, { password });
}
const apply = (p: BackupPreviewView) => run('apply', UI_COMMANDS.BackupApply, { token: p.token });
const discard = () => run('discard', UI_COMMANDS.BackupDiscard);
const undo = () => run('undo', UI_COMMANDS.BackupUndo);
const dismiss = () => run('dismiss', UI_COMMANDS.BackupDismiss);

const errorText = (code: string) => { const key = `backup.error.${code}`; const text = t(key); return text === key ? t('backup.error.other') : text; };
const areaName = (area: string) => t(`backup.area.${area}`);
const deltaText = (d: BackupPreviewView['deltas'][number]) =>
  !d.differs ? t('backup.delta.same', { area: areaName(d.area) }) : d.area === 'settings' ? t('backup.delta.settings', { area: areaName(d.area) }) : t('backup.delta.changed', { area: areaName(d.area), current: d.current, backup: d.backup });
const pluginStatus = (status: string) => t(`backup.plugin.${status}`);
const createdText = (iso: string | undefined) => (iso ? new Date(iso).toLocaleString() : '');
const failedReason = (code: string | undefined) => errorText(code ?? 'other');
const exportedNote = computed(() => {
  const last = backup.value.last;
  if (!last || last.action !== 'export' || last.error) return '';
  return last.includedSecrets ? t('backup.exported.keys', { n: last.secretCount }) : last.encrypted ? t('backup.exported.encrypted') : t('backup.exported.plain');
});
const lastError = computed(() => (backup.value.last?.error ? errorText(backup.value.last.error) : ''));
</script>

<template>
  <section class="group backup" data-backup>
    <h2>{{ t('backup.title') }}</h2>
    <p class="hint-text small">{{ t('backup.hint') }}</p>

    <h3>{{ t('backup.export') }}</h3>
    <SettingRow :title="t('backup.includeKeys')" :hint="t('backup.export.hint')">
      <input v-model="includeKeys" type="checkbox" :disabled="!!busy" data-backup-keys :aria-label="t('backup.includeKeys')" />
    </SettingRow>
    <SettingRow :title="t('backup.password')" :hint="t('backup.password.hint')" for-id="backup-password">
      <input id="backup-password" v-model="exportPassword" type="password" autocomplete="off" spellcheck="false" maxlength="256" class="field" :disabled="!!busy" data-backup-password />
    </SettingRow>
    <div class="actions">
      <button type="button" class="btn primary" :disabled="!canExport || !!busy" data-backup-export @click="exportNow">{{ busy === 'export' ? t('backup.exporting') : t('backup.exportNow') }}</button>
    </div>
    <p v-if="backup.last?.action === 'export' && !backup.last.error" class="result small hint-text" role="status" data-backup-exported>{{ t('backup.exported', { file: backup.last.fileName ?? '' }) }} {{ exportedNote }}</p>
    <p v-if="backup.last?.action === 'export' && backup.last.error" class="result small error-text" role="alert" data-backup-export-error>{{ lastError }}</p>

    <h3>{{ t('backup.import') }}</h3>
    <SettingRow :title="t('backup.import')" :hint="backup.canImport ? t('backup.import.hint') : t('backup.unavailable')">
      <button type="button" class="btn" :disabled="!backup.canImport || !!busy" data-backup-pick @click="pick">{{ busy === 'pick' ? t('backup.choosing') : t('backup.choose') }}</button>
    </SettingRow>
    <p v-if="backup.last?.action === 'preview' && backup.last.error" class="result small error-text" role="alert" data-backup-preview-error>{{ lastError }}</p>

    <div v-if="backup.step === 'password'" class="pending" data-backup-unlock>
      <h3>{{ t('backup.unlock.title', { file: backup.fileName ?? '' }) }}</h3>
      <p class="hint-text small">{{ t('backup.unlock.hint') }}</p>
      <div class="actions">
        <input v-model="unlockPassword" type="password" autocomplete="off" spellcheck="false" maxlength="256" class="field" :aria-label="t('backup.password')" :disabled="!!busy" data-backup-unlock-password @keydown.enter="unlockPassword && unlock()" />
        <button type="button" class="btn primary" :disabled="!unlockPassword || !!busy" data-backup-unlock-button @click="unlock">{{ busy === 'unlock' ? t('backup.unlocking') : t('backup.unlock') }}</button>
        <button type="button" class="btn" :disabled="!!busy" data-backup-cancel @click="discard">{{ t('backup.discard') }}</button>
      </div>
    </div>

    <div v-if="backup.step === 'preview' && backup.preview" class="pending" data-backup-preview>
      <h3>{{ t('backup.preview.title', { file: backup.fileName ?? '' }) }}</h3>
      <p class="small hint-text" data-backup-meta>{{ t('backup.preview.meta', { version: backup.preview.appVersion }) }} {{ createdText(backup.preview.created) }}</p>
      <p v-if="backup.preview.schemaOlder" class="small hint-text" data-backup-older>{{ t('backup.olderSchema') }}</p>
      <p class="small"><strong>{{ t('backup.preview.replaces') }}</strong></p>
      <ul class="deltas" data-backup-deltas>
        <li v-for="d in backup.preview.deltas" :key="d.area" :data-delta="d.area" :class="d.differs ? '' : 'hint-text'">{{ deltaText(d) }}</li>
      </ul>
      <p v-if="backup.preview.includesSecrets" class="small" data-backup-keys-included>{{ t('backup.keysIncluded', { n: backup.preview.backupSecrets }) }}</p>
      <p v-if="backup.preview.keysRemoved > 0" class="small warn-text" data-backup-keys-removed>{{ t('backup.keysRemoved', { n: backup.preview.keysRemoved }) }}</p>
      <p class="small hint-text" data-backup-kept>{{ t('backup.kept', { fav: backup.preview.keptFavorites, outbox: backup.preview.keptOutbox }) }}</p>

      <template v-if="backup.preview.accounts.length">
        <h3>{{ t('backup.accounts.title') }}</h3>
        <p class="small hint-text">{{ t('backup.accounts.hint') }}</p>
        <ul class="deltas" data-backup-accounts>
          <li v-for="a in backup.preview.accounts" :key="a.id" :data-account="a.id">{{ a.missingSecrets.length ? t('backup.account.keysMissing', { label: a.label, names: a.missingSecrets.join(', ') }) : t('backup.account.keysIncluded', { label: a.label }) }}</li>
        </ul>
      </template>

      <template v-if="backup.preview.plugins.length || backup.preview.missingPackages.length">
        <h3>{{ t('backup.plugins.title') }}</h3>
        <ul class="deltas" data-backup-plugins>
          <li v-for="p in backup.preview.plugins" :key="p.id" :data-plugin="p.id" :data-status="p.status">{{ p.id }} {{ p.backupVersion }} · {{ pluginStatus(p.status) }}</li>
        </ul>
        <p v-if="backup.preview.missingPackages.length" class="small warn-text" data-backup-missing>{{ t('backup.missing', { ids: backup.preview.missingPackages.join(', ') }) }}</p>
        <p v-if="backup.preview.disabledInstances.length" class="small warn-text" data-backup-disabled>{{ t('backup.disabled', { n: backup.preview.disabledInstances.length }) }}</p>
      </template>

      <div class="actions">
        <button type="button" class="btn primary" :disabled="!!busy" data-backup-apply @click="apply(backup.preview)">{{ busy === 'apply' ? t('backup.applying') : t('backup.apply') }}</button>
        <button type="button" class="btn" :disabled="!!busy" data-backup-cancel @click="discard">{{ t('backup.discard') }}</button>
      </div>
    </div>

    <div v-if="backup.scheduled" class="pending" role="status" data-backup-scheduled>
      <p class="small">{{ backup.scheduledSource === 'undo' ? t('backup.scheduled.undo') : t('backup.scheduled') }}</p>
      <div class="actions">
        <button type="button" class="btn" :disabled="!!busy" data-backup-unschedule @click="discard">{{ t('backup.scheduled.cancel') }}</button>
      </div>
    </div>

    <div v-if="backup.applied" class="result small" role="status" data-backup-applied :class="backup.applied.state === 'Failed' ? 'error-text' : 'hint-text'">
      <template v-if="backup.applied.state === 'Failed'"><span data-backup-applied-failed>{{ t('backup.failed', { reason: failedReason(backup.applied.error) }) }}</span></template>
      <template v-else>
        <span data-backup-applied-ok>{{ backup.applied.source === 'undo' ? t('backup.applied.undo') : t('backup.applied') }}</span>
        <span v-if="backup.applied.disabledInstances > 0" data-backup-applied-disabled> {{ t('backup.applied.disabled', { n: backup.applied.disabledInstances }) }}</span>
      </template>
      <button type="button" class="btn" :disabled="!!busy" data-backup-dismiss @click="dismiss">{{ t('backup.dismiss') }}</button>
    </div>

    <SettingRow v-if="backup.canUndo && !backup.scheduled" :title="t('backup.undo')" :hint="t('backup.undo.hint')">
      <button type="button" class="btn" :disabled="!!busy" data-backup-undo @click="undo">{{ t('backup.undo') }}</button>
    </SettingRow>
    <p v-if="error" class="error-text small" role="alert" data-backup-failed>{{ error }}</p>
  </section>
</template>

<style scoped>
.group { margin-bottom: 22px; }
.group h2 { font-size: 12.5px; font-weight: 600; margin: 0 0 4px; }
.group h3 { font-size: 12px; font-weight: 600; margin: 16px 0 4px; }
.small { font-size: 11px; margin: 2px 0; word-break: break-word; }
.pending { border: 1px solid var(--line); border-radius: 6px; padding: 10px 12px; margin: 10px 0; }
.pending h3 { margin-top: 0; }
.deltas { list-style: none; margin: 6px 0; padding: 0; font-size: 12px; word-break: break-word; }
.actions { display: flex; gap: 8px; margin-top: 8px; align-items: center; }
.warn-text { color: var(--warn, #b26b00); }
.result { margin: 8px 0; display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.field { min-width: 180px; }
</style>
