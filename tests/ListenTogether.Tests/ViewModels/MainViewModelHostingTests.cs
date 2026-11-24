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

    private MainViewModel CreateViewModel(IHostReadinessChecker checker) =>
        new(new UserSettingsStore(Path.Combine(_directory, "settings.json")), checker);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
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
            Assert.Equal("http://localhost:5000", serverUri.ToString());
            return Task.FromResult(result);
        }
    }
}
