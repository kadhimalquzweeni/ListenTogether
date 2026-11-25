using System.Windows;
using ListenTogether.App.ViewModels;
using ListenTogether.Infrastructure.Audio;

namespace ListenTogether.App;

public partial class AudioSourceWindow : Window
{
    private readonly AudioSourceViewModel _viewModel;
    private readonly Uri _serverUri;

    public AudioSourceWindow(Uri serverUri, string displayName)
    {
        InitializeComponent();
        _serverUri = serverUri ?? throw new ArgumentNullException(nameof(serverUri));

        _viewModel = new AudioSourceViewModel(
            new WindowsAudioSourceDiscovery(),
            new WasapiAudioCaptureFactory(),
            serverUri,
            displayName);

        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshAsync();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCopyCodeClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.SessionCode))
            return;

        var details = $"ListenTogether invite\nServer: {_serverUri.ToString().TrimEnd('/')}\nInvite code: {_viewModel.SessionCode}";
        System.Windows.Clipboard.SetText(details);
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        try { await _viewModel.DisposeAsync(); }
        catch { /* The window is already closing; teardown has been attempted. */ }
    }
}
