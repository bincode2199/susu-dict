// Su-Su built-in package (F10.1, DEV-PLAN P-S03): Tencent Cloud TTS TextToVoice, API 3.0
// (https://cloud.tencent.com/document/api/1073/37995). Signed by the host's named `tencent-tc3` signer (PLAN 4.5.3):
// this plugin never sees SecretId/SecretKey; it reserves the Authorization/X-TC-Timestamp headers empty for the signer.
// The same Tencent Cloud account can be shared with Tencent Translator/OCR (PLAN 1.3).
// The answer is JSON with the audio as Base64 in Response.Audio. The host decodes that field into a file
// (responseFiles, PLAN 4.5.1): the plugin gets a handle and the JSON with Audio set to null, never the Base64 (B03).
// Most failures come back as HTTP 200 + Response.Error; the host hands back that subtree (errorPointer).
// `ctx.config.baseUrl` lets a test point this at a local server. One HTTP attempt per Invoke: the host owns retry.
const DEFAULT_BASE_URL = 'https://tts.tencentcloudapi.com';
const ACTION = 'TextToVoice';
const VERSION = '2019-08-23';
// TextToVoice: at most 150 Chinese characters (full-width punctuation counts as one) or 500 English letters.
const MAX_CJK_CHARS = 150;
const MAX_CHARS = 500;

// Basic voices of TextToVoice (https://cloud.tencent.com/document/product/1073/92668). PrimaryLanguage 1 = Chinese, 2 = English.
const VOICES = [
  { id: '101001', name: '智瑜 (female)', lang: 'zh-CN' },
  { id: '101002', name: '智聆 (female)', lang: 'zh-CN' },
  { id: '101003', name: '智美 (female)', lang: 'zh-CN' },
  { id: '101004', name: '智云 (male)', lang: 'zh-CN' },
  { id: '101005', name: '智莉 (female)', lang: 'zh-CN' },
  { id: '101050', name: 'WeJack (male)', lang: 'en-US' },
  { id: '101051', name: 'WeRose (female)', lang: 'en-US' },
];

function primaryLanguage(lang) {
  const primary = String(lang || 'zh').split('-')[0];
  if (primary === 'zh') return 1;
  if (primary === 'en') return 2;
  throw new PluginError('unsupported_language', `Tencent TTS speaks only Chinese and English, not '${lang}'`);
}

// Speed is [-2, 6]: -2 = 0.6x, -1 = 0.8x, 0 = 1.0x, 1 = 1.2x, 2 = 1.5x, 6 = 2.5x (decimals allowed); interpolate between.
const SPEED_POINTS = [[0.6, -2], [0.8, -1], [1.0, 0], [1.2, 1], [1.5, 2], [2.5, 6]];
function speed(rate) {
  const r = Math.min(2, Math.max(0.6, Number(rate) || 1));
  for (let i = 1; i < SPEED_POINTS.length; i++) {
    const [x1, y1] = SPEED_POINTS[i - 1];
    const [x2, y2] = SPEED_POINTS[i];
    if (r <= x2) return Math.round((y1 + ((r - x1) * (y2 - y1)) / (x2 - x1)) * 100) / 100;
  }
  return 6;
}

function vendorError(code, message) {
  const detail = message ? `${code}: ${String(message).slice(0, 200)}` : code;
  if (code.indexOf('AuthFailure') === 0 || code === 'UnauthorizedOperation' || code.indexOf('UnauthorizedOperation.') === 0
    || code === 'UnsupportedOperation.ServerNotOpen' || code === 'FailedOperation.UserNotRegistered')
    return new PluginError('auth', detail);
  if (code === 'FailedOperation.NoFreeAmount' || code === 'FailedOperation.ServiceIsolate' || code === 'FailedOperation.InsufficientBalance'
    || code === 'UnsupportedOperation.AccountArrears' || code === 'UnsupportedOperation.ServerDestroy' || code === 'UnsupportedOperation.ServerStopped'
    || code === 'ResourceUnavailable.ServiceIsolate' || code === 'FailedOperation.ServerNotOpen')
    return new PluginError('quota', detail);
  if (code === 'RequestLimitExceeded' || code.indexOf('LimitExceeded') === 0 || code.indexOf('RequestLimitExceeded') === 0)
    return new PluginError('rate_limited', detail);
  if (code === 'UnsupportedOperation.TextTooLong' || code === 'InvalidParameterValue.TextTooLong') return new PluginError('bad_response', detail);
  if (code.indexOf('InternalError') === 0 || code === 'ServiceUnavailable' || code === 'FailedOperation.ServerError' || code === 'FailedOperation.RequestTimeout')
    return new PluginError('network', detail);
  return new PluginError('bad_response', detail);
}

