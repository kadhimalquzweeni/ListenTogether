# ListenTogether networking architecture

## Product goal

ListenTogether is a downloadable Windows desktop application that lets one person share audio playing on their PC with listeners on other networks. Hosts and listeners should not need to configure routers, install VPN adapters, or run server software.

The intended connection model is inspired by Parsec's general networking approach. It does not use or reproduce Parsec's proprietary protocol.

## Target architecture

1. **Public signalling service** — accepts outbound client connections, creates invite-code sessions, authenticates participants, and exchanges network candidates.
2. **STUN service** — lets clients discover server-reflexive UDP endpoints.
3. **Direct UDP media path** — peers attempt simultaneous connectivity checks / UDP hole punching and exchange encrypted Opus datagrams.
4. **Relay fallback** — a publicly reachable UDP relay forwards encrypted datagrams when direct connectivity cannot be established.
5. **Desktop client** — retains Windows audio capture, mixing, Opus encoding/decoding, and bounded playback buffering.

The public services are independent of any user's desktop PC. The repository can build and self-host these services, but the official public service must be deployed to an internet-reachable environment before arbitrary users can connect. The desktop application must never assume the developer's home PC is the service.

## Connection sequence

1. Host and listeners establish outbound secure signalling connections.
2. Host creates an invite code; listeners join with that code.
3. Each participant gathers local UDP endpoints and a server-reflexive endpoint from STUN.
4. Participants publish candidates through the signalling channel.
5. Peers attempt direct UDP connectivity checks.
6. A secure peer handshake authenticates the session and derives media keys.
7. The host sends Opus datagrams directly to connected listeners.
8. If direct connectivity fails, peers use the public relay service.
9. Signalling remains available for participant events and connection recovery.

## Current implementation status

- [x] WPF audio capture, mixing, Opus encoding/decoding and playback.
- [x] Invite-code sessions and SignalR signalling.
- [x] Basic RFC 8489 STUN Binding server.
- [x] Session-scoped peer-candidate exchange over SignalR.
- [ ] STUN client and local UDP candidate gathering.
- [ ] Direct UDP connectivity checks / hole punching.
- [ ] Authenticated key exchange and encrypted media datagrams.
- [ ] Public UDP media relay and client fallback.
- [ ] Automatic direct/relay path selection and reconnection.
- [ ] Public service deployment, monitoring, abuse controls and capacity planning.

## Important deployment constraint

STUN and signalling do not guarantee a direct path through every NAT. Some symmetric NATs, carrier-grade NATs, restrictive firewalls and enterprise networks prevent direct UDP connectivity. A relay is therefore required for reliable connectivity. The relay must be reachable independently of the host and must be sized for the expected concurrent audio bandwidth.

No user should be asked to configure inbound router port forwarding. Clients establish outbound signalling and UDP traffic; the public service endpoints are deployed separately.
