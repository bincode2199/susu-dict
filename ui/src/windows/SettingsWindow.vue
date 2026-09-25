<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue';
import { UI_COMMANDS, type CommandResult, type SettingsIssueView, type SettingsView } from '@protocol/ui';
import type { Bridge } from '../bridge/bridge';
import type { UiState } from '../bridge/store';
import TitleBar from '../components/TitleBar.vue';
import Icon, { type IconName } from '../components/Icon.vue';
import SettingRow from '../components/SettingRow.vue';
import Toggle from '../components/Toggle.vue';
import Stepper from '../components/Stepper.vue';
import SecretField from '../components/SecretField.vue';
import ServiceDetails from '../components/ServiceDetails.vue';
import HotkeyField from '../components/HotkeyField.vue';
import { t, serviceName } from '../locales/i18n';

// Settings 900×700 (DESIGN 9): centered each time, 190 px navigation, content padding 20/26. F03 provides the
// minimal pages (DEV-PLAN F03.4); pages of features not built yet are not offered. Credentials are one-way.
const props = defineProps<{ bridge: Bridge; state: UiState }>();
type Page = 'general' | 'hotkeys' | 'engines' | 'ai' | 'ocr' | 'speech' | 'vocab' | 'network';
const pages: { id: Page; icon: IconName }[] = [
  { id: 'general', icon: 'settings' }, { id: 'hotkeys', icon: 'keyboard' }, { id: 'engines', icon: 'grid' }, { id: 'ai', icon: 'sparkle' },
  { id: 'ocr', icon: 'ocr' }, { id: 'speech', icon: 'audio' }, { id: 'vocab', icon: 'book' }, { id: 'network', icon: 'globe' },
];
const page = ref<Page>('general');
const clearToken = ref(0); // bumps on page change and window hide: unsaved secret input is dropped
watch(page, () => clearToken.value++);
watch(() => props.state.hiddenCount, () => clearToken.value++);

const view = computed(() => props.state.settings);
const draft = reactive({ general: {} as SettingsView['general'], hotkeys: {} as Record<string, string>, network: {} as SettingsView['network'], services: {} as Record<string, boolean> });
const message = ref<{ kind: 'ok' | 'error'; text: string } | null>(null);
const issues = ref<SettingsIssueView[]>([]);
const expanded = ref<string | null>(null);

let loaded = false;
function load(source: SettingsView | null): void {
  if (!source) return;
  loaded = true;
  draft.general = { ...source.general };
  draft.hotkeys = Object.fromEntries(source.hotkeys.map((h) => [h.action, h.chord]));
  draft.network = { ...source.network };
  draft.services = Object.fromEntries(source.services.map((s) => [s.serviceId, s.enabled]));
}
const snapshotOf = (source: SettingsView | null) => source && JSON.stringify({ g: source.general, h: Object.fromEntries(source.hotkeys.map((h) => [h.action, h.chord])), n: { ...source.network }, s: Object.fromEntries(source.services.map((s) => [s.serviceId, s.enabled])) });
const dirty = computed(() => !!view.value && JSON.stringify({ g: draft.general, h: draft.hotkeys, n: draft.network, s: draft.services }) !== snapshotOf(view.value));
// Adopt host changes (e.g. a hand edit of settings.yaml) unless the user has unsaved edits in the form.
watch(view, (next) => { if (!loaded || !dirty.value) load(next); }, { immediate: true });

function applyResult(result: CommandResult): void {
  issues.value = [];
  if (result.ok) {
    if (result.value) props.state.settings = result.value as SettingsView;
    load(props.state.settings);
    message.value = { kind: 'ok', text: t('save.saved') };
    return;
  }
  if (result.error === 'conflict' && result.value) {
    props.state.settings = result.value as SettingsView;
    load(props.state.settings);
    message.value = { kind: 'error', text: t('save.conflict') };
  } else if (result.error === 'invalid') {
    issues.value = (result.value as SettingsIssueView[]) ?? [];
    message.value = { kind: 'error', text: t('save.invalid') };
  } else message.value = { kind: 'error', text: t('save.failed') };
}

