namespace BullerStudio.Models;

public static class SceneNodeTypes
{
    public static IReadOnlyList<string> Materials { get; } = [
        "Plastic", "SmoothPlastic", "Neon", "Wood", "WoodPlanks", "Marble", "Basalt", "Slate", "CrackedLava", "Concrete", "Limestone", "Granite", "Pavement", "Brick", "Pebble", "Cobblestone", "Rock", "Sandstone", "CorrodedMetal", "DiamondPlate", "Foil", "Metal", "Grass", "LeafyGrass", "Sand", "Fabric", "Snow", "Mud", "Ground", "Asphalt", "Salt", "Ice", "Glacier", "Glass", "ForceField", "Cardboard", "Carpet", "CeramicTiles", "ClayRoofTiles", "RoofShingles", "Leather", "Plaster", "Rubber"
    ];

    public static IReadOnlyList<string> EnumMaterials { get; } = Materials.Append("Air").ToArray();

    private static readonly HashSet<string> TerrainMaterials = new(StringComparer.Ordinal)
    {
        "WoodPlanks", "Basalt", "Slate", "CrackedLava", "Concrete", "Limestone", "Pavement", "Brick", "Cobblestone", "Rock", "Sandstone",
        "Grass", "LeafyGrass", "Sand", "Snow", "Mud", "Ground", "Asphalt", "Salt", "Ice", "Glacier"
    };

    public static bool IsMaterial(string? material) => material is not null && Materials.Contains(material, StringComparer.Ordinal);

    public static bool IsTerrainMaterial(string? material) => material == "Air" || material is not null && TerrainMaterials.Contains(material);

    public static string TerrainColor(string material) => material switch
    {
        "Asphalt" => "#505454",
        "Basalt" => "#4B4A4A",
        "Brick" => "#8A6149",
        "Cardboard" => "#FFCE98",
        "Carpet" => "#A3A2A5",
        "CeramicTiles" => "#B5AD9C",
        "ClayRoofTiles" => "#FF8E57",
        "Cobblestone" => "#868676",
        "Concrete" => "#989898",
        "CorrodedMetal" => "#688CAD",
        "CrackedLava" => "#FF1843",
        "DiamondPlate" or "Foil" or "Metal" => "#A8AFB0",
        "Fabric" => "#C2C1A8",
        "ForceField" => "#A3A2A5",
        "Glacier" => "#DDE4E5",
        "Glass" => "#8AA7A8",
        "Granite" => "#95928B",
        "Grass" => "#6F7E3E",
        "Ground" => "#8C8268",
        "Ice" => "#CCD2DF",
        "LeafyGrass" => "#6A8640",
        "Leather" => "#6E4935",
        "Limestone" => "#FFF3C0",
        "Marble" => "#7A7A7A",
        "Mud" => "#797062",
        "Pavement" => "#8F9087",
        "Pebble" => "#7A7A76",
        "Plaster" => "#CC8E69",
        "Plastic" or "SmoothPlastic" => "#A3A2A5",
        "Rock" => "#636466",
        "RoofShingles" => "#424242",
        "Rubber" => "#202020",
        "Salt" => "#FFFFFE",
        "Sand" => "#CFCBA7",
        "Sandstone" => "#947C5F",
        "Slate" => "#585956",
        "Snow" => "#EBFDFF",
        "Wood" or "WoodPlanks" => "#AC946C",
        _ => "#547A3F"
    };

    public static bool IsBasePart(SceneNode? node) => IsBasePartType(node?.Type);

    public static bool IsBasePartType(string? type) => type is "Part" or "MeshPart" or "WedgePart" or "CornerWedgePart" or "SpawnLocation" or "TrussPart" or "Seat" or "VehicleSeat";

    public static bool IsLight(SceneNode? node) => node?.Type is "PointLight" or "SpotLight" or "SurfaceLight";

    public static bool IsPostEffect(SceneNode? node) => node?.Type is "BlurEffect";

    public static bool IsPostEffectClass(SceneNode? node) => node?.Type is "BlurEffect" or "ColorCorrectionEffect";

    public static bool IsSurfaceAppearance(SceneNode? node) => node?.Type == "SurfaceAppearance";

    public static bool IsParticleEffect(SceneNode? node) => node?.Type is "Fire" or "Smoke" or "Sparkles" or "ParticleEmitter";

    public static bool IsSizedParticleEffect(SceneNode? node) => node?.Type is "Fire" or "Smoke";

    public static bool IsValueObject(SceneNode? node) => IsValueObjectType(node?.Type);

    public static bool IsValueObjectType(string? type) => type is "BoolValue" or "IntValue" or "NumberValue" or "StringValue" or "Vector2Value" or "Vector3Value" or "Color3Value" or "ObjectValue";

    public static bool IsBaseScript(SceneNode? node) => node?.Type is "Script" or "LocalScript";

    public static bool IsFace(string? face) => face is "Front" or "Back" or "Left" or "Right" or "Top" or "Bottom";
}
