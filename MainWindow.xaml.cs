using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BullerStudio.Models;
using BullerStudio.Services;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using HelixToolkit.Wpf.SharpDX;
using Microsoft.Win32;
using AvalonDock.Layout;
using AvalonDock.Themes;

namespace BullerStudio;

public partial class MainWindow : Window
{
    private readonly Stack<string> undo = new();
    private readonly Stack<string> redo = new();
    private readonly Dictionary<string, TextEditor> scriptEditors = new(StringComparer.Ordinal);
    private readonly GameBuildService buildService = new();
    private readonly DispatcherTimer cameraMoveTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer explorerAutoScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private PlayerWindow? playerWindow;
    private GameProject? project;
    private SceneRenderService? renderer;
    private SceneNode? selected;
    private GameScript? selectedScript;
    private TextEditor? scriptEditor;
    private TextBox? outputBox;
    private StackPanel? propertyTarget;
    private bool included = true;
    private bool isRestoring;
    private bool isSuppressingExplorerSelection;
    private string currentSnapshot = string.Empty;
    private bool isRightDragging;
    private bool isTransformDragging;
    private bool transformChanged;
    private bool isGuiTransformDragging;
    private bool guiTransformChanged;
    private string selectedTool = "Select";
    private string activeRibbonTab = "HOME";
    private string assetManagerSearch = string.Empty;
    private string assetManagerFolder = string.Empty;
    private string assetManagerFilter = "ALL";
    private SceneNode? explorerDragSource;
    private Point explorerDragStart;
    private Border? explorerDropTargetHeader;
    private double explorerAutoScrollDirection;
    private string? propertyPickerTargetId;
    private string? propertyPickerProperty;
    private CompletionWindow? activeCompletionWindow;
    private InsightWindow? activeSignatureWindow;
    private TextEditor? activeSignatureEditor;
    private Point lastPointer;
    private Point lastCameraPointer;
    private Point lastGuiPointer;
    private DateTime lastCameraMoveAt;
    private static readonly IHighlightingDefinition LuaXHighlighting = CreateLuaXHighlighting();

    public MainWindow()
    {
        InitializeComponent();
        cameraMoveTimer.Tick += CameraMoveTimer_Tick;
        explorerAutoScrollTimer.Tick += ExplorerAutoScrollTimer_Tick;
        Deactivated += (_, _) => { StopCameraMovement(); StopGuiTransform(); StopExplorerAutoScroll(); };
        GuiPreviewCanvas.SizeChanged += (_, _) => RefreshGuiPreview();
        StateChanged += (_, _) => UpdateWindowStateButton();
        UpdateWindowStateButton();
        DockManager.Theme = new Vs2013DarkTheme();
        DockManager.ActiveContentChanged += (_, _) => HandleActiveDocumentChanged();
        IncludedButton.Background = (Brush)FindResource("Accent");
        ExcludedButton.Background = (Brush)FindResource("PanelRaised");
        SelectRibbonTab("HOME");
        SetTool("Select");
    }

    public Task OpenProjectAsync(GameProject next)
    {
        project = next;
        assetManagerFolder = string.Empty;
        assetManagerSearch = string.Empty;
        assetManagerFilter = "ALL";
        selected = next.Workspace;
        selectedScript = null;
        scriptEditor = null;
        scriptEditors.Clear();
        undo.Clear(); redo.Clear();
        currentSnapshot = JsonSerializer.Serialize(next, GameProjectStore.JsonOptions);
        Title = $"{next.Name} - Buller Studio";
        ViewportDocument.Title = next.Name;
        renderer = new SceneRenderService(SceneViewport, next.Workspace.Children.First(node => node.Type == "Camera"), showAttachmentGizmos: true);
        EnsureOutputBox();
        BottomContent.Content = outputBox;
        SelectBottomTab(showOutput: true);
        RefreshEditor();
        SetStatus($"Opened {next.Name}");
        return Task.CompletedTask;
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
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void RefreshEditor()
    {
        if (project is null) return;
        GameProjectStore.EnsureProject(project);
        if (selected is not null) selected = FindSceneNode(selected.Id);
        UpdateExplorerFilter(project.Workspace, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.Players, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.Lighting, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.ReplicatedStorage, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.ServerScriptService, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.ServerStorage, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.Gui, ExplorerSearch.Text.Trim());
        isSuppressingExplorerSelection = true;
        try { ExplorerTree.ItemsSource = new[] { project.Workspace, project.Players, project.Lighting, project.ReplicatedStorage, project.ServerScriptService, project.ServerStorage, project.Gui }; }
        finally { isSuppressingExplorerSelection = false; }
        ObjectCount.Text = GameProjectStore.Flatten(project).Count.ToString();
        renderer?.Refresh(project);
        renderer?.SetSelection(selected?.Id);
        RefreshGuiPreview();
        RenderProperties();
        if (selected is not null) SelectTreeNode(selected.Id);
    }

    private void ExplorerSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ExplorerSearchHint.Visibility = string.IsNullOrEmpty(ExplorerSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (project is null) return;
        UpdateExplorerFilter(project.Workspace, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.Players, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.Lighting, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.ReplicatedStorage, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.ServerScriptService, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.ServerStorage, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.Gui, ExplorerSearch.Text.Trim());
        isSuppressingExplorerSelection = true;
        try
        {
            ExplorerTree.ItemsSource = null;
            ExplorerTree.ItemsSource = new[] { project.Workspace, project.Players, project.Lighting, project.ReplicatedStorage, project.ServerScriptService, project.ServerStorage, project.Gui };
        }
        finally { isSuppressingExplorerSelection = false; }
        if (selected is not null) SelectTreeNode(selected.Id);
    }

    private static bool UpdateExplorerFilter(SceneNode node, string filter)
    {
        var matchesNode = string.IsNullOrEmpty(filter) || node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || node.Type.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var matchesChild = false;
        foreach (var child in node.Children) matchesChild |= UpdateExplorerFilter(child, filter);
        node.IsExplorerVisible = matchesNode || matchesChild;
        return node.IsExplorerVisible;
    }

    private void ExplorerTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (isSuppressingExplorerSelection) return;
        if (propertyPickerTargetId is not null)
        {
            CompleteObjectReferencePick(e.NewValue as SceneNode);
            return;
        }
        selected = e.NewValue as SceneNode;
        renderer?.SetSelection(selected?.Id);
        RefreshGuiPreview();
        RenderProperties();
    }

    private void ExplorerTree_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (project is null || propertyPickerTargetId is not null) return;
        var item = GetTreeItem(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not SceneNode node) return;
        item.IsSelected = true;
        selected = node;
        renderer?.SetSelection(node.Id);
        RenderProperties();
        var menu = new ContextMenu { PlacementTarget = item };
        var insert = new MenuItem { Header = "Insert Object", IsEnabled = node.Type != "Players" };
        insert.Click += (_, _) => ShowInsertMenu(node, item);
        menu.Items.Add(insert);
        var canEdit = node.Type is not ("Workspace" or "Players" or "Lighting" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui" or "Camera");
        var duplicate = new MenuItem { Header = "Duplicate    Ctrl+D", IsEnabled = canEdit && node.Type != "Sky" };
        duplicate.Click += (_, _) => DuplicateSelected();
        menu.Items.Add(duplicate);
        if (!IsGuiNode(node))
        {
            var group = new MenuItem { Header = "Group in Model    Ctrl+G", IsEnabled = CanGroupSelected() };
            group.Click += (_, _) => GroupSelected();
            menu.Items.Add(group);
            if (node.Type == "Model")
            {
                var ungroup = new MenuItem { Header = "Ungroup    Ctrl+Shift+G" };
                ungroup.Click += (_, _) => UngroupSelected();
                menu.Items.Add(ungroup);
            }
        }
        var rename = new MenuItem { Header = "Rename    F2", IsEnabled = node.Type is not ("Workspace" or "Players" or "Lighting" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui") };
        rename.Click += (_, _) => RenameSelected();
        menu.Items.Add(rename);
        menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = "Delete", IsEnabled = canEdit };
        delete.Click += (_, _) => DeleteSelected();
        menu.Items.Add(delete);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void ExplorerTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var item = GetTreeItem(source);
        explorerDragSource = propertyPickerTargetId is null && item is not null && !IsExplorerInteractiveSource(source, item)
            ? item.DataContext as SceneNode
            : null;
        explorerDragStart = e.GetPosition(ExplorerTree);
    }

    private static bool IsExplorerInteractiveSource(DependencyObject? source, DependencyObject boundary)
    {
        while (source is not null && !ReferenceEquals(source, boundary))
        {
            if (source is ButtonBase or TextBoxBase) return true;
            try { source = VisualTreeHelper.GetParent(source); }
            catch (InvalidOperationException) { source = LogicalTreeHelper.GetParent(source); }
        }
        return false;
    }

    private void ExplorerTree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        explorerDragSource = null;
        if (propertyPickerTargetId is not null) TryCompleteExplorerObjectPick(e.OriginalSource as DependencyObject);
    }

    private bool TryCompleteExplorerObjectPick(DependencyObject? source)
    {
        if (propertyPickerTargetId is null) return false;
        var item = GetTreeItem(source);
        if (item is null || IsExplorerInteractiveSource(source, item)) return false;
        return CompleteObjectReferencePick(item.DataContext as SceneNode);
    }

