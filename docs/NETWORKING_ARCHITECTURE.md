# Networking architecture

ListenTogether uses SignalR only for session signalling and encrypted peer-key negotiation. Audio media is sent exclusively over authenticated, encrypted UDP directly between participants. There is no TURN service, audio relay, or server-side media fallback.

## Connection establishment

1. Each desktop connects to the ASP.NET Core SignalR endpoint over HTTP/WebSockets (HTTPS/WSS for internet use).
2. The participant binds an IPv4 UDP socket and gathers local host candidates plus a server-reflexive candidate using the configured signalling host on UDP port 3478. STUN uses the same socket retained for connectivity checks and media.
3. Participants exchange candidates through SignalR. Existing participants republish their candidates when another participant joins. Candidate publication is retried when a transient signalling error occurs.
4. Peers negotiate independent P-256 ECDH keys and verify transcript-bound HMAC proofs through the signalling channel.
5. Once keys are confirmed, peers probe each other's candidates concurrently using authenticated UDP connectivity checks. The first authenticated endpoint is selected. Keepalives maintain NAT mappings; stale paths trigger new candidate checks and retry attempts.
6. Audio is encoded as 20 ms Opus frames, encrypted with AES-256-GCM and sent directly to each connected peer. A peer without an authenticated UDP path receives no audio. The UI reports the stage and reason, and retries connectivity checks automatically.

The server does not expose an audio-publishing hub method. The desktop client does not contain an audio relay send or receive API. Signalling connectivity alone does not imply that audio connectivity has succeeded.

## Protected UDP datagrams

The transport uses AES-256-GCM authenticated encryption. Each direction has its own independently derived 32-byte key and 4-byte nonce prefix. A nonce is the 4-byte prefix followed by the 64-bit big-endian packet sequence number. Sequence values must be unique and must never wrap for a given key/prefix pair; establish a fresh key before sequence exhaustion.

The authenticated header contains the LTUD magic value, protocol version, packet type, session GUID, sequence number and nonce prefix. The complete header is authenticated as additional data, so changing the session, type, sequence or prefix invalidates the authentication tag. The encrypted body is followed by a 16-byte GCM tag.

Receivers enforce a 64-packet sliding replay window. Authentication must succeed before a sequence is committed to the replay window. Datagrams larger than 1,200 bytes, malformed headers, wrong sessions, wrong nonce prefixes, invalid packet types, authentication failures and duplicate/too-old sequences are discarded.

## Peer key agreement and signalling

Each session receives a stable random GUID. The hub exposes the server-assigned SignalR connection ID and forwards ECDH offers and 32-byte confirmation proofs only to a named peer after verifying that both connections are distinct active members of the same session. Offers are bounded and bound to the session and both connection IDs.

`PeerKeyNegotiationCoordinator` maintains a separate ECDH agreement for every remote connection. It negotiates with existing participants on join and later participants on `ParticipantJoined`. A peer is considered secured only after both transcript-bound proofs verify. Proofs arriving before local offer processing are held until the agreement is ready. Peer key material is disposed and zeroed when a participant leaves, the session ends, signalling closes, or the coordinator is disposed.

The HMAC is key confirmation, not identity authentication: a malicious signalling server could substitute both public keys. Current trust is therefore the TLS-protected signalling service and its session membership checks. For protection against a malicious signalling service, add independently verifiable peer identity or a user-verified fingerprint.

## Candidate gathering and retry behaviour

`UdpCandidateGatherer` binds one IPv4 UDP socket, gathers active local interface addresses and asks the configured STUN endpoint for its server-reflexive mapping. `UdpPeerCandidateCoordinator` owns that candidate set for the session, publishes candidates through SignalR, tracks remote candidates by server-assigned peer connection ID, deduplicates repeats and republishes local candidates when another participant joins.

If the STUN request times out but local interfaces were found, the gatherer keeps the bound socket and publishes host candidates so same-LAN peers can still connect. If candidate gathering cannot produce a usable socket/candidate set, session startup fails with a diagnostic; audio is not redirected to SignalR.

`UdpPeerTransportCoordinator` creates one AES-256-GCM send/receive codec pair per secured peer. It probes available candidates with authenticated checks, responds to checks, verifies response challenges and records the authenticated remote endpoint. Keepalives run every five seconds. A path with no authenticated traffic for 15 seconds is treated as stale and candidate checks restart. Failed connectivity rounds raise a diagnostic event; the transport retries again and can recover when a peer publishes candidates later.

## Deployment constraints

A self-hosted signalling/STUN service must be publicly reachable for participants on arbitrary networks. Hosting the service on a home PC behind NAT requires router forwarding (or another public endpoint/tunnel). The server needs TCP 80/443 for HTTPS and UDP 3478 for STUN in the supplied home-hosting setup. Clients do not need inbound router rules.

STUN is not a relay and cannot guarantee traversal through symmetric or restrictive NATs. If a direct path cannot be established, the participants must change networks or use a different direct-connectivity configuration; ListenTogether intentionally does not conceal this failure with server-side audio forwarding.

The current implementation is IPv4-only. Session state is in memory, so a signalling server restart ends active sessions. Invite codes are bearer credentials and should be shared privately.
