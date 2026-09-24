<script setup lang="ts">
import Icon from './Icon.vue';
import { t } from '../locales/i18n';

// DESIGN 7: 28 px high, 6 px radius, 28×26 minus/plus keys, 34 px value.
const props = defineProps<{ modelValue: number; min: number; max: number; label: string; step?: number }>();
const emit = defineEmits<{ 'update:modelValue': [number] }>();
const set = (value: number) => emit('update:modelValue', Math.min(props.max, Math.max(props.min, value)));
</script>

<template>
  <div class="stepper" role="group" :aria-label="label">
    <button type="button" :aria-label="t('stepper.decrease')" :disabled="modelValue <= min" @click="set(modelValue - (step ?? 1))"><Icon name="minus" :size="14" /></button>
    <output :aria-label="label">{{ modelValue }}</output>
    <button type="button" :aria-label="t('stepper.increase')" :disabled="modelValue >= max" @click="set(modelValue + (step ?? 1))"><Icon name="plus" :size="14" /></button>
  </div>
</template>

<style scoped>
.stepper { height: 28px; display: inline-flex; align-items: center; border: 1px solid var(--button-line); border-radius: var(--radius-button); }
button { width: 28px; height: 26px; border: none; background: transparent; color: var(--ink-secondary); display: inline-flex; align-items: center; justify-content: center; }
button:disabled { color: var(--disabled); }
output { width: 34px; text-align: center; font-size: 12px; }
</style>
