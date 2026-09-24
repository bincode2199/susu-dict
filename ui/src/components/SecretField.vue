<script setup lang="ts">
import { ref, watch } from 'vue';
import Icon from './Icon.vue';
import { t } from '../locales/i18n';

// PLAN 5.4 / DESIGN 7 "已保存凭据": a saved secret is shown only as "Saved" with Replace/Delete — no input, no
// mask of the old value. A new value lives only in this component until saved, cancelled, the page changes
// or the window hides (clearToken changes); it is never put in browser storage or echoed back (S07).
const props = defineProps<{ id: string; label: string; saved: boolean; clearToken: number }>();
const emit = defineEmits<{ save: [value: string, done: (ok: boolean) => void]; remove: [] }>();
const editing = ref(!props.saved);
const value = ref('');
const visible = ref(false);
const busy = ref(false);

function reset(): void {
  value.value = '';
  visible.value = false;
  editing.value = !props.saved;
}
watch(() => props.clearToken, reset);
watch(() => props.saved, reset);

function save(): void {
  if (!value.value || busy.value) return;
  busy.value = true;
  emit('save', value.value, (ok) => {
    busy.value = false;
    if (ok) reset();
  });
}
</script>

<template>
  <div class="secret">
    <template v-if="saved && !editing">
      <span class="tag">{{ t('secret.saved') }}</span>
      <button type="button" class="btn" @click="editing = true">{{ t('secret.replace') }}</button>
      <button type="button" class="btn" @click="emit('remove')">{{ t('secret.delete') }}</button>
    </template>
    <template v-else>
      <input :id="id" v-model="value" class="field" :type="visible ? 'text' : 'password'" autocomplete="off" spellcheck="false" :aria-label="label" @keydown.enter.prevent="save" />
      <button type="button" class="icon-btn" :aria-label="visible ? t('secret.hide') : t('secret.show')" :aria-pressed="visible" :disabled="!value" @click="visible = !visible">
        <Icon :name="visible ? 'eyeOff' : 'eye'" />
      </button>
      <button type="button" class="btn primary" :disabled="!value || busy" @click="save">{{ t('secret.save') }}</button>
      <button v-if="saved" type="button" class="btn" @click="reset">{{ t('secret.cancel') }}</button>
    </template>
  </div>
</template>

<style scoped>
.secret { display: flex; align-items: center; gap: 6px; }
.field { width: 220px; }
</style>
