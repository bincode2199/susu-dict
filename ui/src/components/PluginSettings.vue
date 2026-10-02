<script setup lang="ts">
import { computed, ref } from 'vue';
import { UI_COMMANDS, type CommandResult, type PluginPreviewView, type PluginTaskView, type SettingsView } from '@protocol/ui';
import SettingRow from './SettingRow.vue';
import { t } from '../locales/i18n';

// SetPlugins (F16.1, F16.2): choose a package (the host opens the file dialog; the page never sees a path), review what it would change against the
// installed version or the built-in one it overrides (a first install shows everything it asks for), confirm or discard, and uninstall. Origins and
// secrets are listed exactly as declared. A third-party signature is shown as an identity the user accepts, never as host endorsement.
// F16.2: updates found by an update check wait here beside the running version; a package that changes the signer, drops the signature or widens
// permissions is held until the user ticks the acknowledgement, and the old version keeps running until then. Installing or uninstalling cancels the
// calls the package has in flight; the page names them before the action and counts them after. Uninstall keeps the package's stored data unless
// the user asks to delete it. Every command answers with the fresh settings view.
interface Commands { command(name: string, payload?: object): Promise<CommandResult> }
const props = defineProps<{ settings: SettingsView; bridge: Commands }>();
const emit = defineEmits<{ settings: [SettingsView] }>();
const plugins = computed(() => props.settings.plugins!);
const cards = computed<PluginPreviewView[]>(() => [...(plugins.value.pending ? [plugins.value.pending] : []), ...(plugins.value.updates ?? [])]);
const busy = ref<string | null>(null);
const error = ref<string | null>(null);
const confirmUninstall = ref<string | null>(null);
const removeData = ref(false);
const acks = ref<Record<string, boolean>>({});

async function run(key: string, name: string, payload?: object): Promise<void> {
  if (busy.value) return;
  busy.value = key;
  error.value = null;
  try {
    const result = await props.bridge.command(name, payload);
    if (result.ok && result.value) emit('settings', result.value as SettingsView);
    else if (!result.ok) error.value = t(result.error === 'unavailable' ? 'plugins.unavailable' : result.error === 'busy' ? 'plugins.busy' : 'plugins.failed');
  } finally { busy.value = null; }
}
const pick = () => run('pick', UI_COMMANDS.PluginPick);
const checkUpdates = () => run('check', UI_COMMANDS.PluginCheckUpdates);
const held = (p: PluginPreviewView) => (p.reasons ?? []).length > 0;
const confirm = (p: PluginPreviewView) => run('confirm', UI_COMMANDS.PluginConfirm, held(p) ? { token: p.token, acknowledged: !!acks.value[p.token] } : { token: p.token });
const discard = (p: PluginPreviewView) => run('discard', UI_COMMANDS.PluginDiscard, { token: p.token });
const uninstall = async (id: string) => {
  const withData = removeData.value;
  confirmUninstall.value = null;
  removeData.value = false;
  await run(`uninstall:${id}`, UI_COMMANDS.PluginUninstall, withData ? { id, removeData: true } : { id });
};

const signerLine = (kind: string, signer: string) =>
  kind === 'host' ? t('plugins.signer.host', { id: signer }) : kind === 'thirdParty' ? t('plugins.signer.thirdParty', { id: signer.slice(0, 16) }) : t('plugins.signer.unsigned');
const hasChange = (p: PluginPreviewView) => p.addedCapabilities.length + p.addedOrigins.length + p.addedSecrets.length + p.removedCapabilities.length + p.removedOrigins.length + p.removedSecrets.length > 0;
const hasAdditions = (p: PluginPreviewView) => p.addedCapabilities.length + p.addedOrigins.length + p.addedSecrets.length > 0;
const against = (p: PluginPreviewView) =>
  p.against === 'none' ? t('plugins.diff.first') : p.against === 'builtin' ? t('plugins.diff.builtin', { v: p.baseVersion ?? '' }) : t('plugins.diff.installed', { v: p.baseVersion ?? '' });
const tasks = (list: PluginTaskView[] | undefined) => (list ?? []).map((x) => `${x.capability} × ${x.count}`).join(', ');
const reasonText = (r: string) => { const key = `plugins.reason.${r}`; const text = t(key); return text === key ? r : text; };
const issueText = (code: string) => { const key = `plugins.issue.${code}`; const text = t(key); return text === key ? t('plugins.issue.other', { code }) : text; };
const errorText = (code: string) => { const key = `plugins.error.${code}`; const text = t(key); return text === key ? t('plugins.failed') : text; };
const failureText = (code: string) => {
  const key = `plugins.check.failure.${code}`;
  const text = t(key);
  if (text !== key) return text;
  return code.startsWith('rejected:') ? issueText(code.slice(9)) : t('plugins.check.failure.other', { code });
};
</script>

