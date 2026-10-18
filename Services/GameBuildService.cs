using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using BullerStudio.Models;

namespace BullerStudio.Services;

public sealed class GameBuildService
{
    public async Task<string> BuildAsync(GameProject project, bool included, Action<string>? report)
    {
        await GameProjectStore.SaveAsync(project);
        var downloads = GetDownloadsFolder();
        Directory.CreateDirectory(downloads);
        var buildFolderName = $"{GameProjectStore.SanitizeName(project.Name)}-Build-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        var output = Path.Combine(downloads, buildFolderName);
        Directory.CreateDirectory(output);
        var assemblyName = GameProjectStore.SanitizeName(project.Name).Replace(' ', '-');
        var projectFile = FindProjectFile();
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(projectFile)!, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[]
        {
            "publish", projectFile, "--configuration", "Release", "--runtime", "win-x64", "--self-contained", "true",
            "--output", output,
            $"-p:GameProjectPath={Path.Combine(project.ProjectPath, GameProjectStore.GameFileName)}",
            $"-p:GameAssetsPath={Path.Combine(project.ProjectPath, "assets")}",
            $"-p:GameBuildMode={(included ? "Included" : "Excluded")}",
            $"-p:AssemblyName={assemblyName}", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true"
        }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var outputLog = new List<string>();
        process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is { Length: > 0 } line) { lock (outputLog) outputLog.Add(line); report?.Invoke(line); } };
        process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is { Length: > 0 } line) { lock (outputLog) outputLog.Add(line); report?.Invoke(line); } };
        report?.Invoke($"Publishing {project.Name} ({(included ? "assets included" : "external assets")})…");
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            string details; lock (outputLog) details = string.Join(Environment.NewLine, outputLog.TakeLast(30));
            throw new InvalidOperationException($"dotnet publish failed with exit code {process.ExitCode}.{Environment.NewLine}{details}");
        }
        var executable = Path.Combine(output, $"{assemblyName}.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("The game executable was not produced.", executable);
        if (!included)
        {
            await File.WriteAllTextAsync(Path.Combine(output, GameProjectStore.GameFileName), await File.ReadAllTextAsync(Path.Combine(project.ProjectPath, GameProjectStore.GameFileName)));
            var sourceAssets = Path.Combine(project.ProjectPath, "assets");
            if (Directory.Exists(sourceAssets)) CopyDirectory(sourceAssets, Path.Combine(output, "assets"));
        }
        report?.Invoke($"Build ready: {executable}");
        return executable;
    }

    private static void CopyDirectory(string source, string destination)
    {
        var sourceInfo = new DirectoryInfo(source);
        if (!sourceInfo.Exists) return;
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Build assets cannot include symbolic links or junctions.");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Build assets cannot include symbolic links or junctions.");
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        }
        foreach (var folder in Directory.EnumerateDirectories(source)) CopyDirectory(folder, Path.Combine(destination, Path.GetFileName(folder)));
    }

    private static string FindProjectFile()
    {
        foreach (var origin in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(origin);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "BullerStudio.csproj");
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
        }
        throw new FileNotFoundException("Could not locate BullerStudio.csproj. Install the build tools or run the editor from its source distribution.");
    }

    private static string GetDownloadsFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string downloads && !string.IsNullOrWhiteSpace(downloads))
            {
                var expanded = Environment.ExpandEnvironmentVariables(downloads);
                if (Path.IsPathRooted(expanded)) return expanded;
            }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
