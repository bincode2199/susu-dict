// Trusted relay between the native host and the sandboxed iframe. No plugin code runs here.
'use strict';
const frame = document.getElementById('sandbox');
let ready = false;
const queue = [];
window.chrome.webview.addEventListener('message', (event) => {
  if (ready) frame.contentWindow.postMessage(event.data, '*'); else queue.push(event.data);
});
window.addEventListener('message', (event) => {
  if (event.source !== frame.contentWindow) return;
  if (event.data && event.data.type === 'SandboxReady') {
    ready = true;
    for (const message of queue.splice(0)) frame.contentWindow.postMessage(message, '*');
  }
  window.chrome.webview.postMessage(event.data);
});
