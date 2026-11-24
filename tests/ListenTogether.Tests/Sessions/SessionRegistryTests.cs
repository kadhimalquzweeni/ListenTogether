using ListenTogether.Server.Sessions;

namespace ListenTogether.Tests.Sessions;

public sealed class SessionRegistryTests
{
    [Fact]
    public void CreateSession_GeneratesEightCharacterInviteCode()
    {
        var registry = new SessionRegistry();

        var invitation = registry.CreateSession("host-1", "Host");

        Assert.Equal(8, invitation.Code.Length);
        Assert.All(invitation.Code, character => Assert.Contains(character, "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"));
        Assert.True(registry.IsHost("host-1"));
    }

    [Fact]
    public void JoinSession_ReturnsHostAndParticipants()
    {
        var registry = new SessionRegistry();
        var invitation = registry.CreateSession("host-1", "Host");

        var result = registry.JoinSession("listener-1", $"  {invitation.Code.ToLowerInvariant()}  ", "Listener");

        Assert.Equal(invitation.Code, result.Invitation.Code);
        Assert.Equal("Host", result.HostDisplayName);
        Assert.Equal("Listener", result.JoinedParticipant.DisplayName);
        Assert.Equal(2, result.Participants.Count);
        Assert.Contains(result.Participants, participant => participant.DisplayName == "Host" && participant.IsHost);
        Assert.Contains(result.Participants, participant => participant.DisplayName == "Listener" && !participant.IsHost);
    }

    [Fact]
    public void JoinSession_RejectsUnknownCode()
    {
        var registry = new SessionRegistry();

        Assert.Throws<KeyNotFoundException>(() => registry.JoinSession("listener", "ABCDEFGH", "Listener"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("SHORT")]
    [InlineData("ABCD0FGH")]
    public void JoinSession_RejectsMalformedCode(string code)
    {
        var registry = new SessionRegistry();

        Assert.Throws<ArgumentException>(() => registry.JoinSession("listener", code, "Listener"));
    }

    [Fact]
    public void ConnectionCannotJoinMultipleSessions()
    {
        var registry = new SessionRegistry();
        var first = registry.CreateSession("host-1", "Host");
        registry.CreateSession("host-2", "Other host");

        Assert.Throws<InvalidOperationException>(() => registry.JoinSession("host-1", first.Code, "Host"));
    }

    [Fact]
    public void OnlyHostCanPublishAudioPackets()
    {
        var registry = new SessionRegistry();
        var invitation = registry.CreateSession("host-1", "Host");
        registry.JoinSession("listener-1", invitation.Code, "Listener");

        Assert.True(registry.TryAcceptAudioPacket("host-1", 0, 100));
        Assert.False(registry.TryAcceptAudioPacket("listener-1", 1, 100));
    }

    [Fact]
    public void AudioPacketValidation_RejectsOversizedAndRepeatedSequence()
    {
        var registry = new SessionRegistry();
        registry.CreateSession("host-1", "Host");

        Assert.False(registry.TryAcceptAudioPacket("host-1", 0, 1_276));
        Assert.True(registry.TryAcceptAudioPacket("host-1", 1, 100));
        Assert.False(registry.TryAcceptAudioPacket("host-1", 1, 100));
    }

    [Fact]
    public void JoinSession_RejectsParticipantWhenSessionIsFull()
    {
        var registry = new SessionRegistry();
        var invitation = registry.CreateSession("host", "Host");

        for (var index = 1; index < 16; index++)
            registry.JoinSession($"listener-{index}", invitation.Code, $"Listener {index}");

        Assert.Throws<InvalidOperationException>(() =>
            registry.JoinSession("listener-16", invitation.Code, "One too many"));
    }

    [Fact]
    public void CreateSession_GeneratesDifferentCodesForSeparateSessions()
    {
        var registry = new SessionRegistry();

        var first = registry.CreateSession("host-1", "Host 1");
        var second = registry.CreateSession("host-2", "Host 2");

        Assert.NotEqual(first.Code, second.Code);
    }

    [Fact]
    public void RemovingHost_EndsSessionAndDisconnectsParticipantsFromRegistry()
    {
        var registry = new SessionRegistry();
        var invitation = registry.CreateSession("host-1", "Host");
        registry.JoinSession("listener-1", invitation.Code, "Listener");

        var departure = registry.RemoveConnection("host-1");

        Assert.NotNull(departure);
        Assert.True(departure.IsHost);
        Assert.Equal(0, registry.SessionCount);
        Assert.False(registry.IsHost("listener-1"));
        Assert.Null(registry.RemoveConnection("listener-1"));
    }

    [Fact]
    public void RemovingListener_LeavesHostSessionActive()
    {
        var registry = new SessionRegistry();
        var invitation = registry.CreateSession("host-1", "Host");
        registry.JoinSession("listener-1", invitation.Code, "Listener");

        var departure = registry.RemoveConnection("listener-1");

        Assert.NotNull(departure);
        Assert.False(departure.IsHost);
        Assert.Equal(1, registry.SessionCount);
        Assert.True(registry.IsHost("host-1"));
    }
    [Fact]
    public void TryAcceptAudioPacketBatch_AcceptsOrderedBatchAndRejectsReplay()
    {
        var registry = new SessionRegistry();
        registry.CreateSession("host", "Host");

        Assert.True(registry.TryAcceptAudioPacketBatch(
            "host", new long[] { 1, 2, 3, 4, 5 }, new int[] { 40, 41, 42, 43, 44 }));
        Assert.False(registry.TryAcceptAudioPacketBatch(
            "host", new long[] { 5, 6 }, new int[] { 40, 40 }));
        Assert.False(registry.TryAcceptAudioPacketBatch(
            "host", new long[] { 7, 6 }, new int[] { 40, 40 }));
        Assert.False(registry.TryAcceptAudioPacketBatch(
            "host", new long[] { 6 }, new int[] { 1_276 }));
        Assert.False(registry.TryAcceptAudioPacketBatch(
            "listener", new long[] { 1 }, new int[] { 40 }));
    }

}
