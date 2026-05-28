using System;
using NAudio.Wave;
using VoiceChatPlugin.Audio;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class BclMonoPlaybackGraph
{
    private readonly MonoPanRouter _imager;
    private readonly VolumeRouter _normalVolume;
    private readonly VolumeRouter _ghostVolume;
    private readonly VolumeRouter _radioVolume;
    private readonly VolumeRouter _listenerMuffleVolume;
    private readonly VolumeRouter _clientVolume;
    private readonly LevelMeterRouter _levelMeter;
    private readonly AudioManager _audioManager;
    private readonly VolumeRouter.Property _masterVolume;

    public BclMonoPlaybackGraph(int bufferLength, int bufferMaxLength, bool enableRecoveryPrebuffer, int instancePrebufferSamples)
    {
        var source = new SimpleRouter();
        var endpoint = new SimpleEndpoint();
        _imager = new MonoPanRouter();
        _normalVolume = new VolumeRouter();
        _ghostVolume = new VolumeRouter();
        _radioVolume = new VolumeRouter();
        _listenerMuffleVolume = new VolumeRouter();
        _clientVolume = new VolumeRouter();
        _levelMeter = new LevelMeterRouter();

        var ghostLowpass = FilterRouter.CreateLowPassFilter(1900f, 2f);
        var listenerMuffleLowpass = FilterRouter.CreateLowPassFilter(650f, 0.8f);
        var listenerMuffleReverb = new ReverbRouter(41, 0.55f, 0.65f) { IsGlobalRouter = true };
        var ghostReverb1 = new ReverbRouter(53, 0.7f, 0.2f) { IsGlobalRouter = true };
        var ghostReverb2 = new ReverbRouter(173, 0.4f, 0.6f) { IsGlobalRouter = true };
        var radioHighpass = FilterRouter.CreateHighPassFilter(650f, 3.2f);
        var radioDistort = new DistortionFilter { IsGlobalRouter = true, DefaultThreshold = 0.85f };
        var masterRouter = new VolumeRouter { IsGlobalRouter = true };

        source.Connect(_clientVolume);
        _clientVolume.Connect(_imager);
        _imager.Connect(_normalVolume);
        _normalVolume.Connect(_levelMeter);
        _levelMeter.Connect(masterRouter);
        _imager.Connect(ghostLowpass);
        ghostLowpass.Connect(_ghostVolume);
        _ghostVolume.Connect(ghostReverb1);
        ghostReverb1.Connect(ghostReverb2);
        ghostReverb2.Connect(masterRouter);
        _imager.Connect(listenerMuffleLowpass);
        listenerMuffleLowpass.Connect(_listenerMuffleVolume);
        _listenerMuffleVolume.Connect(listenerMuffleReverb);
        listenerMuffleReverb.Connect(masterRouter);
        _clientVolume.Connect(radioHighpass);
        radioHighpass.Connect(_radioVolume);
        _radioVolume.Connect(radioDistort);
        radioDistort.Connect(masterRouter);
        masterRouter.Connect(endpoint);

        _audioManager = new AudioManager(source, bufferLength, bufferMaxLength, enableRecoveryPrebuffer, instancePrebufferSamples);
        _masterVolume = masterRouter.GetProperty(_audioManager);
        _masterVolume.Volume = 1f;
    }

    public ISampleProvider Endpoint => _audioManager.Endpoint!;

    public void SetMasterVolume(float volume) => _masterVolume.Volume = Math.Clamp(volume, 0f, 2f);

    public BclPeerPlaybackRoute Generate(int groupId)
    {
        var instance = _audioManager.Generate(groupId);
        return new BclPeerPlaybackRoute(
            instance,
            _imager.GetProperty(instance),
            _normalVolume.GetProperty(instance),
            _ghostVolume.GetProperty(instance),
            _radioVolume.GetProperty(instance),
            _listenerMuffleVolume.GetProperty(instance),
            _clientVolume.GetProperty(instance),
            _levelMeter.GetProperty(instance));
    }

    public void Remove(int groupId) => _audioManager.Remove(groupId);
}

internal sealed class BclPeerPlaybackRoute
{
    private readonly AudioRoutingInstance _instance;
    private readonly MonoPanRouter.Property _imager;
    private readonly VolumeRouter.Property _normalVolume;
    private readonly VolumeRouter.Property _ghostVolume;
    private readonly VolumeRouter.Property _radioVolume;
    private readonly VolumeRouter.Property _listenerMuffleVolume;
    private readonly VolumeRouter.Property _clientVolume;
    private readonly LevelMeterRouter.Property _levelMeter;

