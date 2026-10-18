using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using System.IO;
using HelixToolkit.Wpf.SharpDX;
using HelixToolkit.SharpDX.Core;
using BullerStudio.Models;
using SharpDX;

namespace BullerStudio.Services;

public sealed class SceneRenderService
{
    private readonly Viewport3DX viewport;
    private readonly DefaultEffectsManager effectsManager = new();
    private readonly Dictionary<string, MeshGeometryModel3D> visuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> renderedAssetSources = new(StringComparer.Ordinal);
    private readonly List<Light3D> objectLights = [];
    private readonly List<(MeshGeometryModel3D Model, Vector3 Direction, float Pitch, float Yaw)> skyboxVisuals = [];
    private SceneNode cameraNode;
    private GameProject? activeProject;
    private string? selectedId;
    private float orbitYaw = 35;
    private float orbitPitch = -24;
    private float orbitDistance = 12;

    public SceneRenderService(Viewport3DX viewport, SceneNode cameraNode)
    {
        this.viewport = viewport;
        this.cameraNode = cameraNode;
        viewport.EffectsManager = effectsManager;
        viewport.Camera = new HelixToolkit.Wpf.SharpDX.PerspectiveCamera { Position = new Point3D(0, 0, 0), LookDirection = new Vector3D(0, 0, -1), UpDirection = new Vector3D(0, 1, 0), FieldOfView = cameraNode.FieldOfView };
        viewport.Items.Add(new AmbientLight3D { Color = Colors.Gray });
        viewport.Items.Add(new DirectionalLight3D { Color = Colors.White, Direction = new Vector3D(-.5, -1, -.7) });
        viewport.IsShadowMappingEnabled = true;
        viewport.Items.Add(new ShadowMap3D { Resolution = new System.Windows.Size(2048, 2048), AutoCoverCompleteScene = true, IsSceneDynamic = true });
        UpdateCamera();
    }

    public IReadOnlyDictionary<string, MeshGeometryModel3D> Visuals => visuals;
    public float CameraYaw => orbitYaw;

    public void SetSelection(string? selectedId)
    {
        this.selectedId = selectedId;
        var selectedNode = activeProject is null ? null : GameProjectStore.Flatten(activeProject.Workspace.Children).FirstOrDefault(node => node.Id == selectedId);
        var selectedParts = selectedNode?.Type == "Model"
            ? GameProjectStore.Flatten(selectedNode.Children).Where(IsRenderable).Select(node => node.Id).ToHashSet(StringComparer.Ordinal)
            : null;
        foreach (var entry in visuals)
        {
            if (entry.Value is not OutLineMeshGeometryModel3D visual) continue;
            visual.EnableOutline = entry.Key == selectedId || selectedParts?.Contains(entry.Key) == true;
        }
    }

    public void Refresh(GameProject project)
    {
        activeProject = project;
        var currentCamera = project.Workspace.Children.First(node => node.Type == "Camera");
        if (!ReferenceEquals(cameraNode, currentCamera))
        {
            cameraNode = currentCamera;
            orbitYaw = cameraNode.Rotation[1];
            orbitPitch = cameraNode.Rotation[0];
        }
        foreach (var visual in visuals.Values) viewport.Items.Remove(visual);
        visuals.Clear();
        renderedAssetSources.Clear();
        viewport.IsShadowMappingEnabled = project.Settings.Shadows;
        foreach (var node in GameProjectStore.Flatten(project.Workspace.Children).Where(node => IsRenderable(node) && (node.Type != "Decal" || FindPartParent(project, node) is not null)))
        {
            var model = CreateVisual(node, project);
            visuals[node.Id] = model;
            viewport.Items.Add(model);
        }
        RefreshObjectLights(project);
        RefreshSkybox(project.Workspace, project);
        UpdateCamera(project);
        SetSelection(selectedId);
    }

