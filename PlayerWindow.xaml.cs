using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BullerStudio.Models;
using BullerStudio.Services;

namespace BullerStudio;

public partial class PlayerWindow : Window
{
    private readonly GameProject project;
    private readonly SceneRenderService renderer;
    private readonly LuaXRuntime runtime;
    private readonly DispatcherTimer updateTimer;
    private bool isDragging;
    private Point lastPointer;

    public PlayerWindow(GameProject project)
    {
        InitializeComponent();
        this.project = project;
        GameTitle.Text = project.Name;
        Title = project.Name;
        renderer = new SceneRenderService(GameViewport, project.Workspace.Children.First(node => node.Type == "Camera"));
        renderer.Refresh(project);
        runtime = new LuaXRuntime(project,
            (node, _, _) => Dispatcher.Invoke(() => { renderer.UpdateNode(node, project); renderer.UpdateCamera(project); }),
            message => Dispatcher.Invoke(() => Title = $"{project.Name} — {message}"));
        runtime.RunAll();
        updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        updateTimer.Tick += (_, _) => runtime.UpdateTouched();
        updateTimer.Start();
        Closed += (_, _) => updateTimer.Stop();
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
        if (!isDragging) return;
        var point = e.GetPosition(GameViewport);
        renderer.SetCameraOrbit((float)(point.X - lastPointer.X) * .25f, (float)(point.Y - lastPointer.Y) * .25f, 0, project);
        lastPointer = point;
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        renderer.SetCameraOrbit(0, 0, e.Delta > 0 ? -1 : 1, project);
    }
}
