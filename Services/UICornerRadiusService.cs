using System.Windows;
using BullerStudio.Models;

namespace BullerStudio.Services;

public static class UICornerRadiusService
{
    private static readonly string[] IndividualProperties = ["TopLeftRadius", "TopRightRadius", "BottomRightRadius", "BottomLeftRadius"];

    public static bool IsRadiusProperty(string property) => property is "CornerRadius" or "TopLeftRadius" or "TopRightRadius" or "BottomRightRadius" or "BottomLeftRadius";

    public static float[] Get(SceneNode node, string property) => property switch
    {
        "TopLeftRadius" or "CornerRadius" => node.TopLeftRadius ?? [node.CornerRadiusScale, node.CornerRadius],
        "TopRightRadius" => node.TopRightRadius ?? [node.CornerRadiusScale, node.CornerRadius],
        "BottomRightRadius" => node.BottomRightRadius ?? [node.CornerRadiusScale, node.CornerRadius],
        "BottomLeftRadius" => node.BottomLeftRadius ?? [node.CornerRadiusScale, node.CornerRadius],
        _ => [0, 0]
    };

    public static void Set(SceneNode node, string property, float scale, float offset)
    {
        if (!IsRadiusProperty(property) || !float.IsFinite(scale) || !float.IsFinite(offset)) return;
        var value = new[] { Math.Clamp(scale, -100, 100), Math.Clamp(offset, -1000000, 1000000) };
        if (property == "CornerRadius")
        {
            node.TopLeftRadius = (float[])value.Clone();
            node.TopRightRadius = (float[])value.Clone();
            node.BottomRightRadius = (float[])value.Clone();
            node.BottomLeftRadius = (float[])value.Clone();
        }
        else
        {
            EnsureIndividualProperties(node);
            switch (property)
            {
                case "TopLeftRadius": node.TopLeftRadius = value; break;
                case "TopRightRadius": node.TopRightRadius = value; break;
                case "BottomRightRadius": node.BottomRightRadius = value; break;
                case "BottomLeftRadius": node.BottomLeftRadius = value; break;
            }
        }
        node.CornerRadiusScale = node.TopLeftRadius![0];
        node.CornerRadius = node.TopLeftRadius[1];
    }

    public static void Normalize(SceneNode node)
    {
        node.CornerRadiusScale = float.IsFinite(node.CornerRadiusScale) ? Math.Clamp(node.CornerRadiusScale, -100, 100) : 0;
        node.CornerRadius = float.IsFinite(node.CornerRadius) ? Math.Clamp(node.CornerRadius, -1000000, 1000000) : 0;
        if (IndividualProperties.All(property => property switch
            {
                "TopLeftRadius" => node.TopLeftRadius is null,
                "TopRightRadius" => node.TopRightRadius is null,
                "BottomRightRadius" => node.BottomRightRadius is null,
                _ => node.BottomLeftRadius is null
            })) return;
        EnsureIndividualProperties(node);
        node.TopLeftRadius = NormalizePair(node.TopLeftRadius!);
        node.TopRightRadius = NormalizePair(node.TopRightRadius!);
        node.BottomRightRadius = NormalizePair(node.BottomRightRadius!);
        node.BottomLeftRadius = NormalizePair(node.BottomLeftRadius!);
        node.CornerRadiusScale = node.TopLeftRadius[0];
        node.CornerRadius = node.TopLeftRadius[1];
    }

    public static CornerRadius Render(SceneNode node, double width, double height)
    {
        var shortest = Math.Max(0, Math.Min(width, height));
        double Radius(string property)
        {
            var value = Get(node, property);
            return Math.Clamp(value[0] * shortest + value[1], 0, shortest / 2);
        }
        return new CornerRadius(Radius("TopLeftRadius"), Radius("TopRightRadius"), Radius("BottomRightRadius"), Radius("BottomLeftRadius"));
    }

    private static void EnsureIndividualProperties(SceneNode node)
    {
        var fallback = new[] { node.CornerRadiusScale, node.CornerRadius };
        node.TopLeftRadius ??= (float[])fallback.Clone();
        node.TopRightRadius ??= (float[])fallback.Clone();
        node.BottomRightRadius ??= (float[])fallback.Clone();
        node.BottomLeftRadius ??= (float[])fallback.Clone();
    }

    private static float[] NormalizePair(float[] values) =>
        [values.Length > 0 && float.IsFinite(values[0]) ? Math.Clamp(values[0], -100, 100) : 0,
         values.Length > 1 && float.IsFinite(values[1]) ? Math.Clamp(values[1], -1000000, 1000000) : 0];
}
