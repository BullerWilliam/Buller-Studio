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

        if (TryGetCylinderRadii(part, out var cylinderCenter, out var cylinderRadiusX, out var cylinderRadiusZ, out var cylinderHalfHeight, out var cylinderRotation))
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

            var radialRate = localDirection.X * localDirection.X / (cylinderRadiusX * cylinderRadiusX)
                + localDirection.Z * localDirection.Z / (cylinderRadiusZ * cylinderRadiusZ);
            if (radialRate > AxisEpsilon)
            {
                var radialOffset = localOrigin.X * localDirection.X / (cylinderRadiusX * cylinderRadiusX)
                    + localOrigin.Z * localDirection.Z / (cylinderRadiusZ * cylinderRadiusZ);
                var radialStart = localOrigin.X * localOrigin.X / (cylinderRadiusX * cylinderRadiusX)
                    + localOrigin.Z * localOrigin.Z / (cylinderRadiusZ * cylinderRadiusZ) - 1;
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
                        Consider(candidateTime, Vector3.Normalize(new Vector3(x / (cylinderRadiusX * cylinderRadiusX), 0, z / (cylinderRadiusZ * cylinderRadiusZ))));
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
                if (x * x / (cylinderRadiusX * cylinderRadiusX) + z * z / (cylinderRadiusZ * cylinderRadiusZ) <= 1) Consider(candidateTime, new Vector3(0, sign, 0));
            }
            if (!float.IsFinite(bestTime)) return null;
            var cylinderHit = origin + direction * bestTime;
            return new RayHit(cylinderHit, Vector3.Normalize(bestNormal), direction.Length() * bestTime);
        }

        if (part.Type is "WedgePart" or "CornerWedgePart")
        {
            var wedgeCenter = new Vector3(part.Position[0], part.Position[1], part.Position[2]);
            var inverseRotation = Matrix4x4.Transpose(RotationFor(part));
            var localOrigin = Vector3.TransformNormal(origin - wedgeCenter, inverseRotation);
            var localDirection = Vector3.TransformNormal(direction, inverseRotation);
            var bestTime = float.PositiveInfinity;
            var bestNormal = Vector3.Zero;
            foreach (var (a, b, c) in WedgeTriangles(part))
            {
                if (!RayTriangle(localOrigin, localDirection, a, b, c, out var candidateTime)) continue;
                if (candidateTime < 0 || candidateTime > 1 || candidateTime >= bestTime) continue;
                var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                if (Vector3.Dot(normal, localDirection) > 0) normal = -normal;
                bestTime = candidateTime;
                bestNormal = Vector3.TransformNormal(normal, RotationFor(part));
            }
            if (!float.IsFinite(bestTime)) return null;
            return new RayHit(origin + direction * bestTime, Vector3.Normalize(bestNormal), direction.Length() * bestTime);
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
        if (firstIsSphere && IsCylinder(second)) return SphereIntersectsCylinder(firstCenter, firstRadius, second);
        if (secondIsSphere && IsCylinder(first)) return SphereIntersectsCylinder(secondCenter, secondRadius, first);
        if (firstIsSphere && IsEllipsoid(second)) return SphereIntersectsEllipsoid(firstCenter, firstRadius, second);
        if (secondIsSphere && IsEllipsoid(first)) return SphereIntersectsEllipsoid(secondCenter, secondRadius, first);
        if (IsWedge(first) && secondIsSphere) return SphereIntersectsWedge(secondCenter, secondRadius, first);
        if (IsWedge(second) && firstIsSphere) return SphereIntersectsWedge(firstCenter, firstRadius, second);
        if (IsWedge(first) || IsWedge(second) || IsCylinder(first) || IsCylinder(second) || IsEllipsoid(first) || IsEllipsoid(second))
        {
            return ConvexShapesIntersect(GetConvexWorldShape(first), GetConvexWorldShape(second));
        }
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
        if (TryGetSphere(stationary, out var sphereCenter, out var sphereRadius)
            && Spherecast(sphereCenter, sphereRadius, -direction, moving) is { } sphereHit)
        {
            var normal = -sphereHit.Normal;
            return new RayHit(sphereCenter + normal * sphereRadius, normal, sphereHit.Distance);
        }
        if ((IsWedge(moving) || IsWedge(stationary) || IsCylinder(moving) || IsCylinder(stationary) || IsEllipsoid(moving) || IsEllipsoid(stationary))
            && !TryGetSphere(moving, out _, out _) && !TryGetSphere(stationary, out _, out _))
        {
            var movingShape = GetConvexWorldShape(moving);
            var stationaryShape = GetConvexWorldShape(stationary);
            if (SweptConvexHit(movingShape, stationaryShape, direction) is not { } wedgeHit) return null;
            return new RayHit(SupportPoint(stationaryShape, wedgeHit.Normal), wedgeHit.Normal, direction.Length() * wedgeHit.Fraction);
        }
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

    public static RayHit? Spherecast(Vector3 origin, float radius, Vector3 direction, SceneNode stationary)
    {
        Vector3 ClosestPoint(Vector3 center)
        {
            if (TryGetSphere(stationary, out var sphereCenter, out var sphereRadius))
            {
                var offset = center - sphereCenter;
                var distance = offset.Length();
                return distance > AxisEpsilon ? sphereCenter + offset * (sphereRadius / distance) : sphereCenter + Vector3.UnitY * sphereRadius;
            }
            if (TryGetCylinderRadii(stationary, out var cylinderCenter, out var cylinderRadiusX, out var cylinderRadiusZ, out var cylinderHalfHeight, out var cylinderRotation))
            {
                var inverse = Matrix4x4.Transpose(cylinderRotation);
                var local = Vector3.TransformNormal(center - cylinderCenter, inverse);
                var closestLocal = ClosestPointOnCylinder(local, cylinderRadiusX, cylinderRadiusZ, cylinderHalfHeight);
                return cylinderCenter + Vector3.TransformNormal(closestLocal, cylinderRotation);
            }
            if (TryGetEllipsoid(stationary, out var ellipsoidCenter, out var ellipsoidRadii, out var ellipsoidRotation))
            {
                var local = Vector3.TransformNormal(center - ellipsoidCenter, Matrix4x4.Transpose(ellipsoidRotation));
                var closestLocal = ClosestPointOnEllipsoid(local, ellipsoidRadii);
                return ellipsoidCenter + Vector3.TransformNormal(closestLocal, ellipsoidRotation);
            }
            if (IsWedge(stationary))
            {
                var triangles = WedgeWorldTriangles(stationary);
                if (PointInsideWedge(center, triangles)) return center;
                return triangles.Select(triangle => ClosestPointOnTriangle(center, triangle.A, triangle.B, triangle.C))
                    .MinBy(point => Vector3.DistanceSquared(center, point));
            }
            var box = new Box(stationary);
            var offsetFromBox = center - box.Center;
            var closest = box.Center;
            for (var axis = 0; axis < 3; axis++)
            {
                var boxAxis = box.Axes[axis];
                closest += boxAxis * Math.Clamp(Vector3.Dot(offsetFromBox, boxAxis), -box.HalfSize[axis], box.HalfSize[axis]);
            }
            return closest;
        }

        float DistanceSquaredAt(float fraction)
        {
            var center = origin + direction * fraction;
            return Vector3.DistanceSquared(center, ClosestPoint(center));
        }

        var radiusSquared = radius * radius;
        if (!float.IsFinite(radiusSquared)) return null;
        if (DistanceSquaredAt(0) <= radiusSquared) return null;
        var lower = 0f;
        var upper = 1f;
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var firstThird = lower + (upper - lower) / 3;
            var secondThird = upper - (upper - lower) / 3;
            if (DistanceSquaredAt(firstThird) <= DistanceSquaredAt(secondThird)) upper = secondThird;
            else lower = firstThird;
        }
        var minimumFraction = (lower + upper) * .5f;
        if (DistanceSquaredAt(minimumFraction) > radiusSquared + 1e-5f) return null;
        lower = 0;
        upper = minimumFraction;
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var middle = (lower + upper) * .5f;
            if (DistanceSquaredAt(middle) <= radiusSquared) upper = middle;
            else lower = middle;
        }
        var hitCenter = origin + direction * upper;
        var hitPosition = ClosestPoint(hitCenter);
        var normal = hitCenter - hitPosition;
        if (normal.LengthSquared() <= AxisEpsilon) normal = -Vector3.Normalize(direction);
        else normal = Vector3.Normalize(normal);
        return new RayHit(hitPosition, normal, direction.Length() * upper);
    }

    public static RayHit? Shapecast(SceneNode moving, SceneNode stationary, Vector3 direction)
    {
        if (TryGetSphere(moving, out var sphereCenter, out var sphereRadius))
            return Spherecast(sphereCenter, sphereRadius, direction, stationary);
        return Boxcast(moving, stationary, direction);
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
        if (TryGetCylinderRadii(node, out var cylinderCenter, out var cylinderRadiusX, out var cylinderRadiusZ, out var cylinderHalfHeight, out var cylinderRotation))
        {
            var xAxis = Vector3.TransformNormal(Vector3.UnitX, cylinderRotation);
            var yAxis = Vector3.TransformNormal(Vector3.UnitY, cylinderRotation);
            var zAxis = Vector3.TransformNormal(Vector3.UnitZ, cylinderRotation);
            var radialSupport = MathF.Sqrt(cylinderRadiusX * cylinderRadiusX * xAxis.Y * xAxis.Y + cylinderRadiusZ * cylinderRadiusZ * zAxis.Y * zAxis.Y);
            return cylinderCenter.Y + cylinderHalfHeight * MathF.Abs(yAxis.Y) + radialSupport;
        }
        if (node.Type is "WedgePart" or "CornerWedgePart")
            return WedgeTriangles(node).SelectMany(triangle => new[] { triangle.A, triangle.B, triangle.C })
                .Max(vertex => (Vector3.TransformNormal(vertex, RotationFor(node)) + new Vector3(node.Position[0], node.Position[1], node.Position[2])).Y);
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
        if (TryGetCylinderRadii(node, out var cylinderCenter, out var cylinderRadiusX, out var cylinderRadiusZ, out var cylinderHalfHeight, out _))
        {
            var horizontalDistance = MathF.Sqrt((x - cylinderCenter.X) * (x - cylinderCenter.X) + (z - cylinderCenter.Z) * (z - cylinderCenter.Z));
            var rayReach = horizontalDistance + Math.Max(cylinderRadiusX, cylinderRadiusZ) + cylinderHalfHeight + 1;
            var hit = Raycast(node, new Vector3(x, cylinderCenter.Y + rayReach, z), new Vector3(0, -2 * rayReach, 0));
            return hit?.Position.Y;
        }
        if (node.Type is "WedgePart" or "CornerWedgePart")
        {
            var center = new Vector3(node.Position[0], node.Position[1], node.Position[2]);
            var reach = MathF.Sqrt(node.Size[0] * node.Size[0] + node.Size[1] * node.Size[1] + node.Size[2] * node.Size[2]) + 1;
            return Raycast(node, new Vector3(x, center.Y + reach, z), new Vector3(0, -2 * reach, 0))?.Position.Y;
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

        if ((IsWedge(moving) || IsCylinder(moving) || IsEllipsoid(moving)) && TryGetSphere(stationary, out var stationarySphereCenter, out var stationarySphereRadius))
            return Spherecast(stationarySphereCenter, stationarySphereRadius, -AxisDirection(motionAxis, motion), moving) is { } reverseSphereHit
                ? Math.Clamp(reverseSphereHit.Distance / Math.Abs(motion), 0, 1) : 1;
        if (TryGetSphere(moving, out movingCenter, out movingRadius) && (IsWedge(stationary) || IsCylinder(stationary) || IsEllipsoid(stationary)))
            return Spherecast(movingCenter, movingRadius, AxisDirection(motionAxis, motion), stationary) is { } wedgeSphereHit
                ? Math.Clamp(wedgeSphereHit.Distance / Math.Abs(motion), 0, 1) : 1;
        if (IsWedge(moving) || IsWedge(stationary) || IsCylinder(moving) || IsCylinder(stationary) || IsEllipsoid(moving) || IsEllipsoid(stationary))
        {
            var movingShape = GetConvexWorldShape(moving);
            var stationaryShape = GetConvexWorldShape(stationary);
            return SweptConvexFraction(movingShape, stationaryShape, AxisDirection(motionAxis, motion));
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

    private static bool IsWedge(SceneNode node) => node.Type is "WedgePart" or "CornerWedgePart";
    private static bool IsCylinder(SceneNode node) => TryGetCylinderRadii(node, out _, out _, out _, out _, out _);
    private static bool IsEllipsoid(SceneNode node) => TryGetEllipsoid(node, out _, out _, out _);

    private sealed record ConvexShape(Vector3[] Vertices, Vector3[] FaceNormals, Vector3[] Edges);

    private static ConvexShape GetConvexWorldShape(SceneNode node)
    {
        if (IsWedge(node)) return GetWedgeWorldShape(node);
        if (TryGetEllipsoid(node, out var ellipsoidCenter, out var ellipsoidRadii, out var ellipsoidRotation))
            return GetEllipsoidWorldShape(ellipsoidCenter, ellipsoidRadii, ellipsoidRotation);
        if (TryGetCylinderRadii(node, out var center, out var radiusX, out var radiusZ, out var halfHeight, out var rotation))
            return GetCylinderWorldShape(center, radiusX, radiusZ, halfHeight, rotation);
        return GetBoxWorldShape(new Box(node));
    }

    private static ConvexShape GetCylinderWorldShape(Vector3 center, float radiusX, float radiusZ, float halfHeight, Matrix4x4 rotation)
    {
        const int segments = 32;
        var step = MathF.Tau / segments;
        var vertices = new Vector3[segments * 2];
        for (var index = 0; index < segments; index++)
        {
            var angle = index * step;
            var x = MathF.Cos(angle) * radiusX;
            var z = MathF.Sin(angle) * radiusZ;
            vertices[index] = Vector3.TransformNormal(new Vector3(x, -halfHeight, z), rotation) + center;
            vertices[index + segments] = Vector3.TransformNormal(new Vector3(x, halfHeight, z), rotation) + center;
        }
        var axis = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, rotation));
        var normals = new List<Vector3> { axis, -axis };
        var edges = new List<Vector3> { axis };
        for (var index = 0; index < segments; index++)
        {
            var angle = (index + .5f) * step;
            normals.Add(Vector3.Normalize(Vector3.TransformNormal(new Vector3(MathF.Cos(angle) / radiusX, 0, MathF.Sin(angle) / radiusZ), rotation)));
            var next = (index + 1) % segments;
            var ringEdge = vertices[next] - vertices[index];
            if (ringEdge.LengthSquared() > AxisEpsilon) edges.Add(Vector3.Normalize(ringEdge));
        }
        return new ConvexShape(vertices, normals.ToArray(), edges.ToArray());
    }

    private static ConvexShape GetEllipsoidWorldShape(Vector3 center, Vector3 radii, Matrix4x4 rotation)
    {
        const int latitudeSegments = 12;
        const int longitudeSegments = 32;
        var localVertices = new List<Vector3> { new(0, radii.Y, 0) };
        for (var latitude = 1; latitude < latitudeSegments; latitude++)
        {
            var polar = MathF.PI * latitude / latitudeSegments;
            var ringRadius = MathF.Sin(polar);
            var y = MathF.Cos(polar);
            for (var longitude = 0; longitude < longitudeSegments; longitude++)
            {
                var azimuth = MathF.Tau * longitude / longitudeSegments;
                localVertices.Add(new Vector3(MathF.Cos(azimuth) * ringRadius * radii.X, y * radii.Y, MathF.Sin(azimuth) * ringRadius * radii.Z));
            }
        }
        var bottomIndex = localVertices.Count;
        localVertices.Add(new Vector3(0, -radii.Y, 0));
        var triangles = new List<(int A, int B, int C)>();
        int RingIndex(int latitude, int longitude) => 1 + (latitude - 1) * longitudeSegments + (longitude % longitudeSegments);
        for (var longitude = 0; longitude < longitudeSegments; longitude++)
        {
            var next = (longitude + 1) % longitudeSegments;
            triangles.Add((0, RingIndex(1, longitude), RingIndex(1, next)));
            triangles.Add((bottomIndex, RingIndex(latitudeSegments - 1, next), RingIndex(latitudeSegments - 1, longitude)));
        }
        for (var latitude = 1; latitude < latitudeSegments - 1; latitude++)
        for (var longitude = 0; longitude < longitudeSegments; longitude++)
        {
            var next = (longitude + 1) % longitudeSegments;
            var a = RingIndex(latitude, longitude);
            var b = RingIndex(latitude, next);
            var c = RingIndex(latitude + 1, next);
            var d = RingIndex(latitude + 1, longitude);
            triangles.Add((a, b, c));
            triangles.Add((a, c, d));
        }
        var vertices = localVertices.Select(vertex => Vector3.TransformNormal(vertex, rotation) + center).ToArray();
        var centroid = vertices.Aggregate(Vector3.Zero, (sum, vertex) => sum + vertex) / vertices.Length;
        var normals = new List<Vector3>(triangles.Count);
        var edges = new List<Vector3>();
        foreach (var (a, b, c) in triangles)
        {
            var normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
            if (normal.LengthSquared() <= AxisEpsilon) continue;
            normal = Vector3.Normalize(normal);
            if (Vector3.Dot(normal, (vertices[a] + vertices[b] + vertices[c]) / 3 - centroid) < 0) normal = -normal;
            normals.Add(normal);
            AddEdge(vertices[b] - vertices[a]);
            AddEdge(vertices[c] - vertices[b]);
            AddEdge(vertices[a] - vertices[c]);
        }
        return new ConvexShape(vertices, normals.ToArray(), edges.ToArray());

        void AddEdge(Vector3 edge)
        {
            if (edge.LengthSquared() <= AxisEpsilon) return;
            edge = Vector3.Normalize(edge);
            if (edge.X < -AxisEpsilon || Math.Abs(edge.X) <= AxisEpsilon && edge.Y < -AxisEpsilon || Math.Abs(edge.X) <= AxisEpsilon && Math.Abs(edge.Y) <= AxisEpsilon && edge.Z < 0) edge = -edge;
            if (!edges.Any(existing => Math.Abs(Vector3.Dot(existing, edge)) > .9999f)) edges.Add(edge);
        }
    }

    private static ConvexShape GetWedgeWorldShape(SceneNode node)
    {
        var triangles = WedgeTriangles(node).ToArray();
        var center = new Vector3(node.Position[0], node.Position[1], node.Position[2]);
        var rotation = RotationFor(node);
        var localVertices = triangles.SelectMany(triangle => new[] { triangle.A, triangle.B, triangle.C }).Distinct().ToArray();
        var worldVertices = localVertices.Select(vertex => Vector3.TransformNormal(vertex, rotation) + center).ToArray();
        var centroid = worldVertices.Aggregate(Vector3.Zero, (sum, vertex) => sum + vertex) / worldVertices.Length;
        var normals = new List<Vector3>();
        var edges = new List<Vector3>();
        foreach (var (a, b, c) in triangles)
        {
            var worldA = Vector3.TransformNormal(a, rotation) + center;
            var worldB = Vector3.TransformNormal(b, rotation) + center;
            var worldC = Vector3.TransformNormal(c, rotation) + center;
            var normal = Vector3.Cross(worldB - worldA, worldC - worldA);
            if (normal.LengthSquared() <= AxisEpsilon) continue;
            normal = Vector3.Normalize(normal);
            if (Vector3.Dot(normal, (worldA + worldB + worldC) / 3 - centroid) < 0) normal = -normal;
            normals.Add(normal);
            AddEdge(worldB - worldA);
            AddEdge(worldC - worldB);
            AddEdge(worldA - worldC);
        }
        return new ConvexShape(worldVertices, normals.ToArray(), edges.ToArray());

        void AddEdge(Vector3 edge)
        {
            if (edge.LengthSquared() <= AxisEpsilon) return;
            edge = Vector3.Normalize(edge);
            if (edge.X < -AxisEpsilon || Math.Abs(edge.X) <= AxisEpsilon && edge.Y < -AxisEpsilon || Math.Abs(edge.X) <= AxisEpsilon && Math.Abs(edge.Y) <= AxisEpsilon && edge.Z < 0) edge = -edge;
            if (!edges.Any(existing => Math.Abs(Vector3.Dot(existing, edge)) > .9999f)) edges.Add(edge);
        }
    }

    private static ConvexShape GetBoxWorldShape(Box box)
    {
        var vertices = new List<Vector3>(8);
        for (var x = -1; x <= 1; x += 2)
        for (var y = -1; y <= 1; y += 2)
        for (var z = -1; z <= 1; z += 2)
            vertices.Add(box.Center + box.Axes[0] * (box.HalfSize.X * x) + box.Axes[1] * (box.HalfSize.Y * y) + box.Axes[2] * (box.HalfSize.Z * z));
        return new ConvexShape(vertices.ToArray(), box.Axes, box.Axes);
    }

    private static bool ConvexShapesIntersect(ConvexShape first, ConvexShape second)
    {
        bool Separated(Vector3 axis)
        {
            if (axis.LengthSquared() <= AxisEpsilon) return false;
            axis = Vector3.Normalize(axis);
            var firstMin = first.Vertices.Min(vertex => Vector3.Dot(vertex, axis));
            var firstMax = first.Vertices.Max(vertex => Vector3.Dot(vertex, axis));
            var secondMin = second.Vertices.Min(vertex => Vector3.Dot(vertex, axis));
            var secondMax = second.Vertices.Max(vertex => Vector3.Dot(vertex, axis));
            return firstMax < secondMin - AxisEpsilon || secondMax < firstMin - AxisEpsilon;
        }
        foreach (var normal in first.FaceNormals) if (Separated(normal)) return false;
        foreach (var normal in second.FaceNormals) if (Separated(normal)) return false;
        foreach (var firstEdge in first.Edges)
        foreach (var secondEdge in second.Edges)
            if (Separated(Vector3.Cross(firstEdge, secondEdge))) return false;
        return true;
    }

    private static float SweptConvexFraction(ConvexShape moving, ConvexShape stationary, Vector3 direction)
        => SweptConvexHit(moving, stationary, direction)?.Fraction ?? 1;

    private static (float Fraction, Vector3 Normal)? SweptConvexHit(ConvexShape moving, ConvexShape stationary, Vector3 direction)
    {
        var axes = moving.FaceNormals.Concat(stationary.FaceNormals)
            .Concat(moving.Edges.SelectMany(firstEdge => stationary.Edges.Select(secondEdge => Vector3.Cross(firstEdge, secondEdge))))
            .Where(axis => axis.LengthSquared() > AxisEpsilon)
            .Select(Vector3.Normalize)
            .ToArray();
        var entry = float.NegativeInfinity;
        var exit = float.PositiveInfinity;
        var alreadyOverlapping = true;
        foreach (var axis in axes)
        {
            var movingMin = moving.Vertices.Min(vertex => Vector3.Dot(vertex, axis));
            var movingMax = moving.Vertices.Max(vertex => Vector3.Dot(vertex, axis));
            var stationaryMin = stationary.Vertices.Min(vertex => Vector3.Dot(vertex, axis));
            var stationaryMax = stationary.Vertices.Max(vertex => Vector3.Dot(vertex, axis));
            if (movingMax <= stationaryMin + AxisEpsilon || stationaryMax <= movingMin + AxisEpsilon) alreadyOverlapping = false;
            var rate = Vector3.Dot(direction, axis);
            if (Math.Abs(rate) <= AxisEpsilon)
            {
                if (movingMax < stationaryMin - AxisEpsilon || stationaryMax < movingMin - AxisEpsilon) return null;
                continue;
            }
            var first = (stationaryMin - movingMax) / rate;
            var last = (stationaryMax - movingMin) / rate;
            var near = Math.Min(first, last);
            var far = Math.Max(first, last);
            if (near > entry) entry = near;
            exit = Math.Min(exit, far);
            if (entry > exit) return null;
        }
        if (alreadyOverlapping || exit <= 0 || entry > 1 || entry < 0) return null;
        var normal = Vector3.Zero;
        foreach (var axis in axes)
        {
            var movingMin = moving.Vertices.Min(vertex => Vector3.Dot(vertex, axis));
            var movingMax = moving.Vertices.Max(vertex => Vector3.Dot(vertex, axis));
            var stationaryMin = stationary.Vertices.Min(vertex => Vector3.Dot(vertex, axis));
            var stationaryMax = stationary.Vertices.Max(vertex => Vector3.Dot(vertex, axis));
            var rate = Vector3.Dot(direction, axis);
            if (Math.Abs(rate) <= AxisEpsilon) continue;
            var near = Math.Min((stationaryMin - movingMax) / rate, (stationaryMax - movingMin) / rate);
            if (Math.Abs(near - entry) <= AxisEpsilon)
            {
                normal = rate > 0 ? -axis : axis;
                break;
            }
        }
        return (Math.Clamp(entry, 0, 1), normal);
    }

    private static Vector3 SupportPoint(ConvexShape shape, Vector3 direction) => shape.Vertices.MaxBy(vertex => Vector3.Dot(vertex, direction));

    private static Vector3 AxisDirection(int axis, float distance) => axis switch
    {
        0 => new Vector3(distance, 0, 0),
        1 => new Vector3(0, distance, 0),
        _ => new Vector3(0, 0, distance)
    };

    private static bool SphereIntersectsWedge(Vector3 sphereCenter, float sphereRadius, SceneNode wedge)
    {
        var triangles = WedgeWorldTriangles(wedge);
        if (PointInsideWedge(sphereCenter, triangles)) return true;
        var radiusSquared = sphereRadius * sphereRadius;
        return triangles.Any(triangle => Vector3.DistanceSquared(sphereCenter, ClosestPointOnTriangle(sphereCenter, triangle.A, triangle.B, triangle.C)) <= radiusSquared);
    }

    private static (Vector3 A, Vector3 B, Vector3 C)[] WedgeWorldTriangles(SceneNode wedge)
    {
        var center = new Vector3(wedge.Position[0], wedge.Position[1], wedge.Position[2]);
        var rotation = RotationFor(wedge);
        return WedgeTriangles(wedge).Select(triangle => (
            A: Vector3.TransformNormal(triangle.A, rotation) + center,
            B: Vector3.TransformNormal(triangle.B, rotation) + center,
            C: Vector3.TransformNormal(triangle.C, rotation) + center)).ToArray();
    }

    private static bool PointInsideWedge(Vector3 point, (Vector3 A, Vector3 B, Vector3 C)[] triangles)
    {
        var vertices = triangles.SelectMany(triangle => new[] { triangle.A, triangle.B, triangle.C }).Distinct().ToArray();
        var centroid = vertices.Aggregate(Vector3.Zero, (sum, vertex) => sum + vertex) / vertices.Length;
        foreach (var triangle in triangles)
        {
            var normal = Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A);
            if (normal.LengthSquared() <= AxisEpsilon) continue;
            normal = Vector3.Normalize(normal);
            if (Vector3.Dot(normal, (triangle.A + triangle.B + triangle.C) / 3 - centroid) < 0) normal = -normal;
            if (Vector3.Dot(point - triangle.A, normal) > AxisEpsilon) return false;
        }
        return true;
    }

    private static Vector3 ClosestPointOnTriangle(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = point - a;
        var d1 = Vector3.Dot(ab, ap);
        var d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = point - b;
        var d3 = Vector3.Dot(ab, bp);
        var d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = point - c;
        var d5 = Vector3.Dot(ab, cp);
        var d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        var denominator = 1 / (va + vb + vc);
        return a + ab * (vb * denominator) + ac * (vc * denominator);
    }

    private static Matrix4x4 RotationFor(SceneNode node)
    {
        var radians = new Vector3(node.Rotation[0], node.Rotation[1], node.Rotation[2]) * (MathF.PI / 180);
        return Matrix4x4.CreateRotationZ(radians.Z) * Matrix4x4.CreateRotationX(radians.X) * Matrix4x4.CreateRotationY(radians.Y);
    }

    private static IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> WedgeTriangles(SceneNode node)
    {
        var x = Math.Abs(node.Size[0]) * .5f;
        var y = Math.Abs(node.Size[1]) * .5f;
        var z = Math.Abs(node.Size[2]) * .5f;
        var a = new Vector3(-x, -y, -z);
        var b = new Vector3(x, -y, -z);
        var c = new Vector3(x, -y, z);
        var d = new Vector3(-x, -y, z);
        var e = new Vector3(-x, y, z);
        var f = new Vector3(x, y, z);
        yield return (a, b, c);
        yield return (a, c, d);
        if (node.Type == "WedgePart")
        {
            yield return (d, c, f);
            yield return (d, f, e);
            yield return (a, e, f);
            yield return (a, f, b);
            yield return (a, d, e);
            yield return (b, f, c);
        }
        else
        {
            yield return (a, d, e);
            yield return (d, c, e);
            yield return (c, b, e);
            yield return (b, a, e);
        }
    }

    private static bool RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float time)
    {
        var edge1 = b - a;
        var edge2 = c - a;
        var cross = Vector3.Cross(direction, edge2);
        var determinant = Vector3.Dot(edge1, cross);
        if (Math.Abs(determinant) < AxisEpsilon)
        {
            time = 0;
            return false;
        }
        var inverseDeterminant = 1 / determinant;
        var offset = origin - a;
        var u = Vector3.Dot(offset, cross) * inverseDeterminant;
        if (u < 0 || u > 1)
        {
            time = 0;
            return false;
        }
        var q = Vector3.Cross(offset, edge1);
        var v = Vector3.Dot(direction, q) * inverseDeterminant;
        if (v < 0 || u + v > 1)
        {
            time = 0;
            return false;
        }
        time = Vector3.Dot(edge2, q) * inverseDeterminant;
        return float.IsFinite(time);
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
        var radiusX = Math.Abs(node.Size[0]) * .5f;
        var radiusZ = Math.Abs(node.Size[2]) * .5f;
        radius = (radiusX + radiusZ) * .5f;
        halfHeight = Math.Abs(node.Size[1]) * .5f;
        rotation = RotationFor(node);
        return node.Type == "Part" && node.Shape == "Cylinder"
            && radiusX > AxisEpsilon && radiusZ > AxisEpsilon && Math.Abs(radiusX - radiusZ) < .0001f;
    }

    private static bool TryGetCylinderRadii(SceneNode node, out Vector3 center, out float radiusX, out float radiusZ, out float halfHeight, out Matrix4x4 rotation)
    {
        center = new Vector3(node.Position[0], node.Position[1], node.Position[2]);
        radiusX = Math.Abs(node.Size[0]) * .5f;
        radiusZ = Math.Abs(node.Size[2]) * .5f;
        halfHeight = Math.Abs(node.Size[1]) * .5f;
        rotation = RotationFor(node);
        return node.Type == "Part" && node.Shape == "Cylinder"
            && radiusX > AxisEpsilon && radiusZ > AxisEpsilon && halfHeight > AxisEpsilon;
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

    private static bool SphereIntersectsCylinder(Vector3 sphereCenter, float sphereRadius, SceneNode cylinder)
    {
        if (!TryGetCylinderRadii(cylinder, out var center, out var radiusX, out var radiusZ, out var halfHeight, out var rotation)) return false;
        var local = Vector3.TransformNormal(sphereCenter - center, Matrix4x4.Transpose(rotation));
        var closest = ClosestPointOnCylinder(local, radiusX, radiusZ, halfHeight);
        return Vector3.DistanceSquared(local, closest) <= sphereRadius * sphereRadius;
    }

    private static bool SphereIntersectsEllipsoid(Vector3 sphereCenter, float sphereRadius, SceneNode ellipsoid)
    {
        if (!TryGetEllipsoid(ellipsoid, out var center, out var radii, out var rotation)) return false;
        var local = Vector3.TransformNormal(sphereCenter - center, Matrix4x4.Transpose(rotation));
        var closest = ClosestPointOnEllipsoid(local, radii);
        return Vector3.DistanceSquared(local, closest) <= sphereRadius * sphereRadius;
    }

    private static Vector3 ClosestPointOnEllipsoid(Vector3 point, Vector3 radii)
    {
        var normalizedDistance = point.X * point.X / (radii.X * radii.X)
            + point.Y * point.Y / (radii.Y * radii.Y)
            + point.Z * point.Z / (radii.Z * radii.Z);
        if (normalizedDistance <= 1) return point;
        var low = 0f;
        var high = Math.Max(radii.X * Math.Abs(point.X), Math.Max(radii.Y * Math.Abs(point.Y), radii.Z * Math.Abs(point.Z)))
            + Math.Max(radii.X * radii.X, Math.Max(radii.Y * radii.Y, radii.Z * radii.Z));
        for (var iteration = 0; iteration < 48; iteration++)
        {
            var lambda = (low + high) * .5f;
            var x = radii.X * radii.X * point.X / (lambda + radii.X * radii.X);
            var y = radii.Y * radii.Y * point.Y / (lambda + radii.Y * radii.Y);
            var z = radii.Z * radii.Z * point.Z / (lambda + radii.Z * radii.Z);
            var value = x * x / (radii.X * radii.X) + y * y / (radii.Y * radii.Y) + z * z / (radii.Z * radii.Z);
            if (value > 1) low = lambda;
            else high = lambda;
        }
        return new Vector3(
            radii.X * radii.X * point.X / (high + radii.X * radii.X),
            radii.Y * radii.Y * point.Y / (high + radii.Y * radii.Y),
            radii.Z * radii.Z * point.Z / (high + radii.Z * radii.Z));
    }

    private static Vector3 ClosestPointOnCylinder(Vector3 point, float radiusX, float radiusZ, float halfHeight)
    {
        var normalizedRadius = point.X * point.X / (radiusX * radiusX) + point.Z * point.Z / (radiusZ * radiusZ);
        if (normalizedRadius <= 1 && Math.Abs(point.Y) <= halfHeight) return point;
        var closestX = point.X;
        var closestZ = point.Z;
        if (normalizedRadius > 1)
        {
            var low = 0f;
            var high = Math.Max(radiusX * Math.Abs(point.X), radiusZ * Math.Abs(point.Z)) + Math.Max(radiusX * radiusX, radiusZ * radiusZ);
            for (var iteration = 0; iteration < 40; iteration++)
            {
                var lambda = (low + high) * .5f;
                var x = radiusX * radiusX * point.X / (lambda + radiusX * radiusX);
                var z = radiusZ * radiusZ * point.Z / (lambda + radiusZ * radiusZ);
                var value = x * x / (radiusX * radiusX) + z * z / (radiusZ * radiusZ);
                if (value > 1) low = lambda;
                else high = lambda;
            }
            closestX = radiusX * radiusX * point.X / (high + radiusX * radiusX);
            closestZ = radiusZ * radiusZ * point.Z / (high + radiusZ * radiusZ);
        }
        return new Vector3(closestX, Math.Clamp(point.Y, -halfHeight, halfHeight), closestZ);
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
