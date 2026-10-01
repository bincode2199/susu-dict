// Su-Su built-in package (F12.2, DEV-PLAN P-R01): OpenAI speech to text, POST /v1/audio/transcriptions (multipart).
// The audio goes up as a multipart `file` part that the host builds from the leased file handle (PLAN 4.5.1); this plugin
// never sees the bytes. The API key is the local secret `apiKey`, sent as `Authorization: Bearer ...` (PLAN 4.5.2).
// Models: `whisper-1` answers `verbose_json` with segments[].start/end/text (output "text" or "segments"); the gpt-4o
// transcribe models answer `json` with text only (output "text"). The host sizes every chunk to the model limits and caps the
// whole request body, so this plugin sends exactly one request per Invoke. `ctx.config.baseUrl` lets a test point at a local server.
const DEFAULT_BASE_URL = 'https://api.openai.com';
const MODELS = { 'whisper-1': { segments: true }, 'gpt-4o-transcribe': { segments: false }, 'gpt-4o-mini-transcribe': { segments: false } };

function header(headers, name) {
  if (!headers) return undefined;
  const lower = name.toLowerCase();
  for (const key of Object.keys(headers)) if (key.toLowerCase() === lower) return headers[key];
  return undefined;
}

function errorInfo(body) {
  const e = body && typeof body === 'object' ? body.error : null;
  if (!e || typeof e !== 'object') return { code: '', type: '' };
  return { code: String(e.code || ''), type: String(e.type || '') };
}

function classify(status, headers, body) {
  const { code, type } = errorInfo(body);
  const detail = `http ${status}${code || type ? ` ${(code || type).slice(0, 120)}` : ''}`;
  if (status === 401 || status === 403 || code === 'invalid_api_key') return new PluginError('auth', detail);
  if (status === 402 || code === 'insufficient_quota' || type === 'insufficient_quota' || code === 'billing_hard_limit_reached') return new PluginError('quota', detail);
  if (status === 429) return new PluginError('rate_limited', detail, header(headers, 'retry-after'));
  if (status === 408 || status === 504) return new PluginError('timeout', detail);
  if (status >= 500 && status <= 599) return new PluginError('network', detail);
  return new PluginError('bad_response', detail);
}

// A BCP-47 hint becomes the ISO-639-1 language OpenAI wants; unknown hints send nothing (auto-detect).
function languageOf(lang) {
  if (typeof lang !== 'string' || lang.length === 0) return undefined;
  const primary = lang.split('-')[0].toLowerCase();
  return /^[a-z]{2}$/.test(primary) ? primary : undefined;
}

export default {
  async asr(req, ctx) {
    const audio = req && req.audio;
    if (!audio || typeof audio.id !== 'string' || audio.id.length === 0) throw new PluginError('bad_response', 'audio handle required');
    const model = req.model;
    if (typeof model !== 'string' || !Object.prototype.hasOwnProperty.call(MODELS, model)) throw new PluginError('bad_response', `unknown model '${model}'`);
    const output = req.output;
    if (output !== 'text' && output !== 'segments') throw new PluginError('bad_response', `unknown output '${output}'`);
    if (output === 'segments' && !MODELS[model].segments) throw new PluginError('bad_response', `model '${model}' has no timecodes`);
    const cfg = ctx.config || {};
    const baseUrl = cfg.baseUrl || DEFAULT_BASE_URL;

    const fields = [
      { name: 'file', filename: 'audio.wav', contentType: 'audio/wav', file: audio.id },
      { name: 'model', text: model },
      { name: 'response_format', text: output === 'segments' ? 'verbose_json' : 'json' },
    ];
    if (output === 'segments') fields.push({ name: 'timestamp_granularities[]', text: 'segment' });
    const language = languageOf(req.lang);
    if (language) fields.push({ name: 'language', text: language });

    const r = await ctx.$http({
      method: 'POST',
      url: `${baseUrl}/v1/audio/transcriptions`,
      headers: { Authorization: '' },
      credentials: [{ target: { area: 'header', name: 'Authorization' }, parts: [{ literal: 'Bearer ' }, { secret: 'apiKey' }] }],
      body: { kind: 'multipart', fields },
    });
    if (r.status < 200 || r.status >= 300) throw classify(r.status, r.headers, r.body);
    const body = r.body;
    if (!body || typeof body !== 'object') throw new PluginError('bad_response', 'response is not JSON');
    if (output === 'text') {
      if (typeof body.text !== 'string') throw new PluginError('bad_response', 'missing text');
      return { kind: 'text', text: body.text };
    }
    // Segments are passed through untouched: the host validates every number and field, and nothing is repaired here.
    if (!Array.isArray(body.segments)) throw new PluginError('bad_response', 'missing segments');
    return { kind: 'segments', segments: body.segments.map(s => (s && typeof s === 'object' ? { start: s.start, end: s.end, text: s.text } : s)) };
  },
};
