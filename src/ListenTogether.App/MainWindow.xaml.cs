using System.Windows;
using ListenTogether.App.ViewModels;

namespace ListenTogether.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var viewModel = new MainViewModel();
        viewModel.CreateSessionRequested += OnCreateSessionRequested;
        viewModel.JoinSessionRequested += OnJoinSessionRequested;
        DataContext = viewModel;
    }

    private void OnCreateSessionRequested(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;
        var serverUri = viewModel.ValidateConnectionSettings();
        if (serverUri is null)
            return;

        var window = new AudioSourceWindow(serverUri, viewModel.DisplayName) { Owner = this };
        window.ShowDialog();
    }

    private void OnJoinSessionRequested(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || !viewModel.ValidateConnectionSettings().TryGetTarget(out var serverUri))
            return;

        var window = new ListeningWindow(serverUri, viewModel.DisplayName, viewModel.SessionCode) { Owner = this };
        window.ShowDialog();
    }
}
