using System.Windows;
using ListenTogether.App.ViewModels;
using ListenTogether.Infrastructure.Audio;

namespace ListenTogether.App;

public partial class AudioSourceWindow : Window
{
    private readonly AudioSourceViewModel _viewModel;

    public AudioSourceWindow(Uri serverUri, string displayName)
    {
        InitializeComponent();

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
        if (!string.IsNullOrWhiteSpace(_viewModel.SessionCode))
            Clipboard.SetText(_viewModel.SessionCode);
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        await _viewModel.DisposeAsync();
    }
}
