using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using Vector3 = System.Numerics.Vector3;
using BullerStudio.Models;

namespace BullerStudio.Services;

public static class GuiRenderService
{
    private static readonly HashSet<string> Components = ["UICorner", "UIOutline", "UIStroke", "UIGradient", "UIShadow", "UIPadding", "UIScale", "UIListLayout", "UIGridLayout", "UITableLayout", "UIAspectRatio", "UIAspectRatioConstraint", "UISizeConstraint", "UITextSizeConstraint", "UIFlexItem"];

    public static Canvas Render(GameProject project, bool isInteractive, double viewportWidth, double viewportHeight, string? selectedId = null, Action<SceneNode>? activated = null, Action<SceneNode, string>? textChanged = null, SceneNode? screenGuiRoot = null)
    {
        var canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true, IsHitTestVisible = isInteractive };
        var width = double.IsFinite(viewportWidth) && viewportWidth > 0 ? viewportWidth : 1280;
        var height = double.IsFinite(viewportHeight) && viewportHeight > 0 ? viewportHeight : 800;
        foreach (var screen in (screenGuiRoot ?? project.Gui).Children.Where(node => node.Type == "ScreenGui" && node.Enabled && node.Visible).OrderBy(node => node.DisplayOrder))
        {
            var screenCanvas = new Canvas { Width = width, Height = height, Background = Brushes.Transparent };
            AddChildren(screenCanvas, screen, project, width, height, selectedId, activated, textChanged);
            if (screen.Id == selectedId)
            {
                var outline = new Border { Width = width, Height = height, BorderBrush = new SolidColorBrush(Color.FromRgb(74, 163, 255)), BorderThickness = new Thickness(2), IsHitTestVisible = false };
                Canvas.SetLeft(outline, 0);
                Canvas.SetTop(outline, 0);
                screenCanvas.Children.Add(outline);
            }
            Panel.SetZIndex(screenCanvas, screen.DisplayOrder);
            canvas.Children.Add(screenCanvas);
        }
        return canvas;
    }

    public static void AddBillboards(Canvas canvas, GameProject project, Vector3 cameraPosition, Vector3 lookDirection,
        Vector3 upDirection, float fieldOfView, double viewportWidth, double viewportHeight, bool isInteractive,
        Action<SceneNode>? activated = null, Action<SceneNode, string>? textChanged = null, string? selectedId = null)
    {
        var width = double.IsFinite(viewportWidth) && viewportWidth > 0 ? viewportWidth : 1280;
        var height = double.IsFinite(viewportHeight) && viewportHeight > 0 ? viewportHeight : 800;
        var nodes = GameProjectStore.Flatten(project.Workspace.Children);
        foreach (var billboard in nodes.Where(node => node.Type == "BillboardGui"))
        {
            if (!billboard.Enabled || ResolveBillboardAdornee(project, billboard) is not { } adornee) continue;
            var billboardWidth = Math.Clamp(billboard.SizeScale[0] * width + billboard.Size[0], 1, 4000);
            var billboardHeight = Math.Clamp(billboard.SizeScale[1] * height + billboard.Size[1], 1, 4000);
            var element = new Canvas
            {
                Tag = billboard,
                Width = billboardWidth,
                Height = billboardHeight,
                Background = Brushes.Transparent,
                IsHitTestVisible = isInteractive
            };
            AddChildren(element, billboard, project, billboardWidth, billboardHeight, selectedId, activated, textChanged);
            Panel.SetZIndex(element, billboard.AlwaysOnTop ? int.MaxValue : int.MinValue);
            canvas.Children.Add(element);
        }
        UpdateBillboardPositions(canvas, project, cameraPosition, lookDirection, upDirection, fieldOfView, width, height);
    }

    public static void UpdateBillboardPositions(Canvas canvas, GameProject project, Vector3 cameraPosition, Vector3 lookDirection,
        Vector3 upDirection, float fieldOfView, double viewportWidth, double viewportHeight)
    {
        var width = double.IsFinite(viewportWidth) && viewportWidth > 0 ? viewportWidth : 1280;
        var height = double.IsFinite(viewportHeight) && viewportHeight > 0 ? viewportHeight : 800;
        foreach (var visual in canvas.Children.OfType<Canvas>().Where(item => item.Tag is SceneNode { Type: "BillboardGui" }))
        {
            var billboard = (SceneNode)visual.Tag;
            var adornee = ResolveBillboardAdornee(project, billboard);
            if (!billboard.Enabled || adornee is null || !TryProjectBillboard(adornee.Position, cameraPosition, lookDirection,
                    upDirection, fieldOfView, width, height, out var screenX, out var screenY, out var distance)
                || billboard.MaxDistance > 0 && distance > billboard.MaxDistance
                || !billboard.AlwaysOnTop && IsBillboardOccluded(project, adornee, cameraPosition))
            {
                visual.Visibility = Visibility.Collapsed;
                continue;
            }
            visual.Visibility = Visibility.Visible;
            Canvas.SetLeft(visual, screenX - visual.Width / 2);
            Canvas.SetTop(visual, screenY - visual.Height / 2);
        }
    }

    public static void AddSurfaceGuis(Canvas canvas, GameProject project, Vector3 cameraPosition, Vector3 lookDirection,
        Vector3 upDirection, float fieldOfView, double viewportWidth, double viewportHeight, bool isInteractive,
        Action<SceneNode>? activated = null, Action<SceneNode, string>? textChanged = null, string? selectedId = null)
    {
        var width = double.IsFinite(viewportWidth) && viewportWidth > 0 ? viewportWidth : 1280;
        var height = double.IsFinite(viewportHeight) && viewportHeight > 0 ? viewportHeight : 800;
        foreach (var surfaceGui in GameProjectStore.Flatten(project.Workspace.Children).Where(node => node.Type == "SurfaceGui" && node.Enabled))
        {
            if (ResolveWorldGuiAdornee(project, surfaceGui) is not { } adornee || !TryGetSurfaceFrame(adornee, surfaceGui.Face, out var frame)) continue;
            var pixelsPerStud = Math.Clamp(surfaceGui.PixelsPerStud, 1, 1000);
            var guiWidth = Math.Clamp(frame.Width * pixelsPerStud, 1, 4000);
            var guiHeight = Math.Clamp(frame.Height * pixelsPerStud, 1, 4000);
            var element = new Canvas
            {
                Tag = surfaceGui,
                Width = guiWidth,
                Height = guiHeight,
                Background = Brushes.Transparent,
                IsHitTestVisible = isInteractive,
                RenderTransformOrigin = new Point(.5, .5)
            };
            AddChildren(element, surfaceGui, project, guiWidth, guiHeight, selectedId, activated, textChanged);
            Panel.SetZIndex(element, surfaceGui.AlwaysOnTop ? int.MaxValue : int.MinValue + 1);
            canvas.Children.Add(element);
        }
        UpdateSurfaceGuiPositions(canvas, project, cameraPosition, lookDirection, upDirection, fieldOfView, width, height);
    }

    public static void UpdateSurfaceGuiPositions(Canvas canvas, GameProject project, Vector3 cameraPosition, Vector3 lookDirection,
        Vector3 upDirection, float fieldOfView, double viewportWidth, double viewportHeight)
    {
        var width = double.IsFinite(viewportWidth) && viewportWidth > 0 ? viewportWidth : 1280;
        var height = double.IsFinite(viewportHeight) && viewportHeight > 0 ? viewportHeight : 800;
        foreach (var visual in canvas.Children.OfType<Canvas>().Where(item => item.Tag is SceneNode { Type: "SurfaceGui" }))
        {
            var surfaceGui = (SceneNode)visual.Tag;
            var adornee = ResolveWorldGuiAdornee(project, surfaceGui);
            if (!surfaceGui.Enabled || adornee is null || !TryGetSurfaceFrame(adornee, surfaceGui.Face, out var frame)
                || !TryProjectBillboard([frame.Center.X, frame.Center.Y, frame.Center.Z], cameraPosition, lookDirection,
                    upDirection, fieldOfView, width, height, out var screenX, out var screenY, out var distance)
                || surfaceGui.MaxDistance > 0 && distance > surfaceGui.MaxDistance
                || Vector3.Dot(frame.Normal, cameraPosition - frame.Center) <= 0
                || !surfaceGui.AlwaysOnTop && IsWorldGuiOccluded(project, adornee, frame.Center, cameraPosition))
            {
                visual.Visibility = Visibility.Collapsed;
                continue;
            }
            if (!TryProjectBillboard([frame.Center.X + frame.Right.X * frame.Width, frame.Center.Y + frame.Right.Y * frame.Width, frame.Center.Z + frame.Right.Z * frame.Width],
                    cameraPosition, lookDirection, upDirection, fieldOfView, width, height, out var rightX, out var rightY, out _)
                || !TryProjectBillboard([frame.Center.X + frame.Up.X * frame.Height, frame.Center.Y + frame.Up.Y * frame.Height, frame.Center.Z + frame.Up.Z * frame.Height],
                    cameraPosition, lookDirection, upDirection, fieldOfView, width, height, out var upX, out var upY, out _))
            {
                visual.Visibility = Visibility.Collapsed;
                continue;
            }
            var projectedWidth = Math.Sqrt(Math.Pow(rightX - screenX, 2) + Math.Pow(rightY - screenY, 2)) * 2;
            var projectedHeight = Math.Sqrt(Math.Pow(upX - screenX, 2) + Math.Pow(upY - screenY, 2)) * 2;
            if (!double.IsFinite(projectedWidth) || !double.IsFinite(projectedHeight) || projectedWidth < .5 || projectedHeight < .5)
            {
                visual.Visibility = Visibility.Collapsed;
                continue;
            }
            var rotation = Math.Atan2(rightY - screenY, rightX - screenX) * 180 / Math.PI;
            visual.RenderTransform = new TransformGroup
            {
                Children =
                [
                    new ScaleTransform(projectedWidth / visual.Width, projectedHeight / visual.Height),
                    new RotateTransform(rotation)
                ]
            };
            visual.Visibility = Visibility.Visible;
            Canvas.SetLeft(visual, screenX - visual.Width / 2);
            Canvas.SetTop(visual, screenY - visual.Height / 2);
        }
    }

    private static SceneNode? ResolveWorldGuiAdornee(GameProject project, SceneNode gui)
    {
        var nodes = GameProjectStore.Flatten(project.Workspace.Children);
        if (!string.IsNullOrEmpty(gui.Adornee)) return nodes.FirstOrDefault(node => node.Id == gui.Adornee && SceneNodeTypes.IsBasePart(node));
        return nodes.FirstOrDefault(node => node.Children.Any(child => child.Id == gui.Id) && SceneNodeTypes.IsBasePart(node));
    }

    private static bool TryGetSurfaceFrame(SceneNode adornee, string face, out (Vector3 Center, Vector3 Right, Vector3 Up, Vector3 Normal, float Width, float Height) frame)
    {
        var local = face switch
        {
            "Back" => (Right: -Vector3.UnitX, Up: Vector3.UnitY, Normal: Vector3.UnitZ),
            "Left" => (Right: Vector3.UnitZ, Up: Vector3.UnitY, Normal: -Vector3.UnitX),
            "Right" => (Right: -Vector3.UnitZ, Up: Vector3.UnitY, Normal: Vector3.UnitX),
            "Top" => (Right: Vector3.UnitX, Up: -Vector3.UnitZ, Normal: Vector3.UnitY),
            "Bottom" => (Right: Vector3.UnitX, Up: Vector3.UnitZ, Normal: -Vector3.UnitY),
            _ => (Right: Vector3.UnitX, Up: Vector3.UnitY, Normal: -Vector3.UnitZ)
        };
        var radians = new Vector3(adornee.Rotation[0], adornee.Rotation[1], adornee.Rotation[2]) * (MathF.PI / 180);
        var rotation = System.Numerics.Matrix4x4.CreateRotationZ(radians.Z) * System.Numerics.Matrix4x4.CreateRotationX(radians.X) * System.Numerics.Matrix4x4.CreateRotationY(radians.Y);
        var right = Vector3.Normalize(Vector3.TransformNormal(local.Right, rotation));
        var up = Vector3.Normalize(Vector3.TransformNormal(local.Up, rotation));
        var normal = Vector3.Normalize(Vector3.TransformNormal(local.Normal, rotation));
        var size = new Vector3(Math.Abs(adornee.Size[0]), Math.Abs(adornee.Size[1]), Math.Abs(adornee.Size[2]));
        var halfDepth = (Math.Abs(local.Normal.X) * size.X + Math.Abs(local.Normal.Y) * size.Y + Math.Abs(local.Normal.Z) * size.Z) * .5f;
        var faceWidth = Math.Abs(local.Right.X) * size.X + Math.Abs(local.Right.Y) * size.Y + Math.Abs(local.Right.Z) * size.Z;
        var faceHeight = Math.Abs(local.Up.X) * size.X + Math.Abs(local.Up.Y) * size.Y + Math.Abs(local.Up.Z) * size.Z;
        var center = new Vector3(adornee.Position[0], adornee.Position[1], adornee.Position[2]) + normal * halfDepth;
        frame = (center, right, up, normal, faceWidth, faceHeight);
        return faceWidth > 0 && faceHeight > 0;
    }

    private static SceneNode? ResolveBillboardAdornee(GameProject project, SceneNode billboard)
    {
        var nodes = GameProjectStore.Flatten(project.Workspace.Children);
        if (billboard.Adornee.Length > 0)
            return nodes.FirstOrDefault(node => node.Id == billboard.Adornee && SceneNodeTypes.IsBasePart(node));
        return nodes.FirstOrDefault(node => node.Children.Any(child => child.Id == billboard.Id) && SceneNodeTypes.IsBasePart(node));
    }

    private static bool TryProjectBillboard(float[] position, Vector3 cameraPosition, Vector3 lookDirection, Vector3 upDirection,
        float fieldOfView, double viewportWidth, double viewportHeight, out double screenX, out double screenY, out float distance)
    {
        screenX = screenY = 0;
        distance = 0;
        if (position.Length < 3 || viewportWidth <= 0 || viewportHeight <= 0 || lookDirection.LengthSquared() < .000001f) return false;
        var look = Vector3.Normalize(lookDirection);
        var right = Vector3.Cross(look, upDirection);
        if (right.LengthSquared() < .000001f) return false;
        right = Vector3.Normalize(right);
        var screenUp = Vector3.Normalize(Vector3.Cross(right, look));
        var relative = new Vector3(position[0], position[1], position[2]) - cameraPosition;
        distance = relative.Length();
        var depth = Vector3.Dot(relative, look);
        if (depth <= .01f || !float.IsFinite(depth)) return false;
        var tangent = Math.Tan(Math.Clamp(fieldOfView, 1, 179) * Math.PI / 360);
        var aspect = viewportWidth / viewportHeight;
        var horizontal = Vector3.Dot(relative, right) / ((double)depth * tangent * aspect);
        var vertical = Vector3.Dot(relative, screenUp) / ((double)depth * tangent);
        screenX = viewportWidth * (.5 + horizontal * .5);
        screenY = viewportHeight * (.5 - vertical * .5);
        return double.IsFinite(screenX) && double.IsFinite(screenY);
    }

    private static bool IsBillboardOccluded(GameProject project, SceneNode adornee, Vector3 cameraPosition)
    {
        return IsWorldGuiOccluded(project, adornee, new Vector3(adornee.Position[0], adornee.Position[1], adornee.Position[2]), cameraPosition);
    }

    private static bool IsWorldGuiOccluded(GameProject project, SceneNode adornee, Vector3 targetPosition, Vector3 cameraPosition)
    {
        var toTarget = targetPosition - cameraPosition;
        var maxDistance = toTarget.Length();
        if (maxDistance < .001f) return false;
        var ray = toTarget / maxDistance;
        foreach (var part in GameProjectStore.Flatten(project.Workspace.Children).Where(SceneNodeTypes.IsBasePart))
        {
            if (part.Id == adornee.Id || !part.CanQuery) continue;
            var hit = CollisionGeometry.Raycast(part, cameraPosition, ray * maxDistance);
            if (hit is { } rayHit && rayHit.Distance + .05f < maxDistance) return true;
        }
        return false;
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
            left -= Math.Clamp(node.AnchorPoint[0], 0, 1) * width * visualScale;
            top -= Math.Clamp(node.AnchorPoint[1], 0, 1) * height * visualScale;
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
        var textShadows = node.Children.Where(child => child.Type == "UIShadow" && child.Enabled && child.ShadowMode == "Text")
            .OrderBy(child => child.ZIndex).ToArray();
        if (textShadows.Length > 0 && parentSupportsTextShadow(node))
        {
            content.Children.Add(new Border { Background = BackgroundFor(node), IsHitTestVisible = false });
            var textLayer = new Canvas { Width = width, Height = height, IsHitTestVisible = false };
            foreach (var shadow in textShadows)
                textLayer.Children.Add(CreateShadowVisual(node, shadow, width, height, rounded));
            content.Children.Add(textLayer);
            if (element is TextBlock textBlock) textBlock.Background = Brushes.Transparent;
            else if (element is Control control) control.Background = Brushes.Transparent;
            else if (element is Grid textBoxGrid) textBoxGrid.Background = Brushes.Transparent;
        }
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
        foreach (var shadow in node.Children.Where(child => child.Type == "UIShadow" && child.Enabled && child.ShadowInset && child.ShadowMode == "Shape").OrderBy(child => child.ZIndex))
        {
            var insetLayer = new Canvas { Width = width, Height = height, ClipToBounds = true, IsHitTestVisible = false };
            insetLayer.Children.Add(CreateShadowVisual(node, shadow, width, height, rounded));
            content.Children.Add(insetLayer);
        }
        if (node.Id == selectedId || node.Children.Any(child => child.Id == selectedId && Components.Contains(child.Type)))
            content.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(74, 163, 255)), BorderThickness = new Thickness(2), IsHitTestVisible = false });
        var outerShadows = node.Children.Where(child => child.Type == "UIShadow" && child.Enabled && child.ShadowMode == "Shape" && !child.ShadowInset)
            .OrderBy(child => child.ZIndex).ToArray();
        if (outerShadows.Length == 0) return root;
        var layer = new Canvas { Tag = node, Width = width, Height = height, RenderTransform = transform };
        root.RenderTransform = null;
        foreach (var shadow in outerShadows)
            layer.Children.Add(CreateShadowVisual(node, shadow, width, height, rounded));
        layer.Children.Add(root);
        return layer;
    }

    private static bool parentSupportsTextShadow(SceneNode node) => node.Type is "TextLabel" or "TextButton" or "TextBox";

    private static FrameworkElement CreateShadowVisual(SceneNode parent, SceneNode shadow, double width, double height, SceneNode? rounded)
    {
        var (offsetX, offsetY) = UIShadowService.ResolveOffset(shadow, width, height);
        var (spreadX, spreadY) = shadow.ShadowMode == "Text" ? (0d, 0d) : UIShadowService.ResolveSpread(shadow, width, height);
        var blur = UIShadowService.ResolveBlur(shadow, width, height);
        var color = WithTransparency(ColorFrom(shadow.Color, Colors.Black), shadow.Transparency);
        var gradient = shadow.Children.FirstOrDefault(child => child.Type == "UIGradient" && child.Enabled);
        Brush brush = gradient is null ? new SolidColorBrush(color) : GradientBrush(gradient, color);
        FrameworkElement visual;
        if (shadow.ShadowMode == "Text")
        {
            var fontSize = FontSizeFor(parent, width, height);
            visual = new TextBlock
            {
                Text = parent.Text,
                Width = width,
                Height = height,
                Foreground = brush,
                FontSize = fontSize,
                Padding = new Thickness(0, Math.Max(0, (height - fontSize * 1.2) / 2), 0, 0),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                IsHitTestVisible = false
            };
        }
        else
        {
            var shadowWidth = Math.Max(0, shadow.ShadowInset ? width : width + spreadX * 2);
            var shadowHeight = Math.Max(0, shadow.ShadowInset ? height : height + spreadY * 2);
            var radius = rounded is null ? new CornerRadius(0) : UICornerRadiusService.Render(rounded, shadowWidth, shadowHeight);
            visual = new Border
            {
                Width = shadowWidth,
                Height = shadowHeight,
                CornerRadius = radius,
                Background = !shadow.ShadowInset && shadow.ShadowShowBehindParent ? brush : Brushes.Transparent,
                BorderBrush = brush,
                BorderThickness = shadow.ShadowInset ? new Thickness(Math.Max(1, blur + Math.Max(spreadX, spreadY)))
                    : !shadow.ShadowShowBehindParent ? new Thickness(Math.Max(1, blur)) : new Thickness(0),
                IsHitTestVisible = false
            };
        }
        if (blur > 0) visual.Effect = new BlurEffect { Radius = blur };
        Canvas.SetLeft(visual, offsetX - (shadow.ShadowInset ? 0 : spreadX));
        Canvas.SetTop(visual, offsetY - (shadow.ShadowInset ? 0 : spreadY));
        return visual;
    }

    private static FrameworkElement? CreateVisual(SceneNode node, GameProject project, double width, double height, Action<SceneNode>? activated, Action<SceneNode, string>? textChanged)
    {
        var background = BackgroundFor(node);
        var fontSize = FontSizeFor(node, width, height);
        if (node.Type == "TextBox")
        {
            var input = new TextBox
            {
                Tag = node,
                Text = node.Text,
                Foreground = ForegroundFor(node),
                Background = Brushes.Transparent,
                FontSize = fontSize,
                AcceptsReturn = node.MultiLine,
                IsReadOnly = textChanged is null || !node.TextEditable,
                TextWrapping = node.MultiLine || node.TextScaled ? TextWrapping.Wrap : TextWrapping.NoWrap,
                VerticalContentAlignment = node.MultiLine ? VerticalAlignment.Top : VerticalAlignment.Center
            };
            var placeholder = new TextBlock
            {
                Text = node.PlaceholderText,
                Foreground = ForegroundFor(node),
                Opacity = .55,
                Margin = new Thickness(6, node.MultiLine ? 4 : 0, 4, 0),
                VerticalAlignment = node.MultiLine ? VerticalAlignment.Top : VerticalAlignment.Center,
                TextWrapping = node.MultiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
                IsHitTestVisible = false
            };
            placeholder.Visibility = string.IsNullOrEmpty(node.Text) && !string.IsNullOrEmpty(node.PlaceholderText) ? Visibility.Visible : Visibility.Collapsed;
            input.TextChanged += (_, _) =>
            {
                placeholder.Visibility = string.IsNullOrEmpty(input.Text) && !string.IsNullOrEmpty(node.PlaceholderText) ? Visibility.Visible : Visibility.Collapsed;
                textChanged?.Invoke(node, input.Text);
            };
            if (textChanged is not null)
                input.GotKeyboardFocus += (_, _) => { if (node.TextEditable && node.ClearTextOnFocus && input.Text.Length > 0) input.Clear(); };
            var textBox = new Grid { Background = background };
            textBox.Children.Add(input);
            textBox.Children.Add(placeholder);
            return textBox;
        }
        FrameworkElement? visual = node.Type switch
        {
            "Frame" => new Border { Background = background },
            "ScrollingFrame" => new Border { Background = background },
            "ImageLabel" => CreateImageLabel(node, project, background),
            "ImageButton" => new Button { Content = new Image { Source = ImageFor(node, project), Stretch = Stretch.Fill }, Background = background, BorderThickness = new Thickness(0), Padding = new Thickness(0) },
            "TextLabel" => new TextBlock { Text = node.Text, Foreground = ForegroundFor(node), Background = background, FontSize = fontSize, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap },
            "TextButton" => new Button { Content = node.TextScaled ? new TextBlock { Text = node.Text, FontSize = fontSize, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center } : node.Text, Foreground = ForegroundFor(node), Background = background, FontSize = fontSize, Padding = new Thickness(2) },
            _ => null
        };
        if (visual is Button button && activated is not null) button.Click += (_, _) => activated(node);
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
        var right = children.Select((node, index) => padding.Left + node.PositionScale[0] * innerWidth + node.Position[0]
            + elementSizes[index].Width * (1 - Math.Clamp(node.AnchorPoint[0], 0, 1))).Max();
        var bottom = children.Select((node, index) => padding.Top + node.PositionScale[1] * innerHeight + node.Position[1]
            + elementSizes[index].Height * (1 - Math.Clamp(node.AnchorPoint[1], 0, 1))).Max();
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
