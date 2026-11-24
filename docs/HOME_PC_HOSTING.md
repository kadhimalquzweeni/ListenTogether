# Host ListenTogether on a Windows home PC

This guide runs the signalling service and self-hosted STUN service on your Windows PC for a private setup between you and a friend. The launcher detects your public IPv4 address each time it starts. Caddy provides HTTPS using a publicly trusted, short-lived Let's Encrypt certificate for that IP. The application attempts encrypted direct UDP audio; note that the current codebase still includes SignalR relay fallback, which must be removed separately to achieve the intended direct-only behaviour.

> This makes your PC a public internet server. Keep Windows updated, use a strong account password, do not expose the development HTTP endpoint, and only forward the ports listed below. Your PC must remain powered on and connected to the internet.

## Requirements

- Windows 10/11 x64.
- .NET 10 SDK to publish locally, or the Windows x64 server artifact from GitHub Actions.
- Caddy 2.10 or later for Windows.
- Router administrator access and a public IPv4 address. Your router's WAN IPv4 must match the public IPv4 detected by the launcher; otherwise your ISP or another upstream router may be using NAT.
- A static/reserved LAN IPv4 address for the server PC.
- An email address for the ACME account (used by Caddy).

No domain name, DuckDNS account, or dynamic DNS updater is required.

## 1. Publish the server

From the repository root, run:

    dotnet publish src/ListenTogether.Server/ListenTogether.Server.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/server

The server defaults to loopback HTTP on 127.0.0.1:5000, while STUN binds to 0.0.0.0:3478/UDP. This is intentional: Caddy handles public HTTPS, and STUN is exposed directly through the router.

## 2. Configure your router

Reserve a fixed LAN address for the server PC in your router. Forward these ports to that LAN address:

| Protocol | External port | Internal port | Purpose |
|---|---:|---:|---|
| TCP | 80 | 80 | Caddy HTTP-01 certificate validation / HTTP redirect |
| TCP | 443 | 443 | HTTPS and SignalR WebSockets |
| UDP | 3478 | 3478 | STUN binding requests |

Do not forward TCP 5000. It is deliberately loopback-only.

## 3. Run the guided setup script

Open PowerShell as Administrator in the repository root and run:

    .\scripts\home-server\Setup-ListenTogether.ps1

This creates inbound Windows Firewall rules for TCP 80/443 and UDP 3478, detects the PC's LAN IPv4 addresses, and prints the exact router forwarding table. Reserve the selected LAN address in your router's DHCP settings so it does not change.

The script deliberately does not attempt to log in to or modify your router: router interfaces and APIs vary, and guessing could change unrelated settings. You must add the three forwarding rules shown by the script.

## 4. Start the server

Install Caddy 2.10 or later and ensure caddy.exe is on PATH. From the repository root, run PowerShell as Administrator:

    .\scripts\home-server\Start-Server.ps1 -AcmeEmail "you@example.com"

The launcher:

1. Queries two public IP discovery services and validates the result as a globally routable IPv4 address. If the services disagree, it stops rather than choosing an uncertain address.
2. Saves the HTTPS server address to `%LOCALAPPDATA%\ListenTogether\server-address.txt`.
3. Starts the published signalling/STUN server and checks its local health endpoint.
4. Starts Caddy with the detected IP and the Let's Encrypt `shortlived` ACME profile.

The public endpoint is `https://YOUR_PUBLIC_IP`. The IP certificate is valid for 160 hours (just over six days), so Caddy must remain installed and running regularly to renew it automatically. Do not disable Caddy's certificate storage or renewal process.

Check locally:

- http://127.0.0.1:5000/healthz should return healthy.
- https://YOUR_PUBLIC_IP/healthz should return healthy after Caddy obtains its certificate.
- Set the desktop app's server URL to the detected `https://YOUR_PUBLIC_IP` address. The launcher saves the detected address in the file above for the host PC.

## 5. Verify router configuration from your friend's network

