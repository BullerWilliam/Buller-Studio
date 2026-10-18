using BullerStudio.Models;

namespace BullerStudio.Services;

public sealed record ProximityPromptTarget(SceneNode Prompt, SceneNode Parent, float Distance);

public static class ProximityPromptService
{
    public static ProximityPromptTarget? FindNearest(GameProject project, SceneNode playerRoot)
    {
        ProximityPromptTarget? closest = null;
        foreach (var parent in GameProjectStore.Flatten(project.Workspace.Children).Where(SceneNodeTypes.IsBasePart))
        foreach (var prompt in parent.Children.Where(node => node.Type == "ProximityPrompt" && node.Enabled))
        {
            var distance = DistanceToPartSurface(parent, playerRoot);
            var range = Math.Clamp(prompt.MaxActivationDistance, 1, 100);
            if (distance > range) continue;
            if (closest is null || distance < closest.Distance)
                closest = new ProximityPromptTarget(prompt, parent, distance);
        }
        return closest;
    }

    public static float DistanceToPartSurface(SceneNode part, SceneNode playerRoot)
    {
        var x = playerRoot.Position[0] - part.Position[0];
        var y = playerRoot.Position[1] - part.Position[1];
        var z = playerRoot.Position[2] - part.Position[2];
        var pitch = -part.Rotation[0] * MathF.PI / 180;
        var yaw = -part.Rotation[1] * MathF.PI / 180;
        var roll = -part.Rotation[2] * MathF.PI / 180;
        (y, z) = (y * MathF.Cos(pitch) - z * MathF.Sin(pitch), y * MathF.Sin(pitch) + z * MathF.Cos(pitch));
        (x, z) = (x * MathF.Cos(yaw) + z * MathF.Sin(yaw), -x * MathF.Sin(yaw) + z * MathF.Cos(yaw));
        (x, y) = (x * MathF.Cos(roll) - y * MathF.Sin(roll), x * MathF.Sin(roll) + y * MathF.Cos(roll));
        Span<float> local = stackalloc float[3] { x, y, z };
        var distanceSquared = 0f;
        for (var axis = 0; axis < 3; axis++)
        {
            var outside = Math.Max(0, Math.Abs(local[axis]) - Math.Abs(part.Size[axis]) * .5f);
            distanceSquared += outside * outside;
        }
        return MathF.Sqrt(distanceSquared);
    }
}
