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
            UpdateReference(node, "Video", node.Video, newAssetsRelativePath);
            UpdateReference(node, "HoverImage", node.HoverImage, newAssetsRelativePath);
            UpdateReference(node, "PressedImage", node.PressedImage, newAssetsRelativePath);
            UpdateReference(node, "Texture", node.Texture, newAssetsRelativePath);
            UpdateReference(node, "SurfaceAppearanceColorMap", node.SurfaceAppearanceColorMap, newAssetsRelativePath);
            UpdateReference(node, "SurfaceAppearanceNormalMap", node.SurfaceAppearanceNormalMap, newAssetsRelativePath);
            UpdateReference(node, "SurfaceAppearanceRoughnessMap", node.SurfaceAppearanceRoughnessMap, newAssetsRelativePath);
            UpdateReference(node, "SurfaceAppearanceMetalnessMap", node.SurfaceAppearanceMetalnessMap, newAssetsRelativePath);
            UpdateReference(node, "SurfaceAppearanceEmissiveMask", node.SurfaceAppearanceEmissiveMask, newAssetsRelativePath);
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
                case "Video": node.Video = value; break;
                case "HoverImage": node.HoverImage = value; break;
                case "PressedImage": node.PressedImage = value; break;
                case "Texture": node.Texture = value; break;
                case "SurfaceAppearanceColorMap": node.SurfaceAppearanceColorMap = value; break;
                case "SurfaceAppearanceNormalMap": node.SurfaceAppearanceNormalMap = value; break;
                case "SurfaceAppearanceRoughnessMap": node.SurfaceAppearanceRoughnessMap = value; break;
                case "SurfaceAppearanceMetalnessMap": node.SurfaceAppearanceMetalnessMap = value; break;
                case "SurfaceAppearanceEmissiveMask": node.SurfaceAppearanceEmissiveMask = value; break;
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
        project.Settings.MoveSnapIncrement = float.IsFinite(project.Settings.MoveSnapIncrement) ? Math.Clamp(project.Settings.MoveSnapIncrement, .01f, 1000) : 1;
        project.Settings.RotateSnapIncrement = float.IsFinite(project.Settings.RotateSnapIncrement) ? Math.Clamp(project.Settings.RotateSnapIncrement, .1f, 360) : 15;
        project.Workspace ??= new SceneNode { Id = "workspace", Type = "Workspace", Name = "Workspace" };
        project.Workspace.Children ??= [];
        project.Workspace.Gravity = float.IsFinite(project.Workspace.Gravity) ? Math.Clamp(project.Workspace.Gravity, 0, 1000) : 24;
        project.Workspace.FallenPartsDestroyHeight = float.IsFinite(project.Workspace.FallenPartsDestroyHeight) ? Math.Clamp(project.Workspace.FallenPartsDestroyHeight, -1000000, 1000000) : -500;
        var terrain = project.Workspace.Children.FirstOrDefault(node => node.Type == "Terrain");
        if (terrain is null)
        {
            terrain = new SceneNode { Id = "workspace-terrain", Type = "Terrain", Name = "Terrain" };
            project.Workspace.Children.Add(terrain);
        }
        terrain.Id = string.IsNullOrWhiteSpace(terrain.Id) ? Guid.NewGuid().ToString("N") : terrain.Id;
        terrain.Type = "Terrain";
        terrain.Name = "Terrain";
        terrain.Children ??= [];
        foreach (var duplicateTerrain in project.Workspace.Children.Where(node => node.Type == "Terrain" && !ReferenceEquals(node, terrain)).ToArray())
        {
            terrain.Children.AddRange(duplicateTerrain.Children);
            project.Workspace.Children.Remove(duplicateTerrain);
        }
        project.Players ??= new SceneNode { Id = "players", Type = "Players", Name = "Players" };
        project.Players.Id = "players";
        project.Players.Type = "Players";
        project.Players.Name = "Players";
        project.Players.Children ??= [];
        project.Teams ??= new SceneNode { Id = "teams", Type = "Teams", Name = "Teams" };
        project.Teams.Id = "teams";
        project.Teams.Type = "Teams";
        project.Teams.Name = "Teams";
        project.Teams.Children ??= [];
        foreach (var team in project.Teams.Children.Where(node => node.Type == "Team"))
        {
            if (team.TeamColor is null || !System.Text.RegularExpressions.Regex.IsMatch(team.TeamColor, "^#[0-9A-Fa-f]{6}$")) team.TeamColor = "#E53935";
        }
        foreach (var spawn in Flatten(project.Workspace.Children).Where(node => node.Type == "SpawnLocation"))
        {
            if (spawn.TeamColor is null || !System.Text.RegularExpressions.Regex.IsMatch(spawn.TeamColor, "^#[0-9A-Fa-f]{6}$")) spawn.TeamColor = "#E53935";
            spawn.Duration = float.IsFinite(spawn.Duration) ? Math.Clamp(spawn.Duration, 0, 10000) : 10;
        }
        project.Lighting ??= new SceneNode { Id = "lighting", Type = "Lighting", Name = "Lighting", Ambient = "#808080", ClockTime = 14, Children = [new SceneNode { Id = "sky-main", Type = "Sky", Name = "Sky" }] };
        project.Lighting.Id = "lighting";
        project.Lighting.Type = "Lighting";
        project.Lighting.Name = "Lighting";
        project.Lighting.Children ??= [];
        if (project.Lighting.Ambient is null || !System.Text.RegularExpressions.Regex.IsMatch(project.Lighting.Ambient, "^#[0-9A-Fa-f]{6}$")) project.Lighting.Ambient = "#808080";
        project.Lighting.ClockTime = float.IsFinite(project.Lighting.ClockTime) ? Math.Clamp(project.Lighting.ClockTime, 0, 24) : 14;
        project.Lighting.Brightness = float.IsFinite(project.Lighting.Brightness) ? Math.Clamp(project.Lighting.Brightness, 0, 8) : 1;
        foreach (var atmosphere in project.Lighting.Children.Where(node => node.Type == "Atmosphere"))
        {
            atmosphere.Density = float.IsFinite(atmosphere.Density) ? Math.Clamp(atmosphere.Density, 0, 1) : .3f;
            atmosphere.Haze = float.IsFinite(atmosphere.Haze) ? Math.Clamp(atmosphere.Haze, 0, 10) : 0;
            atmosphere.Glare = float.IsFinite(atmosphere.Glare) ? Math.Clamp(atmosphere.Glare, 0, 10) : 0;
            atmosphere.Offset = float.IsFinite(atmosphere.Offset) ? Math.Clamp(atmosphere.Offset, -1, 1) : 0;
            if (atmosphere.Color is null || !System.Text.RegularExpressions.Regex.IsMatch(atmosphere.Color, "^#[0-9A-Fa-f]{6}$")) atmosphere.Color = "#C6D9FF";
            if (atmosphere.AtmosphereDecay is null || !System.Text.RegularExpressions.Regex.IsMatch(atmosphere.AtmosphereDecay, "^#[0-9A-Fa-f]{6}$")) atmosphere.AtmosphereDecay = "#607080";
        }
        project.SoundService ??= new SceneNode { Id = "sound-service", Type = "SoundService", Name = "SoundService" };
        project.SoundService.Id = "sound-service";
        project.SoundService.Type = "SoundService";
        project.SoundService.Name = "SoundService";
        project.SoundService.Children ??= [];
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
        project.StarterPack ??= new SceneNode { Id = "starter-pack", Type = "StarterPack", Name = "StarterPack" };
        project.StarterPack.Id = "starter-pack";
        project.StarterPack.Type = "StarterPack";
        project.StarterPack.Name = "StarterPack";
        project.StarterPack.Children ??= [];
        project.StarterPlayer ??= new SceneNode { Id = "starter-player", Type = "StarterPlayer", Name = "StarterPlayer" };
        project.StarterPlayer.Id = "starter-player";
        project.StarterPlayer.Type = "StarterPlayer";
        project.StarterPlayer.Name = "StarterPlayer";
        project.StarterPlayer.Children ??= [];
        foreach (var templateName in new[] { "StarterPlayerScripts", "StarterCharacterScripts" })
            if (!project.StarterPlayer.Children.Any(node => node.Name == templateName && node.Type == "Folder"))
                project.StarterPlayer.Children.Add(new SceneNode { Type = "Folder", Name = templateName });
        project.Gui ??= new SceneNode { Id = "gui", Type = "StarterGui", Name = "StarterGui" };
        project.Gui.Id = "gui";
        project.Gui.Type = "StarterGui";
        project.Gui.Name = "StarterGui";
        project.Gui.Children ??= [];
        if (!project.Gui.Children.Any(node => node.Type == "ScreenGui"))
            project.Gui.Children.Add(new SceneNode { Id = Guid.NewGuid().ToString("N"), Type = "ScreenGui", Name = "ScreenGui" });
        foreach (var node in Flatten(project.Workspace.Children).Concat(Flatten(project.Players.Children)).Concat(Flatten(project.Teams.Children)).Concat(Flatten(project.Lighting.Children)).Concat(Flatten(project.SoundService.Children)).Concat(Flatten(project.ReplicatedStorage.Children)).Concat(Flatten(project.ServerScriptService.Children)).Concat(Flatten(project.ServerStorage.Children)).Concat(Flatten(project.StarterPack.Children)).Concat(Flatten(project.StarterPlayer.Children)).Concat(Flatten(project.Gui.Children)))
        {
            node.Children ??= [];
            node.Position = EnsureVector(node.Position, [0, 0, 0]);
            node.Rotation = EnsureVector(node.Rotation, [0, 0, 0]);
            if (node.Type == "Model") node.ModelScale = float.IsFinite(node.ModelScale) && node.ModelScale > 0 ? Math.Clamp(node.ModelScale, .01f, 1000) : 1;
            node.Part0OffsetPosition = EnsureVector(node.Part0OffsetPosition, [0, 0, 0]);
            node.Part0OffsetRotation = EnsureVector(node.Part0OffsetRotation, [0, 0, 0]);
            node.Part1OffsetPosition = EnsureVector(node.Part1OffsetPosition, [0, 0, 0]);
            node.Part1OffsetRotation = EnsureVector(node.Part1OffsetRotation, [0, 0, 0]);
            node.MotorTransformPosition = EnsureVector(node.MotorTransformPosition, [0, 0, 0]);
            node.MotorTransformRotation = EnsureVector(node.MotorTransformRotation, [0, 0, 0]);
            for (var axis = 0; axis < 3; axis++)
            {
                node.Part0OffsetPosition[axis] = float.IsFinite(node.Part0OffsetPosition[axis]) ? Math.Clamp(node.Part0OffsetPosition[axis], -1000000, 1000000) : 0;
                node.Part1OffsetPosition[axis] = float.IsFinite(node.Part1OffsetPosition[axis]) ? Math.Clamp(node.Part1OffsetPosition[axis], -1000000, 1000000) : 0;
                node.Part0OffsetRotation[axis] = float.IsFinite(node.Part0OffsetRotation[axis]) ? Math.Clamp(node.Part0OffsetRotation[axis], -360000, 360000) : 0;
                node.Part1OffsetRotation[axis] = float.IsFinite(node.Part1OffsetRotation[axis]) ? Math.Clamp(node.Part1OffsetRotation[axis], -360000, 360000) : 0;
                node.MotorTransformPosition[axis] = float.IsFinite(node.MotorTransformPosition[axis]) ? Math.Clamp(node.MotorTransformPosition[axis], -1000000, 1000000) : 0;
                node.MotorTransformRotation[axis] = float.IsFinite(node.MotorTransformRotation[axis]) ? Math.Clamp(node.MotorTransformRotation[axis], -360000, 360000) : 0;
            }
            node.Size = EnsureVector(node.Size, [2, 2, 2]);
            node.Velocity = EnsureVector(node.Velocity, [0, 0, 0]);
            node.PositionScale = EnsurePair(node.PositionScale);
            node.AnchorPoint = EnsureVector(node.AnchorPoint, [0, 0]);
            for (var axis = 0; axis < 2; axis++) node.AnchorPoint[axis] = Math.Clamp(node.AnchorPoint[axis], 0, 1);
            node.SizeScale = EnsurePair(node.SizeScale);
            if (node.AutomaticSize is not ("None" or "X" or "Y" or "XY")) node.AutomaticSize = "None";
            if (node.BorderColor3 is null || !System.Text.RegularExpressions.Regex.IsMatch(node.BorderColor3, "^#[0-9A-Fa-f]{6}$")) node.BorderColor3 = "#000000";
            node.BorderColor3 = node.BorderColor3.ToUpperInvariant();
            node.BorderSizePixel = Math.Clamp(node.BorderSizePixel, 0, 64);
            node.PaddingLeft = EnsurePair(node.PaddingLeft);
            node.PaddingTop = EnsurePair(node.PaddingTop);
            node.PaddingRight = EnsurePair(node.PaddingRight);
            node.PaddingBottom = EnsurePair(node.PaddingBottom);
            node.CellSize = node.CellSize is null ? [100, 100] : EnsurePair(node.CellSize);
            node.CellSizeScale = EnsurePair(node.CellSizeScale);
            node.CellPadding = EnsurePair(node.CellPadding);
            node.CellPaddingScale = EnsurePair(node.CellPaddingScale);
            node.CanvasPosition = EnsurePair(node.CanvasPosition);
            node.CanvasSize = EnsurePair(node.CanvasSize);
            node.CanvasSizeScale = EnsurePair(node.CanvasSizeScale);
            if (node.AutomaticCanvasSize is not ("None" or "X" or "Y" or "XY")) node.AutomaticCanvasSize = "None";
            node.ScrollBarThickness = Math.Clamp(node.ScrollBarThickness, 0, 100);
            if (node.ScrollingDirection is not ("X" or "Y" or "XY")) node.ScrollingDirection = "XY";
            for (var axis = 0; axis < 2; axis++)
            {
                node.CanvasPosition[axis] = float.IsFinite(node.CanvasPosition[axis]) ? Math.Clamp(node.CanvasPosition[axis], 0, 1000000) : 0;
                node.CanvasSize[axis] = float.IsFinite(node.CanvasSize[axis]) ? Math.Clamp(node.CanvasSize[axis], 0, 1000000) : 0;
                node.CanvasSizeScale[axis] = float.IsFinite(node.CanvasSizeScale[axis]) ? Math.Clamp(node.CanvasSizeScale[axis], 0, 100) : 0;
            }
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
            if (node.SpecialMeshType is not ("Brick" or "FileMesh" or "Head" or "Sphere" or "Cylinder" or "Wedge" or "Torso")) node.SpecialMeshType = "Brick";
            node.SpecialMeshScale = EnsureVector(node.SpecialMeshScale, [1, 1, 1]);
            node.SpecialMeshOffset = EnsureVector(node.SpecialMeshOffset, [0, 0, 0]);
            for (var axis = 0; axis < 3; axis++)
            {
                node.SpecialMeshScale[axis] = float.IsFinite(node.SpecialMeshScale[axis]) ? Math.Clamp(node.SpecialMeshScale[axis], .01f, 1000) : 1;
                node.SpecialMeshOffset[axis] = float.IsFinite(node.SpecialMeshOffset[axis]) ? Math.Clamp(node.SpecialMeshOffset[axis], -100000, 100000) : 0;
            }
            node.SpecialMeshVertexColor = EnsureVector(node.SpecialMeshVertexColor, [1, 1, 1]);
            for (var axis = 0; axis < 3; axis++) node.SpecialMeshVertexColor[axis] = float.IsFinite(node.SpecialMeshVertexColor[axis]) ? Math.Clamp(node.SpecialMeshVertexColor[axis], 0, 1) : 1;
            if (!SceneNodeTypes.IsFace(node.Face)) node.Face = "Front";
            node.StudsPerTileU = float.IsFinite(node.StudsPerTileU) ? Math.Clamp(node.StudsPerTileU, .05f, 1000) : 2;
            node.StudsPerTileV = float.IsFinite(node.StudsPerTileV) ? Math.Clamp(node.StudsPerTileV, .05f, 1000) : 2;
            node.OffsetStudsU = float.IsFinite(node.OffsetStudsU) ? Math.Clamp(node.OffsetStudsU, -10000, 10000) : 0;
            node.OffsetStudsV = float.IsFinite(node.OffsetStudsV) ? Math.Clamp(node.OffsetStudsV, -10000, 10000) : 0;
            if (!SceneNodeTypes.IsMaterial(node.Material)) node.Material = "Plastic";
            node.Transparency = float.IsFinite(node.Transparency) ? Math.Clamp(node.Transparency, 0, 1) : 0;
            node.BackgroundTransparency = float.IsFinite(node.BackgroundTransparency) ? Math.Clamp(node.BackgroundTransparency, 0, 1) : 0;
            node.GroupTransparency = float.IsFinite(node.GroupTransparency) ? Math.Clamp(node.GroupTransparency, 0, 1) : 0;
            node.TextTransparency = float.IsFinite(node.TextTransparency) ? Math.Clamp(node.TextTransparency, 0, 1) : 0;
            node.ImageTransparency = float.IsFinite(node.ImageTransparency) ? Math.Clamp(node.ImageTransparency, 0, 1) : 0;
            if (node.ImageColor3 is null || !System.Text.RegularExpressions.Regex.IsMatch(node.ImageColor3, "^#[0-9A-Fa-f]{6}$")) node.ImageColor3 = "#FFFFFF";
            node.HeadColor3 = NormalizeHexColor(node.HeadColor3, "#E3B98A");
            node.TorsoColor3 = NormalizeHexColor(node.TorsoColor3, "#3973CF");
            node.LeftArmColor3 = NormalizeHexColor(node.LeftArmColor3, "#E3B98A");
            node.RightArmColor3 = NormalizeHexColor(node.RightArmColor3, "#E3B98A");
            node.LeftLegColor3 = NormalizeHexColor(node.LeftLegColor3, "#343B58");
            node.RightLegColor3 = NormalizeHexColor(node.RightLegColor3, "#343B58");
            if (node.ScaleType is not ("Stretch" or "Fit" or "Crop" or "Tile" or "Slice")) node.ScaleType = "Stretch";
            node.SliceCenter = NormalizeSliceCenter(node.SliceCenter);
            node.SliceScale = float.IsFinite(node.SliceScale) ? Math.Clamp(node.SliceScale, .01f, 100) : 1;
            node.ImageRectOffset = NormalizeImageRectPair(node.ImageRectOffset);
            node.ImageRectSize = NormalizeImageRectPair(node.ImageRectSize);
            node.TextSize = float.IsFinite(node.TextSize) ? Math.Clamp(node.TextSize, 1, 200) : 18;
            node.LayoutSpacing = float.IsFinite(node.LayoutSpacing) ? Math.Clamp(node.LayoutSpacing, 0, 1000000) : 0;
            node.ListPaddingScale = float.IsFinite(node.ListPaddingScale) ? Math.Clamp(node.ListPaddingScale, 0, 100) : 0;
            node.MinTextSize = float.IsFinite(node.MinTextSize) ? Math.Clamp(node.MinTextSize, 1, 1000) : 1;
            node.MaxTextSize = float.IsFinite(node.MaxTextSize) ? Math.Clamp(node.MaxTextSize, node.MinTextSize, 1000) : 1000;
            node.AspectRatio = float.IsFinite(node.AspectRatio) ? Math.Clamp(node.AspectRatio, .001f, 1000) : 1;
            if (node.AspectType is not ("FitWithinMaxSize" or "ScaleWithParentSize")) node.AspectType = "FitWithinMaxSize";
            if (node.DominantAxis is not ("Width" or "Height")) node.DominantAxis = "Width";
            if (node.HorizontalAlignment is not ("Left" or "Center" or "Right")) node.HorizontalAlignment = "Left";
            if (node.VerticalAlignment is not ("Top" or "Center" or "Bottom")) node.VerticalAlignment = "Top";
            if (node.SortOrder is not ("LayoutOrder" or "Name")) node.SortOrder = "LayoutOrder";
            node.CurrentPageIndex = Math.Clamp(node.CurrentPageIndex, 1, 1000000);
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
            node.SurfaceTransparency = float.IsFinite(node.SurfaceTransparency) ? Math.Clamp(node.SurfaceTransparency, 0, 1) : 1;
            node.LineThickness = float.IsFinite(node.LineThickness) ? Math.Clamp(node.LineThickness, 0, 10) : .05f;
            if (node.Type == "UICorner") UICornerRadiusService.Normalize(node);
            if (node.Type == "UIShadow") UIShadowService.Normalize(node);
            node.Adornee ??= string.Empty;
            node.ViewportCurrentCameraId ??= string.Empty;
            node.Video ??= string.Empty;
            node.HoverImage ??= string.Empty;
            node.PressedImage ??= string.Empty;
            if (node.SurfaceColor3 is null || !System.Text.RegularExpressions.Regex.IsMatch(node.SurfaceColor3, "^#[0-9A-Fa-f]{6}$")) node.SurfaceColor3 = "#FFFFFF";
            if (node.TintColor is null || !System.Text.RegularExpressions.Regex.IsMatch(node.TintColor, "^#[0-9A-Fa-f]{6}$")) node.TintColor = "#FFFFFF";
            node.ColorCorrectionContrast = float.IsFinite(node.ColorCorrectionContrast) ? Math.Clamp(node.ColorCorrectionContrast, -1, 1) : 0;
            node.ColorCorrectionSaturation = float.IsFinite(node.ColorCorrectionSaturation) ? Math.Clamp(node.ColorCorrectionSaturation, -1, 1) : 0;
            node.Brightness = float.IsFinite(node.Brightness)
                ? node.Type == "ColorCorrectionEffect" ? Math.Clamp(node.Brightness, -1, 1) : Math.Clamp(node.Brightness, 0, 8)
                : node.Type == "ColorCorrectionEffect" ? 0 : 1;
            node.Range = float.IsFinite(node.Range) ? Math.Clamp(node.Range, 1, 1000) : 16;
            node.SecondaryColor ??= "#FFE69A";
            node.EffectSize = float.IsFinite(node.EffectSize) ? Math.Clamp(node.EffectSize, node.Type == "Fire" ? 2 : node.Type is "BlurEffect" or "BloomEffect" ? 0 : .1f, node.Type == "Fire" ? 30 : node.Type is "BlurEffect" or "BloomEffect" ? 56 : 100) : node.Type == "Fire" ? 5 : node.Type == "BloomEffect" ? 24 : 1;
            node.BloomIntensity = float.IsFinite(node.BloomIntensity) ? Math.Clamp(node.BloomIntensity, 0, 1) : 1;
            node.BloomThreshold = float.IsFinite(node.BloomThreshold) ? Math.Clamp(node.BloomThreshold, 0, 2) : 1;
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
            if (!SceneNodeTypes.KeyCodes.Contains(node.KeyboardKeyCode, StringComparer.Ordinal) || node.KeyboardKeyCode is "Unknown" or "None") node.KeyboardKeyCode = "E";
            node.MaxActivationDistance = float.IsFinite(node.MaxActivationDistance) ? Math.Clamp(node.MaxActivationDistance, 1, 100) : 10;
            node.MaxDistance = float.IsFinite(node.MaxDistance) ? Math.Clamp(node.MaxDistance, 0, 100000) : 0;
            node.PixelsPerStud = float.IsFinite(node.PixelsPerStud) ? Math.Clamp(node.PixelsPerStud, 1, 1000) : 50;
            if (node.Type == "SurfaceGui" && !SceneNodeTypes.IsFace(node.Face)) node.Face = "Front";
            node.Volume = float.IsFinite(node.Volume) ? Math.Clamp(node.Volume, 0, 1) : .5f;
            node.PlaybackSpeed = float.IsFinite(node.PlaybackSpeed) ? Math.Clamp(node.PlaybackSpeed, 0, 20) : 1;
            node.SoundServiceVolume = float.IsFinite(node.SoundServiceVolume) ? Math.Clamp(node.SoundServiceVolume, 0, 1) : 1;
            node.SoundGroupVolume = float.IsFinite(node.SoundGroupVolume) ? Math.Clamp(node.SoundGroupVolume, 0, 1) : 1;
            node.SoundGroupId ??= string.Empty;
            node.PitchShiftOctave = float.IsFinite(node.PitchShiftOctave) ? Math.Clamp(node.PitchShiftOctave, .5f, 2) : 1.25f;
            node.EchoDelay = float.IsFinite(node.EchoDelay) ? Math.Clamp(node.EchoDelay, .01f, 5) : 1;
            node.EchoFeedback = float.IsFinite(node.EchoFeedback) ? Math.Clamp(node.EchoFeedback, 0, 1) : .5f;
            node.EchoDryLevel = float.IsFinite(node.EchoDryLevel) ? Math.Clamp(node.EchoDryLevel, -80, 10) : 0;
            node.EchoWetLevel = float.IsFinite(node.EchoWetLevel) ? Math.Clamp(node.EchoWetLevel, -80, 10) : 0;
            node.FlangeDepth = float.IsFinite(node.FlangeDepth) ? Math.Clamp(node.FlangeDepth, .01f, 1) : .45f;
            node.FlangeMix = float.IsFinite(node.FlangeMix) ? Math.Clamp(node.FlangeMix, 0, 1) : .85f;
            node.FlangeRate = float.IsFinite(node.FlangeRate) ? Math.Clamp(node.FlangeRate, 0, 20) : 5;
            node.TremoloDepth = float.IsFinite(node.TremoloDepth) ? Math.Clamp(node.TremoloDepth, 0, 1) : 1;
            node.TremoloDuty = float.IsFinite(node.TremoloDuty) ? Math.Clamp(node.TremoloDuty, 0, 1) : .5f;
            node.TremoloFrequency = float.IsFinite(node.TremoloFrequency) ? Math.Clamp(node.TremoloFrequency, .1f, 20) : 5;
            node.ReverbDecayTime = float.IsFinite(node.ReverbDecayTime) ? Math.Clamp(node.ReverbDecayTime, .1f, 20) : 1.5f;
            node.ReverbDensity = float.IsFinite(node.ReverbDensity) ? Math.Clamp(node.ReverbDensity, 0, 1) : 1;
            node.ReverbDiffusion = float.IsFinite(node.ReverbDiffusion) ? Math.Clamp(node.ReverbDiffusion, 0, 1) : 1;
            node.ReverbDryLevel = float.IsFinite(node.ReverbDryLevel) ? Math.Clamp(node.ReverbDryLevel, -80, 10) : -6;
            node.ReverbWetLevel = float.IsFinite(node.ReverbWetLevel) ? Math.Clamp(node.ReverbWetLevel, -80, 10) : 0;
            node.RollOffMinDistance = float.IsFinite(node.RollOffMinDistance) ? Math.Clamp(node.RollOffMinDistance, 0, 1000) : 10;
            node.RollOffMaxDistance = float.IsFinite(node.RollOffMaxDistance) ? Math.Clamp(node.RollOffMaxDistance, node.RollOffMinDistance + .01f, 10000) : 100;
            node.Part0 ??= string.Empty;
            node.Part1 ??= string.Empty;
            node.PrimaryPart ??= string.Empty;
            node.Character ??= string.Empty;
            node.Occupant ??= string.Empty;
            node.VehicleSeatMaxSpeed = float.IsFinite(node.VehicleSeatMaxSpeed) ? Math.Clamp(node.VehicleSeatMaxSpeed, 0, 10000) : 25;
            node.VehicleSeatTorque = float.IsFinite(node.VehicleSeatTorque) ? Math.Clamp(node.VehicleSeatTorque, 0, 1000000) : 10;
            node.VehicleSeatTurnSpeed = float.IsFinite(node.VehicleSeatTurnSpeed) ? Math.Clamp(node.VehicleSeatTurnSpeed, 0, 1000) : 1;
            node.VehicleSeatThrottleFloat = float.IsFinite(node.VehicleSeatThrottleFloat) ? Math.Clamp(node.VehicleSeatThrottleFloat, -1, 1) : 0;
            node.VehicleSeatSteerFloat = float.IsFinite(node.VehicleSeatSteerFloat) ? Math.Clamp(node.VehicleSeatSteerFloat, -1, 1) : 0;
            node.MaxHealth = float.IsFinite(node.MaxHealth) ? Math.Clamp(node.MaxHealth, 1, 100000) : 100;
            node.Health = float.IsFinite(node.Health) ? Math.Clamp(node.Health, 0, node.MaxHealth) : node.MaxHealth;
            node.WalkSpeed = float.IsFinite(node.WalkSpeed) ? Math.Clamp(node.WalkSpeed, 0, 1000) : 7.5f;
            node.JumpPower = float.IsFinite(node.JumpPower) ? Math.Clamp(node.JumpPower, 0, 1000) : 10;
            node.IntegerValue = Math.Clamp(node.IntegerValue, -9007199254740991L, 9007199254740991L);
            node.NumberValue = double.IsFinite(node.NumberValue) ? node.NumberValue : 0;
            node.StringValueText ??= string.Empty;
            node.Vector2Value = EnsureVector(node.Vector2Value, [0, 0]);
            node.Vector3Value = EnsureVector(node.Vector3Value, [0, 0, 0]);
            if (node.Color3Value is null || !System.Text.RegularExpressions.Regex.IsMatch(node.Color3Value, "^#[0-9A-Fa-f]{6}$")) node.Color3Value = "#000000";
            node.ObjectValueId ??= string.Empty;
            node.Width0 = float.IsFinite(node.Width0) ? Math.Clamp(node.Width0, 0, 100) : .2f;
            node.Width1 = float.IsFinite(node.Width1) ? Math.Clamp(node.Width1, 0, 100) : .2f;
            node.Force = EnsureVector(node.Force, [0, 0, 0]);
            for (var axis = 0; axis < 3; axis++) node.Force[axis] = float.IsFinite(node.Force[axis]) ? Math.Clamp(node.Force[axis], -1000000, 1000000) : 0;
            node.BodyThrustForce = EnsureVector(node.BodyThrustForce, [0, 1, 0]);
            node.BodyThrustLocation = EnsureVector(node.BodyThrustLocation, [0, 0, 0]);
            for (var axis = 0; axis < 3; axis++)
            {
                node.BodyThrustForce[axis] = float.IsFinite(node.BodyThrustForce[axis]) ? Math.Clamp(node.BodyThrustForce[axis], -1000000, 1000000) : 0;
                node.BodyThrustLocation[axis] = float.IsFinite(node.BodyThrustLocation[axis]) ? Math.Clamp(node.BodyThrustLocation[axis], -1000000, 1000000) : 0;
            }
            node.BodyVelocityTarget = EnsureVector(node.BodyVelocityTarget, [0, 0, 0]);
            node.BodyVelocityMaxForce = EnsureVector(node.BodyVelocityMaxForce, [0, 0, 0]);
            for (var axis = 0; axis < 3; axis++)
            {
                node.BodyVelocityTarget[axis] = float.IsFinite(node.BodyVelocityTarget[axis]) ? Math.Clamp(node.BodyVelocityTarget[axis], -10000, 10000) : 0;
                node.BodyVelocityMaxForce[axis] = float.IsFinite(node.BodyVelocityMaxForce[axis]) ? Math.Clamp(node.BodyVelocityMaxForce[axis], 0, 1000000) : 0;
            }
            node.BodyVelocityP = float.IsFinite(node.BodyVelocityP) ? Math.Clamp(node.BodyVelocityP, 0, 1000000) : 1250;
            node.BodyPositionTarget = EnsureVector(node.BodyPositionTarget, [0, 0, 0]);
            node.BodyPositionMaxForce = EnsureVector(node.BodyPositionMaxForce, [0, 0, 0]);
            for (var axis = 0; axis < 3; axis++)
            {
                node.BodyPositionTarget[axis] = float.IsFinite(node.BodyPositionTarget[axis]) ? Math.Clamp(node.BodyPositionTarget[axis], -1000000, 1000000) : 0;
                node.BodyPositionMaxForce[axis] = float.IsFinite(node.BodyPositionMaxForce[axis]) ? Math.Clamp(node.BodyPositionMaxForce[axis], 0, 1000000) : 0;
            }
            node.BodyPositionP = float.IsFinite(node.BodyPositionP) ? Math.Clamp(node.BodyPositionP, 0, 1000000) : 10000;
            node.BodyPositionD = float.IsFinite(node.BodyPositionD) ? Math.Clamp(node.BodyPositionD, 0, 1000000) : 1000;
            node.BodyGyroTargetRotation = EnsureVector(node.BodyGyroTargetRotation, [0, 0, 0]);
            node.BodyGyroMaxTorque = EnsureVector(node.BodyGyroMaxTorque, [0, 0, 0]);
            for (var axis = 0; axis < 3; axis++)
            {
                node.BodyGyroTargetRotation[axis] = float.IsFinite(node.BodyGyroTargetRotation[axis]) ? Math.Clamp(node.BodyGyroTargetRotation[axis], -360000, 360000) : 0;
                node.BodyGyroMaxTorque[axis] = float.IsFinite(node.BodyGyroMaxTorque[axis]) ? Math.Clamp(node.BodyGyroMaxTorque[axis], 0, 1000000) : 0;
            }
            node.BodyGyroP = float.IsFinite(node.BodyGyroP) ? Math.Clamp(node.BodyGyroP, 0, 1000000) : 3000;
            node.BodyGyroD = float.IsFinite(node.BodyGyroD) ? Math.Clamp(node.BodyGyroD, 0, 1000000) : 500;
            node.BodyAngularVelocityTarget = EnsureVector(node.BodyAngularVelocityTarget, [0, 0, 0]);
            node.BodyAngularVelocityMaxTorque = EnsureVector(node.BodyAngularVelocityMaxTorque, [0, 0, 0]);
            for (var axis = 0; axis < 3; axis++)
            {
                node.BodyAngularVelocityTarget[axis] = float.IsFinite(node.BodyAngularVelocityTarget[axis]) ? Math.Clamp(node.BodyAngularVelocityTarget[axis], -10000, 10000) : 0;
                node.BodyAngularVelocityMaxTorque[axis] = float.IsFinite(node.BodyAngularVelocityMaxTorque[axis]) ? Math.Clamp(node.BodyAngularVelocityMaxTorque[axis], 0, 1000000) : 0;
            }
            node.BodyAngularVelocityP = float.IsFinite(node.BodyAngularVelocityP) ? Math.Clamp(node.BodyAngularVelocityP, 0, 1000000) : 1250;
            node.RocketPropulsionTarget ??= string.Empty;
            node.RocketPropulsionTargetOffset = EnsureVector(node.RocketPropulsionTargetOffset, [0, 0, 0]);
            for (var axis = 0; axis < 3; axis++)
            {
                node.RocketPropulsionTargetOffset[axis] = float.IsFinite(node.RocketPropulsionTargetOffset[axis]) ? Math.Clamp(node.RocketPropulsionTargetOffset[axis], -1000000, 1000000) : 0;
            }
            node.RocketPropulsionTargetRadius = float.IsFinite(node.RocketPropulsionTargetRadius) ? Math.Clamp(node.RocketPropulsionTargetRadius, 0, 1000000) : 4;
            node.RocketPropulsionCartoonFactor = float.IsFinite(node.RocketPropulsionCartoonFactor) ? Math.Clamp(node.RocketPropulsionCartoonFactor, 0, 1) : .7f;
            node.RocketPropulsionMaxSpeed = float.IsFinite(node.RocketPropulsionMaxSpeed) ? Math.Clamp(node.RocketPropulsionMaxSpeed, 0, 10000) : 30;
            node.RocketPropulsionMaxThrust = float.IsFinite(node.RocketPropulsionMaxThrust) ? Math.Clamp(node.RocketPropulsionMaxThrust, 0, 1000000) : 4000;
            node.RocketPropulsionMaxTorque = EnsureVector(node.RocketPropulsionMaxTorque, [400000, 400000, 0]);
            for (var axis = 0; axis < 3; axis++)
            {
                node.RocketPropulsionMaxTorque[axis] = float.IsFinite(node.RocketPropulsionMaxTorque[axis]) ? Math.Clamp(node.RocketPropulsionMaxTorque[axis], 0, 1000000) : 0;
            }
            node.RocketPropulsionThrustD = float.IsFinite(node.RocketPropulsionThrustD) ? Math.Clamp(node.RocketPropulsionThrustD, 0, 1000000) : .001f;
            node.RocketPropulsionThrustP = float.IsFinite(node.RocketPropulsionThrustP) ? Math.Clamp(node.RocketPropulsionThrustP, 0, 1000000) : 5;
            node.RocketPropulsionTurnD = float.IsFinite(node.RocketPropulsionTurnD) ? Math.Clamp(node.RocketPropulsionTurnD, 0, 1000000) : 500;
            node.RocketPropulsionTurnP = float.IsFinite(node.RocketPropulsionTurnP) ? Math.Clamp(node.RocketPropulsionTurnP, 0, 1000000) : 3000;
            node.LineForceMagnitude = float.IsFinite(node.LineForceMagnitude) ? Math.Clamp(node.LineForceMagnitude, 0, 1000000) : 1000;
            node.LineForceMaxForce = float.IsFinite(node.LineForceMaxForce) ? Math.Clamp(node.LineForceMaxForce, 0, 1000000) : 1000000;
            if (node.Type is "AngularVelocity" or "Torque")
            {
                if (node.RelativeTo is not ("World" or "Attachment0" or "Attachment1")) node.RelativeTo = node.Type == "Torque" ? "Attachment0" : "World";
            }
            else if (node.Type is "VectorForce" or "LinearVelocity"
                ? node.RelativeTo is not ("World" or "Attachment0" or "Attachment1")
                : node.RelativeTo is not ("World" or "Attachment0"))
                node.RelativeTo = node.Type == "VectorForce" ? "Attachment0" : "World";
            node.VectorVelocity = EnsureVector(node.VectorVelocity, [0, 0, 0]);
            node.LineDirection = EnsureVector(node.LineDirection, [1, 0, 0]);
            node.PrimaryTangentAxis = EnsureVector(node.PrimaryTangentAxis, [1, 0, 0]);
            node.SecondaryTangentAxis = EnsureVector(node.SecondaryTangentAxis, [0, 0, 1]);
            node.MaxAxesForce = EnsureVector(node.MaxAxesForce, [1000, 1000, 1000]);
            node.MaxPlanarAxesForce = EnsurePair(node.MaxPlanarAxesForce);
            for (var axis = 0; axis < 3; axis++) node.MaxAxesForce[axis] = float.IsFinite(node.MaxAxesForce[axis]) ? Math.Clamp(node.MaxAxesForce[axis], 0, 1000000) : 1000;
            for (var axis = 0; axis < 2; axis++) node.MaxPlanarAxesForce[axis] = float.IsFinite(node.MaxPlanarAxesForce[axis]) ? Math.Clamp(node.MaxPlanarAxesForce[axis], 0, 1000000) : 1000;
            if (node.ForceLimitMode is not ("Magnitude" or "PerAxis")) node.ForceLimitMode = "Magnitude";
            foreach (var vector in new[] { node.VectorVelocity, node.LineDirection, node.PrimaryTangentAxis, node.SecondaryTangentAxis })
                for (var axis = 0; axis < 3; axis++) vector[axis] = Math.Clamp(vector[axis], -10000, 10000);
            node.PlaneVelocity = EnsurePair(node.PlaneVelocity);
            for (var axis = 0; axis < 2; axis++) node.PlaneVelocity[axis] = float.IsFinite(node.PlaneVelocity[axis]) ? Math.Clamp(node.PlaneVelocity[axis], -10000, 10000) : 0;
            node.LineVelocity = float.IsFinite(node.LineVelocity) ? Math.Clamp(node.LineVelocity, -10000, 10000) : 0;
            node.MaxForce = float.IsFinite(node.MaxForce) ? Math.Clamp(node.MaxForce, 0, 1000000) : 1000;
            if (node.AlignmentMode is not ("OneAttachment" or "TwoAttachment")) node.AlignmentMode = "TwoAttachment";
            node.Responsiveness = float.IsFinite(node.Responsiveness) ? Math.Clamp(node.Responsiveness, 0, 200) : 10;
            node.MaxVelocity = float.IsFinite(node.MaxVelocity) ? Math.Clamp(node.MaxVelocity, 0, 10000) : 100;
            node.AlignOrientationRotation = EnsureVector(node.AlignOrientationRotation, [0, 0, 0]);
            for (var axis = 0; axis < 3; axis++) node.AlignOrientationRotation[axis] = float.IsFinite(node.AlignOrientationRotation[axis]) ? Math.Clamp(node.AlignOrientationRotation[axis], -360000, 360000) : 0;
            node.AngularVelocityTarget = EnsureVector(node.AngularVelocityTarget, [0, 0, 0]);
            for (var axis = 0; axis < 3; axis++) node.AngularVelocityTarget[axis] = float.IsFinite(node.AngularVelocityTarget[axis]) ? Math.Clamp(node.AngularVelocityTarget[axis], -1000, 1000) : 0;
            node.TorqueTarget = EnsureVector(node.TorqueTarget, [0, 0, 0]);
            for (var axis = 0; axis < 3; axis++) node.TorqueTarget[axis] = float.IsFinite(node.TorqueTarget[axis]) ? Math.Clamp(node.TorqueTarget[axis], -1000000, 1000000) : 0;
            node.SpringDamping = float.IsFinite(node.SpringDamping) ? Math.Clamp(node.SpringDamping, 0, 10000) : 10;
            node.SpringFreeLength = float.IsFinite(node.SpringFreeLength) ? Math.Clamp(node.SpringFreeLength, 0, 10000) : 4;
            node.SpringMaxForce = float.IsFinite(node.SpringMaxForce) ? Math.Clamp(node.SpringMaxForce, 0, 1000000) : 5000;
            node.SpringMinLength = float.IsFinite(node.SpringMinLength) ? Math.Clamp(node.SpringMinLength, 0, 10000) : 0;
            node.SpringMaxLength = float.IsFinite(node.SpringMaxLength) ? Math.Clamp(node.SpringMaxLength, node.SpringMinLength, 10000) : Math.Max(10, node.SpringMinLength);
            node.SpringStiffness = float.IsFinite(node.SpringStiffness) ? Math.Clamp(node.SpringStiffness, 0, 100000) : 100;
            node.TorsionSpringDamping = float.IsFinite(node.TorsionSpringDamping) ? Math.Clamp(node.TorsionSpringDamping, 0, 100000) : 10;
            node.TorsionSpringMaxAngle = float.IsFinite(node.TorsionSpringMaxAngle) ? Math.Clamp(node.TorsionSpringMaxAngle, 0, 180) : 45;
            node.TorsionSpringMaxTorque = float.IsFinite(node.TorsionSpringMaxTorque) ? Math.Clamp(node.TorsionSpringMaxTorque, 0, 1000000) : 10000;
            node.TorsionSpringRadius = float.IsFinite(node.TorsionSpringRadius) ? Math.Clamp(node.TorsionSpringRadius, 0, 100) : .15f;
            node.TorsionSpringRestitution = float.IsFinite(node.TorsionSpringRestitution) ? Math.Clamp(node.TorsionSpringRestitution, 0, 1) : 0;
            node.TorsionSpringStiffness = float.IsFinite(node.TorsionSpringStiffness) ? Math.Clamp(node.TorsionSpringStiffness, 0, 100000) : 100;
            node.TorsionSpringCoils = float.IsFinite(node.TorsionSpringCoils) ? Math.Clamp(node.TorsionSpringCoils, 0, 8) : 3;
            node.BallSocketUpperAngle = float.IsFinite(node.BallSocketUpperAngle) ? Math.Clamp(node.BallSocketUpperAngle, 0, 180) : 45;
            node.BallSocketTwistLowerAngle = float.IsFinite(node.BallSocketTwistLowerAngle) ? Math.Clamp(node.BallSocketTwistLowerAngle, -180, 180) : -45;
            node.BallSocketTwistUpperAngle = float.IsFinite(node.BallSocketTwistUpperAngle) ? Math.Clamp(node.BallSocketTwistUpperAngle, node.BallSocketTwistLowerAngle, 180) : Math.Max(45, node.BallSocketTwistLowerAngle);
            node.BallSocketMaxFrictionTorque = float.IsFinite(node.BallSocketMaxFrictionTorque) ? Math.Clamp(node.BallSocketMaxFrictionTorque, 0, 1000000) : 0;
            node.BallSocketRadius = float.IsFinite(node.BallSocketRadius) ? Math.Clamp(node.BallSocketRadius, 0, 100) : .15f;
            node.BallSocketRestitution = float.IsFinite(node.BallSocketRestitution) ? Math.Clamp(node.BallSocketRestitution, 0, 1) : .2f;
            node.UniversalMaxAngle = float.IsFinite(node.UniversalMaxAngle) ? Math.Clamp(node.UniversalMaxAngle, 0, 180) : 45;
            node.UniversalRadius = float.IsFinite(node.UniversalRadius) ? Math.Clamp(node.UniversalRadius, 0, 100) : .2f;
            node.UniversalRestitution = float.IsFinite(node.UniversalRestitution) ? Math.Clamp(node.UniversalRestitution, 0, 1) : 0;
            if (node.HingeActuatorType is not ("None" or "Motor" or "Servo")) node.HingeActuatorType = "None";
            node.HingeAngularResponsiveness = float.IsFinite(node.HingeAngularResponsiveness) ? Math.Clamp(node.HingeAngularResponsiveness, 0, 200) : 45;
            node.HingeAngularSpeed = float.IsFinite(node.HingeAngularSpeed) ? Math.Clamp(node.HingeAngularSpeed, 0, 1000) : 10;
            node.HingeAngularVelocity = float.IsFinite(node.HingeAngularVelocity) ? Math.Clamp(node.HingeAngularVelocity, -1000, 1000) : 0;
            node.HingeLowerAngle = float.IsFinite(node.HingeLowerAngle) ? Math.Clamp(node.HingeLowerAngle, -180, 180) : -45;
            node.HingeUpperAngle = float.IsFinite(node.HingeUpperAngle) ? Math.Clamp(node.HingeUpperAngle, node.HingeLowerAngle, 180) : Math.Max(45, node.HingeLowerAngle);
            node.HingeMotorMaxAcceleration = float.IsFinite(node.HingeMotorMaxAcceleration) ? Math.Clamp(node.HingeMotorMaxAcceleration, 0, 100000) : 1000;
            node.HingeMotorMaxTorque = float.IsFinite(node.HingeMotorMaxTorque) ? Math.Clamp(node.HingeMotorMaxTorque, 0, 1000000) : 10000;
            node.HingeRadius = float.IsFinite(node.HingeRadius) ? Math.Clamp(node.HingeRadius, 0, 100) : .15f;
            node.HingeRestitution = float.IsFinite(node.HingeRestitution) ? Math.Clamp(node.HingeRestitution, 0, 1) : 0;
            node.HingeServoMaxTorque = float.IsFinite(node.HingeServoMaxTorque) ? Math.Clamp(node.HingeServoMaxTorque, 0, 1000000) : 10000;
            node.HingeTargetAngle = float.IsFinite(node.HingeTargetAngle) ? Math.Clamp(node.HingeTargetAngle, -360000, 360000) : 0;
            if (node.PrismaticActuatorType is not ("None" or "Motor" or "Servo")) node.PrismaticActuatorType = "None";
            node.PrismaticLinearResponsiveness = float.IsFinite(node.PrismaticLinearResponsiveness) ? Math.Clamp(node.PrismaticLinearResponsiveness, 0, 200) : 45;
            node.PrismaticLowerLimit = float.IsFinite(node.PrismaticLowerLimit) ? Math.Clamp(node.PrismaticLowerLimit, -10000, 10000) : 0;
            node.PrismaticMotorMaxAcceleration = float.IsFinite(node.PrismaticMotorMaxAcceleration) ? Math.Clamp(node.PrismaticMotorMaxAcceleration, 0, 100000) : 1000;
            node.PrismaticMotorMaxForce = float.IsFinite(node.PrismaticMotorMaxForce) ? Math.Clamp(node.PrismaticMotorMaxForce, 0, 1000000) : 10000;
            node.PrismaticRestitution = float.IsFinite(node.PrismaticRestitution) ? Math.Clamp(node.PrismaticRestitution, 0, 1) : 0;
            node.PrismaticServoMaxForce = float.IsFinite(node.PrismaticServoMaxForce) ? Math.Clamp(node.PrismaticServoMaxForce, 0, 1000000) : 10000;
            node.PrismaticSize = float.IsFinite(node.PrismaticSize) ? Math.Clamp(node.PrismaticSize, 0, 100) : .15f;
            node.PrismaticSpeed = float.IsFinite(node.PrismaticSpeed) ? Math.Clamp(node.PrismaticSpeed, 0, 10000) : 10;
            node.PrismaticTargetPosition = float.IsFinite(node.PrismaticTargetPosition) ? Math.Clamp(node.PrismaticTargetPosition, -10000, 10000) : 0;
            node.PrismaticUpperLimit = float.IsFinite(node.PrismaticUpperLimit) ? Math.Clamp(node.PrismaticUpperLimit, node.PrismaticLowerLimit, 10000) : Math.Max(node.PrismaticLowerLimit, 0);
            node.PrismaticVelocity = float.IsFinite(node.PrismaticVelocity) ? Math.Clamp(node.PrismaticVelocity, -10000, 10000) : 0;
            node.RopeLength = float.IsFinite(node.RopeLength) ? Math.Clamp(node.RopeLength, 0, 10000) : 10;
            node.RopeRestitution = float.IsFinite(node.RopeRestitution) ? Math.Clamp(node.RopeRestitution, 0, 1) : 0;
            node.RopeWinchForce = float.IsFinite(node.RopeWinchForce) ? Math.Clamp(node.RopeWinchForce, 0, 1000000) : 10000;
            node.RopeWinchResponsiveness = float.IsFinite(node.RopeWinchResponsiveness) ? Math.Clamp(node.RopeWinchResponsiveness, 0, 200) : 10;
            node.RopeWinchSpeed = float.IsFinite(node.RopeWinchSpeed) ? Math.Clamp(node.RopeWinchSpeed, 0, 10000) : 10;
            node.RopeWinchTarget = float.IsFinite(node.RopeWinchTarget) ? Math.Clamp(node.RopeWinchTarget, 0, 10000) : 5;
            node.RodLength = float.IsFinite(node.RodLength) ? Math.Clamp(node.RodLength, 0, 10000) : 10;
            node.RodLimitAngle0 = float.IsFinite(node.RodLimitAngle0) ? Math.Clamp(node.RodLimitAngle0, 0, 180) : 45;
            node.RodLimitAngle1 = float.IsFinite(node.RodLimitAngle1) ? Math.Clamp(node.RodLimitAngle1, 0, 180) : 45;
            node.RodThickness = float.IsFinite(node.RodThickness) ? Math.Clamp(node.RodThickness, 0, 100) : .1f;
            node.CylindricalInclinationAngle = float.IsFinite(node.CylindricalInclinationAngle) ? Math.Clamp(node.CylindricalInclinationAngle, -180, 180) : 0;
            node.MaxTorque = float.IsFinite(node.MaxTorque) ? Math.Clamp(node.MaxTorque, 0, 1000000) : 1000;
            node.MaxAngularVelocity = float.IsFinite(node.MaxAngularVelocity) ? Math.Clamp(node.MaxAngularVelocity, 0, 10000) : 100;
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
        var validTeamIds = project.Teams.Children.Where(node => node.Type == "Team").Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var player in Flatten(project.Players.Children).Where(node => node.Type == "Player" && node.Team.Length > 0))
            if (!validTeamIds.Contains(player.Team)) player.Team = string.Empty;
        NormalizeModelPrimaryParts(project);
        foreach (var objectValue in Flatten(project).Where(node => node.Type == "ObjectValue" && node.ObjectValueId.Length > 0))
            if (!validObjectIds.Contains(objectValue.ObjectValueId)) objectValue.ObjectValueId = string.Empty;
        var validSoundGroupIds = Flatten(project).Where(node => node.Type == "SoundGroup").Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var sound in Flatten(project).Where(node => node.Type == "Sound" && node.SoundGroupId.Length > 0))
            if (!validSoundGroupIds.Contains(sound.SoundGroupId)) sound.SoundGroupId = string.Empty;
        var validAdorneeIds = Flatten(project)
            .Where(node => SceneNodeTypes.IsBasePart(node) || node.Type == "Model")
            .Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var adornment in Flatten(project).Where(node => node.Type is ("SelectionBox" or "SelectionSphere" or "Highlight" or "BillboardGui" or "SurfaceGui") && node.Adornee.Length > 0))
            if (!validAdorneeIds.Contains(adornment.Adornee)) adornment.Adornee = string.Empty;
        var validCameraIds = Flatten(project).Where(node => node.Type == "Camera").Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var viewport in Flatten(project).Where(node => node.Type == "ViewportFrame" && node.ViewportCurrentCameraId.Length > 0))
            if (!validCameraIds.Contains(viewport.ViewportCurrentCameraId)) viewport.ViewportCurrentCameraId = string.Empty;
        var scriptNames = Flatten(project.Workspace.Children).Concat(Flatten(project.ReplicatedStorage.Children)).Concat(Flatten(project.ServerScriptService.Children)).Concat(Flatten(project.ServerStorage.Children)).Concat(Flatten(project.StarterPack.Children)).Concat(Flatten(project.StarterPlayer.Children)).Concat(Flatten(project.Gui.Children))
        .Where(node => node.Type is "Script" or "LocalScript" or "ModuleScript")
            .GroupBy(node => node.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);
        foreach (var script in project.Scripts)
            if (scriptNames.TryGetValue(script.Id, out var name)) script.Name = name;
        var cameras = Flatten(project.Workspace.Children).Where(node => node.Type == "Camera").ToList();
        var camera = cameras.FirstOrDefault() ?? new SceneNode { Id = "camera-main", Type = "Camera", Name = "Camera", Position = [8, 7, 11], Rotation = [-24, 35, 0] };
        foreach (var duplicate in cameras.Skip(1)) RemoveNode(project.Workspace.Children, duplicate.Id);
        RemoveNode(project.Workspace.Children, camera.Id);
        project.Workspace.Children.Insert(0, camera);
        project.Workspace.Children.Remove(terrain);
        project.Workspace.Children.Insert(1, terrain);
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

    private static float[] NormalizeImageRectPair(float[]? values)
    {
        var result = EnsurePair(values);
        for (var index = 0; index < result.Length; index++)
            result[index] = float.IsFinite(result[index]) ? Math.Clamp(result[index], 0, 1000000) : 0;
        return result;
    }

    private static float[] NormalizeSliceCenter(float[]? values)
    {
        var result = EnsureVector(values, [0, 0, 0, 0]);
        for (var index = 0; index < result.Length; index++) result[index] = Math.Clamp(result[index], 0, 1000000);
        result[2] = Math.Max(result[0], result[2]);
        result[3] = Math.Max(result[1], result[3]);
        return result;
    }

    private static string NormalizeHexColor(string? value, string fallback) =>
        value is not null && System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$") ? value.ToUpperInvariant() : fallback;

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

    public static IReadOnlyList<SceneNode> Flatten(GameProject project) => Flatten([project.Workspace, project.Players, project.Teams, project.Lighting, project.SoundService, project.ReplicatedStorage, project.ServerScriptService, project.ServerStorage, project.StarterPack, project.StarterPlayer, project.Gui]);

    public static void NormalizeModelPrimaryParts(GameProject project)
    {
        foreach (var model in Flatten(project).Where(node => node.Type == "Model" && node.PrimaryPart.Length > 0))
            if (!SceneModelService.ContainsPrimaryPart(model, model.PrimaryPart))
            {
                model.PrimaryPart = string.Empty;
                model.HasModelPivot = true;
            }
    }

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
            if (idMap.TryGetValue(node.PrimaryPart, out var primaryPart)) node.PrimaryPart = primaryPart;
            if (idMap.TryGetValue(node.Attachment0, out var attachment0)) node.Attachment0 = attachment0;
            if (idMap.TryGetValue(node.Attachment1, out var attachment1)) node.Attachment1 = attachment1;
            if (idMap.TryGetValue(node.Subject, out var subject)) node.Subject = subject;
            if (idMap.TryGetValue(node.Character, out var character)) node.Character = character;
            if (idMap.TryGetValue(node.Occupant, out var occupant)) node.Occupant = occupant;
            if (idMap.TryGetValue(node.ObjectValueId, out var referencedObject)) node.ObjectValueId = referencedObject;
            if (idMap.TryGetValue(node.SoundGroupId, out var soundGroup)) node.SoundGroupId = soundGroup;
            if (idMap.TryGetValue(node.Adornee, out var adornee)) node.Adornee = adornee;
            if (idMap.TryGetValue(node.ViewportCurrentCameraId, out var viewportCamera)) node.ViewportCurrentCameraId = viewportCamera;
        }
        if (!respectArchivable) clone.Archivable = true;
        return clone;
    }

    public static bool CanParentNode(GameProject project, SceneNode source, SceneNode destinationParent)
    {
        if (source.Id == project.Workspace.Id || source.Id == project.Players.Id || source.Id == project.Teams.Id || source.Id == project.Lighting.Id || source.Id == project.SoundService.Id || source.Id == project.ReplicatedStorage.Id || source.Id == project.ServerScriptService.Id || source.Id == project.ServerStorage.Id || source.Id == project.StarterPack.Id || source.Id == project.StarterPlayer.Id || source.Id == project.Gui.Id || source.Type == "Terrain" || source.Id == destinationParent.Id) return false;
        if (!IsNodeInTree(project.Workspace, destinationParent.Id) && !IsNodeInTree(project.Players, destinationParent.Id) && !IsNodeInTree(project.Teams, destinationParent.Id) && !IsNodeInTree(project.Lighting, destinationParent.Id) && !IsNodeInTree(project.SoundService, destinationParent.Id) && !IsNodeInTree(project.ReplicatedStorage, destinationParent.Id) && !IsNodeInTree(project.ServerScriptService, destinationParent.Id) && !IsNodeInTree(project.ServerStorage, destinationParent.Id) && !IsNodeInTree(project.StarterPack, destinationParent.Id) && !IsNodeInTree(project.StarterPlayer, destinationParent.Id) && !IsNodeInTree(project.Gui, destinationParent.Id)) return false;
        if (Flatten(source.Children).Any(node => node.Id == destinationParent.Id)) return false;
        if (destinationParent.Type == "Terrain") return source.Type == "Clouds" && source.Children.Count == 0
            || SceneNodeTypes.IsBasePart(source) && IsWorkspaceSubtreeCompatible(source, "Terrain");
        if (source.Type == "Humanoid")
            return destinationParent.Type == "Model" && source.Children.Count == 0
                && !destinationParent.Children.Any(child => child.Type == "Humanoid" && child.Id != source.Id);
        if (source.Type == "BodyColors")
            return destinationParent.Type == "Model" && source.Children.Count == 0
                && !destinationParent.Children.Any(child => child.Type == "BodyColors" && child.Id != source.Id);
        if (source.Type == "PathfindingModifier")
            return !IsInsideNodeType(project.Gui, destinationParent.Id, "WorldModel")
                && SceneNodeTypes.IsBasePart(destinationParent) && source.Children.Count == 0
                && !destinationParent.Children.Any(child => child.Type == "PathfindingModifier" && child.Id != source.Id);
        if (source.Type == "PathfindingLink") return source.Children.Count == 0
            && !IsInsideNodeType(project.Gui, destinationParent.Id, "WorldModel")
            && (destinationParent.Id == project.Workspace.Id || destinationParent.Type is "Folder" or "Model" or "Configuration");
        if (source.Type == "WorldModel")
            return destinationParent.Type == "ViewportFrame" && IsNodeInTree(project.Gui, destinationParent.Id)
                && !destinationParent.Children.Any(child => child.Type == "WorldModel" && child.Id != source.Id)
                && source.Children.All(IsViewportFrameChildCompatible);
        if (destinationParent.Type == "WorldModel")
            return IsNodeInTree(project.Gui, destinationParent.Id) && IsViewportFrameChildCompatible(source);
        if (IsInsideNodeType(project.Gui, destinationParent.Id, "WorldModel"))
            return source.Type is not ("Camera" or "WorldModel" or "PathfindingModifier" or "PathfindingLink")
                && IsViewportWorldModelSubtreeCompatible(source, destinationParent.Type);
        if (source.Type == "Camera")
            return IsNodeInTree(project.Workspace, source.Id)
                ? destinationParent.Id == project.Workspace.Id && source.Children.All(child => IsWorkspaceSubtreeCompatible(child, "Camera"))
                : destinationParent.Type == "ViewportFrame" && IsNodeInTree(project.Gui, destinationParent.Id)
                    && source.Children.All(child => IsWorkspaceSubtreeCompatible(child, "Camera"));
        if (source.Type == "Animation")
            return (destinationParent.Type is "Workspace" or "Folder" or "Configuration" or "Model" or "Humanoid" or "Tool" or "ReplicatedStorage" or "ServerStorage" or "ServerScriptService" or "StarterPack" or "StarterPlayer" or "StarterGui" or "Backpack" or "PlayerScripts" or "PlayerGui" || SceneNodeTypes.IsBasePart(destinationParent))
                && source.Children.Count == 0;
        if (source.Type == "AnimationController")
            return destinationParent.Type == "Model" && source.Children.All(child => child.Type == "Animator" && IsWorkspaceSubtreeCompatible(child, "AnimationController"))
                && !destinationParent.Children.Any(child => child.Type == "AnimationController" && child.Id != source.Id);
        if (source.Type == "Animator")
            return destinationParent.Type is "Humanoid" or "AnimationController" && source.Children.Count == 0
                && !destinationParent.Children.Any(child => child.Type == "Animator" && child.Id != source.Id);
        if (SceneNodeTypes.IsSurfaceAppearance(source))
            return destinationParent.Type == "MeshPart" && source.Children.Count == 0
                && !destinationParent.Children.Any(child => SceneNodeTypes.IsSurfaceAppearance(child) && child.Id != source.Id);
        if (SceneNodeTypes.IsSpecialMesh(source))
            return destinationParent.Type == "Part" && source.Children.Count == 0
                && !destinationParent.Children.Any(child => SceneNodeTypes.IsSpecialMesh(child) && child.Id != source.Id);
        if (source.Type is "BlurEffect" or "BloomEffect" or "ColorCorrectionEffect")
            return source.Children.Count == 0 && (destinationParent.Id == project.Lighting.Id
                || destinationParent.Type == "Camera" && project.Workspace.Children.Any(camera => camera.Id == destinationParent.Id && camera.Type == "Camera"));
        if (destinationParent.Id == project.Lighting.Id)
            return source.Type is "Sky" or "Atmosphere" && source.Children.Count == 0
                && !destinationParent.Children.Any(child => child.Type == source.Type && child.Id != source.Id);
        if (source.Type is "Sky" or "Atmosphere" or "Clouds" or "BloomEffect" or "BlurEffect" or "ColorCorrectionEffect" || IsNodeInTree(project.Lighting, destinationParent.Id)) return false;
        if (destinationParent.Id == project.SoundService.Id) return IsWorkspaceSubtreeCompatible(source, "SoundService") || source.Type == "Folder";
        if (IsNodeInTree(project.SoundService, destinationParent.Id)) return destinationParent.Type is "Folder" or "Configuration" or "Sound" or "SoundGroup" && IsWorkspaceSubtreeCompatible(source, destinationParent.Type);

        if (destinationParent.Id == project.Players.Id) return source.Type == "Player";
        if (destinationParent.Id == project.Teams.Id) return source.Type == "Team" && source.Children.Count == 0;
        if (IsNodeInTree(project.Teams, destinationParent.Id)) return false;
        if (destinationParent.Id == project.StarterPlayer.Id)
            return source.Type == "Folder" && source.Name is ("StarterPlayerScripts" or "StarterCharacterScripts")
                && !destinationParent.Children.Any(child => child.Name == source.Name && child.Id != source.Id)
                && IsStarterPlayerTemplateCompatible(source);
        if (IsNodeInTree(project.StarterPlayer, destinationParent.Id))
        {
            var current = destinationParent;
            while (FindParentNode(project.StarterPlayer, current) is { } parent && parent.Id != project.StarterPlayer.Id) current = parent;
            return current.Name is ("StarterPlayerScripts" or "StarterCharacterScripts") && IsStarterPlayerTemplateCompatible(source);
        }
        if (destinationParent.Type == "PlayerScripts") return IsStarterPlayerTemplateCompatible(source);
        if (destinationParent.Type == "Backpack") return source.Type is ("Tool" or "Folder" or "LocalScript" or "ModuleScript")
            && IsWorkspaceSubtreeCompatible(source, "Backpack");
        if (IsNodeInTree(project.Players, destinationParent.Id))
        {
            if (destinationParent.Type == "Player") return source.Type == "Backpack" && source.Name == "Backpack"
                || source.Type == "PlayerScripts" && source.Name == "PlayerScripts";
            if (destinationParent.Type == "PlayerGui") return source.Type == "LocalScript" && source.Children.Count == 0 || source.Type == "ScreenGui" && IsGuiSubtreeCompatible(source, true);
            var playerGui = project.Players.Children.SelectMany(player => player.Children).FirstOrDefault(child => child.Type == "PlayerGui");
            if (playerGui is not null && IsNodeInTree(playerGui, destinationParent.Id))
            {
                if (source.Type == "ScreenGui" || !IsGuiContainer(destinationParent.Type) && !SceneNodeTypes.IsValueObject(destinationParent)) return false;
                return IsGuiSubtreeCompatible(source, false, destinationParent.Type);
            }
            if (destinationParent.Type == "Backpack") return source.Type is ("Tool" or "Folder" or "LocalScript" or "ModuleScript");
            if (destinationParent.Type == "PlayerScripts") return IsStarterPlayerTemplateCompatible(source);
            return destinationParent.Type == "Model" && (source.Type is "Tool" or "Accessory");
        }

        var destinationIsGui = IsNodeInTree(project.Gui, destinationParent.Id);
        if (destinationParent.Type is "BillboardGui" or "SurfaceGui") return IsGuiSubtreeCompatible(source, false, destinationParent.Type);
        if (destinationIsGui)
        {
            if (destinationParent.Id == project.Gui.Id)
                return (source.Type is "ScreenGui" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Script" or "LocalScript" || SceneNodeTypes.IsValueObject(source)) && IsGuiSubtreeCompatible(source, true);
            if (!IsGuiContainer(destinationParent.Type) && !SceneNodeTypes.IsValueObject(destinationParent)) return false;
            if (source.Type == "ScreenGui") return false;
            return IsGuiSubtreeCompatible(source, false, destinationParent.Type);
        }

        if (IsNodeInTree(project.ReplicatedStorage, destinationParent.Id)
            || IsNodeInTree(project.ServerScriptService, destinationParent.Id)
            || IsNodeInTree(project.ServerStorage, destinationParent.Id)
            || IsNodeInTree(project.StarterPack, destinationParent.Id))
        {
            if (destinationParent.Type is not ("ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" or "SoundService" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Model" or "Tool" or "Accessory" or "Attachment")
                && !SceneNodeTypes.IsBasePart(destinationParent) && !SceneNodeTypes.IsValueObject(destinationParent)) return false;
            return IsWorkspaceSubtreeCompatible(source, destinationParent.Type is "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" ? "Workspace" : destinationParent.Type);
        }

        if (destinationParent.Type is not ("Workspace" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Model" or "Tool" or "Accessory" or "Attachment" or "Sound" or "SoundGroup") && !SceneNodeTypes.IsBasePart(destinationParent) && !SceneNodeTypes.IsValueObject(destinationParent)) return false;
        return IsWorkspaceSubtreeCompatible(source, destinationParent.Type);
    }

    public static bool CanParentDetachedNode(SceneNode source, SceneNode destinationParent)
    {
        if (source.Id == destinationParent.Id || Flatten(source.Children).Any(child => child.Id == destinationParent.Id)) return false;
        if (source.Type is "Workspace" or "Players" or "PlayerGui" or "Teams" or "Lighting" or "SoundService" or "ReplicatedStorage" or "ServerScriptService" or "ServerStorage" or "StarterPack" or "StarterPlayer" or "Gui" or "StarterGui" or "Terrain") return false;
        if (source.Type == "WorldModel")
            return destinationParent.Type == "ViewportFrame" && !destinationParent.Children.Any(child => child.Type == "WorldModel" && child.Id != source.Id)
                && source.Children.All(IsViewportFrameChildCompatible);
        if (destinationParent.Type == "WorldModel") return IsViewportFrameChildCompatible(source);
        if (source.Type == "Camera") return destinationParent.Type == "ViewportFrame" && source.Children.All(child => IsWorkspaceSubtreeCompatible(child, "Camera"));
        if (destinationParent.Type == "Teams") return source.Type == "Team" && source.Children.Count == 0;
        if (destinationParent.Type == "StarterPlayer") return source.Type == "Folder" && source.Name is ("StarterPlayerScripts" or "StarterCharacterScripts") && IsStarterPlayerTemplateCompatible(source);
        if (destinationParent.Name is "StarterPlayerScripts" or "StarterCharacterScripts") return IsStarterPlayerTemplateCompatible(source);
        if (destinationParent.Type == "PlayerScripts") return IsStarterPlayerTemplateCompatible(source);
        if (destinationParent.Type == "Backpack") return source.Type is ("Tool" or "Folder" or "LocalScript" or "ModuleScript")
            && IsWorkspaceSubtreeCompatible(source, "Backpack");
        if (destinationParent.Type == "Terrain") return SceneNodeTypes.IsBasePart(source)
            && IsWorkspaceSubtreeCompatible(source, "Terrain");
        if (source.Type == "Animation")
            return (destinationParent.Type is "Workspace" or "Folder" or "Configuration" or "Model" or "Humanoid" or "Tool" or "ReplicatedStorage" or "ServerStorage" or "ServerScriptService" or "StarterPack" or "StarterPlayer" or "StarterGui" or "Backpack" or "PlayerScripts" or "PlayerGui" || SceneNodeTypes.IsBasePart(destinationParent))
                && source.Children.Count == 0;
        if (source.Type == "AnimationController")
            return destinationParent.Type == "Model" && source.Children.All(child => child.Type == "Animator" && IsWorkspaceSubtreeCompatible(child, "AnimationController"))
                && !destinationParent.Children.Any(child => child.Type == "AnimationController");
        if (source.Type == "Animator")
            return destinationParent.Type is "Humanoid" or "AnimationController" && source.Children.Count == 0
                && !destinationParent.Children.Any(child => child.Type == "Animator");
        if (SceneNodeTypes.IsSurfaceAppearance(source))
            return destinationParent.Type == "MeshPart" && source.Children.Count == 0
                && !destinationParent.Children.Any(SceneNodeTypes.IsSurfaceAppearance);
        if (SceneNodeTypes.IsSpecialMesh(source))
            return destinationParent.Type == "Part" && source.Children.Count == 0
                && !destinationParent.Children.Any(SceneNodeTypes.IsSpecialMesh);
        if (destinationParent.Type == "Team") return false;
        if (destinationParent.Type == "Lighting") return source.Type is "Sky" or "Atmosphere" && source.Children.Count == 0 && destinationParent.Children.All(child => child.Type != source.Type)
            || source.Type is "BlurEffect" or "BloomEffect" or "ColorCorrectionEffect" && source.Children.Count == 0;
        if (source.Type is "BlurEffect" or "BloomEffect" or "ColorCorrectionEffect") return destinationParent.Type == "Camera" && source.Children.Count == 0;
        if (destinationParent.Type is "Sound" or "SoundGroup") return IsWorkspaceSubtreeCompatible(source, destinationParent.Type);
        if (source.Type is "Sky" or "Atmosphere") return false;
        if (destinationParent.Type == "PlayerGui") return source.Type == "LocalScript" && source.Children.Count == 0 || source.Type == "ScreenGui" && IsGuiSubtreeCompatible(source, true);
        if (source.Type == "Humanoid")
            return destinationParent.Type == "Model" && source.Children.Count == 0
                && !destinationParent.Children.Any(child => child.Type == "Humanoid" && child.Id != source.Id);
        if (destinationParent.Type is "Model" or "Accessory" or "Attachment" || SceneNodeTypes.IsBasePart(destinationParent))
            return IsWorkspaceSubtreeCompatible(source, destinationParent.Type);
        if (destinationParent.Type is "BillboardGui" or "SurfaceGui") return IsGuiSubtreeCompatible(source, false, destinationParent.Type);
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
            ?? FindParentCollection(project.SoundService.Children, source)
            ?? FindParentCollection(project.Teams.Children, source)
            ?? FindParentCollection(project.ReplicatedStorage.Children, source)
            ?? FindParentCollection(project.ServerScriptService.Children, source)
            ?? FindParentCollection(project.ServerStorage.Children, source)
            ?? FindParentCollection(project.StarterPack.Children, source)
            ?? FindParentCollection(project.StarterPlayer.Children, source)
            ?? FindParentCollection(project.Players.Children, source)
            ?? FindParentCollection(project.Gui.Children, source);
        if (sourceCollection is null || ReferenceEquals(sourceCollection, destinationParent.Children)) return false;
        sourceCollection.Remove(source);
        destinationParent.Children.Add(source);
        NormalizeModelPrimaryParts(project);
        return true;
    }

    public static bool MoveNodeBeside(GameProject project, SceneNode source, SceneNode target, bool after)
    {
        if (source.Type == "Camera" || target.Type == "Camera" || ReferenceEquals(source, target) || source.Id == target.Id) return false;
        var destinationParent = FindParentNode(project.Workspace, target) ?? FindParentNode(project.Teams, target) ?? FindParentNode(project.Lighting, target) ?? FindParentNode(project.SoundService, target) ?? FindParentNode(project.ReplicatedStorage, target) ?? FindParentNode(project.ServerScriptService, target) ?? FindParentNode(project.ServerStorage, target) ?? FindParentNode(project.StarterPack, target) ?? FindParentNode(project.StarterPlayer, target) ?? FindParentNode(project.Gui, target);
        if (destinationParent is null || !CanParentNode(project, source, destinationParent)) return false;
        var sourceCollection = FindParentCollection(project.Workspace.Children, source)
            ?? FindParentCollection(project.Lighting.Children, source)
            ?? FindParentCollection(project.SoundService.Children, source)
            ?? FindParentCollection(project.Teams.Children, source)
            ?? FindParentCollection(project.ReplicatedStorage.Children, source)
            ?? FindParentCollection(project.ServerScriptService.Children, source)
            ?? FindParentCollection(project.ServerStorage.Children, source)
            ?? FindParentCollection(project.StarterPack.Children, source)
            ?? FindParentCollection(project.StarterPlayer.Children, source)
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
        NormalizeModelPrimaryParts(project);
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

    private static bool IsInsideNodeType(SceneNode root, string nodeId, string type)
    {
        var node = Flatten(root.Children).FirstOrDefault(candidate => candidate.Id == nodeId);
        while (node is not null)
        {
            if (node.Type == type) return true;
            node = FindParentNode(root, node);
        }
        return false;
    }

    private static bool IsNodeInTree(SceneNode root, string nodeId) =>
        root.Id == nodeId || Flatten(root.Children).Any(node => node.Id == nodeId);

    private static bool IsStarterPlayerTemplateCompatible(SceneNode node) => node.Type switch
    {
        "Folder" => node.Children.All(IsStarterPlayerTemplateCompatible),
        "LocalScript" or "ModuleScript" => node.Children.Count == 0,
        _ => false
    };

    private static bool IsGuiContainer(string type) => type is "Gui" or "StarterGui" or "PlayerGui" or "ScreenGui" or "BillboardGui" or "SurfaceGui" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Frame" or "ScrollingFrame" or "ViewportFrame" or "VideoFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox" or "UICanvasGroup" or "UIShadow";

    private static bool IsGuiObject(string type) => type is "Frame" or "ScrollingFrame" or "ViewportFrame" or "VideoFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox" or "UICanvasGroup";

    private static bool IsGuiComponent(string type) => type is "UICorner" or "UIOutline" or "UIStroke" or "UIGradient" or "UIShadow" or "UIPadding" or "UIScale" or "UIListLayout" or "UIGridLayout" or "UITableLayout" or "UIPageLayout" or "UIAspectRatio" or "UIAspectRatioConstraint" or "UISizeConstraint" or "UITextSizeConstraint" or "UIFlexItem";

    private static bool IsGuiSubtreeCompatible(SceneNode node, bool isGuiServiceChild, string? destinationType = null)
    {
        if (destinationType == "ViewportFrame" && (SceneNodeTypes.IsBasePart(node) || node.Type is "Camera" or "Folder" or "Model" or "WorldModel"))
            return IsViewportFrameChildCompatible(node);
        if (node.Type == "ScreenGui") return isGuiServiceChild && node.Children.All(child => IsGuiSubtreeCompatible(child, false, "ScreenGui"));
        if (node.Type is "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript") return node.Children.All(child => IsGuiSubtreeCompatible(child, false, node.Type));
        if (node.Type is "Script" or "LocalScript") return node.Children.Count == 0;
        if (SceneNodeTypes.IsValueObject(node)) return node.Children.All(child => IsGuiSubtreeCompatible(child, false, node.Type));
        if (node.Type == "UITextSizeConstraint") return destinationType is "TextLabel" or "TextButton" or "TextBox" && node.Children.Count == 0;
        if (node.Type == "UIGradient") return (destinationType == "UIShadow" || destinationType is not null && IsGuiObject(destinationType)) && node.Children.Count == 0;
        if (node.Type == "UIShadow") return destinationType is not null && IsGuiObject(destinationType)
            && node.Children.All(child => IsGuiSubtreeCompatible(child, false, node.Type));
        if (IsGuiComponent(node.Type)) return destinationType is not null && IsGuiObject(destinationType) && node.Children.Count == 0;
        if (!IsGuiObject(node.Type)) return false;
        return node.Children.All(child => IsGuiSubtreeCompatible(child, false, node.Type));
    }

    private static bool IsViewportFrameChildCompatible(SceneNode node)
    {
        if (node.Type == "Camera") return node.Children.Count == 0;
        if (node.Type == "WorldModel") return node.Children.All(child => IsViewportWorldModelSubtreeCompatible(child, "Workspace"));
        if (node.Type is "Folder" or "Model") return IsViewportWorldModelSubtreeCompatible(node, "Workspace");
        if (SceneNodeTypes.IsBasePart(node)) return IsWorkspaceSubtreeCompatible(node, "Workspace");
        return false;
    }

    private static bool IsViewportWorldModelSubtreeCompatible(SceneNode node, string? parentType) =>
        !new[] { node }.Concat(Flatten(node.Children)).Any(child => child.Type is "Camera" or "WorldModel" or "PathfindingModifier" or "PathfindingLink")
        && IsWorkspaceSubtreeCompatible(node, parentType);

    private static bool IsWorkspaceSubtreeCompatible(SceneNode node, string? parentType)
    {
        if (node.Type == "Model" && (node.Children.Count(child => child.Type == "Humanoid") > 1
            || node.Children.Count(child => child.Type == "AnimationController") > 1
            || node.Children.Count(child => child.Type == "BodyColors") > 1)) return false;
        if (SceneNodeTypes.IsSurfaceAppearance(node)) return parentType == "MeshPart" && node.Children.Count == 0;
        if (SceneNodeTypes.IsSpecialMesh(node)) return parentType == "Part" && node.Children.Count == 0;
        if (node.Type == "MeshPart" && node.Children.Count(SceneNodeTypes.IsSurfaceAppearance) > 1) return false;
        if (node.Type is "BlurEffect" or "BloomEffect" or "ColorCorrectionEffect") return parentType is "Lighting" or "Camera" && node.Children.Count == 0;
        if (node.Type == "Terrain") return parentType == "Workspace" && node.Children.All(child => IsWorkspaceSubtreeCompatible(child, "Terrain"));
        if (parentType == "Terrain") return SceneNodeTypes.IsBasePart(node)
            && node.Children.All(child => IsWorkspaceSubtreeCompatible(child, node.Type));
        if (node.Type == "SoundGroup")
            return (parentType is "Workspace" or "SoundService" or "Folder" or "Configuration" or "Model" || SceneNodeTypes.IsBasePartType(parentType))
                && node.Children.All(child => IsWorkspaceSubtreeCompatible(child, node.Type));
        if (node.Type is "PitchShiftSoundEffect" or "DistortionSoundEffect" or "EqualizerSoundEffect" or "ChorusSoundEffect" or "CompressorSoundEffect" or "EchoSoundEffect" or "FlangeSoundEffect" or "TremoloSoundEffect" or "ReverbSoundEffect")
            return parentType is "Sound" or "SoundGroup" && node.Children.Count == 0;
        if (node.Type is "RemoteEvent" or "UnreliableRemoteEvent" or "RemoteFunction")
            return (parentType is "Workspace" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Model" or "Tool" || SceneNodeTypes.IsBasePartType(parentType))
                && node.Children.Count == 0;
        if (node.Type == "Tool") return (parentType is "Workspace" or "Folder" or "Model" or "StarterPack" or "Backpack")
            && node.Children.All(child => IsWorkspaceSubtreeCompatible(child, "Model"));
        if (node.Type == "Accessory") return (parentType is "Workspace" or "Folder" or "Model")
            && node.Children.All(child => IsWorkspaceSubtreeCompatible(child, "Model"));
        if (node.Type is "BillboardGui" or "SurfaceGui")
            return (parentType is "Workspace" or "Folder" or "Configuration" or "Model" || SceneNodeTypes.IsBasePartType(parentType))
                && node.Children.All(child => IsGuiSubtreeCompatible(child, false, node.Type));
        if (node.Type == "Humanoid") return parentType == "Model" && node.Children.Count(child => child.Type == "Animator") <= 1
            && node.Children.All(child => child.Type == "Animator" && IsWorkspaceSubtreeCompatible(child, "Humanoid"));
        if (node.Type == "AnimationController") return parentType == "Model" && node.Children.Count(child => child.Type == "Animator") <= 1
            && node.Children.All(child => child.Type == "Animator" && IsWorkspaceSubtreeCompatible(child, "AnimationController"));
        if (node.Type == "Animator") return parentType is "Humanoid" or "AnimationController" && node.Children.Count == 0;
        if (node.Type == "BodyColors") return parentType == "Model" && node.Children.Count == 0;
        if (node.Type == "PathfindingModifier") return SceneNodeTypes.IsBasePartType(parentType) && node.Children.Count == 0;
        if (node.Type == "PathfindingLink") return (parentType is "Workspace" or "Folder" or "Model" or "Configuration") && node.Children.Count == 0;
        if (node.Type == "Attachment")
            return SceneNodeTypes.IsBasePartType(parentType)
                && node.Children.All(child => IsWorkspaceSubtreeCompatible(child, "Attachment"));
        if (node.Type is "BodyForce" or "BodyThrust" or "BodyVelocity" or "BodyPosition" or "BodyGyro" or "BodyAngularVelocity" or "RocketPropulsion") return SceneNodeTypes.IsBasePartType(parentType) && node.Children.Count == 0;
        if (node.Type is "Beam" or "Trail" or "VectorForce" or "LineForce" or "LinearVelocity" or "AlignPosition" or "AlignOrientation" or "AngularVelocity" or "Torque" or "SpringConstraint" or "TorsionSpringConstraint" or "RopeConstraint" or "RodConstraint" or "BallSocketConstraint" or "HingeConstraint" or "PrismaticConstraint" or "CylindricalConstraint" or "PlaneConstraint" or "UniversalConstraint" or "RigidConstraint")
            return (parentType is "Workspace" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Model" or "Tool" or "Attachment" || SceneNodeTypes.IsBasePartType(parentType))
                && node.Children.Count == 0;
        if (SceneNodeTypes.IsValueObject(node))
            return (parentType is "Workspace" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Model" or "Tool" || SceneNodeTypes.IsBasePartType(parentType) || SceneNodeTypes.IsValueObjectType(parentType))
                && node.Children.All(child => IsWorkspaceSubtreeCompatible(child, node.Type));
        if (node.Type is "Decal" or "Texture")
            return SceneNodeTypes.IsBasePartType(parentType) && node.Children.Count == 0;
        if (SceneNodeTypes.IsParticleEffect(node))
            return (SceneNodeTypes.IsBasePartType(parentType) || parentType == "Attachment") && node.Children.Count == 0;
        if (node.Type is "ProximityPrompt" or "ClickDetector")
            return SceneNodeTypes.IsBasePartType(parentType) && node.Children.Count == 0;
        if (node.Type == "Sound")
            return (parentType is "Workspace" or "SoundService" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Model" or "Tool" || SceneNodeTypes.IsBasePartType(parentType) || SceneNodeTypes.IsValueObjectType(parentType))
                && node.Children.All(child => IsWorkspaceSubtreeCompatible(child, node.Type));
        if (node.Type is "SelectionBox" or "SelectionSphere")
            return (parentType is "Workspace" or "Folder" or "Configuration" or "Model" || SceneNodeTypes.IsBasePartType(parentType)) && node.Children.Count == 0;
        if (node.Type == "Highlight")
            return (parentType is "Workspace" or "Folder" or "Configuration" or "Model" || SceneNodeTypes.IsBasePartType(parentType)) && node.Children.Count == 0;
        if (node.Type == "ForceField")
            return (SceneNodeTypes.IsBasePartType(parentType) || parentType == "Model") && node.Children.Count == 0;
        if (node.Type == "Explosion")
            return (parentType is "Workspace" or "Folder" or "Configuration" or "Model" || SceneNodeTypes.IsBasePartType(parentType)) && node.Children.Count == 0;
        if (node.Type is "WeldConstraint" or "Weld" or "Motor6D" or "NoCollisionConstraint")
            return (parentType is "Workspace" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Model" or "Tool" || SceneNodeTypes.IsBasePartType(parentType) || SceneNodeTypes.IsValueObjectType(parentType)) && node.Children.Count == 0;
        if (SceneNodeTypes.IsLight(node))
            return SceneNodeTypes.IsBasePartType(parentType)
                && node.Children.Count == 0;
        if (!SceneNodeTypes.IsBasePart(node) && node.Type is not ("ImageLabel" or "Folder" or "Configuration" or "BindableEvent" or "BindableFunction" or "ModuleScript" or "Model" or "Tool" or "Script" or "LocalScript")) return false;
        if (node.Type is "Script" or "LocalScript" or "ImageLabel" && node.Children.Count > 0) return false;
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
        project.Workspace.Children.Insert(0, new SceneNode { Id = "camera-main", Type = "Camera", Name = "Camera", Position = [8, 7, 11], Rotation = [-24, 35, 0] });
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
