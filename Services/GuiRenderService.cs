using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using System.Windows.Input;
using Vector3 = System.Numerics.Vector3;
using BullerStudio.Models;

namespace BullerStudio.Services;

public static class GuiRenderService
{
    private static readonly HashSet<string> Components = ["UICorner", "UIOutline", "UIStroke", "UIGradient", "UIShadow", "UIPadding", "UIScale", "UIListLayout", "UIGridLayout", "UITableLayout", "UIPageLayout", "UIAspectRatio", "UIAspectRatioConstraint", "UISizeConstraint", "UITextSizeConstraint", "UIFlexItem", "UIDragDetector"];

    public static Canvas Render(GameProject project, bool isInteractive, double viewportWidth, double viewportHeight, string? selectedId = null, Action<SceneNode>? activated = null, Action<SceneNode, string>? textChanged = null, SceneNode? screenGuiRoot = null, Action<SceneNode, string>? propertyChanged = null, Action<SceneNode, string, double, double>? dragSignal = null, Action<SceneNode, string>? mouseButtonSignal = null, Action<SceneNode, string, bool>? textFocusSignal = null)
    {
        var canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true, IsHitTestVisible = isInteractive };
        var width = double.IsFinite(viewportWidth) && viewportWidth > 0 ? viewportWidth : 1280;
        var height = double.IsFinite(viewportHeight) && viewportHeight > 0 ? viewportHeight : 800;
        foreach (var screen in (screenGuiRoot ?? project.Gui).Children.Where(node => node.Type == "ScreenGui" && node.Enabled && node.Visible).OrderBy(node => node.DisplayOrder))
        {
            var screenCanvas = new Canvas { Width = width, Height = height, Background = Brushes.Transparent };
            AddChildren(screenCanvas, screen, project, width, height, selectedId, activated, textChanged, propertyChanged, dragSignal, mouseButtonSignal, textFocusSignal);
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
        Action<SceneNode>? activated = null, Action<SceneNode, string>? textChanged = null, string? selectedId = null, Action<SceneNode, string>? propertyChanged = null, Action<SceneNode, string, double, double>? dragSignal = null, Action<SceneNode, string>? mouseButtonSignal = null, Action<SceneNode, string, bool>? textFocusSignal = null)
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
                ClipToBounds = billboard.ClipsDescendants,
                IsHitTestVisible = isInteractive
            };
            AddChildren(element, billboard, project, billboardWidth, billboardHeight, selectedId, activated, textChanged, propertyChanged, dragSignal, mouseButtonSignal, textFocusSignal);
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
            if (!billboard.Enabled || adornee is null || !TryProjectBillboard(ApplyBillboardOffset(adornee.Position, billboard.StudsOffset, billboard.StudsOffsetWorldSpace, lookDirection, upDirection), cameraPosition, lookDirection,
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
        Action<SceneNode>? activated = null, Action<SceneNode, string>? textChanged = null, string? selectedId = null, Action<SceneNode, string>? propertyChanged = null, Action<SceneNode, string, double, double>? dragSignal = null, Action<SceneNode, string>? mouseButtonSignal = null, Action<SceneNode, string, bool>? textFocusSignal = null)
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
                ClipToBounds = surfaceGui.ClipsDescendants,
                IsHitTestVisible = isInteractive,
                RenderTransformOrigin = new Point(.5, .5)
            };
            AddChildren(element, surfaceGui, project, guiWidth, guiHeight, selectedId, activated, textChanged, propertyChanged, dragSignal, mouseButtonSignal, textFocusSignal);
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

    private static float[] ApplyBillboardOffset(float[] origin, float[] offset, float[] worldOffset, Vector3 lookDirection, Vector3 upDirection)
    {
        if (origin.Length < 3 || offset.Length < 3 || worldOffset.Length < 3 || lookDirection.LengthSquared() < .000001f) return origin;
        var look = Vector3.Normalize(lookDirection);
        var right = Vector3.Cross(look, upDirection);
        if (right.LengthSquared() < .000001f) return origin;
        right = Vector3.Normalize(right);
        var screenUp = Vector3.Normalize(Vector3.Cross(right, look));
        var displacement = new Vector3(worldOffset[0], worldOffset[1], worldOffset[2]) + right * offset[0] + screenUp * offset[1] + look * offset[2];
        return [origin[0] + displacement.X, origin[1] + displacement.Y, origin[2] + displacement.Z];
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

    private static void AddChildren(Canvas canvas, SceneNode parent, GameProject project, double availableWidth, double availableHeight, string? selectedId, Action<SceneNode>? activated, Action<SceneNode, string>? textChanged, Action<SceneNode, string>? propertyChanged, Action<SceneNode, string, double, double>? dragSignal, Action<SceneNode, string>? mouseButtonSignal, Action<SceneNode, string, bool>? textFocusSignal)
    {
        var padding = GetPadding(parent, availableWidth, availableHeight);
        var innerWidth = Math.Max(0, availableWidth - padding.Left - padding.Right);
        var innerHeight = Math.Max(0, availableHeight - padding.Top - padding.Bottom);
        var listLayout = parent.Children.FirstOrDefault(node => node.Type == "UIListLayout");
        var gridLayout = parent.Children.FirstOrDefault(node => node.Type == "UIGridLayout");
        var tableLayout = parent.Children.FirstOrDefault(node => node.Type == "UITableLayout");
        var pageLayout = parent.Children.FirstOrDefault(node => node.Type == "UIPageLayout");
        var children = GetRenderableChildren(parent).ToArray();
        if (listLayout is not null && listLayout.SortOrder == "Name")
            children = children.OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
        else if (listLayout is not null || gridLayout is not null || tableLayout is not null)
            children = children.OrderBy(node => node.LayoutOrder).ToArray();
        if (pageLayout is not null && children.Length > 0)
        {
            children = pageLayout.SortOrder == "Name"
                ? children.OrderBy(node => node.Name, StringComparer.Ordinal).ToArray()
                : children.OrderBy(node => node.LayoutOrder).ToArray();
            pageLayout.CurrentPageIndex = pageLayout.Circular
                ? (pageLayout.CurrentPageIndex - 1 + children.Length) % children.Length + 1
                : Math.Clamp(pageLayout.CurrentPageIndex, 1, children.Length);
            children = [children[pageLayout.CurrentPageIndex - 1]];
        }
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
            var visual = CreateNode(node, project, width, height, selectedId, activated, textChanged, propertyChanged, dragSignal, mouseButtonSignal, textFocusSignal);
            if (visual is null) continue;
            Canvas.SetLeft(visual, left);
            Canvas.SetTop(visual, top);
            Panel.SetZIndex(visual, node.ZIndex);
            canvas.Children.Add(visual);
        }
    }

    private static FrameworkElement? CreateNode(SceneNode node, GameProject project, double width, double height, string? selectedId, Action<SceneNode>? activated, Action<SceneNode, string>? textChanged, Action<SceneNode, string>? propertyChanged, Action<SceneNode, string, double, double>? dragSignal, Action<SceneNode, string>? mouseButtonSignal, Action<SceneNode, string, bool>? textFocusSignal)
    {
        var rounded = node.Children.FirstOrDefault(child => child.Type == "UICorner");
        var outline = node.Children.FirstOrDefault(child => child.Enabled && child.Type is ("UIOutline" or "UIStroke"));
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
            BorderBrush = outline is null ? BrushFrom(node.BorderColor3, Brushes.Black) : BrushFrom(outline.StrokeColor, Brushes.White),
            BorderThickness = outline is null
                ? new Thickness(node.BackgroundTransparency >= 1 ? 0 : Math.Clamp(node.BorderSizePixel, 0, 64))
                : new Thickness(Math.Max(0, outline.StrokeThickness)),
            CornerRadius = rounded is null ? new CornerRadius(0) : UICornerRadiusService.Render(rounded, width, height),
            ClipToBounds = rounded is not null,
            RenderTransform = transform
        };
        if (node.Type == "UICanvasGroup") root.Opacity = 1 - Math.Clamp(node.GroupTransparency, 0, 1);
        var content = new Grid();
        root.Child = content;
        var element = CreateVisual(node, project, width, height, activated, textChanged, propertyChanged, mouseButtonSignal, textFocusSignal);
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
        var childCanvas = new Canvas { ClipToBounds = node.Type == "ScrollingFrame" || node.ClipsDescendants };
        var requestedCanvasWidth = node.CanvasSizeScale[0] * width + node.CanvasSize[0];
        var requestedCanvasHeight = node.CanvasSizeScale[1] * height + node.CanvasSize[1];
        childCanvas.Width = Math.Max(width, requestedCanvasWidth);
        childCanvas.Height = Math.Max(height, requestedCanvasHeight);
        if (node.Type == "ScrollingFrame")
        {
            var canScrollHorizontally = node.ScrollingEnabled && node.ScrollingDirection is ("X" or "XY");
            var canScrollVertically = node.ScrollingEnabled && node.ScrollingDirection is ("Y" or "XY");
            var scroll = new ScrollViewer
            {
                Content = childCanvas,
                HorizontalScrollBarVisibility = canScrollHorizontally ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = canScrollVertically ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };
            var scrollBarStyle = new Style(typeof(ScrollBar));
            scrollBarStyle.Setters.Add(new Setter(FrameworkElement.WidthProperty, (double)node.ScrollBarThickness));
            scrollBarStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, (double)node.ScrollBarThickness));
            scroll.Resources[typeof(ScrollBar)] = scrollBarStyle;
            scroll.PreviewMouseWheel += (_, args) =>
            {
                if (!node.ScrollingEnabled)
                {
                    args.Handled = true;
                    return;
                }
                if (node.ScrollingDirection == "X")
                {
                    scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - args.Delta);
                    args.Handled = true;
                }
            };
            void ApplyCanvasPosition()
            {
                scroll.ScrollToHorizontalOffset(canScrollHorizontally ? Math.Clamp(node.CanvasPosition[0], 0, Math.Max(0, childCanvas.Width - width)) : 0);
                scroll.ScrollToVerticalOffset(canScrollVertically ? Math.Clamp(node.CanvasPosition[1], 0, Math.Max(0, childCanvas.Height - height)) : 0);
            }
            ApplyCanvasPosition();
            scroll.Loaded += (_, _) => ApplyCanvasPosition();
            scroll.ScrollChanged += (_, _) =>
            {
                var next = new[]
                {
                    canScrollHorizontally ? (float)scroll.HorizontalOffset : 0,
                    canScrollVertically ? (float)scroll.VerticalOffset : 0
                };
                if (node.CanvasPosition.SequenceEqual(next)) return;
                node.CanvasPosition = next;
                propertyChanged?.Invoke(node, "CanvasPosition");
            };
            content.Children.Add(scroll);
        }
        else content.Children.Add(childCanvas);
        AddChildren(childCanvas, node, project, width, height, selectedId, activated, textChanged, propertyChanged, dragSignal, mouseButtonSignal, textFocusSignal);
        if (node.Type == "ScrollingFrame" && node.AutomaticCanvasSize != "None")
        {
            var contentWidth = childCanvas.Children.OfType<FrameworkElement>()
                .Select(child => Canvas.GetLeft(child) + (double.IsFinite(child.Width) ? child.Width : child.ActualWidth))
                .Where(double.IsFinite).DefaultIfEmpty(width).Max();
            var contentHeight = childCanvas.Children.OfType<FrameworkElement>()
                .Select(child => Canvas.GetTop(child) + (double.IsFinite(child.Height) ? child.Height : child.ActualHeight))
                .Where(double.IsFinite).DefaultIfEmpty(height).Max();
            if (node.AutomaticCanvasSize is "X" or "XY") childCanvas.Width = Math.Max(childCanvas.Width, contentWidth);
            if (node.AutomaticCanvasSize is "Y" or "XY") childCanvas.Height = Math.Max(childCanvas.Height, contentHeight);
        }
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
        if (outerShadows.Length == 0)
        {
            AttachDragBehavior(root, node, propertyChanged, dragSignal);
            return root;
        }
        var layer = new Canvas { Tag = node, Width = width, Height = height, RenderTransform = transform };
        root.RenderTransform = null;
        foreach (var shadow in outerShadows)
            layer.Children.Add(CreateShadowVisual(node, shadow, width, height, rounded));
        layer.Children.Add(root);
        AttachDragBehavior(layer, node, propertyChanged, dragSignal);
        return layer;
    }

    private static void AttachDragBehavior(FrameworkElement visual, SceneNode node, Action<SceneNode, string>? propertyChanged, Action<SceneNode, string, double, double>? dragSignal)
    {
        var detectors = node.Children.Where(child => child.Type == "UIDragDetector").ToArray();
        if (propertyChanged is null || detectors.Length == 0) return;
        var dragging = false;
        SceneNode? activeDetector = null;
        var pointerStart = new Point();
        var leftStart = 0d;
        var topStart = 0d;
        var xStart = node.Position[0];
        var yStart = node.Position[1];
        bool BeginDrag(Point pointer, Func<bool> capture)
        {
            if (dragging) return false;
            activeDetector = detectors.FirstOrDefault(detector => detector.Enabled);
            if (activeDetector is null) return false;
            pointerStart = pointer;
            leftStart = Canvas.GetLeft(visual);
            topStart = Canvas.GetTop(visual);
            if (!double.IsFinite(leftStart)) leftStart = 0;
            if (!double.IsFinite(topStart)) topStart = 0;
            xStart = node.Position[0];
            yStart = node.Position[1];
            dragging = capture();
            if (!dragging)
            {
                activeDetector = null;
                return false;
            }
            dragSignal?.Invoke(activeDetector, "DragStart", pointer.X, pointer.Y);
            return true;
        }
        void UpdateDrag(Point pointer, Action releaseCapture)
        {
            if (!dragging) return;
            if (activeDetector is null || !activeDetector.Enabled)
            {
                if (activeDetector is not null) dragSignal?.Invoke(activeDetector, "DragEnd", pointer.X, pointer.Y);
                dragging = false;
                activeDetector = null;
                releaseCapture();
                return;
            }
            var deltaX = pointer.X - pointerStart.X;
            var deltaY = pointer.Y - pointerStart.Y;
            Canvas.SetLeft(visual, leftStart + deltaX);
            Canvas.SetTop(visual, topStart + deltaY);
            node.Position[0] = (float)(xStart + deltaX);
            node.Position[1] = (float)(yStart + deltaY);
            propertyChanged(node, "Position");
            dragSignal?.Invoke(activeDetector, "DragContinue", pointer.X, pointer.Y);
        }
        void EndDrag(Point pointer, Action releaseCapture)
        {
            if (!dragging) return;
            dragging = false;
            if (activeDetector is not null) dragSignal?.Invoke(activeDetector, "DragEnd", pointer.X, pointer.Y);
            activeDetector = null;
            releaseCapture();
        }
        visual.MouseLeftButtonDown += (_, args) =>
        {
            if (dragging || ParentCanvas(visual) is not { } canvas) return;
            if (BeginDrag(args.GetPosition(canvas), visual.CaptureMouse)) args.Handled = true;
        };
        visual.MouseMove += (_, args) =>
        {
            if (!dragging || ParentCanvas(visual) is not { } canvas) return;
            UpdateDrag(args.GetPosition(canvas), visual.ReleaseMouseCapture);
            args.Handled = true;
        };
        visual.MouseLeftButtonUp += (_, args) =>
        {
            if (!dragging) return;
            var position = ParentCanvas(visual) is { } canvas ? args.GetPosition(canvas) : new Point();
            EndDrag(position, visual.ReleaseMouseCapture);
            args.Handled = true;
        };
        visual.TouchDown += (_, args) =>
        {
            if (dragging || ParentCanvas(visual) is not { } canvas) return;
            if (BeginDrag(args.GetTouchPoint(canvas).Position, () => visual.CaptureTouch(args.TouchDevice))) args.Handled = true;
        };
        visual.TouchMove += (_, args) =>
        {
            if (!dragging || ParentCanvas(visual) is not { } canvas) return;
            UpdateDrag(args.GetTouchPoint(canvas).Position, () => visual.ReleaseTouchCapture(args.TouchDevice));
            args.Handled = true;
        };
        visual.TouchUp += (_, args) =>
        {
            if (!dragging || ParentCanvas(visual) is not { } canvas) return;
            EndDrag(args.GetTouchPoint(canvas).Position, () => visual.ReleaseTouchCapture(args.TouchDevice));
            args.Handled = true;
        };
    }

    private static Canvas? ParentCanvas(DependencyObject node)
    {
        for (var parent = VisualTreeHelper.GetParent(node); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is Canvas canvas) return canvas;
        return null;
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

    private static FrameworkElement? CreateVisual(SceneNode node, GameProject project, double width, double height, Action<SceneNode>? activated, Action<SceneNode, string>? textChanged, Action<SceneNode, string>? propertyChanged, Action<SceneNode, string>? mouseButtonSignal, Action<SceneNode, string, bool>? textFocusSignal)
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
            var enterPressed = false;
            input.GotKeyboardFocus += (_, _) =>
            {
                textFocusSignal?.Invoke(node, "Focused", false);
                if (textChanged is not null && node.TextEditable && node.ClearTextOnFocus && input.Text.Length > 0) input.Clear();
            };
            input.LostKeyboardFocus += (_, _) =>
            {
                textFocusSignal?.Invoke(node, "FocusLost", enterPressed);
                enterPressed = false;
            };
            input.PreviewKeyDown += (_, args) =>
            {
                if (args.Key != Key.Enter || input.AcceptsReturn) return;
                enterPressed = true;
                args.Handled = true;
                Keyboard.ClearFocus();
            };
            var textBox = new Grid { Background = background };
            textBox.Children.Add(input);
            textBox.Children.Add(placeholder);
            return textBox;
        }
        FrameworkElement? visual = node.Type switch
        {
            "Frame" or "UICanvasGroup" => new Border { Background = background },
            "ScrollingFrame" => new Border { Background = background },
            "ViewportFrame" => CreateViewportFrame(node, project),
            "VideoFrame" => CreateVideoFrame(node, project, background, propertyChanged),
            "ImageLabel" => CreateImageLabel(node, project, background),
            "ImageButton" => CreateImageButton(node, project, background),
            "TextLabel" => new TextBlock { Text = node.Text, Foreground = ForegroundFor(node), Background = background, FontSize = fontSize, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = node.TextWrapped || node.TextScaled ? TextWrapping.Wrap : TextWrapping.NoWrap },
            "TextButton" => new Button { Content = node.TextScaled || node.TextWrapped ? new TextBlock { Text = node.Text, FontSize = fontSize, TextWrapping = node.TextWrapped || node.TextScaled ? TextWrapping.Wrap : TextWrapping.NoWrap, TextAlignment = TextAlignment.Center } : node.Text, Foreground = ForegroundFor(node), Background = background, FontSize = fontSize, Padding = new Thickness(2) },
            _ => null
        };
        if (visual is Button button)
        {
            if (activated is not null) button.Click += (_, _) => activated(node);
            if (mouseButtonSignal is not null)
            {
                button.PreviewMouseLeftButtonDown += (_, _) => mouseButtonSignal(node, "MouseButton1Down");
                button.PreviewMouseLeftButtonUp += (_, _) => mouseButtonSignal(node, "MouseButton1Up");
            }
        }
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
        var grid = new Grid { Background = background, ClipToBounds = true };
        grid.Children.Add(CreateImageContent(node, project));
        return grid;
    }

    private static Button CreateImageButton(SceneNode node, GameProject project, Brush background)
    {
        FrameworkElement Image(string? assetOverride = null) => CreateImageContent(node, project, assetOverride);
        var button = new Button { Content = Image(), Background = background, BorderThickness = new Thickness(0), Padding = new Thickness(0), ClipToBounds = true };
        var hover = string.IsNullOrWhiteSpace(node.HoverImage) ? null : Image(node.HoverImage);
        var pressed = string.IsNullOrWhiteSpace(node.PressedImage) ? null : Image(node.PressedImage);
        button.MouseEnter += (_, _) => { if (hover is not null) button.Content = hover; };
        button.MouseLeave += (_, _) => button.Content = Image();
        button.PreviewMouseLeftButtonDown += (_, _) => { if (pressed is not null) button.Content = pressed; };
        button.PreviewMouseLeftButtonUp += (_, _) => button.Content = button.IsMouseOver ? hover ?? Image() : Image();
        return button;
    }

    private static FrameworkElement CreateImageContent(SceneNode node, GameProject project, string? assetOverride = null)
    {
        var source = ImageRectFor(node, project, assetOverride);
        if (node.ScaleType == "Slice" && source is BitmapSource bitmap && node.SliceCenter is { Length: >= 4 } center)
            return CreateSlicedImage(bitmap, center, node.SliceScale);
        if (node.ScaleType == "Tile" && source is not null)
        {
            var brush = new ImageBrush(source)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, Math.Max(1, source.Width), Math.Max(1, source.Height)),
                Stretch = Stretch.Fill
            };
            return new Border { Background = brush, ClipToBounds = true };
        }
        var stretch = node.ScaleType switch
        {
            "Fit" => Stretch.Uniform,
            "Crop" => Stretch.UniformToFill,
            _ => Stretch.Fill
        };
        return new Image { Source = source, Stretch = stretch, ClipToBounds = true };
    }

    private static Grid CreateSlicedImage(BitmapSource source, float[] center, float sliceScale)
    {
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
            return new Grid { ClipToBounds = true, Children = { new Image { Source = source, Stretch = Stretch.Fill } } };
        var x1 = Math.Clamp((int)MathF.Floor(center[0]), 0, source.PixelWidth);
        var y1 = Math.Clamp((int)MathF.Floor(center[1]), 0, source.PixelHeight);
        var x2 = Math.Clamp((int)MathF.Floor(center[2]), x1, source.PixelWidth);
        var y2 = Math.Clamp((int)MathF.Floor(center[3]), y1, source.PixelHeight);
        if (x1 == 0 && y1 == 0 && x2 == 0 && y2 == 0)
            return new Grid { ClipToBounds = true, Children = { new Image { Source = source, Stretch = Stretch.Fill } } };

        var scale = float.IsFinite(sliceScale) ? Math.Clamp(sliceScale, .01f, 100) : 1;
        var widths = new[] { x1 * source.Width / source.PixelWidth * scale, (x2 - x1) * source.Width / source.PixelWidth, (source.PixelWidth - x2) * source.Width / source.PixelWidth * scale };
        var heights = new[] { y1 * source.Height / source.PixelHeight * scale, (y2 - y1) * source.Height / source.PixelHeight, (source.PixelHeight - y2) * source.Height / source.PixelHeight * scale };
        var grid = new Grid { ClipToBounds = true };
        for (var index = 0; index < 3; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = index == 1 ? new GridLength(1, GridUnitType.Star) : new GridLength(widths[index]) });
            grid.RowDefinitions.Add(new RowDefinition { Height = index == 1 ? new GridLength(1, GridUnitType.Star) : new GridLength(heights[index]) });
        }
        void FitSliceBands(double available, double first, double last, Action<double, double> apply)
        {
            if (!double.IsFinite(available) || available <= 0) return;
            var total = first + last;
            var factor = total > available && total > 0 ? available / total : 1;
            apply(first * factor, last * factor);
        }
        void FitSliceToSize()
        {
            FitSliceBands(grid.ActualWidth, widths[0], widths[2], (first, last) =>
            {
                grid.ColumnDefinitions[0].Width = new GridLength(first);
                grid.ColumnDefinitions[2].Width = new GridLength(last);
            });
            FitSliceBands(grid.ActualHeight, heights[0], heights[2], (first, last) =>
            {
                grid.RowDefinitions[0].Height = new GridLength(first);
                grid.RowDefinitions[2].Height = new GridLength(last);
            });
        }
        grid.SizeChanged += (_, _) => FitSliceToSize();
        var xEdges = new[] { 0, x1, x2 };
        var xEnds = new[] { x1, x2, source.PixelWidth };
        var yEdges = new[] { 0, y1, y2 };
        var yEnds = new[] { y1, y2, source.PixelHeight };
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
        {
            var cropWidth = xEnds[column] - xEdges[column];
            var cropHeight = yEnds[row] - yEdges[row];
            if (cropWidth <= 0 || cropHeight <= 0) continue;
            var crop = new CroppedBitmap(source, new Int32Rect(xEdges[column], yEdges[row], cropWidth, cropHeight));
            if (crop.CanFreeze) crop.Freeze();
            var image = new Image { Source = crop, Stretch = Stretch.Fill };
            Grid.SetColumn(image, column);
            Grid.SetRow(image, row);
            grid.Children.Add(image);
        }
        return grid;
    }

    private static Grid CreateVideoFrame(SceneNode node, GameProject project, Brush background, Action<SceneNode, string>? propertyChanged)
    {
        var grid = new Grid { Background = background, ClipToBounds = true };
        string path;
        try { path = GameProjectStore.GetSafeAssetFilePathInAssets(project, node.Video); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { path = string.Empty; }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return grid;
        var media = new MediaElement
        {
            Source = new Uri(path, UriKind.Absolute),
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Stop,
            Stretch = Stretch.Uniform,
            Volume = Math.Clamp(node.Volume, 0, 1),
            Tag = node
        };
        media.Loaded += (_, _) =>
        {
            if (node.Playing) media.Play();
        };
        media.MediaEnded += (_, _) =>
        {
            if (node.Looped)
            {
                media.Position = TimeSpan.Zero;
                media.Play();
            }
            else if (node.Playing)
            {
                node.Playing = false;
                propertyChanged?.Invoke(node, "Playing");
            }
        };
        media.MediaFailed += (_, _) =>
        {
            if (!node.Playing) return;
            node.Playing = false;
            propertyChanged?.Invoke(node, "Playing");
        };
        grid.Children.Add(media);
        return grid;
    }

    private static FrameworkElement CreateViewportFrame(SceneNode node, GameProject project)
    {
        var container = new Grid { Background = BackgroundFor(node), ClipToBounds = true };
        var viewport = new Viewport3D { ClipToBounds = true };
        var allNodes = GameProjectStore.Flatten(project).ToArray();
        var camera = allNodes.FirstOrDefault(item => item.Id == node.ViewportCurrentCameraId && item.Type == "Camera");
        if (camera is null) return container;
        var cameraPosition = camera.Position;
        var rotation = camera.Rotation;
        var yaw = rotation[1] * Math.PI / 180;
        var pitch = rotation[0] * Math.PI / 180;
        var direction = new Vector3D(Math.Sin(yaw) * Math.Cos(pitch), -Math.Sin(pitch), -Math.Cos(yaw) * Math.Cos(pitch));
        viewport.Camera = new PerspectiveCamera
        {
            Position = new Point3D(cameraPosition[0], cameraPosition[1], cameraPosition[2]),
            LookDirection = direction,
            UpDirection = new Vector3D(0, 1, 0),
            FieldOfView = Math.Clamp(camera.FieldOfView, 10, 120)
        };
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Color.FromRgb(120, 120, 120)));
        scene.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-.5, -1, -.7)));
        var bodyColorOverrides = BodyColorsService.BuildOverrides(node);
        foreach (var part in GameProjectStore.Flatten(node.Children).Where(SceneNodeTypes.IsBasePart).Take(1000))
        {
            if (!part.Visible || part.Transparency >= 1) continue;
            var color = ColorFrom(bodyColorOverrides.GetValueOrDefault(part.Id, part.Color), Colors.LightGray);
            color.A = (byte)Math.Round(color.A * (1 - Math.Clamp(part.Transparency, 0, 1)));
            var brush = new SolidColorBrush(color);
            var material = new DiffuseMaterial(brush);
            var geometry = CreateViewportGeometry(part, project);
            var transform = new Transform3DGroup();
            transform.Children.Add(new ScaleTransform3D(Math.Max(.001, part.Size[0]), Math.Max(.001, part.Size[1]), Math.Max(.001, part.Size[2])));
            transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), part.Rotation[0])));
            transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), part.Rotation[1])));
            transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), part.Rotation[2])));
            transform.Children.Add(new TranslateTransform3D(part.Position[0], part.Position[1], part.Position[2]));
            scene.Children.Add(new GeometryModel3D(geometry, material) { BackMaterial = material, Transform = transform });
        }
        viewport.Children.Add(new ModelVisual3D { Content = scene });
        container.Children.Add(viewport);
        return container;
    }

    private static MeshGeometry3D CreateViewportGeometry(SceneNode node, GameProject project)
    {
        if (node.Type == "MeshPart" && !string.IsNullOrWhiteSpace(node.Asset))
        {
            try
            {
                var path = GameProjectStore.GetSafeAssetFilePathInAssets(project, node.Asset);
                if (File.Exists(path)) return ReadViewportObj(path);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        if (node.Type == "Part" && node.Shape == "Ball") return CreateViewportSphere();
        if (node.Type == "Part" && node.Shape == "Cylinder") return CreateViewportCylinder();
        if (node.Type == "WedgePart") return CreateViewportWedge(false);
        if (node.Type == "CornerWedgePart") return CreateViewportWedge(true);
        if (node.Type == "SpawnLocation") return CreateViewportSpawnLocation();
        if (node.Type is "Seat" or "VehicleSeat") return CreateViewportSeat();
        if (node.Type == "TrussPart") return CreateViewportTruss();

        var mesh = new MeshGeometry3D();
        AddViewportBox(mesh, new Point3D(0, 0, 0), 1, 1, 1);
        return mesh;
    }

    private static MeshGeometry3D ReadViewportObj(string path)
    {
        var vertices = new List<Point3D>();
        var mesh = new MeshGeometry3D();
        foreach (var line in File.ReadLines(path))
        {
            var fields = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length == 4 && fields[0] == "v"
                && double.TryParse(fields[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
                && double.TryParse(fields[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)
                && double.TryParse(fields[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z))
                vertices.Add(new Point3D(x, y, z));
            if (fields.Length < 4 || fields[0] != "f") continue;
            var indices = fields.Skip(1).Select(field => int.TryParse(field.Split('/')[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index)
                ? index < 0 ? vertices.Count + index : index - 1 : -1).ToArray();
            for (var index = 1; index < indices.Length - 1; index++)
                if (indices[0] >= 0 && indices[index] >= 0 && indices[index + 1] >= 0
                    && indices[0] < vertices.Count && indices[index] < vertices.Count && indices[index + 1] < vertices.Count)
                    AddViewportTriangle(mesh, vertices[indices[0]], vertices[indices[index]], vertices[indices[index + 1]], orientAwayFromOrigin: false);
        }
        return mesh.Positions.Count > 0 ? mesh : CreateViewportBoxGeometry();
    }

    private static MeshGeometry3D CreateViewportBoxGeometry()
    {
        var mesh = new MeshGeometry3D();
        AddViewportBox(mesh, new Point3D(0, 0, 0), 1, 1, 1);
        return mesh;
    }

    private static void AddViewportBox(MeshGeometry3D mesh, Point3D center, double x, double y, double z)
    {
        var hx = x * .5; var hy = y * .5; var hz = z * .5;
        var a = new Point3D(center.X - hx, center.Y - hy, center.Z - hz);
        var b = new Point3D(center.X + hx, center.Y - hy, center.Z - hz);
        var c = new Point3D(center.X + hx, center.Y + hy, center.Z - hz);
        var d = new Point3D(center.X - hx, center.Y + hy, center.Z - hz);
        var e = new Point3D(center.X - hx, center.Y - hy, center.Z + hz);
        var f = new Point3D(center.X + hx, center.Y - hy, center.Z + hz);
        var g = new Point3D(center.X + hx, center.Y + hy, center.Z + hz);
        var h = new Point3D(center.X - hx, center.Y + hy, center.Z + hz);
        AddViewportQuad(mesh, a, b, c, d); AddViewportQuad(mesh, f, e, h, g);
        AddViewportQuad(mesh, e, a, d, h); AddViewportQuad(mesh, b, f, g, c);
        AddViewportQuad(mesh, d, c, g, h); AddViewportQuad(mesh, e, f, b, a);
    }

    private static MeshGeometry3D CreateViewportSphere()
    {
        var mesh = new MeshGeometry3D();
        const int longitude = 24, latitude = 16;
        for (var row = 0; row < latitude; row++)
        for (var column = 0; column < longitude; column++)
        {
            var a = SpherePoint(row, column, latitude, longitude);
            var b = SpherePoint(row + 1, column, latitude, longitude);
            var c = SpherePoint(row + 1, column + 1, latitude, longitude);
            var d = SpherePoint(row, column + 1, latitude, longitude);
            AddViewportTriangle(mesh, a, b, c); AddViewportTriangle(mesh, a, c, d);
        }
        return mesh;
    }

    private static Point3D SpherePoint(int row, int column, int latitude, int longitude)
    {
        var theta = Math.PI * row / latitude;
        var phi = Math.PI * 2 * column / longitude;
        return new Point3D(.5 * Math.Sin(theta) * Math.Cos(phi), .5 * Math.Cos(theta), .5 * Math.Sin(theta) * Math.Sin(phi));
    }

    private static MeshGeometry3D CreateViewportCylinder()
    {
        var mesh = new MeshGeometry3D();
        const int segments = 32;
        for (var segment = 0; segment < segments; segment++)
        {
            var a = 2 * Math.PI * segment / segments;
            var b = 2 * Math.PI * (segment + 1) / segments;
            var bottomA = new Point3D(.5 * Math.Cos(a), -.5, .5 * Math.Sin(a));
            var bottomB = new Point3D(.5 * Math.Cos(b), -.5, .5 * Math.Sin(b));
            var topA = new Point3D(bottomA.X, .5, bottomA.Z);
            var topB = new Point3D(bottomB.X, .5, bottomB.Z);
            AddViewportQuad(mesh, bottomA, bottomB, topB, topA);
            AddViewportTriangle(mesh, new Point3D(0, -.5, 0), bottomB, bottomA);
            AddViewportTriangle(mesh, new Point3D(0, .5, 0), topA, topB);
        }
        return mesh;
    }

    private static MeshGeometry3D CreateViewportWedge(bool corner)
    {
        var mesh = new MeshGeometry3D();
        var a = new Point3D(-.5, -.5, -.5); var b = new Point3D(.5, -.5, -.5);
        var c = new Point3D(.5, -.5, .5); var d = new Point3D(-.5, -.5, .5);
        var e = new Point3D(-.5, .5, .5); var f = new Point3D(.5, .5, .5);
        AddViewportQuad(mesh, a, b, c, d);
        if (corner)
        {
            AddViewportTriangle(mesh, a, d, e); AddViewportTriangle(mesh, d, c, e);
            AddViewportTriangle(mesh, c, b, e); AddViewportTriangle(mesh, b, a, e);
        }
        else
        {
            AddViewportQuad(mesh, d, c, f, e); AddViewportQuad(mesh, a, e, f, b);
            AddViewportTriangle(mesh, a, d, e); AddViewportTriangle(mesh, b, f, c);
        }
        return mesh;
    }

    private static MeshGeometry3D CreateViewportSpawnLocation()
    {
        var mesh = new MeshGeometry3D();
        AddViewportBox(mesh, new Point3D(0, -.06, 0), 1, .88, 1);
        AddViewportBox(mesh, new Point3D(0, .41, 0), .65, .06, .13);
        AddViewportBox(mesh, new Point3D(0, .41, 0), .13, .06, .65);
        return mesh;
    }

    private static MeshGeometry3D CreateViewportSeat()
    {
        var mesh = new MeshGeometry3D();
        AddViewportBox(mesh, new Point3D(0, .25, 0), 1, .5, 1);
        AddViewportBox(mesh, new Point3D(0, .08, .42), 1, .84, .16);
        return mesh;
    }

    private static MeshGeometry3D CreateViewportTruss()
    {
        var mesh = new MeshGeometry3D();
        foreach (var x in new[] { -.42, .42 })
        foreach (var z in new[] { -.42, .42 })
            AddViewportBox(mesh, new Point3D(x, 0, z), .12, 1, .12);
        foreach (var y in new[] { -.42, 0d, .42 })
        {
            AddViewportBox(mesh, new Point3D(0, y, -.42), .9, .1, .1);
            AddViewportBox(mesh, new Point3D(0, y, .42), .9, .1, .1);
            AddViewportBox(mesh, new Point3D(-.42, y, 0), .1, .1, .9);
            AddViewportBox(mesh, new Point3D(.42, y, 0), .1, .1, .9);
        }
        AddViewportSegment(mesh, new Point3D(-.42, -.42, -.42), new Point3D(.42, .42, -.42), .045);
        AddViewportSegment(mesh, new Point3D(.42, -.42, .42), new Point3D(-.42, .42, .42), .045);
        return mesh;
    }

    private static void AddViewportSegment(MeshGeometry3D mesh, Point3D start, Point3D end, double thickness)
    {
        var axis = end - start;
        var length = axis.Length;
        if (length < 1e-9) return;
        axis.Normalize();
        var up = Math.Abs(Vector3D.DotProduct(axis, new Vector3D(0, 1, 0))) > .98 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
        var side = Vector3D.CrossProduct(axis, up);
        side.Normalize();
        var vertical = Vector3D.CrossProduct(side, axis);
        vertical.Normalize();
        var half = thickness * .5;
        var a = start + side * half + vertical * half;
        var b = start - side * half + vertical * half;
        var c = end - side * half + vertical * half;
        var d = end + side * half + vertical * half;
        var e = start + side * half - vertical * half;
        var f = start - side * half - vertical * half;
        var g = end - side * half - vertical * half;
        var h = end + side * half - vertical * half;
        AddViewportQuad(mesh, a, b, c, d); AddViewportQuad(mesh, f, e, h, g);
        AddViewportQuad(mesh, e, a, d, h); AddViewportQuad(mesh, b, f, g, c);
        AddViewportQuad(mesh, d, c, g, h); AddViewportQuad(mesh, e, f, b, a);
    }

    private static void AddViewportQuad(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c, Point3D d)
    {
        AddViewportTriangle(mesh, a, b, c); AddViewportTriangle(mesh, a, c, d);
    }

    private static void AddViewportTriangle(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c, bool orientAwayFromOrigin = true)
    {
        var normal = Vector3D.CrossProduct(b - a, c - a);
        if (normal.LengthSquared < 1e-12) return;
        var triangleCenter = new Vector3D((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3);
        if (orientAwayFromOrigin && Vector3D.DotProduct(normal, triangleCenter) < 0)
        {
            (b, c) = (c, b);
            normal = -normal;
        }
        normal.Normalize();
        var start = mesh.Positions.Count;
        mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c);
        mesh.Normals.Add(normal); mesh.Normals.Add(normal); mesh.Normals.Add(normal);
        mesh.TriangleIndices.Add(start); mesh.TriangleIndices.Add(start + 1); mesh.TriangleIndices.Add(start + 2);
    }

    private static bool IsRenderable(string type) => type is "Frame" or "ScrollingFrame" or "ViewportFrame" or "VideoFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox" or "UICanvasGroup";

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
        var constrained = ApplyLayoutConstraints(node, width, height, availableWidth, availableHeight);
        if (node.Type != "ScrollingFrame" && node.AutomaticSize != "None")
        {
            var children = GetChildrenExtent(node, constrained.Width, constrained.Height);
            var text = GetTextContentExtent(node, constrained.Width, constrained.Height);
            width = node.AutomaticSize is "X" or "XY" ? Math.Max(constrained.Width, Math.Max(children.Width, text.Width)) : constrained.Width;
            height = node.AutomaticSize is "Y" or "XY" ? Math.Max(constrained.Height, Math.Max(children.Height, text.Height)) : constrained.Height;
            constrained = ApplyLayoutConstraints(node, width, height, availableWidth, availableHeight);
        }
        return constrained;
    }

    private static Size GetTextContentExtent(SceneNode node, double width, double height)
    {
        if (node.Type is not ("TextLabel" or "TextButton" or "TextBox") || string.IsNullOrEmpty(node.Text)) return new Size(0, 0);
        var padding = GetPadding(node, width, height);
        var automaticWidth = node.AutomaticSize is "X" or "XY";
        var wrapsText = node.TextWrapped || node.TextScaled || node.MultiLine;
        var text = new TextBlock
        {
            Text = node.Text,
            FontSize = FontSizeFor(node, width, height),
            TextWrapping = wrapsText ? TextWrapping.Wrap : TextWrapping.NoWrap
        };
        var measureWidth = wrapsText && !automaticWidth ? Math.Max(1, width - padding.Left - padding.Right) : double.PositiveInfinity;
        text.Measure(new Size(measureWidth, double.PositiveInfinity));
        return new Size(text.DesiredSize.Width + padding.Left + padding.Right, text.DesiredSize.Height + padding.Top + padding.Bottom);
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
        var colors = EffectiveGradientColors(gradient);
        var transparencies = EffectiveGradientTransparency(gradient);
        var angle = gradient.GradientRotation * Math.PI / 180;
        var dx = Math.Cos(angle) * .5 / Math.Max(.001, gradient.GradientScale);
        var dy = Math.Sin(angle) * .5 / Math.Max(.001, gradient.GradientScale);
        var offset = gradient.GradientOffset is { Length: >= 2 } ? gradient.GradientOffset : [0f, 0f];
        var stops = new GradientStopCollection();
        foreach (var time in colors.Select(point => point.Time).Concat(transparencies.Select(point => point.Time)).Distinct().Order())
        {
            var color = Multiply(baseColor, ParticleSequenceService.SampleColor(colors, gradient.StartColor, time));
            color = WithTransparency(color, ParticleSequenceService.SampleNumber(transparencies, 0, time, 0));
            stops.Add(new GradientStop(color, time));
        }
        GradientBrush brush = gradient.GradientType is "Radial" or "Elliptical"
            ? new RadialGradientBrush(stops)
            {
                Center = new System.Windows.Point(.5 + offset[0], .5 + offset[1]),
                GradientOrigin = new System.Windows.Point(.5 + offset[0], .5 + offset[1]),
                RadiusX = .5 / Math.Max(.001, gradient.GradientScale),
                RadiusY = gradient.GradientType == "Elliptical" ? .35 / Math.Max(.001, gradient.GradientScale) : .5 / Math.Max(.001, gradient.GradientScale)
            }
            : new LinearGradientBrush(stops,
                new System.Windows.Point(.5 - dx + offset[0], .5 - dy + offset[1]),
                new System.Windows.Point(.5 + dx + offset[0], .5 + dy + offset[1]));
        brush.SpreadMethod = gradient.GradientTileMode switch
        {
            "Repeat" => GradientSpreadMethod.Repeat,
            "Mirror" => GradientSpreadMethod.Reflect,
            _ => GradientSpreadMethod.Pad
        };
        brush.Freeze();
        return brush;
    }

    private static IReadOnlyList<ParticleColorKeypoint> EffectiveGradientColors(SceneNode gradient) =>
        gradient.ColorSequence.Count >= 2 ? gradient.ColorSequence : [new(0, gradient.StartColor), new(1, gradient.EndColor)];

    private static IReadOnlyList<ParticleNumberKeypoint> EffectiveGradientTransparency(SceneNode gradient) =>
        gradient.TransparencySequence.Count >= 2 ? gradient.TransparencySequence : [new(0, gradient.Transparency), new(1, gradient.Transparency)];

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
        node.Type is "Frame" or "TextLabel" or "TextButton" or "ImageLabel" or "ImageButton" or "UICanvasGroup"
            ? node.Children.FirstOrDefault(child => child.Type == "UIGradient" && child.Enabled)
            : null;

    private static Color Multiply(Color first, Color second)
    {
        static byte Scale(byte a, byte b) => (byte)((a * b + 127) / 255);
        return Color.FromArgb(Scale(first.A, second.A), Scale(first.R, second.R),
            Scale(first.G, second.G), Scale(first.B, second.B));
    }

    private static ImageSource? ImageFor(SceneNode node, GameProject project, string? assetOverride = null)
    {
        var source = LoadImage(project, assetOverride ?? node.Asset);
        if (source is not BitmapSource image) return source;
        var gradient = ActiveGradient(node);
        var baseColor = ColorFrom(node.ImageColor3, Colors.White);
        baseColor = WithTransparency(baseColor, node.ImageTransparency);
        if (gradient is null && baseColor == Colors.White) return source;
        var width = image.PixelWidth;
        var height = image.PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height * 4 > 256L * 1024 * 1024) return source;
        var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        var colors = gradient is null ? null : EffectiveGradientColors(gradient);
        var transparencies = gradient is null ? null : EffectiveGradientTransparency(gradient);
        var gradientOffset = gradient?.GradientOffset is { Length: >= 2 } configuredOffset ? configuredOffset : [0f, 0f];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = y * stride + x * 4;
            var u = (x + .5) / width - .5 - gradientOffset[0];
            var v = (y + .5) / height - .5 - gradientOffset[1];
            var t = GradientParameter(gradient, u, v);
            var gradientColor = colors is null ? Colors.White : ParticleSequenceService.SampleColor(colors, gradient!.StartColor, (float)t);
            var gradientTransparency = transparencies is null ? 0 : ParticleSequenceService.SampleNumber(transparencies, 0, (float)t, 0);
            gradientColor = WithTransparency(gradientColor, gradientTransparency);
            static byte Tint(byte pixel, byte baseChannel, double gradientChannel) =>
                (byte)Math.Clamp(Math.Round(pixel * baseChannel * gradientChannel / 65025), 0, 255);
            pixels[offset] = Tint(pixels[offset], baseColor.B, gradientColor.B);
            pixels[offset + 1] = Tint(pixels[offset + 1], baseColor.G, gradientColor.G);
            pixels[offset + 2] = Tint(pixels[offset + 2], baseColor.R, gradientColor.R);
            pixels[offset + 3] = Tint(pixels[offset + 3], baseColor.A, gradientColor.A);
        }
        var tinted = BitmapSource.Create(width, height, image.DpiX, image.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        tinted.Freeze();
        return tinted;
    }

    private static double GradientParameter(SceneNode? gradient, double u, double v)
    {
        if (gradient is null) return .5;
        double parameter;
        if (gradient.GradientType is "Radial" or "Elliptical")
        {
            var yScale = gradient.GradientType == "Elliptical" ? 1.4 : 1;
            parameter = Math.Sqrt(u * u + v * v * yScale * yScale) * 2 * gradient.GradientScale;
        }
        else if (gradient.GradientType == "Conical")
        {
            parameter = (Math.Atan2(v, u) + Math.PI) / (2 * Math.PI) * gradient.GradientScale;
        }
        else
        {
            var angle = gradient.GradientRotation * Math.PI / 180;
            parameter = (u * Math.Cos(angle) + v * Math.Sin(angle)) * gradient.GradientScale + .5;
        }
        return gradient.GradientTileMode switch
        {
            "Repeat" => parameter - Math.Floor(parameter),
            "Mirror" => (parameter - 2 * Math.Floor(parameter / 2)) is var mirrored && mirrored > 1 ? 2 - mirrored : mirrored,
            _ => Math.Clamp(parameter, 0, 1)
        };
    }

    private static ImageSource? ImageRectFor(SceneNode node, GameProject project, string? assetOverride = null)
    {
        if (node.ImageRectSize is not { Length: >= 2 } size || size[0] <= 0 || size[1] <= 0) return ImageFor(node, project, assetOverride);
        var image = ImageFor(node, project, assetOverride);
        if (image is not BitmapSource source || source.PixelWidth <= 0 || source.PixelHeight <= 0) return image;
        var offset = node.ImageRectOffset is { Length: >= 2 } value ? value : [0f, 0f];
        var x = Math.Clamp((int)MathF.Floor(Math.Max(0f, offset[0])), 0, source.PixelWidth - 1);
        var y = Math.Clamp((int)MathF.Floor(Math.Max(0f, offset[1])), 0, source.PixelHeight - 1);
        var width = Math.Clamp((int)MathF.Floor(size[0]), 1, source.PixelWidth - x);
        var height = Math.Clamp((int)MathF.Floor(size[1]), 1, source.PixelHeight - y);
        var cropped = new CroppedBitmap(source, new Int32Rect(x, y, width, height));
        if (cropped.CanFreeze) cropped.Freeze();
        return cropped;
    }

    private static Color ColorFrom(string value, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch (Exception exception) when (exception is FormatException or NotSupportedException or InvalidOperationException or ArgumentException) { return fallback; }
    }
}
