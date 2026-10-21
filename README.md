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
- Insert Block, Ball, and Cylinder Parts, WedgePart, CornerWedgePart, TrussPart, SpawnLocation, Seat, MeshPart, Models, Folders, Humanoid, and PointLight, SpotLight, SurfaceLight, Decal, Texture, Sound, ProximityPrompt, ClickDetector, Highlight, or WeldConstraint objects. A Humanoid belongs under a Model, with one Humanoid per Model. Lights, Decals, Textures, prompts, and click detectors are children of a 3D part; Sound can also sit under Workspace, a Folder, or a Model. A Highlight can be a child of a part or Model. A Part's Shape and a light's color, brightness, range, and enabled state are editable in Properties; SpotLight also has rotation, and SurfaceLight points outward from its selected Face. An enabled SpawnLocation places the play-mode character on its pad.
- Insert BoolValue, IntValue, NumberValue, and StringValue under compatible Workspace, GUI, or ReplicatedStorage objects to store simple typed data. Value objects can contain child objects. Edit `Value` in Properties; values persist with the project. In Lua X, read or assign `.Value`, call `:SetProperty("Value", value)`, and connect `Changed` to receive the new value. `Instance.new` also creates these objects during play. IntValue accepts whole numbers within Lua's exact integer range (±9,007,199,254,740,991).
- Insert Vector3Value and Color3Value in the same hierarchies. Their `Value` fields store a three-component vector or a `#RRGGBB` color; Lua X reads and writes them as `Vector3` and `Color3` values. `Changed` receives the new typed value. They also support `Instance.new`, cloning, and project save/load.
- Insert ObjectValue to store a reference to another project object. Choose its `Value` with the Properties object picker or assign an Instance or `nil` in Lua X. Its `Changed` event receives the new reference. Cloning a Model redirects references to objects inside the clone and keeps references to objects outside it; deleting a referenced object clears the value.
- Insert a Configuration to group editable value objects and other children under a model, part, service, or GUI hierarchy. It behaves as a container in Explorer, survives save/load and cloning, and can be created with `Instance.new("Configuration", parent)` in Lua X.
- Insert a UIGradient under a GUI object to render a two-color background gradient. Edit StartColor, EndColor, Rotation, and Enabled in Properties, or create and change it from Lua X with `Instance.new("UIGradient", frame)` and Color3 values for its two colors. The gradient appears in the editor preview and play mode, and survives save/load and cloning. Image and text foregrounds are not yet tinted by the gradient.
- Insert UIPadding under a GUI object to inset its children. PaddingLeft, PaddingTop, PaddingRight, and PaddingBottom accept scale and pixel offset pairs in Properties or `UDim` values in Lua X, such as `padding.PaddingLeft = UDim.new(0, 12)`. Padding affects free placement, scaled sizes, and list and table layouts in the editor preview and play mode.
- Insert UIScale under a GUI object to scale that object and its descendants visually. Its Scale property is editable in Properties and Lua X, and horizontal or vertical list layouts reserve space for the scaled result. UIScale combines with GUI Rotation and persists in saved projects.
- Insert UIGridLayout under a GUI object to arrange children in fixed cells. CellSize and CellPadding use UDim2 scale and offset values; FillDirection selects horizontal or vertical filling, and FillDirectionMaxCells limits cells per row or column (`0` chooses from available space). It works with UIPadding and appears in the editor preview and play mode. UITableLayout remains available for older projects.
- GUI objects have ZIndex and LayoutOrder properties in Properties and Lua X. ZIndex controls which overlapping sibling draws on top; LayoutOrder controls placement in list, grid, and table layouts without changing Explorer order. Equal values keep the Explorer order.
- Insert a BindableEvent to send a one-way signal between Lua X scripts during play. Connect with `event.Event:Connect(function(...) ... end)` and send arguments with `event:Fire(...)`; firing returns before listeners run. The returned connection has `Connected` and `Disconnect()`. Events can be saved, cloned, and created with `Instance.new("BindableEvent", parent)`. Play runs in one local process, so these events do not provide network communication; table arguments are currently passed by reference.
- Insert a BindableFunction for a request and response between Lua X scripts in the same play process. Assign the write-only `OnInvoke` callback, then call `functionObject:Invoke(...)` to receive its return values. The caller waits if the callback yields or has not yet been assigned; assigning another callback replaces the earlier one. These objects can be saved, cloned, and created with `Instance.new("BindableFunction", parent)`.
- Insert a ModuleScript to share Lua X code. Double-click it in Explorer to edit its source, return a value, then call `require(game.ReplicatedStorage.ModuleScript)` from another script. You can also pass a string path: `./` starts at the calling script's parent, `../` at its grandparent, `@self/` at the script itself, and `@game/` at a service under the game. The module stays dormant until required and runs once per play runtime; later calls receive the same returned value. Callers wait while a module yields or another script is loading it, and recursive requires report an error. ModuleScripts support save/load, duplication, cloning, and `Instance.new`. Asset ID arguments are not supported.
- ReplicatedStorage is a persistent Explorer service for shared folders, models, parts, sounds, values, and Script templates. Retrieve it with `game:GetService("ReplicatedStorage")` or `game.ReplicatedStorage`. Parts kept there are not rendered or simulated. Scripts kept there stay dormant; moving or cloning an enabled one into Workspace or GUI starts it during play. A Script's `Enabled` checkbox controls startup, and changing `Enabled` in Lua X can stop or restart it. Moving a running Script into ReplicatedStorage stops it until it returns to an active hierarchy.
- ServerScriptService and ServerStorage appear as persistent Explorer services. Enabled Scripts under ServerScriptService run during play, including Scripts inside its Folders and Models. Scripts and parts in ServerStorage stay dormant and out of the scene until moved or cloned into an active hierarchy. Use `game:GetService("ServerScriptService")`, `game:GetService("ServerStorage")`, or their `game` properties in Lua X. Play currently runs in one local process; these services organize execution and storage without network replication or separate client/server security contexts.
- Lighting is a persistent Explorer service with Ambient, Brightness, ClockTime, and GlobalShadows properties. It contains a Sky object whose SkyboxFt/Bk/Lf/Rt/Up/Dn image paths can be set in Properties or from Asset Manager. Lua X can use `game:GetService("Lighting")`, edit these properties during play, and replace the Sky with `Instance.new("Sky", game.Lighting)`. Existing Workspace skybox paths still render as fallback faces when a Sky face is empty. The sun direction follows ClockTime approximately; celestial bodies, atmosphere, and Roblox's full lighting pipeline are not yet implemented.
- Use `instance:Clone()` in Lua X to make an unparented copy of a stored template, then parent the copy into Workspace or GUI. The copy gets new IDs, keeps child properties, and reconnects internal weld references. Set `Archivable` in Properties or Lua X to exclude an object and its descendants from cloning; non-archivable children are omitted. The editor's **Duplicate** command still copies those children and sets the duplicate's own Archivable value to true.
- Import an image in Asset Manager, then use **Use on selected object** with a selected Decal or set its Asset path in Properties. Choose Front, Back, Left, Right, Top, or Bottom in Face. Decals are planar overlays and fit flat part faces best; their texture follows the parent part during editing and play.
- Add a Texture to a part and assign an imported image through Asset Manager or its Asset property. Choose its Face, StudsPerTileU/V, and OffsetStudsU/V in Properties to repeat and shift the image across that face. The tiling updates when the part is resized.
- Add a Sound to Workspace or a part and assign an imported WAV, MP3, or WMA asset. Set Playing and Looped in Properties, or call `sound:Play()` and `sound:Stop()` in Lua X. Sounds attached directly to a part fade between RollOffMinDistance and RollOffMaxDistance relative to the play character, or the camera when automatic character creation is off. Workspace sounds play at their full Volume. Audio runs during play tests and in packaged games.
- Use **Model → Duplicate** or `Ctrl+D` to clone the selected object and its children, including attached scripts. The clone receives new IDs so it can be edited independently. Right-click an Explorer object to insert, duplicate, rename, or delete it; `F2` also renames the selection.
- Use **Model → Group** or `Ctrl+G` to wrap the selected workspace object in a Model; **Ungroup** or `Ctrl+Shift+G` moves its children back to the parent. Explorer currently selects one object at a time.
- Edit Position, Rotation (degrees), Size, Color, Material, Transparency, Anchored, CanCollide, CanTouch, CanQuery, Velocity, and CastShadow in Properties. Available materials are Plastic, Metal, Neon, Glass, Wood, and Concrete. Lua X exposes `LookAtRotation(fromPosition, toPosition)`, which returns a Vector3 rotation in degrees; for example, `part.Rotation = LookAtRotation(part.Position, target.Position)` turns a 3D part toward another position. It also works with a Camera in Custom mode. When the positions coincide, it returns zero rotation. Select a Model in Explorer to move all of its 3D children together by editing its Position; its child geometry is outlined as a group.
- Workspace has an `AutoManageCharacter` checkbox, enabled by default. When enabled at the start of play, the engine creates a simple character at the first enabled SpawnLocation, or on the largest solid part under the origin when no spawn pad is enabled. Use WASD to move, Space to jump, right mouse drag to orbit the follow camera, and the wheel to zoom. The engine respawns this character after it falls out of the world. When the checkbox is off, play starts without a generated character or automatic movement and respawn; the camera retains its configured mode. The `Players` service remains available either way, with a play-only `LocalPlayer` whose `Character` is nil until assigned a Model by a script. Unanchored BaseParts fall under Workspace Gravity and collide with solid parts. Turn off CanCollide for pass-through parts. Play uses a separate scene copy, so simulated movement and script changes do not alter the editor project. Block collision and touch detection follow Position and Rotation; balls, cylinders, and wedges still use box-shaped collision approximations.
- The play-only avatar appears under Workspace as `PlayerCharacter` with a `HumanoidRootPart`, a Humanoid, and visible body parts when automatic character management is enabled. Scripts can find it through `game:GetService("Players").LocalPlayer.Character`; it is not saved into the project. Its visual parts have `CanTouch` disabled so they do not generate contacts with its collision root. The Humanoid's `WalkSpeed` and `JumpPower` control movement; zero `Health` fires `Died` and respawns the generated character with `MaxHealth`.
- A Seat accepts the automatically managed character when it lands on its rotated collision surface. Movement pauses while seated; press Space to stand and jump. `Seat.Occupant` returns the Humanoid in Lua X, or nil when vacant. Seats can be placed and edited like other BaseParts.
- Add a ProximityPrompt to a part and edit its ActionText, ObjectText, Enabled state, and MaxActivationDistance in Properties. During play with an automatically managed character, approach the part and press E to fire its Lua X `Triggered` event. The callback receives `Players.LocalPlayer`. The nearest enabled prompt appears at the bottom of the game window.
- Add a ClickDetector to a part and edit its Enabled state and MaxActivationDistance in Properties. During play with an automatically managed character, left-click that part while in range to fire its Lua X `MouseClick` event. The callback receives `Players.LocalPlayer`.
- Add a Highlight to a part or Model to tint and outline it in the editor and during play. Edit Enabled, FillColor, OutlineColor, FillTransparency, and OutlineTransparency in Properties or Lua X. A Model Highlight covers its descendant parts; a closer Highlight on a child part takes precedence.
- Add a WeldConstraint under Workspace, a Folder, Model, or part. Set Part0 and Part1 using the Properties object pickers; adding it under a part fills Part0 automatically. Enabled welds join unanchored parts into one translating assembly during play, and an anchored member holds the assembly still. Lua X can read or assign Part0 and Part1 as BasePart instances. Welds preserve the parts' relative positions during physics, and moving one member translates the assembly. When an assembly has an anchored member, its position controls the assembly. Rotation is not yet propagated through welds.
- Add an Attachment under each of two parts, then insert a Beam and set Attachment0 and Attachment1 with the Properties object pickers. Attachment Position is relative to its parent part. Small Attachment markers are selectable in the editor viewport and hidden during play. Beams render as tapered colored tubes between the endpoints, follow moving and rotating parts, and expose Enabled, Width0, Width1, Color, and Transparency. Lua X can create both classes with `Instance.new` and set Beam endpoint references to Attachment instances. Cloning a Model that contains the Beam and both Attachments reconnects the clone's endpoints.
- Set `Velocity` or `AssemblyLinearVelocity` in Lua X to move an unanchored BasePart during play. Welded parts share translation and velocity. Collision stops the affected velocity component; bounce and friction are not simulated.
- The Workspace Camera has `Mode` (`Custom` or `Subject`), `Position`, `Rotation`, `Subject`, and `FieldOfView` properties. Custom mode uses Position and Rotation. Subject mode orbits the selected BasePart. The scene view uses this same camera.
- Use right mouse drag to turn the editor camera, WASD/QE to move it, and the wheel to zoom. The editor camera controls remain separate from the play-mode character and follow camera.
- Import PNG/JPEG/BMP/GIF images, WAV/MP3/WMA audio, and Wavefront OBJ meshes into the local assets list. Assign an asset's relative path, such as `assets/tree.obj`, to a MeshPart or `assets/logo.png` to an ImageLabel. Lua X can also access imported asset paths.
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
prompt.Triggered:Connect(function(player)
    print("Used by", player.Name)
end)
local detector = part:WaitForChild("ClickDetector")
detector.MouseClick:Connect(function(player)
    print("Clicked by", player.Name)
end)
local humanoid = game.Players.LocalPlayer.Character:WaitForChild("Humanoid")
humanoid.Died:Connect(function() print("Character died") end)
humanoid.WalkSpeed = 12
local score = Instance.new("IntValue", workspace)
score.Name = "Score"
score.Changed:Connect(function(value) print("Score:", value) end)
score.Value = score.Value + 1
local template = game.ReplicatedStorage:FindFirstChild("Tree")
if template then
    local tree = template:Clone()
    tree:SetProperty("Parent", workspace)
