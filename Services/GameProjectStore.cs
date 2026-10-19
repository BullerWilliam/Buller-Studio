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
        project.Gui ??= new SceneNode { Id = "gui", Type = "Gui", Name = "Gui" };
        project.Gui.Id = "gui";
        project.Gui.Type = "Gui";
        project.Gui.Name = "Gui";
        project.Gui.Children ??= [];
        if (!project.Gui.Children.Any(node => node.Type == "ScreenGui"))
            project.Gui.Children.Add(new SceneNode { Id = Guid.NewGuid().ToString("N"), Type = "ScreenGui", Name = "ScreenGui" });
        foreach (var node in Flatten(project.Workspace.Children).Concat(Flatten(project.Players.Children)).Concat(Flatten(project.Gui.Children)))
        {
            node.Children ??= [];
            node.Position = EnsureVector(node.Position, [0, 0, 0]);
            node.Rotation = EnsureVector(node.Rotation, [0, 0, 0]);
            node.Size = EnsureVector(node.Size, [2, 2, 2]);
            node.Velocity = EnsureVector(node.Velocity, [0, 0, 0]);
            node.PositionScale = EnsurePair(node.PositionScale);
            node.SizeScale = EnsurePair(node.SizeScale);
            if (node.Shape is not ("Block" or "Ball" or "Cylinder")) node.Shape = "Block";
            if (!SceneNodeTypes.IsFace(node.Face)) node.Face = "Front";
            node.StudsPerTileU = float.IsFinite(node.StudsPerTileU) ? Math.Clamp(node.StudsPerTileU, .05f, 1000) : 2;
            node.StudsPerTileV = float.IsFinite(node.StudsPerTileV) ? Math.Clamp(node.StudsPerTileV, .05f, 1000) : 2;
            node.OffsetStudsU = float.IsFinite(node.OffsetStudsU) ? Math.Clamp(node.OffsetStudsU, -10000, 10000) : 0;
            node.OffsetStudsV = float.IsFinite(node.OffsetStudsV) ? Math.Clamp(node.OffsetStudsV, -10000, 10000) : 0;
            if (node.Material is not ("Plastic" or "Metal" or "Neon" or "Glass" or "Wood" or "Concrete")) node.Material = "Plastic";
            node.Transparency = float.IsFinite(node.Transparency) ? Math.Clamp(node.Transparency, 0, 1) : 0;
            node.FillColor ??= "#00BFFF";
            node.OutlineColor ??= "#FFFFFF";
            node.FillTransparency = float.IsFinite(node.FillTransparency) ? Math.Clamp(node.FillTransparency, 0, 1) : .5f;
            node.OutlineTransparency = float.IsFinite(node.OutlineTransparency) ? Math.Clamp(node.OutlineTransparency, 0, 1) : 0;
            node.Brightness = float.IsFinite(node.Brightness) ? Math.Clamp(node.Brightness, 0, 8) : 1;
            node.Range = float.IsFinite(node.Range) ? Math.Clamp(node.Range, 1, 1000) : 16;
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
        }
        var scriptNames = Flatten(project.Workspace.Children).Concat(Flatten(project.Gui.Children))
            .Where(node => node.Type == "Script")
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

    public static IReadOnlyList<SceneNode> Flatten(GameProject project) => Flatten([project.Workspace, project.Players, project.Gui]);

    public static bool CanParentNode(GameProject project, SceneNode source, SceneNode destinationParent)
    {
        if (source.Id == project.Workspace.Id || source.Id == project.Players.Id || source.Id == project.Gui.Id || source.Id == destinationParent.Id) return false;
        if (!IsNodeInTree(project.Workspace, destinationParent.Id) && !IsNodeInTree(project.Gui, destinationParent.Id)) return false;
        if (Flatten(source.Children).Any(node => node.Id == destinationParent.Id)) return false;
        if (source.Type == "Humanoid")
            return destinationParent.Type == "Model" && source.Children.Count == 0
                && !destinationParent.Children.Any(child => child.Type == "Humanoid" && child.Id != source.Id);
        if (source.Type == "Camera")
            return destinationParent.Id == project.Workspace.Id && source.Children.All(child => IsWorkspaceSubtreeCompatible(child, "Camera"));

        var destinationIsGui = IsNodeInTree(project.Gui, destinationParent.Id);
        if (destinationIsGui)
        {
            if (destinationParent.Id == project.Gui.Id)
                return (source.Type is "ScreenGui" or "Folder" or "Script") && IsGuiSubtreeCompatible(source, true);
            if (!IsGuiContainer(destinationParent.Type)) return false;
            if (source.Type == "ScreenGui") return false;
            return IsGuiSubtreeCompatible(source, false, destinationParent.Type);
        }

        if (destinationParent.Type is not ("Workspace" or "Folder" or "Model") && !SceneNodeTypes.IsBasePart(destinationParent)) return false;
        return IsWorkspaceSubtreeCompatible(source, destinationParent.Type);
    }

    public static bool CanParentDetachedNode(SceneNode source, SceneNode destinationParent)
    {
        if (source.Id == destinationParent.Id || Flatten(source.Children).Any(child => child.Id == destinationParent.Id)) return false;
        if (source.Type is "Workspace" or "Players" or "Gui" or "Camera") return false;
        if (source.Type == "Humanoid")
            return destinationParent.Type == "Model" && source.Children.Count == 0
                && !destinationParent.Children.Any(child => child.Type == "Humanoid" && child.Id != source.Id);
        if (destinationParent.Type == "Model" || SceneNodeTypes.IsBasePart(destinationParent))
            return IsWorkspaceSubtreeCompatible(source, destinationParent.Type);
        if (destinationParent.Type == "Folder")
            return IsWorkspaceSubtreeCompatible(source, "Folder") || IsGuiSubtreeCompatible(source, false, "Folder");
        return IsGuiContainer(destinationParent.Type) && IsGuiSubtreeCompatible(source, false, destinationParent.Type);
    }

    public static bool MoveNodeToParent(GameProject project, SceneNode source, SceneNode destinationParent)
    {
        if (!CanParentNode(project, source, destinationParent)) return false;
        var sourceCollection = FindParentCollection(project.Workspace.Children, source)
            ?? FindParentCollection(project.Gui.Children, source);
        if (sourceCollection is null || ReferenceEquals(sourceCollection, destinationParent.Children)) return false;
        sourceCollection.Remove(source);
        destinationParent.Children.Add(source);
        return true;
    }

    public static bool MoveNodeBeside(GameProject project, SceneNode source, SceneNode target, bool after)
    {
        if (source.Type == "Camera" || target.Type == "Camera" || ReferenceEquals(source, target) || source.Id == target.Id) return false;
        var destinationParent = FindParentNode(project.Workspace, target) ?? FindParentNode(project.Gui, target);
        if (destinationParent is null || !CanParentNode(project, source, destinationParent)) return false;
        var sourceCollection = FindParentCollection(project.Workspace.Children, source)
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

    private static bool IsGuiContainer(string type) => type is "Gui" or "ScreenGui" or "Folder" or "Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox";

    private static bool IsGuiObject(string type) => type is "Frame" or "ScrollingFrame" or "ImageLabel" or "ImageButton" or "TextLabel" or "TextButton" or "TextBox";

    private static bool IsGuiComponent(string type) => type is "UICorner" or "UIOutline" or "UIStroke" or "UIListLayout" or "UITableLayout" or "UIAspectRatio";

    private static bool IsGuiSubtreeCompatible(SceneNode node, bool isGuiServiceChild, string? destinationType = null)
    {
        if (node.Type == "ScreenGui") return isGuiServiceChild && node.Children.All(child => IsGuiSubtreeCompatible(child, false, "ScreenGui"));
        if (node.Type == "Folder") return node.Children.All(child => IsGuiSubtreeCompatible(child, false, "Folder"));
        if (node.Type == "Script") return node.Children.Count == 0;
        if (IsGuiComponent(node.Type)) return destinationType is not null && IsGuiObject(destinationType) && node.Children.Count == 0;
        if (!IsGuiObject(node.Type)) return false;
        return node.Children.All(child => IsGuiSubtreeCompatible(child, false, node.Type));
    }

    private static bool IsWorkspaceSubtreeCompatible(SceneNode node, string? parentType)
    {
        if (node.Type == "Humanoid") return parentType == "Model" && node.Children.Count == 0;
        if (node.Type is "Decal" or "Texture")
            return SceneNodeTypes.IsBasePartType(parentType) && node.Children.Count == 0;
        if (node.Type is "ProximityPrompt" or "ClickDetector")
            return SceneNodeTypes.IsBasePartType(parentType) && node.Children.Count == 0;
        if (node.Type == "Sound")
            return (parentType is "Workspace" or "Folder" or "Model" || SceneNodeTypes.IsBasePartType(parentType)) && node.Children.Count == 0;
        if (node.Type == "Highlight")
            return (SceneNodeTypes.IsBasePartType(parentType) || parentType == "Model") && node.Children.Count == 0;
        if (node.Type == "WeldConstraint")
            return (parentType is "Workspace" or "Folder" or "Model" || SceneNodeTypes.IsBasePartType(parentType)) && node.Children.Count == 0;
        if (SceneNodeTypes.IsLight(node))
            return SceneNodeTypes.IsBasePartType(parentType)
                && node.Children.Count == 0;
        if (!SceneNodeTypes.IsBasePart(node) && node.Type is not ("ImageLabel" or "Folder" or "Model" or "Script")) return false;
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
