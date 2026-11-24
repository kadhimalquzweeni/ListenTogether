# ListenTogether

A Windows desktop application for sharing audio with other people in real time.

The host will be able to choose **all system audio** or select **one or more individual applications**. Selected application streams will be mixed with per-application volume controls before being encoded and sent to listeners.

## Current status

This repository contains the initial solution foundation and first home screen:

- .NET 10 WPF desktop application.
- Separate Core, Infrastructure, App, and Tests projects.
- Dark Create Session / Join Session home screen.
- Domain model for system-wide and multi-application source selection.
- Validation preventing empty selections, duplicate process IDs, and mixing system mode with application mode.
- Initial automated tests for the audio-source selection rules.

Session creation, joining, Windows audio capture, audio mixing, and network streaming are **not implemented yet**. The home-screen actions currently report that the signalling service is still to be connected; they do not create a fake session.

## Requirements

- Windows 10/11
- Visual Studio 2022 with the .NET desktop development workload
- .NET 10 SDK

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
| `ListenTogether.Core` | Audio source and session domain models/contracts |
| `ListenTogether.Infrastructure` | Windows audio, capture, mixing, and networking implementations |
| `ListenTogether.Tests` | Automated tests for domain and service behaviour |

## Planned milestones

1. **Foundation:** solution, home screen, domain rules, and tests.
2. **Audio discovery and capture:** enumerate Windows audio sessions; capture system output and individual process audio.
3. **Audio mixing:** select multiple apps, individual gain controls, clipping protection, and stable sample format.
4. **Sessions and signalling:** create/join codes, participant state, reconnect handling, and secure signalling.
5. **Streaming:** Opus encoding and WebRTC transport.
6. **Polish:** diagnostics, quality settings, installer, and release pipeline.

## Audio capture design

System capture and per-application capture will be separate providers. Windows process-loopback capture is the intended path for individual applications; WASAPI loopback is the intended path for all system output. The application will clearly report unavailable or unsupported sources instead of silently falling back to capturing unrelated audio.
