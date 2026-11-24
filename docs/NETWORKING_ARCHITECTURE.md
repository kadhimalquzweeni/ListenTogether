# Networking architecture

ListenTogether uses a signalling service to coordinate sessions and attempts a direct UDP media path between participants. The current desktop MVP still sends audio through SignalR; direct UDP media is being introduced in independently testable stages.

## Connection establishment

1. The desktop client connects to the ASP.NET Core SignalR signalling endpoint over TLS.
2. A participant gathers local IPv4 UDP host candidates and a server-reflexive candidate using the configured signalling host on UDP port 3478. STUN uses the same UDP socket that is retained for subsequent connectivity checks and media.
3. Candidates are exchanged through the session signalling channel. Existing participants republish their candidates when a new participant joins, so late joiners receive candidates from everyone.
4. Both peers still need to send authenticated connectivity checks to the other peer's candidates concurrently (UDP hole punching). That check/selection stage is not implemented yet. The selected socket must remain alive for the duration of the session.
5. If direct connectivity cannot be established, a publicly reachable UDP relay is required. STUN does not relay traffic and cannot make a private home server reachable through NAT.

## Protected UDP datagrams

The transport foundation uses AES-256-GCM authenticated encryption. Each direction must have its own independently derived 32-byte key and its own 4-byte nonce prefix. A nonce is the 4-byte prefix followed by the 64-bit big-endian packet sequence number. Sequence values must be unique and must never wrap for a given key/prefix pair; establish a fresh key before sequence exhaustion.

The authenticated header contains the LTUD magic value, protocol version, packet type, session GUID, sequence number and nonce prefix. The complete header is authenticated as additional data, so changing the session, type, sequence or prefix invalidates the authentication tag. The encrypted body is followed by a 16-byte GCM tag.

Receivers enforce a 64-packet sliding replay window. Authentication must succeed before a sequence is committed to the replay window. Datagrams larger than 1,200 bytes, malformed headers, wrong sessions, wrong nonce prefixes, invalid packet types, authentication failures and duplicate/too-old sequences are discarded.

**Key agreement foundation implemented:** `PeerKeyAgreement` creates ephemeral P-256 ECDH key pairs, exchanges public-key offers bound to a session and the two SignalR connection IDs, hashes a canonical transcript, and derives a confirmation key plus independent directional AES-256-GCM keys and nonce prefixes using HKDF-SHA256. Each side emits a transcript-bound HMAC proof; both sides must verify the other proof before constructing packet codecs. Key material is held in disposable result objects and zeroed on disposal.

**SignalR signalling integration implemented:** each created session receives a stable random GUID, returned to every participant. The hub exposes the server-assigned connection ID and relays ECDH offers and 32-byte confirmation proofs directly to a named peer only after verifying that sender and recipient are distinct active members of the same session and that the offer's session/connection fields match the server context. Offers and proofs are bounded before forwarding. The desktop SignalR client exposes session ID, connection ID, peer-offer/proof events, and corresponding send methods. `IPeerKeySignalling` isolates this protocol from SignalR so coordinator orchestration can be tested independently.

**Trust requirements:** the HMAC is key confirmation, not identity authentication: a malicious signalling server could substitute both public keys. Current trust is therefore the TLS-protected signalling service and its session membership checks. Do not enable direct UDP media until the desktop negotiation coordinator verifies both proofs and the client falls back safely to SignalR. For protection against a malicious signalling service, add an independently verifiable peer identity or user-verified fingerprint.

## Deployment constraint

A self-hosted STUN/signalling/relay service must be reachable from the public internet to support arbitrary users without router configuration. Hosting the service on a PC behind a typical home NAT is not sufficient by itself. A relay fallback requires a publicly reachable server; STUN alone is not a relay.


### Automatic per-peer key negotiation

`PeerKeyNegotiationCoordinator` maintains a separate ECDH agreement for every remote SignalR connection. It starts negotiation with existing peers on `SessionJoined`, and with later arrivals on `ParticipantJoined`. Each peer is considered secured only after the transcript hash and HMAC key-confirmation proof both verify. Proofs arriving before local offer processing are held until the agreement is ready, including the case where the proof datagram/message overtakes the remote offer on the signalling path. Peer key material is disposed and zeroed when that participant leaves, the session ends, the signalling connection closes, or the coordinator is disposed. Subscriber exceptions are isolated from protocol processing. Consumers must keep the coordinator alive for the session and must not dispose a secured result while its key material is in use.

The coordinator uses the `IPeerKeySignalling` abstraction. Automated tests exercise simultaneous offers, automatic session/participant event startup, proof-before-offer delivery, multiple independent listener keys, and participant departure cleanup.


### UDP candidate gathering and exchange

`UdpCandidateGatherer` binds one IPv4 UDP socket, gathers active local interface addresses, and asks the configured STUN endpoint for its server-reflexive mapping. `UdpPeerCandidateCoordinator` owns that candidate set for the session, publishes each candidate through SignalR, tracks remote candidates by server-assigned peer connection ID, deduplicates repeats, and republishes local candidates when another participant joins. The host and listener view models start this process after session creation/join and dispose the retained socket during teardown.

Candidate discovery is optional: if DNS/STUN discovery fails or times out, session setup continues using the existing SignalR audio relay. Candidate exchange is not yet a direct media connection; no UDP connectivity checks or UDP audio frames are sent at this stage. The current STUN implementation and gatherer are IPv4-only.
