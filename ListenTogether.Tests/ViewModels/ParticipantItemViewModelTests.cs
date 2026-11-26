using ListenTogether.App.ViewModels;
using ListenTogether.Core.Sessions;

namespace ListenTogether.Tests.ViewModels;

public sealed class ParticipantItemViewModelTests
{
    [Fact]
    public void Constructor_ForHost_ShowsLocalSharingStatus()
    {
        var viewModel = new ParticipantItemViewModel(new SessionParticipant("Host PC", true, "host-connection"));

        Assert.Equal("Host PC", viewModel.DisplayName);
        Assert.True(viewModel.IsHost);
        Assert.Equal("host-connection", viewModel.ConnectionId);
        Assert.Equal("Host · sharing from this PC", viewModel.ConnectionStatus);
    }

    [Fact]
    public void Constructor_ForListener_ShowsWaitingForDirectAudioStatus()
    {
        var viewModel = new ParticipantItemViewModel(new SessionParticipant("Listener", false, "listener-connection"));

        Assert.Equal("Signalling connected · waiting for direct audio", viewModel.ConnectionStatus);
    }

    [Fact]
    public void SetDirectConnection_WhenConnected_UpdatesStatusAndRaisesNotification()
    {
        var viewModel = new ParticipantItemViewModel(new SessionParticipant("Listener", false, "listener-connection"));
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.SetDirectConnection(true);

        Assert.Equal("Secure direct UDP connected", viewModel.ConnectionStatus);
        Assert.Contains(nameof(ParticipantItemViewModel.ConnectionStatus), changedProperties);
    }

    [Fact]
    public void SetConnectionFailure_UsesProvidedFailureDetail()
    {
        var viewModel = new ParticipantItemViewModel(new SessionParticipant("Listener", false, "listener-connection"));

        viewModel.SetConnectionFailure("Direct UDP connection failed");

        Assert.Equal("Direct UDP connection failed", viewModel.ConnectionStatus);
    }

    [Fact]
    public void SetConnectionFailure_WhenDetailIsBlank_UsesFallbackStatus()
    {
        var viewModel = new ParticipantItemViewModel(new SessionParticipant("Listener", false, "listener-connection"));

        viewModel.SetConnectionFailure(" ");

        Assert.Equal("Direct UDP unavailable", viewModel.ConnectionStatus);
    }
}