    public void UpdateNode(SceneNode node, GameProject project)
    {
        if (SceneNodeTypes.IsLight(node) || SceneNodeTypes.IsBasePart(node) && node.Children.Any(SceneNodeTypes.IsLight)) RefreshObjectLights(project);
        if (node.Type == "Decal" && FindPartParent(project, node) is null) return;
        if (!IsRenderable(node) || !GameProjectStore.Flatten(project.Workspace.Children).Any(item => item.Id == node.Id)) return;
        var assetSource = GetAssetSource(node, project);
        if (visuals.TryGetValue(node.Id, out var existingModel)
            && (!renderedAssetSources.TryGetValue(node.Id, out var renderedAssetSource)
                || !string.Equals(renderedAssetSource, assetSource, StringComparison.OrdinalIgnoreCase)))
        {
            viewport.Items.Remove(existingModel);
            visuals.Remove(node.Id);
            renderedAssetSources.Remove(node.Id);
        }
        if (!visuals.TryGetValue(node.Id, out var model))
        {
            model = CreateVisual(node, project);
            visuals[node.Id] = model;
            viewport.Items.Add(model);
        }
        model.Transform = node.Type == "Decal" && FindPartParent(project, node) is { } parent ? TransformFor(parent) : TransformFor(node);
        model.IsThrowingShadow = node.Type != "Decal" && node.CastShadow;
        if (node.Type is "Decal" or "ImageLabel")
            model.Visibility = node.Visible && (node.Type != "Decal" || model.Material is PhongMaterial { DiffuseMap: not null })
                ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (node.Type is not ("ImageLabel" or "Decal") && !(node.Type == "Part" && !string.IsNullOrWhiteSpace(node.Texture))) model.Material = MakeMaterial(node);
        if (SceneNodeTypes.IsBasePart(node))
            foreach (var decal in node.Children.Where(child => child.Type == "Decal")) UpdateNode(decal, project);
        UpdateCamera(project);
    }

    public void SetCameraOrbit(float yawDelta, float pitchDelta, float zoomDelta = 0, GameProject? project = null)
    {
        orbitYaw += yawDelta;
        orbitPitch = Math.Clamp(orbitPitch + pitchDelta, -89, 89);
        if (cameraNode.CameraMode == "Custom")
        {
            cameraNode.Rotation[0] = orbitPitch;
            cameraNode.Rotation[1] = orbitYaw;
        }
        if (cameraNode.CameraMode == "Custom" && zoomDelta != 0)
        {
            var direction = LookDirection(cameraNode.Rotation[0], cameraNode.Rotation[1]);
            cameraNode.Position[0] -= direction.X * zoomDelta;
            cameraNode.Position[1] -= direction.Y * zoomDelta;
            cameraNode.Position[2] -= direction.Z * zoomDelta;
        }
        else orbitDistance = Math.Clamp(orbitDistance + zoomDelta, 2, 100);
        UpdateCamera(project);
    }

    public void MoveCamera(Vector3 direction, float amount, GameProject? project = null)
    {
        ApplyCameraInput(direction, amount, 0, 0, project);
    }

    public void ApplyCameraInput(Vector3 direction, float amount, float yawDelta, float pitchDelta, GameProject? project = null)
    {
        if (cameraNode.CameraMode != "Custom") return;
        cameraNode.Rotation[1] += yawDelta;
        cameraNode.Rotation[0] = Math.Clamp(cameraNode.Rotation[0] + pitchDelta, -89, 89);
        orbitYaw = cameraNode.Rotation[1];
        orbitPitch = cameraNode.Rotation[0];
        var yaw = Degrees(cameraNode.Rotation[1]);
        var forward = LookDirection(cameraNode.Rotation[0], cameraNode.Rotation[1]);
        var right = new Vector3((float)Math.Cos(yaw), 0, (float)-Math.Sin(yaw));
        var movement = right * direction.X + new Vector3(0, direction.Y, 0) + forward * direction.Z;
        if (movement.LengthSquared() > 1) movement.Normalize();
        cameraNode.Position[0] += movement.X * amount;
        cameraNode.Position[1] += movement.Y * amount;
        cameraNode.Position[2] += movement.Z * amount;
        UpdateCamera(project);
    }

