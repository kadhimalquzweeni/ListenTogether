using System.Windows;
using ListenTogether.App.ViewModels;

namespace ListenTogether.App;

public partial class ListeningWindow : Window
{
    private readonly ListeningViewModel _viewModel;

    public ListeningWindow(Uri serverUri, string displayName, string sessionCode)
    {
        InitializeComponent();

        _viewModel = new ListeningViewModel(serverUri, displayName, sessionCode);
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.ConnectAsync();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        await _viewModel.DisposeAsync();
    }
}
