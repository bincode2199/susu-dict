<script setup lang="ts">
// DESIGN 6: 16 px canvas, 1.5 px round stroke, fill none, currentColor. Drawn in-house; no icon library.
const paths = {
  close: 'M4.5 4.5l7 7M11.5 4.5l-7 7',
  minimize: 'M3.5 8h9',
  maximize: 'M4.5 3.5h7a1 1 0 0 1 1 1v7a1 1 0 0 1-1 1h-7a1 1 0 0 1-1-1v-7a1 1 0 0 1 1-1z',
  pin: 'M6 2.5h4M7 2.5v4L4.5 9h7L9 6.5v-4M8 9v4.5',
  settings: 'M8 5.75a2.25 2.25 0 1 1 0 4.5 2.25 2.25 0 0 1 0-4.5zM8 1.75v1.75M8 12.5v1.75M1.75 8H3.5M12.5 8h1.75M3.6 3.6l1.2 1.2M11.2 11.2l1.2 1.2M3.6 12.4l1.2-1.2M11.2 4.8l1.2-1.2',
  chevronDown: 'M4.5 6.5L8 10l3.5-3.5',
  chevronRight: 'M6.5 4.5L10 8l-3.5 3.5',
  chevronUp: 'M4.5 9.5L8 6l3.5 3.5',
  swap: 'M3 5.5h9.5L10 3M13 10.5H3.5L6 13',
  copy: 'M6.5 5.5h6a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1h-6a1 1 0 0 1-1-1v-6a1 1 0 0 1 1-1zM10.5 3.5v-.25a.75.75 0 0 0-.75-.75H3.25a.75.75 0 0 0-.75.75v6.5c0 .41.34.75.75.75h.25',
  retry: 'M13 8a5 5 0 1 1-1.46-3.54M13 2.5v3h-3',
  info: 'M8 14A6 6 0 1 0 8 2a6 6 0 0 0 0 12zM8 7.25v3.5M8 5v.01',
  warning: 'M8 2.5l6 10.5H2L8 2.5zM8 6.75v2.75M8 11.5v.01',
  stop: 'M5 5h6v6H5z',
  check: 'M3.5 8.5l3 3 6-7',
  clear: 'M3 4.5h10M6.5 4.5v-2h3v2M4.5 4.5l.75 9h5.5l.75-9',
  eye: 'M1.5 8S4 3.5 8 3.5 14.5 8 14.5 8 12 12.5 8 12.5 1.5 8 1.5 8zM8 6a2 2 0 1 1 0 4 2 2 0 0 1 0-4z',
  eyeOff: 'M1.5 8S4 3.5 8 3.5 14.5 8 14.5 8 12 12.5 8 12.5 1.5 8 1.5 8zM8 6a2 2 0 1 1 0 4 2 2 0 0 1 0-4zM2.5 2.5l11 11',
  input: 'M2.5 4h11a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1h-11a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1zM4.5 6.75h.01M7 6.75h.01M9.5 6.75h.01M12 6.75h.01M5 9.5h6',
  clipboard: 'M5.5 3H4.5a1 1 0 0 0-1 1v9a1 1 0 0 0 1 1h7a1 1 0 0 0 1-1V4a1 1 0 0 0-1-1h-1M6 2h4v2H6z',
  ocr: 'M2.5 5.5v-3h3M10.5 2.5h3v3M13.5 10.5v3h-3M5.5 13.5h-3v-3M5.5 8h5',
  mic: 'M8 2a2 2 0 0 1 2 2v4a2 2 0 0 1-4 0V4a2 2 0 0 1 2-2zM4 7.5a4 4 0 0 0 8 0M8 11.5v2.5',
  audio: 'M2.5 6H5l3-2.5v9L5 10H2.5zM10.5 6a3 3 0 0 1 0 4M12.5 4a6 6 0 0 1 0 8',
  video: 'M2.5 3.5h7a1 1 0 0 1 1 1v7a1 1 0 0 1-1 1h-7a1 1 0 0 1-1-1v-7a1 1 0 0 1 1-1zM10.5 7l4-2v6l-4-2',
  update: 'M8 2.5v7M5 6.5l3 3 3-3M3 13.5h10',
  exit: 'M6.5 13.5h-3a1 1 0 0 1-1-1v-9a1 1 0 0 1 1-1h3M10.5 11l3-3-3-3M13.5 8h-7',
  globe: 'M8 1.5a6.5 6.5 0 1 1 0 13 6.5 6.5 0 0 1 0-13zM1.5 8h13M8 1.5c2 2 2 11 0 13M8 1.5c-2 2-2 11 0 13',
  grid: 'M2.5 2.5h4v4h-4zM9.5 2.5h4v4h-4zM2.5 9.5h4v4h-4zM9.5 9.5h4v4h-4z',
  prompt: 'M2.5 3.5h11a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1H7l-3 2.5v-2.5H2.5a1 1 0 0 1-1-1v-6a1 1 0 0 1 1-1zM4.5 6.5h7M4.5 8.5h4.5',
  sparkle: 'M8 2l1.5 4.5L14 8l-4.5 1.5L8 14l-1.5-4.5L2 8l4.5-1.5z',
  book: 'M3 3.5a1 1 0 0 1 1-1h7.5v10H4a1 1 0 0 0-1 1zM3 13.5a1 1 0 0 1 1-1h7.5v1',
  plus: 'M8 3.5v9M3.5 8h9',
  minus: 'M3.5 8h9',
  star: 'M8 2.25l1.8 3.65 4.03.59-2.92 2.84.69 4.01L8 11.44l-3.6 1.9.69-4.01-2.92-2.84 4.03-.59z',
  keyboard: 'M2.5 4h11a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1h-11a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1zM4.5 6.75h.01M7 6.75h.01M9.5 6.75h.01M12 6.75h.01M5 9.5h6',
} as const;

export type IconName = keyof typeof paths;
defineProps<{ name: IconName; size?: number }>();
</script>

<template>
  <svg :width="size ?? 16" :height="size ?? 16" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">
    <path :d="paths[name]" />
  </svg>
</template>
