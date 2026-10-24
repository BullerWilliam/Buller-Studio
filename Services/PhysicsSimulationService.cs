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
    private sealed record VelocityTarget(SceneNode Constraint, Matrix4x4 Orientation);
    private sealed record OrientationOwner(SceneNode Attachment, SceneNode Part);

    public PhysicsSimulationService(GameProject project, Action<SceneNode> moved)
    {
        this.project = project;
        this.moved = moved;
    }

    public void Step(float elapsedSeconds)
    {
        if (!float.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return;
        var parts = GameProjectStore.Flatten(project.Workspace.Children).Where(SceneNodeTypes.IsBasePart).ToArray();
        var appliedForces = BuildForceMap(parts);
        var velocityTargets = BuildVelocityMap(parts);
        var orientations = BuildOrientationOwners(parts);
        var ignoredPairs = BuildIgnoredCollisionPairs(parts);
        var weldedGroups = BuildWeldedGroups(parts);
        var activeAssemblies = weldedGroups.Values.Distinct().Select(AssemblyKey).ToHashSet(StringComparer.Ordinal);
        foreach (var key in weldedVelocities.Keys.Where(key => !activeAssemblies.Contains(key)).ToArray()) weldedVelocities.Remove(key);
        foreach (var key in weldedTransforms.Keys.Where(key => !activeAssemblies.Contains(key)).ToArray()) weldedTransforms.Remove(key);
        var remaining = Math.Min(elapsedSeconds, .25f);
        while (remaining > 0)
        {
            var deltaTime = Math.Min(remaining, .02f);
            var stepForces = new Dictionary<string, Vector3>(appliedForces, StringComparer.Ordinal);
            foreach (var (partId, force) in BuildAlignmentForceMap(parts))
                stepForces[partId] = stepForces.GetValueOrDefault(partId) + force;
            SimulateStep(parts, weldedGroups, stepForces, velocityTargets, ignoredPairs, deltaTime);
            ApplyAngularVelocityTargets(parts, orientations, deltaTime);
            ApplyOrientationTargets(orientations, deltaTime);
            remaining -= deltaTime;
        }
    }

    public bool IsGrounded(SceneNode part)
    {
        if (part.Anchored || !part.CanCollide || part.Velocity[1] > .01f) return false;
        var parts = GameProjectStore.Flatten(project.Workspace.Children).Where(SceneNodeTypes.IsBasePart).ToArray();
        var ignoredPairs = BuildIgnoredCollisionPairs(parts);
        foreach (var other in parts)
        {
            if (ReferenceEquals(other, part) || !other.CanCollide) continue;
            if (ignoredPairs.Contains(CollisionPair(part.Id, other.Id))) continue;
            if (CollisionGeometry.SweepFraction(part, other, 1, -.08f) < 1) return true;
        }
        return false;
    }

    private HashSet<(string, string)> BuildIgnoredCollisionPairs(SceneNode[] parts)
    {
        var partIds = parts.Select(part => part.Id).ToHashSet(StringComparer.Ordinal);
        var result = new HashSet<(string, string)>();
        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type == "NoCollisionConstraint" && node.Enabled))
        {
            if (constraint.Part0 == constraint.Part1 || !partIds.Contains(constraint.Part0) || !partIds.Contains(constraint.Part1)) continue;
            result.Add(CollisionPair(constraint.Part0, constraint.Part1));
        }
        return result;
    }

    private static (string, string) CollisionPair(string first, string second) =>
        string.CompareOrdinal(first, second) <= 0 ? (first, second) : (second, first);

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

    private Dictionary<string, Vector3> BuildForceMap(SceneNode[] parts)
    {
        var attachmentOwners = parts.SelectMany(part => part.Children.Where(child => child.Type == "Attachment")
            .Select(attachment => (attachment, part)))
            .ToDictionary(pair => pair.attachment.Id, pair => pair, StringComparer.Ordinal);
        var forces = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children).Where(node => node.Type == "VectorForce" && node.Enabled))
        {
            if (!attachmentOwners.TryGetValue(constraint.Attachment0, out var owner)) continue;
            var force = new Vector3(constraint.Force[0], constraint.Force[1], constraint.Force[2]);
            if (!float.IsFinite(force.X) || !float.IsFinite(force.Y) || !float.IsFinite(force.Z)) continue;
            if (constraint.RelativeTo == "Attachment0")
                force = Vector3.TransformNormal(force,
                    RotationMath.FromDegrees(owner.attachment.Rotation) * RotationMath.FromDegrees(owner.part.Rotation));
            forces[owner.part.Id] = forces.GetValueOrDefault(owner.part.Id) + force;
        }
        return forces;
    }

    private Dictionary<string, Vector3> BuildAlignmentForceMap(SceneNode[] parts)
    {
        var owners = parts.SelectMany(part => part.Children.Where(child => child.Type == "Attachment")
            .Select(attachment => (attachment, part)))
            .ToDictionary(pair => pair.attachment.Id, pair => pair, StringComparer.Ordinal);
        var forces = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        static Vector3 WorldPosition(SceneNode attachment, SceneNode part) =>
            Vector(part.Position) + Vector3.Transform(Vector(attachment.Position), RotationMath.FromDegrees(part.Rotation));
        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type == "AlignPosition" && node.Enabled))
        {
            if (!owners.TryGetValue(constraint.Attachment0, out var first)) continue;
            var target = Vector(constraint.Position);
            var targetVelocity = Vector3.Zero;
            SceneNode? reactionPart = null;
            if (constraint.AlignmentMode == "TwoAttachment")
            {
                if (!owners.TryGetValue(constraint.Attachment1, out var second)) continue;
                if (second.part.Id == first.part.Id) continue;
                target = WorldPosition(second.attachment, second.part);
                targetVelocity = Vector(second.part.Velocity);
                reactionPart = second.part;
            }
            var error = target - WorldPosition(first.attachment, first.part);
            var response = constraint.RigidityEnabled ? 50 : Math.Clamp(constraint.Responsiveness, 0, 200);
            var desiredVelocity = error * response;
            var maxVelocity = constraint.RigidityEnabled ? 10000 : Math.Clamp(constraint.MaxVelocity, 0, 10000);
            if (desiredVelocity.Length() > maxVelocity && desiredVelocity.Length() > 0)
                desiredVelocity *= maxVelocity / desiredVelocity.Length();
            var force = (desiredVelocity + targetVelocity - Vector(first.part.Velocity)) * response * PartMass(first.part);
            var maxForce = constraint.RigidityEnabled ? 1000000 : Math.Clamp(constraint.MaxForce, 0, 1000000);
            var magnitude = force.Length();
            if (!float.IsFinite(magnitude) || magnitude <= 0 || maxForce <= 0) continue;
            if (magnitude > maxForce) force *= maxForce / magnitude;
            forces[first.part.Id] = forces.GetValueOrDefault(first.part.Id) + force;
            if (reactionPart is not null && constraint.ReactionForceEnabled && reactionPart.Id != first.part.Id)
                forces[reactionPart.Id] = forces.GetValueOrDefault(reactionPart.Id) - force;
        }
        return forces;
    }

    private static float PartMass(SceneNode part) => Math.Max(.1f,
        Math.Abs(part.Size[0] * part.Size[1] * part.Size[2]));

    private Dictionary<string, List<VelocityTarget>> BuildVelocityMap(SceneNode[] parts)
    {
        var attachmentOwners = parts.SelectMany(part => part.Children.Where(child => child.Type == "Attachment")
            .Select(attachment => (attachment, part)))
            .ToDictionary(pair => pair.attachment.Id, pair => pair, StringComparer.Ordinal);
        var targets = new Dictionary<string, List<VelocityTarget>>(StringComparer.Ordinal);
        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children).Where(node => node.Type == "LinearVelocity" && node.Enabled))
        {
            if (!attachmentOwners.TryGetValue(constraint.Attachment0, out var owner)) continue;
            var orientation = constraint.RelativeTo == "Attachment0"
                ? RotationMath.FromDegrees(owner.attachment.Rotation) * RotationMath.FromDegrees(owner.part.Rotation)
                : Matrix4x4.Identity;
            if (!targets.TryGetValue(owner.part.Id, out var list)) targets[owner.part.Id] = list = [];
            list.Add(new VelocityTarget(constraint, orientation));
        }
        return targets;
    }

    private Dictionary<string, OrientationOwner> BuildOrientationOwners(SceneNode[] parts) => parts
        .SelectMany(part => part.Children.Where(child => child.Type == "Attachment")
            .Select(attachment => (attachment, part)))
        .ToDictionary(pair => pair.attachment.Id, pair => new OrientationOwner(pair.attachment, pair.part), StringComparer.Ordinal);

    private void ApplyAngularVelocityTargets(SceneNode[] parts, Dictionary<string, OrientationOwner> owners, float deltaTime)
    {
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type == "AngularVelocity" && node.Enabled))
        {
            if (!owners.TryGetValue(constraint.Attachment0, out var owner) || owner.Part.Anchored) continue;
            var target = Vector(constraint.AngularVelocityTarget);
            OrientationOwner? reactionOwner = null;
            if (constraint.RelativeTo == "Attachment0")
                target = Vector3.TransformNormal(target, RotationMath.FromDegrees(owner.Attachment.Rotation) * RotationMath.FromDegrees(owner.Part.Rotation));
            else if (constraint.RelativeTo == "Attachment1")
            {
                if (!owners.TryGetValue(constraint.Attachment1, out var relativeAttachment)) continue;
                target = Vector3.TransformNormal(target, RotationMath.FromDegrees(relativeAttachment.Attachment.Rotation) * RotationMath.FromDegrees(relativeAttachment.Part.Rotation));
                if (constraint.ReactionTorqueEnabled && !relativeAttachment.Part.Anchored && relativeAttachment.Part.Id != owner.Part.Id)
                    reactionOwner = relativeAttachment;
            }
            var current = Vector(owner.Part.AssemblyAngularVelocity);
            var error = target - current;
            var magnitude = error.Length();
            var maxChange = constraint.MaxTorque / PartMomentOfInertia(owner.Part) * deltaTime;
            if (!float.IsFinite(magnitude) || !float.IsFinite(maxChange) || maxChange <= 0) continue;
            var change = magnitude > maxChange ? error * (maxChange / magnitude) : error;
            owner.Part.AssemblyAngularVelocity = [current.X + change.X, current.Y + change.Y, current.Z + change.Z];
            if (reactionOwner is { } reaction)
            {
                var firstInertia = PartMomentOfInertia(owner.Part);
                var secondInertia = PartMomentOfInertia(reaction.Part);
                var reactionChange = -change * (firstInertia / secondInertia);
                reaction.Part.AssemblyAngularVelocity = [reaction.Part.AssemblyAngularVelocity[0] + reactionChange.X,
                    reaction.Part.AssemblyAngularVelocity[1] + reactionChange.Y,
                    reaction.Part.AssemblyAngularVelocity[2] + reactionChange.Z];
            }
        }

        foreach (var part in parts)
        {
            if (part.Anchored)
            {
                part.AssemblyAngularVelocity = [0, 0, 0];
                continue;
            }
            var angularVelocity = Vector(part.AssemblyAngularVelocity);
            if (!float.IsFinite(angularVelocity.X) || !float.IsFinite(angularVelocity.Y) || !float.IsFinite(angularVelocity.Z))
            {
                part.AssemblyAngularVelocity = [0, 0, 0];
                continue;
            }
            var rotationDelta = angularVelocity * (deltaTime * (180f / MathF.PI));
            if (rotationDelta.LengthSquared() < .000001f) continue;
            part.Rotation = [part.Rotation[0] + rotationDelta.X, part.Rotation[1] + rotationDelta.Y, part.Rotation[2] + rotationDelta.Z];
            moved(part);
        }
    }

    private void ApplyOrientationTargets(Dictionary<string, OrientationOwner> owners, float deltaTime)
    {
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        static float WrapAngle(float degrees)
        {
            degrees %= 360;
            if (degrees > 180) degrees -= 360;
            if (degrees < -180) degrees += 360;
            return degrees;
        }

        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type == "AlignOrientation" && node.Enabled))
        {
            if (!owners.TryGetValue(constraint.Attachment0, out var first) || first.Part.Anchored) continue;
            var targetRotation = RotationMath.FromDegrees(constraint.AlignOrientationRotation);
            OrientationOwner? reactionOwner = null;
            if (constraint.AlignmentMode == "TwoAttachment")
            {
                if (!owners.TryGetValue(constraint.Attachment1, out var second) || second.Part.Id == first.Part.Id) continue;
                var targetAttachmentRotation = RotationMath.FromDegrees(second.Attachment.Rotation) * RotationMath.FromDegrees(second.Part.Rotation);
                targetRotation = Matrix4x4.Transpose(RotationMath.FromDegrees(first.Attachment.Rotation)) * targetAttachmentRotation;
                reactionOwner = second;
            }
            else
                targetRotation = Matrix4x4.Transpose(RotationMath.FromDegrees(first.Attachment.Rotation)) * targetRotation;

            var current = Vector(first.Part.Rotation);
            var desired = RotationMath.ToDegrees(targetRotation);
            var delta = new Vector3(WrapAngle(desired[0] - current.X), WrapAngle(desired[1] - current.Y), WrapAngle(desired[2] - current.Z));
            var error = delta.Length();
            if (!float.IsFinite(error) || error < .001f) continue;

            var maxSpeed = Math.Clamp(constraint.MaxAngularVelocity, 0, 10000) * (180f / MathF.PI);
            var maxAngularAcceleration = Math.Clamp(constraint.MaxTorque, 0, 1000000) / PartMomentOfInertia(first.Part) * (180f / MathF.PI);
            if (constraint.RigidityEnabled) { maxSpeed = 10000; maxAngularAcceleration = 1000000; }
            if (maxSpeed <= 0 || maxAngularAcceleration <= 0) continue;
            var response = constraint.RigidityEnabled ? 1 : Math.Clamp(constraint.Responsiveness, 0, 200) * deltaTime;
            var step = delta * Math.Min(1, response);
            var stepLength = step.Length();
            var maxStep = Math.Min(maxSpeed * deltaTime, maxAngularAcceleration * deltaTime * deltaTime);
            if (stepLength > maxStep && stepLength > 0) step *= maxStep / stepLength;
            var firstStep = step;
            var secondStep = Vector3.Zero;
            var applyReaction = reactionOwner is { } reaction && constraint.ReactionTorqueEnabled && !reaction.Part.Anchored;
            if (applyReaction && reactionOwner is { } reactionPart)
            {
                var firstInertia = PartMomentOfInertia(first.Part);
                var secondInertia = PartMomentOfInertia(reactionPart.Part);
                firstStep *= secondInertia / (firstInertia + secondInertia);
                secondStep = -step * (firstInertia / (firstInertia + secondInertia));
            }
            first.Part.Rotation = [current.X + firstStep.X, current.Y + firstStep.Y, current.Z + firstStep.Z];
            moved(first.Part);

            if (applyReaction && reactionOwner is { } reactionOwnerPart)
            {
                reactionOwnerPart.Part.Rotation = [reactionOwnerPart.Part.Rotation[0] + secondStep.X,
                    reactionOwnerPart.Part.Rotation[1] + secondStep.Y,
                    reactionOwnerPart.Part.Rotation[2] + secondStep.Z];
                moved(reactionOwnerPart.Part);
            }
        }
    }

    private static float PartMomentOfInertia(SceneNode part)
    {
        var x = Math.Max(.01f, Math.Abs(part.Size[0]));
        var y = Math.Max(.01f, Math.Abs(part.Size[1]));
        var z = Math.Max(.01f, Math.Abs(part.Size[2]));
        return Math.Max(.001f, PartMass(part) * (x * x + y * y + z * z) / 18f);
    }

    private static void ApplyVelocityTargets(float[] velocity, IEnumerable<SceneNode> members,
        Dictionary<string, List<VelocityTarget>> targets, float mass, float deltaTime)
    {
        var current = new Vector3(velocity[0], velocity[1], velocity[2]);
        foreach (var member in members)
        {
            if (!targets.TryGetValue(member.Id, out var constraints)) continue;
            foreach (var target in constraints)
            {
                var requested = TargetVelocityDelta(target, current);
                if (target.Constraint.ForceLimitsEnabled)
                {
                    var maximumChange = target.Constraint.MaxForce / mass * deltaTime;
                    if (requested.LengthSquared() > maximumChange * maximumChange && maximumChange >= 0)
                        requested = Vector3.Normalize(requested) * maximumChange;
                }
                current += requested;
            }
        }
        velocity[0] = current.X; velocity[1] = current.Y; velocity[2] = current.Z;
    }

    private static Vector3 TargetVelocityDelta(VelocityTarget target, Vector3 current)
    {
        var node = target.Constraint;
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        static Vector3 UnitOr(Vector3 value, Vector3 fallback) => value.LengthSquared() > .000001f ? Vector3.Normalize(value) : fallback;
        switch (node.VelocityConstraintMode)
        {
            case "Line":
                var line = UnitOr(Vector3.TransformNormal(Vector(node.LineDirection), target.Orientation),
                    Vector3.TransformNormal(Vector3.UnitX, target.Orientation));
                return line * (node.LineVelocity - Vector3.Dot(current, line));
            case "Plane":
                var primary = UnitOr(Vector3.TransformNormal(Vector(node.PrimaryTangentAxis), target.Orientation), Vector3.UnitX);
                var secondaryInput = Vector3.TransformNormal(Vector(node.SecondaryTangentAxis), target.Orientation);
                var secondary = secondaryInput - primary * Vector3.Dot(secondaryInput, primary);
                secondary = UnitOr(secondary, UnitOr(Vector3.Cross(primary, Vector3.UnitY), Vector3.UnitZ));
                return primary * (node.PlaneVelocity[0] - Vector3.Dot(current, primary))
                    + secondary * (node.PlaneVelocity[1] - Vector3.Dot(current, secondary));
            default:
                return Vector3.TransformNormal(Vector(node.VectorVelocity), target.Orientation) - current;
        }
    }

    private void SimulateStep(SceneNode[] parts, Dictionary<string, SceneNode[]> weldedGroups,
        Dictionary<string, Vector3> appliedForces, Dictionary<string, List<VelocityTarget>> velocityTargets,
        HashSet<(string, string)> ignoredPairs, float deltaTime)
    {
        foreach (var part in parts)
        {
            if (weldedGroups.TryGetValue(part.Id, out var group))
            {
                if (ReferenceEquals(group[0], part)) SimulateWeldedGroup(group, parts, appliedForces, velocityTargets, ignoredPairs, deltaTime);
                continue;
            }
            SimulateSingle(part, parts, appliedForces, velocityTargets, ignoredPairs, deltaTime);
        }
    }

    private void SimulateWeldedGroup(SceneNode[] group, SceneNode[] parts,
        Dictionary<string, Vector3> appliedForces, Dictionary<string, List<VelocityTarget>> velocityTargets,
        HashSet<(string, string)> ignoredPairs, float deltaTime)
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
        var groupForce = group.Aggregate(Vector3.Zero, (sum, part) => sum + appliedForces.GetValueOrDefault(part.Id));
        var groupMass = group.Sum(PartMass);
        var acceleration = Vector3.Clamp(groupForce / groupMass, new Vector3(-10000), new Vector3(10000));
        velocity[0] += acceleration.X * deltaTime;
        velocity[1] += acceleration.Y * deltaTime;
        velocity[2] += acceleration.Z * deltaTime;
        ApplyVelocityTargets(velocity, group, velocityTargets, groupMass, deltaTime);
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
                if (ignoredPairs.Contains(CollisionPair(member.Id, other.Id))) continue;
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
        if (!weldedTransforms.TryGetValue(assemblyKey, out var previous) || group.Any(part => !previous.ContainsKey(part.Id))) return;
        static bool HasChanged(float[] current, float[] before) =>
            Enumerable.Range(0, 3).Any(axis => Math.Abs(current[axis] - before[axis]) > .000001f);
        bool WasEdited(SceneNode part) => HasChanged(part.Position, previous[part.Id].Position) || HasChanged(part.Rotation, previous[part.Id].Rotation);
        var changed = group.FirstOrDefault(WasEdited);
        if (changed is null) return;
        var anchored = group.FirstOrDefault(part => part.Anchored);
        var source = anchored is null
            ? changed
            : group.FirstOrDefault(part => part.Anchored && WasEdited(part)) ?? anchored;
        var sourceBefore = previous[source.Id];
        var previousRotation = RotationMath.FromDegrees(sourceBefore.Rotation);
        var currentRotation = RotationMath.FromDegrees(source.Rotation);
        var deltaRotation = Matrix4x4.Transpose(previousRotation) * currentRotation;
        var rotationChanged = HasChanged(source.Rotation, sourceBefore.Rotation);
        var oldPivot = new Vector3(sourceBefore.Position[0], sourceBefore.Position[1], sourceBefore.Position[2]);
        var newPivot = new Vector3(source.Position[0], source.Position[1], source.Position[2]);
        foreach (var part in group)
        {
            if (ReferenceEquals(part, source)) { moved(part); continue; }
            var before = previous[part.Id];
            var oldPosition = new Vector3(before.Position[0], before.Position[1], before.Position[2]);
            var expectedPosition = newPivot + Vector3.TransformNormal(oldPosition - oldPivot, deltaRotation);
            var expectedRotation = rotationChanged ? RotationMath.ToDegrees(RotationMath.FromDegrees(before.Rotation) * deltaRotation) : before.Rotation;
            var position = new[] { expectedPosition.X, expectedPosition.Y, expectedPosition.Z };
            if (!HasChanged(part.Position, position) && !HasChanged(part.Rotation, expectedRotation)) continue;
            part.Position = position;
            part.Rotation = expectedRotation;
            moved(part);
        }
    }

    private void RememberWeldedTransforms(SceneNode[] group, string assemblyKey) =>
        weldedTransforms[assemblyKey] = group.ToDictionary(part => part.Id, part => new WeldedTransform((float[])part.Position.Clone(), (float[])part.Rotation.Clone()), StringComparer.Ordinal);

    private static string AssemblyKey(SceneNode[] group) =>
        string.Join("|", group.Select(part => part.Id).OrderBy(id => id, StringComparer.Ordinal));

    private void SimulateSingle(SceneNode part, SceneNode[] parts,
        Dictionary<string, Vector3> appliedForces, Dictionary<string, List<VelocityTarget>> velocityTargets,
        HashSet<(string, string)> ignoredPairs, float deltaTime)
    {
        if (part.Anchored) return;
        var velocity = part.Velocity;
        var oldVelocity = (float[])velocity.Clone();
        velocity[1] -= Math.Clamp(project.Workspace.Gravity, 0, 1000) * deltaTime;
        var acceleration = Vector3.Clamp(appliedForces.GetValueOrDefault(part.Id) / PartMass(part),
            new Vector3(-10000), new Vector3(10000));
        velocity[0] += acceleration.X * deltaTime;
        velocity[1] += acceleration.Y * deltaTime;
        velocity[2] += acceleration.Z * deltaTime;
        ApplyVelocityTargets(velocity, [part], velocityTargets, PartMass(part), deltaTime);
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
                    if (ignoredPairs.Contains(CollisionPair(part.Id, other.Id))) continue;
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
