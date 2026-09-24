// Su-Su built-in package (F06.1, DEV-PLAN P-T01): MyMemory's free, keyless translate endpoint - the
// default translation service (BuiltInCatalog: enabled out of the box, no account/secret needed).
// `ctx.config.baseUrl` lets a test point this at a local server instead of the real vendor; production
// never sets it, so the default below is what actually ships.
const DEFAULT_BASE_URL = 'https://api.mymemory.translated.net';

// MyMemory expects its own language tags, not Su-Su's canonical BCP-47 codes (PluginProvider: "manifest-
// declared pairs are an F05/F06 adapter concern" - the vendor mapping lives in the adapter, not the host).
const LANG_MAP = { 'zh-Hans': 'zh-CN' };
function vendorLang(code) { return LANG_MAP[code] || code || 'en'; }

export default {
  async translate(req, ctx) {
    const text = req && req.text;
    if (typeof text !== 'string' || text.length === 0) throw new PluginError('bad_response', 'empty text');
    const baseUrl = (ctx.config && ctx.config.baseUrl) || DEFAULT_BASE_URL;
    const langpair = `${vendorLang(req.from)}|${vendorLang(req.to)}`;
    const url = `${baseUrl}/get?q=${encodeURIComponent(text)}&langpair=${encodeURIComponent(langpair)}`;
    const r = await ctx.$http({ method: 'GET', url });
    if (r.status === 429) throw new PluginError('rate_limited', `http ${r.status}`);
    if (r.status !== 200) throw new PluginError('bad_response', `http ${r.status}`);
    const body = r.body || {};
    const status = typeof body.responseStatus === 'string' ? parseInt(body.responseStatus, 10) : body.responseStatus;
    const translated = body.responseData && body.responseData.translatedText;
    // The free endpoint answers HTTP 200 with a quota message inside the body instead of a real error
    // status once the shared daily allowance is used up.
    if (status === 403 || (typeof translated === 'string' && translated.indexOf('MYMEMORY WARNING') === 0))
      throw new PluginError('quota', 'MyMemory daily quota exceeded');
    if (status !== 200 || typeof translated !== 'string') throw new PluginError('bad_response', `unexpected MyMemory response (status ${body.responseStatus})`);
    return { text: translated };
  },
};
