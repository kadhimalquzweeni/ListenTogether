using ListenTogether.App.Hosting;
using ListenTogether.App.Networking;
using ListenTogether.App.Settings;
using ListenTogether.App.ViewModels;

namespace ListenTogether.Tests.ViewModels;

public sealed class MainViewModelHostingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ListenTogether.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CheckHostingAsync_WhenReady_ShowsReadyStatusAndReEnablesCommand()
    {
        var checker = new StubChecker(new HostReadinessResult(true, "Both checks passed."));
        var viewModel = CreateViewModel(checker);
        await viewModel.CheckHostingAsync();
        Assert.Contains("READY TO HOST", viewModel.StatusMessage);
        Assert.Contains("Both checks passed", viewModel.StatusMessage);
        Assert.False(viewModel.IsCheckingHosting);
        Assert.False(viewModel.CanCopyHostingResult);
        Assert.Empty(viewModel.HostingCheckResult);
        Assert.True(viewModel.CheckHostingCommand.CanExecute(null));
    }

    [Fact]
    public async Task CheckHostingAsync_WhenNotReady_ShowsFailureStatus()
    {
        var viewModel = CreateViewModel(new StubChecker(new HostReadinessResult(false, "STUN is unreachable.")));
        await viewModel.CheckHostingAsync();
        Assert.Contains("NOT READY TO HOST", viewModel.StatusMessage);
        Assert.Contains("STUN is unreachable", viewModel.StatusMessage);
        Assert.True(viewModel.CanCopyHostingResult);
        Assert.Equal(viewModel.StatusMessage, viewModel.HostingCheckResult);
    }

    [Fact]
    public async Task CheckHostingAsync_WhenCheckerThrows_ExposesExceptionDetailsForCopying()
    {
        var viewModel = CreateViewModel(new ThrowingChecker());
        await viewModel.CheckHostingAsync();

        Assert.Contains("HOST CHECK FAILED", viewModel.StatusMessage);
        Assert.True(viewModel.CanCopyHostingResult);
        Assert.Equal(viewModel.StatusMessage, viewModel.HostingCheckResult);
    }

    [Fact]
    public async Task CheckHostingAsync_WhenServerUrlIsInvalid_DoesNotCallChecker()
    {
        var checker = new StubChecker(new HostReadinessResult(true, "ok"));
        var viewModel = CreateViewModel(checker);
        viewModel.ServerUrl = "not a url";
        await viewModel.CheckHostingAsync();
        Assert.False(checker.Called);
        Assert.Contains("valid server address", viewModel.StatusMessage);
    }

    private MainViewModel CreateViewModel(IHostReadinessChecker checker, IHomeServerLauncher? launcher = null) =>
        new(new UserSettingsStore(Path.Combine(_directory, "settings.json")), checker, launcher);

    [Fact]
    public async Task StartServerAsync_WhenHiddenConsoleSelected_PassesOptionToLauncher()
    {
        var launcher = new StubHomeServerLauncher(new HomeServerLaunchResult(true, "Running in background."));
        var viewModel = CreateViewModel(new StubChecker(new HostReadinessResult(true, "ok")), launcher);
        viewModel.AcmeEmail = "host@example.com";
        viewModel.HideServerConsole = true;

        await viewModel.StartServerAsync();

        Assert.True(launcher.HideConsole);
    }

    [Fact]
    public async Task StartServerAsync_WithValidEmail_StartsLauncherAndUpdatesServerUrl()
    {
        var launcher = new StubHomeServerLauncher(new HomeServerLaunchResult(
            true, "Home server started.", "https://203.0.113.10"));
        var viewModel = CreateViewModel(new StubChecker(new HostReadinessResult(true, "ok")), launcher);
        viewModel.AcmeEmail = "host@example.com";

        await viewModel.StartServerAsync();

        Assert.Equal("host@example.com", launcher.Email);
        Assert.False(launcher.HideConsole);
        Assert.Equal("https://203.0.113.10", viewModel.ServerUrl);
        Assert.Equal("Home server started.", viewModel.StatusMessage);
        Assert.False(viewModel.IsStartingServer);
        Assert.True(viewModel.StartServerCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("host@example.com,attacker@example.com")]
    public async Task StartServerAsync_WithInvalidEmail_DoesNotLaunch(string email)
    {
        var launcher = new StubHomeServerLauncher(new HomeServerLaunchResult(true, "Started."));
        var viewModel = CreateViewModel(new StubChecker(new HostReadinessResult(true, "ok")), launcher);
        viewModel.AcmeEmail = email;

        await viewModel.StartServerAsync();

        Assert.Null(launcher.Email);
        Assert.Contains("valid email", viewModel.StatusMessage);
        Assert.False(viewModel.IsStartingServer);
    }

    [Fact]
    public async Task StartServerAsync_WhenLauncherFails_ExposesFailureForCopying()
    {
        var launcher = new StubHomeServerLauncher(new HomeServerLaunchResult(false, "Caddy is missing."));
        var viewModel = CreateViewModel(new StubChecker(new HostReadinessResult(true, "ok")), launcher);
        viewModel.AcmeEmail = "host@example.com";

        await viewModel.StartServerAsync();

        Assert.Contains("Caddy is missing", viewModel.StatusMessage);
        Assert.True(viewModel.CanCopyHostingResult);
        Assert.Equal(viewModel.StatusMessage, viewModel.HostingCheckResult);
    }

    [Fact]
    public async Task StartServerAsync_WhenLauncherThrows_ReportsException()
    {
        var viewModel = CreateViewModel(
            new StubChecker(new HostReadinessResult(true, "ok")),
            new ThrowingHomeServerLauncher());
        viewModel.AcmeEmail = "host@example.com";

        await viewModel.StartServerAsync();

        Assert.Contains("Couldn't start the home server", viewModel.StatusMessage);
        Assert.True(viewModel.CanCopyHostingResult);
    }


    [Fact]
    public async Task StopServerAsync_StopsLauncherAndReportsSuccess()
    {
        var launcher = new StubHomeServerLauncher(new HomeServerLaunchResult(true, "Started."));
        launcher.StopResult = new HomeServerStopResult(true, "All services stopped.");
        var viewModel = CreateViewModel(new StubChecker(new HostReadinessResult(true, "ok")), launcher);
        viewModel.HideServerConsole = true;

        await viewModel.StopServerAsync();

        Assert.True(launcher.StopCalled);
        Assert.True(launcher.StopHideConsole);
        Assert.Equal("All services stopped.", viewModel.StatusMessage);
        Assert.False(viewModel.IsStoppingServer);
        Assert.True(viewModel.StopServerCommand.CanExecute(null));
        Assert.False(viewModel.CanCopyHostingResult);
    }

    [Fact]
    public async Task StopServerAsync_WhenLauncherFails_ExposesFailureForCopying()
    {
        var launcher = new StubHomeServerLauncher(new HomeServerLaunchResult(true, "Started."));
        launcher.StopResult = new HomeServerStopResult(false, "Some process could not be stopped.");
        var viewModel = CreateViewModel(new StubChecker(new HostReadinessResult(true, "ok")), launcher);

        await viewModel.StopServerAsync();

        Assert.Contains("could not be stopped", viewModel.StatusMessage);
        Assert.True(viewModel.CanCopyHostingResult);
        Assert.False(viewModel.IsStoppingServer);
    }

    [Fact]
    public async Task StopServerAsync_WhenLauncherThrows_ReportsException()
    {
        var viewModel = CreateViewModel(
            new StubChecker(new HostReadinessResult(true, "ok")),
            new ThrowingHomeServerLauncher());
        await viewModel.StopServerAsync();

        Assert.Contains("Couldn't stop the home server", viewModel.StatusMessage);
        Assert.True(viewModel.CanCopyHostingResult);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }

    private sealed class ThrowingHomeServerLauncher : IHomeServerLauncher
    {
        public Task<HomeServerLaunchResult> StartAsync(string acmeEmail, bool hideConsole = false) =>
            throw new InvalidOperationException("Simulated process launch failure.");
        public Task<HomeServerStopResult> StopAsync(bool hideConsole = false) =>
            throw new InvalidOperationException("Simulated shutdown failure.");
    }

    private sealed class StubHomeServerLauncher(HomeServerLaunchResult result) : IHomeServerLauncher
    {
        public string? Email { get; private set; }
        public bool HideConsole { get; private set; }
        public bool StopCalled { get; private set; }
        public bool StopHideConsole { get; private set; }
        public HomeServerStopResult StopResult { get; set; } = new(true, "Stopped.");
        public Task<HomeServerStopResult> StopAsync(bool hideConsole = false)
        {
            StopCalled = true;
            StopHideConsole = hideConsole;
            return Task.FromResult(StopResult);
        }
        public Task<HomeServerLaunchResult> StartAsync(string acmeEmail, bool hideConsole = false)
        {
            Email = acmeEmail;
            HideConsole = hideConsole;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingChecker : IHostReadinessChecker
    {
        public Task<HostReadinessResult> CheckAsync(Uri serverUri, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated network failure.");
    }

    private sealed class StubChecker(HostReadinessResult result) : IHostReadinessChecker
    {
        public bool Called { get; private set; }
        public Task<HostReadinessResult> CheckAsync(Uri serverUri, CancellationToken cancellationToken = default)
        {
            Called = true;
            Assert.Equal(new Uri("http://localhost:5000"), serverUri);
            return Task.FromResult(result);
        }
    }
}
