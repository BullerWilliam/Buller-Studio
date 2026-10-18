using BullerStudio.Models;

namespace BullerStudio.Services;

public sealed class PhysicsSimulationService
{
    private readonly GameProject project;
    private readonly Action<SceneNode> moved;

    public PhysicsSimulationService(GameProject project, Action<SceneNode> moved)
    {
        this.project = project;
        this.moved = moved;
    }

    public void Step(float elapsedSeconds)
    {
        if (!float.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return;
        var parts = GameProjectStore.Flatten(project.Workspace.Children).Where(SceneNodeTypes.IsBasePart).ToArray();
        var remaining = Math.Min(elapsedSeconds, .25f);
        while (remaining > 0)
        {
            var deltaTime = Math.Min(remaining, .02f);
            SimulateStep(parts, deltaTime);
            remaining -= deltaTime;
        }
    }

    public bool IsGrounded(SceneNode part)
    {
        if (part.Anchored || !part.CanCollide || part.Velocity[1] > .01f) return false;
        var bottom = part.Position[1] - Math.Abs(part.Size[1]) * .5f;
        foreach (var other in GameProjectStore.Flatten(project.Workspace.Children).Where(SceneNodeTypes.IsBasePart))
        {
            if (ReferenceEquals(other, part) || !other.CanCollide) continue;
            var top = other.Position[1] + Math.Abs(other.Size[1]) * .5f;
            if (Math.Abs(bottom - top) > .08f) continue;
            if (OverlapsOnOtherAxes(part, other, 1)) return true;
        }
        return false;
    }

    private void SimulateStep(SceneNode[] parts, float deltaTime)
    {
        foreach (var part in parts)
        {
            if (part.Anchored) continue;
            var velocity = part.Velocity;
            var oldVelocity = (float[])velocity.Clone();
            velocity[1] -= Math.Clamp(project.Workspace.Gravity, 0, 1000) * deltaTime;
            var changed = false;
            for (var axis = 0; axis < 3; axis++)
            {
                var motion = velocity[axis] * deltaTime;
                if (motion == 0) continue;
                var origin = part.Position[axis];
                var destination = origin + motion;
                if (part.CanCollide)
                {
                    var halfSize = Math.Abs(part.Size[axis]) * .5f;
                    foreach (var other in parts)
                    {
                        if (ReferenceEquals(part, other) || !other.CanCollide || !OverlapsOnOtherAxes(part, other, axis)) continue;
                        var otherHalf = Math.Abs(other.Size[axis]) * .5f;
                        var lower = other.Position[axis] - otherHalf - halfSize;
                        var upper = other.Position[axis] + otherHalf + halfSize;
                        if (motion > 0 && origin < upper && destination >= lower && origin <= lower)
                            destination = Math.Min(destination, lower);
                        else if (motion < 0 && origin > lower && destination <= upper && origin >= upper)
                            destination = Math.Max(destination, upper);
                        else if (origin > lower && origin < upper)
                            destination = motion > 0 ? Math.Min(destination, lower) : Math.Max(destination, upper);
                    }
                    if (destination != origin + motion) velocity[axis] = 0;
                }
                if (Math.Abs(destination - origin) < .000001f) continue;
                part.Position[axis] = destination;
                changed = true;
            }
            if (changed || !oldVelocity.SequenceEqual(velocity)) moved(part);
        }
    }

    private static bool OverlapsOnOtherAxes(SceneNode first, SceneNode second, int motionAxis)
    {
        for (var axis = 0; axis < 3; axis++)
        {
            if (axis == motionAxis) continue;
            var combinedHalfSize = (Math.Abs(first.Size[axis]) + Math.Abs(second.Size[axis])) * .5f;
            if (Math.Abs(first.Position[axis] - second.Position[axis]) >= combinedHalfSize) return false;
        }
        return true;
    }
}