async function save(): Promise<void> {
  if (!view.value) return;
  const result = await props.bridge.command(UI_COMMANDS.SettingsSave, {
    expectedRevision: view.value.revision,
    expectedFileHash: view.value.fileHash,
    general: draft.general,
    hotkeys: Object.entries(draft.hotkeys).map(([action, chord]) => ({ action, chord, state: 'ok' })),
    network: draft.network,
    services: Object.entries(draft.services).map(([serviceId, enabled]) => ({ serviceId, enabled })),
  });
  applyResult(result);
}
function discard(): void {
  load(view.value);
  message.value = null;
  issues.value = [];
}
async function writeSecret(instanceId: string, secretName: string, value: string, done: (ok: boolean) => void): Promise<void> {
  const result = await props.bridge.command(UI_COMMANDS.SecretWriteNew, { instanceId, secretName, value });
  if (!result.ok) message.value = { kind: 'error', text: t('save.failed') };
  else if (result.value) props.state.settings = result.value as SettingsView;
  done(result.ok);
}
async function deleteSecret(instanceId: string, secretName: string): Promise<void> {
  const result = await props.bridge.command(UI_COMMANDS.SecretDelete, { instanceId, secretName });
  if (result.ok && result.value) props.state.settings = result.value as SettingsView;
}

const serviceHint = (service: SettingsView['services'][number]) => {
  const targets = service.credentialTargets ?? [];
  const notGranted = service.availability === 'MissingCredential' && targets.length > 0 && targets.every((c) => c.saved) && targets.some((c) => !c.granted);
  const state = service.availability === 'Ready' && service.secretNames.length === 0 ? t('services.state.NoCredential') : notGranted ? t('services.state.NotGranted') : t(`services.state.${service.availability}`);
  const parts = [service.implemented ? state : `${state} · ${t('services.notImplemented')}`];
  // Local usage is a count on this PC, never a vendor balance; MyMemory's limits are written apart from it.
  if (service.usageThisMonth !== undefined && service.usageThisMonth !== null) parts.push(t('services.usage', { n: String(service.usageThisMonth) }));
  if (service.instanceId === 'mymemory') parts.push(t('services.mymemoryLimits'));
  return parts.join(' · ');
};
// Result order (CFG03): translation services are listed and moved within their own page; the host keeps the
// other page's positions in the shared order. Services outside the order follow in settings order.
type Service = SettingsView['services'][number];
const orderable = (service: Service) => service.capability === 'translate';
const rank = (service: Service) => (orderable(service) && (service.order ?? -1) >= 0 ? service.order! : Number.MAX_SAFE_INTEGER);
const orderedOnPage = computed(() => servicesOnPage.value.filter(orderable).slice().sort((a, b) => rank(a) - rank(b)));
const pageIndex = (service: Service) => orderedOnPage.value.findIndex((s) => s.serviceId === service.serviceId);
const listedOnPage = computed(() => [...orderedOnPage.value, ...servicesOnPage.value.filter((s) => !orderable(s))]);
// General page (DESIGN "合并服务排序"): translation and AI services in the one order the cards follow.
const mergedOrder = computed(() => (view.value?.services ?? []).filter(orderable).slice().sort((a, b) => rank(a) - rank(b)));
const mergedIndex = (service: Service) => mergedOrder.value.findIndex((s) => s.serviceId === service.serviceId);
const showOrder = ref(false);
async function move(service: Service, delta: number, merged = false): Promise<void> {
  const index = (merged ? mergedIndex(service) : pageIndex(service)) + delta;
  const result = await props.bridge.command(UI_COMMANDS.ReorderService, { serviceId: service.serviceId, index, ...(merged ? { merged } : {}) });
  if (result.ok && result.value) props.state.settings = result.value as SettingsView;
  else if (!result.ok) message.value = { kind: 'error', text: t('save.failed') };
}
/** Name plus capability when one instance offers several services on the page (e.g. Youdao translate + dictionary). */
const serviceTitle = (service: SettingsView['services'][number]) =>
  servicesOnPage.value.filter((s) => s.instanceId === service.instanceId).length > 1 ? `${serviceName(service.instanceId)} · ${t(`capability.${service.capability}`)}` : serviceName(service.instanceId);
const servicesOnPage = computed(() => (view.value?.services ?? []).filter((s) => s.page === page.value));
const hotkeyView = (action: string) => view.value?.hotkeys.find((h) => h.action === action);
const hotkeyConflict = (action: string) => {
  const chord = draft.hotkeys[action];
  if (!chord) return false;
  const shared = new Set(['selectionTranslate', 'clipboardTranslate']);
  return Object.entries(draft.hotkeys).some(([other, value]) => other !== action && value === chord && !(shared.has(other) && shared.has(action)));
};
const swap = () => { const { sourceLanguage, targetLanguage } = draft.general; draft.general.sourceLanguage = targetLanguage; draft.general.targetLanguage = sourceLanguage; };
</script>