    public void UpdateCamera(GameProject? project = null)
    {
        if (viewport.Camera is not HelixToolkit.Wpf.SharpDX.PerspectiveCamera camera) return;
        camera.FieldOfView = cameraNode.FieldOfView;
        if (cameraNode.CameraMode == "Subject")
        {
            if (project is null) return;
            var subject = GameProjectStore.Flatten(project.Workspace.Children).FirstOrDefault(node => node.Id == cameraNode.Subject && SceneNodeTypes.IsBasePart(node));
            if (subject is not null)
            {
                var target = ToVector(subject.Position);
                var yaw = orbitYaw * Math.PI / 180;
                var pitch = orbitPitch * Math.PI / 180;
                var position = target + new Vector3((float)(orbitDistance * Math.Cos(pitch) * Math.Sin(yaw)), (float)(orbitDistance * Math.Sin(-pitch)), (float)(orbitDistance * Math.Cos(pitch) * Math.Cos(yaw)));
                camera.Position = new Point3D(position.X, position.Y, position.Z);
                var look = target - ToVector(camera.Position);
                camera.LookDirection = new Vector3D(look.X, look.Y, look.Z);
                camera.UpDirection = new Vector3D(0, 1, 0);
                UpdateSkybox(camera.Position);
            }
            return;
        }
        var rotation = cameraNode.Rotation;
        orbitYaw = rotation[1]; orbitPitch = rotation[0];
        var direction = LookDirection(rotation[0], rotation[1]);
        camera.Position = new Point3D(cameraNode.Position[0], cameraNode.Position[1], cameraNode.Position[2]);
        camera.LookDirection = new Vector3D(direction.X, direction.Y, direction.Z);
        camera.UpDirection = new Vector3D(0, 1, 0);
        UpdateSkybox(camera.Position);
    }

    private MeshGeometryModel3D CreateVisual(SceneNode node, GameProject project)
    {
        var builder = new MeshBuilder();
        if (node.Type == "ImageLabel")
        {
            const float halfX = .5f; const float halfY = .5f;
            builder.AddQuad(new Vector3(-halfX, -halfY, 0), new Vector3(halfX, -halfY, 0), new Vector3(halfX, halfY, 0), new Vector3(-halfX, halfY, 0));
        }
        else if (node.Type == "Decal") AddDecalFace(builder, node.Face);
        else if (node.Type == "MeshPart" && !string.IsNullOrWhiteSpace(node.Asset) && File.Exists(ResolveAsset(project, node.Asset)))
        {
            try { builder = ReadObj(ResolveAsset(project, node.Asset)); }
            catch { builder.AddBox(Vector3.Zero, 1, 1, 1); }
        }
        else if (node.Type == "Part" && node.Shape == "Ball") builder.AddSphere(Vector3.Zero, .5, 24, 16);
        else if (node.Type == "Part" && node.Shape == "Cylinder") builder.AddCylinder(new Vector3(0, -.5f, 0), new Vector3(0, .5f, 0), .5, 24, true, true);
        else if (node.Type == "WedgePart") AddWedge(builder);
        else if (node.Type == "CornerWedgePart") AddCornerWedge(builder);
        else if (node.Type == "SpawnLocation") AddSpawnLocation(builder);
        else if (node.Type == "TrussPart") AddTruss(builder);
        else builder.AddBox(Vector3.Zero, 1, 1, 1);
        var decalParent = node.Type == "Decal" ? FindPartParent(project, node) : null;
        var visual = new OutLineMeshGeometryModel3D { Geometry = builder.ToMeshGeometry3D(), Material = MakeMaterial(node), Transform = decalParent is null ? TransformFor(node) : TransformFor(decalParent), IsThrowingShadow = node.Type != "Decal" && node.CastShadow, IsHitTestVisible = node.Type != "Decal", IsTransparent = node.Type is ("ImageLabel" or "Decal") || EffectiveTransparency(node) > 0, EnableOutline = false, OutlineColor = Colors.Orange, Visibility = node.Type is ("Decal" or "ImageLabel") && !node.Visible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible };
        if (node.Type == "ImageLabel" && !string.IsNullOrWhiteSpace(node.Asset)) LoadImageAsset(visual, ResolveAsset(project, node.Asset), node);
        if (node.Type == "Decal" && !string.IsNullOrWhiteSpace(node.Asset)) LoadImageAsset(visual, ResolveAsset(project, node.Asset), node);
        if (node.Type == "Part" && !string.IsNullOrWhiteSpace(node.Texture)) LoadImageAsset(visual, ResolveTexture(project, node.Texture), node);
        if (node.Type == "Decal" && visual.Material is not PhongMaterial { DiffuseMap: not null })
            visual.Visibility = System.Windows.Visibility.Collapsed;
        renderedAssetSources[node.Id] = GetAssetSource(node, project);
        return visual;
    }

