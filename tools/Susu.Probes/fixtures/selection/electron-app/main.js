// SEL01 fixture: loads the shared synthetic page; no network, no node integration.
const { app, BrowserWindow } = require('electron');
const path = require('path');
app.whenReady().then(() => {
  const win = new BrowserWindow({ width: 640, height: 320, x: 100, y: 100, title: 'Su-Su SEL fixture Electron ' + process.versions.electron,
    webPreferences: { nodeIntegration: false, contextIsolation: true, sandbox: true } });
  win.loadFile(path.join(__dirname, '..', 'page.html'));
  win.on('page-title-updated', (e) => e.preventDefault());
});
app.on('window-all-closed', () => app.quit());
