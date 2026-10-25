using System.IO;
using System.Buffers.Binary;
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

    private readonly record struct EchoParameters(float Delay, float Feedback, float DryLevel, float WetLevel, bool ContinueTail);
    private readonly record struct FlangeParameters(float Depth, float Mix, float Rate);
    private readonly record struct TremoloParameters(float Depth, float Duty, float Frequency);
    private readonly record struct ReverbParameters(float DecayTime, float Density, float Diffusion, float DryLevel, float WetLevel);

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
            var echoParameters = GetEchoParameters(sound, soundGroup);
            var flangeParameters = GetFlangeParameters(sound, soundGroup);
            var tremoloParameters = GetTremoloParameters(sound, soundGroup);
            var reverbParameters = GetReverbParameters(sound, soundGroup);
            var volume = CalculateVolume(sound, parentPart, listenerPosition, soundGroupVolume, project.SoundService.SoundServiceVolume);
            if (active.TryGetValue(sound.Id, out var playback) && string.Equals(playback.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                playback.Output.Volume = (float)volume;
                playback.Provider.Rate = GetPlaybackSpeed(sound.PlaybackSpeed);
                playback.Provider.PitchOctaves = pitchOctaves;
                playback.Echo.SetParameters(echoParameters);
                playback.Flange.SetParameters(flangeParameters);
                playback.Tremolo.SetParameters(tremoloParameters);
                playback.Reverb.SetParameters(reverbParameters);
                continue;
            }

            Stop(sound.Id);
            Start(sound, path, volume, pitchOctaves, echoParameters, flangeParameters, tremoloParameters, reverbParameters);
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

    private void Start(SceneNode sound, string path, double volume, float pitchOctaves, EchoParameters echoParameters, FlangeParameters flangeParameters, TremoloParameters tremoloParameters, ReverbParameters reverbParameters)
    {
        MediaFoundationReader? reader = null;
        WaveOutEvent? output = null;
        try
        {
            reader = new MediaFoundationReader(path);
            var provider = new SoundTouchWaveProvider(reader) { Rate = GetPlaybackSpeed(sound.PlaybackSpeed), PitchOctaves = pitchOctaves };
            var echo = new EchoWaveProvider(provider);
            echo.SetParameters(echoParameters);
            var flange = new FlangeWaveProvider(echo);
            flange.SetParameters(flangeParameters);
            var tremolo = new TremoloWaveProvider(flange);
            tremolo.SetParameters(tremoloParameters);
            var reverb = new ReverbWaveProvider(tremolo);
            reverb.SetParameters(reverbParameters);
            output = new WaveOutEvent { Volume = (float)volume };
            output.Init(reverb);
            var playback = new Playback(output, reader, provider, echo, flange, tremolo, reverb, path);
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
                playback.Echo.Reset();
                playback.Flange.Reset();
                playback.Tremolo.Reset();
                playback.Reverb.Reset();
                playback.Output.Init(playback.Reverb);
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

    private sealed record Playback(WaveOutEvent Output, MediaFoundationReader Reader, SoundTouchWaveProvider Provider, EchoWaveProvider Echo, FlangeWaveProvider Flange, TremoloWaveProvider Tremolo, ReverbWaveProvider Reverb, string Path);

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
            var maxDelay = format.SampleRate * .04 * Math.Clamp(depth, .01f, 1);
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
                    var output = input * (1 - mixAmount) + delayed * mixAmount;
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
