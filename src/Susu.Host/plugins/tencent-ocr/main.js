// Su-Su built-in package (F11.2, DEV-PLAN P-O01): Tencent Cloud OCR GeneralBasicOCR, API 3.0
// (https://cloud.tencent.com/document/api/866/33526). The image never passes through this plugin: the request
// reserves `ImageBase64: null` and names the host file handle in `bodyFiles`; the host reads the leased image,
// writes its standard Base64 into that field and then signs the final bytes with the named `tencent-tc3` signer
// (PLAN 4.5.1/4.5.2, B01). This plugin sees neither the image bytes, the Base64, nor SecretId/SecretKey.
// The secrets use the fixed local names secretId/secretKey, so the Tencent Cloud account shared with TMT/TTS
// is released here only under its own grant for ocr.tencentcloudapi.com (PLAN 1.3, 4.5.4).
// `ctx.config.baseUrl` lets a test point this at a local server; production never sets it. One HTTP attempt
// per Invoke: the host owns retry and deadlines (ARCHITECTURE 5.1).
const DEFAULT_BASE_URL = 'https://ocr.tencentcloudapi.com';
const DEFAULT_REGION = 'ap-guangzhou';
const ACTION = 'GeneralBasicOCR';
const VERSION = '2018-11-19';
// GeneralBasicOCR: "图片经Base64编码后不超过10M" - 7 MiB of raw image is 9.33 MiB of Base64.
const MAX_IMAGE_BYTES = 7 * 1024 * 1024;

// Vendor LanguageType codes the manifest offers.
const VENDOR_LANGS = ['auto', 'zh', 'jap', 'kor', 'spa', 'fre', 'ger', 'por', 'vie', 'may', 'rus', 'ita', 'hol', 'swe', 'fin', 'dan', 'nor', 'hun', 'tha', 'ara'];
// Canonical BCP-47 hint -> LanguageType ("zh" is the vendor's mixed Chinese/English model).
const LANG_MAP = {
  zh: 'zh', en: 'zh', ja: 'jap', ko: 'kor', es: 'spa', fr: 'fre', de: 'ger', pt: 'por', vi: 'vie', ms: 'may',
  ru: 'rus', it: 'ita', nl: 'hol', sv: 'swe', fi: 'fin', da: 'dan', no: 'nor', nb: 'nor', hu: 'hun', th: 'tha', ar: 'ara',
};

function languageType(reqLang, cfg) {
  if (reqLang && reqLang !== 'auto') {
    const mapped = LANG_MAP[String(reqLang).split('-')[0].toLowerCase()];
    if (!mapped) throw new PluginError('unsupported_language', `Tencent OCR does not read '${reqLang}'`);
    return mapped;
  }
  const configured = cfg.lang || 'auto';
  if (VENDOR_LANGS.indexOf(configured) < 0) throw new PluginError('unsupported_language', `unknown LanguageType '${configured}'`);
  return configured;
}

function region(cfg) {
  const r = cfg.region || DEFAULT_REGION;
  if (typeof r !== 'string' || !/^[a-z]{2}-[a-z]+(-[a-z0-9]+)*$/.test(r)) throw new PluginError('bad_response', `invalid region '${r}'`);
  return r;
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
  if (status === 504) return new PluginError('timeout', `http ${status}`);
  if (status >= 500 && status <= 599) return new PluginError('network', `http ${status}`);
  return new PluginError('bad_response', `http ${status}`);
}

