using BullerStudio.Models;

namespace BullerStudio.Services;

public static class ClickDetectorService
{
    public static SceneNode? FindActive(SceneNode part, SceneNode playerRoot)
    {
        if (!SceneNodeTypes.IsBasePart(part)) return null;
        var distance = ProximityPromptService.DistanceToPartSurface(part, playerRoot);
        return part.Children.FirstOrDefault(node => node.Type == "ClickDetector" && node.Enabled
            && distance <= Math.Clamp(node.MaxActivationDistance, 1, 100));
    }
}
