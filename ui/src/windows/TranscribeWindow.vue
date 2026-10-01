<script setup lang="ts">
import { computed, ref } from 'vue';
import { UI_COMMANDS } from '@protocol/ui';
import type { TranscribeChoiceView } from '@protocol/ui';
import type { Bridge } from '../bridge/bridge';
import type { UiState } from '../bridge/store';
import TitleBar from '../components/TitleBar.vue';
import Icon from '../components/Icon.vue';
import { t, serviceName } from '../locales/i18n';

// Transcribe window 760x580 (DESIGN 9, Transcribe artboard, PLAN 6.2): pick or drop a file (name, length, audio tracks, unsupported
// encoding), start, confirm the upload (services and limits), then stage progress (decode / transcribe / translate) over an
// incremental cue list, pause and resume, a quota stop that offers another service, and the export buttons when finished. Close
// cancels the job; finished cues stay in the host for reopen. The page sees names, numbers and cues: never a path or a token.
const props = defineProps<{ bridge: Bridge; state: UiState }>();
const view = computed(() => props.state.transcribe);
const phase = computed(() => view.value?.phase ?? 'idle');
const cues = computed(() => view.value?.cues ?? []);
const dragging = ref(false);
const busy = ref(false);

const known = (key: string) => t(key) !== key;
const clock = (seconds: number) => {
  const total = Math.max(0, Math.floor(seconds));
  const h = Math.floor(total / 3600), m = Math.floor((total % 3600) / 60), s = total % 60;
  const mm = String(m).padStart(2, '0'), ss = String(s).padStart(2, '0');
  return h > 0 ? `${h}:${mm}:${ss}` : `${mm}:${ss}`;
};
const size = (bytes: number) => bytes >= 1 << 20 ? `${(bytes / (1 << 20)).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`;
const duration = computed(() => clock((view.value?.durationMs ?? 0) / 1000));

const errorText = computed(() => {
  const code = view.value?.errorCode;
  if (!code) return '';
  if (known(`transcribe.error.${code}`)) return t(`transcribe.error.${code}`);
  const kind = view.value?.errorKind;
  return kind && known(`error.${kind}`) ? t(`error.${kind}`) : t('transcribe.error.generic');
});
const settingsHelp = computed(() => ['video.noAsr', 'video.noTranslation', 'asr.noTimecodes'].includes(view.value?.errorCode ?? '') || view.value?.errorKind === 'auth' || view.value?.errorKind === 'quota');

const stages = ['probe', 'asr', 'translate'] as const;
const stageIndex = computed(() => stages.indexOf((view.value?.stage ?? 'none') as (typeof stages)[number]));
const active = computed(() => ['running', 'paused', 'quota', 'confirm', 'failed', 'cancelled', 'done'].includes(phase.value));
const counts = computed(() => {
  const v = view.value;
  if (!v) return '';
  return v.failedCues > 0 ? t('transcribe.countsFailed', { cues: v.cueCount, translated: v.translated, failed: v.failedCues }) : t('transcribe.counts', { cues: v.cueCount, translated: v.translated });
});
const progress = computed(() => {
  const v = view.value;
  return v && v.slicesTotal > 0 ? t('transcribe.progress', { done: Math.min(v.slicesDone, v.slicesTotal), total: v.slicesTotal }) : '';
});
const progressPercent = computed(() => {
  const v = view.value;
  if (!v || v.slicesTotal <= 0) return 0;
  return Math.min(100, Math.round((v.slicesDone / v.slicesTotal) * 100));
});
const canExport = computed(() => ['done', 'failed', 'cancelled', 'paused', 'quota'].includes(phase.value) && (view.value?.cueCount ?? cues.value.length) > 0);
const partial = computed(() => phase.value !== 'done');
const statusLine = computed(() => {
  const v = view.value;
  switch (phase.value) {
    case 'picked': return t('transcribe.statusPicked');
    case 'pickError': return errorText.value;
    case 'confirm': return t('transcribe.status.confirm');
    case 'running': return `${t('transcribe.status.running')}${progress.value ? ` · ${progress.value}` : ''}`;
    case 'paused': case 'quota': case 'failed': case 'cancelled': case 'done': return t(`transcribe.status.${phase.value}`);
    default: return v?.hotkey ? t('transcribe.statusIdle', { chord: v.hotkey }) : t('transcribe.statusIdleNoChord');
  }
});

