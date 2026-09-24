<script setup lang="ts">
import { nextTick, onMounted, ref, watch } from 'vue';
import { UI_COMMANDS } from '@protocol/ui';
import type { Bridge } from '../bridge/bridge';
import type { UiState } from '../bridge/store';
import Icon, { type IconName } from '../components/Icon.vue';
import { t } from '../locales/i18n';

// DESIGN 9 tray menu: 236 px, 5 px padding, 32 px items with 5 px radius, hairline separators inset 6 px.
// Disabled items (feature not provided or not configured) are greyed, not clickable, and say why.
const props = defineProps<{ bridge: Bridge; state: UiState }>();
const icons: Record<string, IconName> = {
  'input-translation': 'input', clipboard: 'clipboard', ocr: 'ocr', voice: 'mic', 'system-audio': 'audio',
  transcription: 'video', settings: 'settings', 'check-update': 'update', exit: 'exit',
};
const list = ref<HTMLElement | null>(null);

function open(id: string): void {
  if (id === 'exit') void props.bridge.command(UI_COMMANDS.TrayExit);
  else void props.bridge.command(UI_COMMANDS.TrayOpen, { id });
}
function move(event: KeyboardEvent): void {
  const items = [...(list.value?.querySelectorAll<HTMLButtonElement>('button:not(:disabled)') ?? [])];
  const index = items.indexOf(document.activeElement as HTMLButtonElement);
  if (event.key === 'ArrowDown') items[(index + 1) % items.length]?.focus();
  else if (event.key === 'ArrowUp') items[(index - 1 + items.length) % items.length]?.focus();
  else return;
  event.preventDefault();
}
const focusFirst = () => nextTick(() => list.value?.querySelector<HTMLButtonElement>('button:not(:disabled)')?.focus());
onMounted(focusFirst);
watch(() => props.state.tray, focusFirst);
</script>

<template>
  <nav class="menu" :aria-label="t('tray.menu')" @keydown="move">
    <ul ref="list" role="menu">
      <template v-for="item in state.tray?.items ?? []" :key="item.id">
        <li v-if="item.separatorBefore" class="separator" role="separator" />
        <li role="none">
          <button type="button" role="menuitem" class="item" :disabled="!item.enabled" :aria-disabled="!item.enabled"
            :title="item.reasonKey ? t(item.reasonKey) : undefined" @click="open(item.id)">
            <Icon :name="icons[item.id] ?? 'settings'" />
            <span class="label">{{ t(`tray.${item.id}`) }}</span>
            <span class="shortcut">{{ item.id === 'settings' ? t('tray.settingsHint') : item.enabled ? item.chord : item.reasonKey ? t(item.reasonKey) : '' }}</span>
          </button>
        </li>
      </template>
    </ul>
  </nav>
</template>

<style scoped>
.menu { height: 100%; padding: 5px; border: 1px solid var(--frame); border-radius: var(--radius-card); background: var(--paper); }
ul { list-style: none; margin: 0; padding: 0; }
.item { width: 100%; height: 32px; display: flex; align-items: center; gap: 10px; padding: 0 10px; border: none; border-radius: var(--radius-icon); background: transparent; color: var(--ink); text-align: left; }
.item:hover:not(:disabled), .item:focus-visible { outline: none; background: var(--skeleton-light); }
.item:disabled, .item:disabled .shortcut { color: var(--disabled); }
.label { flex: 1; font-size: 12px; }
.shortcut { font-size: 10.5px; color: var(--hint); }
.separator { height: 1px; background: var(--line); margin: 4px 6px; }
</style>
