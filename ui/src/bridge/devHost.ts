// Development-server stand-in for susu.exe (vite dev only; never part of the production bundle — main.ts imports
// it behind import.meta.env.DEV). It answers Ready and commands with fixture data so layouts can be reviewed in a
// browser. Nothing here talks to a real service or stores anything; typed secrets are dropped at once and only a
// "saved" flag is kept, like the real host (S07).
import { UI_VERSION, type CardSnapshot, type ConfigFieldView, type CredentialTargetView, type ServiceView, type SettingsView, type SpeechSlotView, type TranslationSnapshot, type UiEnvelope, type WindowKind } from '@protocol/ui';
import type { HostPort } from './bridge';

export function createDevHost(kind: WindowKind, session: string, language: string): HostPort {
  const listeners: ((event: MessageEvent) => void)[] = [];
  let sequence = 0;
  let generation = 0;
  let revision = 0;
  let from = 'en';
  let to = 'zh-Hans';
  const timers = new Map<string, ReturnType<typeof setTimeout>[]>();
  const emit = (envelope: Omit<UiEnvelope, 'uiVersion' | 'windowSessionId' | 'sequence'>) =>
    setTimeout(() => {
      const data = { uiVersion: UI_VERSION, windowSessionId: session, sequence: ++sequence, ...envelope };
      for (const listener of listeners) listener(new MessageEvent('message', { data }));
    }, 20);

  // ---------- translation fixtures ----------
  // echo: instant result · stream: appends in pieces · network: fails first, the manual retry succeeds ·
  // unsupported: never requested. The first two are expanded (defaultExpandedCards = 2).
  const names: Record<string, string> = { 'fixture-echo': 'Fixture · 回显', 'fixture-stream': 'Fixture · 流式', 'fixture-network': 'Fixture · 网络错误', 'fixture-unsupported': 'Fixture · 不支持语言对' };
  const blank = (serviceId: string, collapsed: boolean): CardSnapshot => ({ serviceId, displayName: names[serviceId], state: serviceId === 'fixture-unsupported' ? 'Unsupported' : 'CollapsedIdle', collapsed: collapsed || serviceId === 'fixture-unsupported', text: '' });
  let translation: TranslationSnapshot = { revision, generation, sourceText: '', from, to, cards: Object.keys(names).map((id, i) => blank(id, i >= 2)) };
  let networkAttempts = 0;
  const offline = () => {
    const requested = translation.cards.filter((c) => c.state !== 'CollapsedIdle' && c.state !== 'Cancelled' && c.state !== 'Unsupported');
    return requested.length > 0 && requested.every((c) => c.state === 'Failed' && (c.error === 'network' || c.error === 'timeout'));
  };
  const put = (card: CardSnapshot) => {
    translation.cards = translation.cards.map((c) => (c.serviceId === card.serviceId ? card : c));
    emit({ kind: 'Patch', name: 'card', payload: { revision: ++revision, generation, card, offline: offline() } });
  };
  const cancel = (serviceId: string) => { for (const timer of timers.get(serviceId) ?? []) clearTimeout(timer); timers.delete(serviceId); };
  const later = (serviceId: string, ms: number, action: () => void) => {
    const own = generation;
    const timer = setTimeout(() => { if (own === generation) action(); }, ms);
    timers.set(serviceId, [...(timers.get(serviceId) ?? []), timer]);
  };
  const current = (serviceId: string) => translation.cards.find((c) => c.serviceId === serviceId)!;
  const request = (serviceId: string) => {
    cancel(serviceId);
    const card = current(serviceId);
    const text = translation.sourceText;
    put({ ...card, collapsed: false, state: 'Loading', text: '', error: undefined });
    if (serviceId === 'fixture-echo') later(serviceId, 400, () => put({ ...current(serviceId), state: 'Ready', text: `［预览 ${from}→${to}］${text}` }));
    else if (serviceId === 'fixture-stream')
      ['这是', '开发预览', '的流式译文。'].forEach((piece, i, all) =>
        later(serviceId, 300 + i * 250, () => put({ ...current(serviceId), state: i === all.length - 1 ? 'Ready' : 'Streaming', text: all.slice(0, i + 1).join('') })));
    else if (serviceId === 'fixture-network')
      later(serviceId, 500, () => put(++networkAttempts % 2 === 1 ? { ...current(serviceId), state: 'Failed', error: 'network', text: '' } : { ...current(serviceId), state: 'Ready', text: `［重试成功］${text}` }));
  };

  // ---------- settings fixtures ----------
  const DEEPL = { free: 'https://api-free.deepl.com:443', pro: 'https://api.deepl.com:443' };
  const saved = new Set<string>(['tencent-translate/secretId']);
  const granted = new Set<string>(['tencent-translate/secretId']);
  let deeplPlan: 'free' | 'pro' = 'free';
  const targets = (instanceId: string): CredentialTargetView[] | undefined => {
    const t = (secret: string, origin: string, use: string) => ({ secret, origin, use, saved: saved.has(`${instanceId}/${secret}`), granted: granted.has(`${instanceId}/${secret}`) });
    switch (instanceId) {
      case 'mymemory': return [];
      case 'deepl': return [t('apiKey', DEEPL[deeplPlan], 'header:Authorization')];
      case 'tencent-translate': return [t('secretId', 'https://tmt.tencentcloudapi.com:443', 'signer:tencent-tc3'), t('secretKey', 'https://tmt.tencentcloudapi.com:443', 'signer:tencent-tc3')];
      case 'openai': return [t('apiKey', 'https://api.openai.com:443', 'header:Authorization')];
      default: return undefined;
    }
  };
  let order = ['mymemory/translate', 'deepl/translate', 'tencent-translate/translate', 'openai/translate'];
  const enabled: Record<string, boolean> = { 'mymemory/translate': true, 'deepl/translate': true, 'tencent-translate/translate': false, 'openai/translate': false };
  const page = (serviceId: string) => (serviceId.startsWith('openai') ? 'ai' : 'engines');
  // F07.2 fixture: OpenAI's schema-driven config (model with dynamic options, baseUrl). The model list's
  // revision moves with the address and the key, like the host's dependency revision.
  const openaiConfig: Record<string, string> = {};
  let openaiRevision = 1;
  let modelRevision = 1;
  const openaiFields = (): ConfigFieldView[] => [
    { name: 'model', type: 'string', value: openaiConfig.model, default: 'gpt-4o-mini', title: 'Model', placeholder: 'gpt-4o-mini', dynamic: true, optionsRevision: modelRevision },
    { name: 'baseUrl', type: 'string', value: openaiConfig.baseUrl, format: 'uri', title: 'API address', group: 'advanced', placeholder: 'https://api.openai.com', optionsRevision: 0 },
  ];
  const service = (serviceId: string, secretNames: string[]): ServiceView => {
    const instanceId = serviceId.split('/')[0];
    const credentialTargets = targets(instanceId);
    const ready = (credentialTargets ?? []).every((c) => c.saved && c.granted);
    return {
      serviceId, instanceId, capability: 'translate', page: page(serviceId), enabled: enabled[serviceId], availability: !enabled[serviceId] ? 'Disabled' : ready ? 'Ready' : 'MissingCredential',
      implemented: true, secretNames, accountId: secretNames.length ? instanceId : undefined, credentialTargets, plan: instanceId === 'deepl' ? deeplPlan : undefined, order: order.indexOf(serviceId),
      config: instanceId === 'openai' ? openaiFields() : undefined, instanceRevision: instanceId === 'openai' ? openaiRevision : 1,
    };
  };
  const settings: SettingsView = {
    revision: 3,
    fileHash: 'dev',
    issues: [],
    general: { uiLanguage: language, sourceLanguage: 'en', targetLanguage: 'zh-Hans', defaultExpandedCards: 2, allowClipboardBorrowing: false, closeAction: 'hide', launchAtStartup: false },
    hotkeys: [
      { action: 'inputTranslate', chord: 'Alt+A', state: 'ok' },
      { action: 'selectionTranslate', chord: 'Alt+D', state: 'unavailable', reasonKey: 'feature.inDevelopment' },
      { action: 'clipboardTranslate', chord: 'Alt+D', state: 'unavailable', reasonKey: 'feature.inDevelopment' },
      { action: 'ocrTranslate', chord: 'Alt+S', state: 'unavailable', reasonKey: 'feature.noService.ocr' },
      { action: 'voiceTranslate', chord: 'Alt+V', state: 'unavailable', reasonKey: 'feature.inDevelopment' },
      { action: 'audioTranslate', chord: 'Alt+B', state: 'failed' },
      { action: 'videoTranscribe', chord: '', state: 'unassigned' },
      { action: 'pronounce', chord: '', state: 'unassigned' },
    ],
    network: { proxyMode: 'system', proxyHost: '', proxyPort: 0, proxyUsername: '', proxyPasswordSaved: false, aiTimeoutSeconds: 30 },
    services: [],
    accounts: [],
    prompt: {
      level: '', profile: '', scope: ['openai', 'glm', 'gemini', 'claude', 'ollama'],
      levels: ['literal', 'free', 'ielts-6.5', 'ielts-7.0', 'toefl-100', 'cet-6', 'academic'], aiServices: ['openai', 'glm', 'gemini', 'claude', 'ollama'], profiles: [],
      defaultTemplate: '你是一名专业译者。把下面的原文从 {{from}} 译成 {{to}}。按 {{level}} 对应的书写水平选词与造句：句式自然，不堆砌生僻词，不解释、不加注，只输出译文。\n\n原文：\n{{text}}',
      variables: ['text', 'from', 'to', 'level'],
    },
  };
  // F07.4 fixture: the three speech selections over the catalog's planned packages (none installed yet).
  const speechSel: Record<string, [string, string]> = { tts: ['native-sapi', ''], asr: ['openai-asr', 'whisper-1'], videoAsr: ['openai-asr', 'whisper-1'] };
  const speechCatalog = [
    { instanceId: 'native-sapi', cap: 'tts', native: true, installed: true, plan: 'F10.1', models: [] as [string, boolean][] },
    { instanceId: 'microsoft-tts', cap: 'tts', native: false, installed: false, plan: 'F10.1 P-S01', models: [] as [string, boolean][] },
    { instanceId: 'google-tts', cap: 'tts', native: false, installed: false, plan: 'F10.1 P-S02', models: [] as [string, boolean][] },
    { instanceId: 'tencent-tts', cap: 'tts', native: false, installed: false, plan: 'F10.1 P-S03', models: [] as [string, boolean][] },
    { instanceId: 'openai-asr', cap: 'asr', native: false, installed: false, plan: 'F12.2 P-R01', models: [['whisper-1', true], ['gpt-4o-transcribe', false], ['gpt-4o-mini-transcribe', false]] as [string, boolean][] },
    { instanceId: 'gemini-asr', cap: 'asr', native: false, installed: false, plan: 'F12.2 P-R02', models: [['gemini-2.5-flash', false]] as [string, boolean][] },
  ];
  const speechSlot = (slot: string): SpeechSlotView => {
    const video = slot === 'videoAsr';
    const [instance, model] = speechSel[slot];
    const choices = speechCatalog.filter((c) => c.cap === (slot === 'tts' ? 'tts' : 'asr')).map((c) => {
      const timecodes = c.models.some(([, tc]) => tc);
      return { instanceId: c.instanceId, native: c.native, installed: c.installed, plan: c.plan, timecodes, selectable: !video || timecodes, availability: c.native ? 'Ready' : 'MissingCredential',
        models: c.models.map(([id, tc]) => ({ id, timecodes: tc, selectable: !video || tc })), reasonKey: video && !timecodes ? 'needs-timecodes' : undefined };
    });
    const chosen = speechCatalog.find((c) => c.instanceId === instance);
    const reasonKey = !chosen ? (video ? 'needs-timecodes' : 'none-selected') : chosen.native ? 'not-built' : 'not-installed';
    return { slot, instance, model, choices, ready: false, reasonKey };
  };
  // Dev-only stand-in for the host's single-pass renderer (Susu.Domain.PromptTemplate): values are never rescanned.
  const renderPrompt = (template: string, values: Record<string, string>, text: string) => {
    let inserted = false;
    const out = template.replace(/\{\{(.*?)\}\}/g, (whole, raw: string) => {
      const name = raw.trim();
      if (name === 'text') { if (inserted) return whole; inserted = true; return text; }
      return values[name] ?? whole;
    });
    return inserted ? out : `${out}\n\n${text}`;
  };
  // F15.4 fixture state: the favorited card ids, the rows to check and the last export (dev preview only).
  const favorited = new Set<string>();
  let vocabRows: NonNullable<SettingsView['vocab']>['rows'] = [{ entryId: 'e1', word: 'serendipity', target: 'eudic', revision: 1, state: 'Uncertain', attempts: 1 }];
  let vocabExport: NonNullable<SettingsView['vocab']>['export'];
  // F16.1 fixture: one pick stages a package that overrides nothing; confirm installs it; uninstall removes it (dev preview only).
  type PluginsFixture = NonNullable<SettingsView['plugins']>;
  let pluginInstalled: PluginsFixture['installed'] = [];
  let pluginPending: PluginsFixture['pending'];
  let pluginLast: PluginsFixture['last'];
  let pluginUpdates: NonNullable<PluginsFixture['updates']> = [];
  let pluginCheck: PluginsFixture['check'];
  const pluginsView = (): PluginsFixture => ({ installed: pluginInstalled, pending: pluginPending, last: pluginLast, canPick: true, updates: pluginUpdates, canCheckUpdates: true, check: pluginCheck });
  // F17.1 fixture: a backup flow with no real file (dev preview only; nothing is written or restored).
  type BackupFixture = NonNullable<SettingsView['backup']>;
  let backupStep: BackupFixture['step'] = 'idle';
  let backupLast: BackupFixture['last'];
  let backupScheduled = false;
  const backupPreview = (): NonNullable<BackupFixture['preview']> => ({ token: 'dev-backup', created: '2026-01-01T00:00:00.0000000Z', appVersion: '1.0.0', encrypted: true, includesSecrets: false, schemaOlder: false,
    deltas: [{ area: 'settings', current: 0, backup: 0, differs: true }, { area: 'accounts', current: 1, backup: 2, differs: true }, { area: 'instances', current: 23, backup: 23, differs: false }, { area: 'enabledServices', current: 3, backup: 4, differs: true }, { area: 'prompts', current: 0, backup: 1, differs: true }],
    plugins: [{ id: 'dev.sample', backupVersion: '1.0.0', status: 'missing' }], missingPackages: ['dev.sample'], disabledInstances: ['sample'], accounts: [{ id: 'acct-deepl', label: 'DeepL', missingSecrets: ['apiKey'] }], backupSecrets: 0, keysRemoved: 1, keptFavorites: 12, keptOutbox: 2, conflicts: ['plugins-missing', 'accounts-need-authorization'] });
  const backupView = (): BackupFixture => ({ canExport: true, canImport: true, step: backupStep, fileName: backupStep === 'idle' ? undefined : 'su-su-backup.susubak', preview: backupStep === 'preview' ? backupPreview() : undefined, last: backupLast, scheduled: backupScheduled, scheduledSource: backupScheduled ? 'backup' : undefined, canUndo: false });
  const vocabView = (): NonNullable<SettingsView['vocab']> => ({
    favorites: 3 + favorited.size, canExport: true, rows: vocabRows, export: vocabExport,
    targets: [
      { instanceId: 'ankiconnect', enabled: true, availability: 'Ready', usable: true, origin: 'http://127.0.0.1:8765', local: true, lookup: true, pending: 0, retrying: 0, failed: 1, uncertain: 0, succeeded: 2, lastError: 'network', passFailed: false },
      { instanceId: 'eudic', enabled: false, availability: 'Disabled', usable: false, reasonKey: 'vocab.reason.disabled', origin: 'https://api.frdic.com', local: false, lookup: true, pending: 0, retrying: 0, failed: 0, uncertain: 1, succeeded: 0, passFailed: false },
    ],
  });
  // F11.3 fixture: SetOcr fields and a recognized OCR window (dev preview only; nothing is captured or sent).
  const ocrSettings = { service: 'tencent-ocr', autoTranslate: true, keepScreenshots: false, retentionDays: 7 };
  const ocrView = {
    id: 1, phase: 'recognized', serviceId: 'tencent-ocr', text: 'Rendering is the art of failing better.\nE = mc^2', blocks: [{ text: 'Rendering is the art of failing better.', kind: 'text' }, { text: 'E = mc^2', kind: 'formula' }],
    width: 832, height: 264, translated: true, autoTranslate: true, hotkey: 'Alt+S', elapsedMs: 800,
  };
  // F12.3 fixture: a transcribed voice window (dev preview only; nothing is recorded or sent).
  const voiceView = {
    id: 1, phase: 'transcribed', elapsedMs: 4200, limitMs: 600000, level: 0, silent: false, serviceId: 'openai-asr', text: 'Rendering is the art of failing better.',
    translated: true, hotkey: 'Alt+V', canTranscribe: false, transcribeMs: 1200,
  };
  const project = (): SettingsView => {
    settings.services = [
      service('mymemory/translate', []), service('deepl/translate', ['apiKey']), service('tencent-translate/translate', ['secretId', 'secretKey']), service('openai/translate', ['apiKey']),
    ];
    settings.accounts = ['deepl', 'tencent-translate', 'openai'].filter((id) => [...saved].some((k) => k.startsWith(`${id}/`))).map((id) => ({
      id, label: id, secrets: (targets(id) ?? []).map((c) => ({ name: c.secret, saved: c.saved })), usedBy: [id],
    }));
    settings.speech = { tts: speechSlot('tts'), asr: speechSlot('asr'), videoAsr: speechSlot('videoAsr') };
    settings.services.push(...(['tencent-ocr/ocr', 'simple-latex/ocr'] as const).map((id, i) => ({ ...service(id, i === 0 ? ['secretId', 'secretKey'] : ['apiKey']), capability: 'ocr', page: 'ocr', enabled: i === 0, availability: i === 0 ? 'MissingCredential' : 'Disabled', order: -1 })));
    settings.ocr = { ...ocrSettings, minRetentionDays: 1, maxRetentionDays: 365, hotkey: 'Alt+S', ready: false, reasonKey: 'feature.noService.ocr',
      choices: ['tencent-ocr', 'simple-latex'].map((id) => ({ instanceId: id, serviceId: `${id}/ocr`, enabled: id === 'tencent-ocr', availability: id === 'tencent-ocr' ? 'MissingCredential' : 'Disabled', usable: false })) };
    // F15.4 fixture: SetVocab with one enabled-looking target and one row to check (dev preview only; nothing is sent or written).
    settings.services.push(...(['ankiconnect/vocab', 'eudic/vocab'] as const).map((id) => ({ ...service(id, ['apiKey']), capability: 'vocab', page: 'vocab', enabled: id.startsWith('anki'), availability: id.startsWith('anki') ? 'Ready' : 'Disabled', order: -1 })));
    settings.vocab = vocabView();
    settings.plugins = pluginsView();
    settings.backup = backupView();
    return structuredClone(settings);
  };

  const reply = (name: string | undefined, correlationId: string | undefined, ok: boolean, value?: unknown, error?: string) =>
    emit({ kind: 'Result', name, correlationId, payload: { ok, value, error } });

  const handle = (envelope: UiEnvelope) => {
    if (envelope.kind === 'Ready') {
      emit({
        kind: 'Snapshot',
        payload: {
          window: { kind, uiLanguage: language, theme: 'light', maximized: false, pinned: false, devPreview: true, features: ['input-translation'] },
          translation: kind === 'Main' || kind === 'Ocr' || kind === 'Voice' ? translation : undefined,
          voice: kind === 'Voice' ? voiceView : undefined,
          ocr: kind === 'Ocr' ? ocrView : undefined,
          settings: kind === 'Settings' ? project() : undefined,
          tray: kind === 'Tray' ? { items: ['input-translation', 'clipboard', 'ocr', 'voice', 'system-audio', 'transcription', 'settings', 'check-update', 'exit'].map((id, i) => ({ id, chord: ['Alt+A', 'Alt+D', 'Alt+S', 'Alt+V', 'Alt+B'][i] ?? '', enabled: id === 'input-translation' || id === 'settings' || id === 'exit', reasonKey: ['settings', 'exit', 'input-translation'].includes(id) ? undefined : 'feature.inDevelopment', separatorBefore: id === 'settings' })) } : undefined,
        },
      });
      return;
    }
    const payload = envelope.payload as Record<string, unknown> | undefined;
    const ok = (value?: unknown) => reply(envelope.name, envelope.correlationId, true, value);
    const fail = (error: string) => reply(envelope.name, envelope.correlationId, false, undefined, error);
    switch (envelope.name) {
      case 'Translation.SubmitText': {
        for (const id of Object.keys(names)) cancel(id);
        generation++;
        const text = String(payload?.text ?? '');
        const collapsed = new Map(translation.cards.map((c) => [c.serviceId, c.collapsed]));
        translation = { revision: ++revision, generation, sourceText: text, from, to, cards: Object.keys(names).map((id) => blank(id, collapsed.get(id) ?? true)) };
        ok();
        emit({ kind: 'Event', name: 'translation', payload: structuredClone(translation) });
        for (const card of translation.cards) if (!card.collapsed) setTimeout(() => request(card.serviceId), 30);
        return;
      }
      case 'Translation.ToggleCard': {
        const card = translation.cards.find((c) => c.serviceId === payload?.serviceId);
        if (!card || card.state === 'Unsupported') { ok(); return; }
        if (!card.collapsed) {
          // Collapsing cancels unfinished work (J03); a finished result is kept for the re-expand.
          const active = card.state === 'Loading' || card.state === 'Queued' || card.state === 'Streaming';
          cancel(card.serviceId);
          put(active ? { ...card, collapsed: true, state: 'Cancelled', text: '' } : { ...card, collapsed: true });
        } else if (generation > 0 && card.state !== 'Ready' && card.state !== 'Failed') request(card.serviceId);
        else put({ ...card, collapsed: false });
        ok();
        return;
      }
      case 'Translation.RetryCard': {
        const card = translation.cards.find((c) => c.serviceId === payload?.serviceId);
        if (card && !card.collapsed && generation > 0) request(card.serviceId);
        ok();
        return;
      }
      case 'Translation.SelectLanguage':
        from = String(payload?.from);
        to = String(payload?.to);
        ok();
        return;
      case 'Window.CopyText':
        ok();
        return;
      case 'Settings.Read':
        ok(project());
        return;
      case 'Settings.Save':
        settings.revision++;
        for (const toggle of (payload?.services as { serviceId: string; enabled: boolean }[]) ?? []) enabled[toggle.serviceId] = toggle.enabled;
        settings.general = { ...(payload?.general as SettingsView['general']) };
        ok(project());
        return;
      case 'Settings.ReorderService': {
        const id = String(payload?.serviceId);
        const same = (s: string) => !!payload?.merged || page(s) === page(id); // merged: the General page's list
        const onPage = order.filter(same);
        const slots = order.map((s, i) => (same(s) ? i : -1)).filter((i) => i >= 0);
        onPage.splice(onPage.indexOf(id), 1);
        onPage.splice(Math.max(0, Math.min(Number(payload?.index), onPage.length)), 0, id);
        order = order.slice();
        slots.forEach((slot, k) => (order[slot] = onPage[k]));
        ok(project());
        return;
      }
      case 'Settings.ValidateProvider': {
        const id = String(payload?.serviceId).split('/')[0];
        if ((targets(id) ?? []).some((c) => !c.saved || !c.granted)) { setTimeout(() => fail('missing-credential'), 300); return; }
        // Fixture outcomes: OpenAI's key is valid but its quota is used up; the others are fine.
        setTimeout(() => ok(id === 'openai' ? { serviceId: payload?.serviceId, credential: 'valid', serviceAvailable: false, error: 'quota' } : { serviceId: payload?.serviceId, credential: 'valid', serviceAvailable: true }), 600);
        return;
      }
      case 'Settings.BindAccount': {
        const id = String(payload?.instanceId);
        if (payload?.confirmGrants) for (const c of targets(id) ?? []) if (c.saved) granted.add(`${id}/${c.secret}`);
        ok(project());
        return;
      }
      case 'Settings.SaveServiceConfig': {
        if (payload?.instanceId !== 'openai') { fail('unknown-instance'); return; }
        if (payload?.expectedInstanceRevision !== openaiRevision) { reply(envelope.name, envelope.correlationId, false, project(), 'conflict'); return; }
        for (const { name, value } of (payload?.values as { name: string; value: string }[]) ?? []) {
          if (name === 'baseUrl' && value && !/^https:\/\/[^/?#@]+(\/[^?#]*)?$/.test(value)) {
            reply(envelope.name, envelope.correlationId, false, [{ path: 'instances.openai.config.baseUrl', code: 'uri', message: '', line: 0 }], 'invalid');
            return;
          }
          if (name === 'baseUrl' && (openaiConfig.baseUrl ?? '') !== value) { modelRevision++; granted.delete('openai/apiKey'); }
          if (value) openaiConfig[name] = value; else delete openaiConfig[name];
        }
        openaiRevision++;
        ok(project());
        return;
      }
      case 'Settings.PreviewPrompt': {
        const template = String(payload?.template ?? '') || settings.prompt!.defaultTemplate;
        const level = String(payload?.level ?? '');
        const rendered = renderPrompt(template, { from: 'English', to: 'Chinese (Simplified)', level: level || '通用' }, String(payload?.text ?? ''));
        const unknown = [...template.matchAll(/\{\{(.*?)\}\}/g)].map((m) => m[1].trim()).filter((n, i, all) => !['text', 'from', 'to', 'level'].includes(n) && all.indexOf(n) === i);
        ok({ rendered, unknown });
        return;
      }
      case 'Settings.SavePrompt':
        settings.revision++;
        settings.prompt = { ...settings.prompt!, level: String(payload?.level), profile: String(payload?.profile), scope: payload?.scope as string[], profiles: payload?.profiles as NonNullable<SettingsView['prompt']>['profiles'] };
        ok(project());
        return;
      case 'Settings.SaveOcr': {
        Object.assign(ocrSettings, { service: String(payload?.service), autoTranslate: !!payload?.autoTranslate, keepScreenshots: !!payload?.keepScreenshots, retentionDays: Number(payload?.retentionDays) });
        settings.revision++;
        ok(project());
        return;
      }
      case 'Vocab.Collect': {
        const id = String(payload?.serviceId);
        if (payload?.favorite === true) favorited.add(id);
        else if (payload?.favorite === false) favorited.delete(id);
        ok({ favorited: favorited.has(id), targets: 1 });
        return;
      }
      case 'Vocab.Export':
        vocabExport = { format: String(payload?.format), path: `C:\\Users\\you\\Documents\\su-su-vocabulary.${String(payload?.format)}`, retryable: false, exported: 3, skipped: 0, issues: [] };
        ok(project());
        return;
      case 'Vocab.Resolve':
        vocabRows = vocabRows.filter((r) => !(r.entryId === payload?.entryId && r.target === payload?.target));
        ok(project());
        return;
      case 'Plugin.Pick':
        pluginLast = undefined;
        pluginPending = { token: 'dev', id: 'dev.sample', name: 'Sample', version: '1.0.0', signerKind: 'thirdParty', signer: 'a1b2c3d4e5f60718293a', against: 'none', addedCapabilities: ['dictionary'], removedCapabilities: [], addedOrigins: ['https://example.com'], removedOrigins: [], addedSecrets: [], removedSecrets: [] };
        ok(project());
        return;
      case 'Plugin.CheckUpdates': {
        const target = pluginInstalled[0];
        pluginUpdates = target ? [{ token: 'dev-update', id: target.id, name: target.name, version: '2.0.0', signerKind: target.signerKind, signer: target.signer, replacesVersion: target.version, against: 'installed', baseVersion: target.version, addedCapabilities: [], removedCapabilities: [], addedOrigins: ['https://new.example.com'], removedOrigins: [], addedSecrets: [], removedSecrets: [], reasons: ['permissions-expanded'], isUpdate: true, inFlight: [] }] : [];
        pluginCheck = { checked: pluginInstalled.length, staged: pluginUpdates.length, failures: [] };
        ok(project());
        return;
      }
      case 'Plugin.Confirm':
        if (payload?.token === 'dev-update') {
          if (!payload.acknowledged) { pluginLast = { action: 'install', error: 'install.needsConfirmation', issues: [] }; ok(project()); return; }
          pluginInstalled = pluginInstalled.map((p) => (p.id === pluginUpdates[0]?.id ? { ...p, version: pluginUpdates[0]!.version } : p));
          pluginLast = { action: 'install', id: pluginUpdates[0]?.id, version: pluginUpdates[0]?.version, issues: [], interrupted: 0 };
          pluginUpdates = [];
          ok(project());
          return;
        }
        if (pluginPending) pluginInstalled = [...pluginInstalled, { id: pluginPending.id, name: pluginPending.name, version: pluginPending.version, signerKind: pluginPending.signerKind, signer: pluginPending.signer, capabilities: pluginPending.addedCapabilities, origins: pluginPending.addedOrigins, secrets: [] }];
        pluginLast = { action: 'install', id: pluginPending?.id, version: pluginPending?.version, issues: [] };
        pluginPending = undefined;
        ok(project());
        return;
      case 'Plugin.Discard':
        pluginUpdates = pluginUpdates.filter((u) => u.token !== payload?.token);
        pluginPending = undefined;
        ok(project());
        return;
      case 'Plugin.Uninstall':
        pluginInstalled = pluginInstalled.filter((p) => p.id !== payload?.id);
        pluginLast = { action: 'uninstall', id: String(payload?.id), issues: [] };
        ok(project());
        return;
      case 'Backup.Export':
        backupLast = payload?.includeSecrets && !payload?.password ? { action: 'export', error: 'password-required', encrypted: false, includedSecrets: false, secretCount: 0 } : { action: 'export', fileName: 'su-su-backup.susubak', encrypted: !!payload?.password, includedSecrets: !!payload?.includeSecrets, secretCount: payload?.includeSecrets ? 2 : 0 };
        ok(project());
        return;
      case 'Backup.Pick':
        backupLast = undefined;
        backupStep = 'password';
        ok(project());
        return;
      case 'Backup.Unlock':
        backupLast = payload?.password === 'correct horse battery' ? undefined : { action: 'preview', error: 'decrypt-failed', fileName: 'su-su-backup.susubak', encrypted: false, includedSecrets: false, secretCount: 0 };
        if (!backupLast) backupStep = 'preview';
        ok(project());
        return;
      case 'Backup.Apply':
        backupStep = 'idle';
        backupScheduled = true;
        backupLast = { action: 'apply', encrypted: false, includedSecrets: false, secretCount: 0 };
        ok(project());
        return;
      case 'Backup.Discard':
        backupStep = 'idle';
        backupScheduled = false;
        backupLast = undefined;
        ok(project());
        return;
      case 'Vocab.Sync':
        ok(project());
        return;
      case 'Audio.StartRecording':
      case 'Audio.PauseRecording':
      case 'Audio.StopRecording':
      case 'Audio.CancelRecording':
      case 'Audio.TranscribeRecorded':
      case 'Capture.BeginCapture':
        ok();
        return;
      case 'Settings.SelectSpeech': {
        const slot = String(payload?.slot);
        const instance = String(payload?.instance ?? '');
        const model = String(payload?.model ?? '');
        const entry = speechCatalog.find((c) => c.instanceId === instance);
        if (slot === 'videoAsr' && entry && !entry.models.some(([id, tc]) => id === model && tc)) { fail('needs-timecodes'); return; }
        speechSel[slot] = [instance, model];
        settings.revision++;
        ok(project());
        return;
      }
      case 'Settings.TestNetwork': {
        const mode = (payload?.network as SettingsView['network'] | undefined)?.proxyMode;
        setTimeout(() => ok({
          testedAt: new Date().toISOString(),
          paths: [
            { origin: 'https://api.mymemory.translated.net:443', services: ['mymemory/translate'], route: mode === 'none' ? 'direct' : 'proxy', ok: true, status: 200, elapsedMs: 182 },
            { origin: 'https://api.openai.com:443', services: ['openai/translate'], route: mode === 'none' ? 'direct' : 'proxy', ok: false, error: 'timeout', elapsedMs: 10000 },
          ],
        }), 600);
        return;
      }
      case 'Settings.LoadOptions': {
        if (payload?.dependsOnRevision !== modelRevision) { ok({ instanceId: 'openai', field: 'model', dependsOnRevision: modelRevision, items: [], stale: true }); return; }
        if ((targets('openai') ?? []).some((c) => !c.saved || !c.granted)) { fail('missing-credential'); return; }
        const models = openaiConfig.baseUrl ? ['llama3.1:8b', 'qwen2.5:14b'] : ['gpt-4o', 'gpt-4o-mini', 'gpt-4.1-mini'];
        setTimeout(() => ok({ instanceId: 'openai', field: 'model', dependsOnRevision: payload?.dependsOnRevision, items: models.map((value) => ({ value, label: value })) }), 500);
        return;
      }
      case 'Secret.WriteNew': {
        const id = String(payload?.instanceId);
        const key = `${id}/${String(payload?.secretName)}`;
        if (id === 'openai') modelRevision++; // a new key invalidates the model list
        if (id === 'network.proxy') { settings.network.proxyPasswordSaved = true; ok(project()); return; }
        if (id === 'deepl') {
          const plan = String(payload?.value).trim().endsWith(':fx') ? 'free' : 'pro';
          if (plan !== deeplPlan) granted.delete(key); // the old origin's grant goes with the old key
          deeplPlan = plan;
        }
        saved.add(key);
        if (payload?.confirmGrants) granted.add(key);
        ok(project()); // the value itself is not kept or echoed
        return;
      }
      case 'Secret.Delete': {
        const id = String(payload?.instanceId);
        if (id === 'network.proxy') settings.network.proxyPasswordSaved = false;
        saved.delete(`${id}/${String(payload?.secretName)}`);
        granted.delete(`${id}/${String(payload?.secretName)}`);
        ok(project());
        return;
      }
      default:
        ok();
    }
  };

  return {
    postMessage: (message: string) => handle(JSON.parse(message) as UiEnvelope),
    addEventListener: (_type, listener) => listeners.push(listener),
  };
}
