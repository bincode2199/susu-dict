import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

export default defineConfig(({ mode }) => {
  if (mode !== 'probe' && mode !== 'development') throw new Error('Production packaging is unavailable before the F03 gate.');
  return { plugins: [vue()], base: './', build: { outDir: 'dist', sourcemap: true }, server: { strictPort: true } };
});