// API 3.0 common + OCR error codes (HTTP 200 with Response.Error). FailedOperation.ImageNoText is not an
// error here: it becomes an empty block list, which the host reports as "no text recognized".
function vendorError(code, message) {
  const detail = message ? `${code}: ${String(message).slice(0, 200)}` : code;
  if (code.indexOf('AuthFailure') === 0 || code.indexOf('UnauthorizedOperation') === 0 || code === 'FailedOperation.UnOpenError')
    return new PluginError('auth', detail);
  if (/Arrears|ResourcePackageRunOut|ChargeStatusException|CountLimit|ResourcesSoldOut/.test(code))
    return new PluginError('quota', detail);
  if (code === 'LimitExceeded.TooLargeFileError') return new PluginError('bad_response', detail);
  if (code.indexOf('RequestLimitExceeded') === 0 || code.indexOf('LimitExceeded') === 0)
    return new PluginError('rate_limited', detail);
  if (code === 'FailedOperation.LanguageNotSupport') return new PluginError('unsupported_language', detail);
  if (code === 'FailedOperation.EngineRecognizeTimeout') return new PluginError('timeout', detail);
  if (code.indexOf('InternalError') === 0 || code === 'ServiceUnavailable' || code === 'FailedOperation.UnKnowError')
    return new PluginError('network', detail);
  // ImageDecodeFailed, ImageSizeTooLarge, OcrFailed, DownLoadError, InvalidParameter*, anything else.
  return new PluginError('bad_response', detail);
}

const clamp01 = v => Math.min(1, Math.max(0, v));

// Pixel rectangle -> [x, y, w, h] normalized to the image; null without a usable image size or rectangle.
function normalizedBox(d, width, height) {
  if (!(width > 0 && height > 0)) return null;
  let x, y, w, h;
  const r = d.ItemPolygon;
  if (r && [r.X, r.Y, r.Width, r.Height].every(Number.isFinite)) { x = r.X; y = r.Y; w = r.Width; h = r.Height; }
  else if (Array.isArray(d.Polygon) && d.Polygon.length > 0 && d.Polygon.every(p => p && Number.isFinite(p.X) && Number.isFinite(p.Y))) {
    const xs = d.Polygon.map(p => p.X), ys = d.Polygon.map(p => p.Y);
    x = Math.min(...xs); y = Math.min(...ys); w = Math.max(...xs) - x; h = Math.max(...ys) - y;
  } else return null;
  const nx = clamp01(x / width), ny = clamp01(y / height);
  return [nx, ny, Math.min(clamp01(w / width), 1 - nx), Math.min(clamp01(h / height), 1 - ny)];
}

export default {
  async ocr(req, ctx) {
    const image = req && req.image;
    if (!image || typeof image.id !== 'string' || image.id.length === 0) throw new PluginError('bad_response', 'image handle required');
    // Vendor limit first (PLAN 4.5.1); the host enforces its own 32 MiB / 48 MiB caps on the real bytes regardless.
    if (Number(image.bytes) > MAX_IMAGE_BYTES) throw new PluginError('bad_response', `image too large for Tencent OCR (${image.bytes} > ${MAX_IMAGE_BYTES} bytes)`);
    const cfg = ctx.config || {};
    const baseUrl = cfg.baseUrl || DEFAULT_BASE_URL;

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
      body: { kind: 'json', value: { ImageBase64: null, LanguageType: languageType(req.lang, cfg) } },
      bodyFiles: [{ pointer: '/ImageBase64', file: image.id, encoding: 'base64' }],
      sign: { scheme: 'tencent-tc3', service: 'ocr' },
    });
    if (r.status < 200 || r.status >= 300) throw statusError(r.status, r.headers);
    const response = r.body && r.body.Response;
    if (!response || typeof response !== 'object') throw new PluginError('bad_response', 'missing Response');
    if (response.Error) {
      const code = String(response.Error.Code || 'Unknown');
      if (code === 'FailedOperation.ImageNoText') return { blocks: [] };
      throw vendorError(code, response.Error.Message);
    }
    if (!Array.isArray(response.TextDetections)) throw new PluginError('bad_response', 'missing TextDetections');
    const blocks = [];
    for (const d of response.TextDetections) {
      if (!d || typeof d.DetectedText !== 'string') throw new PluginError('bad_response', 'TextDetections item without DetectedText');
      const block = { text: d.DetectedText, kind: 'text' };
      const box = normalizedBox(d, Number(image.width), Number(image.height));
      if (box) block.box = box;
      if (Number.isFinite(d.Confidence)) block.confidence = clamp01(d.Confidence / 100);
      blocks.push(block);
    }
    return { blocks };
  },
};
