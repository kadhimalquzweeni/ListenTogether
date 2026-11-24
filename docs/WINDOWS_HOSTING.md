# Hosting ListenTogether directly on Windows

This guide prepares the C# relay to run directly on your own Windows PC. Docker is not required.

## Current implementation

- The relay runs interactively or under the Windows Service Control Manager.
- The server can be published as a self-contained Windows executable.
- Without an HTTPS certificate configured, Kestrel binds only to loopback at http://127.0.0.1:5000. This safe default is not reachable from the internet.
- When an HTTPS certificate is configured, Kestrel binds to all network interfaces on the configured HTTPS port (443 by default).
- The SignalR endpoint remains /hubs/listening; health check is /healthz.

## Publish

Install the .NET 10 SDK, clone the repository, and run from the repository root:

\`\`\`powershell
.\scripts\publish-relay-server.ps1
\`\`\`

The self-contained output is written to artifacts\relay-server.

## Install as a Windows Service

Open PowerShell as Administrator from the repository root:

\`\`\`powershell
.\scripts\install-relay-service.ps1
\`\`\`

The service is installed with automatic startup but is deliberately not started by the installer. This gives you time to configure HTTPS first.

To remove the service (installed files are retained):

\`\`\`powershell
.\scripts\uninstall-relay-service.ps1
\`\`\`

## HTTPS configuration

Set these environment variables for the service process, or edit the installed appsettings.json:

- Hosting__Https__CertificatePath: path to a PFX certificate containing the public IP address in its Subject Alternative Name.
- Hosting__Https__CertificatePassword: PFX password. Prefer a protected machine-level secret rather than committing it to source control.
- Hosting__Https__Port: normally 443.

The next hosting milestone will add ACME certificate issuance and renewal for your public IP. Do not configure router port forwarding or expose the relay before that is complete and tested.

## Local checks

With no certificate configured, start the executable interactively:

\`\`\`powershell
.\artifacts\relay-server\ListenTogether.Server.exe
\`\`\`

On the same PC, check http://127.0.0.1:5000/ and http://127.0.0.1:5000/healthz. The root endpoint should return relay status and the health endpoint should return healthy. Stop the interactive process with Ctrl+C.

## Security notes

- Never disable TLS certificate validation in the desktop client.
- Do not expose the HTTP development listener to the internet.
- Do not put certificate passwords or private keys in Git.
- Keep Windows and .NET dependencies updated.
- Public hosting also requires a stable local IP reservation, router forwarding, and a Windows Firewall rule. Those steps are deferred until HTTPS issuance and renewal are implemented.
