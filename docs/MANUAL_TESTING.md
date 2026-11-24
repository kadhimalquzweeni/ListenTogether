# Manual testing guide

This guide covers the direct-only media implementation. SignalR is used for signalling only; audio requires an authenticated UDP path between every participant.

## 1. Get the Windows desktop package

After a successful Windows CI run, open the repository's **Actions** tab, select the latest successful `.NET` workflow for this feature branch, and download the `ListenTogether-win-x64` artifact. Extract it and run `ListenTogether.App.exe`.

The same workflow also publishes a `ListenTogether-Relay-win-x64` artifact if you want to run the signalling/STUN server outside Docker. Artifacts are retained by GitHub Actions for 14 days.

## 2. Build and start the development server

From the repository root:

```powershell
docker compose up --build
```

The development server exposes:

- TCP 5000 for the local development SignalR signalling endpoint (or TCP 443 for the public HTTPS endpoint).
- UDP 3478 for the STUN Binding service.

For same-LAN testing, allow inbound TCP 5000 and UDP 3478 in the server PC's firewall. Find that PC's LAN IPv4 address and use `http://HOST-LAN-IP:5000` as the server URL on both desktop clients.

## 3. Test two desktops on the same LAN

1. Start ListenTogether on both desktops.
2. Set both server URLs to the same LAN address.
3. On the host desktop, create a session.
4. Select system audio or one or more application audio sources, then start sharing.
5. Copy the eight-character invite code.
6. On the listener desktop, enter the code and join.
7. Play audio on the host PC.
8. Check that the listener hears the audio.
9. Both clients should report that a secure direct UDP connection has been established. If direct UDP cannot be established, the UI must report the failed stage/reason and retry; no audio should be relayed.
10. Leave the session and confirm that playback stops.

## 4. Test multiple listeners

1. Keep the host session running.
2. Join from a second listener desktop.
3. Confirm that each listener receives audio.
4. Confirm that the host reports independent peer connections as they become available.
5. Close one listener and verify that the remaining listener continues receiving audio.

The host sends Opus frames only over authenticated direct UDP paths. Listeners without a direct path receive no audio; there is no SignalR media fallback.

## 5. Test internet connectivity

For clients on different networks, the signalling server and STUN service must be publicly reachable.

The supplied production Compose setup expects a DNS name and Caddy-managed HTTPS. It publishes UDP 3478 directly to the signalling/STUN server container. The public server firewall must allow TCP 80/443 and UDP 3478. Clients do not need inbound router rules or port forwarding.

A home PC behind a typical NAT is not a publicly reachable server just because Docker is running. Without router forwarding, a tunnel, or a public hosting endpoint, remote clients will not be able to reach a server running only on that home PC. The direct UDP peer path does not remove this server reachability requirement.

## 6. Direct-connection failure behaviour

- If STUN responds, the clients exchange local and server-reflexive candidates and attempt authenticated UDP connectivity checks.
- If STUN times out but local interface candidates exist, same-LAN direct connectivity is still attempted.
- If direct UDP cannot be established, the listener receives no audio and the UI displays a diagnostic.
- If the UDP path goes stale, the next keepalive detects it and the UI shows a new direct-connection attempt; if that attempt fails, it displays the reason. Audio remains direct-only and is not relayed.
- STUN discovery and direct media are IPv4-only at this stage.

## 7. Automated verification

Run the full test suite from the repository root:

```powershell
dotnet restore ListenTogether.sln
dotnet build ListenTogether.sln --configuration Release --no-restore
dotnet test ListenTogether.sln --configuration Release --no-build --no-restore
```

The networking tests include peer key negotiation, candidate exchange, authenticated UDP connectivity and audio, jitter-buffer ordering, failed-connectivity diagnostics and candidate-arrival retries.
