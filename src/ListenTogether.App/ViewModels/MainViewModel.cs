using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ListenTogether.App.Networking;
using ListenTogether.App.Settings;

namespace ListenTogether.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _sessionCode = string.Empty;
    private string _serverUrl = "http://localhost:5000";
    private string _displayName = Environment.UserName;
    private string _statusMessage = "Create a session to share your audio, or join a friend's session.";
    private bool _isCheckingHosting;
    private string _hostingCheckResult = string.Empty;
    private bool _canCopyHostingResult;
    private readonly UserSettingsStore _settingsStore;
    private readonly IHostReadinessChecker _hostReadinessChecker;

    public MainViewModel(UserSettingsStore? settingsStore = null, IHostReadinessChecker? hostReadinessChecker = null)
    {
        _settingsStore = settingsStore ?? new UserSettingsStore();
        _hostReadinessChecker = hostReadinessChecker ?? new HostReadinessChecker();
        var settings = _settingsStore.Load();
        _serverUrl = settings.ServerUrl;
        _displayName = settings.DisplayName;

        CreateSessionCommand = new RelayCommand(CreateSession);
        JoinSessionCommand = new RelayCommand(JoinSession, () => !string.IsNullOrWhiteSpace(SessionCode));
        CheckHostingCommand = new MainAsyncRelayCommand(CheckHostingAsync, () => !IsCheckingHosting);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CreateSessionRequested;
    public event EventHandler? JoinSessionRequested;

    public string SessionCode
    {
        get => _sessionCode;
        set
        {
            if (SetProperty(ref _sessionCode, value?.Trim().ToUpperInvariant() ?? string.Empty))
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

    public string HostingCheckResult
    {
        get => _hostingCheckResult;
        private set => SetProperty(ref _hostingCheckResult, value);
    }

    public bool CanCopyHostingResult
    {
        get => _canCopyHostingResult;
        private set => SetProperty(ref _canCopyHostingResult, value);
    }

    public bool IsCheckingHosting
    {
        get => _isCheckingHosting;
        private set
        {
            if (SetProperty(ref _isCheckingHosting, value))
                (CheckHostingCommand as MainAsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public ICommand CreateSessionCommand { get; }
    public ICommand JoinSessionCommand { get; }
    public ICommand CheckHostingCommand { get; }

    public async Task CheckHostingAsync()
    {
        if (IsCheckingHosting)
            return;

        HostingCheckResult = string.Empty;
        CanCopyHostingResult = false;

        if (!Uri.TryCreate(ServerUrl?.Trim(), UriKind.Absolute, out var serverUri) ||
            serverUri.Scheme is not ("http" or "https"))
        {
            StatusMessage = "Enter a valid server address before checking hosting.";
            return;
        }

        IsCheckingHosting = true;
        StatusMessage = "Checking server health and UDP STUN connectivity…";
        try
        {
            var result = await _hostReadinessChecker.CheckAsync(serverUri);
            StatusMessage = (result.CanHost ? "READY TO HOST — " : "NOT READY TO HOST — ") + result.Message;
            HostingCheckResult = result.CanHost ? string.Empty : StatusMessage;
            CanCopyHostingResult = !result.CanHost;
        }
        catch (Exception exception)
        {
            StatusMessage = "HOST CHECK FAILED — " + exception.Message;
            HostingCheckResult = StatusMessage;
            CanCopyHostingResult = true;
        }
        finally
        {
            IsCheckingHosting = false;
        }
    }

    public void SaveSettings()
    {
        try { _settingsStore.Save(new UserSettings(ServerUrl, DisplayName)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusMessage = "Couldn't save settings: " + exception.Message;
        }
    }

    public bool TryApplyInviteDetails(string? text)
    {
        if (!InviteDetailsParser.TryParse(text, out var details) || details is null)
        {
            StatusMessage = "The clipboard doesn't contain valid ListenTogether invite details.";
            return false;
        }

        ServerUrl = details.ServerUrl;
        SessionCode = details.InviteCode;
        StatusMessage = "Invite details loaded. Select Join session when you're ready.";
        return true;
    }

    public Uri? ValidateConnectionSettings()
    {
        if (!Uri.TryCreate(ServerUrl?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            StatusMessage = "Enter a valid server address beginning with http:// or https://.";
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

public sealed class MainAsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public async void Execute(object? parameter) => await execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
