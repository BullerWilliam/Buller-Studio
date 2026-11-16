using BullerStudio.Models;

namespace BullerStudio.Services;

public static class PhysicalPropertiesService
{
    private static readonly Dictionary<string, PhysicalPropertiesData> MaterialDefaults = SceneNodeTypes.Materials
        .ToDictionary(material => material, CreateMaterialDefaults, StringComparer.Ordinal);

    public static PhysicalPropertiesData Get(SceneNode part) => part.CustomPhysicalProperties ?? GetMaterialProperties(part.Material);

    public static PhysicalPropertiesData GetMaterialDefaults(string? material)
    {
        var defaults = GetMaterialProperties(material);
        return new PhysicalPropertiesData
        {
            Density = defaults.Density,
            Friction = defaults.Friction,
            Elasticity = defaults.Elasticity,
            FrictionWeight = defaults.FrictionWeight,
            ElasticityWeight = defaults.ElasticityWeight,
            AcousticAbsorption = defaults.AcousticAbsorption
        };
    }

    private static PhysicalPropertiesData GetMaterialProperties(string? material) =>
        material is not null && MaterialDefaults.TryGetValue(material, out var properties) ? properties : MaterialDefaults["Plastic"];

    private static PhysicalPropertiesData CreateMaterialDefaults(string material) => material switch
    {
        "Asphalt" or "RoofShingles" => new() { Density = 2.36f, Elasticity = .2f, Friction = .8f, FrictionWeight = .3f },
        "Basalt" => new() { Density = 2.691f, Elasticity = .15f, Friction = .7f, FrictionWeight = .3f },
        "Cobblestone" => new() { Density = 2.691f, Elasticity = .17f, Friction = .5f },
        "Brick" => new() { Density = 1.922f, Elasticity = .15f, Friction = .8f, FrictionWeight = .3f },
        "Cardboard" => new() { Density = .7f, Elasticity = .05f, ElasticityWeight = 2, Friction = .5f },
        "Carpet" => new() { Density = 1.1f, Elasticity = .25f, ElasticityWeight = 2, Friction = .4f },
        "CeramicTiles" => new() { Density = 2.4f, Elasticity = .2f, Friction = .51f },
        "ClayRoofTiles" => new() { Density = 2, Elasticity = .2f, Friction = .51f },
        "Concrete" => new() { Density = 2.403f, Elasticity = .2f, Friction = .7f, FrictionWeight = .3f },
        "CorrodedMetal" => new() { Density = 7.85f, Elasticity = .2f, Friction = .7f },
        "CrackedLava" => new() { Density = 2.691f, Elasticity = .15f, Friction = .65f },
        "DiamondPlate" => new() { Density = 7.85f, Elasticity = .25f, Friction = .35f },
        "Fabric" => new() { Density = .7f, Elasticity = .05f, Friction = .35f },
        "Foil" => new() { Density = 2.7f, Elasticity = .25f, Friction = .4f },
        "ForceField" or "Glass" => new() { Density = 2.403f, Elasticity = .2f, Friction = .25f },
        "Glacier" => new() { Density = .919f, Elasticity = .15f, Friction = .05f, FrictionWeight = 2 },
        "Ice" => new() { Density = .919f, Elasticity = .15f, Friction = .02f, FrictionWeight = 3 },
        "Granite" => new() { Density = 2.691f, Elasticity = .2f, Friction = .4f },
        "Grass" => new() { Density = .9f, Elasticity = .1f, ElasticityWeight = 1.5f, Friction = .4f },
        "Ground" => new() { Density = .9f, Elasticity = .1f, Friction = .45f },
        "LeafyGrass" => new() { Density = .9f, Elasticity = .1f, ElasticityWeight = 2, Friction = .4f, FrictionWeight = 2 },
        "Leather" => new() { Density = .86f, Elasticity = .25f, Friction = .35f },
        "Limestone" => new() { Density = 2.691f, Elasticity = .15f, Friction = .5f },
        "Marble" => new() { Density = 2.563f, Elasticity = .17f, Friction = .2f },
        "Metal" => new() { Density = 7.85f, Elasticity = .25f, Friction = .4f },
        "Mud" => new() { Density = .9f, Elasticity = .07f, ElasticityWeight = 4, Friction = .3f, FrictionWeight = 3 },
        "Neon" or "Plastic" => new() { Density = .7f, Elasticity = .5f, Friction = .3f },
        "Pavement" => new() { Density = 2.691f, Elasticity = .17f, Friction = .5f, FrictionWeight = .3f },
        "Pebble" => new() { Density = 2.403f, Elasticity = .17f, ElasticityWeight = 1.5f, Friction = .4f },
        "Plaster" => new() { Density = .75f, Elasticity = .2f, Friction = .6f, FrictionWeight = .3f },
        "Rock" => new() { Density = 2.691f, Elasticity = .17f, Friction = .5f },
        "Rubber" => new() { Density = 1.3f, Elasticity = .95f, ElasticityWeight = 2, Friction = 1.5f, FrictionWeight = 3 },
        "Salt" => new() { Density = 2.165f, Elasticity = .05f, Friction = .5f },
        "Sand" => new() { Density = 1.602f, Elasticity = .05f, ElasticityWeight = 2.5f, Friction = .5f, FrictionWeight = 5 },
        "Sandstone" => new() { Density = 2.691f, Elasticity = .15f, Friction = .5f, FrictionWeight = 5 },
        "Slate" => new() { Density = 2.691f, Elasticity = .2f, Friction = .4f },
        "Snow" => new() { Density = .9f, Elasticity = .03f, ElasticityWeight = 4, Friction = .3f, FrictionWeight = 3 },
        "Wood" or "WoodPlanks" => new() { Density = .35f, Elasticity = .2f, Friction = .48f },
        "SmoothPlastic" => new() { Density = .7f, Elasticity = .5f, Friction = .2f },
        _ => new()
    };

    public static float Combine(float first, float firstWeight, float second, float secondWeight)
    {
        var totalWeight = Math.Max(0, firstWeight) + Math.Max(0, secondWeight);
        return totalWeight <= .000001f ? 0
            : (first * Math.Max(0, firstWeight) + second * Math.Max(0, secondWeight)) / totalWeight;
    }
}
