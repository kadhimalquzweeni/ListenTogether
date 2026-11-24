# ListenTogether

**ListenTogether** is a Windows desktop app for sharing the audio playing on your PC with friends in a live listening session. It captures audio locally, mixes selected sources, encodes the mix with Opus, and streams it through a small SignalR relay server.

The host can share either:

- **All system audio** from the default Windows output device.
- **Selected applications**, with multiple apps mixed together and individual volume/mute controls.

Listeners join with an eight-character invite code and hear the host's live mix.

## Current capabilities

- .NET 10 WPF desktop application with a consistent dark UI.
- Windows process discovery for visible desktop applications.
- WASAPI loopback capture for all system output.
- Windows process-loopback capture for an individual app and its child process tree.
- Multi-source 48 kHz stereo float PCM mixer with per-source gain, bounded buffering, silence on underflow, and output clipping protection.
- 20 ms Opus encoding/decoding using Concentus.
- Low-latency Windows audio playback with a bounded buffer.
- SignalR relay server for creating sessions, joining by invite code, participant events, and relaying audio packets.
- Automated tests for domain validation, PCM conversion, mixing, Opus codec round-trips, playback buffering, and session registry rules.
- Windows CI builds the complete solution and runs the tests.

## Requirements

### Desktop app

- Windows 10 version 2004 (build 19041) or later for per-application capture.
- Windows 10/11.
- **Packaged Windows x64 release:** Windows 10/11 x64; the self-contained app does not require a separately installed .NET runtime.
- **Build from source:** .NET 10 SDK and Visual Studio 2022 with the .NET desktop development workload.

### Relay server

One of:
- .NET 10 SDK to run it directly, or
- Docker / Docker Compose.

The relay server is required for sessions. It forwards encoded audio packets and keeps session state in memory; it does not record or persist audio.

## Run locally

### 1. Start the relay server

From the repository root:

```powershell
docker compose up --build
```

The relay will be available at `http://localhost:5000`. Alternatively, run it directly:

```powershell
dotnet run --project src/ListenTogether.Server
```

The server health endpoint is `http://localhost:5000/healthz`.

### 2. Start the desktop app

Open `ListenTogether.sln` in Visual Studio, set `ListenTogether.App` as the startup project, then run it.

On the home screen:
1. Set **Relay server** to `http://localhost:5000`.
2. Enter the name you want other listeners to see. The relay URL and display name are saved in your Windows user profile for the next launch.
3. Choose **Create session**.
4. Select **All system audio** or **Selected applications**.
5. Adjust the source volumes and mute controls.
6. Select **Start sharing** and share the invite code with your listeners.

### 3. Join from another desktop

The listener must be able to reach the same relay URL. Enter that URL and the invite code on the home screen, then select **Join session**.

For two PCs on the same network, use the host PC's LAN address in the relay URL, for example `http://192.168.1.20:5000`, and allow inbound TCP port 5000 through the host firewall.

For friends connecting over the internet, deploy the relay behind a public HTTPS domain. See [Relay deployment](docs/DEPLOYMENT.md). Do not expose an unencrypted HTTP relay to the public internet.

## How the audio path works

```text
Windows audio sources
  ├─ Default output loopback
  └─ Selected process loopback
           |
           v
     PCM source buffers
           |
           v
       PCM mixer
   (per-app gain / mute)
           |
           v
   48 kHz stereo float PCM
           |
           v
       Opus encoder
      (20 ms packets)
           |
           v
    SignalR relay server
      /hubs/listening
           |
           v
       Opus decoder
           |
           v
     Bounded PCM buffer
           |
           v
    Windows audio output
```

The relay is a central media relay rather than peer-to-peer WebRTC. This avoids requiring each listener to establish a direct NAT traversal connection, but the relay host uses bandwidth for every listener. At the default Opus bitrate, allow roughly 128 kbit/s of host upload per listener, plus protocol overhead.

## Architecture

| Project | Responsibility |
| --- | --- |
| `ListenTogether.App` | WPF home screen, source picker, host window, listener window, view models |
| `ListenTogether.Core` | Audio source, PCM, and session domain models |
| `ListenTogether.Infrastructure` | Windows process discovery, WASAPI capture, PCM mixing, Opus codec, playback, SignalR client |
| `ListenTogether.Server` | SignalR hub, invite-code registry, participant state, audio packet relay |
| `ListenTogether.Tests` | Automated unit tests |

## Important behaviour and limitations

- System audio capture uses the default Windows render device.
- Per-application capture uses the Windows process-loopback API and includes the selected process tree. If the app is not currently playing audio, it may not produce samples.
- The server currently stores session state in memory. Restarting the server ends all active sessions.
- A host disconnect ends that session; automatic session resumption is not implemented yet.
- The relay supports up to 16 participants per session, including the host.
- Invite codes are bearer credentials: anyone who has the code can join. Share them privately.
- The server does not save audio packets or create recordings.
- Public deployments should use HTTPS/WSS through a trusted reverse proxy.
- Some protected media or application-specific audio paths may not be capturable. ListenTogether does not bypass DRM or other content protections.
- Only share audio you have the right to share.

## Build and test

```powershell
dotnet restore ListenTogether.sln
dotnet build ListenTogether.sln --configuration Release --no-restore
dotnet test ListenTogether.sln --configuration Release --no-build --no-restore
```

## Desktop release

The release workflow publishes a self-contained Windows x64 desktop package when a version tag (`v*`) is pushed. The relay server is deployed separately; the desktop package does not bundle a public relay service.

## Deployment

See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) for local Docker, LAN testing, and public HTTPS relay deployment.
