// Su-Su built-in package (F09.1, DEV-PLAN P-T07): Youdao (有道智云) text translation and dictionary.
//   translate  -> POST https://openapi.youdao.com/api      (ai.youdao.com/DOCSIRMA/html/trans/api/wbfy)
//   dictionary -> POST https://openapi.youdao.com/v2/dict  (ai.youdao.com/DOCSIRMA/html/dictionary/api/ydcd)
// Since the text-translation API's v3.0.0 (2024-04-22) /api no longer returns dictionary data, so the
// dictionary capability uses the separate dictionary endpoint. Youdao is Su-Su's only dictionary source
// (PLAN 1.2/5); nothing here invents an entry when the vendor has none.
//
// Signing (PLAN 4.5.3 `digest`): sign = sha256(appKey + input + salt + curtime + appSecret), signType=v3,
// input = q when q.length <= 20, else first 10 chars + q.length + last 10 chars. The truncation, salt and
// curtime are public, so this plugin builds them as literal parts; the host's digest primitive resolves the
// appKey/appSecret secret parts and writes the hex digest into the reserved `sign` query field. This plugin
// never sees appSecret (or appKey, which the host also injects into the reserved `appKey` query field).
// Lengths use JavaScript string length (UTF-16 code units), matching Youdao's Java demo.
//
// Transport: the control fields (appKey, sign, salt, curtime, signType, languages) travel in the query,
// because host credential/signature targets are header/query/json only (PLAN 4.5.2); q travels in the
// form-encoded body so long text does not end up in the URL. Youdao documents "GET/POST, 表单" for both.
//
// `ctx.config.baseUrl` lets a test point this at a local server; production never sets it. One HTTP
// attempt per Invoke: the host owns retry and the shared deadline (ARCHITECTURE 5.1).
const DEFAULT_BASE_URL = 'https://openapi.youdao.com';
// 文本翻译 "单次查询最大字符数 5000" - counted here in Unicode code points (TranslationPackages uses the same unit).
const MAX_CHARS = 5000;
// PLAN 6.1 word forms are at most 32 Latin characters or 4 Han characters; allow some slack, refuse text.
const MAX_WORD_CHARS = 64;

// Result caps: a hostile or broken response cannot make the card arbitrarily large.
const MAX_STRING = 400;
const MAX_PARTS = 24;
const MAX_MEANS = 16;
const MAX_FORMS = 16;
const MAX_EXAMPLES = 8;

// Canonical BCP-47 code -> Youdao code.
const LANG_MAP = {
  'zh-Hans': 'zh-CHS', 'zh-CN': 'zh-CHS', zh: 'zh-CHS', 'zh-Hant': 'zh-CHT', 'zh-TW': 'zh-CHT',
  en: 'en', ja: 'ja', ko: 'ko', fr: 'fr', es: 'es', it: 'it', de: 'de', ru: 'ru', pt: 'pt', nl: 'nl',
  vi: 'vi', id: 'id', th: 'th', ar: 'ar', hi: 'hi', tr: 'tr', ms: 'ms', pl: 'pl', uk: 'uk',
};
// Youdao code -> canonical, for detectedFrom (only codes the host table knows).
const CANONICAL = { 'zh-CHS': 'zh-Hans', en: 'en' };

function vendorLang(code, allowAuto) {
  if (!code || code === 'auto') {
    if (allowAuto) return 'auto';
    throw new PluginError('unsupported_language', 'target language required');
  }
  const mapped = LANG_MAP[code] || LANG_MAP[String(code).split('-')[0]];
  if (!mapped) throw new PluginError('unsupported_language', `Youdao does not support '${code}'`);
  return mapped;
}

