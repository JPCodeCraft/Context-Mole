using ContextMole.Infrastructure;

namespace ContextMole.App.UI.ViewModels;

internal enum AiConnectionOperation { Checking, Configuring, Updating, Removing }

internal sealed class AiConnectionItemViewModel(AiClientDefinition client) : ViewModelBase
{
    private AiConnectionStatus _snapshot = new(client, client.SupportsAutomaticSetup
        ? AiConnectionState.Disconnected : AiConnectionState.ManualSetup,
        client.SupportsAutomaticSetup ? "Checking configuration…" : "Follow the manual setup instructions in the README.");
    private bool _isBusy = client.SupportsAutomaticSetup;
    private bool _explicitlyChosen;
    private AiConnectionOperation _operation = AiConnectionOperation.Checking;

    public AiClientDefinition Client { get; } = client;
    public AiConnectionStatus Snapshot => _snapshot;
    public string Id => Client.Id;
    public string DisplayName => Client.DisplayName;
    public string Description => Client.Description;
    public bool SupportsAutomaticSetup => Client.SupportsAutomaticSetup;
    public bool RequiresManualSetup => !SupportsAutomaticSetup;
    public bool CanShowAutomaticSetupAction => SupportsAutomaticSetup && !IsUnsupportedPlatform;
    public bool IsUnsupportedPlatform => State == AiConnectionState.UnsupportedPlatform;
    public bool RequiresReadOnlyCheck => State is AiConnectionState.Conflict or AiConnectionState.ServerUnavailable;
    public AiConnectionState State => _snapshot.State;
    public string ConfigPath => _snapshot.ConfigPath ?? string.Empty;
    public bool HasConfigPath => !string.IsNullOrWhiteSpace(ConfigPath);
    public string Message => IsBusy ? _operation switch
    {
        AiConnectionOperation.Configuring => "Configuring this AI tool…",
        AiConnectionOperation.Updating => "Updating this AI tool's configuration…",
        AiConnectionOperation.Removing => "Removing the managed Context Mole configuration…",
        _ => "Checking current configuration without changing it…"
    } : _snapshot.Message;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            NotifyPresentationChanged();
        }
    }

    public bool IsConfigured => State is AiConnectionState.Connected or AiConnectionState.Broken;
    public bool HasManagedConfiguration => State is AiConnectionState.Connected or AiConnectionState.UpdateRequired
        or AiConnectionState.Broken;
    public bool CanChange => SupportsAutomaticSetup && !IsBusy && !IsUnsupportedPlatform &&
        State is (AiConnectionState.Connected or AiConnectionState.Disconnected or AiConnectionState.UpdateRequired
            or AiConnectionState.Broken or AiConnectionState.Conflict or AiConnectionState.ServerUnavailable);
    public bool IsReadyStatus => !IsBusy && State == AiConnectionState.Connected;
    public bool IsWarningStatus => !IsBusy && State == AiConnectionState.UpdateRequired;
    public bool IsErrorStatus => !IsBusy && State is (AiConnectionState.Conflict or AiConnectionState.ServerUnavailable
        or AiConnectionState.Broken);
    public bool NeedsAttention => (HasManagedConfiguration || _explicitlyChosen) &&
        (IsWarningStatus || IsErrorStatus);
    public bool HasDetailMessage => IsBusy || State != AiConnectionState.Disconnected;

    public string StatusLabel => State switch
    {
        _ when IsBusy => _operation switch
        {
            AiConnectionOperation.Configuring => "Configuring",
            AiConnectionOperation.Updating => "Updating",
            AiConnectionOperation.Removing => "Removing",
            _ => "Checking"
        },
        AiConnectionState.Connected => "Configured",
        AiConnectionState.UpdateRequired => "Update needed",
        AiConnectionState.Conflict => "Conflict",
        AiConnectionState.ServerUnavailable => "Server unavailable",
        AiConnectionState.UnsupportedPlatform => "Not available on this platform",
        AiConnectionState.Broken => "Needs attention",
        AiConnectionState.ManualSetup => "Manual setup",
        _ => "Not configured"
    };

    public string ActionLabel => State switch
    {
        _ when IsBusy => "Working…",
        AiConnectionState.Connected or AiConnectionState.Broken => "Remove",
        AiConnectionState.UpdateRequired => "Update",
        AiConnectionState.Conflict or AiConnectionState.ServerUnavailable => "Check again",
        AiConnectionState.UnsupportedPlatform => "Unavailable",
        _ => "Configure"
    };

    public void BeginOperation(AiConnectionOperation operation, bool explicitlyChosen = false)
    {
        _operation = operation;
        _explicitlyChosen |= explicitlyChosen;
        IsBusy = true;
        NotifyPresentationChanged();
    }

    public void Apply(AiConnectionStatus status)
    {
        if (!string.Equals(status.Client.Id, Id, StringComparison.Ordinal))
            throw new ArgumentException("The connection status belongs to a different client.", nameof(status));
        if (_snapshot == status) return;
        _snapshot = status;
        if (status.State == AiConnectionState.Disconnected) _explicitlyChosen = false;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(ConfigPath));
        OnPropertyChanged(nameof(HasConfigPath));
        OnPropertyChanged(nameof(IsUnsupportedPlatform));
        OnPropertyChanged(nameof(CanShowAutomaticSetupAction));
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(RequiresReadOnlyCheck));
        OnPropertyChanged(nameof(HasManagedConfiguration));
        NotifyPresentationChanged();
    }

    private void NotifyPresentationChanged()
    {
        OnPropertyChanged(nameof(CanChange));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(IsReadyStatus));
        OnPropertyChanged(nameof(IsWarningStatus));
        OnPropertyChanged(nameof(IsErrorStatus));
        OnPropertyChanged(nameof(NeedsAttention));
        OnPropertyChanged(nameof(HasDetailMessage));
    }
}
