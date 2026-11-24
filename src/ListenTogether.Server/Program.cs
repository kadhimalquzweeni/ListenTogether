using System.Security.Cryptography.X509Certificates;
using ListenTogether.Server.Hubs;
using ListenTogether.Server.Sessions;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "ListenTogether Relay");

var certificatePath = builder.Configuration["Hosting:Https:CertificatePath"];
if (!string.IsNullOrWhiteSpace(certificatePath))
{
    var resolvedPath = Path.IsPathRooted(certificatePath)
        ? certificatePath
        : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, certificatePath));
    if (!File.Exists(resolvedPath))
        throw new FileNotFoundException("The configured HTTPS certificate was not found.", resolvedPath);

    var certificate = new X509Certificate2(
        resolvedPath,
        builder.Configuration["Hosting:Https:CertificatePassword"],
        X509KeyStorageFlags.EphemeralKeySet);
    if (!certificate.HasPrivateKey)
    {
        certificate.Dispose();
        throw new InvalidOperationException("The configured HTTPS certificate does not contain a private key.");
    }

    var port = builder.Configuration.GetValue("Hosting:Https:Port", 443);
    builder.WebHost.ConfigureKestrel(options =>
        options.ListenAnyIP(port, listen => listen.UseHttps(certificate)));
}
else
{
    // Safe-by-default: without a certificate the relay is loopback-only.
    var port = builder.Configuration.GetValue("Hosting:Http:Port", 5000);
    builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));
}

builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 16 * 1024;
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
});
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/healthz");
app.MapGet("/", () => Results.Ok(new
{
    name = "ListenTogether relay",
    status = "ready",
    protocol = "SignalR over WebSockets"
}));
app.MapHub<ListeningHub>("/hubs/listening");
app.Run();

public partial class Program { }
