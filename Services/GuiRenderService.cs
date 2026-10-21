using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BullerStudio.Models;

namespace BullerStudio.Services;

public static class GuiRenderService
{
    private static readonly HashSet<string> Components = ["UICorner", "UIOutline", "UIStroke", "UIGradient", "UIPadding", "UIScale", "UIListLayout", "UIGridLayout", "UITableLayout", "UIAspectRatio"];

    public static Canvas Render(GameProject project, bool isInteractive, double viewportWidth, double viewportHeight, string? selectedId = null, Action<SceneNode>? activated = null, Action<SceneNode, string>? textChanged = null)
    {
        var canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true, IsHitTestVisible = isInteractive };
        var width = double.IsFinite(viewportWidth) && viewportWidth > 0 ? viewportWidth : 1280;
        var height = double.IsFinite(viewportHeight) && viewportHeight > 0 ? viewportHeight : 800;
        foreach (var screen in project.Gui.Children.Where(node => node.Type == "ScreenGui" && node.Visible))
        {
            AddChildren(canvas, screen, project, width, height, selectedId, activated, textChanged);
            if (screen.Id == selectedId)
            {
                var outline = new Border { Width = width, Height = height, BorderBrush = new SolidColorBrush(Color.FromRgb(74, 163, 255)), BorderThickness = new Thickness(2), IsHitTestVisible = false };
                Canvas.SetLeft(outline, 0);
                Canvas.SetTop(outline, 0);
                canvas.Children.Add(outline);
            }
        }
        return canvas;
    }

    private static void AddChildren(Canvas canvas, SceneNode parent, GameProject project, double availableWidth, double availableHeight, string? selectedId, Action<SceneNode>? activated, Action<SceneNode, string>? textChanged)
    {
        var padding = GetPadding(parent, availableWidth, availableHeight);
        var innerWidth = Math.Max(0, availableWidth - padding.Left - padding.Right);
        var innerHeight = Math.Max(0, availableHeight - padding.Top - padding.Bottom);
        var listLayout = parent.Children.FirstOrDefault(node => node.Type == "UIListLayout");
        var gridLayout = parent.Children.FirstOrDefault(node => node.Type == "UIGridLayout");
        var tableLayout = parent.Children.FirstOrDefault(node => node.Type == "UITableLayout");
        var children = GetRenderableChildren(parent).ToArray();
        if (listLayout is not null || gridLayout is not null || tableLayout is not null)
            children = children.OrderBy(node => node.LayoutOrder).ToArray();
        var grid = gridLayout is null ? default : GetGridMetrics(gridLayout, innerWidth, innerHeight, children.Length);
        var cursorX = padding.Left;
        var cursorY = padding.Top;
        var tableColumns = tableLayout is null ? 1 : Math.Clamp(tableLayout.TableColumns, 1, 24);
        var cellWidth = children.Length == 0 ? 0 : Math.Max(1, innerWidth / tableColumns);
        var rowHeight = children.Length == 0 ? 0 : children.Max(node => GetElementSize(node, innerWidth, innerHeight, tableLayout is null ? null : cellWidth).Height * GetScale(node));
        for (var index = 0; index < children.Length; index++)
        {
            var node = children[index];
            var elementSize = GetElementSize(node, innerWidth, innerHeight);
            double width = elementSize.Width;
            double height = elementSize.Height;
            var visualScale = GetScale(node);
            double left = padding.Left + node.PositionScale[0] * innerWidth + node.Position[0];
            double top = padding.Top + node.PositionScale[1] * innerHeight + node.Position[1];
            if (gridLayout is not null)
            {
                var column = gridLayout.FillDirection == "Vertical" ? index / grid.Capacity : index % grid.Capacity;
                var row = gridLayout.FillDirection == "Vertical" ? index % grid.Capacity : index / grid.Capacity;
                left = padding.Left + column * (grid.CellWidth + grid.GapX);
                top = padding.Top + row * (grid.CellHeight + grid.GapY);
                width = grid.CellWidth;
                height = grid.CellHeight;
            }
            else if (listLayout is not null)
            {
                if (listLayout.LayoutDirection == "Horizontal")
                {
                    left = cursorX;
                    top = padding.Top;
                    cursorX += width * visualScale + listLayout.LayoutSpacing;
                }
                else
                {
                    left = padding.Left;
                    top = cursorY;
                    cursorY += height * visualScale + listLayout.LayoutSpacing;
                }
            }
            else if (tableLayout is not null)
            {
                left = padding.Left + index % tableColumns * cellWidth;
                top = padding.Top + index / tableColumns * (rowHeight + tableLayout.LayoutSpacing);
                width = cellWidth;
                var aspect = node.Children.FirstOrDefault(child => child.Type == "UIAspectRatio");
                height = aspect is not null && aspect.AspectRatio > 0 ? width / aspect.AspectRatio : rowHeight;
            }
            var visual = CreateNode(node, project, width, height, selectedId, activated, textChanged);
            if (visual is null) continue;
            Canvas.SetLeft(visual, left);
            Canvas.SetTop(visual, top);
            Panel.SetZIndex(visual, node.ZIndex);
            canvas.Children.Add(visual);
        }
    }

    private static FrameworkElement? CreateNode(SceneNode node, GameProject project, double width, double height, string? selectedId, Action<SceneNode>? activated, Action<SceneNode, string>? textChanged)
    {
        var rounded = node.Children.FirstOrDefault(child => child.Type == "UICorner");
        var outline = node.Children.FirstOrDefault(child => child.Type is "UIOutline" or "UIStroke");
        var visualScale = GetScale(node);
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(visualScale, visualScale));
        transform.Children.Add(new RotateTransform(node.Rotation[2], width * visualScale * .5, height * visualScale * .5));
        var root = new Border
        {
            Tag = node,
            Width = width,
            Height = height,
            Background = Brushes.Transparent,
            BorderBrush = outline is null ? Brushes.Transparent : BrushFrom(outline.StrokeColor, Brushes.White),
            BorderThickness = outline is null ? new Thickness(0) : new Thickness(Math.Max(0, outline.StrokeThickness)),
            CornerRadius = rounded is null ? new CornerRadius(0) : new CornerRadius(Math.Max(0, rounded.CornerRadius)),
            ClipToBounds = rounded is not null,
            RenderTransform = transform
        };
        var content = new Grid();
        root.Child = content;
        var element = CreateVisual(node, project, activated, textChanged);
        if (element is not null) content.Children.Add(element);
        var childCanvas = new Canvas { ClipToBounds = node.Type != "ScrollingFrame" };
        var childExtent = GetChildrenExtent(node, width, height);
        childCanvas.Width = Math.Max(width, childExtent.Width);
        childCanvas.Height = Math.Max(height, childExtent.Height);
        if (node.Type == "ScrollingFrame")
        {
            var scroll = new ScrollViewer
            {
                Content = childCanvas,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = BackgroundFor(node),
                BorderThickness = new Thickness(0)
            };
            content.Children.Add(scroll);
        }
        else content.Children.Add(childCanvas);
        AddChildren(childCanvas, node, project, width, height, selectedId, activated, textChanged);
        if (node.Id == selectedId || node.Children.Any(child => child.Id == selectedId && Components.Contains(child.Type)))
            content.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(74, 163, 255)), BorderThickness = new Thickness(2), IsHitTestVisible = false });
        return root;
    }

    private static FrameworkElement? CreateVisual(SceneNode node, GameProject project, Action<SceneNode>? activated, Action<SceneNode, string>? textChanged)
    {
        var background = BackgroundFor(node);
        FrameworkElement? visual = node.Type switch
        {
            "Frame" => new Border { Background = background },
            "ScrollingFrame" => new Border { Background = background },
            "ImageLabel" => new Image { Source = LoadImage(project, node.Asset), Stretch = Stretch.Fill },
            "ImageButton" => new Button { Content = new Image { Source = LoadImage(project, node.Asset), Stretch = Stretch.Fill }, Background = background, BorderThickness = new Thickness(0), Padding = new Thickness(0) },
            "TextLabel" => new TextBlock { Text = node.Text, Foreground = BrushFrom(node.TextColor, Brushes.White), Background = background, FontSize = Math.Max(1, node.TextSize), TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap },
            "TextButton" => new Button { Content = node.Text, Foreground = BrushFrom(node.TextColor, Brushes.White), Background = background, FontSize = Math.Max(1, node.TextSize), Padding = new Thickness(2) },
            "TextBox" => new TextBox { Tag = node, Text = node.Text, Foreground = BrushFrom(node.TextColor, Brushes.White), Background = background, FontSize = Math.Max(1, node.TextSize), VerticalContentAlignment = VerticalAlignment.Center },
            _ => null
        };
        if (visual is Button button && activated is not null) button.Click += (_, _) => activated(node);
        if (visual is TextBox textBox && textChanged is not null) textBox.TextChanged += (_, _) => textChanged(node, textBox.Text);
        return visual;
    }

    private static bool IsRenderable(string type) => type is "Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox";

    private static IEnumerable<SceneNode> GetRenderableChildren(SceneNode parent)
    {
        foreach (var child in parent.Children)
        {
            if (!child.Visible) continue;
            if (child.Type is "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript")
            {
                foreach (var descendant in GetRenderableChildren(child)) yield return descendant;
            }
            else if (!Components.Contains(child.Type) && IsRenderable(child.Type)) yield return child;
        }
    }

    private static Size GetChildrenExtent(SceneNode parent, double availableWidth, double availableHeight)
    {
        var padding = GetPadding(parent, availableWidth, availableHeight);
        var innerWidth = Math.Max(0, availableWidth - padding.Left - padding.Right);
        var innerHeight = Math.Max(0, availableHeight - padding.Top - padding.Bottom);
        var children = GetRenderableChildren(parent).ToArray();
        if (children.Length == 0) return new Size(0, 0);
        var elementSizes = children.Select(node =>
        {
            var size = GetElementSize(node, innerWidth, innerHeight);
            var scale = GetScale(node);
            return new Size(size.Width * scale, size.Height * scale);
        }).ToArray();
        var listLayout = parent.Children.FirstOrDefault(node => node.Type == "UIListLayout");
        var gridLayout = parent.Children.FirstOrDefault(node => node.Type == "UIGridLayout");
        var tableLayout = parent.Children.FirstOrDefault(node => node.Type == "UITableLayout");
        if (gridLayout is not null)
        {
            var grid = GetGridMetrics(gridLayout, innerWidth, innerHeight, children.Length);
            var columns = gridLayout.FillDirection == "Vertical"
                ? (int)Math.Ceiling(children.Length / (double)grid.Capacity)
                : Math.Min(children.Length, grid.Capacity);
            var rows = gridLayout.FillDirection == "Vertical"
                ? Math.Min(children.Length, grid.Capacity)
                : (int)Math.Ceiling(children.Length / (double)grid.Capacity);
            var contentWidth = columns * grid.CellWidth + Math.Max(0, columns - 1) * grid.GapX;
            var contentHeight = rows * grid.CellHeight + Math.Max(0, rows - 1) * grid.GapY;
            return new Size(Math.Max(availableWidth, padding.Left + contentWidth + padding.Right),
                Math.Max(availableHeight, padding.Top + contentHeight + padding.Bottom));
        }
        if (listLayout is not null)
        {
            if (listLayout.LayoutDirection == "Horizontal")
            {
                var contentWidth = elementSizes.Sum(size => size.Width) + listLayout.LayoutSpacing * Math.Max(0, children.Length - 1);
                return new Size(Math.Max(availableWidth, padding.Left + contentWidth + padding.Right), Math.Max(availableHeight, padding.Top + elementSizes.Max(size => size.Height) + padding.Bottom));
            }
            var contentHeight = elementSizes.Sum(size => size.Height) + listLayout.LayoutSpacing * Math.Max(0, children.Length - 1);
            return new Size(Math.Max(availableWidth, padding.Left + elementSizes.Max(size => size.Width) + padding.Right), Math.Max(availableHeight, padding.Top + contentHeight + padding.Bottom));
        }
        if (tableLayout is not null)
        {
            var columns = Math.Clamp(tableLayout.TableColumns, 1, 24);
            var rows = (int)Math.Ceiling(children.Length / (double)columns);
            var cellWidth = Math.Max(1, innerWidth / columns);
            var rowHeight = children.Max(node => GetElementSize(node, innerWidth, innerHeight, cellWidth).Height * GetScale(node));
            var contentHeight = rows * rowHeight + Math.Max(0, rows - 1) * tableLayout.LayoutSpacing;
            return new Size(availableWidth, Math.Max(availableHeight, padding.Top + contentHeight + padding.Bottom));
        }
        var right = children.Select((node, index) => padding.Left + node.PositionScale[0] * innerWidth + node.Position[0] + elementSizes[index].Width).Max();
        var bottom = children.Select((node, index) => padding.Top + node.PositionScale[1] * innerHeight + node.Position[1] + elementSizes[index].Height).Max();
        return new Size(Math.Max(availableWidth, right + padding.Right), Math.Max(availableHeight, bottom + padding.Bottom));
    }

    private static Thickness GetPadding(SceneNode parent, double width, double height)
    {
        var node = parent.Children.FirstOrDefault(child => child.Type == "UIPadding");
        if (node is null) return new Thickness(0);
        static double Resolve(float[] value, double dimension) =>
            value.Length >= 2 && float.IsFinite(value[0]) && float.IsFinite(value[1])
                ? Math.Clamp(value[0] * dimension + value[1], 0, dimension)
                : 0;
        return new Thickness(
            Resolve(node.PaddingLeft, width),
            Resolve(node.PaddingTop, height),
            Resolve(node.PaddingRight, width),
            Resolve(node.PaddingBottom, height));
    }

    private static double GetScale(SceneNode node)
    {
        var component = node.Children.FirstOrDefault(child => child.Type == "UIScale");
        return component is not null && float.IsFinite(component.Scale)
            ? Math.Clamp(component.Scale, .01f, 100f)
            : 1;
    }

    private readonly record struct GridMetrics(double CellWidth, double CellHeight, double GapX, double GapY, int Capacity);

    private static GridMetrics GetGridMetrics(SceneNode layout, double width, double height, int childCount)
    {
        var cellWidth = Math.Max(1, layout.CellSizeScale[0] * width + layout.CellSize[0]);
        var cellHeight = Math.Max(1, layout.CellSizeScale[1] * height + layout.CellSize[1]);
        var gapX = Math.Max(0, layout.CellPaddingScale[0] * width + layout.CellPadding[0]);
        var gapY = Math.Max(0, layout.CellPaddingScale[1] * height + layout.CellPadding[1]);
        var horizontal = layout.FillDirection != "Vertical";
        var available = horizontal ? width : height;
        var cell = horizontal ? cellWidth : cellHeight;
        var gap = horizontal ? gapX : gapY;
        var naturalCapacity = (int)Math.Min(1000, Math.Max(1, Math.Floor((available + gap) / (cell + gap))));
        var capacity = layout.FillDirectionMaxCells > 0
            ? Math.Min(naturalCapacity, Math.Clamp(layout.FillDirectionMaxCells, 1, 1000))
            : naturalCapacity;
        return new GridMetrics(cellWidth, cellHeight, gapX, gapY, Math.Max(1, Math.Min(capacity, Math.Max(1, childCount))));
    }

    private static Size GetElementSize(SceneNode node, double availableWidth, double availableHeight, double? widthOverride = null)
    {
        var width = Math.Max(1, widthOverride ?? node.SizeScale[0] * availableWidth + node.Size[0]);
        double height = Math.Max(1, node.SizeScale[1] * availableHeight + node.Size[1]);
        var aspect = node.Children.FirstOrDefault(child => child.Type == "UIAspectRatio");
        if (aspect is not null && aspect.AspectRatio > 0) height = width / aspect.AspectRatio;
        return new Size(width, height);
    }

    private static ImageSource? LoadImage(GameProject project, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        try
        {
            var path = GameProjectStore.GetSafeAssetFilePathInAssets(project, relativePath);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static Brush BrushFrom(string value, Brush fallback)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return fallback;
        }
    }

    private static Brush GradientBrush(SceneNode gradient)
    {
        var start = ColorFrom(gradient.StartColor, Colors.White);
        var end = ColorFrom(gradient.EndColor, Colors.Gray);
        var angle = gradient.Rotation[2] * Math.PI / 180;
        var dx = Math.Cos(angle) * .5;
        var dy = Math.Sin(angle) * .5;
        var brush = new LinearGradientBrush(start, end, new System.Windows.Point(.5 - dx, .5 - dy), new System.Windows.Point(.5 + dx, .5 + dy));
        brush.Freeze();
        return brush;
    }

    private static Brush BackgroundFor(SceneNode node)
    {
        var gradient = node.Children.FirstOrDefault(child => child.Type == "UIGradient" && child.Enabled);
        return gradient is null ? BrushFrom(node.Color, Brushes.Transparent) : GradientBrush(gradient);
    }

    private static Color ColorFrom(string value, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch (Exception exception) when (exception is FormatException or NotSupportedException or InvalidOperationException or ArgumentException) { return fallback; }
    }
}
