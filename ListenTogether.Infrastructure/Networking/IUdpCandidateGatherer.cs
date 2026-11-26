using System.Net;
using ListenTogether.Core.Sessions;

namespace ListenTogether.Infrastructure.Networking;

/// <summary>Abstraction for gathering candidates on a reusable UDP socket.</summary>
public interface IUdpCandidateGatherer
{
    Task<UdpCandidateSet> GatherAsync(
        IPEndPoint stunServer,
        TimeSpan? stunTimeout = null,
        CancellationToken cancellationToken = default);
}
