using ListenTogether.Server.Hubs;
using ListenTogether.Server.Sessions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 16 * 1024;
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
});

builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseHttpsRedirection();
app.MapHealthChecks("/healthz");
app.MapGet("/", () => Results.Ok(new
{
    name = "ListenTogether relay",
    status = "ready",
    protocol = "SignalR over WebSockets with MessagePack"
}));
app.MapHub<ListeningHub>("/hubs/listening");

app.Run();

public partial class Program { }
