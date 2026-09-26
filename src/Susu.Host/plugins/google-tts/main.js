// Su-Su built-in package (F10.1, DEV-PLAN P-S02): Google Cloud Text-to-Speech v1 text:synthesize
// (https://cloud.google.com/text-to-speech/docs/reference/rest/v1/text/synthesize).
// The answer is JSON with the audio as Base64 in `audioContent`. The plugin asks the host to decode that field into a
// file (responseFiles, PLAN 4.5.1): the plugin gets a file handle and the JSON with `audioContent` set to null, never
// the Base64 (B03). The host injects the key into X-Goog-Api-Key; this plugin never sees it.
// `ctx.config.baseUrl` lets a test point this at a local server. One HTTP attempt per Invoke: the host owns retry.
const DEFAULT_BASE_URL = 'https://texttospeech.googleapis.com';
// text:synthesize accepts at most 5000 bytes of input.
const MAX_BYTES = 5000;

// Canonical BCP-47 code -> Google language code (voices list uses cmn-/yue- for Chinese).
const LANG_MAP = {
  'zh-Hans': 'cmn-CN', zh: 'cmn-CN', 'zh-CN': 'cmn-CN', 'zh-Hant': 'cmn-TW', 'zh-TW': 'cmn-TW', 'zh-HK': 'yue-HK',
  en: 'en-US', ja: 'ja-JP', ko: 'ko-KR', fr: 'fr-FR', de: 'de-DE', es: 'es-ES', it: 'it-IT', pt: 'pt-BR',
  ru: 'ru-RU', vi: 'vi-VN', th: 'th-TH', id: 'id-ID', ar: 'ar-XA', hi: 'hi-IN', tr: 'tr-TR', ms: 'ms-MY',
};

const KEY = [{ target: { area: 'header', name: 'X-Goog-Api-Key' }, parts: [{ secret: 'apiKey' }] }];

function languageCode(req, voice) {
  if (voice) return voice.split('-').slice(0, 2).join('-');
  const lang = req.lang && req.lang !== 'auto' ? req.lang : 'en';
  if (/^[a-z]{2,3}-[A-Z]{2}$/.test(lang) && !LANG_MAP[lang]) return lang;
  const mapped = LANG_MAP[lang] || LANG_MAP[String(lang).split('-')[0]];
  if (!mapped) throw new PluginError('unsupported_language', `Google TTS has no voice mapping for '${lang}'`);
  return mapped;
}

function utf8Length(s) {
  let n = 0;
  for (const ch of s) { const c = ch.codePointAt(0); n += c < 0x80 ? 1 : c < 0x800 ? 2 : c < 0x10000 ? 3 : 4; }
  return n;
}

function header(headers, name) {
  if (!headers) return undefined;
  const lower = name.toLowerCase();
  for (const key of Object.keys(headers)) if (key.toLowerCase() === lower) return headers[key];
  return undefined;
}

// Google errors: { error: { code, message, status } } (https://cloud.google.com/apis/design/errors).
function googleError(status, headers, body) {
  const e = body && typeof body === 'object' ? body.error : null;
  const code = e && e.status ? String(e.status) : '';
  const detail = `http ${status}${code ? ` ${code}` : ''}${e && e.message ? `: ${String(e.message).slice(0, 200)}` : ''}`;
  if (status === 401 || status === 403 || code === 'UNAUTHENTICATED' || code === 'PERMISSION_DENIED') {
    if (e && /quota|billing/i.test(String(e.message || ''))) return new PluginError('quota', detail);
    return new PluginError('auth', detail);
  }
  if (status === 429 || code === 'RESOURCE_EXHAUSTED') return new PluginError('rate_limited', detail, header(headers, 'retry-after'));
  if (status === 504 || code === 'DEADLINE_EXCEEDED') return new PluginError('timeout', detail);
  if ((status >= 500 && status <= 599) || code === 'UNAVAILABLE' || code === 'INTERNAL') return new PluginError('network', detail);
  if (e && /language|voice/i.test(String(e.message || ''))) return new PluginError('unsupported_language', detail);
  return new PluginError('bad_response', detail);
}

export default {
  async tts(req, ctx) {
    const text = req && req.text;
    if (typeof text !== 'string' || text.trim().length === 0) throw new PluginError('bad_response', 'empty text');
    const bytes = utf8Length(text);
    if (bytes > MAX_BYTES) throw new PluginError('bad_response', `text too long for Google TTS (${bytes} > ${MAX_BYTES} bytes)`);
    const cfg = ctx.config || {};
    const voice = req.voice || cfg.voice || '';
    if (voice && !/^[A-Za-z]{2,3}-[A-Za-z0-9]{2,8}(-[A-Za-z0-9]+)*$/.test(voice)) throw new PluginError('bad_response', `invalid voice '${voice}'`);
    const rate = Math.min(2, Math.max(0.5, Number(req.rate) || 1));
    const voiceParams = { languageCode: languageCode(req, voice) };
    if (voice) voiceParams.name = voice;
    const r = await ctx.$http({
      method: 'POST',
      url: `${cfg.baseUrl || DEFAULT_BASE_URL}/v1/text:synthesize`,
      headers: { 'X-Goog-Api-Key': '', 'Content-Type': 'application/json' },
      credentials: KEY,
      body: { kind: 'json', value: { input: { text }, voice: voiceParams, audioConfig: { audioEncoding: 'MP3', speakingRate: rate } } },
      responseType: 'json',
      responseFiles: [{ name: 'audio', pointer: '/audioContent', mime: 'audio/mpeg' }],
      errorPointer: '/error',
    });
    if (r.status < 200 || r.status >= 300) throw googleError(r.status, r.headers, r.body);
    const audio = r.files && r.files.audio;
    if (!audio || typeof audio.id !== 'string') {
      // 2xx without audioContent: the host handed back the /error subtree (or nothing) instead.
      if (r.body && typeof r.body === 'object' && (r.body.status || r.body.message)) throw googleError(Number(r.body.code) || r.status, r.headers, { error: r.body });
      throw new PluginError('bad_response', 'no audioContent in the response');
    }
    return { audio };
  },

  async voices(req, ctx) {
    const cfg = ctx.config || {};
    const r = await ctx.$http({
      method: 'GET',
      url: `${cfg.baseUrl || DEFAULT_BASE_URL}/v1/voices`,
      headers: { 'X-Goog-Api-Key': '' },
      credentials: KEY,
    });
    if (r.status < 200 || r.status >= 300) throw googleError(r.status, r.headers, r.body);
    const list = r.body && Array.isArray(r.body.voices) ? r.body.voices : null;
    if (!list) throw new PluginError('bad_response', 'no voices in the response');
    return list
      .filter((v) => v && typeof v.name === 'string')
      .map((v) => ({ id: v.name, name: v.ssmlGender ? `${v.name} (${String(v.ssmlGender).toLowerCase()})` : v.name, lang: Array.isArray(v.languageCodes) && v.languageCodes.length ? String(v.languageCodes[0]) : '' }));
  },
};
