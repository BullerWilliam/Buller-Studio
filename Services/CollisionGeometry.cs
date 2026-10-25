using System.Numerics;
using BullerStudio.Models;

namespace BullerStudio.Services;

internal static class CollisionGeometry
{
    private const float AxisEpsilon = 1e-8f;

    internal readonly record struct RayHit(Vector3 Position, Vector3 Normal, float Distance);

    public static RayHit? Raycast(SceneNode part, Vector3 origin, Vector3 direction)
    {
        if (TryGetSphere(part, out var sphereCenter, out var sphereRadius))
        {
            var sphereOffset = origin - sphereCenter;
            var a = Vector3.Dot(direction, direction);
            if (a < AxisEpsilon) return null;
            var b = 2 * Vector3.Dot(sphereOffset, direction);
            var c = Vector3.Dot(sphereOffset, sphereOffset) - sphereRadius * sphereRadius;
            var discriminant = b * b - 4 * a * c;
            if (discriminant < 0) return null;
            var root = MathF.Sqrt(discriminant);
            var sphereTime = (-b - root) / (2 * a);
            if (sphereTime < 0) sphereTime = (-b + root) / (2 * a);
            if (sphereTime < 0 || sphereTime > 1 || !float.IsFinite(sphereTime)) return null;
            var position = origin + direction * sphereTime;
            var normal = Vector3.Normalize(position - sphereCenter);
            return new RayHit(position, normal, direction.Length() * sphereTime);
        }

        if (TryGetEllipsoid(part, out var ellipsoidCenter, out var ellipsoidRadii, out var ellipsoidRotation))
        {
            var inverseRotation = Matrix4x4.Transpose(ellipsoidRotation);
            var localOrigin = Vector3.TransformNormal(origin - ellipsoidCenter, inverseRotation) / ellipsoidRadii;
            var localDirection = Vector3.TransformNormal(direction, inverseRotation) / ellipsoidRadii;
            var a = Vector3.Dot(localDirection, localDirection);
            if (a < AxisEpsilon) return null;
            var b = 2 * Vector3.Dot(localOrigin, localDirection);
            var c = Vector3.Dot(localOrigin, localOrigin) - 1;
            var discriminant = b * b - 4 * a * c;
            if (discriminant < 0) return null;
            var root = MathF.Sqrt(discriminant);
            var ellipsoidTime = (-b - root) / (2 * a);
            if (ellipsoidTime < 0) ellipsoidTime = (-b + root) / (2 * a);
            if (ellipsoidTime < 0 || ellipsoidTime > 1 || !float.IsFinite(ellipsoidTime)) return null;
            var position = origin + direction * ellipsoidTime;
            var localHit = localOrigin + localDirection * ellipsoidTime;
            var localNormal = localHit / ellipsoidRadii;
            var normal = Vector3.Normalize(Vector3.TransformNormal(localNormal, inverseRotation));
            return new RayHit(position, normal, direction.Length() * ellipsoidTime);
        }

        if (TryGetCylinder(part, out var cylinderCenter, out var cylinderRadius, out var cylinderHalfHeight, out var cylinderRotation))
        {
            var inverseRotation = Matrix4x4.Transpose(cylinderRotation);
            var localOrigin = Vector3.TransformNormal(origin - cylinderCenter, inverseRotation);
            var localDirection = Vector3.TransformNormal(direction, inverseRotation);
            var bestTime = float.PositiveInfinity;
            var bestNormal = Vector3.Zero;
            void Consider(float candidateTime, Vector3 localNormal)
            {
                if (candidateTime < 0 || candidateTime > 1 || candidateTime >= bestTime) return;
                bestTime = candidateTime;
                bestNormal = Vector3.TransformNormal(localNormal, cylinderRotation);
            }

            var radialRate = localDirection.X * localDirection.X + localDirection.Z * localDirection.Z;
            if (radialRate > AxisEpsilon)
            {
                var radialOffset = localOrigin.X * localDirection.X + localOrigin.Z * localDirection.Z;
                var radialStart = localOrigin.X * localOrigin.X + localOrigin.Z * localOrigin.Z - cylinderRadius * cylinderRadius;
                var discriminant = radialOffset * radialOffset - radialRate * radialStart;
                if (discriminant >= 0)
                {
                    var root = MathF.Sqrt(discriminant);
                    foreach (var candidateTime in new[] { (-radialOffset - root) / radialRate, (-radialOffset + root) / radialRate })
                    {
                        var y = localOrigin.Y + localDirection.Y * candidateTime;
                        if (Math.Abs(y) <= cylinderHalfHeight)
                        {
                            var x = localOrigin.X + localDirection.X * candidateTime;
                            var z = localOrigin.Z + localDirection.Z * candidateTime;
                            Consider(candidateTime, Vector3.Normalize(new Vector3(x, 0, z)));
                        }
                    }
                }
            }
            if (Math.Abs(localDirection.Y) > AxisEpsilon)
            for (var sign = -1; sign <= 1; sign += 2)
            {
                var candidateTime = (sign * cylinderHalfHeight - localOrigin.Y) / localDirection.Y;
                var x = localOrigin.X + localDirection.X * candidateTime;
                var z = localOrigin.Z + localDirection.Z * candidateTime;
                if (x * x + z * z <= cylinderRadius * cylinderRadius) Consider(candidateTime, new Vector3(0, sign, 0));
            }
            if (!float.IsFinite(bestTime)) return null;
            var cylinderHit = origin + direction * bestTime;
            return new RayHit(cylinderHit, Vector3.Normalize(bestNormal), direction.Length() * bestTime);
        }

        var box = new Box(part);
        var offset = origin - box.Center;
        var entry = float.NegativeInfinity;
        var exit = float.PositiveInfinity;
        var entryNormal = Vector3.Zero;
        var exitNormal = Vector3.Zero;
        for (var index = 0; index < 3; index++)
        {
            var axis = box.Axes[index];
            var localOrigin = Vector3.Dot(offset, axis);
            var rate = Vector3.Dot(direction, axis);
            var half = box.HalfSize[index];
            if (Math.Abs(rate) < AxisEpsilon)
            {
                if (Math.Abs(localOrigin) > half) return null;
                continue;
            }
            var first = (-half - localOrigin) / rate;
            var last = (half - localOrigin) / rate;
            var nearNormal = rate > 0 ? -axis : axis;
            if (first > last) (first, last) = (last, first);
            if (first > entry) { entry = first; entryNormal = nearNormal; }
            if (last < exit) { exit = last; exitNormal = -nearNormal; }
            if (entry > exit) return null;
        }
        var time = entry >= 0 ? entry : exit;
        if (time < 0 || time > 1 || !float.IsFinite(time)) return null;
        return new RayHit(origin + direction * time, entry >= 0 ? entryNormal : exitNormal, direction.Length() * time);
    }

