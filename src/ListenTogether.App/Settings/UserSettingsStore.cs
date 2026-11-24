using System.IO;
using System.Text.Json;

namespace ListenTogether.App.Settings;

/// <summary>
/// Persists non-sensitive desktop preferences such as the relay URL and display name.
/// </summary>
public sealed class UserSettingsStore
{
    private const string DefaultServerUrl = "http://localhost:5000";
    private readonly string _settingsPath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public UserSettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ListenTogether",
            "settings.json");
    }

    public UserSettings Load()
    {
        if (!File.Exists(_settingsPath))
            return CreateDefaults();

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<UserSettings>(json);
            if (settings is null ||
                !Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") ||
                string.IsNullOrWhiteSpace(settings.DisplayName) ||
                settings.DisplayName.Trim().Length > 32)
            {
                return CreateDefaults();
            }

            return new UserSettings(uri.ToString().TrimEnd('/'), settings.DisplayName.Trim());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return CreateDefaults();
        }
    }

    public void Save(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The relay URL must be an absolute HTTP or HTTPS URL.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.DisplayName) || settings.DisplayName.Trim().Length > 32)
            throw new ArgumentException("Display names must contain between 1 and 32 characters.", nameof(settings));

        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("The settings path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = _settingsPath + ".tmp";
        try
        {
            var normalized = new UserSettings(uri.ToString().TrimEnd('/'), settings.DisplayName.Trim());
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static UserSettings CreateDefaults()
    {
        var displayName = Environment.UserName;
        if (displayName.Length > 32)
            displayName = displayName[..32];

        return new UserSettings(DefaultServerUrl, displayName);
    }
}

public sealed record UserSettings(string ServerUrl, string DisplayName);
