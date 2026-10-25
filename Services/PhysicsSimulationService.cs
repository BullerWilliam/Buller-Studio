using BullerStudio.Models;
using System.Numerics;

namespace BullerStudio.Services;

public sealed class PhysicsSimulationService
{
    private readonly GameProject project;
    private readonly Action<SceneNode> moved;
    private readonly Action<SceneNode>? rocketReachedTarget;
    private readonly Action<SceneNode>? destroyFallenPart;
    private readonly Dictionary<string, float[]> weldedVelocities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, WeldedTransform>> weldedTransforms = new(StringComparer.Ordinal);
    private sealed record WeldedTransform(float[] Position, float[] Rotation);
    private sealed record VelocityTarget(SceneNode Constraint, Matrix4x4 Orientation, SceneNode? ReferencePart);
    private sealed record OrientationOwner(SceneNode Attachment, SceneNode Part);

    public PhysicsSimulationService(GameProject project, Action<SceneNode> moved, Action<SceneNode>? rocketReachedTarget = null, Action<SceneNode>? destroyFallenPart = null)
    {
        this.project = project;
        this.moved = moved;
        this.rocketReachedTarget = rocketReachedTarget;
        this.destroyFallenPart = destroyFallenPart;
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
            var alignmentForces = BuildAlignmentForceMap(parts, weldedGroups, out var alignmentTorques);
            foreach (var (partId, force) in alignmentForces)
                stepForces[partId] = stepForces.GetValueOrDefault(partId) + force;
            var lineForces = BuildLineForceMap(parts, weldedGroups, out var lineForceTorques);
            foreach (var (partId, force) in lineForces)
                stepForces[partId] = stepForces.GetValueOrDefault(partId) + force;
            foreach (var (partId, force) in BuildBodyMoverForceMap(parts))
                stepForces[partId] = stepForces.GetValueOrDefault(partId) + force;
            foreach (var (partId, force) in BuildBodyThrustForceMap(parts, weldedGroups))
                stepForces[partId] = stepForces.GetValueOrDefault(partId) + force;
            foreach (var (partId, force) in BuildRocketPropulsionForceMap(parts, weldedGroups))
                stepForces[partId] = stepForces.GetValueOrDefault(partId) + force;
            foreach (var (partId, force) in BuildSpringForceMap(parts))
                stepForces[partId] = stepForces.GetValueOrDefault(partId) + force;
            foreach (var (partId, force) in BuildBallSocketForceMap(parts))
                stepForces[partId] = stepForces.GetValueOrDefault(partId) + force;
            foreach (var (partId, force) in BuildPrismaticForceMap(parts, deltaTime))
                stepForces[partId] = stepForces.GetValueOrDefault(partId) + force;
            foreach (var (partId, force) in BuildRopeForceMap(parts, deltaTime))
                stepForces[partId] = stepForces.GetValueOrDefault(partId) + force;
            foreach (var (partId, force) in BuildRodForceMap(parts))
                stepForces[partId] = stepForces.GetValueOrDefault(partId) + force;
        SimulateStep(parts, weldedGroups, stepForces, velocityTargets, ignoredPairs, deltaTime);
            var appliedForceTorques = BuildVectorForceTorqueMap(parts, weldedGroups);
            foreach (var (partId, torque) in BuildBodyThrustTorqueMap(parts, weldedGroups))
                appliedForceTorques[partId] = appliedForceTorques.GetValueOrDefault(partId) + torque;
            foreach (var (partId, torque) in BuildRocketPropulsionTorqueMap(parts, weldedGroups))
                appliedForceTorques[partId] = appliedForceTorques.GetValueOrDefault(partId) + torque;
            foreach (var (partId, torque) in BuildBodyMoverTorqueMap(parts))
                appliedForceTorques[partId] = appliedForceTorques.GetValueOrDefault(partId) + torque;
            foreach (var (partId, torque) in alignmentTorques)
                appliedForceTorques[partId] = appliedForceTorques.GetValueOrDefault(partId) + torque;
            foreach (var (partId, torque) in lineForceTorques)
                appliedForceTorques[partId] = appliedForceTorques.GetValueOrDefault(partId) + torque;
            ApplyAppliedForceTorques(appliedForceTorques, parts, weldedGroups, deltaTime);
            ApplyTorqueTargets(parts, orientations, deltaTime);
            ApplyAngularVelocityTargets(parts, orientations, deltaTime);
            ApplyOrientationTargets(orientations, deltaTime);
            ApplyAngularMotion(parts, weldedGroups, ignoredPairs, deltaTime);
            ApplyMotorTransforms(parts, weldedGroups);
            remaining -= deltaTime;
        }
        var destroyHeight = project.Workspace.FallenPartsDestroyHeight;
        if (float.IsFinite(destroyHeight))
        {
            var remainingIds = GameProjectStore.Flatten(project.Workspace.Children).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var part in parts.Where(part => IsPartBelowDestroyHeight(part, destroyHeight)).ToArray())
            {
                if (!remainingIds.Contains(part.Id)) continue;
                var removedIds = new[] { part }.Concat(GameProjectStore.Flatten(part.Children)).Select(node => node.Id).ToArray();
                if (destroyFallenPart is not null) destroyFallenPart(part);
                else GameProjectStore.RemoveNode(project.Workspace.Children, part.Id);
                remainingIds.ExceptWith(removedIds);
            }
        }
    }

    public static bool IsPartBelowDestroyHeight(SceneNode part, float height)
    {
        var halfSize = new Vector3(Math.Max(0, part.Size[0]) * .5f, Math.Max(0, part.Size[1]) * .5f, Math.Max(0, part.Size[2]) * .5f);
        var rotation = RotationMath.FromDegrees(part.Rotation);
        var lowestY = float.PositiveInfinity;
        for (var x = -1; x <= 1; x += 2)
        for (var y = -1; y <= 1; y += 2)
        for (var z = -1; z <= 1; z += 2)
        {
            var corner = new Vector3(halfSize.X * x, halfSize.Y * y, halfSize.Z * z);
            lowestY = Math.Min(lowestY, part.Position[1] + Vector3.Transform(corner, rotation).Y);
        }
        return lowestY < height;
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
        foreach (var weld in GameProjectStore.Flatten(project.Workspace.Children).Where(node => node.Type is "WeldConstraint" or "Weld" or "Motor6D" && node.Enabled))
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
            else if (constraint.RelativeTo == "Attachment1")
            {
                if (!attachmentOwners.TryGetValue(constraint.Attachment1, out var reference)) continue;
                force = Vector3.TransformNormal(force,
                    RotationMath.FromDegrees(reference.attachment.Rotation) * RotationMath.FromDegrees(reference.part.Rotation));
            }
            forces[owner.part.Id] = forces.GetValueOrDefault(owner.part.Id) + force;
        }
        foreach (var part in parts)
        foreach (var bodyForce in part.Children.Where(node => node.Type == "BodyForce" && node.Enabled))
        {
            var force = new Vector3(bodyForce.Force[0], bodyForce.Force[1], bodyForce.Force[2]);
            if (!float.IsFinite(force.X) || !float.IsFinite(force.Y) || !float.IsFinite(force.Z)) continue;
            forces[part.Id] = forces.GetValueOrDefault(part.Id) + force;
        }
        return forces;
    }

    private static Dictionary<string, Vector3> BuildBodyMoverForceMap(SceneNode[] parts)
    {
        var forces = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        foreach (var part in parts)
        foreach (var mover in part.Children.Where(node => node.Type == "BodyVelocity" && node.Enabled))
        {
            if (mover.BodyVelocityTarget.Length < 3 || mover.BodyVelocityMaxForce.Length < 3) continue;
            var velocity = new Vector3(part.Velocity[0], part.Velocity[1], part.Velocity[2]);
            var target = new Vector3(mover.BodyVelocityTarget[0], mover.BodyVelocityTarget[1], mover.BodyVelocityTarget[2]);
            var maximum = new Vector3(mover.BodyVelocityMaxForce[0], mover.BodyVelocityMaxForce[1], mover.BodyVelocityMaxForce[2]);
            if (!float.IsFinite(velocity.X) || !float.IsFinite(velocity.Y) || !float.IsFinite(velocity.Z)
                || !float.IsFinite(target.X) || !float.IsFinite(target.Y) || !float.IsFinite(target.Z)
                || !float.IsFinite(maximum.X) || !float.IsFinite(maximum.Y) || !float.IsFinite(maximum.Z)) continue;
            var p = float.IsFinite(mover.BodyVelocityP) ? Math.Clamp(mover.BodyVelocityP, 0, 1000000) : 0;
            var error = (target - velocity) * p;
            var force = new Vector3(
                Math.Clamp(error.X, -Math.Abs(maximum.X), Math.Abs(maximum.X)),
                Math.Clamp(error.Y, -Math.Abs(maximum.Y), Math.Abs(maximum.Y)),
                Math.Clamp(error.Z, -Math.Abs(maximum.Z), Math.Abs(maximum.Z)));
            forces[part.Id] = forces.GetValueOrDefault(part.Id) + force;
        }
        foreach (var part in parts)
        foreach (var mover in part.Children.Where(node => node.Type == "BodyPosition" && node.Enabled))
        {
            if (mover.BodyPositionTarget.Length < 3 || mover.BodyPositionMaxForce.Length < 3) continue;
            var position = new Vector3(part.Position[0], part.Position[1], part.Position[2]);
            var velocity = new Vector3(part.Velocity[0], part.Velocity[1], part.Velocity[2]);
            var target = new Vector3(mover.BodyPositionTarget[0], mover.BodyPositionTarget[1], mover.BodyPositionTarget[2]);
            var maximum = new Vector3(mover.BodyPositionMaxForce[0], mover.BodyPositionMaxForce[1], mover.BodyPositionMaxForce[2]);
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)
                || !float.IsFinite(velocity.X) || !float.IsFinite(velocity.Y) || !float.IsFinite(velocity.Z)
                || !float.IsFinite(target.X) || !float.IsFinite(target.Y) || !float.IsFinite(target.Z)
                || !float.IsFinite(maximum.X) || !float.IsFinite(maximum.Y) || !float.IsFinite(maximum.Z)) continue;
            var p = float.IsFinite(mover.BodyPositionP) ? Math.Clamp(mover.BodyPositionP, 0, 1000000) : 0;
            var d = float.IsFinite(mover.BodyPositionD) ? Math.Clamp(mover.BodyPositionD, 0, 1000000) : 0;
            var error = (target - position) * p - velocity * d;
            var force = new Vector3(
                Math.Clamp(error.X, -Math.Abs(maximum.X), Math.Abs(maximum.X)),
                Math.Clamp(error.Y, -Math.Abs(maximum.Y), Math.Abs(maximum.Y)),
                Math.Clamp(error.Z, -Math.Abs(maximum.Z), Math.Abs(maximum.Z)));
            forces[part.Id] = forces.GetValueOrDefault(part.Id) + force;
        }
        return forces;
    }

    private static Dictionary<string, Vector3> BuildBodyThrustForceMap(SceneNode[] parts, Dictionary<string, SceneNode[]> weldedGroups)
    {
        var forces = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        foreach (var part in parts)
        foreach (var thrust in part.Children.Where(node => node.Type == "BodyThrust" && node.Enabled))
        {
            if (thrust.BodyThrustForce.Length < 3) continue;
            var force = new Vector3(thrust.BodyThrustForce[0], thrust.BodyThrustForce[1], thrust.BodyThrustForce[2]);
            if (!float.IsFinite(force.X) || !float.IsFinite(force.Y) || !float.IsFinite(force.Z)) continue;
            var group = weldedGroups.GetValueOrDefault(part.Id) ?? [part];
            force = Vector3.TransformNormal(force, RotationMath.FromDegrees(group[0].Rotation));
            forces[part.Id] = forces.GetValueOrDefault(part.Id) + force;
        }
        return forces;
    }

    private Dictionary<string, Vector3> BuildBodyThrustTorqueMap(SceneNode[] parts, Dictionary<string, SceneNode[]> weldedGroups)
    {
        var torques = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        foreach (var part in parts)
        foreach (var thrust in part.Children.Where(node => node.Type == "BodyThrust" && node.Enabled))
        {
            if (thrust.BodyThrustForce.Length < 3 || thrust.BodyThrustLocation.Length < 3) continue;
            var localForce = new Vector3(thrust.BodyThrustForce[0], thrust.BodyThrustForce[1], thrust.BodyThrustForce[2]);
            var localLocation = new Vector3(thrust.BodyThrustLocation[0], thrust.BodyThrustLocation[1], thrust.BodyThrustLocation[2]);
            if (!float.IsFinite(localForce.X) || !float.IsFinite(localForce.Y) || !float.IsFinite(localForce.Z)
                || !float.IsFinite(localLocation.X) || !float.IsFinite(localLocation.Y) || !float.IsFinite(localLocation.Z)) continue;
            var group = weldedGroups.GetValueOrDefault(part.Id) ?? [part];
            var assemblyRoot = group[0];
            var rotation = RotationMath.FromDegrees(assemblyRoot.Rotation);
            var force = Vector3.TransformNormal(localForce, rotation);
            var location = Vector3.TransformNormal(localLocation, rotation) + new Vector3(assemblyRoot.Position[0], assemblyRoot.Position[1], assemblyRoot.Position[2]);
            if (group.Any(member => member.Anchored)) continue;
            var totalMass = group.Sum(PartMass);
            if (totalMass <= .000001f) continue;
            var centerOfMass = group.Aggregate(Vector3.Zero,
                (sum, member) => sum + new Vector3(member.Position[0], member.Position[1], member.Position[2]) * PartMass(member)) / totalMass;
            var torque = Vector3.Cross(location - centerOfMass, force);
            if (!float.IsFinite(torque.X) || !float.IsFinite(torque.Y) || !float.IsFinite(torque.Z)) continue;
            torques[part.Id] = torques.GetValueOrDefault(part.Id) + torque;
        }
        return torques;
    }

    private Dictionary<string, Vector3> BuildRocketPropulsionForceMap(SceneNode[] parts, Dictionary<string, SceneNode[]> weldedGroups)
    {
        var forces = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        var partsById = parts.ToDictionary(part => part.Id, StringComparer.Ordinal);
        foreach (var part in parts)
        foreach (var rocket in part.Children.Where(node => node.Type == "RocketPropulsion" && node.Enabled && node.RocketPropulsionActive))
        {
            var group = weldedGroups.GetValueOrDefault(part.Id) ?? [part];
            if (group.Any(member => member.Anchored)) continue;
            var root = group[0];
            var target = partsById.GetValueOrDefault(rocket.RocketPropulsionTarget);
            if (target is not null && group.Any(member => member.Id == target.Id)) continue;
            var targetPosition = target is null
                ? new Vector3(rocket.RocketPropulsionTargetOffset[0], rocket.RocketPropulsionTargetOffset[1], rocket.RocketPropulsionTargetOffset[2])
                : new Vector3(target.Position[0] + rocket.RocketPropulsionTargetOffset[0], target.Position[1] + rocket.RocketPropulsionTargetOffset[1], target.Position[2] + rocket.RocketPropulsionTargetOffset[2]);
            var position = new Vector3(root.Position[0], root.Position[1], root.Position[2]);
            var delta = targetPosition - position;
            var distance = delta.Length();
            if (!float.IsFinite(distance)) continue;
            if (distance <= rocket.RocketPropulsionTargetRadius)
            {
                if (!rocket.RocketPropulsionReachedTarget)
                {
                    rocket.RocketPropulsionReachedTarget = true;
                    rocketReachedTarget?.Invoke(rocket);
                }
            }
            if (distance < .0001f) continue;
            var mass = group.Sum(PartMass);
            if (!float.IsFinite(mass) || mass <= .000001f) continue;
            var maxThrust = Math.Clamp(rocket.RocketPropulsionMaxThrust, 0, 1000000);
            var maxSpeed = Math.Clamp(rocket.RocketPropulsionMaxSpeed, 0, 10000);
            var brakingSpeed = MathF.Sqrt(2 * (maxThrust / mass) * distance);
            var desiredSpeed = Math.Min(maxSpeed, brakingSpeed);
            var targetVelocity = target is null ? Vector3.Zero : new Vector3(target.Velocity[0], target.Velocity[1], target.Velocity[2]);
            var desiredVelocity = targetVelocity + delta / distance * desiredSpeed;
            var currentVelocity = new Vector3(root.Velocity[0], root.Velocity[1], root.Velocity[2]);
            var thrustP = Math.Clamp(rocket.RocketPropulsionThrustP, 0, 1000000);
            var thrustD = Math.Clamp(rocket.RocketPropulsionThrustD, 0, 1000000);
            var force = (desiredVelocity - currentVelocity) * thrustP - currentVelocity * thrustD;
            if (!float.IsFinite(force.X) || !float.IsFinite(force.Y) || !float.IsFinite(force.Z)) continue;
            var magnitude = force.Length();
            if (magnitude > maxThrust && magnitude > .0001f) force *= maxThrust / magnitude;
            forces[part.Id] = forces.GetValueOrDefault(part.Id) + force;
        }
        return forces;
    }

    private static Dictionary<string, Vector3> BuildRocketPropulsionTorqueMap(SceneNode[] parts, Dictionary<string, SceneNode[]> weldedGroups)
    {
        var torques = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        var partsById = parts.ToDictionary(part => part.Id, StringComparer.Ordinal);
        static float RotationError(float targetDegrees, float currentDegrees)
        {
            var delta = (targetDegrees - currentDegrees) % 360;
            if (delta > 180) delta -= 360;
            if (delta < -180) delta += 360;
            return delta * (MathF.PI / 180);
        }
        foreach (var part in parts)
        foreach (var rocket in part.Children.Where(node => node.Type == "RocketPropulsion" && node.Enabled && node.RocketPropulsionActive))
        {
            var group = weldedGroups.GetValueOrDefault(part.Id) ?? [part];
            if (group.Any(member => member.Anchored)) continue;
            var root = group[0];
            var target = partsById.GetValueOrDefault(rocket.RocketPropulsionTarget);
            if (target is not null && group.Any(member => member.Id == target.Id)) continue;
            var targetPosition = target is null
                ? new Vector3(rocket.RocketPropulsionTargetOffset[0], rocket.RocketPropulsionTargetOffset[1], rocket.RocketPropulsionTargetOffset[2])
                : new Vector3(target.Position[0] + rocket.RocketPropulsionTargetOffset[0], target.Position[1] + rocket.RocketPropulsionTargetOffset[1], target.Position[2] + rocket.RocketPropulsionTargetOffset[2]);
            var position = new Vector3(root.Position[0], root.Position[1], root.Position[2]);
            var delta = targetPosition - position;
            var distance = delta.Length();
            if (!float.IsFinite(distance) || distance < .0001f) continue;
            var pitch = MathF.Asin(Math.Clamp(delta.Y / distance, -1, 1)) * (180 / MathF.PI);
            var yaw = delta.X * delta.X + delta.Z * delta.Z < .000001f ? 0 : MathF.Atan2(-delta.X, -delta.Z) * (180 / MathF.PI);
            var currentRotation = root.Rotation;
            var angularVelocity = root.AssemblyAngularVelocity;
            var maximum = rocket.RocketPropulsionMaxTorque;
            var cartoonFactor = Math.Clamp(rocket.RocketPropulsionCartoonFactor, 0, 1);
            var turnP = Math.Clamp(rocket.RocketPropulsionTurnP, 0, 1000000) * cartoonFactor;
            var turnD = Math.Clamp(rocket.RocketPropulsionTurnD, 0, 1000000);
            var error = new Vector3(
                RotationError(pitch, currentRotation[0]),
                RotationError(yaw, currentRotation[1]),
                RotationError(0, currentRotation[2]));
            var torque = error * turnP - new Vector3(angularVelocity[0], angularVelocity[1], angularVelocity[2]) * turnD;
            torque = new Vector3(
                Math.Clamp(torque.X, -Math.Abs(maximum[0]), Math.Abs(maximum[0])),
                Math.Clamp(torque.Y, -Math.Abs(maximum[1]), Math.Abs(maximum[1])),
                Math.Clamp(torque.Z, -Math.Abs(maximum[2]), Math.Abs(maximum[2])));
            if (!float.IsFinite(torque.X) || !float.IsFinite(torque.Y) || !float.IsFinite(torque.Z)) continue;
            torques[part.Id] = torques.GetValueOrDefault(part.Id) + torque;
        }
        return torques;
    }

    private Dictionary<string, Vector3> BuildVectorForceTorqueMap(SceneNode[] parts, Dictionary<string, SceneNode[]> weldedGroups)
    {
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        var attachmentOwners = parts.SelectMany(part => part.Children.Where(child => child.Type == "Attachment")
            .Select(attachment => (attachment, part)))
            .ToDictionary(pair => pair.attachment.Id, pair => pair, StringComparer.Ordinal);
        var torques = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        foreach (var forceConstraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type == "VectorForce" && node.Enabled))
        {
            if (!attachmentOwners.TryGetValue(forceConstraint.Attachment0, out var owner)) continue;
            if (forceConstraint.ApplyAtCenterOfMass) continue;
            var force = Vector(forceConstraint.Force);
            if (forceConstraint.RelativeTo == "Attachment0")
                force = Vector3.TransformNormal(force,
                    RotationMath.FromDegrees(owner.attachment.Rotation) * RotationMath.FromDegrees(owner.part.Rotation));
            else if (forceConstraint.RelativeTo == "Attachment1")
            {
                if (!attachmentOwners.TryGetValue(forceConstraint.Attachment1, out var reference)) continue;
                force = Vector3.TransformNormal(force,
                    RotationMath.FromDegrees(reference.attachment.Rotation) * RotationMath.FromDegrees(reference.part.Rotation));
            }
            if (!float.IsFinite(force.X) || !float.IsFinite(force.Y) || !float.IsFinite(force.Z)) continue;

            var group = weldedGroups.GetValueOrDefault(owner.part.Id) ?? [owner.part];
            if (group.Any(member => member.Anchored)) continue;
            var totalMass = group.Sum(PartMass);
            if (totalMass <= .000001f) continue;
            var centerOfMass = group.Aggregate(Vector3.Zero,
                (sum, member) => sum + Vector(member.Position) * PartMass(member)) / totalMass;
            var attachmentPosition = Vector(owner.part.Position)
                + Vector3.Transform(Vector(owner.attachment.Position), RotationMath.FromDegrees(owner.part.Rotation));
            var torque = Vector3.Cross(attachmentPosition - centerOfMass, force);
            if (!float.IsFinite(torque.X) || !float.IsFinite(torque.Y) || !float.IsFinite(torque.Z)) continue;
            torques[owner.part.Id] = torques.GetValueOrDefault(owner.part.Id) + torque;
        }
        return torques;
    }

    private static Dictionary<string, Vector3> BuildBodyMoverTorqueMap(SceneNode[] parts)
    {
        var torques = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        static float RotationError(float targetDegrees, float currentDegrees)
        {
            var delta = (targetDegrees - currentDegrees) % 360;
            if (delta > 180) delta -= 360;
            if (delta < -180) delta += 360;
            return delta * (MathF.PI / 180);
        }
        foreach (var part in parts)
        foreach (var gyro in part.Children.Where(node => node.Type == "BodyGyro" && node.Enabled))
        {
            if (gyro.BodyGyroTargetRotation.Length < 3 || gyro.BodyGyroMaxTorque.Length < 3) continue;
            var target = gyro.BodyGyroTargetRotation;
            var current = part.Rotation;
            var velocity = part.AssemblyAngularVelocity;
            var maximum = gyro.BodyGyroMaxTorque;
            if (Enumerable.Range(0, 3).Any(axis => !float.IsFinite(target[axis]) || !float.IsFinite(current[axis])
                    || !float.IsFinite(velocity[axis]) || !float.IsFinite(maximum[axis]))) continue;
            var p = float.IsFinite(gyro.BodyGyroP) ? Math.Clamp(gyro.BodyGyroP, 0, 1000000) : 0;
            var d = float.IsFinite(gyro.BodyGyroD) ? Math.Clamp(gyro.BodyGyroD, 0, 1000000) : 0;
            var error = new Vector3(
                RotationError(target[0], current[0]),
                RotationError(target[1], current[1]),
                RotationError(target[2], current[2]));
            var torque = error * p - new Vector3(velocity[0], velocity[1], velocity[2]) * d;
            torque = new Vector3(
                Math.Clamp(torque.X, -Math.Abs(maximum[0]), Math.Abs(maximum[0])),
                Math.Clamp(torque.Y, -Math.Abs(maximum[1]), Math.Abs(maximum[1])),
                Math.Clamp(torque.Z, -Math.Abs(maximum[2]), Math.Abs(maximum[2])));
            torques[part.Id] = torques.GetValueOrDefault(part.Id) + torque;
        }
        foreach (var part in parts)
        foreach (var mover in part.Children.Where(node => node.Type == "BodyAngularVelocity" && node.Enabled))
        {
            if (mover.BodyAngularVelocityTarget.Length < 3 || mover.BodyAngularVelocityMaxTorque.Length < 3) continue;
            var current = part.AssemblyAngularVelocity;
            var target = mover.BodyAngularVelocityTarget;
            var maximum = mover.BodyAngularVelocityMaxTorque;
            if (Enumerable.Range(0, 3).Any(axis => !float.IsFinite(target[axis]) || !float.IsFinite(current[axis]) || !float.IsFinite(maximum[axis]))) continue;
            var p = float.IsFinite(mover.BodyAngularVelocityP) ? Math.Clamp(mover.BodyAngularVelocityP, 0, 1000000) : 0;
            var error = new Vector3(target[0] - current[0], target[1] - current[1], target[2] - current[2]) * p;
            var torque = new Vector3(
                Math.Clamp(error.X, -Math.Abs(maximum[0]), Math.Abs(maximum[0])),
                Math.Clamp(error.Y, -Math.Abs(maximum[1]), Math.Abs(maximum[1])),
                Math.Clamp(error.Z, -Math.Abs(maximum[2]), Math.Abs(maximum[2])));
            torques[part.Id] = torques.GetValueOrDefault(part.Id) + torque;
        }
        return torques;
    }

    private static void ApplyAppliedForceTorques(Dictionary<string, Vector3> torques, SceneNode[] parts,
        Dictionary<string, SceneNode[]> weldedGroups, float deltaTime)
    {
        var partsById = parts.ToDictionary(part => part.Id, StringComparer.Ordinal);
        var handledAssemblies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (partId, torque) in torques)
        {
            if (!weldedGroups.TryGetValue(partId, out var group))
            {
                if (!partsById.TryGetValue(partId, out var part) || part.Anchored) continue;
                var angularVelocity = new Vector3(part.AssemblyAngularVelocity[0], part.AssemblyAngularVelocity[1], part.AssemblyAngularVelocity[2])
                    + torque / PartMomentOfInertia(part) * deltaTime;
                if (float.IsFinite(angularVelocity.X) && float.IsFinite(angularVelocity.Y) && float.IsFinite(angularVelocity.Z))
                    part.AssemblyAngularVelocity = [Math.Clamp(angularVelocity.X, -10000, 10000),
                        Math.Clamp(angularVelocity.Y, -10000, 10000), Math.Clamp(angularVelocity.Z, -10000, 10000)];
                continue;
            }

            var assemblyKey = AssemblyKey(group);
            if (!handledAssemblies.Add(assemblyKey)) continue;
            if (group.Any(member => member.Anchored)) continue;
            var totalMass = group.Sum(PartMass);
            if (totalMass <= .000001f) continue;
            var centerOfMass = group.Aggregate(Vector3.Zero,
                (sum, member) => sum + new Vector3(member.Position[0], member.Position[1], member.Position[2]) * PartMass(member)) / totalMass;
            var totalTorque = Vector3.Zero;
            var totalInertia = 0f;
            foreach (var member in group)
            {
                totalTorque += torques.GetValueOrDefault(member.Id);
                var offset = new Vector3(member.Position[0], member.Position[1], member.Position[2]) - centerOfMass;
                totalInertia += PartMomentOfInertia(member) + PartMass(member) * offset.LengthSquared();
            }
            if (totalInertia <= .000001f) continue;
            foreach (var member in group)
            {
                var angularVelocity = new Vector3(member.AssemblyAngularVelocity[0], member.AssemblyAngularVelocity[1], member.AssemblyAngularVelocity[2])
                    + totalTorque / totalInertia * deltaTime;
                if (!float.IsFinite(angularVelocity.X) || !float.IsFinite(angularVelocity.Y) || !float.IsFinite(angularVelocity.Z)) continue;
                member.AssemblyAngularVelocity = [Math.Clamp(angularVelocity.X, -10000, 10000),
                    Math.Clamp(angularVelocity.Y, -10000, 10000), Math.Clamp(angularVelocity.Z, -10000, 10000)];
            }
        }
    }

    private Dictionary<string, Vector3> BuildAlignmentForceMap(SceneNode[] parts, Dictionary<string, SceneNode[]> weldedGroups,
        out Dictionary<string, Vector3> torques)
    {
        var owners = parts.SelectMany(part => part.Children.Where(child => child.Type == "Attachment")
            .Select(attachment => (attachment, part)))
            .ToDictionary(pair => pair.attachment.Id, pair => pair, StringComparer.Ordinal);
        var forces = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        var collectedTorques = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        torques = collectedTorques;
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        static Vector3 WorldPosition(SceneNode attachment, SceneNode part) =>
            Vector(part.Position) + Vector3.Transform(Vector(attachment.Position), RotationMath.FromDegrees(part.Rotation));
        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type == "AlignPosition" && node.Enabled))
        {
            if (!owners.TryGetValue(constraint.Attachment0, out var first)) continue;
            var target = Vector(constraint.Position);
            var targetVelocity = Vector3.Zero;
            (SceneNode attachment, SceneNode part)? reactionOwner = null;
            if (constraint.AlignmentMode == "TwoAttachment")
            {
                if (!owners.TryGetValue(constraint.Attachment1, out var second)) continue;
                if (second.part.Id == first.part.Id) continue;
                target = WorldPosition(second.attachment, second.part);
                targetVelocity = Vector(second.part.Velocity);
                reactionOwner = second;
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
            if (!constraint.ApplyAtCenterOfMass)
                AccumulateAttachmentTorque(first.attachment, first.part, force);
            if (reactionOwner is { } reaction && constraint.ReactionForceEnabled && reaction.part.Id != first.part.Id)
            {
                forces[reaction.part.Id] = forces.GetValueOrDefault(reaction.part.Id) - force;
                if (!constraint.ApplyAtCenterOfMass)
                    AccumulateAttachmentTorque(reaction.attachment, reaction.part, -force);
            }
        }
        return forces;

        void AccumulateAttachmentTorque(SceneNode attachment, SceneNode part, Vector3 force)
        {
            var group = weldedGroups.GetValueOrDefault(part.Id) ?? [part];
            if (group.Any(member => member.Anchored)) return;
            var totalMass = group.Sum(PartMass);
            if (totalMass <= .000001f) return;
            var centerOfMass = group.Aggregate(Vector3.Zero,
                (sum, member) => sum + Vector(member.Position) * PartMass(member)) / totalMass;
            var attachmentPosition = Vector(part.Position)
                + Vector3.Transform(Vector(attachment.Position), RotationMath.FromDegrees(part.Rotation));
            var torque = Vector3.Cross(attachmentPosition - centerOfMass, force);
            if (float.IsFinite(torque.X) && float.IsFinite(torque.Y) && float.IsFinite(torque.Z))
                collectedTorques[part.Id] = collectedTorques.GetValueOrDefault(part.Id) + torque;
        }
    }

    private Dictionary<string, Vector3> BuildLineForceMap(SceneNode[] parts, Dictionary<string, SceneNode[]> weldedGroups,
        out Dictionary<string, Vector3> torques)
    {
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        static Vector3 WorldPosition(SceneNode attachment, SceneNode part) =>
            Vector(part.Position) + Vector3.Transform(Vector(attachment.Position), RotationMath.FromDegrees(part.Rotation));
        var owners = parts.SelectMany(part => part.Children.Where(child => child.Type == "Attachment")
            .Select(attachment => (attachment, part)))
            .ToDictionary(pair => pair.attachment.Id, pair => pair, StringComparer.Ordinal);
        var forces = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        var collectedTorques = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        torques = collectedTorques;
        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type == "LineForce" && node.Enabled))
        {
            if (!owners.TryGetValue(constraint.Attachment0, out var first)
                || !owners.TryGetValue(constraint.Attachment1, out var second)
                || first.part.Id == second.part.Id) continue;
            var firstGroup = weldedGroups.GetValueOrDefault(first.part.Id) ?? [first.part];
            var secondGroup = weldedGroups.GetValueOrDefault(second.part.Id) ?? [second.part];
            var firstCenter = CenterOfMass(firstGroup);
            var secondCenter = CenterOfMass(secondGroup);
            var firstPosition = constraint.ApplyAtCenterOfMass ? firstCenter : WorldPosition(first.attachment, first.part);
            var secondPosition = WorldPosition(second.attachment, second.part);
            var delta = secondPosition - firstPosition;
            var distanceSquared = delta.LengthSquared();
            if (!float.IsFinite(distanceSquared) || distanceSquared < .000001f) continue;
            var magnitude = Math.Clamp(constraint.LineForceMagnitude, 0, 1000000);
            if (constraint.LineForceInverseSquareLaw)
            {
                magnitude = Math.Min(1000000000, magnitude / distanceSquared);
                magnitude = Math.Min(magnitude, Math.Clamp(constraint.LineForceMaxForce, 0, 1000000));
            }
            var force = delta / MathF.Sqrt(distanceSquared) * magnitude;
            if (!float.IsFinite(force.X) || !float.IsFinite(force.Y) || !float.IsFinite(force.Z)) continue;
            forces[first.part.Id] = forces.GetValueOrDefault(first.part.Id) + force;
            if (!constraint.ApplyAtCenterOfMass)
                AccumulateTorque(first.attachment, first.part, firstGroup, force);
            if (constraint.LineForceReactionForceEnabled)
            {
                forces[second.part.Id] = forces.GetValueOrDefault(second.part.Id) - force;
                if (!constraint.ApplyAtCenterOfMass)
                    AccumulateTorque(second.attachment, second.part, secondGroup, -force);
            }
        }
        return forces;

        static Vector3 CenterOfMass(SceneNode[] group)
        {
            var totalMass = group.Sum(PartMass);
            return totalMass > .000001f
                ? group.Aggregate(Vector3.Zero, (sum, member) => sum + Vector(member.Position) * PartMass(member)) / totalMass
                : Vector(group[0].Position);
        }

        void AccumulateTorque(SceneNode attachment, SceneNode part, SceneNode[] group, Vector3 force)
        {
            if (group.Any(member => member.Anchored)) return;
            var center = CenterOfMass(group);
            var torque = Vector3.Cross(WorldPosition(attachment, part) - center, force);
            if (float.IsFinite(torque.X) && float.IsFinite(torque.Y) && float.IsFinite(torque.Z))
                collectedTorques[part.Id] = collectedTorques.GetValueOrDefault(part.Id) + torque;
        }
    }

    private Dictionary<string, Vector3> BuildSpringForceMap(SceneNode[] parts)
    {
        var owners = parts.SelectMany(part => part.Children.Where(child => child.Type == "Attachment")
            .Select(attachment => (attachment, part)))
            .ToDictionary(pair => pair.attachment.Id, pair => pair, StringComparer.Ordinal);
        var forces = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        static Vector3 WorldPosition(SceneNode attachment, SceneNode part) =>
            Vector(part.Position) + Vector3.Transform(Vector(attachment.Position), RotationMath.FromDegrees(part.Rotation));
        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type == "SpringConstraint"))
        {
            constraint.SpringCurrentLength = 0;
            if (!owners.TryGetValue(constraint.Attachment0, out var first)
                || !owners.TryGetValue(constraint.Attachment1, out var second)
                || first.part.Id == second.part.Id) continue;
            var delta = WorldPosition(second.attachment, second.part) - WorldPosition(first.attachment, first.part);
            var length = delta.Length();
            if (!float.IsFinite(length)) continue;
            constraint.SpringCurrentLength = length;
            if (!constraint.Enabled || length < .0001f) continue;
            var axis = delta / length;
            var restLength = Math.Clamp(constraint.SpringFreeLength, 0, 10000);
            if (constraint.SpringLimitsEnabled)
                restLength = Math.Clamp(restLength, constraint.SpringMinLength, constraint.SpringMaxLength);
            var lengthError = length - restLength;
            var relativeSpeed = Vector3.Dot(Vector(second.part.Velocity) - Vector(first.part.Velocity), axis);
            var magnitude = lengthError * constraint.SpringStiffness + relativeSpeed * constraint.SpringDamping;
            if (!float.IsFinite(magnitude)) continue;
            if (constraint.SpringLimitsEnabled)
            {
                if (length >= constraint.SpringMaxLength && magnitude < 0) magnitude = 0;
                if (length <= constraint.SpringMinLength && magnitude > 0) magnitude = 0;
            }
            var force = axis * Math.Clamp(magnitude, -constraint.SpringMaxForce, constraint.SpringMaxForce);
            if (!float.IsFinite(force.X) || !float.IsFinite(force.Y) || !float.IsFinite(force.Z)) continue;
            if (!first.part.Anchored) forces[first.part.Id] = forces.GetValueOrDefault(first.part.Id) + force;
            if (!second.part.Anchored) forces[second.part.Id] = forces.GetValueOrDefault(second.part.Id) - force;
        }
        return forces;
    }

    private Dictionary<string, Vector3> BuildBallSocketForceMap(SceneNode[] parts)
    {
        var owners = parts.SelectMany(part => part.Children.Where(child => child.Type == "Attachment")
            .Select(attachment => (attachment, part)))
            .ToDictionary(pair => pair.attachment.Id, pair => pair, StringComparer.Ordinal);
        var forces = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        static Vector3 WorldPosition(SceneNode attachment, SceneNode part) =>
            Vector(part.Position) + Vector3.Transform(Vector(attachment.Position), RotationMath.FromDegrees(part.Rotation));
        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type is ("BallSocketConstraint" or "HingeConstraint" or "PlaneConstraint" or "UniversalConstraint" or "RigidConstraint") && node.Enabled))
        {
            if (!owners.TryGetValue(constraint.Attachment0, out var first)
                || !owners.TryGetValue(constraint.Attachment1, out var second)
                || first.part.Id == second.part.Id) continue;
            var error = WorldPosition(second.attachment, second.part) - WorldPosition(first.attachment, first.part);
            if (constraint.Type == "PlaneConstraint")
            {
                var attachmentFrame = RotationMath.FromDegrees(first.attachment.Rotation) * RotationMath.FromDegrees(first.part.Rotation);
                var normal = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, attachmentFrame));
                error = normal * Vector3.Dot(error, normal);
            }
            var distance = error.Length();
            if (!float.IsFinite(distance) || distance < .0001f) continue;
            var relativeVelocity = Vector(second.part.Velocity) - Vector(first.part.Velocity);
            var force = error * 4000 + relativeVelocity * 120;
            var magnitude = force.Length();
            if (!float.IsFinite(magnitude) || magnitude <= 0) continue;
            if (magnitude > 100000) force *= 100000 / magnitude;
            if (!first.part.Anchored) forces[first.part.Id] = forces.GetValueOrDefault(first.part.Id) + force;
            if (!second.part.Anchored) forces[second.part.Id] = forces.GetValueOrDefault(second.part.Id) - force;
        }
        return forces;
    }

    private Dictionary<string, Vector3> BuildPrismaticForceMap(SceneNode[] parts, float deltaTime)
    {
        var owners = parts.SelectMany(part => part.Children.Where(child => child.Type == "Attachment")
            .Select(attachment => (attachment, part)))
            .ToDictionary(pair => pair.attachment.Id, pair => pair, StringComparer.Ordinal);
        var forces = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        static Vector3 WorldPosition(SceneNode attachment, SceneNode part) =>
            Vector(part.Position) + Vector3.Transform(Vector(attachment.Position), RotationMath.FromDegrees(part.Rotation));
        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type is "PrismaticConstraint" or "CylindricalConstraint"))
        {
            constraint.PrismaticCurrentPosition = 0;
            if (!owners.TryGetValue(constraint.Attachment0, out var first)
                || !owners.TryGetValue(constraint.Attachment1, out var second)
                || first.part.Id == second.part.Id) continue;
            var firstFrame = RotationMath.FromDegrees(first.attachment.Rotation) * RotationMath.FromDegrees(first.part.Rotation);
            var axis = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, firstFrame));
            var delta = WorldPosition(second.attachment, second.part) - WorldPosition(first.attachment, first.part);
            var currentPosition = Vector3.Dot(delta, axis);
            constraint.PrismaticCurrentPosition = currentPosition;
            if (!constraint.Enabled) continue;
            var relativeVelocity = Vector(second.part.Velocity) - Vector(first.part.Velocity);
            var currentSpeed = Vector3.Dot(relativeVelocity, axis);
            var perpendicularError = delta - axis * currentPosition;
            var perpendicularVelocity = relativeVelocity - axis * currentSpeed;
            var force = perpendicularError * 4000 + perpendicularVelocity * 120;
            var effectiveMass = first.part.Anchored ? PartMass(second.part)
                : second.part.Anchored ? PartMass(first.part)
                : Math.Max(.1f, PartMass(first.part) * PartMass(second.part) / (PartMass(first.part) + PartMass(second.part)));

            if (constraint.PrismaticActuatorType != "None")
            {
                var targetSpeed = constraint.PrismaticActuatorType == "Motor"
                    ? Math.Clamp(constraint.PrismaticVelocity, -10000, 10000)
                    : Math.Clamp((constraint.PrismaticTargetPosition - currentPosition) * Math.Clamp(constraint.PrismaticLinearResponsiveness, 0, 200),
                        -Math.Clamp(constraint.PrismaticSpeed, 0, 10000), Math.Clamp(constraint.PrismaticSpeed, 0, 10000));
                if (constraint.PrismaticLimitsEnabled)
                {
                    if (currentPosition >= constraint.PrismaticUpperLimit && targetSpeed > 0)
                        targetSpeed = -targetSpeed * Math.Clamp(constraint.PrismaticRestitution, 0, 1);
                    else if (currentPosition <= constraint.PrismaticLowerLimit && targetSpeed < 0)
                        targetSpeed = -targetSpeed * Math.Clamp(constraint.PrismaticRestitution, 0, 1);
                }
                var desiredAcceleration = (targetSpeed - currentSpeed) / Math.Max(deltaTime, .0001f);
                var maxAcceleration = constraint.PrismaticActuatorType == "Motor"
                    ? Math.Clamp(constraint.PrismaticMotorMaxAcceleration, 0, 100000)
                    : 100000;
                desiredAcceleration = Math.Clamp(desiredAcceleration, -maxAcceleration, maxAcceleration);
                var maxForce = constraint.PrismaticActuatorType == "Motor"
                    ? Math.Clamp(constraint.PrismaticMotorMaxForce, 0, 1000000)
                    : Math.Clamp(constraint.PrismaticServoMaxForce, 0, 1000000);
                var actuatorForce = Math.Clamp(desiredAcceleration * effectiveMass, -maxForce, maxForce);
                force -= axis * actuatorForce;
            }

            if (constraint.PrismaticLimitsEnabled)
            {
                var limitedPosition = Math.Clamp(currentPosition, constraint.PrismaticLowerLimit, constraint.PrismaticUpperLimit);
                var limitError = limitedPosition - currentPosition;
                var outwardSpeed = currentPosition < constraint.PrismaticLowerLimit ? -currentSpeed
                    : currentPosition > constraint.PrismaticUpperLimit ? currentSpeed : 0;
                var limitForce = limitError * 10000 + limitError / Math.Max(deltaTime * deltaTime, .0001f) * effectiveMass;
                if (outwardSpeed > 0) limitForce += (1 + constraint.PrismaticRestitution) * outwardSpeed * effectiveMass / Math.Max(deltaTime, .0001f);
                force -= axis * limitForce;
            }

            var forceMagnitude = force.Length();
            if (!float.IsFinite(forceMagnitude) || forceMagnitude <= 0) continue;
            if (forceMagnitude > 1000000) force *= 1000000 / forceMagnitude;
            if (!first.part.Anchored) forces[first.part.Id] = forces.GetValueOrDefault(first.part.Id) + force;
            if (!second.part.Anchored) forces[second.part.Id] = forces.GetValueOrDefault(second.part.Id) - force;
        }
        return forces;
    }

    private Dictionary<string, Vector3> BuildRopeForceMap(SceneNode[] parts, float deltaTime)
    {
        var owners = parts.SelectMany(part => part.Children.Where(child => child.Type == "Attachment")
            .Select(attachment => (attachment, part)))
            .ToDictionary(pair => pair.attachment.Id, pair => pair, StringComparer.Ordinal);
        var forces = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        static Vector3 WorldPosition(SceneNode attachment, SceneNode part) =>
            Vector(part.Position) + Vector3.Transform(Vector(attachment.Position), RotationMath.FromDegrees(part.Rotation));
        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type == "RopeConstraint"))
        {
            constraint.RopeCurrentLength = 0;
            if (!owners.TryGetValue(constraint.Attachment0, out var first)
                || !owners.TryGetValue(constraint.Attachment1, out var second)
                || first.part.Id == second.part.Id) continue;
            var delta = WorldPosition(second.attachment, second.part) - WorldPosition(first.attachment, first.part);
            var length = delta.Length();
            if (!float.IsFinite(length)) continue;
            constraint.RopeCurrentLength = length;
            if (!constraint.Enabled || length < .0001f) continue;
            var axis = delta / length;
            var relativeSpeed = Vector3.Dot(Vector(second.part.Velocity) - Vector(first.part.Velocity), axis);
            var magnitude = 0f;
            if (length > constraint.RopeLength)
            {
                var effectiveMass = first.part.Anchored ? PartMass(second.part)
                    : second.part.Anchored ? PartMass(first.part)
                    : Math.Max(.1f, PartMass(first.part) * PartMass(second.part) / (PartMass(first.part) + PartMass(second.part)));
                var impactForce = Math.Max(0, relativeSpeed) * (1 + constraint.RopeRestitution) * effectiveMass / Math.Max(deltaTime, .0001f);
                magnitude += (length - constraint.RopeLength) * 300 + impactForce;
            }
            if (constraint.RopeWinchEnabled)
            {
                var responsiveness = Math.Clamp(constraint.RopeWinchResponsiveness, 0, 200);
                var speed = Math.Clamp(constraint.RopeWinchSpeed, 0, 10000);
                var targetRadialSpeed = Math.Clamp((constraint.RopeWinchTarget - length) * responsiveness, -speed, speed);
                var effectiveMass = first.part.Anchored ? PartMass(second.part)
                    : second.part.Anchored ? PartMass(first.part)
                    : Math.Max(.1f, PartMass(first.part) * PartMass(second.part) / (PartMass(first.part) + PartMass(second.part)));
                var winchForce = Math.Clamp((relativeSpeed - targetRadialSpeed) * responsiveness * effectiveMass,
                    -constraint.RopeWinchForce, constraint.RopeWinchForce);
                magnitude += winchForce;
            }
            if (!float.IsFinite(magnitude)) continue;
            var force = axis * magnitude;
            if (!first.part.Anchored) forces[first.part.Id] = forces.GetValueOrDefault(first.part.Id) + force;
            if (!second.part.Anchored) forces[second.part.Id] = forces.GetValueOrDefault(second.part.Id) - force;
        }
        return forces;
    }

    private Dictionary<string, Vector3> BuildRodForceMap(SceneNode[] parts)
    {
        var owners = parts.SelectMany(part => part.Children.Where(child => child.Type == "Attachment")
            .Select(attachment => (attachment, part)))
            .ToDictionary(pair => pair.attachment.Id, pair => pair, StringComparer.Ordinal);
        var forces = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        static Vector3 WorldPosition(SceneNode attachment, SceneNode part) =>
            Vector(part.Position) + Vector3.Transform(Vector(attachment.Position), RotationMath.FromDegrees(part.Rotation));
        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type == "RodConstraint"))
        {
            constraint.RodCurrentDistance = 0;
            if (!owners.TryGetValue(constraint.Attachment0, out var first)
                || !owners.TryGetValue(constraint.Attachment1, out var second)
                || first.part.Id == second.part.Id) continue;
            var delta = WorldPosition(second.attachment, second.part) - WorldPosition(first.attachment, first.part);
            var distance = delta.Length();
            constraint.RodCurrentDistance = distance;
            if (!constraint.Enabled || !float.IsFinite(distance) || distance < .0001f) continue;
            var axis = delta / distance;
            var relativeSpeed = Vector3.Dot(Vector(second.part.Velocity) - Vector(first.part.Velocity), axis);
            var effectiveMass = first.part.Anchored ? PartMass(second.part)
                : second.part.Anchored ? PartMass(first.part)
                : Math.Max(.1f, PartMass(first.part) * PartMass(second.part) / (PartMass(first.part) + PartMass(second.part)));
            var magnitude = (distance - Math.Clamp(constraint.RodLength, 0, 10000)) * 1200 + relativeSpeed * effectiveMass * 12;
            if (!float.IsFinite(magnitude)) continue;
            var force = axis * Math.Clamp(magnitude, -1000000, 1000000);
            if (!first.part.Anchored) forces[first.part.Id] = forces.GetValueOrDefault(first.part.Id) + force;
            if (!second.part.Anchored) forces[second.part.Id] = forces.GetValueOrDefault(second.part.Id) - force;
        }
        return forces;
    }

    public static float PartMass(SceneNode part) => part.Massless ? .1f : Math.Max(.1f,
        Math.Abs(part.Size[0] * part.Size[1] * part.Size[2]));

    public static Vector3 AssemblyCenterOfMass(GameProject project, SceneNode part)
    {
        var members = GetAssemblyMembers(project, part);
        if (members.Length == 0) return ReadVector3(part.Position);
        var totalMass = members.Sum(PartMass);
        return members.Aggregate(Vector3.Zero, (sum, member) => sum + ReadVector3(member.Position) * PartMass(member)) / totalMass;
    }

    public static void ApplyImpulse(GameProject project, SceneNode part, Vector3 impulse)
    {
        if (!float.IsFinite(impulse.X) || !float.IsFinite(impulse.Y) || !float.IsFinite(impulse.Z)) return;
        var members = GetAssemblyMembers(project, part);
        if (members.Length == 0 || members.Any(member => member.Anchored)) return;
        var mass = members.Sum(PartMass);
        var velocity = ReadVector3(part.Velocity) + impulse / mass;
        if (!float.IsFinite(velocity.X) || !float.IsFinite(velocity.Y) || !float.IsFinite(velocity.Z)) return;
        var values = new[] { Math.Clamp(velocity.X, -10000, 10000), Math.Clamp(velocity.Y, -10000, 10000), Math.Clamp(velocity.Z, -10000, 10000) };
        foreach (var member in members) member.Velocity = (float[])values.Clone();
    }

    public static void ApplyImpulseAtPosition(GameProject project, SceneNode part, Vector3 impulse, Vector3 worldPosition)
    {
        if (!float.IsFinite(impulse.X) || !float.IsFinite(impulse.Y) || !float.IsFinite(impulse.Z)
            || !float.IsFinite(worldPosition.X) || !float.IsFinite(worldPosition.Y) || !float.IsFinite(worldPosition.Z)) return;
        var members = GetAssemblyMembers(project, part);
        if (members.Length == 0 || members.Any(member => member.Anchored)) return;
        var center = AssemblyCenterOfMass(project, part);
        var angularImpulse = Vector3.Cross(worldPosition - center, impulse);
        var inertia = members.Sum(member =>
        {
            var offset = ReadVector3(member.Position) - center;
            return PartMomentOfInertia(member) + PartMass(member) * offset.LengthSquared();
        });
        if (!float.IsFinite(inertia) || inertia <= .000001f) return;
        ApplyImpulse(project, part, impulse);
        var angularVelocity = ReadVector3(part.AssemblyAngularVelocity) + angularImpulse / inertia;
        if (!float.IsFinite(angularVelocity.X) || !float.IsFinite(angularVelocity.Y) || !float.IsFinite(angularVelocity.Z)) return;
        var values = new[] { Math.Clamp(angularVelocity.X, -10000, 10000), Math.Clamp(angularVelocity.Y, -10000, 10000), Math.Clamp(angularVelocity.Z, -10000, 10000) };
        foreach (var member in members) member.AssemblyAngularVelocity = (float[])values.Clone();
    }

    public static void ApplyAngularImpulse(GameProject project, SceneNode part, Vector3 impulse)
    {
        if (!float.IsFinite(impulse.X) || !float.IsFinite(impulse.Y) || !float.IsFinite(impulse.Z)) return;
        var members = GetAssemblyMembers(project, part);
        if (members.Length == 0 || members.Any(member => member.Anchored)) return;
        var center = AssemblyCenterOfMass(project, part);
        var inertia = members.Sum(member =>
        {
            var offset = ReadVector3(member.Position) - center;
            return PartMomentOfInertia(member) + PartMass(member) * offset.LengthSquared();
        });
        if (!float.IsFinite(inertia) || inertia <= .000001f) return;
        var angularVelocity = ReadVector3(part.AssemblyAngularVelocity) + impulse / inertia;
        if (!float.IsFinite(angularVelocity.X) || !float.IsFinite(angularVelocity.Y) || !float.IsFinite(angularVelocity.Z)) return;
        var values = new[] { Math.Clamp(angularVelocity.X, -10000, 10000), Math.Clamp(angularVelocity.Y, -10000, 10000), Math.Clamp(angularVelocity.Z, -10000, 10000) };
        foreach (var member in members) member.AssemblyAngularVelocity = (float[])values.Clone();
    }

    private static SceneNode[] GetAssemblyMembers(GameProject project, SceneNode part)
    {
        var parts = GameProjectStore.Flatten(project.Workspace.Children).Where(SceneNodeTypes.IsBasePart).ToArray();
        if (!parts.Any(member => member.Id == part.Id)) return [];
        var connected = new HashSet<string>(StringComparer.Ordinal) { part.Id };
        var constraints = GameProjectStore.Flatten(project.Workspace.Children)
            .Where(node => node.Enabled && node.Type is "WeldConstraint" or "Weld" or "Motor6D").ToArray();
        bool changed;
        do
        {
            changed = false;
            foreach (var constraint in constraints)
            {
                if (connected.Contains(constraint.Part0) && connected.Add(constraint.Part1)) changed = true;
                if (connected.Contains(constraint.Part1) && connected.Add(constraint.Part0)) changed = true;
            }
        } while (changed);
        return parts.Where(member => connected.Contains(member.Id)).ToArray();
    }

    private static Vector3 ReadVector3(float[] values) => new(values[0], values[1], values[2]);

    private Dictionary<string, List<VelocityTarget>> BuildVelocityMap(SceneNode[] parts)
    {
        var attachmentOwners = parts.SelectMany(part => part.Children.Where(child => child.Type == "Attachment")
            .Select(attachment => (attachment, part)))
            .ToDictionary(pair => pair.attachment.Id, pair => pair, StringComparer.Ordinal);
        var targets = new Dictionary<string, List<VelocityTarget>>(StringComparer.Ordinal);
        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children).Where(node => node.Type == "LinearVelocity" && node.Enabled))
        {
            if (!attachmentOwners.TryGetValue(constraint.Attachment0, out var owner)) continue;
            SceneNode? referencePart = null;
            var orientation = Matrix4x4.Identity;
            if (constraint.RelativeTo == "Attachment0")
                orientation = RotationMath.FromDegrees(owner.attachment.Rotation) * RotationMath.FromDegrees(owner.part.Rotation);
            else if (constraint.RelativeTo == "Attachment1")
            {
                if (!attachmentOwners.TryGetValue(constraint.Attachment1, out var reference)) continue;
                referencePart = reference.part;
                orientation = RotationMath.FromDegrees(reference.attachment.Rotation) * RotationMath.FromDegrees(reference.part.Rotation);
            }
            if (!targets.TryGetValue(owner.part.Id, out var list)) targets[owner.part.Id] = list = [];
            list.Add(new VelocityTarget(constraint, orientation, referencePart));
        }
        return targets;
    }

    private Dictionary<string, OrientationOwner> BuildOrientationOwners(SceneNode[] parts) => parts
        .SelectMany(part => part.Children.Where(child => child.Type == "Attachment")
            .Select(attachment => (attachment, part)))
        .ToDictionary(pair => pair.attachment.Id, pair => new OrientationOwner(pair.attachment, pair.part), StringComparer.Ordinal);

    private void ApplyBallSocketAngularLimits(Dictionary<string, OrientationOwner> owners, float deltaTime)
    {
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        static Vector3 UnitOr(Vector3 value, Vector3 fallback) => value.LengthSquared() > .000001f ? Vector3.Normalize(value) : fallback;
        static float WrapRadians(float value)
        {
            value %= MathF.Tau;
            if (value > MathF.PI) value -= MathF.Tau;
            if (value < -MathF.PI) value += MathF.Tau;
            return value;
        }

        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type == "BallSocketConstraint" && node.Enabled))
        {
            if (!owners.TryGetValue(constraint.Attachment0, out var first)
                || !owners.TryGetValue(constraint.Attachment1, out var second)
                || first.Part.Id == second.Part.Id) continue;
            var firstInverseInertia = first.Part.Anchored ? 0 : 1 / PartMomentOfInertia(first.Part);
            var secondInverseInertia = second.Part.Anchored ? 0 : 1 / PartMomentOfInertia(second.Part);
            var inverseInertiaSum = firstInverseInertia + secondInverseInertia;
            if (inverseInertiaSum <= 0) continue;

            var firstFrame = RotationMath.FromDegrees(first.Attachment.Rotation) * RotationMath.FromDegrees(first.Part.Rotation);
            var secondFrame = RotationMath.FromDegrees(second.Attachment.Rotation) * RotationMath.FromDegrees(second.Part.Rotation);
            var firstVelocity = Vector(first.Part.AssemblyAngularVelocity);
            var secondVelocity = Vector(second.Part.AssemblyAngularVelocity);
            var relativeVelocity = secondVelocity - firstVelocity;

            void ApplyRelativeChange(Vector3 change)
            {
                var firstChange = -change * (firstInverseInertia / inverseInertiaSum);
                var secondChange = change * (secondInverseInertia / inverseInertiaSum);
                firstVelocity += firstChange;
                secondVelocity += secondChange;
                relativeVelocity += secondChange - firstChange;
            }

            var axis = UnitOr(Vector3.TransformNormal(Vector3.UnitX, firstFrame), Vector3.UnitX);
            if (constraint.BallSocketLimitsEnabled)
            {
                var otherAxis = UnitOr(Vector3.TransformNormal(Vector3.UnitX, secondFrame), axis);
                var tilt = MathF.Acos(Math.Clamp(Vector3.Dot(axis, otherAxis), -1, 1));
                var maximumTilt = Math.Clamp(constraint.BallSocketUpperAngle, 0, 180) * (MathF.PI / 180);
                if (tilt >= maximumTilt - .0001f)
                {
                    var correctionAxis = UnitOr(Vector3.Cross(axis, otherAxis),
                        UnitOr(Vector3.TransformNormal(Vector3.UnitY, firstFrame), Vector3.UnitY));
                    var outwardSpeed = Vector3.Dot(relativeVelocity, correctionAxis);
                    var excessTilt = Math.Max(0, tilt - maximumTilt);
                    var targetSpeed = excessTilt > .0001f
                        ? -Math.Min(8, excessTilt * 20)
                        : outwardSpeed > 0 ? -outwardSpeed * Math.Clamp(constraint.BallSocketRestitution, 0, 1) : 0;
                    ApplyRelativeChange(correctionAxis * Math.Clamp(targetSpeed - outwardSpeed, -20, 20));
                }

                if (constraint.BallSocketTwistLimitsEnabled)
                {
                    var relativeFrame = Matrix4x4.Transpose(firstFrame) * secondFrame;
                    var relativeRotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(relativeFrame));
                    var twist = new Quaternion(relativeRotation.X, 0, 0, relativeRotation.W);
                    if (twist.LengthSquared() > .000001f)
                    {
                        twist = Quaternion.Normalize(twist);
                        var twistAngle = WrapRadians(2 * MathF.Atan2(twist.X, twist.W));
                        var lower = Math.Clamp(constraint.BallSocketTwistLowerAngle, -180, 180) * (MathF.PI / 180);
                        var upper = Math.Clamp(constraint.BallSocketTwistUpperAngle, constraint.BallSocketTwistLowerAngle, 180) * (MathF.PI / 180);
                        var clampedTwist = Math.Clamp(twistAngle, lower, upper);
                        var excessTwist = twistAngle - clampedTwist;
                        if (MathF.Abs(excessTwist) > .0001f || twistAngle <= lower + .0001f || twistAngle >= upper - .0001f)
                        {
                            var worldTwistAxis = axis;
                            var outwardSpeed = Vector3.Dot(relativeVelocity, worldTwistAxis) * MathF.Sign(twistAngle == 0 ? 1 : twistAngle);
                            var targetSpeed = MathF.Abs(excessTwist) > .0001f
                                ? -Math.Min(8, MathF.Abs(excessTwist) * 20)
                                : outwardSpeed > 0 ? -outwardSpeed * Math.Clamp(constraint.BallSocketRestitution, 0, 1) : 0;
                            var signedAxis = worldTwistAxis * MathF.Sign(twistAngle == 0 ? 1 : twistAngle);
                            ApplyRelativeChange(signedAxis * Math.Clamp(targetSpeed - outwardSpeed, -20, 20));
                        }
                    }
                }
            }

            var frictionTorque = Math.Clamp(constraint.BallSocketMaxFrictionTorque, 0, 1000000);
            var relativeSpeed = relativeVelocity.Length();
            var maxFrictionChange = frictionTorque / Math.Max(.0001f, 1 / inverseInertiaSum) * deltaTime;
            if (relativeSpeed > .0001f && maxFrictionChange > 0)
                ApplyRelativeChange(-relativeVelocity * (Math.Min(relativeSpeed, maxFrictionChange) / relativeSpeed));

            first.Part.AssemblyAngularVelocity = [firstVelocity.X, firstVelocity.Y, firstVelocity.Z];
            second.Part.AssemblyAngularVelocity = [secondVelocity.X, secondVelocity.Y, secondVelocity.Z];
        }
    }

    private void ApplyRigidConstraints(Dictionary<string, OrientationOwner> owners, float deltaTime)
    {
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        static float[] Array(Vector3 value) => [value.X, value.Y, value.Z];
        static Vector3 UnitOr(Vector3 value, Vector3 fallback) => value.LengthSquared() > .000001f ? Vector3.Normalize(value) : fallback;
        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type == "RigidConstraint" && node.Enabled))
        {
            if (!owners.TryGetValue(constraint.Attachment0, out var first)
                || !owners.TryGetValue(constraint.Attachment1, out var second)
                || first.Part.Id == second.Part.Id) continue;
            var firstInverseInertia = first.Part.Anchored ? 0 : 1 / PartMomentOfInertia(first.Part);
            var secondInverseInertia = second.Part.Anchored ? 0 : 1 / PartMomentOfInertia(second.Part);
            var inverseInertiaSum = firstInverseInertia + secondInverseInertia;
            if (inverseInertiaSum <= 0) continue;

            var firstFrame = RotationMath.FromDegrees(first.Attachment.Rotation) * RotationMath.FromDegrees(first.Part.Rotation);
            var secondFrame = RotationMath.FromDegrees(second.Attachment.Rotation) * RotationMath.FromDegrees(second.Part.Rotation);
            var relativeFrame = Matrix4x4.Transpose(firstFrame) * secondFrame;
            var errorRotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(relativeFrame));
            if (errorRotation.W < 0) errorRotation = new Quaternion(-errorRotation.X, -errorRotation.Y, -errorRotation.Z, -errorRotation.W);
            var angle = 2 * MathF.Acos(Math.Clamp(errorRotation.W, -1, 1));
            var sinHalfAngle = MathF.Sqrt(Math.Max(0, 1 - errorRotation.W * errorRotation.W));
            if (!float.IsFinite(angle) || angle < .0001f || sinHalfAngle < .0001f) continue;
            var localAxis = new Vector3(errorRotation.X, errorRotation.Y, errorRotation.Z) / sinHalfAngle;
            var worldAxis = UnitOr(Vector3.TransformNormal(localAxis, firstFrame), Vector3.UnitX);
            var firstVelocity = Vector(first.Part.AssemblyAngularVelocity);
            var secondVelocity = Vector(second.Part.AssemblyAngularVelocity);
            var relativeSpeed = Vector3.Dot(secondVelocity - firstVelocity, worldAxis);
            var angularChange = worldAxis * Math.Clamp((-angle * 400 - relativeSpeed * 40) * deltaTime, -20, 20);
            var firstChange = -angularChange * (firstInverseInertia / inverseInertiaSum);
            var secondChange = angularChange * (secondInverseInertia / inverseInertiaSum);
            first.Part.AssemblyAngularVelocity = Array(Vector(first.Part.AssemblyAngularVelocity) + firstChange);
            second.Part.AssemblyAngularVelocity = Array(Vector(second.Part.AssemblyAngularVelocity) + secondChange);
        }
    }

    private void ApplyUniversalConstraints(Dictionary<string, OrientationOwner> owners, float deltaTime)
    {
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        static Vector3 UnitOr(Vector3 value, Vector3 fallback) => value.LengthSquared() > .000001f ? Vector3.Normalize(value) : fallback;
        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type == "UniversalConstraint" && node.Enabled))
        {
            if (!owners.TryGetValue(constraint.Attachment0, out var first)
                || !owners.TryGetValue(constraint.Attachment1, out var second)
                || first.Part.Id == second.Part.Id) continue;
            var firstInverseInertia = first.Part.Anchored ? 0 : 1 / PartMomentOfInertia(first.Part);
            var secondInverseInertia = second.Part.Anchored ? 0 : 1 / PartMomentOfInertia(second.Part);
            var inverseInertiaSum = firstInverseInertia + secondInverseInertia;
            if (inverseInertiaSum <= 0) continue;

            var firstFrame = RotationMath.FromDegrees(first.Attachment.Rotation) * RotationMath.FromDegrees(first.Part.Rotation);
            var secondFrame = RotationMath.FromDegrees(second.Attachment.Rotation) * RotationMath.FromDegrees(second.Part.Rotation);
            var firstPrimary = UnitOr(Vector3.TransformNormal(Vector3.UnitX, firstFrame), Vector3.UnitX);
            var secondPrimary = UnitOr(Vector3.TransformNormal(Vector3.UnitX, secondFrame), firstPrimary);
            var firstSecondary = UnitOr(Vector3.TransformNormal(Vector3.UnitY, firstFrame), Vector3.UnitY);
            var secondSecondary = UnitOr(Vector3.TransformNormal(Vector3.UnitY, secondFrame), firstSecondary);
            var firstVelocity = Vector(first.Part.AssemblyAngularVelocity);
            var secondVelocity = Vector(second.Part.AssemblyAngularVelocity);
            var relativeVelocity = secondVelocity - firstVelocity;

            void ApplyRelativeChange(Vector3 change)
            {
                var firstChange = -change * (firstInverseInertia / inverseInertiaSum);
                var secondChange = change * (secondInverseInertia / inverseInertiaSum);
                firstVelocity += firstChange;
                secondVelocity += secondChange;
                relativeVelocity += secondChange - firstChange;
            }

            var secondaryDot = Math.Clamp(Vector3.Dot(firstSecondary, secondSecondary), -1, 1);
            var secondaryError = MathF.Asin(secondaryDot);
            var secondaryAxis = UnitOr(Vector3.Cross(firstSecondary, secondSecondary),
                UnitOr(Vector3.Cross(firstPrimary, firstSecondary), Vector3.UnitZ));
            var secondarySpeed = Vector3.Dot(relativeVelocity, secondaryAxis);
            var correctionTorque = -secondaryError * 10000 - secondarySpeed * 250;
            ApplyRelativeChange(secondaryAxis * Math.Clamp(correctionTorque * inverseInertiaSum * deltaTime, -20, 20));

            if (constraint.UniversalLimitsEnabled)
            {
                var tilt = MathF.Acos(Math.Clamp(Vector3.Dot(firstPrimary, secondPrimary), -1, 1));
                var maximumTilt = Math.Clamp(constraint.UniversalMaxAngle, 0, 180) * (MathF.PI / 180);
                var limitAxis = UnitOr(Vector3.Cross(firstPrimary, secondPrimary), firstSecondary);
                var outwardSpeed = Vector3.Dot(relativeVelocity, limitAxis);
                var excessTilt = Math.Max(0, tilt - maximumTilt);
                if (excessTilt > .0001f || tilt >= maximumTilt - .0001f && outwardSpeed > 0)
                {
                    var torque = -excessTilt * 40000;
                    if (outwardSpeed > 0)
                        torque -= outwardSpeed * (1 + Math.Clamp(constraint.UniversalRestitution, 0, 1))
                                  / (inverseInertiaSum * Math.Max(deltaTime, .0001f));
                    ApplyRelativeChange(limitAxis * Math.Clamp(torque * inverseInertiaSum * deltaTime, -20, 20));
                }
            }

            first.Part.AssemblyAngularVelocity = [firstVelocity.X, firstVelocity.Y, firstVelocity.Z];
            second.Part.AssemblyAngularVelocity = [secondVelocity.X, secondVelocity.Y, secondVelocity.Z];
        }
    }

    private void ApplyTorsionSpringConstraints(Dictionary<string, OrientationOwner> owners, float deltaTime)
    {
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        static Vector3 UnitOr(Vector3 value, Vector3 fallback) => value.LengthSquared() > .000001f ? Vector3.Normalize(value) : fallback;
        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type == "TorsionSpringConstraint"))
        {
            constraint.TorsionSpringCurrentAngle = 0;
            if (!owners.TryGetValue(constraint.Attachment0, out var first)
                || !owners.TryGetValue(constraint.Attachment1, out var second)
                || first.Part.Id == second.Part.Id) continue;
            var firstInverseInertia = first.Part.Anchored ? 0 : 1 / PartMomentOfInertia(first.Part);
            var secondInverseInertia = second.Part.Anchored ? 0 : 1 / PartMomentOfInertia(second.Part);
            var inverseInertiaSum = firstInverseInertia + secondInverseInertia;
            if (inverseInertiaSum <= 0) continue;
            var firstFrame = RotationMath.FromDegrees(first.Attachment.Rotation) * RotationMath.FromDegrees(first.Part.Rotation);
            var secondFrame = RotationMath.FromDegrees(second.Attachment.Rotation) * RotationMath.FromDegrees(second.Part.Rotation);
            var firstAxis = UnitOr(Vector3.TransformNormal(Vector3.UnitY, firstFrame), Vector3.UnitY);
            var secondAxis = UnitOr(Vector3.TransformNormal(Vector3.UnitY, secondFrame), firstAxis);
            var dot = Math.Clamp(Vector3.Dot(firstAxis, secondAxis), -1, 1);
            var angle = MathF.Acos(dot);
            constraint.TorsionSpringCurrentAngle = angle * (180 / MathF.PI);
            if (!constraint.Enabled || angle < .0001f) continue;
            var torqueAxis = Vector3.Cross(firstAxis, secondAxis);
            if (torqueAxis.LengthSquared() < .000001f)
                torqueAxis = Vector3.Cross(firstAxis, MathF.Abs(Vector3.Dot(firstAxis, Vector3.UnitX)) < .9f ? Vector3.UnitX : Vector3.UnitZ);
            torqueAxis = UnitOr(torqueAxis, Vector3.UnitZ);
            var relativeVelocity = Vector(second.Part.AssemblyAngularVelocity) - Vector(first.Part.AssemblyAngularVelocity);
            var angularSpeed = Vector3.Dot(relativeVelocity, torqueAxis);
            var stiffness = Math.Clamp(constraint.TorsionSpringStiffness, 0, 100000);
            var damping = Math.Clamp(constraint.TorsionSpringDamping, 0, 100000);
            var torque = -angle * stiffness - angularSpeed * damping;
            if (constraint.TorsionSpringLimitsEnabled)
            {
                var maxAngle = Math.Clamp(constraint.TorsionSpringMaxAngle, 0, 180) * (MathF.PI / 180);
                var excess = angle - maxAngle;
                if (excess > 0)
                {
                    torque -= excess * stiffness * 4;
                    if (angularSpeed > 0)
                        torque -= angularSpeed * (1 + Math.Clamp(constraint.TorsionSpringRestitution, 0, 1))
                                  / (inverseInertiaSum * Math.Max(deltaTime, .0001f));
                }
            }
            torque = Math.Clamp(torque, -Math.Clamp(constraint.TorsionSpringMaxTorque, 0, 1000000),
                Math.Clamp(constraint.TorsionSpringMaxTorque, 0, 1000000));
            var relativeChange = torqueAxis * (torque * inverseInertiaSum * deltaTime);
            var firstVelocity = Vector(first.Part.AssemblyAngularVelocity) - relativeChange * (firstInverseInertia / inverseInertiaSum);
            var secondVelocity = Vector(second.Part.AssemblyAngularVelocity) + relativeChange * (secondInverseInertia / inverseInertiaSum);
            first.Part.AssemblyAngularVelocity = [firstVelocity.X, firstVelocity.Y, firstVelocity.Z];
            second.Part.AssemblyAngularVelocity = [secondVelocity.X, secondVelocity.Y, secondVelocity.Z];
        }
    }

    private void ApplyHingeConstraints(Dictionary<string, OrientationOwner> owners, float deltaTime)
    {
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        static Vector3 UnitOr(Vector3 value, Vector3 fallback) => value.LengthSquared() > .000001f ? Vector3.Normalize(value) : fallback;
        static float WrapRadians(float value)
        {
            value %= MathF.Tau;
            if (value > MathF.PI) value -= MathF.Tau;
            if (value < -MathF.PI) value += MathF.Tau;
            return value;
        }

        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => (node.Type is "HingeConstraint" or "CylindricalConstraint") && node.Enabled))
        {
            if (!owners.TryGetValue(constraint.Attachment0, out var first)
                || !owners.TryGetValue(constraint.Attachment1, out var second)
                || first.Part.Id == second.Part.Id) continue;
            var firstInverseInertia = first.Part.Anchored ? 0 : 1 / PartMomentOfInertia(first.Part);
            var secondInverseInertia = second.Part.Anchored ? 0 : 1 / PartMomentOfInertia(second.Part);
            var inverseInertiaSum = firstInverseInertia + secondInverseInertia;
            if (inverseInertiaSum <= 0) continue;
            var firstFrame = RotationMath.FromDegrees(first.Attachment.Rotation) * RotationMath.FromDegrees(first.Part.Rotation);
            var secondFrame = RotationMath.FromDegrees(second.Attachment.Rotation) * RotationMath.FromDegrees(second.Part.Rotation);
            var inclination = constraint.Type == "CylindricalConstraint"
                ? constraint.CylindricalInclinationAngle * (MathF.PI / 180)
                : 0;
            var rotationPlane = Matrix4x4.CreateRotationZ(inclination);
            var firstRotationFrame = firstFrame * rotationPlane;
            var secondRotationFrame = secondFrame * rotationPlane;
            var firstAxis = UnitOr(Vector3.TransformNormal(Vector3.UnitX, firstRotationFrame), Vector3.UnitX);
            var secondAxis = UnitOr(Vector3.TransformNormal(Vector3.UnitX, secondRotationFrame), firstAxis);
            var firstVelocity = Vector(first.Part.AssemblyAngularVelocity);
            var secondVelocity = Vector(second.Part.AssemblyAngularVelocity);
            var relativeVelocity = secondVelocity - firstVelocity;

            void ApplyRelativeChange(Vector3 change)
            {
                var firstChange = -change * (firstInverseInertia / inverseInertiaSum);
                var secondChange = change * (secondInverseInertia / inverseInertiaSum);
                firstVelocity += firstChange;
                secondVelocity += secondChange;
                relativeVelocity += secondChange - firstChange;
            }

            var swingErrorAxis = Vector3.Cross(firstAxis, secondAxis);
            if (swingErrorAxis.LengthSquared() < .000001f && Vector3.Dot(firstAxis, secondAxis) < 0)
                swingErrorAxis = Vector3.Cross(firstAxis, Vector3.TransformNormal(Vector3.UnitY, firstRotationFrame));
            var swingError = MathF.Acos(Math.Clamp(Vector3.Dot(firstAxis, secondAxis), -1, 1));
            var relativeSwingVelocity = relativeVelocity - firstAxis * Vector3.Dot(relativeVelocity, firstAxis);
            var desiredSwingVelocity = Vector3.Zero;
            if (swingErrorAxis.LengthSquared() > .000001f)
            {
                var swingAxis = Vector3.Normalize(swingErrorAxis);
                desiredSwingVelocity = swingAxis * -Math.Min(8, swingError * 20);
            }
            ApplyRelativeChange(desiredSwingVelocity - relativeSwingVelocity);

            var relativeFrame = Matrix4x4.Transpose(firstRotationFrame) * secondRotationFrame;
            var relativeRotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(relativeFrame));
            if (relativeRotation.W < 0)
                relativeRotation = new Quaternion(-relativeRotation.X, -relativeRotation.Y, -relativeRotation.Z, -relativeRotation.W);
            var twist = new Quaternion(relativeRotation.X, 0, 0, relativeRotation.W);
            var angle = 0f;
            if (twist.LengthSquared() > .000001f)
            {
                twist = Quaternion.Normalize(twist);
                angle = WrapRadians(2 * MathF.Atan2(twist.X, twist.W));
            }
            constraint.HingeCurrentAngle = angle * (180 / MathF.PI);

            var hingeSpeed = Vector3.Dot(relativeVelocity, firstAxis);
            if (constraint.HingeActuatorType == "Motor")
            {
                var accelerationChange = Math.Clamp(constraint.HingeAngularVelocity - hingeSpeed,
                    -Math.Clamp(constraint.HingeMotorMaxAcceleration, 0, 100000) * deltaTime,
                    Math.Clamp(constraint.HingeMotorMaxAcceleration, 0, 100000) * deltaTime);
                var maxChange = Math.Clamp(constraint.HingeMotorMaxTorque, 0, 1000000) / Math.Max(.0001f, 1 / inverseInertiaSum) * deltaTime;
                ApplyRelativeChange(firstAxis * Math.Clamp(accelerationChange, -maxChange, maxChange));
            }
            else if (constraint.HingeActuatorType == "Servo")
            {
                var targetAngle = constraint.HingeLimitsEnabled
                    ? Math.Clamp(constraint.HingeTargetAngle, constraint.HingeLowerAngle, constraint.HingeUpperAngle)
                    : constraint.HingeTargetAngle;
                var angleError = WrapRadians((targetAngle - constraint.HingeCurrentAngle) * (MathF.PI / 180));
                var targetSpeed = Math.Clamp(angleError * Math.Clamp(constraint.HingeAngularResponsiveness, 0, 200),
                    -Math.Clamp(constraint.HingeAngularSpeed, 0, 1000), Math.Clamp(constraint.HingeAngularSpeed, 0, 1000));
                var maxChange = Math.Clamp(constraint.HingeServoMaxTorque, 0, 1000000) / Math.Max(.0001f, 1 / inverseInertiaSum) * deltaTime;
                ApplyRelativeChange(firstAxis * Math.Clamp(targetSpeed - hingeSpeed, -maxChange, maxChange));
            }

            if (constraint.HingeLimitsEnabled)
            {
                var lower = Math.Clamp(constraint.HingeLowerAngle, -180, 180) * (MathF.PI / 180);
                var upper = Math.Clamp(constraint.HingeUpperAngle, constraint.HingeLowerAngle, 180) * (MathF.PI / 180);
                var excess = angle - Math.Clamp(angle, lower, upper);
                var outwardSpeed = Vector3.Dot(relativeVelocity, firstAxis) * MathF.Sign(angle == 0 ? 1 : angle);
                if (MathF.Abs(excess) > .0001f || angle <= lower + .0001f || angle >= upper - .0001f)
                {
                    var targetSpeed = MathF.Abs(excess) > .0001f
                        ? -Math.Min(8, MathF.Abs(excess) * 20)
                        : outwardSpeed > 0 ? -outwardSpeed * Math.Clamp(constraint.HingeRestitution, 0, 1) : 0;
                    var signedAxis = firstAxis * MathF.Sign(angle == 0 ? 1 : angle);
                    ApplyRelativeChange(signedAxis * Math.Clamp(targetSpeed - outwardSpeed, -20, 20));
                }
            }

            first.Part.AssemblyAngularVelocity = [firstVelocity.X, firstVelocity.Y, firstVelocity.Z];
            second.Part.AssemblyAngularVelocity = [secondVelocity.X, secondVelocity.Y, secondVelocity.Z];
        }
    }

    private void ApplyPrismaticOrientationLocks(Dictionary<string, OrientationOwner> owners)
    {
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type == "PrismaticConstraint" && node.Enabled))
        {
            if (!owners.TryGetValue(constraint.Attachment0, out var first)
                || !owners.TryGetValue(constraint.Attachment1, out var second)
                || first.Part.Id == second.Part.Id) continue;
            var firstInverseInertia = first.Part.Anchored ? 0 : 1 / PartMomentOfInertia(first.Part);
            var secondInverseInertia = second.Part.Anchored ? 0 : 1 / PartMomentOfInertia(second.Part);
            var inverseInertiaSum = firstInverseInertia + secondInverseInertia;
            if (inverseInertiaSum <= 0) continue;
            var firstFrame = RotationMath.FromDegrees(first.Attachment.Rotation) * RotationMath.FromDegrees(first.Part.Rotation);
            var secondFrame = RotationMath.FromDegrees(second.Attachment.Rotation) * RotationMath.FromDegrees(second.Part.Rotation);
            var relativeFrame = Matrix4x4.Transpose(firstFrame) * secondFrame;
            var relativeRotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(relativeFrame));
            if (relativeRotation.W < 0)
                relativeRotation = new Quaternion(-relativeRotation.X, -relativeRotation.Y, -relativeRotation.Z, -relativeRotation.W);
            var angle = 2 * MathF.Acos(Math.Clamp(relativeRotation.W, -1, 1));
            var halfSine = MathF.Sqrt(Math.Max(0, 1 - relativeRotation.W * relativeRotation.W));
            if (angle < .001f || halfSine < .0001f) continue;
            var localAxis = new Vector3(relativeRotation.X, relativeRotation.Y, relativeRotation.Z) / halfSine;
            var worldAxis = Vector3.Normalize(Vector3.TransformNormal(localAxis, firstFrame));
            var firstVelocity = Vector(first.Part.AssemblyAngularVelocity);
            var secondVelocity = Vector(second.Part.AssemblyAngularVelocity);
            var relativeVelocity = secondVelocity - firstVelocity;
            var desiredRelativeVelocity = -worldAxis * Math.Min(8, angle * 20);
            var change = desiredRelativeVelocity - relativeVelocity;
            firstVelocity -= change * (firstInverseInertia / inverseInertiaSum);
            secondVelocity += change * (secondInverseInertia / inverseInertiaSum);
            first.Part.AssemblyAngularVelocity = [firstVelocity.X, firstVelocity.Y, firstVelocity.Z];
            second.Part.AssemblyAngularVelocity = [secondVelocity.X, secondVelocity.Y, secondVelocity.Z];
        }
    }

    private void ApplyRodAngularLimits(Dictionary<string, OrientationOwner> owners, float deltaTime)
    {
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        static Vector3 UnitOr(Vector3 value, Vector3 fallback) => value.LengthSquared() > .000001f ? Vector3.Normalize(value) : fallback;
        static Vector3 WorldPosition(OrientationOwner owner) =>
            Vector(owner.Part.Position) + Vector3.Transform(Vector(owner.Attachment.Position), RotationMath.FromDegrees(owner.Part.Rotation));
        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type == "RodConstraint" && node.Enabled && node.RodLimitsEnabled))
        {
            if (!owners.TryGetValue(constraint.Attachment0, out var first)
                || !owners.TryGetValue(constraint.Attachment1, out var second)
                || first.Part.Id == second.Part.Id) continue;
            var rodDirection = UnitOr(WorldPosition(second) - WorldPosition(first), Vector3.UnitX);
            ApplyLimit(first, rodDirection, constraint.RodLimitAngle0);
            ApplyLimit(second, -rodDirection, constraint.RodLimitAngle1);

            void ApplyLimit(OrientationOwner owner, Vector3 targetDirection, float maximumAngle)
            {
                if (owner.Part.Anchored) return;
                var frame = RotationMath.FromDegrees(owner.Attachment.Rotation) * RotationMath.FromDegrees(owner.Part.Rotation);
                var axis = UnitOr(Vector3.TransformNormal(Vector3.UnitX, frame), Vector3.UnitX);
                var angle = MathF.Acos(Math.Clamp(Vector3.Dot(axis, targetDirection), -1, 1));
                var excess = angle - Math.Clamp(maximumAngle, 0, 180) * (MathF.PI / 180);
                if (excess <= .001f) return;
                var correctionAxis = Vector3.Cross(axis, targetDirection);
                if (correctionAxis.LengthSquared() < .000001f)
                    correctionAxis = Vector3.Cross(axis, MathF.Abs(Vector3.Dot(axis, Vector3.UnitY)) < .9f ? Vector3.UnitY : Vector3.UnitX);
                correctionAxis = UnitOr(correctionAxis, Vector3.UnitZ);
                var current = Vector(owner.Part.AssemblyAngularVelocity);
                var speed = Math.Clamp(excess * 20, 0, 8);
                var maxChange = Math.Max(1, 50 * deltaTime);
                var speedChange = Math.Clamp(speed - Vector3.Dot(current, correctionAxis), -maxChange, maxChange);
                current += correctionAxis * speedChange;
                owner.Part.AssemblyAngularVelocity = [current.X, current.Y, current.Z];
            }
        }
    }

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

        ApplyBallSocketAngularLimits(owners, deltaTime);
        ApplyUniversalConstraints(owners, deltaTime);
        ApplyRigidConstraints(owners, deltaTime);
        ApplyTorsionSpringConstraints(owners, deltaTime);
        ApplyHingeConstraints(owners, deltaTime);
        ApplyPrismaticOrientationLocks(owners);
        ApplyRodAngularLimits(owners, deltaTime);

    }

    private void ApplyAngularMotion(SceneNode[] parts, Dictionary<string, SceneNode[]> weldedGroups,
        HashSet<(string, string)> ignoredPairs, float deltaTime)
    {
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        static float[] Array(Vector3 value) => [value.X, value.Y, value.Z];
        var handledAssemblies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            if (!weldedGroups.TryGetValue(part.Id, out var group))
            {
                ApplySingleAngularMotion(part, parts, ignoredPairs, deltaTime);
                continue;
            }

            var assemblyKey = AssemblyKey(group);
            if (!handledAssemblies.Add(assemblyKey)) continue;
            if (group.Any(member => member.Anchored))
            {
                foreach (var member in group) member.AssemblyAngularVelocity = [0, 0, 0];
                continue;
            }

            var centerOfMass = group.Aggregate(Vector3.Zero,
                (sum, member) => sum + Vector(member.Position) * PartMass(member)) / group.Sum(PartMass);
            var totalInertia = 0f;
            var angularMomentum = Vector3.Zero;
            foreach (var member in group)
            {
                var mass = PartMass(member);
                var offset = Vector(member.Position) - centerOfMass;
                var inertia = PartMomentOfInertia(member) + mass * offset.LengthSquared();
                var angularVelocity = Vector(member.AssemblyAngularVelocity);
                if (!float.IsFinite(angularVelocity.X) || !float.IsFinite(angularVelocity.Y) || !float.IsFinite(angularVelocity.Z))
                    angularVelocity = Vector3.Zero;
                angularMomentum += angularVelocity * inertia;
                totalInertia += inertia;
            }
            var assemblyAngularVelocity = totalInertia > .000001f ? angularMomentum / totalInertia : Vector3.Zero;
            foreach (var member in group) member.AssemblyAngularVelocity = Array(assemblyAngularVelocity);
            ApplyRigidAssemblyRotation(group, centerOfMass, assemblyAngularVelocity, deltaTime, parts, ignoredPairs);
        }
    }

    private void ApplySingleAngularMotion(SceneNode part, SceneNode[] worldParts,
        HashSet<(string, string)> ignoredPairs, float deltaTime)
    {
        if (part.Anchored)
        {
            part.AssemblyAngularVelocity = [0, 0, 0];
            return;
        }
        var angularVelocity = new Vector3(part.AssemblyAngularVelocity[0], part.AssemblyAngularVelocity[1], part.AssemblyAngularVelocity[2]);
        if (!float.IsFinite(angularVelocity.X) || !float.IsFinite(angularVelocity.Y) || !float.IsFinite(angularVelocity.Z))
        {
            part.AssemblyAngularVelocity = [0, 0, 0];
            return;
        }
        var center = new Vector3(part.Position[0], part.Position[1], part.Position[2]);
        ApplyRigidAssemblyRotation([part], center, angularVelocity, deltaTime, worldParts, ignoredPairs);
    }

    private void ApplyRigidAssemblyRotation(SceneNode[] group, Vector3 centerOfMass, Vector3 angularVelocity, float deltaTime,
        SceneNode[] worldParts, HashSet<(string, string)> ignoredPairs)
    {
        var angle = angularVelocity.Length() * deltaTime;
        if (!float.IsFinite(angle) || angle < .000001f) return;
        angle = Math.Min(angle, MathF.Tau);
        var axis = Vector3.Normalize(angularVelocity);
        var origins = group.ToDictionary(part => part.Id,
            part => (Position: (float[])part.Position.Clone(), Rotation: (float[])part.Rotation.Clone()), StringComparer.Ordinal);
        var groupIds = group.Select(part => part.Id).ToHashSet(StringComparer.Ordinal);
        var collisionPairs = group.Where(part => part.CanCollide)
            .SelectMany(part => worldParts.Where(other => !groupIds.Contains(other.Id) && other.CanCollide
                    && !ignoredPairs.Contains(CollisionPair(part.Id, other.Id)))
                .Select(other => (Member: part, Other: other)))
            .ToArray();
        var maximumSweep = group.Max(part => (Vector3.Distance(new(part.Position[0], part.Position[1], part.Position[2]), centerOfMass)
            + new Vector3(part.Size[0], part.Size[1], part.Size[2]).Length() * .5f) * angle);
        var sampleCount = collisionPairs.Length == 0
            ? 1
            : Math.Clamp((int)MathF.Ceiling(Math.Max(angle / (MathF.PI / 90), maximumSweep / .2f)), 1, 512);
        var previousOverlaps = new HashSet<(string, string)>();
        var safeFraction = 0f;
        for (var sample = 0; sample <= sampleCount; sample++)
        {
            var fraction = (float)sample / sampleCount;
            var rotationDelta = Matrix4x4.CreateFromAxisAngle(axis, angle * fraction);
            foreach (var part in group)
            {
                var origin = origins[part.Id];
                var position = new Vector3(origin.Position[0], origin.Position[1], origin.Position[2]);
                var rotatedPosition = centerOfMass + Vector3.TransformNormal(position - centerOfMass, rotationDelta);
                part.Position = [rotatedPosition.X, rotatedPosition.Y, rotatedPosition.Z];
                part.Rotation = RotationMath.ToDegrees(RotationMath.FromDegrees(origin.Rotation) * rotationDelta);
            }

            var overlaps = new HashSet<(string, string)>();
            foreach (var (member, other) in collisionPairs)
            {
                if (!CollisionGeometry.Intersects(member, other)) continue;
                var pair = CollisionPair(member.Id, other.Id);
                if (sample > 0 && !previousOverlaps.Contains(pair))
                {
                    foreach (var part in group)
                    {
                        part.AssemblyAngularVelocity = [0, 0, 0];
                        var origin = origins[part.Id];
                        part.Position = (float[])origin.Position.Clone();
                        part.Rotation = (float[])origin.Rotation.Clone();
                    }
                    var safeDelta = Matrix4x4.CreateFromAxisAngle(axis, angle * safeFraction);
                    foreach (var part in group)
                    {
                        var origin = origins[part.Id];
                        var position = new Vector3(origin.Position[0], origin.Position[1], origin.Position[2]);
                        var safePosition = centerOfMass + Vector3.TransformNormal(position - centerOfMass, safeDelta);
                        part.Position = [safePosition.X, safePosition.Y, safePosition.Z];
                        part.Rotation = RotationMath.ToDegrees(RotationMath.FromDegrees(origin.Rotation) * safeDelta);
                        moved(part);
                    }
                    return;
                }
                overlaps.Add(pair);
            }
            previousOverlaps = overlaps;
            safeFraction = fraction;
        }

        foreach (var part in group)
        {
            moved(part);
        }
    }

    private void ApplyTorqueTargets(SceneNode[] parts, Dictionary<string, OrientationOwner> owners, float deltaTime)
    {
        static Vector3 Vector(float[] values) => new(values[0], values[1], values[2]);
        foreach (var constraint in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type == "Torque" && node.Enabled))
        {
            if (!owners.TryGetValue(constraint.Attachment0, out var owner) || owner.Part.Anchored) continue;
            var torque = Vector(constraint.TorqueTarget);
            if (constraint.RelativeTo == "Attachment0")
                torque = Vector3.TransformNormal(torque, RotationMath.FromDegrees(owner.Attachment.Rotation) * RotationMath.FromDegrees(owner.Part.Rotation));
            else if (constraint.RelativeTo == "Attachment1")
            {
                if (!owners.TryGetValue(constraint.Attachment1, out var relativeAttachment)) continue;
                torque = Vector3.TransformNormal(torque, RotationMath.FromDegrees(relativeAttachment.Attachment.Rotation) * RotationMath.FromDegrees(relativeAttachment.Part.Rotation));
            }
            var angularVelocity = Vector(owner.Part.AssemblyAngularVelocity) + torque / PartMomentOfInertia(owner.Part) * deltaTime;
            if (!float.IsFinite(angularVelocity.X) || !float.IsFinite(angularVelocity.Y) || !float.IsFinite(angularVelocity.Z)) continue;
            owner.Part.AssemblyAngularVelocity = [Math.Clamp(angularVelocity.X, -10000, 10000), Math.Clamp(angularVelocity.Y, -10000, 10000), Math.Clamp(angularVelocity.Z, -10000, 10000)];
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
        Dictionary<string, List<VelocityTarget>> targets, float mass, float deltaTime,
        Dictionary<string, SceneNode[]> weldedGroups)
    {
        var current = new Vector3(velocity[0], velocity[1], velocity[2]);
        foreach (var member in members)
        {
            if (!targets.TryGetValue(member.Id, out var constraints)) continue;
            foreach (var target in constraints)
            {
                var reference = target.ReferencePart;
                var referenceMembers = reference is null ? [] : weldedGroups.GetValueOrDefault(reference.Id) ?? [reference];
                var referenceVelocity = reference is null ? Vector3.Zero
                    : new Vector3(reference.Velocity[0], reference.Velocity[1], reference.Velocity[2]);
                var requested = TargetVelocityDelta(target, current - referenceVelocity);
                var reactionEnabled = reference is not null && target.Constraint.LinearVelocityReactionForceEnabled
                    && !referenceMembers.Any(part => part.Anchored);
                var sameAssembly = reactionEnabled && referenceMembers.Any(part => part.Id == member.Id
                    || weldedGroups.GetValueOrDefault(member.Id)?.Any(ownerPart => ownerPart.Id == part.Id) == true);
                if (sameAssembly) continue;
                var referenceMass = reactionEnabled && !sameAssembly ? referenceMembers.Sum(PartMass) : 0;
                var effectiveMass = referenceMass > 0 ? mass * referenceMass / (mass + referenceMass) : mass;
                if (target.Constraint.ForceLimitsEnabled)
                {
                    var constraint = target.Constraint;
                    var forceLimitMode = constraint.ForceLimitMode;
                    if (forceLimitMode == "PerAxis" && constraint.VelocityConstraintMode == "Vector")
                    {
                        var localDelta = Vector3.TransformNormal(requested, Matrix4x4.Transpose(target.Orientation));
                        var limits = constraint.MaxAxesForce;
                        localDelta = new Vector3(
                            ClampVelocityChange(localDelta.X, limits[0], effectiveMass, deltaTime),
                            ClampVelocityChange(localDelta.Y, limits[1], effectiveMass, deltaTime),
                            ClampVelocityChange(localDelta.Z, limits[2], effectiveMass, deltaTime));
                        requested = Vector3.TransformNormal(localDelta, target.Orientation);
                    }
                    else if (forceLimitMode == "PerAxis" && constraint.VelocityConstraintMode == "Plane")
                    {
                        static Vector3 UnitOr(Vector3 value, Vector3 fallback) => value.LengthSquared() > .000001f ? Vector3.Normalize(value) : fallback;
                        var primary = UnitOr(Vector3.TransformNormal(new Vector3(constraint.PrimaryTangentAxis[0], constraint.PrimaryTangentAxis[1], constraint.PrimaryTangentAxis[2]), target.Orientation), Vector3.UnitX);
                        var secondary = Vector3.TransformNormal(new Vector3(constraint.SecondaryTangentAxis[0], constraint.SecondaryTangentAxis[1], constraint.SecondaryTangentAxis[2]), target.Orientation);
                        secondary = UnitOr(secondary - primary * Vector3.Dot(secondary, primary), Vector3.UnitZ);
                        var limits = constraint.MaxPlanarAxesForce;
                        var limitedPrimary = ClampVelocityChange(Vector3.Dot(requested, primary), limits[0], effectiveMass, deltaTime);
                        var limitedSecondary = ClampVelocityChange(Vector3.Dot(requested, secondary), limits[1], effectiveMass, deltaTime);
                        requested = primary * limitedPrimary + secondary * limitedSecondary;
                    }
                    else
                    {
                        var maximumChange = constraint.MaxForce / effectiveMass * deltaTime;
                        if (requested.LengthSquared() > maximumChange * maximumChange && maximumChange >= 0)
                            requested = Vector3.Normalize(requested) * maximumChange;
                    }
                }
                if (referenceMass > 0)
                {
                    current += requested * (referenceMass / (mass + referenceMass));
                    var referenceDelta = -requested * (mass / (mass + referenceMass));
                    foreach (var referenceMember in referenceMembers)
                    {
                        var referenceMemberVelocity = new Vector3(referenceMember.Velocity[0], referenceMember.Velocity[1], referenceMember.Velocity[2]) + referenceDelta;
                        if (!float.IsFinite(referenceMemberVelocity.X) || !float.IsFinite(referenceMemberVelocity.Y) || !float.IsFinite(referenceMemberVelocity.Z)) continue;
                        referenceMember.Velocity = [referenceMemberVelocity.X, referenceMemberVelocity.Y, referenceMemberVelocity.Z];
                    }
                }
                else current += requested;
            }
        }
        velocity[0] = current.X; velocity[1] = current.Y; velocity[2] = current.Z;
    }

    private static float ClampVelocityChange(float requested, float maximumForce, float mass, float deltaTime)
    {
        var maximumChange = Math.Max(0, maximumForce) / mass * deltaTime;
        return Math.Clamp(requested, -maximumChange, maximumChange);
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
                if (ReferenceEquals(group[0], part)) SimulateWeldedGroup(group, parts, weldedGroups, appliedForces, velocityTargets, ignoredPairs, deltaTime);
                continue;
            }
            SimulateSingle(part, parts, weldedGroups, appliedForces, velocityTargets, ignoredPairs, deltaTime);
        }
    }

    private void SimulateWeldedGroup(SceneNode[] group, SceneNode[] parts, Dictionary<string, SceneNode[]> weldedGroups,
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
        ApplyVelocityTargets(velocity, group, velocityTargets, groupMass, deltaTime, weldedGroups);
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

    private void ApplyMotorTransforms(SceneNode[] parts, Dictionary<string, SceneNode[]> weldedGroups)
    {
        static Vector3 Vector(float[] value) => new(value[0], value[1], value[2]);
        var partById = parts.ToDictionary(part => part.Id, StringComparer.Ordinal);
        foreach (var motor in GameProjectStore.Flatten(project.Workspace.Children)
                     .Where(node => node.Type is "Weld" or "Motor6D"))
        {
            if (!motor.Enabled) continue;
            if (!partById.TryGetValue(motor.Part0, out var part0) || !partById.TryGetValue(motor.Part1, out var part1) || ReferenceEquals(part0, part1))
                continue;
            var part0OffsetRotation = RotationMath.FromDegrees(motor.Part0OffsetRotation);
            var part1OffsetRotation = RotationMath.FromDegrees(motor.Part1OffsetRotation);
            var transformRotation = RotationMath.FromDegrees(motor.MotorTransformRotation);
            var transformOffset = new Vector3(motor.MotorTransformPosition[0], motor.MotorTransformPosition[1], motor.MotorTransformPosition[2]);
            SceneNode drivenPart;
            Vector3 targetPosition;
            Matrix4x4 targetRotation;
            if (part1.Anchored)
            {
                if (part0.Anchored
                    || !Matrix4x4.Invert(part0OffsetRotation, out var inversePart0OffsetRotation)
                    || !Matrix4x4.Invert(transformRotation, out var inverseTransformRotation)) continue;
                drivenPart = part0;
                targetRotation = inversePart0OffsetRotation * inverseTransformRotation * part1OffsetRotation * RotationMath.FromDegrees(part1.Rotation);
                var part1JointPosition = new Vector3(part1.Position[0], part1.Position[1], part1.Position[2])
                    + Vector3.Transform(Vector(motor.Part1OffsetPosition), RotationMath.FromDegrees(part1.Rotation));
                var part0JointOffset = Vector3.Transform(Vector(motor.Part0OffsetPosition), targetRotation);
                var transformWorldOffset = Vector3.Transform(transformOffset, part0OffsetRotation * targetRotation);
                targetPosition = part1JointPosition - part0JointOffset - transformWorldOffset;
            }
            else
            {
                if (!Matrix4x4.Invert(part1OffsetRotation, out var inversePart1OffsetRotation)) continue;
                drivenPart = part1;
                var parentRotation = RotationMath.FromDegrees(part0.Rotation);
                targetRotation = inversePart1OffsetRotation * transformRotation * part0OffsetRotation * parentRotation;
                var part0JointOffset = Vector3.Transform(Vector(motor.Part0OffsetPosition), parentRotation);
                var jointPosition = new Vector3(part0.Position[0], part0.Position[1], part0.Position[2]) + part0JointOffset;
                jointPosition += Vector3.Transform(transformOffset, part0OffsetRotation * parentRotation);
                var part1JointOffset = Vector3.Transform(Vector(motor.Part1OffsetPosition), targetRotation);
                targetPosition = jointPosition - part1JointOffset;
            }
            var targetPositionArray = new[] { targetPosition.X, targetPosition.Y, targetPosition.Z };
            var targetRotationArray = RotationMath.ToDegrees(targetRotation);
            static bool Differs(float[] current, float[] desired) =>
                current.Length < 3 || Enumerable.Range(0, 3).Any(axis => Math.Abs(current[axis] - desired[axis]) > .0001f);
            if (!Differs(drivenPart.Position, targetPositionArray) && !Differs(drivenPart.Rotation, targetRotationArray)) continue;
            drivenPart.Position = targetPositionArray;
            drivenPart.Rotation = targetRotationArray;
            moved(drivenPart);
        }

        foreach (var group in weldedGroups.Values.Distinct())
            RememberWeldedTransforms(group, AssemblyKey(group));
    }

    private void SimulateSingle(SceneNode part, SceneNode[] parts, Dictionary<string, SceneNode[]> weldedGroups,
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
        ApplyVelocityTargets(velocity, [part], velocityTargets, PartMass(part), deltaTime, weldedGroups);
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
