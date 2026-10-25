using BullerStudio.Models;
using System.Numerics;

namespace BullerStudio.Services;

public static class SceneModelService
{
    public static bool ContainsPrimaryPart(SceneNode model, string partId) => model.Type == "Model"
        && GameProjectStore.Flatten(model.Children).Any(node => node.Id == partId && SceneNodeTypes.IsBasePart(node));

    public static bool SetPrimaryPart(SceneNode model, SceneNode? part)
    {
        if (model.Type != "Model" || part is not null && (!SceneNodeTypes.IsBasePart(part) || !ContainsPrimaryPart(model, part.Id))) return false;
        model.PrimaryPart = part?.Id ?? string.Empty;
        model.HasModelPivot = part is not null;
        if (part is not null)
        {
            model.Position = (float[])part.Position.Clone();
            model.Rotation = (float[])part.Rotation.Clone();
        }
        return true;
    }

    public static float[] GetPivot(SceneNode model)
    {
        if (model.Type == "Model" && !string.IsNullOrEmpty(model.PrimaryPart)
            && GameProjectStore.Flatten(model.Children).FirstOrDefault(node => node.Id == model.PrimaryPart && SceneNodeTypes.IsBasePart(node)) is { } primaryPart)
            return (float[])primaryPart.Position.Clone();
        if (model.HasModelPivot) return (float[])model.Position.Clone();
        var parts = GameProjectStore.Flatten(model.Children)
            .Where(node => SceneNodeTypes.IsBasePart(node) || node.Type == "ImageLabel")
            .ToArray();
        if (parts.Length == 0) return (float[])model.Position.Clone();

        var lower = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        var upper = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        foreach (var part in parts)
        {
            var rotation = RotationMath.FromDegrees(part.Rotation);
            var localAxes = new[]
            {
                Vector3.TransformNormal(Vector3.UnitX, rotation),
                Vector3.TransformNormal(Vector3.UnitY, rotation),
                Vector3.TransformNormal(Vector3.UnitZ, rotation)
            };
            for (var axis = 0; axis < 3; axis++)
            {
                var halfExtent = Enumerable.Range(0, 3).Sum(localAxis => Math.Abs(axis switch
                {
                    0 => localAxes[localAxis].X,
                    1 => localAxes[localAxis].Y,
                    _ => localAxes[localAxis].Z
                }) * Math.Abs(part.Size[localAxis]) * .5f);
                lower[axis] = Math.Min(lower[axis], part.Position[axis] - halfExtent);
                upper[axis] = Math.Max(upper[axis], part.Position[axis] + halfExtent);
            }
        }
        return [
            (lower[0] + upper[0]) * .5f,
            (lower[1] + upper[1]) * .5f,
            (lower[2] + upper[2]) * .5f
        ];
    }

    public static float[] GetExtentsSize(SceneNode model)
    {
        var parts = GameProjectStore.Flatten(model.Children)
            .Where(node => SceneNodeTypes.IsBasePart(node) || node.Type == "ImageLabel")
            .ToArray();
        if (parts.Length == 0) return [0, 0, 0];
        var lower = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        var upper = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        foreach (var part in parts)
        {
            var rotation = RotationMath.FromDegrees(part.Rotation);
            var axes = new[]
            {
                Vector3.TransformNormal(Vector3.UnitX, rotation),
                Vector3.TransformNormal(Vector3.UnitY, rotation),
                Vector3.TransformNormal(Vector3.UnitZ, rotation)
            };
            for (var axis = 0; axis < 3; axis++)
            {
                var extent = Enumerable.Range(0, 3).Sum(localAxis => Math.Abs(axis switch
                {
                    0 => axes[localAxis].X,
                    1 => axes[localAxis].Y,
                    _ => axes[localAxis].Z
                }) * Math.Abs(part.Size[localAxis]) * .5f);
                lower[axis] = Math.Min(lower[axis], part.Position[axis] - extent);
                upper[axis] = Math.Max(upper[axis], part.Position[axis] + extent);
            }
        }
        return [upper[0] - lower[0], upper[1] - lower[1], upper[2] - lower[2]];
    }

