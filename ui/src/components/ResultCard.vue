<script setup lang="ts">
import { computed } from 'vue';
import type { CardSnapshot } from '@protocol/ui';
import Icon from './Icon.vue';
import { t } from '../locales/i18n';

// DESIGN 8: 1 px line, 8 px radius; 38 px header (26 px toggle · name · right status · icon buttons);
// body indented 38 px. Service text is always rendered as text (mustache), never as HTML (PLAN 4.5.5, S08).
const props = defineProps<{ card: CardSnapshot; from: string; to: string }>();
const emit = defineEmits<{ toggle: []; retry: []; copy: []; settings: [] }>();

const retryable = new Set(['timeout', 'network', 'rate_limited', 'busy', 'unavailable']);
const busy = computed(() => props.card.state === 'Loading' || props.card.state === 'Queued' || props.card.state === 'Streaming');
const collapsed = computed(() => props.card.collapsed || props.card.state === 'Unsupported' || props.card.state === 'CollapsedIdle');
const status = computed(() => {
  if (props.card.state === 'Unsupported') return t('card.unsupported', { from: t(`lang.${props.from}`), to: t(`lang.${props.to}`) });
  if (props.card.collapsed || props.card.state === 'CollapsedIdle') return t('card.expandToTranslate');
  if (busy.value) return t('card.translating');
  if (props.card.state === 'Ready' && props.card.chunked) return t('card.chunked');
  return '';
});
const errorKey = computed(() => (props.card.state === 'Failed' ? `error.${props.card.error ?? 'bad_response'}` : props.card.state === 'Cancelled' ? 'card.cancelled' : null));
</script>

<template>
  <section class="card" :class="{ collapsed }" :aria-busy="busy">
    <header class="head">
      <button type="button" class="toggle icon-btn" :aria-expanded="!collapsed" :aria-label="collapsed ? t('card.expand') : t('card.collapse')" :disabled="card.state === 'Unsupported'" @click="emit('toggle')">
        <Icon :name="collapsed ? 'chevronRight' : 'chevronDown'" />
      </button>
      <h2 class="name">{{ card.displayName }}</h2>
      <span class="status">{{ status }}</span>
      <div v-if="!collapsed && card.state === 'Ready'" class="actions">
        <button type="button" class="icon-btn" :aria-label="t('card.copy')" :title="t('card.copy')" @click="emit('copy')"><Icon name="copy" /></button>
      </div>
    </header>
    <div v-if="!collapsed" class="body">
      <div v-if="card.state === 'Loading' || card.state === 'Queued'" class="skeleton" aria-hidden="true"><span /><span /></div>
      <p v-else-if="errorKey" class="error" role="alert">
        {{ t(errorKey) }}
        <a v-if="card.error && retryable.has(card.error)" href="#" @click.prevent="emit('retry')">{{ t('card.retry') }}</a>
        <a v-else-if="card.error === 'quota' || card.error === 'auth'" href="#" @click.prevent="emit('settings')">{{ t('card.goSettings') }}</a>
      </p>
      <p v-else class="text selectable">{{ card.text }}</p>
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
.body { padding: 0 14px 12px 38px; }
.text { margin: 0; font-size: 14.5px; line-height: 1.75; white-space: pre-wrap; word-break: break-word; }
.error { margin: 0; font-size: 12px; color: var(--error); display: flex; gap: 10px; align-items: baseline; }
.skeleton { display: grid; gap: 10px; padding: 8px 0 6px; }
.skeleton span { display: block; height: 1.5px; background: var(--skeleton-dark); }
.skeleton span:last-child { width: 62%; background: var(--skeleton-light); }
</style>
