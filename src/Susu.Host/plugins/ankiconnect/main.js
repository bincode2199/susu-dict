// Su-Su built-in package (F15.3, DEV-PLAN P-V01): Anki through AnkiConnect (API v6, a local add-on, default http://127.0.0.1:8765).
//   vocab   -> upsert: ensure the "Su-Su" note type and the deck exist, find the note by its stable SusuId field, then
//              updateNoteFields (found) or addNote (absent); lookup: findNotes only. No remote delete exists in this package.
//   options -> field `deck`: deckNames.
// Idempotency (ARCHITECTURE 8.3): every note carries the entry id in its SusuId field and the lookup is by that field, so a
// request whose response was lost is confirmed by the host's lookup and never added twice. The operationId rides along as a tag.
// Write safety: everything before the final addNote/updateNoteFields call is read-only or idempotent, so a failure there is a
// thrown PluginError (the host may retry: nothing was written). Once the write request is on the wire any transport failure,
// 5xx or unreadable answer is returned as {status:'unknown'}: the host then marks the delivery Uncertain and confirms by lookup.
// The optional API key (`useApiKey`) is written by the host into the request's `key` field; this plugin never sees it.
// `ctx.config.baseUrl` lets a test point at a local server. One attempt per call: the host owns retries (ARCHITECTURE 5.1).
const DEFAULT_BASE_URL = 'http://127.0.0.1:8765';
const DEFAULT_DECK = 'Su-Su';
const MODEL = 'Su-Su';
const FIELDS = ['Word', 'Phonetic', 'Definition', 'Example', 'Source', 'SusuId'];
const MAX_FIELD = 20000;
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
  if (status === 429) return new PluginError('rate_limited', detail, header(headers, 'retry-after'));
  if (status === 408 || status === 504) return new PluginError('timeout', detail);
  if (status >= 500 && status <= 599) return new PluginError('network', detail);
  return new PluginError('bad_response', detail);
}

function transport(e) { return e && (e.kind === 'network' || e.kind === 'timeout'); }

function request(ctx, action, params) {
  const cfg = ctx.config || {};
  const body = { action, version: 6 };
  if (params !== undefined) body.params = params;
  const req = {
    method: 'POST',
    url: `${cfg.baseUrl || DEFAULT_BASE_URL}/`,
    headers: { 'Content-Type': 'application/json' },
    body: { kind: 'json', value: body },
  };
  if (cfg.useApiKey === 'true' || cfg.useApiKey === true) {
    body.key = null; // reserved: the host fills the secret in
    req.credentials = [{ target: { area: 'json', pointer: '/key' }, parts: [{ secret: 'apiKey' }] }];
  }
  return req;
}

function answerError(message) {
  const text = String(message);
  if (/api key|apikey|permission/i.test(text)) return new PluginError('auth', text.slice(0, 120));
  return new PluginError('bad_response', text.slice(0, 120));
}

/** A read-only or idempotent call: any failure is a normal PluginError. */
async function read(ctx, action, params) {
  const r = await ctx.$http(request(ctx, action, params));
  if (r.status < 200 || r.status >= 300) throw statusError(r.status, r.headers);
  const body = r.body;
  if (!body || typeof body !== 'object' || !('error' in body) || !('result' in body)) throw new PluginError('bad_response', 'not an AnkiConnect answer');
  if (body.error !== null && body.error !== undefined) throw answerError(body.error);
  return body.result;
}

/**
 * The call that changes notes. Returns {unknown} when the request may have been applied, {error} for an AnkiConnect refusal
 * (nothing applied), {result} on success. A refused/unauthorised/rate-limited request throws: the vendor did not process it.
 */
async function write(ctx, action, params) {
  let r;
  try { r = await ctx.$http(request(ctx, action, params)); }
  catch (e) {
    if (transport(e)) return { unknown: `${action}: ${e.kind}` };
    throw e;
  }
  if (r.status === 401 || r.status === 403 || r.status === 429) throw statusError(r.status, r.headers);
  if (r.status < 200 || r.status >= 300) return { unknown: `${action}: http ${r.status}` };
  const body = r.body;
  if (!body || typeof body !== 'object' || !('error' in body) || !('result' in body)) return { unknown: `${action}: unreadable answer` };
  if (body.error !== null && body.error !== undefined) {
    if (/api key|apikey|permission/i.test(String(body.error))) throw answerError(body.error);
    return { error: String(body.error) };
  }
  return { result: body.result };
}

function html(text) {
  return String(text).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/\n/g, '<br>');
}

function str(v) { return typeof v === 'string' ? v : ''; }

