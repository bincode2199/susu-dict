<script setup lang="ts">
// DESIGN 7: 34×18 capsule, 1 px border; off: #C9CDD3 border and 12 px dot on the left; on: accent, dot right.
defineProps<{ modelValue: boolean; label: string; disabled?: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [boolean] }>();
</script>

<template>
  <button type="button" role="switch" class="toggle" :class="{ on: modelValue }" :aria-checked="modelValue" :aria-label="label" :disabled="disabled"
    @click="emit('update:modelValue', !modelValue)"><span class="dot" /></button>
</template>

<style scoped>
.toggle { width: 34px; height: 18px; border-radius: 9px; border: 1px solid var(--line-accent); background: var(--paper); padding: 0 2px; display: inline-flex; align-items: center; flex: none; }
.dot { width: 12px; height: 12px; border-radius: 50%; background: var(--line-accent); transition: none; }
.toggle.on { border-color: var(--accent); justify-content: flex-end; }
.toggle.on .dot { background: var(--accent); }
.toggle:disabled { border-color: var(--line); }
.toggle:disabled .dot { background: var(--line); }
</style>
