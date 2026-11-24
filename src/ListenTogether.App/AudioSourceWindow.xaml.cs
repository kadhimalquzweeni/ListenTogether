using System.Windows;
using ListenTogether.App.ViewModels;
using ListenTogether.Infrastructure.Audio;

namespace ListenTogether.App;

public partial class AudioSourceWindow : Window
{
    private readonly AudioSourceViewModel _viewModel;

    public AudioSourceWindow()
    {
        InitializeComponent();

        _viewModel = new AudioSourceViewModel(
            new WindowsAudioSourceDiscovery(),
            new WasapiAudioCaptureFactory());

        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshAsync();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();\n\n    private async void OnClosed(object? sender, EventArgs e)
    {
        await _viewModel.DisposeAsync();
    }
}
