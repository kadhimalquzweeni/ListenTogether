using System.Security.Cryptography;
using ListenTogether.Core.Sessions;
using ListenTogether.Infrastructure.Networking;

namespace ListenTogether.Tests.Networking;

public sealed class PeerKeyAgreementTests
{
    private static readonly Guid SessionId = Guid.Parse("e4d97d4e-37c1-4fb6-b90d-fb41ad49a2f1");

    [Fact]
    public void Complete_BothPeersDeriveMatchingOppositeDirectionalKeys()
    {
        using var host = PeerKeyAgreement.Create(SessionId, "host-connection", "listener-connection");
        using var listener = PeerKeyAgreement.Create(SessionId, "listener-connection", "host-connection");

        using var hostResult = host.Complete(listener.LocalOffer);
        using var listenerResult = listener.Complete(host.LocalOffer);

        Assert.True(hostResult.VerifyPeerProof(listenerResult.LocalProof));
        Assert.True(listenerResult.VerifyPeerProof(hostResult.LocalProof));
        Assert.Equal(hostResult.SendKey, listenerResult.ReceiveKey);
        Assert.Equal(hostResult.SendNoncePrefix, listenerResult.ReceiveNoncePrefix);
        Assert.Equal(hostResult.ReceiveKey, listenerResult.SendKey);
        Assert.Equal(hostResult.ReceiveNoncePrefix, listenerResult.SendNoncePrefix);
        Assert.Equal(hostResult.TranscriptHash, listenerResult.TranscriptHash);
    }

    [Fact]
    public void Complete_DerivesFreshKeysForEachAgreement()
    {
        using var host1 = PeerKeyAgreement.Create(SessionId, "host", "listener");
        using var listener1 = PeerKeyAgreement.Create(SessionId, "listener", "host");
        using var host2 = PeerKeyAgreement.Create(SessionId, "host", "listener");
        using var listener2 = PeerKeyAgreement.Create(SessionId, "listener", "host");

        using var hostResult1 = host1.Complete(listener1.LocalOffer);
        using var listenerResult1 = listener1.Complete(host1.LocalOffer);
        using var hostResult2 = host2.Complete(listener2.LocalOffer);

        Assert.False(hostResult1.SendKey.SequenceEqual(hostResult2.SendKey));
        Assert.True(hostResult1.VerifyPeerProof(listenerResult1.LocalProof));
    }

    [Fact]
    public void Complete_ThreeParticipantsHaveIndependentPairwiseKeys()
    {
        using var hostListenerA = PeerKeyAgreement.Create(SessionId, "host", "listener-a");
        using var listenerAHost = PeerKeyAgreement.Create(SessionId, "listener-a", "host");
        using var hostListenerB = PeerKeyAgreement.Create(SessionId, "host", "listener-b");
        using var listenerBHost = PeerKeyAgreement.Create(SessionId, "listener-b", "host");

        using var hostAResult = hostListenerA.Complete(listenerAHost.LocalOffer);
        using var listenerAResult = listenerAHost.Complete(hostListenerA.LocalOffer);
        using var hostBResult = hostListenerB.Complete(listenerBHost.LocalOffer);
        using var listenerBResult = listenerBHost.Complete(hostListenerB.LocalOffer);

        Assert.True(hostAResult.VerifyPeerProof(listenerAResult.LocalProof));
        Assert.True(hostBResult.VerifyPeerProof(listenerBResult.LocalProof));
        Assert.False(hostAResult.SendKey.SequenceEqual(hostBResult.SendKey));
        Assert.False(hostAResult.TranscriptHash.SequenceEqual(hostBResult.TranscriptHash));
    }

    [Fact]
    public void Complete_RejectsOfferForDifferentSession()
    {
        using var host = PeerKeyAgreement.Create(SessionId, "host", "listener");
        using var other = PeerKeyAgreement.Create(Guid.NewGuid(), "listener", "host");

        Assert.Throws<CryptographicException>(() => host.Complete(other.LocalOffer));
    }

    [Fact]
    public void Complete_RejectsOfferForDifferentConnectionPair()
    {
        using var host = PeerKeyAgreement.Create(SessionId, "host", "listener");
        using var attacker = PeerKeyAgreement.Create(SessionId, "attacker", "host");

        Assert.Throws<CryptographicException>(() => host.Complete(attacker.LocalOffer));
    }

    [Fact]
    public void Complete_RejectsMalformedPublicKey()
    {
        using var host = PeerKeyAgreement.Create(SessionId, "host", "listener");
        using var listener = PeerKeyAgreement.Create(SessionId, "listener", "host");
        var malformed = listener.LocalOffer with { PublicKey = [1, 2, 3] };

        Assert.Throws<CryptographicException>(() => host.Complete(malformed));
    }

    [Fact]
    public void PeerProof_RejectsModifiedProof()
    {
        using var host = PeerKeyAgreement.Create(SessionId, "host", "listener");
        using var listener = PeerKeyAgreement.Create(SessionId, "listener", "host");
        using var hostResult = host.Complete(listener.LocalOffer);
        using var listenerResult = listener.Complete(host.LocalOffer);

        var modified = listenerResult.LocalProof.ToArray();
        modified[0] ^= 0x01;

        Assert.False(hostResult.VerifyPeerProof(modified));
    }

    [Fact]
    public void Complete_CanOnlyBeCalledOnce()
    {
        using var host = PeerKeyAgreement.Create(SessionId, "host", "listener");
        using var listener = PeerKeyAgreement.Create(SessionId, "listener", "host");
        using var result = host.Complete(listener.LocalOffer);

        Assert.Throws<InvalidOperationException>(() => host.Complete(listener.LocalOffer));
    }

    [Fact]
    public void Create_RejectsEmptyOrIdenticalConnectionIds()
    {
        Assert.Throws<ArgumentException>(() => PeerKeyAgreement.Create(SessionId, "", "listener"));
        Assert.Throws<ArgumentException>(() => PeerKeyAgreement.Create(SessionId, "same", "same"));
    }
}
