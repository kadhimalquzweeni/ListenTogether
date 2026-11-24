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
- SignalR sessions with eight-character invite codes, participant events and the existing Opus media relay.
- Basic RFC 8489 STUN Binding server.
- Session-scoped UDP peer-candidate signalling groundwork.
- Automated tests for audio, codec, session, STUN and signalling behaviour.

## Intended user experience

Anyone can install the desktop application, create a session, share an invite code, and let friends listen from other networks. Users should not need to configure routers, install VPN adapters, or run server software.

The target networking model is inspired by Parsec's general approach: public signalling and STUN coordinate peers, direct encrypted UDP carries audio when possible, and a separately deployed public relay provides fallback. The current implementation still uses SignalR for audio; direct UDP and public relay fallback are not implemented yet.

## Requirements

- Windows 10/11 x64.
- Windows 10 version 2004 (build 19041) or later for per-application capture.
- .NET 10 SDK and Visual Studio 2022 for building from source.

## Run locally

### 1. Start the development relay

From the repository root:

```powershell
docker compose up --build
```

The development relay is available at `http://localhost:5000`.

### 2. Start the desktop app

Open `ListenTogether.sln` in Visual Studio, set `ListenTogether.App` as the startup project, and run it.

Set the relay URL to `http://localhost:5000`, enter a display name, create a session, select audio sources, and start sharing.

### 3. Join from another desktop

Both clients must use the same reachable signalling URL. For local development, this can be a LAN address. For real users on arbitrary networks, the public signalling and relay services must be deployed separately with HTTPS/WSS and public UDP reachability.

## Networking roadmap

See [Networking architecture](docs/NETWORKING_ARCHITECTURE.md) for the target Parsec-inspired connection model, implementation status, and deployment requirements.

## Important behaviour and limitations

- The server currently stores session state in memory. Restarting it ends active sessions.
- The existing SignalR audio relay is still the only implemented media transport.
- Direct UDP peer-to-peer audio, NAT traversal, media encryption and relay fallback are not yet implemented.
- STUN discovers mapped addresses; it does not itself make a host reachable or relay media.
- The current relay supports up to 16 participants per session, including the host.
- Invite codes are bearer credentials: anyone with a code can join. Share them privately.
- The server does not save audio packets or create recordings.
- Some protected media paths may not be capturable. ListenTogether does not bypass DRM or other content protections.
- Only share audio you have the right to share.

## Build and test

```powershell
dotnet restore ListenTogether.sln
dotnet build ListenTogether.sln --configuration Release --no-restore
dotnet test ListenTogether.sln --configuration Release --no-build --no-restore
```
