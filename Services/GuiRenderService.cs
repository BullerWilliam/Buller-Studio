using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BullerStudio.Models;

namespace BullerStudio.Services;

public static class GuiRenderService
{
    private static readonly HashSet<string> Components = ["UICorner", "UIOutline", "UIStroke", "UIListLayout", "UITableLayout", "UIAspectRatio"];

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
        var children = GetRenderableChildren(parent).ToArray();
        var listLayout = parent.Children.FirstOrDefault(node => node.Type == "UIListLayout");
        var tableLayout = parent.Children.FirstOrDefault(node => node.Type == "UITableLayout");
        var cursorX = 0d;
        var cursorY = 0d;
        var tableColumns = tableLayout is null ? 1 : Math.Clamp(tableLayout.TableColumns, 1, 24);
        var cellWidth = children.Length == 0 ? 0 : Math.Max(1, availableWidth / tableColumns);
        var rowHeight = children.Length == 0 ? 0 : children.Max(node => GetElementSize(node, availableWidth, availableHeight, tableLayout is null ? null : cellWidth).Height);
        for (var index = 0; index < children.Length; index++)
        {
            var node = children[index];
            var elementSize = GetElementSize(node, availableWidth, availableHeight);
            double width = elementSize.Width;
            double height = elementSize.Height;
            double left = node.PositionScale[0] * availableWidth + node.Position[0];
            double top = node.PositionScale[1] * availableHeight + node.Position[1];
            if (listLayout is not null)
            {
                if (listLayout.LayoutDirection == "Horizontal")
                {
                    left = cursorX;
                    top = 0;
                    cursorX += width + listLayout.LayoutSpacing;
                }
                else
                {
                    left = 0;
                    top = cursorY;
                    cursorY += height + listLayout.LayoutSpacing;
                }
            }
            else if (tableLayout is not null)
            {
                left = index % tableColumns * cellWidth;
                top = index / tableColumns * (rowHeight + tableLayout.LayoutSpacing);
                width = cellWidth;
                var aspect = node.Children.FirstOrDefault(child => child.Type == "UIAspectRatio");
                height = aspect is not null && aspect.AspectRatio > 0 ? width / aspect.AspectRatio : rowHeight;
            }
            var visual = CreateNode(node, project, width, height, selectedId, activated, textChanged);
            if (visual is null) continue;
            Canvas.SetLeft(visual, left);
            Canvas.SetTop(visual, top);
            canvas.Children.Add(visual);
        }
    }

    private static FrameworkElement? CreateNode(SceneNode node, GameProject project, double width, double height, string? selectedId, Action<SceneNode>? activated, Action<SceneNode, string>? textChanged)
    {
        var rounded = node.Children.FirstOrDefault(child => child.Type == "UICorner");
        var outline = node.Children.FirstOrDefault(child => child.Type is "UIOutline" or "UIStroke");
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
            RenderTransformOrigin = new System.Windows.Point(.5, .5),
            RenderTransform = new RotateTransform(node.Rotation[2])
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
                Background = BrushFrom(node.Color, Brushes.Transparent),
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
        var background = BrushFrom(node.Color, Brushes.Transparent);
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
            if (child.Type == "Folder")
            {
                foreach (var descendant in GetRenderableChildren(child)) yield return descendant;
            }
            else if (!Components.Contains(child.Type) && IsRenderable(child.Type)) yield return child;
        }
    }

    private static Size GetChildrenExtent(SceneNode parent, double availableWidth, double availableHeight)
    {
        var children = GetRenderableChildren(parent).ToArray();
        if (children.Length == 0) return new Size(0, 0);
        var elementSizes = children.Select(node => GetElementSize(node, availableWidth, availableHeight)).ToArray();
        var listLayout = parent.Children.FirstOrDefault(node => node.Type == "UIListLayout");
        var tableLayout = parent.Children.FirstOrDefault(node => node.Type == "UITableLayout");
        if (listLayout is not null)
        {
            if (listLayout.LayoutDirection == "Horizontal")
            {
                var contentWidth = elementSizes.Sum(size => size.Width) + listLayout.LayoutSpacing * Math.Max(0, children.Length - 1);
                return new Size(Math.Max(availableWidth, contentWidth), Math.Max(availableHeight, elementSizes.Max(size => size.Height)));
            }
            var contentHeight = elementSizes.Sum(size => size.Height) + listLayout.LayoutSpacing * Math.Max(0, children.Length - 1);
            return new Size(Math.Max(availableWidth, elementSizes.Max(size => size.Width)), Math.Max(availableHeight, contentHeight));
        }
        if (tableLayout is not null)
        {
            var columns = Math.Clamp(tableLayout.TableColumns, 1, 24);
            var rows = (int)Math.Ceiling(children.Length / (double)columns);
            var cellWidth = Math.Max(1, availableWidth / columns);
            var rowHeight = children.Max(node => GetElementSize(node, availableWidth, availableHeight, cellWidth).Height);
            var contentHeight = rows * rowHeight + Math.Max(0, rows - 1) * tableLayout.LayoutSpacing;
            return new Size(availableWidth, Math.Max(availableHeight, contentHeight));
        }
        var right = children.Select((node, index) => node.PositionScale[0] * availableWidth + node.Position[0] + elementSizes[index].Width).Max();
        var bottom = children.Select((node, index) => node.PositionScale[1] * availableHeight + node.Position[1] + elementSizes[index].Height).Max();
        return new Size(Math.Max(availableWidth, right), Math.Max(availableHeight, bottom));
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
}
