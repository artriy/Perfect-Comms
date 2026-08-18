using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class PlayerVoteAreaPlayerIdTests
{
    [Fact]
    public void ReadsLegacyInteropProperty()
    {
        var reader = PlayerVoteAreaPlayerId.CreateReader(typeof(LegacyInteropVoteArea), out var memberName);

        Assert.NotNull(reader);
        Assert.Equal("TargetPlayerId", memberName);
        Assert.Equal((byte)7, reader(new LegacyInteropVoteArea(7)));
    }

    [Fact]
    public void ReadsLegacyDummyField()
    {
        var reader = PlayerVoteAreaPlayerId.CreateReader(typeof(LegacyDummyVoteArea), out var memberName);

        Assert.NotNull(reader);
        Assert.Equal("TargetPlayerId", memberName);
        Assert.Equal((byte)8, reader(new LegacyDummyVoteArea { TargetPlayerId = 8 }));
    }

    [Fact]
    public void ReadsModernPlayerIdThroughImplicitConversion()
    {
        var reader = PlayerVoteAreaPlayerId.CreateReader(typeof(ModernVoteArea), out var memberName);

        Assert.NotNull(reader);
        Assert.Equal("PlayerId", memberName);
        Assert.Equal((byte)9, reader(new ModernVoteArea(new ModernPlayerId(9))));
    }

    [Fact]
    public void ReadsModernPlayerIdValueFieldWithoutOperator()
    {
        var reader = PlayerVoteAreaPlayerId.CreateReader(typeof(ModernValueFieldVoteArea), out var memberName);

        Assert.NotNull(reader);
        Assert.Equal("PlayerId", memberName);
        Assert.Equal((byte)10, reader(new ModernValueFieldVoteArea(new ModernValueFieldPlayerId(10))));
    }

    [Fact]
    public void PrefersModernMemberWhenBothShapesExist()
    {
        var reader = PlayerVoteAreaPlayerId.CreateReader(typeof(TransitionalVoteArea), out var memberName);

        Assert.NotNull(reader);
        Assert.Equal("PlayerId", memberName);
        Assert.Equal((byte)11, reader(new TransitionalVoteArea()));
    }

    [Fact]
    public void RejectsUnknownIdentifierShape()
    {
        var reader = PlayerVoteAreaPlayerId.CreateReader(typeof(UnsupportedVoteArea), out var memberName);

        Assert.Null(reader);
        Assert.Null(memberName);
    }

    [Fact]
    public void ResolvesReferencedGameInteropMember()
    {
        var expectedMemberName = typeof(PlayerVoteArea).GetProperty("PlayerId") == null
            ? "TargetPlayerId"
            : "PlayerId";

        var reader = PlayerVoteAreaPlayerId.CreateReader(typeof(PlayerVoteArea), out var memberName);

        Assert.NotNull(reader);
        Assert.Equal(expectedMemberName, memberName);
    }

    private sealed class LegacyInteropVoteArea(byte playerId)
    {
        public byte TargetPlayerId { get; } = playerId;
    }

    private sealed class LegacyDummyVoteArea
    {
        public byte TargetPlayerId;
    }

    private readonly struct ModernPlayerId(byte value)
    {
        public readonly byte Value = value;

        public static implicit operator byte(ModernPlayerId playerId) => playerId.Value;
    }

    private sealed class ModernVoteArea(ModernPlayerId playerId)
    {
        public ModernPlayerId PlayerId { get; } = playerId;
    }

    private readonly struct ModernValueFieldPlayerId(byte value)
    {
        public readonly byte Value = value;
    }

    private sealed class ModernValueFieldVoteArea(ModernValueFieldPlayerId playerId)
    {
        public ModernValueFieldPlayerId PlayerId { get; } = playerId;
    }

    private sealed class TransitionalVoteArea
    {
        public ModernPlayerId PlayerId { get; } = new(11);
        public byte TargetPlayerId { get; } = 12;
    }

    private sealed class UnsupportedVoteArea
    {
        public int PlayerId { get; } = 13;
    }
}
