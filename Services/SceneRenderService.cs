using System.Windows.Media;
using System.Windows.Media.Media3D;
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
    private readonly SceneNode cameraNode;
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

    public void Refresh(GameProject project)
    {
        foreach (var visual in visuals.Values) viewport.Items.Remove(visual);
        visuals.Clear();
        viewport.IsShadowMappingEnabled = project.Settings.Shadows;
        foreach (var node in GameProjectStore.Flatten(project.Workspace.Children).Where(IsRenderable))
        {
            var model = CreateVisual(node, project);
            visuals[node.Id] = model;
            viewport.Items.Add(model);
        }
        UpdateCamera(project);
    }

    public void UpdateNode(SceneNode node, GameProject project)
    {
        if (!IsRenderable(node)) return;
        if (!visuals.TryGetValue(node.Id, out var model))
        {
            model = CreateVisual(node, project);
            visuals[node.Id] = model;
            viewport.Items.Add(model);
        }
        model.Transform = TransformFor(node);
        if (node.Type != "ImageLabel") model.Material = MakeMaterial(node.Color);
        if (node.Type == "MeshPart" && !string.IsNullOrWhiteSpace(node.Asset)) LoadMeshAsset(model, ResolveAsset(project, node.Asset));
        if (node.Type == "ImageLabel" && !string.IsNullOrWhiteSpace(node.Asset)) LoadImageAsset(model, ResolveAsset(project, node.Asset));
        UpdateCamera(project);
    }

    public void SetCameraOrbit(float yawDelta, float pitchDelta, float zoomDelta = 0, GameProject? project = null)
    {
        orbitYaw += yawDelta;
        orbitPitch = Math.Clamp(orbitPitch + pitchDelta, -89, 89);
        if (cameraNode.CameraMode == "Custom" && zoomDelta != 0)
        {
            var direction = LookDirection(cameraNode.Rotation[0], cameraNode.Rotation[1]);
            cameraNode.Position[0] += direction.X * zoomDelta;
            cameraNode.Position[1] += direction.Y * zoomDelta;
            cameraNode.Position[2] += direction.Z * zoomDelta;
        }
        else orbitDistance = Math.Clamp(orbitDistance + zoomDelta, 2, 100);
        UpdateCamera(project);
    }

    public void MoveCamera(Vector3 direction, float amount, GameProject? project = null)
    {
        if (cameraNode.CameraMode != "Custom") return;
        cameraNode.Position[0] += direction.X * amount;
        cameraNode.Position[1] += direction.Y * amount;
        cameraNode.Position[2] += direction.Z * amount;
        UpdateCamera(project);
    }

    public void UpdateCamera(GameProject? project = null)
    {
        if (viewport.Camera is not HelixToolkit.Wpf.SharpDX.PerspectiveCamera camera) return;
        camera.FieldOfView = cameraNode.FieldOfView;
        if (cameraNode.CameraMode == "Subject" && project is not null)
        {
            var subject = GameProjectStore.Flatten(project.Workspace.Children).FirstOrDefault(node => node.Id == cameraNode.Subject);
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
                return;
            }
        }
        var rotation = cameraNode.Rotation;
        orbitYaw = rotation[1]; orbitPitch = rotation[0];
        var direction = LookDirection(rotation[0], rotation[1]);
        camera.Position = new Point3D(cameraNode.Position[0], cameraNode.Position[1], cameraNode.Position[2]);
        camera.LookDirection = new Vector3D(direction.X, direction.Y, direction.Z);
        camera.UpDirection = new Vector3D(0, 1, 0);
    }

    private MeshGeometryModel3D CreateVisual(SceneNode node, GameProject project)
    {
        var builder = new MeshBuilder();
        if (node.Type == "ImageLabel")
        {
            const float halfX = .5f; const float halfY = .5f;
            builder.AddQuad(new Vector3(-halfX, -halfY, 0), new Vector3(halfX, -halfY, 0), new Vector3(halfX, halfY, 0), new Vector3(-halfX, halfY, 0));
        }
        else if (node.Type == "MeshPart" && !string.IsNullOrWhiteSpace(node.Asset) && File.Exists(ResolveAsset(project, node.Asset)))
        {
            try { builder = ReadObj(ResolveAsset(project, node.Asset)); }
            catch { builder.AddBox(Vector3.Zero, 1, 1, 1); }
        }
        else builder.AddBox(Vector3.Zero, 1, 1, 1);
        var visual = new MeshGeometryModel3D { Geometry = builder.ToMeshGeometry3D(), Material = MakeMaterial(node.Color), Transform = TransformFor(node), IsThrowingShadow = node.CastShadow, IsHitTestVisible = true };
        if (node.Type == "ImageLabel" && !string.IsNullOrWhiteSpace(node.Asset)) LoadImageAsset(visual, ResolveAsset(project, node.Asset));
        return visual;
    }

    private static bool IsRenderable(SceneNode node) => node.Type is "Part" or "MeshPart" or "ImageLabel";

    private static PhongMaterial MakeMaterial(string color)
    {
        try
        {
            var parsed = (System.Windows.Media.Color)ColorConverter.ConvertFromString(color);
            return new PhongMaterial { DiffuseColor = new Color4(parsed.R / 255f, parsed.G / 255f, parsed.B / 255f, parsed.A / 255f), SpecularColor = new Color4(.14f, .14f, .14f, 1), SpecularShininess = 24 };
        }
        catch { return PhongMaterials.Blue; }
    }

    private static Transform3D TransformFor(SceneNode node)
    {
        var transform = new Transform3DGroup();
        transform.Children.Add(new ScaleTransform3D(node.Size[0], node.Size[1], node.Size[2]));
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
        var root = Path.GetFullPath(Path.Combine(project.ProjectPath, "assets")) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(project.ProjectPath, relative));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path : string.Empty;
    }

    private static void LoadImageAsset(MeshGeometryModel3D model, string path)
    {
        if (!File.Exists(path)) return;
        try { model.Material = new PhongMaterial { DiffuseMap = TextureModel.Create(path), DiffuseColor = new Color4(1, 1, 1, 1), SpecularColor = new Color4(0, 0, 0, 1) }; }
        catch { }
    }

    private static void LoadMeshAsset(MeshGeometryModel3D model, string path)
    {
        if (!File.Exists(path)) return;
        try { model.Geometry = ReadObj(path).ToMeshGeometry3D(); }
        catch { }
    }
}
