using System.IO;
using System.Reflection;
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

    public static async Task<IReadOnlyList<ProjectAsset>> ImportAssetsAsync(GameProject project, IEnumerable<string> sourceFiles)
    {
        var assetsFolder = Path.Combine(project.ProjectPath, "assets");
        Directory.CreateDirectory(assetsFolder);
        var imported = new List<ProjectAsset>();
        foreach (var source in sourceFiles)
        {
            var name = Path.GetFileName(source);
            var destination = Path.Combine(assetsFolder, name);
            var suffix = 2;
            while (File.Exists(destination))
            {
                var extension = Path.GetExtension(name);
                destination = Path.Combine(assetsFolder, $"{Path.GetFileNameWithoutExtension(name)}-{suffix++}{extension}");
            }
            await using (var input = File.OpenRead(source))
            await using (var output = File.Create(destination)) await input.CopyToAsync(output);
            var info = new FileInfo(destination);
            var asset = new ProjectAsset { Name = info.Name, RelativePath = Path.Combine("assets", info.Name), Type = info.Extension.TrimStart('.').ToLowerInvariant(), Size = info.Length };
            project.Assets.Add(asset);
            imported.Add(asset);
        }
        await SaveAsync(project);
        return imported;
    }

    public static void EnsureProject(GameProject project)
    {
        project.Assets ??= [];
        project.Scripts ??= [];
        project.Settings ??= new ProjectSettings();
        project.Workspace ??= new SceneNode { Id = "workspace", Type = "Workspace", Name = "Workspace" };
        project.Workspace.Children ??= [];
        foreach (var node in Flatten(project.Workspace.Children)) node.Children ??= [];
        var cameras = Flatten(project.Workspace.Children).Where(node => node.Type == "Camera").ToList();
        var camera = cameras.FirstOrDefault() ?? new SceneNode { Id = "camera-main", Type = "Camera", Name = "Camera", Position = [8, 7, 11], Rotation = [-24, 35, 0] };
        foreach (var duplicate in cameras.Skip(1)) RemoveNode(project.Workspace.Children, duplicate.Id);
        RemoveNode(project.Workspace.Children, camera.Id);
        project.Workspace.Children.Insert(0, camera);
        project.Workspace.Position = [0, 0, 0];
    }

    public static IReadOnlyList<SceneNode> Flatten(IEnumerable<SceneNode> nodes)
    {
        var result = new List<SceneNode>();
        foreach (var node in nodes) { result.Add(node); result.AddRange(Flatten(node.Children ?? [])); }
        return result;
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
            var cacheFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Buller Studio", "Build Cache", SanitizeName(project.Name));
            var assetRoot = Path.Combine(cacheFolder, "assets");
            Directory.CreateDirectory(assetRoot);
            foreach (var resourceName in assembly.GetManifestResourceNames().Where(name => name.StartsWith("BullerStudioAssets/", StringComparison.Ordinal)))
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
}

public sealed record ProjectSummary(string Name, string Path, DateTime LastModified);