    private void ExplorerTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (propertyPickerTargetId is not null)
        {
            explorerDragSource = null;
            return;
        }
        if (explorerDragSource is null || e.LeftButton != MouseButtonState.Pressed) return;
        var pointer = e.GetPosition(ExplorerTree);
        if (Math.Abs(pointer.X - explorerDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pointer.Y - explorerDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var source = explorerDragSource;
        explorerDragSource = null;
        if (source.Type is "Workspace" or "Players" or "Lighting" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui" or "Camera") return;
        DragDrop.DoDragDrop(ExplorerTree, new DataObject(typeof(SceneNode), source), DragDropEffects.Move);
    }

    private void ExplorerTree_DragOver(object sender, DragEventArgs e)
    {
        if (project is null || !e.Data.GetDataPresent(typeof(SceneNode)) || e.Data.GetData(typeof(SceneNode)) is not SceneNode source)
        {
            ClearExplorerDropFeedback();
            StopExplorerAutoScroll();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        var point = e.GetPosition(ExplorerTree);
        UpdateExplorerAutoScroll(point);
        if (TryGetExplorerDropTarget(point, source, out var parent, out var sibling, out var after))
        {
            SetExplorerDropFeedback(sibling ?? parent, sibling is not null, after);
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            ClearExplorerDropFeedback();
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void ExplorerTree_DragLeave(object sender, DragEventArgs e)
    {
        ClearExplorerDropFeedback();
        StopExplorerAutoScroll();
    }

    private void ExplorerTree_Drop(object sender, DragEventArgs e)
    {
        ClearExplorerDropFeedback();
        StopExplorerAutoScroll();
        if (project is null || !e.Data.GetDataPresent(typeof(SceneNode)) || e.Data.GetData(typeof(SceneNode)) is not SceneNode source) return;
        if (!TryGetExplorerDropTarget(e.GetPosition(ExplorerTree), source, out var parent, out var sibling, out var after)) return;
        if (sibling is null) MoveNodeToParent(source, parent);
        else MoveNodeBeside(source, sibling, after);
        e.Handled = true;
    }

    private void UpdateExplorerAutoScroll(Point point)
    {
        var height = ExplorerTree.ActualHeight;
        if (!double.IsFinite(height) || height <= 0)
        {
            StopExplorerAutoScroll();
            return;
        }
        var edge = Math.Min(36, height * .12);
        explorerAutoScrollDirection = point.Y < edge ? -1 : point.Y > height - edge ? 1 : 0;
        if (explorerAutoScrollDirection == 0) explorerAutoScrollTimer.Stop();
        else explorerAutoScrollTimer.Start();
    }

    private void ExplorerAutoScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (explorerAutoScrollDirection == 0 || FindExplorerScrollViewer() is not { } scrollViewer) return;
        var nextOffset = scrollViewer.VerticalOffset + explorerAutoScrollDirection * Math.Max(1, scrollViewer.ViewportHeight * .08);
        if (nextOffset <= 0 || nextOffset >= scrollViewer.ScrollableHeight)
        {
            scrollViewer.ScrollToVerticalOffset(Math.Clamp(nextOffset, 0, scrollViewer.ScrollableHeight));
            StopExplorerAutoScroll();
            return;
        }
        scrollViewer.ScrollToVerticalOffset(nextOffset);
    }

    private ScrollViewer? FindExplorerScrollViewer() => FindExplorerScrollViewer(ExplorerTree);

    private static ScrollViewer? FindExplorerScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer scrollViewer) return scrollViewer;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindExplorerScrollViewer(VisualTreeHelper.GetChild(parent, index)) is { } found) return found;
        return null;
    }

    private void StopExplorerAutoScroll()
    {
        explorerAutoScrollDirection = 0;
        explorerAutoScrollTimer.Stop();
    }

    private void SetExplorerDropFeedback(SceneNode node, bool isReorder, bool after)
    {
        var item = FindTreeItem(ExplorerTree, node.Id);
        var header = item?.Template.FindName("HeaderChrome", item) as Border;
        if (header is null)
        {
            ClearExplorerDropFeedback();
            return;
        }
        if (!ReferenceEquals(explorerDropTargetHeader, header)) ClearExplorerDropFeedback();
        explorerDropTargetHeader = header;
        header.Background = isReorder ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(90, 55, 126, 180));
        header.BorderBrush = new SolidColorBrush(Color.FromRgb(91, 171, 232));
        header.BorderThickness = isReorder
            ? after ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0)
            : new Thickness(1);
    }

    private void ClearExplorerDropFeedback()
    {
        if (explorerDropTargetHeader is null) return;
        explorerDropTargetHeader.ClearValue(Border.BackgroundProperty);
        explorerDropTargetHeader.ClearValue(Border.BorderBrushProperty);
        explorerDropTargetHeader.ClearValue(Border.BorderThicknessProperty);
        explorerDropTargetHeader = null;
    }

    private void ExplorerTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (project is null || propertyPickerTargetId is not null) return;
        var source = e.OriginalSource as DependencyObject;
        var item = GetTreeItem(source);
        if (item is null || IsExplorerInteractiveSource(source, item)) return;
        var node = item.DataContext as SceneNode;
        if (node is not { Type: "Script" or "ModuleScript" }) return;
        var script = project.Scripts.FirstOrDefault(item => item.Id == node.Id);
        if (script is not null) ShowScript(script);
    }

    private TreeViewItem? GetTreeItem(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, ExplorerTree))
        {
            if (source is TreeViewItem item) return item;
            try { source = VisualTreeHelper.GetParent(source); }
            catch (InvalidOperationException) { source = LogicalTreeHelper.GetParent(source); }
        }
        return null;
    }

    private SceneNode? GetTreeNodeAt(Point point)
    {
        var source = ExplorerTree.InputHitTest(point) as DependencyObject;
        return GetTreeItem(source)?.DataContext as SceneNode;
    }

    private bool TryGetExplorerDropTarget(Point point, SceneNode source, out SceneNode parent, out SceneNode? sibling, out bool after)
    {
        parent = project?.Workspace ?? new SceneNode();
        sibling = null;
        after = false;
        if (project is null) return false;
        var target = GetTreeNodeAt(point);
        if (target is null)
        {
            if (FindParentNode(source)?.Id == project.Workspace.Id)
            {
                sibling = project.Workspace.Children.LastOrDefault(node => node.Id != source.Id && node.Type != "Camera");
                if (sibling is not null)
                {
                    parent = project.Workspace;
                    after = true;
                    return CanMoveNode(source, parent);
                }
            }
            return CanMoveNode(source, project.Workspace);
        }
        if (ReferenceEquals(target, project.Workspace) || target.Id == project.Workspace.Id) return CanMoveNode(source, project.Workspace);
        if (ReferenceEquals(target, project.Lighting) || target.Id == project.Lighting.Id) return CanMoveNode(source, project.Lighting);
        if (ReferenceEquals(target, project.ReplicatedStorage) || target.Id == project.ReplicatedStorage.Id) return CanMoveNode(source, project.ReplicatedStorage);
        if (ReferenceEquals(target, project.ServerScriptService) || target.Id == project.ServerScriptService.Id) return CanMoveNode(source, project.ServerScriptService);
        if (ReferenceEquals(target, project.ServerStorage) || target.Id == project.ServerStorage.Id) return CanMoveNode(source, project.ServerStorage);
        if (ReferenceEquals(target, project.Gui) || target.Id == project.Gui.Id) return CanMoveNode(source, project.Gui);
        var container = FindTreeItem(ExplorerTree, target.Id);
        var header = container?.Template.FindName("PART_Header", container) as FrameworkElement;
        var top = header is null ? container?.TranslatePoint(new Point(0, 0), ExplorerTree).Y ?? point.Y : header.TranslatePoint(new Point(0, 0), ExplorerTree).Y;
        var height = Math.Max(1, header?.ActualHeight ?? 24);
        var offset = (point.Y - top) / height;
        if (offset < .25 || offset > .75)
        {
            parent = FindParentNode(target) ?? project.Workspace;
            sibling = target;
            after = offset > .75;
            return CanMoveNode(source, parent);
        }
        parent = target;
        return CanMoveNode(source, parent);
    }

    private void RenderProperties()
    {
        PropertyEditor.Children.Clear();
        propertyTarget = null;
        if (project is null || selected is null) return;

        var dataSection = AddPropertySection("Data");
        propertyTarget = dataSection;
        AddLabel($"Class: {selected.Type}", "Class");
        if (selected.Type is "Workspace" or "Players" or "Lighting" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui") AddLabel($"Name: {selected.Name}");
        else AddTextField("Name", selected.Name);
        if (selected.Type != "Players") AddObjectReferenceField("Parent", selected, FindParentNode(selected));
        if (selected.Type is not ("Workspace" or "Players" or "Lighting" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui")) AddCheckField("Archivable", selected.Archivable);
        if (SceneNodeTypes.IsValueObject(selected))
        {
            propertyTarget = AddPropertySection("Value");
            if (selected.Type == "BoolValue") AddCheckField("Value", selected.BooleanValue);
            else if (selected.Type == "ObjectValue")
            {
                AddObjectReferenceField("Value", selected, FindSceneNode(selected.ObjectValueId));
                var valueNode = selected;
                var clearValue = new Button { Content = "Clear Value", Margin = new Thickness(3, 3, 3, 1), Padding = new Thickness(5, 2, 5, 2) };
                clearValue.Click += (_, _) =>
                {
                    valueNode.ObjectValueId = string.Empty;
                    if (propertyPickerTargetId == valueNode.Id && propertyPickerProperty == "Value")
                    {
                        propertyPickerTargetId = null;
                        propertyPickerProperty = null;
                        SetObjectReferencePickerActive(false);
                    }
                    RecordEdit();
                    RenderProperties();
                };
                propertyTarget.Children.Add(clearValue);
            }
            else AddTextField("Value", selected.Type switch
            {
                "IntValue" => selected.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "NumberValue" => selected.NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Vector3Value" => VectorText(selected.Vector3Value),
                "Color3Value" => selected.Color3Value,
                _ => selected.StringValueText
            });
        }
        if (selected.Type == "Model" && !IsGuiNode(selected))
        {
            propertyTarget = AddPropertySection("Transform");
            AddTextField("Position", VectorText(SceneModelService.GetPivot(selected)));
            AddTextField("Rotation", VectorText(selected.Rotation));
            AddLabel("Moving or rotating the model transforms its parts together.", "Position");
        }
        if (selected.Type == "Workspace")
        {
            propertyTarget = AddPropertySection("Physics");
            AddTextField("Gravity", selected.Gravity.ToString(System.Globalization.CultureInfo.InvariantCulture));
            propertyTarget = AddPropertySection("Character");
            AddCheckField("AutoManageCharacter", selected.AutoManageCharacter);
            AddLabel("Create, move, and respawn the default character in play mode.", "AutoManageCharacter");
            propertyTarget = AddPropertySection("Skybox");
            AddTextField("SkyboxFront", selected.SkyboxFront);
            AddTextField("SkyboxBack", selected.SkyboxBack);
            AddTextField("SkyboxLeft", selected.SkyboxLeft);
            AddTextField("SkyboxRight", selected.SkyboxRight);
            AddTextField("SkyboxTop", selected.SkyboxTop);
            AddTextField("SkyboxBottom", selected.SkyboxBottom);
            AddLabel("Image paths are relative to the project assets folder.", "Skybox");
        }
        if (selected.Type == "Lighting")
        {
            propertyTarget = AddPropertySection("Lighting");
            AddTextField("Ambient", selected.Ambient);
            AddTextField("Brightness", selected.Brightness.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("ClockTime", selected.ClockTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("GlobalShadows", project.Settings.Shadows);
        }
        if (selected.Type == "Sky")
        {
            propertyTarget = AddPropertySection("Skybox");
            AddTextField("SkyboxFt", selected.SkyboxFront);
            AddTextField("SkyboxBk", selected.SkyboxBack);
            AddTextField("SkyboxLf", selected.SkyboxLeft);
            AddTextField("SkyboxRt", selected.SkyboxRight);
            AddTextField("SkyboxUp", selected.SkyboxTop);
            AddTextField("SkyboxDn", selected.SkyboxBottom);
            AddLabel("Image paths are relative to the project assets folder.", "Skybox");
        }
        if (!IsGuiNode(selected) && (SceneNodeTypes.IsBasePart(selected) || selected.Type is "ImageLabel" or "Camera"))
        {
            propertyTarget = AddPropertySection("Transform");
            AddTextField("Position", VectorText(selected.Position));
            AddTextField("Rotation", VectorText(selected.Rotation));
            if (selected.Type != "Camera")
            {
                AddTextField("Size", VectorText(selected.Size));
                propertyTarget = AddPropertySection("Appearance");
                AddTextField("Color", selected.Color);
                if (SceneNodeTypes.IsBasePart(selected))
                {
                    AddComboField("Material", ["Plastic", "Metal", "Neon", "Glass", "Wood", "Concrete"], selected.Material);
                    AddTextField("Transparency", selected.Transparency.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                if (selected.Type == "Part") AddComboField("Shape", ["Block", "Ball", "Cylinder"], selected.Shape);
                if (selected.Type == "Part") AddTextField("Texture", selected.Texture);
                AddCheckField("CastShadow", selected.CastShadow);
                propertyTarget = AddPropertySection("Behavior");
                AddCheckField("Anchored", selected.Anchored);
                if (selected.Type == "ImageLabel") AddCheckField("Visible", selected.Visible);
                if (SceneNodeTypes.IsBasePart(selected))
                {
                    AddCheckField("CanCollide", selected.CanCollide);
                    AddCheckField("CanTouch", selected.CanTouch);
                    AddCheckField("CanQuery", selected.CanQuery);
                    if (selected.Type == "SpawnLocation") AddCheckField("Enabled", selected.Enabled);
                    AddTextField("Velocity", VectorText(selected.Velocity));
                }
                if (selected.Type == "Seat")
                    AddLabel($"Occupant: {(string.IsNullOrEmpty(selected.Occupant) ? "None" : "Player")}", "Occupant");
            }
            if (selected.Type is "MeshPart" or "ImageLabel")
            {
                propertyTarget = dataSection;
                AddTextField("Asset", selected.Asset);
            }
            if (selected.Type == "Camera")
            {
                propertyTarget = AddPropertySection("Camera");
                AddComboField("Mode", ["Custom", "Subject"], selected.CameraMode);
                var subject = FindSceneNode(selected.Subject);
                AddObjectReferenceField("Subject", selected, SceneNodeTypes.IsBasePart(subject) ? subject : null);
                AddTextField("FieldOfView", selected.FieldOfView.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddLabel("The Workspace Camera is the only view camera.", "Camera");
                AddLabel("Subject mode orbits a BasePart; Custom uses Position and Rotation.", "Subject");
            }
        }
        if (SceneNodeTypes.IsLight(selected))
        {
            propertyTarget = AddPropertySection("Light");
            AddCheckField("Enabled", selected.Enabled);
            AddTextField("Color", selected.Color);
            AddTextField("Brightness", selected.Brightness.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Range", selected.Range.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Position", VectorText(selected.Position));
            if (selected.Type is "SpotLight" or "SurfaceLight")
            {
                AddTextField("Angle", selected.Angle.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (selected.Type == "SpotLight") AddTextField("Rotation", VectorText(selected.Rotation));
            }
            if (selected.Type == "SurfaceLight") AddComboField("Face", ["Front", "Back", "Left", "Right", "Top", "Bottom"], selected.Face);
        }
        if (selected.Type is "Decal" or "Texture")
        {
            propertyTarget = AddPropertySection("Appearance");
            AddCheckField("Visible", selected.Visible);
            AddComboField("Face", ["Front", "Back", "Left", "Right", "Top", "Bottom"], selected.Face);
            AddTextField("Asset", selected.Asset);
            AddTextField("Transparency", selected.Transparency.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("Use an image from Asset Manager for this face.", "Asset");
            if (selected.Type == "Texture")
            {
                AddTextField("StudsPerTileU", selected.StudsPerTileU.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("StudsPerTileV", selected.StudsPerTileV.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("OffsetStudsU", selected.OffsetStudsU.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("OffsetStudsV", selected.OffsetStudsV.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        if (selected.Type == "ProximityPrompt")
        {
            propertyTarget = AddPropertySection("Prompt");
            AddCheckField("Enabled", selected.Enabled);
            AddTextField("ActionText", selected.ActionText);
            AddTextField("ObjectText", selected.ObjectText);
            AddTextField("MaxActivationDistance", selected.MaxActivationDistance.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("Press E near this part during play to fire Triggered.", "Triggered");
        }
        if (selected.Type == "ClickDetector")
        {
            propertyTarget = AddPropertySection("Click Detector");
            AddCheckField("Enabled", selected.Enabled);
            AddTextField("MaxActivationDistance", selected.MaxActivationDistance.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("Left-click this part during play to fire MouseClick.", "MouseClick");
        }
        if (selected.Type == "Sound")
        {
            propertyTarget = AddPropertySection("Sound");
            AddTextField("Asset", selected.Asset);
            AddTextField("Volume", selected.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("Looped", selected.Looped);
            AddCheckField("Playing", selected.Playing);
            AddTextField("RollOffMinDistance", selected.RollOffMinDistance.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("RollOffMaxDistance", selected.RollOffMaxDistance.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("Use a WAV or MP3 asset. A Sound under a part fades with distance during play.", "Asset");
        }
        if (selected.Type == "Highlight")
        {
            propertyTarget = AddPropertySection("Highlight");
            AddCheckField("Enabled", selected.Enabled);
            AddTextField("FillColor", selected.FillColor);
            AddTextField("FillTransparency", selected.FillTransparency.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("OutlineColor", selected.OutlineColor);
            AddTextField("OutlineTransparency", selected.OutlineTransparency.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("Highlights the parent part or all parts in a Model.", "Adornee");
        }
        if (selected.Type == "ForceField")
        {
            propertyTarget = AddPropertySection("ForceField");
            AddCheckField("Visible", selected.Visible);
            AddLabel("Protects a Humanoid in its Model from TakeDamage; hiding the shield keeps protection active.", "Visible");
        }
        if (selected.Type == "Explosion")
        {
            propertyTarget = AddPropertySection("Explosion");
            AddTextField("Position", VectorText(selected.Position));
            AddTextField("BlastRadius", selected.BlastRadius.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("BlastPressure", selected.BlastPressure.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("DestroyJointRadiusPercent", selected.DestroyJointRadiusPercent.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("Visible", selected.Visible);
            AddLabel("Detonates once when play starts or when a script places it in Workspace.", "BlastRadius");
        }
        if (SceneNodeTypes.IsParticleEffect(selected))
        {
            propertyTarget = AddPropertySection(selected.Type);
            AddCheckField("Enabled", selected.Enabled);
            if (selected.Type == "Sparkles") AddTextField("SparkleColor", selected.Color);
            else
            {
                AddTextField("Color", selected.Type == "ParticleEmitter"
                    ? ParticleSequenceService.FormatColor(selected.ColorSequence, selected.Color) : selected.Color);
                AddTextField("Size", selected.Type == "ParticleEmitter"
                    ? ParticleSequenceService.FormatNumber(selected.SizeSequence, selected.EffectSize)
                    : selected.EffectSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            AddTextField("TimeScale", selected.TimeScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (selected.Type == "Fire")
            {
                AddTextField("SecondaryColor", selected.SecondaryColor);
                AddTextField("Heat", selected.Heat.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (selected.Type == "Smoke")
            {
                AddTextField("Opacity", selected.Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("RiseVelocity", selected.RiseVelocity.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (selected.Type == "ParticleEmitter")
            {
                AddTextField("Texture", selected.Texture);
                AddTextField("Rate", selected.Rate.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("Speed", VectorText(selected.SpeedRange));
                AddTextField("Lifetime", VectorText(selected.LifetimeRange));
                AddTextField("SpreadAngle", VectorText(selected.SpreadAngle));
                AddComboField("EmissionDirection", ["Top", "Bottom", "Front", "Back", "Left", "Right"], selected.EmissionDirection);
                AddTextField("Transparency", ParticleSequenceService.FormatNumber(selected.TransparencySequence, selected.Transparency));
                AddLabel("Sequences: time:value[, time:value]. Number values may also use time:value:envelope.", "Size");
            }
            AddLabel("Emits a visual effect from the parent part or Attachment.", "Enabled");
        }
        if (selected.Type == "WeldConstraint")
        {
            propertyTarget = AddPropertySection("Weld Constraint");
            AddCheckField("Enabled", selected.Enabled);
            var part0 = FindSceneNode(selected.Part0);
            var part1 = FindSceneNode(selected.Part1);
            AddObjectReferenceField("Part0", selected, SceneNodeTypes.IsBasePart(part0) ? part0 : null);
            AddObjectReferenceField("Part1", selected, SceneNodeTypes.IsBasePart(part1) ? part1 : null);
            AddLabel("Joined parts move as one assembly during play.", "Part0");
        }
        if (selected.Type == "Attachment")
        {
            propertyTarget = AddPropertySection("Attachment");
            AddTextField("Position", VectorText(selected.Position));
            AddTextField("Rotation", VectorText(selected.Rotation));
            AddLabel("Position is relative to the parent part.", "Position");
        }
        if (selected.Type == "Beam")
        {
            propertyTarget = AddPropertySection("Beam");
            AddCheckField("Enabled", selected.Enabled);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } attachment0 ? attachment0 : null);
            AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } attachment1 ? attachment1 : null);
            AddTextField("Width0", selected.Width0.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Width1", selected.Width1.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Color", selected.Color);
            AddTextField("Transparency", selected.Transparency.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (selected.Type == "Trail")
        {
            propertyTarget = AddPropertySection("Trail");
            AddCheckField("Enabled", selected.Enabled);
            AddCheckField("FaceCamera", selected.FaceCamera);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } trailAttachment0 ? trailAttachment0 : null);
            AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } trailAttachment1 ? trailAttachment1 : null);
            AddTextField("Lifetime", selected.TrailLifetime.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("MinLength", selected.TrailMinLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("MaxLength", selected.TrailMaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("WidthScale", ParticleSequenceService.FormatNumber(selected.TrailWidthSequence, selected.TrailWidth));
            AddTextField("Color", ParticleSequenceService.FormatColor(selected.ColorSequence, selected.Color));
            AddTextField("Transparency", ParticleSequenceService.FormatNumber(selected.TransparencySequence, selected.Transparency));
            AddTextField("Texture", selected.Texture);
            AddComboField("TextureMode", ["Stretch", "Wrap", "Static"], selected.TextureMode);
            AddTextField("TextureLength", selected.TextureLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("Moves between two Attachments. Keypoints use time:value from 0 to 1.", "WidthScale");
        }
        if (selected.Type == "VectorForce")
        {
            propertyTarget = AddPropertySection("VectorForce");
            AddCheckField("Enabled", selected.Enabled);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } forceAttachment ? forceAttachment : null);
            AddTextField("Force", VectorText(selected.Force));
            AddComboField("RelativeTo", ["World", "Attachment0"], selected.RelativeTo);
            AddLabel("Applies translational force to Attachment0's part during play.", "Force");
        }
        if (selected.Type == "LinearVelocity")
        {
            propertyTarget = AddPropertySection("LinearVelocity");
            AddCheckField("Enabled", selected.Enabled);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } velocityAttachment ? velocityAttachment : null);
            AddComboField("RelativeTo", ["World", "Attachment0"], selected.RelativeTo);
            AddComboField("VelocityConstraintMode", ["Vector", "Line", "Plane"], selected.VelocityConstraintMode);
            if (selected.VelocityConstraintMode == "Vector") AddTextField("VectorVelocity", VectorText(selected.VectorVelocity));
            if (selected.VelocityConstraintMode == "Line")
            {
                AddTextField("LineDirection", VectorText(selected.LineDirection));
                AddTextField("LineVelocity", selected.LineVelocity.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (selected.VelocityConstraintMode == "Plane")
            {
                AddTextField("PrimaryTangentAxis", VectorText(selected.PrimaryTangentAxis));
                AddTextField("SecondaryTangentAxis", VectorText(selected.SecondaryTangentAxis));
                AddTextField("PlaneVelocity", VectorText(selected.PlaneVelocity));
            }
            AddCheckField("ForceLimitsEnabled", selected.ForceLimitsEnabled);
            if (selected.ForceLimitsEnabled) AddTextField("MaxForce", selected.MaxForce.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (selected.Type == "Humanoid")
        {
            propertyTarget = AddPropertySection("Humanoid");
            AddTextField("Health", selected.Health.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("MaxHealth", selected.MaxHealth.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("WalkSpeed", selected.WalkSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("JumpPower", selected.JumpPower.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("The generated character uses these values for movement and respawns at zero Health.", "Health");
        }
        if (IsGuiNode(selected) && selected.Type != "Gui") RenderGuiProperties(selected);
        if (selected.Type is "Script" or "ModuleScript")
        {
            var script = project.Scripts.FirstOrDefault(item => item.Id == selected.Id);
            if (script is not null)
            {
                propertyTarget = AddPropertySection(selected.Type);
                if (selected.Type == "Script") AddCheckField("Enabled", selected.Enabled);
                AddLabel("Double-click this script in Explorer to open it.", "Source");
            }
        }
        RenderTags(selected);
        RenderAttributes(selected);
        ApplyPropertyFilter(PropertySearch.Text.Trim());
    }

    private void RenderTags(SceneNode node)
    {
        propertyTarget = AddPropertySection("Tags");
        foreach (var tag in node.Tags.OrderBy(value => value, StringComparer.Ordinal))
        {
            var property = $"Tag:{tag}";
            var remove = new Button { Content = "×", ToolTip = $"Remove {tag}", Tag = property, Width = 24, Margin = new Thickness(3, 0, 0, 0) };
            remove.Click += (_, _) =>
            {
                node.Tags.Remove(tag);
                RecordEdit();
                RenderProperties();
            };
            var row = CreatePropertyRow(tag, remove);
            row.Tag = property;
            propertyTarget.Children.Add(row);
        }
        var add = new Button { Content = "+ Add Tag", Tag = "Tags", Margin = new Thickness(3, 5, 3, 2), Padding = new Thickness(5, 3, 5, 3) };
        add.Click += (_, _) =>
        {
            var tag = StudioDialog.Prompt(this, "Add Tag", "Tag name", "NewTag")?.Trim();
            if (tag is null) return;
            if (tag.Length == 0 || tag.Length > 256 || node.Tags.Contains(tag, StringComparer.Ordinal))
            {
                SetStatus("Enter a unique tag name of at most 256 characters.");
                return;
            }
            node.Tags.Add(tag);
            RecordEdit();
            RenderProperties();
        };
        propertyTarget.Children.Add(add);
    }

    private void RenderAttributes(SceneNode node)
    {
        propertyTarget = AddPropertySection("Attributes");
        foreach (var (name, attribute) in node.Attributes.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var property = $"Attribute:{name}";
            FrameworkElement editor;
            if (attribute.Type == "Boolean")
            {
                var check = new CheckBox { IsChecked = attribute.BooleanValue, Tag = property, DataContext = node.Id, Margin = new Thickness(5, 2, 0, 2) };
                check.Checked += PropertyCheck_Changed;
                check.Unchecked += PropertyCheck_Changed;
                editor = check;
            }
            else
            {
                var text = attribute.Type switch
                {
                    "Number" => attribute.NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "Vector3" => VectorText(attribute.Vector3Value),
                    "Color3" => attribute.Color3Value,
                    _ => attribute.StringValue
                };
                var box = new TextBox { Text = text, Tag = property, DataContext = node.Id, MinWidth = 80, Padding = new Thickness(5, 3, 5, 3), ToolTip = attribute.Type };
                box.LostFocus += PropertyText_LostFocus;
                box.KeyDown += (_, args) => { if (args.Key == Key.Enter) { ApplyTextProperty(box); Keyboard.ClearFocus(); } };
                editor = box;
            }
            var valuePanel = new Grid();
            valuePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            valuePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            valuePanel.Children.Add(editor);
            var delete = new Button { Content = "×", ToolTip = $"Delete {name}", Tag = property, Width = 24, Margin = new Thickness(3, 0, 0, 0) };
            delete.Click += (_, _) =>
            {
                node.Attributes.Remove(name);
                RecordEdit();
                RenderProperties();
            };
            Grid.SetColumn(delete, 1);
            valuePanel.Children.Add(delete);
            var row = CreatePropertyRow(name, valuePanel);
            row.Tag = property;
            propertyTarget.Children.Add(row);
        }
        var add = new Button { Content = "+ Add Attribute", Tag = "Attributes", Margin = new Thickness(3, 5, 3, 2), Padding = new Thickness(5, 3, 5, 3) };
        add.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = add };
            foreach (var type in new[] { "String", "Boolean", "Number", "Vector3", "Color3" })
            {
                var item = new MenuItem { Header = type };
                item.Click += (_, _) =>
                {
                    var name = StudioDialog.Prompt(this, "Add Attribute", "Attribute name", "NewAttribute")?.Trim();
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || node.Attributes.ContainsKey(name))
                    {
                        if (name is not null) SetStatus("Enter a unique attribute name of at most 256 characters.");
                        return;
                    }
                    node.Attributes[name] = new SceneAttributeValue { Type = type };
                    RecordEdit();
                    RenderProperties();
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        propertyTarget.Children.Add(add);
    }

    private void RenderGuiProperties(SceneNode node)
    {
        if (node.Type is "ScreenGui" or "Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox")
        {
            propertyTarget = AddPropertySection("Layout");
            if (node.Type != "ScreenGui")
            {
                AddTextField("Position", GuiUDim2Text(node.Position, node.PositionScale));
                AddTextField("Size", GuiUDim2Text(node.Size, node.SizeScale));
                AddTextField("Rotation", VectorText(node.Rotation));
                AddTextField("ZIndex", node.ZIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("LayoutOrder", node.LayoutOrder.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            AddCheckField("Visible", node.Visible);
        }
        if (node.Type is "Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox")
        {
            propertyTarget = AddPropertySection("Appearance");
            AddTextField("Color", node.Color);
        }
        if (node.Type is "TextLabel" or "TextButton" or "TextBox")
        {
            propertyTarget = AddPropertySection("Text");
            AddTextField("Text", node.Text);
            AddTextField("TextColor", node.TextColor);
            AddTextField("TextSize", node.TextSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (node.Type is "ImageLabel" or "ImageButton")
        {
            propertyTarget = AddPropertySection("Image");
            AddTextField("Asset", node.Asset);
        }
        if (node.Type == "UICorner")
        {
            propertyTarget = AddPropertySection("Appearance");
            AddTextField("CornerRadius", node.CornerRadius.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (node.Type is "UIOutline" or "UIStroke")
        {
            propertyTarget = AddPropertySection("Appearance");
            AddTextField("StrokeColor", node.StrokeColor);
            AddTextField("StrokeThickness", node.StrokeThickness.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (node.Type == "UIGradient")
        {
            propertyTarget = AddPropertySection("Gradient");
            AddTextField("StartColor", node.StartColor);
            AddTextField("EndColor", node.EndColor);
            AddTextField("Rotation", VectorText(node.Rotation));
            AddCheckField("Enabled", node.Enabled);
        }
        if (node.Type == "UIPadding")
        {
            propertyTarget = AddPropertySection("Padding");
            AddTextField("PaddingLeft", GuiUDimText(node.PaddingLeft));
            AddTextField("PaddingTop", GuiUDimText(node.PaddingTop));
            AddTextField("PaddingRight", GuiUDimText(node.PaddingRight));
            AddTextField("PaddingBottom", GuiUDimText(node.PaddingBottom));
        }
        if (node.Type == "UIScale")
        {
            propertyTarget = AddPropertySection("Scale");
            AddTextField("Scale", node.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (node.Type == "UIListLayout")
        {
            propertyTarget = AddPropertySection("Layout");
            AddComboField("LayoutDirection", ["Vertical", "Horizontal"], node.LayoutDirection);
            AddTextField("LayoutSpacing", node.LayoutSpacing.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (node.Type == "UIGridLayout")
        {
            propertyTarget = AddPropertySection("Layout");
            AddTextField("CellSize", GuiUDim2Text(node.CellSize, node.CellSizeScale));
            AddTextField("CellPadding", GuiUDim2Text(node.CellPadding, node.CellPaddingScale));
            AddComboField("FillDirection", ["Horizontal", "Vertical"], node.FillDirection);
            AddTextField("FillDirectionMaxCells", node.FillDirectionMaxCells.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (node.Type == "UITableLayout")
        {
            propertyTarget = AddPropertySection("Layout");
            AddTextField("TableColumns", node.TableColumns.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("LayoutSpacing", node.LayoutSpacing.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (node.Type == "UIAspectRatio")
        {
            propertyTarget = AddPropertySection("Layout");
            AddTextField("AspectRatio", node.AspectRatio.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private bool IsGuiNode(SceneNode node)
    {
        if (project is null) return false;
        var current = node;
        while (true)
        {
            if (ReferenceEquals(current, project.Gui)) return true;
            var parent = FindParentNode(current);
            if (parent is null) return false;
            current = parent;
        }
    }

    private StackPanel AddPropertySection(string title)
    {
        var content = new StackPanel();
        PropertyEditor.Children.Add(new Expander { Header = title, Content = content, IsExpanded = true, Margin = new Thickness(0, 1, 0, 3), Style = (Style)FindResource("PropertySectionExpander") });
        return content;
    }

    private void AddLabel(string text, string? property = null) => (propertyTarget ?? PropertyEditor).Children.Add(new TextBlock { Text = text, Tag = property, Margin = new Thickness(0, 6, 0, 3), Foreground = (Brush)FindResource("TextMuted"), TextWrapping = TextWrapping.Wrap });

    private void AddTextField(string property, string value)
    {
        if (selected is not { } target) return;
        var box = new TextBox { Text = value, Tag = property, DataContext = target.Id, MinWidth = 100, Padding = new Thickness(5, 3, 5, 3) };
        box.LostFocus += PropertyText_LostFocus;
        box.KeyDown += (_, args) => { if (args.Key == Key.Enter) { ApplyTextProperty(box); Keyboard.ClearFocus(); } };
        (propertyTarget ?? PropertyEditor).Children.Add(CreatePropertyRow(property, box));
    }

    private void AddCheckField(string property, bool value)
    {
        if (selected is not { } target) return;
        var box = new CheckBox { IsChecked = value, Margin = new Thickness(5, 2, 0, 2), Tag = property, DataContext = target.Id, Foreground = (Brush)FindResource("TextPrimary"), HorizontalAlignment = HorizontalAlignment.Left };
        box.Checked += PropertyCheck_Changed; box.Unchecked += PropertyCheck_Changed;
        (propertyTarget ?? PropertyEditor).Children.Add(CreatePropertyRow(property, box));
    }

    private void AddComboField(string property, IEnumerable<string> values, string selectedValue)
    {
        if (selected is not { } target) return;
        var combo = new ComboBox { ItemsSource = values, SelectedItem = selectedValue, Tag = property, DataContext = target.Id, Margin = new Thickness(0, 1, 0, 1), MinWidth = 100, Height = 26 };
        combo.SelectionChanged += PropertyCombo_Changed;
        (propertyTarget ?? PropertyEditor).Children.Add(CreatePropertyRow(property, combo));
    }

    private void AddObjectReferenceField(string property, SceneNode target, SceneNode? value)
    {
        var isPicking = propertyPickerTargetId == target.Id && propertyPickerProperty == property;
        var button = new Button
        {
            Content = isPicking ? "Select in Explorer or viewport…" : value is null ? "Select object…" : $"{value.Name}  [{value.Type}]",
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 3, 6, 3),
            MinWidth = 100,
            Tag = property,
            IsEnabled = !(property == "Parent" && (ReferenceEquals(target, project?.Workspace) || ReferenceEquals(target, project?.Players) || ReferenceEquals(target, project?.Lighting) || ReferenceEquals(target, project?.ReplicatedStorage) || ReferenceEquals(target, project?.ServerScriptService) || ReferenceEquals(target, project?.ServerStorage) || ReferenceEquals(target, project?.Gui))),
            ToolTip = isPicking ? "Choose an object in Explorer or the viewport. Press Escape to cancel." : value is null ? "Select an object in Explorer or the 3D viewport" : $"{GetObjectPath(value)}\nClick to select another object"
        };
        if (isPicking)
        {
            button.Background = new SolidColorBrush(Color.FromRgb(45, 74, 96));
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(91, 171, 232));
        }
        button.Click += (_, _) => BeginObjectReferencePick(target, property);
        (propertyTarget ?? PropertyEditor).Children.Add(CreatePropertyRow(property, button));
    }

    private static Grid CreatePropertyRow(string property, FrameworkElement value)
    {
        var row = new Grid { MinHeight = 28, Margin = new Thickness(0, 1, 0, 1), Tag = property };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock { Text = property, Foreground = (Brush)Application.Current.FindResource("TextMuted"), FontSize = 10, Margin = new Thickness(4, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        row.Children.Add(label);
        value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return row;
    }

    private void PropertySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        PropertySearchHint.Visibility = string.IsNullOrEmpty(PropertySearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        ApplyPropertyFilter(PropertySearch.Text.Trim());
    }

    private void ApplyPropertyFilter(string filter)
    {
        foreach (var section in PropertyEditor.Children.OfType<Expander>())
        {
            var sectionHasMatch = false;
            if (section.Content is StackPanel content)
            {
                foreach (UIElement child in content.Children)
                {
                    var property = (child as FrameworkElement)?.Tag as string;
                    var isVisible = string.IsNullOrEmpty(filter) || property is null || property.Contains(filter, StringComparison.OrdinalIgnoreCase);
                    child.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
                    sectionHasMatch |= property is not null && isVisible;
                }
            }
            section.Visibility = string.IsNullOrEmpty(filter) || sectionHasMatch ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrEmpty(filter) && sectionHasMatch) section.IsExpanded = true;
        }
    }

    private void PropertyText_LostFocus(object sender, RoutedEventArgs e) { if (!isRestoring && sender is TextBox box) ApplyTextProperty(box); }

    private void ApplyTextProperty(TextBox box)
    {
        if (project is null || box.Tag is not string property || box.DataContext is not string targetId || FindSceneNode(targetId) is not { } target) return;
        if (property.StartsWith("Attribute:", StringComparison.Ordinal))
        {
            ApplyAttributeText(target, property[10..], box);
            return;
        }
        var text = box.Text.Trim();
        var currentNumber = NumericPropertyText(target, property);
        if (property == "Value" && target.Type == "IntValue")
        {
            if (!long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var integerValue)
                || integerValue < -9007199254740991L || integerValue > 9007199254740991L)
            {
                SetStatus("Enter a whole number within Lua's exact integer range.");
                box.Text = target.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return;
            }
            target.IntegerValue = integerValue;
            RecordEdit();
            return;
        }
        else if (property == "Value" && target.Type == "NumberValue")
        {
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var numberValue) || !double.IsFinite(numberValue))
            {
                SetStatus("Enter a valid number for Value.");
                box.Text = target.NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return;
            }
            target.NumberValue = numberValue;
            RecordEdit();
            return;
        }
        else if (property == "Value" && target.Type == "StringValue")
        {
            target.StringValueText = box.Text;
            RecordEdit();
            return;
        }
        else if (property == "Value" && target.Type == "Vector3Value")
        {
            var vector = ParseVector(text);
            if (vector is null)
            {
                SetStatus("Enter three comma-separated numbers for Value.");
                box.Text = VectorText(target.Vector3Value);
                return;
            }
            target.Vector3Value = vector;
            RecordEdit();
            return;
        }
        else if (property == "Value" && target.Type == "Color3Value")
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(text, "^#[0-9A-Fa-f]{6}$"))
            {
                SetStatus("Enter a color as #RRGGBB for Value.");
                box.Text = target.Color3Value;
                return;
            }
            target.Color3Value = text.ToUpperInvariant();
            RecordEdit();
            return;
        }
        if (currentNumber is not null)
        {
            var valid = property is "TableColumns" or "FillDirectionMaxCells" or "ZIndex" or "LayoutOrder"
                ? int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _)
                : float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && float.IsFinite(number);
            if (!valid)
            {
                SetStatus($"Enter a valid number for {property}.");
                box.Text = currentNumber;
                return;
            }
        }
        if (target.Type == "Model" && property == "Position" && !IsGuiNode(target))
        {
            var pivot = SceneModelService.GetPivot(target);
            var position = ParseVector(text);
            if (position is null)
            {
                SetStatus("Enter three comma-separated numbers.");
                box.Text = VectorText(pivot);
                return;
            }
            if (!SceneModelService.MoveTo(target, position)) return;
            renderer?.Refresh(project);
            RecordEdit();
            RenderProperties();
            return;
        }
        if (target.Type == "Model" && property == "Rotation" && !IsGuiNode(target))
        {
            var rotation = ParseVector(text);
            if (rotation is null)
            {
                SetStatus("Enter three comma-separated numbers.");
                box.Text = VectorText(target.Rotation);
                return;
            }
            if (!SceneModelService.RotateTo(target, rotation)) return;
            renderer?.Refresh(project);
            RecordEdit();
            RenderProperties();
            return;
        }
        if (target.Type == "UIGridLayout" && property is ("CellSize" or "CellPadding"))
        {
            var values = ParseGuiUDim2(text);
            if (values is null)
            {
                SetStatus("Enter two pixel offsets or scale, offset pairs for both axes.");
                box.Text = property == "CellSize"
                    ? GuiUDim2Text(target.CellSize, target.CellSizeScale)
                    : GuiUDim2Text(target.CellPadding, target.CellPaddingScale);
                return;
            }
            if (property == "CellSize")
            {
                target.CellSizeScale = [values[0], values[2]];
                target.CellSize = [values[1], values[3]];
            }
            else
            {
                target.CellPaddingScale = [values[0], values[2]];
                target.CellPadding = [values[1], values[3]];
            }
        }
        else if (target.Type == "UIPadding" && property is ("PaddingLeft" or "PaddingTop" or "PaddingRight" or "PaddingBottom"))
        {
            var value = ParseGuiUDim(text);
            if (value is null)
            {
                SetStatus("Enter a pixel offset or a scale, offset pair.");
                box.Text = GuiUDimText(property switch
                {
                    "PaddingLeft" => target.PaddingLeft,
                    "PaddingTop" => target.PaddingTop,
                    "PaddingRight" => target.PaddingRight,
                    _ => target.PaddingBottom
                });
                return;
            }
            if (property == "PaddingLeft") target.PaddingLeft = value;
            else if (property == "PaddingTop") target.PaddingTop = value;
            else if (property == "PaddingRight") target.PaddingRight = value;
            else target.PaddingBottom = value;
        }
        else if ((property is "Position" or "Size") && IsGuiNode(target))
        {
            var values = ParseGuiUDim2(text);
            if (values is null)
            {
                SetStatus("Enter two pixel offsets or scale, offset pairs for both axes.");
                box.Text = property == "Position" ? GuiUDim2Text(target.Position, target.PositionScale) : GuiUDim2Text(target.Size, target.SizeScale);
                return;
            }
            if (property == "Position")
            {
                target.PositionScale = [values[0], values[2]];
                target.Position = [values[1], values[3], target.Position[2]];
            }
            else
            {
                target.SizeScale = [values[0], values[2]];
                target.Size = [values[1], values[3], target.Size[2]];
            }
        }
        else if (property == "Size" && (SceneNodeTypes.IsSizedParticleEffect(target) || target.Type == "ParticleEmitter"))
        {
            if (target.Type == "ParticleEmitter" && text.Contains(':'))
            {
                if (!ParticleSequenceService.TryParseNumberText(text, 0, 100, out var points))
                {
                    SetStatus("Use keypoints from time 0 to 1, such as 0:1, 0.5:2, 1:0.");
                    box.Text = ParticleSequenceService.FormatNumber(target.SizeSequence, target.EffectSize);
                    return;
                }
                target.SizeSequence = points;
                target.EffectSize = points[0].Value;
            }
            else
            {
            if (!float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var effectSize) || !float.IsFinite(effectSize))
            {
                SetStatus("Enter a valid number for Size.");
                box.Text = target.Type == "ParticleEmitter" ? ParticleSequenceService.FormatNumber(target.SizeSequence, target.EffectSize)
                    : target.EffectSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return;
            }
            target.EffectSize = Math.Clamp(effectSize, target.Type == "Fire" ? 2 : .1f, target.Type == "Fire" ? 30 : 100);
            if (target.Type == "ParticleEmitter") target.SizeSequence = [];
            }
        }
        else if (target.Type == "ParticleEmitter" && property is "Speed" or "Lifetime" or "SpreadAngle")
        {
            var values = ParsePair(text);
            if (values is null)
            {
                SetStatus("Enter two comma-separated numbers.");
                box.Text = VectorText(property == "Speed" ? target.SpeedRange : property == "Lifetime" ? target.LifetimeRange : target.SpreadAngle);
                return;
            }
            if (property == "Speed") target.SpeedRange = [Math.Clamp(values[0], -100, 100), Math.Clamp(values[1], Math.Clamp(values[0], -100, 100), 100)];
            else if (property == "Lifetime") target.LifetimeRange = [Math.Clamp(values[0], .05f, 30), Math.Clamp(values[1], Math.Clamp(values[0], .05f, 30), 30)];
            else target.SpreadAngle = [Math.Clamp(values[0], 0, 360), Math.Clamp(values[1], 0, 360)];
        }
        else if (target.Type == "LinearVelocity" && property == "PlaneVelocity")
        {
            var values = ParsePair(text);
            if (values is null) { SetStatus("Enter two comma-separated numbers."); box.Text = VectorText(target.PlaneVelocity); return; }
            target.PlaneVelocity = values.Select(value => Math.Clamp(value, -10000, 10000)).ToArray();
        }
        else if (property is "Position" or "Rotation" or "Size" or "Velocity" or "Force" or "VectorVelocity" or "LineDirection" or "PrimaryTangentAxis" or "SecondaryTangentAxis")
        {
            var values = ParseVector(text);
            if (values is null) { SetStatus("Enter three comma-separated numbers."); box.Text = property switch { "Position" => VectorText(target.Position), "Rotation" => VectorText(target.Rotation), "Velocity" => VectorText(target.Velocity), "Force" => VectorText(target.Force), "VectorVelocity" => VectorText(target.VectorVelocity), "LineDirection" => VectorText(target.LineDirection), "PrimaryTangentAxis" => VectorText(target.PrimaryTangentAxis), "SecondaryTangentAxis" => VectorText(target.SecondaryTangentAxis), _ => VectorText(target.Size) }; return; }
            if (property == "Position") target.Position = values;
            if (property == "Rotation") target.Rotation = values;
            if (property == "Size") target.Size = values.Select(value => Math.Max(.01f, Math.Abs(value))).ToArray();
            if (property == "Velocity" && SceneNodeTypes.IsBasePart(target)) target.Velocity = values;
            if (property == "Force" && target.Type == "VectorForce") target.Force = values.Select(value => Math.Clamp(value, -1000000, 1000000)).ToArray();
            if (target.Type == "LinearVelocity")
            {
                var normalized = values.Select(value => Math.Clamp(value, -10000, 10000)).ToArray();
                if (property == "VectorVelocity") target.VectorVelocity = normalized;
                if (property == "LineDirection") target.LineDirection = normalized;
                if (property == "PrimaryTangentAxis") target.PrimaryTangentAxis = normalized;
                if (property == "SecondaryTangentAxis") target.SecondaryTangentAxis = normalized;
            }
        }
        else if (property == "FieldOfView" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fov)) target.FieldOfView = Math.Clamp(fov, 10, 120);
        else if (property == "Color" && target.Type is ("ParticleEmitter" or "Trail") && text.Contains(':'))
        {
            if (!ParticleSequenceService.TryParseColorText(text, out var points))
            {
                SetStatus("Use color keypoints such as 0:#FF0000, 1:#0000FF.");
                box.Text = ParticleSequenceService.FormatColor(target.ColorSequence, target.Color);
                return;
            }
            target.ColorSequence = points;
            target.Color = points[0].Value;
        }
        else if (property == "Color")
        {
            target.Color = text;
            if (target.Type is "ParticleEmitter" or "Trail") target.ColorSequence = [];
        }
        else if (property == "SparkleColor" && target.Type == "Sparkles") target.Color = text;
        else if (property == "SecondaryColor" && target.Type == "Fire") target.SecondaryColor = text;
        else if (property == "TimeScale" && SceneNodeTypes.IsParticleEffect(target) && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var timeScale) && float.IsFinite(timeScale)) target.TimeScale = Math.Clamp(timeScale, 0, 1);
        else if (property == "Heat" && target.Type == "Fire" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var heat) && float.IsFinite(heat)) target.Heat = Math.Clamp(heat, -25, 25);
        else if (property == "RiseVelocity" && target.Type == "Smoke" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var riseVelocity) && float.IsFinite(riseVelocity)) target.RiseVelocity = Math.Clamp(riseVelocity, -25, 25);
        else if (property == "Opacity" && target.Type == "Smoke" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var opacity) && float.IsFinite(opacity)) target.Opacity = Math.Clamp(opacity, 0, 1);
        else if (property == "Rate" && target.Type == "ParticleEmitter" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate) && float.IsFinite(rate)) target.Rate = Math.Clamp(rate, 0, 200);
        else if (target.Type == "UIScale" && property == "Scale" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var guiScale) && float.IsFinite(guiScale)) target.Scale = Math.Clamp(guiScale, .01f, 100);
        else if (target.Type == "UIGradient" && property == "StartColor") target.StartColor = text;
        else if (target.Type == "UIGradient" && property == "EndColor") target.EndColor = text;
        else if (property == "FillColor" && target.Type == "Highlight") target.FillColor = text;
        else if (property == "OutlineColor" && target.Type == "Highlight") target.OutlineColor = text;
        else if (property == "Transparency" && target.Type is ("ParticleEmitter" or "Trail") && text.Contains(':'))
        {
            if (!ParticleSequenceService.TryParseNumberText(text, 0, 1, out var points))
            {
                SetStatus("Use transparency keypoints such as 0:0, 1:1.");
                box.Text = ParticleSequenceService.FormatNumber(target.TransparencySequence, target.Transparency);
                return;
            }
            target.TransparencySequence = points;
            target.Transparency = points[0].Value;
        }
        else if (property == "Transparency" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var transparency) && float.IsFinite(transparency))
        {
            target.Transparency = Math.Clamp(transparency, 0, 1);
            if (target.Type is "ParticleEmitter" or "Trail") target.TransparencySequence = [];
        }
        else if (target.Type == "Explosion" && property == "BlastRadius" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var blastRadius) && float.IsFinite(blastRadius)) target.BlastRadius = Math.Clamp(blastRadius, 0, 100);
        else if (target.Type == "Explosion" && property == "BlastPressure" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var blastPressure) && float.IsFinite(blastPressure)) target.BlastPressure = Math.Clamp(blastPressure, 0, 1000000);
        else if (target.Type == "Explosion" && property == "DestroyJointRadiusPercent" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var jointRadiusPercent) && float.IsFinite(jointRadiusPercent)) target.DestroyJointRadiusPercent = Math.Clamp(jointRadiusPercent, 0, 1);
        else if (target.Type == "Texture" && property is ("StudsPerTileU" or "StudsPerTileV" or "OffsetStudsU" or "OffsetStudsV")
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tileValue) && float.IsFinite(tileValue))
        {
            if (property == "StudsPerTileU") target.StudsPerTileU = Math.Clamp(tileValue, .05f, 1000);
            else if (property == "StudsPerTileV") target.StudsPerTileV = Math.Clamp(tileValue, .05f, 1000);
            else if (property == "OffsetStudsU") target.OffsetStudsU = Math.Clamp(tileValue, -10000, 10000);
            else if (property == "OffsetStudsV") target.OffsetStudsV = Math.Clamp(tileValue, -10000, 10000);
            else return;
        }
        else if (property == "FillTransparency" && target.Type == "Highlight" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fillTransparency) && float.IsFinite(fillTransparency)) target.FillTransparency = Math.Clamp(fillTransparency, 0, 1);
        else if (property == "OutlineTransparency" && target.Type == "Highlight" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var outlineTransparency) && float.IsFinite(outlineTransparency)) target.OutlineTransparency = Math.Clamp(outlineTransparency, 0, 1);
        else if (target.Type == "Beam" && property is ("Width0" or "Width1")
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var beamWidth) && float.IsFinite(beamWidth))
        {
            if (property == "Width0") target.Width0 = Math.Clamp(beamWidth, 0, 100);
            else target.Width1 = Math.Clamp(beamWidth, 0, 100);
        }
        else if (target.Type == "Trail" && property == "WidthScale")
        {
            if (text.Contains(':'))
            {
                if (!ParticleSequenceService.TryParseNumberText(text, 0, 1, out var points))
                {
                    SetStatus("Use width keypoints such as 0:1, 1:0.");
                    box.Text = ParticleSequenceService.FormatNumber(target.TrailWidthSequence, target.TrailWidth);
                    return;
                }
                target.TrailWidthSequence = points;
                target.TrailWidth = points[0].Value;
            }
            else if (float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var width) && float.IsFinite(width))
            {
                target.TrailWidth = Math.Clamp(width, 0, 1);
                target.TrailWidthSequence = [];
            }
            else { SetStatus("Enter a width from 0 to 1."); return; }
        }
        else if (target.Type == "Trail" && property is ("Lifetime" or "MinLength" or "MaxLength" or "TextureLength")
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var trailLength) && float.IsFinite(trailLength))
        {
            if (property == "Lifetime") target.TrailLifetime = Math.Clamp(trailLength, .01f, 20);
            else if (property == "MinLength") target.TrailMinLength = Math.Clamp(trailLength, 0, 100);
            else if (property == "MaxLength") target.TrailMaxLength = Math.Clamp(trailLength, 0, 10000);
            else target.TextureLength = Math.Clamp(trailLength, .01f, 1000);
        }
        else if (target.Type == "LinearVelocity" && property is ("LineVelocity" or "MaxForce")
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var velocityNumber) && float.IsFinite(velocityNumber))
        {
            if (property == "LineVelocity") target.LineVelocity = Math.Clamp(velocityNumber, -10000, 10000);
            else target.MaxForce = Math.Clamp(velocityNumber, 0, 1000000);
        }
        else if (property == "Gravity" && target.Type == "Workspace" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gravity) && float.IsFinite(gravity)) target.Gravity = Math.Clamp(gravity, 0, 1000);
        else if (target.Type == "Humanoid" && property is ("Health" or "MaxHealth" or "WalkSpeed" or "JumpPower")
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var humanoidValue) && float.IsFinite(humanoidValue))
        {
            if (property == "MaxHealth") { target.MaxHealth = Math.Clamp(humanoidValue, 1, 100000); target.Health = Math.Min(target.Health, target.MaxHealth); }
            else if (property == "Health") target.Health = Math.Clamp(humanoidValue, 0, target.MaxHealth);
            else if (property == "WalkSpeed") target.WalkSpeed = Math.Clamp(humanoidValue, 0, 1000);
            else target.JumpPower = Math.Clamp(humanoidValue, 0, 1000);
        }
        else if (property == "Brightness" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var brightness) && float.IsFinite(brightness)) target.Brightness = Math.Clamp(brightness, 0, 8);
        else if (property == "ClockTime" && target.Type == "Lighting" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var clockTime) && float.IsFinite(clockTime)) target.ClockTime = Math.Clamp(clockTime, 0, 24);
        else if (property == "Ambient" && target.Type == "Lighting")
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(text, "^#[0-9A-Fa-f]{6}$"))
            {
                SetStatus("Enter Ambient as #RRGGBB.");
                box.Text = target.Ambient;
                return;
            }
            target.Ambient = text.ToUpperInvariant();
        }
        else if (property == "Range" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var range) && float.IsFinite(range)) target.Range = Math.Clamp(range, 1, 1000);
        else if (property == "Angle" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var angle) && float.IsFinite(angle)) target.Angle = Math.Clamp(angle, 1, 175);
        else if (property == "MaxActivationDistance" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var maxActivationDistance) && float.IsFinite(maxActivationDistance)) target.MaxActivationDistance = Math.Clamp(maxActivationDistance, 1, 100);
        else if (property == "Volume" && target.Type == "Sound" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var volume) && float.IsFinite(volume)) target.Volume = Math.Clamp(volume, 0, 1);
        else if (property == "RollOffMinDistance" && target.Type == "Sound" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minDistance) && float.IsFinite(minDistance))
        {
            target.RollOffMinDistance = Math.Clamp(minDistance, 0, 1000);
            target.RollOffMaxDistance = Math.Max(target.RollOffMaxDistance, target.RollOffMinDistance + .01f);
        }
        else if (property == "RollOffMaxDistance" && target.Type == "Sound" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var maxDistance) && float.IsFinite(maxDistance)) target.RollOffMaxDistance = Math.Clamp(maxDistance, target.RollOffMinDistance + .01f, 10000);
        else if (property == "Asset") target.Asset = text;
        else if (property == "ActionText") target.ActionText = text;
        else if (property == "ObjectText") target.ObjectText = text;
        else if (property == "Texture" && target.Type is ("Part" or "ParticleEmitter" or "Trail")) target.Texture = text;
        else if (property == "Text") target.Text = text;
        else if (property == "TextColor") target.TextColor = text;
        else if (property == "StrokeColor") target.StrokeColor = text;
        else if (property == "LayoutSpacing" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var spacing)) target.LayoutSpacing = Math.Max(0, spacing);
        else if (property == "TableColumns" && int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var tableColumns)) target.TableColumns = Math.Clamp(tableColumns, 1, 24);
        else if (property == "FillDirectionMaxCells" && target.Type == "UIGridLayout" && int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var maxCells)) target.FillDirectionMaxCells = Math.Clamp(maxCells, 0, 1000);
        else if (property == "ZIndex" && IsGuiNode(target) && int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var zIndex)) target.ZIndex = zIndex;
        else if (property == "LayoutOrder" && IsGuiNode(target) && int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var layoutOrder)) target.LayoutOrder = layoutOrder;
        else if (property == "AspectRatio" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var aspect)) target.AspectRatio = Math.Max(.001f, aspect);
        else if (property == "CornerRadius" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cornerRadius)) target.CornerRadius = Math.Max(0, cornerRadius);
        else if (property == "StrokeThickness" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var strokeThickness)) target.StrokeThickness = Math.Max(0, strokeThickness);
        else if (property == "TextSize" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var textSize)) target.TextSize = Math.Clamp(textSize, 1, 200);
        else if (property.StartsWith("Skybox", StringComparison.Ordinal) && target.Type is "Workspace" or "Sky")
        {
            switch (property)
            {
                case "SkyboxFront" or "SkyboxFt": target.SkyboxFront = text; break;
                case "SkyboxBack" or "SkyboxBk": target.SkyboxBack = text; break;
                case "SkyboxLeft" or "SkyboxLf": target.SkyboxLeft = text; break;
                case "SkyboxRight" or "SkyboxRt": target.SkyboxRight = text; break;
                case "SkyboxTop" or "SkyboxUp": target.SkyboxTop = text; break;
                case "SkyboxBottom" or "SkyboxDn": target.SkyboxBottom = text; break;
                default: return;
            }
            renderer?.Refresh(project);
        }
        else if (property == "Name" && target.Type is not ("Workspace" or "Players" or "Lighting" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui") && !string.IsNullOrWhiteSpace(text))
        {
            target.Name = text;
            if (target.Type is "Script" or "ModuleScript")
            {
                var gameScript = project.Scripts.FirstOrDefault(script => script.Id == target.Id);
                if (gameScript is not null)
                {
                    gameScript.Name = text;
                    foreach (var document in DockManager.Layout.Descendents().OfType<LayoutDocument>().Where(document => document.ContentId == gameScript.Id))
                        document.Title = gameScript.Name;
                }
            }
        }
        else return;
        if (currentNumber is not null) box.Text = NumericPropertyText(target, property) ?? box.Text;
        if (IsGuiNode(target)) RefreshGuiPreview();
        if (target.Type == "Camera")
        {
            if (property == "FieldOfView" || target.CameraMode == "Custom") renderer?.UpdateCamera(project);
        }
        else renderer?.UpdateNode(target, project);
        RecordEdit();
        if (property == "Name") RefreshEditor();
    }

    private void ApplyAttributeText(SceneNode target, string name, TextBox box)
    {
        if (!target.Attributes.TryGetValue(name, out var attribute)) return;
        var text = box.Text.Trim();
        switch (attribute.Type)
        {
            case "String": attribute.StringValue = box.Text; break;
            case "Number":
                if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                {
                    SetStatus("Enter a valid number for this attribute.");
                    box.Text = attribute.NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return;
                }
                attribute.NumberValue = number;
                break;
            case "Vector3":
                if (ParseVector(text) is not { } vector)
                {
                    SetStatus("Enter three comma-separated numbers for this attribute.");
                    box.Text = VectorText(attribute.Vector3Value);
                    return;
                }
                attribute.Vector3Value = vector;
                break;
            case "Color3":
                if (!Regex.IsMatch(text, "^#[0-9A-Fa-f]{6}$"))
                {
                    SetStatus("Enter a color as #RRGGBB for this attribute.");
                    box.Text = attribute.Color3Value;
                    return;
                }
                attribute.Color3Value = text.ToUpperInvariant();
                break;
            default: return;
        }
        RecordEdit();
    }

    private static string? NumericPropertyText(SceneNode node, string property)
    {
        var value = property switch
        {
            "FieldOfView" => node.FieldOfView,
            "Transparency" => node.Transparency,
            "StudsPerTileU" => node.StudsPerTileU,
            "StudsPerTileV" => node.StudsPerTileV,
            "OffsetStudsU" => node.OffsetStudsU,
            "OffsetStudsV" => node.OffsetStudsV,
            "FillTransparency" => node.FillTransparency,
            "OutlineTransparency" => node.OutlineTransparency,
            "Gravity" => node.Gravity,
            "ClockTime" => node.ClockTime,
            "Health" => node.Health,
            "MaxHealth" => node.MaxHealth,
            "WalkSpeed" => node.WalkSpeed,
            "JumpPower" => node.JumpPower,
            "Brightness" => node.Brightness,
            "Range" => node.Range,
            "Size" => SceneNodeTypes.IsSizedParticleEffect(node) || node.Type == "ParticleEmitter" ? node.EffectSize : (float?)null,
            "Rate" => node.Type == "ParticleEmitter" ? node.Rate : (float?)null,
            "TextureLength" => node.Type == "Trail" ? node.TextureLength : (float?)null,
            "LineVelocity" => node.Type == "LinearVelocity" ? node.LineVelocity : (float?)null,
            "MaxForce" => node.Type == "LinearVelocity" ? node.MaxForce : (float?)null,
            "TimeScale" => SceneNodeTypes.IsParticleEffect(node) ? node.TimeScale : (float?)null,
            "Heat" => node.Type == "Fire" ? node.Heat : (float?)null,
            "RiseVelocity" => node.Type == "Smoke" ? node.RiseVelocity : (float?)null,
            "Opacity" => node.Type == "Smoke" ? node.Opacity : (float?)null,
            "BlastRadius" => node.BlastRadius,
            "BlastPressure" => node.BlastPressure,
            "DestroyJointRadiusPercent" => node.DestroyJointRadiusPercent,
            "Angle" => node.Angle,
            "MaxActivationDistance" => node.MaxActivationDistance,
            "Volume" => node.Volume,
            "RollOffMinDistance" => node.RollOffMinDistance,
            "RollOffMaxDistance" => node.RollOffMaxDistance,
            "LayoutSpacing" => node.LayoutSpacing,
            "TableColumns" => node.TableColumns,
            "FillDirectionMaxCells" => node.FillDirectionMaxCells,
            "ZIndex" => node.ZIndex,
            "LayoutOrder" => node.LayoutOrder,
            "AspectRatio" => node.AspectRatio,
            "Scale" => node.Type == "UIScale" ? node.Scale : (float?)null,
            "CornerRadius" => node.CornerRadius,
            "StrokeThickness" => node.StrokeThickness,
            "TextSize" => node.TextSize,
            _ => (float?)null
        };
        return value?.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void PropertyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (project is null || sender is not CheckBox box || box.Tag is not string property || box.DataContext is not string targetId || FindSceneNode(targetId) is not { } target) return;
        if (property.StartsWith("Attribute:", StringComparison.Ordinal))
        {
            if (target.Attributes.TryGetValue(property[10..], out var attribute) && attribute.Type == "Boolean")
            {
                attribute.BooleanValue = box.IsChecked == true;
                RecordEdit();
            }
            return;
        }
        if (property == "Anchored") target.Anchored = box.IsChecked == true;
        if (property == "CanCollide") target.CanCollide = box.IsChecked == true;
        if (property == "CanTouch") target.CanTouch = box.IsChecked == true;
        if (property == "CanQuery" && SceneNodeTypes.IsBasePart(target)) target.CanQuery = box.IsChecked == true;
        if (property == "CastShadow") target.CastShadow = box.IsChecked == true;
        if (property == "Visible") target.Visible = box.IsChecked == true;
        if (property == "Enabled") target.Enabled = box.IsChecked == true;
        if (property == "FaceCamera" && target.Type == "Trail") target.FaceCamera = box.IsChecked == true;
        if (property == "ForceLimitsEnabled" && target.Type == "LinearVelocity") target.ForceLimitsEnabled = box.IsChecked == true;
        if (property == "Looped") target.Looped = box.IsChecked == true;
        if (property == "Playing") target.Playing = box.IsChecked == true;
        if (property == "AutoManageCharacter" && target.Type == "Workspace") target.AutoManageCharacter = box.IsChecked == true;
        if (property == "GlobalShadows" && target.Type == "Lighting") project.Settings.Shadows = box.IsChecked == true;
        if (property == "Archivable") target.Archivable = box.IsChecked == true;
        if (property == "Value" && target.Type == "BoolValue") target.BooleanValue = box.IsChecked == true;
        if (IsGuiNode(target)) RefreshGuiPreview();
        renderer?.UpdateNode(target, project);
        RecordEdit();
        if (property == "ForceLimitsEnabled") RenderProperties();
    }

    private void PropertyCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (project is null || sender is not ComboBox combo || combo.Tag is not string property || combo.DataContext is not string targetId || FindSceneNode(targetId) is not { } target || combo.SelectedItem is not string value) return;
        if (property == "Mode" && value == "Subject" && !SceneNodeTypes.IsBasePart(FindSceneNode(target.Subject)))
        {
            SetStatus("Select a BasePart for the camera subject first.");
            RenderProperties();
            return;
        }
        if (property == "Mode") target.CameraMode = value;
        if (property == "Shape" && target.Type == "Part") target.Shape = value;
        if (property == "Material" && SceneNodeTypes.IsBasePart(target)) target.Material = value;
        if (property == "Face" && target.Type is ("Decal" or "Texture" or "SurfaceLight") && SceneNodeTypes.IsFace(value)) target.Face = value;
        if (property == "EmissionDirection" && target.Type == "ParticleEmitter" && SceneNodeTypes.IsFace(value)) target.EmissionDirection = value;
        if (property == "TextureMode" && target.Type == "Trail" && value is ("Stretch" or "Wrap" or "Static")) target.TextureMode = value;
        if (property == "RelativeTo" && target.Type is ("VectorForce" or "LinearVelocity") && value is ("World" or "Attachment0")) target.RelativeTo = value;
        if (property == "VelocityConstraintMode" && target.Type == "LinearVelocity" && value is ("Vector" or "Line" or "Plane")) target.VelocityConstraintMode = value;
        if (property == "LayoutDirection") target.LayoutDirection = value;
        if (property == "FillDirection" && target.Type == "UIGridLayout") target.FillDirection = value;
        if (property == "Mode") renderer?.UpdateCamera(project);
        if (property is "Shape" or "Material" or "Face" or "EmissionDirection" or "TextureMode") renderer?.UpdateNode(target, project);
        if (IsGuiNode(target)) RefreshGuiPreview();
        RecordEdit();
        RenderProperties();
    }

    private void BeginObjectReferencePick(SceneNode target, string property)
    {
        explorerDragSource = null;
        propertyPickerTargetId = target.Id;
        propertyPickerProperty = property;
        SetObjectReferencePickerActive(true);
        RenderProperties();
        SetStatus($"Select {property} in Explorer or the 3D viewport · Esc cancels");
    }

    private void SetObjectReferencePickerActive(bool isActive)
    {
        var cursor = isActive ? Cursors.Cross : null;
        ExplorerTree.Cursor = cursor;
        SceneViewport.Cursor = cursor;
        GuiPreviewCanvas.Cursor = cursor;
    }

    private bool CompleteObjectReferencePick(SceneNode? value)
    {
        if (propertyPickerTargetId is null || propertyPickerProperty is null || project is null) return false;
        var targetId = propertyPickerTargetId;
        var property = propertyPickerProperty;
        var target = FindSceneNode(targetId);
        if (target is null)
        {
            CancelObjectReferencePick();
            return true;
        }
        if (value is null) return true;
        if (property == "Parent")
        {
            if (!CanMoveNode(target, value))
            {
                SetStatus("That parent would create a cycle or violate the Workspace Camera hierarchy.");
                return true;
            }
            propertyPickerTargetId = null;
            propertyPickerProperty = null;
            SetObjectReferencePickerActive(false);
            if (!ReferenceEquals(FindParentNode(target), value)) MoveNodeToParent(target, value);
        }
        else if (property == "Subject")
        {
            if (!SceneNodeTypes.IsBasePart(value))
            {
                SetStatus("Camera Subject must be a BasePart.");
                return true;
            }
            propertyPickerTargetId = null;
            propertyPickerProperty = null;
            target.Subject = value.Id;
            renderer?.UpdateCamera(project);
            RecordEdit();
        }
        else if (property is "Part0" or "Part1")
        {
            if (!SceneNodeTypes.IsBasePart(value) || value.Id == (property == "Part0" ? target.Part1 : target.Part0))
            {
                SetStatus("Choose a different BasePart for each weld endpoint.");
                return true;
            }
            propertyPickerTargetId = null;
            propertyPickerProperty = null;
            if (property == "Part0") target.Part0 = value.Id;
            else target.Part1 = value.Id;
            RecordEdit();
        }
        else if (property is "Attachment0" or "Attachment1")
        {
            if (value.Type != "Attachment" || value.Id == (property == "Attachment0" ? target.Attachment1 : target.Attachment0))
            {
                SetStatus("Choose a different Attachment for each endpoint.");
                return true;
            }
            propertyPickerTargetId = null;
            propertyPickerProperty = null;
            if (property == "Attachment0") target.Attachment0 = value.Id;
            else target.Attachment1 = value.Id;
            renderer?.UpdateNode(target, project);
            RecordEdit();
        }
        else if (property == "Value" && target.Type == "ObjectValue")
        {
            propertyPickerTargetId = null;
            propertyPickerProperty = null;
            target.ObjectValueId = value.Id;
            RecordEdit();
        }
        propertyPickerTargetId = null;
        propertyPickerProperty = null;
        SetObjectReferencePickerActive(false);
        selected = FindSceneNode(targetId);
        RefreshEditor();
        if (selected is not null) SelectTreeNode(selected.Id);
        SetStatus($"Updated {property}");
        return true;
    }

    private void CancelObjectReferencePick()
    {
        var targetId = propertyPickerTargetId;
        propertyPickerTargetId = null;
        propertyPickerProperty = null;
        SetObjectReferencePickerActive(false);
        selected = targetId is null ? selected : FindSceneNode(targetId);
        renderer?.SetSelection(selected?.Id);
        RenderProperties();
        if (selected is not null) SelectTreeNode(selected.Id);
        SetStatus("Object selection cancelled");
    }

    private SceneNode? FindSceneNode(string id)
    {
        if (project is null) return null;
        if (project.Workspace.Id == id) return project.Workspace;
        if (project.Players.Id == id) return project.Players;
        if (project.Lighting.Id == id) return project.Lighting;
        if (project.ReplicatedStorage.Id == id) return project.ReplicatedStorage;
        if (project.ServerScriptService.Id == id) return project.ServerScriptService;
        if (project.ServerStorage.Id == id) return project.ServerStorage;
        if (project.Gui.Id == id) return project.Gui;
        return GameProjectStore.Flatten(project).FirstOrDefault(node => node.Id == id);
    }

    private SceneNode? FindParentNode(SceneNode target)
    {
        if (project is null || ReferenceEquals(target, project.Workspace) || ReferenceEquals(target, project.Players) || ReferenceEquals(target, project.Lighting) || ReferenceEquals(target, project.ReplicatedStorage) || ReferenceEquals(target, project.ServerScriptService) || ReferenceEquals(target, project.ServerStorage) || ReferenceEquals(target, project.Gui)) return null;
        return FindParentNode(project.Workspace, target) ?? FindParentNode(project.Players, target) ?? FindParentNode(project.Lighting, target) ?? FindParentNode(project.ReplicatedStorage, target) ?? FindParentNode(project.ServerScriptService, target) ?? FindParentNode(project.ServerStorage, target) ?? FindParentNode(project.Gui, target);
    }

    private static SceneNode? FindParentNode(SceneNode parent, SceneNode target)
    {
        foreach (var child in parent.Children)
        {
            if (ReferenceEquals(child, target) || child.Id == target.Id) return parent;
            var nestedParent = FindParentNode(child, target);
            if (nestedParent is not null) return nestedParent;
        }
        return null;
    }

    private string GetObjectPath(SceneNode node)
    {
        var names = new Stack<string>();
        var current = node;
        while (true)
        {
            names.Push(current.Name);
            var parent = FindParentNode(current);
            if (parent is null) break;
            current = parent;
        }
        return string.Join(" / ", names);
    }

    private bool CanMoveNode(SceneNode source, SceneNode destinationParent)
    {
        return project is not null && GameProjectStore.CanParentNode(project, source, destinationParent);
    }

    private void MoveNodeToParent(SceneNode source, SceneNode destinationParent)
    {
        if (project is null || !GameProjectStore.MoveNodeToParent(project, source, destinationParent)) return;
        FinishHierarchyMove(source);
    }

    private void MoveNodeBeside(SceneNode source, SceneNode target, bool after)
    {
        if (project is null || !GameProjectStore.MoveNodeBeside(project, source, target, after)) return;
        FinishHierarchyMove(source);
    }

    private void FinishHierarchyMove(SceneNode moved)
    {
        selected = moved;
        RecordEdit();
        RefreshEditor();
        SelectTreeNode(moved.Id);
    }

    private async Task AddNodeToParentAsync(SceneNode parent, string type, string name)
    {
        if (project is null) return;
        if (type is "Script" or "ModuleScript")
        {
            var script = new GameScript { Name = name, Source = type == "ModuleScript" ? "local module = {}\nreturn module" : "print('Hello from Lua X')" };
            var scriptNode = new SceneNode { Id = script.Id, Type = type, Name = script.Name };
            if (!GameProjectStore.CanParentNode(project, scriptNode, parent))
            {
                SetStatus($"{type} cannot be added under this object.");
                return;
            }
            project.Scripts.Add(script);
            parent.Children.Add(scriptNode);
            RecordEdit(); RefreshEditor(); SelectTreeNode(scriptNode.Id);
            await GameProjectStore.SaveAsync(project);
            return;
        }
        var node = new SceneNode { Type = type is "Ball" or "Cylinder" ? "Part" : type, Name = name };
        if (type is "Ball" or "Cylinder") node.Shape = type;
        if (type == "WeldConstraint" && SceneNodeTypes.IsBasePart(parent)) node.Part0 = parent.Id;
        if (type == "VectorForce") node.RelativeTo = "Attachment0";
        if (type is "VectorForce" or "LinearVelocity") node.Attachment0 = parent.Type == "Attachment" ? parent.Id
            : SceneNodeTypes.IsBasePart(parent) ? parent.Children.FirstOrDefault(child => child.Type == "Attachment")?.Id ?? string.Empty : string.Empty;
        if (SceneNodeTypes.IsBasePart(node)) node.Position = [0, 1, 0];
        if (type == "SpawnLocation") { node.Size = [6, 1, 6]; node.Position = [0, .5f, 0]; node.Color = "#72BF9D"; node.Material = "Metal"; }
        if (type == "Seat") { node.Size = [2, 1, 2]; node.Position = [0, .5f, 0]; node.Color = "#596B89"; node.Material = "Metal"; }
        if (type == "TrussPart") { node.Size = [2, 6, 2]; node.Position = [0, 3, 0]; node.Color = "#AD9682"; node.Material = "Metal"; }
        if (type is "Decal" or "Texture") node.Color = "#FFFFFF";
        if (type == "Fire") { node.Color = "#FF6A00"; node.EffectSize = 5; }
        if (type == "Smoke") node.Color = "#B0B0B0";
        if (type == "Sparkles") node.Color = "#FFFFFF";
        if (type == "ParticleEmitter") node.Color = "#FFFFFF";
        if (type == "Trail") node.Color = "#FFFFFF";
        if (type == "SurfaceLight") node.Angle = 120;
        if (SceneNodeTypes.IsLight(node)) node.Color = "#FFFFFF";
        if (type is "Frame" or "ScrollingFrame") { node.Size = [320, 180, 1]; node.Color = "#303235"; }
        if (type is "TextLabel" or "TextButton" or "TextBox") { node.Size = [180, 36, 1]; node.Color = "#303235"; }
        if (type is "ImageLabel" or "ImageButton") { node.Size = [128, 128, 1]; node.Color = "#FFFFFF"; }
        if (!GameProjectStore.CanParentNode(project, node, parent))
        {
            SetStatus("This object cannot be added under the selected parent.");
            return;
        }
        parent.Children.Add(node);
        RecordEdit(); RefreshEditor();
        SelectTreeNode(node.Id);
        await GameProjectStore.SaveAsync(project);
    }

    private void ExplorerAddObject_Click(object sender, RoutedEventArgs e)
    {
        if (project is null || sender is not Button { Tag: SceneNode parent } button) return;
        ShowInsertMenu(parent, button);
    }

    private void ShowInsertMenu(SceneNode parent, UIElement placementTarget)
    {
        if (project is null) return;
        var choices = IsGuiNode(parent)
            ? (parent.Type == "Gui"
                ? new (string Type, string Name)[] { ("ScreenGui", "ScreenGui"), ("Folder", "Folder"), ("BoolValue", "BoolValue"), ("IntValue", "IntValue"), ("NumberValue", "NumberValue"), ("StringValue", "StringValue"), ("Script", $"Script{project.Scripts.Count + 1}") }
                : new (string Type, string Name)[] { ("Frame", "Frame"), ("ScrollingFrame", "ScrollingFrame"), ("ImageLabel", "ImageLabel"), ("TextLabel", "TextLabel"), ("ImageButton", "ImageButton"), ("TextButton", "TextButton"), ("TextBox", "TextBox"), ("UICorner", "UICorner"), ("UIOutline", "UIOutline"), ("UIStroke", "UIStroke"), ("UIGradient", "UIGradient"), ("UIPadding", "UIPadding"), ("UIScale", "UIScale"), ("UIListLayout", "UIListLayout"), ("UIGridLayout", "UIGridLayout"), ("UITableLayout", "UITableLayout"), ("UIAspectRatio", "UIAspectRatio"), ("BoolValue", "BoolValue"), ("IntValue", "IntValue"), ("NumberValue", "NumberValue"), ("StringValue", "StringValue"), ("Folder", "Folder"), ("Script", $"Script{project.Scripts.Count + 1}") })
            : new (string Type, string Name)[] { ("Part", "Part"), ("Ball", "Ball"), ("Cylinder", "Cylinder"), ("WedgePart", "WedgePart"), ("CornerWedgePart", "CornerWedgePart"), ("TrussPart", "TrussPart"), ("SpawnLocation", "SpawnLocation"), ("Seat", "Seat"), ("MeshPart", "MeshPart"), ("Attachment", "Attachment"), ("Beam", "Beam"), ("Trail", "Trail"), ("VectorForce", "VectorForce"), ("LinearVelocity", "LinearVelocity"), ("PointLight", "PointLight"), ("SpotLight", "SpotLight"), ("SurfaceLight", "SurfaceLight"), ("Decal", "Decal"), ("Texture", "Texture"), ("Sound", "Sound"), ("ProximityPrompt", "ProximityPrompt"), ("ClickDetector", "ClickDetector"), ("Highlight", "Highlight"), ("ForceField", "ForceField"), ("Explosion", "Explosion"), ("WeldConstraint", "WeldConstraint"), ("Humanoid", "Humanoid"), ("ImageLabel", "ImageLabel"), ("Model", "Model"), ("Folder", "Folder"), ("BoolValue", "BoolValue"), ("IntValue", "IntValue"), ("NumberValue", "NumberValue"), ("StringValue", "StringValue"), ("Script", $"Script{project.Scripts.Count + 1}") };
        choices = choices.Concat(new (string Type, string Name)[] { ("Configuration", "Configuration"), ("BindableEvent", "BindableEvent"), ("BindableFunction", "BindableFunction"), ("ModuleScript", $"ModuleScript{project.Scripts.Count + 1}"), ("Vector3Value", "Vector3Value"), ("Color3Value", "Color3Value"), ("ObjectValue", "ObjectValue"), ("Sky", "Sky"), ("Fire", "Fire"), ("Smoke", "Smoke"), ("Sparkles", "Sparkles"), ("ParticleEmitter", "ParticleEmitter") })
            .Where(choice => GameProjectStore.CanParentNode(project, new SceneNode { Type = choice.Type is "Ball" or "Cylinder" ? "Part" : choice.Type, Name = choice.Name }, parent)).ToArray();
        var menu = new ContextMenu { PlacementTarget = placementTarget, StaysOpen = true };
        var search = new TextBox { Width = 190, Margin = new Thickness(5), Padding = new Thickness(5, 3, 5, 3), ToolTip = "Search objects" };
        var searchItem = new MenuItem { Header = search, StaysOpenOnClick = true, Padding = new Thickness(0) };
        menu.Items.Add(searchItem);
        menu.Items.Add(new Separator());
        if (choices.Length == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No compatible child objects", IsEnabled = false });
            menu.IsOpen = true;
            _ = Dispatcher.BeginInvoke(new Action(() => search.Focus()));
            return;
        }
        var objectItems = new List<MenuItem>();
        var noMatchingChoices = new MenuItem { Header = "No matching object types", IsEnabled = false, Visibility = Visibility.Collapsed };
        menu.Items.Add(noMatchingChoices);
        foreach (var choice in choices)
        {
            var item = new MenuItem { Header = choice.Type, Tag = choice, StaysOpenOnClick = true };
            item.Click += async (_, _) =>
            {
                menu.IsOpen = false;
                await AddNodeToParentAsync(parent, choice.Type, choice.Name);
            };
            objectItems.Add(item);
            menu.Items.Add(item);
        }
        search.TextChanged += (_, _) =>
        {
            var hasMatches = false;
            foreach (var item in objectItems)
            {
                var isMatch = item.Header is string type && type.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase);
                item.Visibility = isMatch ? Visibility.Visible : Visibility.Collapsed;
                hasMatches |= isMatch;
            }
            noMatchingChoices.Visibility = hasMatches ? Visibility.Collapsed : Visibility.Visible;
        };
        menu.IsOpen = true;
        _ = Dispatcher.BeginInvoke(new Action(() => search.Focus()));
    }

    private async void ImportAssets_Click(object sender, RoutedEventArgs e)
    {
        if (project is null) return;
        var picker = new OpenFileDialog { Multiselect = true, Title = "Upload game assets", Filter = "All files|*.*" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            await GameProjectStore.ImportAssetsAsync(project, picker.FileNames, assetManagerFolder);
            ShowAssets(); SetStatus($"Imported {picker.FileNames.Length} asset(s)");
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void CreateAssetFolder_Click(object sender, RoutedEventArgs e)
    {
        if (project is null) return;
        var name = StudioDialog.Prompt(this, "Create asset folder", "Folder name", "New Folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        var folderName = GameProjectStore.SanitizeName(name);
        string parent;
        try { parent = GetCurrentAssetsDirectory(); }
        catch (Exception exception) { ShowError(exception); return; }
        var folder = Path.GetFullPath(Path.Combine(parent, folderName));
        if (!IsInsideAssetsRoot(folder) || Directory.Exists(folder) || File.Exists(folder))
        {
            SetStatus("A folder with that name already exists.");
            return;
        }
        Directory.CreateDirectory(folder);
        assetManagerFolder = string.IsNullOrEmpty(assetManagerFolder) ? folderName : Path.Combine(assetManagerFolder, folderName).Replace('\\', '/');
        ShowAssets();
        SetStatus($"Created folder {folderName}");
    }

    private void ShowAssets()
    {
        if (project is null) return;
        SetOutputPanelVisible(true);
        string currentDirectory;
        try { currentDirectory = GetCurrentAssetsDirectory(); }
        catch (Exception exception) { ShowError(exception); return; }
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new DockPanel { Margin = new Thickness(8, 2, 8, 1), LastChildFill = false };
        var title = new TextBlock { Text = "Asset Manager", FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("TextPrimary") };
        DockPanel.SetDock(title, Dock.Left);
        header.Children.Add(title);
        var upload = new Button { Content = "↑ Upload", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(2, 1, 0, 1) };
        upload.Click += ImportAssets_Click;
        DockPanel.SetDock(upload, Dock.Right);
        header.Children.Add(upload);
        var createFolder = new Button { Content = "＋ Folder", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(2, 1, 0, 1) };
        createFolder.Click += CreateAssetFolder_Click;
        DockPanel.SetDock(createFolder, Dock.Right);
        header.Children.Add(createFolder);
        panel.Children.Add(header);

        var breadcrumb = new DockPanel { Margin = new Thickness(8, 0, 8, 1), LastChildFill = true };
        var up = new Button { Content = "↑", Width = 28, Padding = new Thickness(2), IsEnabled = !string.IsNullOrEmpty(assetManagerFolder), ToolTip = "Up one folder" };
        up.Click += (_, _) =>
        {
            assetManagerFolder = Path.GetDirectoryName(assetManagerFolder.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? string.Empty;
            ShowAssets();
        };
        DockPanel.SetDock(up, Dock.Left);
        breadcrumb.Children.Add(up);
        var breadcrumbParts = new[] { "assets" }.Concat(assetManagerFolder.Split('/', StringSplitOptions.RemoveEmptyEntries));
        breadcrumb.Children.Add(new TextBlock { Text = string.Join(" / ", breadcrumbParts), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0), Foreground = (Brush)FindResource("TextMuted") });
        Grid.SetRow(breadcrumb, 1);
        panel.Children.Add(breadcrumb);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 0) };
        foreach (var filter in new[] { "ALL", "OBJ", "PNG", "OTHER" })
        {
            var tab = new Button { Content = filter, Tag = filter, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(1, 0, 1, 0), Background = assetManagerFilter == filter ? new SolidColorBrush(Color.FromRgb(48, 90, 124)) : Brushes.Transparent };
            tab.Click += (_, _) => { assetManagerFilter = filter; ShowAssets(); };
            tabs.Children.Add(tab);
        }
        Grid.SetRow(tabs, 2);
        panel.Children.Add(tabs);

        var search = new TextBox { Text = assetManagerSearch, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(8, 1, 8, 2), ToolTip = "Search files and folders" };
        Grid.SetRow(search, 3);
        panel.Children.Add(search);
        var body = new StackPanel { Margin = new Thickness(7, 2, 7, 5) };
        var contentScroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(contentScroll, 4);
        panel.Children.Add(contentScroll);
        search.TextChanged += (_, _) => { assetManagerSearch = search.Text; RefreshAssetContents(body, currentDirectory); };
        RefreshAssetContents(body, currentDirectory);
        BottomContent.Content = panel;
        SelectBottomTab(showOutput: false);
    }

    private void RefreshAssetContents(StackPanel body, string currentDirectory)
    {
        if (project is null) return;
        body.Children.Clear();
        var search = assetManagerSearch.Trim();
        foreach (var folder in Directory.EnumerateDirectories(currentDirectory).Where(IsAssetFolderSafe).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(folder);
            if (!string.IsNullOrEmpty(search) && !name.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            var open = new Button { Content = $"▱  {name}", HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent };
            open.Click += (_, _) =>
            {
                assetManagerFolder = string.IsNullOrEmpty(assetManagerFolder) ? name : Path.Combine(assetManagerFolder, name).Replace('\\', '/');
                ShowAssets();
            };
            body.Children.Add(open);
        }
        var currentRelativeDirectory = string.IsNullOrEmpty(assetManagerFolder) ? "assets" : $"assets/{assetManagerFolder.Replace('\\', '/')}";
        var assets = project.Assets.Where(asset =>
        {
            var path = asset.RelativePath.Replace('\\', '/');
            var parent = Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/');
            var matchesFolder = string.Equals(parent, currentRelativeDirectory, StringComparison.OrdinalIgnoreCase);
            var matchesFilter = assetManagerFilter switch
            {
                "OBJ" => string.Equals(asset.Type, "obj", StringComparison.OrdinalIgnoreCase),
                "PNG" => string.Equals(asset.Type, "png", StringComparison.OrdinalIgnoreCase),
                "OTHER" => !string.Equals(asset.Type, "obj", StringComparison.OrdinalIgnoreCase) && !string.Equals(asset.Type, "png", StringComparison.OrdinalIgnoreCase),
                _ => true
            };
            var matchesSearch = string.IsNullOrEmpty(search) || asset.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || asset.Type.Contains(search, StringComparison.OrdinalIgnoreCase);
            return matchesFolder && matchesFilter && matchesSearch;
        }).OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var asset in assets) body.Children.Add(CreateAssetListItem(asset));
        if (body.Children.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = project.Assets.Count == 0 ? "This project has no assets yet. Upload files to add them here." : "This folder has no files matching the current filter.",
                Foreground = (Brush)FindResource("TextMuted"), FontSize = 11, TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 360, TextAlignment = TextAlignment.Center, Margin = new Thickness(12)
            });
        }
    }

    private string GetCurrentAssetsDirectory()
    {
        if (project is null) throw new InvalidOperationException("No project is open.");
        return GameProjectStore.EnsureAssetsDirectory(project, assetManagerFolder);
    }

    private static bool IsAssetFolderSafe(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private bool IsInsideAssetsRoot(string path)
    {
        if (project is null) return false;
        var root = Path.GetFullPath(Path.Combine(project.ProjectPath, "assets"));
        return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private FrameworkElement CreateAssetListItem(ProjectAsset asset)
    {
        var row = new Grid { MinHeight = 52 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var preview = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(3), Background = (Brush)FindResource("PanelRaised"), VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        var previewImage = LoadAssetThumbnail(asset);
        if (previewImage is not null)
        {
            var previewContents = new Grid();
            previewContents.Children.Add(new Image { Source = previewImage, Stretch = Stretch.UniformToFill });
            previewContents.Children.Add(new TextBlock { Text = asset.Type.ToUpperInvariant(), FontSize = 7, FontWeight = FontWeights.SemiBold, Padding = new Thickness(3, 1, 3, 1), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Color.FromArgb(210, 25, 27, 30)), Foreground = Brushes.White });
            preview.Child = previewContents;
        }
        else
        {
            preview.Child = new TextBlock { Text = asset.Type == "obj" ? "◈" : "▧", FontSize = 17, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = asset.Type == "obj" ? new SolidColorBrush(Color.FromRgb(168, 155, 228)) : (Brush)FindResource("TextMuted") };
        }
        row.Children.Add(preview);
        var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) };
        details.Children.Add(new TextBlock { Text = asset.Name, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (Brush)FindResource("TextPrimary") });
        details.Children.Add(new TextBlock { Text = $"{asset.Type.ToUpperInvariant()}  ·  {asset.Size:N0} bytes", FontSize = 9, Margin = new Thickness(0, 2, 0, 0), Foreground = (Brush)FindResource("TextMuted") });
        Grid.SetColumn(details, 1);
        row.Children.Add(details);
        var item = new Border { Background = Brushes.Transparent, BorderBrush = new SolidColorBrush(Color.FromRgb(55, 57, 60)), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(5), Margin = new Thickness(0, 1, 0, 1), Child = row };
        item.ContextMenu = CreateAssetContextMenu(asset);
        return item;
    }

    private ContextMenu CreateAssetContextMenu(ProjectAsset asset)
    {
        var menu = new ContextMenu();
        var relativePath = asset.RelativePath.Replace('\\', '/').TrimStart('/');
        var luaPath = relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ? relativePath[7..] : relativePath;
        var useAsset = new MenuItem { Header = "Use on selected object" };
        useAsset.Click += (_, _) => ApplyAssetToSelectedObject(asset);
        menu.Items.Add(useAsset);
        if (selected is { Type: "Workspace" or "Sky" } skyTarget && IsImageAssetType(asset.Type))
        {
            var skyboxFaces = new MenuItem { Header = $"Set {skyTarget.Type} skybox face" };
            foreach (var face in new[] { (Name: "Front", Property: "SkyboxFront"), (Name: "Back", Property: "SkyboxBack"), (Name: "Left", Property: "SkyboxLeft"), (Name: "Right", Property: "SkyboxRight"), (Name: "Top", Property: "SkyboxTop"), (Name: "Bottom", Property: "SkyboxBottom") })
            {
                var faceItem = new MenuItem { Header = face.Name };
                faceItem.Click += (_, _) => ApplyAssetToSkyboxFace(asset, skyTarget.Id, face.Property);
                skyboxFaces.Items.Add(faceItem);
            }
            menu.Items.Add(skyboxFaces);
        }
        var copyPath = new MenuItem { Header = "Copy asset path" };
        copyPath.Click += (_, _) => CopyAssetText(luaPath);
        menu.Items.Add(copyPath);
        var copyLuaExpression = new MenuItem { Header = "Copy Lua X asset expression" };
        copyLuaExpression.Click += (_, _) => CopyAssetText($"GetAssetPath(\"{luaPath.Replace("\\", "\\\\").Replace("\"", "\\\"")}\")");
        menu.Items.Add(copyLuaExpression);
        menu.Items.Add(new Separator());
        var destinations = new MenuItem { Header = "Move to folder" };
        destinations.SubmenuOpened += (_, _) =>
        {
            destinations.Items.Clear();
            if (project is null) return;
            var assetsRoot = Path.Combine(project.ProjectPath, "assets");
            var source = Path.GetFullPath(Path.Combine(project.ProjectPath, asset.RelativePath));
            foreach (var folder in EnumerateAssetFolders())
            {
                var relativeFolder = Path.GetRelativePath(assetsRoot, folder).Replace('\\', '/');
                var label = relativeFolder == "." ? "assets" : $"assets / {relativeFolder.Replace("/", " / ")}";
                var sameFolder = string.Equals(Path.GetDirectoryName(source), folder, StringComparison.OrdinalIgnoreCase);
                var item = new MenuItem { Header = label, IsEnabled = !sameFolder };
                item.Click += async (_, _) => await MoveAssetToFolderAsync(asset, relativeFolder == "." ? string.Empty : relativeFolder);
                destinations.Items.Add(item);
            }
        };
        menu.Items.Add(destinations);
        return menu;
    }

    private void ApplyAssetToSelectedObject(ProjectAsset asset)
    {
        if (project is null || selected is null) return;
        var imageAsset = IsImageAssetType(asset.Type);
        var property = selected.Type switch
        {
            "Part" when imageAsset => "Texture",
            "ParticleEmitter" when imageAsset => "Texture",
            "Trail" when imageAsset => "Texture",
            "MeshPart" when asset.Type == "obj" => "Asset",
            "ImageLabel" or "ImageButton" or "Decal" or "Texture" when imageAsset => "Asset",
            "Sound" when IsAudioAssetType(asset.Type) => "Asset",
            _ => null
        };
        if (property is null)
        {
            SetStatus("This asset type is not compatible with the selected object.");
            return;
        }

        var relativePath = asset.RelativePath.Replace('\\', '/').TrimStart('/');
        var assetsRoot = Path.GetFullPath(Path.Combine(project.ProjectPath, "assets"));
        var assetPath = Path.GetFullPath(Path.Combine(project.ProjectPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!assetPath.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(assetPath))
        {
            SetStatus("The selected asset file is missing or outside this project's assets folder.");
            return;
        }

        if (property == "Texture") selected.Texture = relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ? relativePath[7..] : relativePath;
        else selected.Asset = relativePath;
        renderer?.UpdateNode(selected, project);
        if (IsGuiNode(selected)) RefreshGuiPreview();
        RenderProperties();
        RecordEdit();
        SetStatus($"Applied {asset.Name} to {selected.Name}");
    }

    private void ApplyAssetToSkyboxFace(ProjectAsset asset, string targetId, string property)
    {
        if (project is null || FindSceneNode(targetId) is not { Type: "Workspace" or "Sky" } skyTarget || !IsImageAssetType(asset.Type)) return;
        var relativePath = asset.RelativePath.Replace('\\', '/').TrimStart('/');
        var assetsRoot = Path.GetFullPath(Path.Combine(project.ProjectPath, "assets"));
        var assetPath = Path.GetFullPath(Path.Combine(project.ProjectPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!assetPath.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(assetPath))
        {
            SetStatus("The selected image is missing or outside this project's assets folder.");
            return;
        }
        var imagePath = relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ? relativePath[7..] : relativePath;
        switch (property)
        {
            case "SkyboxFront": skyTarget.SkyboxFront = imagePath; break;
            case "SkyboxBack": skyTarget.SkyboxBack = imagePath; break;
            case "SkyboxLeft": skyTarget.SkyboxLeft = imagePath; break;
            case "SkyboxRight": skyTarget.SkyboxRight = imagePath; break;
            case "SkyboxTop": skyTarget.SkyboxTop = imagePath; break;
            case "SkyboxBottom": skyTarget.SkyboxBottom = imagePath; break;
            default: return;
        }
        renderer?.Refresh(project);
        RenderProperties();
        RecordEdit();
        SetStatus($"Applied {asset.Name} to the {skyTarget.Type} skybox {property[6..].ToLowerInvariant()} face");
    }

    private void CopyAssetText(string value)
    {
        try
        {
            Clipboard.SetText(value);
            SetStatus("Copied asset reference");
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            SetStatus("Could not copy the asset reference.");
        }
    }

    private static bool IsImageAssetType(string type) =>
        type.Equals("png", StringComparison.OrdinalIgnoreCase)
        || type.Equals("jpg", StringComparison.OrdinalIgnoreCase)
        || type.Equals("jpeg", StringComparison.OrdinalIgnoreCase)
        || type.Equals("bmp", StringComparison.OrdinalIgnoreCase)
        || type.Equals("gif", StringComparison.OrdinalIgnoreCase);

    private static bool IsAudioAssetType(string type) =>
        type.Equals("wav", StringComparison.OrdinalIgnoreCase)
        || type.Equals("mp3", StringComparison.OrdinalIgnoreCase)
        || type.Equals("wma", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<string> EnumerateAssetFolders()
    {
        if (project is null) yield break;
        var root = Path.GetFullPath(Path.Combine(project.ProjectPath, "assets"));
        var pending = new Stack<string>();
        pending.Push(root);
        yield return root;
        while (pending.Count > 0)
        {
            var parent = pending.Pop();
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(parent).ToArray(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var fullPath = Path.GetFullPath(child);
                if (!IsInsideAssetsRoot(fullPath)) continue;
                try
                {
                    if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
                yield return fullPath;
                pending.Push(fullPath);
            }
        }
    }

    private async Task MoveAssetToFolderAsync(ProjectAsset asset, string relativeFolder)
    {
        if (project is null) return;
        try
        {
            await GameProjectStore.MoveAssetAsync(project, asset, relativeFolder);
            renderer?.Refresh(project);
            RefreshGuiPreview();
            RenderProperties();
            ShowAssets();
            SetStatus($"Moved {asset.Name}");
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private BitmapImage? LoadAssetThumbnail(ProjectAsset asset)
    {
        if (project is null || asset.Type is not ("png" or "jpg" or "jpeg" or "bmp" or "gif")) return null;
        try
        {
            var assetsRoot = Path.GetFullPath(Path.Combine(project.ProjectPath, "assets")) + Path.DirectorySeparatorChar;
            var assetPath = Path.GetFullPath(Path.Combine(project.ProjectPath, asset.RelativePath));
            if (!assetPath.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(assetPath)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 96;
            image.UriSource = new System.Uri(assetPath, System.UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private void ShowScript(GameScript script)
    {
        CommitScriptEditor();
        CloseLuaXSignatureWindow();
        var document = DockManager.Layout.Descendents().OfType<LayoutDocument>().FirstOrDefault(item => item.ContentId == script.Id);
        if (document is null)
        {
            document = new LayoutDocument { ContentId = script.Id, CanClose = true };
            var documentPane = DockManager.Layout.Descendents().OfType<LayoutDocumentPane>().First();
            documentPane.Children.Add(document);
        }
        if (!scriptEditors.ContainsKey(script.Id) || document.Content is null)
            document.Content = CreateScriptEditorPanel(script);
        document.Title = script.Name;
        DockManager.ActiveContent = document;
        SelectRibbonTab("SCRIPT");
    }

    private DockPanel CreateScriptEditorPanel(GameScript script)
    {
        var editor = new TextEditor
        {
            Text = script.Source,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            ShowLineNumbers = true,
            LineNumbersForeground = new SolidColorBrush(Color.FromRgb(112, 119, 129)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            WordWrap = false,
            Background = new SolidColorBrush(Color.FromRgb(17, 24, 36)),
            Foreground = (Brush)FindResource("TextPrimary"),
            BorderThickness = new Thickness(0),
            SyntaxHighlighting = LuaXHighlighting
        };
        editor.TextArea.TextEntered += (_, args) => ShowLuaXCompletions(editor, args.Text);
        editor.TextArea.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Space || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            ShowLuaXCompletions(editor, string.Empty);
            args.Handled = true;
        };
        editor.TextArea.Caret.PositionChanged += (_, _) => UpdateLuaXSignatureWindow(editor);
        scriptEditors[script.Id] = editor;
        return new DockPanel { Margin = new Thickness(0), Children = { editor } };
    }

    private void ShowLuaXCompletions(TextEditor editor, string text)
    {
        var isManualInvocation = text.Length == 0;
        if ((!isManualInvocation && text.Length != 1) || activeCompletionWindow?.IsVisible == true) return;
        var typed = isManualInvocation ? '\0' : text[0];
        if (!isManualInvocation && typed is '"' or '\'')
        {
            if (IsLuaXCompletionSuppressedBeforeCurrentCharacter(editor)) return;
            if (GetLuaXStringCompletions(editor) is { } stringCompletions)
                ShowLuaXCompletionWindow(editor, stringCompletions, stringStartOffset: GetLuaXStringCompletionStart(editor));
            return;
        }
        if (IsLuaXCompletionSuppressed(editor))
        {
            if (!isManualInvocation || GetLuaXStringCompletions(editor) is not { } stringCompletions) return;
            ShowLuaXCompletionWindow(editor, stringCompletions, stringStartOffset: GetLuaXStringCompletionStart(editor));
            return;
        }
        if (!isManualInvocation && !char.IsLetterOrDigit(typed) && typed is not ('_' or '.' or ':')) return;
        var document = editor.Document;
        var offset = Math.Clamp(editor.CaretOffset - 1, -1, document.TextLength - 1);
        while (offset >= 0 && (char.IsLetterOrDigit(document.GetCharAt(offset)) || document.GetCharAt(offset) == '_')) offset--;
        var context = offset >= 0 ? document.GetCharAt(offset) : '\0';
        if (typed == '.') context = '.';
        if (typed == ':') context = ':';
        var receiver = context is '.' or ':' ? ReadLuaXReceiver(document, offset) : string.Empty;
        var suggestions = context switch
        {
            '.' => GetLuaXMembers(receiver, editor),
            ':' => GetLuaXMethods(receiver, editor),
            _ => GetLuaXScriptCompletions(editor)
        };
        ShowLuaXCompletionWindow(editor, suggestions, receiver);
    }

    private void ShowLuaXCompletionWindow(TextEditor editor, IEnumerable<string> suggestions, string receiver = "", int? stringStartOffset = null)
    {
        var items = suggestions.Distinct(StringComparer.Ordinal).ToArray();
        if (items.Length == 0) return;
        var completion = new CompletionWindow(editor.TextArea);
        if (stringStartOffset is int startOffset) completion.StartOffset = startOffset;
        foreach (var suggestion in items) completion.CompletionList.CompletionData.Add(new LuaXCompletionData(suggestion, receiver));
        completion.Closed += (_, _) =>
        {
            if (ReferenceEquals(activeCompletionWindow, completion)) activeCompletionWindow = null;
        };
        activeCompletionWindow = completion;
        completion.Show();
    }

    private void UpdateLuaXSignatureWindow(TextEditor editor)
    {
        if (!ReferenceEquals(editor, scriptEditor)) return;
        var hint = ReadLuaXSignatureHint(editor.Document.Text, editor.CaretOffset);
        if (hint is null)
        {
            if (ReferenceEquals(activeSignatureEditor, editor)) CloseLuaXSignatureWindow();
            return;
        }
        CloseLuaXSignatureWindow();
        var text = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        text.Inlines.Add(new Run(hint.Name + "(") { Foreground = new SolidColorBrush(Color.FromRgb(121, 190, 255)) });
        for (var index = 0; index < hint.Parameters.Length; index++)
        {
            if (index > 0) text.Inlines.Add(new Run(", ") { Foreground = (Brush)FindResource("TextMuted") });
            text.Inlines.Add(new Run(hint.Parameters[index])
            {
                Foreground = index == hint.ActiveArgument ? new SolidColorBrush(Color.FromRgb(255, 213, 128)) : (Brush)FindResource("TextPrimary"),
                FontWeight = index == hint.ActiveArgument ? FontWeights.SemiBold : FontWeights.Normal
            });
        }
        text.Inlines.Add(new Run(")") { Foreground = new SolidColorBrush(Color.FromRgb(121, 190, 255)) });
        var window = new InsightWindow(editor.TextArea)
        {
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 38, 47)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(69, 83, 102)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8),
                Child = text
            },
            CloseAutomatically = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        window.StartOffset = hint.StartOffset;
        window.EndOffset = editor.CaretOffset;
        activeSignatureWindow = window;
        activeSignatureEditor = editor;
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(activeSignatureWindow, window)) return;
            activeSignatureWindow = null;
            activeSignatureEditor = null;
        };
        window.Show();
    }

    private void CloseLuaXSignatureWindow()
    {
        var window = activeSignatureWindow;
        activeSignatureWindow = null;
        activeSignatureEditor = null;
        if (window?.IsVisible == true) window.Close();
    }

    private sealed record LuaXSignatureHint(string Name, string[] Parameters, int ActiveArgument, int StartOffset);

    private sealed record LuaXSignatureDefinition(string Name, string[] Parameters);

    private static readonly IReadOnlyDictionary<string, LuaXSignatureDefinition> LuaXSignatures = new Dictionary<string, LuaXSignatureDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        ["game.getservice"] = new("GetService", ["name: string"]),
        ["instance.new"] = new("Instance.new", ["className: string", "parent: Instance?"]),
        ["task.wait"] = new("task.wait", ["duration: number?"]),
        ["task.spawn"] = new("task.spawn", ["callback: function", "...: any"]),
        ["task.defer"] = new("task.defer", ["callback: function", "...: any"]),
        ["task.delay"] = new("task.delay", ["duration: number", "callback: function", "...: any"]),
        ["task.cancel"] = new("task.cancel", ["thread: coroutine"]),
        ["wait"] = new("wait", ["duration: number?"]),
        ["destroy"] = new("Destroy", []),
        ["getassetpath"] = new("GetAssetPath", ["pathOrName: string"]),
        ["connecttouched"] = new("ConnectTouched", ["object: BasePart", "callback: (other: BasePart) -> ()"]),
        ["waitforchild"] = new("WaitForChild", ["name: string", "timeout: number?"]),
        ["findfirstchild"] = new("FindFirstChild", ["name: string", "recursive: boolean?"]),
        ["getchildren"] = new("GetChildren", []),
        ["isa"] = new("IsA", ["className: string"]),
        ["vector3.new"] = new("Vector3.new", ["x: number", "y: number", "z: number"]),
        ["color3.new"] = new("Color3.new", ["r: number", "g: number", "b: number"]),
        ["color3.fromrgb"] = new("Color3.fromRGB", ["r: number", "g: number", "b: number"]),
        ["color3.fromhsv"] = new("Color3.fromHSV", ["h: number", "s: number", "v: number"]),
        ["color3.fromhex"] = new("Color3.fromHex", ["hex: string"]),
        ["udim.new"] = new("UDim.new", ["scale: number", "offset: number"]),
        ["udim2.new"] = new("UDim2.new", ["xScale: number", "xOffset: number", "yScale: number", "yOffset: number"]),
        ["udim2.fromscale"] = new("UDim2.fromScale", ["xScale: number", "yScale: number"]),
        ["udim2.fromoffset"] = new("UDim2.fromOffset", ["xOffset: number", "yOffset: number"]),
        ["dot"] = new("Dot", ["other: Vector3"]),
        ["cross"] = new("Cross", ["other: Vector3"]),
        ["lerp"] = new("Lerp", ["goal: Vector3 | Color3", "alpha: number"]),
        ["connect"] = new("Connect", ["callback: (...any) -> ()"]),
        ["getproperty"] = new("GetProperty", ["propertyName: string"]),
        ["setproperty"] = new("SetProperty", ["propertyName: string", "value: any"]),
        ["math.clamp"] = new("math.clamp", ["value: number", "minimum: number", "maximum: number"]),
        ["math.atan2"] = new("math.atan2", ["y: number", "x: number"]),
        ["string.format"] = new("string.format", ["format: string", "...: any"]),
        ["table.insert"] = new("table.insert", ["list: table", "value: any"]),
        ["coroutine.yield"] = new("coroutine.yield", ["...: any"])
    };

    private static LuaXSignatureHint? ReadLuaXSignatureHint(string source, int caretOffset)
    {
        if (caretOffset <= 0 || caretOffset > source.Length) return null;
        var prefix = source[..caretOffset];
        var code = GetLuaXCodeContext(prefix, out _, out var isComment);
        if (isComment) return null;
        var delimiters = new List<(char Token, int Offset)>();
        for (var index = 0; index < code.Length; index++)
        {
            var token = code[index];
            if (token is '(' or '[' or '{') delimiters.Add((token, index));
            else if (token is ')' or ']' or '}')
            {
                var expected = token switch { ')' => '(', ']' => '[', _ => '{' };
                for (var openIndex = delimiters.Count - 1; openIndex >= 0; openIndex--)
                {
                    if (delimiters[openIndex].Token != expected) continue;
                    delimiters.RemoveRange(openIndex, delimiters.Count - openIndex);
                    break;
                }
            }
        }

        var callIndex = delimiters.FindLastIndex(delimiter => delimiter.Token == '(');
        if (callIndex < 0) return null;
        var callOpen = delimiters[callIndex].Offset;
        var calleeMatch = Regex.Match(code[..callOpen], @"(?<callee>[A-Za-z_][A-Za-z0-9_]*(?:[.:][A-Za-z_][A-Za-z0-9_]*)*)\s*$");
        if (!calleeMatch.Success) return null;
        var callee = calleeMatch.Groups["callee"].Value;
        var normalizedCallee = callee.Replace(':', '.');
        var simpleName = normalizedCallee.Split('.').Last();
        LuaXSignatureDefinition? definition = null;
        if (simpleName.Equals("SetProperty", StringComparison.OrdinalIgnoreCase) && callee.Contains(':'))
            definition = new LuaXSignatureDefinition("SetProperty", ["propertyName: string", "value: any"]);
        else if (simpleName.Equals("GetProperty", StringComparison.OrdinalIgnoreCase) && callee.Contains(':'))
            definition = new LuaXSignatureDefinition("GetProperty", ["propertyName: string"]);
        else if (!LuaXSignatures.TryGetValue(normalizedCallee, out definition)) LuaXSignatures.TryGetValue(simpleName, out definition);
        if (definition is null) return null;

        var activeArgument = 0;
        var nestedDepth = 0;
        for (var index = callOpen + 1; index < code.Length; index++)
        {
            switch (code[index])
            {
                case '(' or '[' or '{': nestedDepth++; break;
                case ')' or ']' or '}': nestedDepth = Math.Max(0, nestedDepth - 1); break;
                case ',' when nestedDepth == 0: activeArgument++; break;
            }
        }
        return new LuaXSignatureHint(definition.Name, definition.Parameters, activeArgument, calleeMatch.Groups["callee"].Index);
    }

    private bool IsLuaXAssetPathContext(TextEditor editor)
    {
        var source = editor.Document.GetText(0, editor.CaretOffset);
        return Regex.IsMatch(source, @"\bGetAssetPath\s*\(\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)
            || Regex.IsMatch(source, @"\b(?:Texture|Asset|Skybox(?:Front|Back|Left|Right|Top|Bottom))\s*=\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase);
    }

    private static int? GetLuaXStringCompletionStart(TextEditor editor)
    {
        var source = editor.Document.GetText(0, Math.Clamp(editor.CaretOffset, 0, editor.Document.TextLength));
        var match = Regex.Match(source, @"(?<!\\)(?:\\\\)*['""](?<value>[^'""\r\n]*)$");
        return match.Success ? match.Groups["value"].Index : null;
    }

    private string[]? GetLuaXStringCompletions(TextEditor editor)
    {
        var source = editor.Document.GetText(0, Math.Clamp(editor.CaretOffset, 0, editor.Document.TextLength));
        if (GetLuaXStringCompletionStart(editor) is int stringStartOffset)
        {
            var quoteOffset = stringStartOffset - 1;
            if (quoteOffset < 0) return null;
            _ = GetLuaXCodeContext(source[..quoteOffset], out var isNonCode, out var isComment);
            if (isNonCode || isComment) return null;
        }
        if (IsLuaXAssetPathContext(editor))
        {
            var imageAssetContext = Regex.IsMatch(source, @"\b(?:Texture|Skybox(?:Front|Back|Left|Right|Top|Bottom))\s*=\s*(?:GetAssetPath\s*\(\s*)?(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase);
            return GetLuaXAssetPathCompletions(imageAssetContext);
        }
        if (Regex.IsMatch(source, @"\bgame:GetService\s*\(\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)) return ["Workspace", "Players", "Lighting", "ReplicatedStorage", "ServerScriptService", "ServerStorage", "Gui", "Debris", "CollectionService", "RunService"];
        if (Regex.IsMatch(source, @"\bInstance\.new\s*\(\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)) return LuaXCreatableClassNameCompletions;
        if (Regex.IsMatch(source, @"\b(?:Mode|CameraMode)\s*=\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)) return ["Custom", "Subject"];
        if (Regex.IsMatch(source, @"\bLayoutDirection\s*=\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)) return ["Vertical", "Horizontal"];
        if (Regex.IsMatch(source, @"\bFillDirection\s*=\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)) return ["Horizontal", "Vertical"];
        if (Regex.IsMatch(source, @"\bIsA\s*\(\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)) return LuaXClassNameCompletions;
        var propertyMatch = Regex.Match(source, @"(?:SetProperty|GetProperty)\s*\(\s*['""][^'""\r\n]*$", RegexOptions.IgnoreCase);
        if (propertyMatch.Success)
        {
            var propertySeparatorOffset = source.LastIndexOf(':', propertyMatch.Index);
            if (propertySeparatorOffset < 0) return [];
            var propertyReceiver = ReadLuaXReceiver(editor.Document, propertySeparatorOffset);
            return GetLuaXMembers(propertyReceiver, editor)
                .Where(member => member is not ("SetProperty" or "GetProperty" or "WaitForChild" or "waitforchild" or "FindFirstChild" or "GetChildren" or "IsA"))
                .ToArray();
        }
        var childMatch = Regex.Match(source, @"(?:WaitForChild|waitforchild|FindFirstChild)\s*\(\s*['""][^'""\r\n]*$", RegexOptions.IgnoreCase);
        if (!childMatch.Success) return null;
        var separatorOffset = source.LastIndexOf(':', childMatch.Index);
        if (separatorOffset < 0) return [];
        var receiver = ReadLuaXReceiver(editor.Document, separatorOffset);
        var parent = ResolveLuaXExpressionNode(receiver, editor) ?? ResolveLuaXReceiverNode(receiver, editor);
        return parent?.Children
            .Select(child => child.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
    }

    private static bool IsLuaXCompletionSuppressedBeforeCurrentCharacter(TextEditor editor)
    {
        var sourceLength = Math.Clamp(editor.CaretOffset - 1, 0, editor.Document.TextLength);
        var source = editor.Document.GetText(0, sourceLength);
        _ = GetLuaXCodeContext(source, out var isNonCode);
        return isNonCode;
    }

    private string[] GetLuaXAssetPathCompletions(bool imageAssetsOnly = false)
    {
        if (project is null) return [];
        return project.Assets
            .Where(asset => !imageAssetsOnly || IsImageAssetType(asset.Type))
            .Select(asset => asset.RelativePath.Replace('\\', '/').TrimStart('/'))
            .Select(path => path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ? path[7..] : path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ReadLuaXReceiver(ICSharpCode.AvalonEdit.Document.IDocument document, int separatorOffset)
    {
        var end = separatorOffset - 1;
        while (end >= 0 && char.IsWhiteSpace(document.GetCharAt(end))) end--;
        var start = end;
        var parentheses = 0;
        char? quote = null;
        for (var index = end; index >= 0; index--)
        {
            var character = document.GetCharAt(index);
            if (quote is char quoted)
            {
                start = index;
                if (character == quoted && !IsLuaXEscaped(document, index)) quote = null;
                continue;
            }
            if (character is '"' or '\'')
            {
                quote = character;
                start = index;
                continue;
            }
            if (character == ')')
            {
                parentheses++;
                start = index;
                continue;
            }
            if (character == '(')
            {
                if (parentheses == 0) break;
                parentheses--;
                start = index;
                continue;
            }
            if (parentheses == 0 && IsLuaXExpressionBoundary(character)) break;
            start = index;
        }
        return end < start ? string.Empty : document.GetText(start, end - start + 1);
    }

    private static bool IsLuaXEscaped(ICSharpCode.AvalonEdit.Document.IDocument document, int offset)
    {
        var backslashes = 0;
        for (var index = offset - 1; index >= 0 && document.GetCharAt(index) == '\\'; index--) backslashes++;
        return backslashes % 2 != 0;
    }

    private static bool IsLuaXExpressionBoundary(char character) =>
        char.IsWhiteSpace(character) || character is '=' or ',' or ';' or '+' or '-' or '*' or '/' or '%' or '^' or '~' or '<' or '>' or '&' or '|' or '{' or '}' or '[' or ']';

    private string[] GetLuaXGlobals()
    {
        return LuaXGlobalCompletions;
    }

    private static string[] GetLuaXScriptCompletions(TextEditor editor)
    {
        var sourceLength = Math.Clamp(editor.CaretOffset, 0, editor.Document.TextLength);
        var source = editor.Document.GetText(0, sourceLength);
        var code = GetLuaXCodeContext(source, out _);
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        var localDeclarations = LuaXLocalDeclarationPattern.Matches(code).Cast<Match>().ToArray();
        var functionDeclarations = LuaXFunctionParameterPattern.Matches(code).Cast<Match>().ToArray();
        var forDeclarations = LuaXForDeclarationPattern.Matches(code).Cast<Match>()
            .Select(match => (Declaration: match, Do: LuaXDoKeywordPattern.Match(code, match.Index + match.Length)))
            .Where(entry => entry.Do.Success).ToArray();
        var namedFunctions = LuaXNamedFunctionPattern.Matches(code).Cast<Match>().ToArray();
        var scopeOffsets = new List<int> { code.Length };
        scopeOffsets.AddRange(localDeclarations.Select(match => match.Index));
        scopeOffsets.AddRange(functionDeclarations.Select(match => match.Index + match.Length));
        scopeOffsets.AddRange(forDeclarations.Select(entry => entry.Do.Index + entry.Do.Length));
        scopeOffsets.AddRange(namedFunctions.Select(match => match.Index));
        var scopes = GetLuaXScopePaths(code, scopeOffsets);
        var currentScope = scopes[code.Length];
        foreach (Match match in localDeclarations)
        {
            var declarationScope = scopes[match.Index];
            if (IsLuaXScopeVisible(declarationScope, currentScope)) AddLuaXIdentifiers(symbols, LuaXLocalDeclarationPattern, match.Value, "names");
        }
        foreach (Match match in functionDeclarations)
        {
            var functionScope = scopes[match.Index + match.Length];
            if (!IsLuaXScopeVisible(functionScope, currentScope)) continue;
            foreach (var parameter in GetLuaXFunctionParameters(match.Value)) symbols.Add(parameter.Name);
        }
        foreach (var entry in forDeclarations)
        {
            var loopScope = scopes[entry.Do.Index + entry.Do.Length];
            if (IsLuaXScopeVisible(loopScope, currentScope)) AddLuaXIdentifiers(symbols, LuaXForDeclarationPattern, entry.Declaration.Value, "names");
        }
        foreach (Match match in namedFunctions)
        {
            var isLocalFunction = Regex.IsMatch(code[..match.Index], @"\blocal\s+$", RegexOptions.CultureInvariant);
            if (!isLocalFunction || IsLuaXScopeVisible(scopes[match.Index], currentScope))
                symbols.Add(match.Groups["name"].Value.Split('.', ':').Last());
        }
        return LuaXGlobalCompletions.Concat(symbols.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<int, int[]> GetLuaXScopePaths(string code, IEnumerable<int> offsets)
    {
        var positions = offsets.Select(offset => Math.Clamp(offset, 0, code.Length)).Distinct().Order().ToArray();
        var scopes = new Dictionary<int, int[]>(positions.Length);
        var stack = new List<(int Start, bool EndsWithUntil, bool IsBranch)>();
        var pendingLoopStart = -1;
        var positionIndex = 0;
        foreach (Match token in LuaXScopeKeywordPattern.Matches(code[..(positions.Length == 0 ? 0 : positions[^1])]))
        {
            while (positionIndex < positions.Length && positions[positionIndex] <= token.Index)
                scopes[positions[positionIndex++]] = stack.Select(scope => scope.Start).ToArray();
            switch (token.Value)
            {
                case "function":
                    stack.Add((token.Index, false, false));
                    break;
                case "then":
                    stack.Add((token.Index, false, true));
                    break;
                case "else":
                case "elseif":
                    var branchIndex = stack.FindLastIndex(scope => scope.IsBranch);
                    if (branchIndex >= 0) stack.RemoveRange(branchIndex, stack.Count - branchIndex);
                    if (token.Value == "else") stack.Add((token.Index, false, true));
                    break;
                case "for":
                case "while":
                    pendingLoopStart = token.Index;
                    break;
                case "do":
                    stack.Add((pendingLoopStart >= 0 ? pendingLoopStart : token.Index, false, false));
                    pendingLoopStart = -1;
                    break;
                case "repeat":
                    stack.Add((token.Index, true, false));
                    break;
                case "end":
                    var endIndex = stack.FindLastIndex(scope => !scope.EndsWithUntil);
                    if (endIndex >= 0) stack.RemoveAt(endIndex);
                    break;
                case "until":
                    var untilIndex = stack.FindLastIndex(scope => scope.EndsWithUntil);
                    if (untilIndex >= 0) stack.RemoveAt(untilIndex);
                    break;
            }
        }
        while (positionIndex < positions.Length) scopes[positions[positionIndex++]] = stack.Select(scope => scope.Start).ToArray();
        return scopes;
    }

    private static bool IsLuaXScopeVisible(int[] declarationScope, int[] currentScope)
    {
        if (declarationScope.Length > currentScope.Length) return false;
        for (var index = 0; index < declarationScope.Length; index++)
            if (declarationScope[index] != currentScope[index]) return false;
        return true;
    }

    private static void AddLuaXIdentifiers(HashSet<string> symbols, Regex pattern, string source, string group)
    {
        foreach (Match match in pattern.Matches(source))
        foreach (Match identifier in LuaXIdentifierPattern.Matches(match.Groups[group].Value))
            symbols.Add(identifier.Value);
    }

    private static bool IsLuaXCompletionSuppressed(TextEditor editor)
    {
        var sourceLength = Math.Clamp(editor.CaretOffset, 0, editor.Document.TextLength);
        var source = editor.Document.GetText(0, sourceLength);
        _ = GetLuaXCodeContext(source, out var isNonCode);
        return isNonCode;
    }

    private static string GetLuaXCodeContext(string source, out bool isNonCode)
    {
        return GetLuaXCodeContext(source, out isNonCode, out _);
    }

    private static string GetLuaXCodeContext(string source, out bool isNonCode, out bool isComment)
    {
        var code = source.ToCharArray();
        char? quote = null;
        var longBracketLevel = -1;
        var longBracketIsComment = false;
        var inLineComment = false;
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            if (inLineComment)
            {
                BlankLuaXCharacter(code, index);
                if (character is '\r' or '\n') inLineComment = false;
                continue;
            }
            if (quote is char quoted)
            {
                BlankLuaXCharacter(code, index);
                if (character == '\\' && index + 1 < source.Length)
                {
                    BlankLuaXCharacter(code, ++index);
                    continue;
                }
                if (character == quoted) quote = null;
                continue;
            }
            if (longBracketLevel >= 0)
            {
                if (TryReadLuaXLongBracketClose(source, index, longBracketLevel, out var closingLength))
                {
                    for (var offset = 0; offset < closingLength; offset++) BlankLuaXCharacter(code, index + offset);
                    index += closingLength - 1;
                    longBracketLevel = -1;
                    longBracketIsComment = false;
                }
                else BlankLuaXCharacter(code, index);
                continue;
            }
            if (character == '-' && index + 1 < source.Length && source[index + 1] == '-')
            {
                if (TryReadLuaXLongBracket(source, index + 2, out var commentLevel, out var commentOpeningLength))
                {
                    for (var offset = 0; offset < commentOpeningLength + 2; offset++) BlankLuaXCharacter(code, index + offset);
                    index += commentOpeningLength + 1;
                    longBracketLevel = commentLevel;
                    longBracketIsComment = true;
                }
                else
                {
                    BlankLuaXCharacter(code, index++);
                    BlankLuaXCharacter(code, index);
                    inLineComment = true;
                }
                continue;
            }
            if (character is '"' or '\'' or '`')
            {
                quote = character;
                BlankLuaXCharacter(code, index);
                continue;
            }
            if (TryReadLuaXLongBracket(source, index, out var stringLevel, out var openingLength))
            {
                for (var offset = 0; offset < openingLength; offset++) BlankLuaXCharacter(code, index + offset);
                index += openingLength - 1;
                longBracketLevel = stringLevel;
                longBracketIsComment = false;
            }
        }
        isNonCode = quote is not null || longBracketLevel >= 0 || inLineComment;
        isComment = inLineComment || longBracketLevel >= 0 && longBracketIsComment;
        return new string(code);
    }

    private static bool TryReadLuaXLongBracket(string source, int offset, out int level, out int length)
    {
        level = -1;
        length = 0;
        if (offset >= source.Length || source[offset] != '[') return false;
        var index = offset + 1;
        while (index < source.Length && source[index] == '=') index++;
        if (index >= source.Length || source[index] != '[') return false;
        level = index - offset - 1;
        length = index - offset + 1;
        return true;
    }

    private static bool TryReadLuaXLongBracketClose(string source, int offset, int level, out int length)
    {
        length = 0;
        if (offset >= source.Length || source[offset] != ']') return false;
        var index = offset + 1;
        for (var count = 0; count < level; count++, index++)
            if (index >= source.Length || source[index] != '=') return false;
        if (index >= source.Length || source[index] != ']') return false;
        length = index - offset + 1;
        return true;
    }

    private static void BlankLuaXCharacter(char[] source, int index)
    {
        if (index >= 0 && index < source.Length && source[index] is not ('\r' or '\n')) source[index] = ' ';
    }

    private static readonly Regex LuaXIdentifierPattern = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LuaXScopeKeywordPattern = new(@"\b(?:function|then|else|elseif|for|while|do|repeat|end|until)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LuaXDoKeywordPattern = new(@"\bdo\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LuaXLocalDeclarationPattern = new(@"\blocal\s+(?:function\s+)?(?<names>[A-Za-z_][A-Za-z0-9_]*(?:\s*,\s*[A-Za-z_][A-Za-z0-9_]*)*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LuaXFunctionParameterPattern = new(@"\bfunction(?:\s+[A-Za-z_][A-Za-z0-9_]*(?:[.:][A-Za-z_][A-Za-z0-9_]*)*)?\s*\((?<parameters>[^)]*)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LuaXForDeclarationPattern = new(@"\bfor\s+(?<names>[A-Za-z_][A-Za-z0-9_]*(?:\s*,\s*[A-Za-z_][A-Za-z0-9_]*)*)\s+(?:in|=)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LuaXNamedFunctionPattern = new(@"\bfunction\s+(?<name>[A-Za-z_][A-Za-z0-9_]*(?:[.:][A-Za-z_][A-Za-z0-9_]*)*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LuaXParameterNamePattern = new(@"^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LuaXTypedLocalPattern = new(@"\blocal\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?<type>[^=,\r\n;]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LuaXConnectedFunctionPattern = new(@"\.(?<signal>Touched|TextChanged|Triggered|MouseClick|Changed)\s*:\s*Connect\s*\(\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> LuaXInstanceTypes = new(["Instance", "BasePart", "Part", "MeshPart", "WedgePart", "CornerWedgePart", "SpawnLocation", "TrussPart", "Seat", "Humanoid", "ValueBase", "BoolValue", "IntValue", "NumberValue", "StringValue", "Vector3Value", "Color3Value", "ObjectValue", "Light", "PointLight", "SpotLight", "SurfaceLight", "FaceInstance", "Decal", "Texture", "Sound", "Fire", "Smoke", "Sparkles", "ParticleEmitter", "ProximityPrompt", "ClickDetector", "Highlight", "ForceField", "Explosion", "WeldConstraint", "Attachment", "Beam", "Trail", "VectorForce", "LinearVelocity", "Model", "Camera", "Workspace", "Players", "Player", "Lighting", "Sky", "ReplicatedStorage", "ServerScriptService", "ServerStorage", "DataModel", "CollectionService", "RunService", "Folder", "Configuration", "BindableEvent", "BindableFunction", "ModuleScript", "LuaSourceContainer", "BaseScript", "Script", "Gui", "GuiBase2d", "GuiObject", "UIComponent", "LayerCollector", "ScreenGui", "Frame", "ScrollingFrame", "ImageLabel", "ImageButton", "TextLabel", "TextButton", "TextBox", "UICorner", "UIOutline", "UIStroke", "UIGradient", "UIPadding", "UIScale", "UIListLayout", "UIGridLayout", "UITableLayout", "UIAspectRatio"], StringComparer.Ordinal);

    private static IEnumerable<(string Name, string? Type)> GetLuaXFunctionParameters(string source)
    {
        foreach (Match functionMatch in LuaXFunctionParameterPattern.Matches(source))
        {
            var parameters = functionMatch.Groups["parameters"].Value;
            var parameterStart = 0;
            var genericDepth = 0;
            for (var index = 0; index <= parameters.Length; index++)
            {
                if (index < parameters.Length)
                {
                    if (parameters[index] == '<') genericDepth++;
                    else if (parameters[index] == '>') genericDepth = Math.Max(0, genericDepth - 1);
                    else if (parameters[index] != ',' || genericDepth > 0) continue;
                }

                var parameter = parameters[parameterStart..index].Trim();
                parameterStart = index + 1;
                var nameMatch = LuaXParameterNamePattern.Match(parameter);
                if (!nameMatch.Success) continue;
                var name = nameMatch.Groups["name"].Value;
                var type = (string?)null;
                var typeSeparator = parameter.IndexOf(':', nameMatch.Length);
                if (typeSeparator >= 0)
                {
                    var typeText = parameter[(typeSeparator + 1)..].Split('=')[0];
                    type = GetLuaXTypeAnnotation(typeText);
                }
                yield return (name, type);
            }
        }
    }

    private static string? GetLuaXTypeAnnotation(string typeText)
    {
        var typeNames = LuaXIdentifierPattern.Matches(typeText).Select(match => match.Value).ToArray();
        return typeNames.FirstOrDefault(LuaXInstanceTypes.Contains) ?? typeNames.FirstOrDefault();
    }

    private string[] GetLuaXMembers(string receiver, TextEditor editor)
    {
        if (receiver == "game") return ["GetService", "Gui", "Players", "Lighting", "ReplicatedStorage", "ServerScriptService", "ServerStorage", "Workspace", "Debris", "CollectionService", "RunService"];
        if (receiver == "game.Debris") return ["Name", "ClassName", "AddItem"];
        if (receiver == "game.CollectionService") return ["Name", "ClassName", "AddTag", "RemoveTag", "HasTag", "GetTags", "GetTagged", "GetAllTags", "GetInstanceAddedSignal", "GetInstanceRemovedSignal"];
        if (receiver == "game.RunService") return ["Name", "ClassName", "IsRunning", "IsEdit", "IsStudio", "PreSimulation", "Stepped", "PostSimulation", "Heartbeat", "PreRender", "RenderStepped"];
        if (receiver == "Instance") return ["new"];
        if (receiver == "Enum") return ["CameraMode", "LayoutDirection", "FillDirection", "RaycastFilterType", "NormalId"];
        if (receiver == "Enum.NormalId") return ["Top", "Bottom", "Front", "Back", "Left", "Right"];
        if (receiver == "Enum.CameraMode") return ["Custom", "Subject"];
        if (receiver == "Enum.LayoutDirection") return ["Horizontal", "Vertical"];
        if (receiver == "Enum.FillDirection") return ["Horizontal", "Vertical"];
        if (receiver == "Enum.RaycastFilterType") return ["Exclude", "Include"];
        if (receiver == "RaycastParams") return ["new"];
        if (receiver == "Vector3") return ["new", "one", "zero"];
        if (receiver is "Vector2" or "NumberRange" or "NumberSequence" or "ColorSequence" or "NumberSequenceKeypoint" or "ColorSequenceKeypoint") return ["new"];
        if (receiver == "Color3") return ["fromHex", "fromHSV", "fromRGB", "new"];
        if (receiver == "UDim") return ["new"];
        if (receiver == "UDim2") return ["fromOffset", "fromScale", "new", "zero"];
        if (LuaXStandardLibraryMembers.TryGetValue(receiver, out var libraryMembers)) return libraryMembers;
        if (receiver.EndsWith(".Touched", StringComparison.Ordinal) || receiver == "Touched" || receiver.EndsWith(".Triggered", StringComparison.Ordinal) || receiver == "Triggered" || receiver.EndsWith(".MouseClick", StringComparison.Ordinal) || receiver == "MouseClick" || receiver.EndsWith(".Died", StringComparison.Ordinal) || receiver == "Died" || receiver.EndsWith(".Changed", StringComparison.Ordinal) || receiver == "Changed" || receiver.EndsWith(".AttributeChanged", StringComparison.Ordinal) || receiver == "AttributeChanged" || receiver.EndsWith(".Hit", StringComparison.Ordinal) || receiver == "Hit" || receiver.EndsWith(".PreSimulation", StringComparison.Ordinal) || receiver.EndsWith(".Stepped", StringComparison.Ordinal) || receiver.EndsWith(".PostSimulation", StringComparison.Ordinal) || receiver.EndsWith(".Heartbeat", StringComparison.Ordinal) || receiver.EndsWith(".PreRender", StringComparison.Ordinal) || receiver.EndsWith(".RenderStepped", StringComparison.Ordinal)) return ["Connect"];
        var node = ResolveLuaXNode(receiver);
        if (node is null)
        {
            var inferredType = InferLuaXReceiverType(receiver, editor);
            if (inferredType == "Vector3") return ["Cross", "Dot", "Lerp", "Magnitude", "Unit", "X", "Y", "Z"];
            if (inferredType == "Vector2") return ["X", "Y"];
            if (inferredType == "NumberRange") return ["Min", "Max"];
            if (inferredType is "NumberSequence" or "ColorSequence") return ["Keypoints", "Value"];
            if (inferredType == "NumberSequenceKeypoint") return ["Time", "Value", "Envelope"];
            if (inferredType == "ColorSequenceKeypoint") return ["Time", "Value"];
            if (inferredType == "Color3") return ["B", "G", "Lerp", "R", "ToHex", "ToHSV"];
            if (inferredType == "UDim") return ["Offset", "Scale"];
            if (inferredType == "UDim2") return ["X", "Y"];
            if (inferredType == "RaycastParams") return ["FilterDescendantsInstances", "FilterType"];
            if (inferredType == "RaycastResult") return ["Instance", "Position", "Normal", "Distance", "Material"];
            if (inferredType == "RBXScriptSignal") return ["Connect"];
            if (inferredType == "RunService") return ["Name", "ClassName", "IsRunning", "IsEdit", "IsStudio", "PreSimulation", "Stepped", "PostSimulation", "Heartbeat", "PreRender", "RenderStepped"];
            if (inferredType is "number" or "string" or "boolean" or "table") return [];
            node = ResolveLuaXReceiverNode(receiver, editor);
            if (node is null && inferredType is not null && LuaXInstanceTypes.Contains(inferredType))
                node = new SceneNode { Type = inferredType, Name = receiver };
            if (node is null) return LuaXMemberCompletions;
        }

        var members = new List<string> { "Name", "Parent", "Archivable", "ClassName", "Id", "Children", "SetProperty", "GetProperty", "WaitForChild", "FindFirstChild", "GetChildren", "IsA", "Clone", "Destroy", "SetAttribute", "GetAttribute", "GetAttributes", "GetAttributeChangedSignal", "AttributeChanged", "AddTag", "RemoveTag", "HasTag", "GetTags" };
        if (node.Type == "Model") members.AddRange(["Position", "Rotation"]);
        if (node.Type == "BasePart" || SceneNodeTypes.IsBasePart(node)) members.AddRange(["Position", "Rotation", "Size", "Color", "Material", "Transparency", "Anchored", "CanCollide", "CanTouch", "CanQuery", "Velocity", "AssemblyLinearVelocity", "CastShadow", "Touched", "touched"]);
        if (node.Type == "Part") members.AddRange(["Shape", "Texture"]);
        if (node.Type == "SpawnLocation") members.Add("Enabled");
        if (node.Type == "Seat") members.Add("Occupant");
        if (node.Type == "Humanoid") members.AddRange(["Health", "MaxHealth", "WalkSpeed", "JumpPower", "Died", "TakeDamage"]);
        if (node.Type == "ForceField") members.Add("Visible");
        if (node.Type == "Explosion") members.AddRange(["Position", "BlastRadius", "BlastPressure", "DestroyJointRadiusPercent", "Visible", "Hit"]);
        if (SceneNodeTypes.IsParticleEffect(node)) members.AddRange(["Enabled", "TimeScale"]);
        if (SceneNodeTypes.IsSizedParticleEffect(node)) members.AddRange(["Color", "Size"]);
        if (node.Type == "ParticleEmitter") members.AddRange(["Color", "Size", "Transparency", "Rate", "Speed", "Lifetime", "SpreadAngle", "EmissionDirection", "Texture", "Emit", "Clear"]);
        if (node.Type == "Sparkles") members.AddRange(["SparkleColor", "Color"]);
        if (node.Type == "Fire") members.AddRange(["Heat", "SecondaryColor"]);
        if (node.Type == "Smoke") members.AddRange(["Opacity", "RiseVelocity"]);
        if (node.Type == "Debris") members.Add("AddItem");
        if (node.Type == "CollectionService") members.AddRange(["AddTag", "RemoveTag", "HasTag", "GetTags", "GetTagged", "GetAllTags", "GetInstanceAddedSignal", "GetInstanceRemovedSignal"]);
        if (node.Type == "RunService") members.AddRange(["IsRunning", "IsEdit", "IsStudio", "PreSimulation", "Stepped", "PostSimulation", "Heartbeat", "PreRender", "RenderStepped"]);
        if (SceneNodeTypes.IsValueObject(node) || node.Type == "ValueBase") members.AddRange(["Value", "Changed"]);
        if (node.Type == "BindableEvent") members.AddRange(["Event", "Fire"]);
        if (node.Type == "BindableFunction") members.AddRange(["OnInvoke", "Invoke"]);
        if (node.Type == "MeshPart") members.Add("Asset");
        if (node.Type == "Light" || SceneNodeTypes.IsLight(node)) members.AddRange(["Enabled", "Color", "Brightness", "Range", "Position"]);
        if (node.Type is "SpotLight" or "SurfaceLight") members.Add("Angle");
        if (node.Type == "SpotLight") members.Add("Rotation");
        if (node.Type is "FaceInstance" or "Decal" or "Texture" or "SurfaceLight") members.Add("Face");
        if (node.Type is "Decal" or "Texture") members.AddRange(["Asset", "Transparency", "Visible"]);
        if (node.Type == "Texture") members.AddRange(["StudsPerTileU", "StudsPerTileV", "OffsetStudsU", "OffsetStudsV"]);
        if (node.Type == "Sound") members.AddRange(["Asset", "Volume", "Looped", "Playing", "RollOffMinDistance", "RollOffMaxDistance", "Play", "Stop"]);
        if (node.Type == "ProximityPrompt") members.AddRange(["Enabled", "ActionText", "ObjectText", "MaxActivationDistance", "Triggered"]);
        if (node.Type == "ClickDetector") members.AddRange(["Enabled", "MaxActivationDistance", "MouseClick"]);
        if (node.Type == "Highlight") members.AddRange(["Enabled", "FillColor", "FillTransparency", "OutlineColor", "OutlineTransparency"]);
        if (node.Type == "WeldConstraint") members.AddRange(["Enabled", "Part0", "Part1"]);
        if (node.Type == "Beam") members.AddRange(["Enabled", "Attachment0", "Attachment1", "Width0", "Width1", "Color", "Transparency"]);
        if (node.Type == "Trail") members.AddRange(["Enabled", "Attachment0", "Attachment1", "Lifetime", "MinLength", "MaxLength", "WidthScale", "Color", "Transparency", "Texture", "FaceCamera", "TextureMode", "TextureLength", "Clear"]);
        if (node.Type == "VectorForce") members.AddRange(["Enabled", "Attachment0", "Force", "RelativeTo"]);
        if (node.Type == "LinearVelocity") members.AddRange(["Enabled", "Attachment0", "RelativeTo", "VelocityConstraintMode", "VectorVelocity", "LineDirection", "LineVelocity", "PlaneVelocity", "PrimaryTangentAxis", "SecondaryTangentAxis", "ForceLimitsEnabled", "MaxForce"]);
        if (node.Type == "Attachment") members.AddRange(["Position", "Rotation"]);
        if (node.Type == "Camera") members.AddRange(["Mode", "CameraMode", "Subject", "FieldOfView", "Position", "Rotation"]);
        if (node.Type == "Workspace") members.AddRange(["Gravity", "AutoManageCharacter", "Raycast", "SkyboxFront", "SkyboxBack", "SkyboxLeft", "SkyboxRight", "SkyboxTop", "SkyboxBottom"]);
        if (node.Type == "Lighting") members.AddRange(["Ambient", "Brightness", "ClockTime", "GlobalShadows"]);
        if (node.Type == "Sky") members.AddRange(["SkyboxFt", "SkyboxBk", "SkyboxLf", "SkyboxRt", "SkyboxUp", "SkyboxDn"]);
        if (node.Type == "Players") members.Add("LocalPlayer");
        if (node.Type == "Player") members.Add("Character");
        if (node.Type is "GuiBase2d" or "GuiObject" or "ScreenGui" or "Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox") members.AddRange(["Visible", "Position", "Size", "ZIndex", "LayoutOrder"]);
        if (node.Type is "GuiObject" or "Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox") members.AddRange(["Color", "Rotation"]);
        if (node.Type is "TextLabel" or "TextButton" or "TextBox") members.AddRange(["Text", "TextColor", "TextSize"]);
        if (node.Type is "TextButton" or "ImageButton") members.AddRange(["Activated", "MouseButton1Click"]);
        if (node.Type == "TextBox") members.Add("TextChanged");
        if (node.Type is "ImageLabel" or "ImageButton") members.Add("Asset");
        if (node.Type == "UICorner") members.Add("CornerRadius");
        if (node.Type is "UIOutline" or "UIStroke") members.AddRange(["StrokeColor", "StrokeThickness"]);
        if (node.Type == "UIGradient") members.AddRange(["Enabled", "StartColor", "EndColor", "Rotation"]);
        if (node.Type == "UIPadding") members.AddRange(["PaddingLeft", "PaddingTop", "PaddingRight", "PaddingBottom"]);
        if (node.Type == "UIScale") members.Add("Scale");
        if (node.Type == "UIListLayout") members.AddRange(["LayoutDirection", "LayoutSpacing"]);
        if (node.Type == "UIGridLayout") members.AddRange(["CellSize", "CellPadding", "FillDirection", "FillDirectionMaxCells"]);
        if (node.Type == "UITableLayout") members.AddRange(["LayoutSpacing", "TableColumns"]);
        if (node.Type == "UIAspectRatio") members.Add("AspectRatio");
        return members.Concat(node.Children.Select(child => child.Name)).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private string[] GetLuaXMethods(string receiver, TextEditor editor)
    {
        if (receiver == "game") return ["GetService"];
        if (receiver == "game.RunService") return ["IsRunning", "IsEdit", "IsStudio"];
        if (receiver == "workspace" || receiver == "game.Workspace") return ["Raycast", "GetProperty", "SetProperty", "WaitForChild", "FindFirstChild", "GetChildren", "IsA"];
        if (receiver.Equals("Touched", StringComparison.OrdinalIgnoreCase) || receiver.EndsWith(".Touched", StringComparison.OrdinalIgnoreCase)
            || receiver.EndsWith(".Activated", StringComparison.OrdinalIgnoreCase) || receiver.EndsWith(".MouseButton1Click", StringComparison.OrdinalIgnoreCase)
            || receiver.EndsWith(".TextChanged", StringComparison.OrdinalIgnoreCase) || receiver.EndsWith(".Triggered", StringComparison.OrdinalIgnoreCase)
            || receiver.EndsWith(".MouseClick", StringComparison.OrdinalIgnoreCase) || receiver.EndsWith(".Changed", StringComparison.OrdinalIgnoreCase)
            || receiver.EndsWith(".Hit", StringComparison.OrdinalIgnoreCase)
            || receiver.EndsWith(".PreSimulation", StringComparison.OrdinalIgnoreCase)
            || receiver.EndsWith(".Stepped", StringComparison.OrdinalIgnoreCase)
            || receiver.EndsWith(".PostSimulation", StringComparison.OrdinalIgnoreCase)
            || receiver.EndsWith(".Heartbeat", StringComparison.OrdinalIgnoreCase)
            || receiver.EndsWith(".PreRender", StringComparison.OrdinalIgnoreCase)
            || receiver.EndsWith(".RenderStepped", StringComparison.OrdinalIgnoreCase)) return ["Connect"];
        var inferredType = InferLuaXReceiverType(receiver, editor);
        if (inferredType is "Vector3Constructor" or "Vector2Constructor" or "NumberRangeConstructor" or "NumberSequenceConstructor" or "ColorSequenceConstructor" or "Color3Constructor" or "UDimConstructor" or "UDim2Constructor" or "RaycastParamsConstructor" or "RaycastParams" or "RaycastResult" or "UDim" or "UDim2" or "Vector2" or "NumberRange" or "NumberSequence" or "ColorSequence") return [];
        if (inferredType == "Vector3") return ["Cross", "Dot", "Lerp"];
        if (inferredType == "Color3") return ["Lerp", "ToHex", "ToHSV"];
        if (inferredType == "RBXScriptSignal") return ["Connect"];
        if (inferredType == "RunService") return ["IsRunning", "IsEdit", "IsStudio"];
        if (inferredType == "Workspace") return ["Raycast", "GetProperty", "SetProperty", "WaitForChild", "FindFirstChild", "GetChildren", "IsA"];
        if (inferredType is "number" or "string" or "boolean" or "table") return [];
        var receiverNode = ResolveLuaXNode(receiver) ?? ResolveLuaXReceiverNode(receiver, editor);
        if (inferredType == "Humanoid" || receiverNode?.Type == "Humanoid") return ["TakeDamage", "GetProperty", "SetProperty", "WaitForChild", "FindFirstChild", "GetChildren", "IsA", "Clone", "Destroy"];
        if (inferredType == "Sound" || receiverNode?.Type == "Sound") return ["GetProperty", "SetProperty", "WaitForChild", "waitforchild", "FindFirstChild", "GetChildren", "IsA", "Clone", "Destroy", "Play", "Stop"];
        if (inferredType == "ParticleEmitter" || receiverNode?.Type == "ParticleEmitter") return ["GetProperty", "SetProperty", "WaitForChild", "FindFirstChild", "GetChildren", "IsA", "Clone", "Destroy", "Emit", "Clear"];
        if (inferredType == "Trail" || receiverNode?.Type == "Trail") return ["GetProperty", "SetProperty", "WaitForChild", "FindFirstChild", "GetChildren", "IsA", "Clone", "Destroy", "Clear"];
        return LuaXInstanceTypes.Contains(inferredType ?? string.Empty) || receiverNode is not null
            ? ["GetProperty", "SetProperty", "WaitForChild", "waitforchild", "FindFirstChild", "GetChildren", "IsA", "Clone", "Destroy"]
            : LuaXMethodCompletions;
    }

    private string? InferLuaXReceiverType(string receiver, TextEditor editor, HashSet<string>? visited = null)
    {
        if (receiver == "Vector3") return "Vector3Constructor";
        if (receiver == "Vector2") return "Vector2Constructor";
        if (receiver == "NumberRange") return "NumberRangeConstructor";
        if (receiver == "NumberSequence") return "NumberSequenceConstructor";
        if (receiver == "NumberSequenceKeypoint") return "NumberSequenceKeypointConstructor";
        if (receiver == "ColorSequence") return "ColorSequenceConstructor";
        if (receiver == "ColorSequenceKeypoint") return "ColorSequenceKeypointConstructor";
        if (receiver == "Color3") return "Color3Constructor";
        if (receiver == "UDim") return "UDimConstructor";
        if (receiver == "UDim2") return "UDim2Constructor";
        if (receiver == "RaycastParams") return "RaycastParamsConstructor";
        var directExpression = StripExpressionParentheses(receiver);
        if (Regex.IsMatch(directExpression, @"^Vector3\s*\.\s*new\s*\(", RegexOptions.CultureInvariant)) return "Vector3";
        if (Regex.IsMatch(directExpression, @"^Vector2\s*\.\s*new\s*\(", RegexOptions.CultureInvariant)) return "Vector2";
        if (Regex.IsMatch(directExpression, @"^NumberRange\s*\.\s*new\s*\(", RegexOptions.CultureInvariant)) return "NumberRange";
        if (Regex.IsMatch(directExpression, @"^NumberSequence\s*\.\s*new\s*\(", RegexOptions.CultureInvariant)) return "NumberSequence";
        if (Regex.IsMatch(directExpression, @"^NumberSequenceKeypoint\s*\.\s*new\s*\(", RegexOptions.CultureInvariant)) return "NumberSequenceKeypoint";
        if (Regex.IsMatch(directExpression, @"^ColorSequence\s*\.\s*new\s*\(", RegexOptions.CultureInvariant)) return "ColorSequence";
        if (Regex.IsMatch(directExpression, @"^ColorSequenceKeypoint\s*\.\s*new\s*\(", RegexOptions.CultureInvariant)) return "ColorSequenceKeypoint";
        if (Regex.IsMatch(directExpression, @"^Color3\s*\.\s*(?:new|fromRGB|fromHSV|fromHex)\s*\(", RegexOptions.CultureInvariant)) return "Color3";
        if (Regex.IsMatch(directExpression, @"^UDim\s*\.\s*new\s*\(", RegexOptions.CultureInvariant)) return "UDim";
        if (Regex.IsMatch(directExpression, @"^UDim2\s*\.\s*(?:new|fromOffset|fromScale)\s*\(", RegexOptions.CultureInvariant)) return "UDim2";
        if (Regex.IsMatch(directExpression, @"^RaycastParams\s*\.\s*new\s*\(", RegexOptions.CultureInvariant)) return "RaycastParams";
        var memberMatch = Regex.Match(directExpression, @"^(?<parent>.+)\.(?<member>[A-Za-z_][A-Za-z0-9_]*)$", RegexOptions.CultureInvariant);
        if (memberMatch.Success)
        {
            visited ??= new HashSet<string>(StringComparer.Ordinal);
            if (!visited.Add(receiver)) return null;
            var parentExpression = memberMatch.Groups["parent"].Value;
            var member = memberMatch.Groups["member"].Value;
            var parentNode = ResolveLuaXNode(parentExpression) ?? ResolveLuaXReceiverNode(parentExpression, editor);
            var memberType = InferLuaXMemberType(parentNode, null, member);
            if (memberType is not null) return memberType;
            var parentType = InferLuaXReceiverType(parentExpression, editor, visited);
            return InferLuaXMemberType(null, parentType, member);
        }
        if (ResolveLuaXNode(receiver) is not null) return "Instance";
        visited ??= new HashSet<string>(StringComparer.Ordinal);
        if (!visited.Add(receiver)) return null;
        var parameterType = FindLuaXParameterType(receiver, editor);
        if (parameterType is not null && !HasLuaXVisibleLocalShadowingParameter(receiver, editor)) return parameterType;
        var localType = FindLuaXTypedLocalType(receiver, editor);
        if (localType is not null) return localType;
        var expression = FindLuaXVariableExpression(receiver, editor);
        if (expression is null) return null;
        expression = StripExpressionParentheses(expression);
        if (expression.StartsWith("Vector3.new", StringComparison.Ordinal)) return "Vector3";
        if (expression.StartsWith("Vector2.new", StringComparison.Ordinal)) return "Vector2";
        if (expression.StartsWith("NumberRange.new", StringComparison.Ordinal)) return "NumberRange";
        if (expression.StartsWith("NumberSequence.new", StringComparison.Ordinal)) return "NumberSequence";
        if (expression.StartsWith("NumberSequenceKeypoint.new", StringComparison.Ordinal)) return "NumberSequenceKeypoint";
        if (expression.StartsWith("ColorSequence.new", StringComparison.Ordinal)) return "ColorSequence";
        if (expression.StartsWith("ColorSequenceKeypoint.new", StringComparison.Ordinal)) return "ColorSequenceKeypoint";
        if (expression.StartsWith("Color3.new", StringComparison.Ordinal) || expression.StartsWith("Color3.fromRGB", StringComparison.Ordinal) || expression.StartsWith("Color3.fromHSV", StringComparison.Ordinal) || expression.StartsWith("Color3.fromHex", StringComparison.Ordinal)) return "Color3";
        if (expression.StartsWith("UDim.new", StringComparison.Ordinal)) return "UDim";
        if (expression.StartsWith("UDim2.new", StringComparison.Ordinal) || expression.StartsWith("UDim2.fromOffset", StringComparison.Ordinal) || expression.StartsWith("UDim2.fromScale", StringComparison.Ordinal)) return "UDim2";
        if (expression.StartsWith("RaycastParams.new", StringComparison.Ordinal)) return "RaycastParams";
        if (ResolveLuaXExpressionNode(expression, editor) is not null) return "Instance";
        return InferLuaXReceiverType(expression, editor, visited);
    }

    private static string? FindLuaXParameterType(string variable, TextEditor editor)
    {
        var sourceLength = Math.Clamp(editor.CaretOffset, 0, editor.Document.TextLength);
        var source = GetLuaXCodeContext(editor.Document.GetText(0, sourceLength), out _);
        var functionMatches = LuaXFunctionParameterPattern.Matches(source).Cast<Match>().ToArray();
        var scopeOffsets = functionMatches.Select(match => match.Index + match.Length).Append(source.Length);
        var scopes = GetLuaXScopePaths(source, scopeOffsets);
        var currentScope = scopes[source.Length];
        (string? Type, int ScopeDepth, int Index)? bestMatch = null;
        foreach (var functionMatch in functionMatches)
        {
            var functionScope = scopes[functionMatch.Index + functionMatch.Length];
            if (!IsLuaXScopeVisible(functionScope, currentScope)) continue;
            var parameter = GetLuaXFunctionParameters(functionMatch.Value).FirstOrDefault(candidate => candidate.Name == variable);
            if (parameter.Name != variable) continue;
            var inferredType = parameter.Type;
            if (inferredType is null)
            {
                var connectedFunction = LuaXConnectedFunctionPattern.Match(source[..functionMatch.Index]);
                var callbackParameters = GetLuaXFunctionParameters(functionMatch.Value).ToArray();
                if (connectedFunction.Success && callbackParameters.Length > 0 && callbackParameters[0].Name == variable)
                    inferredType = connectedFunction.Groups["signal"].Value.ToLowerInvariant() switch
                    {
                        "touched" => "BasePart",
                        "triggered" or "mouseclick" => "Model",
                        "changed" => null,
                        _ => "string"
                    };
            }
            var candidate = (inferredType, functionScope.Length, functionMatch.Index);
            if (bestMatch is null || candidate.Length > bestMatch.Value.ScopeDepth
                || candidate.Length == bestMatch.Value.ScopeDepth && candidate.Index > bestMatch.Value.Index)
                bestMatch = candidate;
        }
        return bestMatch?.Type;
    }

    private static string? FindLuaXTypedLocalType(string variable, TextEditor editor)
    {
        var sourceLength = Math.Clamp(editor.CaretOffset, 0, editor.Document.TextLength);
        var source = GetLuaXCodeContext(editor.Document.GetText(0, sourceLength), out _);
        var matches = LuaXTypedLocalPattern.Matches(source).Cast<Match>().ToArray();
        var scopes = GetLuaXScopePaths(source, matches.Select(match => match.Index).Append(source.Length));
        var currentScope = scopes[source.Length];
        Match? bestMatch = null;
        int[]? bestScope = null;
        foreach (var match in matches)
        {
            if (match.Groups["name"].Value != variable) continue;
            var declarationScope = scopes[match.Index];
            if (!IsLuaXScopeVisible(declarationScope, currentScope)) continue;
            if (bestMatch is null || declarationScope.Length > bestScope!.Length
                || declarationScope.Length == bestScope.Length && match.Index > bestMatch.Index)
            {
                bestMatch = match;
                bestScope = declarationScope;
            }
        }
        return bestMatch is null ? null : GetLuaXTypeAnnotation(bestMatch.Groups["type"].Value);
    }

    private static bool HasLuaXVisibleLocalShadowingParameter(string variable, TextEditor editor)
    {
        var sourceLength = Math.Clamp(editor.CaretOffset, 0, editor.Document.TextLength);
        var code = GetLuaXCodeContext(editor.Document.GetText(0, sourceLength), out _);
        var functionMatches = LuaXFunctionParameterPattern.Matches(code).Cast<Match>().ToArray();
        var localDeclarations = LuaXLocalDeclarationPattern.Matches(code).Cast<Match>().ToArray();
        var scopes = GetLuaXScopePaths(code, functionMatches.Select(match => match.Index + match.Length)
            .Concat(localDeclarations.Select(match => match.Index)).Append(code.Length));
        var currentScope = scopes[code.Length];
        int[]? parameterScope = null;
        var parameterIndex = -1;
        foreach (var functionMatch in functionMatches)
        {
            if (!GetLuaXFunctionParameters(functionMatch.Value).Any(parameter => parameter.Name == variable)) continue;
            var scope = scopes[functionMatch.Index + functionMatch.Length];
            if (!IsLuaXScopeVisible(scope, currentScope)) continue;
            if (parameterScope is null || scope.Length > parameterScope.Length
                || scope.Length == parameterScope.Length && functionMatch.Index + functionMatch.Length > parameterIndex)
            {
                parameterScope = scope;
                parameterIndex = functionMatch.Index + functionMatch.Length;
            }
        }
        if (parameterScope is null) return false;

        foreach (var declaration in localDeclarations)
        {
            if (!LuaXIdentifierPattern.Matches(declaration.Groups["names"].Value).Any(name => name.Value == variable)) continue;
            var scope = scopes[declaration.Index];
            if (!IsLuaXScopeVisible(scope, currentScope)) continue;
            if (scope.Length > parameterScope.Length || scope.Length == parameterScope.Length && declaration.Index > parameterIndex) return true;
        }
        return false;
    }

    private string? InferLuaXMemberType(SceneNode? node, string? receiverType, string member)
    {
        if (node?.Type == "ParticleEmitter")
        {
            var emitterType = member.ToLowerInvariant() switch
            {
                "color" => "ColorSequence",
                "size" or "transparency" => "NumberSequence",
                "speed" or "lifetime" => "NumberRange",
                "spreadangle" => "Vector2",
                "rate" or "timescale" => "number",
                "emissiondirection" or "texture" => "string",
                _ => null
            };
            if (emitterType is not null) return emitterType;
        }
        if (node?.Type == "Trail")
        {
            var trailType = member.ToLowerInvariant() switch
            {
                "color" => "ColorSequence",
                "transparency" or "widthscale" => "NumberSequence",
                "attachment0" or "attachment1" => "Attachment",
                "lifetime" or "minlength" or "maxlength" or "texturelength" => "number",
                "enabled" or "facecamera" => "boolean",
                "texture" or "texturemode" => "string",
                _ => null
            };
            if (trailType is not null) return trailType;
        }
        if (node?.Type == "VectorForce")
        {
            var forceType = member.ToLowerInvariant() switch
            {
                "attachment0" => "Attachment",
                "force" => "Vector3",
                "relativeto" => "string",
                "enabled" => "boolean",
                _ => null
            };
            if (forceType is not null) return forceType;
        }
        if (node?.Type == "LinearVelocity")
        {
            var velocityType = member.ToLowerInvariant() switch
            {
                "attachment0" => "Attachment",
                "vectorvelocity" or "linedirection" or "primarytangentaxis" or "secondarytangentaxis" => "Vector3",
                "planevelocity" => "Vector2",
                "linevelocity" or "maxforce" => "number",
                "forcelimitsenabled" or "enabled" => "boolean",
                "relativeto" or "velocityconstraintmode" => "string",
                _ => null
            };
            if (velocityType is not null) return velocityType;
        }
        if (node is not null)
        {
            var propertyType = member.ToLowerInvariant() switch
            {
                "position" => IsLuaXGuiObject(node) || node.Type is "GuiBase2d" or "GuiObject" ? "UDim2" : "Vector3",
                "rotation" => "Vector3",
                "size" => IsLuaXGuiObject(node) || node.Type is "GuiBase2d" or "GuiObject" ? "UDim2" : "Vector3",
                "velocity" or "assemblylinearvelocity" => "Vector3",
                "color" or "fillcolor" or "outlinecolor" => "Color3",
                "parent" or "subject" => "Instance",
                "part0" or "part1" => "BasePart",
                "touched" or "activated" or "mousebutton1click" or "textchanged" or "triggered" or "mouseclick" or "hit" => "RBXScriptSignal",
                "fieldofview" or "textsize" or "strokethickness" or "cornerradius" or "layoutspacing" or "tablecolumns" or "aspectratio" or "transparency" or "filltransparency" or "outlinetransparency" or "brightness" or "range" or "angle" or "maxactivationdistance" or "blastradius" or "blastpressure" or "destroyjointradiuspercent" => "number",
                "anchored" or "cancollide" or "cantouch" or "castshadow" or "visible" or "enabled" => "boolean",
                "name" or "id" or "classname" or "type" or "texture" or "asset" or "face" or "text" or "textcolor" or "strokecolor" or "layoutdirection" or "cameramode" or "mode" or "skyboxfront" or "skyboxback" or "skyboxleft" or "skyboxright" or "skyboxtop" or "skyboxbottom" or "actiontext" or "objecttext" => "string",
                _ => null
            };
            if (propertyType is not null) return propertyType;
        }

        return (receiverType, member.ToLowerInvariant()) switch
        {
            ("ParticleEmitter", "color") => "ColorSequence",
            ("ParticleEmitter", "size" or "transparency") => "NumberSequence",
            ("ParticleEmitter", "speed" or "lifetime") => "NumberRange",
            ("ParticleEmitter", "spreadangle") => "Vector2",
            ("ParticleEmitter", "rate" or "timescale") => "number",
            ("Trail", "color") => "ColorSequence",
            ("Trail", "transparency" or "widthscale") => "NumberSequence",
            ("Trail", "attachment0" or "attachment1") => "Attachment",
            ("Trail", "lifetime" or "minlength" or "maxlength" or "texturelength") => "number",
            ("Trail", "facecamera") => "boolean",
            ("Trail", "texturemode") => "string",
            ("VectorForce", "attachment0") => "Attachment",
            ("VectorForce", "force") => "Vector3",
            ("VectorForce", "relativeto") => "string",
            ("LinearVelocity", "attachment0") => "Attachment",
            ("LinearVelocity", "vectorvelocity" or "linedirection" or "primarytangentaxis" or "secondarytangentaxis") => "Vector3",
            ("LinearVelocity", "planevelocity") => "Vector2",
            ("LinearVelocity", "linevelocity" or "maxforce") => "number",
            ("LinearVelocity", "forcelimitsenabled") => "boolean",
            ("LinearVelocity", "relativeto" or "velocityconstraintmode") => "string",
            ("Vector2", "x" or "y") => "number",
            ("NumberRange", "min" or "max") => "number",
            ("NumberSequence", "value") => "number",
            ("ColorSequence", "value") => "Color3",
            ("BasePart", "position" or "rotation" or "size" or "velocity" or "assemblylinearvelocity") => "Vector3",
            ("BasePart", "color") => "Color3",
            ("BasePart", "anchored" or "cancollide" or "cantouch" or "castshadow") => "boolean",
            ("Part", "texture") => "string",
            ("MeshPart", "asset") => "string",
            ("GuiObject" or "GuiBase2d", "position" or "size") => "UDim2",
            ("GuiObject", "color") => "Color3",
            ("GuiObject", "rotation") => "Vector3",
            ("GuiObject" or "GuiBase2d", "visible") => "boolean",
            ("Vector3", "x" or "y" or "z" or "magnitude") => "number",
            ("Vector3", "unit") => "Vector3",
            ("Color3", "r" or "g" or "b") => "number",
            ("UDim2", "x" or "y") => "UDim",
            ("UDim", "scale" or "offset") => "number",
            ("Instance", "parent" or "subject") => "Instance",
            _ => null
        };
    }

    private bool IsLuaXGuiObject(SceneNode node) =>
        node.Type is "Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox"
        && IsGuiNode(node);

    private SceneNode? ResolveLuaXReceiverNode(string receiver, TextEditor editor, HashSet<string>? visited = null)
    {
        var node = ResolveLuaXExpressionNode(receiver, editor);
        if (node is not null) return node;
        visited ??= new HashSet<string>(StringComparer.Ordinal);
        if (!visited.Add(receiver)) return null;
        var expression = FindLuaXVariableExpression(receiver, editor);
        if (expression is null) return null;
        expression = StripExpressionParentheses(expression);
        node = ResolveLuaXExpressionNode(expression, editor);
        if (node is not null) return node;
        return IsLuaXIdentifier(expression) ? ResolveLuaXReceiverNode(expression, editor, visited) : null;
    }

    private static string? FindLuaXVariableExpression(string variable, TextEditor editor)
    {
        if (!IsLuaXIdentifier(variable)) return null;
        var sourceLength = Math.Clamp(editor.CaretOffset, 0, editor.Document.TextLength);
        var source = editor.Document.GetText(0, sourceLength);
        var code = GetLuaXCodeContext(source, out _);
        var pattern = $@"(?<![A-Za-z0-9_.:])(?:local\s+)?{Regex.Escape(variable)}\s*=\s*(?<expression>[^\r\n;]+)";
        var assignments = Regex.Matches(code, pattern).Cast<Match>().ToArray();
        if (assignments.Length == 0) return null;
        var declarations = LuaXLocalDeclarationPattern.Matches(code).Cast<Match>().ToArray();
        var scopes = GetLuaXScopePaths(code, assignments.Select(match => match.Index)
            .Concat(declarations.Select(match => match.Index)).Append(code.Length));
        var currentScope = scopes[code.Length];
        var localBindings = new List<(string Name, int[] Scope, int Index)>();
        foreach (var declaration in declarations)
        {
            var declarationScope = scopes[declaration.Index];
            foreach (Match name in LuaXIdentifierPattern.Matches(declaration.Groups["names"].Value))
                localBindings.Add((name.Value, declarationScope, declaration.Index));
        }
        Match? bestAssignment = null;
        int bestScopeDepth = -1;
        foreach (var assignment in assignments)
        {
            var assignmentScope = scopes[assignment.Index];
            var isLocal = assignment.Value.StartsWith("local ", StringComparison.Ordinal);
            var binding = isLocal
                ? (Name: variable, Scope: assignmentScope, Index: assignment.Index)
                : localBindings.Where(candidate => candidate.Name == variable && candidate.Index < assignment.Index && IsLuaXScopeVisible(candidate.Scope, assignmentScope))
                    .OrderByDescending(candidate => candidate.Scope.Length).ThenByDescending(candidate => candidate.Index)
                    .Select(candidate => (candidate.Name, candidate.Scope, candidate.Index)).FirstOrDefault();
            var bindingScope = isLocal ? assignmentScope : binding.Scope ?? [];
            if (!IsLuaXScopeVisible(bindingScope, currentScope)) continue;
            if (bestAssignment is null || bindingScope.Length > bestScopeDepth
                || bindingScope.Length == bestScopeDepth && assignment.Index > bestAssignment.Index)
            {
                bestAssignment = assignment;
                bestScopeDepth = bindingScope.Length;
            }
        }
        if (bestAssignment is null) return null;
        var expression = bestAssignment.Groups["expression"];
        return source.Substring(expression.Index, expression.Length).Trim();
    }

    private SceneNode? ResolveLuaXExpressionNode(string expression, TextEditor editor)
    {
        expression = StripExpressionParentheses(expression.Trim());
        var node = ResolveLuaXNode(expression);
        if (node is not null) return node;
        if (expression == "game.Debris") return new SceneNode { Type = "Debris", Name = "Debris" };
        if (Regex.IsMatch(expression, @"^game:GetService\s*\(\s*[""']Debris[""']\s*\)$")) return new SceneNode { Type = "Debris", Name = "Debris" };
        if (expression == "game.CollectionService") return new SceneNode { Type = "CollectionService", Name = "CollectionService" };
        if (Regex.IsMatch(expression, @"^game:GetService\s*\(\s*[""']CollectionService[""']\s*\)$")) return new SceneNode { Type = "CollectionService", Name = "CollectionService" };
        if (expression == "game.RunService") return new SceneNode { Type = "RunService", Name = "RunService" };
        if (Regex.IsMatch(expression, @"^game:GetService\s*\(\s*[""']RunService[""']\s*\)$")) return new SceneNode { Type = "RunService", Name = "RunService" };
        var serviceMatch = Regex.Match(expression, @"^game:GetService\s*\(\s*[""'](?<service>Workspace|Players|Lighting|ReplicatedStorage|ServerScriptService|ServerStorage|Gui)[""']\s*\)$");
        if (serviceMatch.Success) return serviceMatch.Groups["service"].Value switch { "Workspace" => project?.Workspace, "Players" => project?.Players, "Lighting" => project?.Lighting, "ReplicatedStorage" => project?.ReplicatedStorage, "ServerScriptService" => project?.ServerScriptService, "ServerStorage" => project?.ServerStorage, _ => project?.Gui };
        var childMatch = Regex.Match(expression, @"^(?<parent>.+):(?<method>WaitForChild|waitforchild|FindFirstChild)\s*\(\s*[""'](?<name>[^""']+)[""']\s*(?:,\s*(?<recursive>true|false)\s*)?\)$", RegexOptions.IgnoreCase);
        if (childMatch.Success)
        {
            var parent = ResolveLuaXExpressionNode(childMatch.Groups["parent"].Value, editor);
            var childName = childMatch.Groups["name"].Value;
            var isRecursive = childMatch.Groups["method"].Value.Equals("FindFirstChild", StringComparison.OrdinalIgnoreCase)
                && childMatch.Groups["recursive"].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (parent is null) return null;
            IEnumerable<SceneNode> children = isRecursive ? GameProjectStore.Flatten(parent.Children) : parent.Children;
            return children.FirstOrDefault(child => child.Name == childName || child.Id == childName);
        }
        var memberMatch = Regex.Match(expression, @"^(?<parent>.+)\.(?<member>[A-Za-z_][A-Za-z0-9_]*)$");
        if (!memberMatch.Success) return null;
        var instance = ResolveLuaXNode(memberMatch.Groups["parent"].Value) ?? ResolveLuaXReceiverNode(memberMatch.Groups["parent"].Value, editor);
        if (instance is null) return null;
        var member = memberMatch.Groups["member"].Value;
        if (member.Equals("Parent", StringComparison.OrdinalIgnoreCase)) return FindParentNode(instance);
        if (member.Equals("LocalPlayer", StringComparison.OrdinalIgnoreCase) && instance.Type == "Players")
            return new SceneNode { Type = "Player", Name = "LocalPlayer" };
        if (member.Equals("Subject", StringComparison.OrdinalIgnoreCase) && instance.Type == "Camera") return FindSceneNode(instance.Subject);
        if (member.Equals("Character", StringComparison.OrdinalIgnoreCase) && instance.Type == "Player") return FindSceneNode(instance.Character);
        if (member.Equals("Part0", StringComparison.OrdinalIgnoreCase) && instance.Type == "WeldConstraint") return FindSceneNode(instance.Part0);
        if (member.Equals("Part1", StringComparison.OrdinalIgnoreCase) && instance.Type == "WeldConstraint") return FindSceneNode(instance.Part1);
        if (member.Equals("Attachment0", StringComparison.OrdinalIgnoreCase) && instance.Type is ("Beam" or "Trail" or "VectorForce" or "LinearVelocity")) return FindSceneNode(instance.Attachment0);
        if (member.Equals("Attachment1", StringComparison.OrdinalIgnoreCase) && instance.Type is ("Beam" or "Trail")) return FindSceneNode(instance.Attachment1);
        return instance.Children.FirstOrDefault(child => child.Name == member || child.Id == member);
    }

    private static bool IsLuaXIdentifier(string value) =>
        Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$");

    private static string StripExpressionParentheses(string value)
    {
        value = value.Trim();
        while (value.Length >= 2 && value[0] == '(' && value[^1] == ')') value = value[1..^1].Trim();
        return value;
    }

    private SceneNode? ResolveLuaXNode(string expression)
    {
        if (project is null || string.IsNullOrWhiteSpace(expression)) return null;
        var segments = expression.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0) return null;
        SceneNode? node;
        var index = 1;
        if (segments[0] == "workspace") node = project.Workspace;
        else if (segments[0] == "Players") node = project.Players;
        else if (segments[0] == "Lighting") node = project.Lighting;
        else if (segments[0] == "ReplicatedStorage") node = project.ReplicatedStorage;
        else if (segments[0] == "ServerScriptService") node = project.ServerScriptService;
        else if (segments[0] == "ServerStorage") node = project.ServerStorage;
        else if (segments[0] == "Gui") node = project.Gui;
        else if (segments[0] == "script") node = selectedScript is null ? null : FindSceneNode(selectedScript.Id);
        else if (segments[0] == "game")
        {
            if (segments.Length == 1) return null;
            node = segments[1] switch { "Workspace" => project.Workspace, "Players" => project.Players, "Lighting" => project.Lighting, "ReplicatedStorage" => project.ReplicatedStorage, "ServerScriptService" => project.ServerScriptService, "ServerStorage" => project.ServerStorage, "Gui" => project.Gui, _ => null };
            index = 2;
        }
        else
        {
            var matchingNodes = GameProjectStore.Flatten(project).Where(candidate => candidate.Name == segments[0]).Take(2).ToArray();
            if (matchingNodes.Length != 1) return null;
            node = matchingNodes[0];
        }
        for (; node is not null && index < segments.Length; index++) node = node.Children.FirstOrDefault(child => child.Name == segments[index]);
        return node;
    }

    private static readonly string[] LuaXGlobalCompletions =
    [
        "and", "break", "continue", "do", "else", "elseif", "end", "false", "for", "function", "if", "in", "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
        "assert", "collectgarbage", "error", "getmetatable", "ipairs", "next", "pairs", "pcall", "print", "require", "warn", "rawequal", "rawget", "rawlen", "rawset", "select", "setmetatable", "tonumber", "tostring", "type", "typeof", "unpack", "xpcall",
        "math", "string", "table", "coroutine", "utf8", "task", "wait", "game", "workspace", "script", "Gui", "Players", "Lighting", "ReplicatedStorage", "ServerScriptService", "ServerStorage", "Instance", "Enum", "Vector3", "Vector2", "NumberRange", "NumberSequence", "NumberSequenceKeypoint", "ColorSequence", "ColorSequenceKeypoint", "Color3", "UDim", "UDim2", "RaycastParams",
        "SetProperty", "GetProperty", "WaitForChild", "GetAssetPath", "ConnectTouched", "LookAtRotation"
    ];

    private static readonly string[] LuaXMemberCompletions =
    [
        "Name", "Parent", "Archivable", "Value", "Changed", "Event", "Fire", "OnInvoke", "Invoke", "Connected", "Disconnect", "Position", "Rotation", "Size", "Color", "FillColor", "OutlineColor", "FillTransparency", "OutlineTransparency", "Shape", "Face", "Material", "Transparency", "Texture", "Asset", "Volume", "Looped", "Playing", "RollOffMinDistance", "RollOffMaxDistance", "Play", "Stop", "Anchored", "CanCollide", "CanTouch", "CanQuery", "Velocity", "AssemblyLinearVelocity", "CastShadow", "Gravity", "AutoManageCharacter", "Raycast", "BlastRadius", "BlastPressure", "DestroyJointRadiusPercent", "Hit", "Ambient", "ClockTime", "GlobalShadows", "Character", "LocalPlayer", "Occupant", "Health", "MaxHealth", "WalkSpeed", "JumpPower", "Died", "TakeDamage", "Enabled", "Part0", "Part1", "Attachment0", "Attachment1", "Width0", "Width1", "Brightness", "Range", "Angle", "ActionText", "ObjectText", "MaxActivationDistance", "Triggered", "MouseClick", "CameraMode", "Subject", "FieldOfView", "Children", "ClassName", "Type", "Touched", "touched", "Activated", "MouseButton1Click", "TextChanged", "SkyboxFront", "SkyboxBack", "SkyboxLeft", "SkyboxRight", "SkyboxTop", "SkyboxBottom", "SkyboxFt", "SkyboxBk", "SkyboxLf", "SkyboxRt", "SkyboxUp", "SkyboxDn", "SetProperty", "GetProperty", "WaitForChild", "waitforchild", "FindFirstChild", "GetChildren", "IsA", "Clone", "Destroy", "SecondaryColor", "Heat", "RiseVelocity", "Opacity", "TimeScale", "SparkleColor", "SetAttribute", "GetAttribute", "GetAttributes", "GetAttributeChangedSignal", "AttributeChanged", "AddTag", "RemoveTag", "HasTag", "GetTags", "GetTagged", "GetAllTags", "GetInstanceAddedSignal", "GetInstanceRemovedSignal", "CollectionService"
    ];

    private static readonly string[] LuaXMethodCompletions = ["GetProperty", "SetProperty", "WaitForChild", "waitforchild", "FindFirstChild", "GetChildren", "IsA", "Clone", "Destroy", "Fire", "Invoke", "Disconnect", "TakeDamage", "AddItem", "SetAttribute", "GetAttribute", "GetAttributes", "GetAttributeChangedSignal", "AddTag", "RemoveTag", "HasTag", "GetTags", "GetTagged", "GetAllTags", "GetInstanceAddedSignal", "GetInstanceRemovedSignal"];
    private static readonly string[] LuaXCreatableClassNameCompletions = ["Part", "MeshPart", "WedgePart", "CornerWedgePart", "TrussPart", "SpawnLocation", "Seat", "Model", "Folder", "Configuration", "BindableEvent", "BindableFunction", "ModuleScript", "Humanoid", "BoolValue", "IntValue", "NumberValue", "StringValue", "Vector3Value", "Color3Value", "ObjectValue", "Sky", "Attachment", "Beam", "Trail", "VectorForce", "LinearVelocity", "PointLight", "SpotLight", "SurfaceLight", "Decal", "Texture", "Sound", "Fire", "Smoke", "Sparkles", "ParticleEmitter", "ProximityPrompt", "ClickDetector", "Highlight", "ForceField", "Explosion", "WeldConstraint", "ScreenGui", "Frame", "ScrollingFrame", "ImageLabel", "ImageButton", "TextLabel", "TextButton", "TextBox", "UICorner", "UIOutline", "UIStroke", "UIGradient", "UIPadding", "UIScale", "UIListLayout", "UIGridLayout", "UITableLayout", "UIAspectRatio"];
    private static readonly string[] LuaXClassNameCompletions = ["Instance", "DataModel", "Workspace", "Players", "Player", "Lighting", "Sky", "ReplicatedStorage", "ServerScriptService", "ServerStorage", "Camera", "BasePart", "Part", "WedgePart", "CornerWedgePart", "TrussPart", "SpawnLocation", "Seat", "Humanoid", "ValueBase", "BoolValue", "IntValue", "NumberValue", "StringValue", "Vector3Value", "Color3Value", "ObjectValue", "MeshPart", "Light", "PointLight", "SpotLight", "SurfaceLight", "FaceInstance", "Decal", "Texture", "Sound", "Fire", "Smoke", "Sparkles", "ParticleEmitter", "ProximityPrompt", "ClickDetector", "Highlight", "ForceField", "Explosion", "WeldConstraint", "Attachment", "Beam", "Trail", "VectorForce", "LinearVelocity", "Model", "Folder", "Configuration", "BindableEvent", "BindableFunction", "ModuleScript", "LuaSourceContainer", "BaseScript", "Script", "Gui", "GuiBase2d", "GuiObject", "UIComponent", "LayerCollector", "ScreenGui", "Frame", "ScrollingFrame", "ImageLabel", "ImageButton", "TextLabel", "TextButton", "TextBox", "UICorner", "UIOutline", "UIStroke", "UIGradient", "UIPadding", "UIScale", "UIListLayout", "UIGridLayout", "UITableLayout", "UIAspectRatio"];
    private static readonly IReadOnlyDictionary<string, string[]> LuaXStandardLibraryMembers = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["math"] = ["abs", "acos", "asin", "atan", "atan2", "ceil", "clamp", "cos", "deg", "exp", "floor", "fmod", "huge", "log", "max", "min", "modf", "pi", "rad", "random", "round", "sign", "sin", "sqrt", "tan"],
        ["string"] = ["byte", "char", "dump", "find", "format", "gmatch", "gsub", "len", "lower", "match", "rep", "reverse", "sub", "upper"],
        ["table"] = ["concat", "insert", "maxn", "remove", "sort", "unpack"],
        ["coroutine"] = ["create", "resume", "running", "status", "wrap", "yield"],
        ["utf8"] = ["char", "codes", "codepoint", "len", "offset"],
        ["task"] = ["wait", "spawn", "defer", "delay", "cancel"]
    };

    private static IHighlightingDefinition CreateLuaXHighlighting()
    {
        var longBracketCommentSpans = string.Concat(Enumerable.Range(0, 65).Select(level =>
        {
            var equals = new string('=', level);
            return $"<Span color=\"Comment\" begin=\"--\\[{equals}\\[\" end=\"\\]{equals}\\]\" multiline=\"true\"><RuleSet><Rule color=\"TODO\">(?i)\\b(?:TODO|FIXME|HACK|NOTE)\\b</Rule></RuleSet></Span>\n";
        }));
        var longBracketStringSpans = string.Concat(Enumerable.Range(0, 65).Select(level =>
        {
            var equals = new string('=', level);
            return $"<Span color=\"String\" begin=\"\\[{equals}\\[\" end=\"\\]{equals}\\]\" multiline=\"true\" />\n";
        }));
        var definition = """
            <SyntaxDefinition name="Lua X" extensions=".lua" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="#6A9955" />
              <Color name="String" foreground="#CE9178" />
              <Color name="Number" foreground="#B5CEA8" />
              <Color name="Keyword" foreground="#C586C0" fontWeight="bold" />
              <Color name="Local" foreground="#C586C0" fontWeight="bold" />
              <Color name="Function" foreground="#C586C0" fontWeight="bold" />
              <Color name="Nil" foreground="#569CD6" />
              <Color name="Bool" foreground="#569CD6" />
              <Color name="Self" foreground="#9CDCFE" />
              <Color name="LuauKeyword" foreground="#C586C0" fontWeight="bold" />
              <Color name="Builtin" foreground="#4EC9B0" />
              <Color name="Method" foreground="#DCDCAA" />
              <Color name="Type" foreground="#4EC9B0" />
              <Color name="FunctionName" foreground="#DCDCAA" />
              <Color name="Property" foreground="#9CDCFE" />
              <Color name="Operator" foreground="#D4D4D4" />
              <Color name="Bracket" foreground="#D4D4D4" />
              <Color name="TODO" foreground="#DCDCAA" fontWeight="bold" />
              <RuleSet>
                <!--LONG_BRACKET_COMMENT_SPANS-->
                <Span color="Comment" begin="--">
                  <RuleSet><Rule color="TODO">(?i)\b(?:TODO|FIXME|HACK|NOTE)\b</Rule></RuleSet>
                </Span>
                <!--LONG_BRACKET_STRING_SPANS-->
                <Span color="String" begin="`" end="`" />
                <Span color="String" begin="&quot;" end="(?&lt;!\\)(?:\\\\)*&quot;" />
                <Span color="String" begin="'" end="(?&lt;!\\)(?:\\\\)*'" />
                <Keywords color="Keyword">
                  <Word>and</Word><Word>break</Word><Word>do</Word><Word>else</Word><Word>elseif</Word><Word>end</Word><Word>for</Word><Word>if</Word><Word>in</Word><Word>not</Word><Word>or</Word><Word>repeat</Word><Word>return</Word><Word>then</Word><Word>until</Word><Word>while</Word>
                </Keywords>
                <Keywords color="Local"><Word>local</Word></Keywords>
                <Keywords color="Function"><Word>function</Word></Keywords>
                <Keywords color="Nil"><Word>nil</Word></Keywords>
                <Keywords color="Bool"><Word>false</Word><Word>true</Word></Keywords>
                <Keywords color="Self"><Word>self</Word></Keywords>
                <Keywords color="LuauKeyword"><Word>continue</Word><Word>export</Word><Word>type</Word><Word>typeof</Word></Keywords>
                <Keywords color="Builtin">
                  <Word>assert</Word><Word>collectgarbage</Word><Word>error</Word><Word>getmetatable</Word><Word>ipairs</Word><Word>next</Word><Word>pairs</Word><Word>pcall</Word><Word>print</Word><Word>require</Word><Word>warn</Word><Word>rawequal</Word><Word>rawget</Word><Word>rawlen</Word><Word>rawset</Word><Word>select</Word><Word>setmetatable</Word><Word>tonumber</Word><Word>tostring</Word><Word>type</Word><Word>unpack</Word><Word>xpcall</Word>
                  <Word>math</Word><Word>string</Word><Word>table</Word><Word>coroutine</Word><Word>utf8</Word><Word>task</Word><Word>wait</Word>
                  <Word>workspace</Word><Word>game</Word><Word>script</Word><Word>Gui</Word><Word>Players</Word><Word>Lighting</Word><Word>ReplicatedStorage</Word><Word>ServerScriptService</Word><Word>ServerStorage</Word><Word>LocalPlayer</Word><Word>Instance</Word><Word>Enum</Word><Word>SetProperty</Word><Word>GetProperty</Word><Word>WaitForChild</Word><Word>GetAssetPath</Word><Word>ConnectTouched</Word><Word>Vector3</Word><Word>LookAtRotation</Word><Word>Color3</Word><Word>GetService</Word>
                </Keywords>
                <Keywords color="Type"><Word>any</Word><Word>boolean</Word><Word>never</Word><Word>number</Word><Word>string</Word><Word>unknown</Word><Word>userdata</Word><Word>vector</Word><Word>Vector2</Word><Word>Vector3</Word><Word>NumberRange</Word><Word>NumberSequence</Word><Word>NumberSequenceKeypoint</Word><Word>ColorSequence</Word><Word>ColorSequenceKeypoint</Word><Word>Color3</Word><Word>UDim</Word><Word>UDim2</Word><Word>BrickColor</Word><Word>Instance</Word><Word>DataModel</Word><Word>BasePart</Word><Word>Part</Word><Word>MeshPart</Word><Word>Model</Word><Word>Workspace</Word><Word>Lighting</Word><Word>Sky</Word><Word>Camera</Word><Word>Gui</Word><Word>GuiBase2d</Word><Word>GuiObject</Word><Word>LayerCollector</Word><Word>Folder</Word><Word>Configuration</Word><Word>BindableEvent</Word><Word>BindableFunction</Word><Word>ModuleScript</Word><Word>LuaSourceContainer</Word><Word>BaseScript</Word><Word>Script</Word><Word>ValueBase</Word><Word>BoolValue</Word><Word>IntValue</Word><Word>NumberValue</Word><Word>StringValue</Word><Word>Vector3Value</Word><Word>Color3Value</Word><Word>ObjectValue</Word><Word>ForceField</Word><Word>Explosion</Word><Word>Fire</Word><Word>Smoke</Word><Word>Sparkles</Word><Word>ParticleEmitter</Word><Word>Trail</Word><Word>ScreenGui</Word><Word>Frame</Word><Word>ScrollingFrame</Word><Word>TextLabel</Word><Word>TextButton</Word><Word>TextBox</Word><Word>ImageLabel</Word><Word>ImageButton</Word><Word>UICorner</Word><Word>UIOutline</Word><Word>UIStroke</Word><Word>UIListLayout</Word><Word>UITableLayout</Word><Word>UIGradient</Word><Word>UIPadding</Word><Word>UIScale</Word><Word>UIGridLayout</Word><Word>UIAspectRatio</Word><Word>RBXScriptSignal</Word><Word>RBXScriptConnection</Word></Keywords>
                <Rule color="LuauKeyword">(?m)^\s*(?:export\s+)?type\b</Rule>
                <Rule color="Method">(?&lt;=[:.])[A-Za-z_][A-Za-z0-9_]*(?=\s*\()</Rule>
                <Rule color="FunctionName">(?&lt;=function\s+)[A-Za-z_][A-Za-z0-9_]*(?:[.:][A-Za-z_][A-Za-z0-9_]*)*</Rule>
                <Rule color="Property">(?&lt;=\.)[A-Za-z_][A-Za-z0-9_]*(?!\s*\()</Rule>
                <Rule color="Operator">(?:\.\.\.?|::|-&gt;|==|~=|&lt;=|&gt;=|[-+*/%^#=&lt;&gt;|&amp;?])</Rule>
                <Rule color="Bracket">(?:\(|\)|\[|\]|\{|\})</Rule>
                <Rule color="Number">\b0[bB][01][01_]*\b</Rule>
                <Rule color="Number">\b0[xX][0-9a-fA-F][0-9a-fA-F_]*\b</Rule>
                <Rule color="Number">\b\d[\d_]*(\.[\d_]*)?([eE][+-]?[\d_]+)?\b</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;
        definition = definition
            .Replace("<!--LONG_BRACKET_COMMENT_SPANS-->", longBracketCommentSpans, StringComparison.Ordinal)
            .Replace("<!--LONG_BRACKET_STRING_SPANS-->", longBracketStringSpans, StringComparison.Ordinal);
        using var reader = XmlReader.Create(new StringReader(definition));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private void HandleActiveDocumentChanged()
    {
        if (DockManager.ActiveContent is not LayoutDocument document) return;
        var nextScript = project?.Scripts.FirstOrDefault(script => script.Id == document.ContentId);
        if (selectedScript?.Id != nextScript?.Id) CommitScriptEditor();
        selectedScript = nextScript;
        scriptEditor = nextScript is not null && scriptEditors.TryGetValue(nextScript.Id, out var editor) ? editor : null;
        if (nextScript is not null) SelectRibbonTab("SCRIPT");
        else if (activeRibbonTab == "SCRIPT") SelectRibbonTab("HOME");
    }

    private void RunScript(GameScript script)
    {
        OpenPlayer(script.Id);
    }

    private void RefreshGuiPreview()
    {
        if (project is null || GuiPreviewCanvas is null) return;
        var rendered = GuiRenderService.Render(project, true, GuiPreviewCanvas.ActualWidth, GuiPreviewCanvas.ActualHeight, selected?.Id);
        var children = rendered.Children.Cast<UIElement>().ToArray();
        rendered.Children.Clear();
        GuiPreviewCanvas.Children.Clear();
        foreach (var child in children) GuiPreviewCanvas.Children.Add(child);
    }

    private void GuiPreviewCanvas_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var node = FindGuiPreviewNode(e.OriginalSource as DependencyObject);
        if (node is null) return;
        if (propertyPickerTargetId is not null)
        {
            CompleteObjectReferencePick(node);
            e.Handled = true;
            return;
        }

        selected = node;
        renderer?.SetSelection(selected.Id);
        SelectTreeNode(node.Id);
        RenderProperties();
        RefreshGuiPreview();
        if (selectedTool is "Move" or "Scale" or "Rotate" && node.Type != "ScreenGui")
        {
            var parent = FindParentNode(node);
            var parentUsesLayout = parent?.Children.Any(child => child.Type is "UIListLayout" or "UIGridLayout" or "UITableLayout") == true;
            if (selectedTool != "Move" || !parentUsesLayout)
            {
                isGuiTransformDragging = true;
                guiTransformChanged = false;
                lastGuiPointer = e.GetPosition(GuiPreviewCanvas);
                GuiPreviewCanvas.Focus();
                GuiPreviewCanvas.CaptureMouse();
            }
            else SetStatus("This element's position is controlled by its layout. Reorder it in Explorer.");
        }
        e.Handled = true;
    }

    private void GuiPreviewCanvas_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!isGuiTransformDragging || e.LeftButton != MouseButtonState.Pressed || selected is null || !IsGuiNode(selected)) return;
        var pointer = e.GetPosition(GuiPreviewCanvas);
        var deltaX = pointer.X - lastGuiPointer.X;
        var deltaY = pointer.Y - lastGuiPointer.Y;
        lastGuiPointer = pointer;
        if (selectedTool == "Move")
        {
            selected.Position[0] += (float)deltaX;
            selected.Position[1] += (float)deltaY;
        }
        if (selectedTool == "Scale")
        {
            selected.Size[0] = Math.Max(.05f, selected.Size[0] + (float)deltaX);
            selected.Size[1] = Math.Max(.05f, selected.Size[1] + (float)deltaY);
        }
        if (selectedTool == "Rotate") selected.Rotation[2] += (float)deltaX * .5f;
        guiTransformChanged = true;
        RefreshGuiPreview();
        e.Handled = true;
    }

    private void GuiPreviewCanvas_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!isGuiTransformDragging) return;
        StopGuiTransform();
        e.Handled = true;
    }

    private void StopGuiTransform()
    {
        if (!isGuiTransformDragging) return;
        isGuiTransformDragging = false;
        GuiPreviewCanvas.ReleaseMouseCapture();
        if (!guiTransformChanged) return;
        guiTransformChanged = false;
        RecordEdit();
        RenderProperties();
        RefreshGuiPreview();
    }

    private static SceneNode? FindGuiPreviewNode(DependencyObject? target)
    {
        while (target is not null)
        {
            if (target is FrameworkElement { Tag: SceneNode node }) return node;
            DependencyObject? parent;
            try { parent = VisualTreeHelper.GetParent(target); }
            catch (InvalidOperationException) { parent = LogicalTreeHelper.GetParent(target); }
            target = parent ?? LogicalTreeHelper.GetParent(target);
        }
        return null;
    }

    private void AssetsTab_Click(object sender, RoutedEventArgs e) => ShowAssets();
    private void OutputTab_Click(object sender, RoutedEventArgs e)
    {
        SetOutputPanelVisible(true);
        EnsureOutputBox();
        BottomContent.Content = outputBox;
        SelectBottomTab(showOutput: true);
    }

    private void EnsureOutputBox()
    {
        outputBox ??= new TextBox { IsReadOnly = true, AcceptsReturn = true, Text = "Build and Lua X messages appear here.", Background = Brushes.Transparent, Foreground = (Brush)FindResource("TextPrimary"), FontFamily = new FontFamily("Consolas") };
    }

    private void SelectBottomTab(bool showOutput)
    {
        OutputTab.Background = showOutput ? (Brush)FindResource("PanelRaised") : Brushes.Transparent;
        AssetsTab.Background = showOutput ? Brushes.Transparent : (Brush)FindResource("PanelRaised");
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
            StudioDialog.Show(this, "Build complete", $"Game built successfully:\n{executable}", StudioDialogKind.Success);
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void Included_Click(object sender, RoutedEventArgs e) { included = true; IncludedButton.Background = (Brush)FindResource("Accent"); ExcludedButton.Background = (Brush)FindResource("PanelRaised"); SetStatus("Build mode: Included assets"); }
    private void Excluded_Click(object sender, RoutedEventArgs e) { included = false; ExcludedButton.Background = (Brush)FindResource("Accent"); IncludedButton.Background = (Brush)FindResource("PanelRaised"); SetStatus("Build mode: Excluded assets"); }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void UpdateWindowStateButton()
    {
        MaximizeWindowButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeWindowButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private async void ShowProjects_Click(object sender, RoutedEventArgs e)
    {
        if (project is not null)
        {
            try { CommitScriptEditor(); await GameProjectStore.SaveAsync(project); }
            catch (Exception exception) { ShowError(exception); return; }
        }
        var home = new HomeWindow();
        Application.Current.MainWindow = home;
        home.Show();
        Close();
    }

    private void FileMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var save = new MenuItem { Header = "Save project     Ctrl+S" };
        save.Click += Save_Click;
        var home = new MenuItem { Header = "Home" };
        home.Click += ShowProjects_Click;
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Close();
        menu.Items.Add(save); menu.Items.Add(home); menu.Items.Add(new Separator()); menu.Items.Add(exit);
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private void RibbonTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tab }) SelectRibbonTab(tab);
    }

    private void SelectRibbonTab(string tab)
    {
        activeRibbonTab = tab;
        foreach (var child in RibbonTabBar.Children.OfType<Button>())
        {
            var isSelected = Equals(child.Tag, tab);
            child.Background = isSelected ? new SolidColorBrush(Color.FromRgb(48, 49, 52)) : Brushes.Transparent;
            child.BorderBrush = isSelected ? (Brush)FindResource("Accent") : Brushes.Transparent;
            child.BorderThickness = new Thickness(0, 0, 0, isSelected ? 2 : 0);
            child.Foreground = isSelected ? Brushes.White : (Brush)FindResource("TextMuted");
        }
        ApplyRibbonTab(tab);
    }

    private void ApplyRibbonTab(string tab)
    {
        foreach (var group in RibbonTools.Children.OfType<FrameworkElement>())
            group.Visibility = group.Tag is string tabs && tabs.Split(',').Contains(tab, StringComparer.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AnchorSelection_Click(object sender, RoutedEventArgs e)
    {
        if (selected is null) return;
        var parts = selected.Type == "Model"
            ? GameProjectStore.Flatten(selected.Children).Where(SceneNodeTypes.IsBasePart).ToArray()
            : SceneNodeTypes.IsBasePart(selected) ? [selected] : [];
        if (parts.Length == 0) return;
        var anchor = !parts.All(part => part.Anchored);
        foreach (var part in parts) part.Anchored = anchor;
        RecordEdit(); RenderProperties();
    }

    private void InsertModel_Click(object sender, RoutedEventArgs e)
    {
        if (project is null || sender is not Button button) return;
        var parent = selected is not null && !IsGuiNode(selected)
            && (SceneNodeTypes.IsBasePart(selected) || selected.Type is "Workspace" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Model")
            ? selected : project.Workspace;
        ShowInsertMenu(parent, button);
    }

    private void CameraCustom_Click(object sender, RoutedEventArgs e)
    {
        if (project is null) return;
        var camera = project.Workspace.Children.First(node => node.Type == "Camera");
        camera.CameraMode = "Custom";
        renderer?.UpdateCamera(project);
        RenderProperties();
        RecordEdit();
        SetStatus("Camera mode: Custom");
    }

    private void FocusSelection_Click(object sender, RoutedEventArgs e)
    {
        if (project is null) return;
        if (selected is null || !SceneNodeTypes.IsBasePart(selected))
        {
            SetStatus("Select a BasePart to use Subject camera mode.");
            return;
        }
        var camera = project.Workspace.Children.First(node => node.Type == "Camera");
        camera.CameraMode = "Subject"; camera.Subject = selected.Id;
        renderer?.UpdateCamera(project); RenderProperties(); RecordEdit();
        SetStatus($"Camera subject: {selected.Name}");
    }

    private void ToggleShadows_Click(object sender, RoutedEventArgs e)
    {
        if (project is null) return;
        project.Settings.Shadows = !project.Settings.Shadows;
        renderer?.Refresh(project); RenderProperties(); SetStatus($"Shadows {(project.Settings.Shadows ? "enabled" : "disabled")}"); RecordEdit();
    }

    private void ViewportOptions_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Visualization options", IsEnabled = false });
        menu.Items.Add(new Separator());
        var shadows = new MenuItem { Header = "Shadows", IsCheckable = true, IsChecked = project?.Settings.Shadows == true };
        shadows.Click += ToggleShadows_Click;
        var focus = new MenuItem { Header = "Focus selection" };
        focus.Click += FocusSelection_Click;
        var reset = new MenuItem { Header = "Reset camera" };
        reset.Click += ResetCamera_Click;
        menu.Items.Add(shadows);
        menu.Items.Add(new Separator());
        menu.Items.Add(focus);
        menu.Items.Add(reset);
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private void ResetCamera_Click(object sender, RoutedEventArgs e)
    {
        if (project is null) return;
        var camera = project.Workspace.Children.First(node => node.Type == "Camera");
        camera.CameraMode = "Custom"; camera.Position = [8, 7, 11]; camera.Rotation = [-24, 35, 0];
        renderer?.UpdateCamera(project); RenderProperties(); RecordEdit();
        SetStatus("Camera reset");
    }

    private void RunSelectedScript_Click(object sender, RoutedEventArgs e)
    {
        if (project is null) return;
        if (selected is { Type: "ModuleScript" })
        {
            SetStatus("ModuleScripts run when another script calls require(module).");
            return;
        }
        var script = selected is { Type: "Script" } ? project.Scripts.FirstOrDefault(item => item.Id == selected.Id) : selectedScript ?? project.Scripts.FirstOrDefault();
        if (script is null) { SetStatus("Add a Script object before running Lua X."); return; }
        if (GameProjectStore.Flatten(project).Any(node => node.Id == script.Id && node.Type == "ModuleScript"))
        {
            SetStatus("ModuleScripts run when another script calls require(module).");
            return;
        }
        RunScript(script);
    }

    private void ToolSelect_Click(object sender, RoutedEventArgs e) => SetTool("Select");
    private void ToolMove_Click(object sender, RoutedEventArgs e) => SetTool("Move");
    private void ToolScale_Click(object sender, RoutedEventArgs e) => SetTool("Scale");
    private void ToolRotate_Click(object sender, RoutedEventArgs e) => SetTool("Rotate");

    private void SetTool(string tool)
    {
        selectedTool = tool;
        SelectToolButton.Background = Brushes.Transparent;
        MoveToolButton.Background = Brushes.Transparent;
        ScaleToolButton.Background = Brushes.Transparent;
        RotateToolButton.Background = Brushes.Transparent;
        var active = tool switch { "Move" => MoveToolButton, "Scale" => ScaleToolButton, "Rotate" => RotateToolButton, _ => SelectToolButton };
        active.Background = new SolidColorBrush(Color.FromRgb(0, 103, 168));
        SetStatus($"{tool} tool selected. Choose an object in the Explorer or viewport.");
    }

    private void Explorer_Click(object sender, RoutedEventArgs e) => SetExplorerPanelVisible(!ExplorerDock.IsVisible);
    private void Properties_Click(object sender, RoutedEventArgs e)
    {
        SetPropertiesPanelVisible(!PropertiesDock.IsVisible);
        if (PropertiesDock.IsVisible) PropertyEditor.Focus();
    }

    private void WindowMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateWindowMenuItem("Explorer", ExplorerDock.IsVisible, SetExplorerPanelVisible));
        menu.Items.Add(CreateWindowMenuItem("Properties", PropertiesDock.IsVisible, SetPropertiesPanelVisible));
        menu.Items.Add(CreateWindowMenuItem("Output", OutputDock.IsVisible, SetOutputPanelVisible));
        var assets = new MenuItem { Header = "Asset Manager" };
        assets.Click += AssetsTab_Click;
        menu.Items.Add(assets);
        menu.Items.Add(new Separator());
        var shadows = new MenuItem { Header = "Shadows", IsCheckable = true, IsChecked = project?.Settings.Shadows == true };
        shadows.Click += ToggleShadows_Click;
        menu.Items.Add(shadows);
        var resetCamera = new MenuItem { Header = "Reset Camera" };
        resetCamera.Click += ResetCamera_Click;
        menu.Items.Add(resetCamera);
        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Reset Layout" };
        reset.Click += (_, _) => ResetDockLayout();
        menu.Items.Add(reset);
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private MenuItem CreateWindowMenuItem(string title, bool isVisible, Action<bool> setVisibility)
    {
        var item = new MenuItem { Header = title, IsCheckable = true, IsChecked = isVisible };
        item.Click += (_, _) => setVisibility(item.IsChecked);
        return item;
    }

    private void SetExplorerPanelVisible(bool isVisible) => ExplorerDock.IsVisible = isVisible;

    private void SetPropertiesPanelVisible(bool isVisible) => PropertiesDock.IsVisible = isVisible;

    private void SetOutputPanelVisible(bool isVisible) => OutputDock.IsVisible = isVisible;

    private void ResetDockLayout()
    {
        ResetDockLayoutStructure();
        ExplorerDock.IsVisible = true;
        PropertiesDock.IsVisible = true;
        OutputDock.IsVisible = true;
    }

    private void ResetDockLayoutStructure()
    {
        var documents = DockManager.Layout.Descendents().OfType<LayoutDocument>().ToArray();
        foreach (var dock in new[] { ExplorerDock, PropertiesDock, OutputDock })
            if (dock.Parent is LayoutAnchorablePane pane) pane.Children.Remove(dock);
        foreach (var document in documents)
            if (document.Parent is LayoutDocumentPane pane) pane.Children.Remove(document);

        var explorerPane = new LayoutAnchorablePane { DockHeight = new GridLength(1.12, GridUnitType.Star) };
        explorerPane.Children.Add(ExplorerDock);
        var propertiesPane = new LayoutAnchorablePane { DockHeight = new GridLength(.88, GridUnitType.Star) };
        propertiesPane.Children.Add(PropertiesDock);
        var rightPaneGroup = new LayoutAnchorablePaneGroup { Orientation = Orientation.Vertical, DockWidth = new GridLength(310) };
        rightPaneGroup.Children.Add(explorerPane);
        rightPaneGroup.Children.Add(propertiesPane);

        var documentPane = new LayoutDocumentPane();
        foreach (var document in documents) documentPane.Children.Add(document);
        var documentPaneGroup = new LayoutDocumentPaneGroup();
        documentPaneGroup.Children.Add(documentPane);
        var outputPane = new LayoutAnchorablePane { DockHeight = new GridLength(164) };
        outputPane.Children.Add(OutputDock);
        var centerPaneGroup = new LayoutPanel { Orientation = Orientation.Vertical };
        centerPaneGroup.Children.Add(documentPaneGroup);
        centerPaneGroup.Children.Add(outputPane);

        var rootPanel = new LayoutPanel { Orientation = Orientation.Horizontal };
        rootPanel.Children.Add(centerPaneGroup);
        rootPanel.Children.Add(rightPaneGroup);
        DockManager.Layout.RootPanel = rootPanel;
        DockManager.ActiveContent = ViewportDocument;
    }

    private void Play_Click(object sender, RoutedEventArgs e) => OpenPlayer();

    private void OpenPlayer(string? scriptId = null)
    {
        if (project is null) return;
        if (playerWindow is { IsVisible: true })
        {
            if (scriptId is null) { playerWindow.Activate(); return; }
            playerWindow.Close();
        }
        CommitScriptEditor();
        var playProject = JsonSerializer.Deserialize<GameProject>(JsonSerializer.Serialize(project, GameProjectStore.JsonOptions), GameProjectStore.JsonOptions);
        if (playProject is null) return;
        playProject.ProjectPath = project.ProjectPath;
        playProject.BuildIncluded = project.BuildIncluded;
        GameProjectStore.EnsureProject(playProject);
        var player = new PlayerWindow(playProject, scriptId, scriptId is null ? null : message => AppendOutput(message));
        playerWindow = player;
        player.Closed += (_, _) => { if (ReferenceEquals(playerWindow, player)) playerWindow = null; };
        player.Show();
    }

    private void StopPlayer_Click(object sender, RoutedEventArgs e) => playerWindow?.Close();
    private void Help_Click(object sender, RoutedEventArgs e) => StudioDialog.Show(this, "Buller Studio Help", "Buller Studio saves projects locally. Game scripts run with Lua X: use SetProperty(id, 'Position', {X=0,Y=2,Z=0}), WaitForChild(workspace, 'Part'), and part.Touched:Connect(function(other) ... end). Camera Mode is Custom or Subject.");
    private void Undo_Click(object sender, RoutedEventArgs e) => UndoEdit();
    private void Redo_Click(object sender, RoutedEventArgs e) => RedoEdit();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var textInputHasFocus = scriptEditor?.IsKeyboardFocusWithin == true || Keyboard.FocusedElement is TextBox;
        if (e.Key == Key.Escape && propertyPickerTargetId is not null)
        {
            CancelObjectReferencePick();
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key == Key.Space && scriptEditor?.IsKeyboardFocusWithin == true) { ShowLuaXCompletions(scriptEditor, string.Empty); e.Handled = true; }
            else if (e.Key == Key.S) { _ = SaveProjectAsync(); e.Handled = true; }
            else if (e.Key == Key.D && !textInputHasFocus) { DuplicateSelected(); e.Handled = true; }
            else if (e.Key == Key.G && !textInputHasFocus && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { UngroupSelected(); e.Handled = true; }
            else if (e.Key == Key.G && !textInputHasFocus) { GroupSelected(); e.Handled = true; }
            else if (e.Key == Key.Z && textInputHasFocus) return;
            else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { RedoEdit(); e.Handled = true; }
            else if (e.Key == Key.Z) { UndoEdit(); e.Handled = true; }
            return;
        }
        if (e.Key == Key.F2 && !textInputHasFocus && Keyboard.FocusedElement is not ComboBox)
        {
            RenameSelected(); e.Handled = true; return;
        }
        if (e.Key == Key.Delete && !textInputHasFocus && Keyboard.FocusedElement is not ComboBox)
        {
            DeleteSelected(); e.Handled = true; return;
        }
        if (isRightDragging && project is not null && renderer is not null && (e.Key is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E))
        {
            e.Handled = true;
        }
    }

    private void CameraMoveTimer_Tick(object? sender, EventArgs e)
    {
        if (!isRightDragging || renderer is null || project is null) return;
        var now = DateTime.UtcNow;
        var elapsed = Math.Clamp((now - lastCameraMoveAt).TotalSeconds, 0, .05);
        lastCameraMoveAt = now;
        var pointer = Mouse.GetPosition(SceneViewport);
        var deltaX = (float)(pointer.X - lastCameraPointer.X);
        var deltaY = (float)(pointer.Y - lastCameraPointer.Y);
        lastCameraPointer = pointer;
        var direction = new SharpDX.Vector3(
            (Keyboard.IsKeyDown(Key.D) ? 1 : 0) - (Keyboard.IsKeyDown(Key.A) ? 1 : 0),
            (Keyboard.IsKeyDown(Key.E) ? 1 : 0) - (Keyboard.IsKeyDown(Key.Q) ? 1 : 0),
            (Keyboard.IsKeyDown(Key.W) ? 1 : 0) - (Keyboard.IsKeyDown(Key.S) ? 1 : 0));
        var (yawDelta, pitchDelta) = MapCameraPointerDelta(deltaX, deltaY);
        if (project.Workspace.Children.FirstOrDefault(node => node.Type == "Camera")?.CameraMode == "Subject")
        {
            if (yawDelta != 0 || pitchDelta != 0) renderer.SetCameraOrbit(yawDelta, pitchDelta, 0, project);
            return;
        }
        if (direction.LengthSquared() != 0 || yawDelta != 0 || pitchDelta != 0)
            renderer.ApplyCameraInput(direction, (float)(elapsed * 12), yawDelta, pitchDelta, project);
    }

    private void StopCameraMovement()
    {
        isRightDragging = false;
        cameraMoveTimer.Stop();
        if (Mouse.Captured == SceneViewport) Mouse.Capture(null);
    }

    private static (float Yaw, float Pitch) MapCameraPointerDelta(float deltaX, float deltaY) => (-deltaX * .25f, -deltaY * .25f);

    private void SceneViewport_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (propertyPickerTargetId is not null || selectedTool == "Select" || isRightDragging || selected is null || !(SceneNodeTypes.IsBasePart(selected) || selected.Type == "Model") || renderer is null) return;
        if (selected.Type == "Model" && selectedTool == "Scale") return;
        var hit = SceneViewport.FindHits(e.GetPosition(SceneViewport)).FirstOrDefault()?.ModelHit;
        if (hit is not MeshGeometryModel3D hitModel) return;
        IEnumerable<string> selectedVisuals = selected.Type == "Model"
            ? GameProjectStore.Flatten(selected.Children).Where(SceneNodeTypes.IsBasePart).Select(node => node.Id)
            : [selected.Id];
        if (!selectedVisuals.Any(id => renderer.Visuals.TryGetValue(id, out var visual) && ReferenceEquals(hitModel, visual))) return;
        isTransformDragging = true;
        transformChanged = false;
        lastPointer = e.GetPosition(SceneViewport);
        SceneViewport.CaptureMouse();
        e.Handled = true;
    }

    private void SceneViewport_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!isTransformDragging) return;
        isTransformDragging = false;
        SceneViewport.ReleaseMouseCapture();
        if (transformChanged)
        {
            RecordEdit();
            RenderProperties();
        }
        e.Handled = true;
    }

    private void SceneViewport_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) { isRightDragging = true; lastCameraPointer = e.GetPosition(SceneViewport); lastCameraMoveAt = DateTime.UtcNow; SceneViewport.Focus(); Mouse.Capture(SceneViewport); cameraMoveTimer.Start(); e.Handled = true; }
    private void SceneViewport_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e) { StopCameraMovement(); e.Handled = true; }
    private void SceneViewport_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (isTransformDragging && e.LeftButton == MouseButtonState.Pressed && selected is not null && project is not null)
        {
            var pointer = e.GetPosition(SceneViewport);
            var deltaX = (float)(pointer.X - lastPointer.X);
            var deltaY = (float)(pointer.Y - lastPointer.Y);
            lastPointer = pointer;
            if (selected.Type == "Model")
            {
                if (selectedTool == "Move")
                {
                    var pivot = SceneModelService.GetPivot(selected);
                    SceneModelService.MoveTo(selected, [pivot[0] + deltaX * .02f, pivot[1] - deltaY * .02f, pivot[2]]);
                }
                if (selectedTool == "Rotate")
                    SceneModelService.RotateTo(selected, [selected.Rotation[0] - deltaY * .5f, selected.Rotation[1] + deltaX * .5f, selected.Rotation[2]]);
                renderer?.Refresh(project);
                transformChanged = true;
                return;
            }
            if (selectedTool == "Move") { selected.Position[0] += deltaX * .02f; selected.Position[1] -= deltaY * .02f; }
            if (selectedTool == "Scale") { selected.Size[0] = Math.Max(.05f, selected.Size[0] + deltaX * .02f); selected.Size[1] = Math.Max(.05f, selected.Size[1] - deltaY * .02f); }
            if (selectedTool == "Rotate") { selected.Rotation[1] += deltaX * .5f; selected.Rotation[0] -= deltaY * .5f; }
            renderer?.UpdateNode(selected, project);
            transformChanged = true;
            return;
        }
        if (isRightDragging) e.Handled = true;
    }
    private void SceneViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e) { renderer?.SetCameraOrbit(0, 0, e.Delta > 0 ? -1 : 1, project); }
    private void SceneViewport_MouseDown3D(object sender, RoutedEventArgs e)
    {
        if (renderer is null || project is null || isRightDragging) return;
        var hit = SceneViewport.FindHits(Mouse.GetPosition(SceneViewport)).FirstOrDefault()?.ModelHit;
        if (hit is not MeshGeometryModel3D model) return;
        var id = renderer.Visuals.FirstOrDefault(entry => ReferenceEquals(entry.Value, model)).Key;
        if (string.IsNullOrEmpty(id)) return;
        var node = FindSceneNode(id);
        if (propertyPickerTargetId is not null)
        {
            CompleteObjectReferencePick(node);
            return;
        }
        selected = node;
        renderer.SetSelection(selected?.Id);
        SelectTreeNode(id);
        RenderProperties();
    }

    private void DeleteSelected()
    {
        if (project is null || selected is null || selected.Type is "Camera" or "Workspace" or "Players" or "Lighting" or "Sky" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui") return;
        if (!StudioDialog.Confirm(this, "Delete object", $"Delete {selected.Name}?")) return;
        var removedNodes = GameProjectStore.RemoveNodeAndGetSubtree(project.Workspace.Children, selected.Id)
            .Concat(GameProjectStore.RemoveNodeAndGetSubtree(project.Lighting.Children, selected.Id))
            .Concat(GameProjectStore.RemoveNodeAndGetSubtree(project.ReplicatedStorage.Children, selected.Id))
            .Concat(GameProjectStore.RemoveNodeAndGetSubtree(project.ServerScriptService.Children, selected.Id))
            .Concat(GameProjectStore.RemoveNodeAndGetSubtree(project.ServerStorage.Children, selected.Id))
            .Concat(GameProjectStore.RemoveNodeAndGetSubtree(project.Gui.Children, selected.Id))
            .ToArray();
        if (removedNodes.Length == 0) return;
        var removedIds = removedNodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var weld in GameProjectStore.Flatten(project).Where(node => node.Type == "WeldConstraint"))
        {
            if (removedIds.Contains(weld.Part0)) weld.Part0 = string.Empty;
            if (removedIds.Contains(weld.Part1)) weld.Part1 = string.Empty;
        }
        foreach (var beam in GameProjectStore.Flatten(project).Where(node => node.Type is "Beam" or "Trail" or "VectorForce" or "LinearVelocity"))
        {
            if (removedIds.Contains(beam.Attachment0)) beam.Attachment0 = string.Empty;
            if (removedIds.Contains(beam.Attachment1)) beam.Attachment1 = string.Empty;
        }
        foreach (var objectValue in GameProjectStore.Flatten(project).Where(node => node.Type == "ObjectValue" && removedIds.Contains(node.ObjectValueId)))
            objectValue.ObjectValueId = string.Empty;
        if (selectedScript is not null && removedIds.Contains(selectedScript.Id)) CommitScriptEditor();
        project.Scripts.RemoveAll(script => removedIds.Contains(script.Id));
        var scriptDocuments = DockManager.Layout.Descendents().OfType<LayoutDocument>()
            .Where(document => document.ContentId is { } contentId && removedIds.Contains(contentId))
            .ToArray();
        var activeDocumentWasRemoved = scriptDocuments.Any(document => ReferenceEquals(DockManager.ActiveContent, document));
        foreach (var scriptDocument in scriptDocuments)
        {
            if (scriptDocument.Parent is LayoutDocumentPane pane) pane.Children.Remove(scriptDocument);
            if (scriptDocument.ContentId is { } contentId) scriptEditors.Remove(contentId);
        }
        if (activeDocumentWasRemoved) DockManager.ActiveContent = ViewportDocument;
        selected = null;
        RecordEdit(); RefreshEditor();
    }

    private void DuplicateSelected_Click(object sender, RoutedEventArgs e) => DuplicateSelected();

    private void GroupSelected_Click(object sender, RoutedEventArgs e) => GroupSelected();
    private void UngroupSelected_Click(object sender, RoutedEventArgs e) => UngroupSelected();

    private bool CanGroupSelected() => project is not null && selected is not null
        && !IsGuiNode(selected) && selected.Type is not ("Workspace" or "Players" or "Lighting" or "Sky" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui" or "Camera" or "Decal" or "Texture" or "ProximityPrompt" or "ClickDetector" or "Highlight" or "WeldConstraint" or "Humanoid")
        && !SceneNodeTypes.IsLight(selected)
        && FindParentNode(selected) is not null;

    private void GroupSelected()
    {
        if (!CanGroupSelected() || project is null || selected is null) return;
        var source = selected;
        var parent = FindParentNode(source);
        if (parent is null) return;
        var index = parent.Children.IndexOf(source);
        if (index < 0) return;
        var model = new SceneNode { Type = "Model", Name = source.Name + " Model" };
        parent.Children.Insert(index, model);
        if (!GameProjectStore.MoveNodeToParent(project, source, model))
        {
            parent.Children.Remove(model);
            return;
        }
        selected = model;
        RecordEdit();
        RefreshEditor();
        SelectTreeNode(model.Id);
    }

    private void UngroupSelected()
    {
        if (project is null || selected?.Type != "Model" || IsGuiNode(selected)) return;
        var model = selected;
        var parent = FindParentNode(model);
        if (parent is null) return;
        var index = parent.Children.IndexOf(model);
        if (index < 0) return;
        parent.Children.RemoveAt(index);
        var children = model.Children.ToArray();
        model.Children.Clear();
        parent.Children.InsertRange(index, children);
        foreach (var objectValue in GameProjectStore.Flatten(project).Where(node => node.Type == "ObjectValue" && node.ObjectValueId == model.Id))
            objectValue.ObjectValueId = string.Empty;
        selected = children.FirstOrDefault() ?? parent;
        RecordEdit();
        RefreshEditor();
        SelectTreeNode(selected.Id);
    }

    private void RenameSelected()
    {
        if (selected is null || selected.Type is "Workspace" or "Players" or "Lighting" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui") return;
        var name = StudioDialog.Prompt(this, "Rename object", "Name", selected.Name, "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        var id = selected.Id;
        ApplyTextProperty(new TextBox { Tag = "Name", DataContext = id, Text = name });
        SelectTreeNode(id);
    }

    private void DuplicateSelected()
    {
        if (project is null || selected is null || selected.Type is "Camera" or "Workspace" or "Players" or "Lighting" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui") return;
        var parent = FindParentNode(selected);
        if (parent is null) return;
        var original = selected;
        var clone = GameProjectStore.CloneSubtree(original, out var newIds, respectArchivable: false);
        if (clone is null) return;
        clone.Name += " Copy";
        if (!IsGuiNode(original))
        {
            foreach (var part in new[] { clone }.Concat(GameProjectStore.Flatten(clone.Children)).Where(SceneNodeTypes.IsBasePart))
                part.Position[0] += 1;
        }
        else if (IsGuiNode(original))
        {
            clone.Position[0] += 20;
            clone.Position[1] += 20;
        }
        if (!GameProjectStore.CanParentNode(project, clone, parent)) return;
        foreach (var oldNode in new[] { original }.Concat(GameProjectStore.Flatten(original.Children)).Where(node => node.Type is "Script" or "ModuleScript"))
        {
            if (!newIds.TryGetValue(oldNode.Id, out var newId)) continue;
            var oldScript = project.Scripts.FirstOrDefault(script => script.Id == oldNode.Id);
            if (oldScript is null) continue;
            var newNode = new[] { clone }.Concat(GameProjectStore.Flatten(clone.Children)).First(node => node.Id == newId);
            project.Scripts.Add(new GameScript { Id = newId, Name = newNode.Name, Source = oldScript.Source });
        }
        var index = parent.Children.IndexOf(original);
        parent.Children.Insert(index + 1, clone);
        selected = clone;
        RecordEdit();
        RefreshEditor();
        SelectTreeNode(clone.Id);
        SetStatus($"Duplicated {original.Name}");
    }

    private void RecordEdit()
    {
        if (project is null || isRestoring) return;
        var nextSnapshot = JsonSerializer.Serialize(project, GameProjectStore.JsonOptions);
        if (nextSnapshot == currentSnapshot) return;
        if (!string.IsNullOrEmpty(currentSnapshot)) undo.Push(currentSnapshot);
        currentSnapshot = nextSnapshot;
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
        var attempts = 0;
        void Select()
        {
            ExplorerTree.UpdateLayout();
            var item = FindTreeItem(ExplorerTree, id);
            if (item is not null)
            {
                isSuppressingExplorerSelection = true;
                try
                {
                    item.IsSelected = true;
                    item.BringIntoView();
                }
                finally { isSuppressingExplorerSelection = false; }
                return;
            }
            if (attempts++ < 4) _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Select));
        }
        Select();
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
        for (var index = 0; index < 3; index++)
            if (!float.TryParse(parts[index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result[index]) || !float.IsFinite(result[index])) return null;
        return result;
    }
    private static float[]? ParsePair(string value)
    {
        var parts = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return null;
        var result = new float[2];
        for (var index = 0; index < 2; index++)
            if (!float.TryParse(parts[index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result[index]) || !float.IsFinite(result[index])) return null;
        return result;
    }
    private static float[]? ParseGuiUDim2(string value)
    {
        var parts = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is not (2 or 4)) return null;
        var result = new float[4];
        if (parts.Length == 2)
        {
            if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result[1])
                || !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result[3])
                || !float.IsFinite(result[1]) || !float.IsFinite(result[3])) return null;
            return result;
        }
        for (var index = 0; index < 4; index++)
            if (!float.TryParse(parts[index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result[index]) || !float.IsFinite(result[index])) return null;
        return result;
    }
    private static float[]? ParseGuiUDim(string value)
    {
        var parts = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is not (1 or 2)) return null;
        var result = new float[2];
        for (var index = 0; index < parts.Length; index++)
            if (!float.TryParse(parts[index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) || !float.IsFinite(number)) return null;
            else result[parts.Length == 1 ? 1 : index] = number;
        return result;
    }
    private static string VectorText(float[] value) => string.Join(", ", value.Select(number => number.ToString("G9", System.Globalization.CultureInfo.InvariantCulture)));
    private static string GuiUDimText(float[] value) => string.Join(", ", value.Select(number => number.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
    private static string GuiUDim2Text(float[] offset, float[] scale) => string.Join(", ", new[] { scale[0], offset[0], scale[1], offset[1] }.Select(number => number.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
    private void SetStatus(string message) => Dispatcher.Invoke(() => StatusText.Text = message);
    private void ShowError(Exception exception) => StudioDialog.Show(this, "Buller Studio", exception.Message, StudioDialogKind.Error);

}

internal sealed class LuaXCompletionData : ICompletionData
{
    private static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Vector3"] = "Vector3 — Three-dimensional position, rotation, size, and direction value.",
        ["Vector3.new"] = "new(x: number, y: number, z: number): Vector3 — Creates a vector from three components.",
        ["Vector3.one"] = "Vector3 — A vector with each component set to 1.",
        ["Vector3.zero"] = "Vector3 — A vector with each component set to 0.",
        ["Color3.new"] = "new(r: number, g: number, b: number): Color3 — Creates a color from normalized RGB components.",
        ["Color3.fromRGB"] = "fromRGB(r: number, g: number, b: number): Color3 — Creates a color from 0–255 RGB components.",
        ["Color3.fromHSV"] = "fromHSV(h: number, s: number, v: number): Color3 — Creates a color from hue, saturation, and value.",
        ["Color3.fromHex"] = "fromHex(hex: string): Color3 — Creates a color from a hexadecimal color string.",
        ["Color3"] = "Color3 — Color value with normalized red, green, and blue components.",
        ["UDim.new"] = "new(scale: number, offset: number): UDim — Creates a one-dimensional scale and pixel offset.",
        ["UDim2.new"] = "new(xScale: number, xOffset: number, yScale: number, yOffset: number): UDim2 — Creates a 2D scale and pixel offset.",
        ["UDim2.fromScale"] = "fromScale(xScale: number, yScale: number): UDim2 — Creates a size or position from scale values.",
        ["UDim2.fromOffset"] = "fromOffset(xOffset: number, yOffset: number): UDim2 — Creates a size or position from pixel offsets.",
        ["UDim2.zero"] = "UDim2 — A 2D value with zero scale and offset components.",
        ["UDim"] = "UDim — A 1D scale and pixel-offset value.",
        ["UDim2"] = "UDim2 — A 2D scale and pixel-offset value.",
        ["Dot"] = "Dot(other: Vector3): number — Returns the dot product of two vectors.",
        ["Cross"] = "Cross(other: Vector3): Vector3 — Returns the cross product of two vectors.",
        ["Magnitude"] = "number — The vector's length.",
        ["Unit"] = "Vector3 — The vector normalized to unit length.",
        ["ToHex"] = "ToHex(): string — Converts this Color3 value to a hexadecimal color string.",
        ["ToHSV"] = "ToHSV(): (number, number, number) — Returns hue, saturation, and value components.",
        ["math.clamp"] = "clamp(value: number, minimum: number, maximum: number): number — Limits a number to an inclusive range.",
        ["math.atan2"] = "atan2(y: number, x: number): number — Returns the two-argument arctangent in radians.",
        ["math.round"] = "round(value: number): number — Rounds to the nearest integer.",
        ["math.sign"] = "sign(value: number): number — Returns -1, 0, or 1 for the value's sign.",
        ["string.format"] = "format(format: string, ...: any): string — Formats values into a string.",
        ["table.insert"] = "insert(list: table, value: any): () — Appends a value to a list.",
        ["coroutine.yield"] = "yield(...: any): (...any) — Suspends the current Lua X script until the runtime resumes it.",
        ["GetService"] = "GetService(name: string): Instance? — Returns a built-in service such as Workspace, Players, Debris, CollectionService, or RunService.",
        ["IsRunning"] = "IsRunning(): boolean — Returns true while the play runtime is active.",
        ["IsStudio"] = "IsStudio(): boolean — Returns true for an editor playtest and false for a packaged game.",
        ["IsEdit"] = "IsEdit(): boolean — Returns false in the play runtime.",
        ["PreSimulation"] = "RBXScriptSignal — Fires with delta time before physics each frame.",
        ["PostSimulation"] = "RBXScriptSignal — Fires with delta time after physics each frame.",
        ["Heartbeat"] = "RBXScriptSignal — Fires with delta time near the end of each frame.",
        ["PreRender"] = "RBXScriptSignal — Fires with delta time before the play view refreshes.",
        ["RenderStepped"] = "RBXScriptSignal — Legacy pre-render frame signal.",
        ["Stepped"] = "RBXScriptSignal — Legacy pre-simulation signal with elapsed play time and delta time.",
        ["AddTag"] = "AddTag(tag: string): () on an Instance, or AddTag(instance: Instance, tag: string): () on CollectionService.",
        ["RemoveTag"] = "RemoveTag(tag: string): () on an Instance, or RemoveTag(instance: Instance, tag: string): () on CollectionService.",
        ["HasTag"] = "HasTag(tag: string): boolean on an Instance, or HasTag(instance: Instance, tag: string): boolean on CollectionService.",
        ["GetTags"] = "GetTags(): {string} on an Instance, or GetTags(instance: Instance): {string} on CollectionService.",
        ["GetTagged"] = "GetTagged(tag: string): {Instance} — Returns tagged instances currently in the game tree.",
        ["GetAllTags"] = "GetAllTags(): {string} — Returns tags used by instances in the game tree.",
        ["GetInstanceAddedSignal"] = "GetInstanceAddedSignal(tag: string): RBXScriptSignal — Fires when a tag is added or a tagged instance enters the game tree.",
        ["GetInstanceRemovedSignal"] = "GetInstanceRemovedSignal(tag: string): RBXScriptSignal — Fires when a tag is removed or a tagged instance leaves the game tree.",
        ["AddItem"] = "AddItem(item: Instance, lifetime?: number) — Schedules an object for destruction after a lifetime (10 seconds by default).",
        ["SetAttribute"] = "SetAttribute(name: string, value: string | boolean | number | Vector3 | Color3 | nil) — Stores or deletes custom instance data.",
        ["GetAttribute"] = "GetAttribute(name: string): any — Returns a custom value, or nil when absent.",
        ["GetAttributes"] = "GetAttributes(): table — Returns a copy of the instance's custom values.",
        ["GetAttributeChangedSignal"] = "GetAttributeChangedSignal(name: string): RBXScriptSignal — Fires when one named attribute changes.",
        ["AttributeChanged"] = "RBXScriptSignal — Fires with the name of any changed attribute.",
        ["Instance.new"] = "Instance.new(className: string, parent: Instance?): Instance? — Creates a supported object during play.",
        ["Raycast"] = "Raycast(origin: Vector3, direction: Vector3, params: RaycastParams?): RaycastResult? — Casts through Workspace parts; direction sets the maximum range.",
        ["RaycastParams"] = "RaycastParams.new(): RaycastParams — FilterDescendantsInstances and FilterType (Enum.RaycastFilterType.Exclude or Include) control hits.",
        ["TakeDamage"] = "TakeDamage(amount: number): () — Changes Humanoid Health unless protected by a ForceField. A negative amount heals.",
        ["ForceField"] = "ForceField — Protects a Humanoid from TakeDamage when placed in its Model; Visible controls the shield effect.",
        ["Explosion"] = "Explosion — Detonates once in Workspace, applying outward velocity and hitting parts within BlastRadius.",
        ["Smoke"] = "Smoke — Emits a colored smoke effect from a BasePart or Attachment.",
        ["Sparkles"] = "Sparkles — Emits bright glints from a BasePart or Attachment.",
        ["ParticleEmitter"] = "ParticleEmitter — Emits adjustable particles from a BasePart or Attachment.",
        ["Trail"] = "Trail — Draws a fading ribbon between two moving Attachments.",
        ["VectorForce"] = "VectorForce — Applies a world or attachment-relative force to a part during play.",
        ["LinearVelocity"] = "LinearVelocity — Maintains a target velocity along a vector, line, or plane during play.",
        ["Rate"] = "number — Automatically emitted particles per second, up to 200.",
        ["Speed"] = "NumberRange — Emission speed range in studs per second.",
        ["Lifetime"] = "NumberRange — Particle lifetime range in seconds.",
        ["SpreadAngle"] = "Vector2 — Horizontal and vertical emission spread in degrees.",
        ["EmissionDirection"] = "Enum.NormalId — Face from which particles emit.",
        ["Emit"] = "Emit(count: number): () — Emits a burst of particles, including while disabled.",
        ["Clear"] = "Clear(): () — Removes active emitted particles.",
        ["SparkleColor"] = "Color3 — Color of the particles emitted by Sparkles.",
        ["SecondaryColor"] = "Color3 — Inner flame color of a Fire effect.",
        ["Heat"] = "number — Fire rise speed, from -25 to 25; negative values move downward.",
        ["RiseVelocity"] = "number — Smoke rise speed, from -25 to 25; negative values move downward.",
        ["Opacity"] = "number — Smoke opacity from 0 to 1.",
        ["TimeScale"] = "number — Fire, Smoke, or Sparkles animation speed from 0 to 1.",
        ["BlastRadius"] = "number — Explosion effect radius from 0 to 100 studs.",
        ["BlastPressure"] = "number — Strength of the outward impulse applied to unanchored parts.",
        ["DestroyJointRadiusPercent"] = "number — Fraction of BlastRadius that breaks welds and defeats unprotected Humanoids.",
        ["Hit"] = "RBXScriptSignal — Explosion callback receives each affected BasePart and its distance from the blast center.",
        ["CanQuery"] = "boolean — Whether a BasePart can be hit by Workspace:Raycast, independently of CanCollide and CanTouch.",
        ["require"] = "require(module: ModuleScript | string): any — Runs once per play runtime; paths may start ./, ../, @self/, or @game/.",
        ["task.wait"] = "task.wait(duration: number?): number — Resumes on a later play update after at least this many seconds.",
        ["task.spawn"] = "task.spawn(callback: function, ...: any): coroutine — Starts a function immediately in a separate task.",
        ["task.defer"] = "task.defer(callback: function, ...: any): coroutine — Runs a function after the current script resume point.",
        ["task.delay"] = "task.delay(duration: number, callback: function, ...: any): coroutine — Runs a function after the delay.",
        ["task.cancel"] = "task.cancel(thread: coroutine): () — Cancels a pending or yielded task.",
        ["wait"] = "wait(duration: number?): number — Alias for task.wait in Lua X.",
        ["Destroy"] = "Destroy(): () — Removes this object and its descendants from the running game.",
        ["WaitForChild"] = "WaitForChild(name: string, timeout: number?): Instance? — Yields until a direct child is available; returns nil on timeout.",
        ["FindFirstChild"] = "FindFirstChild(name: string, recursive: boolean?): Instance? — Finds a direct child, or searches descendants when recursive is true.",
        ["GetChildren"] = "GetChildren(): {Instance} — Returns this object's direct children.",
        ["IsA"] = "IsA(className: string): boolean — Tests the object's class or supported base class.",
        ["SetProperty"] = "SetProperty(object: Instance, propertyName: string, value: any) — Sets a supported Lua X property.",
        ["GetProperty"] = "GetProperty(object: Instance, propertyName: string): any — Reads a supported Lua X property.",
        ["GetAssetPath"] = "GetAssetPath(pathOrName: string): string? — Resolves a project asset to its local file path.",
        ["ConnectTouched"] = "ConnectTouched(object: BasePart, callback: (other: BasePart) -> ()) — Connects to the part's Touched event.",
        ["Connect"] = "Connect(callback: (...any) -> ()) — Registers a callback for this signal.",
        ["Event"] = "RBXScriptSignal — BindableEvent callbacks receive the arguments passed to Fire.",
        ["OnInvoke"] = "function? — Write-only callback used by BindableFunction.Invoke.",
        ["Invoke"] = "Invoke(...arguments: any): (...any) — Waits for OnInvoke and returns its results.",
        ["Fire"] = "Fire(...arguments: any): () — Schedules connected BindableEvent callbacks without yielding.",
        ["Disconnect"] = "Disconnect(): () — Stops a BindableEvent connection from receiving future fires.",
        ["Connected"] = "boolean — Whether a BindableEvent connection is active.",
        ["Touched"] = "RBXScriptSignal — Fires when another BasePart overlaps this part during Lua X execution.",
        ["Activated"] = "RBXScriptSignal — Fires when a TextButton or ImageButton is activated.",
        ["MouseButton1Click"] = "RBXScriptSignal — Alias for the TextButton or ImageButton Activated signal.",
        ["MouseClick"] = "RBXScriptSignal — Fires when the player clicks a ClickDetector's parent part within range.",
        ["Triggered"] = "RBXScriptSignal — Fires when the player presses E near a ProximityPrompt.",
        ["TextChanged"] = "RBXScriptSignal — Fires with the new text when a TextBox changes.",
        ["Parent"] = "Instance? — The object's parent. Assign nil only where supported by the runtime.",
        ["Name"] = "string — The object's Explorer name.",
        ["ClassName"] = "string — The object's class name.",
        ["Id"] = "string — Stable project identifier for this object.",
        ["Children"] = "{Instance} — The object's direct children, available by index, name, or ID.",
        ["Value"] = "any — Stored value of a ValueBase object. ObjectValue holds an Instance or nil.",
        ["LookAtRotation"] = "LookAtRotation(fromPosition: Vector3, toPosition: Vector3): Vector3 — Rotation in degrees that points a 3D part or Custom camera at the target.",
        ["Position"] = "Vector3 for 3D objects; UDim2 for GUI objects.",
        ["Rotation"] = "Vector3 — Rotation in degrees; GUI objects use the Z component.",
        ["Size"] = "Vector3 for parts; UDim2 for GUI objects; number for Fire or Smoke.",
        ["Color"] = "Color3 — Object color.",
        ["Texture"] = "string — Project-relative image path used by a Part, ParticleEmitter, or Trail, such as blocks/grass.png.",
        ["FillColor"] = "Color3 — Highlight fill color.",
        ["OutlineColor"] = "Color3 — Highlight outline color.",
        ["FillTransparency"] = "number — Highlight fill transparency from 0 to 1.",
        ["OutlineTransparency"] = "number — Highlight outline transparency from 0 to 1.",
        ["MaxActivationDistance"] = "number — Maximum distance from the part for a prompt or click detector to work.",
        ["ActionText"] = "string — Action displayed on a ProximityPrompt.",
        ["ObjectText"] = "string — Object label displayed on a ProximityPrompt.",
        ["Asset"] = "string — Project asset path used by a MeshPart or image GUI object.",
        ["CameraMode"] = "string — Custom or Subject.",
        ["Mode"] = "string — Camera mode alias: Custom or Subject.",
        ["Subject"] = "BasePart? — Part or MeshPart tracked by a Subject-mode Camera.",
        ["Part0"] = "BasePart? — First endpoint of a WeldConstraint.",
        ["Part1"] = "BasePart? — Second endpoint of a WeldConstraint.",
        ["FieldOfView"] = "number — Camera field of view in degrees.",
        ["Ambient"] = "Color3 — Ambient lighting color for the scene.",
        ["ClockTime"] = "number — Lighting time of day in hours, from 0 to 24.",
        ["GlobalShadows"] = "boolean — Whether the scene renders global shadows.",
        ["SkyboxFt"] = "string — Project-relative image path for the Sky front face.",
        ["SkyboxBk"] = "string — Project-relative image path for the Sky back face.",
        ["SkyboxLf"] = "string — Project-relative image path for the Sky left face.",
        ["SkyboxRt"] = "string — Project-relative image path for the Sky right face.",
        ["SkyboxUp"] = "string — Project-relative image path for the Sky top face.",
        ["SkyboxDn"] = "string — Project-relative image path for the Sky bottom face.",
        ["SkyboxFront"] = "string — Project-relative image path for the Workspace skybox front face.",
        ["SkyboxBack"] = "string — Project-relative image path for the Workspace skybox back face.",
        ["SkyboxLeft"] = "string — Project-relative image path for the Workspace skybox left face.",
        ["SkyboxRight"] = "string — Project-relative image path for the Workspace skybox right face.",
        ["SkyboxTop"] = "string — Project-relative image path for the Workspace skybox top face.",
        ["SkyboxBottom"] = "string — Project-relative image path for the Workspace skybox bottom face.",
        ["Visible"] = "boolean — Whether this GUI object is rendered.",
        ["ZIndex"] = "integer — Draw order among overlapping GUI siblings; higher values appear on top.",
        ["LayoutOrder"] = "integer — Placement order within list, grid, and table layouts.",
        ["Text"] = "string — Text displayed by a TextLabel, TextButton, or TextBox.",
        ["TextColor"] = "string — Hex color used for GUI text, for example #FFFFFF.",
        ["TextSize"] = "number — GUI text size in pixels.",
        ["CornerRadius"] = "number — UICorner radius in pixels.",
        ["StrokeColor"] = "string — UIOutline or UIStroke hex color.",
        ["StrokeThickness"] = "number — UIOutline or UIStroke width in pixels.",
        ["LayoutDirection"] = "string — UIListLayout direction: Vertical or Horizontal.",
        ["LayoutSpacing"] = "number — Pixel spacing used by a UI layout.",
        ["TableColumns"] = "number — UITableLayout column count.",
        ["CellSize"] = "UDim2 — Width and height of UIGridLayout cells.",
        ["CellPadding"] = "UDim2 — Horizontal and vertical gap between UIGridLayout cells.",
        ["FillDirection"] = "string — Fill UIGridLayout horizontally or vertically.",
        ["FillDirectionMaxCells"] = "integer — Maximum grid cells per row or column; zero uses available space.",
        ["AspectRatio"] = "number — UIAspectRatio width-to-height ratio.",
        ["Anchored"] = "boolean — Whether the part is anchored.",
        ["CastShadow"] = "boolean — Whether the part casts a shadow."
    };

    private readonly string receiver;

    public LuaXCompletionData(string text) : this(text, "") { }

    public LuaXCompletionData(string text, string receiver)
    {
        Text = text;
        this.receiver = receiver;
    }

    public System.Windows.Media.ImageSource? Image => null;
    public string Text { get; }
    public object Content => Text;
    public object Description => Descriptions.TryGetValue($"{receiver}.{Text}", out var contextualDescription)
        ? contextualDescription
        : Descriptions.TryGetValue(Text, out var description) ? description : $"Lua X identifier: {Text}";
    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, Text);
    }
}
