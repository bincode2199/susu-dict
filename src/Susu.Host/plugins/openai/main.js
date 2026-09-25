// Su-Su built-in package (F06.2a, DEV-PLAN P-A01): OpenAI-compatible chat-completions translate,
// streamed through $http.stream (SSE). `ctx.config.baseUrl` lets a test point this at a local server
// instead of the real vendor; production never sets it, so DEFAULT_BASE_URL is what actually ships.
// One HTTP attempt per Invoke - JobCoordinator/TranslationSession (Susu.Jobs), not this plugin, owns
// the at-most-one automatic retry and the shared deadline (ARCHITECTURE 5.1): a plugin/HTTP layer
// that retried on its own would double the retry budget.
const DEFAULT_BASE_URL = 'https://api.openai.com';
const DEFAULT_MODEL = 'gpt-4o-mini';
const DEFAULT_PROMPT = 'You are a translation engine embedded in a desktop app. Translate the user message from {from} to {to}. Reply with the translation only: no quotes, no notes, no explanation.';

function systemPrompt(from, to, template) {
  return (template || DEFAULT_PROMPT).split('{from}').join(from || 'auto').split('{to}').join(to || 'en');
}

// Incremental SSE line parser: OpenAI's stream delivers "data: {json}\n\n" events, but $http.stream
// hands back arbitrary byte-boundary text pieces, so an event can straddle two pieces - buffer until a
// blank line closes it. Returns the data payloads found in this piece (usually 0 or 1, occasionally
// more if the network layer coalesced several events into one piece).
function makeSseParser() {
  let buffer = '';
  return function push(piece) {
    buffer += piece;
    const out = [];
    let at;
    while ((at = buffer.indexOf('\n\n')) !== -1) {
      const raw = buffer.slice(0, at);
      buffer = buffer.slice(at + 2);
      let data = '';
      for (const line of raw.split('\n')) if (line.startsWith('data:')) data += (data ? '\n' : '') + line.slice(5).replace(/^ /, '');
      if (data.length > 0) out.push(data);
    }
    return out;
  };
}

function header(headers, name) {
  if (!headers) return undefined;
  const lower = name.toLowerCase();
  for (const key of Object.keys(headers)) if (key.toLowerCase() === lower) return headers[key];
  return undefined;
}

// J05: classifies a non-2xx response into the published ErrorKind set. 401/403 are credential
// failures; 429 is rate_limited unless the vendor's own error code says the account is out of quota
// (OpenAI answers both cases with HTTP 429); 5xx is a transient/network-shaped failure the host may
// retry; anything else is bad_response. retryAfter is the vendor's raw header, unparsed - the host
// (RetryPolicy.ParseRetryAfter) decides what it means.
function statusError(status, headers, bodyText) {
  let code;
  try { code = JSON.parse(bodyText).error && JSON.parse(bodyText).error.code; } catch (e) { /* not JSON or no error.code */ }
  const retryAfter = header(headers, 'retry-after');
  if (status === 401 || status === 403) return new PluginError('auth', `http ${status}`);
  if (status === 429) {
    if (code === 'insufficient_quota' || code === 'billing_hard_limit_reached') return new PluginError('quota', code);
    return new PluginError('rate_limited', code || `http ${status}`, retryAfter);
  }
  if (status >= 500 && status <= 599) return new PluginError('network', `http ${status}`);
  return new PluginError('bad_response', `http ${status}`);
}

// F07.2 options (ARCHITECTURE 3.1): the model list comes from GET /v1/models, a free listing call (never a
// generation request). Models that cannot answer a chat completion (embeddings, audio, images, moderation)
// are left out; an OpenAI-compatible server's own ids pass through. The whole list arrives in one response,
// so paging is an offset cursor over it, PAGE_SIZE at a time (the host caps a page at 200 items).
const PAGE_SIZE = 200;
const NON_CHAT = /(embedding|whisper|tts|dall-e|moderation|transcribe|realtime|audio|image|search|^babbage|^davinci)/i;

