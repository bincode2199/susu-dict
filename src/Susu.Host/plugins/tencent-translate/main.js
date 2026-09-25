// Su-Su built-in package (F06.2b, DEV-PLAN P-T02): Tencent Cloud Machine Translation (TMT) TextTranslate,
// API 3.0 (https://cloud.tencent.com/document/api/551/15619). The request is signed by the host's
// named `tencent-tc3` signer (PLAN 4.5.3, Susu.Net TencentTc3Signer): this plugin never sees SecretId or
// SecretKey, it only reserves the Authorization/X-TC-Timestamp headers empty for the signer to fill.
// The secrets use the fixed local names secretId/secretKey, so one Tencent Cloud account can be shared
// with Tencent OCR/TTS once the user links it to each instance (PLAN 1.3, DESIGN credential binding).
// `ctx.config.baseUrl` lets a test point this at a local server instead of the real vendor; production
// never sets it, so DEFAULT_BASE_URL is what actually ships. One HTTP attempt per Invoke: the host
// (JobCoordinator/TranslationSession) owns retry and the shared deadline (ARCHITECTURE 5.1).
const DEFAULT_BASE_URL = 'https://tmt.tencentcloudapi.com';
const DEFAULT_REGION = 'ap-guangzhou';
const ACTION = 'TextTranslate';
const VERSION = '2018-03-21';
// TextTranslate: "单次请求的文本长度需要低于6000字符" - counted here in Unicode code points.
const MAX_CHARS = 5999;

// Canonical BCP-47 code -> TMT code. TMT's own codes for the languages Su-Su's host table can emit
// today plus the common ones, so a wider host table later needs no plugin change for these.
const LANG_MAP = {
  'zh-Hans': 'zh', 'zh-CN': 'zh', zh: 'zh', 'zh-Hant': 'zh-TW', 'zh-TW': 'zh-TW',
  en: 'en', ja: 'ja', ko: 'ko', fr: 'fr', es: 'es', it: 'it', de: 'de', tr: 'tr', ru: 'ru', pt: 'pt',
  vi: 'vi', id: 'id', th: 'th', ms: 'ms', ar: 'ar', hi: 'hi',
};
// TMT code -> canonical, for detectedFrom (only codes the host table knows).
const CANONICAL = { zh: 'zh-Hans', en: 'en' };

function vendorLang(code, allowAuto) {
  if (!code || code === 'auto') {
    if (allowAuto) return 'auto';
    throw new PluginError('unsupported_language', 'target language required');
  }
  const mapped = LANG_MAP[code] || LANG_MAP[String(code).split('-')[0]];
  if (!mapped) throw new PluginError('unsupported_language', `Tencent TMT does not support '${code}'`);
  return mapped;
}

function region(cfg) {
  const r = cfg.region || DEFAULT_REGION;
  if (typeof r !== 'string' || !/^[a-z]{2}-[a-z]+(-[a-z0-9]+)*$/.test(r)) throw new PluginError('bad_response', `invalid region '${r}'`);
  return r;
}

// J05: TMT answers most failures as HTTP 200 with Response.Error.Code (API 3.0 common + TMT codes).
function vendorError(code, message) {
  const detail = message ? `${code}: ${String(message).slice(0, 200)}` : code;
  if (code.indexOf('AuthFailure') === 0 || code === 'FailedOperation.UserNotRegistered' || code === 'UnauthorizedOperation')
    return new PluginError('auth', detail);
  if (code === 'FailedOperation.NoFreeAmount' || code === 'FailedOperation.ServiceIsolate' || code === 'FailedOperation.StopUsing'
    || code === 'ResourceUnavailable.ServiceIsolate' || code === 'FailedOperation.InsufficientBalance')
    return new PluginError('quota', detail);
  if (code === 'RequestLimitExceeded' || code.indexOf('LimitExceeded') === 0 || code.indexOf('RequestLimitExceeded') === 0)
    return new PluginError('rate_limited', detail);
  // UnsupportedOperation.UnsupportedLanguage / UnSupportedTargetLanguage / UnsupportedSourceLanguage
  // (TMT spells the "S" both ways); UnsupportedOperation.TextTooLong stays bad_response below.
  if (/^UnsupportedOperation\.Un[sS]upported.*Language$/.test(code))
    return new PluginError('unsupported_language', detail);
  if (code.indexOf('InternalError') === 0 || code === 'ServiceUnavailable' || code === 'FailedOperation.RequestAiLabErr')
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

export default {
  async translate(req, ctx) {
    const text = req && req.text;
    if (typeof text !== 'string' || text.length === 0) throw new PluginError('bad_response', 'empty text');
    const chars = Array.from(text).length;
    if (chars > MAX_CHARS) throw new PluginError('bad_response', `text too long for Tencent TMT (${chars} > ${MAX_CHARS} characters)`);
    const cfg = ctx.config || {};
    const baseUrl = cfg.baseUrl || DEFAULT_BASE_URL;
    const source = vendorLang(req.from, true);
    const target = vendorLang(req.to || 'en', false);
    const projectId = Number.isInteger(Number(cfg.projectId)) ? Number(cfg.projectId) : 0;

    const r = await ctx.$http({
      method: 'POST',
      url: `${baseUrl}/`,
      headers: {
        'Content-Type': 'application/json',
        'X-TC-Action': ACTION,
        'X-TC-Version': VERSION,
        'X-TC-Region': region(cfg),
        'X-TC-Timestamp': '',
        Authorization: '',
      },
      body: { kind: 'json', value: { SourceText: text, Source: source, Target: target, ProjectId: projectId } },
      sign: { scheme: 'tencent-tc3', service: 'tmt' },
    });
    if (r.status < 200 || r.status >= 300) throw statusError(r.status, r.headers);
    const response = r.body && r.body.Response;
    if (!response || typeof response !== 'object') throw new PluginError('bad_response', 'missing Response');
    if (response.Error) throw vendorError(String(response.Error.Code || 'Unknown'), response.Error.Message);
    if (typeof response.TargetText !== 'string') throw new PluginError('bad_response', 'missing TargetText');
    const result = { text: response.TargetText };
    const detected = CANONICAL[response.Source];
    if (source === 'auto' && detected) result.detectedFrom = detected;
    return result;
  },
};
