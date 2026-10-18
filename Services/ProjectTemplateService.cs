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
                AddPart(project, "Emerald Block", [2, .5f, 0], [1, 1, 1], "#6DBF74");
                AddPart(project, "Gold Block", [-2, .5f, 0], [1, 1, 1], "#E6AB54");
                AddPart(project, "Blue Block", [0, 1.5f, 2], [1.5f, 2, 1.5f], "#809BE2");
                break;
        }
        GameProjectStore.EnsureProject(project);
    }

    private static void AddPart(GameProject project, string name, float[] position, float[] size, string color)
    {
        project.Workspace.Children.Add(new SceneNode
        {
            Type = "Part",
            Name = name,
            Position = position,
            Size = size,
            Color = color
        });
    }
}
