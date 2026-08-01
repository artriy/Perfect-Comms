using UnityEngine;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceAudioOcclusionGeometryTests
{
    [Fact]
    public void SameSidePlayersNearDoorDoNotCrossBarrier()
    {
        Assert.False(Intersects(
            playerA: Vector(0.4f, -0.5f),
            playerB: Vector(0.4f, 0.5f),
            doorA: Vector(0f, -1.3f),
            doorB: Vector(0f, 1.3f)));

        Assert.False(Intersects(
            playerA: Vector(-0.5f, 0.4f),
            playerB: Vector(0.5f, 0.4f),
            doorA: Vector(-1.3f, 0f),
            doorB: Vector(1.3f, 0f)));
    }

    [Fact]
    public void PlayersOnOppositeSidesCrossFiniteBarrier()
    {
        Assert.True(Intersects(
            playerA: Vector(-1f, 0f),
            playerB: Vector(1f, 0f),
            doorA: Vector(0f, -1.3f),
            doorB: Vector(0f, 1.3f)));

        Assert.True(Intersects(
            playerA: Vector(-1f, 1f),
            playerB: Vector(1f, 1f),
            doorA: Vector(0f, -1.3f),
            doorB: Vector(0f, 1.3f)));
    }

    [Fact]
    public void CrossingBeyondDoorEndDoesNotIntersect()
    {
        Assert.False(Intersects(
            playerA: Vector(-1f, 1.5f),
            playerB: Vector(1f, 1.5f),
            doorA: Vector(0f, -1.3f),
            doorB: Vector(0f, 1.3f)));
    }

    [Fact]
    public void RotatedDoorUsesAuthoredBarrierDirection()
    {
        Assert.True(Intersects(
            playerA: Vector(0f, 1f),
            playerB: Vector(1f, 0f),
            doorA: Vector(0f, 0f),
            doorB: Vector(1f, 1f)));

        Assert.False(Intersects(
            playerA: Vector(0.1f, 0.4f),
            playerB: Vector(0.4f, 0.7f),
            doorA: Vector(0f, 0f),
            doorB: Vector(1f, 1f)));
    }

    [Fact]
    public void TouchingAndCollinearOverlapCountAsBlocked()
    {
        Assert.True(Intersects(
            playerA: Vector(-1f, 0f),
            playerB: Vector(0f, 0f),
            doorA: Vector(0f, 0f),
            doorB: Vector(0f, 1f)));

        Assert.True(Intersects(
            playerA: Vector(0f, -0.5f),
            playerB: Vector(0f, 0.5f),
            doorA: Vector(0f, 0f),
            doorB: Vector(0f, 1f)));
    }

    [Fact]
    public void DegeneratePlayerSegmentDoesNotCreateDoorOcclusion()
    {
        Assert.False(Intersects(
            playerA: Vector(0f, 0f),
            playerB: Vector(0f, 0f),
            doorA: Vector(0f, -1f),
            doorB: Vector(0f, 1f)));
    }

    private static bool Intersects(Vector2 playerA, Vector2 playerB, Vector2 doorA, Vector2 doorB)
        => VoiceAudioOcclusion.SegmentsIntersect(playerA, playerB, doorA, doorB);

    private static Vector2 Vector(float x, float y)
    {
        var value = default(Vector2);
        value.x = x;
        value.y = y;
        return value;
    }
}
