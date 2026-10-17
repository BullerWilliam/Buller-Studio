using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace BullerStudio.Services;

public enum StudioDialogKind
{
    Information,
    Success,
    Warning,
    Error
}

public static class StudioDialog
{
    public static void Show(Window owner, string title, string message, StudioDialogKind kind = StudioDialogKind.Information)
    {
        var window = CreateWindow(owner, title, 250);
        var body = CreateBody(window, title);
        body.Margin = new Thickness(20, 15, 20, 12);
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddMessage(body, message, kind);
        AddActions(body, window, "OK", null);
        window.ShowDialog();
    }

    public static bool Confirm(Window owner, string title, string message, string confirmText = "Delete")
    {
        var window = CreateWindow(owner, title, 250);
        var body = CreateBody(window, title);
        body.Margin = new Thickness(20, 15, 20, 12);
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddMessage(body, message, StudioDialogKind.Warning);
        AddActions(body, window, confirmText, "Cancel");
        return window.ShowDialog() == true;
    }

    public static string? Prompt(Window owner, string title, string label, string initialValue, string confirmText = "Create")
    {
        var window = CreateWindow(owner, title, 260);
        var body = CreateBody(window, title);
        body.Margin = new Thickness(20, 15, 20, 12);
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var labelText = new TextBlock { Text = label, Foreground = Brush("TextMuted"), Margin = new Thickness(0, 0, 0, 7) };
        body.Children.Add(labelText);
        var text = new TextBox { Text = initialValue, Padding = new Thickness(7), MinWidth = 280 };
        Grid.SetRow(text, 1);
        body.Children.Add(text);
        AddActions(body, window, confirmText, "Cancel", 3);
        window.Loaded += (_, _) => { text.SelectAll(); text.Focus(); };
        return window.ShowDialog() == true ? text.Text : null;
    }

    private static Window CreateWindow(Window owner, string title, double height)
    {
        var window = new Window
        {
            Title = title,
            Width = 480,
            Height = height,
            MinWidth = 420,
            MinHeight = height,
            MaxHeight = height,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = Brush("PanelBackground"),
            Foreground = Brush("TextPrimary"),
            Owner = owner
        };
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 34,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        });
        return window;
    }

    private static Grid CreateBody(Window window, string title)
    {
        var frame = new Border { Background = Brush("PanelBackground"), BorderBrush = Brush("PanelBorder"), BorderThickness = new Thickness(1) };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Background = Brush("AppBackground") };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleText = new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        header.Children.Add(titleText);
        var close = new Button
        {
            Content = "\uE8BB",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Style = Application.Current.FindResource("TitleBarButton") as Style,
            ToolTip = "Close"
        };
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        close.Click += (_, _) => window.Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        layout.Children.Add(header);
        var body = new Grid();
        Grid.SetRow(body, 1);
        layout.Children.Add(body);
        frame.Child = layout;
        window.Content = frame;
        return body;
    }

    private static void AddMessage(Grid body, string message, StudioDialogKind kind)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var (glyph, color) = kind switch
        {
            StudioDialogKind.Success => ("✓", Color.FromRgb(91, 191, 120)),
            StudioDialogKind.Warning => ("!", Color.FromRgb(221, 177, 74)),
            StudioDialogKind.Error => ("×", Color.FromRgb(226, 94, 89)),
            _ => ("i", Color.FromRgb(0, 162, 255))
        };
        var icon = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromArgb(34, color.R, color.G, color.B)), VerticalAlignment = VerticalAlignment.Center };
        icon.Child = new TextBlock { Text = glyph, Foreground = new SolidColorBrush(color), FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(icon);
        var text = new TextBlock { Text = message, Foreground = Brush("TextPrimary"), FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MaxHeight = 115 };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        body.Children.Add(row);
    }

    private static void AddActions(Grid body, Window window, string primaryText, string? secondaryText, int row = 1)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        if (secondaryText is not null)
        {
            var cancel = new Button { Content = secondaryText, MinWidth = 82, Margin = new Thickness(0, 0, 7, 0), IsCancel = true };
            actions.Children.Add(cancel);
        }
        var primary = new Button { Content = primaryText, MinWidth = 82, IsDefault = true, Background = Brush("Accent"), BorderBrush = Brush("Accent") };
        primary.Click += (_, _) => window.DialogResult = true;
        actions.Children.Add(primary);
        Grid.SetRow(actions, row);
        body.Children.Add(actions);
    }

    private static SolidColorBrush Brush(string resourceKey) => (SolidColorBrush)Application.Current.FindResource(resourceKey);
}