    public BclPeerPlaybackRoute(
        AudioRoutingInstance instance,
        MonoPanRouter.Property imager,
        VolumeRouter.Property normalVolume,
        VolumeRouter.Property ghostVolume,
        VolumeRouter.Property radioVolume,
        VolumeRouter.Property listenerMuffleVolume,
        VolumeRouter.Property clientVolume,
        LevelMeterRouter.Property levelMeter)
    {
        _instance = instance;
        _imager = imager;
        _normalVolume = normalVolume;
        _ghostVolume = ghostVolume;
        _radioVolume = radioVolume;
        _listenerMuffleVolume = listenerMuffleVolume;
        _clientVolume = clientVolume;
        _levelMeter = levelMeter;
        _clientVolume.Volume = 1f;
        MuteAll();
    }

    public int BufferedSamples => _instance.BufferedSamples;
    public float Level => _levelMeter.Level;

    public void SetVolume(float volume) => _clientVolume.Volume = Math.Clamp(volume, 0f, 2f);

    public void MuteAll()
    {
        _normalVolume.Volume = 0f;
        _ghostVolume.Volume = 0f;
        _radioVolume.Volume = 0f;
        _listenerMuffleVolume.Volume = 0f;
        _imager.Pan = 0f;
    }

    public void Apply(VoiceProximityResult result, float gain)
    {
        gain = Math.Clamp(gain, 0f, 1f);
        bool listenerMuffled = result.FilterMode == VoiceAudioFilterMode.ListenerMuffle;
        float routeVolume = Math.Clamp(result.NormalVolume + result.GhostVolume + result.RadioVolume, 0f, 1f);
        _normalVolume.Volume = listenerMuffled ? 0f : result.NormalVolume * gain;
        _ghostVolume.Volume = listenerMuffled ? 0f : result.GhostVolume * gain;
        _radioVolume.Volume = listenerMuffled ? 0f : result.RadioVolume * gain;
        _listenerMuffleVolume.Volume = listenerMuffled ? routeVolume * 0.75f * gain : 0f;
        _imager.Pan = 0f;
    }

    public void AddSamples(float[] samples, int offset, int count) => _instance.AddSamples(samples, offset, count);
    public void ClearBufferedSamples() => _instance.ClearBufferedSamples();
    public string ConsumeDebugStats() => _instance.ConsumeDebugStats();
}

internal sealed class BclStereoPlaybackProvider : ISampleProvider
{
    private const float FullPanFarSideGain = 0.25f;
    private readonly ISampleProvider _left;
    private readonly ISampleProvider _right;
    private float[] _leftBuffer = Array.Empty<float>();
    private float[] _rightBuffer = Array.Empty<float>();

    public BclStereoPlaybackProvider(ISampleProvider left, ISampleProvider right)
    {
        if (left.WaveFormat.Channels != 1 || right.WaveFormat.Channels != 1)
            throw new ArgumentException("BCL stereo playback provider requires mono source graphs.");
        _left = left;
        _right = right;
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioHelpers.ClockRate, 2);

    public static void GetPanGains(float pan, out float leftGain, out float rightGain)
    {
        pan = Math.Clamp(pan, -1f, 1f);
        leftGain = 1f;
        rightGain = 1f;
        if (pan > 0f)
            leftGain = 1f - pan * (1f - FullPanFarSideGain);
        else if (pan < 0f)
            rightGain = 1f + pan * (1f - FullPanFarSideGain);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (count <= 0) return 0;

        var frames = count / 2;
        if (frames <= 0)
        {
            buffer[offset] = 0f;
            return 1;
        }

        if (_leftBuffer.Length < frames) _leftBuffer = new float[frames];
        if (_rightBuffer.Length < frames) _rightBuffer = new float[frames];

        var leftRead = _left.Read(_leftBuffer, 0, frames);
        var rightRead = _right.Read(_rightBuffer, 0, frames);

        for (var frame = 0; frame < frames; frame++)
        {
            buffer[offset + frame * 2] = frame < leftRead ? _leftBuffer[frame] : 0f;
            buffer[offset + frame * 2 + 1] = frame < rightRead ? _rightBuffer[frame] : 0f;
        }

        if ((count & 1) != 0)
            buffer[offset + count - 1] = 0f;

        return count;
    }
}
