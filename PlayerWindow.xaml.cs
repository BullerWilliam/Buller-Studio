using System.Windows;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Vector3 = System.Numerics.Vector3;
using HelixToolkit.Wpf.SharpDX;
using BullerStudio.Models;
using BullerStudio.Services;

namespace BullerStudio;

public partial class PlayerWindow : Window
{
    private readonly GameProject project;
    private readonly SceneRenderService renderer;
    private readonly PhysicsSimulationService physics;
    private readonly PlayerCharacterService? character;
    private readonly SceneNode localPlayer;
    private readonly SceneNode backpack;
    private readonly SceneNode playerGui;
    private readonly List<SceneNode> toolHotbar = [];
    private SceneNode? activeTool;
    private sealed record EquippedPartState(float[] LocalOffset, float[] RotationOffset, float[] Position, float[] Rotation, bool Anchored, bool CanCollide);
    private readonly Dictionary<string, EquippedPartState> equippedToolParts = new(StringComparer.Ordinal);
    private readonly SeatInteractionService? seats;
    private readonly SoundPlaybackService soundPlayback;
    private LuaXRuntime? runtime;
    private readonly DispatcherTimer updateTimer;
    private bool isDragging;
    private Point lastPointer;
    private long lastUpdateTimestamp;
    private bool jumpRequested;
    private bool promptKeyDown;
    private string? holdingPromptId;
    private float promptHoldElapsed;
    private bool promptTriggered;
    private float spawnProtectionRemaining;

