# Buller Studio

Buller Studio is a native C#/.NET 8 Windows game editor and runtime. It renders a local 3D workspace, manages scene objects and assets, runs Lua X scripts, and builds standalone Windows games.

## Run the editor

Install the .NET 8 SDK, then from this folder run:

```powershell
dotnet restore BullerStudio.csproj
dotnet run --project BullerStudio.csproj
```

The app opens to a dedicated Home screen; no editor is shown until you create or open a project. Home includes templates, recent local projects, search, and three-dot build/delete actions. Projects are stored under `Documents/Buller Studio Projects/<project>/` with `game.buller.json` and an `assets/` folder.

## Editing

- Create objects from the `+` button on a compatible Explorer object; search the insert menu to filter object types. The Workspace always keeps one Camera.
- Edit Position, Rotation (degrees), Size, Color, Anchored, and CastShadow in Properties. Transform values are separate vectors; there is no CFrame type.
- The Workspace Camera has `Mode` (`Custom` or `Subject`), `Position`, `Rotation`, `Subject`, and `FieldOfView` properties. Custom mode uses Position and Rotation. Subject mode orbits the selected Part or MeshPart. The scene view uses this same camera.
- Use right mouse drag to turn the editor camera, WASD/QE to move it, and the wheel to zoom. Camera controls only affect the editor camera; games have no built-in player or character.
- Import PNG/JPEG/BMP/GIF images and Wavefront OBJ meshes into the local assets list. Assign an asset's relative path, such as `assets/tree.obj`, to a MeshPart or `assets/logo.png` to an ImageLabel. Lua X can also access imported asset paths.
- `Ctrl+S` saves, `Ctrl+Z` undoes, `Ctrl+Shift+Z` redoes, and `Delete` removes a selected non-camera object.
- The editor uses a Studio-style ribbon and dock layout: the 3D viewport in the center, Explorer and Properties on the right, and Output/Asset Manager below. Manage assets only in Asset Manager. Drag panel headers to snap major docks to another workspace region.

## Lua X

Scripts use a sandboxed Lua interpreter. Instances are available by name and under `game.Workspace`. Useful APIs include:

Lua X completion suggests object members, `WaitForChild` names from the current hierarchy, asset paths, service names, camera modes, and property names for `SetProperty`/`GetProperty`.

```lua
local part = game.Workspace:WaitForChild("Part")
part:SetProperty("Position", {X = 0, Y = 3, Z = 0})
print(part:GetProperty("Name"))
part.Touched:Connect(function(other)
    print("Touched", other.Name)
end)
local imagePath = GetAssetPath("logo.png")
```

`SetProperty(instance, property, value)`, `GetProperty(instance, property)`, `WaitForChild(parent, childName)`, `ConnectTouched(instance, callback)`, `GetAssetPath(assetName)`, and `print(...)` are available. `waitforchild` and lowercase `touched` aliases are supported. Touched events are generated for overlapping Part/MeshPart bounds.

## Build games

Choose **Included** to embed the project and its assets into the game executable, or **Excluded** to place `game.buller.json` and an `assets/` folder beside the executable. Build output is placed in a named subfolder in Downloads. Builds use `dotnet publish` for a self-contained Windows x64 single-file executable; the .NET SDK and a network connection may be needed on the first build to restore the Windows runtime pack.

## Engine capabilities

The first version includes a Direct3D scene view with directional lighting and shadow mapping, local project persistence, basic parts and folders, OBJ mesh and image rendering, camera modes, a Lua X runtime, and included/excluded game packaging. This version does not include physics simulation, glTF import, multiplayer, or built-in character controllers.