end
local imagePath = GetAssetPath("logo.png")
```

`SetProperty(instance, property, value)`, `GetProperty(instance, property)`, `WaitForChild(parent, childName)`, `ConnectTouched(instance, callback)`, `GetAssetPath(assetName)`, and `print(...)` are available. `waitforchild` and lowercase `touched` aliases are supported. Touched events are generated for overlapping BasePart bounds when both parts have `CanTouch` enabled.

Lua X supports `workspace:Raycast(origin, direction, params?)` for querying 3D parts. The direction vector sets the cast length. A hit returns `Instance`, `Position`, `Normal`, `Distance`, and `Material`; a miss returns `nil`. Queries use rotated part bounds and respect `CanQuery` independently of collision and touch. Use `RaycastParams.new()` with `FilterDescendantsInstances = {modelOrPart}` and `FilterType = Enum.RaycastFilterType.Exclude` (default) or `Include` to filter object trees.

Use `task.wait(seconds)` to yield until a later play update; it returns the elapsed simulation time. `task.spawn(callback, ...)` starts a separate coroutine immediately, `task.defer(callback, ...)` runs after the current script resume point, and `task.delay(seconds, callback, ...)` runs after the delay. Each scheduling call returns a coroutine that `task.cancel(thread)` can stop while pending or yielded. The global `wait(seconds)` is an alias for `task.wait`. Disabling, moving out of an active hierarchy, or destroying a Script cancels its pending tasks.

During play, `Instance.new(className, parent?)` creates supported scene and GUI objects. Children can be added to a Model before the Model is placed in Workspace. Assign `Parent = nil` to detach an object, or call `Destroy()` to remove it and its descendants. These changes affect the play copy and are not saved to the editor project. For a project with `Workspace.AutoManageCharacter` off, a script can create and assign its own character Model:

```lua
local character = Instance.new("Model")
character.Name = "CustomCharacter"
local root = Instance.new("Part", character)
root.Name = "HumanoidRootPart"
root.Position = Vector3.new(0, 5, 0)
root.Anchored = false
Instance.new("Humanoid", character)
character.Parent = workspace
game.Players.LocalPlayer.Character = character
workspace.Camera.Subject = root
workspace.Camera.Mode = "Subject"
```

Direct `Name` and `Parent` assignments become visible to hierarchy lookups when the current script yields or finishes. Use `instance:SetProperty("Name", name)` or `instance:SetProperty("Parent", parent)` when later lines in the same script need to find the object immediately.

With automatic character management off, scripts control that character's movement and respawn behavior.

## Build games

Choose **Included** to embed the project and its assets into the game executable, or **Excluded** to place `game.buller.json` and an `assets/` folder beside the executable. Build output is placed in a named subfolder in Downloads. Builds use `dotnet publish` for a self-contained Windows x64 single-file executable; the .NET SDK and a network connection may be needed on the first build to restore the Windows runtime pack.

## Engine capabilities

The editor includes a Direct3D scene view with primitive shapes, point, spot, and surface lights, directional lighting and shadow mapping, local project persistence, parts and model groups, OBJ mesh and image rendering, planar decals and tiled textures, local sound playback, click and proximity interactions, basic play-test gravity, solid-part collision and translation-only welds, a simple spawn and movement controller with Humanoid health and movement settings, camera modes, a Lua X runtime, and included/excluded game packaging. It does not yet include full rigid-body physics, rotating joints, glTF import, multiplayer, or a full Humanoid state machine and character animation.
