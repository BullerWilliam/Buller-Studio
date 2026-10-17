const { app, BrowserWindow } = require('electron');
const path = require('node:path');
app.whenReady().then(() => {
  const window = new BrowserWindow({ width: 1280, height: 800, backgroundColor: '#101522', webPreferences: { nodeIntegration: true, contextIsolation: false } });
  window.loadFile(path.join(__dirname, 'game.html'));
});
