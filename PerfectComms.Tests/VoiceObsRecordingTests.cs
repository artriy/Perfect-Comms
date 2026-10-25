using System.Text;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceObsRecordingTests
{
    [Theory]
    [InlineData((int)VoiceIdentityPrivacyDecision.HideSource)]
    [InlineData((int)VoiceIdentityPrivacyDecision.HideAllForViewer)]
    [InlineData((int)VoiceIdentityPrivacyDecision.DimAll)]
    public void RestrictedIdentityCannotExportItsNameOrDeathState(int decision)
    {
        var player = default(VoicePlayerSnapshot) with
        {
            PlayerId = 7, PlayerName = "Secret", IsDead = true, IsVisible = true,
        };
        var identity = VoiceObsRecordingIdentity.Resolve(
            new VoiceIdentityPrivacyResolution(7, (VoiceIdentityPrivacyDecision)decision, null), false, player, 101u);

        Assert.Equal(0u, identity.Id);
        Assert.Equal(string.Empty, identity.Name);
        Assert.Equal(0, identity.Group);
    }

    [Fact]
    public void AliasUsesOnlyPresentationIdentityAndDoesNotKeepTransportProvenance()
    {
        var alias = default(VoicePlayerSnapshot) with
        {
            PlayerId = 9, PlayerName = "Alias", IsDead = false, IsVisible = true,
        };
        var disguised = VoiceObsRecordingIdentity.Resolve(
            new VoiceIdentityPrivacyResolution(3, VoiceIdentityPrivacyDecision.Alias, 9), false, alias, 101u);
        var genuine = VoiceObsRecordingIdentity.Resolve(
            new VoiceIdentityPrivacyResolution(9, VoiceIdentityPrivacyDecision.Normal, 9), false, alias, 101u);

        Assert.Equal(genuine, disguised);
        Assert.Equal("Alias", disguised.Name);
        Assert.Equal(1, disguised.Group);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void HiddenViewerOrMissingPresentationCannotExportAnIdentity(bool hiddenViewer, bool missing)
    {
        VoicePlayerSnapshot? player = missing ? null : default(VoicePlayerSnapshot) with
        {
            PlayerId = 7, PlayerName = "Secret", IsDead = true, IsVisible = true,
        };
        var identity = VoiceObsRecordingIdentity.Resolve(
            new VoiceIdentityPrivacyResolution(7, VoiceIdentityPrivacyDecision.Normal, 7), hiddenViewer, player, 101u);

        Assert.Equal(0u, identity.Id);
        Assert.Equal(string.Empty, identity.Name);
        Assert.Equal(0, identity.Group);
    }

    [Fact]
    public void IdentityNameIsPlainBoundedUtf8WithoutBrokenSurrogates()
    {
        var player = default(VoicePlayerSnapshot) with
        {
            PlayerId = 7,
            PlayerName = "<color=red>Speaker</color>\r\n" + string.Concat(Enumerable.Repeat("\U0001F680", 70)),
            IsVisible = true,
        };
        var identity = VoiceObsRecordingIdentity.Resolve(
            new VoiceIdentityPrivacyResolution(7, VoiceIdentityPrivacyDecision.Normal, 7), false, player, 101u);

        Assert.DoesNotContain('<', identity.Name);
        Assert.DoesNotContain('>', identity.Name);
        Assert.DoesNotContain('\r', identity.Name);
        Assert.DoesNotContain('\n', identity.Name);
        Assert.InRange(Encoding.UTF8.GetByteCount(identity.Name), 1, 192);
        Assert.DoesNotContain('\uFFFD', Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(identity.Name)));
    }

    [Theory]
    [InlineData(false, false, false, false, true)]
    [InlineData(true, false, true, false, true)]
    [InlineData(false, true, true, false, true)]
    [InlineData(false, false, false, true, false)]
    public void UnavailablePresentationIsPrivateButInvisibleGhostRemainsIdentifiable(
        bool disconnected, bool dummy, bool visible, bool dead, bool privateIdentity)
    {
        var player = default(VoicePlayerSnapshot) with
        {
            PlayerId = 7, PlayerName = "Speaker", Disconnected = disconnected,
            IsDummy = dummy, IsVisible = visible, IsDead = dead,
        };
        var identity = VoiceObsRecordingIdentity.Resolve(
            new VoiceIdentityPrivacyResolution(7, VoiceIdentityPrivacyDecision.Normal, 7), false, player, 101u);
        Assert.Equal(privateIdentity ? 0u : 101u, identity.Id);
        Assert.Equal(privateIdentity ? string.Empty : "Speaker", identity.Name);
        Assert.Equal(privateIdentity ? (byte)0 : (byte)2, identity.Group);
    }

    [Theory]
    [InlineData((int)VoiceIdentityPrivacyDecision.HideAllForViewer, false)]
    [InlineData((int)VoiceIdentityPrivacyDecision.HideAllForViewer, true)]
    [InlineData((int)VoiceIdentityPrivacyDecision.DimAll, false)]
    [InlineData((int)VoiceIdentityPrivacyDecision.DimAll, true)]
    public void SourceWideRestrictionAnonymizesEveryRouteBeforeAnyMetadataIsAssigned(int decision, bool firstSource)
    {
        var first = Player(7, 70, "First");
        var second = Player(9, 90, "Second");
        var snapshot = Snapshot(first, second);
        var projection = new VoiceObsRecordingProjection();
        projection.UpdateParticipants(123, snapshot.Players, rosterComplete: true);
        var sources = new Dictionary<string, VoicePlayerSnapshot> { ["first"] = first, ["second"] = second };
        var routes = new List<SidecarProtocol.GameStatePeerInput>
        {
            new("first", 0.5f, -0.3f, 2, true, 10, "Old first", 1),
            new("second", 0.75f, 0.3f, 0, false, 11, "Old second", 2),
        };
        byte restricted = firstSource ? first.PlayerId : second.PlayerId;
        var resolved = new List<byte>();
        projection.Apply(routes, sources, snapshot, false, (id, _) =>
        {
            resolved.Add(id);
            return id == restricted
                ? new VoiceIdentityPrivacyResolution(id, (VoiceIdentityPrivacyDecision)decision, null)
                : new VoiceIdentityPrivacyResolution(id, VoiceIdentityPrivacyDecision.Normal, id);
        });

        Assert.Equal(new byte[] { 7, 9 }, resolved);
        Assert.All(routes, route =>
        {
            Assert.Equal(0u, route.RecordingId);
            Assert.Equal(string.Empty, route.RecordingName);
            Assert.Equal(0, route.RecordingGroup);
        });
        Assert.Equal((0.5f, -0.3f, 2, true), (routes[0].Gain, routes[0].Pan, routes[0].Mode, routes[0].Muffled));
        Assert.Equal((0.75f, 0.3f, 0, false), (routes[1].Gain, routes[1].Pan, routes[1].Mode, routes[1].Muffled));
    }

    [Fact]
    public void PresentationTokensSurviveDeathButNeverReuseDepartedReplacedRenamedOrPriorSessionIdentity()
    {
        var projection = new VoiceObsRecordingProjection();
        var player = Player(7, 70, "Original");
        projection.UpdateParticipants(123, new[] { player }, rosterComplete: true);
        uint original = Project(projection, player).RecordingId;
        Assert.NotEqual(0u, original);
        projection.UpdateParticipants(123, new[] { player }, rosterComplete: true);
        Assert.Equal(original, Project(projection, player).RecordingId);
        player = player with { IsDead = true };
        projection.UpdateParticipants(123, new[] { player }, rosterComplete: true);
        var dead = Project(projection, player);
        Assert.Equal(original, dead.RecordingId);
        Assert.Equal(2, dead.RecordingGroup);

        player = player with { ClientId = 71, IsDead = false };
        projection.UpdateParticipants(123, new[] { player }, rosterComplete: true);
        uint replaced = Project(projection, player).RecordingId;
        Assert.NotEqual(original, replaced);
        projection.UpdateParticipants(123, Array.Empty<VoicePlayerSnapshot>(), rosterComplete: true);
        projection.UpdateParticipants(123, new[] { player }, rosterComplete: true);
        uint rejoined = Project(projection, player).RecordingId;
        Assert.NotEqual(replaced, rejoined);
        player = player with { PlayerName = "Renamed" };
        projection.UpdateParticipants(123, new[] { player }, rosterComplete: true);
        var renamed = Project(projection, player);
        Assert.NotEqual(rejoined, renamed.RecordingId);
        Assert.Equal("Renamed", renamed.RecordingName);
        projection.UpdateParticipants(124, new[] { player }, rosterComplete: true);
        Assert.NotEqual(renamed.RecordingId, Project(projection, player).RecordingId);
    }

    [Fact]
    public void TemporaryInvisibilityAndSuppressedSnapshotsDoNotReplaceParticipantToken()
    {
        var projection = new VoiceObsRecordingProjection();
        var player = Player(7, 70, "Speaker");
        projection.UpdateParticipants(123, new[] { player }, rosterComplete: true);
        uint token = Project(projection, player).RecordingId;
        Assert.NotEqual(0u, token);

        var hidden = player with { IsVisible = false };
        projection.UpdateParticipants(123, new[] { hidden }, rosterComplete: true);
        var privateRoute = Project(projection, hidden);
        Assert.Equal(0u, privateRoute.RecordingId);
        Assert.Equal(string.Empty, privateRoute.RecordingName);
        Assert.Equal(0, privateRoute.RecordingGroup);
        projection.UpdateParticipants(123, null, rosterComplete: false);
        projection.UpdateParticipants(123, new[] { player }, rosterComplete: true);
        Assert.Equal(token, Project(projection, player).RecordingId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void PartialOrRetainedRosterDoesNotRetireMissingParticipant(bool enumerationComplete, bool retained)
    {
        var projection = new VoiceObsRecordingProjection();
        var player = Player(7, 70, "Speaker");
        projection.UpdateParticipants(123, new[] { player }, rosterComplete: true);
        uint token = Project(projection, player).RecordingId;
        var incomplete = Snapshot() with
        {
            PlayerEnumerationCompleted = enumerationComplete,
            RoutingRosterRetained = retained,
            AuthenticatedRosterAvailable = true,
        };
        projection.UpdateParticipants(123, incomplete.Players,
            incomplete.PlayerEnumerationCompleted && !incomplete.RoutingRosterRetained && incomplete.AuthenticatedRosterAvailable);
        projection.UpdateParticipants(123, new[] { player }, rosterComplete: true);
        Assert.Equal(token, Project(projection, player).RecordingId);

        projection.UpdateParticipants(123, Array.Empty<VoicePlayerSnapshot>(), rosterComplete: true);
        projection.UpdateParticipants(123, new[] { player }, rosterComplete: true);
        Assert.NotEqual(token, Project(projection, player).RecordingId);
    }

    [Fact]
    public void AliasAndActualTargetShareOnlyCurrentParticipantsOpaqueToken()
    {
        var source = Player(7, 70, "Source");
        var target = Player(9, 90, "Target");
        var projection = new VoiceObsRecordingProjection();
        var sources = new Dictionary<string, VoicePlayerSnapshot> { ["source"] = source, ["target"] = target };
        var snapshot = Snapshot(source, target);
        projection.UpdateParticipants(123, snapshot.Players, rosterComplete: true);
        var routes = new List<SidecarProtocol.GameStatePeerInput>
        {
            new("source", 1f, 0f, 0, false), new("target", 1f, 0f, 0, false),
        };
        VoiceIdentityPrivacyResolution Resolve(byte id, VoiceGamePhase _)
            => new(id, id == source.PlayerId ? VoiceIdentityPrivacyDecision.Alias : VoiceIdentityPrivacyDecision.Normal,
                target.PlayerId);
        projection.Apply(routes, sources, snapshot, false, Resolve);
        uint previous = routes[0].RecordingId;
        Assert.NotEqual(0u, previous);
        Assert.Equal(previous, routes[1].RecordingId);
        Assert.All(routes, route => Assert.Equal("Target", route.RecordingName));

        target = target with { ClientId = 91, PlayerName = "Replacement" };
        sources["target"] = target;
        snapshot = Snapshot(source, target);
        projection.UpdateParticipants(123, snapshot.Players, rosterComplete: true);
        projection.Apply(routes, sources, snapshot, false, Resolve);
        Assert.NotEqual(previous, routes[0].RecordingId);
        Assert.Equal(routes[0].RecordingId, routes[1].RecordingId);
        Assert.All(routes, route => Assert.Equal("Replacement", route.RecordingName));
    }

    private static VoicePlayerSnapshot Player(byte playerId, int clientId, string name)
        => default(VoicePlayerSnapshot) with
        {
            PlayerId = playerId, ClientId = clientId, PlayerName = name, IsVisible = true,
        };

    private static SidecarProtocol.GameStatePeerInput Project(VoiceObsRecordingProjection projection, VoicePlayerSnapshot player)
    {
        var routes = new List<SidecarProtocol.GameStatePeerInput> { new("peer", 1f, 0f, 0, false) };
        var sources = new Dictionary<string, VoicePlayerSnapshot> { ["peer"] = player };
        projection.Apply(routes, sources, Snapshot(player), false,
            static (id, _) => new VoiceIdentityPrivacyResolution(id, VoiceIdentityPrivacyDecision.Normal, id));
        return Assert.Single(routes);
    }

    private static VoiceGameStateSnapshot Snapshot(params VoicePlayerSnapshot[] players)
        => new(VoiceGamePhase.Meeting, 0, 0, 0, byte.MaxValue, null, 2f, false, -1, null,
            players, false, true, 0, 0);

    [Fact]
    public void DeathChangesGroupWithoutChangingThePlayersRecordingIdentity()
    {
        var player = default(VoicePlayerSnapshot) with { PlayerId = 4, PlayerName = "Speaker", IsVisible = true };
        var resolution = new VoiceIdentityPrivacyResolution(4, VoiceIdentityPrivacyDecision.Normal, 4);
        var alive = VoiceObsRecordingIdentity.Resolve(resolution, false, player, 101u);
        var dead = VoiceObsRecordingIdentity.Resolve(resolution, false, player with { IsDead = true }, 101u);

        Assert.Equal(alive.Id, dead.Id);
        Assert.Equal(alive.Name, dead.Name);
        Assert.Equal(1, alive.Group);
        Assert.Equal(2, dead.Group);
    }
}
