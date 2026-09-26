// Su-Su built-in package (F10.1, DEV-PLAN P-S01): Microsoft Azure AI Speech text to speech, REST API
// (https://learn.microsoft.com/azure/ai-services/speech-service/rest-text-to-speech).
// POST https://{region}.tts.speech.microsoft.com/cognitiveservices/v1 with an SSML body; the answer is the raw audio
// (X-Microsoft-OutputFormat). The host injects the key into Ocp-Apim-Subscription-Key; this plugin never sees it.
// The audio arrives only as a host file handle (responseType 'file'): the plugin returns that handle and never reads
// the bytes. Non-2xx answers are small bounded error bodies (JSON or text), never audio.
// `ctx.config.baseUrl` lets a test point this at a local server; production derives the origin from `region`
// (the host computes the same origin for the grant). One HTTP attempt per Invoke: the host owns retry.
const DEFAULT_REGION = 'eastus';
const OUTPUT_FORMAT = 'audio-24khz-48kbitrate-mono-mp3';
// Azure limits one request to 10 minutes of audio; 5000 characters of plain text stays far inside that.
const MAX_CHARS = 5000;

// Canonical BCP-47 code -> a long-standing neural voice. Any voice from voices() can be configured instead.
const DEFAULT_VOICES = {
  'zh-Hans': 'zh-CN-XiaoxiaoNeural', zh: 'zh-CN-XiaoxiaoNeural', 'zh-CN': 'zh-CN-XiaoxiaoNeural',
  'zh-Hant': 'zh-TW-HsiaoChenNeural', 'zh-TW': 'zh-TW-HsiaoChenNeural', 'zh-HK': 'zh-HK-HiuMaanNeural',
  en: 'en-US-JennyNeural', 'en-US': 'en-US-JennyNeural', 'en-GB': 'en-GB-SoniaNeural',
  ja: 'ja-JP-NanamiNeural', ko: 'ko-KR-SunHiNeural', fr: 'fr-FR-DeniseNeural', de: 'de-DE-KatjaNeural',
  es: 'es-ES-ElviraNeural', it: 'it-IT-ElsaNeural', pt: 'pt-BR-FranciscaNeural', ru: 'ru-RU-SvetlanaNeural',
  vi: 'vi-VN-HoaiMyNeural', th: 'th-TH-PremwadeeNeural', id: 'id-ID-GadisNeural', ar: 'ar-SA-ZariyahNeural',
  hi: 'hi-IN-SwaraNeural', tr: 'tr-TR-EmelNeural', ms: 'ms-MY-YasminNeural',
};

function baseUrl(cfg) {
  if (cfg.baseUrl) return String(cfg.baseUrl).replace(/\/+$/, '');
  const region = cfg.region || DEFAULT_REGION;
  if (typeof region !== 'string' || !/^[a-z0-9]{1,32}$/.test(region)) throw new PluginError('bad_response', `invalid region '${region}'`);
  return `https://${region}.tts.speech.microsoft.com`;
}

const KEY = [{ target: { area: 'header', name: 'Ocp-Apim-Subscription-Key' }, parts: [{ secret: 'apiKey' }] }];

function voiceFor(req, cfg) {
  const voice = req.voice || cfg.voice;
  if (voice) {
    if (!/^[A-Za-z]{2,3}-[A-Za-z0-9]{2,8}(-[A-Za-z0-9]+)*$/.test(voice)) throw new PluginError('bad_response', `invalid voice '${voice}'`);
    return voice;
  }
  const lang = req.lang && req.lang !== 'auto' ? req.lang : 'en';
  const mapped = DEFAULT_VOICES[lang] || DEFAULT_VOICES[String(lang).split('-')[0]];
  if (!mapped) throw new PluginError('unsupported_language', `no default Azure voice for '${lang}'; choose a voice in Settings`);
  return mapped;
}

function xmlEscape(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;' }[c]));
}

function header(headers, name) {
  if (!headers) return undefined;
  const lower = name.toLowerCase();
  for (const key of Object.keys(headers)) if (key.toLowerCase() === lower) return headers[key];
  return undefined;
}

function statusError(status, headers, body) {
  const text = typeof body === 'string' ? body : body ? JSON.stringify(body) : '';
  const detail = `http ${status}${text ? `: ${text.slice(0, 200)}` : ''}`;
  if (status === 401 || status === 403) return new PluginError('auth', detail);
  if (status === 429) return new PluginError('rate_limited', detail, header(headers, 'retry-after'));
  if (status === 408 || status === 504) return new PluginError('timeout', detail);
  if (status >= 500 && status <= 599) return new PluginError('network', detail);
  return new PluginError('bad_response', detail);
}

export default {
  async tts(req, ctx) {
    const text = req && req.text;
    if (typeof text !== 'string' || text.trim().length === 0) throw new PluginError('bad_response', 'empty text');
    const chars = Array.from(text).length;
    if (chars > MAX_CHARS) throw new PluginError('bad_response', `text too long for Azure TTS (${chars} > ${MAX_CHARS} characters)`);
    const cfg = ctx.config || {};
    const voice = voiceFor(req, cfg);
    const locale = voice.split('-').slice(0, 2).join('-');
    const rate = Math.min(2, Math.max(0.5, Number(req.rate) || 1));
    const percent = Math.round((rate - 1) * 100);
    const ssml = `<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="${xmlEscape(locale)}">`
      + `<voice name="${xmlEscape(voice)}"><prosody rate="${percent >= 0 ? '+' : ''}${percent}%">${xmlEscape(text)}</prosody></voice></speak>`;
    const r = await ctx.$http({
      method: 'POST',
      url: `${baseUrl(cfg)}/cognitiveservices/v1`,
      headers: {
        'Ocp-Apim-Subscription-Key': '',
        'Content-Type': 'application/ssml+xml',
        'X-Microsoft-OutputFormat': OUTPUT_FORMAT,
        'User-Agent': 'Su-Su',
      },
      credentials: KEY,
      body: { kind: 'text', value: ssml },
      responseType: 'file',
    });
    if (r.status < 200 || r.status >= 300) throw statusError(r.status, r.headers, r.body);
    const audio = r.body;
    if (!audio || typeof audio !== 'object' || typeof audio.id !== 'string') throw new PluginError('bad_response', 'no audio in the response');
    if (!/^audio\//i.test(String(audio.mime || ''))) throw new PluginError('bad_response', `response is not audio (${audio.mime})`);
    return { audio };
  },

  async voices(req, ctx) {
    const cfg = ctx.config || {};
    const r = await ctx.$http({
      method: 'GET',
      url: `${baseUrl(cfg)}/cognitiveservices/voices/list`,
      headers: { 'Ocp-Apim-Subscription-Key': '' },
      credentials: KEY,
    });
    if (r.status < 200 || r.status >= 300) throw statusError(r.status, r.headers, r.body);
    if (!Array.isArray(r.body)) throw new PluginError('bad_response', 'voice list is not an array');
    return r.body
      .filter((v) => v && typeof v.ShortName === 'string')
      .map((v) => ({ id: v.ShortName, name: String(v.LocalName || v.DisplayName || v.ShortName), lang: String(v.Locale || '') }));
  },
};
