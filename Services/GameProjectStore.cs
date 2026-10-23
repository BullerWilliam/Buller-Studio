using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BullerStudio.Models;

namespace BullerStudio.Services;

public static class GameProjectStore
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static string ProjectsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Buller Studio Projects");
    public static string GameFileName => "game.buller.json";

    public static async Task<IReadOnlyList<ProjectSummary>> ListAsync()
    {
        Directory.CreateDirectory(ProjectsRoot);
        var result = new List<ProjectSummary>();
        foreach (var directory in Directory.EnumerateDirectories(ProjectsRoot))
        {
            var file = Path.Combine(directory, GameFileName);
            if (!File.Exists(file)) continue;
            try
            {
                var project = await LoadAsync(directory);
                result.Add(new ProjectSummary(project.Name, directory, File.GetLastWriteTime(file)));
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return result.OrderByDescending(project => project.LastModified).ToArray();
    }

    public static async Task<GameProject> CreateAsync(string name)
    {
        Directory.CreateDirectory(ProjectsRoot);
        var folderName = SanitizeName(name);
        var folder = Path.Combine(ProjectsRoot, folderName);
        for (var suffix = 2; Directory.Exists(folder); suffix++) folder = Path.Combine(ProjectsRoot, $"{folderName} {suffix}");
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        var project = NewProject(Path.GetFileName(folder), folder);
        await SaveAsync(project);
        return project;
    }

    public static async Task<GameProject> LoadAsync(string folder)
    {
        var file = Path.Combine(folder, GameFileName);
        var project = JsonSerializer.Deserialize<GameProject>(await File.ReadAllTextAsync(file), JsonOptions)
            ?? throw new InvalidDataException("The project file is empty or invalid.");
        project.ProjectPath = folder;
        EnsureProject(project);
        return project;
    }

    public static async Task SaveAsync(GameProject project)
    {
        EnsureProject(project);
        Directory.CreateDirectory(project.ProjectPath);
        Directory.CreateDirectory(Path.Combine(project.ProjectPath, "assets"));
        var file = Path.Combine(project.ProjectPath, GameFileName);
        var temporary = file + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(project, JsonOptions));
        File.Move(temporary, file, true);
    }

    public static async Task<IReadOnlyList<ProjectAsset>> ImportAssetsAsync(GameProject project, IEnumerable<string> sourceFiles, string relativeFolder = "")
    {
        var folderPart = (relativeFolder ?? string.Empty).Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var fullAssetsFolder = Path.GetFullPath(Path.Combine(project.ProjectPath, "assets"));
        var targetFolder = EnsureAssetsDirectory(project, folderPart);
        var imported = new List<ProjectAsset>();
        foreach (var source in sourceFiles)
        {
            var name = Path.GetFileName(source);
            var destination = Path.Combine(targetFolder, name);
            var suffix = 2;
            while (File.Exists(destination))
            {
                var extension = Path.GetExtension(name);
                destination = Path.Combine(targetFolder, $"{Path.GetFileNameWithoutExtension(name)}-{suffix++}{extension}");
            }
            await using (var input = File.OpenRead(source))
            await using (var output = File.Create(destination)) await input.CopyToAsync(output);
            var info = new FileInfo(destination);
            var relativeAssetPath = Path.Combine("assets", Path.GetRelativePath(fullAssetsFolder, destination));
            var asset = new ProjectAsset { Name = info.Name, RelativePath = relativeAssetPath, Type = info.Extension.TrimStart('.').ToLowerInvariant(), Size = info.Length };
            project.Assets.Add(asset);
            imported.Add(asset);
        }
        await SaveAsync(project);
        return imported;
    }

    public static async Task MoveAssetAsync(GameProject project, ProjectAsset asset, string relativeFolder)
    {
        var assetsRoot = Path.GetFullPath(Path.Combine(project.ProjectPath, "assets"));
        var folderPart = (relativeFolder ?? string.Empty).Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var targetFolder = EnsureAssetsDirectory(project, folderPart);

        var source = GetSafeAssetFilePath(project, asset.RelativePath);
        if (string.Equals(Path.GetDirectoryName(source), targetFolder, StringComparison.OrdinalIgnoreCase)) return;

        var name = Path.GetFileName(source);
        var destination = Path.Combine(targetFolder, name);
        var suffix = 2;
        while (File.Exists(destination))
        {
            var extension = Path.GetExtension(name);
            destination = Path.Combine(targetFolder, $"{Path.GetFileNameWithoutExtension(name)}-{suffix++}{extension}");
        }

        var oldRelativePath = asset.RelativePath;
        var oldName = asset.Name;
        File.Move(source, destination);
        var newRelativePath = Path.Combine("assets", Path.GetRelativePath(assetsRoot, destination));
        var newAssetsRelativePath = Path.GetRelativePath(assetsRoot, destination).Replace('\\', '/');
        var oldAssetsRelativePath = oldRelativePath.Replace('\\', '/');
        if (oldAssetsRelativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) oldAssetsRelativePath = oldAssetsRelativePath[7..];
        var changedReferences = new List<(SceneNode Node, string Property, string Value)>();
        foreach (var node in Flatten(project))
        {
            UpdateReference(node, "Asset", node.Asset, newRelativePath);
            UpdateReference(node, "Texture", node.Texture, newAssetsRelativePath);
            UpdateReference(node, "SkyboxFront", node.SkyboxFront, newAssetsRelativePath);
            UpdateReference(node, "SkyboxBack", node.SkyboxBack, newAssetsRelativePath);
            UpdateReference(node, "SkyboxLeft", node.SkyboxLeft, newAssetsRelativePath);
            UpdateReference(node, "SkyboxRight", node.SkyboxRight, newAssetsRelativePath);
            UpdateReference(node, "SkyboxTop", node.SkyboxTop, newAssetsRelativePath);
            UpdateReference(node, "SkyboxBottom", node.SkyboxBottom, newAssetsRelativePath);
        }
        asset.RelativePath = newRelativePath;
        asset.Name = Path.GetFileName(destination);
        try
        {
            await SaveAsync(project);
        }
        catch
        {
            asset.RelativePath = oldRelativePath;
            asset.Name = oldName;
            foreach (var (node, property, value) in changedReferences) SetReference(node, property, value);
            if (File.Exists(destination) && !File.Exists(source)) File.Move(destination, source);
            throw;
        }

        void UpdateReference(SceneNode node, string property, string value, string replacement)
        {
            var normalized = value.Replace('\\', '/');
            if (normalized.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) normalized = normalized[7..];
            if (!string.Equals(normalized, oldAssetsRelativePath, StringComparison.OrdinalIgnoreCase)) return;
            changedReferences.Add((node, property, value));
            SetReference(node, property, replacement);
        }

        static void SetReference(SceneNode node, string property, string value)
        {
            switch (property)
            {
                case "Asset": node.Asset = value; break;
                case "Texture": node.Texture = value; break;
                case "SkyboxFront": node.SkyboxFront = value; break;
                case "SkyboxBack": node.SkyboxBack = value; break;
                case "SkyboxLeft": node.SkyboxLeft = value; break;
                case "SkyboxRight": node.SkyboxRight = value; break;
                case "SkyboxTop": node.SkyboxTop = value; break;
                case "SkyboxBottom": node.SkyboxBottom = value; break;
            }
        }
    }

    private static bool IsInsideDirectory(string root, string path) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string EnsureAssetsDirectory(GameProject project, string relativeDirectory = "")
    {
        var root = Path.GetFullPath(Path.Combine(project.ProjectPath, "assets"));
        Directory.CreateDirectory(root);
        EnsureNoReparsePoint(root);
        var relative = (relativeDirectory ?? string.Empty).Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Asset folders must be relative to the project assets directory.");
        var directory = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsInsideDirectory(root, directory)) throw new InvalidDataException("Asset folders must stay inside the project assets directory.");
        if (string.Equals(root, directory, StringComparison.OrdinalIgnoreCase)) return root;

        var current = root;
        foreach (var segment in Path.GetRelativePath(root, directory).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) && !Directory.Exists(current)) throw new InvalidDataException("An asset folder path contains a file.");
            if (!Directory.Exists(current)) Directory.CreateDirectory(current);
            EnsureNoReparsePoint(current);
        }
        return directory;
    }

    public static string GetSafeAssetFilePath(GameProject project, string relativePath)
    {
        var root = EnsureAssetsDirectory(project);
        var path = Path.GetFullPath(Path.Combine(project.ProjectPath, relativePath));
        if (!IsInsideDirectory(root, path)) throw new InvalidDataException("The asset file is outside the project assets directory.");
        var segments = Path.GetRelativePath(root, path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) throw new InvalidDataException("The asset file path is invalid.");
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            var isFile = index == segments.Length - 1;
            if (isFile ? !File.Exists(current) : !Directory.Exists(current)) throw new InvalidDataException("The asset file or one of its folders is missing.");
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Assets cannot be moved through symbolic links or junctions.");
            if (isFile == ((attributes & FileAttributes.Directory) != 0)) throw new InvalidDataException("The asset file path is invalid.");
        }
        return path;
    }

    public static string GetSafeAssetFilePathInAssets(GameProject project, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) throw new InvalidDataException("The asset file path is empty.");
        var relative = relativePath.Replace('\\', '/');
        if (relative.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) relative = relative[7..];
        return GetSafeAssetFilePath(project, Path.Combine("assets", relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static void EnsureNoReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Asset folders cannot be symbolic links or junctions.");
    }

    public static void EnsureProject(GameProject project)
    {
        project.Assets ??= [];
        foreach (var asset in project.Assets)
        {
            asset.Name ??= Path.GetFileName(asset.RelativePath ?? string.Empty) ?? string.Empty;
            asset.RelativePath ??= string.Empty;
            var extensionType = Path.GetExtension(asset.RelativePath).TrimStart('.').ToLowerInvariant();
            asset.Type = extensionType.Length > 0
                ? extensionType
                : (asset.Type ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        }
        project.Scripts ??= [];
        project.Settings ??= new ProjectSettings();
        project.Workspace ??= new SceneNode { Id = "workspace", Type = "Workspace", Name = "Workspace" };
        project.Workspace.Children ??= [];
        project.Workspace.Gravity = float.IsFinite(project.Workspace.Gravity) ? Math.Clamp(project.Workspace.Gravity, 0, 1000) : 24;
        project.Players ??= new SceneNode { Id = "players", Type = "Players", Name = "Players" };
        project.Players.Id = "players";
        project.Players.Type = "Players";
        project.Players.Name = "Players";
        project.Players.Children ??= [];
        project.Lighting ??= new SceneNode { Id = "lighting", Type = "Lighting", Name = "Lighting", Ambient = "#808080", ClockTime = 14, Children = [new SceneNode { Id = "sky-main", Type = "Sky", Name = "Sky" }] };
        project.Lighting.Id = "lighting";
        project.Lighting.Type = "Lighting";
        project.Lighting.Name = "Lighting";
        project.Lighting.Children ??= [];
        if (project.Lighting.Ambient is null || !System.Text.RegularExpressions.Regex.IsMatch(project.Lighting.Ambient, "^#[0-9A-Fa-f]{6}$")) project.Lighting.Ambient = "#808080";
        project.Lighting.ClockTime = float.IsFinite(project.Lighting.ClockTime) ? Math.Clamp(project.Lighting.ClockTime, 0, 24) : 14;
        project.Lighting.Brightness = float.IsFinite(project.Lighting.Brightness) ? Math.Clamp(project.Lighting.Brightness, 0, 8) : 1;
        project.ReplicatedStorage ??= new SceneNode { Id = "replicated-storage", Type = "ReplicatedStorage", Name = "ReplicatedStorage" };
        project.ReplicatedStorage.Id = "replicated-storage";
        project.ReplicatedStorage.Type = "ReplicatedStorage";
        project.ReplicatedStorage.Name = "ReplicatedStorage";
        project.ReplicatedStorage.Children ??= [];
        project.ServerScriptService ??= new SceneNode { Id = "server-script-service", Type = "ServerScriptService", Name = "ServerScriptService" };
        project.ServerScriptService.Id = "server-script-service";
        project.ServerScriptService.Type = "ServerScriptService";
        project.ServerScriptService.Name = "ServerScriptService";
        project.ServerScriptService.Children ??= [];
        project.ServerStorage ??= new SceneNode { Id = "server-storage", Type = "ServerStorage", Name = "ServerStorage" };
        project.ServerStorage.Id = "server-storage";
        project.ServerStorage.Type = "ServerStorage";
        project.ServerStorage.Name = "ServerStorage";
        project.ServerStorage.Children ??= [];
        project.Gui ??= new SceneNode { Id = "gui", Type = "Gui", Name = "Gui" };
        project.Gui.Id = "gui";
        project.Gui.Type = "Gui";
        project.Gui.Name = "Gui";
        project.Gui.Children ??= [];
        if (!project.Gui.Children.Any(node => node.Type == "ScreenGui"))
            project.Gui.Children.Add(new SceneNode { Id = Guid.NewGuid().ToString("N"), Type = "ScreenGui", Name = "ScreenGui" });
        foreach (var node in Flatten(project.Workspace.Children).Concat(Flatten(project.Players.Children)).Concat(Flatten(project.Lighting.Children)).Concat(Flatten(project.ReplicatedStorage.Children)).Concat(Flatten(project.ServerScriptService.Children)).Concat(Flatten(project.ServerStorage.Children)).Concat(Flatten(project.Gui.Children)))
        {
            node.Children ??= [];
            node.Position = EnsureVector(node.Position, [0, 0, 0]);
            node.Rotation = EnsureVector(node.Rotation, [0, 0, 0]);
            node.Size = EnsureVector(node.Size, [2, 2, 2]);
            node.Velocity = EnsureVector(node.Velocity, [0, 0, 0]);
            node.PositionScale = EnsurePair(node.PositionScale);
            node.SizeScale = EnsurePair(node.SizeScale);
            node.PaddingLeft = EnsurePair(node.PaddingLeft);
            node.PaddingTop = EnsurePair(node.PaddingTop);
            node.PaddingRight = EnsurePair(node.PaddingRight);
            node.PaddingBottom = EnsurePair(node.PaddingBottom);
            node.CellSize = node.CellSize is null ? [100, 100] : EnsurePair(node.CellSize);
            node.CellSizeScale = EnsurePair(node.CellSizeScale);
            node.CellPadding = EnsurePair(node.CellPadding);
            node.CellPaddingScale = EnsurePair(node.CellPaddingScale);
            node.MinSize = EnsurePair(node.MinSize);
            node.MaxSize = node.MaxSize is null ? [1000000, 1000000] : EnsurePair(node.MaxSize);
            for (var axis = 0; axis < 2; axis++)
            {
                node.MinSize[axis] = float.IsFinite(node.MinSize[axis]) ? Math.Clamp(node.MinSize[axis], 0, 1000000) : 0;
                node.MaxSize[axis] = float.IsFinite(node.MaxSize[axis]) ? Math.Clamp(node.MaxSize[axis], node.MinSize[axis], 1000000) : 1000000;
            }
            if (node.FillDirection is not ("Horizontal" or "Vertical")) node.FillDirection = "Horizontal";
            node.FillDirectionMaxCells = Math.Clamp(node.FillDirectionMaxCells, 0, 1000);
            node.Scale = float.IsFinite(node.Scale) ? Math.Clamp(node.Scale, .01f, 100) : 1;
            if (node.Shape is not ("Block" or "Ball" or "Cylinder")) node.Shape = "Block";
            if (!SceneNodeTypes.IsFace(node.Face)) node.Face = "Front";
            node.StudsPerTileU = float.IsFinite(node.StudsPerTileU) ? Math.Clamp(node.StudsPerTileU, .05f, 1000) : 2;
            node.StudsPerTileV = float.IsFinite(node.StudsPerTileV) ? Math.Clamp(node.StudsPerTileV, .05f, 1000) : 2;
            node.OffsetStudsU = float.IsFinite(node.OffsetStudsU) ? Math.Clamp(node.OffsetStudsU, -10000, 10000) : 0;
            node.OffsetStudsV = float.IsFinite(node.OffsetStudsV) ? Math.Clamp(node.OffsetStudsV, -10000, 10000) : 0;
            if (node.Material is not ("Plastic" or "Metal" or "Neon" or "Glass" or "Wood" or "Concrete")) node.Material = "Plastic";
            node.Transparency = float.IsFinite(node.Transparency) ? Math.Clamp(node.Transparency, 0, 1) : 0;
            node.BackgroundTransparency = float.IsFinite(node.BackgroundTransparency) ? Math.Clamp(node.BackgroundTransparency, 0, 1) : 0;
            node.TextTransparency = float.IsFinite(node.TextTransparency) ? Math.Clamp(node.TextTransparency, 0, 1) : 0;
            node.ImageTransparency = float.IsFinite(node.ImageTransparency) ? Math.Clamp(node.ImageTransparency, 0, 1) : 0;
            node.TextSize = float.IsFinite(node.TextSize) ? Math.Clamp(node.TextSize, 1, 200) : 18;
            node.LayoutSpacing = float.IsFinite(node.LayoutSpacing) ? Math.Clamp(node.LayoutSpacing, 0, 1000000) : 0;
            node.MinTextSize = float.IsFinite(node.MinTextSize) ? Math.Clamp(node.MinTextSize, 1, 1000) : 1;
            node.MaxTextSize = float.IsFinite(node.MaxTextSize) ? Math.Clamp(node.MaxTextSize, node.MinTextSize, 1000) : 1000;
            node.AspectRatio = float.IsFinite(node.AspectRatio) ? Math.Clamp(node.AspectRatio, .001f, 1000) : 1;
            if (node.AspectType is not ("FitWithinMaxSize" or "ScaleWithParentSize")) node.AspectType = "FitWithinMaxSize";
            if (node.DominantAxis is not ("Width" or "Height")) node.DominantAxis = "Width";
            if (node.HorizontalAlignment is not ("Left" or "Center" or "Right")) node.HorizontalAlignment = "Left";
            if (node.VerticalAlignment is not ("Top" or "Center" or "Bottom")) node.VerticalAlignment = "Top";
            if (node.SortOrder is not ("LayoutOrder" or "Name")) node.SortOrder = "LayoutOrder";
            if (node.FlexMode is not ("None" or "Grow" or "Shrink" or "Fill" or "Custom")) node.FlexMode = "None";
            node.GrowRatio = float.IsFinite(node.GrowRatio) ? Math.Clamp(node.GrowRatio, 0, 1000) : 0;
            node.ShrinkRatio = float.IsFinite(node.ShrinkRatio) ? Math.Clamp(node.ShrinkRatio, 0, 1000) : 0;
            if (node.ItemLineAlignment is not ("Automatic" or "Start" or "Center" or "End" or "Stretch")) node.ItemLineAlignment = "Automatic";
            node.FillColor ??= "#00BFFF";
            node.OutlineColor ??= "#FFFFFF";
            node.StartColor ??= "#FFFFFF";
            node.EndColor ??= "#808080";
            node.FillTransparency = float.IsFinite(node.FillTransparency) ? Math.Clamp(node.FillTransparency, 0, 1) : .5f;
            node.OutlineTransparency = float.IsFinite(node.OutlineTransparency) ? Math.Clamp(node.OutlineTransparency, 0, 1) : 0;
            node.Brightness = float.IsFinite(node.Brightness) ? Math.Clamp(node.Brightness, 0, 8) : 1;
            node.Range = float.IsFinite(node.Range) ? Math.Clamp(node.Range, 1, 1000) : 16;
            node.SecondaryColor ??= "#FFE69A";
            node.EffectSize = float.IsFinite(node.EffectSize) ? Math.Clamp(node.EffectSize, node.Type == "Fire" ? 2 : .1f, node.Type == "Fire" ? 30 : 100) : node.Type == "Fire" ? 5 : 1;
            node.Heat = float.IsFinite(node.Heat) ? Math.Clamp(node.Heat, -25, 25) : 9;
            node.RiseVelocity = float.IsFinite(node.RiseVelocity) ? Math.Clamp(node.RiseVelocity, -25, 25) : 1;
            node.Opacity = float.IsFinite(node.Opacity) ? Math.Clamp(node.Opacity, 0, 1) : .5f;
            node.TimeScale = float.IsFinite(node.TimeScale) ? Math.Clamp(node.TimeScale, 0, 1) : 1;
            node.Rate = float.IsFinite(node.Rate) ? Math.Clamp(node.Rate, 0, 200) : 5;
            node.SizeSequence = ParticleSequenceService.NormalizeNumber(node.SizeSequence, 0, 100);
            node.TransparencySequence = ParticleSequenceService.NormalizeNumber(node.TransparencySequence, 0, 1);
            node.ColorSequence = ParticleSequenceService.NormalizeColor(node.ColorSequence);
            node.SpeedRange = EnsurePair(node.SpeedRange);
            node.SpeedRange[0] = float.IsFinite(node.SpeedRange[0]) ? Math.Clamp(node.SpeedRange[0], -100, 100) : 5;
            node.SpeedRange[1] = float.IsFinite(node.SpeedRange[1]) ? Math.Clamp(node.SpeedRange[1], node.SpeedRange[0], 100) : node.SpeedRange[0];
            node.LifetimeRange = EnsurePair(node.LifetimeRange);
            node.LifetimeRange[0] = float.IsFinite(node.LifetimeRange[0]) ? Math.Clamp(node.LifetimeRange[0], .05f, 30) : 5;
            node.LifetimeRange[1] = float.IsFinite(node.LifetimeRange[1]) ? Math.Clamp(node.LifetimeRange[1], node.LifetimeRange[0], 30) : node.LifetimeRange[0];
            node.SpreadAngle = EnsurePair(node.SpreadAngle);
            node.SpreadAngle[0] = float.IsFinite(node.SpreadAngle[0]) ? Math.Clamp(node.SpreadAngle[0], 0, 360) : 0;
            node.SpreadAngle[1] = float.IsFinite(node.SpreadAngle[1]) ? Math.Clamp(node.SpreadAngle[1], 0, 360) : 0;
            if (!SceneNodeTypes.IsFace(node.EmissionDirection)) node.EmissionDirection = "Top";
            node.BlastRadius = float.IsFinite(node.BlastRadius) ? Math.Clamp(node.BlastRadius, 0, 100) : 10;
            node.BlastPressure = float.IsFinite(node.BlastPressure) ? Math.Clamp(node.BlastPressure, 0, 1000000) : 500000;
            node.DestroyJointRadiusPercent = float.IsFinite(node.DestroyJointRadiusPercent) ? Math.Clamp(node.DestroyJointRadiusPercent, 0, 1) : 1;
            node.Angle = float.IsFinite(node.Angle) ? Math.Clamp(node.Angle, 1, 175) : 45;
            node.ActionText ??= "Interact";
            node.ObjectText ??= string.Empty;
            node.MaxActivationDistance = float.IsFinite(node.MaxActivationDistance) ? Math.Clamp(node.MaxActivationDistance, 1, 100) : 10;
            node.Volume = float.IsFinite(node.Volume) ? Math.Clamp(node.Volume, 0, 1) : .5f;
            node.RollOffMinDistance = float.IsFinite(node.RollOffMinDistance) ? Math.Clamp(node.RollOffMinDistance, 0, 1000) : 10;
            node.RollOffMaxDistance = float.IsFinite(node.RollOffMaxDistance) ? Math.Clamp(node.RollOffMaxDistance, node.RollOffMinDistance + .01f, 10000) : 100;
            node.Part0 ??= string.Empty;
            node.Part1 ??= string.Empty;
            node.Character ??= string.Empty;
            node.Occupant ??= string.Empty;
            node.MaxHealth = float.IsFinite(node.MaxHealth) ? Math.Clamp(node.MaxHealth, 1, 100000) : 100;
            node.Health = float.IsFinite(node.Health) ? Math.Clamp(node.Health, 0, node.MaxHealth) : node.MaxHealth;
            node.WalkSpeed = float.IsFinite(node.WalkSpeed) ? Math.Clamp(node.WalkSpeed, 0, 1000) : 7.5f;
            node.JumpPower = float.IsFinite(node.JumpPower) ? Math.Clamp(node.JumpPower, 0, 1000) : 10;
            node.IntegerValue = Math.Clamp(node.IntegerValue, -9007199254740991L, 9007199254740991L);
            node.NumberValue = double.IsFinite(node.NumberValue) ? node.NumberValue : 0;
            node.StringValueText ??= string.Empty;
            node.Vector3Value = EnsureVector(node.Vector3Value, [0, 0, 0]);
            if (node.Color3Value is null || !System.Text.RegularExpressions.Regex.IsMatch(node.Color3Value, "^#[0-9A-Fa-f]{6}$")) node.Color3Value = "#000000";
            node.ObjectValueId ??= string.Empty;
            node.Width0 = float.IsFinite(node.Width0) ? Math.Clamp(node.Width0, 0, 100) : .2f;
            node.Width1 = float.IsFinite(node.Width1) ? Math.Clamp(node.Width1, 0, 100) : .2f;
            node.Force = EnsureVector(node.Force, [0, 0, 0]);
            for (var axis = 0; axis < 3; axis++) node.Force[axis] = float.IsFinite(node.Force[axis]) ? Math.Clamp(node.Force[axis], -1000000, 1000000) : 0;
            if (node.RelativeTo is not ("World" or "Attachment0")) node.RelativeTo = node.Type == "VectorForce" ? "Attachment0" : "World";
            node.VectorVelocity = EnsureVector(node.VectorVelocity, [0, 0, 0]);
            node.LineDirection = EnsureVector(node.LineDirection, [1, 0, 0]);
            node.PrimaryTangentAxis = EnsureVector(node.PrimaryTangentAxis, [1, 0, 0]);
            node.SecondaryTangentAxis = EnsureVector(node.SecondaryTangentAxis, [0, 0, 1]);
            foreach (var vector in new[] { node.VectorVelocity, node.LineDirection, node.PrimaryTangentAxis, node.SecondaryTangentAxis })
                for (var axis = 0; axis < 3; axis++) vector[axis] = Math.Clamp(vector[axis], -10000, 10000);
            node.PlaneVelocity = EnsurePair(node.PlaneVelocity);
            for (var axis = 0; axis < 2; axis++) node.PlaneVelocity[axis] = float.IsFinite(node.PlaneVelocity[axis]) ? Math.Clamp(node.PlaneVelocity[axis], -10000, 10000) : 0;
            node.LineVelocity = float.IsFinite(node.LineVelocity) ? Math.Clamp(node.LineVelocity, -10000, 10000) : 0;
            node.MaxForce = float.IsFinite(node.MaxForce) ? Math.Clamp(node.MaxForce, 0, 1000000) : 1000;
            if (node.AlignmentMode is not ("OneAttachment" or "TwoAttachment")) node.AlignmentMode = "TwoAttachment";
            node.Responsiveness = float.IsFinite(node.Responsiveness) ? Math.Clamp(node.Responsiveness, 0, 200) : 10;
            node.MaxVelocity = float.IsFinite(node.MaxVelocity) ? Math.Clamp(node.MaxVelocity, 0, 10000) : 100;
            if (node.VelocityConstraintMode is not ("Vector" or "Line" or "Plane")) node.VelocityConstraintMode = "Vector";
            node.TrailLifetime = float.IsFinite(node.TrailLifetime) ? Math.Clamp(node.TrailLifetime, .01f, 20) : 2;
            node.TrailMinLength = float.IsFinite(node.TrailMinLength) ? Math.Clamp(node.TrailMinLength, 0, 100) : .1f;
            node.TrailMaxLength = float.IsFinite(node.TrailMaxLength) ? Math.Clamp(node.TrailMaxLength, 0, 10000) : 0;
            node.TrailWidth = float.IsFinite(node.TrailWidth) ? Math.Clamp(node.TrailWidth, 0, 1) : 1;
            node.TrailWidthSequence = ParticleSequenceService.NormalizeNumber(node.TrailWidthSequence, 0, 1);
            if (node.TextureMode is not ("Stretch" or "Wrap" or "Static")) node.TextureMode = "Stretch";
            node.TextureLength = float.IsFinite(node.TextureLength) ? Math.Clamp(node.TextureLength, .01f, 1000) : 1;
        }
        foreach (var node in Flatten(project))
        {
            node.Tags = (node.Tags ?? [])
                .Where(tag => !string.IsNullOrWhiteSpace(tag) && tag.Length <= 256)
                .Distinct(StringComparer.Ordinal).ToList();
            node.Attributes = node.Attributes is null
                ? new Dictionary<string, SceneAttributeValue>(StringComparer.Ordinal)
                : new Dictionary<string, SceneAttributeValue>(node.Attributes, StringComparer.Ordinal);
            foreach (var (name, attribute) in node.Attributes.ToArray())
            {
                if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || attribute is null
                    || attribute.Type is not ("String" or "Boolean" or "Number" or "Vector3" or "Color3"))
                {
                    node.Attributes.Remove(name);
                    continue;
                }
                attribute.StringValue ??= string.Empty;
                attribute.NumberValue = double.IsFinite(attribute.NumberValue) ? attribute.NumberValue : 0;
                attribute.Vector3Value = EnsureVector(attribute.Vector3Value, [0, 0, 0]);
                if (attribute.Color3Value is null || !System.Text.RegularExpressions.Regex.IsMatch(attribute.Color3Value, "^#[0-9A-Fa-f]{6}$"))
                    attribute.Color3Value = "#000000";
            }
        }
        var validObjectIds = Flatten(project).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var objectValue in Flatten(project).Where(node => node.Type == "ObjectValue" && node.ObjectValueId.Length > 0))
            if (!validObjectIds.Contains(objectValue.ObjectValueId)) objectValue.ObjectValueId = string.Empty;
        var scriptNames = Flatten(project.Workspace.Children).Concat(Flatten(project.ReplicatedStorage.Children)).Concat(Flatten(project.ServerScriptService.Children)).Concat(Flatten(project.ServerStorage.Children)).Concat(Flatten(project.Gui.Children))
            .Where(node => node.Type is "Script" or "ModuleScript")
            .GroupBy(node => node.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);
        foreach (var script in project.Scripts)
            if (scriptNames.TryGetValue(script.Id, out var name)) script.Name = name;
        var cameras = Flatten(project.Workspace.Children).Where(node => node.Type == "Camera").ToList();
        var camera = cameras.FirstOrDefault() ?? new SceneNode { Id = "camera-main", Type = "Camera", Name = "Camera", Position = [8, 7, 11], Rotation = [-24, 35, 0] };
        foreach (var duplicate in cameras.Skip(1)) RemoveNode(project.Workspace.Children, duplicate.Id);
        RemoveNode(project.Workspace.Children, camera.Id);
        project.Workspace.Children.Insert(0, camera);
        if (camera.CameraMode == "Subject" && !Flatten(project.Workspace.Children).Any(node => node.Id == camera.Subject && SceneNodeTypes.IsBasePart(node)))
        {
            camera.CameraMode = "Custom";
            camera.Subject = string.Empty;
        }
        project.Workspace.Position = [0, 0, 0];
    }

    private static float[] EnsurePair(float[]? values)
    {
        var result = new float[2];
        if (values is not null) Array.Copy(values, result, Math.Min(values.Length, result.Length));
        return result;
    }

    private static float[] EnsureVector(float[]? values, float[] defaults)
    {
        var result = (float[])defaults.Clone();
        if (values is not null) Array.Copy(values, result, Math.Min(values.Length, result.Length));
        for (var index = 0; index < result.Length; index++)
            if (!float.IsFinite(result[index])) result[index] = defaults[index];
        return result;
    }

    public static IReadOnlyList<SceneNode> Flatten(IEnumerable<SceneNode> nodes)
    {
        var result = new List<SceneNode>();
        foreach (var node in nodes) { result.Add(node); result.AddRange(Flatten(node.Children ?? [])); }
        return result;
    }

    public static IReadOnlyList<SceneNode> Flatten(GameProject project) => Flatten([project.Workspace, project.Players, project.Lighting, project.ReplicatedStorage, project.ServerScriptService, project.ServerStorage, project.Gui]);

    public static SceneNode? CloneSubtree(SceneNode source, out Dictionary<string, string> renewedIds, bool respectArchivable = true)
    {
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        renewedIds = idMap;
        if (respectArchivable && !source.Archivable) return null;
        var clone = JsonSerializer.Deserialize<SceneNode>(JsonSerializer.Serialize(source, JsonOptions), JsonOptions);
        if (clone is null) return null;

        void RenewIds(SceneNode node)
        {
            if (respectArchivable) node.Children.RemoveAll(child => !child.Archivable);
            var oldId = node.Id;
            node.Id = Guid.NewGuid().ToString("N");
            idMap[oldId] = node.Id;
            foreach (var child in node.Children) RenewIds(child);
        }
        RenewIds(clone);
        foreach (var node in new[] { clone }.Concat(Flatten(clone.Children)))
        {
            if (idMap.TryGetValue(node.Part0, out var part0)) node.Part0 = part0;
            if (idMap.TryGetValue(node.Part1, out var part1)) node.Part1 = part1;
            if (idMap.TryGetValue(node.Attachment0, out var attachment0)) node.Attachment0 = attachment0;
            if (idMap.TryGetValue(node.Attachment1, out var attachment1)) node.Attachment1 = attachment1;
            if (idMap.TryGetValue(node.Subject, out var subject)) node.Subject = subject;
            if (idMap.TryGetValue(node.Character, out var character)) node.Character = character;
            if (idMap.TryGetValue(node.Occupant, out var occupant)) node.Occupant = occupant;
            if (idMap.TryGetValue(node.ObjectValueId, out var referencedObject)) node.ObjectValueId = referencedObject;
        }
        if (!respectArchivable) clone.Archivable = true;
        return clone;
    }

    public static bool CanParentNode(GameProject project, SceneNode source, SceneNode destinationParent)
    {
        if (source.Id == project.Workspace.Id || source.Id == project.Players.Id || source.Id == project.Lighting.Id || source.Id == project.ReplicatedStorage.Id || source.Id == project.ServerScriptService.Id || source.Id == project.ServerStorage.Id || source.Id == project.Gui.Id || source.Id == destinationParent.Id) return false;
        if (!IsNodeInTree(project.Workspace, destinationParent.Id) && !IsNodeInTree(project.Lighting, destinationParent.Id) && !IsNodeInTree(project.ReplicatedStorage, destinationParent.Id) && !IsNodeInTree(project.ServerScriptService, destinationParent.Id) && !IsNodeInTree(project.ServerStorage, destinationParent.Id) && !IsNodeInTree(project.Gui, destinationParent.Id)) return false;
        if (Flatten(source.Children).Any(node => node.Id == destinationParent.Id)) return false;
        if (source.Type == "Humanoid")
            return destinationParent.Type == "Model" && source.Children.Count == 0
                && !destinationParent.Children.Any(child => child.Type == "Humanoid" && child.Id != source.Id);
        if (source.Type == "Camera")
            return destinationParent.Id == project.Workspace.Id && source.Children.All(child => IsWorkspaceSubtreeCompatible(child, "Camera"));
        if (destinationParent.Id == project.Lighting.Id)
            return source.Type == "Sky" && source.Children.Count == 0
                && !destinationParent.Children.Any(child => child.Type == "Sky" && child.Id != source.Id);
        if (source.Type == "Sky" || IsNodeInTree(project.Lighting, destinationParent.Id)) return false;

        var destinationIsGui = IsNodeInTree(project.Gui, destinationParent.Id);
        if (destinationIsGui)
        {
            if (destinationParent.Id == project.Gui.Id)
                return (source.Type is "ScreenGui" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Script" || SceneNodeTypes.IsValueObject(source)) && IsGuiSubtreeCompatible(source, true);
            if (!IsGuiContainer(destinationParent.Type) && !SceneNodeTypes.IsValueObject(destinationParent)) return false;
            if (source.Type == "ScreenGui") return false;
            return IsGuiSubtreeCompatible(source, false, destinationParent.Type);
        }

        if (IsNodeInTree(project.ReplicatedStorage, destinationParent.Id)
            || IsNodeInTree(project.ServerScriptService, destinationParent.Id)
            || IsNodeInTree(project.ServerStorage, destinationParent.Id))
        {
            if (destinationParent.Type is not ("ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Model" or "Attachment")
                && !SceneNodeTypes.IsBasePart(destinationParent) && !SceneNodeTypes.IsValueObject(destinationParent)) return false;
            return IsWorkspaceSubtreeCompatible(source, destinationParent.Type is "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" ? "Workspace" : destinationParent.Type);
        }

        if (destinationParent.Type is not ("Workspace" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Model" or "Attachment") && !SceneNodeTypes.IsBasePart(destinationParent) && !SceneNodeTypes.IsValueObject(destinationParent)) return false;
        return IsWorkspaceSubtreeCompatible(source, destinationParent.Type);
    }

    public static bool CanParentDetachedNode(SceneNode source, SceneNode destinationParent)
    {
        if (source.Id == destinationParent.Id || Flatten(source.Children).Any(child => child.Id == destinationParent.Id)) return false;
        if (source.Type is "Workspace" or "Players" or "Lighting" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "Gui" or "Camera") return false;
        if (destinationParent.Type == "Lighting") return source.Type == "Sky" && source.Children.Count == 0 && destinationParent.Children.All(child => child.Type != "Sky");
        if (source.Type == "Sky") return false;
        if (source.Type == "Humanoid")
            return destinationParent.Type == "Model" && source.Children.Count == 0
                && !destinationParent.Children.Any(child => child.Type == "Humanoid" && child.Id != source.Id);
        if (destinationParent.Type is "Model" or "Attachment" || SceneNodeTypes.IsBasePart(destinationParent))
            return IsWorkspaceSubtreeCompatible(source, destinationParent.Type);
        if (destinationParent.Type is "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript")
            return IsWorkspaceSubtreeCompatible(source, destinationParent.Type) || IsGuiSubtreeCompatible(source, false, destinationParent.Type);
        if (SceneNodeTypes.IsValueObject(destinationParent))
            return IsWorkspaceSubtreeCompatible(source, destinationParent.Type) || IsGuiSubtreeCompatible(source, false, destinationParent.Type);
        return IsGuiContainer(destinationParent.Type) && IsGuiSubtreeCompatible(source, false, destinationParent.Type);
    }

    public static bool MoveNodeToParent(GameProject project, SceneNode source, SceneNode destinationParent)
    {
        if (!CanParentNode(project, source, destinationParent)) return false;
        var sourceCollection = FindParentCollection(project.Workspace.Children, source)
            ?? FindParentCollection(project.Lighting.Children, source)
            ?? FindParentCollection(project.ReplicatedStorage.Children, source)
            ?? FindParentCollection(project.ServerScriptService.Children, source)
            ?? FindParentCollection(project.ServerStorage.Children, source)
            ?? FindParentCollection(project.Gui.Children, source);
        if (sourceCollection is null || ReferenceEquals(sourceCollection, destinationParent.Children)) return false;
        sourceCollection.Remove(source);
        destinationParent.Children.Add(source);
        return true;
    }

    public static bool MoveNodeBeside(GameProject project, SceneNode source, SceneNode target, bool after)
    {
        if (source.Type == "Camera" || target.Type == "Camera" || ReferenceEquals(source, target) || source.Id == target.Id) return false;
        var destinationParent = FindParentNode(project.Workspace, target) ?? FindParentNode(project.Lighting, target) ?? FindParentNode(project.ReplicatedStorage, target) ?? FindParentNode(project.ServerScriptService, target) ?? FindParentNode(project.ServerStorage, target) ?? FindParentNode(project.Gui, target);
        if (destinationParent is null || !CanParentNode(project, source, destinationParent)) return false;
        var sourceCollection = FindParentCollection(project.Workspace.Children, source)
            ?? FindParentCollection(project.Lighting.Children, source)
            ?? FindParentCollection(project.ReplicatedStorage.Children, source)
            ?? FindParentCollection(project.ServerScriptService.Children, source)
            ?? FindParentCollection(project.ServerStorage.Children, source)
            ?? FindParentCollection(project.Gui.Children, source);
        if (sourceCollection is null) return false;
        var destinationCollection = destinationParent.Children;
        var currentTargetIndex = destinationCollection.IndexOf(target);
        if (currentTargetIndex < 0) return false;
        var targetIndex = currentTargetIndex + (after ? 1 : 0);
        var sourceIndex = sourceCollection.IndexOf(source);
        if (sourceIndex < 0) return false;
        var destinationIndex = targetIndex;
        if (ReferenceEquals(sourceCollection, destinationCollection) && sourceIndex < targetIndex) destinationIndex--;
        if (ReferenceEquals(sourceCollection, destinationCollection) && sourceIndex == destinationIndex) return false;
        sourceCollection.RemoveAt(sourceIndex);
        destinationCollection.Insert(Math.Clamp(destinationIndex, 0, destinationCollection.Count), source);
        return true;
    }

    private static List<SceneNode>? FindParentCollection(IEnumerable<SceneNode> roots, SceneNode target)
    {
        foreach (var root in roots)
        {
            var collection = FindParentCollection(root.Children ?? [], target);
            if (collection is not null) return collection;
        }
        return null;
    }

    private static List<SceneNode>? FindParentCollection(List<SceneNode> children, SceneNode target)
    {
        foreach (var child in children)
        {
            if (ReferenceEquals(child, target)) return children;
            var nestedCollection = FindParentCollection(child.Children ?? [], target);
            if (nestedCollection is not null) return nestedCollection;
        }
        return null;
    }

    private static SceneNode? FindParentNode(SceneNode parent, SceneNode target)
    {
        foreach (var child in parent.Children ?? [])
        {
            if (ReferenceEquals(child, target)) return parent;
            var nestedParent = FindParentNode(child, target);
            if (nestedParent is not null) return nestedParent;
        }
        return null;
    }

    private static bool IsNodeInTree(SceneNode root, string nodeId) =>
        root.Id == nodeId || Flatten(root.Children).Any(node => node.Id == nodeId);

    private static bool IsGuiContainer(string type) => type is "Gui" or "ScreenGui" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox";

    private static bool IsGuiObject(string type) => type is "Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox";

    private static bool IsGuiComponent(string type) => type is "UICorner" or "UIOutline" or "UIStroke" or "UIGradient" or "UIPadding" or "UIScale" or "UIListLayout" or "UIGridLayout" or "UITableLayout" or "UIAspectRatio" or "UIAspectRatioConstraint" or "UISizeConstraint" or "UITextSizeConstraint" or "UIFlexItem";

    private static bool IsGuiSubtreeCompatible(SceneNode node, bool isGuiServiceChild, string? destinationType = null)
    {
        if (node.Type == "ScreenGui") return isGuiServiceChild && node.Children.All(child => IsGuiSubtreeCompatible(child, false, "ScreenGui"));
        if (node.Type is "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript") return node.Children.All(child => IsGuiSubtreeCompatible(child, false, node.Type));
        if (node.Type == "Script") return node.Children.Count == 0;
        if (SceneNodeTypes.IsValueObject(node)) return node.Children.All(child => IsGuiSubtreeCompatible(child, false, node.Type));
        if (node.Type == "UITextSizeConstraint") return destinationType is "TextLabel" or "TextButton" or "TextBox" && node.Children.Count == 0;
        if (IsGuiComponent(node.Type)) return destinationType is not null && IsGuiObject(destinationType) && node.Children.Count == 0;
        if (!IsGuiObject(node.Type)) return false;
        return node.Children.All(child => IsGuiSubtreeCompatible(child, false, node.Type));
    }

    private static bool IsWorkspaceSubtreeCompatible(SceneNode node, string? parentType)
    {
        if (node.Type == "Humanoid") return parentType == "Model" && node.Children.Count == 0;
        if (node.Type == "Attachment")
            return SceneNodeTypes.IsBasePartType(parentType)
                && node.Children.All(child => IsWorkspaceSubtreeCompatible(child, "Attachment"));
        if (node.Type is "Beam" or "Trail" or "VectorForce" or "LinearVelocity" or "AlignPosition")
            return (parentType is "Workspace" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Model" or "Attachment" || SceneNodeTypes.IsBasePartType(parentType))
                && node.Children.Count == 0;
        if (SceneNodeTypes.IsValueObject(node))
            return (parentType is "Workspace" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Model" || SceneNodeTypes.IsBasePartType(parentType) || SceneNodeTypes.IsValueObjectType(parentType))
                && node.Children.All(child => IsWorkspaceSubtreeCompatible(child, node.Type));
        if (node.Type is "Decal" or "Texture")
            return SceneNodeTypes.IsBasePartType(parentType) && node.Children.Count == 0;
        if (SceneNodeTypes.IsParticleEffect(node))
            return (SceneNodeTypes.IsBasePartType(parentType) || parentType == "Attachment") && node.Children.Count == 0;
        if (node.Type is "ProximityPrompt" or "ClickDetector")
            return SceneNodeTypes.IsBasePartType(parentType) && node.Children.Count == 0;
        if (node.Type == "Sound")
            return (parentType is "Workspace" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Model" || SceneNodeTypes.IsBasePartType(parentType) || SceneNodeTypes.IsValueObjectType(parentType)) && node.Children.Count == 0;
        if (node.Type == "Highlight")
            return (SceneNodeTypes.IsBasePartType(parentType) || parentType == "Model") && node.Children.Count == 0;
        if (node.Type == "ForceField")
            return (SceneNodeTypes.IsBasePartType(parentType) || parentType == "Model") && node.Children.Count == 0;
        if (node.Type == "Explosion")
            return (parentType is "Workspace" or "Folder" or "Configuration" or "Model" || SceneNodeTypes.IsBasePartType(parentType)) && node.Children.Count == 0;
        if (node.Type is "WeldConstraint" or "NoCollisionConstraint")
            return (parentType is "Workspace" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Model" || SceneNodeTypes.IsBasePartType(parentType) || SceneNodeTypes.IsValueObjectType(parentType)) && node.Children.Count == 0;
        if (SceneNodeTypes.IsLight(node))
            return SceneNodeTypes.IsBasePartType(parentType)
                && node.Children.Count == 0;
        if (!SceneNodeTypes.IsBasePart(node) && node.Type is not ("ImageLabel" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Model" or "Script")) return false;
        if (node.Type is "Script" or "ImageLabel" && node.Children.Count > 0) return false;
        return node.Children.All(child => IsWorkspaceSubtreeCompatible(child, node.Type));
    }

    public static IReadOnlyList<SceneNode> RemoveNodeAndGetSubtree(List<SceneNode> nodes, string id)
    {
        var target = Flatten(nodes).FirstOrDefault(node => node.Id == id);
        if (target is null) return [];
        var removedNodes = new[] { target }.Concat(Flatten(target.Children)).ToArray();
        RemoveNode(nodes, id);
        return removedNodes;
    }

    public static void RemoveNode(List<SceneNode> nodes, string id)
    {
        for (var index = nodes.Count - 1; index >= 0; index--)
            if (nodes[index].Id == id) nodes.RemoveAt(index);
            else RemoveNode(nodes[index].Children ?? [], id);
    }

    public static GameProject NewProject(string name, string folder)
    {
        var project = new GameProject { Name = name, ProjectPath = folder };
        project.Workspace.Children.Add(new SceneNode { Id = "camera-main", Type = "Camera", Name = "Camera", Position = [8, 7, 11], Rotation = [-24, 35, 0] });
        project.Workspace.Children.Add(new SceneNode { Id = "part-start", Type = "Part", Name = "Part", Position = [0, .5f, 0], Size = [2, 1, 2] });
        project.Workspace.Children.Add(new SceneNode { Id = "part-ground", Type = "Part", Name = "Ground", Position = [0, -.12f, 0], Size = [24, .2f, 24], Color = "#293345", CastShadow = false });
        return project;
    }

    public static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string((name ?? string.Empty).Where(character => !invalid.Contains(character) && character != '/' && character != '\\').ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(safe) ? "Untitled Game" : safe;
    }

    public static GameProject LoadPackaged()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("BullerStudio.GameProject.json");
        GameProject project;
        var executableFolder = AppContext.BaseDirectory;
        if (stream is not null)
        {
            project = JsonSerializer.Deserialize<GameProject>(stream, JsonOptions) ?? throw new InvalidDataException("Packaged game data is invalid.");
            var assetResources = assembly.GetManifestResourceNames().Where(name => name.StartsWith("BullerStudioAssets/", StringComparison.Ordinal)).ToArray();
            var cacheFolder = GetPackagedBuildCacheFolder(project.Name, Environment.ProcessPath ?? AppContext.BaseDirectory, assetResources);
            var assetRoot = Path.Combine(cacheFolder, "assets");
            Directory.CreateDirectory(assetRoot);
            foreach (var resourceName in assetResources)
            {
                var relativePath = resourceName["BullerStudioAssets/".Length..].Replace('/', Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(Path.Combine(assetRoot, relativePath));
                if (!destination.StartsWith(Path.GetFullPath(assetRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var resource = assembly.GetManifestResourceStream(resourceName);
                if (resource is null) continue;
                using var output = File.Create(destination);
                resource.CopyTo(output);
            }
            project.ProjectPath = cacheFolder;
        }
        else
        {
            var file = Path.Combine(executableFolder, GameFileName);
            project = JsonSerializer.Deserialize<GameProject>(File.ReadAllText(file), JsonOptions) ?? throw new InvalidDataException("The external game data file is missing or invalid.");
            project.ProjectPath = executableFolder;
        }
        EnsureProject(project);
        return project;
    }

    private static string GetPackagedBuildCacheFolder(string projectName, string executablePath, IEnumerable<string> assetResources)
    {
        var normalizedPath = Path.GetFullPath(executablePath);
        var identity = new StringBuilder(normalizedPath).Append('\0');
        foreach (var resourceName in assetResources.OrderBy(name => name, StringComparer.Ordinal))
            identity.Append(resourceName.Length).Append(':').Append(resourceName).Append('\0');
        var buildId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())))[..16];
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Buller Studio", "Build Cache", SanitizeName(projectName), buildId);
    }
}

public sealed record ProjectSummary(string Name, string Path, DateTime LastModified);
