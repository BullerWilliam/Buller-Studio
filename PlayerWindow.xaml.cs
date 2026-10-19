using System.Windows;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly SeatInteractionService? seats;
    private readonly SoundPlaybackService soundPlayback;
    private LuaXRuntime? runtime;
    private readonly DispatcherTimer updateTimer;
    private bool isDragging;
    private Point lastPointer;
    private long lastUpdateTimestamp;
    private bool jumpRequested;
    private bool promptRequested;

    public PlayerWindow(GameProject project)
    {
        InitializeComponent();
        GameGuiCanvas.SizeChanged += (_, _) => RefreshGameGui();
        this.project = project;
        GameProjectStore.EnsureProject(project);
        project.Players.Children.RemoveAll(node => node.Type == "Player" && node.Name == "LocalPlayer");
        localPlayer = new SceneNode { Type = "Player", Name = "LocalPlayer" };
        project.Players.Children.Add(localPlayer);
        if (project.Workspace.AutoManageCharacter)
        {
            character = new PlayerCharacterService(project);
            localPlayer.Character = character.Model.Id;
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
        var gameRuntime = new LuaXRuntime(project,
            (node, property, _) => Dispatcher.Invoke(() =>
            {
                if (node.Type == "Sound") return;
                if (property == "Parent" || node.Type == "Workspace" && property.StartsWith("Skybox", StringComparison.Ordinal)) renderer.Refresh(project);
                else renderer.UpdateNode(node, project);
                renderer.UpdateCamera(project);
                if (node.Type == "TextBox" && property == "Text") UpdateGameTextBox(node);
                else RefreshGameGui();
            }),
            message => Dispatcher.Invoke(() => Title = $"{project.Name} — {message}"));
        runtime = gameRuntime;
        gameRuntime.RunAll();
        physics = new PhysicsSimulationService(project, node =>
        {
            runtime?.SynchronizeExternalProperty(node, "Position");
            runtime?.SynchronizeExternalProperty(node, "Velocity");
            renderer.UpdateNode(node, project);
            renderer.UpdateCamera(project);
        });
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
        var canMove = IsActive && Keyboard.FocusedElement is not TextBox && Keyboard.FocusedElement is not Button;
        var forward = canMove ? (Keyboard.IsKeyDown(Key.W) ? 1 : 0) - (Keyboard.IsKeyDown(Key.S) ? 1 : 0) : 0;
        var strafe = canMove ? (Keyboard.IsKeyDown(Key.D) ? 1 : 0) - (Keyboard.IsKeyDown(Key.A) ? 1 : 0) : 0;
        if (seats is not null)
            foreach (var node in seats.BeforePhysics(canMove && jumpRequested)) SyncSeatNode(node);
        if (seats?.IsSeated != true)
            character?.ApplyInput(physics, renderer.CameraYaw, forward, strafe, canMove && jumpRequested);
        jumpRequested = false;
        if (character is not null) runtime?.SynchronizeExternalProperty(character.Root, "Velocity");
        physics.Step(elapsed);
        if (seats is not null)
            foreach (var node in seats.AfterPhysics()) SyncSeatNode(node);
        runtime?.UpdateHumanoidDeaths();
        if (character?.NeedsRespawn == true)
        {
            if (seats is not null)
                foreach (var node in seats.BeforePhysics(true)) SyncSeatNode(node);
            foreach (var node in character.Respawn()) SyncCharacterNode(node);
            runtime?.SynchronizeExternalProperty(character.Humanoid, "Health");
        }
        else if (character is not null)
        {
            foreach (var node in character.UpdatePose()) SyncCharacterNode(node);
        }
        runtime?.UpdateTouched(elapsed);
        var nearbyPrompt = character is null ? null : ProximityPromptService.FindNearest(project, character.Root);
        PromptBorder.Visibility = nearbyPrompt is null ? Visibility.Collapsed : Visibility.Visible;
        if (nearbyPrompt is not null)
        {
            PromptObjectText.Text = string.IsNullOrWhiteSpace(nearbyPrompt.Prompt.ObjectText)
                ? nearbyPrompt.Parent.Name : nearbyPrompt.Prompt.ObjectText;
            PromptActionText.Text = string.IsNullOrWhiteSpace(nearbyPrompt.Prompt.ActionText)
                ? "Interact" : nearbyPrompt.Prompt.ActionText;
            if (promptRequested) runtime?.InvokePromptTriggered(nearbyPrompt.Prompt.Id, localPlayer.Id);
        }
        promptRequested = false;
        soundPlayback.Update(character?.Root.Position ?? project.Workspace.Children.First(node => node.Type == "Camera").Position);
    }

    private void SyncCharacterNode(SceneNode node)
    {
        runtime?.SynchronizeExternalProperty(node, "Position");
        runtime?.SynchronizeExternalProperty(node, "Rotation");
        runtime?.SynchronizeExternalProperty(node, "Velocity");
        runtime?.SynchronizeExternalProperty(node, "Anchored");
        renderer.UpdateNode(node, project);
    }

    private void SyncSeatNode(SceneNode node)
    {
        if (node.Type == "Seat") runtime?.SynchronizeExternalProperty(node, "Occupant");
        else SyncCharacterNode(node);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or Button) return;
        if (e.Key == Key.Space && !e.IsRepeat) jumpRequested = true;
        if (e.Key == Key.E && !e.IsRepeat) promptRequested = true;
        if (e.Key is Key.W or Key.A or Key.S or Key.D or Key.Space or Key.E) e.Handled = true;
    }

    private void RefreshGameGui()
    {
        var rendered = GuiRenderService.Render(
            project,
            true,
            GameGuiCanvas.ActualWidth,
            GameGuiCanvas.ActualHeight,
            activated: node => runtime?.InvokeGuiActivated(node.Id),
            textChanged: (node, value) => runtime?.UpdateGuiTextInput(node.Id, value));
        var children = rendered.Children.Cast<UIElement>().ToArray();
        rendered.Children.Clear();
        GameGuiCanvas.Children.Clear();
        foreach (var child in children) GameGuiCanvas.Children.Add(child);
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
