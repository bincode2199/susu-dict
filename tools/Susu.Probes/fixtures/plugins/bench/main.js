// F00 synthetic benchmark plugin. Identical source is used for every engine route.
import { languages, toResult } from './lib/shape.js';

export default {
  async translate(req, ctx) {
    const response = await ctx.$http({
      method: 'POST',
      url: 'https://api.bench.example/translate',
      headers: { 'Content-Type': 'application/json' },
      body: { kind: 'json', value: { q: req.text, source: languages.en, target: languages['zh-Hans'] } },
    });
    return toResult(response);
  },
  // Waits on a host API call; cancellation must reject it and abort ctx.signal.
  async wait(req, ctx) {
    const aborted = new Promise((_, reject) => ctx.signal.addEventListener('abort', () => reject(ctx.signal.reason)));
    await Promise.race([ctx.$http({ method: 'GET', url: 'https://api.bench.example/slow' }), aborted]);
    return { text: 'late' };
  },
  async spin() { for (;;) {} },
  async microtasks() { for (;;) await Promise.resolve(); },
  async parse(req) {
    let total = 0;
    for (const item of req.items) total += item.text.length;
    return { count: req.items.length, total };
  },
  async noop() { return {}; },
  async store(req, ctx) {
    await ctx.$store.set('k', { v: req.v });
    return await ctx.$store.get('k');
  },
};
