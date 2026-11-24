# Internet hosting without router changes

ListenTogether can be used over the internet without opening ports on the home router, reserving a public IP, or buying a domain.

## Architecture

- The Windows desktop app on the host PC captures and encodes the selected audio.
- The app opens an outbound secure WebSocket connection to the Cloudflare Worker endpoint.
- The Worker routes the SignalR JSON protocol to a Durable Object, which coordinates invite-code sessions and forwards Opus packets.
- Listener PCs connect outbound to the same HTTPS/WSS endpoint.
- The public edge relay is the internet-facing transport. The home PC is still the audio host, but it is not directly exposed as a public server.

The existing .NET relay remains useful for local development and LAN testing. The Cloudflare edge relay is the no-port-forwarding internet path.

## Why Cloudflare Workers

The Worker uses a SQLite-backed Durable Object and the workers.dev hostname. No domain or inbound home connection is required. The free Workers plan currently includes 100,000 Durable Object requests per day; incoming WebSocket messages are accounted for at a 20:1 ratio for Durable Object request billing. The desktop host batches five 20 ms Opus frames into one message (about ten uploads per second), designed to keep one continuously active session within the daily free request allowance. This is an estimate, not a guarantee: extra sessions, reconnects, and other Worker use consume the same account quota. When a free quota is exceeded, operations of that type fail until the daily reset. Review current limits before inviting a large group.

## Deploy

1. Create or sign in to a Cloudflare account at https://dash.cloudflare.com/.
2. Install Node.js 24 or later on the development PC.
3. Open PowerShell at the repository root.
4. Run:

   ~~~powershell
   cd cloudflare/worker
   npx wrangler@latest login
   npx wrangler@latest deploy
   ~~~

5. Cloudflare prints a Worker URL similar to https://listen-together-edge.ACCOUNT-SUBDOMAIN.workers.dev.
6. In ListenTogether, set Relay server to that URL (including https://) on the host and every listener PC.
7. Check https://WORKER-HOST/healthz. It should return a JSON status of ready.
8. Create a session on the host PC and join from a second PC on a different internet connection.

The first deployment requires the account owner to complete Cloudflare browser sign-in. Never put an API token or account secret in the repository.

## Local validation

~~~powershell
cd cloudflare/worker
npm test
~~~

The repository CI runs these edge-relay tests alongside the Windows .NET build and test suite.

## Network requirements

- No port forwarding.
- No inbound Windows Firewall rule.
- No public IPv4 address or static IP.
- No domain name.
- Outbound HTTPS/WebSocket access on TCP 443.

## Current scope and limitations

- The first internet-ready path is a secure WebSocket Opus relay. It does not yet perform direct UDP peer-to-peer hole punching; the edge relay is the reliable fallback and, for this path, the primary media route.
- The self-hosted UDP STUN listener in the .NET server is not publicly reachable from behind home NAT without a public UDP endpoint. It remains available for LAN testing and a later publicly hosted ICE/STUN deployment.
- Active rooms are associated with connected WebSockets. A Worker deployment or edge restart can disconnect active sessions; users can create a new invite after reconnecting.
- The Cloudflare Free plan has strict daily usage limits. Continuous streaming is not unlimited or guaranteed to remain free. Upgrade or move the media relay to a dedicated host if usage grows beyond the free allowance.
- Audio is forwarded live and is not recorded or stored by the edge relay.
- Invite codes are bearer credentials. Anyone with the code can join, so share it privately.

For a fresh local .NET relay, see DEPLOYMENT.md. For the no-router internet path, follow this guide.
