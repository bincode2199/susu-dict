// Su-Su built-in package (F15.3, DEV-PLAN P-V02): Eudic (欧路词典) OpenAPI study list, https://api.frdic.com.
//   vocab   -> lookup: GET  /api/open/v1/studylist/word/{word}?language=xx   (200 with data = found, 404 = absent)
//              upsert: lookup first, then POST /api/open/v1/studylist/word {language, word, category_ids?}
//   options -> field `vocabList`: GET /api/open/v1/studylist/category?language=en
// The Authorization value (the whole string Eudic shows, e.g. "NIS ...") is the local secret `apiKey`; this plugin never sees it.
// Eudic's study list is keyed by word and adding a word again is harmless, but the API has no idempotency key or operation id and
// returns no id: a lost response is therefore confirmed by the host's lookup (the word being in the list is the goal), never by
// resending. As in the Anki package, once the POST is on the wire any transport failure, 5xx or unreadable answer is returned as
// {status:'unknown'}; everything before it is a read and fails with an ordinary PluginError the host may retry.
// `ctx.config.baseUrl` lets a test point at a local server. One attempt per call: the host owns retries (ARCHITECTURE 5.1).
const DEFAULT_BASE_URL = 'https://api.frdic.com';
const LANGUAGES = ['en', 'fr', 'de', 'es'];
const MAX_WORD = 128;
const PAGE_SIZE = 200;

function header(headers, name) {
  if (!headers) return undefined;
  const lower = name.toLowerCase();
  for (const key of Object.keys(headers)) if (key.toLowerCase() === lower) return headers[key];
  return undefined;
}

function statusError(status, headers) {
  const detail = `http ${status}`;
  if (status === 401 || status === 403) return new PluginError('auth', detail);
  if (status === 402) return new PluginError('quota', detail);
  if (status === 429) return new PluginError('rate_limited', detail, header(headers, 'retry-after'));
  if (status === 408 || status === 504) return new PluginError('timeout', detail);
  if (status >= 500 && status <= 599) return new PluginError('network', detail);
  return new PluginError('bad_response', detail);
}

function transport(e) { return e && (e.kind === 'network' || e.kind === 'timeout'); }

function languageOf(lang) {
  const primary = typeof lang === 'string' ? lang.split('-')[0].toLowerCase() : '';
  if (!LANGUAGES.includes(primary)) throw new PluginError('unsupported_language', `Eudic does not take '${lang}'`);
  return primary;
}

function call(ctx, method, path, query, body) {
  const base = (ctx.config && ctx.config.baseUrl) || DEFAULT_BASE_URL;
  const qs = Object.keys(query || {}).map((k) => `${encodeURIComponent(k)}=${encodeURIComponent(query[k])}`).join('&');
  const req = {
    method,
    url: `${base}${path}${qs ? `?${qs}` : ''}`,
    headers: { Authorization: '', ...(body ? { 'Content-Type': 'application/json' } : {}) },
    credentials: [{ target: { area: 'header', name: 'Authorization' }, parts: [{ secret: 'apiKey' }] }],
  };
  if (body) req.body = { kind: 'json', value: body };
  return ctx.$http(req);
}

/** GET lookup: true when the word is in the user's study list. */
async function lookup(ctx, word, language) {
  const r = await call(ctx, 'GET', `/api/open/v1/studylist/word/${encodeURIComponent(word)}`, { language });
  if (r.status === 404) return false;
  if (r.status < 200 || r.status >= 300) throw statusError(r.status, r.headers);
  const data = r.body && typeof r.body === 'object' ? r.body.data : null;
  if (r.body === null || typeof r.body !== 'object') throw new PluginError('bad_response', 'lookup answer is not JSON');
  return data !== null && data !== undefined && !(Array.isArray(data) && data.length === 0);
}

function checkRequest(req) {
  if (!req || typeof req !== 'object') throw new PluginError('bad_response', 'request required');
  if (req.action !== 'upsert' && req.action !== 'lookup') throw new PluginError('bad_response', `unknown action '${req.action}'`);
  if (typeof req.word !== 'string' || req.word.trim().length === 0 || req.word.length > MAX_WORD) throw new PluginError('bad_response', 'word required (at most 128 characters)');
  if (typeof req.operationId !== 'string' || req.operationId.length === 0) throw new PluginError('bad_response', 'operationId required');
}

export default {
  async options(req, ctx) {
    if (!req || req.field !== 'vocabList') throw new PluginError('bad_response', `no options for field ${req && req.field}`);
    const r = await call(ctx, 'GET', '/api/open/v1/studylist/category', { language: 'en' });
    if (r.status < 200 || r.status >= 300) throw statusError(r.status, r.headers);
    const data = r.body && Array.isArray(r.body.data) ? r.body.data : null;
    if (!data) throw new PluginError('bad_response', 'missing data[]');
    const lists = data.filter((c) => c && (typeof c.id === 'string' || typeof c.id === 'number') && typeof c.name === 'string' && c.name.length > 0);
    const start = req.cursor ? Number(req.cursor) : 0;
    if (!Number.isInteger(start) || start < 0 || start > lists.length) throw new PluginError('bad_response', 'bad cursor');
    return {
      items: lists.slice(start, start + PAGE_SIZE).map((c) => ({ value: String(c.id), label: c.name })),
      nextCursor: start + PAGE_SIZE < lists.length ? String(start + PAGE_SIZE) : undefined,
    };
  },

  async vocab(req, ctx) {
    checkRequest(req);
    const language = languageOf(req.lang);
    const word = req.word.trim();
    const listId = ctx.config && ctx.config.vocabList ? String(ctx.config.vocabList) : '';
    const remoteId = `${language}:${word}`;

    const present = await lookup(ctx, word, language);
    if (req.action === 'lookup') return present ? { status: 'found', remoteId } : { status: 'absent' };
    // Already in the list and no particular list asked for: nothing to write.
    if (present && listId === '') return { status: 'found', remoteId };

    const body = { language, word };
    if (listId !== '') body.category_ids = [/^[0-9]+$/.test(listId) ? Number(listId) : listId];
    let r;
    try { r = await call(ctx, 'POST', '/api/open/v1/studylist/word', {}, body); }
    catch (e) {
      if (transport(e)) return { status: 'unknown' };
      throw e;
    }
    if (r.status === 401 || r.status === 402 || r.status === 403 || r.status === 429) throw statusError(r.status, r.headers);
    if (r.status >= 500 || r.status === 408) return { status: 'unknown' };
    if (r.status < 200 || r.status >= 300) throw new PluginError('bad_response', `http ${r.status}`);
    return { status: 'applied', remoteId };
  },
};