    public static bool Intersects(SceneNode first, SceneNode second)
    {
        var firstIsSphere = TryGetSphere(first, out var firstCenter, out var firstRadius);
        var secondIsSphere = TryGetSphere(second, out var secondCenter, out var secondRadius);
        var firstIsCylinder = TryGetCylinder(first, out var firstCylinderCenter, out var firstCylinderRadius, out var firstCylinderHalfHeight, out var firstCylinderRotation);
        var secondIsCylinder = TryGetCylinder(second, out var secondCylinderCenter, out var secondCylinderRadius, out var secondCylinderHalfHeight, out var secondCylinderRotation);
        if (firstIsSphere && secondIsSphere)
            return Vector3.DistanceSquared(firstCenter, secondCenter) <= (firstRadius + secondRadius) * (firstRadius + secondRadius);
        if (firstIsCylinder && secondIsCylinder)
        {
            var firstAxis = Vector3.TransformNormal(Vector3.UnitY, firstCylinderRotation);
            var secondAxis = Vector3.TransformNormal(Vector3.UnitY, secondCylinderRotation);
            var axisDot = Vector3.Dot(firstAxis, secondAxis);
            if (Math.Abs(axisDot) > .999f)
            {
                var separation = secondCylinderCenter - firstCylinderCenter;
                var axialDistance = Math.Abs(Vector3.Dot(separation, firstAxis));
                var axialOverlap = axialDistance <= firstCylinderHalfHeight + secondCylinderHalfHeight;
                var radialDistanceSquared = Math.Max(0, separation.LengthSquared() - axialDistance * axialDistance);
                return axialOverlap && radialDistanceSquared <= (firstCylinderRadius + secondCylinderRadius) * (firstCylinderRadius + secondCylinderRadius);
            }
        }
        if (firstIsSphere && secondIsCylinder) return SphereIntersectsCylinder(firstCenter, firstRadius, secondCylinderCenter, secondCylinderRadius, secondCylinderHalfHeight, secondCylinderRotation);
        if (secondIsSphere && firstIsCylinder) return SphereIntersectsCylinder(secondCenter, secondRadius, firstCylinderCenter, firstCylinderRadius, firstCylinderHalfHeight, firstCylinderRotation);
        if (firstIsSphere) return SphereIntersectsBox(firstCenter, firstRadius, new Box(second));
        if (secondIsSphere) return SphereIntersectsBox(secondCenter, secondRadius, new Box(first));

        return BoundsIntersect(first, second);
    }

