using System.Numerics;
using BullerStudio.Models;

namespace BullerStudio.Services;

public sealed record ProximityPromptTarget(SceneNode Prompt, SceneNode Parent, float Distance, bool IsIndicatorOnly);

public static class ProximityPromptService
{
    public static ProximityPromptTarget? FindNearest(GameProject project, SceneNode playerRoot, bool enabled = true)
    {
        if (!enabled) return null;
        ProximityPromptTarget? closest = null;
        var workspaceNodes = GameProjectStore.Flatten(project.Workspace.Children).ToArray();
        var playerPosition = ToVector(playerRoot.Position);
        var ignoredIds = new HashSet<string>(StringComparer.Ordinal) { playerRoot.Id };
        if (FindModelContaining(project.Workspace, playerRoot.Id) is { } character)
            foreach (var part in GameProjectStore.Flatten(character.Children).Where(SceneNodeTypes.IsBasePart)) ignoredIds.Add(part.Id);
        var occluders = workspaceNodes.Where(node => SceneNodeTypes.IsBasePart(node) && node.CanQuery && !ignoredIds.Contains(node.Id)).ToArray();
        foreach (var parent in workspaceNodes.Where(SceneNodeTypes.IsBasePart))
        foreach (var (prompt, attachmentPosition) in GetPrompts(parent))
        {
            var distance = attachmentPosition is { } point
                ? Vector3.Distance(playerPosition, point)
                : DistanceToPartSurface(parent, playerRoot);
            var activationRange = Math.Clamp(prompt.MaxActivationDistance, 1, 100);
            var indicatorRange = Math.Clamp(prompt.MaxIndicatorDistance, 0, 1000);
            var range = Math.Max(activationRange, indicatorRange);
            if (distance > range) continue;
            if (prompt.RequiresLineOfSight && !HasLineOfSight(parent, playerRoot, occluders, attachmentPosition)) continue;
            var indicatorOnly = distance > activationRange;
            if (closest is null
                || closest.IsIndicatorOnly && !indicatorOnly
                || closest.IsIndicatorOnly == indicatorOnly && distance < closest.Distance)
                closest = new ProximityPromptTarget(prompt, parent, distance, indicatorOnly);
        }
        return closest;
    }

    private static IEnumerable<(SceneNode Prompt, Vector3? Position)> GetPrompts(SceneNode part)
    {
        var partPosition = ToVector(part.Position);
        var partRotation = RotationMath.FromDegrees(part.Rotation);
        foreach (var child in part.Children)
        {
            if (child.Type == "ProximityPrompt" && child.Enabled) yield return (child, null);
            else if (child.Type == "Attachment")
                foreach (var prompt in GetAttachmentPrompts(child, partPosition, partRotation)) yield return prompt;
        }
    }

    private static IEnumerable<(SceneNode Prompt, Vector3? Position)> GetAttachmentPrompts(
        SceneNode attachment, Vector3 parentPosition, Matrix4x4 parentRotation)
    {
        var worldPosition = parentPosition + Vector3.Transform(ToVector(attachment.Position), parentRotation);
        var worldRotation = RotationMath.FromDegrees(attachment.Rotation) * parentRotation;
        foreach (var child in attachment.Children)
        {
            if (child.Type == "ProximityPrompt" && child.Enabled) yield return (child, worldPosition);
            else if (child.Type == "Attachment")
                foreach (var prompt in GetAttachmentPrompts(child, worldPosition, worldRotation)) yield return prompt;
        }
    }

    private static Vector3 ToVector(float[] values) => new(values[0], values[1], values[2]);

    private static SceneNode? FindModelContaining(SceneNode node, string descendantId)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == "Model" && GameProjectStore.Flatten(child.Children).Any(descendant => descendant.Id == descendantId)) return child;
            if (FindModelContaining(child, descendantId) is { } nested) return nested;
        }
        return null;
    }

    private static bool HasLineOfSight(SceneNode target, SceneNode playerRoot, IReadOnlyList<SceneNode> occluders, Vector3? targetPosition = null)
    {
        var origin = new Vector3(playerRoot.Position[0], playerRoot.Position[1], playerRoot.Position[2]);
        var destination = targetPosition ?? new Vector3(target.Position[0], target.Position[1], target.Position[2]);
        var direction = destination - origin;
        var distance = direction.Length();
        if (distance <= .001f) return true;
        foreach (var occluder in occluders)
        {
            if (occluder.Id == target.Id) continue;
            if (CollisionGeometry.Raycast(occluder, origin, direction) is { } hit && hit.Distance < distance - .01f) return false;
        }
        return true;
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
