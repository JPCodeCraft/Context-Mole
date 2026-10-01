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
    public bool RequiresReadOnlyCheck => State is AiConnectionState.Conflict or AiConnectionState.ServerUnavailable;
    public AiConnectionState State => _state;
    public string Message => IsBusy ? "Checking current configuration…" : _message;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanChange));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(ActionLabel));
            OnPropertyChanged(nameof(HasDetailMessage));
            OnPropertyChanged(nameof(Message));
            OnPropertyChanged(nameof(IsReadyStatus));
            OnPropertyChanged(nameof(IsWarningStatus));
            OnPropertyChanged(nameof(IsErrorStatus));
        }
    }

    public bool IsConfigured => State is AiConnectionState.Connected or AiConnectionState.Broken;
    public bool HasManagedConfiguration => State is AiConnectionState.Connected or AiConnectionState.UpdateRequired
        or AiConnectionState.Broken;
    public bool CanChange => SupportsAutomaticSetup && !IsBusy &&
        State is (AiConnectionState.Connected or AiConnectionState.Disconnected or AiConnectionState.UpdateRequired
            or AiConnectionState.Broken or AiConnectionState.Conflict or AiConnectionState.ServerUnavailable);
    public bool IsReadyStatus => !IsBusy && State == AiConnectionState.Connected;
    public bool IsWarningStatus => !IsBusy && State == AiConnectionState.UpdateRequired;
    public bool IsErrorStatus => !IsBusy && State is (AiConnectionState.Conflict or AiConnectionState.ServerUnavailable
        or AiConnectionState.Broken);
    public bool HasDetailMessage => IsBusy || State != AiConnectionState.Disconnected;

    public string StatusLabel => State switch
    {
        _ when IsBusy => "Checking",
        AiConnectionState.Connected => "Configured",
        AiConnectionState.UpdateRequired => "Update needed",
        AiConnectionState.Conflict => "Conflict",
        AiConnectionState.ServerUnavailable => "Unavailable",
        AiConnectionState.Broken => "Needs attention",
        AiConnectionState.ManualSetup => "Manual setup",
        _ => "Not configured"
    };

    public string ActionLabel => State switch
    {
        _ when IsBusy => "Working…",
        AiConnectionState.Connected => "Remove",
        AiConnectionState.Broken => "Remove",
        AiConnectionState.UpdateRequired => "Update",
        AiConnectionState.Conflict => "Check again",
        AiConnectionState.ServerUnavailable => "Check again",
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
        OnPropertyChanged(nameof(RequiresReadOnlyCheck));
        OnPropertyChanged(nameof(HasManagedConfiguration));
        OnPropertyChanged(nameof(CanChange));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(IsReadyStatus));
        OnPropertyChanged(nameof(IsWarningStatus));
        OnPropertyChanged(nameof(IsErrorStatus));
        OnPropertyChanged(nameof(HasDetailMessage));
    }
}
