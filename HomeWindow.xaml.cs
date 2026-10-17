using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BullerStudio.Models;
using BullerStudio.Services;

namespace BullerStudio;

public partial class HomeWindow : Window
{
    private readonly ObservableCollection<ProjectSummary> projects = [];
    private readonly ICollectionView projectView;
    private readonly GameBuildService buildService = new();
    private string currentSection = "Home";
    private bool includeAssets = true;

    public HomeWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => UpdateWindowStateButton();
        UpdateWindowStateButton();
        projectView = CollectionViewSource.GetDefaultView(projects);
        projectView.Filter = FilterProject;
        ProjectCards.ItemsSource = projectView;
        Loaded += async (_, _) => await RefreshProjectsAsync();
    }

    private async Task RefreshProjectsAsync()
    {
        try
        {
            projects.Clear();
            foreach (var item in await GameProjectStore.ListAsync()) projects.Add(item);
            ProjectCount.Text = $"{projects.Count} project{(projects.Count == 1 ? "" : "s")}";
            projectView.Refresh();
            UpdateEmptyState();
            StatusText.Text = "Ready";
            StatusText.ToolTip = $"Projects saved in {GameProjectStore.ProjectsRoot}";
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private bool FilterProject(object item)
    {
        if (item is not ProjectSummary project) return false;
        var query = SearchBox?.Text?.Trim();
        var matchesSearch = string.IsNullOrEmpty(query) || project.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
        var matchesSection = currentSection != "Recent" || projects.OrderByDescending(entry => entry.LastModified).Take(10).Contains(project);
        return matchesSearch && matchesSection;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        projectView.Refresh(); UpdateEmptyState();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshProjectsAsync();
    private void HomeNav_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string section) return;
        currentSection = section;
        foreach (var button in WorkspaceNav.Children.OfType<Button>().Where(button => button.Tag is not null)) button.Background = Brushes.Transparent;
        var navigationButton = WorkspaceNav.Children.OfType<Button>().FirstOrDefault(button => Equals(button.Tag, section));
        if (navigationButton is not null) navigationButton.Background = new SolidColorBrush(Color.FromRgb(48, 50, 53));
        projectView.Refresh();
        var isHome = section == "Home";
        QuickTourSection.Visibility = isHome ? Visibility.Visible : Visibility.Collapsed;
        ProjectsViewSection.Visibility = section is "Home" or "Recent" or "Projects" ? Visibility.Visible : Visibility.Collapsed;
        TemplatesViewSection.Visibility = section is "Home" or "Templates" ? Visibility.Visible : Visibility.Collapsed;
        DiscoverySection.Visibility = isHome ? Visibility.Visible : Visibility.Collapsed;
        ProjectSectionTitle.Text = section switch
        {
            "Recent" => "RECENT PROJECTS",
            "Projects" => "MY PROJECTS",
            _ => "MY RECENT EXPERIENCES"
        };
        ProjectSectionSubtitle.Text = section switch
        {
            "Recent" => "Continue working on a project you recently opened.",
            "Projects" => "All projects saved on this computer.",
            _ => "Open a local project or continue where you left off."
        };
        SeeAllProjectsButton.Visibility = section == "Projects" ? Visibility.Collapsed : Visibility.Visible;
        UpdateEmptyState();
        if (section == "Templates")
        {
            StatusText.Text = "Choose a template to start a project.";
        }
        else if (section is "Recent" or "Projects")
        {
            StatusText.Text = section == "Recent" ? "Showing recent projects." : "Showing all local projects.";
        }
        else
        {
            StatusText.Text = "Ready";
        }
        HomeScrollViewer.ScrollToTop();
    }

    private void QuickTour_Click(object sender, RoutedEventArgs e)
    {
        StudioDialog.Show(this, "Buller Studio quick tour", "Start with a template, add and arrange Parts in the viewport, then edit object values in Properties. Use Explorer to organize the scene, Ctrl+S to save, and Build to create an executable.");
    }

    private void Discovery_Click(object sender, RoutedEventArgs e)
    {
        var topic = (sender as FrameworkElement)?.Tag as string ?? "Buller Studio";
        var details = topic switch
        {
            "Lua X" => "Create a Script object and use Lua X for property changes, WaitForChild, and Touched events.",
            "Local assets" => "Import images and OBJ meshes into the project assets folder, then reference them from the Toolbox or scripts.",
            "Workspace camera" => "Every project includes a Workspace Camera. Use Custom mode for position and rotation, or Subject mode to orbit a BasePart.",
            _ => "Explore the editor tools and local project workflow."
        };
        StudioDialog.Show(this, topic, details);
    }

    private void UpdateEmptyState()
    {
        var hasVisibleProjects = projectView.Cast<object>().Any();
        EmptyState.Text = projects.Count == 0
            ? currentSection switch
            {
                "Recent" => "No recent projects yet. Projects you open will appear here.",
                "Projects" => "No projects yet. Create one from a template to get started.",
                _ => "No recent projects yet. Create one from a template below."
            }
            : "No projects match your search.";
        EmptyState.Visibility = hasVisibleProjects ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void CreateProject_Click(object sender, RoutedEventArgs e)
    {
        var template = (sender as FrameworkElement)?.Tag as string;
        var initialName = template == "Baseplate" ? "Baseplate" : template == "Empty World" ? "Empty World" : template == "Asset Playground" ? "Asset Playground" : "My Game";
        var name = StudioDialog.Prompt(this, "New project", "Project name", initialName);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var project = await GameProjectStore.CreateAsync(name);
            if (template == "Empty World")
            {
                project.Workspace.Children.RemoveAll(node => node.Type == "Part");
                await GameProjectStore.SaveAsync(project);
            }
            await OpenProjectAsync(project);
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path) return;
        try { await OpenProjectAsync(await GameProjectStore.LoadAsync(path)); }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task OpenProjectAsync(GameProject game)
    {
        var editor = new MainWindow();
        await editor.OpenProjectAsync(game);
        Application.Current.MainWindow = editor;
        editor.Show();
        Close();
    }

    private void ProjectOptions_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path) return;
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open project" };
        open.Click += async (_, _) => { try { await OpenProjectAsync(await GameProjectStore.LoadAsync(path)); } catch (Exception exception) { ShowError(exception); } };
        var includeAssetsToggle = new MenuItem { Header = "Include assets in .exe", IsCheckable = true, IsChecked = includeAssets, StaysOpenOnClick = true };
        includeAssetsToggle.Click += (_, _) => includeAssets = includeAssetsToggle.IsChecked;
        var build = new MenuItem { Header = "Build game…" };
        build.Click += async (_, _) => await BuildProjectAsync(path, includeAssets);
        var delete = new MenuItem { Header = "Delete project…" };
        delete.Click += async (_, _) => await DeleteProjectAsync(path);
        menu.Items.Add(open); menu.Items.Add(new Separator()); menu.Items.Add(includeAssetsToggle); menu.Items.Add(build); menu.Items.Add(new Separator()); menu.Items.Add(delete);
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private async Task BuildProjectAsync(string path, bool included)
    {
        try
        {
            var game = await GameProjectStore.LoadAsync(path);
            StatusText.Text = $"Building {game.Name}…";
            var executable = await buildService.BuildAsync(game, included, message => Dispatcher.Invoke(() => StatusText.Text = message));
            StudioDialog.Show(this, "Build complete", $"Game built successfully:\n{executable}", StudioDialogKind.Success);
            StatusText.Text = "Build complete";
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task DeleteProjectAsync(string path)
    {
        var target = Path.GetFullPath(path);
        var root = Path.GetFullPath(GameProjectStore.ProjectsRoot) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { StudioDialog.Show(this, "Delete project", "This project is outside the local projects folder.", StudioDialogKind.Warning); return; }
        if (!StudioDialog.Confirm(this, "Delete project", "Delete this project and its local assets?")) return;
        try { Directory.Delete(target, true); await RefreshProjectsAsync(); }
        catch (Exception exception) { ShowError(exception); }
    }

    private void OpenProjectsFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(GameProjectStore.ProjectsRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", GameProjectStore.ProjectsRoot) { UseShellExecute = true });
    }

    private void MoreOptions_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var refresh = new MenuItem { Header = "Refresh projects" };
        refresh.Click += async (_, _) => await RefreshProjectsAsync();
        var openFolder = new MenuItem { Header = "Open projects folder" };
        openFolder.Click += OpenProjectsFolder_Click;
        var about = new MenuItem { Header = "About Buller Studio" };
        about.Click += (_, _) => StudioDialog.Show(this, "About Buller Studio", "Buller Studio is a local C# game editor with a Lua X runtime.");
        menu.Items.Add(refresh); menu.Items.Add(openFolder); menu.Items.Add(new Separator()); menu.Items.Add(about);
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

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

    private void ShowError(Exception exception) => StudioDialog.Show(this, "Buller Studio", exception.Message, StudioDialogKind.Error);
}
