namespace BullerStudio.Models;

public static class SceneNodeTypes
{
    public static bool IsBasePart(SceneNode? node) => IsBasePartType(node?.Type);

    public static bool IsBasePartType(string? type) => type is "Part" or "MeshPart" or "WedgePart" or "CornerWedgePart" or "SpawnLocation" or "TrussPart";

    public static bool IsLight(SceneNode? node) => node?.Type is "PointLight" or "SpotLight" or "SurfaceLight";

    public static bool IsFace(string? face) => face is "Front" or "Back" or "Left" or "Right" or "Top" or "Bottom";
}
