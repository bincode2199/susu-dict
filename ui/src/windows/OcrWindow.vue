<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { UI_COMMANDS } from '@protocol/ui';
import type { Bridge } from '../bridge/bridge';
import type { UiState } from '../bridge/store';
import TitleBar from '../components/TitleBar.vue';
import ResultCard from '../components/ResultCard.vue';
import Icon from '../components/Icon.vue';
import { t, serviceName } from '../locales/i18n';

// OCR result window 420×620 (DESIGN 9 "结果窗", Ocr artboard 02–04): the main-window shell (40 px title bar with system
// buttons, 32 px status bar). The recognized text goes into the "OCR 原文" source card, where it can be edited before
// translating; everything below it is the same card list as input translation. Recognizing shows two skeleton lines (no
// spinner); a failure keeps the card's shape and offers "重新截图". The preview is a host-made data URL, never a file path.
const props = defineProps<{ bridge: Bridge; state: UiState }>();
const canSpeak = computed(() => props.state.window?.features.includes('pronunciation') ?? false);
function speakCard(serviceId: string, phonetic?: number): void {
  void props.bridge.command(UI_COMMANDS.SpeakCard, phonetic === undefined ? { serviceId } : { serviceId, phonetic });
}
const ocr = computed(() => props.state.ocr);
const phase = computed(() => ocr.value?.phase ?? 'idle');
const text = ref('');
const composing = ref(false);
const from = ref('en');
const to = ref('zh-Hans');
const languages = ['en', 'zh-Hans'];
const count = computed(() => [...text.value].length);
const formulas = computed(() => (ocr.value?.blocks ?? []).filter((b) => b.kind === 'formula'));
const service = computed(() => (ocr.value?.serviceId ? serviceName(ocr.value.serviceId) : ''));
const translatedText = computed(() => props.state.translation?.sourceText ?? '');
const offline = computed(() => props.state.translation?.offline === true);
// A preview is shown only as an inline PNG data URL made by the host (CSP img-src 'self' data:); anything else is ignored.
const preview = computed(() => (ocr.value?.preview?.startsWith('data:image/png;base64,') ? ocr.value.preview : null));
const meta = computed(() => {
  const view = ocr.value;
  if (!view || phase.value !== 'recognized') return '';
  const seconds = view.elapsedMs !== undefined && view.elapsedMs !== null ? (view.elapsedMs / 1000).toFixed(1) : '–';
  return t('ocr.meta', { service: service.value, seconds, n: count.value });
});
const statusHint = computed(() => (ocr.value?.hotkey ? t('ocr.statusHint', { chord: ocr.value.hotkey }) : t('ocr.statusHintNoChord')));

// Each capture (new id) or its recognized result replaces the source text; later edits stay until the next capture.
let adopted = '';
watch(ocr, (view) => {
  if (!view) return;
  const key = `${view.id}:${view.phase}`;
  if (key === adopted) return;
  adopted = key;
  if (view.phase === 'recognized') text.value = view.text ?? '';
  else if (view.phase === 'recognizing') text.value = '';
}, { immediate: true });
watch(() => props.state.translation, (snapshot) => {
  if (!snapshot) return;
  from.value = snapshot.from || from.value;
  to.value = snapshot.to || to.value;
}, { immediate: true });

function submit(): void {
  if (!text.value.trim()) return;
  void props.bridge.command(UI_COMMANDS.SubmitText, { text: text.value });
}
function onKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Enter' || !event.ctrlKey || composing.value || event.isComposing) return;
  event.preventDefault();
  submit();
}
const recapture = () => props.bridge.command(UI_COMMANDS.BeginCapture);
const copy = (value: string) => props.bridge.command(UI_COMMANDS.CopyText, { text: value });
async function setLanguages(source: string, target: string): Promise<void> {
  from.value = source;
  to.value = target;
  const result = await props.bridge.command(UI_COMMANDS.SelectLanguage, { from: source, to: target });
  if (result.ok && translatedText.value && text.value.trim()) submit();
}
function pick(which: 'from' | 'to', value: string): void {
  const source = which === 'from' ? value : from.value;
  const target = which === 'to' ? value : to.value;
  void (source === target ? setLanguages(to.value, from.value) : setLanguages(source, target));
}
</script>

