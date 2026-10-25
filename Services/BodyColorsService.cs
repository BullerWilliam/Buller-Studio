using BullerStudio.Models;

namespace BullerStudio.Services;

public static class BodyColorsService
{
    private static readonly (string Property, string[] PartNames)[] BodyParts =
    [
        ("HeadColor3", ["head"]),
        ("TorsoColor3", ["torso", "uppertorso", "lowertorso"]),
        ("LeftArmColor3", ["leftarm", "leftupperarm", "leftlowerarm", "lefthand"]),
        ("RightArmColor3", ["rightarm", "rightupperarm", "rightlowerarm", "righthand"]),
        ("LeftLegColor3", ["leftleg", "leftupperleg", "leftlowerleg", "leftfoot"]),
        ("RightLegColor3", ["rightleg", "rightupperleg", "rightlowerleg", "rightfoot"])
    ];

    public static bool IsColorProperty(string property) => BodyParts.Any(bodyPart => bodyPart.Property.Equals(property, StringComparison.OrdinalIgnoreCase));

    public static string GetColor(SceneNode bodyColors, string property) => property.ToLowerInvariant() switch
    {
        "headcolor3" => bodyColors.HeadColor3,
        "torsocolor3" => bodyColors.TorsoColor3,
        "leftarmcolor3" => bodyColors.LeftArmColor3,
        "rightarmcolor3" => bodyColors.RightArmColor3,
        "leftlegcolor3" => bodyColors.LeftLegColor3,
        "rightlegcolor3" => bodyColors.RightLegColor3,
        _ => "#FFFFFF"
    };

    public static void SetColor(SceneNode bodyColors, string property, string color)
    {
        switch (property.ToLowerInvariant())
        {
            case "headcolor3": bodyColors.HeadColor3 = color; break;
            case "torsocolor3": bodyColors.TorsoColor3 = color; break;
            case "leftarmcolor3": bodyColors.LeftArmColor3 = color; break;
            case "rightarmcolor3": bodyColors.RightArmColor3 = color; break;
            case "leftlegcolor3": bodyColors.LeftLegColor3 = color; break;
            case "rightlegcolor3": bodyColors.RightLegColor3 = color; break;
        }
    }

    public static Dictionary<string, string> BuildOverrides(SceneNode workspace)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var model in GameProjectStore.Flatten(workspace.Children).Where(node => node.Type == "Model"))
        {
            if (!GameProjectStore.Flatten(model.Children).Any(node => node.Type == "Humanoid")) continue;
            var bodyColors = model.Children.FirstOrDefault(node => node.Type == "BodyColors");
            if (bodyColors is null) continue;
            foreach (var part in GameProjectStore.Flatten(model.Children).Where(SceneNodeTypes.IsBasePart))
            {
                var name = part.Name.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
                var bodyPart = BodyParts.FirstOrDefault(candidate => candidate.PartNames.Contains(name, StringComparer.Ordinal));
                if (bodyPart.Property is not null) result[part.Id] = GetColor(bodyColors, bodyPart.Property);
            }
        }
        return result;
    }
}