    private static string GetAssetSource(SceneNode node, GameProject project) => node.Type switch
    {
        "ImageLabel" => ResolveAsset(project, node.Asset),
        "Decal" => $"{node.Face}|{ResolveAsset(project, node.Asset)}|{node.Transparency.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        "MeshPart" => $"{ResolveAsset(project, node.Asset)}|{MaterialKey(node)}",
        "Part" => $"{node.Shape}|{ResolveTexture(project, node.Texture)}|{MaterialKey(node)}",
        "WedgePart" or "CornerWedgePart" or "SpawnLocation" or "TrussPart" => MaterialKey(node),
        _ => string.Empty
    };

    private static string MaterialKey(SceneNode node) => $"{node.Material}|{node.Transparency.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static float EffectiveTransparency(SceneNode node) => Math.Clamp(Math.Max(node.Transparency, node.Material == "Glass" ? .55f : 0), 0, 1);

    private static bool IsRenderable(SceneNode node) => SceneNodeTypes.IsBasePart(node) || node.Type is "ImageLabel" or "Decal";

    private static SceneNode? FindPartParent(GameProject project, SceneNode child) =>
        GameProjectStore.Flatten(project.Workspace.Children).FirstOrDefault(parent => SceneNodeTypes.IsBasePart(parent) && parent.Children.Contains(child));

    private static void AddDecalFace(MeshBuilder builder, string face)
    {
        const float edge = .5f;
        const float surface = .502f;
        switch (face)
        {
            case "Back":
                builder.AddQuad(new Vector3(-edge, -edge, surface), new Vector3(edge, -edge, surface), new Vector3(edge, edge, surface), new Vector3(-edge, edge, surface));
                break;
            case "Left":
                builder.AddQuad(new Vector3(-surface, -edge, -edge), new Vector3(-surface, -edge, edge), new Vector3(-surface, edge, edge), new Vector3(-surface, edge, -edge));
                break;
            case "Right":
                builder.AddQuad(new Vector3(surface, -edge, edge), new Vector3(surface, -edge, -edge), new Vector3(surface, edge, -edge), new Vector3(surface, edge, edge));
                break;
            case "Top":
                builder.AddQuad(new Vector3(-edge, surface, edge), new Vector3(edge, surface, edge), new Vector3(edge, surface, -edge), new Vector3(-edge, surface, -edge));
                break;
            case "Bottom":
                builder.AddQuad(new Vector3(-edge, -surface, -edge), new Vector3(edge, -surface, -edge), new Vector3(edge, -surface, edge), new Vector3(-edge, -surface, edge));
                break;
            default:
                builder.AddQuad(new Vector3(edge, -edge, -surface), new Vector3(-edge, -edge, -surface), new Vector3(-edge, edge, -surface), new Vector3(edge, edge, -surface));
                break;
        }
    }

    private static Point3D FaceCenter(string face) => face switch
    {
        "Back" => new Point3D(0, 0, .501),
        "Left" => new Point3D(-.501, 0, 0),
        "Right" => new Point3D(.501, 0, 0),
        "Top" => new Point3D(0, .501, 0),
        "Bottom" => new Point3D(0, -.501, 0),
        _ => new Point3D(0, 0, -.501)
    };

    private static Vector3D FaceNormal(string face) => face switch
    {
        "Back" => new Vector3D(0, 0, 1),
        "Left" => new Vector3D(-1, 0, 0),
        "Right" => new Vector3D(1, 0, 0),
        "Top" => new Vector3D(0, 1, 0),
        "Bottom" => new Vector3D(0, -1, 0),
        _ => new Vector3D(0, 0, -1)
    };

