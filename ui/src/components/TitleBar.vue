<script setup lang="ts">
import Icon from './Icon.vue';
import { t } from '../locales/i18n';

// DESIGN 9: 40 px bar; left 16 px app mark + 12.5/600 title; right 36×32 app buttons then 46×32 system
// buttons. The bar is a CSS drag region handled by WebView2's non-client region support; buttons opt out.
defineProps<{ title: string; pinned?: boolean; showPin?: boolean; showSettings?: boolean; maximizable?: boolean }>();
const emit = defineEmits<{ minimize: []; maximize: []; close: []; pin: []; settings: [] }>();
</script>

<template>
  <header class="titlebar">
    <svg class="mark" width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
      <path d="M4 4h5a2 2 0 0 1 0 4H7a2 2 0 0 0 0 4h5" />
    </svg>
    <h1 class="title">{{ title }}</h1>
    <div class="buttons">
      <button v-if="showPin" type="button" class="app-button" :class="{ active: pinned }" :aria-pressed="pinned" :aria-label="t('window.pin')" :title="t('window.pin')" @click="emit('pin')">
        <Icon name="pin" />
      </button>
      <button v-if="showSettings" type="button" class="app-button" :aria-label="t('window.settings')" :title="t('window.settings')" @click="emit('settings')">
        <Icon name="settings" />
      </button>
      <button type="button" class="system-button" :aria-label="t('window.minimize')" :title="t('window.minimize')" @click="emit('minimize')"><Icon name="minimize" /></button>
      <button v-if="maximizable" type="button" class="system-button" :aria-label="t('window.maximize')" :title="t('window.maximize')" @click="emit('maximize')"><Icon name="maximize" :size="14" /></button>
      <button type="button" class="system-button close" :aria-label="t('window.close')" :title="t('window.close')" @click="emit('close')"><Icon name="close" /></button>
    </div>
  </header>
</template>

<style scoped>
.titlebar {
  height: 40px;
  display: flex;
  align-items: center;
  padding-left: 14px;
  border-bottom: 1px solid var(--line);
  app-region: drag;
  -webkit-app-region: drag;
  flex: none;
}
.mark { color: var(--ink); flex: none; }
.title {
  margin: 0 0 0 8px;
  font-size: 12.5px;
  font-weight: 600;
  letter-spacing: 0.02em;
  flex: 1;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.buttons { display: flex; align-self: flex-start; app-region: no-drag; -webkit-app-region: no-drag; }
.app-button, .system-button {
  height: 32px;
  border: none;
  background: transparent;
  color: var(--ink-secondary);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  padding: 0;
}
.app-button { width: 36px; }
.system-button { width: 46px; }
.app-button:hover, .system-button:hover { color: var(--ink); background: var(--skeleton-light); }
.system-button.close:hover { background: var(--system-close); color: var(--paper); }
.app-button.active { color: var(--accent); }
</style>
