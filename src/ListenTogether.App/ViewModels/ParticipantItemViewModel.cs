using System.ComponentModel;
using System.Runtime.CompilerServices;
using ListenTogether.Core.Sessions;

namespace ListenTogether.App.ViewModels;

/// <summary>UI state for one participant in the current listening session.</summary>
public sealed class ParticipantItemViewModel : INotifyPropertyChanged
{
    private string _connectionStatus;

    public ParticipantItemViewModel(SessionParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        DisplayName = participant.DisplayName;
        IsHost = participant.IsHost;
        ConnectionId = participant.ConnectionId;
        _connectionStatus = participant.IsHost
            ? "Host · sharing from this PC"
            : "Signalling connected · waiting for direct audio";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string DisplayName { get; }
    public bool IsHost { get; }
    public string? ConnectionId { get; }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set
        {
            if (_connectionStatus == value)
                return;
            _connectionStatus = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionStatus)));
        }
    }

    public void SetDirectConnection(bool connected, string? detail = null)
        => ConnectionStatus = connected
            ? "Secure direct UDP connected"
            : detail ?? "Waiting for direct UDP";

    public void SetConnectionFailure(string detail)
        => ConnectionStatus = string.IsNullOrWhiteSpace(detail) ? "Direct UDP unavailable" : detail;
}
