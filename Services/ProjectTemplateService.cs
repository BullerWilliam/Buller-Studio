using BullerStudio.Models;

namespace BullerStudio.Services;

public static class ProjectTemplateService
{
    public static GameProject CreatePreview(string template)
    {
        var project = GameProjectStore.NewProject(template, string.Empty);
        Apply(project, template);
        return project;
    }

    public static void Apply(GameProject project, string template)
    {
        switch (template)
        {
            case "Empty World":
                project.Workspace.Children.RemoveAll(node => node.Type != "Camera");
                break;
            case "Asset Playground":
                var showcase = new SceneNode { Type = "Model", Name = "Shape Showcase" };
                project.Workspace.Children.Add(showcase);
                AddPart(showcase, "Emerald Block", [2, .5f, 0], [1, 1, 1], "#6DBF74");
                var cylinder = AddPart(showcase, "Gold Cylinder", [-2, .5f, 0], [1, 1, 1], "#E6AB54");
                cylinder.Shape = "Cylinder";
                cylinder.Material = "Metal";
                cylinder.Children.Add(new SceneNode { Type = "SurfaceLight", Name = "Top Light", Face = "Top", Color = "#E6AB54", Range = 12, Angle = 110 });
                var ball = AddPart(showcase, "Neon Ball", [0, 1.5f, 2], [1.5f, 1.5f, 1.5f], "#809BE2");
                ball.Shape = "Ball";
                ball.Material = "Neon";
                ball.Children.Add(new SceneNode { Type = "PointLight", Name = "Glow", Color = "#809BE2", Range = 14 });
                showcase.Children.Add(new SceneNode { Type = "WedgePart", Name = "Wedge", Position = [4, .5f, 2], Size = [2, 1, 2], Color = "#D6866A" });
                showcase.Children.Add(new SceneNode { Type = "TrussPart", Name = "Truss", Position = [-5, 3, 2], Size = [2, 6, 2], Color = "#AD9682", Material = "Metal" });
                showcase.Children.Add(new SceneNode { Type = "SpawnLocation", Name = "Spawn Pad", Position = [5, .5f, -4], Size = [6, 1, 6], Color = "#72BF9D", Material = "Metal" });
                break;
        }
        GameProjectStore.EnsureProject(project);
    }

    private static SceneNode AddPart(SceneNode parent, string name, float[] position, float[] size, string color)
    {
        var part = new SceneNode
        {
            Type = "Part",
            Name = name,
            Position = position,
            Size = size,
            Color = color
        };
        parent.Children.Add(part);
        return part;
    }
}
