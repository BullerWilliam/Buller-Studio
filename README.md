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

- Create objects from the `+` button on a compatible Explorer object or **Model → Insert**; search the insert menu to filter object types. The Workspace always keeps one Camera.
- Insert Block, Ball, and Cylinder Parts, WedgePart, CornerWedgePart, TrussPart, SpawnLocation, MeshPart, Models, Folders, and PointLight, SpotLight, SurfaceLight, Decal, or ProximityPrompt objects. Lights, Decals, and prompts are children of a 3D part. A Part's Shape and a light's color, brightness, range, and enabled state are editable in Properties; SpotLight also has rotation, and SurfaceLight points outward from its selected Face. An enabled SpawnLocation places the play-mode character on its pad.
- Import an image in Asset Manager, then use **Use on selected object** with a selected Decal or set its Asset path in Properties. Choose Front, Back, Left, Right, Top, or Bottom in Face. Decals are planar overlays and fit flat part faces best; their texture follows the parent part during editing and play.
- Use **Model → Duplicate** or `Ctrl+D` to clone the selected object and its children, including attached scripts. The clone receives new IDs so it can be edited independently. Right-click an Explorer object to insert, duplicate, rename, or delete it; `F2` also renames the selection.
- Use **Model → Group** or `Ctrl+G` to wrap the selected workspace object in a Model; **Ungroup** or `Ctrl+Shift+G` moves its children back to the parent. Explorer currently selects one object at a time.
- Edit Position, Rotation (degrees), Size, Color, Material, Transparency, Anchored, CanCollide, CanTouch, Velocity, and CastShadow in Properties. Available materials are Plastic, Metal, Neon, Glass, Wood, and Concrete. Transform values are separate vectors; there is no CFrame type. Select a Model in Explorer to move all of its 3D children together by editing its Position; its child geometry is outlined as a group.
- During play tests, a simple character appears at the first enabled SpawnLocation, or on the largest solid part under the origin when no spawn pad is enabled. Use WASD to move, Space to jump, right mouse drag to orbit the follow camera, and the wheel to zoom. The character respawns after falling out of the world. Unanchored BaseParts fall under Workspace Gravity and collide with solid parts. Turn off CanCollide for pass-through parts. Play uses a separate scene copy, so simulated movement and script changes do not alter the editor project. Collision uses axis-aligned boxes, so rotated shapes do not yet have exact collision geometry.
- The play-only avatar appears under Workspace as `PlayerCharacter` with a `HumanoidRootPart` and visible body parts. Scripts can find it there; it is not saved into the project. Its visual parts have `CanTouch` disabled so they do not generate contacts with its collision root.
- Add a ProximityPrompt to a part and edit its ActionText, ObjectText, Enabled state, and MaxActivationDistance in Properties. During play, approach the part and press E to fire its Lua X `Triggered` event. The callback receives the play-only character Model. The nearest enabled prompt appears at the bottom of the game window.
- Set `Velocity` or `AssemblyLinearVelocity` in Lua X to move an unanchored BasePart during play. Collision currently stops the affected velocity component rather than simulating bounce, friction, or joined assemblies.
- The Workspace Camera has `Mode` (`Custom` or `Subject`), `Position`, `Rotation`, `Subject`, and `FieldOfView` properties. Custom mode uses Position and Rotation. Subject mode orbits the selected BasePart. The scene view uses this same camera.
- Use right mouse drag to turn the editor camera, WASD/QE to move it, and the wheel to zoom. The editor camera controls remain separate from the play-mode character and follow camera.
- Import PNG/JPEG/BMP/GIF images and Wavefront OBJ meshes into the local assets list. Assign an asset's relative path, such as `assets/tree.obj`, to a MeshPart or `assets/logo.png` to an ImageLabel. Lua X can also access imported asset paths.
- `Ctrl+S` saves, `Ctrl+Z` undoes, `Ctrl+Shift+Z` redoes, `Ctrl+D` duplicates, and `Delete` removes a selected non-camera object.
- The editor uses a Studio-style ribbon and dock layout: the 3D viewport in the center, Explorer and Properties on the right, and Output/Asset Manager below. Manage assets only in Asset Manager. Drag panel headers to snap major docks to another workspace region.

## Lua X

Scripts use a sandboxed Lua interpreter. Instances are available by name and under `game.Workspace`. Useful APIs include:

Lua X completion suggests object members, `WaitForChild` names from the current hierarchy, asset paths, service names, camera modes, and property names for `SetProperty`/`GetProperty`.

```lua
local part = game.Workspace:WaitForChild("Part")
part:SetProperty("Position", {X = 0, Y = 3, Z = 0})
part:SetProperty("Shape", "Ball")
part:SetProperty("Material", "Neon")
part.Anchored = false
part.AssemblyLinearVelocity = Vector3.new(5, 0, 0)
print(part:GetProperty("Name"))
part.Touched:Connect(function(other)
    print("Touched", other.Name)
end)
local prompt = part:WaitForChild("ProximityPrompt")
prompt.Triggered:Connect(function(character)
    print("Used by", character.Name)
end)
local imagePath = GetAssetPath("logo.png")
```

`SetProperty(instance, property, value)`, `GetProperty(instance, property)`, `WaitForChild(parent, childName)`, `ConnectTouched(instance, callback)`, `GetAssetPath(assetName)`, and `print(...)` are available. `waitforchild` and lowercase `touched` aliases are supported. Touched events are generated for overlapping BasePart bounds when both parts have `CanTouch` enabled.

## Build games

Choose **Included** to embed the project and its assets into the game executable, or **Excluded** to place `game.buller.json` and an `assets/` folder beside the executable. Build output is placed in a named subfolder in Downloads. Builds use `dotnet publish` for a self-contained Windows x64 single-file executable; the .NET SDK and a network connection may be needed on the first build to restore the Windows runtime pack.

## Engine capabilities

The editor includes a Direct3D scene view with primitive shapes, point, spot, and surface lights, directional lighting and shadow mapping, local project persistence, parts and model groups, OBJ mesh and image rendering, planar decals, basic play-test gravity and solid-part collision, a simple spawn and movement controller, camera modes, a Lua X runtime, and included/excluded game packaging. It does not yet include full rigid-body physics, joints, glTF import, multiplayer, or Roblox-style Humanoid behavior and character animation.
