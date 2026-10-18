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
            var distanceSquared = 0f;
            for (var axis = 0; axis < 3; axis++)
            {
                var outside = Math.Max(0, Math.Abs(playerRoot.Position[axis] - parent.Position[axis]) - Math.Abs(parent.Size[axis]) * .5f);
                distanceSquared += outside * outside;
            }
            var range = Math.Clamp(prompt.MaxActivationDistance, 1, 100);
            if (distanceSquared > range * range) continue;
            var distance = MathF.Sqrt(distanceSquared);
            if (closest is null || distance < closest.Distance)
                closest = new ProximityPromptTarget(prompt, parent, distance);
        }
        return closest;
    }
}
