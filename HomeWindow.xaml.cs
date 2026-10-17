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

    public HomeWindow()
    {
        InitializeComponent();
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
            EmptyState.Visibility = projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            projectView.Refresh();
            StatusText.Text = $"Projects saved in {GameProjectStore.ProjectsRoot}";
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private bool FilterProject(object item)
    {
        if (item is not ProjectSummary project) return false;
        var query = SearchBox?.Text?.Trim();
        return string.IsNullOrEmpty(query) || project.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => projectView.Refresh();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshProjectsAsync();
    private void HomeNav_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: "Templates" }) CreateProject_Click(sender, e); else projectView.Refresh(); }

    private async void CreateProject_Click(object sender, RoutedEventArgs e)
    {
        var template = (sender as FrameworkElement)?.Tag as string;
        var initialName = template == "Baseplate" ? "Baseplate" : template == "Empty World" ? "Empty World" : template == "Asset Playground" ? "Asset Playground" : "My Game";
        var name = Prompt("New project", "Project name", initialName);
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
        var buildIncluded = new MenuItem { Header = "Build game  ·  Included assets" };
        buildIncluded.Click += async (_, _) => await BuildProjectAsync(path, true);
        var buildExternal = new MenuItem { Header = "Build game  ·  External assets" };
        buildExternal.Click += async (_, _) => await BuildProjectAsync(path, false);
        var delete = new MenuItem { Header = "Delete project…" };
        delete.Click += async (_, _) => await DeleteProjectAsync(path);
        menu.Items.Add(open); menu.Items.Add(new Separator()); menu.Items.Add(buildIncluded); menu.Items.Add(buildExternal); menu.Items.Add(new Separator()); menu.Items.Add(delete);
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
            MessageBox.Show(this, $"Game built successfully:\n{executable}", "Build complete", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = "Build complete";
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task DeleteProjectAsync(string path)
    {
        var target = Path.GetFullPath(path);
        var root = Path.GetFullPath(GameProjectStore.ProjectsRoot) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(this, "This project is outside the local projects folder."); return; }
        if (MessageBox.Show(this, "Delete this project and its local assets?", "Delete project", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { Directory.Delete(target, true); await RefreshProjectsAsync(); }
        catch (Exception exception) { ShowError(exception); }
    }

    private void OpenProjectsFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(GameProjectStore.ProjectsRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", GameProjectStore.ProjectsRoot) { UseShellExecute = true });
    }

    private void ShowError(Exception exception) => MessageBox.Show(this, exception.Message, "Buller Studio", MessageBoxButton.OK, MessageBoxImage.Error);

    private static string? Prompt(string title, string label, string initial)
    {
        var dialog = new Window { Title = title, Width = 380, Height = 162, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Owner = Application.Current.MainWindow, Background = new SolidColorBrush(Color.FromRgb(34, 35, 37)), Foreground = Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 7) });
        var text = new TextBox { Text = initial, Padding = new Thickness(7) }; panel.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 11, 0, 0) };
        var ok = new Button { Content = "Create", IsDefault = true, Padding = new Thickness(14, 5, 14, 5) }; ok.Click += (_, _) => dialog.DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(7, 0, 0, 0), Padding = new Thickness(14, 5, 14, 5) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons); dialog.Content = panel;
        return dialog.ShowDialog() == true ? text.Text : null;
    }
}