    public PlayerWindow(GameProject project, string? startupScriptId = null, Action<string>? scriptOutput = null)
    {
        InitializeComponent();
        GameGuiCanvas.SizeChanged += (_, _) => RefreshGameGui();
        this.project = project;
        GameProjectStore.EnsureProject(project);
        void CopyLocalScripts(SceneNode clonedRoot, IReadOnlyDictionary<string, string> renewedIds)
        {
            var clonedNodes = new[] { clonedRoot }.Concat(GameProjectStore.Flatten(clonedRoot.Children)).ToDictionary(node => node.Id, StringComparer.Ordinal);
            foreach (var (originalId, clonedId) in renewedIds)
            {
                if (project.Scripts.FirstOrDefault(script => script.Id == originalId) is not { } source
                    || !clonedNodes.TryGetValue(clonedId, out var clonedNode) || clonedNode.Type is not ("LocalScript" or "ModuleScript")) continue;
                project.Scripts.Add(new GameScript { Id = clonedId, Name = clonedNode.Name, Source = source.Source });
            }
        }
        project.Players.Children.RemoveAll(node => node.Type == "Player" && node.Name == "LocalPlayer");
        localPlayer = new SceneNode { Type = "Player", Name = "LocalPlayer" };
        localPlayer.Team = project.Teams.Children.FirstOrDefault(team => team.Type == "Team" && team.AutoAssignable)?.Id ?? string.Empty;
        backpack = new SceneNode { Type = "Backpack", Name = "Backpack" };
        localPlayer.Children.Add(backpack);
        var playerScripts = new SceneNode { Type = "PlayerScripts", Name = "PlayerScripts" };
        localPlayer.Children.Add(playerScripts);
        var starterPlayerScripts = project.StarterPlayer.Children.First(node => node.Name == "StarterPlayerScripts");
        foreach (var starterScript in starterPlayerScripts.Children)
            if (GameProjectStore.CloneSubtree(starterScript, out var renewedIds, respectArchivable: true) is { } clonedScript)
            {
                playerScripts.Children.Add(clonedScript);
                CopyLocalScripts(clonedScript, renewedIds);
            }
        playerGui = new SceneNode { Type = "PlayerGui", Name = "PlayerGui" };
        foreach (var starterScreen in project.Gui.Children.Where(node => node.Type == "ScreenGui"))
            if (GameProjectStore.CloneSubtree(starterScreen, out var renewedIds, respectArchivable: true) is { } screen)
            {
                playerGui.Children.Add(screen);
                CopyLocalScripts(screen, renewedIds);
            }
        localPlayer.Children.Add(playerGui);
        project.Players.Children.Add(localPlayer);
        foreach (var starterItem in project.StarterPack.Children)
            if (GameProjectStore.CloneSubtree(starterItem, out var renewedIds, respectArchivable: true) is { } item)
            {
                backpack.Children.Add(item);
                if (item.Type == "Tool") toolHotbar.Add(item);
                CopyLocalScripts(item, renewedIds);
            }
        if (project.Workspace.AutoManageCharacter)
        {
            character = new PlayerCharacterService(project, localPlayer);
            localPlayer.Character = character.Model.Id;
            spawnProtectionRemaining = character.SpawnProtection.Enabled ? character.SpawnLocation?.Duration ?? 0 : 0;
        }
        var camera = project.Workspace.Children.First(node => node.Type == "Camera");
        if (character is not null)
        {
            camera.CameraMode = "Subject";
            camera.Subject = character.Root.Id;
            seats = new SeatInteractionService(project, character);
        }
        GameTitle.Text = project.Name;
        Title = project.Name;
        renderer = new SceneRenderService(GameViewport, camera);
        renderer.Refresh(project);
#if GAME_PLAYER_BUILD
        const bool isStudioBuild = false;
#else
        const bool isStudioBuild = true;
#endif
        var gameRuntime = new LuaXRuntime(project,
            (node, property, _) => Dispatcher.Invoke(() =>
            {
                if (node.Type == "Sound") return;
                var isGuiNode = node.Id == project.Gui.Id
                    || GameProjectStore.Flatten(project.Gui.Children).Any(guiNode => guiNode.Id == node.Id)
                    || node.Id == playerGui.Id
                    || GameProjectStore.Flatten(playerGui.Children).Any(guiNode => guiNode.Id == node.Id)
                    || GameProjectStore.Flatten(project.Workspace.Children).Any(guiNode => guiNode.Type is "BillboardGui" or "SurfaceGui"
                        && (guiNode.Id == node.Id || GameProjectStore.Flatten(guiNode.Children).Any(child => child.Id == node.Id)));
                if (property == "Parent" || node.Type == "Workspace" && property.StartsWith("Skybox", StringComparison.Ordinal)) renderer.Refresh(project);
                else renderer.UpdateNode(node, project);
                renderer.UpdateCamera(project);
                if (node.Type == "TextBox" && property == "Text") UpdateGameTextBox(node);
                else if (isGuiNode || property == "Parent") RefreshGameGui();
            }),
            message => Dispatcher.Invoke(() =>
            {
                Title = $"{project.Name} — {message}";
                scriptOutput?.Invoke(message);
            }), isStudio: isStudioBuild, sitOnSeat: (targetSeat, humanoid) => Dispatcher.Invoke(() =>
            {
                if (seats is null) return;
                foreach (var node in seats.Sit(targetSeat, humanoid)) SyncSeatNode(node);
            }));
        runtime = gameRuntime;
        GameViewport.MouseDown += GameViewport_MouseDown;
        GameViewport.MouseUp += GameViewport_MouseUp;
        if (startupScriptId is null) gameRuntime.RunAll();
        else gameRuntime.Run(startupScriptId);
        physics = new PhysicsSimulationService(project, node =>
        {
            runtime?.SynchronizeExternalProperty(node, "Position");
            runtime?.SynchronizeExternalProperty(node, "Velocity");
            renderer.UpdateNode(node, project);
            renderer.UpdateCamera(project);
        }, rocketReachedTarget: node => runtime?.FireRocketReachedTarget(node));
        soundPlayback = new SoundPlaybackService(project, node => runtime?.SynchronizeExternalProperty(node, "Playing"));
        soundPlayback.Update(character?.Root.Position ?? camera.Position);
        RefreshGameGui();
        updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        lastUpdateTimestamp = Stopwatch.GetTimestamp();
        updateTimer.Tick += UpdateTimer_Tick;
        updateTimer.Start();
        Closed += (_, _) => { updateTimer.Stop(); soundPlayback.Dispose(); };
        Loaded += (_, _) => GameViewport.Focus();
        Deactivated += (_, _) =>
        {
            isDragging = false;
            if (Mouse.Captured == GameViewport) Mouse.Capture(null);
        };
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Clamp((float)Stopwatch.GetElapsedTime(lastUpdateTimestamp, now).TotalSeconds, .001f, .25f);
        lastUpdateTimestamp = now;
        UpdateSpawnProtection(elapsed);
        var canMove = IsActive && Keyboard.FocusedElement is not TextBox && Keyboard.FocusedElement is not Button;
        var forward = canMove ? (Keyboard.IsKeyDown(Key.W) ? 1 : 0) - (Keyboard.IsKeyDown(Key.S) ? 1 : 0) : 0;
        var strafe = canMove ? (Keyboard.IsKeyDown(Key.D) ? 1 : 0) - (Keyboard.IsKeyDown(Key.A) ? 1 : 0) : 0;
        seats?.SetVehicleInput(forward, strafe);
        if (seats is not null)
            foreach (var node in seats.BeforePhysics(canMove && jumpRequested)) SyncSeatNode(node);
        if (seats is not null)
            foreach (var motor in seats.UpdateVehicleMotorTargets()) SyncSeatNode(motor);
        if (seats?.IsSeated != true)
            character?.ApplyInput(physics, renderer.CameraYaw, forward, strafe, canMove && jumpRequested);
        jumpRequested = false;
        if (character is not null) runtime?.SynchronizeExternalProperty(character.Root, "Velocity");
        runtime?.UpdateBeforePhysics(elapsed);
        physics.Step(elapsed);
        UpdateSpawnTeamChange();
        if (seats is not null)
            foreach (var node in seats.AfterPhysics()) SyncSeatNode(node);
        runtime?.UpdateHumanoidDeaths();
        if (character?.NeedsRespawn == true)
        {
            if (seats is not null)
                foreach (var node in seats.BeforePhysics(true)) SyncSeatNode(node);
            if (activeTool is not null) EquipTool(null);
            foreach (var node in character.Respawn()) SyncCharacterNode(node);
            ResetSpawnProtection();
            runtime?.SynchronizeExternalProperty(character.Humanoid, "Health");
        }
        else if (character is not null)
        {
            foreach (var node in character.UpdatePose()) SyncCharacterNode(node);
        }
        UpdateEquippedToolGrip();
        runtime?.UpdateTouched(elapsed);
        renderer.UpdateParticleEffects(elapsed);
        var nearbyPrompt = character is null ? null : ProximityPromptService.FindNearest(project, character.Root);
        PromptBorder.Visibility = nearbyPrompt is null ? Visibility.Collapsed : Visibility.Visible;
        if (nearbyPrompt is not null)
        {
            PromptObjectText.Text = string.IsNullOrWhiteSpace(nearbyPrompt.Prompt.ObjectText)
                ? nearbyPrompt.Parent.Name : nearbyPrompt.Prompt.ObjectText;
            PromptActionText.Text = string.IsNullOrWhiteSpace(nearbyPrompt.Prompt.ActionText)
                ? "Interact" : nearbyPrompt.Prompt.ActionText;
            if (promptKeyDown && Keyboard.FocusedElement is not (TextBox or Button))
            {
                if (holdingPromptId != nearbyPrompt.Prompt.Id)
                {
                    holdingPromptId = nearbyPrompt.Prompt.Id;
                    promptHoldElapsed = 0;
                    promptTriggered = false;
                }
                if (!promptTriggered)
                {
                    promptHoldElapsed += elapsed;
                    var holdDuration = Math.Clamp(nearbyPrompt.Prompt.HoldDuration, 0, 60);
                    if (promptHoldElapsed >= holdDuration)
                    {
                        runtime?.InvokePromptTriggered(nearbyPrompt.Prompt.Id, localPlayer.Id);
                        promptTriggered = true;
                        PromptActionText.Text = string.IsNullOrWhiteSpace(nearbyPrompt.Prompt.ActionText) ? "Interact" : nearbyPrompt.Prompt.ActionText;
                    }
                    else PromptActionText.Text = $"Hold E  {promptHoldElapsed:0.0}/{holdDuration:0.0}s";
                }
            }
            else ResetPromptHold();
        }
        else ResetPromptHold();
        if (seats?.VehicleSeat is { } vehicle && vehicle.VehicleSeatHeadsUpDisplay)
        {
            VehicleHud.Visibility = Visibility.Visible;
            VehicleHudText.Text = $"Throttle  {vehicle.VehicleSeatThrottleFloat:+0.0;-0.0;0.0}\nSteer  {vehicle.VehicleSeatSteerFloat:+0.0;-0.0;0.0}\nMotor hinges  {vehicle.VehicleSeatAreHingesDetected}";
        }
        else VehicleHud.Visibility = Visibility.Collapsed;
        soundPlayback.Update(character?.Root.Position ?? project.Workspace.Children.First(node => node.Type == "Camera").Position);
        UpdateBillboardGuiPositions();
    }

