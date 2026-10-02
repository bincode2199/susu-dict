<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { UI_COMMANDS, type CardSnapshot, type CollectView, type CommandResult, type SpeechStateView } from '@protocol/ui';
import Icon from './Icon.vue';
import { t } from '../locales/i18n';
import DictionaryEntry from './DictionaryEntry.vue';
import { entryPlainText } from './dictionary';

// DESIGN 8: 1 px line, 8 px radius; 38 px header (26 px toggle · name · right status · icon buttons);
// body indented 38 px. Service text is always rendered as text (mustache), never as HTML (PLAN 4.5.5, S08).
// F10.2: speech is the one player's state; canSpeak says a pronunciation service can run (the pronunciation feature).
// F15.4: `bridge` carries the favorite star (Vocab.Collect); without it the star stays unavailable.
interface Commands { command(name: string, payload?: object): Promise<CommandResult> }
const props = defineProps<{ card: CardSnapshot; from: string; to: string; speech?: SpeechStateView | null; canSpeak?: boolean; bridge?: Commands }>();
// `copy` carries the text to copy: the card text, or the entry's plain-text projection on a dictionary card (DICT03).
// `speak` reads the card (no index) or one dictionary phonetic (its index); `stopSpeech` stops what this card is playing.
const emit = defineEmits<{ toggle: []; retry: []; copy: [text: string]; settings: []; speak: [phonetic?: number]; stopSpeech: [] }>();

// DESIGN 8 "译文 · 失败": one action per error kind. Retryable kinds (the host already retried once
// automatically) offer a manual retry; quota/auth lead to settings; bad_response has no action yet (the log
// viewer comes later). A cancelled attempt can be retried too.
const retryable = new Set(['timeout', 'network', 'rate_limited', 'busy', 'unavailable', 'cancelled']);
const busy = computed(() => props.card.state === 'Loading' || props.card.state === 'Queued' || props.card.state === 'Streaming');
const collapsed = computed(() => props.card.collapsed || props.card.state === 'Unsupported' || props.card.state === 'CollapsedIdle');
const copied = ref(false);
let copiedTimer: ReturnType<typeof setTimeout> | undefined;
const status = computed(() => {
  if (props.card.state === 'Unsupported') return t('card.unsupported', { from: t(`lang.${props.from}`), to: t(`lang.${props.to}`) });
  if (props.card.collapsed || props.card.state === 'CollapsedIdle') return t('card.expandToTranslate');
  if (busy.value) return t('card.translating');
  if (copied.value) return t('card.copied');
  if (props.card.state === 'Ready' && props.card.chunked) return t('card.chunked');
  return '';
});
const errorKey = computed(() => (props.card.state === 'Failed' ? `error.${props.card.error ?? 'bad_response'}` : props.card.state === 'Cancelled' ? 'card.cancelled' : null));
const errorKind = computed(() => (props.card.state === 'Cancelled' ? 'cancelled' : props.card.error));
// Streaming text keeps appending in place; the copy button appears once the result is complete.
watch(() => [props.card.text, props.card.entry], () => { copied.value = false; });

// F09.3: a dictionary card shows the structured entry; favorite (F15) is shown but unavailable until that module exists.
const entry = computed(() => (props.card.state === 'Ready' ? props.card.entry : undefined));
// F10.2 (DESIGN 8 key order [regenerate] · read aloud · copy · favorite): every finished card can be read aloud with the
// default service; the key shows the playback it started and stops it when pressed again.
const target = computed(() => `card:${props.card.serviceId}`);
const reading = computed(() => props.speech?.target === target.value && (props.speech.phase === 'loading' || props.speech.phase === 'playing'));
function readAloud(): void {
  if (reading.value) emit('stopSpeech');
  else emit('speak');
}

// F15.4 favorite star: the host keeps the entry locally (works with no sync target) and queues it to the enabled targets. The star asks the host for the
// state when the shown entry changes; a host without the vocabulary service answers unavailable and the star stays greyed. Unfavoriting never deletes a remote entry.
const favorite = ref<{ phase: 'unknown' | 'unavailable' | 'ready' | 'busy'; on: boolean; targets: number; failed: boolean }>({ phase: 'unknown', on: false, targets: 0, failed: false });
/** The star state from a Vocab.Collect answer, or null when the answer is not a usable one (refused or malformed). */
function collected(result: CommandResult): { phase: 'ready'; on: boolean; targets: number; failed: false } | null {
  const v = result.ok ? (result.value as CollectView | undefined) : undefined;
  return v && typeof v.favorited === 'boolean' ? { phase: 'ready', on: v.favorited, targets: v.targets ?? 0, failed: false } : null;
}
async function askFavorite(): Promise<void> {
  const bridge = props.bridge;
  if (!bridge || !entry.value) { favorite.value = { phase: 'unavailable', on: false, targets: 0, failed: false }; return; }
  const serviceId = props.card.serviceId;
  const result = await bridge.command(UI_COMMANDS.Collect, { serviceId });
  if (serviceId !== props.card.serviceId) return;
  favorite.value = collected(result) ?? { phase: 'unavailable', on: false, targets: 0, failed: false };
}
watch(() => [props.card.serviceId, entry.value?.word, props.from, props.bridge], () => { void askFavorite(); }, { immediate: true });
async function toggleFavorite(): Promise<void> {
  const bridge = props.bridge;
  if (!bridge || favorite.value.phase === 'busy' || favorite.value.phase === 'unavailable') return;
  const was = favorite.value;
  favorite.value = { ...was, phase: 'busy' };
  const result = await bridge.command(UI_COMMANDS.Collect, { serviceId: props.card.serviceId, favorite: !was.on });
  favorite.value = collected(result) ?? { ...was, phase: 'ready', failed: true };
}
const favoriteTitle = computed(() => {
  const f = favorite.value;
  if (f.phase === 'unavailable' || f.phase === 'unknown') return t('card.needsVocab');
  if (f.failed) return t('card.favoriteFailed');
  if (!f.on) return t('card.favorite');
  return f.targets > 0 ? t('card.favorited') : t('card.favoritedLocal');
});