/** The card text of a vocab projection ({phonetics, meanings, examples, source}), mirroring the host's own exporters. */
function cardFields(req) {
  const c = req.content && typeof req.content === 'object' ? req.content : {};
  const phonetic = (Array.isArray(c.phonetics) ? c.phonetics : []).map((p) => {
    if (typeof p === 'string') return p;
    const ipa = str(p && p.ipa);
    if (!ipa) return '';
    const accent = str(p.accent);
    return (accent ? `${accent} ` : '') + (ipa.startsWith('/') || ipa.startsWith('[') ? ipa : `/${ipa}/`);
  }).filter(Boolean).join('  ');
  const definition = (Array.isArray(c.meanings) ? c.meanings : []).map((m) => {
    if (typeof m === 'string') return m;
    const means = Array.isArray(m && m.means) ? m.means.filter((x) => typeof x === 'string' && x).join('; ') : '';
    if (!means) return '';
    const pos = str(m.pos);
    return (pos ? `${pos} ` : '') + means;
  }).filter(Boolean).join('\n');
  const example = (Array.isArray(c.examples) ? c.examples : []).map((e) => {
    if (typeof e === 'string') return e;
    const a = str(e && e.src), b = str(e && e.dst);
    return a && b ? `${a} - ${b}` : a + b;
  }).filter(Boolean).join('\n');
  const cut = (t) => (t.length > MAX_FIELD ? t.slice(0, MAX_FIELD) : t);
  return {
    Word: html(cut(req.word)), Phonetic: html(cut(phonetic)), Definition: html(cut(definition)), Example: html(cut(example)),
    Source: html(cut(str(c.source) || 'Su-Su')), SusuId: req.entryId,
  };
}

function checkRequest(req) {
  if (!req || typeof req !== 'object') throw new PluginError('bad_response', 'request required');
  if (req.action !== 'upsert' && req.action !== 'lookup') throw new PluginError('bad_response', `unknown action '${req.action}'`);
  if (typeof req.entryId !== 'string' || !/^[A-Za-z0-9_-]{1,64}$/.test(req.entryId)) throw new PluginError('bad_response', 'entryId required');
  if (typeof req.word !== 'string' || req.word.trim().length === 0) throw new PluginError('bad_response', 'word required');
  if (typeof req.operationId !== 'string' || req.operationId.length === 0) throw new PluginError('bad_response', 'operationId required');
}

async function findByEntry(ctx, entryId) {
  const ids = await read(ctx, 'findNotes', { query: `"note:${MODEL}" SusuId:${entryId}` });
  if (!Array.isArray(ids)) throw new PluginError('bad_response', 'findNotes: not a list');
  return ids;
}

async function ensureModel(ctx) {
  const names = await read(ctx, 'modelNames');
  if (Array.isArray(names) && names.includes(MODEL)) return;
  await read(ctx, 'createModel', {
    modelName: MODEL,
    inOrderFields: FIELDS,
    css: '.card{font-family:sans-serif;font-size:18px;text-align:center}.p{color:#666}.s{color:#999;font-size:12px}',
    cardTemplates: [{
      Name: 'Card 1',
      Front: '<div class="w">{{Word}}</div>{{#Phonetic}}<div class="p">{{Phonetic}}</div>{{/Phonetic}}',
      Back: '{{FrontSide}}<hr id=answer>{{Definition}}{{#Example}}<div class="e">{{Example}}</div>{{/Example}}<div class="s">{{Source}}</div>',
    }],
  });
}

export default {
  async options(req, ctx) {
    if (!req || req.field !== 'deck') throw new PluginError('bad_response', `no options for field ${req && req.field}`);
    const names = await read(ctx, 'deckNames');
    if (!Array.isArray(names)) throw new PluginError('bad_response', 'deckNames: not a list');
    const decks = [...new Set(names.filter((n) => typeof n === 'string' && n.length > 0))].sort();
    const start = req.cursor ? Number(req.cursor) : 0;
    if (!Number.isInteger(start) || start < 0 || start > decks.length) throw new PluginError('bad_response', 'bad cursor');
    const page = decks.slice(start, start + PAGE_SIZE);
    return { items: page.map((d) => ({ value: d, label: d })), nextCursor: start + PAGE_SIZE < decks.length ? String(start + PAGE_SIZE) : undefined };
  },

  async vocab(req, ctx) {
    checkRequest(req);
    const deck = (ctx.config && ctx.config.deck) || DEFAULT_DECK;
    const existing = await findByEntry(ctx, req.entryId);
    if (req.action === 'lookup') return existing.length > 0 ? { status: 'found', remoteId: String(existing[0]) } : { status: 'absent' };

    const fields = cardFields(req);
    if (existing.length > 0) {
      const id = existing[0];
      const w = await write(ctx, 'updateNoteFields', { note: { id, fields } });
      if (w.unknown) return { status: 'unknown' };
      if (w.error) throw new PluginError('bad_response', w.error.slice(0, 120));
      return { status: 'applied', remoteId: String(id) };
    }

    await ensureModel(ctx);
    await read(ctx, 'createDeck', { deck });
    const w = await write(ctx, 'addNote', {
      note: { deckName: deck, modelName: MODEL, fields, options: { allowDuplicate: false, duplicateScope: 'deck' }, tags: ['susu', `susu-op-${req.operationId}`.slice(0, 60)] },
    });
    if (w.unknown) return { status: 'unknown' };
    if (w.error) {
      if (/duplicate/i.test(w.error)) {
        // The same word is already in the deck (added by hand, or a write that landed after all): never add a second note.
        let again = [];
        try { again = await findByEntry(ctx, req.entryId); } catch (e) { again = []; }
        return again.length > 0 ? { status: 'found', remoteId: String(again[0]) } : { status: 'found' };
      }
      throw new PluginError('bad_response', w.error.slice(0, 120));
    }
    if (typeof w.result !== 'number' && typeof w.result !== 'string') return { status: 'unknown' };
    return { status: 'applied', remoteId: String(w.result) };
  },
};
