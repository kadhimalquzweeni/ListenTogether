# ListenTogether

A Windows desktop application for sharing audio with other people in real time.

The host will be able to choose **all system audio** or select **one or more individual applications**. Selected application streams will be mixed with per-application volume controls before being encoded and sent to listeners.

## Current status

The repository now contains the initial WPF solution and the first Windows audio-capture foundation:

- .NET 10 WPF desktop application with a dark Create Session / Join Session home screen.
- Separate App, Core, Infrastructure, and Tests projects.
- Domain rules for exclusive system-audio mode or selecting multiple application processes.
- Windows process discovery for user-facing desktop applications.
- WASAPI system-output loopback capture.
- Windows process-loopback capture for individual applications and their child processes.
- A managed 48 kHz, stereo, IEEE float PCM frame contract.
- Safe copying and validation of captured PCM buffers.
- Automated tests for source-selection rules, PCM conversion, and audio-frame validation.

The capture services are implemented as infrastructure components; the current home screen is not yet wired to the source picker or capture controls. Session creation, joining, audio mixing, and network streaming are still outstanding. The Create and Join actions do not create fake sessions.

## Requirements

- Windows 10 version 2004 (build 19041) or later for per-application capture
- Windows 10/11 for the desktop application
- Visual Studio 2022 with the .NET desktop development workload
- .NET 10 SDK

System-wide loopback capture uses the default Windows render device. Per-application capture uses the Windows process-loopback API and captures the selected process tree. If a selected process is not currently rendering audio, Windows may deliver no audio buffers until it starts playing.

## Open and run

1. Clone this repository.
2. Open `ListenTogether.sln` in Visual Studio.
3. Restore NuGet packages.
4. Set `ListenTogether.App` as the startup project.
5. Build and run.

Run the automated tests from the solution directory:

```powershell
dotnet test ListenTogether.sln
```

## Architecture

| Project | Responsibility |
| --- | --- |
| `ListenTogether.App` | WPF UI, views, and view models |
| `ListenTogether.Core` | Audio source, PCM frame, and session domain contracts |
| `ListenTogether.Infrastructure` | Windows audio discovery, WASAPI capture, mixing, and networking implementations |
| `ListenTogether.Tests` | Automated tests for domain and service behaviour |

## Planned milestones

1. **Foundation:** solution, home screen, domain rules, and tests.
2. **Audio discovery and capture:** Windows process discovery, system loopback, and process-specific loopback. The capture services and PCM contracts are now in place; the UI integration and device validation remain.
3. **Audio mixing:** select multiple apps, individual gain controls, sample alignment, clipping protection, and a shared output clock.
4. **Sessions and signalling:** create/join codes, participant state, reconnect handling, and secure signalling.
5. **Streaming:** Opus encoding and WebRTC transport.
6. **Polish:** diagnostics, quality settings, installer, and release pipeline.

## Audio capture design

System capture and per-application capture use separate WASAPI modes behind one capture interface. NAudio's modern `WasapiRecorder` provides both render-endpoint loopback and Windows process-loopback capture. All capture sources are converted to the same 48 kHz stereo IEEE float PCM contract before they enter the mixer and streaming pipeline.