    private void SyncCharacterNode(SceneNode node)
    {
        runtime?.SynchronizeExternalProperty(node, "Position");
        runtime?.SynchronizeExternalProperty(node, "Rotation");
        runtime?.SynchronizeExternalProperty(node, "Velocity");
        runtime?.SynchronizeExternalProperty(node, "Anchored");
        runtime?.SynchronizeExternalProperty(node, "CanCollide");
        renderer.UpdateNode(node, project);
    }

    private void UpdateSpawnTeamChange()
    {
        if (character is null) return;
        foreach (var spawn in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type == "SpawnLocation" && node.Enabled && !node.Neutral && node.AllowTeamChangeOnTouch))
        {
            var top = CollisionGeometry.TopYAt(spawn, character.Root.Position[0], character.Root.Position[2]);
            if (top is null || Math.Abs(character.Root.Position[1] - (top.Value + character.Root.Size[1] * .5f)) > .16f) continue;
            var team = project.Teams.Children.FirstOrDefault(node => node.Type == "Team"
                && string.Equals(node.TeamColor, spawn.TeamColor, StringComparison.OrdinalIgnoreCase));
            if (team is null || localPlayer.Team == team.Id) continue;
            localPlayer.Team = team.Id;
            runtime?.SynchronizeExternalProperty(localPlayer, "Team");
            runtime?.SynchronizeExternalProperty(localPlayer, "TeamColor");
            break;
        }
    }

    private void UpdateSpawnProtection(float elapsed)
    {
        if (character is null || spawnProtectionRemaining <= 0) return;
        spawnProtectionRemaining = Math.Max(0, spawnProtectionRemaining - elapsed);
        if (spawnProtectionRemaining > 0) return;
        character.SpawnProtection.Enabled = false;
        runtime?.SynchronizeExternalProperty(character.SpawnProtection, "Enabled");
        renderer.UpdateNode(character.SpawnProtection, project);
    }

    private void ResetSpawnProtection()
    {
        if (character is null) return;
        spawnProtectionRemaining = Math.Clamp(character.SpawnLocation?.Duration ?? 0, 0, 10000);
        character.SpawnProtection.Enabled = spawnProtectionRemaining > 0;
        runtime?.SynchronizeExternalProperty(character.SpawnProtection, "Enabled");
        renderer.UpdateNode(character.SpawnProtection, project);
    }

    private void SyncSeatNode(SceneNode node)
    {
        if (node.Type is "Seat" or "VehicleSeat")
        {
            runtime?.SynchronizeExternalProperty(node, "Occupant");
            if (node.Type == "VehicleSeat")
            {
                runtime?.SynchronizeExternalProperty(node, "ThrottleFloat");
                runtime?.SynchronizeExternalProperty(node, "SteerFloat");
                runtime?.SynchronizeExternalProperty(node, "AreHingesDetected");
            }
        }
        else if (node.Type is "HingeConstraint" or "CylindricalConstraint")
        {
            runtime?.SynchronizeExternalProperty(node, "AngularVelocity");
            runtime?.SynchronizeExternalProperty(node, "AngularSpeed");
            runtime?.SynchronizeExternalProperty(node, "MotorMaxAcceleration");
            runtime?.SynchronizeExternalProperty(node, "TargetAngle");
        }
        else SyncCharacterNode(node);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or Button) return;
        var toolIndex = e.Key switch { >= Key.D1 and <= Key.D9 => e.Key - Key.D1, >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1, _ => -1 };
        foreach (var item in backpack.Children.Where(node => node.Type == "Tool"))
            if (!toolHotbar.Contains(item)) toolHotbar.Add(item);
        var tools = toolHotbar.Where(node => node.Enabled && (backpack.Children.Contains(node) || ReferenceEquals(activeTool, node))).ToArray();
        if (toolIndex >= 0 && toolIndex < tools.Length)
        {
            EquipTool(ReferenceEquals(activeTool, tools[toolIndex]) ? null : tools[toolIndex]);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Back && activeTool is { CanBeDropped: true } droppedTool)
        {
            var heldPose = new[] { droppedTool }.Concat(GameProjectStore.Flatten(droppedTool.Children)).Where(SceneNodeTypes.IsBasePart)
                .ToDictionary(part => part.Id, part => ((float[])part.Position.Clone(), (float[])part.Rotation.Clone()), StringComparer.Ordinal);
            EquipTool(null);
            if (runtime?.SetParent(droppedTool, project.Workspace) == true)
            {
                foreach (var part in new[] { droppedTool }.Concat(GameProjectStore.Flatten(droppedTool.Children)).Where(SceneNodeTypes.IsBasePart))
                    if (heldPose.TryGetValue(part.Id, out var pose))
                    {
                        part.Position = pose.Item1;
                        part.Rotation = pose.Item2;
                        SyncCharacterNode(part);
                    }
                Title = $"{project.Name} — dropped {droppedTool.Name}";
                renderer.Refresh(project);
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Space && !e.IsRepeat) jumpRequested = true;
        if (e.Key == Key.E) promptKeyDown = true;
        if (e.Key is Key.W or Key.A or Key.S or Key.D or Key.Space or Key.E) e.Handled = true;
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.E) return;
        promptKeyDown = false;
        ResetPromptHold();
    }

    private void ResetPromptHold()
    {
        holdingPromptId = null;
        promptHoldElapsed = 0;
        promptTriggered = false;
    }

    private void GameViewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && activeTool is { Enabled: true } tool)
        {
            if (!tool.ManualActivationOnly) runtime?.InvokeToolActivated(tool.Id);
            e.Handled = true;
        }
    }

    private void GameViewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && activeTool is { } tool) runtime?.DeactivateTool(tool.Id);
    }

    private void EquipTool(SceneNode? tool)
    {
        if (activeTool is { } oldTool)
        {
            runtime?.DeactivateTool(oldTool.Id);
            runtime?.InvokeToolEquipped(oldTool.Id, false);
            if (character is not null) runtime?.SetParent(oldTool, backpack);
            foreach (var part in new[] { oldTool }.Concat(GameProjectStore.Flatten(oldTool.Children)).Where(SceneNodeTypes.IsBasePart))
            {
                if (!equippedToolParts.Remove(part.Id, out var state)) continue;
                part.Position = state.Position;
                part.Rotation = state.Rotation;
                part.Anchored = state.Anchored;
                part.CanCollide = state.CanCollide;
                SyncCharacterNode(part);
            }
            activeTool = null;
        }
        if (tool is null) { Title = project.Name; renderer.Refresh(project); return; }
        var handle = FindToolHandle(tool);
        if (tool.RequiresHandle && handle is null)
        {
            Title = $"{project.Name} — {tool.Name} needs a Handle";
            renderer.Refresh(project);
            return;
        }
        if (character is not null && (runtime is null || !runtime.SetParent(tool, character.Model)))
        {
            Title = project.Name;
            renderer.Refresh(project);
            return;
        }
        activeTool = tool;
        runtime?.InvokeToolEquipped(tool.Id, true);
        if (handle is not null)
        {
            var yaw = character?.Root.Rotation[1] * MathF.PI / 180 ?? 0;
            var cosine = MathF.Cos(yaw);
            var sine = MathF.Sin(yaw);
            foreach (var part in new[] { tool }.Concat(GameProjectStore.Flatten(tool.Children)).Where(SceneNodeTypes.IsBasePart))
            {
                var dx = part.Position[0] - handle.Position[0];
                var dz = part.Position[2] - handle.Position[2];
                var localOffset = new[] { dx * cosine - dz * sine, part.Position[1] - handle.Position[1], dx * sine + dz * cosine };
                var rotationOffset = new[] { part.Rotation[0] - handle.Rotation[0], part.Rotation[1] - handle.Rotation[1], part.Rotation[2] - handle.Rotation[2] };
                equippedToolParts[part.Id] = new EquippedPartState(localOffset, rotationOffset, (float[])part.Position.Clone(), (float[])part.Rotation.Clone(), part.Anchored, part.CanCollide);
                part.Anchored = true;
                part.CanCollide = false;
            }
        }
        Title = $"{project.Name} — Tool: {tool.Name}";
        UpdateEquippedToolGrip();
        renderer.Refresh(project);
    }

    private void UpdateEquippedToolGrip()
    {
        if (character is null || activeTool is null || !character.Model.Children.Contains(activeTool)) return;
        var handle = FindToolHandle(activeTool);
        if (handle is null) return;
        var yaw = character.Root.Rotation[1] * MathF.PI / 180;
        var cosine = MathF.Cos(yaw);
        var sine = MathF.Sin(yaw);
        var localGrip = activeTool.GripPosition;
        var handlePosition = new[]
        {
            character.Root.Position[0] + localGrip[0] * cosine + localGrip[2] * sine,
            character.Root.Position[1] + localGrip[1],
            character.Root.Position[2] - localGrip[0] * sine + localGrip[2] * cosine
        };
        var handleRotation = new[] { activeTool.GripRotation[0], character.Root.Rotation[1] + activeTool.GripRotation[1], activeTool.GripRotation[2] };
        foreach (var part in new[] { activeTool }.Concat(GameProjectStore.Flatten(activeTool.Children)).Where(SceneNodeTypes.IsBasePart))
        {
            if (!equippedToolParts.TryGetValue(part.Id, out var state)) continue;
            var offset = state.LocalOffset;
            var position = new[]
            {
                handlePosition[0] + offset[0] * cosine + offset[2] * sine,
                handlePosition[1] + offset[1],
                handlePosition[2] - offset[0] * sine + offset[2] * cosine
            };
            var rotation = new[] { handleRotation[0] + state.RotationOffset[0], handleRotation[1] + state.RotationOffset[1], handleRotation[2] + state.RotationOffset[2] };
            if (part.Position.SequenceEqual(position) && part.Rotation.SequenceEqual(rotation)) continue;
            part.Position = position;
            part.Rotation = rotation;
            SyncCharacterNode(part);
        }
    }

    private static SceneNode? FindToolHandle(SceneNode tool) => GameProjectStore.Flatten(tool.Children).FirstOrDefault(node => node.Name == "Handle" && SceneNodeTypes.IsBasePart(node));

    private void RefreshGameGui()
    {
        var rendered = GuiRenderService.Render(
            project,
            true,
            GameGuiCanvas.ActualWidth,
            GameGuiCanvas.ActualHeight,
            activated: node => runtime?.InvokeGuiActivated(node.Id),
            textChanged: (node, value) => runtime?.UpdateGuiTextInput(node.Id, value),
            screenGuiRoot: playerGui);
        var children = rendered.Children.Cast<UIElement>().ToArray();
        rendered.Children.Clear();
        GameGuiCanvas.Children.Clear();
        foreach (var child in children) GameGuiCanvas.Children.Add(child);
        AddBillboardGuis();
    }

    private void AddBillboardGuis()
    {
        if (GameViewport.Camera is not PerspectiveCamera camera) return;
        GuiRenderService.AddBillboards(GameGuiCanvas, project,
            new Vector3((float)camera.Position.X, (float)camera.Position.Y, (float)camera.Position.Z),
            new Vector3((float)camera.LookDirection.X, (float)camera.LookDirection.Y, (float)camera.LookDirection.Z),
            new Vector3((float)camera.UpDirection.X, (float)camera.UpDirection.Y, (float)camera.UpDirection.Z),
            (float)camera.FieldOfView, GameGuiCanvas.ActualWidth, GameGuiCanvas.ActualHeight, true,
            activated: node => runtime?.InvokeGuiActivated(node.Id),
            textChanged: (node, value) => runtime?.UpdateGuiTextInput(node.Id, value));
        GuiRenderService.AddSurfaceGuis(GameGuiCanvas, project,
            new Vector3((float)camera.Position.X, (float)camera.Position.Y, (float)camera.Position.Z),
            new Vector3((float)camera.LookDirection.X, (float)camera.LookDirection.Y, (float)camera.LookDirection.Z),
            new Vector3((float)camera.UpDirection.X, (float)camera.UpDirection.Y, (float)camera.UpDirection.Z),
            (float)camera.FieldOfView, GameGuiCanvas.ActualWidth, GameGuiCanvas.ActualHeight, true,
            activated: node => runtime?.InvokeGuiActivated(node.Id),
            textChanged: (node, value) => runtime?.UpdateGuiTextInput(node.Id, value));
    }

    private void UpdateBillboardGuiPositions()
    {
        if (GameViewport.Camera is not PerspectiveCamera camera) return;
        GuiRenderService.UpdateBillboardPositions(GameGuiCanvas, project,
            new Vector3((float)camera.Position.X, (float)camera.Position.Y, (float)camera.Position.Z),
            new Vector3((float)camera.LookDirection.X, (float)camera.LookDirection.Y, (float)camera.LookDirection.Z),
            new Vector3((float)camera.UpDirection.X, (float)camera.UpDirection.Y, (float)camera.UpDirection.Z),
            (float)camera.FieldOfView, GameGuiCanvas.ActualWidth, GameGuiCanvas.ActualHeight);
        GuiRenderService.UpdateSurfaceGuiPositions(GameGuiCanvas, project,
            new Vector3((float)camera.Position.X, (float)camera.Position.Y, (float)camera.Position.Z),
            new Vector3((float)camera.LookDirection.X, (float)camera.LookDirection.Y, (float)camera.LookDirection.Z),
            new Vector3((float)camera.UpDirection.X, (float)camera.UpDirection.Y, (float)camera.UpDirection.Z),
            (float)camera.FieldOfView, GameGuiCanvas.ActualWidth, GameGuiCanvas.ActualHeight);
    }

    private void UpdateGameTextBox(SceneNode node)
    {
        var textBox = FindGameTextBox(GameGuiCanvas, node.Id);
        if (textBox is null)
        {
            RefreshGameGui();
            return;
        }
        if (textBox.Text == node.Text) return;
        var selectionStart = textBox.SelectionStart;
        var selectionLength = textBox.SelectionLength;
        var hadKeyboardFocus = textBox.IsKeyboardFocusWithin;
        textBox.Text = node.Text;
        textBox.Select(Math.Min(selectionStart, textBox.Text.Length), Math.Min(selectionLength, textBox.Text.Length - Math.Min(selectionStart, textBox.Text.Length)));
        if (hadKeyboardFocus) textBox.Focus();
    }

    private static TextBox? FindGameTextBox(DependencyObject parent, string nodeId)
    {
        if (parent is TextBox { Tag: SceneNode node } textBox && node.Id == nodeId) return textBox;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var result = FindGameTextBox(VisualTreeHelper.GetChild(parent, index), nodeId);
            if (result is not null) return result;
        }
        return null;
    }

    private void Viewport_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        isDragging = true;
        lastPointer = e.GetPosition(GameViewport);
        Mouse.Capture(GameViewport);
        GameViewport.Focus();
        e.Handled = true;
    }

    private void Viewport_RightButtonUp(object sender, MouseButtonEventArgs e)
    {
        isDragging = false;
        Mouse.Capture(null);
        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!isDragging)
        {
            GameViewport.Cursor = FindPointedClickDetector() is null ? Cursors.Arrow : Cursors.Hand;
            return;
        }
        var point = e.GetPosition(GameViewport);
        renderer.SetCameraOrbit(-(float)(point.X - lastPointer.X) * .25f, -(float)(point.Y - lastPointer.Y) * .25f, 0, project);
        lastPointer = point;
        e.Handled = true;
    }

    private void Viewport_MouseLeave(object sender, MouseEventArgs e) => GameViewport.Cursor = Cursors.Arrow;

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        renderer.SetCameraOrbit(0, 0, e.Delta > 0 ? -1 : 1, project);
        e.Handled = true;
    }

    private void Viewport_MouseDown3D(object sender, RoutedEventArgs e)
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        var detector = FindPointedClickDetector();
        if (detector is null) return;
        runtime?.InvokeClickDetector(detector.Id, localPlayer.Id);
        e.Handled = true;
    }

    private SceneNode? FindPointedClickDetector()
    {
        if (character is null) return null;
        var hit = GameViewport.FindHits(Mouse.GetPosition(GameViewport)).FirstOrDefault()?.ModelHit;
        if (hit is not MeshGeometryModel3D model) return null;
        var partId = renderer.Visuals.FirstOrDefault(entry => ReferenceEquals(entry.Value, model)).Key;
        if (partId is null) return null;
        var part = GameProjectStore.Flatten(project.Workspace.Children).FirstOrDefault(node => node.Id == partId);
        return part is null ? null : ClickDetectorService.FindActive(part, character.Root);
    }
}
