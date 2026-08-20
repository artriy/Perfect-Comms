using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceChatHudGeometryTests
{
    [Fact]
    public void FullViewportGeometryRemainsUnchanged()
    {
        VoiceHudRect viewport = VoiceChatHudState.CameraRelativeSafeViewportRect(
            new VoiceHudRect(0f, 0f, 1920f, 1080f),
            new VoiceHudRect(0f, 0f, 1920f, 1080f));

        AssertRect(viewport, 0f, 0f, 1f, 1f);
    }

    [Fact]
    public void FullScreenCameraNormalizesAsymmetricHorizontalCutouts()
    {
        VoiceHudRect viewport = VoiceChatHudState.CameraRelativeSafeViewportRect(
            new VoiceHudRect(100f, 0f, 2200f, 1080f),
            new VoiceHudRect(0f, 0f, 2400f, 1080f));

        AssertRect(viewport, 1f / 24f, 0f, 11f / 12f, 1f);
    }

    [Fact]
    public void CroppedCameraUsesItsOwnPixelCoordinateSpace()
    {
        VoiceHudRect containedCamera = VoiceChatHudState.CameraRelativeSafeViewportRect(
            new VoiceHudRect(100f, 0f, 2200f, 1080f),
            new VoiceHudRect(200f, 0f, 2000f, 1080f));
        VoiceHudRect partiallyContainedCamera = VoiceChatHudState.CameraRelativeSafeViewportRect(
            new VoiceHudRect(100f, 0f, 2200f, 1080f),
            new VoiceHudRect(0f, 0f, 2000f, 1080f));

        AssertRect(containedCamera, 0f, 0f, 1f, 1f);
        AssertRect(partiallyContainedCamera, 0.05f, 0f, 0.95f, 1f);
    }

    [Fact]
    public void CroppedCameraIntersectsAsymmetricVerticalCutout()
    {
        VoiceHudRect viewport = VoiceChatHudState.CameraRelativeSafeViewportRect(
            new VoiceHudRect(100f, 180f, 2100f, 700f),
            new VoiceHudRect(200f, 100f, 1800f, 800f));

        AssertRect(viewport, 0f, 0.1f, 1f, 0.875f);
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 1080f, 0f, 0f, 1920f, 1080f)]
    [InlineData(2200f, 0f, 100f, 1080f, 0f, 0f, 1920f, 1080f)]
    [InlineData(0f, 0f, 1920f, 1080f, 0f, 0f, 0f, 1080f)]
    public void DegenerateOrEmptyIntersectionFallsBackToFullCameraViewport(
        float safeX,
        float safeY,
        float safeWidth,
        float safeHeight,
        float cameraX,
        float cameraY,
        float cameraWidth,
        float cameraHeight)
    {
        VoiceHudRect viewport = VoiceChatHudState.CameraRelativeSafeViewportRect(
            new VoiceHudRect(safeX, safeY, safeWidth, safeHeight),
            new VoiceHudRect(cameraX, cameraY, cameraWidth, cameraHeight));

        AssertRect(viewport, 0f, 0f, 1f, 1f);
    }

    [Fact]
    public void ToastAnchorIsRelativeToSafeViewportAndItsBoundsClampInside()
    {
        var safe = new VoiceHudRect(0.1f, 0.05f, 0.8f, 0.85f);
        VoiceHudPoint anchor = VoiceChatHudState.SafeViewportAnchor(safe, 0.5f, 0.84f);
        var toastBounds = new VoiceHudRect(0.02f, 0.82f, 0.5f, 0.15f);
        VoiceHudPoint shift = VoiceChatHudState.CalculateViewportRectShift(toastBounds, safe);
        VoiceHudRect shifted = Shift(toastBounds, shift);

        Assert.Equal(0.5f, anchor.X, 5);
        Assert.Equal(0.764f, anchor.Y, 5);
        AssertContained(shifted, safe);
    }

    [Fact]
    public void MeasuredTooltipBoundsRespectAsymmetricSafePadding()
    {
        var safe = new VoiceHudRect(0.1f, 0.05f, 0.8f, 0.85f);
        var tooltipBounds = new VoiceHudRect(0.05f, 0.8f, 0.4f, 0.18f);
        VoiceHudPoint shift = VoiceChatHudState.CalculateViewportRectShift(
            tooltipBounds, safe, paddingX: 0.02f, paddingY: 0.02f);
        VoiceHudRect shifted = Shift(tooltipBounds, shift);
        var paddedSafe = new VoiceHudRect(0.12f, 0.07f, 0.76f, 0.81f);

        AssertContained(shifted, paddedSafe);
    }

    [Fact]
    public void OversizedTooltipCentersWithinAvailableSafeBounds()
    {
        var safe = new VoiceHudRect(0.1f, 0.05f, 0.8f, 0.85f);
        var tooltipBounds = new VoiceHudRect(0.1f, -0.1f, 1f, 1.1f);
        VoiceHudPoint shift = VoiceChatHudState.CalculateViewportRectShift(
            tooltipBounds, safe, paddingX: 0.02f, paddingY: 0.02f);
        VoiceHudRect shifted = Shift(tooltipBounds, shift);

        Assert.Equal(safe.CenterX, shifted.CenterX, 5);
        Assert.Equal(safe.CenterY, shifted.CenterY, 5);
    }

    [Fact]
    public void SpeakingBarBoundsShiftEntirelyInsideSafeViewport()
    {
        var safe = new VoiceHudRect(0.05f, 0.08f, 0.85f, 0.84f);
        var speakingBarBounds = new VoiceHudRect(0.76f, -0.12f, 0.32f, 0.28f);
        VoiceHudPoint shift = VoiceChatHudState.CalculateViewportRectShift(speakingBarBounds, safe);
        VoiceHudRect shifted = Shift(speakingBarBounds, shift);

        AssertContained(shifted, safe);
    }

    private static VoiceHudRect Shift(VoiceHudRect bounds, VoiceHudPoint shift)
        => new(bounds.X + shift.X, bounds.Y + shift.Y, bounds.Width, bounds.Height);

    private static void AssertContained(VoiceHudRect actual, VoiceHudRect expected)
    {
        Assert.InRange(actual.XMin, expected.XMin - 0.00001f, expected.XMax + 0.00001f);
        Assert.InRange(actual.XMax, expected.XMin - 0.00001f, expected.XMax + 0.00001f);
        Assert.InRange(actual.YMin, expected.YMin - 0.00001f, expected.YMax + 0.00001f);
        Assert.InRange(actual.YMax, expected.YMin - 0.00001f, expected.YMax + 0.00001f);
    }

    private static void AssertRect(
        VoiceHudRect actual,
        float x,
        float y,
        float width,
        float height)
    {
        Assert.Equal(x, actual.X, 5);
        Assert.Equal(y, actual.Y, 5);
        Assert.Equal(width, actual.Width, 5);
        Assert.Equal(height, actual.Height, 5);
    }
}