    public static bool BoundsIntersect(SceneNode first, SceneNode second)
        => BoxesIntersect(new Box(first), new Box(second));

    public static bool BoundsIntersectSphere(SceneNode node, Vector3 center, float radius)
    {
        var box = new Box(node);
        var offset = center - box.Center;
        var closest = box.Center;
        for (var axis = 0; axis < 3; axis++)
        {
            var direction = box.Axes[axis];
            var coordinate = Math.Clamp(Vector3.Dot(offset, direction), -box.HalfSize[axis], box.HalfSize[axis]);
            closest += direction * coordinate;
        }
        return Vector3.DistanceSquared(center, closest) <= radius * radius;
    }

    public static RayHit? Boxcast(SceneNode moving, SceneNode stationary, Vector3 direction)
    {
        var movingBox = new Box(moving);
        var stationaryBox = new Box(stationary);
        if (BoxesIntersect(movingBox, stationaryBox)) return null;
        var difference = stationaryBox.Center - movingBox.Center;
        var entry = float.NegativeInfinity;
        var exit = float.PositiveInfinity;
        var hitNormal = Vector3.Zero;
        foreach (var axis in SeparatingAxes(movingBox, stationaryBox))
        {
            var distance = Vector3.Dot(difference, axis);
            var radius = movingBox.RadiusOn(axis) + stationaryBox.RadiusOn(axis);
            var rate = Vector3.Dot(direction, axis);
            if (Math.Abs(rate) < AxisEpsilon)
            {
                if (Math.Abs(distance) > radius) return null;
                continue;
            }
            var first = (distance - radius) / rate;
            var last = (distance + radius) / rate;
            var near = Math.Min(first, last);
            if (near > entry)
            {
                entry = near;
                hitNormal = rate > 0 ? -axis : axis;
            }
            exit = Math.Min(exit, Math.Max(first, last));
            if (entry > exit) return null;
        }
        if (entry < 0 || entry > 1 || !float.IsFinite(entry)) return null;
        var position = stationaryBox.SupportPoint(hitNormal);
        return new RayHit(position, hitNormal, direction.Length() * entry);
    }

    private static bool BoxesIntersect(Box first, Box second)
    {
        var difference = second.Center - first.Center;
        foreach (var axis in SeparatingAxes(first, second))
            if (Math.Abs(Vector3.Dot(difference, axis)) > first.RadiusOn(axis) + second.RadiusOn(axis)) return false;
        return true;
    }

