using System.IO;
using System.Windows.Threading;
using BullerStudio.Models;
using NAudio.Wave;
using SoundTouch.Net.NAudioSupport;

namespace BullerStudio.Services;

public sealed class SoundPlaybackService : IDisposable
{
    private readonly GameProject project;
    private readonly Action<SceneNode> stateChanged;
    private readonly Dispatcher dispatcher;
    private readonly Dictionary<string, Playback> active = new(StringComparer.Ordinal);
    private bool disposed;

    public SoundPlaybackService(GameProject project, Action<SceneNode> stateChanged)
    {
        this.project = project;
        this.stateChanged = stateChanged;
        dispatcher = Dispatcher.CurrentDispatcher;
    }

    public int ActiveCount => active.Count;

    public void Update(float[] listenerPosition)
    {
        if (disposed) return;
        var allNodes = GameProjectStore.Flatten(project).ToArray();
        var nodesById = allNodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var nodes = GameProjectStore.Flatten(project.Workspace.Children).Concat(GameProjectStore.Flatten(project.SoundService.Children)).ToArray();
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
            var soundGroup = nodesById.GetValueOrDefault(sound.SoundGroupId);
            if (soundGroup?.Type != "SoundGroup") soundGroup = null;
            var soundGroupVolume = soundGroup?.SoundGroupVolume ?? 1;
            var pitchOctaves = GetPitchOctaves(sound, soundGroup);
            var volume = CalculateVolume(sound, parentPart, listenerPosition, soundGroupVolume, project.SoundService.SoundServiceVolume);
            if (active.TryGetValue(sound.Id, out var playback) && string.Equals(playback.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                playback.Output.Volume = (float)volume;
                playback.Provider.Rate = GetPlaybackSpeed(sound.PlaybackSpeed);
                playback.Provider.PitchOctaves = pitchOctaves;
                continue;
            }

            Stop(sound.Id);
            Start(sound, path, volume, pitchOctaves);
        }
    }

    public static double CalculateVolume(SceneNode sound, SceneNode? parentPart, float[] listenerPosition, float soundGroupVolume = 1, float masterVolume = 1)
    {
        var soundVolume = float.IsFinite(sound.Volume) ? Math.Clamp(sound.Volume, 0, 1) : 0;
        var groupVolume = float.IsFinite(soundGroupVolume) ? Math.Clamp(soundGroupVolume, 0, 1) : 1;
        var master = float.IsFinite(masterVolume) ? Math.Clamp(masterVolume, 0, 1) : 1;
        var volume = soundVolume * groupVolume * master;
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

    private static float GetPitchOctaves(SceneNode sound, SceneNode? soundGroup)
    {
        var effects = sound.Children.Where(child => child.Type == "PitchShiftSoundEffect")
            .Concat(soundGroup?.Children.Where(child => child.Type == "PitchShiftSoundEffect") ?? []);
        var octaves = effects.Where(effect => effect.Enabled)
            .Sum(effect => Math.Log2(Math.Clamp(float.IsFinite(effect.PitchShiftOctave) ? effect.PitchShiftOctave : 1, .5f, 2)));
        return (float)Math.Clamp(octaves, -1, 1);
    }

    private void Start(SceneNode sound, string path, double volume, float pitchOctaves)
    {
        MediaFoundationReader? reader = null;
        WaveOutEvent? output = null;
        try
        {
            reader = new MediaFoundationReader(path);
            var provider = new SoundTouchWaveProvider(reader) { Rate = GetPlaybackSpeed(sound.PlaybackSpeed), PitchOctaves = pitchOctaves };
            output = new WaveOutEvent { Volume = (float)volume };
            output.Init(provider);
            var playback = new Playback(output, reader, provider, path);
            active[sound.Id] = playback;
            output.PlaybackStopped += (_, eventArgs) => dispatcher.BeginInvoke(new Action(() => HandlePlaybackStopped(sound, playback, eventArgs.Exception)));
            output.Play();
        }
        catch (Exception exception) when (IsAudioException(exception))
        {
            if (active.TryGetValue(sound.Id, out var current) && ReferenceEquals(current.Output, output))
                active.Remove(sound.Id);
            output?.Dispose();
            reader?.Dispose();
            sound.Playing = false;
            stateChanged(sound);
        }
    }

    private void HandlePlaybackStopped(SceneNode sound, Playback playback, Exception? error)
    {
        if (disposed || !active.TryGetValue(sound.Id, out var current) || !ReferenceEquals(current, playback)) return;
        if (error is null && sound.Playing && sound.Looped)
        {
            try
            {
                playback.Reader.Position = 0;
                playback.Provider.Clear();
                playback.Output.Init(playback.Provider);
                playback.Output.Play();
                return;
            }
            catch (Exception exception) when (IsAudioException(exception)) { }
        }

        sound.Playing = false;
        Stop(sound.Id);
        stateChanged(sound);
    }

    private static bool IsAudioException(Exception exception) => exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException or FormatException or InvalidDataException or System.Runtime.InteropServices.COMException;

    private static float GetPlaybackSpeed(float playbackSpeed) => float.IsFinite(playbackSpeed) ? Math.Clamp(playbackSpeed, 0, 20) : 1;

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
        playback.Output.Stop();
        playback.Output.Dispose();
        playback.Reader.Dispose();
    }

    private sealed record Playback(WaveOutEvent Output, MediaFoundationReader Reader, SoundTouchWaveProvider Provider, string Path);
}
