# Networking architecture

ListenTogether uses a signalling service to coordinate sessions and attempts a direct UDP media path between participants. The current desktop MVP still sends audio through SignalR; direct UDP media is being introduced in independently testable stages.

## Connection establishment

1. The desktop client connects to the ASP.NET Core SignalR signalling endpoint over TLS.
2. A participant gathers local UDP host candidates and a server-reflexive candidate using STUN on the same UDP socket that will later carry media.
3. Candidates are exchanged through the session signalling channel.
4. Both peers will send connectivity checks to the other peer's candidates concurrently (UDP hole punching). The selected socket must remain alive for the duration of the session.
5. If direct connectivity cannot be established, a publicly reachable UDP relay is required. STUN does not relay traffic and cannot make a private home server reachable through NAT.

## Protected UDP datagrams

The transport foundation uses AES-256-GCM authenticated encryption. Each direction must have its own independently derived 32-byte key and its own 4-byte nonce prefix. A nonce is the 4-byte prefix followed by the 64-bit big-endian packet sequence number. Sequence values must be unique and must never wrap for a given key/prefix pair; establish a fresh key before sequence exhaustion.

The authenticated header contains the LTUD magic value, protocol version, packet type, session GUID, sequence number and nonce prefix. The complete header is authenticated as additional data, so changing the session, type, sequence or prefix invalidates the authentication tag. The encrypted body is followed by a 16-byte GCM tag.

Receivers enforce a 64-packet sliding replay window. Authentication must succeed before a sequence is committed to the replay window. Datagrams larger than 1,200 bytes, malformed headers, wrong sessions, wrong nonce prefixes, invalid packet types, authentication failures and duplicate/too-old sequences are discarded.

**Key agreement foundation implemented:** `PeerKeyAgreement` creates ephemeral P-256 ECDH key pairs, exchanges public-key offers bound to a session and the two SignalR connection IDs, hashes a canonical transcript, and derives a confirmation key plus independent directional AES-256-GCM keys and nonce prefixes using HKDF-SHA256. Each side emits a transcript-bound HMAC proof; both sides must verify the other proof before constructing packet codecs. Key material is held in disposable result objects and zeroed on disposal.

**SignalR signalling integration implemented:** each created session receives a stable random GUID, returned to every participant. The hub exposes the server-assigned connection ID and relays ECDH offers and 32-byte confirmation proofs directly to a named peer only after verifying that sender and recipient are distinct active members of the same session and that the offer's session/connection fields match the server context. Offers and proofs are bounded before forwarding. The desktop SignalR client now exposes session ID, connection ID, peer-offer/proof events, and corresponding send methods. This is the signalling transport API, not yet an automatic per-peer negotiation coordinator: the desktop lifecycle must create one agreement per remote participant, exchange offers, verify each proof, and dispose agreements on participant departure, leave, or disconnect.

**Trust requirements:** the HMAC is key confirmation, not identity authentication: a malicious signalling server could substitute both public keys. Current trust is therefore the TLS-protected signalling service and its session membership checks. Do not enable direct UDP media until the desktop negotiation coordinator verifies both proofs and the client falls back safely to SignalR. For protection against a malicious signalling service, add an independently verifiable peer identity or user-verified fingerprint.

## Deployment constraint

A self-hosted STUN/signalling/relay service must be reachable from the public internet to support arbitrary users without router configuration. Hosting the service on a PC behind a typical home NAT is not sufficient by itself. A relay fallback requires a publicly reachable server; STUN alone is not a relay.


### Automatic per-peer key negotiation

`PeerKeyNegotiationCoordinator` maintains a separate ECDH agreement for every remote SignalR connection. It starts negotiation with existing peers on `SessionJoined`, and with later arrivals on `ParticipantJoined`. Each peer is considered secured only after the transcript hash and HMAC key-confirmation proof both verify. Proofs arriving before local offer processing are held until the agreement is ready. Peer key material is disposed and zeroed when that participant leaves, the session ends, the signalling connection closes, or the coordinator is disposed. Consumers must keep the coordinator alive for the session and must not dispose a secured result while its key material is in use.
