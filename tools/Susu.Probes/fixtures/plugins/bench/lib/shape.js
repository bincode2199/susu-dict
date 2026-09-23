// F00 synthetic fixture: helpers imported through the restricted module loader.
export const languages = Object.freeze({ en: 'EN', 'zh-Hans': 'ZH' });
export function toResult(response) {
  if (!response || response.status !== 200 || !response.body || typeof response.body.text !== 'string') {
    throw new PluginError('bad_response', 'unexpected response shape');
  }
  return { text: response.body.text };
}
