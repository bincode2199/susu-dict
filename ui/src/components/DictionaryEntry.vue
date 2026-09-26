<script setup lang="ts">
import type { DictionaryEntryView } from '@protocol/ui';
import Icon from './Icon.vue';
import { t } from '../locales/i18n';
import { accentLabel } from './dictionary';

// DESIGN 8 "译文 · 词条": phonetic row (UK/US + a read-aloud key each), then part-of-speech abbreviation
// (30 px fixed, italic) + meanings; word forms and examples follow. Every value is service data and is rendered
// as text (mustache) only, never as HTML, never as a link or image source (PLAN 4.5.5, S08, DICT03).
// The phonetic read-aloud keys need F10 and stay unavailable until then; the audio id is never used by the UI
// to load anything (PLAN 4.5.3: audioUrl is fetched by the host only).
defineProps<{ entry: DictionaryEntryView }>();
</script>

<template>
  <div class="entry selectable">
    <p class="word">{{ entry.word }}</p>
    <div v-if="entry.phonetics.length" class="phonetics">
      <span v-for="(p, i) in entry.phonetics" :key="i" class="phonetic">
        <span class="accent">{{ accentLabel(p.accent) }}</span>
        <span class="ipa">/{{ p.ipa }}/</span>
        <button type="button" class="icon-btn speak" disabled
          :aria-label="t('dict.pronounceAccent', { accent: accentLabel(p.accent) })" :title="t('card.needsSpeech')">
          <Icon name="audio" :size="14" />
        </button>
      </span>
    </div>
    <ul v-if="entry.parts.length" class="parts">
      <li v-for="(part, i) in entry.parts" :key="i" class="part">
        <span class="pos">{{ part.pos }}</span>
        <span class="means">{{ part.means.join('；') }}</span>
      </li>
    </ul>
    <p v-if="entry.forms.length" class="forms">
      <span class="label">{{ t('dict.forms') }}</span>
      <span v-for="(f, i) in entry.forms" :key="i" class="form"><span class="form-name">{{ f.name }}</span> {{ f.value }}</span>
    </p>
    <ul v-if="entry.examples.length" class="examples" :aria-label="t('dict.examples')">
      <li v-for="(e, i) in entry.examples" :key="i" class="example">
        <span class="src">{{ e.src }}</span>
        <span class="dst">{{ e.dst }}</span>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.entry { display: grid; gap: 8px; word-break: break-word; }
.word { margin: 0; font-size: 16px; font-weight: 600; color: var(--ink); }
.phonetics { display: flex; flex-wrap: wrap; gap: 4px 14px; font-size: 12.5px; color: var(--ink-secondary); }
.phonetic { display: inline-flex; align-items: center; gap: 4px; }
.accent { color: var(--hint); }
.speak { width: 22px; height: 22px; }
.speak:disabled { opacity: 0.45; cursor: default; }
.parts, .examples { margin: 0; padding: 0; list-style: none; display: grid; gap: 4px; }
.part { display: flex; gap: 6px; font-size: 14px; line-height: 1.6; }
.pos { flex: 0 0 30px; font-style: italic; color: var(--ink-secondary); }
.means { flex: 1; white-space: pre-wrap; }
.forms { margin: 0; font-size: 12px; color: var(--ink-secondary); display: flex; flex-wrap: wrap; gap: 4px 12px; }
.forms .label, .form-name { color: var(--hint); }
.example { display: grid; font-size: 12.5px; line-height: 1.6; }
.example .dst { color: var(--ink-secondary); }
</style>