/** Youdao v3 `input`: q itself up to 20 characters, else first 10 + length + last 10. */
function truncate(q) {
  const len = q.length;
  return len <= 20 ? q : q.substring(0, 10) + len + q.substring(len - 10, len);
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

// J05: Youdao answers failures as HTTP 200 with a non-zero errorCode (both endpoints share the table).
const AUTH_CODES = ['108', '110', '111', '202', '203', '205', '206'];
const NETWORK_CODES = ['301', '302', '303', '304'];
function vendorError(code) {
  const detail = `youdao errorCode ${code}`;
  if (AUTH_CODES.indexOf(code) >= 0) return new PluginError('auth', detail);
  if (code === '401') return new PluginError('quota', detail);
  if (code === '411' || code === '412') return new PluginError('rate_limited', detail);
  if (code === '102') return new PluginError('unsupported_language', detail);
  if (NETWORK_CODES.indexOf(code) >= 0) return new PluginError('network', detail);
  return new PluginError('bad_response', detail); // 101/103/113/207/390001/... and anything unknown
}

function errorCodeOf(body) {
  if (!body || typeof body !== 'object' || Array.isArray(body)) throw new PluginError('bad_response', 'missing JSON body');
  const code = body.errorCode;
  if (typeof code !== 'string' && typeof code !== 'number') throw new PluginError('bad_response', 'missing errorCode');
  return String(code);
}

/** application/x-www-form-urlencoded (the sandbox has no URLSearchParams). */
function form(fields) {
  return Object.keys(fields).map(k => `${encodeURIComponent(k)}=${encodeURIComponent(fields[k])}`).join('&');
}

/** Youdao's salt only has to be unique per request (anti-replay with curtime), not secret; the sandbox has no
 *  `crypto`, so this is a random UUID-v4-shaped string from Math.random. */
function uuid() {
  let s = '';
  for (let i = 0; i < 32; i++) {
    let d = Math.floor(Math.random() * 16);
    if (i === 12) d = 4;
    else if (i === 16) d = 8 + (d & 3);
    s += d.toString(16);
    if (i === 7 || i === 11 || i === 15 || i === 19) s += '-';
  }
  return s;
}

/** One signed Youdao call: `params` go in the query next to the reserved appKey/sign fields, q in the body. */
async function signedPost(ctx, path, q, params) {
  const cfg = ctx.config || {};
  const baseUrl = cfg.baseUrl || DEFAULT_BASE_URL;
  const salt = uuid();
  const curtime = String(Math.floor(Date.now() / 1000));
  const fields = Object.assign({}, params, { salt, curtime, signType: 'v3' });
  // Reserved empty: the host fills appKey by credential injection and sign by the digest primitive.
  const url = `${baseUrl}${path}?${form(fields)}&appKey=&sign=`;
  const r = await ctx.$http({
    method: 'POST',
    url,
    headers: { 'Content-Type': 'application/x-www-form-urlencoded; charset=utf-8' },
    body: { kind: 'text', value: form({ q }) },
    credentials: [{ target: { area: 'query', name: 'appKey' }, parts: [{ secret: 'appKey' }] }],
    sign: {
      scheme: 'digest',
      alg: 'sha256',
      input: [{ secret: 'appKey' }, { literal: truncate(q) + salt + curtime }, { secret: 'appSecret' }],
      into: { area: 'query', name: 'sign' },
      encoding: 'hex',
    },
  });
  if (r.status < 200 || r.status >= 300) throw statusError(r.status, r.headers);
  return r.body;
}

// ---- dictionary mapping ----

function str(value) {
  if (typeof value !== 'string') return undefined;
  const t = value.trim();
  return t.length === 0 ? undefined : t.slice(0, MAX_STRING);
}

function isObject(value) { return value !== null && typeof value === 'object' && !Array.isArray(value); }

function first(obj, names) {
  for (const n of names) if (obj && obj[n] !== undefined) return obj[n];
  return undefined;
}

/** Audio links are data only (F09.3 authorizes any download); only an absolute https URL is kept. */
function audio(value) {
  const s = str(value);
  return s && /^https:\/\/[^\s]+$/i.test(s) ? s : undefined;
}

// Documented /v2/dict shape: result -> <dict code> (ec/ce/...) -> basic{usPhonetic, ukPhonetic, usSpeech,
// ukSpeech, explains, wordFormats}, sentenceSample[]. The exact nesting is not published as a sample, so the
// entry is located tolerantly; the legacy /api `basic{us-phonetic, explains, wfs}` names are accepted too.
const ENTRY_FIELDS = ['basic', 'explains', 'usPhonetic', 'ukPhonetic', 'us-phonetic', 'uk-phonetic', 'wordFormats', 'wfs', 'sentenceSample'];

function looksLikeEntry(obj) { return isObject(obj) && ENTRY_FIELDS.some(f => obj[f] !== undefined); }

function locateEntry(node, dict, depth) {
  if (depth > 4 || node === null || typeof node !== 'object') return undefined;
  if (Array.isArray(node)) {
    for (const item of node.slice(0, 8)) {
      const found = locateEntry(item, dict, depth + 1);
      if (found) return found;
    }
    return undefined;
  }
  if (looksLikeEntry(node)) return node;
  for (const key of [dict, 'word', 'result']) {
    if (node[key] !== undefined) {
      const found = locateEntry(node[key], dict, depth + 1);
      if (found) return found;
    }
  }
  return undefined;
}

function splitMeans(text) {
  return text.split(/\s*[;；]\s*/).map(str).filter(Boolean).slice(0, MAX_MEANS);
}

function parseExplain(item) {
  if (typeof item === 'string') {
    const s = item.trim().slice(0, 4 * MAX_STRING); // each meaning is capped by splitMeans
    if (!s) return undefined;
    // "n. 好处；善行", "vt. & vi. 跑", "adj. 好的": leading English part-of-speech abbreviation(s).
    const m = /^((?:[a-z]{1,6}\.\s*(?:&\s*)?)+)\s*(.*)$/i.exec(s);
    if (m && m[2]) return { pos: m[1].trim(), means: splitMeans(m[2]) };
    return { pos: '', means: splitMeans(s) };
  }
  if (isObject(item)) {
    const pos = str(first(item, ['pos', 'type'])) || '';
    const raw = first(item, ['explain', 'trans', 'tran', 'means', 'text']);
    let means = [];
    if (Array.isArray(raw)) means = raw.map(str).filter(Boolean).slice(0, MAX_MEANS);
    else if (typeof raw === 'string') means = splitMeans(raw);
    return means.length ? { pos, means } : undefined;
  }
  return undefined;
}

function mapParts(basic) {
  const explains = first(basic, ['explains', 'explain', 'trs']);
  if (!Array.isArray(explains)) return [];
  const parts = [];
  for (const item of explains) {
    const part = parseExplain(item);
    if (part && part.means.length) parts.push(part);
    if (parts.length >= MAX_PARTS) break;
  }
  return parts;
}

function mapPhonetics(basic) {
  const phonetics = [];
  for (const accent of ['us', 'uk']) {
    const ipa = str(first(basic, [`${accent}Phonetic`, `${accent}-phonetic`]));
    if (!ipa) continue;
    const p = { accent, ipa };
    const url = audio(first(basic, [`${accent}Speech`, `${accent}-speech`]));
    if (url) p.audioUrl = url;
    phonetics.push(p);
  }
  return phonetics;
}

function mapForms(basic) {
  const list = first(basic, ['wordFormats', 'wfs']);
  if (!Array.isArray(list)) return [];
  const forms = [];
  for (const item of list) {
    const wf = isObject(item) && isObject(item.wf) ? item.wf : item;
    if (!isObject(wf)) continue;
    const name = str(wf.name), value = str(wf.value);
    if (name && value) forms.push({ name, value });
    if (forms.length >= MAX_FORMS) break;
  }
  return forms;
}

function mapExamples(entry) {
  const list = first(entry, ['sentenceSample', 'sentences', 'examples']);
  if (!Array.isArray(list)) return [];
  const examples = [];
  for (const item of list) {
    if (!isObject(item)) continue;
    const src = str(first(item, ['sentence', 'src'])), dst = str(first(item, ['translation', 'sentenceTranslation', 'dst']));
    if (src && dst) examples.push({ src, dst });
    if (examples.length >= MAX_EXAMPLES) break;
  }
  return examples;
}

/** A legal empty entry: no phonetics, parts, forms or examples (DICT01 falls back to plain translation). */
function emptyEntry(word) { return { word, phonetics: [], parts: [] }; }

function mapEntry(word, entry) {
  if (!entry) return emptyEntry(word);
  const basic = isObject(entry.basic) ? Object.assign({}, entry, entry.basic) : entry;
  const result = { word, phonetics: mapPhonetics(basic), parts: mapParts(basic) };
  const forms = mapForms(basic);
  if (forms.length) result.forms = forms;
  const examples = mapExamples(entry);
  if (examples.length) result.examples = examples;
  return result;
}

// Same Han ranges as Susu.Domain TextForms/Languages.
const HAN = /^[㐀-䶿一-鿿豈-﫿\u{20000}-\u{2ebef}]+$/u;

export default {
  async translate(req, ctx) {
    const text = req && req.text;
    if (typeof text !== 'string' || text.length === 0) throw new PluginError('bad_response', 'empty text');
    const chars = Array.from(text).length;
    if (chars > MAX_CHARS) throw new PluginError('bad_response', `text too long for Youdao (${chars} > ${MAX_CHARS} characters)`);
    const from = vendorLang(req.from, true);
    const to = vendorLang(req.to || 'en', false);
    // strict=true: translate into exactly the target the host chose (false lets Youdao flip zh<->en itself).
    const body = await signedPost(ctx, '/api', text, { from, to, strict: 'true' });
    const code = errorCodeOf(body);
    if (code !== '0') throw vendorError(code);
    const translation = body.translation;
    if (!Array.isArray(translation) || translation.length === 0 || translation.some(t => typeof t !== 'string'))
      throw new PluginError('bad_response', 'missing translation');
    const result = { text: translation.join('\n') };
    if (from === 'auto' && typeof body.l === 'string') {
      const detected = CANONICAL[body.l.split('2')[0]];
      if (detected) result.detectedFrom = detected;
    }
    return result;
  },

  async dictionary(req, ctx) {
    const word = req && typeof req.word === 'string' ? req.word.trim() : '';
    if (word.length === 0) throw new PluginError('bad_response', 'empty word');
    if (word.length > MAX_WORD_CHARS) throw new PluginError('bad_response', 'not a word');
    // Han word -> 汉英 (ce, langType zh-CHS); anything else -> 英汉 (ec, langType en).
    const han = HAN.test(word);
    const dict = han ? 'ce' : 'ec';
    const body = await signedPost(ctx, '/v2/dict', word, { langType: han ? 'zh-CHS' : 'en', dicts: dict, docType: 'json' });
    const code = errorCodeOf(body);
    // 120 "不是词，或未收录": a legal empty entry, not a failure.
    if (code === '120') return emptyEntry(word);
    if (code !== '0') throw vendorError(code);
    return mapEntry(word, locateEntry(body.result !== undefined ? body.result : body, dict, 0));
  },
};
