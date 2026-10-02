<script setup lang="ts">
import { computed, nextTick, ref } from 'vue';
import { UI_COMMANDS, type CommandResult, type SettingsView } from '@protocol/ui';
import SettingRow from './SettingRow.vue';
import Toggle from './Toggle.vue';
import { t } from '../locales/i18n';

// Application update (F18.2), inside the About page. The page never sends a URL, path or file name: the host knows its own source and install folder. Checking
// (also on the background schedule) never downloads; Download stages a verified package; Install asks for a second click, names the tasks it would interrupt,
// and only then sends the command. Every error is a stable key from the host and reads in both languages; an unsigned or failed response is never shown as a
// version or as "up to date".
interface Commands { command(name: string, payload?: object): Promise<CommandResult> }
const props = defineProps<{ settings: SettingsView; bridge: Commands }>();
const emit = defineEmits<{ settings: [SettingsView] }>();
const update = computed(() => props.settings.update!);
const busy = ref<string | null>(null);
const error = ref<string | null>(null);
const confirming = ref(false);
const root = ref<HTMLElement | null>(null);

const sizeText = (bytes: number) => t('update.size.mb', { n: Math.max(0.1, bytes / 1024 / 1024).toFixed(1) });
const errorText = (code: string) => { const key = `update.error.${code}`; const text = t(key); return text === key ? t('update.error.other') : text; };
const state = computed(() => update.value.state);
const offerVersion = computed(() => update.value.offer?.version ?? '');
const stateText = computed(() => t(`update.state.${state.value}`, { version: offerVersion.value, size: sizeText(update.value.offer?.size ?? 0) }));
const lastCheck = computed(() => update.value.lastCheck ? new Date(update.value.lastCheck).toLocaleString() : t('update.never'));
const canCheck = computed(() => update.value.canCheck && !busy.value && !['downloading', 'installing', 'ready'].includes(state.value));

async function run(key: string, name: string, payload?: object): Promise<boolean> {
  if (busy.value) return false;
  busy.value = key;
  error.value = null;
  try {
    const result = await props.bridge.command(name, payload);
    if (result.ok && result.value) emit('settings', result.value as SettingsView);
    else if (!result.ok) {
      const map: Record<string, string> = { busy: 'update.busy', unavailable: 'update.unavailable', 'not-ready': 'update.notReady', 'helper-failed': 'update.helperFailed' };
      error.value = t(map[result.error ?? ''] ?? 'update.error.other');
    }
    return result.ok;
  } finally { busy.value = null; }
}

const focusIn = async (selector: string) => { await nextTick(); root.value?.querySelector<HTMLElement>(selector)?.focus(); };
const check = () => run('check', UI_COMMANDS.UpdateCheck);
const download = () => run('download', UI_COMMANDS.UpdateDownload);
const discard = () => run('discard', UI_COMMANDS.UpdateDiscard);
const setAuto = (enabled: boolean) => run('auto', UI_COMMANDS.UpdateAutoCheck, { enabled });
async function ask(): Promise<void> { confirming.value = true; await focusIn('[data-update-cancel]'); }
async function cancel(): Promise<void> { if (busy.value) return; confirming.value = false; await focusIn('[data-update-install]'); }
async function installNow(): Promise<void> {
  const ok = await run('install', UI_COMMANDS.UpdateInstall, { acknowledgedInFlight: update.value.inFlight > 0 });
  if (!ok) confirming.value = false;
}
</script>

<template>
  <section ref="root" class="update" data-update>
    <h3>{{ t('update.title') }}</h3>
    <p class="small" data-update-current>{{ t('update.current', { version: update.currentVersion }) }}</p>
    <p v-if="!update.keyringEmbedded" class="hint-text small" data-update-unsigned>{{ t('update.unsignedBuild') }}</p>
    <p v-if="!update.canCheck" class="hint-text small" data-update-nosource>{{ t('update.noSource') }}</p>

    <template v-else>
      <p class="small" role="status" data-update-state>{{ stateText }}</p>
      <p v-if="update.error && state !== 'rolledBack'" class="error-text small" role="alert" data-update-error>{{ errorText(update.error) }}</p>
      <p v-if="state === 'rolledBack'" class="error-text small" role="alert" data-update-rolled-back>{{ errorText(update.error ?? 'interrupted') }}</p>
      <p v-if="update.offer?.notes && ['available', 'ready'].includes(state)" class="hint-text small" data-update-notes>{{ t('update.notes') }}: {{ update.offer.notes }}</p>

      <div class="actions">
        <button type="button" class="btn" :disabled="!canCheck" data-update-check @click="check">{{ busy === 'check' ? t('update.checking') : t('update.check') }}</button>
        <button v-if="state === 'available'" type="button" class="btn primary" :disabled="!!busy" data-update-download @click="download">{{ busy === 'download' ? t('update.downloading') : t('update.download') }}</button>
        <button v-if="state === 'ready' && !confirming" type="button" class="btn primary" :disabled="!!busy" data-update-install @click="ask">{{ t('update.install') }}</button>
        <button v-if="['ready', 'rolledBack', 'committed'].includes(state)" type="button" class="btn" :disabled="!!busy" data-update-discard @click="discard">{{ state === 'ready' ? t('update.discard') : t('update.dismiss') }}</button>
      </div>

      <div v-if="confirming && state === 'ready'" class="pending" role="alertdialog" :aria-label="t('update.install')" data-update-confirm @keydown.esc.stop="cancel">
        <p class="small">{{ t('update.installConfirm') }}</p>
        <p v-if="update.inFlight > 0" class="small error-text" data-update-in-flight>{{ t('update.inFlight', { n: update.inFlight }) }}</p>
        <div class="actions">
          <button type="button" class="btn danger" :disabled="!!busy" data-update-install-now @click="installNow">{{ t('update.installNow') }}</button>
          <button type="button" class="btn" :disabled="!!busy" data-update-cancel @click="cancel">{{ t('update.cancel') }}</button>
        </div>
      </div>

      <SettingRow :title="t('update.auto')" :hint="t('update.lastCheck', { time: lastCheck })">
        <Toggle :model-value="update.autoCheck" :label="t('update.auto')" :disabled="!!busy" data-update-auto @update:model-value="setAuto" />
      </SettingRow>
    </template>
    <p v-if="error" class="error-text small" role="alert" data-update-failed>{{ error }}</p>
  </section>
</template>

<style scoped>
.update { margin: 6px 0 14px; }
.update h3 { font-size: 12px; font-weight: 600; margin: 16px 0 4px; }
.small { font-size: 11px; margin: 2px 0; word-break: break-word; }
.pending { border: 1px solid var(--line); border-radius: 6px; padding: 10px 12px; margin: 6px 0 10px; }
.actions { display: flex; gap: 8px; margin-top: 8px; align-items: center; flex-wrap: wrap; }
.danger { color: var(--error, #c0392b); }
</style>
