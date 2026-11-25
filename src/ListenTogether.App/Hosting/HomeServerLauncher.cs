using System.Diagnostics;
using System.IO;

namespace ListenTogether.App.Hosting;

public interface IHomeServerLauncher
{
    Task<HomeServerLaunchResult> StartAsync(string acmeEmail, bool hideConsole = false);
    Task<HomeServerStopResult> StopAsync(bool hideConsole = false);
}

public sealed record HomeServerLaunchResult(bool Started, string Message, string? ServerUrl = null);
public sealed record HomeServerStopResult(bool Stopped, string Message);

/// <summary>Starts the repository's complete Windows home-server stack.</summary>
public sealed class HomeServerLauncher : IHomeServerLauncher
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

    public async Task<HomeServerLaunchResult> StartAsync(string acmeEmail, bool hideConsole = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(acmeEmail);

        if (!OperatingSystem.IsWindows())
            return new(false, "The home-server launcher is only available on Windows.");

        var repositoryRoot = FindRepositoryRoot();
        if (repositoryRoot is null)
            return new(false, "Couldn't find scripts\\home-server\\Start-Server.ps1. Run ListenTogether from its repository folder, or set LISTENTOGETHER_HOME_SERVER_ROOT to that folder.");

        var scriptPath = Path.Combine(repositoryRoot, "scripts", "home-server", "Start-Server.ps1");
        if (!File.Exists(scriptPath))
            return new(false, $"The home-server startup script is missing: {scriptPath}");

        var statusDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ListenTogether");
        Directory.CreateDirectory(statusDirectory);
        var statusFile = Path.Combine(statusDirectory, "server-start-status.txt");
        if (File.Exists(statusFile))
            File.Delete(statusFile);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = hideConsole,
            WindowStyle = hideConsole ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };
        if (!hideConsole)
            startInfo.ArgumentList.Add("-NoExit");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-AcmeEmail");
        startInfo.ArgumentList.Add(acmeEmail);
        startInfo.ArgumentList.Add("-StatusFile");
        startInfo.ArgumentList.Add(statusFile);
        if (hideConsole)
            startInfo.ArgumentList.Add("-HideChildWindows");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return new(false, "Windows couldn't open PowerShell to start the home server.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(false, $"Couldn't launch PowerShell: {exception.Message}");
        }

        var deadline = DateTime.UtcNow + StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(statusFile))
            {
                var serverUrl = (await File.ReadAllTextAsync(statusFile)).Trim();
                if (Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) &&
                    uri.Scheme == Uri.UriSchemeHttps)
                {
                    var visibilityMessage = hideConsole
                        ? "The server is running in the background."
                        : "Keep the PowerShell window open while hosting.";
                    return new(true,
                        $"Home server started. Server address set to {serverUrl}. {visibilityMessage}",
                        serverUrl.TrimEnd('/'));
                }
            }

            await Task.Delay(500);
        }

        var troubleshootingMessage = hideConsole
            ? "The PowerShell window was hidden, so switch off the hidden-console option and try again to see startup errors."
            : "Check the PowerShell window for errors.";
        return new(true,
            $"PowerShell was opened, but the server hasn't reported startup yet. {troubleshootingMessage} " +
            "The server requires the published server executable, Caddy 2.10+ on PATH, and working router forwarding.");
    }

    public async Task<HomeServerStopResult> StopAsync(bool hideConsole = false)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, "The home-server launcher is only available on Windows.");

        var repositoryRoot = FindRepositoryRoot();
        if (repositoryRoot is null)
            return new(false, "Couldn't find scripts\\home-server\\Stop-Server.ps1. Run ListenTogether from its repository folder, or set LISTENTOGETHER_HOME_SERVER_ROOT to that folder.");

        var scriptPath = Path.Combine(repositoryRoot, "scripts", "home-server", "Stop-Server.ps1");
        if (!File.Exists(scriptPath))
            return new(false, $"The home-server shutdown script is missing: {scriptPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = hideConsole,
            WindowStyle = hideConsole ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return new(false, "Windows couldn't open PowerShell to stop the home server.");

            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                return new(false, "The shutdown script could not stop every tracked process. Run scripts\\home-server\\Stop-Server.ps1 in PowerShell to see details.");

            return new(true, "ListenTogether server, STUN and Caddy have been stopped.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(false, $"Couldn't launch the shutdown script: {exception.Message}");
        }
    }

    private static string? FindRepositoryRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("LISTENTOGETHER_HOME_SERVER_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot) && HasStartupScript(configuredRoot))
            return Path.GetFullPath(configuredRoot);

        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (HasStartupScript(directory.FullName))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        return null;
    }

    private static bool HasStartupScript(string path) =>
        File.Exists(Path.Combine(path, "scripts", "home-server", "Start-Server.ps1"));
}
