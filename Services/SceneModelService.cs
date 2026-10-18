using BullerStudio.Models;

namespace BullerStudio.Services;

public static class SceneModelService
{
    public static float[] GetPivot(SceneNode model)
    {
        var parts = GameProjectStore.Flatten(model.Children)
            .Where(node => SceneNodeTypes.IsBasePart(node) || node.Type == "ImageLabel")
            .ToArray();
        if (parts.Length == 0) return (float[])model.Position.Clone();

        var lower = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        var upper = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        foreach (var part in parts)
            for (var axis = 0; axis < 3; axis++)
            {
                var halfSize = Math.Abs(part.Size[axis]) * .5f;
                lower[axis] = Math.Min(lower[axis], part.Position[axis] - halfSize);
                upper[axis] = Math.Max(upper[axis], part.Position[axis] + halfSize);
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
        for (var axis = 0; axis < 3; axis++) model.Position[axis] += delta[axis];
        return true;
    }
}
