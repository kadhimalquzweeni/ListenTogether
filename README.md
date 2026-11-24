# ListenTogether

**ListenTogether** is a Windows desktop app for sharing the audio playing on your PC with friends in a live listening session. It captures audio locally, mixes selected sources, encodes the mix with Opus, and streams it to listeners.

The host can share either:

- **All system audio** from the default Windows output device.
- **Selected applications**, with multiple apps mixed together and individual volume/mute controls.

Listeners join with an eight-character invite code and hear the host's live mix. ListenTogether captures audio rendered by Windows; it does not use the Spotify API or require Spotify Premium. Whether a particular app stream is capturable still depends on Windows and that app's own protected-media path.

## Current capabilities

- .NET 10 WPF desktop application.
- Windows process discovery, WASAPI system loopback and per-application process-loopback capture.
- Multi-source 48 kHz stereo PCM mixing with per-source gain and bounded buffering.
- 20 ms Opus encoding/decoding using Concentus.
- Low-latency Windows audio playback with a bounded buffer.
- SignalR sessions with eight-character invite codes and participant events; SignalR carries signalling only.
- Self-hosted RFC 8489 STUN Binding server and IPv4 host/server-reflexive candidate gathering.
- Session-scoped ECDH P-256 key agreement and HMAC peer confirmation.
- AES-256-GCM authenticated UDP packets with replay protection.
- UDP hole punching, connectivity checks, endpoint keepalives and encrypted direct Opus media.
- Sequence-aware jitter buffering for direct UDP audio.
- Automated tests for audio, key negotiation, candidate exchange, encrypted UDP media, direct-path retries and STUN behaviour.
- CI validates the PowerShell syntax of the Windows home-server scripts before building and testing.

## Intended user experience

The initial deployment is intended for a private setup between two friends. The host can run the signalling and STUN services on their own Windows PC, detect their current public IPv4 at startup, and share that IP address plus the session invite code. The host copies the combined invite details from the audio-sharing window; the listener pastes them into the desktop app to fill in the server address and invite code. No domain or dynamic DNS provider is required for this setup.

The networking model uses SignalR only for session signalling, peer candidate exchange and key negotiation. STUN discovers UDP mappings, then peers establish encrypted direct UDP paths for media. Audio is never sent through the server. If a direct path cannot be established, that listener receives no audio and the app reports the failure while retrying. STUN and the signalling service must be publicly reachable for users on arbitrary networks.

## Requirements

- Windows 10/11 x64.
- Windows 10 version 2004 (build 19041) or later for per-application capture.
- .NET 10 SDK and Visual Studio 2022 for building from source.

## Run locally

### 1. Start the development signalling and STUN server

From the repository root:

```powershell
docker compose up --build
```

The development signalling endpoint is available at `http://localhost:5000`; STUN listens on UDP 3478.

### 2. Start the desktop app

Open `ListenTogether.sln` in Visual Studio, set `ListenTogether.App` as the startup project, and run it.

Set the server address to `http://localhost:5000`, enter a display name, create a session, select audio sources, and start sharing.

### 3. Join from another desktop

Both clients must use the same reachable signalling URL. For the private home-hosted setup, use the host's detected `https://PUBLIC_IP` address and the invite code. The home server guide explains router forwarding and certificate setup.

## Networking and testing

See [Networking architecture](docs/NETWORKING_ARCHITECTURE.md) for the connection model and [Manual testing](docs/MANUAL_TESTING.md) for LAN, multi-listener and internet test steps. Deployment requirements are covered in [Relay deployment](docs/DEPLOYMENT.md). To host the signalling and STUN services on a Windows home PC, follow [Home PC hosting](docs/HOME_PC_HOSTING.md).

## Important behaviour and limitations

- The server currently stores session state in memory. Restarting it ends active sessions.
- Direct UDP is required for audio. Restricted or symmetric NATs may prevent a direct path; ListenTogether reports the problem and retries, but does not relay audio.
- The current STUN implementation is IPv4-only. STUN discovers mapped addresses; it does not itself make a host reachable or relay media.
- The server's STUN endpoint uses UDP port 3478. Internet deployments must expose that UDP port as well as the SignalR HTTPS endpoint.
- A self-hosted server on a home PC behind NAT is not reachable without a public endpoint and router forwarding. Clients do not need router configuration, but the server PC must be publicly reachable.
- The home-hosting setup uses Let's Encrypt IP-address certificates with the `shortlived` ACME profile. These certificates last 160 hours and must be renewed automatically by Caddy.
- The signalling service supports up to 16 participants per session, including the host.
- Invite codes are bearer credentials: anyone with a code can join. Share them privately.
- Audio packets are encrypted and travel directly between peers; the signalling server does not receive or save media packets.
- Some protected media paths may not be capturable. ListenTogether does not bypass DRM or other content protections.
- Only share audio you have the right to share.

## Build and test

```powershell
dotnet restore ListenTogether.sln
dotnet build ListenTogether.sln --configuration Release --no-restore
dotnet test ListenTogether.sln --configuration Release --no-build --no-restore
```
