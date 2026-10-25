using System.IO;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using System.Windows.Threading;
using BullerStudio.Models;
using NAudio.Wave;
using SoundTouch.Net.NAudioSupport;

namespace BullerStudio.Services;

public sealed class SoundPlaybackService : IDisposable
{
    private readonly GameProject project;
    private readonly Action<SceneNode> stateChanged;
    private readonly Action<SceneNode, string>? signalFired;
    private readonly Dispatcher dispatcher;
    private readonly Dictionary<string, Playback> active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, float> audioMeters = new(StringComparer.Ordinal);
    private bool disposed;
    private bool globallyPaused;

    private readonly record struct EchoParameters(float Delay, float Feedback, float DryLevel, float WetLevel, bool ContinueTail);
    private readonly record struct DistortionParameters(float Level);
    private readonly record struct EqualizerParameters(float LowGain, float MidGain, float HighGain);
    private readonly record struct CompressorParameters(float Attack, float GainMakeup, float Ratio, float Release, float Threshold, Func<float>? SideChainLevel);
    private readonly record struct FlangeParameters(float Depth, float Mix, float Rate);
    private readonly record struct TremoloParameters(float Depth, float Duty, float Frequency);
    private readonly record struct ReverbParameters(float DecayTime, float Density, float Diffusion, float DryLevel, float WetLevel);

    public SoundPlaybackService(GameProject project, Action<SceneNode> stateChanged, Action<SceneNode, string>? signalFired = null)
    {
        this.project = project;
        this.stateChanged = stateChanged;
        this.signalFired = signalFired;
        dispatcher = Dispatcher.CurrentDispatcher;
    }

    public int ActiveCount => active.Count;

    public void Pause()
    {
        if (disposed) return;
        globallyPaused = true;
        foreach (var playback in active.Values) playback.Output.Pause();
    }

    public void Resume()
    {
        if (disposed) return;
        globallyPaused = false;
        foreach (var playback in active.Values)
            if (!playback.Node.SoundPaused) playback.Output.Play();
    }

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

