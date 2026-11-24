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
        Closing += (_, _) => viewModel.SaveSettings();
    }

    private void OnPasteInviteDetailsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        try
        {
            if (!Clipboard.ContainsText())
            {
                viewModel.TryApplyInviteDetails(null);
                return;
            }

            viewModel.TryApplyInviteDetails(Clipboard.GetText());
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            MessageBox.Show(this, "Couldn't read the clipboard. Please copy the invite details again and retry.",
                "ListenTogether", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCopyHostingDetailsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel ||
            string.IsNullOrWhiteSpace(viewModel.HostingCheckResult))
            return;

        try
        {
            Clipboard.SetText(viewModel.HostingCheckResult);
            viewModel.StatusMessage = "Hosting check details copied to the clipboard.";
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            MessageBox.Show(this, "Couldn't copy the hosting check details. Please try again.",
                "ListenTogether", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
        if (DataContext is not MainViewModel viewModel)
            return;
        var serverUri = viewModel.ValidateConnectionSettings();
        if (serverUri is null)
            return;

        var window = new ListeningWindow(serverUri, viewModel.DisplayName, viewModel.SessionCode) { Owner = this };
        window.ShowDialog();
    }
}
