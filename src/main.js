const { app, BrowserWindow, dialog, ipcMain, shell } = require('electron');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const runFile = promisify(execFile);

const projectsRoot = path.join(app.getPath('documents'), 'Buller Studio Projects');
const defaultProject = () => ({
  formatVersion: 1,
  name: 'Untitled Game',
  settings: { background: '#101522', shadows: true },
  workspace: {
    name: 'Workspace',
    children: [
      { id: 'camera-main', type: 'Camera', name: 'Camera', properties: { mode: 'Custom', position: [8, 7, 11], rotation: [-24, 35, 0], fov: 55, subject: '' }, children: [] },
      { id: 'part-start', type: 'Part', name: 'Part', properties: { position: [0, 0.5, 0], rotation: [0, 0, 0], size: [2, 1, 2], color: '#4e9dff', anchored: true, castShadow: true }, children: [] },
      { id: 'part-ground', type: 'Part', name: 'Ground', properties: { position: [0, -0.12, 0], rotation: [0, 0, 0], size: [24, 0.2, 24], color: '#293345', anchored: true, castShadow: false }, children: [] }
    ]
  },
  assets: [],
  scripts: []
});

function makeWindow() {
  const window = new BrowserWindow({ width: 1480, height: 920, minWidth: 1120, minHeight: 700, backgroundColor: '#101522', webPreferences: { nodeIntegration: true, contextIsolation: false } });
  window.loadFile(path.join(__dirname, 'index.html'));
}

app.whenReady().then(() => {
  makeWindow();
  app.on('activate', () => { if (BrowserWindow.getAllWindows().length === 0) makeWindow(); });
});
app.on('window-all-closed', () => { if (process.platform !== 'darwin') app.quit(); });

