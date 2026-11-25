using ListenTogether.Core.Sessions;

namespace ListenTogether.Tests.Sessions;

public sealed class PeerCandidateTests
{
    [Theory]
    [InlineData("192.168.1.20", 45000, PeerCandidateType.Host)]
    [InlineData("203.0.113.10", 3478, PeerCandidateType.ServerReflexive)]
    [InlineData("2001:db8::10", 50000, PeerCandidateType.Host)]
    [InlineData("2001:db8::20", 3479, PeerCandidateType.Relay)]
    public void IsValid_AcceptsIpLiteralAndValidPort(
        string address,
        int port,
        PeerCandidateType type)
    {
        var candidate = new PeerCandidate(address, port, type);

        Assert.True(candidate.IsValid);
    }

    [Theory]
    [InlineData("not-an-ip", 45000)]
    [InlineData("example.com", 45000)]
    [InlineData("", 45000)]
    [InlineData("192.168.1.20", 0)]
    [InlineData("192.168.1.20", 65536)]
    public void IsValid_RejectsInvalidAddressOrPort(string address, int port)
    {
        var candidate = new PeerCandidate(address, port, PeerCandidateType.Host);

        Assert.False(candidate.IsValid);
    }

    [Fact]
    public void IsValid_RejectsUndefinedCandidateType()
    {
        var candidate = new PeerCandidate("192.168.1.20", 45000, (PeerCandidateType)999);

        Assert.False(candidate.IsValid);
    }
}
