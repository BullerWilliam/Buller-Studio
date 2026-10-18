using System.Text.Json.Serialization;

namespace BullerStudio.Models;

public sealed class GameProject
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "Untitled Game";
    public ProjectSettings Settings { get; set; } = new();
    public SceneNode Workspace { get; set; } = new() { Id = "workspace", Type = "Workspace", Name = "Workspace" };
    public SceneNode Gui { get; set; } = new() { Id = "gui", Type = "Gui", Name = "Gui", Children = [new SceneNode { Id = "screen-gui-main", Type = "ScreenGui", Name = "ScreenGui" }] };
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
    public float[] PositionScale { get; set; } = [0, 0];
    public float[] Rotation { get; set; } = [0, 0, 0];
    public float[] Size { get; set; } = [2, 2, 2];
    public float[] SizeScale { get; set; } = [0, 0];
    public string Color { get; set; } = "#4E9DFF";
    public string FillColor { get; set; } = "#00BFFF";
    public string OutlineColor { get; set; } = "#FFFFFF";
    public float FillTransparency { get; set; } = .5f;
    public float OutlineTransparency { get; set; }
    public string Shape { get; set; } = "Block";
    public string Face { get; set; } = "Front";
    public float StudsPerTileU { get; set; } = 2;
    public float StudsPerTileV { get; set; } = 2;
    public float OffsetStudsU { get; set; }
    public float OffsetStudsV { get; set; }
    public string Material { get; set; } = "Plastic";
    public float Transparency { get; set; }
    public bool Enabled { get; set; } = true;
    public float Brightness { get; set; } = 1;
    public float Range { get; set; } = 16;
    public float Angle { get; set; } = 45;
    public string ActionText { get; set; } = "Interact";
    public string ObjectText { get; set; } = string.Empty;
    public float MaxActivationDistance { get; set; } = 10;
    public float Volume { get; set; } = .5f;
    public bool Looped { get; set; }
    public bool Playing { get; set; }
    public float RollOffMinDistance { get; set; } = 10;
    public float RollOffMaxDistance { get; set; } = 100;
    public string Part0 { get; set; } = string.Empty;
    public string Part1 { get; set; } = string.Empty;
    public string Text { get; set; } = "Text";
    public string TextColor { get; set; } = "#FFFFFF";
    public string StrokeColor { get; set; } = "#FFFFFF";
    public float TextSize { get; set; } = 18;
    public float CornerRadius { get; set; }
    public float StrokeThickness { get; set; } = 1;
    public float LayoutSpacing { get; set; }
    public int TableColumns { get; set; } = 2;
    public float AspectRatio { get; set; } = 1;
    public string LayoutDirection { get; set; } = "Vertical";
    public bool Visible { get; set; } = true;
    public string SkyboxFront { get; set; } = string.Empty;
    public string SkyboxBack { get; set; } = string.Empty;
    public string SkyboxLeft { get; set; } = string.Empty;
    public string SkyboxRight { get; set; } = string.Empty;
    public string SkyboxTop { get; set; } = string.Empty;
    public string SkyboxBottom { get; set; } = string.Empty;
    public bool Anchored { get; set; } = true;
    public bool CanCollide { get; set; } = true;
    public bool CanTouch { get; set; } = true;
    public float[] Velocity { get; set; } = [0, 0, 0];
    public bool CastShadow { get; set; } = true;
    public float Gravity { get; set; } = 24;
    public string Asset { get; set; } = string.Empty;
    public string Texture { get; set; } = string.Empty;
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
