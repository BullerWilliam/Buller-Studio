using System.Numerics;

namespace BullerStudio.Services;

internal static class RotationMath
{
    // Match the scene renderer: local Z, then X, then Y (row-vector order).
    public static Matrix4x4 FromDegrees(float[] degrees)
    {
        var factor = MathF.PI / 180;
        return Matrix4x4.CreateRotationZ(degrees[2] * factor)
            * Matrix4x4.CreateRotationX(degrees[0] * factor)
            * Matrix4x4.CreateRotationY(degrees[1] * factor);
    }

    public static float[] ToDegrees(Matrix4x4 matrix)
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
}
