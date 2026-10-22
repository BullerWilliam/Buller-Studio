using System.Globalization;
using System.Windows.Media;
using BullerStudio.Models;

namespace BullerStudio.Services;

public static class ParticleSequenceService
{
    public static List<ParticleNumberKeypoint> NormalizeNumber(IEnumerable<ParticleNumberKeypoint>? source, float minimum, float maximum)
    {
        if (source is null) return [];
        var points = source.Take(21).ToList();
        if (points.Count is < 2 or > 20 || points[0].Time != 0 || points[^1].Time != 1) return [];
        var result = new List<ParticleNumberKeypoint>(points.Count);
        var previous = -1f;
        foreach (var point in points)
        {
            if (!float.IsFinite(point.Time) || !float.IsFinite(point.Value) || !float.IsFinite(point.Envelope)
                || point.Time <= previous || point.Time is < 0 or > 1 || point.Envelope < 0) return [];
            result.Add(new ParticleNumberKeypoint(point.Time, Math.Clamp(point.Value, minimum, maximum),
                Math.Clamp(point.Envelope, 0, maximum - minimum)));
            previous = point.Time;
        }
        return result;
    }

    public static List<ParticleColorKeypoint> NormalizeColor(IEnumerable<ParticleColorKeypoint>? source)
    {
        if (source is null) return [];
        var points = source.Take(21).ToList();
        if (points.Count is < 2 or > 20 || points[0].Time != 0 || points[^1].Time != 1) return [];
        var result = new List<ParticleColorKeypoint>(points.Count);
        var previous = -1f;
        foreach (var point in points)
        {
            if (!float.IsFinite(point.Time) || point.Time <= previous || point.Time is < 0 or > 1
                || !TryParseColor(point.Value, out var color)) return [];
            result.Add(new ParticleColorKeypoint(point.Time, $"#{color.R:X2}{color.G:X2}{color.B:X2}"));
            previous = point.Time;
        }
        return result;
    }

    public static float SampleNumber(IReadOnlyList<ParticleNumberKeypoint>? points, float fallback, float age, float random)
    {
        if (points is null || points.Count < 2) return fallback;
        var time = Math.Clamp(age, 0, 1);
        for (var index = 1; index < points.Count; index++)
        {
            var next = points[index];
            if (time > next.Time && index < points.Count - 1) continue;
            var previous = points[index - 1];
            var alpha = Math.Clamp((time - previous.Time) / (next.Time - previous.Time), 0, 1);
            var value = previous.Value + (next.Value - previous.Value) * alpha;
            var envelope = previous.Envelope + (next.Envelope - previous.Envelope) * alpha;
            return value + (random * 2 - 1) * envelope;
        }
        return points[^1].Value;
    }

    public static Color SampleColor(IReadOnlyList<ParticleColorKeypoint>? points, string fallback, float age)
    {
        if (points is null || points.Count < 2) return TryParseColor(fallback, out var baseColor) ? baseColor : Colors.White;
        var time = Math.Clamp(age, 0, 1);
        for (var index = 1; index < points.Count; index++)
        {
            var next = points[index];
            if (time > next.Time && index < points.Count - 1) continue;
            var previous = points[index - 1];
            var alpha = Math.Clamp((time - previous.Time) / (next.Time - previous.Time), 0, 1);
            TryParseColor(previous.Value, out var first);
            TryParseColor(next.Value, out var last);
            return Color.FromRgb((byte)Math.Round(first.R + (last.R - first.R) * alpha),
                (byte)Math.Round(first.G + (last.G - first.G) * alpha),
                (byte)Math.Round(first.B + (last.B - first.B) * alpha));
        }
        TryParseColor(points[^1].Value, out var end);
        return end;
    }

    public static string FormatNumber(IReadOnlyList<ParticleNumberKeypoint> points, float fallback) => points.Count < 2
        ? fallback.ToString(CultureInfo.InvariantCulture)
        : string.Join(", ", points.Select(point => $"{point.Time.ToString(CultureInfo.InvariantCulture)}:{point.Value.ToString(CultureInfo.InvariantCulture)}"
            + (point.Envelope > 0 ? $":{point.Envelope.ToString(CultureInfo.InvariantCulture)}" : string.Empty)));

    public static string FormatColor(IReadOnlyList<ParticleColorKeypoint> points, string fallback) => points.Count < 2
        ? fallback : string.Join(", ", points.Select(point => $"{point.Time.ToString(CultureInfo.InvariantCulture)}:{point.Value}"));

    public static bool TryParseNumberText(string text, float minimum, float maximum, out List<ParticleNumberKeypoint> points)
    {
        points = [];
        var parsed = new List<ParticleNumberKeypoint>();
        foreach (var token in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = token.Split(':', StringSplitOptions.TrimEntries);
            if (fields.Length is < 2 or > 3
                || !float.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time)
                || !float.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return false;
            var envelope = 0f;
            if (fields.Length == 3 && !float.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out envelope)) return false;
            parsed.Add(new ParticleNumberKeypoint(time, value, envelope));
        }
        points = NormalizeNumber(parsed, minimum, maximum);
        return points.Count > 0;
    }

    public static bool TryParseColorText(string text, out List<ParticleColorKeypoint> points)
    {
        points = [];
        var parsed = new List<ParticleColorKeypoint>();
        foreach (var token in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = token.IndexOf(':');
            if (separator < 0 || !float.TryParse(token[..separator], NumberStyles.Float, CultureInfo.InvariantCulture, out var time)) return false;
            parsed.Add(new ParticleColorKeypoint(time, token[(separator + 1)..].Trim()));
        }
        points = NormalizeColor(parsed);
        return points.Count > 0;
    }

    private static bool TryParseColor(string? value, out Color color)
    {
        color = Colors.White;
        if (value is null || value.Length != 7 || value[0] != '#'
            || !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }
}
