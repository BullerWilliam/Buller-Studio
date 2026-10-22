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
    private readonly bool showAttachmentGizmos;
    private readonly DefaultEffectsManager effectsManager = new();
    private readonly AmbientLight3D ambientLight = new() { Color = Colors.Gray };
    private readonly DirectionalLight3D sunLight = new() { Color = Colors.White, Direction = new Vector3D(-.5, -1, -.7) };
    private readonly Dictionary<string, MeshGeometryModel3D> visuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MeshGeometryModel3D> highlightFills = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MeshGeometryModel3D> forceFieldVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (MeshGeometryModel3D Outer, MeshGeometryModel3D? Inner)> particleEffectVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> particleEffectTimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ParticleBurst>> particleBursts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> particleClearVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> particleClearTimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> particleDisabledTimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> particleWasEnabled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTime Modified, TextureModel? Texture)> particleTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OutLineMeshGeometryModel3D> beamVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BeamState> beamStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MeshGeometryModel3D> trailVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TrailSample>> trailSamples = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> trailTimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> trailClearVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> trailWasEnabled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SceneNode> highlightSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> renderedAssetSources = new(StringComparer.Ordinal);
    private readonly List<Light3D> objectLights = [];
    private readonly List<(MeshGeometryModel3D Model, Vector3 Direction, float Pitch, float Yaw)> skyboxVisuals = [];
    private SceneNode cameraNode;
    private GameProject? activeProject;
    private string? selectedId;
    private float orbitYaw = 35;
    private float orbitPitch = -24;
    private float orbitDistance = 12;
    private bool hasBeamNodes;
    private bool hasTrailNodes;
    private bool hasVisibleForceFields;
    private bool hasParticleEffects;

    private sealed record BeamState(Point3D Start, Point3D End, float Width0, float Width1, string Color, float Transparency);
    private sealed record ParticleBurst(float StartedAt, int Count);

    public SceneRenderService(Viewport3DX viewport, SceneNode cameraNode, bool showAttachmentGizmos = false)
    {
        this.viewport = viewport;
        this.cameraNode = cameraNode;
        this.showAttachmentGizmos = showAttachmentGizmos;
        viewport.EffectsManager = effectsManager;
        viewport.Camera = new HelixToolkit.Wpf.SharpDX.PerspectiveCamera { Position = new Point3D(0, 0, 0), LookDirection = new Vector3D(0, 0, -1), UpDirection = new Vector3D(0, 1, 0), FieldOfView = cameraNode.FieldOfView };
        viewport.Items.Add(ambientLight);
        viewport.Items.Add(sunLight);
        viewport.IsShadowMappingEnabled = true;
        viewport.Items.Add(new ShadowMap3D { Resolution = new System.Windows.Size(2048, 2048), AutoCoverCompleteScene = true, IsSceneDynamic = true });
        UpdateCamera();
    }

    public IReadOnlyDictionary<string, MeshGeometryModel3D> Visuals => visuals;
    public IReadOnlyDictionary<string, OutLineMeshGeometryModel3D> BeamVisuals => beamVisuals;
    public IReadOnlyDictionary<string, MeshGeometryModel3D> HighlightFills => highlightFills;
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
            var isSelected = entry.Key == selectedId || selectedParts?.Contains(entry.Key) == true
                || selectedNode?.Type == "Highlight" && highlightSources.TryGetValue(entry.Key, out var selectedHighlight) && selectedHighlight.Id == selectedId;
            highlightSources.TryGetValue(entry.Key, out var highlight);
            visual.EnableOutline = isSelected || highlight is not null && highlight.Enabled && highlight.OutlineTransparency < 1;
            visual.OutlineColor = isSelected ? Colors.Orange : highlight is null
                ? Colors.Orange : ParseHighlightColor(highlight.OutlineColor, highlight.OutlineTransparency, Colors.White);
        }
        foreach (var (id, beam) in beamVisuals)
        {
            beam.EnableOutline = id == selectedId;
            beam.OutlineColor = Colors.Orange;
        }
    }

    public void Refresh(GameProject project)
    {
        if (!ReferenceEquals(activeProject, project))
        {
            particleEffectTimes.Clear();
            particleBursts.Clear();
            particleClearVersions.Clear();
            particleClearTimes.Clear();
            particleDisabledTimes.Clear();
            particleWasEnabled.Clear();
            particleTextures.Clear();
            trailSamples.Clear();
            trailTimes.Clear();
            trailClearVersions.Clear();
            trailWasEnabled.Clear();
        }
        activeProject = project;
        var currentCamera = project.Workspace.Children.First(node => node.Type == "Camera");
        if (!ReferenceEquals(cameraNode, currentCamera))
        {
            cameraNode = currentCamera;
            orbitYaw = cameraNode.Rotation[1];
            orbitPitch = cameraNode.Rotation[0];
        }
        foreach (var visual in visuals.Values) viewport.Items.Remove(visual);
        foreach (var fill in highlightFills.Values) viewport.Items.Remove(fill);
        foreach (var shield in forceFieldVisuals.Values) viewport.Items.Remove(shield);
        foreach (var (outer, inner) in particleEffectVisuals.Values)
        {
            viewport.Items.Remove(outer);
            if (inner is not null) viewport.Items.Remove(inner);
        }
        visuals.Clear();
        highlightFills.Clear();
        forceFieldVisuals.Clear();
        particleEffectVisuals.Clear();
        highlightSources.Clear();
        renderedAssetSources.Clear();
        foreach (var beam in beamVisuals.Values) viewport.Items.Remove(beam);
        beamVisuals.Clear();
        beamStates.Clear();
        foreach (var visual in trailVisuals.Values) viewport.Items.Remove(visual);
        trailVisuals.Clear();
        viewport.IsShadowMappingEnabled = project.Settings.Shadows;
        RefreshLighting(project.Lighting);
        foreach (var node in GameProjectStore.Flatten(project.Workspace.Children).Where(node => IsRenderable(node) && (node.Type is not ("Decal" or "Texture" or "Attachment") || FindPartParent(project, node) is not null)))
        {
            var model = CreateVisual(node, project);
            visuals[node.Id] = model;
            viewport.Items.Add(model);
        }
        RefreshObjectLights(project);
        RefreshBeams(project);
        RefreshTrails(project);
        RefreshHighlights(project);
        RefreshForceFields(project);
        RefreshParticleEffects(project);
        RefreshSkybox(project.Workspace, project);
        UpdateCamera(project);
        SetSelection(selectedId);
    }

    public void UpdateNode(SceneNode node, GameProject project)
    {
        if (node.Type is "Lighting" or "Sky")
        {
            Refresh(project);
            return;
        }
        if (node.Type == "Model")
        {
            if (hasBeamNodes) RefreshBeams(project);
            if (hasTrailNodes) RefreshTrails(project);
            if (hasVisibleForceFields) RefreshForceFields(project);
            if (hasParticleEffects) RefreshParticleEffects(project);
            if (showAttachmentGizmos)
                foreach (var attachment in GameProjectStore.Flatten(node.Children).Where(child => child.Type == "Attachment"))
                    UpdateAttachmentVisual(attachment, project);
        }
        if (node.Type == "Attachment")
        {
            if (showAttachmentGizmos) UpdateAttachmentVisual(node, project);
            RefreshBeams(project);
            if (hasTrailNodes) RefreshTrails(project);
            if (hasParticleEffects) RefreshParticleEffects(project);
            SetSelection(selectedId);
            return;
        }
        if (node.Type == "Beam")
        {
            RefreshBeams(project);
            SetSelection(selectedId);
            return;
        }
        if (node.Type == "Trail")
        {
            RefreshTrails(project);
            return;
        }
        if (SceneNodeTypes.IsLight(node) || SceneNodeTypes.IsBasePart(node) && node.Children.Any(SceneNodeTypes.IsLight)) RefreshObjectLights(project);
        if (node.Type == "Highlight")
        {
            RefreshHighlights(project);
            SetSelection(selectedId);
            return;
        }
        if (node.Type == "ForceField")
        {
            RefreshForceFields(project);
            return;
        }
        if (SceneNodeTypes.IsParticleEffect(node))
        {
            RefreshParticleEffects(project);
            return;
        }
        if (node.Type == "Explosion")
        {
            if (visuals.Remove(node.Id, out var oldExplosion)) viewport.Items.Remove(oldExplosion);
            if (GameProjectStore.Flatten(project.Workspace.Children).Any(item => item.Id == node.Id))
            {
                var explosionVisual = CreateVisual(node, project);
                visuals[node.Id] = explosionVisual;
                viewport.Items.Add(explosionVisual);
                SetSelection(selectedId);
            }
            return;
        }
        if (node.Type is "Decal" or "Texture" && FindPartParent(project, node) is null) return;
        if (!IsRenderable(node) || !GameProjectStore.Flatten(project.Workspace.Children).Any(item => item.Id == node.Id)) return;
        var assetSource = GetAssetSource(node, project);
        var refreshSelection = false;
        if (visuals.TryGetValue(node.Id, out var existingModel)
            && (!renderedAssetSources.TryGetValue(node.Id, out var renderedAssetSource)
                || !string.Equals(renderedAssetSource, assetSource, StringComparison.OrdinalIgnoreCase)))
        {
            viewport.Items.Remove(existingModel);
            visuals.Remove(node.Id);
            renderedAssetSources.Remove(node.Id);
            refreshSelection = true;
        }
        if (!visuals.TryGetValue(node.Id, out var model))
        {
            model = CreateVisual(node, project);
            visuals[node.Id] = model;
            viewport.Items.Add(model);
            refreshSelection = true;
        }
        model.Transform = node.Type is "Decal" or "Texture" && FindPartParent(project, node) is { } parent ? TransformFor(parent) : TransformFor(node);
        model.IsThrowingShadow = node.Type is not ("Decal" or "Texture") && node.CastShadow;
        if (node.Type is "Decal" or "Texture" or "ImageLabel")
            model.Visibility = node.Visible && (node.Type is not ("Decal" or "Texture") || model.Material is PhongMaterial { DiffuseMap: not null })
                ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (node.Type is not ("ImageLabel" or "Decal" or "Texture") && !(node.Type == "Part" && !string.IsNullOrWhiteSpace(node.Texture))) model.Material = MakeMaterial(node);
        if (SceneNodeTypes.IsBasePart(node))
        {
            if (hasBeamNodes) RefreshBeams(project);
            if (hasTrailNodes) RefreshTrails(project);
            if (hasVisibleForceFields) RefreshForceFields(project);
            if (hasParticleEffects) RefreshParticleEffects(project);
            if (showAttachmentGizmos)
                foreach (var attachment in node.Children.Where(child => child.Type == "Attachment"))
                    UpdateAttachmentVisual(attachment, project);
            foreach (var decal in node.Children.Where(child => child.Type is "Decal" or "Texture")) UpdateNode(decal, project);
            if (highlightFills.TryGetValue(node.Id, out var fill))
            {
                fill.Geometry = model.Geometry;
                fill.Transform = HighlightTransformFor(node);
            }
        }
        if (refreshSelection) SetSelection(selectedId);
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
                if (activeProject is not null && hasParticleEffects) RefreshParticleEffects(activeProject);
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
        if (activeProject is not null && hasParticleEffects) RefreshParticleEffects(activeProject);
    }

    private MeshGeometryModel3D CreateVisual(SceneNode node, GameProject project)
    {
        var builder = new MeshBuilder();
        if (node.Type == "ImageLabel")
        {
            const float halfX = .5f; const float halfY = .5f;
            builder.AddQuad(new Vector3(-halfX, -halfY, 0), new Vector3(halfX, -halfY, 0), new Vector3(halfX, halfY, 0), new Vector3(-halfX, halfY, 0));
        }
        else if (node.Type is "Decal" or "Texture") AddDecalFace(builder, node.Face);
        else if (node.Type == "Attachment") builder.AddSphere(Vector3.Zero, .09, 12, 8);
        else if (node.Type == "Explosion") builder.AddSphere(Vector3.Zero, node.ExplosionAge < 0 ? Math.Max(.1f, node.BlastRadius) : 2 + node.ExplosionAge * 6, 24, 16);
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
        else if (node.Type == "Seat") AddSeat(builder);
        else if (node.Type == "TrussPart") AddTruss(builder);
        else builder.AddBox(Vector3.Zero, 1, 1, 1);
        var decalParent = node.Type is "Decal" or "Texture" ? FindPartParent(project, node) : null;
        var geometry = builder.ToMeshGeometry3D();
        if (node.Type == "Texture" && decalParent is not null) ApplyTextureTiling(geometry, node, decalParent);
        var visual = new OutLineMeshGeometryModel3D { Geometry = geometry, Material = MakeMaterial(node), Transform = node.Type == "Attachment" ? AttachmentTransformFor(node, project) : node.Type == "Explosion" ? new TranslateTransform3D(node.Position[0], node.Position[1], node.Position[2]) : decalParent is null ? TransformFor(node) : TransformFor(decalParent), IsThrowingShadow = node.Type is not ("Decal" or "Texture" or "Attachment" or "Explosion") && node.CastShadow, IsHitTestVisible = node.Type is not ("Decal" or "Texture" or "Explosion"), IsTransparent = node.Type is ("ImageLabel" or "Decal" or "Texture" or "Explosion") || EffectiveTransparency(node) > 0, EnableOutline = false, OutlineColor = Colors.Orange, Visibility = node.Type is ("Decal" or "Texture" or "ImageLabel" or "Explosion") && !node.Visible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible };
        if (node.Type == "ImageLabel" && !string.IsNullOrWhiteSpace(node.Asset)) LoadImageAsset(visual, ResolveAsset(project, node.Asset), node);
        if (node.Type is "Decal" or "Texture" && !string.IsNullOrWhiteSpace(node.Asset)) LoadImageAsset(visual, ResolveAsset(project, node.Asset), node);
        if (node.Type == "Part" && !string.IsNullOrWhiteSpace(node.Texture)) LoadImageAsset(visual, ResolveTexture(project, node.Texture), node);
        if (node.Type is "Decal" or "Texture" && visual.Material is not PhongMaterial { DiffuseMap: not null })
            visual.Visibility = System.Windows.Visibility.Collapsed;
        renderedAssetSources[node.Id] = GetAssetSource(node, project);
        return visual;
    }

    private static string GetAssetSource(SceneNode node, GameProject project) => node.Type switch
    {
        "ImageLabel" => ResolveAsset(project, node.Asset),
        "Decal" => $"{node.Face}|{ResolveAsset(project, node.Asset)}|{node.Transparency.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        "Texture" => $"{node.Face}|{ResolveAsset(project, node.Asset)}|{node.Transparency}|{node.StudsPerTileU}|{node.StudsPerTileV}|{node.OffsetStudsU}|{node.OffsetStudsV}|{string.Join(',', FindPartParent(project, node)?.Size ?? [])}",
        "MeshPart" => $"{ResolveAsset(project, node.Asset)}|{MaterialKey(node)}",
        "Part" => $"{node.Shape}|{ResolveTexture(project, node.Texture)}|{MaterialKey(node)}",
        "WedgePart" or "CornerWedgePart" or "SpawnLocation" or "TrussPart" or "Seat" => MaterialKey(node),
        _ => string.Empty
    };

    private static string MaterialKey(SceneNode node) => $"{node.Material}|{node.Transparency.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static float EffectiveTransparency(SceneNode node) => Math.Clamp(Math.Max(node.Transparency, node.Material == "Glass" ? .55f : 0), 0, 1);

    private bool IsRenderable(SceneNode node) => SceneNodeTypes.IsBasePart(node) || node.Type is "ImageLabel" or "Decal" or "Texture" or "Explosion" || showAttachmentGizmos && node.Type == "Attachment";

    private void UpdateAttachmentVisual(SceneNode attachment, GameProject project)
    {
        if (!visuals.TryGetValue(attachment.Id, out var visual)) return;
        visual.Transform = AttachmentTransformFor(attachment, project);
    }

    private static Transform3D AttachmentTransformFor(SceneNode attachment, GameProject project)
    {
        var parent = FindPartParent(project, attachment);
        if (parent is null) return Transform3D.Identity;
        var point = TransformFor(parent, false).Transform(new Point3D(attachment.Position[0], attachment.Position[1], attachment.Position[2]));
        return new TranslateTransform3D(point.X, point.Y, point.Z);
    }

    private void RefreshBeams(GameProject project)
    {
        var workspaceNodes = GameProjectStore.Flatten(project.Workspace.Children);
        var parts = workspaceNodes.Where(SceneNodeTypes.IsBasePart).ToArray();
        var attachments = workspaceNodes.Where(node => node.Type == "Attachment")
            .ToDictionary(node => node.Id, StringComparer.Ordinal);
        var beams = workspaceNodes.Where(node => node.Type == "Beam" && node.Enabled).ToArray();
        hasBeamNodes = beams.Length > 0;
        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var beam in beams)
        {
            if (!attachments.TryGetValue(beam.Attachment0, out var first)
                || !attachments.TryGetValue(beam.Attachment1, out var second)
                || ReferenceEquals(first, second)) continue;
            var firstPart = parts.FirstOrDefault(part => part.Children.Contains(first));
            var secondPart = parts.FirstOrDefault(part => part.Children.Contains(second));
            if (firstPart is null || secondPart is null) continue;
            var start = TransformFor(firstPart, false).Transform(new Point3D(first.Position[0], first.Position[1], first.Position[2]));
            var end = TransformFor(secondPart, false).Transform(new Point3D(second.Position[0], second.Position[1], second.Position[2]));
            if ((end - start).Length < .001 || beam.Width0 <= 0 && beam.Width1 <= 0) continue;
            activeIds.Add(beam.Id);
            var state = new BeamState(start, end, beam.Width0, beam.Width1, beam.Color, beam.Transparency);
            if (beamStates.TryGetValue(beam.Id, out var oldState) && oldState == state) continue;
            if (beamVisuals.Remove(beam.Id, out var previous)) viewport.Items.Remove(previous);
            var builder = new MeshBuilder();
            var startVector = ToVector(start);
            var endVector = ToVector(end);
            var axis = Vector3.Normalize(endVector - startVector);
            var radialX = Vector3.Normalize(Vector3.Cross(axis, Math.Abs(axis.Y) < .9f ? Vector3.UnitY : Vector3.UnitX));
            var radialY = Vector3.Cross(axis, radialX);
            const int sides = 12;
            for (var side = 0; side < sides; side++)
            {
                var angle0 = side * MathF.Tau / sides;
                var angle1 = (side + 1) * MathF.Tau / sides;
                var direction0 = radialX * MathF.Cos(angle0) + radialY * MathF.Sin(angle0);
                var direction1 = radialX * MathF.Cos(angle1) + radialY * MathF.Sin(angle1);
                builder.AddQuad(startVector + direction0 * beam.Width0 * .5f,
                    startVector + direction1 * beam.Width0 * .5f,
                    endVector + direction1 * beam.Width1 * .5f,
                    endVector + direction0 * beam.Width1 * .5f);
            }
            var visual = new OutLineMeshGeometryModel3D
            {
                Geometry = builder.ToMeshGeometry3D(), Material = MakeMaterial(beam),
                IsTransparent = beam.Transparency > 0, IsThrowingShadow = false,
                IsHitTestVisible = false, EnableOutline = beam.Id == selectedId,
                OutlineColor = Colors.Orange
            };
            beamVisuals[beam.Id] = visual;
            beamStates[beam.Id] = state;
            viewport.Items.Add(visual);
        }
        foreach (var id in beamVisuals.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            viewport.Items.Remove(beamVisuals[id]);
            beamVisuals.Remove(id);
            beamStates.Remove(id);
        }
    }

    private static SceneNode? FindPartParent(GameProject project, SceneNode child) =>
        GameProjectStore.Flatten(project.Workspace.Children).FirstOrDefault(parent => SceneNodeTypes.IsBasePart(parent) && parent.Children.Contains(child));

    private void AdvanceTrails(GameProject project, float elapsedSeconds)
    {
        var nodes = GameProjectStore.Flatten(project.Workspace.Children);
        var parts = nodes.Where(SceneNodeTypes.IsBasePart).ToArray();
        var attachments = nodes.Where(node => node.Type == "Attachment").ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var trail in nodes.Where(node => node.Type == "Trail"))
        {
            var now = trailTimes.GetValueOrDefault(trail.Id) + Math.Clamp(elapsedSeconds, 0, .25f);
            trailTimes[trail.Id] = now;
            if (!trailSamples.TryGetValue(trail.Id, out var samples)) trailSamples[trail.Id] = samples = [];
            if (trailClearVersions.GetValueOrDefault(trail.Id) != trail.TrailClearVersion)
            {
                samples.Clear();
                trailClearVersions[trail.Id] = trail.TrailClearVersion;
            }
            if (trailWasEnabled.TryGetValue(trail.Id, out var wasEnabled) && !wasEnabled && trail.Enabled) samples.Clear();
            trailWasEnabled[trail.Id] = trail.Enabled;
            samples.RemoveAll(sample => now - sample.Time >= trail.TrailLifetime);
            if (!trail.Enabled || !attachments.TryGetValue(trail.Attachment0, out var first)
                || !attachments.TryGetValue(trail.Attachment1, out var second) || ReferenceEquals(first, second)) continue;
            var firstPart = parts.FirstOrDefault(part => part.Children.Contains(first));
            var secondPart = parts.FirstOrDefault(part => part.Children.Contains(second));
            if (firstPart is null || secondPart is null) continue;
            var point0 = ToVector(TransformFor(firstPart, false).Transform(new Point3D(first.Position[0], first.Position[1], first.Position[2])));
            var point1 = ToVector(TransformFor(secondPart, false).Transform(new Point3D(second.Position[0], second.Position[1], second.Position[2])));
            var current = new TrailSample(now, point0, point1);
            if (samples.Count == 0 || Math.Max(Vector3.Distance(samples[^1].Point0, point0), Vector3.Distance(samples[^1].Point1, point1)) >= trail.TrailMinLength)
                samples.Add(current);
            if (trail.TrailMaxLength > 0)
                while (samples.Count > 1 && TrailPathLength(samples) > trail.TrailMaxLength) samples.RemoveAt(0);
        }
    }

    private static float TrailPathLength(IReadOnlyList<TrailSample> samples)
    {
        var length = 0f;
        for (var index = 1; index < samples.Count; index++)
            length += Vector3.Distance((samples[index - 1].Point0 + samples[index - 1].Point1) * .5f,
                (samples[index].Point0 + samples[index].Point1) * .5f);
        return length;
    }

    private void RefreshTrails(GameProject project)
    {
        var trails = GameProjectStore.Flatten(project.Workspace.Children).Where(node => node.Type == "Trail").ToArray();
        hasTrailNodes = trails.Length > 0;
        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trail in trails)
        {
            if (!trailSamples.TryGetValue(trail.Id, out var samples) || samples.Count < 2) continue;
            var geometry = TrailGeometryService.Build(trail, samples, trailTimes.GetValueOrDefault(trail.Id));
            if (geometry.Positions is null || geometry.Positions.Count == 0) continue;
            activeIds.Add(trail.Id);
            if (!trailVisuals.TryGetValue(trail.Id, out var visual))
            {
                visual = new MeshGeometryModel3D { IsThrowingShadow = false, IsHitTestVisible = false, IsTransparent = true };
                trailVisuals[trail.Id] = visual;
                viewport.Items.Add(visual);
            }
            visual.Geometry = geometry;
            var material = ParticleEffectMaterial("#FFFFFF", 1, false);
            material.VertexColorBlendingFactor = 1;
            if (GetParticleTexture(project, trail.Texture) is { } texture) material.DiffuseMap = texture;
            visual.Material = material;
        }
        foreach (var id in trailVisuals.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            viewport.Items.Remove(trailVisuals[id]);
            trailVisuals.Remove(id);
        }
        var currentIds = trails.Select(trail => trail.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in trailSamples.Keys.Where(id => !currentIds.Contains(id)).ToArray()) trailSamples.Remove(id);
        foreach (var id in trailTimes.Keys.Where(id => !currentIds.Contains(id)).ToArray()) trailTimes.Remove(id);
        foreach (var id in trailClearVersions.Keys.Where(id => !currentIds.Contains(id)).ToArray()) trailClearVersions.Remove(id);
        foreach (var id in trailWasEnabled.Keys.Where(id => !currentIds.Contains(id)).ToArray()) trailWasEnabled.Remove(id);
    }

    public void UpdateParticleEffects(float elapsedSeconds)
    {
        if (activeProject is null) return;
        if (hasTrailNodes)
        {
            AdvanceTrails(activeProject, elapsedSeconds);
            RefreshTrails(activeProject);
        }
        if (!hasParticleEffects) return;
        var advanced = false;
        foreach (var effect in GameProjectStore.Flatten(activeProject.Workspace.Children).Where(SceneNodeTypes.IsParticleEffect))
            if ((effect.Enabled || effect.Type == "ParticleEmitter") && effect.TimeScale > 0)
            {
                particleEffectTimes[effect.Id] = particleEffectTimes.GetValueOrDefault(effect.Id) + Math.Clamp(elapsedSeconds, 0, .25f) * effect.TimeScale;
                advanced = true;
            }
        if (advanced) RefreshParticleEffects(activeProject);
    }

    private void RefreshParticleEffects(GameProject project)
    {
        var workspaceNodes = GameProjectStore.Flatten(project.Workspace.Children);
        var parts = workspaceNodes.Where(SceneNodeTypes.IsBasePart).ToArray();
        var effects = workspaceNodes.Where(SceneNodeTypes.IsParticleEffect).ToArray();
        hasParticleEffects = effects.Length > 0;
        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var effect in effects)
        {
            if (effect.Type == "ParticleEmitter") UpdateParticleEmitterState(effect);
            else if (!effect.Enabled || effect.Type == "Smoke" && effect.Opacity <= 0) continue;
            var part = parts.FirstOrDefault(item => item.Children.Contains(effect));
            SceneNode? attachment = null;
            if (part is null)
                foreach (var candidate in parts)
                {
                    attachment = candidate.Children.FirstOrDefault(child => child.Type == "Attachment" && child.Children.Contains(effect));
                    if (attachment is null) continue;
                    part = candidate;
                    break;
                }
            if (part is null) continue;
            if (effect.Type == "ParticleEmitter" && !HasVisibleParticleEmitterParticles(effect)) continue;
            activeIds.Add(effect.Id);
            var transform = ParticleEffectTransform(part, attachment);
            var phase = particleEffectTimes.GetValueOrDefault(effect.Id);
            var texture = effect.Type == "ParticleEmitter" ? GetParticleTexture(project, effect.Texture) : null;
            if (!particleEffectVisuals.TryGetValue(effect.Id, out var visual))
            {
                var outer = new MeshGeometryModel3D { IsThrowingShadow = false, IsHitTestVisible = false, IsTransparent = true };
                MeshGeometryModel3D? inner = effect.Type == "Fire"
                    ? new MeshGeometryModel3D { IsThrowingShadow = false, IsHitTestVisible = false, IsTransparent = true }
                    : null;
                visual = (outer, inner);
                particleEffectVisuals[effect.Id] = visual;
                viewport.Items.Add(outer);
                if (inner is not null) viewport.Items.Add(inner);
            }
            visual.Outer.Geometry = effect.Type == "ParticleEmitter"
                ? BuildParticleEmitterGeometry(effect, phase, attachment is null ? part.Size : [0, 0, 0], particleBursts.GetValueOrDefault(effect.Id), particleClearTimes.GetValueOrDefault(effect.Id, float.NegativeInfinity), particleDisabledTimes.GetValueOrDefault(effect.Id, float.PositiveInfinity), texture is not null ? CameraInParticleSpace(transform) : null)
                : BuildParticleEffectGeometry(effect, phase, false, attachment is null ? part.Size : [1, 1, 1]);
            var material = ParticleEffectMaterial(effect.Type == "ParticleEmitter" ? "#FFFFFF" : effect.Color,
                effect.Type == "ParticleEmitter" ? 1 : effect.Type == "Fire" ? .52f : effect.Type == "Sparkles" ? .9f : effect.Opacity * .38f,
                effect.Type is "Fire" or "Sparkles");
            if (effect.Type == "ParticleEmitter") material.VertexColorBlendingFactor = 1;
            if (texture is not null) material.DiffuseMap = texture;
            visual.Outer.Material = material;
            visual.Outer.Transform = transform;
            if (visual.Inner is not null)
            {
                visual.Inner.Geometry = BuildParticleEffectGeometry(effect, phase, true, part.Size);
                visual.Inner.Material = ParticleEffectMaterial(effect.SecondaryColor, .72f, true);
                visual.Inner.Transform = transform;
            }
        }
        foreach (var id in particleEffectVisuals.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            var visual = particleEffectVisuals[id];
            viewport.Items.Remove(visual.Outer);
            if (visual.Inner is not null) viewport.Items.Remove(visual.Inner);
            particleEffectVisuals.Remove(id);
        }
        var workspaceIds = effects.Select(effect => effect.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in particleEffectTimes.Keys.Where(id => !workspaceIds.Contains(id)).ToArray()) particleEffectTimes.Remove(id);
        foreach (var id in particleBursts.Keys.Where(id => !workspaceIds.Contains(id)).ToArray()) particleBursts.Remove(id);
        foreach (var id in particleClearVersions.Keys.Where(id => !workspaceIds.Contains(id)).ToArray()) particleClearVersions.Remove(id);
        foreach (var id in particleClearTimes.Keys.Where(id => !workspaceIds.Contains(id)).ToArray()) particleClearTimes.Remove(id);
        foreach (var id in particleDisabledTimes.Keys.Where(id => !workspaceIds.Contains(id)).ToArray()) particleDisabledTimes.Remove(id);
        foreach (var id in particleWasEnabled.Keys.Where(id => !workspaceIds.Contains(id)).ToArray()) particleWasEnabled.Remove(id);
    }

    private static Transform3D ParticleEffectTransform(SceneNode part, SceneNode? attachment)
    {
        if (attachment is null) return TransformFor(part, false);
        var transform = new Transform3DGroup();
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), attachment.Rotation[2])));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), attachment.Rotation[0])));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), attachment.Rotation[1])));
        transform.Children.Add(new TranslateTransform3D(attachment.Position[0], attachment.Position[1], attachment.Position[2]));
        transform.Children.Add(TransformFor(part, false));
        return transform;
    }

    private (Vector3 Position, Vector3 Up)? CameraInParticleSpace(Transform3D transform)
    {
        if (viewport.Camera is not HelixToolkit.Wpf.SharpDX.PerspectiveCamera camera || transform.Inverse is not { } inverse) return null;
        var position = inverse.Transform(camera.Position);
        var upPoint = inverse.Transform(camera.Position + camera.UpDirection);
        return (ToVector(position), Vector3.Normalize(ToVector(upPoint) - ToVector(position)));
    }

    private TextureModel? GetParticleTexture(GameProject project, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var path = ResolveTexture(project, relative);
        if (path.Length == 0 || !File.Exists(path)) return null;
        try
        {
            var modified = File.GetLastWriteTimeUtc(path);
            if (particleTextures.TryGetValue(path, out var cached) && cached.Modified == modified) return cached.Texture;
            ValidateImageFile(path);
            var texture = TextureModel.Create(path);
            particleTextures[path] = (modified, texture);
            return texture;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateParticleEmitterState(SceneNode effect)
    {
        var phase = particleEffectTimes.GetValueOrDefault(effect.Id);
        if (particleClearVersions.GetValueOrDefault(effect.Id) != effect.ParticleClearVersion)
        {
            particleBursts.Remove(effect.Id);
            particleClearTimes[effect.Id] = phase;
        }
        particleClearVersions[effect.Id] = effect.ParticleClearVersion;
        if (!particleWasEnabled.TryGetValue(effect.Id, out var wasEnabled))
        {
            if (!effect.Enabled) particleDisabledTimes[effect.Id] = phase - effect.LifetimeRange[1];
        }
        else if (wasEnabled != effect.Enabled)
        {
            if (effect.Enabled) particleDisabledTimes.Remove(effect.Id);
            else particleDisabledTimes[effect.Id] = phase;
        }
        particleWasEnabled[effect.Id] = effect.Enabled;
        if (effect.PendingParticleEmits > 0)
        {
            if (!particleBursts.TryGetValue(effect.Id, out var bursts)) particleBursts[effect.Id] = bursts = [];
            bursts.Add(new ParticleBurst(phase, effect.PendingParticleEmits));
            effect.PendingParticleEmits = 0;
        }
        if (particleBursts.TryGetValue(effect.Id, out var active))
            active.RemoveAll(burst => phase - burst.StartedAt >= effect.LifetimeRange[1]);
    }

    private bool HasVisibleParticleEmitterParticles(SceneNode effect)
    {
        if (effect.TransparencySequence.Count > 0
            ? effect.TransparencySequence.All(point => point.Value - point.Envelope >= 1)
            : effect.Transparency >= 1) return false;
        if (effect.SizeSequence.Count > 0
            ? effect.SizeSequence.All(point => point.Value + point.Envelope <= 0)
            : effect.EffectSize <= 0) return false;
        if (particleBursts.TryGetValue(effect.Id, out var bursts) && bursts.Count > 0) return true;
        if (effect.Rate <= 0) return false;
        if (effect.Enabled) return true;
        return particleDisabledTimes.TryGetValue(effect.Id, out var stoppedAt)
            && particleEffectTimes.GetValueOrDefault(effect.Id) - stoppedAt < effect.LifetimeRange[1];
    }

    private static HelixToolkit.SharpDX.Core.MeshGeometry3D BuildParticleEmitterGeometry(
        SceneNode effect, float phase, float[] parentSize, List<ParticleBurst>? bursts, float clearedAt, float disabledAt,
        (Vector3 Position, Vector3 Up)? camera)
    {
        var builder = new MeshBuilder();
        var colors = new Color4Collection();
        var emitted = 0;
        if (effect.Rate > 0)
        {
            var count = Math.Min(200, (int)MathF.Ceiling(effect.Rate * effect.LifetimeRange[1]));
            for (var index = 0; index < count; index++)
            {
                var lifetime = ParticleRange(effect.LifetimeRange, ParticleHash(index, 19));
                var age = (phase + (index + .5f) / effect.Rate) % lifetime;
                var bornAt = phase - age;
                if (bornAt < clearedAt || bornAt > disabledAt) continue;
                AddEmitterParticle(builder, colors, effect, parentSize, index, age, lifetime, camera);
                emitted++;
            }
        }
        if (bursts is not null)
            foreach (var burst in bursts)
            {
                var age = phase - burst.StartedAt;
                for (var index = 0; index < burst.Count && emitted < 300; index++)
                {
                    var lifetime = ParticleRange(effect.LifetimeRange, ParticleHash(index + burst.Count, 29));
                    if (age >= lifetime) continue;
                    AddEmitterParticle(builder, colors, effect, parentSize, index + 1000, age, lifetime, camera);
                    emitted++;
                }
            }
        var geometry = builder.ToMeshGeometry3D();
        geometry.Colors = colors;
        return geometry;
    }

    private static void AddEmitterParticle(MeshBuilder builder, Color4Collection colors, SceneNode effect, float[] parentSize, int index, float age, float lifetime,
        (Vector3 Position, Vector3 Up)? camera)
    {
        var spreadX = (ParticleHash(index, 31) - .5f) * effect.SpreadAngle[0] * MathF.PI / 180;
        var spreadY = (ParticleHash(index, 37) - .5f) * effect.SpreadAngle[1] * MathF.PI / 180;
        var local = Vector3.Normalize(new Vector3(MathF.Sin(spreadX), MathF.Cos(spreadX) * MathF.Cos(spreadY), MathF.Sin(spreadY)));
        var direction = effect.EmissionDirection switch
        {
            "Bottom" => -local,
            "Right" => new Vector3(local.Y, local.X, local.Z),
            "Left" => new Vector3(-local.Y, local.X, local.Z),
            "Front" => new Vector3(local.X, local.Z, -local.Y),
            "Back" => new Vector3(local.X, local.Z, local.Y),
            _ => local
        };
        var origin = new Vector3((ParticleHash(index, 41) - .5f) * parentSize[0],
            (ParticleHash(index, 43) - .5f) * parentSize[1],
            (ParticleHash(index, 47) - .5f) * parentSize[2]);
        var speed = ParticleRange(effect.SpeedRange, ParticleHash(index, 53));
        var position = origin + direction * speed * age;
        var progress = age / lifetime;
        var size = ParticleSequenceService.SampleNumber(effect.SizeSequence, effect.EffectSize, progress, ParticleHash(index, 59));
        var transparency = ParticleSequenceService.SampleNumber(effect.TransparencySequence, effect.Transparency, progress, ParticleHash(index, 61));
        if (size <= 0 || transparency >= 1) return;
        var color = ParticleSequenceService.SampleColor(effect.ColorSequence, effect.Color, progress);
        var tint = new Color4(color.R / 255f, color.G / 255f, color.B / 255f, 1 - Math.Clamp(transparency, 0, 1));
        var radius = MathF.Max(.025f, size * .35f);
        var first = builder.Positions.Count;
        if (camera is not { } view)
        {
            builder.AddSphere(position, radius, 8, 6);
        }
        else
        {
            var towardCamera = view.Position - position;
            if (towardCamera.LengthSquared() < .000001f) towardCamera = Vector3.UnitZ;
            towardCamera = Vector3.Normalize(towardCamera);
            var right = Vector3.Cross(view.Up, towardCamera);
            if (right.LengthSquared() < .000001f) right = Vector3.Cross(Vector3.UnitX, towardCamera);
            right = Vector3.Normalize(right) * radius;
            var up = Vector3.Normalize(Vector3.Cross(towardCamera, right)) * radius;
            builder.AddQuad(position - right - up, position + right - up, position + right + up, position - right + up);
        }
        for (var vertex = first; vertex < builder.Positions.Count; vertex++) colors.Add(tint);
    }

    private static float ParticleRange(float[] range, float fraction) => range[0] + (range[1] - range[0]) * fraction;

    private static float ParticleHash(int index, int seed)
    {
        var value = MathF.Sin(index * 12.9898f + seed * 78.233f) * 43758.5453f;
        return value - MathF.Floor(value);
    }

    private static HelixToolkit.SharpDX.Core.MeshGeometry3D BuildParticleEffectGeometry(SceneNode effect, float time, bool inner, float[] parentSize)
    {
        var builder = new MeshBuilder();
        if (effect.Type == "Sparkles")
        {
            for (var index = 0; index < 20; index++)
            {
                var age = (time * .7f + (float)index / 20) % 1;
                var twinkle = .25f + .75f * MathF.Abs(MathF.Sin(age * MathF.PI));
                var x = MathF.Sin(index * 12.9898f) * parentSize[0] * .48f;
                var y = MathF.Sin(index * 78.233f) * parentSize[1] * .48f + age * .25f;
                var z = MathF.Sin(index * 43.758f) * parentSize[2] * .48f;
                builder.AddSphere(new Vector3(x, y, z), .025f + .08f * twinkle, 8, 6);
            }
            return builder.ToMeshGeometry3D();
        }
        var count = effect.Type == "Fire" ? inner ? 7 : 10 : 8;
        var size = effect.EffectSize;
        var rise = effect.Type == "Fire" ? effect.Heat : effect.RiseVelocity;
        for (var index = 0; index < count; index++)
        {
            var age = (time * (effect.Type == "Fire" ? 1.3f : .55f) + (float)index / count) % 1;
            var angle = index * 2.39996f + time * .35f;
            var spread = size * (inner ? .07f : effect.Type == "Fire" ? .16f : .3f) * (effect.Type == "Fire" ? 1 - age * .55f : 1 + age * .5f);
            var height = MathF.Sign(rise) * age * MathF.Min(MathF.Abs(rise) * .22f + size * .3f, size * 2.5f);
            var radius = size * (effect.Type == "Fire" ? inner ? .095f : .15f : .14f + age * .09f);
            radius *= effect.Type == "Fire" ? 1 - age * .55f : 1;
            builder.AddSphere(new Vector3(MathF.Cos(angle) * spread, height, MathF.Sin(angle) * spread), Math.Max(.02f, radius), 8, 6);
        }
        return builder.ToMeshGeometry3D();
    }

    private static PhongMaterial ParticleEffectMaterial(string color, float opacity, bool emissive)
    {
        System.Windows.Media.Color parsed;
        try { parsed = (System.Windows.Media.Color)ColorConverter.ConvertFromString(color); }
        catch { parsed = Colors.White; }
        var red = parsed.R / 255f;
        var green = parsed.G / 255f;
        var blue = parsed.B / 255f;
        return new PhongMaterial
        {
            DiffuseColor = new Color4(red, green, blue, Math.Clamp(opacity, 0, 1)),
            EmissiveColor = emissive ? new Color4(red * .45f, green * .45f, blue * .45f, 1) : new Color4(0, 0, 0, 1),
            SpecularColor = new Color4(0, 0, 0, 1)
        };
    }

    private static void ApplyTextureTiling(HelixToolkit.SharpDX.Core.MeshGeometry3D geometry, SceneNode texture, SceneNode parent)
    {
        if (geometry.TextureCoordinates is null) return;
        var width = texture.Face is "Left" or "Right" ? Math.Abs(parent.Size[2]) : Math.Abs(parent.Size[0]);
        var height = texture.Face is "Top" or "Bottom" ? Math.Abs(parent.Size[2]) : Math.Abs(parent.Size[1]);
        var uScale = width / texture.StudsPerTileU;
        var vScale = height / texture.StudsPerTileV;
        var uOffset = texture.OffsetStudsU / texture.StudsPerTileU;
        var vOffset = texture.OffsetStudsV / texture.StudsPerTileV;
        for (var index = 0; index < geometry.TextureCoordinates.Count; index++)
        {
            var uv = geometry.TextureCoordinates[index];
            geometry.TextureCoordinates[index] = new Vector2(uv.X * uScale + uOffset, uv.Y * vScale + vOffset);
        }
    }

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

    private static void AddSeat(MeshBuilder builder)
    {
        builder.AddBox(new Vector3(0, .25f, 0), 1, .5, 1);
        builder.AddBox(new Vector3(0, .08f, .42f), 1, .84, .16);
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

    private void RefreshHighlights(GameProject project)
    {
        foreach (var fill in highlightFills.Values) viewport.Items.Remove(fill);
        highlightFills.Clear();
        highlightSources.Clear();

        void Visit(SceneNode node, SceneNode? inheritedHighlight)
        {
            var highlight = node.Children.FirstOrDefault(child => child.Type == "Highlight" && child.Enabled)
                ?? inheritedHighlight;
            if (SceneNodeTypes.IsBasePart(node) && highlight is not null && visuals.TryGetValue(node.Id, out var target))
            {
                highlightSources[node.Id] = highlight;
                var color = ParseHighlightColor(highlight.FillColor, highlight.FillTransparency, Colors.DeepSkyBlue);
                var fill = new MeshGeometryModel3D
                {
                    Geometry = target.Geometry,
                    Material = new PhongMaterial { DiffuseColor = new Color4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f) },
                    Transform = HighlightTransformFor(node),
                    IsTransparent = true,
                    IsHitTestVisible = false,
                    IsThrowingShadow = false,
                    Visibility = color.A == 0 ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible
                };
                highlightFills[node.Id] = fill;
                viewport.Items.Add(fill);
            }
            foreach (var child in node.Children.Where(child => child.Type != "Highlight")) Visit(child, highlight);
        }

        Visit(project.Workspace, null);
    }

    private void RefreshForceFields(GameProject project)
    {
        foreach (var shield in forceFieldVisuals.Values) viewport.Items.Remove(shield);
        forceFieldVisuals.Clear();
        hasVisibleForceFields = false;

        void Visit(SceneNode parent)
        {
            foreach (var forceField in parent.Children.Where(child => child.Type == "ForceField" && child.Visible))
            {
                var protectedParts = parent.Type == "Model"
                    ? GameProjectStore.Flatten(parent.Children).Where(SceneNodeTypes.IsBasePart).ToArray()
                    : SceneNodeTypes.IsBasePart(parent) ? [parent] : [];
                if (protectedParts.Length == 0) continue;
                var center = parent.Type == "Model" ? SceneModelService.GetPivot(parent) : parent.Position;
                var centerVector = new Vector3(center[0], center[1], center[2]);
                var radius = protectedParts.Max(part => Vector3.Distance(centerVector, new Vector3(part.Position[0], part.Position[1], part.Position[2]))
                    + .5f * new Vector3(part.Size[0], part.Size[1], part.Size[2]).Length()) + .35f;
                var builder = new MeshBuilder();
                builder.AddSphere(Vector3.Zero, radius, 24, 16);
                var shield = new MeshGeometryModel3D
                {
                    Geometry = builder.ToMeshGeometry3D(),
                    Material = new PhongMaterial
                    {
                        DiffuseColor = new Color4(.28f, .62f, 1f, .16f),
                        EmissiveColor = new Color4(.04f, .12f, .24f, 1),
                        SpecularColor = new Color4(.8f, .9f, 1f, 1)
                    },
                    Transform = new TranslateTransform3D(center[0], center[1], center[2]),
                    IsTransparent = true,
                    IsHitTestVisible = false,
                    IsThrowingShadow = false
                };
                forceFieldVisuals[forceField.Id] = shield;
                viewport.Items.Add(shield);
                hasVisibleForceFields = true;
            }
            foreach (var child in parent.Children.Where(child => child.Type != "ForceField")) Visit(child);
        }
        Visit(project.Workspace);
    }

    private static System.Windows.Media.Color ParseHighlightColor(string value, float transparency, System.Windows.Media.Color fallback)
    {
        System.Windows.Media.Color color;
        try { color = (System.Windows.Media.Color)ColorConverter.ConvertFromString(value); }
        catch { color = fallback; }
        return System.Windows.Media.Color.FromArgb((byte)Math.Round(color.A * (1 - Math.Clamp(transparency, 0, 1))), color.R, color.G, color.B);
    }

    private static Transform3D HighlightTransformFor(SceneNode node)
    {
        var transform = new Transform3DGroup();
        transform.Children.Add(new ScaleTransform3D(1.005, 1.005, 1.005));
        transform.Children.Add(TransformFor(node));
        return transform;
    }

    private void RefreshSkybox(SceneNode workspace, GameProject project)
    {
        foreach (var visual in skyboxVisuals) viewport.Items.Remove(visual.Model);
        skyboxVisuals.Clear();
        var sky = project.Lighting.Children.FirstOrDefault(node => node.Type == "Sky");
        foreach (var face in SkyboxFaces)
        {
            static string FacePath(SceneNode node, string property) => property switch
            {
                "SkyboxFront" => node.SkyboxFront,
                "SkyboxBack" => node.SkyboxBack,
                "SkyboxLeft" => node.SkyboxLeft,
                "SkyboxRight" => node.SkyboxRight,
                "SkyboxTop" => node.SkyboxTop,
                "SkyboxBottom" => node.SkyboxBottom,
                _ => string.Empty
            };
            var relativePath = sky is null ? string.Empty : FacePath(sky, face.Property);
            if (string.IsNullOrWhiteSpace(relativePath)) relativePath = FacePath(workspace, face.Property);
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

    private void RefreshLighting(SceneNode lighting)
    {
        try { ambientLight.Color = (System.Windows.Media.Color)ColorConverter.ConvertFromString(lighting.Ambient); }
        catch { ambientLight.Color = Colors.Gray; }
        var phase = (lighting.ClockTime - 6) * Math.PI / 12;
        var elevation = Math.Sin(phase);
        sunLight.Direction = new Vector3D(-Math.Cos(phase), -Math.Max(.08, elevation), -.45);
        var intensity = Math.Clamp(lighting.Brightness, 0, 8) * (elevation > 0 ? 1 : .12);
        sunLight.Color = System.Windows.Media.Color.FromScRgb(1, (float)intensity, (float)intensity, (float)intensity);
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
        if (node.Type == "Explosion")
        {
            var opacity = node.ExplosionAge < 0 ? .12f : Math.Clamp((.5f - node.ExplosionAge) * 1.5f, 0, .75f);
            return new PhongMaterial
            {
                DiffuseColor = new Color4(1, .55f, .12f, opacity),
                EmissiveColor = new Color4(.45f, .16f, .02f, 1),
                SpecularColor = new Color4(1, .85f, .55f, 1)
            };
        }
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
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), node.Rotation[0])));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), node.Rotation[1])));
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
