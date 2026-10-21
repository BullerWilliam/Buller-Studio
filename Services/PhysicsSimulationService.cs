using BullerStudio.Models;
using System.Numerics;

namespace BullerStudio.Services;

public sealed class PhysicsSimulationService
{
    private readonly GameProject project;
    private readonly Action<SceneNode> moved;
    private readonly Dictionary<string, float[]> weldedVelocities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, WeldedTransform>> weldedTransforms = new(StringComparer.Ordinal);
    private sealed record WeldedTransform(float[] Position, float[] Rotation);

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
        foreach (var key in weldedTransforms.Keys.Where(key => !activeAssemblies.Contains(key)).ToArray()) weldedTransforms.Remove(key);
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
        foreach (var other in GameProjectStore.Flatten(project.Workspace.Children).Where(SceneNodeTypes.IsBasePart))
        {
            if (ReferenceEquals(other, part) || !other.CanCollide) continue;
            if (CollisionGeometry.SweepFraction(part, other, 1, -.08f) < 1) return true;
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
        ReconcileWeldedTransforms(group, assemblyKey);
        if (group.Any(part => part.Anchored))
        {
            foreach (var part in group)
            {
                if (part.Velocity.All(value => value == 0)) continue;
                part.Velocity = [0, 0, 0];
                moved(part);
            }
            weldedVelocities[assemblyKey] = [0, 0, 0];
            RememberWeldedTransforms(group, assemblyKey);
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
                if (memberIds.Contains(other.Id) || !other.CanCollide) continue;
                var fraction = CollisionGeometry.SweepFraction(member, other, axis, desiredMotion);
                if (desiredMotion > 0) allowedMotion = Math.Min(allowedMotion, desiredMotion * fraction);
                else allowedMotion = Math.Max(allowedMotion, desiredMotion * fraction);
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
        RememberWeldedTransforms(group, assemblyKey);
    }

    private void ReconcileWeldedTransforms(SceneNode[] group, string assemblyKey)
    {
        if (group.Any(part => part.Name == "WeldChild")) Console.WriteLine($"DEBUG weld start {weldedTransforms.ContainsKey(assemblyKey)} {group[1].Position[0]} {group[1].Rotation[1]}");
        if (!weldedTransforms.TryGetValue(assemblyKey, out var previous) || group.Any(part => !previous.ContainsKey(part.Id))) return;
        static bool HasChanged(float[] current, float[] before) =>
            Enumerable.Range(0, 3).Any(axis => Math.Abs(current[axis] - before[axis]) > .000001f);
        bool WasEdited(SceneNode part) => HasChanged(part.Position, previous[part.Id].Position) || HasChanged(part.Rotation, previous[part.Id].Rotation);
        var changed = group.FirstOrDefault(WasEdited);
        if (group.Any(part => part.Name == "WeldChild")) Console.WriteLine($"DEBUG weld changed {changed?.Name} old {previous[group[1].Id].Position[0]} {previous[group[1].Id].Rotation[1]}");
        if (changed is null) return;
        var anchored = group.FirstOrDefault(part => part.Anchored);
        var source = anchored is null
            ? changed
            : group.FirstOrDefault(part => part.Anchored && WasEdited(part)) ?? anchored;
        if (group.Any(part => part.Name == "WeldChild")) Console.WriteLine($"DEBUG source {source.Name} anchor {anchored?.Name}");
        var sourceBefore = previous[source.Id];
        var previousRotation = RotationMatrix(sourceBefore.Rotation);
        var currentRotation = RotationMatrix(source.Rotation);
        var deltaRotation = Matrix4x4.Transpose(previousRotation) * currentRotation;
        var oldPivot = new Vector3(sourceBefore.Position[0], sourceBefore.Position[1], sourceBefore.Position[2]);
        var newPivot = new Vector3(source.Position[0], source.Position[1], source.Position[2]);
        foreach (var part in group)
        {
            if (ReferenceEquals(part, source)) { moved(part); continue; }
            var before = previous[part.Id];
            var oldPosition = new Vector3(before.Position[0], before.Position[1], before.Position[2]);
            var expectedPosition = newPivot + Vector3.TransformNormal(oldPosition - oldPivot, deltaRotation);
            var expectedRotation = RotationVector(RotationMatrix(before.Rotation) * deltaRotation);
            var position = new[] { expectedPosition.X, expectedPosition.Y, expectedPosition.Z };
            if (group.Any(part => part.Name == "WeldChild")) Console.WriteLine($"DEBUG target {part.Name} expected {position[0]} {position[2]} {expectedRotation[1]} current {part.Position[0]} {part.Rotation[1]}");
            if (!HasChanged(part.Position, position) && !HasChanged(part.Rotation, expectedRotation)) continue;
            part.Position = position;
            part.Rotation = expectedRotation;
            moved(part);
        }
    }

    private void RememberWeldedTransforms(SceneNode[] group, string assemblyKey) =>
        weldedTransforms[assemblyKey] = group.ToDictionary(part => part.Id, part => new WeldedTransform((float[])part.Position.Clone(), (float[])part.Rotation.Clone()), StringComparer.Ordinal);

    private static Matrix4x4 RotationMatrix(float[] degrees)
    {
        var factor = MathF.PI / 180;
        return Matrix4x4.CreateRotationZ(degrees[2] * factor)
            * Matrix4x4.CreateRotationX(degrees[0] * factor)
            * Matrix4x4.CreateRotationY(degrees[1] * factor);
    }

    private static float[] RotationVector(Matrix4x4 matrix)
    {
        var pitch = MathF.Asin(Math.Clamp(-matrix.M32, -1, 1));
        var yaw = MathF.Abs(MathF.Cos(pitch)) > .00001f
            ? MathF.Atan2(matrix.M31, matrix.M33)
            : MathF.Atan2(-matrix.M13, matrix.M11);
        var roll = MathF.Abs(MathF.Cos(pitch)) > .00001f
            ? MathF.Atan2(matrix.M12, matrix.M22) : 0;
        var factor = 180 / MathF.PI;
        return [pitch * factor, yaw * factor, roll * factor];
    }

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
            var allowedMotion = motion;
            if (part.CanCollide)
            {
                foreach (var other in parts)
                {
                    if (ReferenceEquals(part, other) || !other.CanCollide) continue;
                    var fraction = CollisionGeometry.SweepFraction(part, other, axis, motion);
                    if (motion > 0) allowedMotion = Math.Min(allowedMotion, motion * fraction);
                    else allowedMotion = Math.Max(allowedMotion, motion * fraction);
                }
                if (allowedMotion != motion) velocity[axis] = 0;
            }
            var destination = origin + allowedMotion;
            if (Math.Abs(destination - origin) < .000001f) continue;
            part.Position[axis] = destination;
            changed = true;
        }
        if (changed || !oldVelocity.SequenceEqual(velocity)) moved(part);
    }

}
