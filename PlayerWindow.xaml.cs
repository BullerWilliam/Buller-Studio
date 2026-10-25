using System.Windows;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Matrix4x4 = System.Numerics.Matrix4x4;
using HelixToolkit.Wpf.SharpDX;
using BullerStudio.Models;
using BullerStudio.Services;

namespace BullerStudio;

public partial class PlayerWindow : Window
{
    private readonly GameProject project;
    private readonly SceneRenderService renderer;
    private readonly PhysicsSimulationService physics;
    private PlayerCharacterService? character;
    private readonly SceneNode localPlayer;
    private readonly SceneNode backpack;
    private readonly SceneNode playerGui;
    private readonly List<SceneNode> toolHotbar = [];
    private readonly HashSet<Key> contextSunkKeys = [];
    private readonly HashSet<Key> promptKeysDown = [];
    private readonly HashSet<string> promptGamepadKeysDown = new(StringComparer.Ordinal);
    private readonly GamepadInputService gamepadInput = new();
    private Vector2 previousLeftStick;
    private Vector2 previousRightStick;
    private readonly HashSet<MouseButton> contextSunkMouseButtons = [];
    private SceneNode? activeTool;
    private sealed record EquippedPartState(float[] LocalOffset, Matrix4x4 RotationOffset, float[] Position, float[] Rotation, bool Anchored, bool CanCollide);
    private readonly Dictionary<string, EquippedPartState> equippedToolParts = new(StringComparer.Ordinal);
    private SeatInteractionService? seats;
    private readonly SoundPlaybackService soundPlayback;
    private LuaXRuntime? runtime;
    private readonly DispatcherTimer updateTimer;
    private bool isDragging;
    private string? hoveredClickDetectorId;
    private Point lastPointer;
    private long lastUpdateTimestamp;
    private bool jumpRequested;
    private string? holdingPromptId;
    private string? clickablePromptDownId;
    private string? visiblePromptId;
    private bool visiblePromptIndicatorOnly;
    private float promptHoldElapsed;
    private bool promptTriggered;
    private float spawnProtectionRemaining;
    private bool autoManageCharacterEnabled;
    private bool autoManagedCameraOverride;
    private bool resettingPlayerGui;
    private bool isPaused;
    private string cameraModeBeforeAutoManage = "Custom";
    private string cameraSubjectBeforeAutoManage = string.Empty;