const formats = ['srt', 'vtt', 'txt'] as const;
const modes = ['original', 'translation', 'bilingual', 'bilingualTranslationFirst'] as const;
const exportState = computed(() => view.value?.export ?? null);
const exportError = computed(() => {
  const code = exportState.value?.error;
  return code ? (known(`transcribe.export.error.${code}`) ? t(`transcribe.export.error.${code}`) : t('transcribe.error.generic')) : '';
});
const issueText = (code: string) => (known(`transcribe.issue.${code}`) ? t(`transcribe.issue.${code}`) : code);

const quotaChoices = computed(() => (view.value?.choices ?? []).filter((c) => !c.current));
const choiceLabel = (c: TranscribeChoiceView) => (c.kind === 'asr' ? `${serviceName(c.id)} · ${c.model}` : serviceName(c.id));
const uploadView = computed(() => view.value?.upload ?? null);

async function run(name: Parameters<Bridge['command']>[0], payload?: object): Promise<boolean> {
  if (busy.value) return false;
  busy.value = true;
  try { return (await props.bridge.command(name, payload)).ok; } finally { busy.value = false; }
}
const pick = () => run(UI_COMMANDS.PickMedia);
const start = () => run(UI_COMMANDS.StartTranscription);
const pause = () => run(UI_COMMANDS.PauseTranscription);
const resume = () => run(UI_COMMANDS.ResumeTranscription);
const cancel = () => run(UI_COMMANDS.CancelTranscription);
const confirm = (accept: boolean) => run(UI_COMMANDS.ConfirmTranscription, { accept });
const exportAs = (format: string, mode: string) => run(UI_COMMANDS.Export, { format, mode });
async function switchTo(choice: TranscribeChoiceView): Promise<void> {
  if (!(await run(UI_COMMANDS.ChangeTranslator, { side: choice.kind, id: choice.id, model: choice.model }))) return;
  await run(UI_COMMANDS.ResumeTranscription);
}

// The native layer receives the drop (the page is never given a path); the page only shows the target.
const onDragOver = (event: DragEvent) => { event.preventDefault(); dragging.value = ['idle', 'picked', 'pickError', 'done', 'failed', 'cancelled'].includes(phase.value); };
const onDragLeave = () => { dragging.value = false; };
const onDrop = (event: DragEvent) => { event.preventDefault(); dragging.value = false; };
</script>