    private static void AddWedge(MeshBuilder builder)
    {
        var a = new Vector3(-.5f, -.5f, -.5f);
        var b = new Vector3(.5f, -.5f, -.5f);
        var c = new Vector3(.5f, -.5f, .5f);
        var d = new Vector3(-.5f, -.5f, .5f);
        var e = new Vector3(-.5f, .5f, .5f);
        var f = new Vector3(.5f, .5f, .5f);
        builder.AddQuad(a, b, c, d);
        builder.AddQuad(d, c, f, e);
        builder.AddQuad(a, e, f, b);
        builder.AddTriangle(a, d, e);
        builder.AddTriangle(b, f, c);
    }

    private static void AddCornerWedge(MeshBuilder builder)
    {
        var a = new Vector3(-.5f, -.5f, -.5f);
        var b = new Vector3(.5f, -.5f, -.5f);
        var c = new Vector3(.5f, -.5f, .5f);
        var d = new Vector3(-.5f, -.5f, .5f);
        var e = new Vector3(-.5f, .5f, .5f);
        builder.AddQuad(a, b, c, d);
        builder.AddTriangle(a, d, e);
        builder.AddTriangle(d, c, e);
        builder.AddTriangle(c, b, e);
        builder.AddTriangle(b, a, e);
    }

    private static void AddSpawnLocation(MeshBuilder builder)
    {
        builder.AddBox(new Vector3(0, -.06f, 0), 1, .88, 1);
        builder.AddBox(new Vector3(0, .41f, 0), .65, .06, .13);
        builder.AddBox(new Vector3(0, .41f, 0), .13, .06, .65);
    }

    private static void AddTruss(MeshBuilder builder)
    {
        foreach (var x in new[] { -.42f, .42f })
        foreach (var z in new[] { -.42f, .42f })
            builder.AddBox(new Vector3(x, 0, z), .12, 1, .12);
        foreach (var y in new[] { -.42f, 0, .42f })
        {
            builder.AddBox(new Vector3(0, y, -.42f), .9, .1, .1);
            builder.AddBox(new Vector3(0, y, .42f), .9, .1, .1);
            builder.AddBox(new Vector3(-.42f, y, 0), .1, .1, .9);
            builder.AddBox(new Vector3(.42f, y, 0), .1, .1, .9);
        }
        builder.AddCylinder(new Vector3(-.42f, -.42f, -.42f), new Vector3(.42f, .42f, -.42f), .045, 8, true, true);
        builder.AddCylinder(new Vector3(.42f, -.42f, .42f), new Vector3(-.42f, .42f, .42f), .045, 8, true, true);
    }

    private void RefreshObjectLights(GameProject project)
    {
        foreach (var light in objectLights) viewport.Items.Remove(light);
        objectLights.Clear();
        foreach (var parent in GameProjectStore.Flatten(project.Workspace.Children).Where(SceneNodeTypes.IsBasePart))
        {
            foreach (var node in parent.Children.Where(SceneNodeTypes.IsLight))
            {
                if (!node.Enabled) continue;
                System.Windows.Media.Color color;
                try { color = (System.Windows.Media.Color)ColorConverter.ConvertFromString(node.Color); }
                catch { color = Colors.White; }
                var strength = Math.Clamp(node.Brightness, 0, 8);
                color = System.Windows.Media.Color.FromRgb(
                    (byte)Math.Clamp(color.R * strength, 0, 255),
                    (byte)Math.Clamp(color.G * strength, 0, 255),
                    (byte)Math.Clamp(color.B * strength, 0, 255));
                var rotation = TransformFor(parent, includeScale: false);
                var offset = new Vector3D(node.Position[0], node.Position[1], node.Position[2]);
                Light3D light;
                if (node.Type == "SurfaceLight")
                {
                    var position = TransformFor(parent).Transform(FaceCenter(node.Face)) + rotation.Transform(offset);
                    var direction = rotation.Transform(FaceNormal(node.Face));
                    direction.Normalize();
                    light = new SpotLight3D
                    {
                        Color = color, Position = position, Range = Math.Clamp(node.Range, 1, 1000),
                        Direction = direction,
                        InnerAngle = Math.Clamp(node.Angle * .65f, 1, 170), OuterAngle = Math.Clamp(node.Angle, 1, 175)
                    };
                }
                else
                {
                    var position = rotation.Transform(new Point3D(node.Position[0], node.Position[1], node.Position[2]));
                    var direction = LookDirection(parent.Rotation[0] + node.Rotation[0], parent.Rotation[1] + node.Rotation[1]);
                    light = node.Type == "SpotLight"
                        ? new SpotLight3D
                        {
                            Color = color, Position = position, Range = Math.Clamp(node.Range, 1, 1000),
                            Direction = new Vector3D(direction.X, direction.Y, direction.Z),
                            InnerAngle = Math.Clamp(node.Angle * .65f, 1, 170), OuterAngle = Math.Clamp(node.Angle, 1, 175)
                        }
                        : new PointLight3D { Color = color, Position = position, Range = Math.Clamp(node.Range, 1, 1000) };
                }
                objectLights.Add(light);
                viewport.Items.Add(light);
            }
        }
    }

