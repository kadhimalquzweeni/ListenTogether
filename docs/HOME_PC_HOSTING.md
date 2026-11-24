# Host ListenTogether on a Windows home PC

This guide runs the signalling service and self-hosted STUN service on a Windows PC. Caddy provides public HTTPS and automatically obtains and renews a TLS certificate. The server does not need to forward audio for direct peer-to-peer media.

> This makes your PC a public internet server. Keep Windows updated, use a strong account password, do not expose the development HTTP endpoint, and only forward the ports listed below. Your PC must remain powered on and connected to the internet.

## Requirements

- Windows 10/11 x64.
- .NET 10 SDK to publish locally, or the Windows x64 server artifact from GitHub Actions.
- Caddy for Windows (official Caddy executable).
- A free DuckDNS hostname (for example, `yourname.duckdns.org`) configured with the scripts below.
- Router administrator access and a public IPv4 address. If your router's WAN IPv4 differs from the public IPv4 reported by an IP-check website, your ISP may use CGNAT; ordinary port forwarding will not work through CGNAT.
- A static/reserved LAN IPv4 address for the server PC.

## 1. Publish the server

From the repository root, run:

    dotnet publish src/ListenTogether.Server/ListenTogether.Server.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/server

The server defaults to loopback HTTP on 127.0.0.1:5000, while STUN binds to 0.0.0.0:3478/UDP. This is intentional: Caddy handles public HTTPS, and STUN is exposed directly through the router.

## 2. Configure DNS and router forwarding

### Free hostname with DuckDNS

1. Create a free account at [DuckDNS](https://www.duckdns.org/).
2. Sign in and choose an available subdomain, for example `listentogether-home.duckdns.org`.
3. Copy your DuckDNS account token. Do not share it or commit it to GitHub.
4. From the repository root, run this in PowerShell:

       .\scripts\home-server\Configure-DuckDns.ps1 -Subdomain "your-subdomain"

   Paste the token when prompted. It is stored encrypted using Windows DPAPI for your current Windows account.
5. Start the updater in a separate PowerShell window and leave it running:

       .\scripts\home-server\Update-DuckDns.ps1

   It updates the public IPv4 record every five minutes. To test a single update, use `-Once`.

The hostname will be `your-subdomain.duckdns.org`. DuckDNS updates the address to your current public IPv4; it does not bypass CGNAT or make an unreachable router reachable.

Reserve a fixed LAN address for the server PC in your router. Forward these ports to that LAN address:

| Protocol | External port | Internal port | Purpose |
|---|---:|---:|---|
| TCP | 80 | 80 | Caddy certificate validation / HTTP redirect |
| TCP | 443 | 443 | HTTPS and SignalR WebSockets |
| UDP | 3478 | 3478 | STUN binding requests |

Do not forward TCP 5000. It is deliberately loopback-only.

## 3. Configure Windows Firewall

Open PowerShell as Administrator in the repository root:

    .\scripts\home-server\Configure-Firewall.ps1

This creates inbound rules for TCP 80/443 and UDP 3478. It does not configure the router.

## 4. Start the server

Install Caddy and ensure caddy.exe is on PATH. From the repository root, run PowerShell as Administrator:

    .\scripts\home-server\Start-Server.ps1 -Domain "your-subdomain.duckdns.org" -AcmeEmail "you@example.com"

Or pass -CaddyExe with the full path to caddy.exe. Keep the PowerShell window open. The script starts the published server, checks its local health endpoint, and starts Caddy. Caddy needs working public DNS and inbound TCP 80/443 to obtain its certificate.

Check locally:
- http://127.0.0.1:5000/healthz should return healthy.
- https://your-subdomain.duckdns.org/healthz should return healthy after Caddy obtains its certificate.
- Set the desktop app's signalling server URL to https://your-subdomain.duckdns.org.

## 5. Test STUN from outside your home network

Run this from a second PC connected to a different network (for example, a laptop on a mobile hotspot), with the repository script available:

    .\scripts\home-server\Test-Stun.ps1 -Server "your-subdomain.duckdns.org"

A successful result prints the public UDP IP:port mapping observed by STUN. Running this test from inside the same home network may fail if the router does not support NAT loopback; that does not by itself mean external access is broken.

## Troubleshooting

| Symptom | Likely cause / check |
|---|---|
| Local health check fails | Server process failed, wrong publish path, or port 5000 is occupied. Check the server console and Windows Event Viewer. |
| Local health works, public HTTPS fails | Check DNS A record, router TCP 80/443 forwarding, Windows Firewall, Caddy logs, and whether another program uses ports 80/443. |
| Caddy cannot obtain a certificate | DNS must point to this home connection and public inbound TCP 80/443 must reach Caddy. Check whether the ISP blocks inbound ports. |
| HTTPS works but STUN test times out | Check server is listening on UDP 3478, Windows Firewall, router UDP forwarding, DNS A record, and CGNAT. TCP port tests do not test UDP. |
| STUN works but peers cannot connect | STUN only discovers public mappings. Symmetric NAT, carrier-grade NAT, VPNs, enterprise firewalls, or restrictive UDP policies can still prevent hole punching. |
| Works on LAN but not mobile data | Public DNS, port forwarding, firewall or CGNAT issue is likely. Do not use the LAN IP in the remote client's server URL. |

### Check for CGNAT

Compare the router's WAN/Internet IPv4 address with the public IPv4 address shown by an IP-check website. If the router WAN address is in one of these ranges, it is not a globally routable public IPv4 address:

- 10.0.0.0/8
- 172.16.0.0/12
- 192.168.0.0/16
- 100.64.0.0/10 (commonly used for CGNAT)

If the addresses differ, or the WAN address is in one of those ranges, contact your ISP and ask for a public IPv4 address (dynamic is sufficient). Without one, router port forwarding generally cannot expose this PC directly.

## Current limitations

- STUN and candidate gathering currently use IPv4 only.
- Hosting from home means the service is unavailable when this PC, router, or internet connection is offline.
- A changing public IP requires dynamic DNS updates.
- Session state is held in memory; restarting the server ends active sessions.
- A successful STUN test confirms STUN reachability, not that every pair of participant networks can establish direct UDP.
- Do not expose plain HTTP port 5000 to the internet.
