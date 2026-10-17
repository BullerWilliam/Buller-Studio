using System.IO;
using System.Text.Json;
using System.Xml;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BullerStudio.Models;
using BullerStudio.Services;
using ICSharpCode.AvalonEdit;
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
    private string currentSnapshot = string.Empty;
    private bool isRightDragging;
    private bool isTransformDragging;
    private bool transformChanged;
    private string selectedTool = "Select";
    private string toolboxCategory = "All";
    private string activeRibbonTab = "HOME";
    private Point lastPointer;
    private static readonly IHighlightingDefinition LuaXHighlighting = CreateLuaXHighlighting();

    public MainWindow()
    {
        InitializeComponent();
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
        selected = next.Workspace;
        selectedScript = null;
        scriptEditor = null;
        scriptEditors.Clear();
        undo.Clear(); redo.Clear();
        currentSnapshot = JsonSerializer.Serialize(next, GameProjectStore.JsonOptions);
        Title = $"{next.Name} - Buller Studio";
        ViewportDocument.Title = next.Name;
        renderer = new SceneRenderService(SceneViewport, next.Workspace.Children.First(node => node.Type == "Camera"));
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
        UpdateExplorerFilter(project.Workspace, ExplorerSearch.Text.Trim());
        ExplorerTree.ItemsSource = new[] { project.Workspace };
        ObjectCount.Text = GameProjectStore.Flatten(project.Workspace.Children).Count.ToString();
        RefreshToolboxAssets();
        renderer?.Refresh(project);
        RenderProperties();
        if (selected is not null) SelectTreeNode(selected.Id);
    }

    private void ExplorerSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ExplorerSearchHint.Visibility = string.IsNullOrEmpty(ExplorerSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (project is null) return;
        UpdateExplorerFilter(project.Workspace, ExplorerSearch.Text.Trim());
        ExplorerTree.ItemsSource = null;
        ExplorerTree.ItemsSource = new[] { project.Workspace };
    }

    private static bool UpdateExplorerFilter(SceneNode node, string filter)
    {
        var matchesNode = string.IsNullOrEmpty(filter) || node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || node.Type.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var matchesChild = false;
        foreach (var child in node.Children) matchesChild |= UpdateExplorerFilter(child, filter);
        node.IsExplorerVisible = matchesNode || matchesChild;
        return node.IsExplorerVisible;
    }

    private void ToolboxSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ToolboxSearchHint.Visibility = string.IsNullOrEmpty(ToolboxSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        RefreshToolboxAssets();
    }

    private void ToolboxCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string category }) return;
        toolboxCategory = category;
        RefreshToolboxAssets();
    }

    private void RefreshToolboxAssets()
    {
        if (project is null || ToolboxAssetList is null) return;
        var query = ToolboxSearch.Text.Trim();
        ToolboxAssetList.Items.Clear();
        foreach (var tab in ToolboxCategoryTabs.Children.OfType<Button>())
            tab.Background = Equals(tab.Tag, toolboxCategory) ? new SolidColorBrush(Color.FromRgb(48, 90, 124)) : Brushes.Transparent;
        var assets = project.Assets.Where(asset =>
        {
            var matchesCategory = toolboxCategory switch
            {
                "Images" => asset.Type is "png" or "jpg" or "jpeg" or "bmp" or "gif",
                "Meshes" => asset.Type == "obj",
                _ => true
            };
            return matchesCategory && (string.IsNullOrEmpty(query) || asset.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || asset.Type.Contains(query, StringComparison.OrdinalIgnoreCase));
        }).ToArray();
        foreach (var asset in assets) ToolboxAssetList.Items.Add(CreateAssetListItem(asset));
        ToolboxEmptyState.Text = project.Assets.Count == 0 ? "No local assets yet. Import image or OBJ files to use them in this project." : "No assets match this search or category.";
        ToolboxEmptyState.Visibility = assets.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ExplorerTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        selected = e.NewValue as SceneNode;
        RenderProperties();
    }

    private void RenderProperties()
    {
        PropertyEditor.Children.Clear();
        propertyTarget = null;
        if (project is null || selected is null) return;

        var dataSection = AddPropertySection("Data");
        propertyTarget = dataSection;
        AddLabel($"Class: {selected.Type}", "Class");
        AddTextField("Name", selected.Name);
        if (selected.Type is "Part" or "MeshPart" or "ImageLabel" or "Camera")
        {
            propertyTarget = AddPropertySection("Transform");
            AddTextField("Position", VectorText(selected.Position));
            AddTextField("Rotation", VectorText(selected.Rotation));
            if (selected.Type != "Camera")
            {
                AddTextField("Size", VectorText(selected.Size));
                propertyTarget = AddPropertySection("Appearance");
                AddTextField("Color", selected.Color);
                AddCheckField("CastShadow", selected.CastShadow);
                propertyTarget = AddPropertySection("Behavior");
                AddCheckField("Anchored", selected.Anchored);
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
                var subjects = GameProjectStore.Flatten(project.Workspace.Children).Where(node => node.Type is "Part" or "MeshPart").Select(node => $"{node.Id} | {node.Name}").Prepend("").ToArray();
                AddComboField("Subject", subjects, subjects.FirstOrDefault(value => value.StartsWith(selected.Subject + " | ", StringComparison.Ordinal)) ?? "");
                AddTextField("FieldOfView", selected.FieldOfView.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddLabel("The Workspace Camera is the only view camera.", "Camera");
                AddLabel("Subject mode orbits a BasePart; Custom uses Position and Rotation.", "Subject");
            }
        }
        if (selected.Type == "Script")
        {
            var script = project.Scripts.FirstOrDefault(item => item.Id == selected.Id);
            if (script is not null)
            {
                propertyTarget = AddPropertySection("Script");
                AddLabel("Edit this Lua X script in its document tab.", "Source");
                ShowScript(script);
            }
        }
        ApplyPropertyFilter(PropertySearch.Text.Trim());
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
        var box = new TextBox { Text = value, Tag = property, MinWidth = 100, Padding = new Thickness(5, 3, 5, 3) };
        box.LostFocus += PropertyText_LostFocus;
        box.KeyDown += (_, args) => { if (args.Key == Key.Enter) { ApplyTextProperty(box); Keyboard.ClearFocus(); } };
        (propertyTarget ?? PropertyEditor).Children.Add(CreatePropertyRow(property, box));
    }

    private void AddCheckField(string property, bool value)
    {
        var box = new CheckBox { IsChecked = value, Margin = new Thickness(5, 2, 0, 2), Tag = property, Foreground = (Brush)FindResource("TextPrimary"), HorizontalAlignment = HorizontalAlignment.Left };
        box.Checked += PropertyCheck_Changed; box.Unchecked += PropertyCheck_Changed;
        (propertyTarget ?? PropertyEditor).Children.Add(CreatePropertyRow(property, box));
    }

    private void AddComboField(string property, IEnumerable<string> values, string selectedValue)
    {
        var combo = new ComboBox { ItemsSource = values, SelectedItem = selectedValue, Tag = property, Margin = new Thickness(0, 1, 0, 1), MinWidth = 100, Height = 26 };
        combo.SelectionChanged += PropertyCombo_Changed;
        (propertyTarget ?? PropertyEditor).Children.Add(CreatePropertyRow(property, combo));
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
        SetOutputPanelVisible(true);
        var query = ToolboxSearch.Text.Trim();
        var assets = project.Assets.Where(asset => string.IsNullOrEmpty(query) || asset.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || asset.Type.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new DockPanel { Margin = new Thickness(8, 2, 8, 1), LastChildFill = false };
        var title = new TextBlock { Text = "Project assets", FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("TextPrimary") };
        DockPanel.SetDock(title, Dock.Left);
        header.Children.Add(title);
        var import = new Button { Content = "＋ Import files", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(2, 1, 0, 1) };
        import.Click += ImportAssets_Click;
        DockPanel.SetDock(import, Dock.Right);
        header.Children.Add(import);
        panel.Children.Add(header);
        var body = new Grid { Margin = new Thickness(7, 2, 7, 5) };
        if (assets.Length == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = project.Assets.Count == 0 ? "No local assets yet. Import images or meshes to use them in this project." : "No assets match this search.",
                Foreground = (Brush)FindResource("TextMuted"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 310,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(12)
            });
        }
        else
        {
            var list = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = (Brush)FindResource("TextPrimary") };
            foreach (var asset in assets) list.Items.Add(CreateAssetListItem(asset));
            body.Children.Add(list);
        }
        Grid.SetRow(body, 1);
        panel.Children.Add(body);
        BottomContent.Content = panel;
        SelectBottomTab(showOutput: false);
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
        return new Border { Background = Brushes.Transparent, BorderBrush = new SolidColorBrush(Color.FromRgb(55, 57, 60)), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(5), Margin = new Thickness(0, 1, 0, 1), Child = row };
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
        var editorPanel = new DockPanel { Margin = new Thickness(6) };
        var editorToolbar = new DockPanel { LastChildFill = false, Background = (Brush)FindResource("PanelRaised"), Margin = new Thickness(0, 0, 0, 1) };
        var language = new TextBlock { Text = script.Name, Foreground = (Brush)FindResource("TextMuted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0), FontSize = 10 };
        var run = new Button { Content = "▶ Run Lua X", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(10, 4, 10, 4) };
        run.Click += (_, _) => RunScript(script);
        DockPanel.SetDock(run, Dock.Right); editorToolbar.Children.Add(run); editorToolbar.Children.Add(language);
        DockPanel.SetDock(editorToolbar, Dock.Top); editorPanel.Children.Add(editorToolbar);
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
        editorPanel.Children.Add(editor);
        scriptEditors[script.Id] = editor;
        return editorPanel;
    }

    private static IHighlightingDefinition CreateLuaXHighlighting()
    {
        const string definition = """
            <SyntaxDefinition name="Lua X" extensions=".lua" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="#6A9955" />
              <Color name="String" foreground="#CE9178" />
              <Color name="Number" foreground="#B5CEA8" />
              <Color name="Keyword" foreground="#C586C0" fontWeight="bold" />
              <Color name="Builtin" foreground="#4EC9B0" />
              <RuleSet>
                <Span color="Comment" begin="--" />
                <Span color="String" begin="&quot;" end="&quot;" />
                <Span color="String" begin="'" end="'" />
                <Keywords color="Keyword">
                  <Word>and</Word><Word>break</Word><Word>do</Word><Word>else</Word><Word>elseif</Word><Word>end</Word><Word>false</Word><Word>for</Word><Word>function</Word><Word>if</Word><Word>in</Word><Word>local</Word><Word>nil</Word><Word>not</Word><Word>or</Word><Word>repeat</Word><Word>return</Word><Word>then</Word><Word>true</Word><Word>until</Word><Word>while</Word>
                </Keywords>
                <Keywords color="Builtin">
                  <Word>print</Word><Word>warn</Word><Word>wait</Word><Word>workspace</Word><Word>game</Word><Word>script</Word><Word>SetProperty</Word><Word>WaitForChild</Word><Word>Vector3</Word><Word>Color3</Word><Word>Instance</Word>
                </Keywords>
                <Rule color="Number">\b\d+(\.\d+)?\b</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;
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
        if (project is null) return;
        CommitScriptEditor();
        var runtime = new LuaXRuntime(project,
            (node, _, _) => Dispatcher.Invoke(() => renderer?.UpdateNode(node, project)),
            message => Dispatcher.Invoke(() => AppendOutput(message)));
        runtime.Run(script.Id);
        renderer?.Refresh(project);
    }

    private void AssetsTab_Click(object sender, RoutedEventArgs e) => ShowAssets();
    private void OpenScript_Click(object sender, RoutedEventArgs e)
    {
        if (project is null) return;
        var script = selectedScript ?? project.Scripts.FirstOrDefault();
        if (script is null) { SetStatus("Add a Script object to open the Lua X editor."); return; }
        ShowScript(script);
    }

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
        if (selected is null || selected.Type is not ("Part" or "MeshPart")) return;
        selected.Anchored = !selected.Anchored;
        RecordEdit(); RenderProperties();
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
        if (selected is null || selected.Type is not ("Part" or "MeshPart"))
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
        renderer?.Refresh(project); SetStatus($"Shadows {(project.Settings.Shadows ? "enabled" : "disabled")}"); RecordEdit();
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
        var script = selected is { Type: "Script" } ? project.Scripts.FirstOrDefault(item => item.Id == selected.Id) : selectedScript ?? project.Scripts.FirstOrDefault();
        if (script is null) { SetStatus("Add a Script object before running Lua X."); return; }
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
    private void Toolbox_Click(object sender, RoutedEventArgs e) => SetToolboxPanelVisible(!ToolboxDock.IsVisible);
    private void Properties_Click(object sender, RoutedEventArgs e)
    {
        SetPropertiesPanelVisible(!PropertiesDock.IsVisible);
        if (PropertiesDock.IsVisible) PropertyEditor.Focus();
    }

    private void WindowMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateWindowMenuItem("Toolbox", ToolboxDock.IsVisible, SetToolboxPanelVisible));
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

    private void ToolboxMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var hide = new MenuItem { Header = "Hide Toolbox" };
        hide.Click += (_, _) => SetToolboxPanelVisible(false);
        menu.Items.Add(hide);
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private void SetToolboxPanelVisible(bool isVisible)
    {
        ToolboxDock.IsVisible = isVisible;
    }

    private void SetExplorerPanelVisible(bool isVisible) => ExplorerDock.IsVisible = isVisible;

    private void SetPropertiesPanelVisible(bool isVisible) => PropertiesDock.IsVisible = isVisible;

    private void SetOutputPanelVisible(bool isVisible) => OutputDock.IsVisible = isVisible;

    private void ResetDockLayout()
    {
        ResetDockLayoutStructure();
        ToolboxDock.IsVisible = true;
        ExplorerDock.IsVisible = true;
        PropertiesDock.IsVisible = true;
        OutputDock.IsVisible = true;
    }

    private void ResetDockLayoutStructure()
    {
        var documents = DockManager.Layout.Descendents().OfType<LayoutDocument>().ToArray();
        foreach (var dock in new[] { ToolboxDock, ExplorerDock, PropertiesDock, OutputDock })
            if (dock.Parent is LayoutAnchorablePane pane) pane.Children.Remove(dock);
        foreach (var document in documents)
            if (document.Parent is LayoutDocumentPane pane) pane.Children.Remove(document);

        var toolboxPane = new LayoutAnchorablePane { DockWidth = new GridLength(220) };
        toolboxPane.Children.Add(ToolboxDock);
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
        rootPanel.Children.Add(toolboxPane);
        rootPanel.Children.Add(centerPaneGroup);
        rootPanel.Children.Add(rightPaneGroup);
        DockManager.Layout.RootPanel = rootPanel;
        DockManager.ActiveContent = ViewportDocument;
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (project is null) return;
        CommitScriptEditor();
        playerWindow = new PlayerWindow(project);
        playerWindow.Closed += (_, _) => playerWindow = null;
        playerWindow.Show();
    }

    private void StopPlayer_Click(object sender, RoutedEventArgs e) => playerWindow?.Close();
    private void Help_Click(object sender, RoutedEventArgs e) => StudioDialog.Show(this, "Buller Studio Help", "Buller Studio saves projects locally. Game scripts run with Lua X: use SetProperty(id, 'Position', {X=0,Y=2,Z=0}), WaitForChild(workspace, 'Part'), and part.Touched:Connect(function(other) ... end). Camera Mode is Custom or Subject.");
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

    private void SceneViewport_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (selectedTool == "Select" || isRightDragging || selected is null || selected.Type is not ("Part" or "MeshPart")) return;
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

    private void SceneViewport_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) { isRightDragging = true; lastPointer = e.GetPosition(SceneViewport); SceneViewport.Focus(); Mouse.Capture(SceneViewport); e.Handled = true; }
    private void SceneViewport_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e) { isRightDragging = false; Mouse.Capture(null); e.Handled = true; }
    private void SceneViewport_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (isTransformDragging && e.LeftButton == MouseButtonState.Pressed && selected is not null && project is not null)
        {
            var pointer = e.GetPosition(SceneViewport);
            var deltaX = (float)(pointer.X - lastPointer.X);
            var deltaY = (float)(pointer.Y - lastPointer.Y);
            lastPointer = pointer;
            if (selectedTool == "Move") { selected.Position[0] += deltaX * .02f; selected.Position[1] -= deltaY * .02f; }
            if (selectedTool == "Scale") { selected.Size[0] = Math.Max(.05f, selected.Size[0] + deltaX * .02f); selected.Size[1] = Math.Max(.05f, selected.Size[1] - deltaY * .02f); }
            if (selectedTool == "Rotate") { selected.Rotation[1] += deltaX * .5f; selected.Rotation[0] -= deltaY * .5f; }
            renderer?.UpdateNode(selected, project);
            transformChanged = true;
            return;
        }
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

    private void DeleteSelected()
    {
        if (project is null || selected is null || selected.Type is "Camera" or "Workspace") return;
        if (!StudioDialog.Confirm(this, "Delete object", $"Delete {selected.Name}?")) return;
        var removeId = selected.Id;
        if (selected.Type == "Script") CommitScriptEditor();
        GameProjectStore.RemoveNode(project.Workspace.Children, removeId);
        project.Scripts.RemoveAll(script => script.Id == removeId);
        var scriptDocument = DockManager.Layout.Descendents().OfType<LayoutDocument>().FirstOrDefault(item => item.ContentId == removeId.ToString());
        if (scriptDocument is not null)
        {
            var isActive = ReferenceEquals(DockManager.ActiveContent, scriptDocument);
            if (scriptDocument.Parent is LayoutDocumentPane pane) pane.Children.Remove(scriptDocument);
            scriptEditors.Remove(removeId);
            if (isActive) DockManager.ActiveContent = ViewportDocument;
        }
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
    private void ShowError(Exception exception) => StudioDialog.Show(this, "Buller Studio", exception.Message, StudioDialogKind.Error);

}