            var sourcePosition = GetSpatialSoundPosition(sound, nodes);
            var soundGroup = nodesById.GetValueOrDefault(sound.SoundGroupId);
            if (soundGroup?.Type != "SoundGroup") soundGroup = null;
            var soundGroupVolume = soundGroup?.SoundGroupVolume ?? 1;
            var pitchOctaves = GetPitchOctaves(sound, soundGroup);
            var distortionParameters = GetDistortionParameters(sound, soundGroup);
            var equalizerParameters = GetEqualizerParameters(sound, soundGroup);
            var chorusParameters = GetChorusParameters(sound, soundGroup);
            var echoParameters = GetEchoParameters(sound, soundGroup);
            var flangeParameters = GetFlangeParameters(sound, soundGroup);
            var tremoloParameters = GetTremoloParameters(sound, soundGroup);
            var reverbParameters = GetReverbParameters(sound, soundGroup);
            var volume = CalculateVolumeAtPosition(sound, sourcePosition, listenerPosition, soundGroupVolume, project.SoundService.SoundServiceVolume);
            var compressorParameters = GetCompressorParameters(sound, nodes, nodesById);
            if (active.TryGetValue(sound.Id, out var playback) && string.Equals(playback.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                if (sound.SoundSeekTarget is { } seekTarget)
                {
                    var wasPlaying = playback.Output.PlaybackState == PlaybackState.Playing;
                    if (wasPlaying) playback.Output.Pause();
                    Seek(playback.Reader, sound, seekTarget);
                    ResetProcessors(playback);
                    if (wasPlaying && !globallyPaused && !sound.SoundPaused) playback.Output.Play();
                }
                var currentTime = (float)playback.Reader.CurrentTime.TotalSeconds;
                if (float.IsFinite(currentTime) && Math.Abs(currentTime - sound.SoundTimePosition) > .01f)
                {
                    sound.SoundTimePosition = currentTime;
                    stateChanged(sound);
                }
                playback.Output.Volume = (float)volume;
                playback.Meter.SetGain((float)volume);
                playback.Provider.Rate = GetPlaybackSpeed(sound.PlaybackSpeed);
                playback.Provider.PitchOctaves = pitchOctaves;
                playback.Distortion.SetParameters(distortionParameters);
                playback.Equalizer.SetParameters(equalizerParameters);
                playback.Chorus.SetParameters(chorusParameters);
                playback.Compressor.SetParameters(compressorParameters);
                playback.Echo.SetParameters(echoParameters);
                playback.Flange.SetParameters(flangeParameters);
                playback.Tremolo.SetParameters(tremoloParameters);
                playback.Reverb.SetParameters(reverbParameters);
                if (globallyPaused || sound.SoundPaused)
                {
                    if (playback.Output.PlaybackState == PlaybackState.Playing) playback.Output.Pause();
                }
                else if (playback.Output.PlaybackState != PlaybackState.Playing) playback.Output.Play();
                continue;
            }

            Stop(sound.Id);
            Start(sound, path, volume, pitchOctaves, distortionParameters, equalizerParameters, chorusParameters, compressorParameters, echoParameters, flangeParameters, tremoloParameters, reverbParameters);
        }
    }

    public static double CalculateVolume(SceneNode sound, SceneNode? parentPart, float[] listenerPosition, float soundGroupVolume = 1, float masterVolume = 1)
    {
        Vector3? sourcePosition = parentPart is not null && SceneNodeTypes.IsBasePart(parentPart) && parentPart.Position.Length >= 3
            ? new Vector3(parentPart.Position[0], parentPart.Position[1], parentPart.Position[2])
            : null;
        return CalculateVolumeAtPosition(sound, sourcePosition, listenerPosition, soundGroupVolume, masterVolume);
    }

    private static double CalculateVolumeAtPosition(SceneNode sound, Vector3? sourcePosition, float[] listenerPosition, float soundGroupVolume, float masterVolume)
    {
        var soundVolume = float.IsFinite(sound.Volume) ? Math.Clamp(sound.Volume, 0, 1) : 0;
        var groupVolume = float.IsFinite(soundGroupVolume) ? Math.Clamp(soundGroupVolume, 0, 1) : 1;
        var master = float.IsFinite(masterVolume) ? Math.Clamp(masterVolume, 0, 1) : 1;
        var volume = soundVolume * groupVolume * master;
        if (sourcePosition is null || listenerPosition is null || listenerPosition.Length < 3) return volume;
        var dx = sourcePosition.Value.X - listenerPosition[0];
        var dy = sourcePosition.Value.Y - listenerPosition[1];
        var dz = sourcePosition.Value.Z - listenerPosition[2];
        var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!float.IsFinite(distance)) return 0;
        var minimum = float.IsFinite(sound.RollOffMinDistance) ? Math.Clamp(sound.RollOffMinDistance, 0, 1000) : 10;
        var maximum = float.IsFinite(sound.RollOffMaxDistance) ? Math.Clamp(sound.RollOffMaxDistance, minimum + .01f, 10000) : 100;
        if (distance <= minimum) return volume;
        if (distance >= maximum) return 0;
        var linear = Math.Clamp(1 - (distance - minimum) / (maximum - minimum), 0, 1);
        var inverse = minimum <= 0 ? 0 : Math.Clamp(minimum / distance, 0, 1);
        var attenuation = sound.RollOffMode switch
        {
            "Linear" => linear,
            "LinearSquare" => linear * linear,
            "Inverse" => inverse,
            "InverseTapered" => Math.Min(inverse, linear * linear),
            _ => Math.Min(inverse, linear * linear)
        };
        return volume * attenuation;
    }

    private static Vector3? GetSpatialSoundPosition(SceneNode sound, IReadOnlyList<SceneNode> nodes)
    {
        var parent = nodes.FirstOrDefault(candidate => candidate.Children.Contains(sound));
        if (parent is null) return null;
        if (SceneNodeTypes.IsBasePart(parent)) return ReadPosition(parent.Position);
        if (parent.Type != "Attachment") return null;
        return GetAttachmentWorldTransform(parent, nodes, [])?.Position;
    }

    private static (Vector3 Position, Matrix4x4 Rotation)? GetAttachmentWorldTransform(SceneNode attachment, IReadOnlyList<SceneNode> nodes, HashSet<string> visiting)
    {
        if (!visiting.Add(attachment.Id)) return null;
        var parent = nodes.FirstOrDefault(candidate => candidate.Children.Contains(attachment));
        if (parent is null) return null;
        Vector3 parentPosition;
        Matrix4x4 parentRotation;
        if (SceneNodeTypes.IsBasePart(parent))
        {
            if (ReadPosition(parent.Position) is not { } partPosition) return null;
            parentPosition = partPosition;
            parentRotation = RotationMath.FromDegrees(parent.Rotation);
        }
        else if (parent.Type == "Attachment")
        {
            if (GetAttachmentWorldTransform(parent, nodes, visiting) is not { } parentTransform) return null;
            parentPosition = parentTransform.Position;
            parentRotation = parentTransform.Rotation;
        }
        else return null;
        if (ReadPosition(attachment.Position) is not { } localPosition) return null;
        var worldPosition = parentPosition + Vector3.Transform(localPosition, parentRotation);
        var worldRotation = RotationMath.FromDegrees(attachment.Rotation) * parentRotation;
        return float.IsFinite(worldPosition.X) && float.IsFinite(worldPosition.Y) && float.IsFinite(worldPosition.Z)
            ? (worldPosition, worldRotation) : null;
    }

    private static Vector3? ReadPosition(float[] position) =>
        position.Length >= 3 && float.IsFinite(position[0]) && float.IsFinite(position[1]) && float.IsFinite(position[2])
            ? new Vector3(position[0], position[1], position[2])
            : null;

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

    private static EchoParameters GetEchoParameters(SceneNode sound, SceneNode? soundGroup)
    {
        var effect = sound.Children.FirstOrDefault(child => child.Type == "EchoSoundEffect" && child.Enabled);
        var continueTail = false;
        if (effect is null)
        {
            effect = soundGroup?.Children.FirstOrDefault(child => child.Type == "EchoSoundEffect" && child.Enabled);
            continueTail = effect is not null;
        }
        return effect is null
            ? new EchoParameters(0, 0, 0, -80, false)
            : new EchoParameters(
                Math.Clamp(float.IsFinite(effect.EchoDelay) ? effect.EchoDelay : 1, .01f, 5),
                Math.Clamp(float.IsFinite(effect.EchoFeedback) ? effect.EchoFeedback : .5f, 0, 1),
                Math.Clamp(float.IsFinite(effect.EchoDryLevel) ? effect.EchoDryLevel : 0, -80, 10),
                Math.Clamp(float.IsFinite(effect.EchoWetLevel) ? effect.EchoWetLevel : 0, -80, 10),
                continueTail);
    }

    private static DistortionParameters GetDistortionParameters(SceneNode sound, SceneNode? soundGroup)
    {
        var effect = sound.Children.FirstOrDefault(child => child.Type == "DistortionSoundEffect" && child.Enabled)
            ?? soundGroup?.Children.FirstOrDefault(child => child.Type == "DistortionSoundEffect" && child.Enabled);
        return new DistortionParameters(effect is null ? 0 : Math.Clamp(float.IsFinite(effect.DistortionLevel) ? effect.DistortionLevel : .5f, 0, 1));
    }

    private static EqualizerParameters GetEqualizerParameters(SceneNode sound, SceneNode? soundGroup)
    {
        var effect = sound.Children.FirstOrDefault(child => child.Type == "EqualizerSoundEffect" && child.Enabled)
            ?? soundGroup?.Children.FirstOrDefault(child => child.Type == "EqualizerSoundEffect" && child.Enabled);
        return effect is null
            ? new EqualizerParameters(0, 0, 0)
            : new EqualizerParameters(
                Math.Clamp(float.IsFinite(effect.EqualizerLowGain) ? effect.EqualizerLowGain : 0, -80, 10),
                Math.Clamp(float.IsFinite(effect.EqualizerMidGain) ? effect.EqualizerMidGain : 0, -80, 10),
                Math.Clamp(float.IsFinite(effect.EqualizerHighGain) ? effect.EqualizerHighGain : 0, -80, 10));
    }

    private static FlangeParameters GetChorusParameters(SceneNode sound, SceneNode? soundGroup)
    {
        var effect = sound.Children.FirstOrDefault(child => child.Type == "ChorusSoundEffect" && child.Enabled)
            ?? soundGroup?.Children.FirstOrDefault(child => child.Type == "ChorusSoundEffect" && child.Enabled);
        return effect is null
            ? new FlangeParameters(0, 0, 0)
            : new FlangeParameters(
                Math.Clamp(float.IsFinite(effect.ChorusDepth) ? effect.ChorusDepth : .15f, 0, 1),
                Math.Clamp(float.IsFinite(effect.ChorusMix) ? effect.ChorusMix : .5f, 0, 1),
                Math.Clamp(float.IsFinite(effect.ChorusRate) ? effect.ChorusRate : .5f, 0, 20));
    }

    private CompressorParameters GetCompressorParameters(SceneNode sound, SceneNode[] sounds, IReadOnlyDictionary<string, SceneNode> nodesById)
    {
        var effect = sound.Children.FirstOrDefault(child => child.Type == "CompressorSoundEffect" && child.Enabled)
            ?? (nodesById.GetValueOrDefault(sound.SoundGroupId) is { Type: "SoundGroup" } soundGroup
                ? soundGroup.Children.FirstOrDefault(child => child.Type == "CompressorSoundEffect" && child.Enabled)
                : null);
        Func<float>? sideChainLevel = null;
        if (effect is not null && nodesById.TryGetValue(effect.CompressorSideChainId, out var sideChain))
        {
            if (sideChain.Type == "Sound")
                sideChainLevel = () => audioMeters.GetValueOrDefault(sideChain.Id);
            else if (sideChain.Type == "SoundGroup")
            {
                var sideChainSoundIds = sounds.Where(candidate => candidate.SoundGroupId == sideChain.Id).Select(candidate => candidate.Id).ToArray();
                sideChainLevel = () => MathF.Min(1, MathF.Sqrt(sideChainSoundIds.Sum(id => MathF.Pow(audioMeters.GetValueOrDefault(id), 2))));
            }
        }
        return effect is null
            ? new CompressorParameters(.1f, 0, 1, .1f, 0, null)
            : new CompressorParameters(
                Math.Clamp(float.IsFinite(effect.CompressorAttack) ? effect.CompressorAttack : .1f, .1f, 1),
                Math.Clamp(float.IsFinite(effect.CompressorGainMakeup) ? effect.CompressorGainMakeup : 0, -30, 30),
                Math.Clamp(float.IsFinite(effect.CompressorRatio) ? effect.CompressorRatio : 1, 1, 50),
                Math.Clamp(float.IsFinite(effect.CompressorRelease) ? effect.CompressorRelease : .1f, 0, 5),
                Math.Clamp(float.IsFinite(effect.CompressorThreshold) ? effect.CompressorThreshold : 0, -80, 0),
                sideChainLevel);
    }

    private static FlangeParameters GetFlangeParameters(SceneNode sound, SceneNode? soundGroup)
    {
        var effect = sound.Children.FirstOrDefault(child => child.Type == "FlangeSoundEffect" && child.Enabled)
            ?? soundGroup?.Children.FirstOrDefault(child => child.Type == "FlangeSoundEffect" && child.Enabled);
        return effect is null
            ? new FlangeParameters(0, 0, 0)
            : new FlangeParameters(
                Math.Clamp(float.IsFinite(effect.FlangeDepth) ? effect.FlangeDepth : .45f, .01f, 1),
                Math.Clamp(float.IsFinite(effect.FlangeMix) ? effect.FlangeMix : .85f, 0, 1),
                Math.Clamp(float.IsFinite(effect.FlangeRate) ? effect.FlangeRate : 5, 0, 20));
    }

    private static TremoloParameters GetTremoloParameters(SceneNode sound, SceneNode? soundGroup)
    {
        var effect = sound.Children.FirstOrDefault(child => child.Type == "TremoloSoundEffect" && child.Enabled)
            ?? soundGroup?.Children.FirstOrDefault(child => child.Type == "TremoloSoundEffect" && child.Enabled);
        return effect is null
            ? new TremoloParameters(0, .5f, 5)
            : new TremoloParameters(
                Math.Clamp(float.IsFinite(effect.TremoloDepth) ? effect.TremoloDepth : 1, 0, 1),
                Math.Clamp(float.IsFinite(effect.TremoloDuty) ? effect.TremoloDuty : .5f, 0, 1),
                Math.Clamp(float.IsFinite(effect.TremoloFrequency) ? effect.TremoloFrequency : 5, .1f, 20));
    }

    private static ReverbParameters GetReverbParameters(SceneNode sound, SceneNode? soundGroup)
    {
        var effect = sound.Children.FirstOrDefault(child => child.Type == "ReverbSoundEffect" && child.Enabled)
            ?? soundGroup?.Children.FirstOrDefault(child => child.Type == "ReverbSoundEffect" && child.Enabled);
        return effect is null
            ? new ReverbParameters(1.5f, 0, 0, 0, -80)
            : new ReverbParameters(
                Math.Clamp(float.IsFinite(effect.ReverbDecayTime) ? effect.ReverbDecayTime : 1.5f, .1f, 20),
                Math.Clamp(float.IsFinite(effect.ReverbDensity) ? effect.ReverbDensity : 1, 0, 1),
                Math.Clamp(float.IsFinite(effect.ReverbDiffusion) ? effect.ReverbDiffusion : 1, 0, 1),
                Math.Clamp(float.IsFinite(effect.ReverbDryLevel) ? effect.ReverbDryLevel : -6, -80, 10),
                Math.Clamp(float.IsFinite(effect.ReverbWetLevel) ? effect.ReverbWetLevel : 0, -80, 10));
    }

    private void Start(SceneNode sound, string path, double volume, float pitchOctaves, DistortionParameters distortionParameters, EqualizerParameters equalizerParameters, FlangeParameters chorusParameters, CompressorParameters compressorParameters, EchoParameters echoParameters, FlangeParameters flangeParameters, TremoloParameters tremoloParameters, ReverbParameters reverbParameters)
    {
        MediaFoundationReader? reader = null;
        WaveOutEvent? output = null;
        try
        {
            reader = new MediaFoundationReader(path);
            Seek(reader, sound, sound.SoundSeekTarget ?? sound.SoundTimePosition);
            var provider = new SoundTouchWaveProvider(reader) { Rate = GetPlaybackSpeed(sound.PlaybackSpeed), PitchOctaves = pitchOctaves };
            var meter = new LevelMeterWaveProvider(provider, level => audioMeters[sound.Id] = level) { Gain = (float)volume };
            audioMeters[sound.Id] = 0;
            var distortion = new DistortionWaveProvider(meter);
            distortion.SetParameters(distortionParameters);
            var equalizer = new EqualizerWaveProvider(distortion);
            equalizer.SetParameters(equalizerParameters);
            var chorus = new FlangeWaveProvider(equalizer);
            chorus.SetParameters(chorusParameters);
            var compressor = new CompressorWaveProvider(chorus);
            compressor.SetParameters(compressorParameters);
            var echo = new EchoWaveProvider(compressor);
            echo.SetParameters(echoParameters);
            var flange = new FlangeWaveProvider(echo);
            flange.SetParameters(flangeParameters);
            var tremolo = new TremoloWaveProvider(flange);
            tremolo.SetParameters(tremoloParameters);
            var reverb = new ReverbWaveProvider(tremolo);
            reverb.SetParameters(reverbParameters);
            output = new WaveOutEvent { Volume = (float)volume };
            output.Init(reverb);
            var playback = new Playback(sound, output, reader, provider, meter, distortion, equalizer, chorus, compressor, echo, flange, tremolo, reverb, path);
            active[sound.Id] = playback;
            output.PlaybackStopped += (_, eventArgs) => dispatcher.BeginInvoke(new Action(() => HandlePlaybackStopped(sound, playback, eventArgs.Exception)));
            output.Play();
            if (globallyPaused || sound.SoundPaused) output.Pause();
            else signalFired?.Invoke(sound, "Played");
        }
        catch (Exception exception) when (IsAudioException(exception))
        {
            if (active.TryGetValue(sound.Id, out var current) && ReferenceEquals(current.Output, output))
                active.Remove(sound.Id);
            output?.Dispose();
            reader?.Dispose();
            audioMeters.TryRemove(sound.Id, out _);
            sound.Playing = false;
            sound.SoundPaused = false;
            sound.SoundTimePosition = 0;
            stateChanged(sound);
        }
    }

    private void HandlePlaybackStopped(SceneNode sound, Playback playback, Exception? error)
    {
        if (disposed || !active.TryGetValue(sound.Id, out var current) || !ReferenceEquals(current, playback)) return;
        if (error is null && sound.Playing && !sound.SoundPaused && sound.Looped)
        {
            try
            {
                playback.Reader.Position = 0;
                sound.SoundTimePosition = 0;
                ResetProcessors(playback);
                playback.Output.Init(playback.Reverb);
                playback.Output.Play();
                stateChanged(sound);
                signalFired?.Invoke(sound, "DidLoop");
                return;
            }
            catch (Exception exception) when (IsAudioException(exception)) { }
        }

        var endedNaturally = error is null && sound.Playing && !sound.SoundPaused;
        sound.Playing = false;
        sound.SoundPaused = false;
        sound.SoundTimePosition = 0;
        Stop(sound.Id);
        stateChanged(sound);
        if (endedNaturally) signalFired?.Invoke(sound, "Ended");
    }

    private static void Seek(MediaFoundationReader reader, SceneNode sound, float requestedTime)
    {
        sound.SoundSeekTarget = null;
        var duration = Math.Max(0, reader.TotalTime.TotalSeconds);
        var maximum = double.IsFinite(duration) ? (float)Math.Min(duration, float.MaxValue) : 0;
        var time = float.IsFinite(requestedTime) ? Math.Clamp(requestedTime, 0, maximum) : 0;
        try
        {
            reader.CurrentTime = TimeSpan.FromSeconds(time);
            sound.SoundTimePosition = time;
        }
        catch (Exception exception) when (IsAudioException(exception))
        {
            sound.SoundTimePosition = 0;
        }
    }

    private static void ResetProcessors(Playback playback)
    {
        playback.Provider.Clear();
        playback.Chorus.Reset();
        playback.Compressor.Reset();
        playback.Echo.Reset();
        playback.Flange.Reset();
        playback.Tremolo.Reset();
        playback.Reverb.Reset();
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
        if (!active.Remove(id, out var playback))
        {
            audioMeters.TryRemove(id, out _);
            return;
        }
        playback.Output.Stop();
        playback.Output.Dispose();
        playback.Reader.Dispose();
        audioMeters.TryRemove(id, out _);
    }

    private sealed record Playback(SceneNode Node, WaveOutEvent Output, MediaFoundationReader Reader, SoundTouchWaveProvider Provider, LevelMeterWaveProvider Meter, DistortionWaveProvider Distortion, EqualizerWaveProvider Equalizer, FlangeWaveProvider Chorus, CompressorWaveProvider Compressor, EchoWaveProvider Echo, FlangeWaveProvider Flange, TremoloWaveProvider Tremolo, ReverbWaveProvider Reverb, string Path);

    private sealed class LevelMeterWaveProvider(IWaveProvider source, Action<float> report) : IWaveProvider
    {
        public float Gain { get; set; } = 1;
        public WaveFormat WaveFormat => source.WaveFormat;
        public void SetGain(float value) => Gain = float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            var format = source.WaveFormat;
            var sampleSize = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32 ? 4
                : format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16 ? 2 : 0;
            if (read <= 0 || sampleSize == 0)
            {
                report(0);
                return read;
            }
            var sampleCount = read / sampleSize;
            double sumSquares = 0;
            for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                var sampleOffset = offset + sampleIndex * sampleSize;
                var sample = sampleSize == 4 ? BitConverter.ToSingle(buffer, sampleOffset) : BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(sampleOffset, 2)) / 32768f;
                if (float.IsFinite(sample)) sumSquares += sample * sample;
            }
            report((float)Math.Clamp(Math.Sqrt(sumSquares / Math.Max(1, sampleCount)) * Gain, 0, 1));
            return read;
        }
    }

    private sealed class DistortionWaveProvider(IWaveProvider source) : IWaveProvider
    {
        private float level;
        public WaveFormat WaveFormat => source.WaveFormat;
        public void SetParameters(DistortionParameters parameters) => level = Math.Clamp(parameters.Level, 0, 1);

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            if (level <= 0 || read <= 0) return read;
            var format = source.WaveFormat;
            var sampleSize = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32 ? 4
                : format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16 ? 2 : 0;
            if (sampleSize == 0) return read;
            var end = offset + read - (read % sampleSize);
            var drive = 1 + 24 * level * level;
            var normalization = MathF.Tanh(drive);
            for (var index = offset; index < end; index += sampleSize)
            {
                var original = sampleSize == 4 ? BitConverter.ToSingle(buffer, index) : BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(index, 2)) / 32768f;
                if (!float.IsFinite(original)) continue;
                var clipped = MathF.Tanh(original * drive) / normalization;
                var output = Math.Clamp(original + (clipped - original) * level, -1, 1);
                if (sampleSize == 4) BitConverter.TryWriteBytes(buffer.AsSpan(index, 4), output);
                else BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(index, 2), (short)Math.Clamp((int)MathF.Round(output * 32767), short.MinValue, short.MaxValue));
            }
            return read;
        }
    }

    private sealed class EqualizerWaveProvider(IWaveProvider source) : IWaveProvider
    {
        private EqualizerParameters parameters;
        private float[] lowPassState = [];
        private float[] midPassState = [];
        public WaveFormat WaveFormat => source.WaveFormat;
        public void SetParameters(EqualizerParameters value) => parameters = value;

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            var format = source.WaveFormat;
            var sampleSize = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32 ? 4
                : format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16 ? 2 : 0;
            var channels = format.Channels;
            if (sampleSize == 0 || channels < 1 || format.SampleRate < 1 || read <= 0) return read;
            if (lowPassState.Length != channels)
            {
                lowPassState = new float[channels];
                midPassState = new float[channels];
            }

            var lowCoefficient = 1 - MathF.Exp(-2 * MathF.PI * 400 / format.SampleRate);
            var midCoefficient = 1 - MathF.Exp(-2 * MathF.PI * Math.Min(4000, format.SampleRate * .45f) / format.SampleRate);
            var lowGain = MathF.Pow(10, Math.Clamp(parameters.LowGain, -80, 10) / 20);
            var midGain = MathF.Pow(10, Math.Clamp(parameters.MidGain, -80, 10) / 20);
            var highGain = MathF.Pow(10, Math.Clamp(parameters.HighGain, -80, 10) / 20);
            var frameSize = sampleSize * channels;
            var end = offset + read - read % frameSize;
            for (var index = offset; index < end; index += sampleSize)
            {
                var channel = (index - offset) / sampleSize % channels;
                var input = sampleSize == 4 ? BitConverter.ToSingle(buffer, index) : BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(index, 2)) / 32768f;
                if (!float.IsFinite(input)) continue;
                lowPassState[channel] += lowCoefficient * (input - lowPassState[channel]);
                midPassState[channel] += midCoefficient * (input - midPassState[channel]);
                var low = lowPassState[channel];
                var mid = midPassState[channel] - low;
                var high = input - midPassState[channel];
                var output = Math.Clamp(low * lowGain + mid * midGain + high * highGain, -1, 1);
                if (sampleSize == 4) BitConverter.TryWriteBytes(buffer.AsSpan(index, 4), output);
                else BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(index, 2), (short)Math.Clamp((int)MathF.Round(output * 32767), short.MinValue, short.MaxValue));
            }
            return read;
        }
    }

    private sealed class CompressorWaveProvider(IWaveProvider source) : IWaveProvider
    {
        private CompressorParameters parameters = new(.1f, 0, 1, .1f, 0, null);
        private float envelope;
        public WaveFormat WaveFormat => source.WaveFormat;
        public void SetParameters(CompressorParameters value) => parameters = value;
        public void Reset() => envelope = 0;

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            var format = source.WaveFormat;
            var sampleSize = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32 ? 4
                : format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16 ? 2 : 0;
            var channels = format.Channels;
            if (sampleSize == 0 || channels < 1 || format.SampleRate < 1 || read <= 0) return read;

            var attack = Math.Clamp(parameters.Attack, .1f, 1);
            var release = Math.Clamp(parameters.Release, 0, 5);
            var attackCoefficient = MathF.Exp(-1 / (attack * format.SampleRate));
            var releaseCoefficient = release <= 0 ? 0 : MathF.Exp(-1 / (release * format.SampleRate));
            var threshold = Math.Clamp(parameters.Threshold, -80, 0);
            var ratio = Math.Clamp(parameters.Ratio, 1, 50);
            var makeupGain = MathF.Pow(10, Math.Clamp(parameters.GainMakeup, -30, 30) / 20);
            var sideChainLevel = parameters.SideChainLevel?.Invoke();
            var frameSize = sampleSize * channels;
            var frameCount = read / frameSize;
            var samples = new float[channels];
            for (var frame = 0; frame < frameCount; frame++)
            {
                var frameOffset = offset + frame * frameSize;
                var peak = 0f;
                for (var channel = 0; channel < channels; channel++)
                {
                    var sampleOffset = frameOffset + channel * sampleSize;
                    var sample = sampleSize == 4 ? BitConverter.ToSingle(buffer, sampleOffset) : BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(sampleOffset, 2)) / 32768f;
                    samples[channel] = float.IsFinite(sample) ? sample : 0;
                    peak = Math.Max(peak, Math.Abs(samples[channel]));
                }

                var detectedLevel = sideChainLevel is { } sideChain ? Math.Clamp(sideChain, 0, 1) : peak;
                var coefficient = detectedLevel > envelope ? attackCoefficient : releaseCoefficient;
                envelope = coefficient * envelope + (1 - coefficient) * detectedLevel;
                var inputDb = 20 * MathF.Log10(Math.Max(envelope, 1e-6f));
                var reductionDb = inputDb > threshold ? (threshold - inputDb) * (1 - 1 / ratio) : 0;
                var gain = MathF.Pow(10, reductionDb / 20) * makeupGain;
                for (var channel = 0; channel < channels; channel++)
                {
                    var sampleOffset = frameOffset + channel * sampleSize;
                    var output = Math.Clamp(samples[channel] * gain, -1, 1);
                    if (sampleSize == 4) BitConverter.TryWriteBytes(buffer.AsSpan(sampleOffset, 4), output);
                    else BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(sampleOffset, 2), (short)Math.Clamp((int)MathF.Round(output * 32767), short.MinValue, short.MaxValue));
                }
            }
            return read;
        }
    }

    private sealed class EchoWaveProvider(IWaveProvider source) : IWaveProvider
    {
        private float delay;
        private float feedback;
        private float dryLevel;
        private float wetLevel;
        private bool continueTail;
        private float[] delayLine = [];
        private int delaySamples;
        private int writeIndex;
        private int tailFramesRemaining;

        public WaveFormat WaveFormat => source.WaveFormat;

        public void SetParameters(EchoParameters parameters)
        {
            delay = parameters.Delay;
            feedback = parameters.Feedback;
            dryLevel = parameters.DryLevel;
            wetLevel = parameters.WetLevel;
            continueTail = parameters.ContinueTail;
        }

        public void Reset()
        {
            Array.Clear(delayLine);
            writeIndex = 0;
            tailFramesRemaining = 0;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            var format = source.WaveFormat;
            var sampleSize = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32 ? 4
                : format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16 ? 2 : 0;
            if (sampleSize == 0 || format.Channels < 1 || format.SampleRate < 1) return read;
            if (read > 0 && continueTail)
            {
                var feedbackAmount = Math.Clamp(feedback, 0, 1);
                var repeats = feedbackAmount >= .999f ? 6 : feedbackAmount <= .001f ? 1 : Math.Ceiling(Math.Log(.001) / Math.Log(feedbackAmount));
                tailFramesRemaining = Math.Min(format.SampleRate * 30, (int)Math.Ceiling(Math.Clamp(delay, .01f, 5) * repeats * format.SampleRate));
            }
            else if (read <= 0 && continueTail)
            {
                if (tailFramesRemaining <= 0) return 0;
                read = Math.Min(count, tailFramesRemaining * sampleSize * format.Channels);
                read -= read % (sampleSize * format.Channels);
                if (read <= 0) return 0;
                Array.Clear(buffer, offset, read);
                tailFramesRemaining -= read / sampleSize / format.Channels;
            }
            else if (read <= 0) return read;

            var requestedDelaySamples = Math.Clamp((int)Math.Round(Math.Clamp(delay, 0, 5) * format.SampleRate), 0, format.SampleRate * 5) * format.Channels;
            if (requestedDelaySamples != delaySamples)
            {
                delaySamples = requestedDelaySamples;
                delayLine = delaySamples == 0 ? [] : new float[delaySamples];
                writeIndex = 0;
            }
            var dry = DbToGain(dryLevel);
            var wet = DbToGain(wetLevel);
            var sampleCount = read / sampleSize;
            for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                var sampleOffset = offset + sampleIndex * sampleSize;
                var input = sampleSize == 4
                    ? BitConverter.ToSingle(buffer, sampleOffset)
                    : BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(sampleOffset, 2)) / 32768f;
                if (!float.IsFinite(input)) input = 0;
                var echo = delaySamples == 0 ? 0 : delayLine[writeIndex];
                var output = input * dry + echo * wet;
                if (delaySamples > 0)
                {
                    delayLine[writeIndex] = Math.Clamp(input + echo * Math.Clamp(feedback, 0, 1), -1, 1);
                    if (++writeIndex >= delaySamples) writeIndex = 0;
                }
                if (sampleSize == 4) BitConverter.TryWriteBytes(buffer.AsSpan(sampleOffset, 4), Math.Clamp(output, -1, 1));
                else BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(sampleOffset, 2), (short)Math.Clamp((int)MathF.Round(output * 32767), short.MinValue, short.MaxValue));
            }
            return read;
        }

        private static float DbToGain(float decibels) => decibels <= -79 ? 0 : (float)Math.Pow(10, Math.Clamp(decibels, -80, 10) / 20);
    }

    private sealed class FlangeWaveProvider(IWaveProvider source) : IWaveProvider
    {
        private float depth = .45f;
        private float mix = .85f;
        private float rate = 5;
        private float[] delayLine = [];
        private int writeFrame;
        private double phase;

        public WaveFormat WaveFormat => source.WaveFormat;

        public void SetParameters(FlangeParameters parameters)
        {
            depth = parameters.Depth;
            mix = parameters.Mix;
            rate = parameters.Rate;
        }

        public void Reset()
        {
            Array.Clear(delayLine);
            writeFrame = 0;
            phase = 0;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            var format = source.WaveFormat;
            var sampleSize = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32 ? 4
                : format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16 ? 2 : 0;
            var channels = format.Channels;
            if (read <= 0 || sampleSize == 0 || channels < 1 || format.SampleRate < 1) return read;

            var maxDelayFrames = Math.Max(2, (int)Math.Ceiling(format.SampleRate * .04));
            var neededSamples = (maxDelayFrames + 1) * channels;
            if (delayLine.Length != neededSamples)
            {
                delayLine = new float[neededSamples];
                writeFrame = 0;
                phase = 0;
            }

            var sampleCount = read / sampleSize;
            var frameCount = sampleCount / channels;
            var maxDelay = format.SampleRate * .04 * Math.Clamp(depth, 0, 1);
            var mixAmount = Math.Clamp(mix, 0, 1);
            var frameStride = channels;
            for (var frame = 0; frame < frameCount; frame++)
            {
                var delayFrames = (int)Math.Round((Math.Sin(phase) + 1) * .5 * maxDelay);
                phase += Math.Tau * Math.Clamp(rate, 0, 20) / format.SampleRate;
                if (phase >= Math.Tau) phase %= Math.Tau;
                for (var channel = 0; channel < channels; channel++)
                {
                    var sampleIndex = frame * frameStride + channel;
                    var sampleOffset = offset + sampleIndex * sampleSize;
                    var input = sampleSize == 4
                        ? BitConverter.ToSingle(buffer, sampleOffset)
                        : BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(sampleOffset, 2)) / 32768f;
                    if (!float.IsFinite(input)) input = 0;
                    var writeSample = writeFrame * channels + channel;
                    var readFrame = (writeFrame - delayFrames + maxDelayFrames + 1) % (maxDelayFrames + 1);
                    var delayed = delayLine[readFrame * channels + channel];
                    var output = depth <= 0 ? input : input * (1 - mixAmount) + delayed * mixAmount;
                    delayLine[writeSample] = input;
                    if (sampleSize == 4) BitConverter.TryWriteBytes(buffer.AsSpan(sampleOffset, 4), Math.Clamp(output, -1, 1));
                    else BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(sampleOffset, 2), (short)Math.Clamp((int)MathF.Round(output * 32767), short.MinValue, short.MaxValue));
                }
                if (++writeFrame > maxDelayFrames) writeFrame = 0;
            }
            return read;
        }
    }

    private sealed class TremoloWaveProvider(IWaveProvider source) : IWaveProvider
    {
        private float depth = 1;
        private float duty = .5f;
        private float frequency = 5;
        private double phase;

        public WaveFormat WaveFormat => source.WaveFormat;

        public void SetParameters(TremoloParameters parameters)
        {
            depth = parameters.Depth;
            duty = parameters.Duty;
            frequency = parameters.Frequency;
        }

        public void Reset() => phase = 0;

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            var format = source.WaveFormat;
            var sampleSize = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32 ? 4
                : format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16 ? 2 : 0;
            if (read <= 0 || sampleSize == 0 || format.Channels < 1 || format.SampleRate < 1) return read;

            var frameCount = (read / sampleSize) / format.Channels;
            var depthAmount = Math.Clamp(depth, 0, 1);
            var dutyAmount = Math.Clamp(duty, 0, 1);
            var phaseStep = Math.Clamp(frequency, .1f, 20) / format.SampleRate;
            for (var frame = 0; frame < frameCount; frame++)
            {
                var gain = 1f;
                if (dutyAmount > 0 && phase < dutyAmount)
                {
                    var activePhase = phase / dutyAmount;
                    gain = 1 - depthAmount * (float)((1 - Math.Cos(Math.Tau * activePhase)) * .5);
                }
                for (var channel = 0; channel < format.Channels; channel++)
                {
                    var sampleOffset = offset + (frame * format.Channels + channel) * sampleSize;
                    var input = sampleSize == 4
                        ? BitConverter.ToSingle(buffer, sampleOffset)
                        : BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(sampleOffset, 2)) / 32768f;
                    if (!float.IsFinite(input)) input = 0;
                    var output = Math.Clamp(input * gain, -1, 1);
                    if (sampleSize == 4) BitConverter.TryWriteBytes(buffer.AsSpan(sampleOffset, 4), output);
                    else BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(sampleOffset, 2), (short)Math.Clamp((int)MathF.Round(output * 32767), short.MinValue, short.MaxValue));
                }
                phase += phaseStep;
                if (phase >= 1) phase %= 1;
            }
            return read;
        }
    }

    private sealed class ReverbWaveProvider(IWaveProvider source) : IWaveProvider
    {
        private static readonly double[] DelaySeconds = [.0297, .0371, .0411, .0437];
        private static readonly float[] DelayWeights = [.28f, .25f, .24f, .23f];
        private readonly float[][] delayLines = new float[DelaySeconds.Length][];
        private readonly int[] writeIndices = new int[DelaySeconds.Length];
        private float[] previousInput = [];
        private ReverbParameters parameters = new(1.5f, 1, 1, -6, 0);
        private int tailFramesRemaining;

        public WaveFormat WaveFormat => source.WaveFormat;

        public void SetParameters(ReverbParameters value) => parameters = value;

        public void Reset()
        {
            foreach (var line in delayLines)
                if (line is not null) Array.Clear(line);
            Array.Clear(writeIndices);
            Array.Clear(previousInput);
            tailFramesRemaining = 0;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            var format = source.WaveFormat;
            var sampleSize = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32 ? 4
                : format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16 ? 2 : 0;
            var channels = format.Channels;
            if (sampleSize == 0 || channels < 1 || format.SampleRate < 1) return read;
            if (read > 0 && Math.Clamp(parameters.Density, 0, 1) > 0 && parameters.WetLevel > -79)
                tailFramesRemaining = (int)Math.Ceiling(format.SampleRate * Math.Clamp(parameters.DecayTime, .1f, 20));
            else if (read <= 0)
            {
                if (tailFramesRemaining <= 0) return 0;
                read = Math.Min(count, tailFramesRemaining * sampleSize * channels);
                read -= read % (sampleSize * channels);
                if (read <= 0) return 0;
                Array.Clear(buffer, offset, read);
                tailFramesRemaining -= read / sampleSize / channels;
            }

            if (previousInput.Length != channels) previousInput = new float[channels];
            for (var lineIndex = 0; lineIndex < DelaySeconds.Length; lineIndex++)
            {
                var frameLength = Math.Max(1, (int)Math.Round(DelaySeconds[lineIndex] * format.SampleRate));
                var required = frameLength * channels;
                if (delayLines[lineIndex]?.Length != required)
                {
                    delayLines[lineIndex] = new float[required];
                    writeIndices[lineIndex] = 0;
                }
            }

            var dryGain = DbToGain(parameters.DryLevel);
            var wetGain = DbToGain(parameters.WetLevel) * Math.Clamp(parameters.Density, 0, 1);
            var density = Math.Clamp(parameters.Density, 0, 1);
            var diffusion = Math.Clamp(parameters.Diffusion, 0, 1);
            var decay = Math.Clamp(parameters.DecayTime, .1f, 20);
            var frameCount = (read / sampleSize) / channels;
            for (var frame = 0; frame < frameCount; frame++)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var sampleOffset = offset + (frame * channels + channel) * sampleSize;
                    var input = sampleSize == 4
                        ? BitConverter.ToSingle(buffer, sampleOffset)
                        : BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(sampleOffset, 2)) / 32768f;
                    if (!float.IsFinite(input)) input = 0;
                    var accumulated = 0f;
                    for (var lineIndex = 0; lineIndex < DelaySeconds.Length; lineIndex++)
                    {
                        var line = delayLines[lineIndex];
                        var frameLength = line.Length / channels;
                        var sampleIndex = writeIndices[lineIndex] * channels + channel;
                        var echo = line[sampleIndex];
                        var decayPerDelay = (float)Math.Pow(.001, DelaySeconds[lineIndex] / decay);
                        var feedback = Math.Clamp(decayPerDelay * density, 0, .995f);
                        var diffuseInput = input * (1 - diffusion * .5f) + previousInput[channel] * (diffusion * .5f);
                        line[sampleIndex] = Math.Clamp(diffuseInput + echo * feedback, -2, 2);
                        accumulated += echo * DelayWeights[lineIndex];
                    }
                    previousInput[channel] = input;
                    var output = Math.Clamp(input * dryGain + accumulated * wetGain, -1, 1);
                    if (sampleSize == 4) BitConverter.TryWriteBytes(buffer.AsSpan(sampleOffset, 4), output);
                    else BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(sampleOffset, 2), (short)Math.Clamp((int)MathF.Round(output * 32767), short.MinValue, short.MaxValue));
                }
                for (var lineIndex = 0; lineIndex < DelaySeconds.Length; lineIndex++)
                    if (++writeIndices[lineIndex] >= delayLines[lineIndex].Length / channels) writeIndices[lineIndex] = 0;
            }
            return read;
        }

        private static float DbToGain(float decibels) => decibels <= -79 ? 0 : (float)Math.Pow(10, Math.Clamp(decibels, -80, 10) / 20);
    }
}
