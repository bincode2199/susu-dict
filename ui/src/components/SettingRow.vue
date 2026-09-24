<script setup lang="ts">
// DESIGN 7 setting row: 9-11 px vertical padding, hairline between rows, 12.5 px title, 11 px hint 3 px below.
defineProps<{ title: string; hint?: string; forId?: string }>();
</script>

<template>
  <div class="row">
    <div class="text">
      <label v-if="forId" class="title" :for="forId">{{ title }}</label>
      <span v-else class="title">{{ title }}</span>
      <span v-if="hint" class="hint">{{ hint }}</span>
      <slot name="below" />
    </div>
    <div class="control"><slot /></div>
  </div>
</template>

<style scoped>
.row { display: flex; align-items: center; gap: 16px; padding: 10px 0; border-bottom: 1px solid var(--line); }
.text { flex: 1; display: flex; flex-direction: column; min-width: 0; }
.title { font-size: 12.5px; }
.hint { font-size: 11px; color: var(--hint); margin-top: 3px; }
.control { display: flex; align-items: center; gap: 8px; flex: none; }
</style>
