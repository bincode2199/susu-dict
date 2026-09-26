// Su-Su built-in package (F11.2, DEV-PLAN P-O02): SimpleTex ("Simple LaTeX") open platform
// (https://doc.simpletex.cn/en/api/). The image goes up as a multipart `file` field that the host builds from the
// leased file handle (PLAN 4.5.1); this plugin never sees the bytes. The User Access Token (UAT) is the local secret
// `apiKey`, injected by the host into the reserved `token` header (PLAN 4.5.2 explicit credentials, no signer).
// Models: `simpletex_ocr` (general: text and formulas, default), `latex_ocr` and `latex_ocr_turbo` (one formula).
// Responses: { status: true, res: { latex, conf } } for the formula models, { status: true, res: { type, info, conf } }
// for simpletex_ocr (type "formula" -> info is LaTeX; otherwise info is Markdown text with inline $...$ formulas);
// failures carry status false plus err_info, or an HTTP error status.
// `ctx.config.baseUrl` lets a test point this at a local server. One HTTP attempt per Invoke.
const DEFAULT_BASE_URL = 'https://server.simpletex.cn';
const MODES = ['simpletex_ocr', 'latex_ocr', 'latex_ocr_turbo'];
// SimpleTex rejects oversized images (image_oversize); keep uploads within 10 MiB.
const MAX_IMAGE_BYTES = 10 * 1024 * 1024;

function header(headers, name) {
  if (!headers) return undefined;
  const lower = name.toLowerCase();
  for (const key of Object.keys(headers)) if (key.toLowerCase() === lower) return headers[key];
  return undefined;
}

function errorCode(body) {
  if (!body || typeof body !== 'object') return '';
  const info = body.err_info;
  if (typeof info === 'string') return info;
  if (info && typeof info === 'object') return String(info.err_type || info.err_code || info.code || info.err_msg || '');
  return String(body.errType || body.err_type || body.message || '');
}

function classify(status, headers, body) {
  const code = errorCode(body);
  const detail = `http ${status}${code ? ` ${code.slice(0, 200)}` : ''}`;
  const c = code.toLowerCase();
  if (status === 401 || status === 403 || /unauthori[sz]ed|token|auth|uat/.test(c)) return new PluginError('auth', detail);
  if (status === 402 || /money|balance|resource_not_enough|resource_no_valid|quota|arrear/.test(c)) return new PluginError('quota', detail);
  if (status === 429 || /qps|ccy|rate|limit|frequen/.test(c)) return new PluginError('rate_limited', detail, header(headers, 'retry-after'));
  if (status === 504 || /timeout/.test(c)) return new PluginError('timeout', detail);
  if ((status >= 500 && status <= 599) || /server|sever|inner|closed|unavailable/.test(c)) return new PluginError('network', detail);
  return new PluginError('bad_response', detail);
}

const clamp01 = v => Math.min(1, Math.max(0, v));

function block(text, kind, conf) {
  const b = { text, kind };
  if (Number.isFinite(conf)) b.confidence = clamp01(conf);
  return b;
}

export default {
  async ocr(req, ctx) {
    const image = req && req.image;
    if (!image || typeof image.id !== 'string' || image.id.length === 0) throw new PluginError('bad_response', 'image handle required');
    if (Number(image.bytes) > MAX_IMAGE_BYTES) throw new PluginError('bad_response', `image too large for SimpleTex (${image.bytes} > ${MAX_IMAGE_BYTES} bytes)`);
    const cfg = ctx.config || {};
    const baseUrl = cfg.baseUrl || DEFAULT_BASE_URL;
    const mode = cfg.mode || 'simpletex_ocr';
    if (MODES.indexOf(mode) < 0) throw new PluginError('bad_response', `unknown model '${mode}'`);

    const fields = [{ name: 'file', filename: 'image.png', contentType: 'image/png', file: image.id }];
    if (mode === 'simpletex_ocr') fields.push({ name: 'rec_mode', text: 'auto' });
    const r = await ctx.$http({
      method: 'POST',
      url: `${baseUrl}/api/${mode}`,
      headers: { token: '' },
      credentials: [{ target: { area: 'header', name: 'token' }, parts: [{ secret: 'apiKey' }] }],
      body: { kind: 'multipart', fields },
    });
    if (r.status < 200 || r.status >= 300) throw classify(r.status, r.headers, r.body);
    const body = r.body;
    if (!body || typeof body !== 'object') throw new PluginError('bad_response', 'response is not JSON');
    if (body.status !== true) throw classify(r.status, r.headers, body);
    const res = body.res;
    if (!res || typeof res !== 'object') throw new PluginError('bad_response', 'missing res');

    if (mode !== 'simpletex_ocr') {
      if (typeof res.latex !== 'string') throw new PluginError('bad_response', 'missing res.latex');
      return { blocks: res.latex.trim().length === 0 ? [] : [block(res.latex, 'formula', res.conf)] };
    }
    const info = typeof res.info === 'string' ? res.info
      : res.info && typeof res.info === 'object' && typeof res.info.markdown === 'string' ? res.info.markdown : null;
    if (info === null) throw new PluginError('bad_response', 'missing res.info');
    if (info.trim().length === 0) return { blocks: [] };
    return { blocks: [block(info, res.type === 'formula' ? 'formula' : 'text', res.conf)] };
  },
};