    public static bool MoveTo(SceneNode model, float[] position)
    {
        if (model.Type != "Model" || position.Length != 3 || position.Any(value => !float.IsFinite(value))) return false;
        var current = GetPivot(model);
        var delta = new[] { position[0] - current[0], position[1] - current[1], position[2] - current[2] };
        if (delta.All(value => Math.Abs(value) < .000001f)) return false;
        foreach (var node in GameProjectStore.Flatten(model.Children))
        {
            if (node.Type == "Model" || SceneNodeTypes.IsBasePart(node) || node.Type == "ImageLabel")
                for (var axis = 0; axis < 3; axis++) node.Position[axis] += delta[axis];
        }
        model.Position = (float[])position.Clone();
        model.HasModelPivot = true;
        return true;
    }

    public static bool RotateTo(SceneNode model, float[] rotation)
    {
        if (model.Type != "Model" || rotation.Length != 3 || rotation.Any(value => !float.IsFinite(value))) return false;
        if (model.Rotation.SequenceEqual(rotation)) return false;
        var pivot = GetPivot(model);
        var oldMatrix = RotationMath.FromDegrees(model.Rotation);
        var delta = Matrix4x4.Transpose(oldMatrix) * RotationMath.FromDegrees(rotation);
        var pivotVector = new Vector3(pivot[0], pivot[1], pivot[2]);
        foreach (var node in GameProjectStore.Flatten(model.Children))
        {
            if (node.Type != "Model" && !SceneNodeTypes.IsBasePart(node) && node.Type != "ImageLabel") continue;
            var current = new Vector3(node.Position[0], node.Position[1], node.Position[2]);
            var next = pivotVector + Vector3.TransformNormal(current - pivotVector, delta);
            node.Position = [next.X, next.Y, next.Z];
            node.Rotation = RotationMath.ToDegrees(RotationMath.FromDegrees(node.Rotation) * delta);
        }
        model.Position = pivot;
        model.HasModelPivot = true;
        model.Rotation = (float[])rotation.Clone();
        return true;
    }

    public static bool ScaleTo(SceneNode model, float scale)
    {
        if (model.Type != "Model" || !float.IsFinite(scale)) return false;
        scale = Math.Clamp(scale, .01f, 1000);
        var currentScale = float.IsFinite(model.ModelScale) && model.ModelScale > 0 ? model.ModelScale : 1;
        if (Math.Abs(scale - currentScale) < .000001f) return false;
        var factor = scale / currentScale;
        var pivot = new Vector3(GetPivot(model)[0], GetPivot(model)[1], GetPivot(model)[2]);
        foreach (var node in GameProjectStore.Flatten(model.Children))
        {
            if (node.Type == "Model")
            {
                node.Position = ScalePosition(node.Position, pivot, factor);
                node.ModelScale = Math.Clamp((float.IsFinite(node.ModelScale) && node.ModelScale > 0 ? node.ModelScale : 1) * factor, .01f, 1000);
            }
            else if (SceneNodeTypes.IsBasePart(node) || node.Type == "ImageLabel")
            {
                node.Position = ScalePosition(node.Position, pivot, factor);
                node.Size = node.Size.Select(value => Math.Clamp(value * factor, .05f, 100000)).ToArray();
            }
            else if (node.Type == "Attachment")
                node.Position = ScaleVector(node.Position, factor);
            else if (node.Type is "Weld" or "Motor6D")
            {
                node.Part0OffsetPosition = ScaleVector(node.Part0OffsetPosition, factor);
                node.Part1OffsetPosition = ScaleVector(node.Part1OffsetPosition, factor);
                if (node.Type == "Motor6D") node.MotorTransformPosition = ScaleVector(node.MotorTransformPosition, factor);
            }
        }
        model.Position = [pivot.X, pivot.Y, pivot.Z];
        model.HasModelPivot = true;
        model.ModelScale = scale;
        return true;
    }

    private static float[] ScalePosition(float[] position, Vector3 pivot, float factor) =>
    [
        pivot.X + (position[0] - pivot.X) * factor,
        pivot.Y + (position[1] - pivot.Y) * factor,
        pivot.Z + (position[2] - pivot.Z) * factor
    ];

    private static float[] ScaleVector(float[] vector, float factor) =>
        vector.Select(value => Math.Clamp(value * factor, -100000, 100000)).ToArray();
}