<template>
  <div class="window">
    <TitleBar :title="t('settings.title')" maximizable @minimize="bridge.command(UI_COMMANDS.Minimize)" @maximize="bridge.command(UI_COMMANDS.Maximize)" @close="bridge.command(UI_COMMANDS.Close)" />
    <div class="body">
      <nav class="nav" :aria-label="t('settings.title')">
        <button v-for="p in pages" :key="p.id" type="button" class="nav-item" :class="{ current: page === p.id }" :aria-current="page === p.id ? 'page' : undefined" @click="page = p.id">
          <Icon :name="p.icon" :size="15" /><span>{{ t(`nav.${p.id}`) }}</span>
        </button>
      </nav>
      <main v-if="view" class="content">
        <p v-if="view.issues.length" class="banner error-text" role="alert">
          {{ t('file.invalid') }}<span v-for="issue in view.issues" :key="issue.path + issue.code">{{ issue.path }}{{ issue.line ? ` (${issue.line})` : '' }}: {{ issue.message }}; </span>
        </p>
        <p v-if="state.window?.devPreview" class="banner hint-text">{{ t('app.devPreview') }}</p>

        <template v-if="page === 'general'">
          <section class="group">
            <h2>{{ t('general.uiLanguage') }}</h2>
            <SettingRow :title="t('general.uiLanguage')" :hint="t('general.uiLanguageHint')">
              <label class="radio"><input v-model="draft.general.uiLanguage" type="radio" value="zh-Hans" /> 简体中文</label>
              <label class="radio"><input v-model="draft.general.uiLanguage" type="radio" value="en" /> English</label>
            </SettingRow>
          </section>
          <section class="group">
            <h2>{{ t('general.translation') }}</h2>
            <SettingRow :title="t('general.source')" for-id="g-source">
              <select id="g-source" v-model="draft.general.sourceLanguage" class="field" @change="draft.general.sourceLanguage === draft.general.targetLanguage && (draft.general.targetLanguage = draft.general.sourceLanguage === 'en' ? 'zh-Hans' : 'en')">
                <option value="en">{{ t('lang.en') }}</option><option value="zh-Hans">{{ t('lang.zh-Hans') }}</option>
              </select>
              <button type="button" class="icon-btn" :aria-label="t('lang.swap')" @click="swap"><Icon name="swap" /></button>
            </SettingRow>
            <SettingRow :title="t('general.target')" for-id="g-target">
              <select id="g-target" v-model="draft.general.targetLanguage" class="field" @change="draft.general.sourceLanguage === draft.general.targetLanguage && (draft.general.sourceLanguage = draft.general.targetLanguage === 'en' ? 'zh-Hans' : 'en')">
                <option value="en">{{ t('lang.en') }}</option><option value="zh-Hans">{{ t('lang.zh-Hans') }}</option>
              </select>
            </SettingRow>
          </section>
          <section class="group">
            <h2>{{ t('general.results') }}</h2>
            <SettingRow :title="t('general.defaultExpanded')" :hint="t('general.defaultExpandedHint')">
              <Stepper v-model="draft.general.defaultExpandedCards" :min="0" :max="32" :label="t('general.defaultExpanded')" />
            </SettingRow>
            <SettingRow :title="t('general.resultOrder')" :hint="t('general.resultOrderHint')">
              <button type="button" class="icon-btn" :aria-expanded="showOrder" :aria-label="t('general.resultOrderEdit')" @click="showOrder = !showOrder">
                <Icon :name="showOrder ? 'chevronDown' : 'chevronRight'" />
              </button>
            </SettingRow>
            <ol v-if="showOrder" class="expanded result-order" :aria-label="t('general.resultOrder')">
              <li v-for="(service, i) in mergedOrder" :key="service.serviceId" :data-service="service.serviceId">
                <span class="rank">{{ i + 1 }}</span>
                <span class="name">{{ serviceName(service.instanceId) }}</span>
                <span class="tag">{{ t(`services.${service.page}`) }}</span>
                <span class="hint-text state">{{ t(`services.state.${service.availability}`) }}</span>
                <button type="button" class="icon-btn" :disabled="i === 0" :aria-label="t('services.moveUp', { name: serviceName(service.instanceId) })" @click="move(service, -1, true)"><Icon name="chevronUp" /></button>
                <button type="button" class="icon-btn" :disabled="i === mergedOrder.length - 1" :aria-label="t('services.moveDown', { name: serviceName(service.instanceId) })" @click="move(service, 1, true)"><Icon name="chevronDown" /></button>
              </li>
            </ol>
          </section>
          <section class="group">
            <h2>{{ t('general.capture') }}</h2>
            <SettingRow :title="t('general.allowBorrow')" :hint="t('general.allowBorrowHint')">
              <Toggle v-model="draft.general.allowClipboardBorrowing" :label="t('general.allowBorrow')" />
            </SettingRow>
          </section>
          <section class="group">
            <h2>{{ t('general.window') }}</h2>
            <SettingRow :title="t('general.closeAction')">
              <label class="radio"><input v-model="draft.general.closeAction" type="radio" value="hide" /> {{ t('general.closeHide') }}</label>
              <label class="radio"><input v-model="draft.general.closeAction" type="radio" value="exit" /> {{ t('general.closeExit') }}</label>
            </SettingRow>
            <SettingRow :title="t('general.launch')">
              <Toggle v-model="draft.general.launchAtStartup" :label="t('general.launch')" />
            </SettingRow>
          </section>
          <section class="group">
            <h2>{{ t('general.theme') }}</h2>
            <SettingRow :title="t('general.theme')" :hint="t('general.themeDarkPending')">
              <label class="radio"><input type="radio" checked disabled /> {{ t('general.themeLight') }}</label>
              <label class="radio"><input type="radio" disabled /> {{ t('general.themeDark') }}</label>
            </SettingRow>
          </section>
        </template>

        <section v-else-if="page === 'hotkeys'" class="group">
          <h2>{{ t('hotkeys.title') }}</h2>
          <p class="hint-text">{{ t('hotkeys.hint') }}</p>
          <SettingRow v-for="(chord, action) in draft.hotkeys" :key="action" :title="t(`hotkeys.${action}`)"
            :hint="hotkeyConflict(String(action)) ? t('hotkeys.state.conflict') : hotkeyView(String(action))?.state === 'failed' ? t('hotkeys.state.failed') : hotkeyView(String(action))?.state === 'unavailable' ? t('hotkeys.state.unavailable') : undefined">
            <HotkeyField v-model="draft.hotkeys[action]" :label="t(`hotkeys.${action}`)" :invalid="hotkeyConflict(String(action)) || hotkeyView(String(action))?.state === 'failed'" />
          </SettingRow>
        </section>

        <section v-else-if="page === 'network'" class="group">
          <h2>{{ t('network.proxy') }}</h2>
          <SettingRow :title="t('network.proxy')">
            <select v-model="draft.network.proxyMode" class="field" :aria-label="t('network.proxy')">
              <option value="system">{{ t('network.system') }}</option><option value="none">{{ t('network.none') }}</option>
              <option value="http">{{ t('network.http') }}</option><option value="socks5">{{ t('network.socks5') }}</option>
            </select>
          </SettingRow>
          <template v-if="draft.network.proxyMode === 'http' || draft.network.proxyMode === 'socks5'">
            <SettingRow :title="t('network.host')" for-id="n-host"><input id="n-host" v-model.trim="draft.network.proxyHost" class="field" spellcheck="false" /></SettingRow>
            <SettingRow :title="t('network.port')" for-id="n-port"><input id="n-port" v-model.number="draft.network.proxyPort" class="field port" type="number" min="1" max="65535" /></SettingRow>
            <SettingRow :title="t('network.username')" for-id="n-user"><input id="n-user" v-model="draft.network.proxyUsername" class="field" spellcheck="false" autocomplete="off" /></SettingRow>
            <SettingRow :title="t('network.password')">
              <SecretField id="n-password" :label="t('network.password')" :saved="view.network.proxyPasswordSaved" :clear-token="clearToken"
                @save="(value, done) => writeSecret('network.proxy', 'password', value, done)" @remove="deleteSecret('network.proxy', 'password')" />
            </SettingRow>
          </template>
          <SettingRow :title="t('network.timeout')" :hint="t('network.timeoutHint')">
            <Stepper v-model="draft.network.aiTimeoutSeconds" :min="5" :max="600" :step="5" :label="t('network.timeout')" />
          </SettingRow>
        </section>

        <section v-else class="group">
          <h2>{{ t(`services.${page}`) }}</h2>
          <p class="hint-text">{{ t('services.credentialNote') }}</p>
          <div v-for="service in listedOnPage" :key="service.serviceId" class="service">
            <SettingRow :title="serviceTitle(service)" :hint="serviceHint(service)">
              <template v-if="orderable(service)">
                <button type="button" class="icon-btn" :disabled="pageIndex(service) === 0" :aria-label="t('services.moveUp', { name: serviceName(service.instanceId) })" @click="move(service, -1)"><Icon name="chevronUp" /></button>
                <button type="button" class="icon-btn" :disabled="pageIndex(service) === orderedOnPage.length - 1" :aria-label="t('services.moveDown', { name: serviceName(service.instanceId) })" @click="move(service, 1)"><Icon name="chevronDown" /></button>
              </template>
              <Toggle v-model="draft.services[service.serviceId]" :label="t('services.enabled', { name: serviceName(service.instanceId) })" />
              <button v-if="service.secretNames.length || service.credentialTargets" type="button" class="icon-btn" :aria-expanded="expanded === service.serviceId"
                :aria-label="t('services.details', { name: serviceName(service.instanceId) })" @click="expanded = expanded === service.serviceId ? null : service.serviceId">
                <Icon :name="expanded === service.serviceId ? 'chevronDown' : 'chevronRight'" />
              </button>
            </SettingRow>
            <ServiceDetails v-if="expanded === service.serviceId" class="expanded" :service="service" :accounts="view.accounts" :bridge="bridge" :clear-token="clearToken"
              @settings="(next) => (state.settings = next)" @error="message = { kind: 'error', text: t('save.failed') }" />
          </div>
        </section>

        <ul v-if="issues.length" class="issues error-text">
          <li v-for="issue in issues" :key="issue.path + issue.code">{{ issue.path }}: {{ issue.message }}</li>
        </ul>
      </main>
    </div>
    <footer class="savebar">
      <span v-if="message" :class="message.kind === 'error' ? 'error-text' : 'hint-text'" role="status">{{ message.text }}</span>
      <span class="spacer" />
      <button type="button" class="btn" :disabled="!dirty" @click="discard">{{ t('save.discard') }}</button>
      <button type="button" class="btn primary" :disabled="!dirty" @click="save">{{ t('save.save') }}</button>
    </footer>
  </div>
