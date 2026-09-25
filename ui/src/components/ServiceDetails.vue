<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { UI_COMMANDS, type AccountView, type CommandResult, type ServiceValidationView, type ServiceView, type SettingsView } from '@protocol/ui';
import SettingRow from './SettingRow.vue';
import SecretField from './SecretField.vue';
import Icon from './Icon.vue';
import { t, serviceName } from '../locales/i18n';

// Expanded area of one service row (DESIGN 7 "已保存凭据", "账户关联行", "状态行"; 第四版 "凭据绑定").
// Key entry: a new value is sent only after the user confirms the exact address it will be sent to
// (Secret.WriteNew with confirmGrants); saved values never come back (S07). Validation shows credential
// validity apart from service availability, and says "not validated" until a real call was made.
interface Commands { command(name: string, payload?: object): Promise<CommandResult> }
const props = defineProps<{ service: ServiceView; accounts: AccountView[]; bridge: Commands; clearToken: number }>();
const emit = defineEmits<{ settings: [SettingsView]; error: [] }>();

const DEEPL_FREE = 'https://api-free.deepl.com:443';
const DEEPL_PRO = 'https://api.deepl.com:443';
const wired = computed(() => props.service.credentialTargets !== undefined && props.service.credentialTargets !== null);
const target = (secret: string) => props.service.credentialTargets?.find((c) => c.secret === secret);
const saved = (secret: string) => target(secret)?.saved ?? !!props.accounts.find((a) => a.id === props.service.accountId)?.secrets.find((s) => s.name === secret)?.saved;
/** The address a newly typed value will go to: DeepL picks its endpoint from the key itself (":fx" = Free). */
const originFor = (secret: string, value: string) =>
  props.service.instanceId === 'deepl' && secret === 'apiKey' ? (value.trim().endsWith(':fx') ? DEEPL_FREE : DEEPL_PRO) : target(secret)?.origin ?? '';

// One pending confirmation at a time: either a typed value waiting for its address to be confirmed, or an
// "authorize" of an already saved value whose address is not granted yet.
type Pending = { kind: 'write'; secret: string; value: string; origins: string[]; done: (ok: boolean) => void } | { kind: 'authorize'; origins: string[] };
const pending = ref<Pending | null>(null);
const busy = ref(false);
function dropPending(): void {
  if (pending.value?.kind === 'write') pending.value.done(false);
  pending.value = null;
}
watch(() => props.clearToken, dropPending);

function adopt(result: CommandResult): void {
  if (result.ok && result.value) emit('settings', result.value as SettingsView);
  else if (!result.ok) emit('error');
}

async function write(secret: string, value: string, done: (ok: boolean) => void, confirmGrants: boolean): Promise<void> {
  busy.value = true;
  const result = await props.bridge.command(UI_COMMANDS.SecretWriteNew, { instanceId: props.service.instanceId, secretName: secret, value, confirmGrants });
  busy.value = false;
  validation.value = null;
  adopt(result);
  done(result.ok);
}
function onSave(secret: string, value: string, done: (ok: boolean) => void): void {
  if (!wired.value) { void write(secret, value, done, false); return; }
  dropPending();
  pending.value = { kind: 'write', secret, value, origins: [originFor(secret, value)], done };
}
function authorize(): void {
  dropPending();
  pending.value = { kind: 'authorize', origins: [...new Set((props.service.credentialTargets ?? []).filter((c) => c.saved && !c.granted).map((c) => c.origin))] };
}
async function confirm(): Promise<void> {
  const current = pending.value;
  if (!current || busy.value) return;
  pending.value = null;
  if (current.kind === 'write') { await write(current.secret, current.value, current.done, true); return; }
  busy.value = true;
  const result = await props.bridge.command(UI_COMMANDS.BindAccount, { instanceId: props.service.instanceId, confirmGrants: true });
  busy.value = false;
  adopt(result);
}
async function remove(secret: string): Promise<void> {
  validation.value = null;
  adopt(await props.bridge.command(UI_COMMANDS.SecretDelete, { instanceId: props.service.instanceId, secretName: secret }));
}
const needsGrant = computed(() => (props.service.credentialTargets ?? []).some((c) => c.saved && !c.granted));

