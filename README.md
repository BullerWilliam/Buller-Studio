# Buller Studio

Buller Studio is a Windows desktop game engine and editor. Projects are saved locally under `Documents/Buller Studio Projects/<project>/`, with `game.buller.json` for scene and script data and `assets/` for imported files.

## Start the editor

Install Node.js 20 or newer, then run:

```powershell
npm install
npm start
```

The first launch creates a project. Later launches open the local project list. Use the three-dot project menu to build or delete a project. `Ctrl+S` saves, `Ctrl+Z` undoes, and `Ctrl+Shift+Z` redoes.

## Editor controls

- Add Parts, MeshParts, ImageLabels, Folders, and Scripts from the toolbar.
- Edit object transforms, appearance, and camera properties in the Properties panel. Positions and rotations use three numeric components; rotations are degrees.
- The Workspace includes its persistent Camera object. Custom mode uses the camera's position and rotation. Subject mode orbits its selected Part. In the editor, WASD moves the custom camera, right mouse drag looks around, and the mouse wheel zooms.
- Import image, OBJ mesh, audio, and font files into the project's `assets/` folder. Assign an imported image to an ImageLabel or an OBJ file to a MeshPart using its Asset path property.
- Add a Script and use the Lua X panel. `SetProperty(instanceId, propertyName, value)`, `GetProperty(instanceId, propertyName)`, `GetAssetPath(assetName)`, `WaitForChild(parentIdOrWorkspace, childName)`, `ConnectTouched(instanceId, callback)`, and `print(...)` are available in game builds. `WaitForChild("Workspace", "Part")` finds an immediate child by name and returns its ID. Touched callbacks receive the other object's ID.
- The scene and game renderer use the Workspace Camera and support directional shadows.
- Drag an Explorer, Scene, Properties, or lower-panel header over another panel to swap their snapped workspace positions.

## Build games

Select **Included** to package the project's assets inside its Windows portable executable, or **External assets** to emit the executable with an `assets/` directory beside it. Builds are saved to a named folder in Downloads. Building downloads Electron and packaging dependencies when needed. The editor itself runs as a desktop Electron application; `npm run dist` packages the editor as a Windows portable executable.

This first engine release renders box Parts, image planes, and OBJ meshes. Projects and imported files stay on the local computer. The Lua X runtime is based on Lua 5.3 through Fengari; the language and engine API will grow with later engine versions.
