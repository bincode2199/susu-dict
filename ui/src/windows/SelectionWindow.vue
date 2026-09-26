<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { UI_COMMANDS } from '@protocol/ui';
import type { Bridge } from '../bridge/bridge';
import type { UiState } from '../bridge/store';
import ResultCard from '../components/ResultCard.vue';
import Icon from '../components/Icon.vue';
import { t } from '../locales/i18n';

// Floating window for selection and clipboard translation (DESIGN 9 "悬浮窗", Selection artboard): 380 wide, height
// from content up to the work area (then the card list scrolls inside), no system buttons. The 34 px header is the
// language row with pin / open in main window / close; the source text sits under it, then the card list and a
// 32 px status bar. Opened only by a hotkey or the tray's clipboard entry, at its remembered position.
const props = defineProps<{ bridge: Bridge; state: UiState }>();
const text = ref('');
const area = ref<HTMLTextAreaElement | null>(null);
const root = ref<HTMLElement | null>(null);
const composing = ref(false);
const from = ref('en');
const to = ref('zh-Hans');
const languages = ['en', 'zh-Hans'];

const capture = computed(() => props.state.capture);
const translated = computed(() => props.state.translation?.sourceText ?? '');
// Text was captured but its translation snapshot has not arrived yet: show a placeholder line, not the empty input.
const waiting = computed(() => !!capture.value && !capture.value.empty && translated.value === '');
const count = computed(() => [...text.value].length);
const changed = computed(() => text.value.trim().length > 0 && text.value !== translated.value);
const pinned = computed(() => props.state.window?.pinned ?? false);
const offline = computed(() => props.state.translation?.offline === true);

