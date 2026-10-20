using System.Numerics;
using BullerStudio.Models;

namespace BullerStudio.Services;

internal static class CollisionGeometry
{
    private const float AxisEpsilon = 1e-8f;

    public static bool Intersects(SceneNode first, SceneNode second)
    {
        var a = new Box(first);
        var b = new Box(second);
        var difference = b.Center - a.Center;
        foreach (var axis in SeparatingAxes(a, b))
        {
            var distance = Math.Abs(Vector3.Dot(difference, axis));
            if (distance > a.RadiusOn(axis) + b.RadiusOn(axis)) return false;
        }
        return true;
    }

    public static float TopY(SceneNode node)
    {
        var box = new Box(node);
        return box.Center.Y + box.RadiusOn(Vector3.UnitY);
    }

    // Height of the upper box surface directly above a world-space X/Z point.
    public static float? TopYAt(SceneNode node, float x, float z)
    {
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
    }
}
