using System.Text.Json.Serialization;

namespace BullerStudio.Models;

public sealed class GameProject
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "Untitled Game";
    public ProjectSettings Settings { get; set; } = new();
    public SceneNode Workspace { get; set; } = new() { Id = "workspace", Type = "Workspace", Name = "Workspace" };
    public List<ProjectAsset> Assets { get; set; } = [];
    public List<GameScript> Scripts { get; set; } = [];
    [JsonIgnore] public string ProjectPath { get; set; } = string.Empty;
    [JsonIgnore] public bool BuildIncluded { get; set; } = true;
}

public sealed class ProjectSettings
{
    public string Background { get; set; } = "#101522";
    public bool Shadows { get; set; } = true;
}

public sealed class SceneNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "Folder";
    public string Name { get; set; } = "Folder";
    public float[] Position { get; set; } = [0, 0, 0];
    public float[] Rotation { get; set; } = [0, 0, 0];
    public float[] Size { get; set; } = [2, 2, 2];
    public string Color { get; set; } = "#4E9DFF";
    public bool Anchored { get; set; } = true;
    public bool CastShadow { get; set; } = true;
    public string Asset { get; set; } = string.Empty;
    public string CameraMode { get; set; } = "Custom";
    public string Subject { get; set; } = string.Empty;
    public float FieldOfView { get; set; } = 55;
    public List<SceneNode> Children { get; set; } = [];
    [JsonIgnore] public bool IsExplorerVisible { get; set; } = true;
}

public sealed class ProjectAsset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public long Size { get; set; }
}

public sealed class GameScript
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Script";
    public string Source { get; set; } = "print('Hello from Lua X')";
}
