using ContextMole.Infrastructure;

namespace ContextMole.App.UI.ViewModels;

internal sealed class AiConnectionItemViewModel(AiClientDefinition client) : ViewModelBase
{
    private AiConnectionState _state = client.SupportsAutomaticSetup
        ? AiConnectionState.Disconnected
        : AiConnectionState.ManualSetup;
    private string _message = client.SupportsAutomaticSetup
        ? "Checking configuration…"
        : "Follow the manual setup instructions in the README.";
    private bool _isBusy = client.SupportsAutomaticSetup;

    public AiClientDefinition Client { get; } = client;
    public string Id => Client.Id;
    public string DisplayName => Client.DisplayName;
    public string Description => Client.Description;
    public bool SupportsAutomaticSetup => Client.SupportsAutomaticSetup;
    public bool RequiresManualSetup => !SupportsAutomaticSetup;
    public AiConnectionState State => _state;
    public string Message => _message;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanChange));
            OnPropertyChanged(nameof(ActionLabel));
        }
    }

    public bool IsConfigured => State is AiConnectionState.Connected or AiConnectionState.Broken;
    public bool CanChange => SupportsAutomaticSetup && !IsBusy &&
        State is (AiConnectionState.Connected or AiConnectionState.Disconnected or AiConnectionState.UpdateRequired
            or AiConnectionState.Broken);

    public string StatusLabel => State switch
    {
        AiConnectionState.Connected => "Configured",
        AiConnectionState.UpdateRequired => "Update needed",
        AiConnectionState.Conflict => "Conflict",
        AiConnectionState.ServerUnavailable => "Unavailable",
        AiConnectionState.Broken => "Broken",
        AiConnectionState.ManualSetup => "Manual setup",
        _ => "Not configured"
    };

    public string ActionLabel => State switch
    {
        _ when IsBusy => "Working…",
        AiConnectionState.Connected => "Remove",
        AiConnectionState.Broken => "Remove",
        AiConnectionState.UpdateRequired => "Update",
        _ => "Configure"
    };

    public void Apply(AiConnectionStatus status)
    {
        if (!string.Equals(status.Client.Id, Id, StringComparison.Ordinal))
            throw new ArgumentException("The connection status belongs to a different client.", nameof(status));

        var stateChanged = SetProperty(ref _state, status.State, nameof(State));
        SetProperty(ref _message, status.Message, nameof(Message));
        if (!stateChanged) return;
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(CanChange));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(ActionLabel));
    }
}