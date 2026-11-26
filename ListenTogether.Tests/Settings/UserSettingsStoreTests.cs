using ListenTogether.App.Settings;

namespace ListenTogether.Tests.Settings;

public sealed class UserSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ListenTogether.Tests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");
    private string DiscoveredAddressPath => Path.Combine(_directory, "server-address.txt");

    [Fact]
    public void Load_WhenNoFileExists_ReturnsDefaults()
    {
        var store = new UserSettingsStore(SettingsPath);

        var settings = store.Load();

        Assert.Equal("http://localhost:5000", settings.ServerUrl);
        Assert.False(string.IsNullOrWhiteSpace(settings.DisplayName));
    }

    [Fact]
    public void Load_WhenServerAddressWasDiscovered_UsesDetectedHttpsAddress()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(DiscoveredAddressPath, "https://203.0.113.42");
        var store = new UserSettingsStore(SettingsPath, DiscoveredAddressPath);

        var settings = store.Load();

        Assert.Equal("https://203.0.113.42", settings.ServerUrl);
    }

    [Fact]
    public void Load_WhenDetectedAddressChanges_OverridesPreviouslySavedServerUrl()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(DiscoveredAddressPath, "https://203.0.113.43");
        var store = new UserSettingsStore(SettingsPath, DiscoveredAddressPath);
        store.Save(new UserSettings("https://203.0.113.42", "Host"));

        var settings = store.Load();

        Assert.Equal("https://203.0.113.43", settings.ServerUrl);
        Assert.Equal("Host", settings.DisplayName);
    }

    [Fact]
    public void Load_WhenDiscoveredAddressIsNotHttps_IgnoresIt()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(DiscoveredAddressPath, "http://203.0.113.42");
        var store = new UserSettingsStore(SettingsPath, DiscoveredAddressPath);

        var settings = store.Load();

        Assert.Equal("http://localhost:5000", settings.ServerUrl);
    }

    [Fact]
    public void SaveAndLoad_PersistRelayUrlAndDisplayName()
    {
        var store = new UserSettingsStore(SettingsPath);
        var expected = new UserSettings("https://listen.example.com", "Listener");

        store.Save(expected);

        Assert.Equal(expected, store.Load());
    }

    [Fact]
    public void Load_WhenJsonIsCorrupt_ReturnsDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{ this is not valid json");

        var settings = new UserSettingsStore(SettingsPath).Load();

        Assert.Equal("http://localhost:5000", settings.ServerUrl);
        Assert.False(string.IsNullOrWhiteSpace(settings.DisplayName));
    }

    [Theory]
    [InlineData("", "Listener")]
    [InlineData("file:///tmp/relay", "Listener")]
    [InlineData("http://localhost:5000", "")]
    public void Save_RejectsInvalidSettings(string serverUrl, string displayName)
    {
        var store = new UserSettingsStore(SettingsPath);

        Assert.Throws<ArgumentException>(() => store.Save(new UserSettings(serverUrl, displayName)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
