# Windows audio smoke test

Use this checklist after building or installing the desktop app. It verifies the Windows audio path with real devices; CI can compile and test the software, but it cannot substitute for testing your actual playback devices and applications.

## Setup

1. On the host PC, start the signalling and STUN server from the repository root with `docker compose up --build`.
2. Confirm the signalling server is reachable at `http://localhost:5000/healthz`.
3. Open ListenTogether on the host PC. Set the server address and display name.
4. Open ListenTogether on a second PC on the same network. Set its server address to the host PC's LAN address, for example `http://192.168.1.20:5000`. Allow inbound TCP port 5000 through the host firewall.

For a single-PC test, launch two app instances and use the same local server URL. Avoid testing **All system audio** with both instances on one PC: the listener's playback can be captured again by the host and create an audio feedback loop. Use a second PC for system-audio testing, or select an individual app on a single PC.

## Individual application capture

1. On the host, choose **Create session**.
2. Select **Selected applications** and press **Refresh**.
3. Start audio playback in an application with a visible desktop window.
4. Select that application's checkbox. You can select more than one app.
5. Set one application's volume to 50%, leave another at 100%, and test mute/unmute.
6. Select **Start sharing** and copy the invite code.
7. On the listener PC, join using the same server address and invite code.
8. Confirm the listener hears the selected applications.
9. Change the host's per-app volume and mute state while the session is running. Confirm the listener hears the changes.
10. Stop sharing and confirm the listener sees that the session has ended.

## System audio capture

1. Stop the current session.
2. Select **All system audio**.
3. Start music or another audio source on the host PC.
4. Start sharing and join from the listener PC.
5. Confirm the listener hears the host's system output, including audio from more than one application.
6. Stop sharing and confirm playback stops on the listener.

## Failure and recovery checks

- Close the host app while sharing. The server should end the session and listeners should receive a session-ended notification.
- Stop the signalling server container. The host should stop capture after losing its signalling connection; listeners should clear buffered playback and report disconnection.
- Restart the signalling server and create a new session. The old session is not restored because session state is in memory.
- Refresh the application list after opening or closing desktop apps.
- Check the host's output-level reading while audio is playing. If it remains at zero, confirm the selected app is producing audio and try refreshing the source list.

## Known requirements

- Per-application capture requires Windows 10 version 2004 (build 19041) or later.
- The host captures the selected process tree; apps that are not currently rendering audio produce silence.
- System mode captures the default Windows output device.
- Listener playback uses the listener PC's default output device.
- Some protected-media paths may not be capturable. ListenTogether does not bypass DRM or other content protections.
- Internet sessions should use a public HTTPS signalling endpoint. Do not expose the local HTTP development endpoint to the public internet.
