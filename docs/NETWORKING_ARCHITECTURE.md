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

**Key management requirement:** the codec does not invent or distribute keys. The signalling/control plane must authenticate participants and establish distinct directional keys before enabling this transport. Never send a media key in plaintext or reuse one key/prefix pair in both directions. The key agreement and authenticated peer binding are a separate milestone; until that exists, the desktop client must continue using the existing SignalR audio path.

## Deployment constraint

A self-hosted STUN/signalling/relay service must be reachable from the public internet to support arbitrary users without router configuration. Hosting the service on a PC behind a typical home NAT is not sufficient by itself. A relay fallback requires a publicly reachable server; STUN alone is not a relay.
