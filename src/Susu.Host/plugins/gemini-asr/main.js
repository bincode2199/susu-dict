// Su-Su built-in package (F12.2, DEV-PLAN P-R02): Gemini speech to text through generateContent with inlineData audio.
// The host writes the standard Base64 of the leased chunk into the reserved null at /contents/0/parts/1/inlineData/data
// (PLAN 4.5.1 bodyFiles); this plugin never sees the bytes. The key is the local secret `apiKey` in `X-Goog-Api-Key`.
// Text only (output "text"): no timecodes are asked for or accepted, a model-written time is never used as alignment (PLAN 4.7.2).
// The answer must be non-empty text from a finished generation; a blocked prompt, a refusal, truncation or an empty answer is
// bad_response. The host caps the whole request (Base64 and prompt included) at the model's maxRequestBytes.
const DEFAULT_BASE_URL = 'https://generativelanguage.googleapis.com';
const MODELS = ['gemini-2.5-flash'];

function header(headers, name) {
  if (!headers) return undefined;
  const lower = name.toLowerCase();
  for (const key of Object.keys(headers)) if (key.toLowerCase() === lower) return headers[key];
  return undefined;
}

function classify(status, headers, body) {
  const e = body && typeof body === 'object' && body.error && typeof body.error === 'object' ? body.error : {};
  const reason = String(e.status || '');
  const detail = `http ${status}${reason ? ` ${reason.slice(0, 120)}` : ''}`;
  if (status === 401 || status === 403 || reason === 'UNAUTHENTICATED' || reason === 'PERMISSION_DENIED') return new PluginError('auth', detail);
  if (status === 429 && /billing|quota|exceeded your current/i.test(String(e.message || ''))) return new PluginError('quota', detail);
  if (status === 429 || reason === 'RESOURCE_EXHAUSTED') return new PluginError('rate_limited', detail, header(headers, 'retry-after'));
  if (status === 408 || status === 504 || reason === 'DEADLINE_EXCEEDED') return new PluginError('timeout', detail);
  if (status >= 500 && status <= 599) return new PluginError('network', detail);
  return new PluginError('bad_response', detail);
}

function promptFor(lang) {
  const hint = typeof lang === 'string' && lang.length > 0 ? ` The speech is in the language with BCP-47 code "${lang}".` : '';
  return 'Transcribe the speech in this audio exactly as spoken. Output only the transcript text: no commentary, no translation, '
    + `no timestamps and no speaker labels. If there is no speech, output nothing.${hint}`;
}

export default {
  async asr(req, ctx) {
    const audio = req && req.audio;
    if (!audio || typeof audio.id !== 'string' || audio.id.length === 0) throw new PluginError('bad_response', 'audio handle required');
    const model = req.model;
    if (MODELS.indexOf(model) < 0) throw new PluginError('bad_response', `unknown model '${model}'`);
    if (req.output !== 'text') throw new PluginError('bad_response', `output '${req.output}' is not offered (text only)`);
    const cfg = ctx.config || {};
    const baseUrl = cfg.baseUrl || DEFAULT_BASE_URL;

    const r = await ctx.$http({
      method: 'POST',
      url: `${baseUrl}/v1beta/models/${model}:generateContent`,
      headers: { 'X-Goog-Api-Key': '', 'Content-Type': 'application/json' },
      credentials: [{ target: { area: 'header', name: 'X-Goog-Api-Key' }, parts: [{ secret: 'apiKey' }] }],
      body: {
        kind: 'json',
        value: {
          contents: [{ role: 'user', parts: [{ text: promptFor(req.lang) }, { inlineData: { mimeType: audio.mime || 'audio/wav', data: null } }] }],
          generationConfig: { temperature: 0 },
        },
      },
      bodyFiles: [{ pointer: '/contents/0/parts/1/inlineData/data', file: audio.id, encoding: 'base64' }],
    });
    if (r.status < 200 || r.status >= 300) throw classify(r.status, r.headers, r.body);
    const body = r.body;
    if (!body || typeof body !== 'object') throw new PluginError('bad_response', 'response is not JSON');
    const blocked = body.promptFeedback && body.promptFeedback.blockReason;
    if (blocked) throw new PluginError('bad_response', `prompt blocked: ${String(blocked).slice(0, 80)}`);
    const candidate = Array.isArray(body.candidates) ? body.candidates[0] : null;
    if (!candidate || typeof candidate !== 'object') throw new PluginError('bad_response', 'no candidate');
    const finish = String(candidate.finishReason || 'STOP');
    if (finish === 'MAX_TOKENS') throw new PluginError('bad_response', 'transcript truncated (MAX_TOKENS)');
    if (finish !== 'STOP') throw new PluginError('bad_response', `generation did not finish normally (${finish.slice(0, 40)})`);
    const parts = candidate.content && Array.isArray(candidate.content.parts) ? candidate.content.parts : [];
    const text = parts.map(p => (p && typeof p.text === 'string' ? p.text : '')).join('').trim();
    if (text.length === 0) throw new PluginError('bad_response', 'empty transcript');
    return { kind: 'text', text };
  },
};
