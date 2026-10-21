using BullerStudio.Models;
using System.Numerics;

namespace BullerStudio.Services;

public static class SceneModelService
{
    public static float[] GetPivot(SceneNode model)
    {
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
}
