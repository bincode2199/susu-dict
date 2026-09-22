<script setup lang="ts">
import { computed, ref, watchEffect } from 'vue';
import ResultCard from './components/ResultCard.vue';
import { zh, en, type MessageKey } from './locales';

const query = new URLSearchParams(location.search);
const kinds = ['main', 'settings', 'selection', 'ocr', 'voice', 'transcribe', 'tray', 'error'] as const;
type Kind = typeof kinds[number];
const kind: Kind = kinds.includes(query.get('kind') as Kind) ? query.get('kind') as Kind : 'main';
const locale = ref(query.get('lang') === 'en' ? 'en' : 'zh');
const messages = computed(() => locale.value === 'en' ? en : zh);
const t = (key: MessageKey) => messages.value[key];
watchEffect(() => { document.documentElement.lang = locale.value === 'en' ? 'en' : 'zh-Hans'; });
const sections: MessageKey[] = ['general', 'hotkeys', 'engines', 'ai', 'prompt', 'speech', 'vocab', 'network', 'about'];
const section = ref<MessageKey>('general');
const input = ref(t('latin'));
const states: MessageKey[] = ['ready', 'loading', 'failed', 'unsupported'];
const names = ['MyMemory', 'DeepL', 'OpenAI', 'Tencent', 'Bing', 'Google'];
</script>
<template>
  <div class="shell" :class="kind">
    <header class="titlebar"><strong class="wordmark">Su-Su</strong><span>{{ t(kind) }}</span><select v-model="locale" :aria-label="t('language')"><option value="zh">中文</option><option value="en">English</option></select></header>
    <div class="fixture" role="note">{{ t('fixture') }}</div>
    <div v-if="kind === 'settings'" class="settings-layout">
      <nav :aria-label="t('settings')"><button v-for="item in sections" :key="item" :aria-current="section === item ? 'page' : undefined" @click="section = item">{{ t(item) }}</button></nav>
      <main class="settings-body"><h1>{{ t(section) }}</h1>
        <template v-if="section === 'general'">
          <div class="setting-row"><label for="language">{{ t('language') }}</label><select id="language" v-model="locale"><option value="zh">简体中文</option><option value="en">English</option></select></div>
          <div class="setting-row"><span>{{ t('appearance') }}</span><span>{{ t('light') }}</span></div>
          <label class="setting-row">{{ t('startup') }}<input type="checkbox" disabled /></label>
          <label class="setting-row">{{ t('clipboard') }}<input type="checkbox" disabled /></label>
          <h2>{{ t('result') }}</h2><p class="muted">{{ t('explanation') }}</p>
        </template>
        <template v-else-if="section === 'network'">
          <div class="setting-row"><label for="proxy">{{ t('proxy') }}</label><select id="proxy"><option>{{ t('system') }}</option><option>{{ t('disabled') }}</option><option>{{ t('custom') }}</option></select></div>
          <div class="setting-row"><label for="timeout">{{ t('timeout') }}</label><input id="timeout" type="number" min="1" max="600" value="30" /></div>
        </template>
        <template v-else-if="section === 'engines' || section === 'ai' || section === 'speech'">
          <div v-for="name in names" :key="name" class="setting-row"><strong>{{ name }}</strong><span class="muted">{{ t('configured') }}</span><button disabled>{{ t('account') }}</button></div>
        </template>
        <p v-else class="empty">{{ t('explanation') }}</p>
        <footer class="settings-footer"><button disabled>{{ t('save') }}</button></footer>
      </main>
    </div>
    <main v-else-if="kind === 'tray'" class="tray-menu"><button v-for="item in ['main','ocr','voice','transcribe','settings','exit'] as const" :key="item" disabled>{{ t(item) }}</button></main>
    <main v-else class="content">
      <template v-if="kind === 'voice'"><div class="recording"><span class="timer">{{ t('duration') }}</span><div class="meter" aria-hidden="true">│ │ │ │ │ │ │ │ │ │ │ │</div><button disabled>{{ t('record') }}</button></div></template>
      <template v-else-if="kind === 'ocr'"><div class="empty"><button disabled>{{ t('capture') }}</button><p>{{ t('explanation') }}</p></div></template>
      <template v-else-if="kind === 'transcribe'"><div class="empty"><p>{{ t('file') }}</p><button disabled>{{ t('unavailable') }}</button></div><table><thead><tr><th>#</th><th>{{ t('original') }}</th><th>{{ t('output') }}</th></tr></thead><tbody><tr v-for="i in 30" :key="i"><td>{{ i }}</td><td>{{ t('latin') }}</td><td>{{ t('synthetic') }}</td></tr></tbody></table></template>
      <template v-else-if="kind === 'error'"><div class="error-message" role="alert"><h1>{{ t('failed') }}</h1><p>{{ t('explanation') }}</p></div></template>
      <template v-else><div class="source-card"><label for="source">{{ t('source') }}</label><textarea id="source" v-model="input" :placeholder="t('input')" /><div class="source-actions"><span>{{ t('detect') }} → {{ t('target') }}</span><button disabled>{{ t('translate') }}</button></div></div></template>
      <div v-if="kind !== 'transcribe'" class="cards"><ResultCard v-for="(name,i) in names" :key="name" :name="name" :text="i % 4 === 2 ? t('failed') : t('synthetic')" :status="t(states[i % states.length]!)" :expand-label="t('expand')" :collapse-label="t('collapse')" /></div>
    </main>
    <footer class="statusbar">{{ t('unavailable') }}<span>F00</span></footer>
  </div>
</template>