<template>
  <div class="window">
    <TitleBar :title="`Su-Su · ${t('ocr.title')}`" show-settings
      @minimize="bridge.command(UI_COMMANDS.Minimize)" @close="bridge.command(UI_COMMANDS.Close)" @settings="bridge.command(UI_COMMANDS.OpenSettings)" />
    <main class="content">
      <section class="source" :class="`phase-${phase}`" :aria-busy="phase === 'recognizing'">
        <header class="languages">
          <label class="sr-only" for="ocr-source-language">{{ t('general.source') }}</label>
          <select id="ocr-source-language" class="lang" :value="from" @change="pick('from', ($event.target as HTMLSelectElement).value)">
            <option v-for="code in languages" :key="code" :value="code">{{ t(`lang.${code}`) }}</option>
          </select>
          <button type="button" class="icon-btn" :aria-label="t('lang.swap')" :title="t('lang.swap')" @click="setLanguages(to, from)"><Icon name="swap" /></button>
          <label class="sr-only" for="ocr-target-language">{{ t('general.target') }}</label>
          <select id="ocr-target-language" class="lang" :value="to" @change="pick('to', ($event.target as HTMLSelectElement).value)">
            <option v-for="code in languages" :key="code" :value="code">{{ t(`lang.${code}`) }}</option>
          </select>
        </header>
        <div class="label-row">
          <span class="label">{{ t('ocr.source') }}</span>
          <span v-if="phase === 'recognizing'" class="tag" role="status">{{ t('ocr.recognizing') }}<template v-if="service"> · {{ service }}</template></span>
          <span v-else-if="phase === 'recognized'" class="hint-text small">{{ t('ocr.editHint') }}</span>
        </div>
        <figure v-if="preview" class="preview">
          <img :src="preview" :alt="t('ocr.preview')" draggable="false" />
          <figcaption class="hint-text small">{{ t('ocr.size', { w: ocr?.width ?? 0, h: ocr?.height ?? 0 }) }}</figcaption>
        </figure>
        <p v-if="ocr?.notice" class="notice error-text small" role="status"><Icon name="warning" :size="13" />{{ t(ocr.notice) }}</p>

        <div v-if="phase === 'recognizing'" class="skeleton" aria-hidden="true"><span class="line dark" /><span class="line light" /></div>
        <template v-else-if="phase === 'recognized'">
          <label class="sr-only" for="ocr-source-text">{{ t('ocr.source') }}</label>
          <textarea id="ocr-source-text" v-model="text" class="input selectable" spellcheck="false" maxlength="100000"
            @compositionstart="composing = true" @compositionend="composing = false" @keydown="onKeydown" />
          <ul v-if="formulas.length" class="formulas" :aria-label="t('ocr.formula')">
            <li v-for="(block, index) in formulas" :key="index">
              <code class="selectable">{{ block.text }}</code>
              <button type="button" class="icon-btn" :aria-label="t('ocr.copyFormula')" :title="t('ocr.copyFormula')" @click="copy(block.text)"><Icon name="copy" /></button>
            </li>
          </ul>
        </template>
        <div v-else-if="phase === 'noText' || phase === 'failed' || phase === 'unavailable' || phase === 'cancelled'" class="failure" role="alert">
          <p class="failure-title">{{ t(phase === 'noText' ? 'ocr.noText' : phase === 'failed' ? 'ocr.failed' : phase === 'unavailable' ? 'ocr.unavailable' : 'ocr.cancelled') }}</p>
          <p class="hint-text small">
            <template v-if="phase === 'noText'">{{ t('ocr.noTextDetail') }}</template>
            <template v-else-if="phase === 'failed'">{{ t(`error.${ocr?.errorKind ?? 'unavailable'}`) }}<template v-if="service"> · {{ service }}</template></template>
            <template v-else-if="phase === 'unavailable'">{{ t('ocr.unavailableDetail') }}</template>
          </p>
          <div class="failure-actions">
            <button v-if="phase === 'unavailable' || ocr?.errorKind === 'auth' || ocr?.errorKind === 'quota'" type="button" class="link" @click="bridge.command(UI_COMMANDS.OpenSettings)">{{ t('capture.link.settings') }}</button>
            <button type="button" class="btn recapture" @click="recapture"><Icon name="ocr" />{{ t('ocr.recapture') }}</button>
          </div>
        </div>

        <footer v-if="phase === 'recognized'" class="foot">
          <span class="meta">{{ meta }}</span>
          <button type="button" class="icon-btn" :aria-label="t('ocr.copy')" :title="t('ocr.copy')" :disabled="count === 0" @click="copy(text)"><Icon name="copy" /></button>
          <button type="button" class="icon-btn" :aria-label="t('ocr.recapture')" :title="t('ocr.recapture')" @click="recapture"><Icon name="ocr" /></button>
          <button type="button" class="btn primary translate" :disabled="count === 0" @click="submit">{{ t('main.translate') }}<span class="hint">Ctrl+↵</span></button>
        </footer>
      </section>
      <ResultCard v-for="card in state.translation?.cards ?? []" :key="card.serviceId" :card="card" :from="from" :to="to"
        @toggle="bridge.command(UI_COMMANDS.ToggleCard, { serviceId: card.serviceId })"
        @retry="bridge.command(UI_COMMANDS.RetryCard, { serviceId: card.serviceId })"
        @copy="(value: string) => copy(value)"
        @settings="bridge.command(UI_COMMANDS.OpenSettings)"
        :speech="state.speech" :can-speak="canSpeak" :bridge="bridge" @speak="(phonetic?: number) => speakCard(card.serviceId, phonetic)"
        @stop-speech="bridge.command(UI_COMMANDS.SpeechStop, {})" />
    </main>
    <footer class="statusbar" :class="{ offline }">
      <span v-if="offline" class="offline-note" role="status"><Icon name="warning" :size="13" />{{ t('main.offline') }}</span>
      <span v-else>{{ statusHint }}</span>
    </footer>
  </div>
