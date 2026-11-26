# Deployment overview

ListenTogether has two separate networking responsibilities:

- **Signalling and STUN:** the ASP.NET Core server coordinates sessions, exchanges peer connection candidates and supports peer-key negotiation. STUN helps clients discover their public UDP mappings.
- **Audio:** audio is sent directly between participants over authenticated, encrypted UDP. The server does not receive, relay, forward or store audio. There is no TURN service or audio-relay fallback.

The signalling server keeps session state in memory and is intended to run as a single instance. Restarting it ends active sessions.

## Choose a hosting method

### Windows home PC (the maintained self-hosting setup)

Use [HOME_PC_HOSTING.md](HOME_PC_HOSTING.md) to host the signalling and STUN services directly on a Windows PC.

This is the setup used by the project's PowerShell scripts and desktop app's **Start Server** workflow. It uses Caddy on Windows to provide HTTPS with a short-lived Let's Encrypt certificate for the host's public IPv4 address. It does not require a domain name.

The host needs a publicly reachable IPv4 address and router forwarding for TCP 80, TCP 443 and UDP 3478. The guide covers firewall setup, router configuration, startup, external testing and troubleshooting.

### Docker Compose

Docker Compose is an alternative for development or for hosting on a Linux server/VM. It is not required for the Windows home-PC setup.

#### Local development

From the repository root:

```powershell
docker compose up --build
```

This starts the server with:
- HTTP signalling on `http://localhost:5000`.
- STUN on UDP port `3478`.

For same-LAN testing, allow inbound TCP 5000 and UDP 3478 through the host firewall and use `http://HOST-LAN-IP:5000` in both desktop clients. Keep this plain-HTTP endpoint on a trusted network; do not expose it directly to the public internet.

#### Public Docker deployment

The production Compose file runs the signalling/STUN server behind Caddy.

Requirements:
- Docker Engine and Docker Compose.
- A DNS name pointing to the server.
- TCP ports 80 and 443 available to Caddy.
- UDP port 3478 reachable directly on the server for STUN.
- Firewall rules allowing those ports.

Copy `.env.example` to `.env`, then set `LISTENTOGETHER_DOMAIN` and `ACME_EMAIL` to your domain and certificate-contact email. Start the stack:

```bash
docker compose -f docker-compose.production.yml up --build -d
```

Caddy obtains and renews the HTTPS certificate and proxies SignalR/WebSocket traffic to the server container. UDP 3478 is published directly to the server container; Caddy does not proxy STUN. Use `https://YOUR_DOMAIN` as the server URL in the desktop clients.

The Docker production configuration is separate from the Windows home-PC setup. Do not run both against the same ports on one machine.

## Network and security requirements

- Internet clients connect to the signalling endpoint over HTTPS/WSS and use UDP 3478 for STUN.
- The signalling server must itself be reachable by every participant. Hosting behind a home router requires port forwarding or another public hosting arrangement.
- Clients do not need inbound router rules or port forwarding.
- STUN discovers public UDP mappings; it does not guarantee that every pair of networks can establish a direct connection. Symmetric NATs, VPNs and restrictive firewalls can prevent direct peer connectivity.
- If a direct authenticated UDP path cannot be established, the listener receives no audio. ListenTogether reports/retries the direct connection; it does not fall back to sending audio through SignalR or the server.
- Treat eight-character session codes as private bearer credentials: anyone who has a code can attempt to join that session.
- Run one signalling server instance. Session state is in memory and is not shared across replicas.
- The current candidate gathering and media transport are IPv4-only.

For protocol details, encryption and connection establishment, see [NETWORKING_ARCHITECTURE.md](NETWORKING_ARCHITECTURE.md). For real-device audio checks, see [SMOKE_TEST.md](SMOKE_TEST.md) and [MANUAL_TESTING.md](MANUAL_TESTING.md).
