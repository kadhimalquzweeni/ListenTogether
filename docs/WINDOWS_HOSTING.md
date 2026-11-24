# Hosting ListenTogether directly on Windows

This guide prepares the C# signalling and STUN service to run directly on your own Windows PC. Docker is not required.

## Current implementation

- The signalling server runs interactively or under the Windows Service Control Manager.
- The server can be published as a self-contained Windows executable.
- Without an HTTPS certificate configured, Kestrel binds only to loopback at http://127.0.0.1:5000. This safe default is not reachable from the internet.
- When an HTTPS certificate is configured, Kestrel binds to all network interfaces on the configured HTTPS port (443 by default).
- The SignalR endpoint remains /hubs/listening; health check is /healthz.
- A lightweight RFC 8489 STUN Binding service runs alongside the signalling server over UDP, on port 3478 by default. It reports the source IP address and port observed by the server; it does not relay media and is not a TURN server.

## Publish

Install the .NET 10 SDK, clone the repository, and run from the repository root:

```powershell
.\scripts\publish-relay-server.ps1
```

The self-contained output is written to artifacts\relay-server.

## Install as a Windows Service

Open PowerShell as Administrator from the repository root:

```powershell
.\scripts\install-relay-service.ps1
```

The service is installed with automatic startup but is deliberately not started by the installer. This gives you time to configure HTTPS first.

To remove the service (installed files are retained):

```powershell
.\scripts\uninstall-relay-service.ps1
```

## STUN configuration

STUN is enabled by default and listens on all IPv4 interfaces at UDP port 3478.

Configuration is available under Hosting:Stun:

- Enabled: true or false.
- ListenAddress: address to bind (default 0.0.0.0).
- Port: UDP port (default 3478).

The STUN endpoint will be:

```text
stun:<your-public-ip>:3478
```

For the current home-hosting setup, the public IPv4 address previously observed was 51.7.230.53, so the initial endpoint would be stun:51.7.230.53:3478. This address can change if the ISP changes your public IP.

STUN does not use HTTPS, a domain name, or a TLS certificate. It uses UDP. To make it reachable from outside your home network, a later deployment step must create:
- A Windows Defender Firewall inbound rule for UDP 3478.
- A router port-forward for UDP 3478 to the PC's reserved LAN address.
- A client configuration pointing to the public STUN endpoint.

Do not open or forward the port until the local STUN smoke test and the external connectivity check have been completed. The service binds locally, but router and firewall exposure remain under your control.

## HTTPS configuration

Set these environment variables for the service process, or edit the installed appsettings.json:

- Hosting__Https__CertificatePath: path to a PFX certificate containing the public IP address in its Subject Alternative Name.
- Hosting__Https__CertificatePassword: PFX password. Prefer a protected machine-level secret rather than committing it to source control.
- Hosting__Https__Port: normally 443.

The server's HTTP development listener remains loopback-only when no certificate is configured.

## Local checks

With no certificate configured, start the executable interactively:

```powershell
.\artifacts\relay-server\ListenTogether.Server.exe
```

On the same PC, check http://127.0.0.1:5000/ and http://127.0.0.1:5000/healthz. The root endpoint should return signalling server status and the health endpoint should return healthy. Stop the interactive process with Ctrl+C.

A local STUN Binding request can be sent to 127.0.0.1:3478/UDP using a STUN diagnostic client. A successful response should contain an XOR-MAPPED-ADDRESS attribute with the source address and port observed by the local server.

## Security notes

- The STUN listener only answers well-formed STUN Binding requests up to 512 bytes; it does not accept arbitrary commands or forward traffic.
- Never disable TLS certificate validation in the desktop client.
- Do not expose the HTTP development listener to the internet.
- Do not put certificate passwords or private keys in Git.
- Keep Windows and .NET dependencies updated.
- Public hosting also requires a stable local IP reservation, router forwarding, and Windows Firewall rules. Do not expose the signalling server until HTTPS issuance and renewal are complete.
