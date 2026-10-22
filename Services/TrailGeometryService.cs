using BullerStudio.Models;
using HelixToolkit.SharpDX.Core;
using SharpDX;

namespace BullerStudio.Services;

public sealed record TrailSample(float Time, Vector3 Point0, Vector3 Point1, float Distance = 0);

public static class TrailGeometryService
{
    public static void AdvanceSamples(SceneNode trail, List<TrailSample> samples, TrailSample? current, float now)
    {
        samples.RemoveAll(sample => now - sample.Time >= trail.TrailLifetime);
        if (!trail.Enabled || current is null) return;
        if (samples.Count == 0 || Math.Max(Vector3.Distance(samples[^1].Point0, current.Point0),
                Vector3.Distance(samples[^1].Point1, current.Point1)) >= trail.TrailMinLength)
        {
            var distance = samples.Count == 0 ? 0 : samples[^1].Distance +
                Vector3.Distance(Midpoint(samples[^1]), Midpoint(current));
            samples.Add(current with { Distance = distance });
        }
        if (trail.TrailMaxLength > 0)
            while (samples.Count > 1 && PathLength(samples) > trail.TrailMaxLength) samples.RemoveAt(0);
    }

    private static float PathLength(IReadOnlyList<TrailSample> samples)
    {
        var length = 0f;
        for (var index = 1; index < samples.Count; index++)
            length += Vector3.Distance((samples[index - 1].Point0 + samples[index - 1].Point1) * .5f,
                (samples[index].Point0 + samples[index].Point1) * .5f);
        return length;
    }

    public static MeshGeometry3D Build(SceneNode trail, IReadOnlyList<TrailSample> samples, float now, Vector3? cameraPosition = null)
    {
        var builder = new MeshBuilder();
        var colors = new Color4Collection();
        var uv = new Vector2Collection();
        if (samples.Count < 2) return builder.ToMeshGeometry3D();
        var firstDistance = samples[0].Distance;
        var lastDistance = samples[^1].Distance;
        for (var index = 1; index < samples.Count; index++)
        {
            var older = samples[index - 1];
            var newer = samples[index];
            var oldMid = Midpoint(older);
            var newMid = Midpoint(newer);
            if (Vector3.DistanceSquared(oldMid, newMid) < .000001f) continue;
            var oldAge = Math.Clamp((now - older.Time) / trail.TrailLifetime, 0, 1);
            var newAge = Math.Clamp((now - newer.Time) / trail.TrailLifetime, 0, 1);
            var oldWidth = Math.Clamp(ParticleSequenceService.SampleNumber(trail.TrailWidthSequence, trail.TrailWidth, oldAge, .5f), 0, 1);
            var newWidth = Math.Clamp(ParticleSequenceService.SampleNumber(trail.TrailWidthSequence, trail.TrailWidth, newAge, .5f), 0, 1);
            if (oldWidth <= 0 && newWidth <= 0) continue;
            var old0 = Vector3.Lerp(oldMid, older.Point0, oldWidth);
            var old1 = Vector3.Lerp(oldMid, older.Point1, oldWidth);
            var new0 = Vector3.Lerp(newMid, newer.Point0, newWidth);
            var new1 = Vector3.Lerp(newMid, newer.Point1, newWidth);
            if (trail.FaceCamera && cameraPosition is { } eye)
            {
                (old0, old1) = FaceSection(oldMid, older.Point1 - older.Point0, oldWidth, newMid - oldMid, eye);
                (new0, new1) = FaceSection(newMid, newer.Point1 - newer.Point0, newWidth, newMid - oldMid, eye);
            }
            var oldV = TextureCoordinate(trail, older.Distance, firstDistance, lastDistance);
            var newV = TextureCoordinate(trail, newer.Distance, firstDistance, lastDistance);
            var oldTint = SampleTint(trail, oldAge);
            var newTint = SampleTint(trail, newAge);
            builder.AddQuad(old0, old1, new1, new0);
            colors.Add(oldTint); colors.Add(oldTint); colors.Add(newTint); colors.Add(newTint);
            uv.Add(new Vector2(0, oldV)); uv.Add(new Vector2(1, oldV)); uv.Add(new Vector2(1, newV)); uv.Add(new Vector2(0, newV));
            builder.AddQuad(new0, new1, old1, old0);
            colors.Add(newTint); colors.Add(newTint); colors.Add(oldTint); colors.Add(oldTint);
            uv.Add(new Vector2(0, newV)); uv.Add(new Vector2(1, newV)); uv.Add(new Vector2(1, oldV)); uv.Add(new Vector2(0, oldV));
        }
        var geometry = builder.ToMeshGeometry3D();
        geometry.Colors = colors;
        geometry.TextureCoordinates = uv;
        return geometry;
    }

    private static Vector3 Midpoint(TrailSample sample) => (sample.Point0 + sample.Point1) * .5f;

    private static float TextureCoordinate(SceneNode trail, float distance, float first, float last) => trail.TextureMode switch
    {
        "Wrap" => (distance - first) / Math.Max(.01f, trail.TextureLength),
        "Static" => distance / Math.Max(.01f, trail.TextureLength),
        _ => (distance - first) / Math.Max(.0001f, last - first) * trail.TextureLength
    };

    private static (Vector3 Start, Vector3 End) FaceSection(Vector3 middle, Vector3 originalWidth,
        float scale, Vector3 tangent, Vector3 cameraPosition)
    {
        var towardCamera = cameraPosition - middle;
        var side = Vector3.Cross(tangent, towardCamera);
        if (side.LengthSquared() < .000001f || originalWidth.LengthSquared() < .000001f)
            return (middle - originalWidth * scale * .5f, middle + originalWidth * scale * .5f);
        side.Normalize();
        if (Vector3.Dot(side, originalWidth) < 0) side = -side;
        var halfWidth = side * (originalWidth.Length() * scale * .5f);
        return (middle - halfWidth, middle + halfWidth);
    }

    private static Color4 SampleTint(SceneNode trail, float age)
    {
        var color = ParticleSequenceService.SampleColor(trail.ColorSequence, trail.Color, age);
        var transparency = ParticleSequenceService.SampleNumber(trail.TransparencySequence, trail.Transparency, age, .5f);
        return new Color4(color.R / 255f, color.G / 255f, color.B / 255f, 1 - Math.Clamp(transparency, 0, 1));
    }
}
