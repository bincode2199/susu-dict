import { UI_VERSION, type CommandResult, type UiCommandName, type UiEnvelope, type WindowKind } from '@protocol/ui';

/** What the page needs from window.chrome.webview (or the development stand-in). */
export interface HostPort {
  postMessage(message: string): void;
  addEventListener(type: 'message', listener: (event: MessageEvent) => void): void;
}

export interface Inbound {
  onSnapshot(payload: unknown): void;
  onPatch(name: string, payload: unknown): void;
  onEvent(name: string, payload: unknown): void;
}

const kinds: Record<string, WindowKind> = { main: 'Main', settings: 'Settings', tray: 'Tray', selection: 'Selection', clipboard: 'Clipboard', ocr: 'Ocr', voice: 'Voice', transcribe: 'Transcribe', error: 'Error' };

export function windowFromQuery(search: string): { kind: WindowKind; session: string; language: string } {
  const params = new URLSearchParams(search);
  return { kind: kinds[params.get('w') ?? ''] ?? 'Main', session: params.get('s') ?? '', language: params.get('l') ?? 'zh-Hans' };
}

/**
 * Typed page ↔ host channel (ARCHITECTURE 6). The page only sends Ready and whitelisted commands with a
 * correlation id; it accepts only envelopes of its UI version and session, applies them in sequence order and
 * asks for a fresh snapshot (Ready) whenever a gap appears. Commands time out rather than hang the UI.
 */
export class Bridge {
  private lastSequence = 0;
  private counter = 0;
  private readonly pending = new Map<string, { resolve: (result: CommandResult) => void; timer: ReturnType<typeof setTimeout> }>();

  constructor(private readonly host: HostPort, private readonly session: string, private readonly inbound: Inbound, private readonly timeoutMs = 30_000) {
    host.addEventListener('message', (event) => this.receive(event.data));
  }

  ready(): void {
    this.send({ uiVersion: UI_VERSION, kind: 'Ready', windowSessionId: this.session });
  }

  command(name: UiCommandName, payload?: object): Promise<CommandResult> {
    const correlationId = `c${++this.counter}`;
    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        this.pending.delete(correlationId);
        resolve({ ok: false, error: 'timeout' });
      }, this.timeoutMs);
      this.pending.set(correlationId, { resolve, timer });
      this.send({ uiVersion: UI_VERSION, kind: 'Command', windowSessionId: this.session, name, correlationId, payload });
    });
  }

  private send(envelope: UiEnvelope): void {
    this.host.postMessage(JSON.stringify(envelope));
  }

  receive(data: unknown): void {
    const envelope = (typeof data === 'string' ? safeParse(data) : data) as UiEnvelope | null;
    if (!envelope || typeof envelope !== 'object' || envelope.uiVersion !== UI_VERSION || envelope.windowSessionId !== this.session) return;
    const sequence = envelope.sequence ?? 0;
    if (envelope.kind === 'Snapshot') {
      this.lastSequence = sequence;
      this.inbound.onSnapshot(envelope.payload);
      return;
    }
    if (sequence <= this.lastSequence) return; // duplicate or stale
    if (sequence !== this.lastSequence + 1) {
      this.lastSequence = sequence;
      this.ready(); // gap: the host replies with a new snapshot
      return;
    }
    this.lastSequence = sequence;
    switch (envelope.kind) {
      case 'Result': {
        const waiter = envelope.correlationId ? this.pending.get(envelope.correlationId) : undefined;
        if (!waiter) return;
        clearTimeout(waiter.timer);
        this.pending.delete(envelope.correlationId!);
        waiter.resolve((envelope.payload as CommandResult) ?? { ok: false, error: 'empty' });
        return;
      }
      case 'Patch':
        this.inbound.onPatch(envelope.name ?? '', envelope.payload);
        return;
      case 'Event':
        this.inbound.onEvent(envelope.name ?? '', envelope.payload);
        return;
    }
  }
}

function safeParse(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}
