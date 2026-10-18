using BullerStudio.Models;

namespace BullerStudio.Services;

public sealed class PhysicsSimulationService
{
    private readonly GameProject project;
    private readonly Action<SceneNode> moved;
    private readonly Dictionary<string, float[]> weldedVelocities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, float[]>> weldedPositions = new(StringComparer.Ordinal);

    public PhysicsSimulationService(GameProject project, Action<SceneNode> moved)
    {
        this.project = project;
        this.moved = moved;
    }

    public void Step(float elapsedSeconds)
    {
        if (!float.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return;
        var parts = GameProjectStore.Flatten(project.Workspace.Children).Where(SceneNodeTypes.IsBasePart).ToArray();
        var weldedGroups = BuildWeldedGroups(parts);
        var activeAssemblies = weldedGroups.Values.Distinct().Select(AssemblyKey).ToHashSet(StringComparer.Ordinal);
        foreach (var key in weldedVelocities.Keys.Where(key => !activeAssemblies.Contains(key)).ToArray()) weldedVelocities.Remove(key);
        foreach (var key in weldedPositions.Keys.Where(key => !activeAssemblies.Contains(key)).ToArray()) weldedPositions.Remove(key);
        var remaining = Math.Min(elapsedSeconds, .25f);
        while (remaining > 0)
        {
            var deltaTime = Math.Min(remaining, .02f);
            SimulateStep(parts, weldedGroups, deltaTime);
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

    private Dictionary<string, SceneNode[]> BuildWeldedGroups(SceneNode[] parts)
    {
        var indexes = parts.Select((part, index) => (part.Id, index)).ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        var parents = Enumerable.Range(0, parts.Length).ToArray();
        int Find(int index)
        {
            while (parents[index] != index) index = parents[index] = parents[parents[index]];
            return index;
        }
        foreach (var weld in GameProjectStore.Flatten(project.Workspace.Children).Where(node => node.Type == "WeldConstraint" && node.Enabled))
        {
            if (!indexes.TryGetValue(weld.Part0, out var first) || !indexes.TryGetValue(weld.Part1, out var second) || first == second) continue;
            var firstRoot = Find(first);
            var secondRoot = Find(second);
            if (firstRoot != secondRoot) parents[secondRoot] = firstRoot;
        }
        var components = new Dictionary<int, List<SceneNode>>();
        for (var index = 0; index < parts.Length; index++)
        {
            var root = Find(index);
            if (!components.TryGetValue(root, out var component)) components[root] = component = [];
            component.Add(parts[index]);
        }
        var result = new Dictionary<string, SceneNode[]>(StringComparer.Ordinal);
        foreach (var component in components.Values.Where(component => component.Count > 1))
        {
            var group = component.ToArray();
            foreach (var part in group) result[part.Id] = group;
        }
        return result;
    }

    private void SimulateStep(SceneNode[] parts, Dictionary<string, SceneNode[]> weldedGroups, float deltaTime)
    {
        foreach (var part in parts)
        {
            if (weldedGroups.TryGetValue(part.Id, out var group))
            {
                if (ReferenceEquals(group[0], part)) SimulateWeldedGroup(group, parts, deltaTime);
                continue;
            }
            SimulateSingle(part, parts, deltaTime);
        }
    }

    private void SimulateWeldedGroup(SceneNode[] group, SceneNode[] parts, float deltaTime)
    {
        var assemblyKey = AssemblyKey(group);
        ReconcileWeldedPositions(group, assemblyKey);
        if (group.Any(part => part.Anchored))
        {
            foreach (var part in group)
            {
                if (part.Velocity.All(value => value == 0)) continue;
                part.Velocity = [0, 0, 0];
                moved(part);
            }
            weldedVelocities[assemblyKey] = [0, 0, 0];
            RememberWeldedPositions(group, assemblyKey);
            return;
        }
        var oldVelocities = group.Select(part => (float[])part.Velocity.Clone()).ToArray();
        var movedPositions = new bool[group.Length];
        var driver = weldedVelocities.TryGetValue(assemblyKey, out var priorVelocity)
            ? group.FirstOrDefault(part => !part.Velocity.SequenceEqual(priorVelocity)) ?? group[0]
            : group.FirstOrDefault(part => part.Velocity.Any(value => Math.Abs(value) > .000001f)) ?? group[0];
        var velocity = (float[])driver.Velocity.Clone();
        velocity[1] -= Math.Clamp(project.Workspace.Gravity, 0, 1000) * deltaTime;
        var memberIds = group.Select(part => part.Id).ToHashSet(StringComparer.Ordinal);
        for (var axis = 0; axis < 3; axis++)
        {
            var desiredMotion = velocity[axis] * deltaTime;
            var allowedMotion = desiredMotion;
            if (desiredMotion == 0) continue;
            foreach (var member in group.Where(part => part.CanCollide))
            foreach (var other in parts)
            {
                if (memberIds.Contains(other.Id) || !other.CanCollide || !OverlapsOnOtherAxes(member, other, axis)) continue;
                var halfSize = Math.Abs(member.Size[axis]) * .5f;
                var otherHalf = Math.Abs(other.Size[axis]) * .5f;
                var lower = other.Position[axis] - otherHalf - halfSize;
                var upper = other.Position[axis] + otherHalf + halfSize;
                var origin = member.Position[axis];
                if (origin > lower && origin < upper)
                {
                    if (Math.Abs(origin + desiredMotion - other.Position[axis]) <= Math.Abs(origin - other.Position[axis]))
                        allowedMotion = 0;
                }
                else if (desiredMotion > 0 && origin <= lower && origin + allowedMotion >= lower)
                    allowedMotion = Math.Min(allowedMotion, lower - origin);
                else if (desiredMotion < 0 && origin >= upper && origin + allowedMotion <= upper)
                    allowedMotion = Math.Max(allowedMotion, upper - origin);
            }
            if (allowedMotion != desiredMotion) velocity[axis] = 0;
            if (Math.Abs(allowedMotion) < .000001f) continue;
            for (var index = 0; index < group.Length; index++)
            {
                group[index].Position[axis] += allowedMotion;
                movedPositions[index] = true;
            }
        }
        for (var index = 0; index < group.Length; index++)
        {
            var part = group[index];
            if (!oldVelocities[index].SequenceEqual(velocity)) part.Velocity = (float[])velocity.Clone();
            if (movedPositions[index] || !oldVelocities[index].SequenceEqual(velocity)) moved(part);
        }
        weldedVelocities[assemblyKey] = (float[])velocity.Clone();
        RememberWeldedPositions(group, assemblyKey);
    }

    private void ReconcileWeldedPositions(SceneNode[] group, string assemblyKey)
    {
        if (!weldedPositions.TryGetValue(assemblyKey, out var previous) || group.Any(part => !previous.ContainsKey(part.Id))) return;
        static bool HasMoved(SceneNode part, float[] before) =>
            Enumerable.Range(0, 3).Any(axis => Math.Abs(part.Position[axis] - before[axis]) > .000001f);
        var changed = group.FirstOrDefault(part => HasMoved(part, previous[part.Id]));
        if (changed is null) return;
        var anchored = group.FirstOrDefault(part => part.Anchored);
        var source = anchored is null
            ? changed
            : group.FirstOrDefault(part => part.Anchored && HasMoved(part, previous[part.Id])) ?? anchored;
        var displacement = Enumerable.Range(0, 3).Select(axis => source.Position[axis] - previous[source.Id][axis]).ToArray();
        foreach (var part in group)
        {
            var expected = Enumerable.Range(0, 3).Select(axis => previous[part.Id][axis] + displacement[axis]).ToArray();
            if (!HasMoved(part, expected))
            {
                if (HasMoved(part, previous[part.Id])) moved(part);
                continue;
            }
            part.Position = expected;
            moved(part);
        }
    }

    private void RememberWeldedPositions(SceneNode[] group, string assemblyKey) =>
        weldedPositions[assemblyKey] = group.ToDictionary(part => part.Id, part => (float[])part.Position.Clone(), StringComparer.Ordinal);

    private static string AssemblyKey(SceneNode[] group) =>
        string.Join("|", group.Select(part => part.Id).OrderBy(id => id, StringComparer.Ordinal));

    private void SimulateSingle(SceneNode part, SceneNode[] parts, float deltaTime)
    {
        if (part.Anchored) return;
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
