using BullerStudio.Models;
using HelixToolkit.SharpDX.Core;
using SharpDX;

namespace BullerStudio.Services;

public sealed record TrailSample(float Time, Vector3 Point0, Vector3 Point1);

public static class TrailGeometryService
{
    public static MeshGeometry3D Build(SceneNode trail, IReadOnlyList<TrailSample> samples, float now)
    {
        var builder = new MeshBuilder();
        var colors = new Color4Collection();
        if (samples.Count < 2) return builder.ToMeshGeometry3D();
        for (var index = 1; index < samples.Count; index++)
        {
            var older = samples[index - 1];
            var newer = samples[index];
            var oldMid = (older.Point0 + older.Point1) * .5f;
            var newMid = (newer.Point0 + newer.Point1) * .5f;
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
            var oldTint = SampleTint(trail, oldAge);
            var newTint = SampleTint(trail, newAge);
            builder.AddQuad(old0, old1, new1, new0);
            colors.Add(oldTint); colors.Add(oldTint); colors.Add(newTint); colors.Add(newTint);
            builder.AddQuad(new0, new1, old1, old0);
            colors.Add(newTint); colors.Add(newTint); colors.Add(oldTint); colors.Add(oldTint);
        }
        var geometry = builder.ToMeshGeometry3D();
        geometry.Colors = colors;
        return geometry;
    }

    private static Color4 SampleTint(SceneNode trail, float age)
    {
        var color = ParticleSequenceService.SampleColor(trail.ColorSequence, trail.Color, age);
        var transparency = ParticleSequenceService.SampleNumber(trail.TransparencySequence, trail.Transparency, age, .5f);
        return new Color4(color.R / 255f, color.G / 255f, color.B / 255f, 1 - Math.Clamp(transparency, 0, 1));
    }
}
