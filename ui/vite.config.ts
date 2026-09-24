import { fileURLToPath, URL } from 'node:url';
import { defineConfig } from 'vitest/config';
import vue from '@vitejs/plugin-vue';
import type { Plugin } from 'vite';

// The dev server injects styles inline and uses a websocket for reloads; relax the CSP for `vite serve` only.
// Production output keeps index.html's strict policy unchanged.
const devCsp: Plugin = {
  name: 'susu-dev-csp',
  apply: 'serve',
  transformIndexHtml: (html) => html.replace("style-src 'self'", "style-src 'self' 'unsafe-inline'").replace("connect-src 'none'", "connect-src 'self' ws:"),
};

// production (default): the Su-Su UI loaded by susu.exe from its ui/ folder (ARCHITECTURE 9).
// probe: the F00 synthetic eight-window fixture used by the feasibility measurements.
export default defineConfig(({ mode }) => {
  const probe = mode === 'probe';
  return {
    root: probe ? fileURLToPath(new URL('./probe', import.meta.url)) : undefined,
    plugins: [vue(), devCsp],
    base: './',
    resolve: { alias: { '@protocol': fileURLToPath(new URL('../protocol/generated', import.meta.url)) } },
    build: {
      outDir: probe ? fileURLToPath(new URL('./dist-probe', import.meta.url)) : 'dist',
      emptyOutDir: true,
      sourcemap: false,
      target: 'es2022',
      modulePreload: { polyfill: false }, // the polyfill injects an inline script, which the CSP forbids
    },
    server: { strictPort: true },
    test: { environment: 'happy-dom', include: ['tests/**/*.test.ts'] },
  };
});