watch(() => props.state.translation, (snapshot) => {
  if (!snapshot) return;
  from.value = snapshot.from || from.value;
  to.value = snapshot.to || to.value;
  if (snapshot.sourceText) text.value = snapshot.sourceText;
}, { immediate: true });
// A new capture replaces whatever was typed; an empty one waits for input with the caret in the box.
watch(() => capture.value?.id, () => {
  if (!capture.value) return;
  if (capture.value.empty) {
    text.value = '';
    void nextTick(() => area.value?.focus());
  }
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
async function setLanguages(source: string, target: string): Promise<void> {
  from.value = source;
  to.value = target;
  const result = await props.bridge.command(UI_COMMANDS.SelectLanguage, { from: source, to: target });
  if (result.ok && translated.value) submit();
}
function pick(which: 'from' | 'to', value: string): void {
  const source = which === 'from' ? value : from.value;
  const target = which === 'to' ? value : to.value;
  void (source === target ? setLanguages(to.value, from.value) : setLanguages(source, target));
}

// Auto height (UI01): report the natural content height in CSS px (= DIP under PMv2); the host clamps it to the work
// area minus 32 DIP, and anything taller scrolls inside the card list.
let lastHeight = 0;
let frame = 0;
function measure(): void {
  frame = 0;
  const el = root.value;
  if (!el) return;
  let height = 2; // 1 px frame top and bottom
  for (const child of Array.from(el.children) as HTMLElement[]) height += child.classList.contains('cards') ? child.scrollHeight : child.offsetHeight;
  height = Math.ceil(height);
  if (height <= 2 || Math.abs(height - lastHeight) < 2) return;
  lastHeight = height;
  void props.bridge.command(UI_COMMANDS.FitContent, { heightDip: height });
}
const schedule = () => { if (!frame) frame = requestAnimationFrame(measure); };
let observer: ResizeObserver | null = null;
let mutations: MutationObserver | null = null;
onMounted(() => {
  if (typeof ResizeObserver !== 'undefined' && root.value) {
    observer = new ResizeObserver(schedule);
    for (const child of Array.from(root.value.children)) observer.observe(child);
  }
  if (typeof MutationObserver !== 'undefined' && root.value) {
    mutations = new MutationObserver(schedule);
    mutations.observe(root.value, { childList: true, subtree: true, characterData: true });
  }
  schedule();
});
onBeforeUnmount(() => { observer?.disconnect(); mutations?.disconnect(); if (frame) cancelAnimationFrame(frame); });
</script>

<template>
  <div ref="root" class="window float">
    <header class="head">
      <label class="sr-only" for="float-source-language">{{ t('general.source') }}</label>
      <select id="float-source-language" class="lang" :value="from" @change="pick('from', ($event.target as HTMLSelectElement).value)">
        <option v-for="code in languages" :key="code" :value="code">{{ t(`lang.${code}`) }}</option>
      </select>
      <button type="button" class="head-btn" :aria-label="t('lang.swap')" :title="t('lang.swap')" @click="setLanguages(to, from)"><Icon name="swap" :size="14" /></button>
      <label class="sr-only" for="float-target-language">{{ t('general.target') }}</label>
      <select id="float-target-language" class="lang" :value="to" @change="pick('to', ($event.target as HTMLSelectElement).value)">
        <option v-for="code in languages" :key="code" :value="code">{{ t(`lang.${code}`) }}</option>
      </select>
      <span class="grow" />
      <button type="button" class="head-btn" :class="{ active: pinned }" :aria-pressed="pinned" :aria-label="t('window.pin')" :title="t('window.pin')" @click="bridge.command(UI_COMMANDS.Pin)"><Icon name="pin" :size="14" /></button>
      <button type="button" class="head-btn" :aria-label="t('float.openInMain')" :title="t('float.openInMain')" @click="bridge.command(UI_COMMANDS.OpenInMain, { text })"><Icon name="input" :size="14" /></button>
      <button type="button" class="head-btn" :aria-label="t('window.close')" :title="t('window.close')" @click="bridge.command(UI_COMMANDS.Close)"><Icon name="close" :size="14" /></button>
    </header>
    <section class="source">
      <p v-if="waiting" class="waiting" role="status">{{ t('float.waiting') }}</p>
      <template v-else>
        <label class="sr-only" for="float-source-text">{{ t('main.source') }}</label>
        <textarea id="float-source-text" ref="area" v-model="text" class="input selectable" :placeholder="t('main.placeholder')" spellcheck="false" maxlength="100000"
          @compositionstart="composing = true" @compositionend="composing = false" @keydown="onKeydown" />
      </template>
      <footer class="source-foot">
        <span class="count">{{ t('main.chars', { n: count }) }}<template v-if="capture && !capture.empty"> · {{ t(`float.origin.${capture.origin}`) }}</template></span>
        <button v-if="translated" type="button" class="icon-btn" :aria-label="t('card.copy')" :title="t('card.copy')" @click="bridge.command(UI_COMMANDS.CopyText, { text: translated })"><Icon name="copy" /></button>
        <button v-if="changed || (!translated && !waiting)" type="button" class="btn primary translate" :disabled="!changed" @click="submit">{{ t('main.translate') }}</button>
      </footer>
    </section>
    <main class="cards">
      <ResultCard v-for="card in state.translation?.cards ?? []" :key="card.serviceId" :card="card" :from="from" :to="to"
        @toggle="bridge.command(UI_COMMANDS.ToggleCard, { serviceId: card.serviceId })"
        @retry="bridge.command(UI_COMMANDS.RetryCard, { serviceId: card.serviceId })"
        @copy="(text: string) => bridge.command(UI_COMMANDS.CopyText, { text })"
        @settings="bridge.command(UI_COMMANDS.OpenSettings)" />
    </main>
    <footer class="statusbar" :class="{ offline }">
      <span v-if="offline" class="offline-note" role="status"><Icon name="warning" :size="13" />{{ t('main.offline') }}</span>
      <span v-else>{{ t('float.statusHint') }}</span>
      <button type="button" class="link" :aria-pressed="pinned" @click="bridge.command(UI_COMMANDS.Pin)">{{ pinned ? t('float.unpin') : t('float.pin') }}</button>
    </footer>
  </div>
</template>

<style scoped>
.float { height: 100%; display: flex; flex-direction: column; overflow: hidden; }
.head { height: 34px; flex: none; display: flex; align-items: center; gap: 2px; padding: 0 4px 0 6px; border-bottom: 1px solid var(--line); app-region: drag; -webkit-app-region: drag; }
.head select, .head button { app-region: no-drag; -webkit-app-region: no-drag; }
.grow { flex: 1; }
.lang { height: 26px; border: none; background: transparent; font-size: 12px; border-radius: var(--radius-icon); padding: 0 4px; color: var(--ink); }
.head-btn { width: 26px; height: 26px; display: inline-flex; align-items: center; justify-content: center; border: none; background: transparent; border-radius: var(--radius-icon); color: var(--ink-secondary); }
.head-btn:hover, .head-btn.active { background: var(--mask); color: var(--ink); }
.source { flex: none; display: flex; flex-direction: column; border-bottom: 1px solid var(--line); }
.input { border: none; outline: none; resize: none; field-sizing: content; min-height: 44px; max-height: 40vh; padding: 10px 14px 4px; font-size: 14px; line-height: 1.6; background: transparent; color: var(--ink); }
.input::placeholder { color: var(--hint); }
.waiting { margin: 0; padding: 10px 14px 4px; min-height: 44px; font-size: 13px; color: var(--hint); }
.source-foot { height: 34px; display: flex; align-items: center; gap: 4px; padding: 0 6px 0 14px; }
.count { font-size: 11px; color: var(--hint); margin-right: auto; }
.translate { height: 26px; }
.cards { flex: 1 1 auto; min-height: 0; overflow-y: auto; padding: 10px; display: flex; flex-direction: column; gap: 10px; }
.cards:empty { display: none; }
.statusbar { height: 32px; flex: none; display: flex; align-items: center; justify-content: space-between; padding: 0 14px; border-top: 1px solid var(--line); font-size: 11px; color: var(--hint); }
.offline-note { display: inline-flex; align-items: center; gap: 6px; color: var(--error); }
.link { border: none; background: transparent; padding: 0; font-size: 11px; color: var(--accent); cursor: pointer; }
.link:hover { color: var(--accent-hover); }
</style>
