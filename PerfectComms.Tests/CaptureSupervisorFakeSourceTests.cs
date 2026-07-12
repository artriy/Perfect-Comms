using System;
using System.Collections.Generic;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class CaptureSupervisorFakeSourceTests
{
    private sealed class FakeCaptureSource : ICaptureSource
    {
        public int StartCount;
        public int StopCount;
        public bool Running;
        public string? LastDeviceId;
        private CaptureHealth _health = CaptureHealth.Healthy;

        public event Action<float[], int>? OnFrame;

        public CaptureHealth Health => Running ? _health : CaptureHealth.Dead;

        public void SetHealth(CaptureHealth h) => _health = h;

        public bool Start(string? deviceId)
        {
            StartCount++;
            LastDeviceId = deviceId;
            Running = true;
            _health = CaptureHealth.Healthy;
            return true;
        }

        public void Stop()
        {
            StopCount++;
            Running = false;
        }

        public void Emit(float[] buf, int count) => OnFrame?.Invoke(buf, count);
    }

    private sealed class SourceRack
    {
        public readonly List<FakeCaptureSource> Sources = new();
        public readonly List<float[]> Forwarded = new();
        public readonly string DeviceName;
        public CaptureSupervisor Supervisor = null!;
        public string? LastAllFailedReason;

        private int _active = -1;
        private Action<float[], int>? _activeHandler;

        public SourceRack(int count, string deviceName)
        {
            DeviceName = deviceName;
            for (var i = 0; i < count; i++)
                Sources.Add(new FakeCaptureSource());
        }

        public FakeCaptureSource Active => Sources[_active];

        public void Build()
        {
            Supervisor = new CaptureSupervisor(
                sourceCount: Sources.Count,
                restartInPlace: (i, _) => Activate(i),
                switchTo: (i, _) => Activate(i),
                onAllFailed: r => LastAllFailedReason = r);
            Activate(0);
        }

        private void Activate(int index)
        {
            if (_active >= 0)
            {
                Sources[_active].OnFrame -= _activeHandler;
                Sources[_active].Stop();
            }
            _active = index;
            _activeHandler = (buf, count) =>
            {
                var copy = new float[count];
                Array.Copy(buf, copy, count);
                Forwarded.Add(copy);
            };
            Sources[index].OnFrame += _activeHandler;
            Sources[index].Start(DeviceName);
        }
    }

    private static void PumpDead(CaptureSupervisor sup, int windows)
    {
        for (var i = 0; i < windows; i++)
            sup.OnStatsWindow(0, unmutedAndCapturing: true);
    }

    [Fact]
    public void HealthyTopSourceStaysAndForwardsFrames()
    {
        var rack = new SourceRack(3, "Default Device");
        rack.Build();

        rack.Active.Emit(new float[] { 0.1f, 0.2f }, 2);
        rack.Supervisor.OnStatsWindow(960, unmutedAndCapturing: true);

        Xunit.Assert.Equal(0, rack.Supervisor.ActiveIndex);
        Xunit.Assert.Equal(1, rack.Sources[0].StartCount);
        Xunit.Assert.Equal(0, rack.Sources[0].StopCount);
        Xunit.Assert.Single(rack.Forwarded);
        Xunit.Assert.Equal(2, rack.Forwarded[0].Length);
    }

    [Fact]
    public void DeadTopSourceRestartsInPlaceThenSwitchesToNext()
    {
        var rack = new SourceRack(3, "Default Device");
        rack.Build();

        PumpDead(rack.Supervisor, 3);
        Xunit.Assert.Equal(0, rack.Supervisor.ActiveIndex);
        Xunit.Assert.Equal(2, rack.Sources[0].StartCount);

        PumpDead(rack.Supervisor, 9);
        Xunit.Assert.Equal(1, rack.Supervisor.ActiveIndex);
        Xunit.Assert.Equal(4, rack.Sources[0].StartCount);
        Xunit.Assert.Equal(4, rack.Sources[0].StopCount);
        Xunit.Assert.Equal(1, rack.Sources[1].StartCount);
    }

    [Fact]
    public void SwitchStopsOldExactlyOnceAndStartsNewExactlyOnce()
    {
        var rack = new SourceRack(3, "Default Device");
        rack.Build();

        rack.Supervisor.OnStatsWindow(0, unmutedAndCapturing: false);
        var startsBeforeSwitch1 = rack.Sources[1].StartCount;
        var stopsBeforeSwitch0 = rack.Sources[0].StopCount;

        PumpDead(rack.Supervisor, 12);

        Xunit.Assert.Equal(1, rack.Supervisor.ActiveIndex);
        Xunit.Assert.Equal(startsBeforeSwitch1 + 1, rack.Sources[1].StartCount);
        Xunit.Assert.True(rack.Sources[0].StopCount > stopsBeforeSwitch0);
        Xunit.Assert.False(rack.Sources[0].Running);
        Xunit.Assert.True(rack.Sources[1].Running);
    }

    [Fact]
    public void FramesComeFromNewActiveSourceAfterSwitch()
    {
        var rack = new SourceRack(3, "Default Device");
        rack.Build();

        PumpDead(rack.Supervisor, 12);
        Xunit.Assert.Equal(1, rack.Supervisor.ActiveIndex);

        rack.Forwarded.Clear();
        rack.Sources[0].Emit(new float[] { 9f }, 1);
        Xunit.Assert.Empty(rack.Forwarded);

        rack.Sources[1].Emit(new float[] { 0.5f, 0.6f, 0.7f }, 3);
        Xunit.Assert.Single(rack.Forwarded);
        Xunit.Assert.Equal(3, rack.Forwarded[0].Length);
    }

    [Fact]
    public void AllSourcesDeadRaisesAllFailed()
    {
        var rack = new SourceRack(1, "Default Device");
        rack.Build();

        PumpDead(rack.Supervisor, 30);

        Xunit.Assert.True(rack.Supervisor.IsAllFailed);
        Xunit.Assert.False(string.IsNullOrEmpty(rack.LastAllFailedReason));
    }

    [Fact]
    public void RecoveredHigherPrioritySourceIsPromotedOnBoundary()
    {
        var rack = new SourceRack(3, "Default Device");
        rack.Build();

        PumpDead(rack.Supervisor, 12);
        Xunit.Assert.Equal(1, rack.Supervisor.ActiveIndex);

        var source0StartsBefore = rack.Sources[0].StartCount;
        rack.Supervisor.OnBoundary();

        Xunit.Assert.Equal(0, rack.Supervisor.ActiveIndex);
        Xunit.Assert.Equal(source0StartsBefore + 1, rack.Sources[0].StartCount);
        Xunit.Assert.True(rack.Sources[0].Running);
        Xunit.Assert.False(rack.Sources[1].Running);
    }

    [Fact]
    public void ActiveSourceReceivesDeviceName()
    {
        var rack = new SourceRack(3, "My Headset");
        rack.Build();

        Xunit.Assert.Equal("My Headset", rack.Sources[0].LastDeviceId);

        PumpDead(rack.Supervisor, 12);
        Xunit.Assert.Equal("My Headset", rack.Sources[1].LastDeviceId);
    }
}
