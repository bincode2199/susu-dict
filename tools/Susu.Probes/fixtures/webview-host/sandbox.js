// Sandboxed (opaque-origin) plugin supervisor for the F00 WebView fallback-route benchmark:
// one module Worker per plugin package, and a watchdog that terminates and rebuilds a worker
// that does not return to its event loop within the same 250 ms continuous-execution budget.
'use strict';
const BUDGET_MS = 250;
const workers = new Map();
const workerSource = [
  "const KINDS = new Set(['auth','quota','rate_limited','network','timeout','unsupported_language','bad_response']);",
  "class PluginError extends Error { constructor(kind, detail) { super(String(kind)); this.kind = kind; this.detail = detail; } }",
  "globalThis.PluginError = PluginError;",
  "for (const name of ['fetch','XMLHttpRequest','WebSocket','EventSource','importScripts','indexedDB','caches']) {",
  "  try { Object.defineProperty(globalThis, name, { value: undefined, writable: false, configurable: false }); } catch (_) {}",
  "}",
  "let plugin = null; const calls = new Map(); const apis = new Map(); let nextApi = 0;",
  "function idle() { setTimeout(() => postMessage({ type: 'Idle' }), 0); }",
  "function toError(e) {",
  "  if (e && typeof e === 'object' && typeof e.kind === 'string' && KINDS.has(e.kind)) return { kind: e.kind, detail: e.detail === undefined ? undefined : String(e.detail).slice(0, 1024) };",
  "  if (e && e.kind === 'cancelled') return { kind: 'cancelled' };",
  "  return { kind: 'bad_response', detail: String(e && e.message || e).slice(0, 1024) };",
  "}",
  "onmessage = async (event) => {",
  "  const m = event.data;",
  "  if (m.type === 'Load') {",
  "    try { plugin = (await import(m.url)).default; postMessage({ type: 'Loaded', ok: typeof plugin === 'object' && plugin !== null }); }",
  "    catch (e) { postMessage({ type: 'Loaded', ok: false, error: String(e && e.message || e) }); }",
  "    idle(); return;",
  "  }",
  "  if (m.type === 'Invoke') {",
  "    const listeners = [];",
  "    const signal = { aborted: false, reason: undefined, onabort: null,",
  "      addEventListener(t, f) { if (t === 'abort' && typeof f === 'function') listeners.push(f); },",
  "      removeEventListener(t, f) { const i = listeners.indexOf(f); if (i >= 0) listeners.splice(i, 1); },",
  "      throwIfAborted() { if (signal.aborted) throw signal.reason; } };",
  "    const call = { abort(reason) { if (signal.aborted) return; signal.aborted = true; signal.reason = reason; for (const f of listeners.slice()) { try { f({ type: 'abort' }); } catch (_) {} } } };",
  "    calls.set(m.callId, call);",
  "    const api = (op, args) => signal.aborted ? Promise.reject(signal.reason) : new Promise((resolve, reject) => {",
  "      const apiId = ++nextApi; apis.set(apiId, { resolve, reject, callId: m.callId });",
  "      postMessage({ type: 'ApiCall', callId: m.callId, apiId, op, args });",
  "    });",
  "    const ctx = Object.freeze({ config: Object.freeze(m.config || {}), signal, lang: Object.freeze({ from: 'en', to: 'zh-Hans' }),",
  "      $http: (r) => api('http', r), $store: Object.freeze({ get: (k) => api('store.get', { key: k }), set: (k, v) => api('store.set', { key: k, value: v }) }),",
  "      $log: () => {} });",
  "    const finish = (message) => { if (calls.delete(m.callId)) postMessage(message); };",
  "    Promise.resolve().then(() => { const f = plugin && plugin[m.capability]; if (typeof f !== 'function') throw new PluginError('bad_response', 'capability not exported'); return f.call(plugin, m.request, ctx); })",
  "      .then((r) => finish({ type: 'Completed', callId: m.callId, ok: true, result: r }), (e) => finish({ type: 'Completed', callId: m.callId, ok: false, error: toError(e) }));",
  "    idle(); return;",
  "  }",
  "  if (m.type === 'ApiResult') { const a = apis.get(m.apiId); if (!a) return; apis.delete(m.apiId); (m.ok ? a.resolve : a.reject)(m.value); idle(); return; }",
  "  if (m.type === 'Cancel') {",
  "    const c = calls.get(m.callId); if (!c) return; calls.delete(m.callId);",
  "    for (const [id, a] of apis) if (a.callId === m.callId) { apis.delete(id); a.reject({ name: 'AbortError', kind: 'cancelled' }); }",
  "    c.abort({ name: 'AbortError', kind: 'cancelled' }); idle();",
  "  }",
  "};",
].join('\n');
const workerUrl = URL.createObjectURL(new Blob([workerSource], { type: 'text/javascript' }));