    private void RefreshSkybox(SceneNode workspace, GameProject project)
    {
        foreach (var visual in skyboxVisuals) viewport.Items.Remove(visual.Model);
        skyboxVisuals.Clear();
        foreach (var face in SkyboxFaces)
        {
            var relativePath = face.Property switch
            {
                "SkyboxFront" => workspace.SkyboxFront,
                "SkyboxBack" => workspace.SkyboxBack,
                "SkyboxLeft" => workspace.SkyboxLeft,
                "SkyboxRight" => workspace.SkyboxRight,
                "SkyboxTop" => workspace.SkyboxTop,
                "SkyboxBottom" => workspace.SkyboxBottom,
                _ => string.Empty
            };
            if (string.IsNullOrWhiteSpace(relativePath)) continue;
            var path = ResolveTexture(project, relativePath);
            if (!File.Exists(path)) continue;
            try
            {
                ValidateImageFile(path);
                var builder = new MeshBuilder();
                builder.AddQuad(new Vector3(-2500, -2500, 0), new Vector3(2500, -2500, 0), new Vector3(2500, 2500, 0), new Vector3(-2500, 2500, 0));
                var visual = new MeshGeometryModel3D
                {
                    Geometry = builder.ToMeshGeometry3D(),
                    Material = new PhongMaterial { DiffuseMap = TextureModel.Create(path), DiffuseColor = new Color4(1, 1, 1, 1), SpecularColor = new Color4(0, 0, 0, 1) },
                    IsThrowingShadow = false,
                    IsHitTestVisible = false,
                    CullMode = SharpDX.Direct3D11.CullMode.None
                };
                skyboxVisuals.Add((visual, face.Direction, face.Pitch, face.Yaw));
                viewport.Items.Add(visual);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException or FileFormatException or SharpDX.SharpDXException)
            {
            }
        }
    }

