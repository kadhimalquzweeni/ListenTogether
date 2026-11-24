using ListenTogether.App.Settings;
using ListenTogether.App.ViewModels;

namespace ListenTogether.Tests.ViewModels;

public sealed class MainViewModelInviteTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ListenTogether.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void TryApplyInviteDetails_UpdatesServerUrlAndSessionCode()
    {
        var viewModel = new MainViewModel(new UserSettingsStore(Path.Combine(_directory, "settings.json")));

        var applied = viewModel.TryApplyInviteDetails(
            "ListenTogether invite\nServer: https://203.0.113.42\nInvite code: AB12CD34");

        Assert.True(applied);
        Assert.Equal("https://203.0.113.42", viewModel.ServerUrl);
        Assert.Equal("AB12CD34", viewModel.SessionCode);
        Assert.Contains("loaded", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryApplyInviteDetails_WhenClipboardIsInvalid_DoesNotChangeConnectionSettings()
    {
        var viewModel = new MainViewModel(new UserSettingsStore(Path.Combine(_directory, "settings.json")));
        var originalServerUrl = viewModel.ServerUrl;

        var applied = viewModel.TryApplyInviteDetails("not an invite");

        Assert.False(applied);
        Assert.Equal(originalServerUrl, viewModel.ServerUrl);
        Assert.Equal(string.Empty, viewModel.SessionCode);
        Assert.Contains("doesn't contain valid", viewModel.StatusMessage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