function header(headers, name) {
  if (!headers) return undefined;
  const lower = name.toLowerCase();
  for (const key of Object.keys(headers)) if (key.toLowerCase() === lower) return headers[key];
  return undefined;
}

function statusError(status, headers) {
  if (status === 401 || status === 403) return new PluginError('auth', `http ${status}`);
  if (status === 429) return new PluginError('rate_limited', `http ${status}`, header(headers, 'retry-after'));
  if (status >= 500 && status <= 599) return new PluginError('network', `http ${status}`);
  return new PluginError('bad_response', `http ${status}`);
}

function sessionId() {
  let s = 'susu-';
  for (let i = 0; i < 24; i++) s += '0123456789abcdef'[Math.floor(Math.random() * 16)];
  return s;
}

export default {
  async tts(req, ctx) {
    const text = req && req.text;
    if (typeof text !== 'string' || text.trim().length === 0) throw new PluginError('bad_response', 'empty text');
    const chars = Array.from(text);
    const cjk = chars.some((c) => /[　-鿿＀-￯]/.test(c));
    const max = cjk ? MAX_CJK_CHARS : MAX_CHARS;
    if (chars.length > max) throw new PluginError('bad_response', `text too long for Tencent TTS (${chars.length} > ${max} characters)`);
    const cfg = ctx.config || {};
    const configured = req.voice || cfg.voice || '';
    let voice = configured ? VOICES.find((v) => v.id === String(configured)) : null;
    if (configured && !/^\d{1,12}$/.test(String(configured))) throw new PluginError('bad_response', `invalid voice '${configured}'`);
    const lang = voice ? voice.lang : req.lang && req.lang !== 'auto' ? req.lang : cjk ? 'zh' : 'en';
    const primary = primaryLanguage(lang);
    const voiceType = configured ? Number(configured) : primary === 2 ? 101051 : 101001;

    const headers = {
      'Content-Type': 'application/json',
      'X-TC-Action': ACTION,
      'X-TC-Version': VERSION,
      'X-TC-Timestamp': '',
      Authorization: '',
    };
    if (cfg.region) {
      if (typeof cfg.region !== 'string' || !/^[a-z]{2}-[a-z]+(-[a-z0-9]+)*$/.test(cfg.region)) throw new PluginError('bad_response', `invalid region '${cfg.region}'`);
      headers['X-TC-Region'] = cfg.region;
    }
    const r = await ctx.$http({
      method: 'POST',
      url: `${cfg.baseUrl || DEFAULT_BASE_URL}/`,
      headers,
      body: {
        kind: 'json',
        value: { Text: text, SessionId: sessionId(), Speed: speed(req.rate), VoiceType: voiceType, PrimaryLanguage: primary, SampleRate: 16000, Codec: 'mp3', ModelType: 1 },
      },
      sign: { scheme: 'tencent-tc3', service: 'tts' },
      responseType: 'json',
      responseFiles: [{ name: 'audio', pointer: '/Response/Audio', mime: 'audio/mpeg' }],
      errorPointer: '/Response/Error',
    });
    if (r.status < 200 || r.status >= 300) throw statusError(r.status, r.headers);
    const audio = r.files && r.files.audio;
    if (audio && typeof audio.id === 'string') return { audio };
    // 2xx business error: the body is the Response.Error subtree ({ Code, Message }).
    if (r.body && typeof r.body === 'object' && r.body.Code) throw vendorError(String(r.body.Code), r.body.Message);
    throw new PluginError('bad_response', 'no audio in the response');
  },

  async voices() {
    return VOICES;
  },
};