</template>

<style scoped>
.window { height: 100%; display: flex; flex-direction: column; }
.content { flex: 1; overflow-y: auto; padding: 14px; display: flex; flex-direction: column; gap: 10px; }
.source { border: 1px solid var(--line-accent); border-radius: var(--radius-card); display: flex; flex-direction: column; flex: none; }
.source:focus-within { border-color: var(--accent); }
.languages { height: 38px; display: flex; align-items: center; gap: 2px; padding: 0 6px; border-bottom: 1px solid var(--line); }
.lang { height: 26px; border: none; background: transparent; font-size: 12px; border-radius: var(--radius-icon); padding: 0 4px; color: var(--ink); }
.label-row { display: flex; align-items: center; gap: 8px; padding: 10px 14px 0; min-height: 28px; }
.label { font-size: 11px; font-weight: 600; color: var(--ink-secondary); margin-right: auto; }
.small { font-size: 11px; }
.preview { margin: 8px 14px 0; display: flex; flex-direction: column; gap: 2px; }
.preview img { display: block; max-width: 100%; max-height: 96px; object-fit: contain; object-position: left center; border: 1px solid var(--line); border-radius: var(--radius-icon); }
.notice { display: flex; align-items: center; gap: 6px; margin: 6px 14px 0; }
.input { border: none; outline: none; resize: none; min-height: 132px; padding: 8px 14px 10px; font-size: 15.5px; line-height: 1.62; background: transparent; color: var(--ink); }
.skeleton { display: flex; flex-direction: column; gap: 12px; padding: 18px 14px 22px; }
.skeleton .line { display: block; height: 1.5px; }
.skeleton .dark { width: 100%; background: var(--skeleton-dark, var(--line-accent)); }
.skeleton .light { width: 54%; background: var(--skeleton-light, var(--line)); }
.formulas { list-style: none; margin: 0; padding: 0 14px 8px; display: flex; flex-direction: column; gap: 4px; }
.formulas li { display: flex; align-items: center; gap: 6px; }
.formulas code { flex: 1; min-width: 0; overflow-x: auto; white-space: pre; font-size: 12px; padding: 4px 6px; border: 1px solid var(--line); border-radius: var(--radius-icon); }
.failure { padding: 10px 14px 12px; display: flex; flex-direction: column; gap: 4px; min-height: 110px; }
.failure p { margin: 0; }
.failure-title { font-size: 14px; color: var(--ink); }
.failure-actions { margin-top: auto; display: flex; align-items: center; justify-content: flex-end; gap: 10px; }
.recapture { display: inline-flex; align-items: center; gap: 6px; }
.foot { height: 40px; display: flex; align-items: center; gap: 4px; padding: 0 6px 0 14px; border-top: 1px solid var(--line); }
.meta { font-size: 11px; color: var(--hint); margin-right: auto; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.link { border: none; background: transparent; padding: 0; font-size: 11px; color: var(--accent); cursor: pointer; }
.offline-note { display: inline-flex; align-items: center; gap: 6px; color: var(--error); }
.statusbar { height: 32px; flex: none; display: flex; align-items: center; padding: 0 14px; border-top: 1px solid var(--line); font-size: 11px; color: var(--hint); }
</style>