<template>
  <section class="group plugins" data-plugins>
    <h2>{{ t('plugins.title') }}</h2>
    <p class="hint-text small">{{ t('plugins.hint') }}</p>

    <SettingRow :title="t('plugins.install')" :hint="plugins.canPick ? t('plugins.install.hint') : t('plugins.noDialog')">
      <button type="button" class="btn primary" :disabled="!plugins.canPick || !!busy" data-plugin-pick @click="pick">{{ busy === 'pick' ? t('plugins.picking') : t('plugins.choose') }}</button>
    </SettingRow>

    <SettingRow :title="t('plugins.updates')" :hint="plugins.canCheckUpdates ? t('plugins.updates.hint') : t('plugins.updates.none')">
      <button type="button" class="btn" :disabled="!plugins.canCheckUpdates || !!busy" data-plugin-check @click="checkUpdates">{{ busy === 'check' ? t('plugins.checking') : t('plugins.check') }}</button>
    </SettingRow>
    <div v-if="plugins.check" class="result small" role="status" data-plugin-check-result>
      <p v-if="plugins.check.failures.length === 0" class="hint-text" data-plugin-check-ok>{{ t('plugins.check.result', { n: plugins.check.checked, staged: plugins.check.staged }) }}</p>
      <template v-else>
        <p class="error-text" data-plugin-check-failed>{{ t('plugins.check.failed') }}</p>
        <ul class="issues">
          <li v-for="f in plugins.check.failures" :key="f.id + f.code" class="error-text" data-plugin-check-failure>{{ f.id ? f.id + ': ' : '' }}{{ failureText(f.code) }}</li>
        </ul>
        <p v-if="plugins.check.staged > 0" class="hint-text">{{ t('plugins.check.staged', { staged: plugins.check.staged }) }}</p>
      </template>
    </div>

    <div v-for="card in cards" :key="card.token" class="pending" data-plugin-pending :data-plugin-update="card.isUpdate ? 'true' : undefined">
      <h3>{{ t(card.isUpdate ? 'plugins.update.review' : 'plugins.review', { name: card.name, version: card.version }) }}</h3>
      <p class="small" data-plugin-id>{{ card.id }}</p>
      <p class="small" :class="card.signerKind === 'unsigned' ? 'warn-text' : 'hint-text'" data-plugin-signer>{{ signerLine(card.signerKind, card.signer) }}</p>
      <p v-if="card.signerKind === 'thirdParty'" class="hint-text small">{{ t('plugins.signer.thirdPartyNote') }}</p>
      <p v-if="card.overridesBuiltIn" class="small" data-plugin-override>{{ t('plugins.overrides', { v: card.overridesBuiltIn }) }}</p>
      <p v-if="card.replacesVersion" class="small" data-plugin-replaces>{{ t('plugins.replaces', { v: card.replacesVersion }) }}</p>
      <p class="small" data-plugin-against>{{ against(card) }}</p>
      <ul class="diff" data-plugin-diff>
        <li v-for="c in card.addedCapabilities" :key="'ac' + c" class="add" data-diff="add-capability">+ {{ t('plugins.diff.capability') }} {{ c }}</li>
        <li v-for="o in card.addedOrigins" :key="'ao' + o" class="add" data-diff="add-origin">+ {{ t('plugins.diff.origin') }} {{ o }}</li>
        <li v-for="s in card.addedSecrets" :key="'as' + s" class="add" data-diff="add-secret">+ {{ t('plugins.diff.secret') }} {{ s }}</li>
        <li v-for="c in card.removedCapabilities" :key="'rc' + c" class="rem" data-diff="remove-capability">- {{ t('plugins.diff.capability') }} {{ c }}</li>
        <li v-for="o in card.removedOrigins" :key="'ro' + o" class="rem" data-diff="remove-origin">- {{ t('plugins.diff.origin') }} {{ o }}</li>
        <li v-for="s in card.removedSecrets" :key="'rs' + s" class="rem" data-diff="remove-secret">- {{ t('plugins.diff.secret') }} {{ s }}</li>
      </ul>
      <p v-if="!hasChange(card)" class="hint-text small" data-plugin-nochange>{{ t('plugins.diff.none') }}</p>
      <p v-if="hasAdditions(card) && card.against !== 'none'" class="warn-text small" data-plugin-wider>{{ t('plugins.diff.wider') }}</p>
      <ul v-if="held(card)" class="issues" data-plugin-reasons>
        <li v-for="r in card.reasons" :key="r" class="warn-text small" :data-plugin-reason="r">{{ reasonText(r) }}</li>
      </ul>
      <p v-if="held(card)" class="hint-text small" data-plugin-old-runs>{{ t('plugins.held.oldRuns', { v: card.replacesVersion ?? card.baseVersion ?? '' }) }}</p>
      <label v-if="held(card)" class="small ack"><input v-model="acks[card.token]" type="checkbox" data-plugin-ack /> {{ t('plugins.ack') }}</label>
      <p v-if="(card.inFlight ?? []).length" class="warn-text small" data-plugin-inflight>{{ t('plugins.inflight', { tasks: tasks(card.inFlight) }) }}</p>
      <p class="hint-text small">{{ t('plugins.interrupt') }}</p>
      <div class="actions">
        <button type="button" class="btn primary" :disabled="!!busy || (held(card) && !acks[card.token])" data-plugin-confirm @click="confirm(card)">{{ busy === 'confirm' ? t('plugins.installing') : t(card.isUpdate ? 'plugins.update.apply' : 'plugins.confirm') }}</button>
        <button type="button" class="btn" :disabled="!!busy" data-plugin-discard @click="discard(card)">{{ t('plugins.discard') }}</button>
      </div>
    </div>

    <div v-if="plugins.last" class="result small" role="status" data-plugin-result :class="plugins.last.error ? 'error-text' : 'hint-text'">
      <template v-if="plugins.last.error">
        <span data-plugin-error>{{ plugins.last.action === 'preview' ? t('plugins.rejected') : errorText(plugins.last.error) }}</span>
        <ul v-if="plugins.last.issues.length" class="issues">
          <li v-for="(issue, i) in plugins.last.issues" :key="i" data-plugin-issue>{{ issueText(issue.code) }}<span v-if="issue.path && issue.path !== '$'" class="path"> ({{ issue.path }})</span></li>
        </ul>
      </template>
      <span v-else-if="plugins.last.action === 'install'" data-plugin-installed>{{ t('plugins.installed', { id: plugins.last.id ?? '', version: plugins.last.version ?? '' }) }}</span>
      <span v-else data-plugin-uninstalled>{{ plugins.last.restoredBuiltIn ? t('plugins.uninstalled.restored', { id: plugins.last.id ?? '', v: plugins.last.restoredBuiltIn }) : t('plugins.uninstalled', { id: plugins.last.id ?? '' }) }}</span>
      <span v-if="!plugins.last.error && (plugins.last.interrupted ?? 0) > 0" data-plugin-interrupted> {{ t('plugins.interrupted', { n: plugins.last.interrupted ?? 0 }) }}</span>
    </div>

    <h3>{{ t('plugins.installedList') }}</h3>
    <p v-if="plugins.installed.length === 0" class="hint-text small" data-plugin-none>{{ t('plugins.none') }}</p>
    <div v-for="p in plugins.installed" :key="p.id" class="item" :data-plugin-item="p.id">
      <SettingRow :title="`${p.name} ${p.version}`" :hint="p.id">
        <button v-if="confirmUninstall !== p.id" type="button" class="btn" :disabled="!!busy" data-plugin-uninstall @click="confirmUninstall = p.id; removeData = false">{{ t('plugins.uninstall') }}</button>
        <template v-else>
          <button type="button" class="btn danger" :disabled="!!busy" data-plugin-uninstall-confirm @click="uninstall(p.id)">{{ t('plugins.uninstall.confirm') }}</button>
          <button type="button" class="btn" :disabled="!!busy" @click="confirmUninstall = null">{{ t('plugins.discard') }}</button>
        </template>
      </SettingRow>
      <p class="small hint-text" data-plugin-signer-line>{{ signerLine(p.signerKind, p.signer) }}</p>
      <p v-if="p.overridesBuiltIn" class="small" data-plugin-override-line>{{ t('plugins.overrides', { v: p.overridesBuiltIn }) }} · {{ t('plugins.uninstall.restores') }}</p>
      <p class="small hint-text">{{ t('plugins.declares', { caps: p.capabilities.join(', '), origins: p.origins.length ? p.origins.join(', ') : t('plugins.noOrigins') }) }}</p>
      <template v-if="confirmUninstall === p.id">
        <p v-if="(p.inFlight ?? []).length" class="warn-text small" data-plugin-inflight>{{ t('plugins.inflight', { tasks: tasks(p.inFlight) }) }}</p>
        <label class="small ack"><input v-model="removeData" type="checkbox" data-plugin-remove-data /> {{ t('plugins.removeData') }}</label>
        <p class="hint-text small">{{ t('plugins.dataKept') }}</p>
      </template>
    </div>
    <p v-if="confirmUninstall" class="hint-text small">{{ t('plugins.interrupt') }}</p>
    <p v-if="error" class="error-text small" role="alert" data-plugin-failed>{{ error }}</p>
  </section>
</template>

<style scoped>
.group { margin-bottom: 22px; }
.group h2 { font-size: 12.5px; font-weight: 600; margin: 0 0 4px; }
.group h3 { font-size: 12px; font-weight: 600; margin: 16px 0 4px; }
.small { font-size: 11px; margin: 2px 0; word-break: break-word; }
.pending { border: 1px solid var(--line); border-radius: 6px; padding: 10px 12px; margin: 10px 0; }
.pending h3 { margin-top: 0; }
.diff { list-style: none; margin: 6px 0; padding: 0; font-size: 12px; word-break: break-all; }
.diff .add { color: var(--accent, inherit); }
.diff .rem { opacity: 0.75; }
.actions { display: flex; gap: 8px; margin-top: 8px; }
.warn-text { color: var(--warn, #b26b00); }
.issues { margin: 4px 0 0; padding-left: 16px; }
.result { margin: 8px 0; }
.item { border-top: 1px solid var(--line); padding: 4px 0; }
.ack { display: block; margin: 6px 0; }
</style>
