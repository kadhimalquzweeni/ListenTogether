# Signalling and STUN deployment

The desktop clients connect to the ListenTogether SignalR signalling service at:

`https://your-domain.example/hubs/listening`

The server handles session signalling, candidate exchange and peer-key negotiation only. It does not receive, forward or persist audio packets. The current server uses in-memory session state and is designed to run as a single instance.

## Local Docker

From the repository root:

```powershell
docker compose up --build
```

The server listens on `http://localhost:5000` and publishes the STUN endpoint on UDP port `3478`. Use the HTTP address in the desktop app for local development.

## Same-network testing

1. Find the host PC's LAN IPv4 address.
2. Allow inbound TCP port 5000 and UDP port 3478 in Windows Firewall.
3. Start the signalling and STUN services with Docker Compose.
4. Set the server address on both desktops to `http://HOST-LAN-IP:5000`.
5. Create a session on one desktop and join from the other. The clients exchange UDP candidates and attempt a direct encrypted audio path. If that path cannot be established, audio continues through SignalR.

Use HTTP only on a trusted local network. Do not expose this endpoint directly to the public internet.

## Public HTTPS deployment with Caddy

You need:
- A Linux server or VM with Docker Engine and Docker Compose.
- A DNS name pointing to the server.
- Inbound TCP ports 80 and 443 available to Caddy.
- Inbound UDP port 3478 available directly to the relay container for STUN.

Copy `.env.example` to `.env` and set `LISTENTOGETHER_DOMAIN` to your public DNS name and `ACME_EMAIL` to an address for certificate notifications. Then run:

```bash
docker compose -f docker-compose.production.yml up --build -d
```

Caddy obtains and renews the TLS certificate and proxies WebSocket connections to the relay container. Docker publishes UDP 3478 directly to the STUN service; Caddy does not proxy STUN. In the desktop app, use the public URL:

`https://your-domain.example`

The Caddy reverse proxy must support WebSocket upgrades. Caddy's `reverse_proxy` does this automatically. Allow UDP 3478 in the VM/cloud firewall as well as in the host firewall.

## Capacity and security notes

- The signalling service supports 16 participants per session, including the host.
- The server accepts Opus packets up to 1,275 bytes and limits the host packet cadence.
- Session codes are random eight-character bearer credentials. Anyone with a code can join that session.
- Session state is held in process memory. A restart or deployment replacement ends all sessions.
- Run a single signalling server instance. The current in-memory registry is not shared between replicas.
- Use HTTPS/WSS for internet traffic. Do not publish the container's plain HTTP port directly to the internet.
- The signalling server never handles live audio packets; media travels directly between peers over encrypted UDP.
- At the default Opus bitrate, estimate approximately 128 kbit/s of host upload per listener, plus protocol overhead and server egress.

For production use, put the signalling server behind a firewall and trusted TLS reverse proxy. Keep the Docker container's port private to the proxy.


## Direct UDP and reachability

- Clients only need outbound access to the signalling HTTPS endpoint and UDP STUN port 3478. They do not need inbound router rules or port forwarding.
- The STUN service must be reachable from the public internet for server-reflexive candidates to work. The direct UDP path is attempted between peers; some NAT combinations (especially symmetric or restrictive NATs) may prevent it. Those listeners do not receive audio. The app reports the failure and retries direct connectivity checks.
- The signalling server itself must have a public endpoint. Running Docker Compose on a home PC behind a router does not make that server reachable from the internet. Without router forwarding or a tunnel/public hosting endpoint, only same-LAN clients can reach it.
- The included production Compose file uses Caddy with a DNS name for managed HTTPS. A domain-free public TLS setup is not currently automated; do not expose the development HTTP endpoint to the public internet.
