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
using Vector3 = System.Numerics.Vector3;
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
    private readonly List<string> selectedObjectIds = [];
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
    private bool snapToGrid = true;
    private float moveSnapIncrement = 1;
    private float rotateSnapIncrement = 15;
    private Point transformStartPointer;
    private float[] transformStartPosition = [0, 0, 0];
    private float[] transformStartRotation = [0, 0, 0];
    private float[] transformStartSize = [1, 1, 1];
    private float[] transformStartModelPivot = [0, 0, 0];
    private List<(SceneNode Node, float[] Position, float[] Size)> transformStartModelNodes = [];
    private List<(SceneNode Node, float[] Position, float[] Rotation)> transformStartSelectionNodes = [];
    private List<(SceneNode Root, float[] Pivot, List<(SceneNode Node, float[] Position, float[] Size)> Nodes)> transformStartSelectionScaleGroups = [];
    private float[] transformStartSelectionPivot = [0, 0, 0];
    private float transformModelScaleBasis = 1;
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
        GameProjectStore.EnsureProject(next);
        project = next;
        LoadSnapSettings();
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
        selectedObjectIds.RemoveAll(id => FindSceneNode(id) is null);
        if (selected is not null && !selectedObjectIds.Contains(selected.Id))
        {
            selectedObjectIds.Clear();
            selectedObjectIds.Add(selected.Id);
        }
        else if (selected is null && selectedObjectIds.Count > 0) selected = FindSceneNode(selectedObjectIds[^1]);
        RefreshExplorerSelectionAppearance();
        UpdateExplorerFilter(project.Workspace, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.Players, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.Teams, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.Lighting, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.SoundService, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.ReplicatedStorage, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.ServerScriptService, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.ServerStorage, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.StarterPack, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.StarterPlayer, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.Gui, ExplorerSearch.Text.Trim());
        isSuppressingExplorerSelection = true;
        try { ExplorerTree.ItemsSource = new[] { project.Workspace, project.Players, project.Teams, project.Lighting, project.SoundService, project.ReplicatedStorage, project.ServerScriptService, project.ServerStorage, project.StarterPack, project.StarterPlayer, project.Gui }; }
        finally { isSuppressingExplorerSelection = false; }
        ObjectCount.Text = GameProjectStore.Flatten(project).Count.ToString();
        renderer?.Refresh(project);
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
        UpdateExplorerFilter(project.Teams, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.Lighting, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.SoundService, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.ReplicatedStorage, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.ServerScriptService, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.ServerStorage, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.StarterPack, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.StarterPlayer, ExplorerSearch.Text.Trim());
        UpdateExplorerFilter(project.Gui, ExplorerSearch.Text.Trim());
        isSuppressingExplorerSelection = true;
        try
        {
            ExplorerTree.ItemsSource = null;
            ExplorerTree.ItemsSource = new[] { project.Workspace, project.Players, project.Teams, project.Lighting, project.SoundService, project.ReplicatedStorage, project.ServerScriptService, project.ServerStorage, project.StarterPack, project.StarterPlayer, project.Gui };
        }
        finally { isSuppressingExplorerSelection = false; }
        if (selected is not null) SelectTreeNode(selected.Id);
    }

    private static bool UpdateExplorerFilter(SceneNode node, string filter, bool hiddenByTerrain = false)
    {
        if (hiddenByTerrain)
        {
            node.IsExplorerVisible = false;
            foreach (var child in node.Children) UpdateExplorerFilter(child, filter, hiddenByTerrain: true);
            return false;
        }
        var matchesNode = string.IsNullOrEmpty(filter) || node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || node.Type.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var matchesChild = false;
        foreach (var child in node.Children) matchesChild |= UpdateExplorerFilter(child, filter, hiddenByTerrain || node.Type == "Terrain");
        node.IsExplorerVisible = matchesNode || matchesChild;
        return node.IsExplorerVisible;
    }

    private void RefreshExplorerSelectionAppearance()
    {
        if (project is null) return;
        var selectedIds = selectedObjectIds.ToHashSet(StringComparer.Ordinal);
        foreach (var node in GameProjectStore.Flatten(project)) node.IsExplorerSelected = selectedIds.Contains(node.Id);
        renderer?.SetSelections(selectedObjectIds, selected?.Id);
    }

    private void SelectOnly(SceneNode? node)
    {
        selectedObjectIds.Clear();
        if (node is not null) selectedObjectIds.Add(node.Id);
        selected = node;
        RefreshExplorerSelectionAppearance();
    }

    private void ToggleSceneSelection(SceneNode node)
    {
        if (selectedObjectIds.Remove(node.Id))
        {
            selected = selectedObjectIds.Count == 0 ? null : FindSceneNode(selectedObjectIds[^1]);
        }
        else
        {
            selectedObjectIds.Add(node.Id);
            selected = node;
        }
        RefreshExplorerSelectionAppearance();
        if (selected is not null) SelectTreeNode(selected.Id);
        else if (ExplorerTree.SelectedItem is SceneNode previous && FindTreeItem(ExplorerTree, previous.Id) is { } item)
        {
            isSuppressingExplorerSelection = true;
            try { item.IsSelected = false; }
            finally { isSuppressingExplorerSelection = false; }
        }
        RenderProperties();
    }

    private void ExplorerTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (isSuppressingExplorerSelection) return;
        if (propertyPickerTargetId is not null)
        {
            CompleteObjectReferencePick(e.NewValue as SceneNode);
            return;
        }
        SelectOnly(e.NewValue as SceneNode);
        RefreshGuiPreview();
        RenderProperties();
    }

    private void ExplorerTree_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (project is null || propertyPickerTargetId is not null) return;
        var item = GetTreeItem(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not SceneNode node) return;
        item.IsSelected = true;
        SelectOnly(node);
        RenderProperties();
        var menu = new ContextMenu { PlacementTarget = item };
        var insert = new MenuItem { Header = "Insert Object", IsEnabled = node.Type is not ("Players" or "Terrain") };
        insert.Click += (_, _) => ShowInsertMenu(node, item);
        menu.Items.Add(insert);
        var canEdit = node.Type is not ("Workspace" or "Players" or "Teams" or "Lighting" or "SoundService" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" or "StarterPlayer" or "Terrain" or "Gui" or "StarterGui" or "Camera");
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
        var rename = new MenuItem { Header = "Rename    F2", IsEnabled = node.Type is not ("Workspace" or "Players" or "Teams" or "Lighting" or "SoundService" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" or "StarterPlayer" or "Terrain" or "Gui" or "StarterGui") };
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
        if (propertyPickerTargetId is null && item?.DataContext is SceneNode clickedNode
            && !IsExplorerInteractiveSource(source, item) && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            explorerDragSource = null;
            ToggleSceneSelection(clickedNode);
            e.Handled = true;
            return;
        }
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
        if (source.Type is "Workspace" or "Players" or "Teams" or "Lighting" or "SoundService" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" or "StarterPlayer" or "Terrain" or "Gui" or "StarterGui" or "Camera") return;
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
        if (node is not { Type: "Script" or "LocalScript" or "ModuleScript" }) return;
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
        if (ReferenceEquals(target, project.Teams) || target.Id == project.Teams.Id) return CanMoveNode(source, project.Teams);
        if (ReferenceEquals(target, project.Lighting) || target.Id == project.Lighting.Id) return CanMoveNode(source, project.Lighting);
        if (ReferenceEquals(target, project.SoundService) || target.Id == project.SoundService.Id) return CanMoveNode(source, project.SoundService);
        if (ReferenceEquals(target, project.ReplicatedStorage) || target.Id == project.ReplicatedStorage.Id) return CanMoveNode(source, project.ReplicatedStorage);
        if (ReferenceEquals(target, project.ServerScriptService) || target.Id == project.ServerScriptService.Id) return CanMoveNode(source, project.ServerScriptService);
        if (ReferenceEquals(target, project.ServerStorage) || target.Id == project.ServerStorage.Id) return CanMoveNode(source, project.ServerStorage);
        if (ReferenceEquals(target, project.StarterPack) || target.Id == project.StarterPack.Id) return CanMoveNode(source, project.StarterPack);
        if (ReferenceEquals(target, project.StarterPlayer) || target.Id == project.StarterPlayer.Id) return CanMoveNode(source, project.StarterPlayer);
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
        if (selected.Type is "Workspace" or "Players" or "Teams" or "Lighting" or "SoundService" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" or "StarterPlayer" or "Terrain" or "Gui" or "StarterGui") AddLabel($"Name: {selected.Name}");
        else AddTextField("Name", selected.Name);
        if (selected.Type is not ("Players" or "Teams" or "SoundService" or "StarterGui" or "Terrain")) AddObjectReferenceField("Parent", selected, FindParentNode(selected));
        if (selected.Type is not ("Workspace" or "Players" or "Teams" or "Lighting" or "SoundService" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" or "StarterPlayer" or "Terrain" or "Gui" or "StarterGui")) AddCheckField("Archivable", selected.Archivable);
        if (selected.Type == "Team")
        {
            propertyTarget = AddPropertySection("Team");
            AddTextField("TeamColor", selected.TeamColor);
            AddCheckField("AutoAssignable", selected.AutoAssignable);
        }
        if (selected.Type == "SpawnLocation")
        {
            propertyTarget = AddPropertySection("Team");
            AddCheckField("Neutral", selected.Neutral);
            AddTextField("TeamColor", selected.TeamColor);
            AddCheckField("AllowTeamChangeOnTouch", selected.AllowTeamChangeOnTouch);
            AddTextField("Duration", selected.Duration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
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
                "Vector2Value" => VectorText(selected.Vector2Value),
                "Vector3Value" => VectorText(selected.Vector3Value),
                "Color3Value" => selected.Color3Value,
                _ => selected.StringValueText
            });
        }
        if (selected.Type == "Model" && !IsGuiNode(selected))
        {
            propertyTarget = AddPropertySection("Transform");
            var primaryPart = FindSceneNode(selected.PrimaryPart);
            AddObjectReferenceField("PrimaryPart", selected, SceneModelService.ContainsPrimaryPart(selected, primaryPart?.Id ?? string.Empty) ? primaryPart : null);
            var clearPrimaryPart = new Button { Content = "Clear PrimaryPart", Margin = new Thickness(3, 2, 3, 2), Padding = new Thickness(5, 2, 5, 2), IsEnabled = selected.PrimaryPart.Length > 0 };
            clearPrimaryPart.Click += (_, _) =>
            {
                SceneModelService.SetPrimaryPart(selected, null);
                RecordEdit();
                RenderProperties();
            };
            propertyTarget.Children.Add(clearPrimaryPart);
            AddTextField("Position", VectorText(SceneModelService.GetPivot(selected)));
            AddTextField("Rotation", VectorText(selected.Rotation));
            AddLabel("PrimaryPart sets the model pivot. Moving or rotating the model transforms its parts together.", "Position");
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
        if (selected.Type == "Tool")
        {
            propertyTarget = AddPropertySection("Tool");
            AddTextField("ToolTip", selected.ToolTip);
            AddCheckField("RequiresHandle", selected.RequiresHandle);
            AddCheckField("CanBeDropped", selected.CanBeDropped);
            AddCheckField("ManualActivationOnly", selected.ManualActivationOnly);
            AddTextField("GripPosition", VectorText(selected.GripPosition));
            AddTextField("GripRotation", VectorText(selected.GripRotation));
        }
        if (selected.Type == "VehicleSeat")
        {
            propertyTarget = AddPropertySection("Vehicle");
            AddCheckField("Disabled", selected.VehicleSeatDisabled);
            AddCheckField("HeadsUpDisplay", selected.VehicleSeatHeadsUpDisplay);
            AddTextField("MaxSpeed", selected.VehicleSeatMaxSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Torque", selected.VehicleSeatTorque.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("TurnSpeed", selected.VehicleSeatTurnSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel($"Throttle: {selected.VehicleSeatThrottleFloat:0.##}  Steer: {selected.VehicleSeatSteerFloat:0.##}  Motor hinges: {selected.VehicleSeatAreHingesDetected}", "ThrottleFloat");
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
        if (selected.Type == "Atmosphere")
        {
            propertyTarget = AddPropertySection("Atmosphere");
            AddCheckField("Enabled", selected.Enabled);
            AddTextField("Color", selected.Color);
            AddTextField("Decay", selected.AtmosphereDecay);
            AddTextField("Density", selected.Density.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Haze", selected.Haze.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Glare", selected.Glare.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Offset", selected.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (selected.Type == "BlurEffect")
        {
            propertyTarget = AddPropertySection("Post Effect");
            AddCheckField("Enabled", selected.Enabled);
            AddTextField("Size", selected.EffectSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (selected.Type == "ColorCorrectionEffect")
        {
            propertyTarget = AddPropertySection("Color Correction");
            AddCheckField("Enabled", selected.Enabled);
            AddTextField("TintColor", selected.TintColor);
            AddTextField("Brightness", selected.Brightness.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Contrast", selected.ColorCorrectionContrast.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Saturation", selected.ColorCorrectionSaturation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (SceneNodeTypes.IsSurfaceAppearance(selected))
        {
            propertyTarget = AddPropertySection("Surface Appearance");
            AddTextField("Color", selected.SurfaceAppearanceColor);
            AddTextField("ColorMap", selected.SurfaceAppearanceColorMap);
            AddTextField("NormalMap", selected.SurfaceAppearanceNormalMap);
            AddTextField("RoughnessMap", selected.SurfaceAppearanceRoughnessMap);
            AddTextField("MetalnessMap", selected.SurfaceAppearanceMetalnessMap);
            AddTextField("EmissiveMaskContent", selected.SurfaceAppearanceEmissiveMask);
            AddTextField("EmissiveTint", selected.SurfaceAppearanceEmissiveTint);
            AddTextField("EmissiveStrength", selected.SurfaceAppearanceEmissiveStrength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("Texture map paths are project assets applied to the parent MeshPart.", "ColorMap");
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
                    AddComboField("Material", SceneNodeTypes.Materials, selected.Material);
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
                    AddTextField("AssemblyAngularVelocity", VectorText(selected.AssemblyAngularVelocity));
                }
                if (selected.Type is "Seat" or "VehicleSeat")
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
            AddTextField("HoldDuration", selected.HoldDuration.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("MaxActivationDistance", selected.MaxActivationDistance.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("RequiresLineOfSight", selected.RequiresLineOfSight);
            AddLabel("Press or hold E near this part during play to fire Triggered. Line of sight checks ignore the character and target part.", "Triggered");
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
            AddTextField("PlaybackSpeed", selected.PlaybackSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddObjectReferenceField("SoundGroup", selected, FindSceneNode(selected.SoundGroupId) is { Type: "SoundGroup" } soundGroup ? soundGroup : null);
            var clearSoundGroup = new Button { Content = "Clear SoundGroup", Margin = new Thickness(3, 2, 3, 2), Padding = new Thickness(5, 2, 5, 2), IsEnabled = selected.SoundGroupId.Length > 0 };
            clearSoundGroup.Click += (_, _) =>
            {
                selected.SoundGroupId = string.Empty;
                RecordEdit();
                RenderProperties();
            };
            propertyTarget.Children.Add(clearSoundGroup);
            AddCheckField("Looped", selected.Looped);
            AddCheckField("Playing", selected.Playing);
            AddTextField("RollOffMinDistance", selected.RollOffMinDistance.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("RollOffMaxDistance", selected.RollOffMaxDistance.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("Use a WAV or MP3 asset. A Sound under a part fades with distance during play.", "Asset");
        }
        if (selected.Type == "SoundGroup")
        {
            propertyTarget = AddPropertySection("Sound Group");
            AddTextField("Volume", selected.SoundGroupVolume.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("Adjust the output level of Sounds assigned to this group.", "Volume");
        }
        if (selected.Type == "SoundService")
        {
            propertyTarget = AddPropertySection("Audio");
            AddTextField("Volume", selected.SoundServiceVolume.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("Master playback level for all Sounds.", "Volume");
        }
        if (selected.Type == "PitchShiftSoundEffect")
        {
            propertyTarget = AddPropertySection("Pitch Shift");
            AddCheckField("Enabled", selected.Enabled);
            AddTextField("Octave", selected.PitchShiftOctave.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("Octave multiplier from 0.5 to 2; 1 leaves pitch unchanged. Playback speed is preserved.", "Octave");
        }
        if (selected.Type == "EchoSoundEffect")
        {
            propertyTarget = AddPropertySection("Echo");
            AddCheckField("Enabled", selected.Enabled);
            AddTextField("Delay", selected.EchoDelay.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Feedback", selected.EchoFeedback.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("DryLevel", selected.EchoDryLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("WetLevel", selected.EchoWetLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("Delay uses seconds (0.01–5); levels use decibels (-80 to 10).", "Delay");
        }
        if (selected.Type == "FlangeSoundEffect")
        {
            propertyTarget = AddPropertySection("Flange");
            AddCheckField("Enabled", selected.Enabled);
            AddTextField("Depth", selected.FlangeDepth.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Mix", selected.FlangeMix.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Rate", selected.FlangeRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("Depth 0.01–1, Mix 0–1, and Rate 0–20 Hz.", "Depth");
        }
        if (selected.Type == "TremoloSoundEffect")
        {
            propertyTarget = AddPropertySection("Tremolo");
            AddCheckField("Enabled", selected.Enabled);
            AddTextField("Depth", selected.TremoloDepth.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Duty", selected.TremoloDuty.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Frequency", selected.TremoloFrequency.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("Depth and Duty use 0–1; Frequency uses 0.1–20 Hz.", "Depth");
        }
        if (selected.Type == "ReverbSoundEffect")
        {
            propertyTarget = AddPropertySection("Reverb");
            AddCheckField("Enabled", selected.Enabled);
            AddTextField("DecayTime", selected.ReverbDecayTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Density", selected.ReverbDensity.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Diffusion", selected.ReverbDiffusion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("DryLevel", selected.ReverbDryLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("WetLevel", selected.ReverbWetLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("DecayTime is 0.1–20 seconds; Density and Diffusion are 0–1; levels are -80 to 10 dB.", "DecayTime");
        }
        if (selected.Type == "Highlight")
        {
            propertyTarget = AddPropertySection("Highlight");
            AddCheckField("Enabled", selected.Enabled);
            var adornee = FindSceneNode(selected.Adornee);
            AddObjectReferenceField("Adornee", selected, adornee is not null && (SceneNodeTypes.IsBasePart(adornee) || adornee.Type == "Model") ? adornee : null);
            AddClearAdorneeButton(selected);
            AddTextField("FillColor", selected.FillColor);
            AddTextField("FillTransparency", selected.FillTransparency.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("OutlineColor", selected.OutlineColor);
            AddTextField("OutlineTransparency", selected.OutlineTransparency.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("When Adornee is empty, a Highlight under a part or Model targets its parent.", "Adornee");
        }
        if (selected.Type is "SelectionBox" or "SelectionSphere")
        {
            propertyTarget = AddPropertySection(selected.Type == "SelectionBox" ? "Selection Box" : "Selection Sphere");
            AddCheckField("Visible", selected.Visible);
            var adornee = FindSceneNode(selected.Adornee);
            AddObjectReferenceField("Adornee", selected, adornee is not null && (SceneNodeTypes.IsBasePart(adornee) || adornee.Type == "Model") ? adornee : null);
            AddClearAdorneeButton(selected);
            AddTextField("Color3", selected.Color);
            AddTextField("Transparency", selected.Transparency.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (selected.Type == "SelectionBox") AddTextField("LineThickness", selected.LineThickness.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("SurfaceColor3", selected.SurfaceColor3);
            AddTextField("SurfaceTransparency", selected.SurfaceTransparency.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
        if (selected.Type is "WeldConstraint" or "NoCollisionConstraint")
        {
            propertyTarget = AddPropertySection(selected.Type == "WeldConstraint" ? "Weld Constraint" : "No Collision Constraint");
            AddCheckField("Enabled", selected.Enabled);
            var part0 = FindSceneNode(selected.Part0);
            var part1 = FindSceneNode(selected.Part1);
            AddObjectReferenceField("Part0", selected, SceneNodeTypes.IsBasePart(part0) ? part0 : null);
            AddObjectReferenceField("Part1", selected, SceneNodeTypes.IsBasePart(part1) ? part1 : null);
            AddLabel(selected.Type == "WeldConstraint" ? "Joined parts move as one assembly during play." : "The linked parts pass through each other during play.", "Part0");
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
            if (selected.RelativeTo == "Attachment1") AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } forceReferenceAttachment ? forceReferenceAttachment : null);
            AddTextField("Force", VectorText(selected.Force));
            AddComboField("RelativeTo", ["World", "Attachment0", "Attachment1"], selected.RelativeTo);
            AddCheckField("ApplyAtCenterOfMass", selected.ApplyAtCenterOfMass);
            AddLabel("When off, an offset Attachment0 can also rotate the assembly.", "ApplyAtCenterOfMass");
        }
        if (selected.Type == "LineForce")
        {
            propertyTarget = AddPropertySection("LineForce");
            AddCheckField("Enabled", selected.Enabled);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } lineForceAttachment0 ? lineForceAttachment0 : null);
            AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } lineForceAttachment1 ? lineForceAttachment1 : null);
            AddTextField("Magnitude", selected.LineForceMagnitude.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("InverseSquareLaw", selected.LineForceInverseSquareLaw);
            AddTextField("MaxForce", selected.LineForceMaxForce.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("ReactionForceEnabled", selected.LineForceReactionForceEnabled);
            AddCheckField("ApplyAtCenterOfMass", selected.ApplyAtCenterOfMass);
        }
        if (selected.Type == "BodyForce")
        {
            propertyTarget = AddPropertySection("BodyForce");
            AddCheckField("Enabled", selected.Enabled);
            AddTextField("Force", VectorText(selected.Force));
            AddLabel("Applies a constant world-axis force to the parent part during play.", "Force");
        }
        if (selected.Type == "BodyThrust")
        {
            propertyTarget = AddPropertySection("BodyThrust");
            AddCheckField("Enabled", selected.Enabled);
            AddTextField("Force", VectorText(selected.BodyThrustForce));
            AddTextField("Location", VectorText(selected.BodyThrustLocation));
            AddLabel("Applies force in the assembly's local axes at an assembly-relative position, which can also rotate it.", "Location");
        }
        if (selected.Type == "BodyVelocity")
        {
            propertyTarget = AddPropertySection("BodyVelocity");
            AddCheckField("Enabled", selected.Enabled);
            AddTextField("Velocity", VectorText(selected.BodyVelocityTarget));
            AddTextField("MaxForce", VectorText(selected.BodyVelocityMaxForce));
            AddTextField("P", selected.BodyVelocityP.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (selected.Type == "BodyPosition")
        {
            propertyTarget = AddPropertySection("BodyPosition");
            AddCheckField("Enabled", selected.Enabled);
            AddTextField("Position", VectorText(selected.BodyPositionTarget));
            AddTextField("MaxForce", VectorText(selected.BodyPositionMaxForce));
            AddTextField("P", selected.BodyPositionP.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("D", selected.BodyPositionD.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (selected.Type == "BodyGyro")
        {
            propertyTarget = AddPropertySection("BodyGyro");
            AddCheckField("Enabled", selected.Enabled);
            AddTextField("Rotation", VectorText(selected.BodyGyroTargetRotation));
            AddTextField("MaxTorque", VectorText(selected.BodyGyroMaxTorque));
            AddTextField("P", selected.BodyGyroP.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("D", selected.BodyGyroD.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (selected.Type == "BodyAngularVelocity")
        {
            propertyTarget = AddPropertySection("BodyAngularVelocity");
            AddCheckField("Enabled", selected.Enabled);
            AddTextField("AngularVelocity", VectorText(selected.BodyAngularVelocityTarget));
            AddTextField("MaxTorque", VectorText(selected.BodyAngularVelocityMaxTorque));
            AddTextField("P", selected.BodyAngularVelocityP.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (selected.Type == "RocketPropulsion")
        {
            propertyTarget = AddPropertySection("RocketPropulsion");
            AddCheckField("Enabled", selected.Enabled);
            AddObjectReferenceField("Target", selected, FindSceneNode(selected.RocketPropulsionTarget) is { } targetPart && SceneNodeTypes.IsBasePart(targetPart) ? targetPart : null);
            AddTextField("TargetOffset", VectorText(selected.RocketPropulsionTargetOffset));
            AddTextField("TargetRadius", selected.RocketPropulsionTargetRadius.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("CartoonFactor", selected.RocketPropulsionCartoonFactor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("MaxSpeed", selected.RocketPropulsionMaxSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("MaxThrust", selected.RocketPropulsionMaxThrust.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("MaxTorque", VectorText(selected.RocketPropulsionMaxTorque));
            AddTextField("ThrustD", selected.RocketPropulsionThrustD.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("ThrustP", selected.RocketPropulsionThrustP.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("TurnD", selected.RocketPropulsionTurnD.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("TurnP", selected.RocketPropulsionTurnP.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("Call Fire() or Abort() in Lua to start or stop target tracking.", "Target");
        }
        if (selected.Type == "LinearVelocity")
        {
            propertyTarget = AddPropertySection("LinearVelocity");
            AddCheckField("Enabled", selected.Enabled);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } velocityAttachment ? velocityAttachment : null);
            if (selected.RelativeTo == "Attachment1") AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } velocityReferenceAttachment ? velocityReferenceAttachment : null);
            AddComboField("RelativeTo", ["World", "Attachment0", "Attachment1"], selected.RelativeTo);
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
            AddCheckField("ReactionForceEnabled", selected.LinearVelocityReactionForceEnabled);
            if (selected.ForceLimitsEnabled)
            {
                AddComboField("ForceLimitMode", ["Magnitude", "PerAxis"], selected.ForceLimitMode);
                if (selected.ForceLimitMode == "Magnitude" || selected.VelocityConstraintMode == "Line") AddTextField("MaxForce", selected.MaxForce.ToString(System.Globalization.CultureInfo.InvariantCulture));
                else if (selected.VelocityConstraintMode == "Vector") AddTextField("MaxAxesForce", VectorText(selected.MaxAxesForce));
                else if (selected.VelocityConstraintMode == "Plane") AddTextField("MaxPlanarAxesForce", VectorText(selected.MaxPlanarAxesForce));
            }
        }
        if (selected.Type == "AlignPosition")
        {
            propertyTarget = AddPropertySection("AlignPosition");
            AddCheckField("Enabled", selected.Enabled);
            AddComboField("Mode", ["OneAttachment", "TwoAttachment"], selected.AlignmentMode);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } alignAttachment0 ? alignAttachment0 : null);
            if (selected.AlignmentMode == "TwoAttachment")
                AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } alignAttachment1 ? alignAttachment1 : null);
            else AddTextField("Position", VectorText(selected.Position));
            AddTextField("Responsiveness", selected.Responsiveness.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("MaxVelocity", selected.MaxVelocity.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("MaxForce", selected.MaxForce.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("RigidityEnabled", selected.RigidityEnabled);
            AddCheckField("ReactionForceEnabled", selected.ReactionForceEnabled);
            AddCheckField("ApplyAtCenterOfMass", selected.ApplyAtCenterOfMass);
        }
        if (selected.Type == "AlignOrientation")
        {
            propertyTarget = AddPropertySection("AlignOrientation");
            AddCheckField("Enabled", selected.Enabled);
            AddComboField("Mode", ["OneAttachment", "TwoAttachment"], selected.AlignmentMode);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } orientationAttachment0 ? orientationAttachment0 : null);
            if (selected.AlignmentMode == "TwoAttachment")
                AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } orientationAttachment1 ? orientationAttachment1 : null);
            else AddTextField("Rotation", VectorText(selected.AlignOrientationRotation));
            AddTextField("Responsiveness", selected.Responsiveness.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("MaxAngularVelocity", selected.MaxAngularVelocity.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("MaxTorque", selected.MaxTorque.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("RigidityEnabled", selected.RigidityEnabled);
            AddCheckField("ReactionTorqueEnabled", selected.ReactionTorqueEnabled);
        }
        if (selected.Type == "AngularVelocity")
        {
            propertyTarget = AddPropertySection("AngularVelocity");
            AddCheckField("Enabled", selected.Enabled);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } angularAttachment0 ? angularAttachment0 : null);
            AddComboField("RelativeTo", ["World", "Attachment0", "Attachment1"], selected.RelativeTo);
            if (selected.RelativeTo == "Attachment1") AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } angularAttachment1 ? angularAttachment1 : null);
            AddTextField("AngularVelocity", VectorText(selected.AngularVelocityTarget));
            AddTextField("MaxTorque", selected.MaxTorque.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("ReactionTorqueEnabled", selected.ReactionTorqueEnabled);
            AddLabel("Angular velocity is measured in radians per second.", "AngularVelocity");
        }
        if (selected.Type == "Torque")
        {
            propertyTarget = AddPropertySection("Torque");
            AddCheckField("Enabled", selected.Enabled);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } torqueAttachment0 ? torqueAttachment0 : null);
            AddComboField("RelativeTo", ["World", "Attachment0", "Attachment1"], selected.RelativeTo);
            if (selected.RelativeTo == "Attachment1") AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } torqueAttachment1 ? torqueAttachment1 : null);
            AddTextField("Torque", VectorText(selected.TorqueTarget));
            AddLabel("Applies constant torque to Attachment0's part during play.", "Torque");
        }
        if (selected.Type == "SpringConstraint")
        {
            propertyTarget = AddPropertySection("SpringConstraint");
            AddCheckField("Enabled", selected.Enabled);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } springAttachment0 ? springAttachment0 : null);
            AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } springAttachment1 ? springAttachment1 : null);
            AddTextField("FreeLength", selected.SpringFreeLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Stiffness", selected.SpringStiffness.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Damping", selected.SpringDamping.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("MaxForce", selected.SpringMaxForce.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("LimitsEnabled", selected.SpringLimitsEnabled);
            if (selected.SpringLimitsEnabled)
            {
                AddTextField("MinLength", selected.SpringMinLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("MaxLength", selected.SpringMaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            AddLabel("Pulls its two Attachments toward FreeLength during play.", "Stiffness");
        }
        if (selected.Type == "TorsionSpringConstraint")
        {
            propertyTarget = AddPropertySection("TorsionSpringConstraint");
            AddCheckField("Enabled", selected.Enabled);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } torsionAttachment0 ? torsionAttachment0 : null);
            AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } torsionAttachment1 ? torsionAttachment1 : null);
            AddLabel($"CurrentAngle: {selected.TorsionSpringCurrentAngle:0.##}", "CurrentAngle");
            AddTextField("Stiffness", selected.TorsionSpringStiffness.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Damping", selected.TorsionSpringDamping.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("MaxTorque", selected.TorsionSpringMaxTorque.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("LimitsEnabled", selected.TorsionSpringLimitsEnabled);
            if (selected.TorsionSpringLimitsEnabled)
            {
                AddTextField("MaxAngle", selected.TorsionSpringMaxAngle.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("Restitution", selected.TorsionSpringRestitution.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            AddTextField("Coils", selected.TorsionSpringCoils.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Radius", selected.TorsionSpringRadius.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("Aligns the attachments’ secondary axes with a damped torsional spring.", "Stiffness");
        }
        if (selected.Type == "BallSocketConstraint")
        {
            propertyTarget = AddPropertySection("BallSocketConstraint");
            AddCheckField("Enabled", selected.Enabled);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } ballSocketAttachment0 ? ballSocketAttachment0 : null);
            AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } ballSocketAttachment1 ? ballSocketAttachment1 : null);
            AddTextField("Radius", selected.BallSocketRadius.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Restitution", selected.BallSocketRestitution.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("MaxFrictionTorque", selected.BallSocketMaxFrictionTorque.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("LimitsEnabled", selected.BallSocketLimitsEnabled);
            if (selected.BallSocketLimitsEnabled) AddTextField("UpperAngle", selected.BallSocketUpperAngle.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("TwistLimitsEnabled", selected.BallSocketTwistLimitsEnabled);
            if (selected.BallSocketTwistLimitsEnabled)
            {
                AddTextField("TwistLowerAngle", selected.BallSocketTwistLowerAngle.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("TwistUpperAngle", selected.BallSocketTwistUpperAngle.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            AddLabel("Keeps attachment points together and limits swing and twist during play.", "LimitsEnabled");
        }
        if (selected.Type == "UniversalConstraint")
        {
            propertyTarget = AddPropertySection("UniversalConstraint");
            AddCheckField("Enabled", selected.Enabled);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } universalAttachment0 ? universalAttachment0 : null);
            AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } universalAttachment1 ? universalAttachment1 : null);
            AddTextField("Radius", selected.UniversalRadius.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Restitution", selected.UniversalRestitution.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("LimitsEnabled", selected.UniversalLimitsEnabled);
            if (selected.UniversalLimitsEnabled) AddTextField("MaxAngle", selected.UniversalMaxAngle.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel("Keeps the attachment secondary axes perpendicular, with an optional primary-axis tilt limit.", "LimitsEnabled");
        }
        if (selected.Type == "HingeConstraint")
        {
            propertyTarget = AddPropertySection("HingeConstraint");
            AddCheckField("Enabled", selected.Enabled);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } hingeAttachment0 ? hingeAttachment0 : null);
            AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } hingeAttachment1 ? hingeAttachment1 : null);
            AddComboField("ActuatorType", ["None", "Motor", "Servo"], selected.HingeActuatorType);
            AddLabel($"CurrentAngle: {selected.HingeCurrentAngle:0.##}°", "CurrentAngle");
            AddTextField("Radius", selected.HingeRadius.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("LimitsEnabled", selected.HingeLimitsEnabled);
            if (selected.HingeLimitsEnabled)
            {
                AddTextField("LowerAngle", selected.HingeLowerAngle.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("UpperAngle", selected.HingeUpperAngle.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("Restitution", selected.HingeRestitution.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (selected.HingeActuatorType == "Motor")
            {
                AddTextField("AngularVelocity", selected.HingeAngularVelocity.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("MotorMaxAcceleration", selected.HingeMotorMaxAcceleration.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("MotorMaxTorque", selected.HingeMotorMaxTorque.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (selected.HingeActuatorType == "Servo")
            {
                AddTextField("TargetAngle", selected.HingeTargetAngle.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("AngularSpeed", selected.HingeAngularSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("AngularResponsiveness", selected.HingeAngularResponsiveness.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("ServoMaxTorque", selected.HingeServoMaxTorque.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            AddLabel("Joins the attachment points, aligns their X axes, and optionally drives rotation with a motor or servo.", "ActuatorType");
        }
        if (selected.Type == "PrismaticConstraint")
        {
            propertyTarget = AddPropertySection("PrismaticConstraint");
            AddCheckField("Enabled", selected.Enabled);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } prismaticAttachment0 ? prismaticAttachment0 : null);
            AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } prismaticAttachment1 ? prismaticAttachment1 : null);
            AddComboField("ActuatorType", ["None", "Motor", "Servo"], selected.PrismaticActuatorType);
            AddLabel($"CurrentPosition: {selected.PrismaticCurrentPosition:0.##}", "CurrentPosition");
            AddTextField("Size", selected.PrismaticSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("LimitsEnabled", selected.PrismaticLimitsEnabled);
            if (selected.PrismaticLimitsEnabled)
            {
                AddTextField("LowerLimit", selected.PrismaticLowerLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("UpperLimit", selected.PrismaticUpperLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("Restitution", selected.PrismaticRestitution.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (selected.PrismaticActuatorType == "Motor")
            {
                AddTextField("Velocity", selected.PrismaticVelocity.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("MotorMaxAcceleration", selected.PrismaticMotorMaxAcceleration.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("MotorMaxForce", selected.PrismaticMotorMaxForce.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (selected.PrismaticActuatorType == "Servo")
            {
                AddTextField("TargetPosition", selected.PrismaticTargetPosition.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("Speed", selected.PrismaticSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("LinearResponsiveness", selected.PrismaticLinearResponsiveness.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("ServoMaxForce", selected.PrismaticServoMaxForce.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            AddLabel("Joins attachment points, locks relative rotation, and allows translation along their X axes.", "ActuatorType");
        }
        if (selected.Type == "CylindricalConstraint")
        {
            propertyTarget = AddPropertySection("CylindricalConstraint");
            AddCheckField("Enabled", selected.Enabled);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } cylindricalAttachment0 ? cylindricalAttachment0 : null);
            AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } cylindricalAttachment1 ? cylindricalAttachment1 : null);
            AddComboField("ActuatorType", ["None", "Motor", "Servo"], selected.PrismaticActuatorType);
            AddLabel($"CurrentPosition: {selected.PrismaticCurrentPosition:0.##}", "CurrentPosition");
            AddTextField("Size", selected.PrismaticSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("LimitsEnabled", selected.PrismaticLimitsEnabled);
            if (selected.PrismaticLimitsEnabled)
            {
                AddTextField("LowerLimit", selected.PrismaticLowerLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("UpperLimit", selected.PrismaticUpperLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("Restitution", selected.PrismaticRestitution.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (selected.PrismaticActuatorType == "Motor")
            {
                AddTextField("Velocity", selected.PrismaticVelocity.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("MotorMaxAcceleration", selected.PrismaticMotorMaxAcceleration.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("MotorMaxForce", selected.PrismaticMotorMaxForce.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (selected.PrismaticActuatorType == "Servo")
            {
                AddTextField("TargetPosition", selected.PrismaticTargetPosition.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("Speed", selected.PrismaticSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("LinearResponsiveness", selected.PrismaticLinearResponsiveness.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("ServoMaxForce", selected.PrismaticServoMaxForce.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            AddComboField("AngularActuatorType", ["None", "Motor", "Servo"], selected.HingeActuatorType);
            AddLabel($"CurrentAngle: {selected.HingeCurrentAngle:0.##}", "CurrentAngle");
            AddCheckField("AngularLimitsEnabled", selected.HingeLimitsEnabled);
            if (selected.HingeLimitsEnabled)
            {
                AddTextField("LowerAngle", selected.HingeLowerAngle.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("UpperAngle", selected.HingeUpperAngle.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("AngularRestitution", selected.HingeRestitution.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (selected.HingeActuatorType == "Motor")
            {
                AddTextField("AngularVelocity", selected.HingeAngularVelocity.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("MotorMaxAngularAcceleration", selected.HingeMotorMaxAcceleration.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("MotorMaxTorque", selected.HingeMotorMaxTorque.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (selected.HingeActuatorType == "Servo")
            {
                AddTextField("TargetAngle", selected.HingeTargetAngle.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("AngularSpeed", selected.HingeAngularSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("AngularResponsiveness", selected.HingeAngularResponsiveness.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("ServoMaxTorque", selected.HingeServoMaxTorque.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            AddTextField("InclinationAngle", selected.CylindricalInclinationAngle.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("RotationAxisVisible", selected.CylindricalRotationAxisVisible);
            AddLabel("Combines sliding along Attachment0 X with rotation around the inclined axis.", "InclinationAngle");
        }
        if (selected.Type == "PlaneConstraint")
        {
            propertyTarget = AddPropertySection("PlaneConstraint");
            AddCheckField("Enabled", selected.Enabled);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } planeAttachment0 ? planeAttachment0 : null);
            AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } planeAttachment1 ? planeAttachment1 : null);
            AddLabel("Keeps Attachment1 on the plane through Attachment0, using Attachment0's X axis as the normal.", "Attachment0");
        }
        if (selected.Type == "RigidConstraint")
        {
            propertyTarget = AddPropertySection("RigidConstraint");
            AddCheckField("Enabled", selected.Enabled);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } rigidAttachment0 ? rigidAttachment0 : null);
            AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } rigidAttachment1 ? rigidAttachment1 : null);
            AddLabel("Keeps both attachment frames aligned during play.", "Attachment0");
        }
        if (selected.Type == "RopeConstraint")
        {
            propertyTarget = AddPropertySection("RopeConstraint");
            AddCheckField("Enabled", selected.Enabled);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } ropeAttachment0 ? ropeAttachment0 : null);
            AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } ropeAttachment1 ? ropeAttachment1 : null);
            AddTextField("Length", selected.RopeLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("Restitution", selected.RopeRestitution.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("WinchEnabled", selected.RopeWinchEnabled);
            if (selected.RopeWinchEnabled)
            {
                AddTextField("WinchTarget", selected.RopeWinchTarget.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("WinchSpeed", selected.RopeWinchSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("WinchResponsiveness", selected.RopeWinchResponsiveness.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("WinchForce", selected.RopeWinchForce.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            AddLabel("Limits separation to Length and can use a winch to change the distance.", "Length");
        }
        if (selected.Type == "RodConstraint")
        {
            propertyTarget = AddPropertySection("RodConstraint");
            AddCheckField("Enabled", selected.Enabled);
            AddObjectReferenceField("Attachment0", selected, FindSceneNode(selected.Attachment0) is { Type: "Attachment" } rodAttachment0 ? rodAttachment0 : null);
            AddObjectReferenceField("Attachment1", selected, FindSceneNode(selected.Attachment1) is { Type: "Attachment" } rodAttachment1 ? rodAttachment1 : null);
            AddTextField("Length", selected.RodLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddLabel($"CurrentDistance: {selected.RodCurrentDistance:0.##}", "CurrentDistance");
            AddTextField("Thickness", selected.RodThickness.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("LimitsEnabled", selected.RodLimitsEnabled);
            if (selected.RodLimitsEnabled)
            {
                AddTextField("LimitAngle0", selected.RodLimitAngle0.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("LimitAngle1", selected.RodLimitAngle1.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            AddLabel("Keeps two attachments at a fixed distance; optional angle limits are exposed for scripts.", "Length");
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
        if (IsGuiNode(selected) && selected.Type != "StarterGui") RenderGuiProperties(selected);
        if (selected.Type is "Script" or "LocalScript" or "ModuleScript")
        {
            var script = project.Scripts.FirstOrDefault(item => item.Id == selected.Id);
            if (script is not null)
            {
                propertyTarget = AddPropertySection(selected.Type);
                if (selected.Type is "Script" or "LocalScript") AddCheckField("Enabled", selected.Enabled);
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
        if (node.Type is "BillboardGui" or "SurfaceGui")
        {
            propertyTarget = AddPropertySection(node.Type);
            AddCheckField("Enabled", node.Enabled);
            AddCheckField("AlwaysOnTop", node.AlwaysOnTop);
            AddTextField("MaxDistance", node.MaxDistance.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (node.Type == "BillboardGui") AddTextField("Size", GuiUDim2Text(node.Size, node.SizeScale));
            if (node.Type == "SurfaceGui")
            {
                AddComboField("Face", ["Front", "Back", "Left", "Right", "Top", "Bottom"], node.Face);
                AddTextField("PixelsPerStud", node.PixelsPerStud.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            AddObjectReferenceField("Adornee", node, FindSceneNode(node.Adornee));
        }
        else if (node.Type is "ScreenGui" or "Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox")
        {
            propertyTarget = AddPropertySection("Layout");
            if (node.Type != "ScreenGui")
            {
                AddTextField("Position", GuiUDim2Text(node.Position, node.PositionScale));
                AddTextField("AnchorPoint", VectorText(node.AnchorPoint));
                AddTextField("Size", GuiUDim2Text(node.Size, node.SizeScale));
                AddTextField("Rotation", VectorText(node.Rotation));
                AddTextField("ZIndex", node.ZIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddTextField("LayoutOrder", node.LayoutOrder.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (node.Type == "ScreenGui")
            {
                AddTextField("DisplayOrder", node.DisplayOrder.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddCheckField("Enabled", node.Enabled);
            }
            else AddCheckField("Visible", node.Visible);
        }
        if (node.Type is "Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox")
        {
            propertyTarget = AddPropertySection("Appearance");
            AddTextField("Color", node.Color);
            AddTextField("BackgroundTransparency", node.BackgroundTransparency.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (node.Type is "TextLabel" or "TextButton" or "TextBox")
        {
            propertyTarget = AddPropertySection("Text");
            AddTextField("Text", node.Text);
            AddTextField("TextColor", node.TextColor);
            if (node.Type == "TextBox") AddTextField("PlaceholderText", node.PlaceholderText);
            AddTextField("TextSize", node.TextSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("TextTransparency", node.TextTransparency.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddCheckField("TextScaled", node.TextScaled);
            if (node.Type == "TextBox")
            {
                AddCheckField("MultiLine", node.MultiLine);
                AddCheckField("TextEditable", node.TextEditable);
                AddCheckField("ClearTextOnFocus", node.ClearTextOnFocus);
            }
        }
        if (node.Type is "ImageLabel" or "ImageButton")
        {
            propertyTarget = AddPropertySection("Image");
            AddTextField("Asset", node.Asset);
            AddTextField("ImageTransparency", node.ImageTransparency.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (node.Type == "UICorner")
        {
            propertyTarget = AddPropertySection("Appearance");
            foreach (var radiusProperty in new[] { "CornerRadius", "TopLeftRadius", "TopRightRadius", "BottomRightRadius", "BottomLeftRadius" })
                AddTextField(radiusProperty, GuiUDimText(UICornerRadiusService.Get(node, radiusProperty)));
        }
        if (node.Type == "UIShadow")
        {
            propertyTarget = AddPropertySection("Shadow");
            AddTextField("Color", node.Color);
            AddTextField("Transparency", node.Transparency.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("BlurRadius", GuiUDimText(node.ShadowBlurRadius));
            AddTextField("Offset", GuiUDim2Text(node.ShadowOffset, node.ShadowOffsetScale));
            AddTextField("Spread", GuiUDim2Text(node.ShadowSpread, node.ShadowSpreadScale));
            AddComboField("Mode", ["Shape", "Text"], node.ShadowMode);
            AddCheckField("Inset", node.ShadowInset);
            AddCheckField("ShowBehindParent", node.ShadowShowBehindParent);
            AddCheckField("Enabled", node.Enabled);
            AddTextField("ZIndex", node.ZIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
            AddComboField("FillDirection", ["Vertical", "Horizontal"], node.LayoutDirection);
            AddComboField("HorizontalAlignment", ["Left", "Center", "Right"], node.HorizontalAlignment);
            AddComboField("VerticalAlignment", ["Top", "Center", "Bottom"], node.VerticalAlignment);
            AddComboField("SortOrder", ["LayoutOrder", "Name"], node.SortOrder);
            AddTextField("Padding", GuiUDimText([node.ListPaddingScale, node.LayoutSpacing]));
        }
        if (node.Type == "UIFlexItem")
        {
            propertyTarget = AddPropertySection("Flex");
            AddComboField("FlexMode", ["None", "Grow", "Shrink", "Fill", "Custom"], node.FlexMode);
            AddTextField("GrowRatio", node.GrowRatio.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("ShrinkRatio", node.ShrinkRatio.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddComboField("ItemLineAlignment", ["Automatic", "Start", "Center", "End", "Stretch"], node.ItemLineAlignment);
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
        if (node.Type is "UIAspectRatio" or "UIAspectRatioConstraint")
        {
            propertyTarget = AddPropertySection("Layout");
            AddTextField("AspectRatio", node.AspectRatio.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (node.Type == "UIAspectRatioConstraint")
            {
                AddComboField("AspectType", ["FitWithinMaxSize", "ScaleWithParentSize"], node.AspectType);
                AddComboField("DominantAxis", ["Width", "Height"], node.DominantAxis);
            }
        }
        if (node.Type == "UISizeConstraint")
        {
            propertyTarget = AddPropertySection("Size limits");
            AddTextField("MinSize", VectorText(node.MinSize));
            AddTextField("MaxSize", VectorText(node.MaxSize));
        }
        if (node.Type == "UITextSizeConstraint")
        {
            propertyTarget = AddPropertySection("Text size limits");
            AddTextField("MinTextSize", node.MinTextSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddTextField("MaxTextSize", node.MaxTextSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private bool IsGuiNode(SceneNode node)
    {
        if (project is null) return false;
        var current = node;
        while (true)
        {
            if (ReferenceEquals(current, project.Gui) || current.Type is "BillboardGui" or "SurfaceGui") return true;
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
            IsEnabled = !(property == "Parent" && (ReferenceEquals(target, project?.Workspace) || ReferenceEquals(target, project?.Players) || ReferenceEquals(target, project?.Lighting) || ReferenceEquals(target, project?.ReplicatedStorage) || ReferenceEquals(target, project?.ServerScriptService) || ReferenceEquals(target, project?.ServerStorage) || ReferenceEquals(target, project?.StarterPack) || ReferenceEquals(target, project?.StarterPlayer) || ReferenceEquals(target, project?.Gui))),
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

    private void AddClearAdorneeButton(SceneNode target)
    {
        var button = new Button { Content = "Clear Adornee", Margin = new Thickness(3, 2, 3, 2), Padding = new Thickness(5, 2, 5, 2), IsEnabled = target.Adornee.Length > 0 };
        button.Click += (_, _) =>
        {
            if (project is null) return;
            target.Adornee = string.Empty;
            renderer?.UpdateNode(target, project);
            RecordEdit();
            RenderProperties();
        };
        (propertyTarget ?? PropertyEditor).Children.Add(button);
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
        if (property is "GripPosition" or "GripRotation" && target.Type == "Tool")
        {
            var vector = ParseVector(text);
            if (vector is null)
            {
                SetStatus("Enter three comma-separated numbers.");
                box.Text = VectorText(property == "GripPosition" ? target.GripPosition : target.GripRotation);
                return;
            }
            if (property == "GripPosition") target.GripPosition = vector;
            else target.GripRotation = vector.Select(value => Math.Clamp(value, -360000, 360000)).ToArray();
            renderer?.Refresh(project);
            RecordEdit();
            return;
        }
        if (property == "ToolTip" && target.Type == "Tool") { target.ToolTip = box.Text; RecordEdit(); return; }
        if (target.Type == "VehicleSeat" && property is "MaxSpeed" or "Torque" or "TurnSpeed")
        {
            if (!float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vehicleNumber) || !float.IsFinite(vehicleNumber))
            {
                SetStatus($"Enter a valid number for {property}.");
                box.Text = (property == "MaxSpeed" ? target.VehicleSeatMaxSpeed : property == "Torque" ? target.VehicleSeatTorque : target.VehicleSeatTurnSpeed).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return;
            }
            if (property == "MaxSpeed") target.VehicleSeatMaxSpeed = Math.Clamp(vehicleNumber, 0, 10000);
            else if (property == "Torque") target.VehicleSeatTorque = Math.Clamp(vehicleNumber, 0, 1000000);
            else target.VehicleSeatTurnSpeed = Math.Clamp(vehicleNumber, 0, 1000);
            renderer?.UpdateNode(target, project);
            RecordEdit();
            return;
        }
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
        else if (property == "Value" && target.Type == "Vector2Value")
        {
            var vector = ParsePair(text);
            if (vector is null)
            {
                SetStatus("Enter two comma-separated numbers for Value.");
                box.Text = VectorText(target.Vector2Value);
                return;
            }
            target.Vector2Value = vector;
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
        if (property == "AnchorPoint" && target.Type is ("Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox"))
        {
            var values = ParsePair(text);
            if (values is null)
            {
                SetStatus("Enter two comma-separated numbers between 0 and 1.");
                box.Text = VectorText(target.AnchorPoint);
                return;
            }
            target.AnchorPoint = [Math.Clamp(values[0], 0, 1), Math.Clamp(values[1], 0, 1)];
            box.Text = VectorText(target.AnchorPoint);
        }
        else if (target.Type == "UIGridLayout" && property is ("CellSize" or "CellPadding"))
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
        else if (target.Type == "UICorner" && UICornerRadiusService.IsRadiusProperty(property))
        {
            var value = ParseGuiUDim(text);
            if (value is null)
            {
                SetStatus("Enter a pixel offset or a scale, offset pair.");
                box.Text = GuiUDimText(UICornerRadiusService.Get(target, property));
                return;
            }
            UICornerRadiusService.Set(target, property, value[0], value[1]);
            box.Text = GuiUDimText(UICornerRadiusService.Get(target, property));
        }
        else if (target.Type == "UIShadow" && property == "BlurRadius")
        {
            var value = ParseGuiUDim(text);
            if (value is null) { SetStatus("Enter a pixel offset or a scale, offset pair."); box.Text = GuiUDimText(target.ShadowBlurRadius); return; }
            target.ShadowBlurRadius = UIShadowService.NormalizeUDim(value, false);
        }
        else if (target.Type == "UIShadow" && property is ("Offset" or "Spread"))
        {
            var value = ParseGuiUDim2(text);
            if (value is null)
            {
                SetStatus("Enter two pixel offsets or scale, offset pairs for both axes.");
                box.Text = property == "Offset" ? GuiUDim2Text(target.ShadowOffset, target.ShadowOffsetScale) : GuiUDim2Text(target.ShadowSpread, target.ShadowSpreadScale);
                return;
            }
            if (property == "Offset")
            {
                target.ShadowOffsetScale = [value[0], value[2]];
                target.ShadowOffset = [value[1], value[3]];
            }
            else
            {
                target.ShadowSpreadScale = [value[0], value[2]];
                target.ShadowSpread = [value[1], value[3]];
            }
            UIShadowService.Normalize(target);
        }
        else if (target.Type == "UIListLayout" && property == "Padding")
        {
            var value = ParseGuiUDim(text);
            if (value is null)
            {
                SetStatus("Enter a pixel offset or a scale, offset pair.");
                box.Text = GuiUDimText([target.ListPaddingScale, target.LayoutSpacing]);
                return;
            }
            target.ListPaddingScale = Math.Clamp(value[0], 0, 100);
            target.LayoutSpacing = Math.Clamp(value[1], 0, 1000000);
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
        else if (property == "Size" && (SceneNodeTypes.IsSizedParticleEffect(target) || target.Type is "ParticleEmitter" or "BlurEffect"))
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
            target.EffectSize = Math.Clamp(effectSize, target.Type == "Fire" ? 2 : target.Type == "BlurEffect" ? 0 : .1f, target.Type == "Fire" ? 30 : target.Type == "BlurEffect" ? 56 : 100);
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
        else if (target.Type == "LinearVelocity" && property is ("PlaneVelocity" or "MaxPlanarAxesForce"))
        {
            var values = ParsePair(text);
            if (values is null) { SetStatus("Enter two comma-separated numbers."); box.Text = VectorText(property == "PlaneVelocity" ? target.PlaneVelocity : target.MaxPlanarAxesForce); return; }
            if (property == "PlaneVelocity") target.PlaneVelocity = values.Select(value => Math.Clamp(value, -10000, 10000)).ToArray();
            else target.MaxPlanarAxesForce = values.Select(value => Math.Clamp(value, 0, 1000000)).ToArray();
        }
        else if (target.Type == "UISizeConstraint" && property is "MinSize" or "MaxSize")
        {
            var values = ParsePair(text);
            if (values is null) { SetStatus("Enter two comma-separated pixel sizes."); box.Text = VectorText(property == "MinSize" ? target.MinSize : target.MaxSize); return; }
            if (property == "MinSize")
            {
                target.MinSize = values.Select(value => Math.Clamp(value, 0, 1000000)).ToArray();
                target.MaxSize = [Math.Max(target.MinSize[0], target.MaxSize[0]), Math.Max(target.MinSize[1], target.MaxSize[1])];
            }
            else target.MaxSize = [Math.Clamp(values[0], target.MinSize[0], 1000000), Math.Clamp(values[1], target.MinSize[1], 1000000)];
        }
        else if (property is "Position" or "Rotation" or "Size" or "Velocity" or "AssemblyAngularVelocity" or "Force" or "Location" or "TargetOffset" or "VectorVelocity" or "AngularVelocity" or "Torque" or "LineDirection" or "PrimaryTangentAxis" or "SecondaryTangentAxis" or "MaxAxesForce" or "MaxForce" or "MaxTorque"
            && !(property == "AngularVelocity" && target.Type is ("HingeConstraint" or "CylindricalConstraint"))
            && !(property == "Location" && target.Type != "BodyThrust")
            && !(property == "TargetOffset" && target.Type != "RocketPropulsion")
            && !((property is "Size" or "Velocity") && target.Type is ("PrismaticConstraint" or "CylindricalConstraint"))
            && !(property == "MaxForce" && target.Type is not ("BodyVelocity" or "BodyPosition"))
            && !(property == "MaxTorque" && target.Type is not ("BodyGyro" or "BodyAngularVelocity" or "RocketPropulsion")))
        {
            var values = ParseVector(text);
            if (values is null) { SetStatus("Enter three comma-separated numbers."); box.Text = property switch { "Position" => VectorText(target.Type == "BodyPosition" ? target.BodyPositionTarget : target.Position), "Rotation" => VectorText(target.Type == "BodyGyro" ? target.BodyGyroTargetRotation : target.Type == "AlignOrientation" ? target.AlignOrientationRotation : target.Rotation), "Velocity" => VectorText(target.Type == "BodyVelocity" ? target.BodyVelocityTarget : target.Velocity), "AssemblyAngularVelocity" => VectorText(target.AssemblyAngularVelocity), "AngularVelocity" => VectorText(target.Type == "BodyAngularVelocity" ? target.BodyAngularVelocityTarget : target.AngularVelocityTarget), "Force" => VectorText(target.Type == "BodyThrust" ? target.BodyThrustForce : target.Force), "Location" when target.Type == "BodyThrust" => VectorText(target.BodyThrustLocation), "TargetOffset" when target.Type == "RocketPropulsion" => VectorText(target.RocketPropulsionTargetOffset), "Torque" => VectorText(target.TorqueTarget), "VectorVelocity" => VectorText(target.VectorVelocity), "LineDirection" => VectorText(target.LineDirection), "PrimaryTangentAxis" => VectorText(target.PrimaryTangentAxis), "SecondaryTangentAxis" => VectorText(target.SecondaryTangentAxis), "MaxAxesForce" => VectorText(target.MaxAxesForce), "MaxForce" when target.Type is "BodyVelocity" or "BodyPosition" => VectorText(target.Type == "BodyVelocity" ? target.BodyVelocityMaxForce : target.BodyPositionMaxForce), "MaxTorque" when target.Type == "BodyGyro" => VectorText(target.BodyGyroMaxTorque), "MaxTorque" when target.Type == "BodyAngularVelocity" => VectorText(target.BodyAngularVelocityMaxTorque), "MaxTorque" when target.Type == "RocketPropulsion" => VectorText(target.RocketPropulsionMaxTorque), _ => VectorText(target.Size) }; return; }
            if (property == "Position" && target.Type == "BodyPosition") target.BodyPositionTarget = values.Select(value => Math.Clamp(value, -1000000, 1000000)).ToArray();
            else if (property == "Position") target.Position = values;
            if (property == "Rotation" && target.Type == "BodyGyro") target.BodyGyroTargetRotation = values.Select(value => Math.Clamp(value, -360000, 360000)).ToArray();
            else if (property == "Rotation" && target.Type == "AlignOrientation") target.AlignOrientationRotation = values.Select(value => Math.Clamp(value, -360000, 360000)).ToArray();
            else if (property == "Rotation") target.Rotation = values;
            if (property == "Size") target.Size = values.Select(value => Math.Max(.01f, Math.Abs(value))).ToArray();
            if (property == "Velocity" && SceneNodeTypes.IsBasePart(target)) target.Velocity = values;
            if (property == "Velocity" && target.Type == "BodyVelocity") target.BodyVelocityTarget = values.Select(value => Math.Clamp(value, -10000, 10000)).ToArray();
            if (property == "MaxForce" && target.Type == "BodyVelocity") target.BodyVelocityMaxForce = values.Select(value => Math.Clamp(value, 0, 1000000)).ToArray();
            if (property == "MaxForce" && target.Type == "BodyPosition") target.BodyPositionMaxForce = values.Select(value => Math.Clamp(value, 0, 1000000)).ToArray();
            if (property == "MaxTorque" && target.Type == "BodyGyro") target.BodyGyroMaxTorque = values.Select(value => Math.Clamp(value, 0, 1000000)).ToArray();
            if (property == "AngularVelocity" && target.Type == "BodyAngularVelocity") target.BodyAngularVelocityTarget = values.Select(value => Math.Clamp(value, -10000, 10000)).ToArray();
            if (property == "MaxTorque" && target.Type == "BodyAngularVelocity") target.BodyAngularVelocityMaxTorque = values.Select(value => Math.Clamp(value, 0, 1000000)).ToArray();
            if (property == "TargetOffset" && target.Type == "RocketPropulsion")
            {
                var rocketOffset = values.Select(value => Math.Clamp(value, -1000000, 1000000)).ToArray();
                if (!target.RocketPropulsionTargetOffset.SequenceEqual(rocketOffset)) target.RocketPropulsionReachedTarget = false;
                target.RocketPropulsionTargetOffset = rocketOffset;
            }
            if (property == "MaxTorque" && target.Type == "RocketPropulsion") target.RocketPropulsionMaxTorque = values.Select(value => Math.Clamp(value, 0, 1000000)).ToArray();
            if (property == "AssemblyAngularVelocity" && SceneNodeTypes.IsBasePart(target)) target.AssemblyAngularVelocity = values.Select(value => Math.Clamp(value, -1000, 1000)).ToArray();
            if (property == "AngularVelocity" && target.Type == "AngularVelocity") target.AngularVelocityTarget = values.Select(value => Math.Clamp(value, -1000, 1000)).ToArray();
            if (property == "Force" && target.Type is ("VectorForce" or "BodyForce")) target.Force = values.Select(value => Math.Clamp(value, -1000000, 1000000)).ToArray();
            if (property == "Force" && target.Type == "BodyThrust") target.BodyThrustForce = values.Select(value => Math.Clamp(value, -1000000, 1000000)).ToArray();
            if (property == "Location" && target.Type == "BodyThrust") target.BodyThrustLocation = values.Select(value => Math.Clamp(value, -1000000, 1000000)).ToArray();
            if (property == "Torque" && target.Type == "Torque") target.TorqueTarget = values.Select(value => Math.Clamp(value, -1000000, 1000000)).ToArray();
            if (target.Type == "LinearVelocity")
            {
                if (property == "MaxAxesForce") target.MaxAxesForce = values.Select(value => Math.Clamp(value, 0, 1000000)).ToArray();
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
            if (SceneNodeTypes.IsSurfaceAppearance(target)) target.SurfaceAppearanceColor = text;
            else target.Color = text;
            if (target.Type is "ParticleEmitter" or "Trail") target.ColorSequence = [];
        }
        else if (property == "TintColor" && target.Type == "ColorCorrectionEffect")
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(text, "^#[0-9A-Fa-f]{6}$"))
            {
                SetStatus("Enter TintColor as #RRGGBB.");
                box.Text = target.TintColor;
                return;
            }
            target.TintColor = text.ToUpperInvariant();
        }
        else if (property == "EmissiveTint" && SceneNodeTypes.IsSurfaceAppearance(target)) target.SurfaceAppearanceEmissiveTint = text;
        else if (property == "Color3" && target.Type is ("SelectionBox" or "SelectionSphere")) target.Color = text;
        else if (property == "SurfaceColor3" && target.Type is ("SelectionBox" or "SelectionSphere")) target.SurfaceColor3 = text;
        else if (property == "SparkleColor" && target.Type == "Sparkles") target.Color = text;
        else if (property == "SecondaryColor" && target.Type == "Fire") target.SecondaryColor = text;
        else if (property == "Decay" && target.Type == "Atmosphere")
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(text, "^#[0-9A-Fa-f]{6}$"))
            {
                SetStatus("Enter Decay as #RRGGBB.");
                box.Text = target.AtmosphereDecay;
                return;
            }
            target.AtmosphereDecay = text.ToUpperInvariant();
        }
        else if (property == "TeamColor" && target.Type is ("Team" or "SpawnLocation"))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(text, "^#[0-9A-Fa-f]{6}$"))
            {
                SetStatus("Enter TeamColor as #RRGGBB.");
                box.Text = target.TeamColor;
                return;
            }
            target.TeamColor = text.ToUpperInvariant();
        }
        else if (property == "Duration" && target.Type == "SpawnLocation")
        {
            if (!float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var spawnDuration) || !float.IsFinite(spawnDuration))
            {
                SetStatus("Enter Duration as a finite number of seconds.");
                box.Text = target.Duration.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return;
            }
            target.Duration = Math.Clamp(spawnDuration, 0, 10000);
        }
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
        else if (target.Type == "UITextSizeConstraint" && property is "MinTextSize" or "MaxTextSize"
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var textLimit)
            && float.IsFinite(textLimit))
        {
            if (property == "MinTextSize")
            {
                target.MinTextSize = Math.Clamp(textLimit, 1, 1000);
                target.MaxTextSize = Math.Max(target.MaxTextSize, target.MinTextSize);
            }
            else target.MaxTextSize = Math.Clamp(textLimit, target.MinTextSize, 1000);
        }
        else if (property is "BackgroundTransparency" or "TextTransparency" or "ImageTransparency"
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var guiTransparency)
            && float.IsFinite(guiTransparency))
        {
            var value = Math.Clamp(guiTransparency, 0, 1);
            if (property == "BackgroundTransparency") target.BackgroundTransparency = value;
            else if (property == "TextTransparency") target.TextTransparency = value;
            else target.ImageTransparency = value;
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
        else if (property == "SurfaceTransparency" && target.Type is ("SelectionBox" or "SelectionSphere") && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var surfaceTransparency) && float.IsFinite(surfaceTransparency)) target.SurfaceTransparency = Math.Clamp(surfaceTransparency, 0, 1);
        else if (property == "LineThickness" && target.Type == "SelectionBox" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lineThickness) && float.IsFinite(lineThickness)) target.LineThickness = Math.Clamp(lineThickness, 0, 10);
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
        else if (target.Type == "LineForce" && property is ("Magnitude" or "MaxForce")
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lineForceNumber) && float.IsFinite(lineForceNumber))
        {
            if (property == "Magnitude") target.LineForceMagnitude = Math.Clamp(lineForceNumber, 0, 1000000);
            else target.LineForceMaxForce = Math.Clamp(lineForceNumber, 0, 1000000);
        }
        else if (target.Type == "AlignPosition" && property is ("Responsiveness" or "MaxVelocity" or "MaxForce")
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var alignmentNumber) && float.IsFinite(alignmentNumber))
        {
            if (property == "Responsiveness") target.Responsiveness = Math.Clamp(alignmentNumber, 0, 200);
            else if (property == "MaxVelocity") target.MaxVelocity = Math.Clamp(alignmentNumber, 0, 10000);
            else target.MaxForce = Math.Clamp(alignmentNumber, 0, 1000000);
        }
        else if (target.Type == "AlignOrientation" && property is ("Responsiveness" or "MaxAngularVelocity" or "MaxTorque")
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var orientationNumber) && float.IsFinite(orientationNumber))
        {
            if (property == "Responsiveness") target.Responsiveness = Math.Clamp(orientationNumber, 0, 200);
            else if (property == "MaxAngularVelocity") target.MaxAngularVelocity = Math.Clamp(orientationNumber, 0, 10000);
            else target.MaxTorque = Math.Clamp(orientationNumber, 0, 1000000);
        }
        else if (property == "MaxTorque" && target.Type == "AngularVelocity" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var angularTorque) && float.IsFinite(angularTorque)) target.MaxTorque = Math.Clamp(angularTorque, 0, 1000000);
        else if (target.Type == "SpringConstraint" && property is ("FreeLength" or "Stiffness" or "Damping" or "MaxForce" or "MinLength" or "MaxLength")
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var springNumber) && float.IsFinite(springNumber))
        {
            if (property == "FreeLength") target.SpringFreeLength = Math.Clamp(springNumber, 0, 10000);
            else if (property == "Stiffness") target.SpringStiffness = Math.Clamp(springNumber, 0, 100000);
            else if (property == "Damping") target.SpringDamping = Math.Clamp(springNumber, 0, 10000);
            else if (property == "MaxForce") target.SpringMaxForce = Math.Clamp(springNumber, 0, 1000000);
            else if (property == "MinLength") target.SpringMinLength = Math.Clamp(springNumber, 0, target.SpringMaxLength);
            else target.SpringMaxLength = Math.Clamp(springNumber, target.SpringMinLength, 10000);
        }
        else if (target.Type == "TorsionSpringConstraint" && property is ("Stiffness" or "Damping" or "MaxTorque" or "MaxAngle" or "Restitution" or "Coils" or "Radius")
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var torsionNumber) && float.IsFinite(torsionNumber))
        {
            if (property == "Stiffness") target.TorsionSpringStiffness = Math.Clamp(torsionNumber, 0, 100000);
            else if (property == "Damping") target.TorsionSpringDamping = Math.Clamp(torsionNumber, 0, 100000);
            else if (property == "MaxTorque") target.TorsionSpringMaxTorque = Math.Clamp(torsionNumber, 0, 1000000);
            else if (property == "MaxAngle") target.TorsionSpringMaxAngle = Math.Clamp(torsionNumber, 0, 180);
            else if (property == "Restitution") target.TorsionSpringRestitution = Math.Clamp(torsionNumber, 0, 1);
            else if (property == "Coils") target.TorsionSpringCoils = Math.Clamp(torsionNumber, 0, 8);
            else target.TorsionSpringRadius = Math.Clamp(torsionNumber, 0, 100);
        }
        else if (target.Type == "RopeConstraint" && property is ("Length" or "Restitution" or "WinchTarget" or "WinchSpeed" or "WinchResponsiveness" or "WinchForce")
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ropeNumber) && float.IsFinite(ropeNumber))
        {
            if (property == "Length") target.RopeLength = Math.Clamp(ropeNumber, 0, 10000);
            else if (property == "Restitution") target.RopeRestitution = Math.Clamp(ropeNumber, 0, 1);
            else if (property == "WinchTarget") target.RopeWinchTarget = Math.Clamp(ropeNumber, 0, 10000);
            else if (property == "WinchSpeed") target.RopeWinchSpeed = Math.Clamp(ropeNumber, 0, 10000);
            else if (property == "WinchResponsiveness") target.RopeWinchResponsiveness = Math.Clamp(ropeNumber, 0, 200);
            else target.RopeWinchForce = Math.Clamp(ropeNumber, 0, 1000000);
        }
        else if (target.Type == "RodConstraint" && property is ("Length" or "LimitAngle0" or "LimitAngle1" or "Thickness")
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rodValue) && float.IsFinite(rodValue))
        {
            if (property == "Length") target.RodLength = Math.Clamp(rodValue, 0, 10000);
            else if (property == "LimitAngle0") target.RodLimitAngle0 = Math.Clamp(rodValue, 0, 180);
            else if (property == "LimitAngle1") target.RodLimitAngle1 = Math.Clamp(rodValue, 0, 180);
            else target.RodThickness = Math.Clamp(rodValue, 0, 100);
        }
        else if (target.Type == "BallSocketConstraint" && property is ("UpperAngle" or "TwistLowerAngle" or "TwistUpperAngle" or "Radius" or "Restitution" or "MaxFrictionTorque")
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ballSocketAngle) && float.IsFinite(ballSocketAngle))
        {
            if (property == "UpperAngle") target.BallSocketUpperAngle = Math.Clamp(ballSocketAngle, 0, 180);
            else if (property == "TwistLowerAngle") target.BallSocketTwistLowerAngle = Math.Clamp(ballSocketAngle, -180, target.BallSocketTwistUpperAngle);
            else if (property == "TwistUpperAngle") target.BallSocketTwistUpperAngle = Math.Clamp(ballSocketAngle, target.BallSocketTwistLowerAngle, 180);
            else if (property == "Radius") target.BallSocketRadius = Math.Clamp(ballSocketAngle, 0, 100);
            else if (property == "Restitution") target.BallSocketRestitution = Math.Clamp(ballSocketAngle, 0, 1);
            else target.BallSocketMaxFrictionTorque = Math.Clamp(ballSocketAngle, 0, 1000000);
        }
        else if (target.Type == "UniversalConstraint" && property is ("MaxAngle" or "Radius" or "Restitution")
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var universalNumber) && float.IsFinite(universalNumber))
        {
            if (property == "MaxAngle") target.UniversalMaxAngle = Math.Clamp(universalNumber, 0, 180);
            else if (property == "Radius") target.UniversalRadius = Math.Clamp(universalNumber, 0, 100);
            else target.UniversalRestitution = Math.Clamp(universalNumber, 0, 1);
        }
        else if (target.Type == "HingeConstraint" && property is ("AngularResponsiveness" or "AngularSpeed" or "AngularVelocity" or "LowerAngle" or "MotorMaxAcceleration" or "MotorMaxTorque" or "Radius" or "Restitution" or "ServoMaxTorque" or "TargetAngle" or "UpperAngle")
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hingeValue) && float.IsFinite(hingeValue))
        {
            if (property == "AngularResponsiveness") target.HingeAngularResponsiveness = Math.Clamp(hingeValue, 0, 200);
            else if (property == "AngularSpeed") target.HingeAngularSpeed = Math.Clamp(hingeValue, 0, 1000);
            else if (property == "AngularVelocity") target.HingeAngularVelocity = Math.Clamp(hingeValue, -1000, 1000);
            else if (property == "LowerAngle") target.HingeLowerAngle = Math.Clamp(hingeValue, -180, target.HingeUpperAngle);
            else if (property == "MotorMaxAcceleration") target.HingeMotorMaxAcceleration = Math.Clamp(hingeValue, 0, 100000);
            else if (property == "MotorMaxTorque") target.HingeMotorMaxTorque = Math.Clamp(hingeValue, 0, 1000000);
            else if (property == "Radius") target.HingeRadius = Math.Clamp(hingeValue, 0, 100);
            else if (property == "Restitution") target.HingeRestitution = Math.Clamp(hingeValue, 0, 1);
            else if (property == "ServoMaxTorque") target.HingeServoMaxTorque = Math.Clamp(hingeValue, 0, 1000000);
            else if (property == "TargetAngle") target.HingeTargetAngle = Math.Clamp(hingeValue, -360000, 360000);
            else target.HingeUpperAngle = Math.Clamp(hingeValue, target.HingeLowerAngle, 180);
        }
        else if (target.Type == "CylindricalConstraint" && property is ("AngularResponsiveness" or "AngularSpeed" or "AngularVelocity" or "LowerAngle" or "MotorMaxAngularAcceleration" or "MotorMaxTorque" or "AngularRestitution" or "ServoMaxTorque" or "TargetAngle" or "UpperAngle")
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cylindricalAngularValue) && float.IsFinite(cylindricalAngularValue))
        {
            if (property == "AngularResponsiveness") target.HingeAngularResponsiveness = Math.Clamp(cylindricalAngularValue, 0, 200);
            else if (property == "AngularSpeed") target.HingeAngularSpeed = Math.Clamp(cylindricalAngularValue, 0, 1000);
            else if (property == "AngularVelocity") target.HingeAngularVelocity = Math.Clamp(cylindricalAngularValue, -1000, 1000);
            else if (property == "LowerAngle") target.HingeLowerAngle = Math.Clamp(cylindricalAngularValue, -180, target.HingeUpperAngle);
            else if (property == "MotorMaxAngularAcceleration") target.HingeMotorMaxAcceleration = Math.Clamp(cylindricalAngularValue, 0, 100000);
            else if (property == "MotorMaxTorque") target.HingeMotorMaxTorque = Math.Clamp(cylindricalAngularValue, 0, 1000000);
            else if (property == "AngularRestitution") target.HingeRestitution = Math.Clamp(cylindricalAngularValue, 0, 1);
            else if (property == "ServoMaxTorque") target.HingeServoMaxTorque = Math.Clamp(cylindricalAngularValue, 0, 1000000);
            else if (property == "TargetAngle") target.HingeTargetAngle = Math.Clamp(cylindricalAngularValue, -180, 180);
            else target.HingeUpperAngle = Math.Clamp(cylindricalAngularValue, target.HingeLowerAngle, 180);
        }
        else if (target.Type == "CylindricalConstraint" && property == "InclinationAngle"
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var inclinationAngle) && float.IsFinite(inclinationAngle))
            target.CylindricalInclinationAngle = Math.Clamp(inclinationAngle, -180, 180);
        else if (target.Type is ("PrismaticConstraint" or "CylindricalConstraint") && property is ("LinearResponsiveness" or "LowerLimit" or "MotorMaxAcceleration" or "MotorMaxForce" or "Restitution" or "ServoMaxForce" or "Size" or "Speed" or "TargetPosition" or "UpperLimit" or "Velocity")
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var prismaticValue) && float.IsFinite(prismaticValue))
        {
            if (property == "LinearResponsiveness") target.PrismaticLinearResponsiveness = Math.Clamp(prismaticValue, 0, 200);
            else if (property == "LowerLimit")
            {
                target.PrismaticLowerLimit = Math.Clamp(prismaticValue, -10000, 10000);
                if (target.PrismaticLowerLimit > target.PrismaticUpperLimit)
                    target.PrismaticUpperLimit = target.PrismaticLowerLimit;
            }
            else if (property == "MotorMaxAcceleration") target.PrismaticMotorMaxAcceleration = Math.Clamp(prismaticValue, 0, 100000);
            else if (property == "MotorMaxForce") target.PrismaticMotorMaxForce = Math.Clamp(prismaticValue, 0, 1000000);
            else if (property == "Restitution") target.PrismaticRestitution = Math.Clamp(prismaticValue, 0, 1);
            else if (property == "ServoMaxForce") target.PrismaticServoMaxForce = Math.Clamp(prismaticValue, 0, 1000000);
            else if (property == "Size") target.PrismaticSize = Math.Clamp(prismaticValue, 0, 100);
            else if (property == "Speed") target.PrismaticSpeed = Math.Clamp(prismaticValue, 0, 10000);
            else if (property == "TargetPosition") target.PrismaticTargetPosition = Math.Clamp(prismaticValue, -10000, 10000);
            else if (property == "UpperLimit")
            {
                target.PrismaticUpperLimit = Math.Clamp(prismaticValue, -10000, 10000);
                if (target.PrismaticUpperLimit < target.PrismaticLowerLimit)
                    target.PrismaticLowerLimit = target.PrismaticUpperLimit;
            }
            else target.PrismaticVelocity = Math.Clamp(prismaticValue, -10000, 10000);
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
        else if (property == "Brightness" && target.Type == "ColorCorrectionEffect" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var colorBrightness) && float.IsFinite(colorBrightness)) target.Brightness = Math.Clamp(colorBrightness, -1, 1);
        else if (property == "Brightness" && (target.Type == "Lighting" || SceneNodeTypes.IsLight(target)) && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var brightness) && float.IsFinite(brightness)) target.Brightness = Math.Clamp(brightness, 0, 8);
        else if (property == "Contrast" && target.Type == "ColorCorrectionEffect" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var colorContrast) && float.IsFinite(colorContrast)) target.ColorCorrectionContrast = Math.Clamp(colorContrast, -1, 1);
        else if (property == "Saturation" && target.Type == "ColorCorrectionEffect" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var colorSaturation) && float.IsFinite(colorSaturation)) target.ColorCorrectionSaturation = Math.Clamp(colorSaturation, -1, 1);
        else if (property == "EmissiveStrength" && SceneNodeTypes.IsSurfaceAppearance(target) && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var emissiveStrength) && float.IsFinite(emissiveStrength)) target.SurfaceAppearanceEmissiveStrength = Math.Clamp(emissiveStrength, 0, 100);
        else if (property == "ClockTime" && target.Type == "Lighting" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var clockTime) && float.IsFinite(clockTime)) target.ClockTime = Math.Clamp(clockTime, 0, 24);
        else if (target.Type == "Atmosphere" && property is ("Density" or "Haze" or "Glare" or "Offset")
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var atmosphereValue) && float.IsFinite(atmosphereValue))
        {
            if (property == "Density") target.Density = Math.Clamp(atmosphereValue, 0, 1);
            else if (property == "Haze") target.Haze = Math.Clamp(atmosphereValue, 0, 10);
            else if (property == "Glare") target.Glare = Math.Clamp(atmosphereValue, 0, 10);
            else target.Offset = Math.Clamp(atmosphereValue, -1, 1);
        }
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
        else if (property == "Range" && SceneNodeTypes.IsLight(target) && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var range) && float.IsFinite(range)) target.Range = Math.Clamp(range, 1, 1000);
        else if (property == "Angle" && target.Type is ("SpotLight" or "SurfaceLight") && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var angle) && float.IsFinite(angle)) target.Angle = Math.Clamp(angle, 1, 175);
        else if (property == "MaxActivationDistance" && target.Type is ("ProximityPrompt" or "ClickDetector") && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var maxActivationDistance) && float.IsFinite(maxActivationDistance)) target.MaxActivationDistance = Math.Clamp(maxActivationDistance, 1, 100);
        else if (property == "HoldDuration" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var holdDuration) && float.IsFinite(holdDuration)) target.HoldDuration = Math.Clamp(holdDuration, 0, 60);
        else if (property == "P" && target.Type == "BodyVelocity" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bodyVelocityP) && float.IsFinite(bodyVelocityP)) target.BodyVelocityP = Math.Clamp(bodyVelocityP, 0, 1000000);
        else if (property == "P" && target.Type == "BodyPosition" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bodyPositionP) && float.IsFinite(bodyPositionP)) target.BodyPositionP = Math.Clamp(bodyPositionP, 0, 1000000);
        else if (property == "D" && target.Type == "BodyPosition" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bodyPositionD) && float.IsFinite(bodyPositionD)) target.BodyPositionD = Math.Clamp(bodyPositionD, 0, 1000000);
        else if (property == "P" && target.Type == "BodyGyro" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bodyGyroP) && float.IsFinite(bodyGyroP)) target.BodyGyroP = Math.Clamp(bodyGyroP, 0, 1000000);
        else if (property == "D" && target.Type == "BodyGyro" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bodyGyroD) && float.IsFinite(bodyGyroD)) target.BodyGyroD = Math.Clamp(bodyGyroD, 0, 1000000);
        else if (property == "P" && target.Type == "BodyAngularVelocity" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bodyAngularVelocityP) && float.IsFinite(bodyAngularVelocityP)) target.BodyAngularVelocityP = Math.Clamp(bodyAngularVelocityP, 0, 1000000);
        else if (target.Type == "RocketPropulsion" && property == "TargetRadius" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rocketTargetRadius) && float.IsFinite(rocketTargetRadius)) target.RocketPropulsionTargetRadius = Math.Clamp(rocketTargetRadius, 0, 1000000);
        else if (target.Type == "RocketPropulsion" && property == "CartoonFactor" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rocketCartoonFactor) && float.IsFinite(rocketCartoonFactor)) target.RocketPropulsionCartoonFactor = Math.Clamp(rocketCartoonFactor, 0, 1);
        else if (target.Type == "RocketPropulsion" && property == "MaxSpeed" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rocketMaxSpeed) && float.IsFinite(rocketMaxSpeed)) target.RocketPropulsionMaxSpeed = Math.Clamp(rocketMaxSpeed, 0, 10000);
        else if (target.Type == "RocketPropulsion" && property == "MaxThrust" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rocketMaxThrust) && float.IsFinite(rocketMaxThrust)) target.RocketPropulsionMaxThrust = Math.Clamp(rocketMaxThrust, 0, 1000000);
        else if (target.Type == "RocketPropulsion" && property == "ThrustD" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rocketThrustD) && float.IsFinite(rocketThrustD)) target.RocketPropulsionThrustD = Math.Clamp(rocketThrustD, 0, 1000000);
        else if (target.Type == "RocketPropulsion" && property == "ThrustP" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rocketThrustP) && float.IsFinite(rocketThrustP)) target.RocketPropulsionThrustP = Math.Clamp(rocketThrustP, 0, 1000000);
        else if (target.Type == "RocketPropulsion" && property == "TurnD" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rocketTurnD) && float.IsFinite(rocketTurnD)) target.RocketPropulsionTurnD = Math.Clamp(rocketTurnD, 0, 1000000);
        else if (target.Type == "RocketPropulsion" && property == "TurnP" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rocketTurnP) && float.IsFinite(rocketTurnP)) target.RocketPropulsionTurnP = Math.Clamp(rocketTurnP, 0, 1000000);
        else if (property == "MaxDistance" && (target.Type is "BillboardGui" or "SurfaceGui") && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var billboardMaxDistance) && float.IsFinite(billboardMaxDistance)) target.MaxDistance = Math.Clamp(billboardMaxDistance, 0, 100000);
        else if (property == "PixelsPerStud" && target.Type == "SurfaceGui" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pixelsPerStud) && float.IsFinite(pixelsPerStud)) target.PixelsPerStud = Math.Clamp(pixelsPerStud, 1, 1000);
        else if (property == "Volume" && target.Type == "Sound" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var volume) && float.IsFinite(volume)) target.Volume = Math.Clamp(volume, 0, 1);
        else if (property == "Volume" && target.Type == "SoundGroup" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var soundGroupVolume) && float.IsFinite(soundGroupVolume)) target.SoundGroupVolume = Math.Clamp(soundGroupVolume, 0, 1);
        else if (property == "Volume" && target.Type == "SoundService" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var soundServiceVolume) && float.IsFinite(soundServiceVolume)) target.SoundServiceVolume = Math.Clamp(soundServiceVolume, 0, 1);
        else if (property == "PlaybackSpeed" && target.Type == "Sound" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var playbackSpeed) && float.IsFinite(playbackSpeed)) target.PlaybackSpeed = Math.Clamp(playbackSpeed, 0, 20);
        else if (property == "Octave" && target.Type == "PitchShiftSoundEffect" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var octave) && float.IsFinite(octave)) target.PitchShiftOctave = Math.Clamp(octave, .5f, 2);
        else if (property == "Delay" && target.Type == "EchoSoundEffect" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var echoDelay) && float.IsFinite(echoDelay)) target.EchoDelay = Math.Clamp(echoDelay, .01f, 5);
        else if (property == "Feedback" && target.Type == "EchoSoundEffect" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var echoFeedback) && float.IsFinite(echoFeedback)) target.EchoFeedback = Math.Clamp(echoFeedback, 0, 1);
        else if (property == "DryLevel" && target.Type == "EchoSoundEffect" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var echoDryLevel) && float.IsFinite(echoDryLevel)) target.EchoDryLevel = Math.Clamp(echoDryLevel, -80, 10);
        else if (property == "WetLevel" && target.Type == "EchoSoundEffect" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var echoWetLevel) && float.IsFinite(echoWetLevel)) target.EchoWetLevel = Math.Clamp(echoWetLevel, -80, 10);
        else if (property == "Depth" && target.Type == "FlangeSoundEffect" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var flangeDepth) && float.IsFinite(flangeDepth)) target.FlangeDepth = Math.Clamp(flangeDepth, .01f, 1);
        else if (property == "Mix" && target.Type == "FlangeSoundEffect" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var flangeMix) && float.IsFinite(flangeMix)) target.FlangeMix = Math.Clamp(flangeMix, 0, 1);
        else if (property == "Rate" && target.Type == "FlangeSoundEffect" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var flangeRate) && float.IsFinite(flangeRate)) target.FlangeRate = Math.Clamp(flangeRate, 0, 20);
        else if (property == "Depth" && target.Type == "TremoloSoundEffect" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tremoloDepth) && float.IsFinite(tremoloDepth)) target.TremoloDepth = Math.Clamp(tremoloDepth, 0, 1);
        else if (property == "Duty" && target.Type == "TremoloSoundEffect" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tremoloDuty) && float.IsFinite(tremoloDuty)) target.TremoloDuty = Math.Clamp(tremoloDuty, 0, 1);
        else if (property == "Frequency" && target.Type == "TremoloSoundEffect" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tremoloFrequency) && float.IsFinite(tremoloFrequency)) target.TremoloFrequency = Math.Clamp(tremoloFrequency, .1f, 20);
        else if (property == "DecayTime" && target.Type == "ReverbSoundEffect" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var reverbDecay) && float.IsFinite(reverbDecay)) target.ReverbDecayTime = Math.Clamp(reverbDecay, .1f, 20);
        else if (property == "Density" && target.Type == "ReverbSoundEffect" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var reverbDensity) && float.IsFinite(reverbDensity)) target.ReverbDensity = Math.Clamp(reverbDensity, 0, 1);
        else if (property == "Diffusion" && target.Type == "ReverbSoundEffect" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var reverbDiffusion) && float.IsFinite(reverbDiffusion)) target.ReverbDiffusion = Math.Clamp(reverbDiffusion, 0, 1);
        else if (property == "DryLevel" && target.Type == "ReverbSoundEffect" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var reverbDry) && float.IsFinite(reverbDry)) target.ReverbDryLevel = Math.Clamp(reverbDry, -80, 10);
        else if (property == "WetLevel" && target.Type == "ReverbSoundEffect" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var reverbWet) && float.IsFinite(reverbWet)) target.ReverbWetLevel = Math.Clamp(reverbWet, -80, 10);
        else if (property == "RollOffMinDistance" && target.Type == "Sound" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minDistance) && float.IsFinite(minDistance))
        {
            target.RollOffMinDistance = Math.Clamp(minDistance, 0, 1000);
            target.RollOffMaxDistance = Math.Max(target.RollOffMaxDistance, target.RollOffMinDistance + .01f);
        }
        else if (property == "RollOffMaxDistance" && target.Type == "Sound" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var maxDistance) && float.IsFinite(maxDistance)) target.RollOffMaxDistance = Math.Clamp(maxDistance, target.RollOffMinDistance + .01f, 10000);
        else if (property == "Asset") target.Asset = text;
        else if (property == "ColorMap" && SceneNodeTypes.IsSurfaceAppearance(target)) target.SurfaceAppearanceColorMap = text;
        else if (property == "NormalMap" && SceneNodeTypes.IsSurfaceAppearance(target)) target.SurfaceAppearanceNormalMap = text;
        else if (property == "RoughnessMap" && SceneNodeTypes.IsSurfaceAppearance(target)) target.SurfaceAppearanceRoughnessMap = text;
        else if (property == "MetalnessMap" && SceneNodeTypes.IsSurfaceAppearance(target)) target.SurfaceAppearanceMetalnessMap = text;
        else if (property == "EmissiveMaskContent" && SceneNodeTypes.IsSurfaceAppearance(target)) target.SurfaceAppearanceEmissiveMask = text;
        else if (property == "ActionText") target.ActionText = text;
        else if (property == "ObjectText") target.ObjectText = text;
        else if (property == "Texture" && target.Type is ("Part" or "ParticleEmitter" or "Trail")) target.Texture = text;
        else if (property == "Text") target.Text = text;
        else if (property == "PlaceholderText" && target.Type == "TextBox") target.PlaceholderText = text;
        else if (property == "TextColor") target.TextColor = text;
        else if (property == "StrokeColor") target.StrokeColor = text;
        else if (property == "LayoutSpacing" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var spacing) && float.IsFinite(spacing)) target.LayoutSpacing = Math.Clamp(spacing, 0, 1000000);
        else if (property == "GrowRatio" && target.Type == "UIFlexItem" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var growRatio) && float.IsFinite(growRatio)) target.GrowRatio = Math.Clamp(growRatio, 0, 1000);
        else if (property == "ShrinkRatio" && target.Type == "UIFlexItem" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var shrinkRatio) && float.IsFinite(shrinkRatio)) target.ShrinkRatio = Math.Clamp(shrinkRatio, 0, 1000);
        else if (property == "TableColumns" && int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var tableColumns)) target.TableColumns = Math.Clamp(tableColumns, 1, 24);
        else if (property == "FillDirectionMaxCells" && target.Type == "UIGridLayout" && int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var maxCells)) target.FillDirectionMaxCells = Math.Clamp(maxCells, 0, 1000);
        else if (property == "ZIndex" && IsGuiNode(target) && int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var zIndex)) target.ZIndex = target.Type == "UIShadow" ? Math.Min(0, zIndex) : zIndex;
        else if (property == "DisplayOrder" && target.Type == "ScreenGui" && int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var displayOrder)) target.DisplayOrder = displayOrder;
        else if (property == "LayoutOrder" && IsGuiNode(target) && int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var layoutOrder)) target.LayoutOrder = layoutOrder;
        else if (property == "AspectRatio" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var aspect) && float.IsFinite(aspect)) target.AspectRatio = Math.Clamp(aspect, .001f, 1000);
        else if (property == "StrokeThickness" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var strokeThickness)) target.StrokeThickness = Math.Max(0, strokeThickness);
        else if (property == "TextSize" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var textSize) && float.IsFinite(textSize)) target.TextSize = Math.Clamp(textSize, 1, 200);
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
        else if (property == "Name" && target.Type is not ("Workspace" or "Players" or "Teams" or "Lighting" or "SoundService" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" or "StarterPlayer" or "Terrain" or "Gui" or "StarterGui") && !string.IsNullOrWhiteSpace(text))
        {
            target.Name = text;
            if (target.Type is "Script" or "LocalScript" or "ModuleScript")
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
        else if (target.Type == "UICorner" && UICornerRadiusService.IsRadiusProperty(property)) RenderProperties();
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
            "BackgroundTransparency" => node.BackgroundTransparency,
            "TextTransparency" => node.TextTransparency,
            "ImageTransparency" => node.ImageTransparency,
            "MinTextSize" => node.MinTextSize,
            "MaxTextSize" => node.MaxTextSize,
            "StudsPerTileU" => node.StudsPerTileU,
            "StudsPerTileV" => node.StudsPerTileV,
            "OffsetStudsU" => node.OffsetStudsU,
            "OffsetStudsV" => node.OffsetStudsV,
            "FillTransparency" => node.FillTransparency,
            "OutlineTransparency" => node.OutlineTransparency,
            "SurfaceTransparency" => node.Type is ("SelectionBox" or "SelectionSphere") ? node.SurfaceTransparency : (float?)null,
            "LineThickness" => node.Type == "SelectionBox" ? node.LineThickness : (float?)null,
            "EmissiveStrength" => SceneNodeTypes.IsSurfaceAppearance(node) ? node.SurfaceAppearanceEmissiveStrength : (float?)null,
            "Gravity" => node.Gravity,
            "ClockTime" => node.ClockTime,
            "Health" => node.Health,
            "MaxHealth" => node.MaxHealth,
            "WalkSpeed" => node.WalkSpeed,
            "JumpPower" => node.JumpPower,
            "Brightness" => node.Type is "Lighting" or "ColorCorrectionEffect" || SceneNodeTypes.IsLight(node) ? node.Brightness : (float?)null,
            "Contrast" => node.Type == "ColorCorrectionEffect" ? node.ColorCorrectionContrast : (float?)null,
            "Saturation" => node.Type == "ColorCorrectionEffect" ? node.ColorCorrectionSaturation : (float?)null,
            "Range" => SceneNodeTypes.IsLight(node) ? node.Range : (float?)null,
            "Magnitude" => node.Type == "LineForce" ? node.LineForceMagnitude : (float?)null,
            "Size" => node.Type is "PrismaticConstraint" or "CylindricalConstraint" ? node.PrismaticSize : SceneNodeTypes.IsSizedParticleEffect(node) || node.Type is "ParticleEmitter" or "BlurEffect" ? node.EffectSize : (float?)null,
            "Velocity" => node.Type is "PrismaticConstraint" or "CylindricalConstraint" ? node.PrismaticVelocity : (float?)null,
            "Speed" => node.Type is "PrismaticConstraint" or "CylindricalConstraint" ? node.PrismaticSpeed : (float?)null,
            "Rate" => node.Type == "ParticleEmitter" ? node.Rate : node.Type == "FlangeSoundEffect" ? node.FlangeRate : (float?)null,
            "DecayTime" => node.Type == "ReverbSoundEffect" ? node.ReverbDecayTime : (float?)null,
            "Density" => node.Type == "ReverbSoundEffect" ? node.ReverbDensity : (float?)null,
            "Diffusion" => node.Type == "ReverbSoundEffect" ? node.ReverbDiffusion : (float?)null,
            "TextureLength" => node.Type == "Trail" ? node.TextureLength : (float?)null,
            "LineVelocity" => node.Type == "LinearVelocity" ? node.LineVelocity : (float?)null,
            "MaxForce" => node.Type == "LineForce" ? node.LineForceMaxForce : node.Type == "SpringConstraint" ? node.SpringMaxForce : node.Type is "LinearVelocity" or "AlignPosition" ? node.MaxForce : (float?)null,
            "FreeLength" => node.Type == "SpringConstraint" ? node.SpringFreeLength : (float?)null,
            "Stiffness" => node.Type == "SpringConstraint" ? node.SpringStiffness : node.Type == "TorsionSpringConstraint" ? node.TorsionSpringStiffness : (float?)null,
            "Damping" => node.Type == "SpringConstraint" ? node.SpringDamping : node.Type == "TorsionSpringConstraint" ? node.TorsionSpringDamping : (float?)null,
            "MinLength" => node.Type == "SpringConstraint" ? node.SpringMinLength : (float?)null,
            "MaxLength" => node.Type == "SpringConstraint" ? node.SpringMaxLength : (float?)null,
            "MaxAngle" => node.Type == "TorsionSpringConstraint" ? node.TorsionSpringMaxAngle : node.Type == "UniversalConstraint" ? node.UniversalMaxAngle : (float?)null,
            "Coils" => node.Type == "TorsionSpringConstraint" ? node.TorsionSpringCoils : (float?)null,
            "Length" => node.Type == "RopeConstraint" ? node.RopeLength : (float?)null,
            "CurrentDistance" => node.Type == "RodConstraint" ? node.RodCurrentDistance : (float?)null,
            "LimitAngle0" => node.Type == "RodConstraint" ? node.RodLimitAngle0 : (float?)null,
            "LimitAngle1" => node.Type == "RodConstraint" ? node.RodLimitAngle1 : (float?)null,
            "Thickness" => node.Type == "RodConstraint" ? node.RodThickness : (float?)null,
            "Restitution" => node.Type == "RopeConstraint" ? node.RopeRestitution : node.Type == "BallSocketConstraint" ? node.BallSocketRestitution : node.Type == "HingeConstraint" ? node.HingeRestitution : node.Type == "TorsionSpringConstraint" ? node.TorsionSpringRestitution : node.Type == "UniversalConstraint" ? node.UniversalRestitution : node.Type is "PrismaticConstraint" or "CylindricalConstraint" ? node.PrismaticRestitution : (float?)null,
            "AngularRestitution" => node.Type == "CylindricalConstraint" ? node.HingeRestitution : (float?)null,
            "WinchTarget" => node.Type == "RopeConstraint" ? node.RopeWinchTarget : (float?)null,
            "WinchSpeed" => node.Type == "RopeConstraint" ? node.RopeWinchSpeed : (float?)null,
            "WinchResponsiveness" => node.Type == "RopeConstraint" ? node.RopeWinchResponsiveness : (float?)null,
            "WinchForce" => node.Type == "RopeConstraint" ? node.RopeWinchForce : (float?)null,
            "UpperAngle" => node.Type == "BallSocketConstraint" ? node.BallSocketUpperAngle : node.Type is "HingeConstraint" or "CylindricalConstraint" ? node.HingeUpperAngle : (float?)null,
            "TwistLowerAngle" => node.Type == "BallSocketConstraint" ? node.BallSocketTwistLowerAngle : (float?)null,
            "TwistUpperAngle" => node.Type == "BallSocketConstraint" ? node.BallSocketTwistUpperAngle : (float?)null,
            "Radius" => node.Type == "BallSocketConstraint" ? node.BallSocketRadius : node.Type == "TorsionSpringConstraint" ? node.TorsionSpringRadius : node.Type == "UniversalConstraint" ? node.UniversalRadius : node.Type is "HingeConstraint" or "CylindricalConstraint" ? node.HingeRadius : (float?)null,
            "MaxFrictionTorque" => node.Type == "BallSocketConstraint" ? node.BallSocketMaxFrictionTorque : (float?)null,
            "AngularResponsiveness" => node.Type is "HingeConstraint" or "CylindricalConstraint" ? node.HingeAngularResponsiveness : (float?)null,
            "AngularSpeed" => node.Type is "HingeConstraint" or "CylindricalConstraint" ? node.HingeAngularSpeed : (float?)null,
            "AngularVelocity" => node.Type is "HingeConstraint" or "CylindricalConstraint" ? node.HingeAngularVelocity : (float?)null,
            "CurrentAngle" => node.Type == "TorsionSpringConstraint" ? node.TorsionSpringCurrentAngle : node.Type is "HingeConstraint" or "CylindricalConstraint" ? node.HingeCurrentAngle : (float?)null,
            "LowerAngle" => node.Type is "HingeConstraint" or "CylindricalConstraint" ? node.HingeLowerAngle : (float?)null,
            "MotorMaxAcceleration" => node.Type is "PrismaticConstraint" or "CylindricalConstraint" ? node.PrismaticMotorMaxAcceleration : node.Type == "HingeConstraint" ? node.HingeMotorMaxAcceleration : (float?)null,
            "MotorMaxAngularAcceleration" => node.Type == "CylindricalConstraint" ? node.HingeMotorMaxAcceleration : (float?)null,
            "MotorMaxTorque" => node.Type is "HingeConstraint" or "CylindricalConstraint" ? node.HingeMotorMaxTorque : (float?)null,
            "ServoMaxTorque" => node.Type is "HingeConstraint" or "CylindricalConstraint" ? node.HingeServoMaxTorque : (float?)null,
            "TargetAngle" => node.Type is "HingeConstraint" or "CylindricalConstraint" ? node.HingeTargetAngle : (float?)null,
            "InclinationAngle" => node.Type == "CylindricalConstraint" ? node.CylindricalInclinationAngle : (float?)null,
            "MaxVelocity" => node.Type == "AlignPosition" ? node.MaxVelocity : (float?)null,
            "Responsiveness" => node.Type == "AlignPosition" ? node.Responsiveness : (float?)null,
            "MaxTorque" => node.Type == "TorsionSpringConstraint" ? node.TorsionSpringMaxTorque : node.Type is "AlignOrientation" or "AngularVelocity" ? node.MaxTorque : (float?)null,
            "MaxAngularVelocity" => node.Type == "AlignOrientation" ? node.MaxAngularVelocity : (float?)null,
            "CurrentPosition" => node.Type is "PrismaticConstraint" or "CylindricalConstraint" ? node.PrismaticCurrentPosition : (float?)null,
            "LinearResponsiveness" => node.Type is "PrismaticConstraint" or "CylindricalConstraint" ? node.PrismaticLinearResponsiveness : (float?)null,
            "LowerLimit" => node.Type is "PrismaticConstraint" or "CylindricalConstraint" ? node.PrismaticLowerLimit : (float?)null,
            "MotorMaxForce" => node.Type is "PrismaticConstraint" or "CylindricalConstraint" ? node.PrismaticMotorMaxForce : (float?)null,
            "ServoMaxForce" => node.Type is "PrismaticConstraint" or "CylindricalConstraint" ? node.PrismaticServoMaxForce : (float?)null,
            "TargetPosition" => node.Type is "PrismaticConstraint" or "CylindricalConstraint" ? node.PrismaticTargetPosition : (float?)null,
            "UpperLimit" => node.Type is "PrismaticConstraint" or "CylindricalConstraint" ? node.PrismaticUpperLimit : (float?)null,
            "TimeScale" => SceneNodeTypes.IsParticleEffect(node) ? node.TimeScale : (float?)null,
            "Heat" => node.Type == "Fire" ? node.Heat : (float?)null,
            "RiseVelocity" => node.Type == "Smoke" ? node.RiseVelocity : (float?)null,
            "Opacity" => node.Type == "Smoke" ? node.Opacity : (float?)null,
            "BlastRadius" => node.BlastRadius,
            "BlastPressure" => node.BlastPressure,
            "DestroyJointRadiusPercent" => node.DestroyJointRadiusPercent,
            "Angle" => node.Type is "SpotLight" or "SurfaceLight" ? node.Angle : (float?)null,
            "MaxActivationDistance" => node.Type is "ProximityPrompt" or "ClickDetector" ? node.MaxActivationDistance : (float?)null,
            "HoldDuration" => node.HoldDuration,
            "MaxDistance" => node.Type is "BillboardGui" or "SurfaceGui" ? node.MaxDistance : (float?)null,
            "PixelsPerStud" => node.Type == "SurfaceGui" ? node.PixelsPerStud : (float?)null,
            "Volume" => node.Type == "SoundGroup" ? node.SoundGroupVolume : node.Type == "SoundService" ? node.SoundServiceVolume : node.Volume,
            "PlaybackSpeed" => node.PlaybackSpeed,
            "Octave" => node.PitchShiftOctave,
            "Delay" => node.Type == "EchoSoundEffect" ? node.EchoDelay : (float?)null,
            "Feedback" => node.Type == "EchoSoundEffect" ? node.EchoFeedback : (float?)null,
            "DryLevel" => node.Type == "EchoSoundEffect" ? node.EchoDryLevel : node.Type == "ReverbSoundEffect" ? node.ReverbDryLevel : (float?)null,
            "WetLevel" => node.Type == "EchoSoundEffect" ? node.EchoWetLevel : node.Type == "ReverbSoundEffect" ? node.ReverbWetLevel : (float?)null,
            "Depth" => node.Type == "FlangeSoundEffect" ? node.FlangeDepth : node.Type == "TremoloSoundEffect" ? node.TremoloDepth : (float?)null,
            "Mix" => node.Type == "FlangeSoundEffect" ? node.FlangeMix : (float?)null,
            "Duty" => node.Type == "TremoloSoundEffect" ? node.TremoloDuty : (float?)null,
            "Frequency" => node.Type == "TremoloSoundEffect" ? node.TremoloFrequency : (float?)null,
            "RollOffMinDistance" => node.RollOffMinDistance,
            "RollOffMaxDistance" => node.RollOffMaxDistance,
            "LayoutSpacing" => node.LayoutSpacing,
            "TableColumns" => node.TableColumns,
            "FillDirectionMaxCells" => node.FillDirectionMaxCells,
            "ZIndex" => node.ZIndex,
            "DisplayOrder" => node.Type == "ScreenGui" ? node.DisplayOrder : (int?)null,
            "LayoutOrder" => node.LayoutOrder,
            "AspectRatio" => node.AspectRatio,
            "Scale" => node.Type == "UIScale" ? node.Scale : (float?)null,
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
        if (property == "TextScaled" && target.Type is "TextLabel" or "TextButton" or "TextBox") target.TextScaled = box.IsChecked == true;
        if (property == "MultiLine" && target.Type == "TextBox") target.MultiLine = box.IsChecked == true;
        if (property == "TextEditable" && target.Type == "TextBox") target.TextEditable = box.IsChecked == true;
        if (property == "ClearTextOnFocus" && target.Type == "TextBox") target.ClearTextOnFocus = box.IsChecked == true;
        if (property == "Enabled") target.Enabled = box.IsChecked == true;
        if (property == "RequiresLineOfSight" && target.Type == "ProximityPrompt") target.RequiresLineOfSight = box.IsChecked == true;
        if (property == "ApplyAtCenterOfMass" && target.Type is ("VectorForce" or "AlignPosition")) target.ApplyAtCenterOfMass = box.IsChecked == true;
        if (property == "AlwaysOnTop" && (target.Type is "BillboardGui" or "SurfaceGui")) target.AlwaysOnTop = box.IsChecked == true;
        if (property == "Inset" && target.Type == "UIShadow") target.ShadowInset = box.IsChecked == true;
        if (property == "ShowBehindParent" && target.Type == "UIShadow") target.ShadowShowBehindParent = box.IsChecked == true;
        if (property == "FaceCamera" && target.Type == "Trail") target.FaceCamera = box.IsChecked == true;
        if (property == "InverseSquareLaw" && target.Type == "LineForce") target.LineForceInverseSquareLaw = box.IsChecked == true;
        if (property == "ReactionForceEnabled" && target.Type == "LineForce") target.LineForceReactionForceEnabled = box.IsChecked == true;
        if (property == "ApplyAtCenterOfMass" && target.Type == "LineForce") target.ApplyAtCenterOfMass = box.IsChecked == true;
        if (property == "ForceLimitsEnabled" && target.Type == "LinearVelocity") target.ForceLimitsEnabled = box.IsChecked == true;
        if (property == "LimitsEnabled" && target.Type == "SpringConstraint") target.SpringLimitsEnabled = box.IsChecked == true;
        if (property == "LimitsEnabled" && target.Type == "TorsionSpringConstraint") target.TorsionSpringLimitsEnabled = box.IsChecked == true;
        if (property == "LimitsEnabled" && target.Type == "BallSocketConstraint") target.BallSocketLimitsEnabled = box.IsChecked == true;
        if (property == "LimitsEnabled" && target.Type == "UniversalConstraint") target.UniversalLimitsEnabled = box.IsChecked == true;
        if (property == "LimitsEnabled" && target.Type == "HingeConstraint") target.HingeLimitsEnabled = box.IsChecked == true;
        if (property == "LimitsEnabled" && target.Type == "PrismaticConstraint") target.PrismaticLimitsEnabled = box.IsChecked == true;
        if (property == "LimitsEnabled" && target.Type == "CylindricalConstraint") target.PrismaticLimitsEnabled = box.IsChecked == true;
        if (property == "AngularLimitsEnabled" && target.Type == "CylindricalConstraint") target.HingeLimitsEnabled = box.IsChecked == true;
        if (property == "RotationAxisVisible" && target.Type == "CylindricalConstraint") target.CylindricalRotationAxisVisible = box.IsChecked == true;
        if (property == "TwistLimitsEnabled" && target.Type == "BallSocketConstraint") target.BallSocketTwistLimitsEnabled = box.IsChecked == true;
        if (property == "WinchEnabled" && target.Type == "RopeConstraint") target.RopeWinchEnabled = box.IsChecked == true;
        if (property == "LimitsEnabled" && target.Type == "RodConstraint") target.RodLimitsEnabled = box.IsChecked == true;
        if (property == "RigidityEnabled" && target.Type == "AlignPosition") target.RigidityEnabled = box.IsChecked == true;
        if (property == "ReactionForceEnabled" && target.Type == "AlignPosition") target.ReactionForceEnabled = box.IsChecked == true;
        if (property == "ReactionForceEnabled" && target.Type == "LinearVelocity") target.LinearVelocityReactionForceEnabled = box.IsChecked == true;
        if (property == "RigidityEnabled" && target.Type == "AlignOrientation") target.RigidityEnabled = box.IsChecked == true;
        if (property == "ReactionTorqueEnabled" && target.Type == "AlignOrientation") target.ReactionTorqueEnabled = box.IsChecked == true;
        if (property == "ReactionTorqueEnabled" && target.Type == "AngularVelocity") target.ReactionTorqueEnabled = box.IsChecked == true;
        if (property == "Looped") target.Looped = box.IsChecked == true;
        if (property == "Playing") target.Playing = box.IsChecked == true;
        if (property == "AutoManageCharacter" && target.Type == "Workspace") target.AutoManageCharacter = box.IsChecked == true;
        if (property == "RequiresHandle" && target.Type == "Tool") target.RequiresHandle = box.IsChecked == true;
        if (property == "CanBeDropped" && target.Type == "Tool") target.CanBeDropped = box.IsChecked == true;
        if (property == "ManualActivationOnly" && target.Type == "Tool") target.ManualActivationOnly = box.IsChecked == true;
        if (property == "Disabled" && target.Type == "VehicleSeat") target.VehicleSeatDisabled = box.IsChecked == true;
        if (property == "HeadsUpDisplay" && target.Type == "VehicleSeat") target.VehicleSeatHeadsUpDisplay = box.IsChecked == true;
        if (property == "AutoAssignable" && target.Type == "Team") target.AutoAssignable = box.IsChecked == true;
        if (property == "Neutral" && target.Type == "SpawnLocation") target.Neutral = box.IsChecked == true;
        if (property == "AllowTeamChangeOnTouch" && target.Type == "SpawnLocation") target.AllowTeamChangeOnTouch = box.IsChecked == true;
        if (property == "GlobalShadows" && target.Type == "Lighting") project.Settings.Shadows = box.IsChecked == true;
        if (property == "Archivable") target.Archivable = box.IsChecked == true;
        if (property == "Value" && target.Type == "BoolValue") target.BooleanValue = box.IsChecked == true;
        if (IsGuiNode(target)) RefreshGuiPreview();
        renderer?.UpdateNode(target, project);
        RecordEdit();
        if (property is "ForceLimitsEnabled" or "LimitsEnabled" or "WinchEnabled") RenderProperties();
    }

    private void PropertyCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (project is null || sender is not ComboBox combo || combo.Tag is not string property || combo.DataContext is not string targetId || FindSceneNode(targetId) is not { } target || combo.SelectedItem is not string value) return;
        if (property == "Mode" && target.Type == "Camera" && value == "Subject" && !SceneNodeTypes.IsBasePart(FindSceneNode(target.Subject)))
        {
            SetStatus("Select a BasePart for the camera subject first.");
            RenderProperties();
            return;
        }
        if (property == "Mode" && target.Type == "Camera") target.CameraMode = value;
        if (property == "Mode" && target.Type == "AlignPosition" && value is ("OneAttachment" or "TwoAttachment")) target.AlignmentMode = value;
        if (property == "Mode" && target.Type == "AlignOrientation" && value is ("OneAttachment" or "TwoAttachment")) target.AlignmentMode = value;
        if (property == "ActuatorType" && target.Type == "HingeConstraint" && value is ("None" or "Motor" or "Servo")) target.HingeActuatorType = value;
        if (property == "ActuatorType" && target.Type == "PrismaticConstraint" && value is ("None" or "Motor" or "Servo")) target.PrismaticActuatorType = value;
        if (property == "ActuatorType" && target.Type == "CylindricalConstraint" && value is ("None" or "Motor" or "Servo")) target.PrismaticActuatorType = value;
        if (property == "AngularActuatorType" && target.Type == "CylindricalConstraint" && value is ("None" or "Motor" or "Servo")) target.HingeActuatorType = value;
        if (property == "Mode" && target.Type == "UIShadow" && value is ("Shape" or "Text")) target.ShadowMode = value;
        if (property == "Shape" && target.Type == "Part") target.Shape = value;
        if (property == "Material" && SceneNodeTypes.IsBasePart(target)) target.Material = value;
        if (property == "Face" && target.Type is ("Decal" or "Texture" or "SurfaceLight" or "SurfaceGui") && SceneNodeTypes.IsFace(value)) target.Face = value;
        if (property == "EmissionDirection" && target.Type == "ParticleEmitter" && SceneNodeTypes.IsFace(value)) target.EmissionDirection = value;
        if (property == "TextureMode" && target.Type == "Trail" && value is ("Stretch" or "Wrap" or "Static")) target.TextureMode = value;
        if (property == "RelativeTo" && target.Type == "VectorForce" && value is ("World" or "Attachment0" or "Attachment1")) target.RelativeTo = value;
        if (property == "RelativeTo" && target.Type == "LinearVelocity" && value is ("World" or "Attachment0" or "Attachment1")) target.RelativeTo = value;
        if (property == "RelativeTo" && target.Type == "AngularVelocity" && value is ("World" or "Attachment0" or "Attachment1")) target.RelativeTo = value;
        if (property == "RelativeTo" && target.Type == "Torque" && value is ("World" or "Attachment0" or "Attachment1")) target.RelativeTo = value;
        if (property == "VelocityConstraintMode" && target.Type == "LinearVelocity" && value is ("Vector" or "Line" or "Plane")) target.VelocityConstraintMode = value;
        if (property == "ForceLimitMode" && target.Type == "LinearVelocity" && value is ("Magnitude" or "PerAxis")) target.ForceLimitMode = value;
        if (property == "LayoutDirection") target.LayoutDirection = value;
        if (property == "FillDirection" && target.Type == "UIListLayout" && value is ("Horizontal" or "Vertical")) target.LayoutDirection = value;
        if (property == "HorizontalAlignment" && target.Type == "UIListLayout" && value is ("Left" or "Center" or "Right")) target.HorizontalAlignment = value;
        if (property == "VerticalAlignment" && target.Type == "UIListLayout" && value is ("Top" or "Center" or "Bottom")) target.VerticalAlignment = value;
        if (property == "SortOrder" && target.Type == "UIListLayout" && value is ("LayoutOrder" or "Name")) target.SortOrder = value;
        if (property == "FlexMode" && target.Type == "UIFlexItem" && value is ("None" or "Grow" or "Shrink" or "Fill" or "Custom")) target.FlexMode = value;
        if (property == "ItemLineAlignment" && target.Type == "UIFlexItem" && value is ("Automatic" or "Start" or "Center" or "End" or "Stretch")) target.ItemLineAlignment = value;
        if (property == "FillDirection" && target.Type == "UIGridLayout") target.FillDirection = value;
        if (property == "AspectType" && target.Type == "UIAspectRatioConstraint" && value is ("FitWithinMaxSize" or "ScaleWithParentSize")) target.AspectType = value;
        if (property == "DominantAxis" && target.Type == "UIAspectRatioConstraint" && value is ("Width" or "Height")) target.DominantAxis = value;
        if (property == "Mode" && target.Type == "Camera") renderer?.UpdateCamera(project);
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
        else if (property == "PrimaryPart" && target.Type == "Model")
        {
            if (!SceneNodeTypes.IsBasePart(value) || !GameProjectStore.Flatten(target.Children).Any(node => node.Id == value.Id))
            {
                SetStatus("PrimaryPart must be a BasePart inside the Model.");
                return true;
            }
            propertyPickerTargetId = null;
            propertyPickerProperty = null;
            SceneModelService.SetPrimaryPart(target, value);
            RecordEdit();
        }
        else if (property == "Adornee" && target.Type is ("SelectionBox" or "SelectionSphere" or "Highlight" or "BillboardGui" or "SurfaceGui"))
        {
            if ((target.Type is "BillboardGui" or "SurfaceGui") ? !SceneNodeTypes.IsBasePart(value) : !SceneNodeTypes.IsBasePart(value) && value.Type != "Model")
            {
                SetStatus((target.Type is "BillboardGui" or "SurfaceGui") ? $"{target.Type} Adornee must be a BasePart." : "Adornee must be a BasePart or Model.");
                return true;
            }
            propertyPickerTargetId = null;
            propertyPickerProperty = null;
            target.Adornee = value.Id;
            renderer?.UpdateNode(target, project);
            RecordEdit();
        }
        else if (property == "Target" && target.Type == "RocketPropulsion")
        {
            if (!SceneNodeTypes.IsBasePart(value))
            {
                SetStatus("RocketPropulsion Target must be a BasePart.");
                return true;
            }
            propertyPickerTargetId = null;
            propertyPickerProperty = null;
            if (target.RocketPropulsionTarget != value.Id) target.RocketPropulsionReachedTarget = false;
            target.RocketPropulsionTarget = value.Id;
            RecordEdit();
        }
        else if (property is "Part0" or "Part1")
        {
            if (!SceneNodeTypes.IsBasePart(value) || value.Id == (property == "Part0" ? target.Part1 : target.Part0))
            {
                SetStatus("Choose a different BasePart for each constraint endpoint.");
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
        else if (property == "SoundGroup" && target.Type == "Sound")
        {
            if (value.Type != "SoundGroup")
            {
                SetStatus("SoundGroup must be a SoundGroup object.");
                return true;
            }
            propertyPickerTargetId = null;
            propertyPickerProperty = null;
            target.SoundGroupId = value.Id;
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
        SelectOnly(selected);
        RenderProperties();
        if (selected is not null) SelectTreeNode(selected.Id);
        SetStatus("Object selection cancelled");
    }

    private SceneNode? FindSceneNode(string id)
    {
        if (project is null) return null;
        if (project.Workspace.Id == id) return project.Workspace;
        if (project.Players.Id == id) return project.Players;
        if (project.Teams.Id == id) return project.Teams;
        if (project.Lighting.Id == id) return project.Lighting;
        if (project.SoundService.Id == id) return project.SoundService;
        if (project.ReplicatedStorage.Id == id) return project.ReplicatedStorage;
        if (project.ServerScriptService.Id == id) return project.ServerScriptService;
        if (project.ServerStorage.Id == id) return project.ServerStorage;
        if (project.StarterPack.Id == id) return project.StarterPack;
        if (project.StarterPlayer.Id == id) return project.StarterPlayer;
        if (project.Gui.Id == id) return project.Gui;
        return GameProjectStore.Flatten(project).FirstOrDefault(node => node.Id == id);
    }

    private SceneNode? FindParentNode(SceneNode target)
    {
        if (project is null || ReferenceEquals(target, project.Workspace) || ReferenceEquals(target, project.Players) || ReferenceEquals(target, project.Teams) || ReferenceEquals(target, project.Lighting) || ReferenceEquals(target, project.SoundService) || ReferenceEquals(target, project.ReplicatedStorage) || ReferenceEquals(target, project.ServerScriptService) || ReferenceEquals(target, project.ServerStorage) || ReferenceEquals(target, project.StarterPack) || ReferenceEquals(target, project.StarterPlayer) || ReferenceEquals(target, project.Gui)) return null;
        return FindParentNode(project.Workspace, target) ?? FindParentNode(project.Players, target) ?? FindParentNode(project.Teams, target) ?? FindParentNode(project.Lighting, target) ?? FindParentNode(project.SoundService, target) ?? FindParentNode(project.ReplicatedStorage, target) ?? FindParentNode(project.ServerScriptService, target) ?? FindParentNode(project.ServerStorage, target) ?? FindParentNode(project.StarterPack, target) ?? FindParentNode(project.StarterPlayer, target) ?? FindParentNode(project.Gui, target);
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
        if (type is "Script" or "LocalScript" or "ModuleScript")
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
        if (type == "Atmosphere") { node.Color = "#C6D9FF"; node.AtmosphereDecay = "#607080"; }
        if (type == "BlurEffect") node.EffectSize = 0;
        if (type == "ColorCorrectionEffect") { node.Brightness = 0; node.TintColor = "#FFFFFF"; }
        if (type is "Tool" or "Accessory") node.Children.Add(new SceneNode { Type = "Part", Name = "Handle", Size = [1, 1, 1], Position = [0, 1, 0], Color = "#8A8F9C", Material = "Metal" });
        if (type is "Ball" or "Cylinder") node.Shape = type;
        if (type is "SelectionBox" or "SelectionSphere" or "Highlight")
        {
            if (type is "SelectionBox" or "SelectionSphere") node.Color = "#FFFFFF";
            if (SceneNodeTypes.IsBasePart(parent) || parent.Type == "Model") node.Adornee = parent.Id;
        }
        if (type == "UICorner") UICornerRadiusService.Set(node, "CornerRadius", 0, 8);
        if (type == "UIShadow") { node.Color = "#000000"; node.Transparency = .5f; node.ZIndex = -1; }
        if (type is "WeldConstraint" or "NoCollisionConstraint" && SceneNodeTypes.IsBasePart(parent)) node.Part0 = parent.Id;
        if (type is "VectorForce" or "Torque") node.RelativeTo = "Attachment0";
        if (type is "VectorForce" or "LineForce" or "LinearVelocity" or "AlignPosition" or "AlignOrientation" or "AngularVelocity" or "Torque" or "SpringConstraint" or "TorsionSpringConstraint" or "RopeConstraint" or "RodConstraint" or "BallSocketConstraint" or "HingeConstraint" or "PrismaticConstraint" or "CylindricalConstraint" or "PlaneConstraint" or "UniversalConstraint" or "RigidConstraint") node.Attachment0 = parent.Type == "Attachment" ? parent.Id
            : SceneNodeTypes.IsBasePart(parent) ? parent.Children.FirstOrDefault(child => child.Type == "Attachment")?.Id ?? string.Empty : string.Empty;
        if (SceneNodeTypes.IsBasePart(node)) node.Position = [0, 1, 0];
        if (type == "SpawnLocation") { node.Size = [6, 1, 6]; node.Position = [0, .5f, 0]; node.Color = "#72BF9D"; node.Material = "Metal"; }
        if (type is "Seat" or "VehicleSeat") { node.Size = [2, 1, 2]; node.Position = [0, .5f, 0]; node.Color = "#596B89"; node.Material = "Metal"; }
        if (type == "TrussPart") { node.Size = [2, 6, 2]; node.Position = [0, 3, 0]; node.Color = "#AD9682"; node.Material = "Metal"; }
        if (type is "Decal" or "Texture") node.Color = "#FFFFFF";
        if (type == "Fire") { node.Color = "#FF6A00"; node.EffectSize = 5; }
        if (type == "Smoke") node.Color = "#B0B0B0";
        if (type == "Sparkles") node.Color = "#FFFFFF";
        if (type == "ParticleEmitter") node.Color = "#FFFFFF";
        if (type == "Trail") node.Color = "#FFFFFF";
        if (type == "SurfaceLight") node.Angle = 120;
        if (SceneNodeTypes.IsLight(node)) node.Color = "#FFFFFF";
        if (type == "BillboardGui") { node.Size = [200, 50, 1]; if (SceneNodeTypes.IsBasePart(parent)) node.Adornee = parent.Id; }
        if (type == "SurfaceGui") { node.PixelsPerStud = 50; if (SceneNodeTypes.IsBasePart(parent)) node.Adornee = parent.Id; }
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
            ? (parent.Type == "StarterGui"
                ? new (string Type, string Name)[] { ("ScreenGui", "ScreenGui"), ("Folder", "Folder"), ("BoolValue", "BoolValue"), ("IntValue", "IntValue"), ("NumberValue", "NumberValue"), ("StringValue", "StringValue"), ("Script", $"Script{project.Scripts.Count + 1}"), ("LocalScript", $"LocalScript{project.Scripts.Count + 1}") }
                : new (string Type, string Name)[] { ("Frame", "Frame"), ("ScrollingFrame", "ScrollingFrame"), ("ImageLabel", "ImageLabel"), ("TextLabel", "TextLabel"), ("ImageButton", "ImageButton"), ("TextButton", "TextButton"), ("TextBox", "TextBox"), ("UICorner", "UICorner"), ("UIOutline", "UIOutline"), ("UIStroke", "UIStroke"), ("UIGradient", "UIGradient"), ("UIShadow", "UIShadow"), ("UIPadding", "UIPadding"), ("UIScale", "UIScale"), ("UIListLayout", "UIListLayout"), ("UIGridLayout", "UIGridLayout"), ("UITableLayout", "UITableLayout"), ("UIAspectRatioConstraint", "UIAspectRatioConstraint"), ("UISizeConstraint", "UISizeConstraint"), ("UITextSizeConstraint", "UITextSizeConstraint"), ("UIFlexItem", "UIFlexItem"), ("BoolValue", "BoolValue"), ("IntValue", "IntValue"), ("NumberValue", "NumberValue"), ("StringValue", "StringValue"), ("Folder", "Folder"), ("Script", $"Script{project.Scripts.Count + 1}"), ("LocalScript", $"LocalScript{project.Scripts.Count + 1}") })
            : new (string Type, string Name)[] { ("Part", "Part"), ("Ball", "Ball"), ("Cylinder", "Cylinder"), ("WedgePart", "WedgePart"), ("CornerWedgePart", "CornerWedgePart"), ("TrussPart", "TrussPart"), ("SpawnLocation", "SpawnLocation"), ("Seat", "Seat"), ("VehicleSeat", "VehicleSeat"), ("MeshPart", "MeshPart"), ("Attachment", "Attachment"), ("Beam", "Beam"), ("Trail", "Trail"), ("BodyForce", "BodyForce"), ("BodyThrust", "BodyThrust"), ("BodyVelocity", "BodyVelocity"), ("BodyPosition", "BodyPosition"), ("BodyGyro", "BodyGyro"), ("BodyAngularVelocity", "BodyAngularVelocity"), ("RocketPropulsion", "RocketPropulsion"), ("VectorForce", "VectorForce"), ("LineForce", "LineForce"), ("Torque", "Torque"), ("SpringConstraint", "SpringConstraint"), ("TorsionSpringConstraint", "TorsionSpringConstraint"), ("RopeConstraint", "RopeConstraint"), ("RodConstraint", "RodConstraint"), ("BallSocketConstraint", "BallSocketConstraint"), ("HingeConstraint", "HingeConstraint"), ("PrismaticConstraint", "PrismaticConstraint"), ("CylindricalConstraint", "CylindricalConstraint"), ("PlaneConstraint", "PlaneConstraint"), ("UniversalConstraint", "UniversalConstraint"), ("RigidConstraint", "RigidConstraint"), ("LinearVelocity", "LinearVelocity"), ("AlignPosition", "AlignPosition"), ("PointLight", "PointLight"), ("SpotLight", "SpotLight"), ("SurfaceLight", "SurfaceLight"), ("Decal", "Decal"), ("Texture", "Texture"), ("Sound", "Sound"), ("ProximityPrompt", "ProximityPrompt"), ("ClickDetector", "ClickDetector"), ("Highlight", "Highlight"), ("SelectionBox", "SelectionBox"), ("SelectionSphere", "SelectionSphere"), ("ForceField", "ForceField"), ("Explosion", "Explosion"), ("WeldConstraint", "WeldConstraint"), ("NoCollisionConstraint", "NoCollisionConstraint"), ("Humanoid", "Humanoid"), ("ImageLabel", "ImageLabel"), ("Model", "Model"), ("Folder", "Folder"), ("Tool", "Tool"), ("Accessory", "Accessory"), ("BoolValue", "BoolValue"), ("IntValue", "IntValue"), ("NumberValue", "NumberValue"), ("StringValue", "StringValue"), ("Script", $"Script{project.Scripts.Count + 1}"), ("LocalScript", $"LocalScript{project.Scripts.Count + 1}") };
        choices = choices.Concat(new (string Type, string Name)[] { ("Team", "Team"), ("Configuration", "Configuration"), ("BindableEvent", "BindableEvent"), ("BindableFunction", "BindableFunction"), ("RemoteEvent", "RemoteEvent"), ("UnreliableRemoteEvent", "UnreliableRemoteEvent"), ("RemoteFunction", "RemoteFunction"), ("AlignOrientation", "AlignOrientation"), ("AngularVelocity", "AngularVelocity"), ("SoundGroup", "SoundGroup"), ("PitchShiftSoundEffect", "PitchShiftSoundEffect"), ("EchoSoundEffect", "EchoSoundEffect"), ("FlangeSoundEffect", "FlangeSoundEffect"), ("TremoloSoundEffect", "TremoloSoundEffect"), ("ModuleScript", $"ModuleScript{project.Scripts.Count + 1}"), ("Vector2Value", "Vector2Value"), ("Vector3Value", "Vector3Value"), ("Color3Value", "Color3Value"), ("ObjectValue", "ObjectValue"), ("Sky", "Sky"), ("Atmosphere", "Atmosphere"), ("BlurEffect", "BlurEffect"), ("ColorCorrectionEffect", "ColorCorrectionEffect"), ("SurfaceAppearance", "SurfaceAppearance"), ("Fire", "Fire"), ("Smoke", "Smoke"), ("Sparkles", "Sparkles"), ("ParticleEmitter", "ParticleEmitter"), ("BillboardGui", "BillboardGui"), ("SurfaceGui", "SurfaceGui") })
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
        if (selected is { } surfaceTarget && SceneNodeTypes.IsSurfaceAppearance(surfaceTarget) && IsImageAssetType(asset.Type))
        {
            var maps = new MenuItem { Header = "Set SurfaceAppearance map" };
            foreach (var map in new[] { (Name: "Color", Property: "ColorMap"), (Name: "Normal", Property: "NormalMap"), (Name: "Roughness", Property: "RoughnessMap"), (Name: "Metalness", Property: "MetalnessMap"), (Name: "Emissive mask", Property: "EmissiveMaskContent") })
            {
                var mapItem = new MenuItem { Header = map.Name };
                mapItem.Click += (_, _) => ApplyAssetToSelectedObject(asset, map.Property);
                maps.Items.Add(mapItem);
            }
            menu.Items.Add(maps);
        }
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

    private void ApplyAssetToSelectedObject(ProjectAsset asset, string? propertyOverride = null)
    {
        if (project is null || selected is null) return;
        var imageAsset = IsImageAssetType(asset.Type);
        var property = propertyOverride ?? (selected.Type switch
        {
            "Part" when imageAsset => "Texture",
            "ParticleEmitter" when imageAsset => "Texture",
            "Trail" when imageAsset => "Texture",
            "MeshPart" when asset.Type == "obj" => "Asset",
            "SurfaceAppearance" when imageAsset => "ColorMap",
            "ImageLabel" or "ImageButton" or "Decal" or "Texture" when imageAsset => "Asset",
            "Sound" when IsAudioAssetType(asset.Type) => "Asset",
            _ => null
        });
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
        else if (property == "ColorMap" && SceneNodeTypes.IsSurfaceAppearance(selected)) selected.SurfaceAppearanceColorMap = relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ? relativePath[7..] : relativePath;
        else if (property == "NormalMap" && SceneNodeTypes.IsSurfaceAppearance(selected)) selected.SurfaceAppearanceNormalMap = relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ? relativePath[7..] : relativePath;
        else if (property == "RoughnessMap" && SceneNodeTypes.IsSurfaceAppearance(selected)) selected.SurfaceAppearanceRoughnessMap = relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ? relativePath[7..] : relativePath;
        else if (property == "MetalnessMap" && SceneNodeTypes.IsSurfaceAppearance(selected)) selected.SurfaceAppearanceMetalnessMap = relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ? relativePath[7..] : relativePath;
        else if (property == "EmissiveMaskContent" && SceneNodeTypes.IsSurfaceAppearance(selected)) selected.SurfaceAppearanceEmissiveMask = relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ? relativePath[7..] : relativePath;
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
        if (Regex.IsMatch(source, @"\bgame:GetService\s*\(\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)) return ["Workspace", "Players", "Teams", "Lighting", "SoundService", "ReplicatedStorage", "ServerScriptService", "ServerStorage", "StarterPack", "StarterPlayer", "Gui", "StarterGui", "Debris", "CollectionService", "RunService", "UserInputService", "ContextActionService", "TweenService"];
        if (Regex.IsMatch(source, @"\bInstance\.new\s*\(\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)) return LuaXCreatableClassNameCompletions.Concat(["LocalScript"]).ToArray();
        if (Regex.IsMatch(source, @"\bCameraMode\s*=\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)) return ["Custom", "Subject"];
        if (Regex.IsMatch(source, @"\bMaterial\s*=\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)) return SceneNodeTypes.Materials.ToArray();
        if (Regex.IsMatch(source, @"\bMode\s*=\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)) return ["Custom", "Subject", "OneAttachment", "TwoAttachment"];
        if (Regex.IsMatch(source, @"\bLayoutDirection\s*=\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)) return ["Vertical", "Horizontal"];
        if (Regex.IsMatch(source, @"\bHorizontalAlignment\s*=\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)) return ["Left", "Center", "Right"];
        if (Regex.IsMatch(source, @"\bVerticalAlignment\s*=\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)) return ["Top", "Center", "Bottom"];
        if (Regex.IsMatch(source, @"\bSortOrder\s*=\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)) return ["LayoutOrder", "Name"];
        if (Regex.IsMatch(source, @"\bFlexMode\s*=\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)) return ["None", "Grow", "Shrink", "Fill", "Custom"];
        if (Regex.IsMatch(source, @"\bItemLineAlignment\s*=\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)) return ["Automatic", "Start", "Center", "End", "Stretch"];
        if (Regex.IsMatch(source, @"\bFillDirection\s*=\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)) return ["Horizontal", "Vertical"];
        if (Regex.IsMatch(source, @"\bIsA\s*\(\s*(?:['""][^'""\r\n]*)?$", RegexOptions.IgnoreCase)) return LuaXClassNameCompletions.Concat(["LocalScript"]).ToArray();
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
    private static readonly HashSet<string> LuaXInstanceTypes = new(["Instance", "BasePart", "Constraint", "BodyMover", "Part", "MeshPart", "WedgePart", "CornerWedgePart", "SpawnLocation", "TrussPart", "Seat", "VehicleSeat", "Humanoid", "ValueBase", "BoolValue", "IntValue", "NumberValue", "StringValue", "Vector2Value", "Vector3Value", "Color3Value", "ObjectValue", "Light", "PointLight", "SpotLight", "SurfaceLight", "FaceInstance", "Decal", "Texture", "Sound", "SoundGroup", "SoundEffect", "PitchShiftSoundEffect", "EchoSoundEffect", "FlangeSoundEffect", "TremoloSoundEffect", "ReverbSoundEffect", "Fire", "Smoke", "Sparkles", "ParticleEmitter", "ProximityPrompt", "ClickDetector", "Highlight", "SelectionBox", "SelectionSphere", "ForceField", "Explosion", "WeldConstraint", "NoCollisionConstraint", "Attachment", "Beam", "Trail", "BodyForce", "BodyThrust", "BodyVelocity", "BodyPosition", "BodyGyro", "BodyAngularVelocity", "RocketPropulsion", "VectorForce", "LineForce", "Torque", "SpringConstraint", "TorsionSpringConstraint", "RopeConstraint", "RodConstraint", "BallSocketConstraint", "HingeConstraint", "PrismaticConstraint", "CylindricalConstraint", "PlaneConstraint", "UniversalConstraint", "RigidConstraint", "LinearVelocity", "AlignPosition", "AlignOrientation", "AngularVelocity", "Tool", "Model", "Camera", "Workspace", "Players", "Player", "Teams", "Team", "Lighting", "SoundService", "Sky", "Atmosphere", "PostEffect", "BlurEffect", "ColorCorrectionEffect", "SurfaceAppearance", "ReplicatedStorage", "ServerScriptService", "ServerStorage", "StarterPack", "StarterPlayer", "StarterGui", "PlayerGui", "BasePlayerGui", "Backpack", "PlayerScripts", "BasePlayerScripts", "Terrain", "DataModel", "CollectionService", "RunService", "UserInputService", "ContextActionService", "TweenService", "Tween", "TweenInfo", "Folder", "Configuration", "BindableEvent", "BindableFunction", "BaseRemoteEvent", "RemoteEvent", "UnreliableRemoteEvent", "RemoteFunction", "ModuleScript", "LocalScript", "LuaSourceContainer", "BaseScript", "Script", "Gui", "GuiBase2d", "GuiObject", "UIComponent", "LayerCollector", "ScreenGui", "BillboardGui", "SurfaceGui", "Frame", "ScrollingFrame", "ImageLabel", "ImageButton", "TextLabel", "TextButton", "TextBox", "UICorner", "UIOutline", "UIStroke", "UIGradient", "UIShadow", "UIPadding", "UIScale", "UIListLayout", "UIGridLayout", "UITableLayout", "UIAspectRatio", "UIAspectRatioConstraint", "UISizeConstraint", "UITextSizeConstraint", "UIFlexItem"], StringComparer.Ordinal);

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
        if (receiver == "game") return ["GetService", "Gui", "Players", "Teams", "Lighting", "SoundService", "ReplicatedStorage", "ServerScriptService", "ServerStorage", "StarterPack", "StarterPlayer", "StarterGui", "Workspace", "Debris", "CollectionService", "RunService", "UserInputService", "ContextActionService", "TweenService"];
        if (receiver is "game.UserInputService" or "UserInputService") return ["Name", "ClassName", "InputBegan", "InputChanged", "InputEnded", "MouseEnabled", "KeyboardEnabled", "TouchEnabled", "GamepadEnabled", "IsKeyDown", "GetKeysPressed", "GetMouseLocation"];
        if (receiver is "game.ContextActionService" or "ContextActionService") return ["Name", "ClassName", "BindAction", "BindActionAtPriority", "UnbindAction", "UnbindAllActions", "GetBoundActionInfo", "GetAllBoundActionInfo"];
        if (receiver == "game.SoundService") return ["Name", "ClassName", "Volume", "Children", "GetChildren", "FindFirstChild", "WaitForChild"];
        if (receiver == "game.Debris") return ["Name", "ClassName", "AddItem"];
        if (receiver == "game.CollectionService") return ["Name", "ClassName", "AddTag", "RemoveTag", "HasTag", "GetTags", "GetTagged", "GetAllTags", "GetInstanceAddedSignal", "GetInstanceRemovedSignal"];
        if (receiver == "game.RunService") return ["Name", "ClassName", "IsRunning", "IsEdit", "IsStudio", "PreSimulation", "Stepped", "PostSimulation", "Heartbeat", "PreRender", "RenderStepped"];
        if (receiver == "game.TweenService") return ["Name", "ClassName", "Create"];
        if (receiver == "TweenInfo") return ["new"];
        if (receiver == "Enum.EasingStyle") return ["Linear", "Sine", "Quad", "Cubic", "Quart", "Quint", "Exponential", "Circular", "Back", "Bounce", "Elastic"];
        if (receiver == "Enum.EasingDirection") return ["In", "Out", "InOut"];
        if (receiver == "Enum.PlaybackState") return ["Begin", "Delayed", "Playing", "Paused", "Completed", "Cancelled"];
        if (receiver == "Instance") return ["new"];
        if (receiver == "Enum") return ["CameraMode", "KeyCode", "UserInputType", "UserInputState", "ContextActionResult", "LayoutDirection", "FillDirection", "PositionAlignmentMode", "OrientationAlignmentMode", "ActuatorRelativeTo", "RaycastFilterType", "NormalId", "Material", "EasingStyle", "EasingDirection", "PlaybackState"];
        if (receiver == "Enum.KeyCode") return ["W", "A", "S", "D", "Space", "E", "Escape", "Enter", "Backspace", "LeftShift", "RightShift", "LeftControl", "RightControl", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Zero", "Up", "Down", "Left", "Right"];
        if (receiver == "Enum.UserInputType") return ["Keyboard", "MouseButton1", "MouseButton2", "MouseButton3", "MouseMovement", "Touch", "Gamepad1"];
        if (receiver == "Enum.UserInputState") return ["Begin", "Change", "End"];
        if (receiver == "Enum.ContextActionResult") return ["Pass", "Sink"];
        if (receiver == "Enum.Material") return SceneNodeTypes.EnumMaterials.ToArray();
        if (receiver == "Enum.NormalId") return ["Top", "Bottom", "Front", "Back", "Left", "Right"];
        if (receiver == "Enum.CameraMode") return ["Custom", "Subject"];
        if (receiver == "Enum.LayoutDirection") return ["Horizontal", "Vertical"];
        if (receiver == "Enum.PositionAlignmentMode") return ["OneAttachment", "TwoAttachment"];
        if (receiver == "Enum.OrientationAlignmentMode") return ["OneAttachment", "TwoAttachment"];
        if (receiver == "Enum.ActuatorRelativeTo") return ["World", "Attachment0", "Attachment1"];
        if (receiver == "Enum.FillDirection") return ["Horizontal", "Vertical"];
        if (receiver == "Enum.RaycastFilterType") return ["Exclude", "Include"];
        if (receiver == "RaycastParams") return ["new"];
        if (receiver == "OverlapParams") return ["new"];
        if (receiver == "Vector3") return ["new", "one", "zero"];
        if (receiver == "Vector2") return ["new", "zero", "one"];
        if (receiver is "NumberRange" or "NumberSequence" or "ColorSequence" or "NumberSequenceKeypoint" or "ColorSequenceKeypoint") return ["new"];
        if (receiver == "Color3") return ["fromHex", "fromHSV", "fromRGB", "new"];
        if (receiver == "UDim") return ["new"];
        if (receiver == "UDim2") return ["fromOffset", "fromScale", "new", "zero"];
        if (LuaXStandardLibraryMembers.TryGetValue(receiver, out var libraryMembers)) return libraryMembers;
        if (receiver.EndsWith(".Touched", StringComparison.Ordinal) || receiver == "Touched" || receiver.EndsWith(".Triggered", StringComparison.Ordinal) || receiver == "Triggered" || receiver.EndsWith(".MouseClick", StringComparison.Ordinal) || receiver == "MouseClick" || receiver.EndsWith(".Died", StringComparison.Ordinal) || receiver == "Died" || receiver.EndsWith(".Changed", StringComparison.Ordinal) || receiver == "Changed" || receiver.EndsWith(".AttributeChanged", StringComparison.Ordinal) || receiver == "AttributeChanged" || receiver.EndsWith(".Hit", StringComparison.Ordinal) || receiver == "Hit" || receiver.EndsWith(".InputBegan", StringComparison.Ordinal) || receiver.EndsWith(".InputChanged", StringComparison.Ordinal) || receiver.EndsWith(".InputEnded", StringComparison.Ordinal) || receiver.EndsWith(".PreSimulation", StringComparison.Ordinal) || receiver.EndsWith(".Stepped", StringComparison.Ordinal) || receiver.EndsWith(".PostSimulation", StringComparison.Ordinal) || receiver.EndsWith(".Heartbeat", StringComparison.Ordinal) || receiver.EndsWith(".PreRender", StringComparison.Ordinal) || receiver.EndsWith(".RenderStepped", StringComparison.Ordinal)) return ["Connect"];
        var node = ResolveLuaXNode(receiver);
        if (node is null)
        {
            var inferredType = InferLuaXReceiverType(receiver, editor);
            if (inferredType == "Vector3") return ["Cross", "Dot", "Lerp", "Angle", "FuzzyEq", "Abs", "Ceil", "Floor", "Sign", "Min", "Max", "ClampMagnitude", "Magnitude", "Unit", "X", "Y", "Z"];
            if (inferredType == "Vector2") return ["X", "Y", "Magnitude", "Unit", "Perpendicular"];
            if (inferredType == "NumberRange") return ["Min", "Max"];
            if (inferredType is "NumberSequence" or "ColorSequence") return ["Keypoints", "Value"];
            if (inferredType == "NumberSequenceKeypoint") return ["Time", "Value", "Envelope"];
            if (inferredType == "ColorSequenceKeypoint") return ["Time", "Value"];
            if (inferredType == "Color3") return ["B", "G", "Lerp", "R", "ToHex", "ToHSV"];
            if (inferredType == "UDim") return ["Offset", "Scale"];
            if (inferredType == "UDim2") return ["X", "Y"];
            if (inferredType == "RaycastParams") return ["FilterDescendantsInstances", "FilterType", "RespectCanCollide"];
            if (inferredType == "OverlapParams") return ["FilterDescendantsInstances", "FilterType", "MaxParts", "RespectCanCollide"];
            if (inferredType == "RaycastResult") return ["Instance", "Position", "Normal", "Distance", "Material"];
            if (inferredType == "Tween") return ["Instance", "PlaybackState", "Completed", "Play", "Pause", "Cancel"];
            if (inferredType == "TweenInfo") return ["Time", "EasingStyle", "EasingDirection", "RepeatCount", "Reverses", "DelayTime"];
            if (inferredType == "RBXScriptSignal") return ["Connect"];
            if (inferredType == "RunService") return ["Name", "ClassName", "IsRunning", "IsEdit", "IsStudio", "PreSimulation", "Stepped", "PostSimulation", "Heartbeat", "PreRender", "RenderStepped"];
            if (inferredType == "UserInputService") return ["Name", "ClassName", "InputBegan", "InputChanged", "InputEnded", "MouseEnabled", "KeyboardEnabled", "TouchEnabled", "GamepadEnabled", "IsKeyDown", "GetKeysPressed", "GetMouseLocation"];
            if (inferredType == "ContextActionService") return ["Name", "ClassName", "BindAction", "BindActionAtPriority", "UnbindAction", "UnbindAllActions", "GetBoundActionInfo", "GetAllBoundActionInfo"];
            if (inferredType is "number" or "string" or "boolean" or "table") return [];
            node = ResolveLuaXReceiverNode(receiver, editor);
            if (node is null && inferredType is not null && LuaXInstanceTypes.Contains(inferredType))
                node = new SceneNode { Type = inferredType, Name = receiver };
            if (node is null) return LuaXMemberCompletions;
        }

        var members = new List<string> { "Name", "Parent", "Archivable", "ClassName", "Id", "Children", "SetProperty", "GetProperty", "WaitForChild", "FindFirstChild", "FindFirstChildWhichIsA", "FindFirstChildOfClass", "FindFirstAncestor", "FindFirstAncestorOfClass", "FindFirstAncestorWhichIsA", "GetChildren", "GetDescendants", "GetFullName", "IsDescendantOf", "IsA", "Clone", "Destroy", "ClearAllChildren", "SetAttribute", "GetAttribute", "GetAttributes", "GetAttributeChangedSignal", "AttributeChanged", "AddTag", "RemoveTag", "HasTag", "GetTags" };
        if (node.Type == "Tween") members.AddRange(["Instance", "PlaybackState", "Completed", "Play", "Pause", "Cancel"]);
        if (node.Type == "TweenInfo") members.AddRange(["Time", "EasingStyle", "EasingDirection", "RepeatCount", "Reverses", "DelayTime"]);
        if (node.Type == "TweenService") members.Add("Create");
        if (node.Type == "Model") members.AddRange(["Position", "Rotation", "MoveTo", "RotateTo"]);
        if (node.Type == "Model") members.Add("PrimaryPart");
        if (node.Type == "BasePart" || SceneNodeTypes.IsBasePart(node)) members.AddRange(["Position", "Rotation", "Size", "Color", "Material", "Transparency", "Anchored", "CanCollide", "CanTouch", "CanQuery", "Velocity", "AssemblyLinearVelocity", "CastShadow", "Touched", "touched"]);
        if (node.Type == "Part") members.AddRange(["Shape", "Texture"]);
        if (node.Type == "SpawnLocation") members.Add("Enabled");
        if (node.Type == "Seat") members.Add("Occupant");
        if (node.Type == "VehicleSeat") members.AddRange(["Occupant", "Disabled", "HeadsUpDisplay", "MaxSpeed", "Torque", "TurnSpeed", "ThrottleFloat", "SteerFloat", "AreHingesDetected"]);
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
        if (node.Type == "UserInputService") members.AddRange(["InputBegan", "InputChanged", "InputEnded", "MouseEnabled", "KeyboardEnabled", "TouchEnabled", "GamepadEnabled", "IsKeyDown", "GetKeysPressed", "GetMouseLocation"]);
        if (node.Type == "ContextActionService") members.AddRange(["BindAction", "BindActionAtPriority", "UnbindAction", "UnbindAllActions", "GetBoundActionInfo", "GetAllBoundActionInfo"]);
        if (SceneNodeTypes.IsValueObject(node) || node.Type == "ValueBase") members.AddRange(["Value", "Changed"]);
        if (node.Type == "BindableEvent") members.AddRange(["Event", "Fire"]);
        if (node.Type == "BindableFunction") members.AddRange(["OnInvoke", "Invoke"]);
        if (node.Type is "RemoteEvent" or "UnreliableRemoteEvent") members.AddRange(["OnServerEvent", "OnClientEvent", "FireServer", "FireClient", "FireAllClients"]);
        if (node.Type == "BlurEffect") members.AddRange(["Enabled", "Size"]);
        if (node.Type == "ColorCorrectionEffect") members.AddRange(["Enabled", "TintColor", "Brightness", "Contrast", "Saturation"]);
        if (SceneNodeTypes.IsSurfaceAppearance(node)) members.AddRange(["Color", "ColorMap", "NormalMap", "RoughnessMap", "MetalnessMap", "EmissiveMaskContent", "EmissiveTint", "EmissiveStrength"]);
        if (node.Type == "RemoteFunction") members.AddRange(["OnServerInvoke", "OnClientInvoke", "InvokeServer", "InvokeClient"]);
        if (node.Type == "MeshPart") members.Add("Asset");
        if (node.Type == "Light" || SceneNodeTypes.IsLight(node)) members.AddRange(["Enabled", "Color", "Brightness", "Range", "Position"]);
        if (node.Type is "SpotLight" or "SurfaceLight") members.Add("Angle");
        if (node.Type == "SpotLight") members.Add("Rotation");
        if (node.Type is "FaceInstance" or "Decal" or "Texture" or "SurfaceLight") members.Add("Face");
        if (node.Type is "Decal" or "Texture") members.AddRange(["Asset", "Transparency", "Visible"]);
        if (node.Type == "Texture") members.AddRange(["StudsPerTileU", "StudsPerTileV", "OffsetStudsU", "OffsetStudsV"]);
        if (node.Type == "Sound") members.AddRange(["Asset", "Volume", "PlaybackSpeed", "SoundGroup", "Looped", "Playing", "RollOffMinDistance", "RollOffMaxDistance", "Play", "Stop"]);
        if (node.Type == "SoundGroup") members.Add("Volume");
        if (node.Type == "SoundService") members.Add("Volume");
        if (node.Type == "PitchShiftSoundEffect") members.Add("Octave");
        if (node.Type == "EchoSoundEffect") members.AddRange(["Delay", "Feedback", "DryLevel", "WetLevel"]);
        if (node.Type == "FlangeSoundEffect") members.AddRange(["Depth", "Mix", "Rate"]);
        if (node.Type == "TremoloSoundEffect") members.AddRange(["Depth", "Duty", "Frequency"]);
        if (node.Type == "ReverbSoundEffect") members.AddRange(["DecayTime", "Density", "Diffusion", "DryLevel", "WetLevel"]);
        if (node.Type == "ProximityPrompt") members.AddRange(["Enabled", "ActionText", "ObjectText", "HoldDuration", "MaxActivationDistance", "RequiresLineOfSight", "Triggered"]);
        if (node.Type == "ClickDetector") members.AddRange(["Enabled", "MaxActivationDistance", "MouseClick"]);
        if (node.Type == "Highlight") members.AddRange(["Adornee", "Enabled", "FillColor", "FillTransparency", "OutlineColor", "OutlineTransparency"]);
        if (node.Type == "SelectionBox") members.AddRange(["Adornee", "Visible", "Color3", "Transparency", "LineThickness", "SurfaceColor3", "SurfaceTransparency"]);
        if (node.Type == "SelectionSphere") members.AddRange(["Adornee", "Visible", "Color3", "Transparency", "SurfaceColor3", "SurfaceTransparency"]);
        if (node.Type is "WeldConstraint" or "NoCollisionConstraint") members.AddRange(["Enabled", "Part0", "Part1"]);
        if (node.Type == "Beam") members.AddRange(["Enabled", "Attachment0", "Attachment1", "Width0", "Width1", "Color", "Transparency"]);
        if (node.Type == "Trail") members.AddRange(["Enabled", "Attachment0", "Attachment1", "Lifetime", "MinLength", "MaxLength", "WidthScale", "Color", "Transparency", "Texture", "FaceCamera", "TextureMode", "TextureLength", "Clear"]);
        if (node.Type == "VectorForce") members.AddRange(["Enabled", "Attachment0", "Force", "RelativeTo", "ApplyAtCenterOfMass"]);
        if (node.Type == "LineForce") members.AddRange(["Enabled", "Attachment0", "Attachment1", "Magnitude", "MaxForce", "InverseSquareLaw", "ReactionForceEnabled", "ApplyAtCenterOfMass"]);
        if (node.Type is "Script" or "LocalScript") members.Add("Enabled");
        if (node.Type == "BodyForce") members.AddRange(["Enabled", "Force"]);
        if (node.Type == "BodyThrust") members.AddRange(["Enabled", "Force", "Location"]);
        if (node.Type == "BodyVelocity") members.AddRange(["Enabled", "Velocity", "MaxForce", "P"]);
        if (node.Type == "BodyPosition") members.AddRange(["Enabled", "Position", "MaxForce", "P", "D"]);
        if (node.Type == "BodyGyro") members.AddRange(["Enabled", "Rotation", "MaxTorque", "P", "D"]);
        if (node.Type == "BodyAngularVelocity") members.AddRange(["Enabled", "AngularVelocity", "MaxTorque", "P"]);
        if (node.Type == "RocketPropulsion") members.AddRange(["Enabled", "Target", "TargetOffset", "TargetRadius", "CartoonFactor", "MaxSpeed", "MaxThrust", "MaxTorque", "ThrustD", "ThrustP", "TurnD", "TurnP", "ReachedTarget", "Fire", "Abort"]);
        if (node.Type == "LinearVelocity") members.AddRange(["Enabled", "Attachment0", "Attachment1", "RelativeTo", "VelocityConstraintMode", "VectorVelocity", "LineDirection", "LineVelocity", "PlaneVelocity", "PrimaryTangentAxis", "SecondaryTangentAxis", "ForceLimitsEnabled", "ForceLimitMode", "MaxForce", "MaxAxesForce", "MaxPlanarAxesForce", "ReactionForceEnabled"]);
        if (node.Type == "AlignPosition") members.AddRange(["Enabled", "Mode", "Attachment0", "Attachment1", "Position", "Responsiveness", "MaxVelocity", "MaxForce", "RigidityEnabled", "ReactionForceEnabled", "ApplyAtCenterOfMass"]);
        if (node.Type == "AlignOrientation") members.AddRange(["Enabled", "Mode", "Attachment0", "Attachment1", "Rotation", "Responsiveness", "MaxAngularVelocity", "MaxTorque", "RigidityEnabled", "ReactionTorqueEnabled"]);
        if (node.Type == "AngularVelocity") members.AddRange(["Enabled", "Attachment0", "Attachment1", "AngularVelocity", "RelativeTo", "MaxTorque", "ReactionTorqueEnabled"]);
        if (node.Type == "Torque") members.AddRange(["Enabled", "Attachment0", "Attachment1", "Torque", "RelativeTo"]);
        if (node.Type == "SpringConstraint") members.AddRange(["Enabled", "Attachment0", "Attachment1", "CurrentLength", "FreeLength", "Stiffness", "Damping", "MaxForce", "LimitsEnabled", "MinLength", "MaxLength"]);
        if (node.Type == "TorsionSpringConstraint") members.AddRange(["Enabled", "Attachment0", "Attachment1", "Coils", "CurrentAngle", "Damping", "LimitsEnabled", "MaxAngle", "MaxTorque", "Radius", "Restitution", "Stiffness"]);
        if (node.Type == "BallSocketConstraint") members.AddRange(["Enabled", "Attachment0", "Attachment1", "LimitsEnabled", "UpperAngle", "TwistLimitsEnabled", "TwistLowerAngle", "TwistUpperAngle", "Restitution", "MaxFrictionTorque", "Radius"]);
        if (node.Type == "UniversalConstraint") members.AddRange(["Enabled", "Attachment0", "Attachment1", "LimitsEnabled", "MaxAngle", "Radius", "Restitution"]);
        if (node.Type == "RigidConstraint") members.AddRange(["Enabled", "Attachment0", "Attachment1"]);
        if (node.Type == "HingeConstraint") members.AddRange(["Enabled", "Attachment0", "Attachment1", "ActuatorType", "AngularResponsiveness", "AngularSpeed", "AngularVelocity", "CurrentAngle", "LimitsEnabled", "LowerAngle", "MotorMaxAcceleration", "MotorMaxTorque", "Radius", "Restitution", "ServoMaxTorque", "TargetAngle", "UpperAngle"]);
        if (node.Type == "PrismaticConstraint") members.AddRange(["Enabled", "Attachment0", "Attachment1", "ActuatorType", "CurrentPosition", "LimitsEnabled", "LinearResponsiveness", "LowerLimit", "MotorMaxAcceleration", "MotorMaxForce", "Restitution", "ServoMaxForce", "Size", "Speed", "TargetPosition", "UpperLimit", "Velocity"]);
        if (node.Type == "CylindricalConstraint") members.AddRange(["Enabled", "Attachment0", "Attachment1", "ActuatorType", "AngularActuatorType", "AngularLimitsEnabled", "AngularRestitution", "AngularResponsiveness", "AngularSpeed", "AngularVelocity", "CurrentAngle", "CurrentPosition", "InclinationAngle", "LimitsEnabled", "LinearResponsiveness", "LowerAngle", "LowerLimit", "MotorMaxAcceleration", "MotorMaxAngularAcceleration", "MotorMaxForce", "MotorMaxTorque", "Restitution", "RotationAxisVisible", "ServoMaxForce", "ServoMaxTorque", "Size", "Speed", "TargetAngle", "TargetPosition", "UpperAngle", "UpperLimit", "Velocity", "WorldRotationAxis"]);
        if (node.Type == "PlaneConstraint") members.AddRange(["Enabled", "Attachment0", "Attachment1"]);
        if (node.Type == "RopeConstraint") members.AddRange(["Enabled", "Attachment0", "Attachment1", "Length", "CurrentLength", "Restitution", "WinchEnabled", "WinchForce", "WinchResponsiveness", "WinchSpeed", "WinchTarget"]);
        if (node.Type == "RodConstraint") members.AddRange(["Enabled", "Attachment0", "Attachment1", "CurrentDistance", "Length", "LimitAngle0", "LimitAngle1", "LimitsEnabled", "Thickness"]);
        if (node.Type == "Attachment") members.AddRange(["Position", "Rotation"]);
        if (node.Type == "Camera") members.AddRange(["Mode", "CameraMode", "Subject", "FieldOfView", "Position", "Rotation"]);
        if (node.Type == "Workspace") members.AddRange(["Gravity", "AutoManageCharacter", "CurrentCamera", "Raycast", "Blockcast", "GetPartsInPart", "GetPartBoundsInBox", "GetPartBoundsInRadius", "SkyboxFront", "SkyboxBack", "SkyboxLeft", "SkyboxRight", "SkyboxTop", "SkyboxBottom"]);
        if (node.Type == "Terrain") members.AddRange(["FillBlock", "FillBall", "FillCylinder", "FillWedge", "ClearBlock", "Clear"]);
        if (node.Type == "Lighting") members.AddRange(["Ambient", "Brightness", "ClockTime", "GlobalShadows"]);
        if (node.Type == "Atmosphere") members.AddRange(["Color", "Decay", "Density", "Haze", "Glare", "Offset", "Enabled"]);
        if (node.Type == "Sky") members.AddRange(["SkyboxFt", "SkyboxBk", "SkyboxLf", "SkyboxRt", "SkyboxUp", "SkyboxDn"]);
        if (node.Type == "Players") members.AddRange(["LocalPlayer", "GetPlayers", "GetPlayerFromCharacter"]);
        if (node.Type == "Tool") members.AddRange(["ToolTip", "RequiresHandle", "CanBeDropped", "ManualActivationOnly", "GripPosition", "GripRotation", "Activated", "Deactivated", "Equipped", "Unequipped", "Activate", "Deactivate"]);
        if (node.Type == "Player") members.AddRange(["Backpack", "Character", "CharacterAdded", "CharacterRemoving", "PlayerScripts", "Team", "TeamColor"]);
        if (node.Type == "Team") members.AddRange(["TeamColor", "AutoAssignable"]);
        if (node.Type == "SpawnLocation") members.AddRange(["Neutral", "TeamColor", "AllowTeamChangeOnTouch", "Duration"]);
        if (node.Type == "ScreenGui") members.AddRange(["Enabled", "DisplayOrder"]);
        if (node.Type == "BillboardGui") members.AddRange(["Enabled", "Adornee", "Size", "MaxDistance", "AlwaysOnTop"]);
        if (node.Type == "SurfaceGui") members.AddRange(["Enabled", "Adornee", "Face", "MaxDistance", "PixelsPerStud", "AlwaysOnTop"]);
        if (node.Type is "GuiBase2d" or "GuiObject" or "Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox") members.AddRange(["Visible", "Position", "Size", "ZIndex", "LayoutOrder"]);
        if (node.Type is "GuiObject" or "Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox") members.AddRange(["Color", "BackgroundColor3", "Rotation", "BackgroundTransparency"]);
        if (node.Type is "Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox") members.Add("AnchorPoint");
        if (node.Type is "TextLabel" or "TextButton" or "TextBox") members.AddRange(["Text", "TextColor", "TextSize", "TextTransparency", "TextScaled"]);
        if (node.Type == "TextBox") members.AddRange(["MultiLine", "TextEditable", "ClearTextOnFocus", "PlaceholderText"]);
        if (node.Type is "TextButton" or "ImageButton" or "Tool") members.Add("Activated");
        if (node.Type == "Tool") members.AddRange(["Equipped", "Unequipped"]);
        if (node.Type is "TextButton" or "ImageButton") members.Add("MouseButton1Click");
        if (node.Type == "TextBox") members.Add("TextChanged");
        if (node.Type is "ImageLabel" or "ImageButton") members.AddRange(["Asset", "ImageTransparency"]);
        if (node.Type == "UICorner") members.AddRange(["CornerRadius", "TopLeftRadius", "TopRightRadius", "BottomRightRadius", "BottomLeftRadius"]);
        if (node.Type is "UIOutline" or "UIStroke") members.AddRange(["StrokeColor", "StrokeThickness"]);
        if (node.Type == "UIGradient") members.AddRange(["Enabled", "StartColor", "EndColor", "Rotation"]);
        if (node.Type == "UIShadow") members.AddRange(["BlurRadius", "Color", "Enabled", "Inset", "Mode", "Offset", "ShowBehindParent", "Spread", "Transparency", "ZIndex"]);
        if (node.Type == "UIPadding") members.AddRange(["PaddingLeft", "PaddingTop", "PaddingRight", "PaddingBottom"]);
        if (node.Type == "UIScale") members.Add("Scale");
        if (node.Type == "UIListLayout") members.AddRange(["FillDirection", "Padding", "LayoutDirection", "LayoutSpacing", "HorizontalAlignment", "VerticalAlignment", "SortOrder"]);
        if (node.Type == "UIFlexItem") members.AddRange(["FlexMode", "GrowRatio", "ShrinkRatio", "ItemLineAlignment"]);
        if (node.Type == "UIGridLayout") members.AddRange(["CellSize", "CellPadding", "FillDirection", "FillDirectionMaxCells"]);
        if (node.Type == "UITableLayout") members.AddRange(["LayoutSpacing", "TableColumns"]);
        if (node.Type is "UIAspectRatio" or "UIAspectRatioConstraint") members.Add("AspectRatio");
        if (node.Type == "UIAspectRatioConstraint") members.AddRange(["AspectType", "DominantAxis"]);
        if (node.Type == "UISizeConstraint") members.AddRange(["MinSize", "MaxSize"]);
        if (node.Type == "UITextSizeConstraint") members.AddRange(["MinTextSize", "MaxTextSize"]);
        return members.Concat(node.Children.Select(child => child.Name)).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private string[] GetLuaXMethods(string receiver, TextEditor editor)
    {
        if (receiver == "game") return ["GetService"];
        if (receiver is "game.UserInputService" or "UserInputService") return ["IsKeyDown", "GetKeysPressed", "GetMouseLocation"];
        if (receiver is "game.ContextActionService" or "ContextActionService") return ["BindAction", "BindActionAtPriority", "UnbindAction", "UnbindAllActions", "GetBoundActionInfo", "GetAllBoundActionInfo"];
        if (receiver == "game.RunService") return ["IsRunning", "IsEdit", "IsStudio"];
        if (receiver == "game.TweenService") return ["Create"];
        if (receiver.EndsWith(".InputChanged", StringComparison.OrdinalIgnoreCase)) return ["Connect"];
        if (receiver == "TweenInfo") return ["new"];
        if (receiver == "workspace" || receiver == "game.Workspace") return ["Raycast", "Blockcast", "GetPartsInPart", "GetPartBoundsInBox", "GetPartBoundsInRadius", "GetProperty", "SetProperty", "WaitForChild", "FindFirstChild", "FindFirstChildWhichIsA", "FindFirstChildOfClass", "FindFirstAncestor", "FindFirstAncestorOfClass", "FindFirstAncestorWhichIsA", "GetChildren", "GetDescendants", "GetFullName", "IsDescendantOf", "IsA", "ClearAllChildren"];
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
        var receiverNode = ResolveLuaXNode(receiver) ?? ResolveLuaXReceiverNode(receiver, editor);
        if (inferredType is "Vector3Constructor" or "Vector2Constructor" or "NumberRangeConstructor" or "NumberSequenceConstructor" or "ColorSequenceConstructor" or "Color3Constructor" or "UDimConstructor" or "UDim2Constructor" or "RaycastParamsConstructor" or "OverlapParamsConstructor" or "RaycastParams" or "OverlapParams" or "RaycastResult" or "UDim" or "UDim2" or "NumberRange" or "NumberSequence" or "ColorSequence") return [];
        if (inferredType == "Vector3") return ["Abs", "Angle", "Ceil", "ClampMagnitude", "Cross", "Dot", "Floor", "FuzzyEq", "Lerp", "Max", "Min", "Sign"];
        if (inferredType == "Vector2") return ["Abs", "Angle", "Ceil", "ClampMagnitude", "Cross", "Dot", "Floor", "FuzzyEq", "Lerp", "Max", "Min", "Sign"];
        if (inferredType == "Color3") return ["Lerp", "ToHex", "ToHSV"];
        if (inferredType == "RBXScriptSignal") return ["Connect"];
        if (inferredType == "RunService") return ["IsRunning", "IsEdit", "IsStudio"];
        if (inferredType == "UserInputService") return ["IsKeyDown", "GetKeysPressed", "GetMouseLocation"];
        if (inferredType == "ContextActionService") return ["BindAction", "BindActionAtPriority", "UnbindAction", "UnbindAllActions", "GetBoundActionInfo", "GetAllBoundActionInfo"];
        if (inferredType == "TweenService") return ["Create"];
        if (inferredType == "Tween") return ["Play", "Pause", "Cancel"];
        if (inferredType == "Workspace") return ["Raycast", "Blockcast", "GetPartsInPart", "GetPartBoundsInBox", "GetPartBoundsInRadius", "GetProperty", "SetProperty", "WaitForChild", "FindFirstChild", "FindFirstChildWhichIsA", "FindFirstChildOfClass", "FindFirstAncestor", "FindFirstAncestorOfClass", "FindFirstAncestorWhichIsA", "GetChildren", "GetDescendants", "GetFullName", "IsDescendantOf", "IsA", "ClearAllChildren"];
        if (inferredType == "Model" || receiverNode?.Type == "Model") return ["GetProperty", "SetProperty", "WaitForChild", "FindFirstChild", "FindFirstChildWhichIsA", "FindFirstChildOfClass", "FindFirstAncestor", "FindFirstAncestorOfClass", "FindFirstAncestorWhichIsA", "GetChildren", "GetDescendants", "GetFullName", "IsDescendantOf", "IsA", "Clone", "Destroy", "ClearAllChildren", "MoveTo", "RotateTo"];
        if (inferredType is "number" or "string" or "boolean" or "table") return [];
        if (inferredType is "Seat" or "VehicleSeat" || receiverNode?.Type is "Seat" or "VehicleSeat") return ["Sit"];
        if (inferredType == "Humanoid" || receiverNode?.Type == "Humanoid") return ["TakeDamage", "GetProperty", "SetProperty", "WaitForChild", "FindFirstChild", "GetChildren", "IsA", "Clone", "Destroy"];
        if (inferredType == "Sound" || receiverNode?.Type == "Sound") return ["GetProperty", "SetProperty", "WaitForChild", "waitforchild", "FindFirstChild", "GetChildren", "IsA", "Clone", "Destroy", "Play", "Stop"];
        if (inferredType == "ParticleEmitter" || receiverNode?.Type == "ParticleEmitter") return ["GetProperty", "SetProperty", "WaitForChild", "FindFirstChild", "GetChildren", "IsA", "Clone", "Destroy", "Emit", "Clear"];
        if (inferredType == "Trail" || receiverNode?.Type == "Trail") return ["GetProperty", "SetProperty", "WaitForChild", "FindFirstChild", "GetChildren", "IsA", "Clone", "Destroy", "Clear"];
        return LuaXInstanceTypes.Contains(inferredType ?? string.Empty) || receiverNode is not null
            ? ["GetProperty", "SetProperty", "WaitForChild", "waitforchild", "FindFirstChild", "FindFirstChildWhichIsA", "FindFirstChildOfClass", "FindFirstAncestor", "FindFirstAncestorOfClass", "FindFirstAncestorWhichIsA", "GetChildren", "GetDescendants", "GetFullName", "IsDescendantOf", "IsA", "Clone", "Destroy", "ClearAllChildren"]
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
        if (receiver == "OverlapParams") return "OverlapParamsConstructor";
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
        if (Regex.IsMatch(directExpression, @"^OverlapParams\s*\.\s*new\s*\(", RegexOptions.CultureInvariant)) return "OverlapParams";
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
        if (expression.StartsWith("OverlapParams.new", StringComparison.Ordinal)) return "OverlapParams";
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
        if (node?.Type == "BodyForce")
        {
            if (member.Equals("Force", StringComparison.OrdinalIgnoreCase)) return "Vector3";
            if (member.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) return "boolean";
        }
        if (node?.Type == "BodyThrust")
        {
            if (member.Equals("Force", StringComparison.OrdinalIgnoreCase) || member.Equals("Location", StringComparison.OrdinalIgnoreCase)) return "Vector3";
            if (member.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) return "boolean";
        }
        if (node?.Type == "BodyVelocity")
        {
            if (member.Equals("Velocity", StringComparison.OrdinalIgnoreCase) || member.Equals("MaxForce", StringComparison.OrdinalIgnoreCase)) return "Vector3";
            if (member.Equals("P", StringComparison.OrdinalIgnoreCase)) return "number";
            if (member.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) return "boolean";
        }
        if (node?.Type == "BodyPosition")
        {
            if (member.Equals("Position", StringComparison.OrdinalIgnoreCase) || member.Equals("MaxForce", StringComparison.OrdinalIgnoreCase)) return "Vector3";
            if (member.Equals("P", StringComparison.OrdinalIgnoreCase) || member.Equals("D", StringComparison.OrdinalIgnoreCase)) return "number";
            if (member.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) return "boolean";
        }
        if (node?.Type == "BodyGyro")
        {
            if (member.Equals("Rotation", StringComparison.OrdinalIgnoreCase) || member.Equals("MaxTorque", StringComparison.OrdinalIgnoreCase)) return "Vector3";
            if (member.Equals("P", StringComparison.OrdinalIgnoreCase) || member.Equals("D", StringComparison.OrdinalIgnoreCase)) return "number";
            if (member.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) return "boolean";
        }
        if (node?.Type == "BodyAngularVelocity")
        {
            if (member.Equals("AngularVelocity", StringComparison.OrdinalIgnoreCase) || member.Equals("MaxTorque", StringComparison.OrdinalIgnoreCase)) return "Vector3";
            if (member.Equals("P", StringComparison.OrdinalIgnoreCase)) return "number";
            if (member.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) return "boolean";
        }
        if (node?.Type == "RocketPropulsion")
        {
            if (member.Equals("Target", StringComparison.OrdinalIgnoreCase)) return "BasePart";
            if (member.Equals("TargetOffset", StringComparison.OrdinalIgnoreCase) || member.Equals("MaxTorque", StringComparison.OrdinalIgnoreCase)) return "Vector3";
            if (member.Equals("Fire", StringComparison.OrdinalIgnoreCase) || member.Equals("Abort", StringComparison.OrdinalIgnoreCase)) return "function";
            if (member.Equals("ReachedTarget", StringComparison.OrdinalIgnoreCase)) return "RBXScriptSignal";
            if (member.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) return "boolean";
            if (member.ToLowerInvariant() is "targetradius" or "cartoonfactor" or "maxspeed" or "maxthrust" or "thrustd" or "thrustp" or "turnd" or "turnp") return "number";
        }
        if (node?.Type == "Torque")
        {
            var torqueType = member.ToLowerInvariant() switch
            {
                "attachment0" or "attachment1" => "Attachment",
                "torque" => "Vector3",
                "relativeto" => "string",
                "enabled" => "boolean",
                _ => null
            };
            if (torqueType is not null) return torqueType;
        }
        if (node?.Type == "SpringConstraint")
        {
            var springType = member.ToLowerInvariant() switch
            {
                "attachment0" or "attachment1" => "Attachment",
                "currentlength" or "freelength" or "stiffness" or "damping" or "maxforce" or "minlength" or "maxlength" => "number",
                "limitsenabled" or "enabled" => "boolean",
                _ => null
            };
            if (springType is not null) return springType;
        }
        if (node?.Type == "BallSocketConstraint")
        {
            var ballSocketType = member.ToLowerInvariant() switch
            {
                "attachment0" or "attachment1" => "Attachment",
                "limitsenabled" or "twistlimitsenabled" or "enabled" => "boolean",
                "upperangle" or "twistlowerangle" or "twistupperangle" or "restitution" or "maxfrictiontorque" or "radius" => "number",
                _ => null
            };
            if (ballSocketType is not null) return ballSocketType;
        }
        if (node?.Type == "HingeConstraint")
        {
            var hingeType = member.ToLowerInvariant() switch
            {
                "attachment0" or "attachment1" => "Attachment",
                "limitsenabled" or "enabled" => "boolean",
                "actuatortype" => "string",
                "angularresponsiveness" or "angularspeed" or "angularvelocity" or "currentangle" or "lowerangle" or "motormaxacceleration" or "motormaxtorque" or "radius" or "restitution" or "servomaxtorque" or "targetangle" or "upperangle" => "number",
                _ => null
            };
            if (hingeType is not null) return hingeType;
        }
        if (node?.Type == "PrismaticConstraint")
        {
            var prismaticType = member.ToLowerInvariant() switch
            {
                "attachment0" or "attachment1" => "Attachment",
                "limitsenabled" or "enabled" => "boolean",
                "actuatortype" => "string",
                "currentposition" or "linearresponsiveness" or "lowerlimit" or "motormaxacceleration" or "motormaxforce" or "restitution" or "servomaxforce" or "size" or "speed" or "targetposition" or "upperlimit" or "velocity" => "number",
                _ => null
            };
            if (prismaticType is not null) return prismaticType;
        }
        if (node?.Type == "RopeConstraint")
        {
            var ropeType = member.ToLowerInvariant() switch
            {
                "attachment0" or "attachment1" => "Attachment",
                "length" or "currentlength" or "restitution" or "winchforce" or "winchresponsiveness" or "winchspeed" or "winchtarget" => "number",
                "winchenabled" or "enabled" => "boolean",
                _ => null
            };
            if (ropeType is not null) return ropeType;
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
                "anchorpoint" => "Vector2",
                "rotation" => "Vector3",
                "size" => IsLuaXGuiObject(node) || node.Type is "GuiBase2d" or "GuiObject" or "BillboardGui" ? "UDim2" : "Vector3",
                "velocity" or "assemblylinearvelocity" => "Vector3",
                "minsize" or "maxsize" => "Vector2",
                "value" when node.Type == "Vector2Value" => "Vector2",
                "value" when node.Type == "Vector3Value" => "Vector3",
                "value" when node.Type == "Color3Value" => "Color3",
                "value" when node.Type == "BoolValue" => "boolean",
                "value" when node.Type is "IntValue" or "NumberValue" => "number",
                "value" when node.Type == "StringValue" => "string",
                "padding" when node.Type == "UIListLayout" => "UDim",
                "cornerradius" or "topleftradius" or "toprightradius" or "bottomrightradius" or "bottomleftradius" when node.Type == "UICorner" => "UDim",
                "blurradius" when node.Type == "UIShadow" => "UDim",
                "offset" or "spread" when node.Type == "UIShadow" => "UDim2",
                "color" or "fillcolor" or "outlinecolor" => "Color3",
                "brightness" when node.Type == "Lighting" || SceneNodeTypes.IsLight(node) => "number",
                "range" when SceneNodeTypes.IsLight(node) => "number",
                "angle" when node.Type is "SpotLight" or "SurfaceLight" => "number",
                "maxactivationdistance" when node.Type is "ProximityPrompt" or "ClickDetector" => "number",
                "requireslineofsight" when node.Type == "ProximityPrompt" => "boolean",
                "emissivetint" when SceneNodeTypes.IsSurfaceAppearance(node) => "Color3",
                "emissivestrength" when SceneNodeTypes.IsSurfaceAppearance(node) => "number",
                "colormap" or "normalmap" or "roughnessmap" or "metalnessmap" or "emissivemaskcontent" when SceneNodeTypes.IsSurfaceAppearance(node) => "string",
                "color3" or "surfacecolor3" when node.Type is ("SelectionBox" or "SelectionSphere") => "Color3",
                "adornee" when node.Type is ("SelectionBox" or "SelectionSphere" or "Highlight" or "BillboardGui") => "Instance",
                "maxdistance" when node.Type is "BillboardGui" or "SurfaceGui" => "number",
                "alwaystop" when node.Type is "BillboardGui" or "SurfaceGui" => "boolean",
                "pixelsperstud" when node.Type == "SurfaceGui" => "number",
                "primarypart" when node.Type == "Model" => "BasePart?",
                "parent" or "subject" => "Instance",
                "part0" or "part1" => "BasePart",
                "touched" or "activated" or "mousebutton1click" or "textchanged" or "triggered" or "mouseclick" or "hit" => "RBXScriptSignal",
                "displayorder" or "fieldofview" or "textsize" or "mintextsize" or "maxtextsize" or "strokethickness" or "layoutspacing" or "growratio" or "shrinkratio" or "tablecolumns" or "aspectratio" or "transparency" or "backgroundtransparency" or "texttransparency" or "imagetransparency" or "filltransparency" or "outlinetransparency" or "surfacetransparency" or "linethickness" or "maxdistance" or "pixelsperstud" or "blastradius" or "blastpressure" or "destroyjointradiuspercent" => "number",
                "anchored" or "cancollide" or "cantouch" or "castshadow" or "visible" or "enabled" or "alwaystop" or "textscaled" or "multiline" or "texteditable" or "cleartextonfocus" => "boolean",
                "name" or "id" or "classname" or "type" or "texture" or "asset" or "face" or "text" or "placeholdertext" or "textcolor" or "strokecolor" or "layoutdirection" or "filldirection" or "horizontalalignment" or "verticalalignment" or "sortorder" or "flexmode" or "itemlinealignment" or "aspecttype" or "dominantaxis" or "cameramode" or "mode" or "skyboxfront" or "skyboxback" or "skyboxleft" or "skyboxright" or "skyboxtop" or "skyboxbottom" or "actiontext" or "objecttext" => "string",
                _ => null
            };
            if (propertyType is not null) return propertyType;
        }

        return (receiverType, member.ToLowerInvariant()) switch
        {
            ("Tool", "tooltip") => "string",
            ("Tool", "requireshandle" or "canbedropped" or "manualactivationonly") => "boolean",
            ("Tool", "gripposition" or "griprotation") => "Vector3",
            ("VehicleSeat", "occupant") => "Humanoid?",
            ("VehicleSeat", "disabled" or "headsupdisplay") => "boolean",
            ("VehicleSeat", "maxspeed" or "torque" or "turnspeed" or "throttlefloat" or "steerfloat" or "arehingesdetected") => "number",
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
            ("Torque", "attachment0" or "attachment1") => "Attachment",
            ("Torque", "torque") => "Vector3",
            ("Torque", "relativeto") => "string",
            ("SpringConstraint", "attachment0" or "attachment1") => "Attachment",
            ("SpringConstraint", "currentlength" or "freelength" or "stiffness" or "damping" or "maxforce" or "minlength" or "maxlength") => "number",
            ("SpringConstraint", "limitsenabled") => "boolean",
            ("TorsionSpringConstraint", "attachment0" or "attachment1") => "Attachment",
            ("TorsionSpringConstraint", "limitsenabled") => "boolean",
            ("TorsionSpringConstraint", "coils" or "currentangle" or "damping" or "maxangle" or "maxtorque" or "radius" or "restitution" or "stiffness") => "number",
            ("BallSocketConstraint", "attachment0" or "attachment1") => "Attachment",
            ("BallSocketConstraint", "limitsenabled" or "twistlimitsenabled") => "boolean",
            ("BallSocketConstraint", "upperangle" or "twistlowerangle" or "twistupperangle" or "restitution" or "maxfrictiontorque" or "radius") => "number",
            ("UniversalConstraint", "attachment0" or "attachment1") => "Attachment",
            ("UniversalConstraint", "limitsenabled") => "boolean",
            ("UniversalConstraint", "maxangle" or "radius" or "restitution") => "number",
            ("RigidConstraint", "attachment0" or "attachment1") => "Attachment",
            ("HingeConstraint", "attachment0" or "attachment1") => "Attachment",
            ("HingeConstraint", "actuatortype") => "string",
            ("HingeConstraint", "limitsenabled") => "boolean",
            ("HingeConstraint", "angularresponsiveness" or "angularspeed" or "angularvelocity" or "currentangle" or "lowerangle" or "motormaxacceleration" or "motormaxtorque" or "radius" or "restitution" or "servomaxtorque" or "targetangle" or "upperangle") => "number",
            ("PrismaticConstraint", "attachment0" or "attachment1") => "Attachment",
            ("PrismaticConstraint", "actuatortype") => "string",
            ("PrismaticConstraint", "limitsenabled") => "boolean",
            ("PrismaticConstraint", "currentposition" or "linearresponsiveness" or "lowerlimit" or "motormaxacceleration" or "motormaxforce" or "restitution" or "servomaxforce" or "size" or "speed" or "targetposition" or "upperlimit" or "velocity") => "number",
            ("CylindricalConstraint", "attachment0" or "attachment1") => "Attachment",
            ("PlaneConstraint", "attachment0" or "attachment1") => "Attachment",
            ("CylindricalConstraint", "actuatortype" or "angularactuatortype") => "string",
            ("CylindricalConstraint", "angularlimitsenabled" or "limitsenabled" or "rotationaxisvisible") => "boolean",
            ("CylindricalConstraint", "worldrotationaxis") => "Vector3",
            ("CylindricalConstraint", "angularrestitution" or "angularresponsiveness" or "angularspeed" or "angularvelocity" or "currentangle" or "currentposition" or "inclinationangle" or "lowerangle" or "lowerlimit" or "motormaxacceleration" or "motormaxangularacceleration" or "motormaxforce" or "motormaxtorque" or "restitution" or "servomaxforce" or "servomaxtorque" or "size" or "speed" or "targetangle" or "targetposition" or "upperangle" or "upperlimit" or "velocity") => "number",
            ("RopeConstraint", "attachment0" or "attachment1") => "Attachment",
            ("RopeConstraint", "length" or "currentlength" or "restitution" or "winchforce" or "winchresponsiveness" or "winchspeed" or "winchtarget") => "number",
            ("RopeConstraint", "winchenabled") => "boolean",
            ("RodConstraint", "attachment0" or "attachment1") => "Attachment",
            ("RodConstraint", "limitsenabled") => "boolean",
            ("RodConstraint", "currentdistance" or "length" or "limitangle0" or "limitangle1" or "thickness") => "number",
            ("LinearVelocity", "attachment0" or "attachment1") => "Attachment",
            ("LinearVelocity", "vectorvelocity" or "linedirection" or "primarytangentaxis" or "secondarytangentaxis") => "Vector3",
            ("LinearVelocity", "planevelocity") => "Vector2",
            ("LinearVelocity", "linevelocity" or "maxforce") => "number",
            ("LinearVelocity", "forcelimitsenabled" or "reactionforceenabled") => "boolean",
            ("LinearVelocity", "relativeto" or "velocityconstraintmode") => "string",
            ("LineForce", "attachment0" or "attachment1") => "Attachment",
            ("LineForce", "magnitude" or "maxforce") => "number",
            ("LineForce", "inversesquarelaw" or "reactionforceenabled" or "applyatcenterofmass") => "boolean",
            ("AlignPosition", "attachment0" or "attachment1") => "Attachment",
            ("AlignPosition", "position") => "Vector3",
            ("AlignPosition", "responsiveness" or "maxvelocity" or "maxforce") => "number",
            ("AlignPosition", "rigidityenabled" or "reactionforceenabled" or "applyatcenterofmass") => "boolean",
            ("AlignPosition", "mode") => "string",
            ("AlignOrientation", "attachment0" or "attachment1") => "Attachment",
            ("AlignOrientation", "rotation") => "Vector3",
            ("AlignOrientation", "responsiveness" or "maxangularvelocity" or "maxtorque") => "number",
            ("AlignOrientation", "rigidityenabled" or "reactiontorqueenabled") => "boolean",
            ("AlignOrientation", "mode") => "string",
            ("AngularVelocity", "attachment0" or "attachment1") => "Attachment",
            ("AngularVelocity", "angularvelocity") => "Vector3",
            ("AngularVelocity", "maxtorque") => "number",
            ("AngularVelocity", "reactiontorqueenabled") => "boolean",
            ("AngularVelocity", "relativeto") => "string",
            ("BasePart", "assemblyangularvelocity") => "Vector3",
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
        if (expression == "game.UserInputService") return new SceneNode { Type = "UserInputService", Name = "UserInputService" };
        if (Regex.IsMatch(expression, @"^game:GetService\s*\(\s*[""']UserInputService[""']\s*\)$")) return new SceneNode { Type = "UserInputService", Name = "UserInputService" };
        if (expression == "game.ContextActionService") return new SceneNode { Type = "ContextActionService", Name = "ContextActionService" };
        if (Regex.IsMatch(expression, @"^game:GetService\s*\(\s*[""']ContextActionService[""']\s*\)$")) return new SceneNode { Type = "ContextActionService", Name = "ContextActionService" };
        var serviceMatch = Regex.Match(expression, @"^game:GetService\s*\(\s*[""'](?<service>Workspace|Players|Teams|Lighting|SoundService|ReplicatedStorage|ServerScriptService|ServerStorage|StarterPack|StarterPlayer|StarterGui|Gui|TweenService)[""']\s*\)$");
        if (serviceMatch.Success) return serviceMatch.Groups["service"].Value switch
        {
            "Workspace" => project?.Workspace,
            "Players" => project?.Players,
            "Teams" => project?.Teams,
            "Lighting" => project?.Lighting,
            "SoundService" => project?.SoundService,
            "ReplicatedStorage" => project?.ReplicatedStorage,
            "ServerScriptService" => project?.ServerScriptService,
            "ServerStorage" => project?.ServerStorage,
            "StarterPack" => project?.StarterPack,
            "StarterPlayer" => project?.StarterPlayer,
            "StarterGui" or "Gui" => project?.Gui,
            _ => null
        };
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
        if (member.Equals("PrimaryPart", StringComparison.OrdinalIgnoreCase) && instance.Type == "Model") return FindSceneNode(instance.PrimaryPart);
        if (member.Equals("Character", StringComparison.OrdinalIgnoreCase) && instance.Type == "Player") return FindSceneNode(instance.Character);
        if (member.Equals("Part0", StringComparison.OrdinalIgnoreCase) && instance.Type == "WeldConstraint") return FindSceneNode(instance.Part0);
        if (member.Equals("Part1", StringComparison.OrdinalIgnoreCase) && instance.Type == "WeldConstraint") return FindSceneNode(instance.Part1);
        if (member.Equals("Attachment0", StringComparison.OrdinalIgnoreCase) && instance.Type is ("Beam" or "Trail" or "VectorForce" or "LineForce" or "LinearVelocity" or "AlignPosition" or "AlignOrientation" or "AngularVelocity" or "Torque" or "SpringConstraint" or "TorsionSpringConstraint" or "RopeConstraint" or "RodConstraint" or "BallSocketConstraint" or "HingeConstraint" or "PrismaticConstraint" or "CylindricalConstraint" or "PlaneConstraint" or "UniversalConstraint" or "RigidConstraint")) return FindSceneNode(instance.Attachment0);
        if (member.Equals("Attachment1", StringComparison.OrdinalIgnoreCase) && instance.Type is ("Beam" or "Trail" or "AlignPosition" or "AlignOrientation" or "AngularVelocity" or "Torque" or "SpringConstraint" or "TorsionSpringConstraint" or "RopeConstraint" or "RodConstraint" or "BallSocketConstraint" or "HingeConstraint" or "PrismaticConstraint" or "CylindricalConstraint" or "PlaneConstraint" or "UniversalConstraint" or "RigidConstraint")) return FindSceneNode(instance.Attachment1);
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
        else if (segments[0] == "Teams") node = project.Teams;
        else if (segments[0] == "Lighting") node = project.Lighting;
        else if (segments[0] == "SoundService") node = project.SoundService;
        else if (segments[0] == "ReplicatedStorage") node = project.ReplicatedStorage;
        else if (segments[0] == "ServerScriptService") node = project.ServerScriptService;
        else if (segments[0] == "ServerStorage") node = project.ServerStorage;
        else if (segments[0] == "StarterPack") node = project.StarterPack;
        else if (segments[0] == "StarterPlayer") node = project.StarterPlayer;
        else if (segments[0] == "StarterGui") node = project.Gui;
        else if (segments[0] == "Gui") node = project.Gui;
        else if (segments[0] == "script") node = selectedScript is null ? null : FindSceneNode(selectedScript.Id);
        else if (segments[0] == "game")
        {
            if (segments.Length == 1) return null;
            node = segments[1] switch { "Workspace" => project.Workspace, "Players" => project.Players, "Teams" => project.Teams, "Lighting" => project.Lighting, "SoundService" => project.SoundService, "ReplicatedStorage" => project.ReplicatedStorage, "ServerScriptService" => project.ServerScriptService, "ServerStorage" => project.ServerStorage, "StarterPack" => project.StarterPack, "StarterPlayer" => project.StarterPlayer, "Gui" => project.Gui, "StarterGui" => project.Gui, _ => null };
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
        "math", "string", "table", "coroutine", "utf8", "task", "wait", "game", "workspace", "script", "Gui", "Players", "Teams", "Lighting", "SoundService", "ReplicatedStorage", "ServerScriptService", "ServerStorage", "StarterPack", "StarterPlayer", "StarterGui", "Instance", "Enum", "Vector3", "Vector2", "NumberRange", "NumberSequence", "NumberSequenceKeypoint", "ColorSequence", "ColorSequenceKeypoint", "Color3", "UDim", "UDim2", "RaycastParams", "OverlapParams", "TweenInfo", "TweenService",
        "SetProperty", "GetProperty", "WaitForChild", "GetAssetPath", "ConnectTouched", "LookAtRotation", "Sit"
    ];

    private static readonly string[] LuaXMemberCompletions =
    [
        "Name", "Parent", "Archivable", "Value", "Changed", "Event", "Fire", "OnInvoke", "Invoke", "Connected", "Disconnect", "Position", "Rotation", "Size", "Color", "FillColor", "OutlineColor", "FillTransparency", "OutlineTransparency", "Shape", "Face", "Material", "Transparency", "BackgroundTransparency", "TextTransparency", "ImageTransparency", "MinSize", "MaxSize", "AspectType", "DominantAxis", "MinTextSize", "MaxTextSize", "TextScaled", "TextSize", "Texture", "Asset", "Volume", "PlaybackSpeed", "Octave", "Delay", "Feedback", "DryLevel", "WetLevel", "SoundGroup", "Looped", "Playing", "RollOffMinDistance", "RollOffMaxDistance", "Play", "Stop", "Anchored", "CanCollide", "CanTouch", "CanQuery", "Velocity", "AssemblyLinearVelocity", "CastShadow", "Gravity", "AutoManageCharacter", "CurrentCamera", "Raycast", "GetPartsInPart", "GetPartBoundsInBox", "GetPartBoundsInRadius", "FilterDescendantsInstances", "FilterType", "MaxParts", "RespectCanCollide", "BlastRadius", "BlastPressure", "DestroyJointRadiusPercent", "Hit", "Ambient", "ClockTime", "GlobalShadows", "Character", "Team", "TeamColor", "AutoAssignable", "LocalPlayer", "Occupant", "Health", "MaxHealth", "WalkSpeed", "JumpPower", "Died", "TakeDamage", "Enabled", "Part0", "Part1", "PrimaryPart", "Attachment0", "Attachment1", "ActuatorType", "AngularActuatorType", "AngularLimitsEnabled", "AngularRestitution", "AngularResponsiveness", "AngularSpeed", "AngularVelocity", "CurrentAngle", "MaxAngle", "MaxTorque", "Coils", "Stiffness", "Damping", "LowerAngle", "MotorMaxAcceleration", "MotorMaxAngularAcceleration", "MotorMaxTorque", "ServoMaxTorque", "TargetAngle", "UpperAngle", "CurrentPosition", "CurrentDistance", "LimitAngle0", "LimitAngle1", "Thickness", "InclinationAngle", "RotationAxisVisible", "WorldRotationAxis", "LinearResponsiveness", "LowerLimit", "MotorMaxForce", "ServoMaxForce", "TargetPosition", "UpperLimit", "Speed", "TwistLowerAngle", "TwistUpperAngle", "TwistLimitsEnabled", "LimitsEnabled", "MaxFrictionTorque", "Radius", "Restitution", "Width0", "Width1", "Density", "Haze", "Glare", "Offset", "Decay", "Brightness", "Contrast", "Saturation", "TintColor", "Range", "Angle", "ActionText", "ObjectText", "HoldDuration", "MaxActivationDistance", "Triggered", "MouseClick", "CameraMode", "Subject", "FieldOfView", "PlaybackState", "Completed", "EasingStyle", "EasingDirection", "Time", "RepeatCount", "Reverses", "DelayTime", "Children", "ClassName", "Type", "Touched", "touched", "Activated", "Deactivated", "Activate", "Deactivate", "MouseButton1Click", "TextChanged", "SkyboxFront", "SkyboxBack", "SkyboxLeft", "SkyboxRight", "SkyboxTop", "SkyboxBottom", "SkyboxFt", "SkyboxBk", "SkyboxLf", "SkyboxRt", "SkyboxUp", "SkyboxDn", "SetProperty", "GetProperty", "WaitForChild", "waitforchild", "FindFirstChild", "GetChildren", "IsA", "Clone", "Destroy", "SecondaryColor", "Heat", "RiseVelocity", "Opacity", "TimeScale", "SparkleColor", "SetAttribute", "GetAttribute", "GetAttributes", "GetAttributeChangedSignal", "AttributeChanged", "AddTag", "RemoveTag", "HasTag", "GetTags", "GetTagged", "GetAllTags", "GetInstanceAddedSignal", "GetInstanceRemovedSignal", "CollectionService"
    ];

    private static readonly string[] LuaXMethodCompletions = ["GetProperty", "SetProperty", "WaitForChild", "waitforchild", "FindFirstChild", "GetChildren", "Blockcast", "GetPartsInPart", "GetPartBoundsInBox", "GetPartBoundsInRadius", "GetPlayers", "GetPlayerFromCharacter", "IsA", "Clone", "Destroy", "Fire", "Abort", "Invoke", "Disconnect", "TakeDamage", "Sit", "AddItem", "SetAttribute", "GetAttribute", "GetAttributes", "GetAttributeChangedSignal", "AddTag", "RemoveTag", "HasTag", "GetTags", "GetTagged", "GetAllTags", "GetInstanceAddedSignal", "GetInstanceRemovedSignal"];
    private static readonly string[] LuaXCreatableClassNameCompletions = ["Part", "MeshPart", "WedgePart", "CornerWedgePart", "TrussPart", "SpawnLocation", "Seat", "VehicleSeat", "Model", "Folder", "Configuration", "BindableEvent", "BindableFunction", "RemoteEvent", "UnreliableRemoteEvent", "RemoteFunction", "Script", "ModuleScript", "Humanoid", "BoolValue", "IntValue", "NumberValue", "StringValue", "Vector2Value", "Vector3Value", "Color3Value", "ObjectValue", "Sky", "Atmosphere", "BlurEffect", "ColorCorrectionEffect", "SurfaceAppearance", "Team", "Attachment", "Beam", "Trail", "BodyForce", "BodyThrust", "BodyVelocity", "BodyPosition", "BodyGyro", "BodyAngularVelocity", "RocketPropulsion", "VectorForce", "LineForce", "Torque", "SpringConstraint", "TorsionSpringConstraint", "RopeConstraint", "RodConstraint", "BallSocketConstraint", "HingeConstraint", "PrismaticConstraint", "CylindricalConstraint", "PlaneConstraint", "UniversalConstraint", "RigidConstraint", "LinearVelocity", "AlignPosition", "AlignOrientation", "AngularVelocity", "Tool", "Accessory", "PointLight", "SpotLight", "SurfaceLight", "Decal", "Texture", "Sound", "SoundGroup", "PitchShiftSoundEffect", "EchoSoundEffect", "FlangeSoundEffect", "TremoloSoundEffect", "ReverbSoundEffect", "Fire", "Smoke", "Sparkles", "ParticleEmitter", "ProximityPrompt", "ClickDetector", "Highlight", "SelectionBox", "SelectionSphere", "ForceField", "Explosion", "WeldConstraint", "NoCollisionConstraint", "ScreenGui", "BillboardGui", "SurfaceGui", "Frame", "ScrollingFrame", "ImageLabel", "ImageButton", "TextLabel", "TextButton", "TextBox", "UICorner", "UIOutline", "UIStroke", "UIGradient", "UIShadow", "UIPadding", "UIScale", "UIListLayout", "UIGridLayout", "UITableLayout", "UIAspectRatio", "UIAspectRatioConstraint", "UISizeConstraint", "UITextSizeConstraint", "UIFlexItem"];
    private static readonly string[] LuaXClassNameCompletions = ["Instance", "DataModel", "Workspace", "Players", "Player", "Teams", "Team", "Lighting", "SoundService", "Sky", "Atmosphere", "PostEffect", "BlurEffect", "ColorCorrectionEffect", "SurfaceAppearance", "ReplicatedStorage", "ServerScriptService", "ServerStorage", "StarterPack", "StarterPlayer", "StarterGui", "PlayerGui", "BasePlayerGui", "Backpack", "PlayerScripts", "BasePlayerScripts", "Terrain", "Camera", "BasePart", "BodyMover", "Accoutrement", "Accessory", "Part", "WedgePart", "CornerWedgePart", "TrussPart", "SpawnLocation", "Seat", "VehicleSeat", "Humanoid", "ValueBase", "BoolValue", "IntValue", "NumberValue", "StringValue", "Vector2Value", "Vector3Value", "Color3Value", "ObjectValue", "MeshPart", "Light", "PointLight", "SpotLight", "SurfaceLight", "FaceInstance", "Decal", "Texture", "Sound", "SoundGroup", "PitchShiftSoundEffect", "EchoSoundEffect", "FlangeSoundEffect", "TremoloSoundEffect", "ReverbSoundEffect", "Fire", "Smoke", "Sparkles", "ParticleEmitter", "ProximityPrompt", "ClickDetector", "Highlight", "SelectionBox", "SelectionSphere", "ForceField", "Explosion", "WeldConstraint", "NoCollisionConstraint", "Attachment", "Beam", "Trail", "BodyForce", "BodyThrust", "BodyVelocity", "BodyPosition", "BodyGyro", "BodyAngularVelocity", "RocketPropulsion", "VectorForce", "LineForce", "Torque", "SpringConstraint", "TorsionSpringConstraint", "RopeConstraint", "RodConstraint", "BallSocketConstraint", "HingeConstraint", "PrismaticConstraint", "CylindricalConstraint", "PlaneConstraint", "UniversalConstraint", "RigidConstraint", "LinearVelocity", "AlignPosition", "AlignOrientation", "AngularVelocity", "Tool", "Accessory", "Model", "Folder", "Configuration", "BindableEvent", "BindableFunction", "BaseRemoteEvent", "RemoteEvent", "UnreliableRemoteEvent", "RemoteFunction", "ModuleScript", "LuaSourceContainer", "BaseScript", "Script", "Gui", "GuiBase2d", "GuiObject", "UIComponent", "LayerCollector", "ScreenGui", "BillboardGui", "SurfaceGui", "Frame", "ScrollingFrame", "ImageLabel", "ImageButton", "TextLabel", "TextButton", "TextBox", "UICorner", "UIOutline", "UIStroke", "UIGradient", "UIShadow", "UIPadding", "UIScale", "UIListLayout", "UIGridLayout", "UITableLayout", "UIAspectRatio", "UIAspectRatioConstraint", "UISizeConstraint", "UITextSizeConstraint", "UIFlexItem"];
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
                  <Word>workspace</Word><Word>game</Word><Word>script</Word><Word>Gui</Word><Word>Players</Word><Word>Teams</Word><Word>Lighting</Word><Word>SoundService</Word><Word>ReplicatedStorage</Word><Word>ServerScriptService</Word><Word>ServerStorage</Word><Word>LocalPlayer</Word><Word>Instance</Word><Word>Enum</Word><Word>SetProperty</Word><Word>GetProperty</Word><Word>WaitForChild</Word><Word>GetAssetPath</Word><Word>ConnectTouched</Word><Word>Vector3</Word><Word>LookAtRotation</Word><Word>Color3</Word><Word>GetService</Word>
                </Keywords>
                <Keywords color="Type"><Word>any</Word><Word>boolean</Word><Word>never</Word><Word>number</Word><Word>string</Word><Word>unknown</Word><Word>userdata</Word><Word>vector</Word><Word>Vector2</Word><Word>Vector3</Word><Word>NumberRange</Word><Word>NumberSequence</Word><Word>NumberSequenceKeypoint</Word><Word>ColorSequence</Word><Word>ColorSequenceKeypoint</Word><Word>Color3</Word><Word>UDim</Word><Word>UDim2</Word><Word>BrickColor</Word><Word>Instance</Word><Word>DataModel</Word><Word>BasePart</Word><Word>Part</Word><Word>MeshPart</Word><Word>Model</Word><Word>Workspace</Word><Word>Players</Word><Word>Teams</Word><Word>Team</Word><Word>Lighting</Word><Word>Sky</Word><Word>Atmosphere</Word><Word>PostEffect</Word><Word>BlurEffect</Word><Word>ColorCorrectionEffect</Word><Word>SurfaceAppearance</Word><Word>Camera</Word><Word>StarterGui</Word><Word>Gui</Word><Word>GuiBase2d</Word><Word>GuiObject</Word><Word>LayerCollector</Word><Word>Folder</Word><Word>Configuration</Word><Word>BindableEvent</Word><Word>BindableFunction</Word><Word>BaseRemoteEvent</Word><Word>RemoteEvent</Word><Word>UnreliableRemoteEvent</Word><Word>RemoteFunction</Word><Word>SoundGroup</Word><Word>SoundEffect</Word><Word>PitchShiftSoundEffect</Word><Word>EchoSoundEffect</Word><Word>ModuleScript</Word><Word>LocalScript</Word><Word>LuaSourceContainer</Word><Word>BaseScript</Word><Word>Script</Word><Word>ValueBase</Word><Word>BoolValue</Word><Word>IntValue</Word><Word>NumberValue</Word><Word>StringValue</Word><Word>Vector2Value</Word><Word>Vector3Value</Word><Word>Color3Value</Word><Word>ObjectValue</Word><Word>ForceField</Word><Word>Explosion</Word><Word>Fire</Word><Word>Smoke</Word><Word>Sparkles</Word><Word>ParticleEmitter</Word><Word>Trail</Word><Word>ScreenGui</Word><Word>Frame</Word><Word>ScrollingFrame</Word><Word>TextLabel</Word><Word>TextButton</Word><Word>TextBox</Word><Word>ImageLabel</Word><Word>ImageButton</Word><Word>UICorner</Word><Word>UIOutline</Word><Word>UIStroke</Word><Word>UIListLayout</Word><Word>UITableLayout</Word><Word>UIGradient</Word><Word>UIPadding</Word><Word>UIScale</Word><Word>UIGridLayout</Word><Word>UIAspectRatio</Word><Word>UIAspectRatioConstraint</Word><Word>UISizeConstraint</Word><Word>UITextSizeConstraint</Word><Word>UIFlexItem</Word><Word>RBXScriptSignal</Word><Word>RBXScriptConnection</Word></Keywords>
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
        if (SceneViewport.Camera is PerspectiveCamera camera)
            GuiRenderService.AddBillboards(GuiPreviewCanvas, project,
                new Vector3((float)camera.Position.X, (float)camera.Position.Y, (float)camera.Position.Z),
                new Vector3((float)camera.LookDirection.X, (float)camera.LookDirection.Y, (float)camera.LookDirection.Z),
                new Vector3((float)camera.UpDirection.X, (float)camera.UpDirection.Y, (float)camera.UpDirection.Z),
                (float)camera.FieldOfView, GuiPreviewCanvas.ActualWidth, GuiPreviewCanvas.ActualHeight,
                true, selectedId: selected?.Id);
        if (SceneViewport.Camera is PerspectiveCamera surfaceCamera)
            GuiRenderService.AddSurfaceGuis(GuiPreviewCanvas, project,
                new Vector3((float)surfaceCamera.Position.X, (float)surfaceCamera.Position.Y, (float)surfaceCamera.Position.Z),
                new Vector3((float)surfaceCamera.LookDirection.X, (float)surfaceCamera.LookDirection.Y, (float)surfaceCamera.LookDirection.Z),
                new Vector3((float)surfaceCamera.UpDirection.X, (float)surfaceCamera.UpDirection.Y, (float)surfaceCamera.UpDirection.Z),
                (float)surfaceCamera.FieldOfView, GuiPreviewCanvas.ActualWidth, GuiPreviewCanvas.ActualHeight,
                true, selectedId: selected?.Id);
    }

    private void UpdateBillboardGuiPreviewPositions()
    {
        if (project is null || SceneViewport.Camera is not PerspectiveCamera camera) return;
        GuiRenderService.UpdateBillboardPositions(GuiPreviewCanvas, project,
            new Vector3((float)camera.Position.X, (float)camera.Position.Y, (float)camera.Position.Z),
            new Vector3((float)camera.LookDirection.X, (float)camera.LookDirection.Y, (float)camera.LookDirection.Z),
            new Vector3((float)camera.UpDirection.X, (float)camera.UpDirection.Y, (float)camera.UpDirection.Z),
            (float)camera.FieldOfView, GuiPreviewCanvas.ActualWidth, GuiPreviewCanvas.ActualHeight);
        GuiRenderService.UpdateSurfaceGuiPositions(GuiPreviewCanvas, project,
            new Vector3((float)camera.Position.X, (float)camera.Position.Y, (float)camera.Position.Z),
            new Vector3((float)camera.LookDirection.X, (float)camera.LookDirection.Y, (float)camera.LookDirection.Z),
            new Vector3((float)camera.UpDirection.X, (float)camera.UpDirection.Y, (float)camera.UpDirection.Z),
            (float)camera.FieldOfView, GuiPreviewCanvas.ActualWidth, GuiPreviewCanvas.ActualHeight);
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

        SelectOnly(node);
        SelectTreeNode(node.Id);
        RenderProperties();
        RefreshGuiPreview();
        if (selectedTool is "Move" or "Scale" or "Rotate" && node.Type is not ("ScreenGui" or "BillboardGui" or "SurfaceGui"))
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
        var snapping = new MenuItem { Header = "Snap to Grid", IsCheckable = true, IsChecked = snapToGrid };
        snapping.Click += (_, _) =>
        {
            snapToGrid = snapping.IsChecked;
            if (project is not null) { project.Settings.SnapToGrid = snapToGrid; RecordEdit(); }
        };
        var moveIncrement = CreateSnapIncrementMenu("Move increment", [0.25f, 0.5f, 1, 2, 4], moveSnapIncrement, value =>
        {
            moveSnapIncrement = value;
            if (project is not null) { project.Settings.MoveSnapIncrement = value; RecordEdit(); }
        });
        var rotateIncrement = CreateSnapIncrementMenu("Rotate increment", [5, 10, 15, 30, 45, 90], rotateSnapIncrement, value =>
        {
            rotateSnapIncrement = value;
            if (project is not null) { project.Settings.RotateSnapIncrement = value; RecordEdit(); }
        });
        menu.Items.Add(snapping);
        menu.Items.Add(moveIncrement);
        menu.Items.Add(rotateIncrement);
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

    private static MenuItem CreateSnapIncrementMenu(string title, IEnumerable<float> increments, float current, Action<float> changed)
    {
        var menu = new MenuItem { Header = $"{title}: {current.ToString(System.Globalization.CultureInfo.InvariantCulture)}" };
        foreach (var increment in increments.Append(current).Distinct().Order())
        {
            var choice = new MenuItem
            {
                Header = increment.ToString(System.Globalization.CultureInfo.InvariantCulture),
                IsCheckable = true,
                IsChecked = Math.Abs(increment - current) < .0001f
            };
            choice.Click += (_, _) =>
            {
                changed(increment);
                menu.Header = $"{title}: {increment.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                foreach (var sibling in menu.Items.OfType<MenuItem>()) sibling.IsChecked = ReferenceEquals(sibling, choice);
            };
            menu.Items.Add(choice);
        }
        return menu;
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
        var script = selected is { Type: "Script" or "LocalScript" } ? project.Scripts.FirstOrDefault(item => item.Id == selected.Id) : selectedScript ?? project.Scripts.FirstOrDefault();
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
        if (e.Key == Key.F5)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                playerWindow?.Close();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.None)
            {
                OpenPlayer();
                e.Handled = true;
            }
            return;
        }
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
            UpdateBillboardGuiPreviewPositions();
            return;
        }
        if (direction.LengthSquared() != 0 || yawDelta != 0 || pitchDelta != 0)
            renderer.ApplyCameraInput(direction, (float)(elapsed * 12), yawDelta, pitchDelta, project);
        UpdateBillboardGuiPreviewPositions();
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
        var transformRoots = GetSelectedRoots(node => SceneNodeTypes.IsBasePart(node) || node.Type == "Model").ToArray();
        if (transformRoots.Length == 0) transformRoots = [selected];
        var hit = SceneViewport.FindHits(e.GetPosition(SceneViewport)).FirstOrDefault()?.ModelHit;
        if (hit is not MeshGeometryModel3D hitModel) return;
        var selectedVisuals = transformRoots.SelectMany(node => node.Type == "Model"
            ? GameProjectStore.Flatten(node.Children).Where(SceneNodeTypes.IsBasePart).Select(part => part.Id)
            : [node.Id]).ToHashSet(StringComparer.Ordinal);
        if (!selectedVisuals.Any(id => renderer.Visuals.TryGetValue(id, out var visual) && ReferenceEquals(hitModel, visual))) return;
        isTransformDragging = true;
        transformChanged = false;
        transformStartPointer = e.GetPosition(SceneViewport);
        transformStartPosition = (float[])selected.Position.Clone();
        transformStartRotation = (float[])selected.Rotation.Clone();
        transformStartSize = (float[])selected.Size.Clone();
        transformStartModelPivot = selected.Type == "Model" ? SceneModelService.GetPivot(selected) : [0, 0, 0];
        transformStartModelNodes = selected.Type == "Model"
            ? GameProjectStore.Flatten(selected.Children)
                .Where(node => node.Type == "Model" || SceneNodeTypes.IsBasePart(node) || node.Type is "ImageLabel" or "Attachment")
                .Select(node => (node, (float[])node.Position.Clone(), (float[])node.Size.Clone())).ToList()
            : [];
        transformStartSelectionNodes = transformRoots.Length > 1 && selectedTool is "Move" or "Rotate" or "Scale"
            ? transformRoots.Select(node => (node, (float[])(node.Type == "Model" ? SceneModelService.GetPivot(node) : (float[])node.Position.Clone()), (float[])node.Rotation.Clone())).ToList()
            : [];
        if (transformStartSelectionNodes.Count > 1)
            transformStartSelectionPivot = Enumerable.Range(0, 3)
                .Select(axis => transformStartSelectionNodes.Average(entry => entry.Position[axis])).ToArray();
        transformStartSelectionScaleGroups = transformStartSelectionNodes.Count > 1 && selectedTool == "Scale"
            ? transformRoots.Select(root => (root,
                (float[])(root.Type == "Model" ? SceneModelService.GetPivot(root) : (float[])root.Position.Clone()),
                GameProjectStore.Flatten(root.Children)
                    .Where(node => node.Type == "Model" || SceneNodeTypes.IsBasePart(node) || node.Type is "ImageLabel" or "Attachment")
                    .Select(node => (node, (float[])node.Position.Clone(), (float[])node.Size.Clone())).ToList())).ToList()
            : [];
        if (transformStartSelectionScaleGroups.Count > 0)
            transformModelScaleBasis = Math.Max(.05f, transformStartSelectionScaleGroups
                .SelectMany(group => group.Nodes.Where(entry => SceneNodeTypes.IsBasePart(entry.Node) || entry.Node.Type == "ImageLabel")
                    .SelectMany(entry => Enumerable.Range(0, 3).Select(axis => Math.Abs(entry.Position[axis] - transformStartSelectionPivot[axis]) + Math.Abs(entry.Size[axis]) * .5f)))
                .Concat(transformStartSelectionNodes.Where(entry => SceneNodeTypes.IsBasePart(entry.Node))
                    .SelectMany(entry => Enumerable.Range(0, 3).Select(axis => Math.Abs(entry.Position[axis] - transformStartSelectionPivot[axis]) + Math.Abs(entry.Node.Size[axis]) * .5f)))
                .DefaultIfEmpty(1).Max());
        if (transformStartSelectionScaleGroups.Count == 0)
            transformModelScaleBasis = Math.Max(.05f, transformStartModelNodes
                .Where(entry => SceneNodeTypes.IsBasePart(entry.Node) || entry.Node.Type == "ImageLabel")
                .SelectMany(entry => Enumerable.Range(0, 3).Select(axis => Math.Abs(entry.Position[axis] - transformStartModelPivot[axis]) + Math.Abs(entry.Size[axis]) * .5f))
                .DefaultIfEmpty(1).Max());
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
            var deltaX = (float)(pointer.X - transformStartPointer.X);
            var deltaY = (float)(pointer.Y - transformStartPointer.Y);
            if (transformStartSelectionNodes.Count > 1)
            {
                var changed = false;
                if (selectedTool == "Move")
                {
                    var move = new[] { SnapTransformDelta(deltaX * .02f, moveSnapIncrement), SnapTransformDelta(-deltaY * .02f, moveSnapIncrement), 0f };
                    foreach (var entry in transformStartSelectionNodes)
                    {
                        var position = Enumerable.Range(0, 3).Select(axis => entry.Position[axis] + move[axis]).ToArray();
                        if (entry.Node.Type == "Model") changed |= SceneModelService.MoveTo(entry.Node, position);
                        else if (!entry.Node.Position.SequenceEqual(position)) { entry.Node.Position = position; changed = true; }
                    }
                }
                else if (selectedTool == "Rotate")
                {
                    var delta = new[] { SnapTransformDelta(-deltaY * .5f, rotateSnapIncrement), SnapTransformDelta(deltaX * .5f, rotateSnapIncrement), 0f };
                    var rotationDelta = RotationMath.FromDegrees(delta);
                    var pivot = new Vector3(transformStartSelectionPivot[0], transformStartSelectionPivot[1], transformStartSelectionPivot[2]);
                    foreach (var entry in transformStartSelectionNodes)
                    {
                        var start = new Vector3(entry.Position[0], entry.Position[1], entry.Position[2]);
                        var rotated = pivot + Vector3.TransformNormal(start - pivot, rotationDelta);
                        var position = new[] { rotated.X, rotated.Y, rotated.Z };
                        var rotation = RotationMath.ToDegrees(RotationMath.FromDegrees(entry.Rotation) * rotationDelta);
                        if (entry.Node.Type == "Model")
                        {
                            changed |= SceneModelService.RotateTo(entry.Node, entry.Rotation.Select((value, axis) => value + delta[axis]).ToArray());
                            changed |= SceneModelService.MoveTo(entry.Node, position);
                        }
                        else
                        {
                            if (!entry.Node.Position.SequenceEqual(position)) { entry.Node.Position = position; changed = true; }
                            if (!entry.Node.Rotation.SequenceEqual(rotation)) { entry.Node.Rotation = rotation; changed = true; }
                        }
                    }
                }
                else if (selectedTool == "Scale")
                {
                    var scaleDelta = SnapTransformDelta((deltaX - deltaY) * .02f, moveSnapIncrement);
                    var factor = Math.Clamp(1 + scaleDelta / transformModelScaleBasis, .01f, 100);
                    if (Math.Abs(factor - 1) > .000001f)
                    {
                        var pivot = new Vector3(transformStartSelectionPivot[0], transformStartSelectionPivot[1], transformStartSelectionPivot[2]);
                        foreach (var group in transformStartSelectionScaleGroups)
                        {
                            var rootStart = new Vector3(group.Pivot[0], group.Pivot[1], group.Pivot[2]);
                            var targetPivot = pivot + (rootStart - pivot) * factor;
                            foreach (var entry in group.Nodes)
                            {
                                if (entry.Node.Type is "Model" || SceneNodeTypes.IsBasePart(entry.Node) || entry.Node.Type == "ImageLabel")
                                {
                                    var start = new Vector3(entry.Position[0], entry.Position[1], entry.Position[2]);
                                    var next = pivot + (start - pivot) * factor;
                                    var position = new[] { next.X, next.Y, next.Z };
                                    if (!entry.Node.Position.SequenceEqual(position)) { entry.Node.Position = position; changed = true; }
                                    if (entry.Node.Type == "Model") entry.Node.HasModelPivot = true;
                                }
                                if (SceneNodeTypes.IsBasePart(entry.Node) || entry.Node.Type == "ImageLabel")
                                {
                                    var size = entry.Size.Select(value => Math.Clamp(value * factor, .05f, 100000)).ToArray();
                                    if (!entry.Node.Size.SequenceEqual(size)) { entry.Node.Size = size; changed = true; }
                                }
                                else if (entry.Node.Type == "Attachment")
                                {
                                    var position = entry.Position.Select(value => value * factor).ToArray();
                                    if (!entry.Node.Position.SequenceEqual(position)) { entry.Node.Position = position; changed = true; }
                                }
                            }
                            if (group.Root.Type == "Model")
                                changed |= SceneModelService.MoveTo(group.Root, [targetPivot.X, targetPivot.Y, targetPivot.Z]);
                            else
                            {
                                var position = new[] { targetPivot.X, targetPivot.Y, targetPivot.Z };
                                if (!group.Root.Position.SequenceEqual(position)) { group.Root.Position = position; changed = true; }
                                var size = group.Root.Size.Select(value => Math.Clamp(value * factor, .05f, 100000)).ToArray();
                                if (!group.Root.Size.SequenceEqual(size)) { group.Root.Size = size; changed = true; }
                            }
                        }
                    }
                }
                if (!changed) return;
                renderer?.Refresh(project);
                UpdateBillboardGuiPreviewPositions();
                transformChanged = true;
                return;
            }
            if (selected.Type == "Model")
            {
                var modelTransformChanged = false;
                if (selectedTool == "Move")
                {
                    var moveX = SnapTransformDelta(deltaX * .02f, moveSnapIncrement);
                    var moveY = SnapTransformDelta(-deltaY * .02f, moveSnapIncrement);
                    modelTransformChanged = SceneModelService.MoveTo(selected, [transformStartModelPivot[0] + moveX, transformStartModelPivot[1] + moveY, transformStartModelPivot[2]]);
                }
                if (selectedTool == "Rotate")
                    modelTransformChanged = SceneModelService.RotateTo(selected,
                        [transformStartRotation[0] + SnapTransformDelta(-deltaY * .5f, rotateSnapIncrement),
                         transformStartRotation[1] + SnapTransformDelta(deltaX * .5f, rotateSnapIncrement),
                         transformStartRotation[2]]);
                if (selectedTool == "Scale")
                {
                    var scaleDelta = SnapTransformDelta((deltaX - deltaY) * .02f, moveSnapIncrement);
                    var factor = Math.Clamp(1 + scaleDelta / transformModelScaleBasis, .01f, 100);
                    if (Math.Abs(factor - 1) > .000001f)
                    {
                        foreach (var (node, startPosition, startSize) in transformStartModelNodes)
                        {
                            if (node.Type == "Model" || SceneNodeTypes.IsBasePart(node) || node.Type == "ImageLabel")
                            {
                                var position = Enumerable.Range(0, 3)
                                    .Select(axis => transformStartModelPivot[axis] + (startPosition[axis] - transformStartModelPivot[axis]) * factor)
                                    .ToArray();
                                if (!node.Position.SequenceEqual(position))
                                {
                                    node.Position = position;
                                    modelTransformChanged = true;
                                }
                            }
                            if (SceneNodeTypes.IsBasePart(node) || node.Type == "ImageLabel")
                            {
                                var size = startSize.Select(value => Math.Clamp(value * factor, .05f, 100000)).ToArray();
                                if (!node.Size.SequenceEqual(size))
                                {
                                    node.Size = size;
                                    modelTransformChanged = true;
                                }
                            }
                            else if (node.Type == "Attachment")
                            {
                                var position = startPosition.Select(value => value * factor).ToArray();
                                if (!node.Position.SequenceEqual(position))
                                {
                                    node.Position = position;
                                    modelTransformChanged = true;
                                }
                            }
                        }
                    }
                }
                if (!modelTransformChanged) return;
                renderer?.Refresh(project);
                UpdateBillboardGuiPreviewPositions();
                transformChanged = true;
                return;
            }
            var previousPosition = (float[])selected.Position.Clone();
            var previousRotation = (float[])selected.Rotation.Clone();
            var previousSize = (float[])selected.Size.Clone();
            if (selectedTool == "Move")
            {
                selected.Position =
                [
                    transformStartPosition[0] + SnapTransformDelta(deltaX * .02f, moveSnapIncrement),
                    transformStartPosition[1] + SnapTransformDelta(-deltaY * .02f, moveSnapIncrement),
                    transformStartPosition[2]
                ];
            }
            if (selectedTool == "Scale")
            {
                selected.Size =
                [
                    Math.Max(.05f, transformStartSize[0] + SnapTransformDelta(deltaX * .02f, moveSnapIncrement)),
                    Math.Max(.05f, transformStartSize[1] + SnapTransformDelta(-deltaY * .02f, moveSnapIncrement)),
                    transformStartSize[2]
                ];
            }
            if (selectedTool == "Rotate")
            {
                selected.Rotation =
                [
                    transformStartRotation[0] + SnapTransformDelta(-deltaY * .5f, rotateSnapIncrement),
                    transformStartRotation[1] + SnapTransformDelta(deltaX * .5f, rotateSnapIncrement),
                    transformStartRotation[2]
                ];
            }
            if (selected.Position.SequenceEqual(previousPosition)
                && selected.Rotation.SequenceEqual(previousRotation)
                && selected.Size.SequenceEqual(previousSize)) return;
            renderer?.UpdateNode(selected, project);
            UpdateBillboardGuiPreviewPositions();
            transformChanged = true;
            return;
        }
        if (isRightDragging) e.Handled = true;
    }
    private float SnapTransformDelta(float delta, float increment) => snapToGrid ? MathF.Round(delta / increment, MidpointRounding.AwayFromZero) * increment : delta;
    private void SceneViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e) { renderer?.SetCameraOrbit(0, 0, e.Delta > 0 ? -1 : 1, project); UpdateBillboardGuiPreviewPositions(); }
    private void SceneViewport_MouseDown3D(object sender, RoutedEventArgs e)
    {
        if (renderer is null || project is null || isRightDragging) return;
        var hit = SceneViewport.FindHits(Mouse.GetPosition(SceneViewport)).FirstOrDefault()?.ModelHit;
        if (hit is not MeshGeometryModel3D model) return;
        var id = renderer.Visuals.FirstOrDefault(entry => ReferenceEquals(entry.Value, model)).Key;
        if (string.IsNullOrEmpty(id)) return;
        var node = FindSceneNode(id);
        if (node is null) return;
        if (propertyPickerTargetId is not null)
        {
            CompleteObjectReferencePick(node);
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ToggleSceneSelection(node);
        else
        {
            SelectOnly(node);
            SelectTreeNode(id);
            RenderProperties();
        }
    }

    private void DeleteSelected()
    {
        if (project is null || selected is null) return;
        var roots = GetSelectedRoots(IsDeletableSceneObject);
        if (roots.Count == 0) return;
        var description = roots.Count == 1 ? roots[0].Name : $"{roots.Count} objects";
        if (!StudioDialog.Confirm(this, "Delete object", $"Delete {description}?")) return;
        var removedNodes = new List<SceneNode>();
        foreach (var target in roots)
        {
            removedNodes.AddRange(GameProjectStore.RemoveNodeAndGetSubtree(project.Workspace.Children, target.Id));
            removedNodes.AddRange(GameProjectStore.RemoveNodeAndGetSubtree(project.Teams.Children, target.Id));
            removedNodes.AddRange(GameProjectStore.RemoveNodeAndGetSubtree(project.Lighting.Children, target.Id));
            removedNodes.AddRange(GameProjectStore.RemoveNodeAndGetSubtree(project.SoundService.Children, target.Id));
            removedNodes.AddRange(GameProjectStore.RemoveNodeAndGetSubtree(project.ReplicatedStorage.Children, target.Id));
            removedNodes.AddRange(GameProjectStore.RemoveNodeAndGetSubtree(project.ServerScriptService.Children, target.Id));
            removedNodes.AddRange(GameProjectStore.RemoveNodeAndGetSubtree(project.ServerStorage.Children, target.Id));
            removedNodes.AddRange(GameProjectStore.RemoveNodeAndGetSubtree(project.StarterPack.Children, target.Id));
            removedNodes.AddRange(GameProjectStore.RemoveNodeAndGetSubtree(project.StarterPlayer.Children, target.Id));
            removedNodes.AddRange(GameProjectStore.RemoveNodeAndGetSubtree(project.Gui.Children, target.Id));
        }
        if (removedNodes.Count == 0) return;
        var removedIds = removedNodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var player in GameProjectStore.Flatten(project.Players.Children).Where(node => node.Type == "Player" && removedIds.Contains(node.Team))) player.Team = string.Empty;
        foreach (var weld in GameProjectStore.Flatten(project).Where(node => node.Type is "WeldConstraint" or "NoCollisionConstraint"))
        {
            if (removedIds.Contains(weld.Part0)) weld.Part0 = string.Empty;
            if (removedIds.Contains(weld.Part1)) weld.Part1 = string.Empty;
        }
        foreach (var beam in GameProjectStore.Flatten(project).Where(node => node.Type is "Beam" or "Trail" or "VectorForce" or "LineForce" or "LinearVelocity" or "AlignPosition" or "AlignOrientation" or "AngularVelocity" or "Torque" or "SpringConstraint" or "TorsionSpringConstraint" or "RopeConstraint" or "RodConstraint" or "BallSocketConstraint" or "HingeConstraint" or "PrismaticConstraint" or "CylindricalConstraint" or "PlaneConstraint" or "UniversalConstraint" or "RigidConstraint"))
        {
            if (removedIds.Contains(beam.Attachment0)) beam.Attachment0 = string.Empty;
            if (removedIds.Contains(beam.Attachment1)) beam.Attachment1 = string.Empty;
        }
        foreach (var objectValue in GameProjectStore.Flatten(project).Where(node => node.Type == "ObjectValue" && removedIds.Contains(node.ObjectValueId)))
            objectValue.ObjectValueId = string.Empty;
        foreach (var sound in GameProjectStore.Flatten(project).Where(node => node.Type == "Sound" && removedIds.Contains(node.SoundGroupId)))
            sound.SoundGroupId = string.Empty;
        foreach (var model in GameProjectStore.Flatten(project).Where(node => node.Type == "Model" && removedIds.Contains(node.PrimaryPart)))
        {
            if (removedNodes.FirstOrDefault(node => node.Id == model.PrimaryPart) is { } deletedPrimaryPart)
            {
                model.Position = (float[])deletedPrimaryPart.Position.Clone();
                model.Rotation = (float[])deletedPrimaryPart.Rotation.Clone();
                model.HasModelPivot = true;
            }
            model.PrimaryPart = string.Empty;
        }
        foreach (var adornment in GameProjectStore.Flatten(project).Where(node => node.Type is ("SelectionBox" or "SelectionSphere" or "Highlight" or "BillboardGui" or "SurfaceGui") && removedIds.Contains(node.Adornee)))
            adornment.Adornee = string.Empty;
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
        selectedObjectIds.Clear();
        RecordEdit(); RefreshEditor();
    }

    private static bool IsDeletableSceneObject(SceneNode node) => node.Type is not ("Terrain" or "Camera" or "Workspace" or "Players" or "Teams" or "Lighting" or "SoundService" or "Sky" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" or "StarterPlayer" or "Gui" or "StarterGui");

    private List<SceneNode> GetSelectedRoots(Func<SceneNode, bool>? predicate = null)
    {
        var candidates = selectedObjectIds.Select(FindSceneNode).Where(node => node is not null).Cast<SceneNode>()
            .Where(node => predicate?.Invoke(node) ?? true).ToArray();
        var candidateIds = candidates.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        return candidates.Where(node =>
        {
            var parent = FindParentNode(node);
            while (parent is not null)
            {
                if (candidateIds.Contains(parent.Id)) return false;
                parent = FindParentNode(parent);
            }
            return true;
        }).ToList();
    }

    private void DuplicateSelected_Click(object sender, RoutedEventArgs e) => DuplicateSelected();

    private void GroupSelected_Click(object sender, RoutedEventArgs e) => GroupSelected();
    private void UngroupSelected_Click(object sender, RoutedEventArgs e) => UngroupSelected();

    private bool CanGroupSelected()
    {
        if (project is null || selected is null) return false;
        var roots = GetSelectedRoots();
        if (roots.Count == 0 || roots.Any(node => IsGuiNode(node)
            || node.Type is "Workspace" or "Players" or "Lighting" or "Sky" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" or "StarterPlayer" or "Terrain" or "Gui" or "StarterGui" or "Camera" or "Decal" or "Texture" or "ProximityPrompt" or "ClickDetector" or "Highlight" or "SelectionBox" or "SelectionSphere" or "WeldConstraint" or "NoCollisionConstraint" or "Humanoid"
            || SceneNodeTypes.IsLight(node) || FindParentNode(node) is null)) return false;
        var parent = FindParentNode(roots[0]);
        return roots.All(node => ReferenceEquals(FindParentNode(node), parent));
    }

    private void GroupSelected()
    {
        if (!CanGroupSelected() || project is null || selected is null) return;
        var sources = GetSelectedRoots();
        var parent = FindParentNode(sources[0]);
        if (parent is null) return;
        var index = sources.Min(parent.Children.IndexOf);
        if (index < 0) return;
        var originalSiblings = parent.Children.ToArray();
        var modelName = sources.Count == 1 ? sources[0].Name + " Model" : "Model";
        var model = new SceneNode { Type = "Model", Name = modelName };
        parent.Children.Insert(index, model);
        if (sources.Any(source => !GameProjectStore.CanParentNode(project, source, model)))
        {
            parent.Children.Remove(model);
            return;
        }
        foreach (var source in sources.OrderBy(parent.Children.IndexOf).ToArray())
            if (!GameProjectStore.MoveNodeToParent(project, source, model))
            {
                parent.Children.Clear();
                parent.Children.AddRange(originalSiblings);
                model.Children.Clear();
                return;
            }
        selected = model;
        selectedObjectIds.Clear();
        selectedObjectIds.Add(model.Id);
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
        if (selected is null || selected.Type is "Workspace" or "Players" or "Teams" or "Lighting" or "SoundService" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" or "StarterPlayer" or "Terrain" or "Gui" or "StarterGui") return;
        var name = StudioDialog.Prompt(this, "Rename object", "Name", selected.Name, "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        var id = selected.Id;
        ApplyTextProperty(new TextBox { Tag = "Name", DataContext = id, Text = name });
        SelectTreeNode(id);
    }

    private void DuplicateSelected()
    {
        if (project is null || selected is null) return;
        var targets = GetSelectedRoots(IsDuplicableSceneObject);
        if (targets.Count > 1)
        {
            var clones = new List<SceneNode>();
            foreach (var originalTarget in targets)
            {
                var targetParent = FindParentNode(originalTarget);
                if (targetParent is null) continue;
                var duplicateNode = GameProjectStore.CloneSubtree(originalTarget, out var renewedIds, respectArchivable: false);
                if (duplicateNode is null) continue;
                duplicateNode.Name += " Copy";
                if (!IsGuiNode(originalTarget))
                {
                    OffsetDuplicateTree(duplicateNode);
                }
                else
                {
                    duplicateNode.Position[0] += 20;
                    duplicateNode.Position[1] += 20;
                }
                if (!GameProjectStore.CanParentNode(project, duplicateNode, targetParent)) continue;
                foreach (var oldNode in new[] { originalTarget }.Concat(GameProjectStore.Flatten(originalTarget.Children)).Where(node => node.Type is "Script" or "LocalScript" or "ModuleScript"))
                {
                    if (!renewedIds.TryGetValue(oldNode.Id, out var newId)) continue;
                    var oldScript = project.Scripts.FirstOrDefault(script => script.Id == oldNode.Id);
                    if (oldScript is null) continue;
                    var newNode = new[] { duplicateNode }.Concat(GameProjectStore.Flatten(duplicateNode.Children)).First(node => node.Id == newId);
                    project.Scripts.Add(new GameScript { Id = newId, Name = newNode.Name, Source = oldScript.Source });
                }
                var insertIndex = targetParent.Children.IndexOf(originalTarget);
                targetParent.Children.Insert(insertIndex + 1, duplicateNode);
                clones.Add(duplicateNode);
            }
            if (clones.Count == 0) return;
            selected = clones[^1];
            selectedObjectIds.Clear();
            selectedObjectIds.AddRange(clones.Select(node => node.Id));
            RecordEdit();
            RefreshEditor();
            SelectTreeNode(selected.Id);
            SetStatus($"Duplicated {clones.Count} objects");
            return;
        }
        if (targets.Count == 0) return;
        if (!targets.Any(node => node.Id == selected.Id)) selected = targets[0];
        if (!IsDuplicableSceneObject(selected)) return;
        var parent = FindParentNode(selected);
        if (parent is null) return;
        var original = selected;
        var clone = GameProjectStore.CloneSubtree(original, out var newIds, respectArchivable: false);
        if (clone is null) return;
        clone.Name += " Copy";
        if (!IsGuiNode(original))
        {
            OffsetDuplicateTree(clone);
        }
        else if (IsGuiNode(original))
        {
            clone.Position[0] += 20;
            clone.Position[1] += 20;
        }
        if (!GameProjectStore.CanParentNode(project, clone, parent)) return;
        foreach (var oldNode in new[] { original }.Concat(GameProjectStore.Flatten(original.Children)).Where(node => node.Type is "Script" or "LocalScript" or "ModuleScript"))
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

    private static bool IsDuplicableSceneObject(SceneNode node) => node.Type is not ("Terrain" or "Camera" or "Workspace" or "Players" or "Teams" or "Lighting" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" or "StarterPlayer" or "Gui" or "StarterGui");

    private static void OffsetDuplicateTree(SceneNode root)
    {
        foreach (var node in new[] { root }.Concat(GameProjectStore.Flatten(root.Children)))
            if (SceneNodeTypes.IsBasePart(node) || node.Type == "Model") node.Position[0] += 1;
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
        restored.ProjectPath = project.ProjectPath;
        GameProjectStore.EnsureProject(restored);
        project = restored;
        LoadSnapSettings();
        currentSnapshot = JsonSerializer.Serialize(project, GameProjectStore.JsonOptions);
        isRestoring = true; RefreshEditor(); isRestoring = false;
    }

    private void LoadSnapSettings()
    {
        if (project is null) return;
        snapToGrid = project.Settings.SnapToGrid;
        moveSnapIncrement = project.Settings.MoveSnapIncrement;
        rotateSnapIncrement = project.Settings.RotateSnapIncrement;
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
        ["Vector3"] = "Vector3 values support componentwise arithmetic and expose X, Y, Z, Magnitude, and Unit, plus Dot, Cross, Angle, Lerp, FuzzyEq, Abs, Ceil, Floor, Sign, Min, Max, and ClampMagnitude.",
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
        ["GetService"] = "GetService(name: string): Instance? — Returns a built-in service such as Workspace, Players, UserInputService, Debris, CollectionService, or RunService.",
        ["CurrentCamera"] = "Camera? — Workspace camera used for rendering in the play preview.",
        ["IsKeyDown"] = "IsKeyDown(keyCode: Enum.KeyCode): boolean — Returns whether the key is currently held in the game preview.",
        ["GetKeysPressed"] = "GetKeysPressed(): {InputObject} — Returns keyboard keys currently held in the game preview.",
        ["GetMouseLocation"] = "GetMouseLocation(): Vector2 — Returns the current pointer position in the game viewport.",
        ["BindAction"] = "BindAction(name: string, callback: function, createTouchButton: boolean, ...inputs: Enum.KeyCode | Enum.UserInputType): () — Binds an action callback to one or more inputs.",
        ["BindActionAtPriority"] = "BindActionAtPriority(name: string, callback: function, createTouchButton: boolean, priority: number, ...inputs): () — Binds an action at an explicit priority.",
        ["UnbindAction"] = "UnbindAction(name: string): () — Removes a named input action.",
        ["UnbindAllActions"] = "UnbindAllActions(): () — Removes every input action binding.",
        ["Vector2"] = "Vector2 values support componentwise arithmetic and expose X, Y, Magnitude, Unit, and Perpendicular, plus Dot, Cross, Angle, Lerp, FuzzyEq, Abs, Ceil, Floor, Sign, Min, Max, and ClampMagnitude.",
        ["GetDescendants"] = "GetDescendants(): {Instance} — Returns every descendant below this Instance in tree order.",
        ["FindFirstChildOfClass"] = "FindFirstChildOfClass(className: string): Instance? — Returns the first direct child with this class or a subclass.",
        ["FindFirstChildWhichIsA"] = "FindFirstChildWhichIsA(className: string, recursive?: boolean): Instance? — Returns the first child that matches a class or subclass.",
        ["FindFirstAncestor"] = "FindFirstAncestor(name: string): Instance? — Finds the closest ancestor with this name.",
        ["FindFirstAncestorOfClass"] = "FindFirstAncestorOfClass(className: string): Instance? — Finds the closest ancestor with this exact class.",
        ["FindFirstAncestorWhichIsA"] = "FindFirstAncestorWhichIsA(className: string): Instance? — Finds the closest ancestor that matches a class or subclass.",
        ["GetFullName"] = "GetFullName(): string — Returns the instance path from game, joined with periods.",
        ["IsDescendantOf"] = "IsDescendantOf(ancestor: Instance): boolean — Returns whether this instance is below the given ancestor.",
        ["ClearAllChildren"] = "ClearAllChildren(): () — Destroys all direct children and their descendants.",
        ["MoveTo"] = "MoveTo(position: Vector3): () — Moves every part in a Model so its Position pivot reaches the target.",
        ["RotateTo"] = "RotateTo(rotation: Vector3): () — Rotates every part in a Model around its pivot using degree-based Rotation.",
        ["InputBegan"] = "InputBegan:Connect(function(input: InputObject, gameProcessed: boolean) ...) — Fires when a keyboard key or mouse button is pressed in the game preview.",
        ["InputChanged"] = "InputChanged:Connect(function(input: InputObject, gameProcessed: boolean) ...) — Fires when the mouse moves in the game preview.",
        ["InputEnded"] = "InputEnded:Connect(function(input: InputObject, gameProcessed: boolean) ...) — Fires when a keyboard key or mouse button is released in the game preview.",
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
        ["GetPlayers"] = "GetPlayers(): {Player} — Returns the Player children of the Players service.",
        ["GetPlayerFromCharacter"] = "GetPlayerFromCharacter(character: Model): Player? — Finds the player whose Character is the supplied Model.",
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
        ["Blockcast"] = "Workspace:Blockcast(position: Vector3, size: Vector3, rotation: Vector3, direction: Vector3, params: RaycastParams?): RaycastResult? — Sweeps a rotated box through Workspace parts.",
        ["RaycastParams"] = "RaycastParams.new(): RaycastParams — FilterDescendantsInstances and FilterType (Enum.RaycastFilterType.Exclude or Include) filter hits; RespectCanCollide switches eligibility from CanQuery to CanCollide.",
        ["GetPartsInPart"] = "Workspace:GetPartsInPart(part: BasePart, overlapParams: OverlapParams?): {BasePart} — Returns queryable Workspace parts intersecting the input part.",
        ["GetPartBoundsInBox"] = "Workspace:GetPartBoundsInBox(position: Vector3, size: Vector3, rotation: Vector3?, overlapParams: OverlapParams?): {BasePart} — Returns part bounds intersecting a rotated box region.",
        ["GetPartBoundsInRadius"] = "Workspace:GetPartBoundsInRadius(position: Vector3, radius: number, overlapParams: OverlapParams?): {BasePart} — Returns part bounds intersecting a sphere region.",
        ["OverlapParams"] = "OverlapParams.new(): OverlapParams — Set FilterDescendantsInstances, FilterType, MaxParts, or RespectCanCollide to control overlap results.",
        ["TweenInfo.new"] = "TweenInfo.new(time, easingStyle, easingDirection, repeatCount, reverses, delayTime): TweenInfo — Creates tween timing and easing options.",
        ["TweenService:Create"] = "TweenService:Create(instance, tweenInfo, goals): Tween — Creates a property animation. Numeric, Vector2, Vector3, Color3, and UDim2 goals are supported.",
        ["Tween:Play"] = "Tween:Play(): () — Starts or resumes the tween.",
        ["Tween:Pause"] = "Tween:Pause(): () — Pauses the tween at its current property values.",
        ["Tween:Cancel"] = "Tween:Cancel(): () — Cancels the tween without reverting property values.",
        ["Tween.Completed"] = "RBXScriptSignal — Fires with Enum.PlaybackState.Completed or Cancelled when the tween ends.",
        ["FillBlock"] = "Terrain:FillBlock(position: Vector3, size: Vector3, material: Enum.Material) — Adds one collidable terrain block.",
        ["FillBall"] = "Terrain:FillBall(position: Vector3, radius: number, material: Enum.Material) — Adds one collidable spherical terrain piece.",
        ["FillCylinder"] = "Terrain:FillCylinder(position: Vector3, size: Vector3, rotation: Vector3, material: Enum.Material) — Adds an oriented cylinder on the 4-stud terrain grid.",
        ["FillWedge"] = "Terrain:FillWedge(position: Vector3, size: Vector3, rotation: Vector3, material: Enum.Material) — Adds an oriented wedge on the 4-stud terrain grid.",
        ["ClearBlock"] = "Terrain:ClearBlock(position: Vector3, size: Vector3) — Removes terrain pieces that intersect the box.",
        ["Clear"] = "Terrain:Clear() — Removes all terrain pieces.",
        ["TakeDamage"] = "TakeDamage(amount: number): () — Changes Humanoid Health unless protected by a ForceField. A negative amount heals.",
        ["ForceField"] = "ForceField — Protects a Humanoid from TakeDamage when placed in its Model; Visible controls the shield effect.",
        ["Explosion"] = "Explosion — Detonates once in Workspace, applying outward velocity and hitting parts within BlastRadius.",
        ["Smoke"] = "Smoke — Emits a colored smoke effect from a BasePart or Attachment.",
        ["Sparkles"] = "Sparkles — Emits bright glints from a BasePart or Attachment.",
        ["ParticleEmitter"] = "ParticleEmitter — Emits adjustable particles from a BasePart or Attachment.",
        ["Trail"] = "Trail — Draws a fading ribbon between two moving Attachments.",
        ["VectorForce"] = "VectorForce — Applies a world or attachment-relative force to a part during play.",
        ["LineForce"] = "LineForce — Applies force along the line between two Attachments during play.",
        ["Torque"] = "Torque — Applies constant angular acceleration to Attachment0's part during play.",
        ["SpringConstraint"] = "SpringConstraint — Pulls two Attachments toward its FreeLength during play.",
        ["TorsionSpringConstraint"] = "TorsionSpringConstraint — Applies damped angular spring torque between Attachments.",
        ["RopeConstraint"] = "RopeConstraint — Keeps two Attachments from separating beyond Length, with an optional winch.",
        ["RodConstraint"] = "RodConstraint — Keeps two Attachments at a fixed distance, with optional tilt limits.",
        ["BallSocketConstraint"] = "BallSocketConstraint — Keeps the positions of two Attachments together during play.",
        ["HingeConstraint"] = "HingeConstraint — Joins two Attachments around one axis and supports motor or servo actuation.",
        ["PrismaticConstraint"] = "PrismaticConstraint — Lets two Attachments slide along one axis with optional motor or servo actuation.",
        ["CylindricalConstraint"] = "CylindricalConstraint — Lets two Attachments slide along an axis and rotate around an inclined axis.",
        ["PlaneConstraint"] = "PlaneConstraint — Keeps Attachment1 on a plane defined by Attachment0.",
        ["UniversalConstraint"] = "UniversalConstraint — Keeps two Attachment axes perpendicular with optional tilt limits.",
        ["BodyForce"] = "BodyForce — Applies a constant world-axis force to its parent part.",
        ["BodyThrust"] = "BodyThrust — Applies an assembly-relative force at an assembly-relative Location, producing torque when offset from the assembly center of mass.",
        ["BodyVelocity"] = "BodyVelocity — Uses P and per-axis MaxForce to drive its parent BasePart toward a target Velocity during play.",
        ["BodyPosition"] = "BodyPosition — Uses P, D, and per-axis MaxForce to move its parent BasePart toward a target Position during play.",
        ["BodyGyro"] = "BodyGyro — Uses P, D, and per-axis MaxTorque to turn its parent BasePart toward a target Rotation during play.",
        ["BodyAngularVelocity"] = "BodyAngularVelocity — Uses P and per-axis MaxTorque to drive its parent BasePart toward a target angular velocity during play.",
        ["RocketPropulsion"] = "RocketPropulsion — Fire() uses capped thrust and turn torque to follow a BasePart target or world TargetOffset; Abort() stops it and ReachedTarget fires once inside the target radius.",
        ["RigidConstraint"] = "RigidConstraint — Keeps two Attachment frames in the same relative position and orientation.",
        ["LinearVelocity"] = "LinearVelocity — Maintains a target velocity along a vector, line, or plane during play.",
        ["AlignPosition"] = "AlignPosition — Moves Attachment0 toward a world position or Attachment1 during play.",
        ["AlignOrientation"] = "AlignOrientation — Rotates Attachment0 toward a target Rotation or Attachment1 during play.",
        ["AngularVelocity"] = "AngularVelocity — Applies torque to maintain a target angular velocity on Attachment0.",
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
        ["CharacterAdded"] = "RBXScriptSignal — Fires with the Player's assigned character Model.",
        ["CharacterRemoving"] = "RBXScriptSignal — Fires with the previous character Model when it is cleared, replaced, or destroyed.",
        ["OnServerEvent"] = "RBXScriptSignal — RemoteEvent callbacks receive the calling player, then the arguments passed to FireServer.",
        ["OnClientEvent"] = "RBXScriptSignal — RemoteEvent callbacks receive arguments sent by FireClient or FireAllClients.",
        ["FireServer"] = "FireServer(...arguments: any): () — Fires OnServerEvent with LocalPlayer as the first argument.",
        ["FireClient"] = "FireClient(player: Player, ...arguments: any): () — Fires OnClientEvent for LocalPlayer in this single-player runtime.",
        ["FireAllClients"] = "FireAllClients(...arguments: any): () — Fires OnClientEvent for the local player.",
        ["OnServerInvoke"] = "function? — Callback used by RemoteFunction.InvokeServer.",
        ["OnClientInvoke"] = "function? — Callback used by RemoteFunction.InvokeClient.",
        ["InvokeServer"] = "InvokeServer(...arguments: any): (...any) — Calls OnServerInvoke and returns its results.",
        ["InvokeClient"] = "InvokeClient(player: Player, ...arguments: any): (...any) — Calls the local player's OnClientInvoke callback.",
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
        ["Sit"] = "Sit(humanoid: Humanoid): () — Seats the humanoid in this Seat or VehicleSeat.",
        ["Position"] = "Vector3 for 3D objects; UDim2 for GUI objects.",
        ["Rotation"] = "Vector3 — Rotation in degrees; GUI objects use the Z component.",
        ["Size"] = "Vector3 for parts; UDim2 for GUI objects; number for Fire or Smoke.",
        ["Color"] = "Color3 — Object color.",
        ["TintColor"] = "Color3 — ColorCorrectionEffect tint applied to scene materials.",
        ["Contrast"] = "number — ColorCorrectionEffect contrast adjustment from -1 to 1.",
        ["Saturation"] = "number — ColorCorrectionEffect saturation adjustment from -1 to 1.",
        ["Delay"] = "number — EchoSoundEffect delay in seconds, from 0 to 2.",
        ["Feedback"] = "number — EchoSoundEffect repeat feedback, from 0 to 0.95.",
        ["DryLevel"] = "number — EchoSoundEffect original signal level in decibels.",
        ["WetLevel"] = "number — EchoSoundEffect delayed signal level in decibels.",
        ["ColorMap"] = "string — SurfaceAppearance albedo texture path, relative to the project assets folder.",
        ["NormalMap"] = "string — SurfaceAppearance tangent-space normal texture path.",
        ["RoughnessMap"] = "string — SurfaceAppearance grayscale roughness texture path.",
        ["MetalnessMap"] = "string — SurfaceAppearance grayscale metalness texture path.",
        ["EmissiveMaskContent"] = "string — SurfaceAppearance grayscale emissive mask texture path.",
        ["EmissiveTint"] = "Color3 — Tint applied to the SurfaceAppearance emissive mask.",
        ["EmissiveStrength"] = "number — SurfaceAppearance emissive brightness multiplier.",
        ["Texture"] = "string — Project-relative image path used by a Part, ParticleEmitter, or Trail, such as blocks/grass.png.",
        ["FillColor"] = "Color3 — Highlight fill color.",
        ["OutlineColor"] = "Color3 — Highlight outline color.",
        ["FillTransparency"] = "number — Highlight fill transparency from 0 to 1.",
        ["OutlineTransparency"] = "number — Highlight outline transparency from 0 to 1.",
        ["Adornee"] = "Instance — BasePart or Model targeted by a Highlight, SelectionBox, or SelectionSphere.",
        ["Color3"] = "Color3 — SelectionBox or SelectionSphere outline color.",
        ["SurfaceColor3"] = "Color3 — SelectionBox or SelectionSphere surface color.",
        ["SurfaceTransparency"] = "number — SelectionBox or SelectionSphere surface transparency from 0 to 1.",
        ["LineThickness"] = "number — SelectionBox outline thickness in studs.",
        ["MaxActivationDistance"] = "number — Maximum distance from the part for a prompt or click detector to work.",
        ["ActionText"] = "string — Action displayed on a ProximityPrompt.",
        ["ObjectText"] = "string — Object label displayed on a ProximityPrompt.",
        ["HoldDuration"] = "number — Seconds the player must hold E before a ProximityPrompt fires.",
        ["RequiresLineOfSight"] = "boolean — When true, parts with CanQuery must not block the path from the character to a ProximityPrompt.",
        ["Activate"] = "Activate(): () — Fires Activated for an equipped Tool.",
        ["Deactivate"] = "Deactivate(): () — Ends the current Tool activation and fires Deactivated.",
        ["Deactivated"] = "RBXScriptSignal — Fires when an equipped Tool is deactivated or its activation ends.",
        ["Asset"] = "string — Project asset path used by a MeshPart or image GUI object.",
        ["CameraMode"] = "string — Custom or Subject.",
        ["Mode"] = "string — Camera mode alias: Custom or Subject.",
        ["Subject"] = "BasePart? — Part or MeshPart tracked by a Subject-mode Camera.",
        ["Part0"] = "BasePart? — First endpoint of a WeldConstraint.",
        ["PrimaryPart"] = "BasePart? — Model descendant used as its pivot.",
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
        ["CornerRadius"] = "UDim — set all four UICorner radii; scale uses the shorter rendered side.",
        ["TopLeftRadius"] = "UDim — UICorner top-left radius.",
        ["TopRightRadius"] = "UDim — UICorner top-right radius.",
        ["BottomRightRadius"] = "UDim — UICorner bottom-right radius.",
        ["BottomLeftRadius"] = "UDim — UICorner bottom-left radius.",
        ["BlurRadius"] = "UDim — blur radius for UIShadow.",
        ["Inset"] = "boolean — draw a UIShadow inside its parent.",
        ["ShowBehindParent"] = "boolean — draw the shadow beneath its parent shape.",
        ["Spread"] = "UDim2 — expand or shrink a UIShadow shape.",
        ["StrokeColor"] = "string — UIOutline or UIStroke hex color.",
        ["StrokeThickness"] = "number — UIOutline or UIStroke width in pixels.",
        ["LayoutDirection"] = "string — Legacy UIListLayout direction: Vertical or Horizontal.",
        ["Padding"] = "UDim — UIListLayout spacing along the fill direction.",
        ["HorizontalAlignment"] = "string — align UIListLayout items and the list horizontally.",
        ["VerticalAlignment"] = "string — align UIListLayout items and the list vertically.",
        ["SortOrder"] = "string — arrange UIListLayout items by LayoutOrder or Name.",
        ["FlexMode"] = "string — grow, shrink, fill, or customize a GUI item in a UIListLayout.",
        ["GrowRatio"] = "number — relative growth weight when UIFlexItem FlexMode is Custom.",
        ["ShrinkRatio"] = "number — relative shrink weight when UIFlexItem FlexMode is Custom.",
        ["ItemLineAlignment"] = "string — align this UIFlexItem across the list line.",
        ["LayoutSpacing"] = "number — Pixel spacing used by a UI layout.",
        ["TableColumns"] = "number — UITableLayout column count.",
        ["CellSize"] = "UDim2 — Width and height of UIGridLayout cells.",
        ["CellPadding"] = "UDim2 — Horizontal and vertical gap between UIGridLayout cells.",
        ["AnchorPoint"] = "Vector2 — GUI origin as a fraction of object size; position is measured from this point.",
        ["DisplayOrder"] = "integer — ScreenGui draw order; higher values appear over lower values.",
        ["FillDirection"] = "string — Fill UIListLayout or UIGridLayout horizontally or vertically.",
        ["FillDirectionMaxCells"] = "integer — Maximum grid cells per row or column; zero uses available space.",
        ["AspectRatio"] = "number — width-to-height ratio for UIAspectRatioConstraint.",
        ["AspectType"] = "string — fit within the object's size or scale with its parent.",
        ["DominantAxis"] = "string — width or height axis used by UIAspectRatioConstraint.",
        ["MinSize"] = "Vector2 — minimum GUI size in pixels for UISizeConstraint.",
        ["MaxSize"] = "Vector2 — maximum GUI size in pixels for UISizeConstraint.",
        ["TextScaled"] = "boolean — fit GUI text to its container.",
        ["MultiLine"] = "boolean — allow a TextBox to accept line breaks.",
        ["TextEditable"] = "boolean — allow typing into a TextBox.",
        ["ClearTextOnFocus"] = "boolean — clear a TextBox when it receives focus during play.",
        ["PlaceholderText"] = "string — hint shown in an empty TextBox.",
        ["MinTextSize"] = "number — minimum rendered font size for UITextSizeConstraint.",
        ["MaxTextSize"] = "number — maximum rendered font size for UITextSizeConstraint.",
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
