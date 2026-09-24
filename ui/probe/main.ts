import { createApp } from 'vue';
import App from './App.vue';
import './styles/tokens.css';

createApp(App).mount('#app');

// F00 PER03/PER04 probe signals (fixture only): report the first painted frame after mount and a
// painted frame after each native "shown" notification. Two rAFs place the callback after a paint.
interface ProbeBridge {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (event: MessageEvent) => void): void;
}
const bridge = (window as unknown as { chrome?: { webview?: ProbeBridge } }).chrome?.webview;
const afterPaint = (callback: () => void) => requestAnimationFrame(() => requestAnimationFrame(callback));
if (bridge) {
  afterPaint(() => bridge.postMessage({ type: 'first-frame' }));
  bridge.addEventListener('message', (event) => {
    const data = event.data as { type?: string; seq?: number } | undefined;
    if (data?.type === 'shown') afterPaint(() => bridge.postMessage({ type: 'shown-frame', seq: data.seq, dom: document.querySelector('#app')?.children.length ?? 0 }));
  });
}
