using System.IO;
using System.Windows.Media;
using BullerStudio.Models;

namespace BullerStudio.Services;

public sealed class SoundPlaybackService : IDisposable
{
    private readonly GameProject project;
    private readonly Action<SceneNode> stateChanged;
    private readonly Dictionary<string, Playback> active = new(StringComparer.Ordinal);
    private bool disposed;

    public SoundPlaybackService(GameProject project, Action<SceneNode> stateChanged)
    {
        this.project = project;
        this.stateChanged = stateChanged;
    }

    public int ActiveCount => active.Count;

    public void Update(float[] listenerPosition)
    {
        if (disposed) return;
        var nodes = GameProjectStore.Flatten(project.Workspace.Children).ToArray();
        var sounds = nodes.Where(node => node.Type == "Sound").ToArray();
        var soundIds = sounds.Select(sound => sound.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in active.Keys.Where(id => !soundIds.Contains(id)).ToArray()) Stop(id);

        foreach (var sound in sounds)
        {
            var path = sound.Playing ? ResolveAssetPath(sound.Asset) : null;
            if (path is null)
            {
                Stop(sound.Id);
                continue;
            }

            var parentPart = nodes.FirstOrDefault(parent => SceneNodeTypes.IsBasePart(parent) && parent.Children.Contains(sound));
            var volume = CalculateVolume(sound, parentPart, listenerPosition);
            if (active.TryGetValue(sound.Id, out var playback) && string.Equals(playback.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                playback.Player.Volume = volume;
                continue;
            }

            Stop(sound.Id);
            try
            {
                var player = new MediaPlayer { Volume = volume };
                player.MediaEnded += (_, _) =>
                {
                    if (disposed || !active.TryGetValue(sound.Id, out var current) || !ReferenceEquals(current.Player, player)) return;
                    if (sound.Playing && sound.Looped)
                    {
                        player.Position = TimeSpan.Zero;
                        player.Play();
                    }
                    else
                    {
                        sound.Playing = false;
                        stateChanged(sound);
                    }
                };
                player.MediaFailed += (_, _) =>
                {
                    if (disposed || !active.TryGetValue(sound.Id, out var current) || !ReferenceEquals(current.Player, player)) return;
                    sound.Playing = false;
                    stateChanged(sound);
                };
                active[sound.Id] = new Playback(player, path);
                player.Open(new Uri(path, UriKind.Absolute));
                player.Play();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException or FormatException or System.Runtime.InteropServices.COMException)
            {
                Stop(sound.Id);
                sound.Playing = false;
                stateChanged(sound);
            }
        }
    }

    public static double CalculateVolume(SceneNode sound, SceneNode? parentPart, float[] listenerPosition)
    {
        var volume = float.IsFinite(sound.Volume) ? Math.Clamp(sound.Volume, 0, 1) : 0;
        if (parentPart is null || listenerPosition.Length < 3) return volume;
        var dx = parentPart.Position[0] - listenerPosition[0];
        var dy = parentPart.Position[1] - listenerPosition[1];
        var dz = parentPart.Position[2] - listenerPosition[2];
        var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!float.IsFinite(distance)) return 0;
        var minimum = float.IsFinite(sound.RollOffMinDistance) ? Math.Clamp(sound.RollOffMinDistance, 0, 1000) : 10;
        var maximum = float.IsFinite(sound.RollOffMaxDistance) ? Math.Clamp(sound.RollOffMaxDistance, minimum + .01f, 10000) : 100;
        if (distance <= minimum) return volume;
        if (distance >= maximum) return 0;
        return volume * (1 - (distance - minimum) / (maximum - minimum));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var id in active.Keys.ToArray()) Stop(id);
    }

    private string? ResolveAssetPath(string asset)
    {
        if (string.IsNullOrWhiteSpace(asset) || asset.Contains("(m)", StringComparison.Ordinal)
            || project.ProjectPath.Contains("(m)", StringComparison.Ordinal)) return null;
        var extension = Path.GetExtension(asset);
        if (!extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".wma", StringComparison.OrdinalIgnoreCase)) return null;
        try { return GameProjectStore.GetSafeAssetFilePathInAssets(project, asset); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private void Stop(string id)
    {
        if (!active.Remove(id, out var playback)) return;
        playback.Player.Stop();
        playback.Player.Close();
    }

    private sealed record Playback(MediaPlayer Player, string Path);
}
