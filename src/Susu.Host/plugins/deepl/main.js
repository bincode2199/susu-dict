// Su-Su built-in package (F06.2b, DEV-PLAN P-T03): DeepL API v2 /translate
// (https://developers.deepl.com/docs/api-reference/translate). The key goes in the Authorization header
// as "DeepL-Auth-Key <apiKey>", injected by the host from the bound account (PLAN 4.5.2) - this plugin
// never sees it, so it cannot look at the key's ":fx" (Free) suffix itself. The endpoint is therefore
// chosen by config: `plan: "free"` (api-free.deepl.com, the default) or `plan: "pro"` (api.deepl.com);
// the settings UI sets it from the ":fx" suffix when the user enters the key. `ctx.config.baseUrl`
// overrides both (tests point it at a local server). One HTTP attempt per Invoke: the host owns retry
// and the shared deadline (ARCHITECTURE 5.1).
const FREE_BASE_URL = 'https://api-free.deepl.com';
const PRO_BASE_URL = 'https://api.deepl.com';
// DeepL caps the whole request body at 128 KiB; keep headroom for the rest of the JSON request.
const MAX_BODY_BYTES = 128 * 1024 - 1024;

// Canonical BCP-47 code -> DeepL code. Source languages are bare (EN, ZH); some targets need a
// variant (EN-GB/EN-US, PT-PT/PT-BR, ZH-HANS/ZH-HANT) because the bare target codes are deprecated.
const TARGET_MAP = { en: 'EN-US', 'en-US': 'EN-US', 'en-GB': 'EN-GB', pt: 'PT-PT', 'pt-PT': 'PT-PT', 'pt-BR': 'PT-BR',
  zh: 'ZH-HANS', 'zh-Hans': 'ZH-HANS', 'zh-CN': 'ZH-HANS', 'zh-Hant': 'ZH-HANT', 'zh-TW': 'ZH-HANT' };
// DeepL detected_source_language -> canonical (only codes the host table knows).
const CANONICAL = { EN: 'en', ZH: 'zh-Hans' };

function primary(code) { return String(code).split('-')[0].toUpperCase(); }
function sourceLang(code) { return !code || code === 'auto' ? undefined : primary(code); }
function targetLang(code) {
  if (!code || code === 'auto') throw new PluginError('unsupported_language', 'target language required');
  return TARGET_MAP[code] || primary(code);
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

function message(body) {
  if (body && typeof body === 'object' && typeof body.message === 'string') return body.message;
  return typeof body === 'string' ? body : '';
}

// J05: DeepL's documented status codes (https://developers.deepl.com/docs/best-practices/error-handling).
// 403 is a bad/missing key or a Free key sent to the Pro endpoint (and vice versa - "Wrong endpoint");
// 456 is the monthly character quota; 429/529 are rate limits (Retry-After passed through raw for the
// host's RetryPolicy); 400 naming source_lang/target_lang is an unsupported language; 5xx is transient.
function statusError(status, headers, body) {
  const msg = message(body).slice(0, 200);
  const detail = msg ? `http ${status}: ${msg}` : `http ${status}`;
  if (status === 401 || status === 403) return new PluginError('auth', /wrong endpoint/i.test(msg) ? `${detail} (check the Free/Pro plan setting)` : detail);
  if (status === 456) return new PluginError('quota', detail);
  if (status === 429 || status === 529) return new PluginError('rate_limited', detail, header(headers, 'retry-after'));
  if (status === 400 && /(source_lang|target_lang)/i.test(msg)) return new PluginError('unsupported_language', detail);
  if (status >= 500 && status <= 599) return new PluginError('network', detail);
  return new PluginError('bad_response', detail);
}

export default {
  async translate(req, ctx) {
    const text = req && req.text;
    if (typeof text !== 'string' || text.length === 0) throw new PluginError('bad_response', 'empty text');
    const bytes = utf8Length(JSON.stringify(text));
    if (bytes > MAX_BODY_BYTES) throw new PluginError('bad_response', `text too long for DeepL (${bytes} > ${MAX_BODY_BYTES} bytes)`);
    const cfg = ctx.config || {};
    const baseUrl = cfg.baseUrl || (cfg.plan === 'pro' ? PRO_BASE_URL : FREE_BASE_URL);
    const value = { text: [text], target_lang: targetLang(req.to || 'en') };
    const source = sourceLang(req.from);
    if (source) value.source_lang = source;

    const r = await ctx.$http({
      method: 'POST',
      url: `${baseUrl}/v2/translate`,
      headers: { Authorization: '', 'Content-Type': 'application/json' },
      credentials: [{ target: { area: 'header', name: 'Authorization' }, parts: [{ literal: 'DeepL-Auth-Key ' }, { secret: 'apiKey' }] }],
      body: { kind: 'json', value },
    });
    if (r.status < 200 || r.status >= 300) throw statusError(r.status, r.headers, r.body);
    const first = r.body && Array.isArray(r.body.translations) ? r.body.translations[0] : undefined;
    if (!first || typeof first.text !== 'string') throw new PluginError('bad_response', 'missing translations[0].text');
    const result = { text: first.text };
    const detected = CANONICAL[first.detected_source_language];
    if (!source && detected) result.detectedFrom = detected;
    return result;
  },
};
