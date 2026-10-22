namespace BullerStudio.Models;

public static class SceneNodeTypes
{
    public static bool IsBasePart(SceneNode? node) => IsBasePartType(node?.Type);

    public static bool IsBasePartType(string? type) => type is "Part" or "MeshPart" or "WedgePart" or "CornerWedgePart" or "SpawnLocation" or "TrussPart" or "Seat";

    public static bool IsLight(SceneNode? node) => node?.Type is "PointLight" or "SpotLight" or "SurfaceLight";

    public static bool IsParticleEffect(SceneNode? node) => node?.Type is "Fire" or "Smoke" or "Sparkles" or "ParticleEmitter";

    public static bool IsSizedParticleEffect(SceneNode? node) => node?.Type is "Fire" or "Smoke";

    public static bool IsValueObject(SceneNode? node) => IsValueObjectType(node?.Type);

    public static bool IsValueObjectType(string? type) => type is "BoolValue" or "IntValue" or "NumberValue" or "StringValue" or "Vector3Value" or "Color3Value" or "ObjectValue";

    public static bool IsFace(string? face) => face is "Front" or "Back" or "Left" or "Right" or "Top" or "Bottom";
}