After the host has started the server, copy the public IPv4 address and send it to your friend. Have them connect their PC to a different internet connection (for example, a mobile hotspot), open PowerShell, and run the diagnostic script from a copy of the repository:

    .\scripts\home-server\Test-HomeServer.ps1 -Server "YOUR_PUBLIC_IP"

The diagnostic tests TCP 80, TCP 443, and an actual STUN Binding request over UDP 3478. It reports each result separately. For every failure, it prints the corresponding router forwarding rule and host-side checks to investigate. It exits with code 1 if any required endpoint is unreachable, making it suitable for troubleshooting or automation.

Run this test from outside the host's home network. Some routers do not support NAT loopback, so testing your own public IP from your home Wi-Fi can report false failures. A TCP port check cannot prove UDP forwarding; the STUN request tests UDP 3478 specifically.

## 6. Share an invite with your friend

Start a session in the desktop app. Use **Copy invite details** in the audio-sharing window to copy the server address and eight-character invite code together. Send that text to your friend. On their desktop app, they select **Paste invite details**; ListenTogether fills in the server address and invite code. They then select **Join session**.

The server address is not a secret. Treat the invite code as a private bearer credential and only share it with the intended listener.

## 7. Test STUN directly (optional)

Run this from a second PC connected to a different network (for example, a laptop on a mobile hotspot), with the repository script available:

    .\scripts\home-server\Test-Stun.ps1 -Server "YOUR_PUBLIC_IP"

A successful result prints the public UDP IP:port mapping observed by STUN. Running this test from inside the same home network may fail if the router does not support NAT loopback; that does not by itself mean external access is broken.

## Troubleshooting

| Symptom | Likely cause / check |
|---|---|
| Public IP detection fails | Check internet access and whether both IP discovery services are reachable. |
| The two IP services disagree | The launcher intentionally stops; retry later rather than issuing a certificate for an uncertain address. |
| Local health check fails | Server process failed, wrong publish path, or port 5000 is occupied. Check the server console and Windows Event Viewer. |
| Local health works, public HTTPS fails | Run Test-HomeServer.ps1 from your friend's network. If TCP 80 or 443 fails, check the exact forwarding rule it reports, Windows Firewall, Caddy logs, and whether another program uses ports 80/443. |
| Caddy cannot obtain a certificate | Public IP must be reachable on TCP 80 from the internet. Check ISP inbound-port restrictions and Caddy logs. |
| HTTPS works but STUN test times out | Run Test-HomeServer.ps1 from your friend's network. Check its UDP 3478 result, the server listener, Windows Firewall and router UDP forwarding. TCP port tests do not test UDP. |
| STUN works but peers cannot connect | STUN only discovers public mappings. Symmetric NAT, VPNs, enterprise firewalls, or restrictive UDP policies can still prevent hole punching. |
| Works on LAN but not mobile data | Public port forwarding, firewall, or upstream NAT issue is likely. Do not use the LAN IP in your friend's server URL. |

### Check for CGNAT

Compare your router's WAN/Internet IPv4 address with the public IPv4 address shown by an IP-check website. If they differ, or the router WAN address is in one of these ranges, it is not a globally routable public IPv4 address:

- 10.0.0.0/8
- 172.16.0.0/12
- 192.168.0.0/16
- 100.64.0.0/10 (commonly used for CGNAT)

If the addresses differ, contact your ISP and ask for a public IPv4 address (dynamic is sufficient). Without one, router port forwarding generally cannot expose this PC directly.

## Current limitations

- STUN and candidate gathering currently use IPv4 only.
- Hosting from home means the service is unavailable when this PC, router, or internet connection is offline.
- If your public IP changes while the server is running, stop and restart the launcher so Caddy and the saved address use the new IP. The next launch detects it automatically.
- IP certificates are short-lived (160 hours). Caddy must be left able to renew them; if the PC is offline for too long, HTTPS may stop working until a certificate can be renewed.
- Session state is held in memory; restarting the server ends active sessions.
- A successful STUN test confirms STUN reachability, not that this pair of networks can establish direct UDP.
- Do not expose plain HTTP port 5000 to the internet.
