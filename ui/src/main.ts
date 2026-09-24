import { createApp, h } from 'vue';
import { UI_COMMANDS, type WindowView } from '@protocol/ui';
import { Bridge, windowFromQuery, type HostPort } from './bridge/bridge';
import { createStore } from './bridge/store';
import { setLocale } from './locales/i18n';
import App from './App.vue';
import './styles/tokens.css';
import './styles/base.css';

const { kind, session, language } = windowFromQuery(location.search);
setLocale(language);

function applyWindow(view: WindowView): void {
  setLocale(view.uiLanguage);
  document.documentElement.dataset.theme = view.theme === 'dark' ? 'dark' : 'light';
}

async function hostPort(): Promise<HostPort | null> {
  const webview = (window as unknown as { chrome?: { webview?: HostPort } }).chrome?.webview;
  if (webview) return webview;
  if (import.meta.env.DEV) return (await import('./bridge/devHost')).createDevHost(kind, session, language);
  return null;
}

void hostPort().then((host) => {
  const { state, inbound } = createStore(applyWindow);
  if (!host) return; // opened outside Su-Su: nothing to talk to
  // After every snapshot (first load and each warm reopen) report the first painted frame: two animation
  // frames place the callback after a paint (PER03 timing point, local only).
  const onSnapshot = inbound.onSnapshot;
  let bridge: Bridge;
  inbound.onSnapshot = (payload) => {
    onSnapshot(payload);
    const started = performance.now();
    const whenContent = () => {
      // Cold opens load the window chunk first; count frames only once its root is in the DOM.
      if (document.querySelector('#app .window, #app .menu') || performance.now() - started > 5000) requestAnimationFrame(() => requestAnimationFrame(() => void bridge.command(UI_COMMANDS.Painted)));
      else requestAnimationFrame(whenContent);
    };
    whenContent();
  };
  bridge = new Bridge(host, session, inbound);
  createApp({ render: () => h(App, { kind, bridge, state }) }).mount('#app');
  bridge.ready();
});
