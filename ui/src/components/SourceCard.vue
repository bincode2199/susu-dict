<script setup lang="ts">
import { computed, ref } from 'vue';
import Icon from './Icon.vue';
import { t } from '../locales/i18n';

// DESIGN 8 source card: #C9CDD3 border (accent while focused), 15.5 px input, 40 px footer with count, clear and
// the primary Translate button. Enter inserts a new line; Ctrl+Enter submits; nothing submits while an IME
// composition is active (ARCHITECTURE 9, UI03).
const props = defineProps<{ modelValue: string; from: string; to: string; disabled?: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [string]; submit: []; swap: []; language: [from: string, to: string] }>();
const composing = ref(false);
const area = ref<HTMLTextAreaElement | null>(null);
defineExpose({ focus: () => area.value?.focus() });
const count = computed(() => [...props.modelValue].length);
const languages = ['en', 'zh-Hans'];

function onKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Enter' || composing.value || event.isComposing) return;
  if (event.ctrlKey) {
    event.preventDefault();
    if (props.modelValue.trim().length > 0 && !props.disabled) emit('submit');
  }
}
function pick(which: 'from' | 'to', value: string): void {
  const from = which === 'from' ? value : props.from;
  const to = which === 'to' ? value : props.to;
  if (from === to) emit('swap');
  else emit('language', from, to);
}
</script>

<template>
  <section class="source">
    <header class="languages">
      <label class="sr-only" for="source-language">{{ t('general.source') }}</label>
      <select id="source-language" class="lang" :value="from" @change="pick('from', ($event.target as HTMLSelectElement).value)">
        <option v-for="code in languages" :key="code" :value="code">{{ t(`lang.${code}`) }}</option>
      </select>
      <button type="button" class="icon-btn" :aria-label="t('lang.swap')" :title="t('lang.swap')" @click="emit('swap')"><Icon name="swap" /></button>
      <label class="sr-only" for="target-language">{{ t('general.target') }}</label>
      <select id="target-language" class="lang" :value="to" @change="pick('to', ($event.target as HTMLSelectElement).value)">
        <option v-for="code in languages" :key="code" :value="code">{{ t(`lang.${code}`) }}</option>
      </select>
    </header>
    <label class="sr-only" for="source-text">{{ t('main.source') }}</label>
    <textarea
      id="source-text"
      ref="area"
      class="input selectable"
      :value="modelValue"
      :placeholder="t('main.placeholder')"
      spellcheck="false"
      maxlength="100000"
      @input="emit('update:modelValue', ($event.target as HTMLTextAreaElement).value)"
      @compositionstart="composing = true"
      @compositionend="composing = false"
      @keydown="onKeydown"
    />
    <footer class="foot">
      <span class="count">{{ t('main.chars', { n: count }) }}</span>
      <button type="button" class="icon-btn" :aria-label="t('main.clear')" :title="t('main.clear')" :disabled="count === 0" @click="emit('update:modelValue', '')"><Icon name="clear" /></button>
      <button type="button" class="btn primary translate" :disabled="count === 0 || disabled" @click="emit('submit')">
        {{ t('main.translate') }}<span class="hint">Ctrl+Enter</span>
      </button>
    </footer>
  </section>
</template>

<style scoped>
.source { border: 1px solid var(--line-accent); border-radius: var(--radius-card); display: flex; flex-direction: column; }
.source:focus-within { border-color: var(--accent); }
.languages { height: 38px; display: flex; align-items: center; gap: 2px; padding: 0 6px; border-bottom: 1px solid var(--line); }
.lang { height: 26px; border: none; background: transparent; font-size: 12px; border-radius: var(--radius-icon); padding: 0 4px; }
.input { border: none; outline: none; resize: none; min-height: 132px; padding: 10px 14px; font-size: 15.5px; line-height: 1.62; background: transparent; }
.input::placeholder { color: var(--hint); }
.foot { height: 40px; display: flex; align-items: center; gap: 4px; padding: 0 6px 0 14px; border-top: 1px solid var(--line); }
.count { font-size: 11px; color: var(--hint); margin-right: auto; }
</style>
