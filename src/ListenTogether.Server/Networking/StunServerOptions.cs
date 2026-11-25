namespace ListenTogether.Server.Networking;

public sealed class StunServerOptions
{
    public bool Enabled { get; set; } = true;
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 3478;
}
