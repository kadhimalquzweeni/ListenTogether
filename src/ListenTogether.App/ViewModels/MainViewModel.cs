using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace ListenTogether.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _sessionCode = string.Empty;
    private string _statusMessage = "Create a session to share your audio, or join a friend's session.";

    public MainViewModel()
    {
        CreateSessionCommand = new RelayCommand(CreateSession);
        JoinSessionCommand = new RelayCommand(JoinSession, () => !string.IsNullOrWhiteSpace(SessionCode));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CreateSessionRequested;

    public string SessionCode
    {
        get => _sessionCode;
        set
        {
            if (SetProperty(ref _sessionCode, value))
                (JoinSessionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public ICommand CreateSessionCommand { get; }
    public ICommand JoinSessionCommand { get; }

    private void CreateSession()
    {
        StatusMessage = "Choose which audio sources you want to share.";
        CreateSessionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void JoinSession()
    {
        if (string.IsNullOrWhiteSpace(SessionCode))
            return;

        StatusMessage = "Session joining will be available when the secure session service is implemented.";
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
