<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { UI_COMMANDS, type UiCommandName } from '@protocol/ui';
import type { Bridge } from '../bridge/bridge';
import type { UiState } from '../bridge/store';
import TitleBar from '../components/TitleBar.vue';
import ResultCard from '../components/ResultCard.vue';
import Icon from '../components/Icon.vue';
import { t, serviceName } from '../locales/i18n';

// Voice window 380 wide, height from content (DESIGN 9, Voice artboard; VO1/VO2): microphone ready, recording (level meter,
// captured time against the 10-minute limit, no live text), paused, transcribing (skeleton lines), result (editable source card
// above the same card list as input translation). One status line at the bottom carries the state; an interrupted recording
// keeps its captured part and offers "transcribe recorded" or "record again". The page never sees audio, only phases and numbers.
const props = defineProps<{ bridge: Bridge; state: UiState }>();
const canSpeak = computed(() => props.state.window?.features.includes('pronunciation') ?? false);
function speakCard(serviceId: string, phonetic?: number): void {
  void props.bridge.command(UI_COMMANDS.SpeakCard, phonetic === undefined ? { serviceId } : { serviceId, phonetic });
}
const voice = computed(() => props.state.voice);
// F13.2: the same window records system audio (source systemAudio); its own wording replaces the microphone's where it differs.
const isSys = computed(() => voice.value?.source === 'systemAudio');
function vt(key: string, params?: Record<string, string | number>): string {
  if (isSys.value) {
    const own = `voice.sys.${key.slice('voice.'.length)}`;
    const text = t(own, params);
    if (text !== own) return text;
  }
  return t(key, params);
}
const phase = computed(() => voice.value?.phase ?? 'idle');
const root = ref<HTMLElement | null>(null);
const text = ref('');
const composing = ref(false);
const from = ref('en');
const to = ref('zh-Hans');
const languages = ['en', 'zh-Hans'];
const count = computed(() => [...text.value].length);
const service = computed(() => (voice.value?.serviceId ? serviceName(voice.value.serviceId) : ''));
const translatedText = computed(() => props.state.translation?.sourceText ?? '');
const offline = computed(() => props.state.translation?.offline === true);
const live = computed(() => phase.value === 'recording' || phase.value === 'paused');
const mmss = (ms: number) => { const s = Math.max(0, Math.floor(ms / 1000)); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`; };
const elapsed = computed(() => mmss(voice.value?.elapsedMs ?? 0));
const remaining = computed(() => mmss((voice.value?.limitMs ?? 0) - (voice.value?.elapsedMs ?? 0)));
const limitMinutes = computed(() => Math.round((voice.value?.limitMs ?? 600000) / 60000));
const level = computed(() => Math.round(Math.min(1, Math.max(0, voice.value?.level ?? 0)) * 100));
const stalled = computed(() => phase.value === 'recording' && voice.value?.silent === true);
const meta = computed(() => {
  const view = voice.value;
  if (!view || phase.value !== 'transcribed') return '';
  const seconds = view.transcribeMs !== undefined && view.transcribeMs !== null ? (view.transcribeMs / 1000).toFixed(1) : '–';
  return vt('voice.meta', { service: service.value, seconds, n: count.value });
});
const micErrors = ['loopback.noDevice', 'loopback.denied', 'loopback.failed', 'mic.denied', 'mic.noDevice', 'mic.failed', 'mic.busy', 'record.diskFull', 'record.writeFailed'];
const errorText = computed(() => {
  const code = voice.value?.errorCode ?? (isSys.value ? 'loopback.failed' : 'mic.failed');
  return vt(`voice.error.${micErrors.includes(code) ? code : isSys.value ? 'loopback.failed' : 'mic.failed'}`);
});
const reasonText = computed(() => vt(`voice.reason.${voice.value?.reason ?? 'failed'}`));
const statusLine = computed(() => {
  const view = voice.value;
  switch (phase.value) {
    case 'recording': return view?.silent ? vt('voice.status.silent') : vt('voice.status.recording', { remaining: remaining.value, limit: limitMinutes.value });
    case 'paused': return vt('voice.status.paused', { elapsed: elapsed.value });
    case 'transcribing': {
      const progress = view?.chunks && view.chunks > 1 ? ` · ${(view.chunk ?? 0) + 1}/${view.chunks}` : '';
      return `${vt('voice.status.transcribing')}${service.value ? ` · ${service.value}` : ''}${progress}`;
    }
    case 'interrupted': return reasonText.value;
    case 'error': return errorText.value;
    case 'noSpeech': return vt('voice.noSpeech');
    case 'failed': return vt('voice.failed');
    case 'unavailable': return vt('voice.unavailable');
    case 'transcribed': return view?.notice === 'limit' ? vt('voice.status.limit', { n: limitMinutes.value }) : view?.hotkey ? vt('voice.statusHint', { chord: view.hotkey }) : vt('voice.statusHintNoChord');
    default: return view?.hotkey ? vt('voice.idleHint', { chord: view.hotkey }) : vt('voice.idleHintNoChord');
  }
});

// Each recording (new id) or its transcript replaces the source text; later edits stay until the next recording.
let adopted = '';
watch(voice, (view) => {
  if (!view) return;
  const key = `${view.id}:${view.phase}`;
  if (key === adopted) return;
  adopted = key;
  if (view.phase === 'transcribed') text.value = view.text ?? '';
  else if (view.phase !== 'transcribing') text.value = '';
}, { immediate: true });
watch(() => props.state.translation, (snapshot) => {
  if (!snapshot) return;
  from.value = snapshot.from || from.value;
  to.value = snapshot.to || to.value;
}, { immediate: true });

const send = (name: UiCommandName) => props.bridge.command(name);
const start = () => send(UI_COMMANDS.StartRecording);
const stop = () => send(UI_COMMANDS.StopRecording);
const pause = () => send(UI_COMMANDS.PauseRecording);
const cancel = () => send(UI_COMMANDS.CancelRecording);
const transcribeRecorded = () => send(UI_COMMANDS.TranscribeRecorded);
function submit(): void {
  if (!text.value.trim()) return;
  void props.bridge.command(UI_COMMANDS.SubmitText, { text: text.value });
}
function onKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Enter' || !event.ctrlKey || composing.value || event.isComposing) return;
  event.preventDefault();
  submit();
}
const copy = (value: string) => props.bridge.command(UI_COMMANDS.CopyText, { text: value });
async function setLanguages(source: string, target: string): Promise<void> {
  from.value = source;
  to.value = target;
  const result = await props.bridge.command(UI_COMMANDS.SelectLanguage, { from: source, to: target });
  if (result.ok && translatedText.value && text.value.trim()) submit();
}
function pick(which: 'from' | 'to', value: string): void {
  const source = which === 'from' ? value : from.value;
  const target = which === 'to' ? value : to.value;
  void (source === target ? setLanguages(to.value, from.value) : setLanguages(source, target));
}

// Auto height: report the natural content height (CSS px = DIP); the host clamps it to the work area, the card list scrolls inside.
let lastHeight = 0;
let frame = 0;
function measure(): void {
  frame = 0;
  const el = root.value;
  if (!el) return;
  let height = 2;
  for (const child of Array.from(el.children) as HTMLElement[]) height += child.classList.contains('content') ? child.scrollHeight : child.offsetHeight;
  height = Math.ceil(height);
  if (height <= 2 || Math.abs(height - lastHeight) < 2) return;
  lastHeight = height;
  void props.bridge.command(UI_COMMANDS.FitContent, { heightDip: height });
}
const schedule = () => { if (!frame) frame = requestAnimationFrame(measure); };
let observer: ResizeObserver | null = null;
let mutations: MutationObserver | null = null;
onMounted(() => {
  if (typeof ResizeObserver !== 'undefined' && root.value) {
    observer = new ResizeObserver(schedule);
    for (const child of Array.from(root.value.children)) observer.observe(child);
  }
  if (typeof MutationObserver !== 'undefined' && root.value) {
    mutations = new MutationObserver(schedule);
    mutations.observe(root.value, { childList: true, subtree: true, characterData: true });
  }
  schedule();
});
onBeforeUnmount(() => { observer?.disconnect(); mutations?.disconnect(); if (frame) cancelAnimationFrame(frame); });
</script>

<template>
  <div ref="root" class="window voice">
    <TitleBar :title="`Su-Su · ${vt('voice.title')}`" show-settings
      @minimize="bridge.command(UI_COMMANDS.Minimize)" @close="bridge.command(UI_COMMANDS.Close)" @settings="bridge.command(UI_COMMANDS.OpenSettings)" />
    <main class="content">
      <section class="source" :class="`phase-${phase}`" :aria-busy="phase === 'transcribing'" data-voice-card>
        <header class="languages">
          <label class="sr-only" for="voice-source-language">{{ t('general.source') }}</label>
          <select id="voice-source-language" class="lang" :value="from" @change="pick('from', ($event.target as HTMLSelectElement).value)">
            <option v-for="code in languages" :key="code" :value="code">{{ t(`lang.${code}`) }}</option>
          </select>
          <button type="button" class="icon-btn" :aria-label="t('lang.swap')" :title="t('lang.swap')" @click="setLanguages(to, from)"><Icon name="swap" /></button>
          <label class="sr-only" for="voice-target-language">{{ t('general.target') }}</label>
          <select id="voice-target-language" class="lang" :value="to" @change="pick('to', ($event.target as HTMLSelectElement).value)">
            <option v-for="code in languages" :key="code" :value="code">{{ t(`lang.${code}`) }}</option>
          </select>
        </header>
        <div class="label-row">
          <span class="label">{{ vt('voice.source') }}</span>
          <span v-if="phase === 'transcribing'" class="tag" role="status">{{ vt('voice.status.transcribing') }}<template v-if="service"> · {{ service }}</template></span>
          <span v-else-if="phase === 'transcribed'" class="hint-text small">{{ vt('voice.editHint') }}</span>
        </div>

        <p v-if="voice?.ownPlayback" class="hint-text small own-playback" role="note" data-own-playback><Icon name="warning" :size="13" />{{ t('voice.sys.ownPlayback') }}</p>

        <!-- microphone ready -->
        <div v-if="phase === 'idle'" class="stage" data-stage="idle">
          <button type="button" class="mic" :aria-label="vt('voice.start')" :title="vt('voice.start')" data-start @click="start"><Icon name="mic" :size="22" /></button>
          <p class="hint-text small">{{ vt('voice.idle') }}</p>
        </div>

        <!-- recording and paused: level and time only, never live text (VO1/VO2) -->
        <div v-else-if="live" class="stage" :data-stage="phase">
          <div class="time" role="timer" data-elapsed><span class="now">{{ elapsed }}</span><span class="of"> / {{ mmss(voice?.limitMs ?? 0) }}</span></div>
          <div class="meter" role="meter" :aria-label="vt('voice.level')" aria-valuemin="0" aria-valuemax="100" :aria-valuenow="phase === 'paused' ? 0 : level" data-meter>
            <span class="fill" :style="{ width: `${phase === 'paused' ? 0 : level}%` }" />
          </div>
          <p v-if="phase === 'paused'" class="hint-text small" role="status" data-paused>{{ vt('voice.pausedHint') }}</p>
          <p v-else-if="stalled" class="notice error-text small" role="status" data-silent><Icon name="warning" :size="13" />{{ vt('voice.silent') }}</p>
          <p v-else class="hint-text small" data-limit>{{ vt('voice.limitHint', { remaining, n: limitMinutes }) }}</p>
          <div class="controls">
            <button type="button" class="btn" data-pause @click="pause">{{ phase === 'paused' ? vt('voice.resume') : vt('voice.pause') }}</button>
            <button type="button" class="btn primary" data-stop @click="stop"><Icon name="stop" />{{ vt('voice.stop') }}</button>
            <button type="button" class="btn" data-cancel @click="cancel">{{ vt('voice.cancel') }}</button>
          </div>
        </div>

        <!-- transcribing: two skeleton lines, no spinner; the recording is already over -->
        <div v-else-if="phase === 'transcribing'" class="stage" data-stage="transcribing">
          <div class="skeleton" aria-hidden="true"><span class="line dark" /><span class="line light" /></div>
          <p v-if="voice?.notice === 'limit'" class="hint-text small" data-limit-note>{{ vt('voice.status.limit', { n: limitMinutes }) }}</p>
          <div class="controls"><button type="button" class="btn" data-cancel @click="cancel">{{ vt('voice.cancel') }}</button></div>
        </div>

        <!-- result -->
        <template v-else-if="phase === 'transcribed'">
          <label class="sr-only" for="voice-source-text">{{ vt('voice.source') }}</label>
          <textarea id="voice-source-text" v-model="text" class="input selectable" spellcheck="false" maxlength="100000"
            @compositionstart="composing = true" @compositionend="composing = false" @keydown="onKeydown" />
        </template>

        <!-- the device went away: what was captured is kept; transcribe it or record again (REC03) -->
        <div v-else-if="phase === 'interrupted'" class="failure" role="alert" data-stage="interrupted">
          <p class="failure-title">{{ vt('voice.interrupted') }}</p>
          <p class="hint-text small">{{ reasonText }}<template v-if="voice?.canTranscribe"> · {{ vt('voice.keptTime', { time: elapsed }) }}</template><template v-else> · {{ vt('voice.nothingKept') }}</template></p>
          <div class="failure-actions">
            <button v-if="voice?.canTranscribe" type="button" class="btn primary" data-transcribe-recorded @click="transcribeRecorded">{{ vt('voice.transcribeRecorded') }}</button>
            <button type="button" class="btn" data-rerecord @click="start"><Icon name="mic" />{{ vt('voice.rerecord') }}</button>
          </div>
        </div>

        <!-- start/save errors and the service outcomes -->
        <div v-else class="failure" role="alert" :data-stage="phase">
          <p class="failure-title">{{ vt(phase === 'error' ? 'voice.errorTitle' : phase === 'noSpeech' ? 'voice.noSpeech' : phase === 'failed' ? 'voice.failed' : 'voice.unavailable') }}</p>
          <p class="hint-text small">
            <template v-if="phase === 'error'">{{ errorText }}</template>
            <template v-else-if="phase === 'noSpeech'">{{ vt('voice.noSpeechDetail') }}</template>
            <template v-else-if="phase === 'failed'">{{ t(`error.${voice?.errorKind ?? 'unavailable'}`) }}<template v-if="service"> · {{ service }}</template></template>
            <template v-else>{{ vt('voice.unavailableDetail') }}</template>
          </p>
          <div class="failure-actions">
            <button v-if="phase === 'unavailable' || voice?.errorKind === 'auth' || voice?.errorKind === 'quota'" type="button" class="link" @click="bridge.command(UI_COMMANDS.OpenSettings)">{{ t('capture.link.settings') }}</button>
            <button v-if="phase !== 'unavailable'" type="button" class="btn" data-rerecord @click="start"><Icon name="mic" />{{ vt('voice.rerecord') }}</button>
          </div>
        </div>

        <footer v-if="phase === 'transcribed'" class="foot">
          <span class="meta">{{ meta }}</span>
          <button type="button" class="icon-btn" :aria-label="vt('voice.copy')" :title="vt('voice.copy')" :disabled="count === 0" @click="copy(text)"><Icon name="copy" /></button>
          <button type="button" class="icon-btn" :aria-label="vt('voice.rerecord')" :title="vt('voice.rerecord')" data-rerecord @click="start"><Icon name="mic" /></button>
          <button type="button" class="btn primary translate" :disabled="count === 0" @click="submit">{{ t('main.translate') }}<span class="hint">Ctrl+↵</span></button>
        </footer>
      </section>
      <ResultCard v-for="card in state.translation?.cards ?? []" :key="card.serviceId" :card="card" :from="from" :to="to"
        @toggle="bridge.command(UI_COMMANDS.ToggleCard, { serviceId: card.serviceId })"
        @retry="bridge.command(UI_COMMANDS.RetryCard, { serviceId: card.serviceId })"
        @copy="(value: string) => copy(value)"
        @settings="bridge.command(UI_COMMANDS.OpenSettings)"
        :speech="state.speech" :can-speak="canSpeak" @speak="(phonetic?: number) => speakCard(card.serviceId, phonetic)"
        @stop-speech="bridge.command(UI_COMMANDS.SpeechStop, {})" />
    </main>
    <footer class="statusbar" :class="{ offline, recording: phase === 'recording', warn: stalled || phase === 'interrupted' || phase === 'error' }" data-status>
      <span v-if="offline" class="offline-note" role="status"><Icon name="warning" :size="13" />{{ t('main.offline') }}</span>
      <span v-else role="status" :data-phase="phase"><span v-if="phase === 'recording'" class="dot" aria-hidden="true" />{{ statusLine }}</span>
    </footer>
  </div>
</template>

<style scoped>
.window { display: flex; flex-direction: column; max-height: 100%; }
.content { flex: 1; overflow-y: auto; padding: 14px; display: flex; flex-direction: column; gap: 10px; min-height: 0; }
.source { border: 1px solid var(--line-accent); border-radius: var(--radius-card); display: flex; flex-direction: column; flex: none; }
.source:focus-within { border-color: var(--accent); }
.languages { height: 38px; display: flex; align-items: center; gap: 2px; padding: 0 6px; border-bottom: 1px solid var(--line); }
.lang { height: 26px; border: none; background: transparent; font-size: 12px; border-radius: var(--radius-icon); padding: 0 4px; color: var(--ink); }
.label-row { display: flex; align-items: center; gap: 8px; padding: 10px 14px 0; min-height: 28px; }
.label { font-size: 11px; font-weight: 600; color: var(--ink-secondary); margin-right: auto; }
.small { font-size: 11px; }
.stage { display: flex; flex-direction: column; align-items: center; gap: 10px; padding: 14px 14px 16px; }
.stage p { margin: 0; text-align: center; }
.own-playback { display: inline-flex; align-items: center; gap: 6px; margin: 0 0 6px; }
.mic { width: 52px; height: 52px; border-radius: 50%; border: 1px solid var(--line-accent); background: transparent; color: var(--ink); display: inline-flex; align-items: center; justify-content: center; cursor: pointer; }
.mic:hover, .mic:focus-visible { outline: none; border-color: var(--accent); color: var(--accent); }
.time { font-size: 26px; font-variant-numeric: tabular-nums; color: var(--ink); }
.time .of { font-size: 12px; color: var(--hint); }
.meter { width: 100%; height: 6px; border-radius: 3px; background: var(--skeleton-light, var(--line)); overflow: hidden; }
.meter .fill { display: block; height: 100%; background: var(--accent); transition: width 80ms linear; }
.notice { display: flex; align-items: center; gap: 6px; }
.controls { display: flex; align-items: center; justify-content: center; gap: 8px; flex-wrap: wrap; }
.controls .btn { display: inline-flex; align-items: center; gap: 6px; }
.input { border: none; outline: none; resize: none; min-height: 132px; padding: 8px 14px 10px; font-size: 15.5px; line-height: 1.62; background: transparent; color: var(--ink); }
.skeleton { display: flex; flex-direction: column; gap: 12px; padding: 4px 0 6px; width: 100%; }
.skeleton .line { display: block; height: 1.5px; }
.skeleton .dark { width: 100%; background: var(--skeleton-dark, var(--line-accent)); }
.skeleton .light { width: 54%; background: var(--skeleton-light, var(--line)); }
.failure { padding: 10px 14px 12px; display: flex; flex-direction: column; gap: 4px; min-height: 110px; }
.failure p { margin: 0; }
.failure-title { font-size: 14px; color: var(--ink); }
.failure-actions { margin-top: auto; display: flex; align-items: center; justify-content: flex-end; gap: 10px; flex-wrap: wrap; padding-top: 8px; }
.failure-actions .btn { display: inline-flex; align-items: center; gap: 6px; }
.foot { height: 40px; display: flex; align-items: center; gap: 4px; padding: 0 6px 0 14px; border-top: 1px solid var(--line); }
.meta { font-size: 11px; color: var(--hint); margin-right: auto; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.link { border: none; background: transparent; padding: 0; font-size: 11px; color: var(--accent); cursor: pointer; }
.offline-note { display: inline-flex; align-items: center; gap: 6px; color: var(--error); }
.statusbar { height: 32px; flex: none; display: flex; align-items: center; padding: 0 14px; border-top: 1px solid var(--line); font-size: 11px; color: var(--hint); }
.statusbar.warn { color: var(--error); }
.statusbar .dot { display: inline-block; width: 7px; height: 7px; border-radius: 50%; background: var(--error); margin-right: 6px; vertical-align: 0; }
</style>
