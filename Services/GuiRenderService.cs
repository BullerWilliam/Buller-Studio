using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BullerStudio.Models;

namespace BullerStudio.Services;

public static class GuiRenderService
{
    private static readonly HashSet<string> Components = ["UICorner", "UIOutline", "UIStroke", "UIGradient", "UIPadding", "UIScale", "UIListLayout", "UIGridLayout", "UITableLayout", "UIAspectRatio", "UIAspectRatioConstraint", "UISizeConstraint", "UITextSizeConstraint", "UIFlexItem"];

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
        if (listLayout is not null && listLayout.SortOrder == "Name")
            children = children.OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
        else if (listLayout is not null || gridLayout is not null || tableLayout is not null)
            children = children.OrderBy(node => node.LayoutOrder).ToArray();
        var grid = gridLayout is null ? default : GetGridMetrics(gridLayout, innerWidth, innerHeight, children.Length);
        var listSizes = listLayout is null ? [] : GetListSizes(children, listLayout, innerWidth, innerHeight);
        var listSpacing = listLayout is null ? 0 : GetListSpacing(listLayout, innerWidth, innerHeight);
        var listCrossSize = listLayout is null || listSizes.Length == 0 ? 0 :
            children.Select((node, index) => (listLayout.LayoutDirection == "Horizontal" ? listSizes[index].Height : listSizes[index].Width) * GetScale(node)).Max();
        var cursorX = padding.Left;
        var cursorY = padding.Top;
        if (listLayout is not null && children.Length > 0)
        {
            if (listLayout.LayoutDirection == "Horizontal")
                cursorX += AlignmentOffset(innerWidth, children.Select((node, index) => listSizes[index].Width * GetScale(node)).Sum() + listSpacing * (children.Length - 1), listLayout.HorizontalAlignment);
            else
                cursorY += AlignmentOffset(innerHeight, children.Select((node, index) => listSizes[index].Height * GetScale(node)).Sum() + listSpacing * (children.Length - 1), listLayout.VerticalAlignment);
        }
        var tableColumns = tableLayout is null ? 1 : Math.Clamp(tableLayout.TableColumns, 1, 24);
        var cellWidth = children.Length == 0 ? 0 : Math.Max(1, innerWidth / tableColumns);
        var rowHeight = children.Length == 0 ? 0 : children.Max(node => GetElementSize(node, innerWidth, innerHeight, tableLayout is null ? null : cellWidth).Height * GetScale(node));
        for (var index = 0; index < children.Length; index++)
        {
            var node = children[index];
            var elementSize = listLayout is null ? GetElementSize(node, innerWidth, innerHeight) : listSizes[index];
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
                }
                else
                {
                    left = padding.Left;
                    top = cursorY;
                }
            }
            else if (tableLayout is not null)
            {
                left = padding.Left + index % tableColumns * cellWidth;
                top = padding.Top + index / tableColumns * (rowHeight + tableLayout.LayoutSpacing);
                width = cellWidth;
                var aspect = node.Children.FirstOrDefault(child => child.Type is "UIAspectRatio" or "UIAspectRatioConstraint");
                height = aspect is not null && aspect.AspectRatio > 0 ? width / aspect.AspectRatio : rowHeight;
            }
            var constrained = listLayout is not null && gridLayout is null ? new Size(width, height) : ApplyLayoutConstraints(node, width, height, innerWidth, innerHeight);
            width = constrained.Width;
            height = constrained.Height;
            if (listLayout is not null && gridLayout is null)
            {
                if (listLayout.LayoutDirection == "Horizontal")
                {
                    var itemAlignment = GetItemLineAlignment(node, listLayout.VerticalAlignment);
                    top += AlignmentOffset(innerHeight, listCrossSize, listLayout.VerticalAlignment)
                        + AlignmentOffset(listCrossSize, height * visualScale, itemAlignment);
                    cursorX += width * visualScale + listSpacing;
                }
                else
                {
                    var itemAlignment = GetItemLineAlignment(node, listLayout.HorizontalAlignment);
                    left += AlignmentOffset(innerWidth, listCrossSize, listLayout.HorizontalAlignment)
                        + AlignmentOffset(listCrossSize, width * visualScale, itemAlignment);
                    cursorY += height * visualScale + listSpacing;
                }
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
            CornerRadius = rounded is null ? new CornerRadius(0) : UICornerRadiusService.Render(rounded, width, height),
            ClipToBounds = rounded is not null,
            RenderTransform = transform
        };
        var content = new Grid();
        root.Child = content;
        var element = CreateVisual(node, project, width, height, activated, textChanged);
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
                Background = Brushes.Transparent,
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

    private static FrameworkElement? CreateVisual(SceneNode node, GameProject project, double width, double height, Action<SceneNode>? activated, Action<SceneNode, string>? textChanged)
    {
        var background = BackgroundFor(node);
        var fontSize = FontSizeFor(node, width, height);
        FrameworkElement? visual = node.Type switch
        {
            "Frame" => new Border { Background = background },
            "ScrollingFrame" => new Border { Background = background },
            "ImageLabel" => CreateImageLabel(node, project, background),
            "ImageButton" => new Button { Content = new Image { Source = ImageFor(node, project), Stretch = Stretch.Fill }, Background = background, BorderThickness = new Thickness(0), Padding = new Thickness(0) },
            "TextLabel" => new TextBlock { Text = node.Text, Foreground = ForegroundFor(node), Background = background, FontSize = fontSize, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap },
            "TextButton" => new Button { Content = node.TextScaled ? new TextBlock { Text = node.Text, FontSize = fontSize, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center } : node.Text, Foreground = ForegroundFor(node), Background = background, FontSize = fontSize, Padding = new Thickness(2) },
            "TextBox" => new TextBox { Tag = node, Text = node.Text, Foreground = ForegroundFor(node), Background = background, FontSize = fontSize, TextWrapping = node.TextScaled ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalContentAlignment = VerticalAlignment.Center },
            _ => null
        };
        if (visual is Button button && activated is not null) button.Click += (_, _) => activated(node);
        if (visual is TextBox textBox && textChanged is not null) textBox.TextChanged += (_, _) => textChanged(node, textBox.Text);
        return visual;
    }

    private static double FontSizeFor(SceneNode node, double width, double height)
    {
        if (node.Type is not ("TextLabel" or "TextButton" or "TextBox")) return 1;
        var constraint = node.Children.FirstOrDefault(child => child.Type == "UITextSizeConstraint");
        var min = constraint is null ? 1 : Math.Clamp(constraint.MinTextSize, 1, 1000);
        var max = constraint is null ? 100 : Math.Clamp(constraint.MaxTextSize, min, 1000);
        if (!node.TextScaled) return Math.Clamp(node.TextSize, min, max);
        var availableWidth = Math.Max(1, width - 8);
        var availableHeight = Math.Max(1, height - 8);
        if (string.IsNullOrEmpty(node.Text)) return Math.Clamp(availableHeight, min, max);
        var lower = min;
        var upper = max;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var candidate = (lower + upper) / 2;
            var probe = new TextBlock { Text = node.Text, FontSize = candidate, TextWrapping = TextWrapping.Wrap };
            probe.Measure(new Size(availableWidth, double.PositiveInfinity));
            if (probe.DesiredSize.Width <= availableWidth + .5 && probe.DesiredSize.Height <= availableHeight + .5) lower = candidate;
            else upper = candidate;
        }
        return lower;
    }

    private static Grid CreateImageLabel(SceneNode node, GameProject project, Brush background)
    {
        var grid = new Grid { Background = background };
        grid.Children.Add(new Image { Source = ImageFor(node, project), Stretch = Stretch.Fill });
        return grid;
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
        var listLayout = parent.Children.FirstOrDefault(node => node.Type == "UIListLayout");
        if (listLayout is not null)
            children = listLayout.SortOrder == "Name"
                ? children.OrderBy(node => node.Name, StringComparer.Ordinal).ToArray()
                : children.OrderBy(node => node.LayoutOrder).ToArray();
        var logicalSizes = listLayout is null
            ? children.Select(node => GetElementSize(node, innerWidth, innerHeight)).ToArray()
            : GetListSizes(children, listLayout, innerWidth, innerHeight);
        var elementSizes = children.Select((node, index) =>
        {
            var scale = GetScale(node);
            return new Size(logicalSizes[index].Width * scale, logicalSizes[index].Height * scale);
        }).ToArray();
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
            var ordered = children.OrderBy(node => node.LayoutOrder).ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                var column = gridLayout.FillDirection == "Vertical" ? index / grid.Capacity : index % grid.Capacity;
                var row = gridLayout.FillDirection == "Vertical" ? index % grid.Capacity : index / grid.Capacity;
                var constrained = ApplyLayoutConstraints(ordered[index], grid.CellWidth, grid.CellHeight, innerWidth, innerHeight);
                contentWidth = Math.Max(contentWidth, column * (grid.CellWidth + grid.GapX) + constrained.Width);
                contentHeight = Math.Max(contentHeight, row * (grid.CellHeight + grid.GapY) + constrained.Height);
            }
            return new Size(Math.Max(availableWidth, padding.Left + contentWidth + padding.Right),
                Math.Max(availableHeight, padding.Top + contentHeight + padding.Bottom));
        }
        if (listLayout is not null)
        {
            var listSpacing = GetListSpacing(listLayout, innerWidth, innerHeight);
            if (listLayout.LayoutDirection == "Horizontal")
            {
                var contentWidth = elementSizes.Sum(size => size.Width) + listSpacing * Math.Max(0, children.Length - 1);
                return new Size(Math.Max(availableWidth, padding.Left + contentWidth + padding.Right), Math.Max(availableHeight, padding.Top + elementSizes.Max(size => size.Height) + padding.Bottom));
            }
            var contentHeight = elementSizes.Sum(size => size.Height) + listSpacing * Math.Max(0, children.Length - 1);
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
        return ApplyLayoutConstraints(node, width, height, availableWidth, availableHeight);
    }

    private static Size ApplyLayoutConstraints(SceneNode node, double width, double height, double parentWidth, double parentHeight)
    {
        var aspect = node.Children.FirstOrDefault(child => child.Type is "UIAspectRatio" or "UIAspectRatioConstraint");
        if (aspect is not null && aspect.AspectRatio > 0)
        {
            if (aspect.Type == "UIAspectRatioConstraint")
            {
                var maxWidth = Math.Max(1, aspect.AspectType == "ScaleWithParentSize" ? parentWidth : width);
                var maxHeight = Math.Max(1, aspect.AspectType == "ScaleWithParentSize" ? parentHeight : height);
                if (aspect.DominantAxis == "Height")
                {
                    height = maxHeight;
                    width = height * aspect.AspectRatio;
                    if (width > maxWidth) { width = maxWidth; height = width / aspect.AspectRatio; }
                }
                else
                {
                    width = maxWidth;
                    height = width / aspect.AspectRatio;
                    if (height > maxHeight) { height = maxHeight; width = height * aspect.AspectRatio; }
                }
            }
            else height = width / aspect.AspectRatio;
        }
        return ApplySizeConstraint(node, width, height);
    }

    private static double AlignmentOffset(double available, double occupied, string alignment) => alignment switch
    {
        "Center" => Math.Max(0, available - occupied) / 2,
        "Right" or "Bottom" or "End" => Math.Max(0, available - occupied),
        _ => 0
    };

    private static string GetItemLineAlignment(SceneNode node, string layoutAlignment)
    {
        var item = node.Children.FirstOrDefault(child => child.Type == "UIFlexItem");
        return item?.ItemLineAlignment is { } alignment && alignment != "Automatic" ? alignment : layoutAlignment;
    }

    private static double GetListSpacing(SceneNode layout, double availableWidth, double availableHeight) =>
        Math.Max(0, layout.ListPaddingScale * (layout.LayoutDirection == "Horizontal" ? availableWidth : availableHeight) + layout.LayoutSpacing);

    private static Size[] GetListSizes(SceneNode[] children, SceneNode layout, double availableWidth, double availableHeight)
    {
        var sizes = children.Select(node => GetElementSize(node, availableWidth, availableHeight)).ToArray();
        if (sizes.Length == 0) return sizes;
        var horizontal = layout.LayoutDirection == "Horizontal";
        var capacity = horizontal ? availableWidth : availableHeight;
        var occupied = children.Select((node, index) => (horizontal ? sizes[index].Width : sizes[index].Height) * GetScale(node)).Sum()
            + GetListSpacing(layout, availableWidth, availableHeight) * (sizes.Length - 1);
        var free = capacity - occupied;
        if (Math.Abs(free) > .001)
        {
            var weights = children.Select(node =>
            {
                var flex = node.Children.FirstOrDefault(child => child.Type == "UIFlexItem");
                if (flex is null) return 0d;
                return flex.FlexMode switch
                {
                    "Fill" => 1d,
                    "Grow" when free > 0 => 1d,
                    "Shrink" when free < 0 => 1d,
                    "Custom" when free > 0 && float.IsFinite(flex.GrowRatio) => Math.Max(0, flex.GrowRatio),
                    "Custom" when free < 0 && float.IsFinite(flex.ShrinkRatio) => Math.Max(0, flex.ShrinkRatio),
                    _ => 0d
                };
            }).ToArray();
            var totalWeight = weights.Sum();
            if (totalWeight > 0)
            {
                var adjustments = new double[sizes.Length];
                if (free < 0)
                {
                    var remaining = -free;
                    var capacities = children.Select((node, index) =>
                    {
                        var minimum = node.Children.FirstOrDefault(child => child.Type == "UISizeConstraint")?.MinSize;
                        var limit = minimum is { Length: >= 2 } ? minimum[horizontal ? 0 : 1] : 1;
                        if (!double.IsFinite(limit)) limit = 1;
                        return Math.Max(0, ((horizontal ? sizes[index].Width : sizes[index].Height) - Math.Max(1, limit)) * GetScale(node));
                    }).ToArray();
                    for (var round = 0; round < sizes.Length && remaining > .001; round++)
                    {
                        var active = Enumerable.Range(0, sizes.Length)
                            .Where(index => weights[index] > 0 && adjustments[index] < capacities[index] - .001).ToArray();
                        var activeWeight = active.Sum(index => weights[index]);
                        if (activeWeight <= 0) break;
                        var consumed = 0d;
                        foreach (var index in active)
                        {
                            var shrink = Math.Min(remaining * weights[index] / activeWeight, capacities[index] - adjustments[index]);
                            adjustments[index] += shrink;
                            consumed += shrink;
                        }
                        if (consumed <= .001) break;
                        remaining -= consumed;
                    }
                }
                for (var index = 0; index < sizes.Length; index++)
                {
                    if (weights[index] <= 0) continue;
                    var scale = GetScale(children[index]);
                    var basis = horizontal ? sizes[index].Width : sizes[index].Height;
                    var deltaPixels = free < 0 ? -adjustments[index] : free * weights[index] / totalWeight;
                    var next = Math.Max(1, basis + deltaPixels / scale);
                    sizes[index] = horizontal
                        ? ApplyLayoutConstraints(children[index], next, sizes[index].Height, availableWidth, availableHeight)
                        : ApplyLayoutConstraints(children[index], sizes[index].Width, next, availableWidth, availableHeight);
                }
            }
        }
        var lineCross = children.Select((node, index) => (horizontal ? sizes[index].Height : sizes[index].Width) * GetScale(node)).Max();
        for (var index = 0; index < sizes.Length; index++)
        {
            if (GetItemLineAlignment(children[index], horizontal ? layout.VerticalAlignment : layout.HorizontalAlignment) != "Stretch") continue;
            var stretched = Math.Max(1, lineCross / GetScale(children[index]));
            sizes[index] = horizontal
                ? ApplyLayoutConstraints(children[index], sizes[index].Width, stretched, availableWidth, availableHeight)
                : ApplyLayoutConstraints(children[index], stretched, sizes[index].Height, availableWidth, availableHeight);
        }
        return sizes;
    }

    private static Size ApplySizeConstraint(SceneNode node, double width, double height)
    {
        var constraint = node.Children.FirstOrDefault(child => child.Type == "UISizeConstraint");
        if (constraint is null) return new Size(width, height);
        var min = constraint.MinSize;
        var max = constraint.MaxSize;
        if (min.Length < 2 || max.Length < 2) return new Size(width, height);
        return new Size(Math.Clamp(width, Math.Max(0, min[0]), Math.Max(min[0], max[0])),
            Math.Clamp(height, Math.Max(0, min[1]), Math.Max(min[1], max[1])));
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

    private static Brush GradientBrush(SceneNode gradient, Color baseColor)
    {
        var start = Multiply(baseColor, ColorFrom(gradient.StartColor, Colors.White));
        var end = Multiply(baseColor, ColorFrom(gradient.EndColor, Colors.Gray));
        var angle = gradient.Rotation[2] * Math.PI / 180;
        var dx = Math.Cos(angle) * .5;
        var dy = Math.Sin(angle) * .5;
        var brush = new LinearGradientBrush(start, end, new System.Windows.Point(.5 - dx, .5 - dy), new System.Windows.Point(.5 + dx, .5 + dy));
        brush.Freeze();
        return brush;
    }

    private static Brush BackgroundFor(SceneNode node)
    {
        var gradient = ActiveGradient(node);
        var baseColor = WithTransparency(ColorFrom(node.Color, Colors.Transparent), node.BackgroundTransparency);
        return gradient is null ? new SolidColorBrush(baseColor) : GradientBrush(gradient, baseColor);
    }

    private static Brush ForegroundFor(SceneNode node)
    {
        var gradient = ActiveGradient(node);
        var baseColor = WithTransparency(ColorFrom(node.TextColor, Colors.White), node.TextTransparency);
        return gradient is null ? new SolidColorBrush(baseColor) : GradientBrush(gradient, baseColor);
    }

    private static Color WithTransparency(Color color, float transparency) =>
        Color.FromArgb((byte)Math.Round(color.A * (1 - Math.Clamp(transparency, 0, 1))), color.R, color.G, color.B);

    private static SceneNode? ActiveGradient(SceneNode node) =>
        node.Type is "Frame" or "TextLabel" or "TextButton" or "ImageLabel" or "ImageButton"
            ? node.Children.FirstOrDefault(child => child.Type == "UIGradient" && child.Enabled)
            : null;

    private static Color Multiply(Color first, Color second)
    {
        static byte Scale(byte a, byte b) => (byte)((a * b + 127) / 255);
        return Color.FromArgb(Scale(first.A, second.A), Scale(first.R, second.R),
            Scale(first.G, second.G), Scale(first.B, second.B));
    }

    private static ImageSource? ImageFor(SceneNode node, GameProject project)
    {
        var source = LoadImage(project, node.Asset);
        if (source is not BitmapSource image) return source;
        var gradient = ActiveGradient(node);
        var baseColor = ColorFrom(node.Color, Colors.White);
        baseColor = WithTransparency(baseColor, node.ImageTransparency);
        if (gradient is null && baseColor == Colors.White) return source;
        var width = image.PixelWidth;
        var height = image.PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height * 4 > 256L * 1024 * 1024) return source;
        var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        var start = gradient is null ? Colors.White : ColorFrom(gradient.StartColor, Colors.White);
        var end = gradient is null ? Colors.White : ColorFrom(gradient.EndColor, Colors.Gray);
        var angle = gradient is null ? 0 : gradient.Rotation[2] * Math.PI / 180;
        var dx = Math.Cos(angle);
        var dy = Math.Sin(angle);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = y * stride + x * 4;
            var u = (x + .5) / width - .5;
            var v = (y + .5) / height - .5;
            var t = Math.Clamp(u * dx + v * dy + .5, 0, 1);
            static byte Tint(byte pixel, byte baseChannel, double gradientChannel) =>
                (byte)Math.Clamp(Math.Round(pixel * baseChannel * gradientChannel / 65025), 0, 255);
            pixels[offset] = Tint(pixels[offset], baseColor.B, start.B + (end.B - start.B) * t);
            pixels[offset + 1] = Tint(pixels[offset + 1], baseColor.G, start.G + (end.G - start.G) * t);
            pixels[offset + 2] = Tint(pixels[offset + 2], baseColor.R, start.R + (end.R - start.R) * t);
            pixels[offset + 3] = Tint(pixels[offset + 3], baseColor.A, start.A + (end.A - start.A) * t);
        }
        var tinted = BitmapSource.Create(width, height, image.DpiX, image.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        tinted.Freeze();
        return tinted;
    }

    private static Color ColorFrom(string value, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch (Exception exception) when (exception is FormatException or NotSupportedException or InvalidOperationException or ArgumentException) { return fallback; }
    }
}
