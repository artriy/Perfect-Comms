using System;

namespace VoiceChatPlugin.VoiceChat;

internal enum CaptureHealth
{
    Healthy = 0,
    Silent = 1,
    Dead = 2,
}

internal interface ICaptureSource
{
    bool Start(string? deviceId);
    void Stop();
    event Action<float[], int> OnFrame;
    CaptureHealth Health { get; }
}