    private void CopyLocalScripts(SceneNode clonedRoot, IReadOnlyDictionary<string, string> renewedIds)
    {
        var clonedNodes = new[] { clonedRoot }.Concat(GameProjectStore.Flatten(clonedRoot.Children)).ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var (originalId, clonedId) in renewedIds)
        {
            if (project.Scripts.FirstOrDefault(script => script.Id == originalId) is not { } source
                || !clonedNodes.TryGetValue(clonedId, out var clonedNode)
                || clonedNode.Type is not ("LocalScript" or "ModuleScript")
                    && !(clonedNode.Type == "Script" && clonedNode.RunContext == "Client")) continue;
            project.Scripts.Add(new GameScript { Id = clonedId, Name = clonedNode.Name, Source = source.Source });
        }
    }

    private void CopyCharacterScripts(PlayerCharacterService sourceCharacter)
    {
        var clonedNodes = new[] { sourceCharacter.Model }.Concat(GameProjectStore.Flatten(sourceCharacter.Model.Children)).ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var (originalId, clonedId) in sourceCharacter.RenewedIds)
        {
            if (project.Scripts.FirstOrDefault(script => script.Id == originalId) is not { } source
                || !clonedNodes.TryGetValue(clonedId, out var clonedNode)
                || !SceneNodeTypes.IsBaseScript(clonedNode)
                || project.Scripts.Any(script => script.Id == clonedId)) continue;
            project.Scripts.Add(new GameScript { Id = clonedId, Name = clonedNode.Name, Source = source.Source });
        }
    }

    public PlayerWindow(GameProject project, string? startupScriptId = null, Action<string>? scriptOutput = null)
    {
        InitializeComponent();
        GameGuiCanvas.SizeChanged += (_, _) => RefreshGameGui();
        this.project = project;
        GameProjectStore.EnsureProject(project);
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
        var camera = project.Workspace.Children.First(node => node.Type == "Camera");
        autoManageCharacterEnabled = project.Workspace.AutoManageCharacter;
        cameraModeBeforeAutoManage = camera.CameraMode;
        cameraSubjectBeforeAutoManage = camera.Subject;
        if (autoManageCharacterEnabled)
        {
            character = new PlayerCharacterService(project, localPlayer);
            CopyCharacterScripts(character);
            localPlayer.Character = character.Model.Id;
            spawnProtectionRemaining = character.SpawnProtection.Enabled ? character.SpawnLocation?.Duration ?? 0 : 0;
            autoManagedCameraOverride = true;
        }
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
        PlayTestControls.Visibility = isStudioBuild ? Visibility.Visible : Visibility.Collapsed;
        var gameRuntime = new LuaXRuntime(project,
            (node, property, _) => Dispatcher.Invoke(() =>
            {
                if (node.Type == "Workspace" && property == "AutoManageCharacter")
                {
                    UpdateAutoManageCharacter();
                    return;
                }
                if (node.Type == "Player" && node.Id == localPlayer.Id && property == "Character")
                {
                    if (!project.Workspace.AutoManageCharacter && seats is not null)
                    {
                        foreach (var changedNode in seats.BeforePhysics(true)) SyncSeatNode(changedNode);
                        seats = null;
                    }
                    if (FindRuntimeNode(node.Character) is { Type: "Model" }) ResetPlayerGui();
                    return;
                }
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
                if (seats is null && !project.Workspace.AutoManageCharacter
                    && FindRuntimeNode(localPlayer.Character) is { Type: "Model" } playerCharacter
                    && GameProjectStore.Flatten(playerCharacter.Children).Any(node => node.Id == humanoid.Id))
                {
                    var parts = GameProjectStore.Flatten(playerCharacter.Children).Where(SceneNodeTypes.IsBasePart).ToArray();
                    var root = parts.FirstOrDefault(part => part.Name == "HumanoidRootPart")
                        ?? parts.FirstOrDefault(part => part.Id == playerCharacter.PrimaryPart)
                        ?? parts.FirstOrDefault();
                    if (root is not null) seats = new SeatInteractionService(project, humanoid, root);
                }
                if (seats is null) return;
                foreach (var node in seats.Sit(targetSeat, humanoid)) SyncSeatNode(node);
            }), focusTextBox: (textBoxId, capture) => Dispatcher.Invoke(() =>
            {
                var input = FindGameTextBox(GameGuiCanvas, textBoxId);
                if (input is null) return;
                if (capture) input.Focus();
                else if (input.IsKeyboardFocused) Keyboard.ClearFocus();
            }));
        runtime = gameRuntime;
        GameViewport.MouseDown += GameViewport_MouseDown;
        GameViewport.MouseUp += GameViewport_MouseUp;
        if (startupScriptId is null) gameRuntime.RunAll();
        else gameRuntime.Run(startupScriptId);
        physics = new PhysicsSimulationService(project, node =>
        {
            runtime?.SynchronizeExternalProperty(node, "Position");
            runtime?.SynchronizeExternalProperty(node, "Rotation");
            runtime?.SynchronizeExternalProperty(node, "Velocity");
            renderer.UpdateNode(node, project);
            renderer.UpdateCamera(project);
        }, rocketReachedTarget: node => runtime?.FireRocketReachedTarget(node), destroyFallenPart: node =>
        {
            if (character is { } managedCharacter && GameProjectStore.Flatten(managedCharacter.Model.Children).Any(part => part.Id == node.Id)) return;
            runtime?.DestroyFromPhysics(node);
        });
        soundPlayback = new SoundPlaybackService(project, node =>
        {
            runtime?.SynchronizeExternalProperty(node, "Playing");
            runtime?.SynchronizeExternalProperty(node, "TimePosition");
        }, (node, signal) => runtime?.FireSoundSignal(node, signal));
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
            clickablePromptDownId = null;
            runtime?.ReleaseAllPressedKeys();
            gamepadInput.Reset();
            previousLeftStick = previousRightStick = Vector2.Zero;
            contextSunkKeys.Clear();
            contextSunkMouseButtons.Clear();
            if (Mouse.Captured == GameViewport) Mouse.Capture(null);
        };
    }

    private void UpdateAutoManageCharacter()
    {
        var shouldManage = project.Workspace.AutoManageCharacter;
        if (autoManageCharacterEnabled == shouldManage)
        {
            if (shouldManage && string.IsNullOrEmpty(localPlayer.Character))
            {
                if (character is null || FindRuntimeNode(character.Model.Id) is null)
                    CreateManagedCharacter();
                else
                    runtime?.SetExternalPlayerCharacter(localPlayer, character.Model);
            }
            return;
        }
        autoManageCharacterEnabled = shouldManage;
        var camera = project.Workspace.Children.First(node => node.Type == "Camera");
        if (shouldManage)
        {
            if (FindRuntimeNode(localPlayer.Character) is { Type: "Model" }) return;
            CreateManagedCharacter();
            return;
        }
        else if (character is { } generatedCharacter)
        {
            if (activeTool is not null) EquipTool(null);
            if (seats is not null)
                foreach (var node in seats.BeforePhysics(true)) SyncSeatNode(node);
            runtime?.RemoveExternalSubtree(generatedCharacter.Model);
            character = null;
            seats = null;
            spawnProtectionRemaining = 0;
            if (autoManagedCameraOverride)
            {
                camera.CameraMode = cameraModeBeforeAutoManage;
                camera.Subject = cameraSubjectBeforeAutoManage;
                runtime?.SynchronizeExternalProperty(camera, "CameraMode");
                runtime?.SynchronizeExternalProperty(camera, "Subject");
                autoManagedCameraOverride = false;
            }
        }
        renderer.Refresh(project);
        renderer.UpdateCamera(project);
    }

    private void CreateManagedCharacter()
    {
        var camera = project.Workspace.Children.First(node => node.Type == "Camera");
        cameraModeBeforeAutoManage = camera.CameraMode;
        cameraSubjectBeforeAutoManage = camera.Subject;
        character = new PlayerCharacterService(project, localPlayer);
        CopyCharacterScripts(character);
        seats = new SeatInteractionService(project, character);
        spawnProtectionRemaining = character.SpawnProtection.Enabled ? character.SpawnLocation?.Duration ?? 0 : 0;
        camera.CameraMode = "Subject";
        camera.Subject = character.Root.Id;
        runtime?.RegisterExternalSubtree(character.Model);
        runtime?.SetExternalPlayerCharacter(localPlayer, character.Model);
        autoManagedCameraOverride = true;
        runtime?.SynchronizeExternalProperty(camera, "CameraMode");
        runtime?.SynchronizeExternalProperty(camera, "Subject");
        renderer.Refresh(project);
        renderer.UpdateCamera(project);
    }

    private void ResetPlayerGui()
    {
        if (resettingPlayerGui || runtime is null) return;
        resettingPlayerGui = true;
        try
        {
            foreach (var oldScreen in playerGui.Children.Where(node => node.Type == "ScreenGui" && node.ResetOnSpawn).ToArray())
                runtime.RemoveExternalSubtree(oldScreen);
            var localScripts = new List<SceneNode>();
            foreach (var template in project.Gui.Children.Where(node => node.Type == "ScreenGui" && node.ResetOnSpawn))
            {
                if (GameProjectStore.CloneSubtree(template, out var renewedIds, respectArchivable: true) is not { } screen) continue;
                playerGui.Children.Add(screen);
                CopyLocalScripts(screen, renewedIds);
                runtime.RegisterExternalSubtree(screen);
                localScripts.AddRange(new[] { screen }.Concat(GameProjectStore.Flatten(screen.Children))
                    .Where(node => node.Type == "LocalScript" && node.Enabled));
            }
            foreach (var localScript in localScripts) runtime.Run(localScript.Id);
            RefreshGameGui();
        }
        finally
        {
            resettingPlayerGui = false;
        }
    }

    private SceneNode? FindRuntimeNode(string id) => string.IsNullOrEmpty(id)
        ? null
        : GameProjectStore.Flatten(project).FirstOrDefault(node => node.Id == id);

    private SceneNode? FindLocalPlayerInteractionRoot()
    {
        if (FindRuntimeNode(localPlayer.Character) is { Type: "Model" } model
            && GameProjectStore.Flatten(project.Workspace.Children).Any(node => node.Id == model.Id))
        {
            var parts = GameProjectStore.Flatten(model.Children).Where(SceneNodeTypes.IsBasePart).ToArray();
            return parts.FirstOrDefault(part => part.Name == "HumanoidRootPart")
                ?? parts.FirstOrDefault(part => part.Id == model.PrimaryPart)
                ?? parts.FirstOrDefault();
        }
        return character is { } managedCharacter && FindRuntimeNode(managedCharacter.Root.Id) is not null
            ? managedCharacter.Root : null;
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Clamp((float)Stopwatch.GetElapsedTime(lastUpdateTimestamp, now).TotalSeconds, .001f, .25f);
        lastUpdateTimestamp = now;
        var gamepadChanges = IsActive ? gamepadInput.Poll() : Array.Empty<(string KeyCode, bool IsDown)>();
        if (IsActive) runtime?.SetGamepadAvailable(gamepadInput.IsConnected);
        foreach (var change in gamepadChanges)
        {
            var consumed = runtime?.FireUserInput(change.KeyCode, change.IsDown, false, inputType: "Gamepad1") == true;
            if (!change.IsDown)
            {
                promptGamepadKeysDown.Remove(change.KeyCode);
                continue;
            }
            if (!consumed && FindLocalPlayerInteractionRoot() is { } gamepadInteractionRoot
                && ProximityPromptService.FindNearest(project, gamepadInteractionRoot, runtime?.ProximityPromptsEnabled ?? true) is { IsIndicatorOnly: false } gamepadPrompt
                && PromptKeyMatches(change.KeyCode, gamepadPrompt.Prompt.GamepadKeyCode))
                promptGamepadKeysDown.Add(change.KeyCode);
        }
        var leftStick = IsActive ? gamepadInput.LeftStick : Vector2.Zero;
        var rightStick = IsActive ? gamepadInput.RightStick : Vector2.Zero;
        var leftStickConsumed = runtime?.FireGamepadStick("Thumbstick1", leftStick.X, leftStick.Y,
            leftStick.X - previousLeftStick.X, leftStick.Y - previousLeftStick.Y) == true;
        var rightStickConsumed = runtime?.FireGamepadStick("Thumbstick2", rightStick.X, rightStick.Y,
            rightStick.X - previousRightStick.X, rightStick.Y - previousRightStick.Y) == true;
        previousLeftStick = leftStick;
        previousRightStick = rightStick;
        if (IsActive && Keyboard.FocusedElement is not TextBox && Keyboard.FocusedElement is not Button
            && !rightStickConsumed && rightStick.LengthSquared() > 0)
            renderer.SetCameraOrbit(-rightStick.X * 120f * elapsed, rightStick.Y * 100f * elapsed, 0, project);
        UpdateAutoManageCharacter();
        UpdateSpawnProtection(elapsed);
        var canMove = IsActive && Keyboard.FocusedElement is not TextBox && Keyboard.FocusedElement is not Button;
        var forward = canMove ? Math.Clamp((Keyboard.IsKeyDown(Key.W) && !contextSunkKeys.Contains(Key.W) ? 1 : 0) - (Keyboard.IsKeyDown(Key.S) && !contextSunkKeys.Contains(Key.S) ? 1 : 0) + (leftStickConsumed ? 0 : leftStick.Y), -1, 1) : 0;
        var strafe = canMove ? Math.Clamp((Keyboard.IsKeyDown(Key.D) && !contextSunkKeys.Contains(Key.D) ? 1 : 0) - (Keyboard.IsKeyDown(Key.A) && !contextSunkKeys.Contains(Key.A) ? 1 : 0) + (leftStickConsumed ? 0 : leftStick.X), -1, 1) : 0;
        seats?.SetVehicleInput(forward, strafe);
        if (seats is not null)
            foreach (var node in seats.BeforePhysics(canMove && jumpRequested)) SyncSeatNode(node);
        if (seats is not null)
            foreach (var motor in seats.UpdateVehicleMotorTargets()) SyncSeatNode(motor);
        if (seats?.IsSeated != true)
            character?.ApplyInput(physics, renderer.CameraYaw, forward, strafe, canMove && jumpRequested, elapsed);
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
            ResetPlayerGui();
        }
        else if (character is not null)
        {
            foreach (var node in character.UpdatePose()) SyncCharacterNode(node);
            if (runtime is not null)
                foreach (var node in runtime.ReapplyAnimations()) SyncCharacterNode(node);
        }
        UpdateEquippedToolGrip();
        runtime?.UpdateTouched(elapsed);
        runtime?.UpdateAfterPhysics(elapsed);
        renderer.UpdateParticleEffects(elapsed);
        var interactionRoot = FindLocalPlayerInteractionRoot();
        var nearbyPrompt = interactionRoot is null ? null : ProximityPromptService.FindNearest(project, interactionRoot, runtime?.ProximityPromptsEnabled ?? true);
        var nextVisiblePromptId = nearbyPrompt?.Prompt.Id;
        var nextIsIndicatorOnly = nearbyPrompt?.IsIndicatorOnly ?? false;
        if (visiblePromptId != nextVisiblePromptId || visiblePromptIndicatorOnly != nextIsIndicatorOnly)
        {
            if (visiblePromptId is { } previousPromptId)
                runtime?.InvokePromptEvent(previousPromptId, visiblePromptIndicatorOnly ? "IndicatorHidden" : "PromptHidden");
            visiblePromptId = nextVisiblePromptId;
            visiblePromptIndicatorOnly = nextIsIndicatorOnly;
            if (visiblePromptId is { } newPromptId)
                runtime?.InvokePromptEvent(newPromptId, visiblePromptIndicatorOnly ? "IndicatorShown" : "PromptShown", inputType: "Keyboard");
        }
        PromptBorder.Visibility = nearbyPrompt is null || nearbyPrompt.Prompt.PromptStyle == "Custom" ? Visibility.Collapsed : Visibility.Visible;
        if (nearbyPrompt is not null)
        {
            PromptInputText.Text = nearbyPrompt.IsIndicatorOnly ? "…" : FormatPromptInput(nearbyPrompt.Prompt);
            PromptObjectText.Text = string.IsNullOrWhiteSpace(nearbyPrompt.Prompt.ObjectText)
                ? nearbyPrompt.Parent.Name : nearbyPrompt.Prompt.ObjectText;
            PromptActionText.Text = nearbyPrompt.IsIndicatorOnly ? "Move closer to interact" : string.IsNullOrWhiteSpace(nearbyPrompt.Prompt.ActionText)
                ? $"{nearbyPrompt.Prompt.KeyboardKeyCode}  Interact"
                : $"{nearbyPrompt.Prompt.KeyboardKeyCode}  {nearbyPrompt.Prompt.ActionText}";
            var promptKeyDown = !nearbyPrompt.IsIndicatorOnly
                && (promptKeysDown.Any(key => PromptKeyMatches(GetKeyCodeName(key), nearbyPrompt.Prompt.KeyboardKeyCode))
                    || promptGamepadKeysDown.Contains(nearbyPrompt.Prompt.GamepadKeyCode));
            var promptMouseDown = !nearbyPrompt.IsIndicatorOnly && nearbyPrompt.Prompt.ClickablePrompt && clickablePromptDownId == nearbyPrompt.Prompt.Id;
            if ((promptKeyDown || promptMouseDown) && Keyboard.FocusedElement is not (TextBox or Button))
            {
                if (holdingPromptId != nearbyPrompt.Prompt.Id)
                {
                    ResetPromptHold();
                    holdingPromptId = nearbyPrompt.Prompt.Id;
                    promptHoldElapsed = 0;
                    promptTriggered = false;
                    if (nearbyPrompt.Prompt.HoldDuration > 0)
                        runtime?.InvokePromptEvent(nearbyPrompt.Prompt.Id, "PromptButtonHoldBegan", localPlayer.Id);
                }
                if (!promptTriggered)
                {
                    promptHoldElapsed += elapsed;
                    var holdDuration = Math.Clamp(nearbyPrompt.Prompt.HoldDuration, 0, 60);
                    if (promptHoldElapsed >= holdDuration)
                    {
                        runtime?.InvokePromptTriggered(nearbyPrompt.Prompt.Id, localPlayer.Id);
                        promptTriggered = true;
                        PromptActionText.Text = string.IsNullOrWhiteSpace(nearbyPrompt.Prompt.ActionText)
                            ? $"{nearbyPrompt.Prompt.KeyboardKeyCode}  Interact"
                            : $"{nearbyPrompt.Prompt.KeyboardKeyCode}  {nearbyPrompt.Prompt.ActionText}";
                    }
                    else PromptActionText.Text = $"Hold {nearbyPrompt.Prompt.KeyboardKeyCode}  {promptHoldElapsed:0.0}/{holdDuration:0.0}s";
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

    private void PausePlayTest_Click(object sender, RoutedEventArgs e)
    {
        if (!isPaused)
        {
            isPaused = true;
            runtime?.SetSimulationPaused(true);
            updateTimer.Stop();
            soundPlayback.Pause();
            foreach (var media in FindVisualDescendants<MediaElement>(GameGuiCanvas))
                if (media.Tag is SceneNode node && node.Playing) media.Pause();
            runtime?.ReleaseAllPressedKeys();
            contextSunkKeys.Clear();
            promptKeysDown.Clear();
            contextSunkMouseButtons.Clear();
            jumpRequested = false;
            ResetPromptHold();
            PauseButton.Content = "▶  Resume";
            GameTitle.Text = $"{project.Name} — Paused";
            Title = $"{project.Name} — Paused";
            return;
        }

        foreach (var media in FindVisualDescendants<MediaElement>(GameGuiCanvas))
            if (media.Tag is SceneNode node && node.Playing) media.Play();
        soundPlayback.Resume();
        lastUpdateTimestamp = Stopwatch.GetTimestamp();
        isPaused = false;
        runtime?.SetSimulationPaused(false);
        PauseButton.Content = "Ⅱ  Pause";
        GameTitle.Text = project.Name;
        Title = project.Name;
        updateTimer.Start();
        GameViewport.Focus();
    }

    private void StopPlayTest_Click(object sender, RoutedEventArgs e) => Close();

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualDescendants<T>(child)) yield return descendant;
        }
    }

    private void SyncCharacterNode(SceneNode node)
    {
        runtime?.SynchronizeExternalProperty(node, "Position");
        runtime?.SynchronizeExternalProperty(node, "Rotation");
        runtime?.SynchronizeExternalProperty(node, "Velocity");
        runtime?.SynchronizeExternalProperty(node, "Anchored");
        runtime?.SynchronizeExternalProperty(node, "CanCollide");
        if (node.Type == "Motor6D")
        {
            runtime?.SynchronizeExternalProperty(node, "TransformPosition");
            runtime?.SynchronizeExternalProperty(node, "TransformRotation");
        }
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
        var gameProcessed = Keyboard.FocusedElement is TextBox or Button;
        var contextSunk = runtime?.FireUserInput(GetKeyCodeName(e.Key), true, gameProcessed, e.IsRepeat) == true;
        if (contextSunk || contextSunkKeys.Contains(e.Key))
        {
            contextSunkKeys.Add(e.Key);
            e.Handled = true;
            return;
        }
        if (gameProcessed) return;
        if (FindLocalPlayerInteractionRoot() is { } interactionRoot
            && ProximityPromptService.FindNearest(project, interactionRoot, runtime?.ProximityPromptsEnabled ?? true) is { IsIndicatorOnly: false } nearbyPrompt
            && PromptKeyMatches(GetKeyCodeName(e.Key), nearbyPrompt.Prompt.KeyboardKeyCode))
        {
            promptKeysDown.Add(e.Key);
            e.Handled = true;
        }
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
        if (e.Key is Key.W or Key.A or Key.S or Key.D or Key.Space or Key.E) e.Handled = true;
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        var wasContextSunk = contextSunkKeys.Remove(e.Key);
        var contextSunk = runtime?.FireUserInput(GetKeyCodeName(e.Key), false, Keyboard.FocusedElement is TextBox or Button) == true;
        if (wasContextSunk || contextSunk) e.Handled = true;
        if (promptKeysDown.Remove(e.Key))
        {
            e.Handled = true;
            ResetPromptHold();
        }
    }

    private static string GetKeyCodeName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => new[] { "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine" }[(int)key - (int)Key.D0],
        Key.Return => "Return",
        Key.Back => "Backspace",
        Key.LeftCtrl => "LeftControl",
        Key.RightCtrl => "RightControl",
        Key.LeftAlt => "LeftAlt",
        Key.RightAlt => "RightAlt",
        Key.LWin => "LeftWindows",
        Key.RWin => "RightWindows",
        Key.OemMinus => "Minus",
        Key.OemPlus => "Equals",
        Key.OemOpenBrackets => "LeftBracket",
        Key.OemCloseBrackets => "RightBracket",
        Key.OemBackslash => "BackSlash",
        Key.OemSemicolon => "Semicolon",
        Key.OemQuotes => "Quote",
        Key.OemComma => "Comma",
        Key.OemPeriod => "Period",
        Key.OemQuestion => "Slash",
        Key.Oem3 => "Backquote",
        _ => key.ToString()
    };

    private static bool PromptKeyMatches(string pressedKeyCode, string configuredKeyCode) =>
        string.Equals(pressedKeyCode, configuredKeyCode, StringComparison.Ordinal)
        || pressedKeyCode == "Backspace" && configuredKeyCode == "Back";

    private static string FormatPromptInput(SceneNode prompt)
    {
        var gamepad = prompt.GamepadKeyCode switch
        {
            "ButtonA" => "A", "ButtonB" => "B", "ButtonX" => "X", "ButtonY" => "Y",
            "ButtonL1" => "LB", "ButtonR1" => "RB", "ButtonL2" => "LT", "ButtonR2" => "RT",
            "ButtonL3" => "LS", "ButtonR3" => "RS", "ButtonStart" => "Start", "ButtonSelect" => "Back",
            "DPadUp" => "↑", "DPadDown" => "↓", "DPadLeft" => "←", "DPadRight" => "→",
            _ => prompt.GamepadKeyCode
        };
        var input = $"{prompt.KeyboardKeyCode} / {gamepad}";
        return prompt.ClickablePrompt ? $"{input} / Click" : input;
    }

    private void ResetPromptHold()
    {
        if (holdingPromptId is { } promptId)
        {
            if (FindRuntimeNode(promptId) is { Type: "ProximityPrompt", HoldDuration: > 0 })
                runtime?.InvokePromptEvent(promptId, "PromptButtonHoldEnded", localPlayer.Id);
            if (promptTriggered) runtime?.InvokePromptEvent(promptId, "TriggerEnded", localPlayer.Id);
        }
        holdingPromptId = null;
        promptHoldElapsed = 0;
        promptTriggered = false;
    }

    private void GameViewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right) return;
        var point = e.GetPosition(GameViewport);
        var inputType = e.ChangedButton switch
        {
            MouseButton.Left => "MouseButton1",
            MouseButton.Right => "MouseButton2",
            MouseButton.Middle => "MouseButton3",
            _ => string.Empty
        };
        var contextSunk = inputType.Length > 0 && runtime?.FireUserInput("Unknown", true, activeTool is { Enabled: true }, inputType: inputType, mouseX: (float)point.X, mouseY: (float)point.Y) == true;
        if (contextSunk) contextSunkMouseButtons.Add(e.ChangedButton);
        if (contextSunk || contextSunkMouseButtons.Contains(e.ChangedButton))
        {
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left
            && FindLocalPlayerInteractionRoot() is { } interactionRoot
            && ProximityPromptService.FindNearest(project, interactionRoot, runtime?.ProximityPromptsEnabled ?? true) is { IsIndicatorOnly: false, Prompt: { ClickablePrompt: true, PromptStyle: "Default" } prompt })
        {
            clickablePromptDownId = prompt.Id;
            GameViewport.Focus();
            if (prompt.HoldDuration <= 0)
            {
                holdingPromptId = prompt.Id;
                promptHoldElapsed = 0;
                promptTriggered = true;
                runtime?.InvokePromptTriggered(prompt.Id, localPlayer.Id);
            }
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && activeTool is { Enabled: true } tool)
        {
            if (!tool.ManualActivationOnly) runtime?.InvokeToolActivated(tool.Id);
            e.Handled = true;
        }
    }

    private void GameViewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right) return;
        if (e.ChangedButton == MouseButton.Left) clickablePromptDownId = null;
        var point = e.GetPosition(GameViewport);
        var inputType = e.ChangedButton switch
        {
            MouseButton.Left => "MouseButton1",
            MouseButton.Right => "MouseButton2",
            MouseButton.Middle => "MouseButton3",
            _ => string.Empty
        };
        var wasContextSunk = contextSunkMouseButtons.Remove(e.ChangedButton);
        var contextSunk = inputType.Length > 0 && runtime?.FireUserInput("Unknown", false, false, inputType: inputType, mouseX: (float)point.X, mouseY: (float)point.Y) == true;
        if (wasContextSunk || contextSunk)
        {
            e.Handled = true;
            return;
        }
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
            var handleRotation = RotationMath.FromDegrees(handle.Rotation);
            var inverseHandleRotation = Matrix4x4.Transpose(handleRotation);
            foreach (var part in new[] { tool }.Concat(GameProjectStore.Flatten(tool.Children)).Where(SceneNodeTypes.IsBasePart))
            {
                var worldOffset = new Vector3(part.Position[0] - handle.Position[0], part.Position[1] - handle.Position[1], part.Position[2] - handle.Position[2]);
                var localOffset = Vector3.TransformNormal(worldOffset, inverseHandleRotation);
                var rotationOffset = RotationMath.FromDegrees(part.Rotation) * inverseHandleRotation;
                equippedToolParts[part.Id] = new EquippedPartState([localOffset.X, localOffset.Y, localOffset.Z], rotationOffset, (float[])part.Position.Clone(), (float[])part.Rotation.Clone(), part.Anchored, part.CanCollide);
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
        var rootRotation = RotationMath.FromDegrees(character.Root.Rotation);
        var gripRotation = RotationMath.FromDegrees(activeTool.GripRotation) * rootRotation;
        var localGrip = new Vector3(activeTool.GripPosition[0], activeTool.GripPosition[1], activeTool.GripPosition[2]);
        var worldGrip = Vector3.TransformNormal(localGrip, rootRotation);
        var handlePosition = new Vector3(character.Root.Position[0] + worldGrip.X, character.Root.Position[1] + worldGrip.Y, character.Root.Position[2] + worldGrip.Z);
        foreach (var part in new[] { activeTool }.Concat(GameProjectStore.Flatten(activeTool.Children)).Where(SceneNodeTypes.IsBasePart))
        {
            if (!equippedToolParts.TryGetValue(part.Id, out var state)) continue;
            var localOffset = new Vector3(state.LocalOffset[0], state.LocalOffset[1], state.LocalOffset[2]);
            var worldOffset = Vector3.TransformNormal(localOffset, gripRotation);
            var worldPosition = handlePosition + worldOffset;
            var position = new[] { worldPosition.X, worldPosition.Y, worldPosition.Z };
            var rotation = RotationMath.ToDegrees(state.RotationOffset * gripRotation);
            if (part.Position.SequenceEqual(position) && part.Rotation.SequenceEqual(rotation)) continue;
            part.Position = position;
            part.Rotation = rotation;
            SyncCharacterNode(part);
        }
    }

    private static SceneNode? FindToolHandle(SceneNode tool) => tool.Children.FirstOrDefault(node => node.Name == "Handle" && SceneNodeTypes.IsBasePart(node));

    private void RefreshGameGui()
    {
        var rendered = GuiRenderService.Render(
            project,
            true,
            GameGuiCanvas.ActualWidth,
            GameGuiCanvas.ActualHeight,
            activated: node => runtime?.InvokeGuiActivated(node.Id),
            textChanged: (node, value) => runtime?.UpdateGuiTextInput(node.Id, value),
            screenGuiRoot: playerGui,
            propertyChanged: (node, property) => runtime?.SynchronizeExternalProperty(node, property),
            dragSignal: (node, eventName, x, y) => runtime?.InvokeUIDragDetectorSignal(node.Id, eventName, x, y),
            mouseButtonSignal: (node, eventName) => runtime?.InvokeGuiMouseButton(node.Id, eventName),
            textFocusSignal: (node, eventName, enterPressed) => runtime?.InvokeGuiTextFocus(node.Id, eventName, enterPressed));
        var children = rendered.Children.Cast<UIElement>().ToArray();
        rendered.Children.Clear();
        GameGuiCanvas.Children.Clear();
        foreach (var child in children) GameGuiCanvas.Children.Add(child);
        AddBillboardGuis();
        foreach (var border in FindVisualDescendants<Border>(GameGuiCanvas))
        {
            if (border.Tag is not SceneNode node || !IsInteractiveGuiObject(node)) continue;
            border.MouseEnter += (_, _) => runtime?.InvokeGuiHover(node.Id, entered: true);
            border.MouseLeave += (_, _) => runtime?.InvokeGuiHover(node.Id, entered: false);
        }
    }

    private static bool IsInteractiveGuiObject(SceneNode node) => node.Type is
        "Frame" or "ScrollingFrame" or "ViewportFrame" or "VideoFrame" or "ImageLabel" or "ImageButton"
        or "TextLabel" or "TextButton" or "TextBox" or "UICanvasGroup";

    private void AddBillboardGuis()
    {
        if (GameViewport.Camera is not PerspectiveCamera camera) return;
        GuiRenderService.AddBillboards(GameGuiCanvas, project,
            new Vector3((float)camera.Position.X, (float)camera.Position.Y, (float)camera.Position.Z),
            new Vector3((float)camera.LookDirection.X, (float)camera.LookDirection.Y, (float)camera.LookDirection.Z),
            new Vector3((float)camera.UpDirection.X, (float)camera.UpDirection.Y, (float)camera.UpDirection.Z),
            (float)camera.FieldOfView, GameGuiCanvas.ActualWidth, GameGuiCanvas.ActualHeight, true,
            activated: node => runtime?.InvokeGuiActivated(node.Id),
            textChanged: (node, value) => runtime?.UpdateGuiTextInput(node.Id, value),
            propertyChanged: (node, property) => runtime?.SynchronizeExternalProperty(node, property),
            dragSignal: (node, eventName, x, y) => runtime?.InvokeUIDragDetectorSignal(node.Id, eventName, x, y),
            mouseButtonSignal: (node, eventName) => runtime?.InvokeGuiMouseButton(node.Id, eventName),
            textFocusSignal: (node, eventName, enterPressed) => runtime?.InvokeGuiTextFocus(node.Id, eventName, enterPressed));
        GuiRenderService.AddSurfaceGuis(GameGuiCanvas, project,
            new Vector3((float)camera.Position.X, (float)camera.Position.Y, (float)camera.Position.Z),
            new Vector3((float)camera.LookDirection.X, (float)camera.LookDirection.Y, (float)camera.LookDirection.Z),
            new Vector3((float)camera.UpDirection.X, (float)camera.UpDirection.Y, (float)camera.UpDirection.Z),
            (float)camera.FieldOfView, GameGuiCanvas.ActualWidth, GameGuiCanvas.ActualHeight, true,
            activated: node => runtime?.InvokeGuiActivated(node.Id),
            textChanged: (node, value) => runtime?.UpdateGuiTextInput(node.Id, value),
            propertyChanged: (node, property) => runtime?.SynchronizeExternalProperty(node, property),
            dragSignal: (node, eventName, x, y) => runtime?.InvokeUIDragDetectorSignal(node.Id, eventName, x, y),
            mouseButtonSignal: (node, eventName) => runtime?.InvokeGuiMouseButton(node.Id, eventName),
            textFocusSignal: (node, eventName, enterPressed) => runtime?.InvokeGuiTextFocus(node.Id, eventName, enterPressed));
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
        var point = e.GetPosition(GameViewport);
        var contextSunk = runtime?.FireUserInput("Unknown", true, activeTool is { Enabled: true }, inputType: "MouseButton2", mouseX: (float)point.X, mouseY: (float)point.Y) == true;
        if (contextSunk) contextSunkMouseButtons.Add(MouseButton.Right);
        if (contextSunk || contextSunkMouseButtons.Contains(MouseButton.Right))
        {
            e.Handled = true;
            return;
        }
        isDragging = true;
        lastPointer = point;
        Mouse.Capture(GameViewport);
        GameViewport.Focus();
        e.Handled = true;
    }

    private void Viewport_RightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(GameViewport);
        var wasContextSunk = contextSunkMouseButtons.Remove(MouseButton.Right);
        var contextSunk = runtime?.FireUserInput("Unknown", false, false, inputType: "MouseButton2", mouseX: (float)point.X, mouseY: (float)point.Y) == true;
        isDragging = false;
        Mouse.Capture(null);
        if (wasContextSunk || contextSunk)
        {
            e.Handled = true;
            return;
        }
        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(GameViewport);
        if (runtime?.FireMouseMovement((float)point.X, (float)point.Y) == true)
        {
            if (!isDragging) SetHoveredClickDetector(null);
            if (isDragging) lastPointer = point;
            e.Handled = true;
            return;
        }
        if (!isDragging)
        {
            var detector = FindPointedClickDetector();
            SetHoveredClickDetector(detector);
            GameViewport.Cursor = detector is null ? Cursors.Arrow : Cursors.Hand;
            return;
        }
        renderer.SetCameraOrbit(-(float)(point.X - lastPointer.X) * .25f, -(float)(point.Y - lastPointer.Y) * .25f, 0, project);
        lastPointer = point;
        e.Handled = true;
    }

    private void Viewport_MouseLeave(object sender, MouseEventArgs e)
    {
        SetHoveredClickDetector(null);
        GameViewport.Cursor = Cursors.Arrow;
    }

    private void SetHoveredClickDetector(SceneNode? detector)
    {
        var nextId = detector?.Id;
        if (hoveredClickDetectorId == nextId) return;
        if (hoveredClickDetectorId is { } previousId)
            runtime?.InvokeClickDetectorHover(previousId, localPlayer.Id, entered: false);
        hoveredClickDetectorId = nextId;
        if (nextId is not null)
            runtime?.InvokeClickDetectorHover(nextId, localPlayer.Id, entered: true);
    }

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
        var interactionRoot = FindLocalPlayerInteractionRoot();
        if (interactionRoot is null) return null;
        var hit = GameViewport.FindHits(Mouse.GetPosition(GameViewport)).FirstOrDefault()?.ModelHit;
        if (hit is not MeshGeometryModel3D model) return null;
        var partId = renderer.Visuals.FirstOrDefault(entry => ReferenceEquals(entry.Value, model)).Key;
        if (partId is null) return null;
        var part = GameProjectStore.Flatten(project.Workspace.Children).FirstOrDefault(node => node.Id == partId);
        return part is null ? null : ClickDetectorService.FindActive(part, interactionRoot);
    }
}