// ---------- validation ----------
const validation = ref<{ running: boolean; view?: ServiceValidationView; error?: string } | null>(null);
async function validate(): Promise<void> {
  validation.value = { running: true };
  const result = await props.bridge.command(UI_COMMANDS.ValidateProvider, { serviceId: props.service.serviceId });
  validation.value = result.ok ? { running: false, view: result.value as ServiceValidationView } : { running: false, error: result.error };
}
const validationText = computed(() => {
  const v = validation.value;
  if (!v) return t('validate.never');
  if (v.running) return t('validate.running');
  if (!v.view) return t(v.error === 'missing-credential' ? 'validate.missing-credential' : 'validate.failed');
  const credential = t(`validate.credential.${v.view.credential}`);
  const service = v.view.serviceAvailable ? t('validate.available') : t('validate.unavailable', { reason: t(`error.${v.view.error ?? 'unavailable'}`) });
  return `${credential} · ${service}`;
});
const validationOk = computed(() => !!validation.value?.view && validation.value.view.credential !== 'invalid' && validation.value.view.serviceAvailable);
const validationFailed = computed(() => !!validation.value && !validation.value.running && !validationOk.value);
</script>

<template>
  <div class="details">
    <template v-for="name in service.secretNames" :key="name">
      <SettingRow :title="t(`secret.${name}`)" :hint="target(name) ? t('grant.target', { origin: target(name)!.origin }) : undefined">
        <span v-if="target(name)?.saved && !target(name)?.granted" class="warn" role="status"><Icon name="warning" :size="13" />{{ t('grant.notGranted') }}</span>
        <SecretField :id="`${service.instanceId}-${name}`" :label="`${serviceName(service.instanceId)} ${t(`secret.${name}`)}`" :saved="saved(name)" :clear-token="clearToken"
          @save="(value, done) => onSave(name, value, done)" @remove="remove(name)" />
      </SettingRow>
      <div v-if="pending?.kind === 'write' && pending.secret === name" class="confirm" role="alertdialog" :aria-label="t('grant.target', { origin: pending.origins[0] })">
        <span class="address">{{ t('grant.target', { origin: pending.origins[0] }) }}</span>
        <button type="button" class="btn" @click="dropPending">{{ t('grant.cancel') }}</button>
        <button type="button" class="btn primary" :disabled="busy" @click="confirm">{{ t('grant.confirm') }}</button>
      </div>
    </template>

    <div v-if="needsGrant && pending?.kind !== 'authorize'" class="authorize">
      <button type="button" class="btn" @click="authorize">{{ t('grant.authorize') }}</button>
    </div>
    <div v-if="pending?.kind === 'authorize'" class="confirm" role="alertdialog" :aria-label="t('grant.authorize')">
      <span class="address">{{ pending.origins.map((origin) => t('grant.target', { origin })).join('；') }}</span>
      <button type="button" class="btn" @click="dropPending">{{ t('grant.cancel') }}</button>
      <button type="button" class="btn primary" :disabled="busy" @click="confirm">{{ t('grant.confirm') }}</button>
    </div>

    <SettingRow v-if="service.plan" :title="t('plan.title')" :hint="t('plan.hint')">
      <span class="tag plan">{{ t(`plan.${service.plan}`) }}</span>
    </SettingRow>

    <div v-if="wired" class="status-line">
      <button type="button" class="btn" :disabled="validation?.running" @click="validate">{{ t('validate.run') }}</button>
      <Icon v-if="validationOk" name="check" :size="13" />
      <Icon v-else-if="validationFailed" name="warning" :size="13" class="error-text" />
      <span class="validation" :class="{ 'error-text': validationFailed }" role="status">{{ validationText }}</span>
    </div>
  </div>
</template>

<style scoped>
.confirm { min-height: 34px; display: flex; align-items: center; gap: 8px; padding: 3px 0 3px 10px; border-left: 1px solid var(--accent); margin: 6px 0; font-size: 11.5px; }
.address { flex: 1; word-break: break-all; }
.warn { display: inline-flex; align-items: center; gap: 4px; font-size: 11px; color: var(--error); }
.authorize { padding: 8px 0; }
.status-line { display: flex; align-items: center; gap: 8px; padding: 10px 0; font-size: 11px; }
.validation { color: var(--hint); }
.validation.error-text { font-size: 11.5px; color: var(--error); }
</style>
