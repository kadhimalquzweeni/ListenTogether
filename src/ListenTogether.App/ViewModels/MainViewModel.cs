using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace ListenTogether.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _sessionCode = string.Empty;
    private string _serverUrl = "http://localhost:5000";
    private string _displayName = Environment.UserName;
    private string _statusMessage = "Create a session to share your audio, or join a friend's session.";

    public MainViewModel()
    {
        CreateSessionCommand = new RelayCommand(CreateSession);
        JoinSessionCommand = new RelayCommand(JoinSession, () => !string.IsNullOrWhiteSpace(SessionCode));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CreateSessionRequested;
    public event EventHandler? JoinSessionRequested;

    public string SessionCode
    {
        get => _sessionCode;
        set
        {
            if (SetProperty(ref _sessionCode, value.Trim().ToUpperInvariant()))
                (JoinSessionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string ServerUrl
    {
        get => _serverUrl;
        set => SetProperty(ref _serverUrl, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public ICommand CreateSessionCommand { get; }
    public ICommand JoinSessionCommand { get; }

    public Uri? ValidateConnectionSettings()
    {
        if (!Uri.TryCreate(ServerUrl?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            StatusMessage = "Enter a valid relay URL beginning with http:// or https://.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Trim().Length > 32)
        {
            StatusMessage = "Enter a display name between 1 and 32 characters.";
            return null;
        }

        return uri;
    }

    private void CreateSession()
    {
        var serverUri = ValidateConnectionSettings();
        if (serverUri is null)
            return;

        StatusMessage = "Choose which audio sources you want to share.";
        CreateSessionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void JoinSession()
    {
        var serverUri = ValidateConnectionSettings();
        if (serverUri is null)
            return;

        if (SessionCode.Length != 8)
        {
            StatusMessage = "Session codes contain eight characters.";
            return;
        }

        StatusMessage = $"Connecting to session {SessionCode}…";
        JoinSessionRequested?.Invoke(this, EventArgs.Empty);
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