function copy(): void {
  emit('copy', entry.value ? entryPlainText(entry.value) : props.card.text);
  copied.value = true;
  clearTimeout(copiedTimer);
  copiedTimer = setTimeout(() => (copied.value = false), 1500);
}
onBeforeUnmount(() => clearTimeout(copiedTimer));
</script>

<template>
  <section class="card" :class="{ collapsed }" :aria-busy="busy">
    <header class="head">
      <button type="button" class="toggle icon-btn" :aria-expanded="!collapsed" :aria-label="collapsed ? t('card.expand') : t('card.collapse')" :disabled="card.state === 'Unsupported'" @click="emit('toggle')">
        <Icon :name="collapsed ? 'chevronRight' : 'chevronDown'" />
      </button>
      <h2 class="name">{{ card.displayName }}</h2>
      <span class="status" aria-live="polite">{{ status }}</span>
      <div v-if="!collapsed && card.state === 'Ready'" class="actions">
        <button type="button" class="icon-btn" :class="{ unavailable: !canSpeak, reading }" data-action="pronounce" :disabled="!canSpeak" :aria-pressed="reading"
          :aria-label="reading ? t('speech.stop') : t('card.pronounce')" :title="canSpeak ? (reading ? t('speech.stop') : t('card.pronounce')) : t('card.needsSpeech')" @click="readAloud">
          <Icon :name="reading ? 'stop' : 'audio'" />
        </button>
        <button type="button" class="icon-btn" data-action="copy" :aria-label="t('card.copy')" :title="t('card.copy')" @click="copy"><Icon name="copy" /></button>
        <button v-if="entry" type="button" class="icon-btn" :class="{ unavailable: favorite.phase !== 'ready', favorited: favorite.on, failed: favorite.failed }" data-action="favorite"
          :disabled="favorite.phase === 'unavailable' || favorite.phase === 'unknown'" :aria-pressed="favorite.on" :aria-busy="favorite.phase === 'busy'" :aria-label="t('card.favorite')" :title="favoriteTitle" @click="toggleFavorite">
          <Icon name="star" />
        </button>
      </div>
    </header>
    <div v-if="!collapsed" class="body">
      <div v-if="card.state === 'Loading' || card.state === 'Queued'" class="skeleton" aria-hidden="true"><span /><span /></div>
      <p v-else-if="errorKey" class="error" role="alert">
        {{ t(errorKey) }}
        <a v-if="errorKind && retryable.has(errorKind)" href="#" class="retry" @click.prevent="emit('retry')">{{ t('card.retry') }}</a>
        <a v-else-if="errorKind === 'quota' || errorKind === 'auth'" href="#" @click.prevent="emit('settings')">{{ t('card.goSettings') }}</a>
      </p>
      <DictionaryEntry v-else-if="entry" :entry="entry" :target="target" :speech="speech" :can-speak="canSpeak"
        @speak="(index: number) => emit('speak', index)" @stop-speech="emit('stopSpeech')" />
      <p v-else class="text selectable" :class="{ streaming: card.state === 'Streaming' }">{{ card.text }}</p>
    </div>
  </section>
</template>

<style scoped>
.card { border: 1px solid var(--line); border-radius: var(--radius-card); background: var(--paper); }
.head { height: 38px; display: flex; align-items: center; padding: 0 6px 0 6px; gap: 4px; }
.toggle { width: 26px; height: 26px; }
.name { margin: 0; font-size: 12.5px; font-weight: 600; color: var(--ink); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.collapsed .name { color: var(--ink-secondary); }
.status { margin-left: auto; font-size: 11px; color: var(--hint); white-space: nowrap; padding-right: 8px; }
.actions { display: flex; gap: 2px; }
.unavailable:disabled { opacity: 0.45; cursor: default; }
.reading { color: var(--ink); }
.favorited { color: var(--accent, var(--ink)); }
.failed { color: var(--error); }
.body { padding: 0 14px 12px 38px; }
.text { margin: 0; font-size: 14.5px; line-height: 1.75; white-space: pre-wrap; word-break: break-word; }
.error { margin: 0; font-size: 12px; color: var(--error); display: flex; gap: 10px; align-items: baseline; }
.skeleton { display: grid; gap: 10px; padding: 8px 0 6px; }
.skeleton span { display: block; height: 1.5px; background: var(--skeleton-dark); }
.skeleton span:last-child { width: 62%; background: var(--skeleton-light); }
</style>
