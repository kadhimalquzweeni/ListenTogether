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
        DataContext = viewModel;
    }

    private void OnCreateSessionRequested(object? sender, EventArgs e)
    {
        var window = new AudioSourceWindow { Owner = this };
        window.ShowDialog();
    }
}