async function listModels(req, ctx) {
  if (!req || req.field !== 'model') throw new PluginError('bad_response', `no options for field ${req && req.field}`);
  const cfg = ctx.config || {};
  const baseUrl = cfg.baseUrl || DEFAULT_BASE_URL;
  const r = await ctx.$http({
    method: 'GET',
    url: `${baseUrl}/v1/models`,
    headers: { Authorization: '' },
    credentials: [{ target: { area: 'header', name: 'Authorization' }, parts: [{ literal: 'Bearer ' }, { secret: 'apiKey' }] }],
  });
  if (r.status < 200 || r.status >= 300) throw statusError(r.status, r.headers, typeof r.body === 'string' ? r.body : JSON.stringify(r.body || {}));
  const data = r.body && Array.isArray(r.body.data) ? r.body.data : null;
  if (!data) throw new PluginError('bad_response', 'missing data[]');
  const ids = [...new Set(data.map((m) => m && m.id).filter((id) => typeof id === 'string' && id.length > 0 && !NON_CHAT.test(id)))].sort();
  const start = req.cursor ? Number(req.cursor) : 0;
  if (!Number.isInteger(start) || start < 0 || start > ids.length) throw new PluginError('bad_response', 'bad cursor');
  const page = ids.slice(start, start + PAGE_SIZE);
  const next = start + PAGE_SIZE < ids.length ? String(start + PAGE_SIZE) : undefined;
  return { items: page.map((id) => ({ value: id, label: id })), nextCursor: next };
}

export default {
  async options(req, ctx) {
    return listModels(req, ctx);
  },

  async translate(req, ctx) {
    const text = req && req.text;
    if (typeof text !== 'string' || text.length === 0) throw new PluginError('bad_response', 'empty text');
    const cfg = ctx.config || {};
    const baseUrl = cfg.baseUrl || DEFAULT_BASE_URL;
    const model = cfg.model || DEFAULT_MODEL;
    const prompt = systemPrompt(req.from, req.to, cfg.prompt);

    const opened = await ctx.$http.stream({
      method: 'POST',
      url: `${baseUrl}/v1/chat/completions`,
      headers: { Authorization: '', 'Content-Type': 'application/json' },
      credentials: [{ target: { area: 'header', name: 'Authorization' }, parts: [{ literal: 'Bearer ' }, { secret: 'apiKey' }] }],
      body: { kind: 'json', value: { model, stream: true, messages: [{ role: 'system', content: prompt }, { role: 'user', content: text }] } },
    });
    if (opened.error) throw statusError(opened.status, opened.headers, opened.error.body);
    if (opened.status < 200 || opened.status >= 300) throw new PluginError('bad_response', `http ${opened.status}`);

    const parse = makeSseParser();
    let assembled = '';
    let finished = false; // [DONE] or a choice's finish_reason seen: the vendor says the answer is whole
    for await (const piece of opened.chunks) {
      for (const data of parse(piece)) {
        if (data === '[DONE]') { finished = true; continue; }
        let event;
        try { event = JSON.parse(data); } catch (e) { continue; } // ignore malformed/keepalive lines
        const choice = event && event.choices && event.choices[0];
        if (choice && choice.error) throw new PluginError('bad_response', String(choice.error.message || choice.error));
        if (choice && choice.finish_reason) finished = true;
        const delta = choice && choice.delta && choice.delta.content;
        if (typeof delta === 'string' && delta.length > 0) {
          assembled += delta;
          await ctx.$emit(delta);
        }
      }
    }
    if (assembled.length === 0) throw new PluginError('bad_response', 'empty stream');
    // A body that ends cleanly after some deltas but before [DONE]/finish_reason is a truncated
    // answer, not a complete one: report it as network so the host's J02 retry path applies
    // instead of showing the partial text as Ready (F06 verification bug 2).
    if (!finished) throw new PluginError('network', 'stream ended before [DONE]');
    return { text: assembled };
  },
};