<template>
  <div class="window transcribe" :class="{ dragging }" @dragover="onDragOver" @dragleave="onDragLeave" @drop="onDrop">
    <TitleBar :title="`Su-Su · ${t('transcribe.title')}`" show-settings maximizable
      @minimize="bridge.command(UI_COMMANDS.Minimize)" @maximize="bridge.command(UI_COMMANDS.Maximize)" @close="bridge.command(UI_COMMANDS.Close)" @settings="bridge.command(UI_COMMANDS.OpenSettings)" />
    <main class="content" :data-phase="phase">
      <!-- no file -->
      <section v-if="phase === 'idle'" class="drop" data-stage="idle">
        <Icon name="video" :size="30" />
        <h2>{{ t('transcribe.idleTitle') }}</h2>
        <p class="hint-text">{{ t('transcribe.idleHint') }}</p>
        <button type="button" class="btn primary" data-pick :disabled="busy" @click="pick">{{ t('transcribe.pick') }}</button>
      </section>

      <!-- file chosen (picked) or unusable (pickError) -->
      <section v-else-if="phase === 'picked' || phase === 'pickError'" class="card" :data-stage="phase">
        <dl class="facts">
          <dt>{{ t('transcribe.file') }}</dt><dd data-file-name>{{ view?.fileName }}</dd>
          <template v-if="view?.durationMs !== undefined && view?.durationMs !== null">
            <dt>{{ t('transcribe.duration') }}</dt>
            <dd data-duration>{{ duration }} · {{ t(view.hasVideo ? 'transcribe.kind.video' : 'transcribe.kind.audio') }}</dd>
          </template>
        </dl>
        <div v-if="view?.streams.length" class="streams">
          <h3>{{ t('transcribe.streams') }}</h3>
          <ul>
            <li v-for="(s, i) in view.streams" :key="i" :class="{ off: !s.decodable }" data-stream>
              {{ t('transcribe.stream', { codec: s.codec, rate: s.sampleRate, channels: s.channels }) }}
              <span class="tag">{{ s.selected ? t('transcribe.stream.selected') : !s.decodable ? t('transcribe.stream.unsupported') : '' }}</span>
            </li>
          </ul>
        </div>
        <p v-if="view?.errorCode" class="error-text" role="alert" data-error>{{ errorText }}
          <button v-if="settingsHelp" type="button" class="link" @click="bridge.command(UI_COMMANDS.OpenSettings)">{{ t('transcribe.settingsLink') }}</button>
        </p>
        <div class="actions">
          <button type="button" class="btn" data-pick :disabled="busy" @click="pick">{{ t('transcribe.pickAnother') }}</button>
          <button v-if="phase === 'picked'" type="button" class="btn primary" data-start :disabled="busy" @click="start">{{ t('transcribe.start') }}</button>
        </div>
      </section>

      <!-- a job: progress, controls and the cue list -->
      <template v-else-if="active">
        <section class="head">
          <div class="file">
            <strong data-file-name>{{ view?.fileName }}</strong>
            <span v-if="view?.durationMs" class="hint-text">{{ duration }}</span>
          </div>
          <ol class="stages" data-stages>
            <li v-for="(stage, i) in stages" :key="stage" :class="{ now: phase !== 'done' && i === stageIndex, past: phase === 'done' || i < stageIndex }" :data-stage-item="stage">
              {{ t(`transcribe.stage.${stage}`) }}
            </li>
          </ol>
          <div class="bar" role="progressbar" aria-valuemin="0" aria-valuemax="100" :aria-valuenow="phase === 'done' ? 100 : progressPercent"><span :style="{ width: `${phase === 'done' ? 100 : progressPercent}%` }" /></div>
          <div class="meta">
            <span data-progress>{{ progress }}</span>
            <span data-counts>{{ counts }}</span>
            <span class="spacer" />
            <button v-if="phase === 'running' && view?.stage !== 'probe'" type="button" class="btn" data-pause :disabled="busy" @click="pause">{{ t('transcribe.pause') }}</button>
            <button v-if="phase === 'paused'" type="button" class="btn primary" data-resume :disabled="busy" @click="resume">{{ t('transcribe.resume') }}</button>
            <button v-if="phase === 'running' || phase === 'paused' || phase === 'quota' || phase === 'confirm'" type="button" class="btn" data-cancel :disabled="busy" @click="cancel">{{ t('transcribe.cancel') }}</button>
            <button v-if="phase === 'done' || phase === 'failed' || phase === 'cancelled'" type="button" class="btn" data-pick :disabled="busy" @click="pick">{{ t('transcribe.pickAnother') }}</button>
          </div>
        </section>

        <!-- upload confirmation (T06): nothing was sent yet -->
        <section v-if="phase === 'confirm' && uploadView" class="card confirm" role="dialog" aria-modal="true" :aria-label="t('transcribe.confirm.title')" data-confirm>
          <h2>{{ t('transcribe.confirm.title') }}</h2>
          <p class="hint-text">{{ t('transcribe.confirm.body') }}</p>
          <dl class="facts">
            <dt>{{ t('transcribe.file') }}</dt><dd>{{ uploadView.fileName }} · {{ clock(uploadView.durationMs / 1000) }}</dd>
            <dt>{{ t('transcribe.confirm.asr') }}</dt><dd data-asr>{{ serviceName(uploadView.asrService) }} · {{ uploadView.asrModel }}</dd>
            <dt>{{ t('transcribe.confirm.translation') }}</dt><dd data-translation>{{ uploadView.translationServiceName || serviceName(uploadView.translationService) }}</dd>
          </dl>
          <ul class="notes">
            <li data-size>{{ t('transcribe.confirm.size', { size: size(uploadView.uploadBytesEstimate) }) }}</li>
            <li data-chunk>{{ t('transcribe.confirm.chunk', { seconds: uploadView.chunkSecondsLimit, size: size(uploadView.chunkBytesLimit) }) }}</li>
            <li data-limit>{{ t('transcribe.confirm.translationLimit', { chars: uploadView.translationMaxInput, items: uploadView.translationMaxItems }) }}</li>
            <li data-chars>{{ uploadView.estimatedCharacters != null ? t('transcribe.confirm.chars', { n: uploadView.estimatedCharacters }) : t('transcribe.confirm.charsUnknown') }}</li>
            <li data-price>{{ uploadView.estimatedPrice ? t('transcribe.confirm.price', { price: uploadView.estimatedPrice }) : t('transcribe.confirm.priceUnknown') }}</li>
            <li v-if="uploadView.quotaNoteKey" class="warn" data-quota-note>{{ t(uploadView.quotaNoteKey) }}</li>
          </ul>
          <div class="actions">
            <button type="button" class="btn" data-confirm-no :disabled="busy" @click="confirm(false)">{{ t('transcribe.confirm.no') }}</button>
            <button type="button" class="btn primary" data-confirm-yes :disabled="busy" @click="confirm(true)">{{ t('transcribe.confirm.yes') }}</button>
          </div>
        </section>

        <!-- quota exhausted: the recognized text is kept; another service finishes the rest -->
        <section v-if="phase === 'quota'" class="card quota" role="alert" data-quota :data-side="view?.quotaSide">
          <h2>{{ t(view?.quotaSide === 'asr' ? 'transcribe.quota.asr' : 'transcribe.quota.translation') }}</h2>
          <p class="hint-text">{{ t('transcribe.quota.keep') }}</p>
          <ul v-if="quotaChoices.length" class="choices">
            <li v-for="c in quotaChoices" :key="`${c.kind}:${c.id}:${c.model}`" data-choice>
              <span>{{ choiceLabel(c) }}</span>
              <button type="button" class="btn" :disabled="busy" @click="switchTo(c)">{{ t('transcribe.quota.switch') }}</button>
            </li>
          </ul>
          <p v-else class="hint-text" data-no-choice>{{ t('transcribe.quota.noChoice') }}</p>
          <div class="actions">
            <button type="button" class="link" @click="bridge.command(UI_COMMANDS.OpenSettings)">{{ t('transcribe.settingsLink') }}</button>
            <button type="button" class="btn" data-resume :disabled="busy" @click="resume">{{ t('transcribe.quota.resume') }}</button>
          </div>
        </section>

        <section v-if="phase === 'failed'" class="card failure" role="alert" data-failed>
          <h2>{{ t('transcribe.failed.title') }}</h2>
          <p class="error-text" data-error>{{ errorText }}
            <button v-if="settingsHelp" type="button" class="link" @click="bridge.command(UI_COMMANDS.OpenSettings)">{{ t('transcribe.settingsLink') }}</button>
          </p>
          <p v-if="canExport" class="hint-text">{{ t('transcribe.failed.keep') }}</p>
        </section>

        <ol class="cues" :aria-label="t('transcribe.cues')" data-cues>
          <li v-if="cues.length === 0" class="empty hint-text" data-empty>{{ t('transcribe.cuesEmpty') }}</li>
          <li v-for="cue in cues" :key="cue.id" class="cue" :data-cue="cue.id">
            <span class="time">{{ clock(cue.start) }} → {{ clock(cue.end) }}</span>
            <span class="original selectable">{{ cue.original }}</span>
            <span v-if="cue.translation" class="translation selectable">{{ cue.translation }}</span>
            <span v-else-if="cue.translationError" class="translation error-text" data-cue-failed>{{ t('transcribe.cue.failed') }}</span>
            <span v-else class="translation hint-text">{{ t('transcribe.cue.pending') }}</span>
          </li>
        </ol>

        <section v-if="canExport" class="card export" data-export-panel>
          <h2>{{ t('transcribe.done.title') }}</h2>
          <p class="hint-text">{{ t('transcribe.done.hint') }}<template v-if="partial"> {{ t('transcribe.export.partialNote') }}</template></p>
          <table class="grid">
            <tbody>
              <tr v-for="mode in modes" :key="mode">
                <th scope="row">{{ t(`transcribe.export.${mode}`) }}</th>
                <td v-for="format in formats" :key="format">
                  <button type="button" class="btn" :data-export="`${format}:${mode}`" :disabled="busy" @click="exportAs(format, mode)">{{ format.toUpperCase() }}</button>
                </td>
              </tr>
            </tbody>
          </table>
          <p v-if="exportState?.path" class="saved" role="status" data-saved>{{ t('transcribe.export.saved', { path: exportState.path, n: exportState.exported }) }}</p>
          <p v-else-if="exportState?.error" class="error-text" role="alert" data-export-error>{{ exportError }}</p>
          <div v-if="exportState?.issues.length" class="issues" data-issues>
            <p class="hint-text">{{ t('transcribe.export.skipped', { n: exportState.issues.length }) }}</p>
            <ul><li v-for="issue in exportState.issues" :key="`${issue.cueId}:${issue.code}`">{{ issue.cueId }} · {{ issueText(issue.code) }}</li></ul>
          </div>
        </section>
      </template>
    </main>
    <footer class="statusbar" :class="{ warn: phase === 'pickError' || phase === 'failed' }" data-status>
      <span role="status" :data-phase="phase">{{ statusLine }}</span>
    </footer>
  </div>
