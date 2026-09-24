// Development-server stand-in for susu.exe (vite dev only; never part of the production bundle — main.ts imports
// it behind import.meta.env.DEV). It answers Ready and commands with fixture data so layouts can be reviewed in a
// browser. Nothing here talks to a real service or stores anything.
import { UI_VERSION, type CardSnapshot, type SettingsView, type TranslationSnapshot, type UiEnvelope, type WindowKind } from '@protocol/ui';
import type { HostPort } from './bridge';

export function createDevHost(kind: WindowKind, session: string, language: string): HostPort {
  const listeners: ((event: MessageEvent) => void)[] = [];
  let sequence = 0;
  let generation = 0;
  let revision = 0;
  const emit = (envelope: Omit<UiEnvelope, 'uiVersion' | 'windowSessionId' | 'sequence'>) =>
    setTimeout(() => {
      const data = { uiVersion: UI_VERSION, windowSessionId: session, sequence: ++sequence, ...envelope };
      for (const listener of listeners) listener(new MessageEvent('message', { data }));
    }, 20);

  const cards = (text: string, state: CardSnapshot['state']): CardSnapshot[] => [
    { serviceId: 'fixture-echo', displayName: 'Fixture · 回显', state, collapsed: false, text },
    { serviceId: 'fixture-stream', displayName: 'Fixture · 流式', state, collapsed: false, text: '' },
    { serviceId: 'fixture-network', displayName: 'Fixture · 网络错误', state: 'CollapsedIdle', collapsed: true, text: '' },
    { serviceId: 'fixture-unsupported', displayName: 'Fixture · 不支持语言对', state: 'Unsupported', collapsed: true, text: '' },
  ];
  let translation: TranslationSnapshot = { revision, generation, sourceText: '', from: 'en', to: 'zh-Hans', cards: cards('', 'CollapsedIdle').map((c) => ({ ...c, state: 'CollapsedIdle', collapsed: c.serviceId !== 'fixture-echo' && c.serviceId !== 'fixture-stream' })) };
  const settings: SettingsView = {
    revision: 3,
    fileHash: 'dev',
    issues: [],
    general: { uiLanguage: language, sourceLanguage: 'en', targetLanguage: 'zh-Hans', defaultExpandedCards: 2, allowClipboardBorrowing: false, closeAction: 'hide', launchAtStartup: false },
    hotkeys: [
      { action: 'inputTranslate', chord: 'Alt+A', state: 'ok' },
      { action: 'selectionTranslate', chord: 'Alt+D', state: 'unavailable', reasonKey: 'feature.inDevelopment' },
      { action: 'clipboardTranslate', chord: 'Alt+D', state: 'unavailable', reasonKey: 'feature.inDevelopment' },
      { action: 'ocrTranslate', chord: 'Alt+S', state: 'unavailable', reasonKey: 'feature.inDevelopment' },
      { action: 'voiceTranslate', chord: 'Alt+V', state: 'unavailable', reasonKey: 'feature.inDevelopment' },
      { action: 'audioTranslate', chord: 'Alt+B', state: 'failed' },
      { action: 'videoTranscribe', chord: '', state: 'unassigned' },
      { action: 'pronounce', chord: '', state: 'unassigned' },
    ],
    network: { proxyMode: 'system', proxyHost: '', proxyPort: 0, proxyUsername: '', proxyPasswordSaved: false, aiTimeoutSeconds: 30 },
    services: [
      { serviceId: 'mymemory/translate', instanceId: 'mymemory', capability: 'translate', page: 'engines', enabled: true, availability: 'Ready', implemented: false, secretNames: [] },
      { serviceId: 'deepl/translate', instanceId: 'deepl', capability: 'translate', page: 'engines', enabled: true, availability: 'MissingCredential', implemented: false, secretNames: ['apiKey'] },
      { serviceId: 'tencent-translate/translate', instanceId: 'tencent-translate', capability: 'translate', page: 'engines', enabled: false, availability: 'Disabled', implemented: false, secretNames: ['secretId', 'secretKey'], accountId: 'tencent-translate' },
      { serviceId: 'openai/translate', instanceId: 'openai', capability: 'translate', page: 'ai', enabled: false, availability: 'Disabled', implemented: false, secretNames: ['apiKey'] },
    ],
    accounts: [{ id: 'tencent-translate', label: 'tencent-translate', secrets: [{ name: 'secretId', saved: true }, { name: 'secretKey', saved: false }], usedBy: ['tencent-translate'] }],
  };

  const reply = (name: string | undefined, correlationId: string | undefined, ok: boolean, value?: unknown, error?: string) =>
    emit({ kind: 'Result', name, correlationId, payload: { ok, value, error } });

  const handle = (envelope: UiEnvelope) => {
    if (envelope.kind === 'Ready') {
      emit({
        kind: 'Snapshot',
        payload: {
          window: { kind, uiLanguage: language, theme: 'light', maximized: false, pinned: false, devPreview: true, features: ['input-translation'] },
          translation: kind === 'Main' ? translation : undefined,
          settings: kind === 'Settings' ? settings : undefined,
          tray: kind === 'Tray' ? { items: ['input-translation', 'clipboard', 'ocr', 'voice', 'system-audio', 'transcription', 'settings', 'check-update', 'exit'].map((id, i) => ({ id, chord: ['Alt+A', 'Alt+D', 'Alt+S', 'Alt+V', 'Alt+B'][i] ?? '', enabled: id === 'input-translation' || id === 'settings' || id === 'exit', reasonKey: ['settings', 'exit', 'input-translation'].includes(id) ? undefined : 'feature.inDevelopment', separatorBefore: id === 'settings' })) } : undefined,
        },
      });
      return;
    }
    const payload = envelope.payload as Record<string, unknown> | undefined;
    switch (envelope.name) {
      case 'Translation.SubmitText': {
        generation++;
        const text = String(payload?.text ?? '');
        translation = { revision: ++revision, generation, sourceText: text, from: 'en', to: 'zh-Hans', cards: cards('', 'Loading') };
        reply(envelope.name, envelope.correlationId, true);
        emit({ kind: 'Event', name: 'translation', payload: translation });
        setTimeout(() => emit({ kind: 'Patch', name: 'card', payload: { revision: ++revision, generation, card: { ...translation.cards[0], state: 'Ready', text: `［预览］${text}` } } }), 400);
        ['这是', '开发预览', '的流式译文。'].forEach((piece, i, all) =>
          setTimeout(() => emit({ kind: 'Patch', name: 'card', payload: { revision: ++revision, generation, card: { ...translation.cards[1], state: i === all.length - 1 ? 'Ready' : 'Streaming', text: all.slice(0, i + 1).join('') } } }), 300 + i * 250));
        return;
      }
      case 'Translation.ToggleCard': {
        const card = translation.cards.find((c) => c.serviceId === payload?.serviceId);
        if (card) emit({ kind: 'Patch', name: 'card', payload: { revision: ++revision, generation, card: card.serviceId === 'fixture-network' && card.collapsed ? { ...card, collapsed: false, state: 'Failed', error: 'network' } : { ...card, collapsed: !card.collapsed, state: card.collapsed ? card.state : 'CollapsedIdle' } } });
        reply(envelope.name, envelope.correlationId, true);
        return;
      }
      case 'Settings.Read':
        reply(envelope.name, envelope.correlationId, true, settings);
        return;
      case 'Settings.Save':
        settings.revision++;
        reply(envelope.name, envelope.correlationId, true, settings);
        return;
      case 'Secret.WriteNew':
      case 'Secret.Delete':
        reply(envelope.name, envelope.correlationId, true, settings);
        return;
      default:
        reply(envelope.name, envelope.correlationId, true);
    }
  };

  return {
    postMessage: (message: string) => handle(JSON.parse(message) as UiEnvelope),
    addEventListener: (_type, listener) => listeners.push(listener),
  };
}
