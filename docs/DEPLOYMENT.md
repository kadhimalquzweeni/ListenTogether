# Relay deployment

The desktop clients connect to a ListenTogether SignalR relay at:

`https://your-domain.example/hubs/listening`

The relay forwards small Opus packets to the listeners in a session. It does not persist audio. The current server uses in-memory session state and is designed to run as a single instance.

## Local Docker

From the repository root:

```powershell
docker compose up --build
```

The server listens on `http://localhost:5000`. Use that address in the desktop app.

## Same-network testing

1. Find the host PC's LAN IPv4 address.
2. Allow inbound TCP port 5000 in Windows Firewall.
3. Start the relay with Docker Compose.
4. Set the relay URL on both desktops to `http://HOST-LAN-IP:5000`.

Use HTTP only on a trusted local network. Do not expose this endpoint directly to the public internet.

## Public HTTPS deployment with Caddy

You need:
- A Linux server or VM with Docker Engine and Docker Compose.
- A DNS name pointing to the server.
- Inbound TCP ports 80 and 443 available to Caddy.

Copy `.env.example` to `.env` and set `LISTENTOGETHER_DOMAIN` to your public DNS name. Then run:

```bash
docker compose -f docker-compose.production.yml up --build -d
```

Caddy obtains and renews the TLS certificate and proxies WebSocket connections to the relay container. In the desktop app, use the public URL:

`https://your-domain.example`

The Caddy reverse proxy must support WebSocket upgrades. Caddy's `reverse_proxy` does this automatically.

## Capacity and security notes

- The relay currently supports 16 participants per session, including the host.
- The server accepts Opus packets up to 1,275 bytes and limits the host packet cadence.
- Session codes are random eight-character bearer credentials. Anyone with a code can join that session.
- Session state is held in process memory. A restart or deployment replacement ends all sessions.
- Run a single relay instance. The current in-memory registry is not shared between replicas.
- Use HTTPS/WSS for internet traffic. Do not publish the container's plain HTTP port directly to the internet.
- The relay does not record audio, but it necessarily handles the live audio packets while forwarding them.
- At the default Opus bitrate, estimate approximately 128 kbit/s of host upload per listener, plus protocol overhead and server egress.

For production use, put the relay behind a firewall and trusted TLS reverse proxy. Keep the Docker container's port private to the proxy.