</template>

<style scoped>
.window { display: flex; flex-direction: column; height: 100%; }
.window.dragging .content { outline: 2px dashed var(--accent); outline-offset: -8px; }
.content { flex: 1; overflow-y: auto; padding: 14px; display: flex; flex-direction: column; gap: 10px; min-height: 0; }
.drop { flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 10px; border: 1px dashed var(--line-accent); border-radius: var(--radius-card); text-align: center; padding: 24px; }
.drop h2, .card h2 { font-size: 14px; font-weight: 600; margin: 0; }
.drop p { margin: 0; max-width: 420px; }
.card { border: 1px solid var(--line-accent); border-radius: var(--radius-card); padding: 12px 14px; display: flex; flex-direction: column; gap: 8px; flex: none; }
.card p { margin: 0; }
.facts { display: grid; grid-template-columns: max-content 1fr; gap: 4px 14px; margin: 0; font-size: 12.5px; }
.facts dt { color: var(--ink-secondary); }
.facts dd { margin: 0; overflow-wrap: anywhere; }
.streams h3 { font-size: 11px; font-weight: 600; color: var(--ink-secondary); margin: 0 0 4px; }
.streams ul, .notes, .choices, .issues ul { margin: 0; padding-left: 18px; font-size: 12px; }
.streams li.off { color: var(--hint); }
.tag { margin-left: 8px; font-size: 11px; color: var(--hint); }
.actions { display: flex; align-items: center; justify-content: flex-end; gap: 8px; flex-wrap: wrap; }
.link { border: none; background: transparent; padding: 0; font-size: 11px; color: var(--accent); cursor: pointer; margin-right: auto; }
.head { display: flex; flex-direction: column; gap: 8px; flex: none; }
.file { display: flex; align-items: baseline; gap: 10px; font-size: 13px; }
.stages { display: flex; gap: 6px; list-style: none; margin: 0; padding: 0; font-size: 11px; }
.stages li { padding: 2px 10px; border: 1px solid var(--line); border-radius: 999px; color: var(--hint); }
.stages li.past { color: var(--ink-secondary); border-color: var(--line-accent); }
.stages li.now { color: var(--accent); border-color: var(--accent); }
.bar { height: 4px; background: var(--skeleton-light, var(--line)); border-radius: 2px; overflow: hidden; }
.bar span { display: block; height: 100%; background: var(--accent); transition: width 200ms linear; }
.meta { display: flex; align-items: center; gap: 12px; font-size: 11px; color: var(--hint); min-height: 28px; }
.spacer { flex: 1; }
.notes li.warn { color: var(--error); }
.choices { list-style: none; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.choices li { display: flex; align-items: center; justify-content: space-between; gap: 10px; }
.cues { list-style: none; margin: 0; padding: 0; flex: 1; min-height: 120px; overflow-y: auto; border: 1px solid var(--line); border-radius: var(--radius-card); }
.cue { display: grid; grid-template-columns: 120px 1fr 1fr; gap: 10px; padding: 6px 12px; border-bottom: 1px solid var(--line); font-size: 12.5px; line-height: 1.5; }
.cue .time { color: var(--hint); font-variant-numeric: tabular-nums; font-size: 11px; padding-top: 1px; }
.empty { padding: 14px; text-align: center; }
.grid { border-collapse: collapse; font-size: 12px; }
.grid th { text-align: left; font-weight: 400; color: var(--ink-secondary); padding: 3px 14px 3px 0; }
.grid td { padding: 3px 6px 3px 0; }
.saved { font-size: 12px; overflow-wrap: anywhere; }
.statusbar { height: 32px; flex: none; display: flex; align-items: center; padding: 0 14px; border-top: 1px solid var(--line); font-size: 11px; color: var(--hint); }
.statusbar.warn { color: var(--error); }
</style>
