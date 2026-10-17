using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BullerStudio.Models;
using BullerStudio.Services;
using HelixToolkit.Wpf.SharpDX;
using Microsoft.Win32;

namespace BullerStudio;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ProjectSummary> projects = [];
    private readonly Stack<string> undo = new();
    private readonly Stack<string> redo = new();
    private readonly GameBuildService buildService = new();
    private GameProject? project;
    private SceneRenderService? renderer;
    private SceneNode? selected;
    private GameScript? selectedScript;
    private TextBox? scriptEditor;
    private TextBox? outputBox;
    private bool included = true;
    private bool isRestoring;
    private string currentSnapshot = string.Empty;
    private bool isRightDragging;
    private Point lastPointer;
    private Border? draggingPanel;
    private Point dragStart;
    private readonly Dictionary<Border, string> dockSlots = new();

    public MainWindow()
    {
        InitializeComponent();
        IncludedButton.Background = (Brush)FindResource("Accent");
        ExcludedButton.Background = (Brush)FindResource("PanelRaised");
        dockSlots[ExplorerPanel] = "left";
        dockSlots[ScenePanel] = "centerTop";
        dockSlots[PropertiesPanel] = "right";
        dockSlots[BottomPanel] = "centerBottom";
        ProjectList.ItemsSource = projects;
        Loaded += async (_, _) => await ShowProjectBrowserAsync();
    }

    private async Task ShowProjectBrowserAsync()
    {
        ProjectBrowser.Visibility = Visibility.Visible;
        projects.Clear();
        foreach (var item in await GameProjectStore.ListAsync()) projects.Add(item);
    }

    private async void CreateProject_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt("New project", "Project name", "My Game");
        if (string.IsNullOrWhiteSpace(name)) return;
        try { await OpenProjectAsync(await GameProjectStore.CreateAsync(name)); }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path) return;
        try { await OpenProjectAsync(await GameProjectStore.LoadAsync(path)); }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task OpenProjectAsync(GameProject next)
    {
        project = next;
        selected = null;
        selectedScript = null;
        undo.Clear(); redo.Clear();
        currentSnapshot = JsonSerializer.Serialize(next, GameProjectStore.JsonOptions);
        TitleProjectPath.Text = next.ProjectPath;
        ProjectBrowser.Visibility = Visibility.Collapsed;
        renderer = new SceneRenderService(SceneViewport, next.Workspace.Children.First(node => node.Type == "Camera"));
        RefreshEditor();
        await GameProjectStore.SaveAsync(next);
        SetStatus($"Opened {next.Name}");
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveProjectAsync();

    private async Task SaveProjectAsync()
    {
        if (project is null) return;
        try
        {
            CommitScriptEditor();
            await GameProjectStore.SaveAsync(project);
            SetStatus("Saved");
            await ReloadProjectListAsync();
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task ReloadProjectListAsync()
    {
        projects.Clear();
        foreach (var item in await GameProjectStore.ListAsync()) projects.Add(item);
    }

    private void RefreshEditor()
    {
        if (project is null) return;
        GameProjectStore.EnsureProject(project);
        ExplorerTree.ItemsSource = new[] { project.Workspace };
        ObjectCount.Text = GameProjectStore.Flatten(project.Workspace.Children).Count.ToString();
        renderer?.Refresh(project);
        RenderProperties();
        ShowAssets();
        if (selected is not null) SelectTreeNode(selected.Id);
    }

    private void ExplorerTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        selected = e.NewValue as SceneNode;
        RenderProperties();
    }

    private void RenderProperties()
    {
        PropertyEditor.Children.Clear();
        if (project is null || selected is null) return;
        AddLabel($"{selected.Type}  ·  {selected.Name}");
        AddTextField("Name", selected.Name);
        if (selected.Type is "Part" or "MeshPart" or "ImageLabel" or "Camera")
        {
            AddTextField("Position", VectorText(selected.Position));
            AddTextField("Rotation", VectorText(selected.Rotation));
            if (selected.Type != "Camera")
            {
                AddTextField("Size", VectorText(selected.Size));
                AddTextField("Color", selected.Color);
                AddCheckField("Anchored", selected.Anchored);
                AddCheckField("CastShadow", selected.CastShadow);
            }
            if (selected.Type is "MeshPart" or "ImageLabel") AddTextField("Asset", selected.Asset);
            if (selected.Type == "Camera")
            {
                AddComboField("Mode", ["Custom", "Subject"], selected.CameraMode);
                var subjects = GameProjectStore.Flatten(project.Workspace.Children).Where(node => node.Type is "Part" or "MeshPart").Select(node => $"{node.Id} | {node.Name}").Prepend("").ToArray();
                AddComboField("Subject", subjects, subjects.FirstOrDefault(value => value.StartsWith(selected.Subject + " | ", StringComparison.Ordinal)) ?? "");
                AddTextField("FieldOfView", selected.FieldOfView.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddLabel("The Workspace Camera is the only view camera.");
                AddLabel("Subject mode orbits a BasePart; Custom uses Position and Rotation.");
            }
        }
        if (selected.Type == "Script")
        {
            var script = project.Scripts.FirstOrDefault(item => item.Id == selected.Id);
            if (script is not null) ShowScript(script);
        }
    }

    private void AddLabel(string text) => PropertyEditor.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 3), Foreground = (Brush)FindResource("TextMuted"), TextWrapping = TextWrapping.Wrap });

    private void AddTextField(string property, string value)
    {
        var label = new TextBlock { Text = property, Margin = new Thickness(0, 8, 0, 3), Foreground = (Brush)FindResource("TextMuted"), FontSize = 11 };
        var box = new TextBox { Text = value, Tag = property, MinWidth = 170 };
        box.LostFocus += PropertyText_LostFocus;
        box.KeyDown += (_, args) => { if (args.Key == Key.Enter) { ApplyTextProperty(box); Keyboard.ClearFocus(); } };
        PropertyEditor.Children.Add(label); PropertyEditor.Children.Add(box);
    }

    private void AddCheckField(string property, bool value)
    {
        var box = new CheckBox { Content = property, IsChecked = value, Margin = new Thickness(0, 8, 0, 2), Tag = property, Foreground = (Brush)FindResource("TextPrimary") };
        box.Checked += PropertyCheck_Changed; box.Unchecked += PropertyCheck_Changed;
        PropertyEditor.Children.Add(box);
    }

    private void AddComboField(string property, IEnumerable<string> values, string selectedValue)
    {
        PropertyEditor.Children.Add(new TextBlock { Text = property, Margin = new Thickness(0, 8, 0, 3), Foreground = (Brush)FindResource("TextMuted"), FontSize = 11 });
        var combo = new ComboBox { ItemsSource = values, SelectedItem = selectedValue, Tag = property, Margin = new Thickness(0, 0, 0, 3) };
        combo.SelectionChanged += PropertyCombo_Changed;
        PropertyEditor.Children.Add(combo);
    }

    private void PropertyText_LostFocus(object sender, RoutedEventArgs e) { if (!isRestoring && sender is TextBox box) ApplyTextProperty(box); }

    private void ApplyTextProperty(TextBox box)
    {
        if (project is null || selected is null || box.Tag is not string property) return;
        var text = box.Text.Trim();
        if (property is "Position" or "Rotation" or "Size")
        {
            var values = ParseVector(text);
            if (values is null) { SetStatus("Enter three comma-separated numbers."); box.Text = property switch { "Position" => VectorText(selected.Position), "Rotation" => VectorText(selected.Rotation), _ => VectorText(selected.Size) }; return; }
            if (property == "Position") selected.Position = values;
            if (property == "Rotation") selected.Rotation = values;
            if (property == "Size") selected.Size = values.Select(value => Math.Max(.01f, Math.Abs(value))).ToArray();
        }
        else if (property == "FieldOfView" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fov)) selected.FieldOfView = Math.Clamp(fov, 10, 120);
        else if (property == "Color") selected.Color = text;
        else if (property == "Asset") selected.Asset = text;
        else if (property == "Name" && !string.IsNullOrWhiteSpace(text)) selected.Name = text;
        else return;
        renderer?.UpdateNode(selected, project);
        if (property == "Name") RefreshEditor(); else RecordEdit();
    }

    private void PropertyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (project is null || selected is null || sender is not CheckBox box || box.Tag is not string property) return;
        if (property == "Anchored") selected.Anchored = box.IsChecked == true;
        if (property == "CastShadow") selected.CastShadow = box.IsChecked == true;
        RecordEdit();
    }

    private void PropertyCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (project is null || selected is null || sender is not ComboBox combo || combo.Tag is not string property || combo.SelectedItem is not string value) return;
        if (property == "Mode") selected.CameraMode = value;
        if (property == "Subject") selected.Subject = value.Split(" | ", 2, StringSplitOptions.None)[0];
        renderer?.UpdateCamera(project);
        RecordEdit();
        RenderProperties();
    }

    private async void AddPart_Click(object sender, RoutedEventArgs e) => await AddNodeAsync("Part", "Part");
    private async void AddMesh_Click(object sender, RoutedEventArgs e) => await AddNodeAsync("MeshPart", "MeshPart");
    private async void AddImage_Click(object sender, RoutedEventArgs e) => await AddNodeAsync("ImageLabel", "ImageLabel");
    private async void AddFolder_Click(object sender, RoutedEventArgs e) => await AddNodeAsync("Folder", "Folder");

    private async Task AddNodeAsync(string type, string name)
    {
        if (project is null) return;
        var parent = selected is { Type: "Folder" or "Workspace" } ? selected : project.Workspace;
        var node = new SceneNode { Type = type, Name = name };
        if (type == "Part") node.Position = [0, 1, 0];
        parent.Children.Add(node);
        RecordEdit(); RefreshEditor();
        SelectTreeNode(node.Id);
        await GameProjectStore.SaveAsync(project);
    }

    private async void AddScript_Click(object sender, RoutedEventArgs e)
    {
        if (project is null) return;
        var script = new GameScript { Name = $"Script{project.Scripts.Count + 1}" };
        project.Scripts.Add(script);
        var node = new SceneNode { Id = script.Id, Type = "Script", Name = script.Name };
        (selected is { Type: "Folder" or "Workspace" } ? selected : project.Workspace).Children.Add(node);
        RecordEdit(); RefreshEditor(); SelectTreeNode(node.Id); ShowScript(script);
        await GameProjectStore.SaveAsync(project);
    }

    private async void ImportAssets_Click(object sender, RoutedEventArgs e)
    {
        if (project is null) return;
        var picker = new OpenFileDialog { Multiselect = true, Title = "Import game assets", Filter = "Supported render assets|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.obj|All files|*.*" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            await GameProjectStore.ImportAssetsAsync(project, picker.FileNames);
            ShowAssets(); SetStatus($"Imported {picker.FileNames.Length} asset(s)");
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void ShowAssets()
    {
        if (project is null) return;
        var list = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = (Brush)FindResource("TextPrimary") };
        foreach (var asset in project.Assets) list.Items.Add($"{asset.Name}   ·   {asset.Type}   ·   {asset.Size:N0} bytes");
        BottomContent.Content = list;
        AssetsTab.Background = (Brush)FindResource("PanelRaised");
    }

    private void ShowScript(GameScript script)
    {
        CommitScriptEditor();
        selectedScript = script;
        var panel = new Grid { Margin = new Thickness(6) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var scripts = new ListBox { ItemsSource = project?.Scripts, DisplayMemberPath = "Name", Background = Brushes.Transparent, Foreground = (Brush)FindResource("TextPrimary") };
        scripts.SelectedItem = script;
        scripts.SelectionChanged += (_, args) => { if (args.AddedItems.Count > 0 && args.AddedItems[0] is GameScript item) ShowScript(item); };
        Grid.SetColumn(scripts, 0); panel.Children.Add(scripts);
        var editorPanel = new DockPanel { Margin = new Thickness(7, 0, 0, 0) };
        var run = new Button { Content = "▶ Run Lua X", HorizontalAlignment = HorizontalAlignment.Right };
        run.Click += (_, _) => RunScript(script);
        DockPanel.SetDock(run, Dock.Top); editorPanel.Children.Add(run);
        scriptEditor = new TextBox { Text = script.Source, AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap, Background = new SolidColorBrush(Color.FromRgb(17, 24, 36)), Foreground = (Brush)FindResource("TextPrimary") };
        editorPanel.Children.Add(scriptEditor);
        Grid.SetColumn(editorPanel, 1); panel.Children.Add(editorPanel);
        BottomContent.Content = panel;
    }

    private void RunScript(GameScript script)
    {
        if (project is null) return;
        CommitScriptEditor();
        var runtime = new LuaXRuntime(project,
            (node, _, _) => Dispatcher.Invoke(() => renderer?.UpdateNode(node, project)),
            message => Dispatcher.Invoke(() => AppendOutput(message)));
        runtime.Run(script.Id);
        renderer?.Refresh(project);
    }

    private void AssetsTab_Click(object sender, RoutedEventArgs e) => ShowAssets();
    private void LuaTab_Click(object sender, RoutedEventArgs e)
    {
        if (project is null) return;
        LuaTab.Background = (Brush)FindResource("PanelRaised");
        var script = selectedScript ?? project.Scripts.FirstOrDefault();
        if (script is null) { BottomContent.Content = new TextBlock { Text = "Add a Script object to write Lua X scripts.", Margin = new Thickness(12), Foreground = (Brush)FindResource("TextMuted") }; return; }
        ShowScript(script);
    }

    private void OutputTab_Click(object sender, RoutedEventArgs e)
    {
        outputBox = new TextBox { IsReadOnly = true, AcceptsReturn = true, Text = "Build and Lua X messages appear here.", Background = Brushes.Transparent, Foreground = (Brush)FindResource("TextPrimary"), FontFamily = new FontFamily("Consolas") };
        BottomContent.Content = outputBox;
    }

    private void AppendOutput(string message)
    {
        if (outputBox is null) return;
        outputBox.AppendText(message + Environment.NewLine);
        outputBox.ScrollToEnd();
    }

    private void CommitScriptEditor()
    {
        if (selectedScript is null || scriptEditor is null || selectedScript.Source == scriptEditor.Text) return;
        selectedScript.Source = scriptEditor.Text;
        RecordEdit();
    }

    private async void Build_Click(object sender, RoutedEventArgs e) => await BuildAsync();
    private async Task BuildAsync()
    {
        if (project is null) return;
        try
        {
            CommitScriptEditor();
            SetStatus("Building game…");
            var executable = await buildService.BuildAsync(project, included, SetStatus);
            SetStatus($"Build complete: {executable}");
            MessageBox.Show(this, $"Game built successfully:\n{executable}", "Build complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void Included_Click(object sender, RoutedEventArgs e) { included = true; IncludedButton.Background = (Brush)FindResource("Accent"); ExcludedButton.Background = (Brush)FindResource("PanelRaised"); SetStatus("Build mode: Included assets"); }
    private void Excluded_Click(object sender, RoutedEventArgs e) { included = false; ExcludedButton.Background = (Brush)FindResource("Accent"); IncludedButton.Background = (Brush)FindResource("PanelRaised"); SetStatus("Build mode: Excluded assets"); }

    private void ProjectOptions_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path) return;
        var menu = new ContextMenu();
        var build = new MenuItem { Header = "Build / compile" }; build.Click += async (_, _) => { try { var game = await GameProjectStore.LoadAsync(path); project = game; await BuildAsync(); } catch (Exception error) { ShowError(error); } };
        var delete = new MenuItem { Header = "Delete project" }; delete.Click += async (_, _) =>
        {
            var safeRoot = Path.GetFullPath(GameProjectStore.ProjectsRoot) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(path);
            if (!target.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(this, "Project folder is outside the projects directory."); return; }
            if (MessageBox.Show(this, "Delete this project and all of its assets?", "Delete project", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try { Directory.Delete(target, true); await ReloadProjectListAsync(); } catch (Exception error) { ShowError(error); }
        };
        menu.Items.Add(build); menu.Items.Add(delete); menu.PlacementTarget = sender as UIElement; menu.IsOpen = true;
    }

    private async void ShowProjects_Click(object sender, RoutedEventArgs e) => await ShowProjectBrowserAsync();
    private void Help_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "Buller Studio saves projects locally. Game scripts run with Lua X: use SetProperty(id, 'Position', {X=0,Y=2,Z=0}), WaitForChild(workspace, 'Part'), and part.Touched:Connect(function(other) ... end). Camera Mode is Custom or Subject.", "Buller Studio Help");
    private void Undo_Click(object sender, RoutedEventArgs e) => UndoEdit();
    private void Redo_Click(object sender, RoutedEventArgs e) => RedoEdit();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key == Key.S) { _ = SaveProjectAsync(); e.Handled = true; }
            else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { RedoEdit(); e.Handled = true; }
            else if (e.Key == Key.Z) { UndoEdit(); e.Handled = true; }
            return;
        }
        if (e.Key == Key.Delete && Keyboard.FocusedElement is not TextBox && Keyboard.FocusedElement is not ComboBox)
        {
            DeleteSelected(); e.Handled = true; return;
        }
        if (isRightDragging && project is not null && renderer is not null && (e.Key is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E))
        {
            var direction = e.Key switch { Key.W => new SharpDX.Vector3(0, 0, -1), Key.S => new SharpDX.Vector3(0, 0, 1), Key.A => new SharpDX.Vector3(-1, 0, 0), Key.D => new SharpDX.Vector3(1, 0, 0), Key.Q => new SharpDX.Vector3(0, -1, 0), _ => new SharpDX.Vector3(0, 1, 0) };
            renderer.MoveCamera(direction, .5f, project); e.Handled = true;
        }
    }

    private void SceneViewport_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) { isRightDragging = true; lastPointer = e.GetPosition(SceneViewport); SceneViewport.Focus(); Mouse.Capture(SceneViewport); e.Handled = true; }
    private void SceneViewport_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e) { isRightDragging = false; Mouse.Capture(null); e.Handled = true; }
    private void SceneViewport_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!isRightDragging || renderer is null || project is null) return;
        var point = e.GetPosition(SceneViewport); var dx = point.X - lastPointer.X; var dy = point.Y - lastPointer.Y; lastPointer = point;
        if (project.Workspace.Children.FirstOrDefault(node => node.Type == "Camera") is { } camera)
        {
            if (camera.CameraMode == "Custom") { camera.Rotation[1] += (float)dx * .25f; camera.Rotation[0] = Math.Clamp(camera.Rotation[0] - (float)dy * .25f, -89, 89); renderer.UpdateCamera(project); }
            else renderer.SetCameraOrbit((float)dx * .25f, (float)dy * .25f, 0, project);
        }
    }
    private void SceneViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e) { renderer?.SetCameraOrbit(0, 0, e.Delta > 0 ? -1 : 1, project); }
    private void SceneViewport_MouseDown3D(object sender, RoutedEventArgs e)
    {
        if (renderer is null || project is null || isRightDragging) return;
        var hit = SceneViewport.FindHits(Mouse.GetPosition(SceneViewport)).FirstOrDefault()?.ModelHit;
        if (hit is not MeshGeometryModel3D model) return;
        var id = renderer.Visuals.FirstOrDefault(entry => ReferenceEquals(entry.Value, model)).Key;
        if (string.IsNullOrEmpty(id)) return;
        selected = GameProjectStore.Flatten(project.Workspace.Children).FirstOrDefault(node => node.Id == id);
        SelectTreeNode(id);
        RenderProperties();
    }

    private void DockHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        draggingPanel = FindPanel(sender as DependencyObject);
        if (draggingPanel is null) return;
        dragStart = e.GetPosition(WorkspaceGrid); draggingPanel.CaptureMouse();
    }
    private void DockHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (draggingPanel is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(WorkspaceGrid);
        if (Math.Abs(point.X - dragStart.X) + Math.Abs(point.Y - dragStart.Y) > 18) draggingPanel.Opacity = .65;
    }
    private void DockHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (draggingPanel is null) return;
        var point = e.GetPosition(WorkspaceGrid);
        var slot = point.X < WorkspaceGrid.ActualWidth * .23 ? "left" : point.X > WorkspaceGrid.ActualWidth * .78 ? "right" : point.Y > WorkspaceGrid.ActualHeight * .72 ? "centerBottom" : "centerTop";
        var occupied = dockSlots.FirstOrDefault(entry => entry.Value == slot).Key;
        if (occupied is not null && occupied != draggingPanel)
        {
            dockSlots[occupied] = dockSlots[draggingPanel];
            dockSlots[draggingPanel] = slot;
            ApplyDockSlots();
        }
        draggingPanel.Opacity = 1; draggingPanel.ReleaseMouseCapture(); draggingPanel = null;
    }

    private void ApplyDockSlots()
    {
        foreach (var (panel, slot) in dockSlots)
        {
            var column = slot == "left" ? 0 : slot == "right" ? 2 : 1;
            var row = slot == "centerBottom" ? 1 : 0;
            Grid.SetColumn(panel, column); Grid.SetRow(panel, row); Grid.SetRowSpan(panel, slot is "left" or "right" ? 2 : 1);
        }
    }

    private static Border? FindPanel(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Border { Tag: "DockPanel" } panel) return panel;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void DeleteSelected()
    {
        if (project is null || selected is null || selected.Type == "Camera") return;
        if (MessageBox.Show(this, $"Delete {selected.Name}?", "Delete object", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var removeId = selected.Id;
        GameProjectStore.RemoveNode(project.Workspace.Children, removeId);
        project.Scripts.RemoveAll(script => script.Id == removeId);
        selected = null;
        RecordEdit(); RefreshEditor();
    }

    private void RecordEdit()
    {
        if (project is null || isRestoring) return;
        if (!string.IsNullOrEmpty(currentSnapshot)) undo.Push(currentSnapshot);
        currentSnapshot = JsonSerializer.Serialize(project, GameProjectStore.JsonOptions);
        redo.Clear();
        SetStatus("Modified");
    }
    private void UndoEdit()
    {
        if (project is null || undo.Count == 0) return;
        redo.Push(JsonSerializer.Serialize(project, GameProjectStore.JsonOptions)); RestoreSnapshot(undo.Pop());
    }
    private void RedoEdit()
    {
        if (project is null || redo.Count == 0) return;
        undo.Push(JsonSerializer.Serialize(project, GameProjectStore.JsonOptions)); RestoreSnapshot(redo.Pop());
    }
    private void RestoreSnapshot(string snapshot)
    {
        if (project is null) return;
        var restored = JsonSerializer.Deserialize<GameProject>(snapshot, GameProjectStore.JsonOptions);
        if (restored is null) return;
        restored.ProjectPath = project.ProjectPath; project = restored; currentSnapshot = JsonSerializer.Serialize(project, GameProjectStore.JsonOptions); isRestoring = true; RefreshEditor(); isRestoring = false;
    }

    private void SelectTreeNode(string id)
    {
        var item = FindTreeItem(ExplorerTree, id);
        if (item is not null) { item.IsSelected = true; item.BringIntoView(); }
    }
    private static TreeViewItem? FindTreeItem(ItemsControl parent, string id)
    {
        foreach (var data in parent.Items)
        {
            var container = parent.ItemContainerGenerator.ContainerFromItem(data) as TreeViewItem;
            if (container is null) continue;
            container.IsExpanded = true;
            if (data is SceneNode node && node.Id == id) return container;
            var nested = FindTreeItem(container, id); if (nested is not null) return nested;
        }
        return null;
    }

    private static float[]? ParseVector(string value)
    {
        var parts = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3) return null;
        var result = new float[3];
        for (var index = 0; index < 3; index++) if (!float.TryParse(parts[index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result[index])) return null;
        return result;
    }
    private static string VectorText(float[] value) => string.Join(", ", value.Select(number => number.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
    private void SetStatus(string message) => Dispatcher.Invoke(() => StatusText.Text = message);
    private void ShowError(Exception exception) => MessageBox.Show(this, exception.Message, "Buller Studio", MessageBoxButton.OK, MessageBoxImage.Error);

    private static string? Prompt(string title, string label, string initial)
    {
        var dialog = new Window { Title = title, Width = 360, Height = 150, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = new SolidColorBrush(Color.FromRgb(26, 34, 49)), Foreground = Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        var text = new TextBox { Text = initial }; panel.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "Create", IsDefault = true }; ok.Click += (_, _) => dialog.DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons); dialog.Content = panel;
        return dialog.ShowDialog() == true ? text.Text : null;
    }
}