    public static float TopY(SceneNode node)
    {
        if (TryGetSphere(node, out var center, out var radius)) return center.Y + radius;
        if (TryGetEllipsoid(node, out var ellipsoidCenter, out var ellipsoidRadii, out var ellipsoidRotation))
        {
            var xAxis = Vector3.TransformNormal(Vector3.UnitX, ellipsoidRotation);
            var yAxis = Vector3.TransformNormal(Vector3.UnitY, ellipsoidRotation);
            var zAxis = Vector3.TransformNormal(Vector3.UnitZ, ellipsoidRotation);
            var support = MathF.Sqrt(
                ellipsoidRadii.X * ellipsoidRadii.X * xAxis.Y * xAxis.Y
                + ellipsoidRadii.Y * ellipsoidRadii.Y * yAxis.Y * yAxis.Y
                + ellipsoidRadii.Z * ellipsoidRadii.Z * zAxis.Y * zAxis.Y);
            return ellipsoidCenter.Y + support;
        }
        if (TryGetCylinder(node, out var cylinderCenter, out var cylinderRadius, out var cylinderHalfHeight, out var cylinderRotation))
        {
            var axis = Vector3.TransformNormal(Vector3.UnitY, cylinderRotation);
            return cylinderCenter.Y + cylinderHalfHeight * MathF.Abs(axis.Y) + cylinderRadius * MathF.Sqrt(axis.X * axis.X + axis.Z * axis.Z);
        }
        var box = new Box(node);
        return box.Center.Y + box.RadiusOn(Vector3.UnitY);
    }

    // Height of the upper box surface directly above a world-space X/Z point.
    public static float? TopYAt(SceneNode node, float x, float z)
    {
        if (TryGetSphere(node, out var sphereCenter, out var sphereRadius))
        {
            var dx = x - sphereCenter.X;
            var dz = z - sphereCenter.Z;
            var remaining = sphereRadius * sphereRadius - dx * dx - dz * dz;
            return remaining < 0 ? null : sphereCenter.Y + MathF.Sqrt(remaining);
        }
        if (TryGetEllipsoid(node, out var ellipsoidCenter, out var ellipsoidRadii, out _))
        {
            var horizontalDistance = MathF.Sqrt((x - ellipsoidCenter.X) * (x - ellipsoidCenter.X) + (z - ellipsoidCenter.Z) * (z - ellipsoidCenter.Z));
            var rayReach = horizontalDistance + ellipsoidRadii.Length() + 1;
            var hit = Raycast(node, new Vector3(x, ellipsoidCenter.Y + rayReach, z), new Vector3(0, -2 * rayReach, 0));
            return hit?.Position.Y;
        }
        if (TryGetCylinder(node, out var cylinderCenter, out var cylinderRadius, out var cylinderHalfHeight, out _))
        {
            var horizontalDistance = MathF.Sqrt((x - cylinderCenter.X) * (x - cylinderCenter.X) + (z - cylinderCenter.Z) * (z - cylinderCenter.Z));
            var rayReach = horizontalDistance + cylinderRadius + cylinderHalfHeight + 1;
            var hit = Raycast(node, new Vector3(x, cylinderCenter.Y + rayReach, z), new Vector3(0, -2 * rayReach, 0));
            return hit?.Position.Y;
        }

        var box = new Box(node);
        var offset = new Vector3(x - box.Center.X, 0, z - box.Center.Z);
        var lower = float.NegativeInfinity;
        var upper = float.PositiveInfinity;
        for (var axis = 0; axis < 3; axis++)
        {
            var localPosition = Vector3.Dot(offset, box.Axes[axis]);
            var verticalRate = Vector3.Dot(Vector3.UnitY, box.Axes[axis]);
            var half = box.HalfSize[axis];
            if (Math.Abs(verticalRate) < AxisEpsilon)
            {
                if (Math.Abs(localPosition) > half) return null;
                continue;
            }
            var first = (-half - localPosition) / verticalRate;
            var last = (half - localPosition) / verticalRate;
            lower = Math.Max(lower, Math.Min(first, last));
            upper = Math.Min(upper, Math.Max(first, last));
            if (lower > upper) return null;
        }
        return box.Center.Y + upper;
    }