    private void UpdateSkybox(Point3D cameraPosition)
    {
        foreach (var face in skyboxVisuals)
        {
            var center = ToVector(cameraPosition) + face.Direction * 1000;
            var transform = new Transform3DGroup();
            transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), face.Pitch)));
            transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), face.Yaw)));
            transform.Children.Add(new TranslateTransform3D(center.X, center.Y, center.Z));
            face.Model.Transform = transform;
        }
    }

    private static readonly (string Property, Vector3 Direction, float Pitch, float Yaw)[] SkyboxFaces =
    [
        ("SkyboxFront", new Vector3(0, 0, 1), 0, 0),
        ("SkyboxBack", new Vector3(0, 0, -1), 0, 180),
        ("SkyboxLeft", new Vector3(-1, 0, 0), 0, -90),
        ("SkyboxRight", new Vector3(1, 0, 0), 0, 90),
        ("SkyboxTop", new Vector3(0, 1, 0), -90, 0),
        ("SkyboxBottom", new Vector3(0, -1, 0), 90, 0)
    ];

    private static PhongMaterial MakeMaterial(SceneNode node)
    {
        System.Windows.Media.Color parsed;
        try
        {
            parsed = (System.Windows.Media.Color)ColorConverter.ConvertFromString(node.Color);
        }
        catch { parsed = Colors.CornflowerBlue; }
        var red = parsed.R / 255f;
        var green = parsed.G / 255f;
        var blue = parsed.B / 255f;
        var specular = node.Material switch { "Metal" => .8f, "Glass" => .65f, "Plastic" => .14f, _ => .03f };
        return new PhongMaterial
        {
            DiffuseColor = new Color4(red, green, blue, parsed.A / 255f * (1 - EffectiveTransparency(node))),
            SpecularColor = new Color4(specular, specular, specular, 1),
            SpecularShininess = node.Material is "Metal" or "Glass" ? 100 : 24,
            EmissiveColor = node.Material == "Neon" ? new Color4(red * .5f, green * .5f, blue * .5f, 1) : new Color4(0, 0, 0, 1)
        };
    }

    private static Transform3D TransformFor(SceneNode node, bool includeScale = true)
    {
        var transform = new Transform3DGroup();
        if (includeScale) transform.Children.Add(new ScaleTransform3D(node.Size[0], node.Size[1], node.Size[2]));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), node.Rotation[2])));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), node.Rotation[1])));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), node.Rotation[0])));
        transform.Children.Add(new TranslateTransform3D(node.Position[0], node.Position[1], node.Position[2]));
        return transform;
    }

    private static float Degrees(float value) => (float)(value * Math.PI / 180);
    private static Vector3 ToVector(float[] values) => new(values[0], values[1], values[2]);
    private static Vector3 ToVector(Point3D point) => new((float)point.X, (float)point.Y, (float)point.Z);
    private static Vector3 LookDirection(float pitch, float yaw)
    {
        var p = Degrees(pitch); var y = Degrees(yaw);
        return new Vector3((float)(-Math.Cos(p) * Math.Sin(y)), (float)Math.Sin(p), (float)(-Math.Cos(p) * Math.Cos(y)));
    }

    private static MeshBuilder ReadObj(string path)
    {
        var vertices = new List<Vector3>();
        var builder = new MeshBuilder();
        foreach (var line in File.ReadLines(path))
        {
            var fields = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length == 4 && fields[0] == "v" && float.TryParse(fields[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) && float.TryParse(fields[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) && float.TryParse(fields[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z)) vertices.Add(new Vector3(x, y, z));
            if (fields.Length < 4 || fields[0] != "f") continue;
            var indices = fields.Skip(1).Select(part => { var index = int.Parse(part.Split('/')[0], System.Globalization.CultureInfo.InvariantCulture); return index < 0 ? vertices.Count + index : index - 1; }).ToArray();
            for (var index = 1; index < indices.Length - 1; index++) builder.AddTriangle(vertices[indices[0]], vertices[indices[index]], vertices[indices[index + 1]]);
        }
        return builder;
    }

    private static string ResolveAsset(GameProject project, string relative)
    {
        return ResolveSafeAsset(project, relative);
    }

    private static string ResolveTexture(GameProject project, string relative)
    {
        return ResolveSafeAsset(project, relative);
    }

    private static string ResolveSafeAsset(GameProject project, string relative)
    {
        try { return GameProjectStore.GetSafeAssetFilePathInAssets(project, relative); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return string.Empty; }
    }

    private static void LoadImageAsset(MeshGeometryModel3D model, string path, SceneNode node)
    {
        if (!File.Exists(path)) return;
        try
        {
            ValidateImageFile(path);
            var material = MakeMaterial(node);
            material.DiffuseMap = TextureModel.Create(path);
            material.DiffuseColor = new Color4(1, 1, 1, 1 - EffectiveTransparency(node));
            model.Material = material;
        }
        catch { }
    }

    private static void ValidateImageFile(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0 || decoder.Frames[0].PixelWidth <= 0 || decoder.Frames[0].PixelHeight <= 0)
            throw new InvalidDataException("The image asset has no decodable frames.");
    }

    private static void LoadMeshAsset(MeshGeometryModel3D model, string path)
    {
        if (!File.Exists(path)) return;
        try { model.Geometry = ReadObj(path).ToMeshGeometry3D(); }
        catch { }
    }
}
