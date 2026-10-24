using BullerStudio.Models;

namespace BullerStudio.Services;

public static class UIShadowService
{
    public static void Normalize(SceneNode node)
    {
        node.ShadowBlurRadius = NormalizeUDim(node.ShadowBlurRadius, false);
        node.ShadowOffsetScale = NormalizePair(node.ShadowOffsetScale);
        node.ShadowOffset = NormalizePair(node.ShadowOffset);
        node.ShadowSpreadScale = NormalizePair(node.ShadowSpreadScale);
        node.ShadowSpread = NormalizePair(node.ShadowSpread);
        if (node.ShadowMode is not ("Shape" or "Text")) node.ShadowMode = "Shape";
        node.Transparency = float.IsFinite(node.Transparency) ? Math.Clamp(node.Transparency, 0, 1) : 0;
        node.ZIndex = Math.Min(0, node.ZIndex);
    }

    public static float[] NormalizeUDim(float[]? value, bool allowNegative)
    {
        var result = NormalizePair(value);
        if (!allowNegative)
        {
            result[0] = Math.Max(0, result[0]);
            result[1] = Math.Max(0, result[1]);
        }
        return result;
    }

    public static float[] NormalizePair(float[]? value) =>
        [value is { Length: > 0 } && float.IsFinite(value[0]) ? Math.Clamp(value[0], -1000, 1000) : 0,
         value is { Length: > 1 } && float.IsFinite(value[1]) ? Math.Clamp(value[1], -1000000, 1000000) : 0];

    public static double ResolveBlur(SceneNode node, double width, double height) =>
        Math.Clamp(node.ShadowBlurRadius[0] * Math.Min(width, height) + node.ShadowBlurRadius[1], 0, 200);

    public static (double X, double Y) ResolveOffset(SceneNode node, double width, double height) =>
        (node.ShadowOffsetScale[0] * width + node.ShadowOffset[0], node.ShadowOffsetScale[1] * height + node.ShadowOffset[1]);

    public static (double X, double Y) ResolveSpread(SceneNode node, double width, double height) =>
        (node.ShadowSpreadScale[0] * width + node.ShadowSpread[0], node.ShadowSpreadScale[1] * height + node.ShadowSpread[1]);
}
