<script setup lang="ts">
import { ref } from 'vue';
defineProps<{ name: string; text: string; status: string; expandLabel: string; collapseLabel: string }>();
const expanded = ref(true);
</script>
<template>
  <section class="result-card">
    <button class="card-heading" type="button" :aria-expanded="expanded" @click="expanded = !expanded">
      <span aria-hidden="true">{{ expanded ? '−' : '+' }}</span><strong>{{ name }}</strong>
      <span class="card-status">{{ status }}</span><span class="sr-only">{{ expanded ? collapseLabel : expandLabel }}</span>
    </button>
    <p v-if="expanded" class="card-content">{{ text }}</p>
  </section>
</template>