function arm(slot, ms = BUDGET_MS) {
  clearTimeout(slot.watchdog);
  slot.watchdog = setTimeout(() => fault(slot), ms);
}

function spawn(pluginId, url) {
  const slot = { pluginId, url, calls: new Map(), watchdog: 0, loadStarted: 0, rebuild: null,
    worker: new Worker(workerUrl, { name: pluginId }) }; // classic: blob module workers fail to start in the opaque-origin sandbox; import() still loads the plugin as a module
  slot.worker.onmessage = (event) => {
    const m = event.data;
    if (m.type === 'Idle') { clearTimeout(slot.watchdog); return; }
    if (m.type === 'Loaded') {
      const ms = performance.now() - slot.loadStarted;
      if (slot.rebuild) parent.postMessage({ type: 'RuntimeFault', pluginId, failedCalls: slot.rebuild.failed, rebuildMs: performance.now() - slot.rebuild.started, ok: m.ok }, '*');
      else parent.postMessage({ type: 'Loaded', pluginId, ok: m.ok, error: m.error, ms }, '*');
      return;
    }
    if (m.type === 'ApiCall') {
      const c = slot.calls.get(m.callId); if (!c) return;
      parent.postMessage({ type: 'ApiCall', pluginId, requestId: c.requestId, grant: c.grant, callId: m.callId, apiId: m.apiId, op: m.op, args: m.args }, '*');
      return;
    }
    if (m.type === 'Completed') {
      const c = slot.calls.get(m.callId); if (!c) return; slot.calls.delete(m.callId);
      parent.postMessage({ type: m.ok ? 'Completed' : 'Failed', pluginId, requestId: c.requestId, callId: m.callId, ok: m.ok, result: m.result, error: m.error }, '*');
    }
  };
  slot.worker.onerror = (e) => parent.postMessage({ type: 'Diagnostic', pluginId, text: 'worker error: ' + (e.message || e.type) }, '*');
  slot.worker.onmessageerror = () => parent.postMessage({ type: 'Diagnostic', pluginId, text: 'worker messageerror' }, '*');
  return slot;
}

function load(slot) {
  slot.loadStarted = performance.now();
  slot.worker.postMessage({ type: 'Load', url: slot.url });
  arm(slot, BUDGET_MS * 10); // compile + top-level evaluation, as in the engine routes
}

function fault(slot) {
  const started = performance.now();
  const failed = [...slot.calls.keys()];
  for (const [callId, c] of slot.calls) parent.postMessage({ type: 'Failed', pluginId: slot.pluginId, requestId: c.requestId, callId, ok: false, error: { kind: 'timeout', detail: 'execution budget exceeded' } }, '*');
  slot.calls.clear();
  slot.worker.terminate();
  const fresh = spawn(slot.pluginId, slot.url);
  fresh.rebuild = { started, failed };
  workers.set(slot.pluginId, fresh);
  load(fresh);
}

window.addEventListener('message', (event) => {
  if (event.source !== parent) return;
  const m = event.data;
  if (m.type === 'Load') { const slot = spawn(m.pluginId, m.url); workers.set(m.pluginId, slot); load(slot); return; }
  const slot = workers.get(m.pluginId);
  if (!slot) return;
  if (m.type === 'Invoke') {
    if (slot.calls.size >= 2) { parent.postMessage({ type: 'Failed', pluginId: m.pluginId, requestId: m.requestId, callId: m.callId, ok: false, error: { kind: 'bad_response', detail: 'runtime busy' } }, '*'); return; }
    slot.calls.set(m.callId, { requestId: m.requestId, grant: m.grant });
    slot.worker.postMessage({ type: 'Invoke', callId: m.callId, capability: m.capability, request: m.request, config: m.config });
    arm(slot);
  } else if (m.type === 'ApiResult') {
    slot.worker.postMessage({ type: 'ApiResult', apiId: m.apiId, ok: m.ok, value: m.value });
    arm(slot);
  } else if (m.type === 'Cancel') {
    const c = slot.calls.get(m.callId); if (!c) return;
    slot.calls.delete(m.callId);
    parent.postMessage({ type: 'Failed', pluginId: m.pluginId, requestId: c.requestId, callId: m.callId, ok: false, error: { kind: 'cancelled' } }, '*');
    slot.worker.postMessage({ type: 'Cancel', callId: m.callId });
    arm(slot);
  }
});

window.addEventListener('error', (e) => parent.postMessage({ type: 'Diagnostic', text: 'sandbox error: ' + e.message }, '*'));
window.addEventListener('securitypolicyviolation', (e) => parent.postMessage({ type: 'Diagnostic', text: 'csp: ' + e.violatedDirective + ' ' + e.blockedURI }, '*'));
parent.postMessage({ type: 'SandboxReady' }, '*');