    // Returns the fraction of a world-axis displacement possible before first contact.
    public static float SweepFraction(SceneNode moving, SceneNode stationary, int motionAxis, float motion)
    {
        if (motion == 0) return 1;
        if (TryGetSphere(moving, out var movingCenter, out var movingRadius)
            && TryGetSphere(stationary, out var stationaryCenter, out var stationaryRadius))
        {
            var sphereDifference = movingCenter - stationaryCenter;
            var sphereDirection = motionAxis switch
            {
                0 => new Vector3(motion, 0, 0),
                1 => new Vector3(0, motion, 0),
                _ => new Vector3(0, 0, motion)
            };
            var radius = movingRadius + stationaryRadius;
            var quadraticA = Vector3.Dot(sphereDirection, sphereDirection);
            var quadraticB = 2 * Vector3.Dot(sphereDifference, sphereDirection);
            var c = Vector3.Dot(sphereDifference, sphereDifference) - radius * radius;
            // A touching pair is not an overlap: block motion that enters the other sphere,
            // while allowing motion that leaves the contact point.
            if (c < -AxisEpsilon) return 1;
            if (c <= AxisEpsilon) return quadraticB < 0 ? 0 : 1;
            var discriminant = quadraticB * quadraticB - 4 * quadraticA * c;
            if (discriminant < 0) return 1;
            var fraction = (-quadraticB - MathF.Sqrt(discriminant)) / (2 * quadraticA);
            return fraction is >= 0 and <= 1 && float.IsFinite(fraction) ? fraction : 1;
        }

        var a = new Box(moving);
        var b = new Box(stationary);
        var direction = motionAxis switch
        {
            0 => new Vector3(motion, 0, 0),
            1 => new Vector3(0, motion, 0),
            _ => new Vector3(0, 0, motion)
        };
        var difference = b.Center - a.Center;
        var entry = float.NegativeInfinity;
        var exit = float.PositiveInfinity;
        var alreadyOverlapping = true;
        foreach (var axis in SeparatingAxes(a, b))
        {
            var distance = Vector3.Dot(difference, axis);
            var radius = a.RadiusOn(axis) + b.RadiusOn(axis);
            if (Math.Abs(distance) >= radius) alreadyOverlapping = false;
            var rate = Vector3.Dot(direction, axis);
            if (Math.Abs(rate) < AxisEpsilon)
            {
                if (Math.Abs(distance) >= radius) return 1;
                continue;
            }
            var first = (distance - radius) / rate;
            var last = (distance + radius) / rate;
            entry = Math.Max(entry, Math.Min(first, last));
            exit = Math.Min(exit, Math.Max(first, last));
            if (entry > exit) return 1;
        }
        // An existing overlap should never teleport a part backward to an old boundary.
        if (alreadyOverlapping || exit <= 0 || entry > 1) return 1;
        return Math.Clamp(entry, 0, 1);
    }

    private static IEnumerable<Vector3> SeparatingAxes(Box first, Box second)
    {
        foreach (var axis in first.Axes) yield return axis;
        foreach (var axis in second.Axes) yield return axis;
        foreach (var a in first.Axes)
        foreach (var b in second.Axes)
        {
            var cross = Vector3.Cross(a, b);
            if (cross.LengthSquared() > AxisEpsilon) yield return Vector3.Normalize(cross);
        }
    }

    private static bool TryGetSphere(SceneNode node, out Vector3 center, out float radius)
    {
        center = new Vector3(node.Position[0], node.Position[1], node.Position[2]);
        radius = Math.Abs(node.Size[0]) * .5f;
        return node.Type == "Part" && node.Shape == "Ball"
            && Math.Abs(Math.Abs(node.Size[0]) - Math.Abs(node.Size[1])) < .0001f
            && Math.Abs(Math.Abs(node.Size[0]) - Math.Abs(node.Size[2])) < .0001f;
    }

    private static bool TryGetEllipsoid(SceneNode node, out Vector3 center, out Vector3 radii, out Matrix4x4 rotation)
    {
        center = new Vector3(node.Position[0], node.Position[1], node.Position[2]);
        radii = new Vector3(Math.Abs(node.Size[0]), Math.Abs(node.Size[1]), Math.Abs(node.Size[2])) * .5f;
        var radians = new Vector3(node.Rotation[0], node.Rotation[1], node.Rotation[2]) * (MathF.PI / 180);
        rotation = Matrix4x4.CreateRotationZ(radians.Z) * Matrix4x4.CreateRotationX(radians.X) * Matrix4x4.CreateRotationY(radians.Y);
        return node.Type == "Part" && node.Shape == "Ball"
            && radii.X > AxisEpsilon && radii.Y > AxisEpsilon && radii.Z > AxisEpsilon;
    }