ipcMain.handle('projects:list', async () => {
  await fs.mkdir(projectsRoot, { recursive: true });
  const entries = await fs.readdir(projectsRoot, { withFileTypes: true });
  const projects = [];
  for (const entry of entries) {
    if (!entry.isDirectory()) continue;
    try {
      const file = path.join(projectsRoot, entry.name, 'game.buller.json');
      const [raw, stat] = await Promise.all([fs.readFile(file, 'utf8'), fs.stat(file)]);
      projects.push({ ...JSON.parse(raw), path: path.dirname(file), modified: stat.mtime.toISOString() });
    } catch { }
  }
  return projects.sort((a, b) => b.modified.localeCompare(a.modified));
});
ipcMain.handle('project:create', async (_event, name) => {
  const safeName = String(name || 'Untitled Game').replace(/[<>:"/\\|?*]/g, '').trim() || 'Untitled Game';
  let folder = path.join(projectsRoot, safeName);
  for (let n = 2; await fs.access(folder).then(() => true, () => false); n++) folder = path.join(projectsRoot, `${safeName} ${n}`);
  await fs.mkdir(path.join(folder, 'assets'), { recursive: true });
  const project = defaultProject(); project.name = path.basename(folder);
  await fs.writeFile(path.join(folder, 'game.buller.json'), JSON.stringify(project, null, 2));
  return { ...project, path: folder, modified: new Date().toISOString() };
});
ipcMain.handle('project:open', async (_event, folder) => {
  const raw = await fs.readFile(path.join(folder, 'game.buller.json'), 'utf8');
  return { ...JSON.parse(raw), path: folder };
});
ipcMain.handle('project:save', async (_event, project) => {
  await fs.mkdir(path.join(project.path, 'assets'), { recursive: true });
  const { path: _projectPath, ...data } = project;
  await fs.writeFile(path.join(project.path, 'game.buller.json'), JSON.stringify(data, null, 2));
  return new Date().toISOString();
});
ipcMain.handle('project:delete', async (_event, folder) => fs.rm(folder, { recursive: true, force: true }));
ipcMain.handle('assets:add', async (_event, folder) => {
  const result = await dialog.showOpenDialog({ properties: ['openFile', 'multiSelections'], filters: [{ name: 'Game assets', extensions: ['png', 'jpg', 'jpeg', 'webp', 'gif', 'svg', 'obj', 'gltf', 'glb', 'fbx', 'wav', 'mp3', 'ogg', 'ttf', 'otf'] }] });
  if (result.canceled) return [];
  const destination = path.join(folder, 'assets'); await fs.mkdir(destination, { recursive: true });
  const added = [];
  for (const source of result.filePaths) {
    let name = path.basename(source), target = path.join(destination, name), suffix = 2;
    while (await fs.access(target).then(() => true, () => false)) { const ext = path.extname(name); target = path.join(destination, `${path.basename(name, ext)}-${suffix++}${ext}`); }
    await fs.copyFile(source, target);
    const stat = await fs.stat(target);
    added.push({ id: `asset-${Date.now()}-${added.length}`, name: path.basename(target), path: path.relative(folder, target).replaceAll('\\', '/'), type: path.extname(target).slice(1).toLowerCase(), size: stat.size });
  }
  return added;
});
ipcMain.handle('project:compile', async (_event, project, included) => {
  const downloads = app.getPath('downloads');
  await fs.mkdir(downloads, { recursive: true });
  const folder = project.path, scratch = await fs.mkdtemp(path.join(os.tmpdir(), 'buller-build-'));
  const buildRoot = path.join(scratch, 'package');
  await fs.mkdir(path.join(buildRoot, 'src'), { recursive: true });
  const { path: _path, ...game } = project;
  game.build = { included: Boolean(included) };
  const outputFolder = path.join(downloads, `${project.name}-Build`);
  await fs.writeFile(path.join(buildRoot, 'package.json'), JSON.stringify({ name: 'buller-game', version: '1.0.0', private: true, main: 'src/game.js', dependencies: { three: '^0.177.0', fengari: '^0.1.4' }, devDependencies: { electron: '^36.3.2' }, build: { appId: `studio.buller.game.${Date.now()}`, productName: project.name, directories: { output: outputFolder }, files: ['src/**/*', 'assets/**/*', 'package.json', 'node_modules/**/*'], extraResources: [], win: { target: ['portable'] }, portable: { artifactName: `${project.name.replace(/[^a-z0-9-_]/gi, '-')}-${included ? 'Included' : 'External'}-${process.arch}.exe` } } }, null, 2));
  await fs.copyFile(path.join(__dirname, 'game-runtime.js'), path.join(buildRoot, 'src', 'game.js'));
  await fs.copyFile(path.join(__dirname, 'game.html'), path.join(buildRoot, 'src', 'game.html'));
  await fs.copyFile(path.join(__dirname, 'game-renderer.js'), path.join(buildRoot, 'src', 'game-renderer.js'));
  await fs.writeFile(path.join(buildRoot, 'src', 'game-data.json'), JSON.stringify(game));
  if (included) await fs.cp(path.join(folder, 'assets'), path.join(buildRoot, 'assets'), { recursive: true });
  const npm = process.platform === 'win32' ? 'npm.cmd' : 'npm';
  try {
    await runFile(npm, ['install', '--prefix', buildRoot, '--no-audit', '--no-fund'], { windowsHide: true, timeout: 300000 });
    await runFile(npm, ['exec', '--yes', '--package=electron-builder@^26.0.12', '--', 'electron-builder', '--projectDir', buildRoot, '--win', 'portable'], { windowsHide: true, timeout: 300000, shell: process.platform === 'win32' });
    if (!included) await fs.cp(path.join(folder, 'assets'), path.join(outputFolder, 'assets'), { recursive: true });
    return outputFolder;
  } catch (error) { throw new Error(error.stderr || error.message); }
  finally { await fs.rm(scratch, { recursive: true, force: true }); }
});
ipcMain.handle('shell:show', (_event, target) => shell.showItemInFolder(target));
