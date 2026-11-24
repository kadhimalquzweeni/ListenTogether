using System.Windows;
using ListenTogether.App.ViewModels;

namespace ListenTogether.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