    private static bool TryGetCylinder(SceneNode node, out Vector3 center, out float radius, out float halfHeight, out Matrix4x4 rotation)
    {
        center = new Vector3(node.Position[0], node.Position[1], node.Position[2]);
        radius = (Math.Abs(node.Size[0]) + Math.Abs(node.Size[2])) * .25f;
        halfHeight = Math.Abs(node.Size[1]) * .5f;
        var radians = new Vector3(node.Rotation[0], node.Rotation[1], node.Rotation[2]) * (MathF.PI / 180);
        rotation = Matrix4x4.CreateRotationZ(radians.Z) * Matrix4x4.CreateRotationX(radians.X) * Matrix4x4.CreateRotationY(radians.Y);
        return node.Type == "Part" && node.Shape == "Cylinder"
            && Math.Abs(Math.Abs(node.Size[0]) - Math.Abs(node.Size[2])) < .0001f;
    }

    private static bool SphereIntersectsBox(Vector3 center, float radius, Box box)
    {
        var offset = center - box.Center;
        var closest = box.Center;
        for (var axis = 0; axis < 3; axis++)
        {
            var direction = box.Axes[axis];
            var coordinate = Math.Clamp(Vector3.Dot(offset, direction), -box.HalfSize[axis], box.HalfSize[axis]);
            closest += direction * coordinate;
        }
        return Vector3.DistanceSquared(center, closest) <= radius * radius;
    }

    private static bool SphereIntersectsCylinder(Vector3 sphereCenter, float sphereRadius, Vector3 cylinderCenter, float cylinderRadius, float cylinderHalfHeight, Matrix4x4 cylinderRotation)
    {
        var local = Vector3.TransformNormal(sphereCenter - cylinderCenter, Matrix4x4.Transpose(cylinderRotation));
        var axial = Math.Clamp(local.Y, -cylinderHalfHeight, cylinderHalfHeight);
        var radial = new Vector2(local.X, local.Z);
        var radialLength = radial.Length();
        if (radialLength > cylinderRadius && radialLength > AxisEpsilon) radial *= cylinderRadius / radialLength;
        var closest = new Vector3(radial.X, axial, radial.Y);
        return Vector3.DistanceSquared(local, closest) <= sphereRadius * sphereRadius;
    }

    private readonly struct Box
    {
        public Vector3 Center { get; }
        public Vector3[] Axes { get; }
        public Vector3 HalfSize { get; }

        public Box(SceneNode node)
        {
            Center = new Vector3(node.Position[0], node.Position[1], node.Position[2]);
            HalfSize = new Vector3(Math.Abs(node.Size[0]), Math.Abs(node.Size[1]), Math.Abs(node.Size[2])) * .5f;
            var radians = new Vector3(node.Rotation[0], node.Rotation[1], node.Rotation[2]) * (MathF.PI / 180);
            // Match SceneRenderService.TransformFor: local Z, then X, then Y.
            var rotation = Matrix4x4.CreateRotationZ(radians.Z)
                * Matrix4x4.CreateRotationX(radians.X)
                * Matrix4x4.CreateRotationY(radians.Y);
            Axes =
            [
                Vector3.TransformNormal(Vector3.UnitX, rotation),
                Vector3.TransformNormal(Vector3.UnitY, rotation),
                Vector3.TransformNormal(Vector3.UnitZ, rotation)
            ];
        }

        public float RadiusOn(Vector3 axis) =>
            HalfSize.X * Math.Abs(Vector3.Dot(Axes[0], axis))
            + HalfSize.Y * Math.Abs(Vector3.Dot(Axes[1], axis))
            + HalfSize.Z * Math.Abs(Vector3.Dot(Axes[2], axis));

        public Vector3 SupportPoint(Vector3 direction)
        {
            var point = Center;
            for (var index = 0; index < 3; index++)
            {
                var projection = Vector3.Dot(direction, Axes[index]);
                point += Axes[index] * (projection < 0 ? -HalfSize[index] : HalfSize[index]);
            }
            return point;
        }
    }
}