</template>

<style scoped>
.window { height: 100%; display: flex; flex-direction: column; }
.body { flex: 1; display: flex; min-height: 0; }
.nav { width: 190px; flex: none; border-right: 1px solid var(--line); padding: 8px 0; display: flex; flex-direction: column; gap: 1px; overflow-y: auto; }
.nav-item { height: 32px; display: flex; align-items: center; gap: 9px; padding-left: 14px; border: none; border-left: 2px solid transparent; background: transparent; color: var(--ink-secondary); font-size: 12.5px; text-align: left; }
.nav-item.current { border-left-color: var(--accent); color: var(--ink); font-weight: 600; }
.content { flex: 1; overflow-y: auto; padding: 20px 26px; }
.group { margin-bottom: 22px; }
.group h2 { font-size: 12.5px; font-weight: 600; margin: 0 0 4px; }
.banner { margin: 0 0 14px; font-size: 11px; }
.radio { display: inline-flex; align-items: center; gap: 6px; font-size: 12px; }
.radio input { accent-color: var(--accent); margin: 0; }
.expanded { padding-left: 27px; }
.result-order { list-style: none; margin: 0 0 6px; display: flex; flex-direction: column; gap: 2px; font-size: 12px; }
.result-order li { min-height: 32px; display: flex; align-items: center; gap: 8px; }
.result-order .rank { width: 16px; color: var(--hint); font-variant-numeric: tabular-nums; }
.result-order .name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.result-order .state { flex: 1; font-size: 11px; }
.port { width: 90px; }
.issues { font-size: 11px; padding-left: 18px; }
.savebar { height: 48px; flex: none; display: flex; align-items: center; gap: 8px; padding: 0 20px; border-top: 1px solid var(--line); font-size: 11px; }
.spacer { flex: 1; }
</style>
