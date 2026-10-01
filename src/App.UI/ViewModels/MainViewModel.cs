using System.Collections.ObjectModel;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using ContextMole.Core;
using ContextMole.Indexing;
using ContextMole.Infrastructure;

namespace ContextMole.App.UI.ViewModels;

internal enum MainSection
{
    Projects,
    Settings,
}

internal partial class MainViewModel : ViewModelBase
{
    private readonly IIndexWriter _writer;
    private readonly ISearchStore _store;
    private readonly IOcrEngine _ocrEngine;
    private readonly IEmbeddingGenerator _embeddingGenerator;
    private readonly IEmbeddingModelSettings _embeddingModelSettings;
    private readonly ICpuUsageSettings _cpuUsageSettings;
    private readonly WindowsStartupService _windowsStartup;
    private readonly ProjectOrderService _projectOrder;
    private readonly GraniteModelInstaller _modelInstaller;
    private readonly AiConnectionsService _aiConnections;
    private readonly IndexingActivityTracker _indexingActivities;
    private readonly IProjectIndexingControl _projectIndexingControl;
    private readonly EmbeddingPolicyRefreshTracker _embeddingPolicyRefreshes;
    private readonly ApplicationUpdateService _applicationUpdates;
    private readonly WindowsUninstallService _windowsUninstall;
    private readonly Dictionary<string, int> _aiConnectionCatalogOrder = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Task> _projectPauseDrains = [];
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _errorRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _aiRefreshGate = new(1, 1);
    private readonly AppPresentationState _presentation = new();
    private AppStatus _appStatus = new("Loading local indexes…", "Reading projects.", AppStatusTone.Busy);
    private OperationNotification? _visibleNotification;
    private long _semanticRefreshSequence;
    private DateTimeOffset _nextAiRefreshUtc = DateTimeOffset.MinValue;
    private CancellationTokenSource? _polling;
    private Task? _pollingTask;
    private bool? _reportedOcrAvailable;
    private string? _reportedOcrMessage;
    private bool _hasAnyActiveIndexingItems;
    private Guid? _fileTypeCountsProjectId;
    private long _fileTypeCountsGeneration = -1;
    private int _fileTypeCountsDocumentCount = -1;
    private DateTimeOffset _nextFileTypeRefreshUtc = DateTimeOffset.MinValue;
    private Guid? _semanticStatusProjectId;
    private long _semanticStatusGeneration = -1;
    private string? _semanticStatusPolicyKey;
    private bool _semanticStatusModelAvailable;
    private DateTimeOffset _nextSemanticStatusRefreshUtc = DateTimeOffset.MinValue;
    private bool _isProjectReordering;

    public MainViewModel(
        IIndexWriter writer,
        ISearchStore store,
        IOcrEngine ocrEngine,
        IEmbeddingGenerator embeddingGenerator,
        IEmbeddingModelSettings embeddingModelSettings,
        ICpuUsageSettings cpuUsageSettings,
        WindowsStartupService windowsStartup,
        ProjectOrderService projectOrder,
        GraniteModelInstaller modelInstaller,
        AiConnectionsService aiConnections,
        IndexingActivityTracker indexingActivities,
        IProjectIndexingControl projectIndexingControl,
        EmbeddingPolicyRefreshTracker embeddingPolicyRefreshes,
        ApplicationUpdateService applicationUpdates,
        WindowsUninstallService windowsUninstall,
        bool initializeWindowsStartup = true)
    {
        _writer = writer;
        _store = store;
        _ocrEngine = ocrEngine;
        _embeddingGenerator = embeddingGenerator;
        _embeddingModelSettings = embeddingModelSettings;
        _cpuUsageSettings = cpuUsageSettings;
        _windowsStartup = windowsStartup;
        if (initializeWindowsStartup) _windowsStartup.Initialize();
        _projectOrder = projectOrder;
        _modelInstaller = modelInstaller;
        _aiConnections = aiConnections;
        _indexingActivities = indexingActivities;
        _projectIndexingControl = projectIndexingControl;
        _embeddingPolicyRefreshes = embeddingPolicyRefreshes;
        _applicationUpdates = applicationUpdates;
        _windowsUninstall = windowsUninstall;
        _applicationUpdates.SnapshotChanged += OnApplicationUpdateSnapshotChanged;
        ApplicationUpdate = _applicationUpdates.Snapshot;
        SelectedCpuUsageProfile = _cpuUsageSettings.Profile;
        SelectedEmbeddingModel = GraniteEmbeddingModels.Get(_embeddingModelSettings.Model);
        StartWithWindowsEnabled = _windowsStartup.IsEnabled;
        var connectionOrder = 0;
        foreach (var client in _aiConnections.Clients)
        {
            _aiConnectionCatalogOrder[client.Id] = connectionOrder++;
            AiConnections.Add(new AiConnectionItemViewModel(client));
        }
    }

    public ObservableCollection<ProjectItemViewModel> Projects { get; } = [];
    public ObservableCollection<IndexingActivityItemViewModel> ActiveIndexingItems { get; } = [];
    public ObservableCollection<AiConnectionItemViewModel> AiConnections { get; } = [];
    public IReadOnlyList<CpuUsageProfile> CpuUsageProfiles { get; } = Enum.GetValues<CpuUsageProfile>();
    public IReadOnlyList<GraniteEmbeddingModelDefinition> EmbeddingModelChoices { get; } = GraniteEmbeddingModels.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(HasNoSelection))]
    public partial ProjectItemViewModel? SelectedProject { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProjectsSection))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSection))]
    [NotifyPropertyChangedFor(nameof(ProjectsNavigationAutomationName))]
    [NotifyPropertyChangedFor(nameof(SettingsNavigationAutomationName))]
    public partial MainSection CurrentSection { get; set; } = MainSection.Projects;

    public string StatusMessage => _appStatus.Message;
    public string StatusDetails => _appStatus.Details;
    public bool IsStatusBusy => _appStatus.Tone == AppStatusTone.Busy;
    public bool IsStatusReady => _appStatus.Tone == AppStatusTone.Ready;
    public bool IsStatusWarning => _appStatus.Tone == AppStatusTone.Warning;
    public bool IsStatusError => _appStatus.Tone == AppStatusTone.Error;
    public bool IsProjectLoadPending => !_presentation.HasLoadedProjects && _presentation.RefreshError is null;
    public bool HasProjectLoadError => !_presentation.HasLoadedProjects && _presentation.RefreshError is not null;
    public string ProjectLoadError => _presentation.RefreshError ?? string.Empty;
    public bool HasNotification => _visibleNotification is not null;
    public string NotificationMessage => _visibleNotification?.Message ?? string.Empty;
    public bool IsNotificationError => _visibleNotification?.IsError == true;
    public string NotificationLabel => IsNotificationError ? "Action needs attention" : "Action completed";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunProjectAction))]
    public partial bool IsProjectActionBusy { get; set; }
    public bool CanRunProjectAction => !IsProjectActionBusy;

    [ObservableProperty]
    public partial string ProjectActionMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckAiConnections))]
    public partial bool IsCheckingAiConnections { get; set; }
    public bool CanCheckAiConnections => !IsCheckingAiConnections;

    [ObservableProperty]
    public partial string IndexingTimingSummary { get; set; } = "No files are currently active.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActiveIndexingCollapsed))]
    public partial bool IsActiveIndexingExpanded { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAiConnectionsCollapsed))]
    public partial bool IsAiConnectionsExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuUsageSummary))]
    public partial CpuUsageProfile SelectedCpuUsageProfile { get; set; } = CpuUsageProfile.Normal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmbeddingModelSummary))]
    [NotifyPropertyChangedFor(nameof(SemanticSearchStatusLabel))]
    [NotifyPropertyChangedFor(nameof(SemanticSearchStatusMessage))]
    [NotifyPropertyChangedFor(nameof(SemanticSearchSetupButtonLabel))]
    [NotifyPropertyChangedFor(nameof(IsSemanticSearchReadyStatus))]
    [NotifyPropertyChangedFor(nameof(IsSemanticSearchWarningStatus))]
    [NotifyPropertyChangedFor(nameof(IsSemanticSearchErrorStatus))]
    public partial GraniteEmbeddingModelDefinition SelectedEmbeddingModel { get; set; } = GraniteEmbeddingModels.All[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeEmbeddingModel))]
    [NotifyPropertyChangedFor(nameof(CanSetUpSemanticSearch))]
    [NotifyPropertyChangedFor(nameof(SemanticSearchStatusLabel))]
    [NotifyPropertyChangedFor(nameof(SemanticSearchStatusMessage))]
    [NotifyPropertyChangedFor(nameof(IsSemanticSearchReadyStatus))]
    [NotifyPropertyChangedFor(nameof(IsSemanticSearchWarningStatus))]
    [NotifyPropertyChangedFor(nameof(IsSemanticSearchErrorStatus))]
    public partial bool IsChangingEmbeddingModel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeEmbeddingModel))]
    [NotifyPropertyChangedFor(nameof(CanSetUpSemanticSearch))]
    [NotifyPropertyChangedFor(nameof(SemanticSearchStatusLabel))]
    [NotifyPropertyChangedFor(nameof(SemanticSearchStatusMessage))]
    [NotifyPropertyChangedFor(nameof(IsSemanticSearchReadyStatus))]
    [NotifyPropertyChangedFor(nameof(IsSemanticSearchWarningStatus))]
    [NotifyPropertyChangedFor(nameof(IsSemanticSearchErrorStatus))]
    public partial bool IsPreparingEmbeddingModel { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OcrStatusLabel))]
    [NotifyPropertyChangedFor(nameof(OcrStatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanRetryOcrSetup))]
    [NotifyPropertyChangedFor(nameof(IsOcrReadyStatus))]
    [NotifyPropertyChangedFor(nameof(IsOcrWarningStatus))]
    public partial bool IsPreparingOcr { get; set; } = true;

    [ObservableProperty]
    public partial bool StartWithWindowsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApplicationUpdateProgressVisible))]
    [NotifyPropertyChangedFor(nameof(IsApplicationUpdateReady))]
    [NotifyPropertyChangedFor(nameof(CanRestartForUpdate))]
    [NotifyPropertyChangedFor(nameof(ApplicationUpdateMessage))]
    [NotifyPropertyChangedFor(nameof(ApplicationUpdateStatusLabel))]
    [NotifyPropertyChangedFor(nameof(IsApplicationUpdateReadyStatus))]
    [NotifyPropertyChangedFor(nameof(IsApplicationUpdateWarningStatus))]
    public partial ApplicationUpdateSnapshot ApplicationUpdate { get; set; } = ApplicationUpdateSnapshot.Disabled;

    public bool IsOcrUnavailable => !_ocrEngine.AreAssetsReady || _ocrEngine.UnavailableReason is not null;
    public bool IsOcrAvailable => !IsOcrUnavailable;
    public bool IsWindowsStartupSupported => _windowsStartup.IsSupported;
    public bool IsWindowsUninstallVisible => _windowsUninstall.Availability.IsVisible;
    public bool CanUninstallFromSettings => _windowsUninstall.Availability.CanUninstall;
    public bool CanDeleteLocalDataDuringUninstall => _windowsUninstall.Availability.CanDeleteData;
    public string WindowsUninstallMessage => _windowsUninstall.Availability.Message;
    public string LocalDataDirectory => _windowsUninstall.Availability.DataDirectory;
    public string CpuUsageSummary
    {
        get
        {
            var percentage = SelectedCpuUsageProfile switch
            {
                CpuUsageProfile.Light => 20,
                CpuUsageProfile.Normal => 40,
                CpuUsageProfile.Heavy => 80,
                _ => throw new ArgumentOutOfRangeException()
            };
            return $"{SelectedCpuUsageProfile} · up to {percentage}% CPU";
        }
    }
    public string OcrStatusLabel => IsPreparingOcr ? "Setting up" : IsOcrAvailable ? "Ready" : "Needs attention";
    public string OcrStatusMessage => IsPreparingOcr
        ? "Preparing OCR for scanned documents and images…"
        : _ocrEngine.UnavailableReason ?? "OCR is ready and loads only when a scanned document needs it.";
    public bool CanRetryOcrSetup => !IsPreparingOcr && IsOcrUnavailable;
    public bool IsOcrReadyStatus => !IsPreparingOcr && IsOcrAvailable;
    public bool IsOcrWarningStatus => !IsPreparingOcr && IsOcrUnavailable;
    public bool IsSemanticSearchUnavailable => !_embeddingGenerator.IsAvailable;
    public bool CanInstallSemanticModel => _modelInstaller.IsSupported;
    public bool CanSetUpSemanticSearch => IsSemanticSearchUnavailable && CanInstallSemanticModel &&
        !IsChangingEmbeddingModel && !IsPreparingEmbeddingModel;
    public bool CanChangeEmbeddingModel => CanInstallSemanticModel &&
        !IsChangingEmbeddingModel && !IsPreparingEmbeddingModel;
    public string EmbeddingModelSummary => $"{SelectedEmbeddingModel.Description}. Supports multilingual search.";
    public string SemanticSearchStatusLabel => IsChangingEmbeddingModel ? "Switching model" : IsPreparingEmbeddingModel ? "Loading"
        : IsSemanticSearchUnavailable && !_modelInstaller.IsSupported ? "Unavailable"
        : IsSemanticSearchUnavailable && _modelInstaller.HasModelAssets(SelectedEmbeddingModel.Choice)
            ? "Needs attention"
            : IsSemanticSearchUnavailable ? "Optional" : "Ready";
    public string SemanticSearchStatusMessage => IsChangingEmbeddingModel
        ? "Switching the semantic model. Keyword search remains available; project coverage updates as compatible embeddings are rebuilt."
        : IsPreparingEmbeddingModel
        ? $"Loading {SelectedEmbeddingModel.DisplayName} in the background."
        : !IsSemanticSearchUnavailable
        ? $"{SelectedEmbeddingModel.DisplayName} is ready for multilingual meaning-based search."
        : !_modelInstaller.IsSupported
            ? _embeddingGenerator.UnavailableReason ?? "Semantic search is unavailable on this platform."
            : _modelInstaller.HasModelAssets(SelectedEmbeddingModel.Choice)
                ? $"Keyword search remains ready. {_embeddingGenerator.UnavailableReason ?? "The selected semantic model needs verification or repair."}"
                : $"Keyword search is ready. Download {SelectedEmbeddingModel.DisplayName} to add multilingual meaning-based search.";
    public string SemanticSearchSetupButtonLabel =>
        _modelInstaller.HasModelAssets(SelectedEmbeddingModel.Choice)
            ? "Verify and repair selected model"
            : "Download selected model";
    public bool IsSemanticSearchReadyStatus => !IsChangingEmbeddingModel && !IsPreparingEmbeddingModel && !IsSemanticSearchUnavailable;
    public bool IsSemanticSearchWarningStatus => !IsChangingEmbeddingModel && !IsPreparingEmbeddingModel && IsSemanticSearchUnavailable &&
        CanInstallSemanticModel && _modelInstaller.HasModelAssets(SelectedEmbeddingModel.Choice);
    public bool IsSemanticSearchErrorStatus => !IsChangingEmbeddingModel && !IsPreparingEmbeddingModel && IsSemanticSearchUnavailable &&
        !CanInstallSemanticModel;
    public bool HasSelection => SelectedProject is not null;
    public bool HasNoSelection => _presentation.HasLoadedProjects && _presentation.RefreshError is null && Projects.Count == 0;
    public bool IsProjectsSection => CurrentSection == MainSection.Projects;
    public bool IsSettingsSection => CurrentSection == MainSection.Settings;
    public string ProjectsNavigationAutomationName => IsProjectsSection ? "Projects, current section" : "Projects";
    public string SettingsNavigationAutomationName => IsSettingsSection ? "Settings, current section" : "Settings";
    public bool IsActiveIndexingCollapsed => !IsActiveIndexingExpanded;
    public bool IsAiConnectionsCollapsed => !IsAiConnectionsExpanded;
    public bool HasActiveIndexingItems => ActiveIndexingItems.Count > 0;
    public string AiConnectionsStatusLabel
    {
        get
        {
            if (AiConnections.Any(connection => connection.IsBusy)) return "Checking";
            if (AiConnections.Any(connection => connection.IsWarningStatus || connection.IsErrorStatus))
                return "Needs attention";
            var configured = AiConnections.Count(connection => connection.IsReadyStatus);
            return configured switch
            {
                0 => "None configured",
                1 => "1 configured",
                _ => $"{configured} configured"
            };
        }
    }
    public bool AreAiConnectionsReady => !AiConnections.Any(connection => connection.IsBusy) &&
        AiConnections.Any(connection => connection.IsReadyStatus) &&
        !AiConnections.Any(connection => connection.IsWarningStatus || connection.IsErrorStatus);
    public bool DoAiConnectionsNeedAttention => !AiConnections.Any(connection => connection.IsBusy) &&
        AiConnections.Any(connection => connection.IsWarningStatus || connection.IsErrorStatus);
    public bool IsApplicationUpdateProgressVisible => ApplicationUpdate.State == ApplicationUpdateState.Downloading;
    public bool IsApplicationUpdateReady => ApplicationUpdate.State == ApplicationUpdateState.Ready;
    public bool CanRestartForUpdate => IsApplicationUpdateReady && !_hasAnyActiveIndexingItems;
    public string ApplicationUpdateMessage => IsApplicationUpdateReady && _hasAnyActiveIndexingItems
        ? $"{ApplicationUpdate.Message} Restart will be available when indexing is idle."
        : ApplicationUpdate.Message;
    public string ApplicationUpdateStatusLabel => ApplicationUpdate.State switch
    {
        ApplicationUpdateState.Checking => "Checking",
        ApplicationUpdateState.Downloading => "Downloading",
        ApplicationUpdateState.Ready => "Ready to install",
        ApplicationUpdateState.Current => "Up to date",
        ApplicationUpdateState.Error => "Needs attention",
        _ => "Installed builds",
    };
    public bool IsApplicationUpdateReadyStatus => ApplicationUpdate.State is ApplicationUpdateState.Current
        or ApplicationUpdateState.Ready;
    public bool IsApplicationUpdateWarningStatus => ApplicationUpdate.State == ApplicationUpdateState.Error;
    public void RefreshAssetAvailability()
    {
        SelectedEmbeddingModel = GraniteEmbeddingModels.Get(_embeddingModelSettings.Model);
        RefreshOcrAvailability();
        OnPropertyChanged(nameof(IsSemanticSearchUnavailable));
        OnPropertyChanged(nameof(CanInstallSemanticModel));
        OnPropertyChanged(nameof(CanSetUpSemanticSearch));
        OnPropertyChanged(nameof(CanChangeEmbeddingModel));
        OnPropertyChanged(nameof(EmbeddingModelSummary));
        OnPropertyChanged(nameof(SemanticSearchStatusLabel));
        OnPropertyChanged(nameof(SemanticSearchStatusMessage));
        OnPropertyChanged(nameof(SemanticSearchSetupButtonLabel));
        OnPropertyChanged(nameof(IsSemanticSearchReadyStatus));
        OnPropertyChanged(nameof(IsSemanticSearchWarningStatus));
        OnPropertyChanged(nameof(IsSemanticSearchErrorStatus));
        if (!IsPreparingEmbeddingModel && _embeddingGenerator.IsAvailable) _presentation.Clear("semantic_setup");
        RefreshPresentation();
    }

    partial void OnIsChangingEmbeddingModelChanged(bool value)
    {
        Interlocked.Increment(ref _semanticRefreshSequence);
        _nextSemanticStatusRefreshUtc = DateTimeOffset.MinValue;
        if (value)
            foreach (var project in Projects) project.BeginSemanticIndexRefresh();
    }

    public void ShowProjects() => CurrentSection = MainSection.Projects;

    public void ShowSettings()
    {
        CurrentSection = MainSection.Settings;
        _ = RefreshAiConnectionsSafeAsync();
    }

    public void RefreshOnWindowFocus()
    {
        if (IsSettingsSection) _ = RefreshAiConnectionsSafeAsync();
    }

    public void DismissNotification()
    {
        _presentation.Dismiss(_visibleNotification);
        RefreshPresentation();
    }

    private void Notify(string source, string message, Guid? projectId = null, bool isError = false)
    {
        _presentation.Notify(source, projectId, message, isError);
        RefreshPresentation();
    }

    private void RefreshPresentation()
    {
        var status = _presentation.CurrentStatus(Projects.ToArray());
        if (_appStatus != status)
        {
            _appStatus = status;
            OnPropertyChanged(nameof(StatusMessage));
            OnPropertyChanged(nameof(StatusDetails));
            OnPropertyChanged(nameof(IsStatusBusy));
            OnPropertyChanged(nameof(IsStatusReady));
            OnPropertyChanged(nameof(IsStatusWarning));
            OnPropertyChanged(nameof(IsStatusError));
        }
        var notification = _presentation.VisibleNotification(SelectedProject?.Id);
        if (_visibleNotification != notification)
        {
            _visibleNotification = notification;
            OnPropertyChanged(nameof(HasNotification));
            OnPropertyChanged(nameof(NotificationMessage));
            OnPropertyChanged(nameof(IsNotificationError));
            OnPropertyChanged(nameof(NotificationLabel));
        }
        OnPropertyChanged(nameof(IsProjectLoadPending));
        OnPropertyChanged(nameof(HasProjectLoadError));
        OnPropertyChanged(nameof(ProjectLoadError));
        OnPropertyChanged(nameof(HasNoSelection));
    }

    public async Task RunProjectActionAsync(Guid? projectId, string description, Func<Task> action,
        string source = "project_action")
    {
        if (IsProjectActionBusy)
            throw new ContextMoleException("project_action_busy", "Another project action is in progress. Wait for it to finish, then try again.", true);
        IsProjectActionBusy = true;
        foreach (var project in Projects) project.SetActionsBusy(true);
        ProjectActionMessage = description;
        try
        {
            await action();
            _presentation.Clear(source, projectId);
        }
        catch (Exception exception)
        {
            Notify(source, exception.Message, projectId, isError: true);
            throw;
        }
        finally
        {
            IsProjectActionBusy = false;
            foreach (var project in Projects) project.SetActionsBusy(false);
            ProjectActionMessage = string.Empty;
            RefreshPresentation();
        }
    }

    public void BeginProjectReorder() => _isProjectReordering = true;

    public bool MoveProject(Guid projectId, int targetIndex)
    {
        if (!_isProjectReordering || Projects.Count < 2) return false;
        var sourceIndex = IndexOfProject(projectId, 0);
        if (sourceIndex < 0) return false;

        targetIndex = Math.Clamp(targetIndex, 0, Projects.Count - 1);
        if (sourceIndex == targetIndex) return false;

        Projects.Move(sourceIndex, targetIndex);
        return true;
    }

    public void EndProjectReorder(bool persist)
    {
        if (!_isProjectReordering) return;
        _isProjectReordering = false;
        if (!persist) return;

        try
        {
            _projectOrder.Save(Projects.Select(project => project.Id).ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Notify("project_order", $"The project order could not be saved: {exception.Message}", isError: true);
        }
    }

    partial void OnSelectedProjectChanged(ProjectItemViewModel? value)
    {
        Interlocked.Increment(ref _semanticRefreshSequence);
        RefreshPresentation();
        ReconcileIndexingActivities(_indexingActivities.GetSnapshot(value?.Id));
        if (value is not null)
        {
            _ = RefreshErrorsSafeAsync(value.Id);
            _ = RefreshSemanticIndexSafeAsync(value.Id, value.SearchGeneration);
        }
    }

    public void StartPolling()
    {
        if (_polling is not null) return;
        _polling = new CancellationTokenSource();
        _applicationUpdates.Start();
        _pollingTask = Task.WhenAll(
            PrepareEmbeddingModelAsync(_polling.Token),
            PrepareOcrAsync(_polling.Token),
            RefreshAiConnectionsAsync(_polling.Token),
            PollAsync(_polling.Token));
    }

    public async Task StopPollingAsync()
    {
        var polling = Interlocked.Exchange(ref _polling, null);
        if (polling is null) return;
        var pollingTask = Interlocked.Exchange(ref _pollingTask, null);
        polling.Cancel();
        _applicationUpdates.Stop();
        try
        {
            if (pollingTask is not null) await pollingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (polling.IsCancellationRequested)
        {
        }
        finally
        {
            polling.Dispose();
        }
    }

    public async Task CreateAsync(string name, IReadOnlyList<string> folders)
    {
        await MutateAsync(() => _writer.CreateProjectAsync(new CreateProjectRequest(name, folders)), selectCreated: true);
        Notify("project_saved", $"{name} saved.");
    }

    public async Task UpdateAsync(Guid projectId, string name, IReadOnlyList<string> folders)
    {
        await MutateAsync(async () => { await _writer.UpdateProjectAsync(new UpdateProjectRequest(projectId, name, folders)); return projectId; });
        Notify("project_saved", $"{name} saved.", projectId);
    }

    public async Task TogglePauseAsync(Guid? projectId = null)
    {
        var selected = projectId is { } id ? Projects.FirstOrDefault(project => project.Id == id) : SelectedProject;
        if (selected is null) return;
        if (selected.State != ProjectState.Paused)
        {
            _projectIndexingControl.BeginPause(selected.Id);
            try
            {
                await _writer.SetProjectPausedAsync(selected.Id, true);
            }
            catch
            {
                // Storage did not establish the paused boundary. Resolve the provisional gate so
                // canceled workers and lease claims can safely continue instead of being stranded.
                _projectIndexingControl.Resume(selected.Id);
                throw;
            }
            var drain = _projectIndexingControl.DrainPausedAsync(selected.Id);
            _projectPauseDrains[selected.Id] = drain;
            _ = ObservePauseDrainAsync(selected.Id, selected.Name, drain);
            await RefreshAsync();
            Notify("pause", $"{selected.Name} is paused. Interrupted files remain queued for resume.", selected.Id);
            return;
        }

        if (_projectPauseDrains.TryGetValue(selected.Id, out var pendingDrain))
        {
            await pendingDrain;
        }

        _projectIndexingControl.Resume(selected.Id);
        try
        {
            await _writer.SetProjectPausedAsync(selected.Id, false);
        }
        catch
        {
            // The database is still paused, so restore the in-process gate before surfacing the failure.
            _projectIndexingControl.BeginPause(selected.Id);
            await _projectIndexingControl.DrainPausedAsync(selected.Id);
            throw;
        }
        _projectPauseDrains.Remove(selected.Id);
        await RefreshAsync();
        Notify("pause", $"{selected.Name} resumed. Queued indexing work can continue.", selected.Id);
    }

    private async Task ObservePauseDrainAsync(Guid projectId, string projectName, Task drain)
    {
        try
        {
            await drain.ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _presentation.Clear("pause_cleanup", projectId);
                RefreshPresentation();
            });
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_projectPauseDrains.TryGetValue(projectId, out var current) &&
                    ReferenceEquals(current, drain) && SelectedProject is { Id: var selectedId, State: ProjectState.Paused } &&
                    selectedId == projectId)
                {
                    Notify("pause_cleanup", $"{projectName} is paused, but its background cleanup needs attention: {exception.Message}", projectId, isError: true);
                }
            });
        }
    }

    public async Task ReindexAsync(Guid? projectId = null)
    {
        var id = projectId ?? SelectedProject?.Id;
        if (id is null) return;
        await MutateAsync(async () => { await _writer.RequestReindexAsync(id.Value); return id.Value; });
    }

    public async Task RetryFailedFilesAsync(Guid? projectId = null)
    {
        var id = projectId ?? SelectedProject?.Id;
        if (id is null) return;
        var result = await _writer.RetryFailedFilesAsync(id.Value);
        await RefreshAsync();
        var message = result switch
        {
            { QueuedCount: 0, AlreadyPendingCount: 1 } =>
                "The file is already queued or being processed. Its status updates as the retry runs.",
            { QueuedCount: 0, AlreadyPendingCount: > 1 } =>
                $"All {result.AlreadyPendingCount} files are already queued or being processed. " +
                "Their status updates as retries run.",
            { QueuedCount: 0 } => "No file-specific failures are currently available to retry.",
            { QueuedCount: 1, AlreadyPendingCount: 0 } => "Queued 1 failed file for retry.",
            { AlreadyPendingCount: 0 } => $"Queued {result.QueuedCount} failed files for retry.",
            _ => $"Queued {result.QueuedCount} failed files for retry; {result.AlreadyPendingCount} were already " +
                 "queued or being processed."
        };
        Notify("retry", message, id);
    }

    public async Task RepairSemanticIndexAsync(Guid? projectId = null)
    {
        var selected = projectId is { } id ? Projects.FirstOrDefault(project => project.Id == id) : SelectedProject;
        if (selected is null || !selected.ShowSemanticRepairButton || selected.State != ProjectState.Active ||
            !_embeddingGenerator.IsAvailable || _embeddingGenerator.Policy is not { } policy) return;
        await _writer.RequestEmbeddingRefreshAsync(selected.Id, policy, retryFailed: true);
        _embeddingPolicyRefreshes.TryBeginRefresh(selected.Id, policy.Key);
        await RefreshSemanticIndexAsync(selected.Id, selected.SearchGeneration);
        await RefreshAsync();
        Notify("semantic_repair", $"Queued semantic-index repair for {selected.Name}. Meaning-based coverage will expand in the background.", selected.Id);
    }

    public Task SetCpuUsageProfileAsync(CpuUsageProfile profile)
    {
        _cpuUsageSettings.SetProfile(profile);
        SelectedCpuUsageProfile = _cpuUsageSettings.Profile;
        Notify("cpu", $"CPU usage is now {SelectedCpuUsageProfile}. {CpuUsageSummary}");
        return Task.CompletedTask;
    }

    public Task RetryOcrSetupAsync(CancellationToken cancellationToken = default) =>
        IsPreparingOcr ? Task.CompletedTask : PrepareOcrAsync(cancellationToken, loadSessions: true);

    public async Task SetEmbeddingModelAsync(GraniteEmbeddingModelDefinition model)
    {
        if (!_modelInstaller.IsModelInstalled(model.Choice))
            throw new ContextMoleException("model_unavailable", $"Download {model.DisplayName} before selecting it.");

        IsChangingEmbeddingModel = true;
        try
        {
            await _embeddingPolicyRefreshes.RunExclusiveAsync(() => SetEmbeddingModelCoreAsync(model));
        }
        finally
        {
            IsChangingEmbeddingModel = false;
        }
    }

    private async Task SetEmbeddingModelCoreAsync(GraniteEmbeddingModelDefinition model)
    {
        var previousChoice = _embeddingModelSettings.Model;
        try
        {
            _embeddingModelSettings.SetModel(model.Choice);
            await _embeddingGenerator.ReloadAsync();
            if (!_embeddingGenerator.IsAvailable ||
                !string.Equals(_embeddingGenerator.Policy?.ModelId, model.ModelId, StringComparison.Ordinal))
                throw new ContextMoleException("model_unavailable",
                    _embeddingGenerator.UnavailableReason ?? $"{model.DisplayName} could not be loaded.");

            SelectedEmbeddingModel = GraniteEmbeddingModels.Get(_embeddingModelSettings.Model);
            RefreshAssetAvailability();
        }
        catch
        {
            if (!_embeddingGenerator.IsAvailable &&
                string.Equals(_embeddingGenerator.Policy?.ModelId, model.ModelId, StringComparison.Ordinal))
            {
                try
                {
                    _modelInstaller.MarkModelForRepair(model.Choice,
                        _embeddingGenerator.UnavailableReason ?? "The model could not be loaded.");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }

            if (_embeddingModelSettings.Model != previousChoice)
            {
                _embeddingModelSettings.SetModel(previousChoice);
                await _embeddingGenerator.ReloadAsync();
            }
            SelectedEmbeddingModel = GraniteEmbeddingModels.Get(previousChoice);
            RefreshAssetAvailability();
            throw;
        }

        var queuedProjects = 0;
        var policy = _embeddingGenerator.Policy!;
        try
        {
            foreach (var project in await _store.ListProjectsAsync())
            {
                if (project.IndexedCount == 0 || project.State == ProjectState.Paused) continue;
                _embeddingPolicyRefreshes.CancelRefresh(project.Id, policy.Key);
                if (!_embeddingPolicyRefreshes.TryBeginRefresh(project.Id, policy.Key)) continue;
                try
                {
                    var metadata = await _store.LoadVectorSnapshotMetadataAsync(project.Id, policy);
                    if (!metadata.IsComplete)
                    {
                        await _writer.RequestEmbeddingRefreshAsync(project.Id, policy, retryFailed: true);
                        queuedProjects++;
                    }
                    else
                    {
                        _embeddingPolicyRefreshes.CancelRefresh(project.Id, policy.Key);
                    }
                }
                catch
                {
                    _embeddingPolicyRefreshes.CancelRefresh(project.Id, policy.Key);
                    throw;
                }
            }
        }
        catch (Exception exception)
        {
            Notify("model", $"{model.DisplayName} is active. Automatic re-embedding needs attention: {exception.Message}", isError: true);
            return;
        }

        Notify("model", queuedProjects == 0
            ? $"{model.DisplayName} is active."
            : $"{model.DisplayName} is active. Re-embedding {queuedProjects} project{(queuedProjects == 1 ? string.Empty : "s")} in the background.");
    }

    public void SetStartWithWindows(bool enabled)
    {
        _windowsStartup.SetEnabled(enabled);
        StartWithWindowsEnabled = _windowsStartup.IsEnabled;
        Notify("startup", StartWithWindowsEnabled
            ? "Context Mole will start automatically with Windows."
            : "Context Mole will not start automatically with Windows.");
    }

    public async Task RemoveAsync(Guid? projectId = null)
    {
        var id = projectId ?? SelectedProject?.Id;
        if (id is null) return;
        await MutateAsync(async () => { await _writer.RemoveProjectAsync(id.Value); return id.Value; });
    }

    public async Task<AiConnectionStatus> ToggleAiConnectionAsync(AiConnectionItemViewModel connection)
    {
        if (!connection.SupportsAutomaticSetup)
            return await _aiConnections.GetStatusAsync(connection.Id).ConfigureAwait(false);

        await _aiRefreshGate.WaitAsync();
        connection.IsBusy = true;
        NotifyAiConnectionsSummaryChanged();
        try
        {
            var result = connection.RequiresReadOnlyCheck
                ? await _aiConnections.GetStatusAsync(connection.Id).ConfigureAwait(false)
                : connection.IsConfigured
                ? await _aiConnections.DisconnectAsync(connection.Id).ConfigureAwait(false)
                : await _aiConnections.ConnectAsync(connection.Id).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyAiConnectionStatus(connection, result));
            return result;
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                connection.IsBusy = false;
                NotifyAiConnectionsSummaryChanged();
            });
            _aiRefreshGate.Release();
        }
    }

    public async Task RefreshAsync(Guid? preferredProjectId = null, CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var projects = _projectOrder.Apply(
                await _store.ListProjectsAsync(cancellationToken).ConfigureAwait(false));
            (Guid Id, long Generation, int DocumentCount)? fileTypeRefresh = null;
            (Guid Id, long Generation)? semanticRefresh = null;
            Guid? errorsProjectId = null;
            var semanticPolicyKey = _embeddingGenerator.Policy?.Key;
            var semanticModelAvailable = _embeddingGenerator.IsAvailable && semanticPolicyKey is not null;
            var semanticStatusRefreshDue = DateTimeOffset.UtcNow >= _nextSemanticStatusRefreshUtc;
            var fileTypeRefreshDue = DateTimeOffset.UtcNow >= _nextFileTypeRefreshUtc;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var selectedId = preferredProjectId ?? SelectedProject?.Id;
                var orderedProjects = _isProjectReordering
                    ? ProjectOrderService.Apply(projects, Projects.Select(project => project.Id).ToArray())
                    : projects;
                ReconcileProjects(orderedProjects);
                SelectedProject = selectedId is null
                    ? Projects.FirstOrDefault()
                    : Projects.FirstOrDefault(project => project.Id == selectedId) ?? Projects.FirstOrDefault();
                ReconcileIndexingActivities(_indexingActivities.GetSnapshot(SelectedProject?.Id));
                errorsProjectId = SelectedProject?.Id;
                _presentation.ProjectsLoaded();
                foreach (var project in Projects) _presentation.Clear("project_refresh", project.Id);
                RefreshPresentation();

                if (SelectedProject is { } selected &&
                    (_fileTypeCountsProjectId != selected.Id ||
                     _fileTypeCountsGeneration != selected.SearchGeneration ||
                     _fileTypeCountsDocumentCount != selected.DocumentCount || fileTypeRefreshDue))
                {
                    fileTypeRefresh = (selected.Id, selected.SearchGeneration, selected.DocumentCount);
                }
                if (SelectedProject is { } semanticProject &&
                    (_semanticStatusProjectId != semanticProject.Id ||
                     _semanticStatusGeneration != semanticProject.SearchGeneration ||
                     !string.Equals(_semanticStatusPolicyKey, semanticPolicyKey, StringComparison.Ordinal) ||
                     _semanticStatusModelAvailable != semanticModelAvailable || semanticStatusRefreshDue))
                {
                    semanticRefresh = (semanticProject.Id, semanticProject.SearchGeneration);
                }
            });

            if (errorsProjectId is { } errorsId)
                await RefreshErrorsSafeAsync(errorsId, cancellationToken).ConfigureAwait(false);
            if (fileTypeRefresh is { } refresh)
                await RefreshFileTypeCountsAsync(refresh, cancellationToken).ConfigureAwait(false);
            if (semanticRefresh is { } statusRefresh)
                await RefreshSemanticIndexSafeAsync(statusRefresh.Id, statusRefresh.Generation, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _presentation.ProjectsFailed(exception.Message);
                RefreshPresentation();
            });
            throw;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var summaryTick = 0;
        try
        {
            try
            {
                await RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _presentation.ProjectsFailed(exception.Message);
                    RefreshPresentation();
                });
            }

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        foreach (var project in Projects)
                            project.UpdateRuntime(_indexingActivities.GetSnapshot(project.Id), _indexingActivities.IsDiscovering(project.Id));
                        ReconcileIndexingActivities(_indexingActivities.GetSnapshot(SelectedProject?.Id));
                    });
                    if (++summaryTick % 4 != 0) continue;

                    await RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(RefreshAssetAvailability);
                    if (IsSettingsSection && DateTimeOffset.UtcNow >= _nextAiRefreshUtc)
                        await RefreshAiConnectionsSafeAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _presentation.ProjectsFailed(exception.Message);
                        RefreshPresentation();
                    });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task PrepareEmbeddingModelAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _embeddingGenerator.ReloadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Notify("semantic_setup", $"Semantic search setup: {exception.Message}", isError: true));
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsPreparingEmbeddingModel = false;
                    RefreshAssetAvailability();
                    if (_embeddingGenerator.IsAvailable) _presentation.Clear("semantic_setup");
                    RefreshPresentation();
                });
            }
        }
    }

    private async Task PrepareOcrAsync(CancellationToken cancellationToken, bool loadSessions = false)
    {
        await Dispatcher.UIThread.InvokeAsync(() => IsPreparingOcr = true);
        try
        {
            if (loadSessions)
                await _ocrEngine.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
            else
                await _ocrEngine.PrepareAssetsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Notify("ocr_setup", $"OCR setup: {exception.Message}", isError: true));
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsPreparingOcr = false;
                    RefreshOcrAvailability();
                    if (_ocrEngine.IsAvailable) _presentation.Clear("ocr_setup");
                    RefreshPresentation();
                });
            }
        }
    }

    private async Task RefreshAiConnectionsAsync(CancellationToken cancellationToken)
    {
        if (!await _aiRefreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
        AiConnectionItemViewModel[] connections = [];
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsCheckingAiConnections = true;
            connections = AiConnections.ToArray();
            foreach (var connection in connections) connection.IsBusy = true;
            NotifyAiConnectionsSummaryChanged();
        });
        await Task.WhenAll(connections.Select(async connection =>
        {
            try
            {
                var status = await _aiConnections.GetStatusAsync(connection.Id, cancellationToken).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => ApplyAiConnectionStatus(connection, status));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                var status = new AiConnectionStatus(connection.Client, AiConnectionState.Conflict, exception.Message);
                await Dispatcher.UIThread.InvokeAsync(() => ApplyAiConnectionStatus(connection, status));
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    connection.IsBusy = false;
                    NotifyAiConnectionsSummaryChanged();
                });
            }
        })).ConfigureAwait(false);
        _nextAiRefreshUtc = DateTimeOffset.UtcNow.AddSeconds(30);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _presentation.Clear("ai_check");
            RefreshPresentation();
        });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsCheckingAiConnections = false);
            _aiRefreshGate.Release();
        }
    }

    public Task CheckAiConnectionsAsync() => RefreshAiConnectionsSafeAsync();

    private async Task RefreshAiConnectionsSafeAsync(CancellationToken cancellationToken = default)
    {
        try { await RefreshAiConnectionsAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Notify("ai_check", $"AI configurations could not be checked: {exception.Message}", isError: true));
        }
    }

    private void ApplyAiConnectionStatus(AiConnectionItemViewModel connection, AiConnectionStatus status)
    {
        connection.Apply(status);
        SortAiConnections();
        NotifyAiConnectionsSummaryChanged();
    }

    private void NotifyAiConnectionsSummaryChanged()
    {
        OnPropertyChanged(nameof(AiConnectionsStatusLabel));
        OnPropertyChanged(nameof(AreAiConnectionsReady));
        OnPropertyChanged(nameof(DoAiConnectionsNeedAttention));
    }

    private void SortAiConnections()
    {
        var ordered = AiConnections
            .OrderByDescending(connection => connection.HasManagedConfiguration)
            .ThenBy(connection => _aiConnectionCatalogOrder[connection.Id])
            .ToArray();

        for (var targetIndex = 0; targetIndex < ordered.Length; targetIndex++)
        {
            var currentIndex = AiConnections.IndexOf(ordered[targetIndex]);
            if (currentIndex != targetIndex) AiConnections.Move(currentIndex, targetIndex);
        }
    }

    private void RefreshOcrAvailability()
    {
        var available = !IsOcrUnavailable;
        var message = OcrStatusMessage;
        if (_reportedOcrAvailable == available && string.Equals(_reportedOcrMessage, message, StringComparison.Ordinal))
            return;

        _reportedOcrAvailable = available;
        _reportedOcrMessage = message;
        OnPropertyChanged(nameof(IsOcrUnavailable));
        OnPropertyChanged(nameof(IsOcrAvailable));
        OnPropertyChanged(nameof(OcrStatusLabel));
        OnPropertyChanged(nameof(OcrStatusMessage));
        OnPropertyChanged(nameof(CanRetryOcrSetup));
        OnPropertyChanged(nameof(IsOcrReadyStatus));
        OnPropertyChanged(nameof(IsOcrWarningStatus));
    }

    private async Task RefreshErrorsSafeAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var generation = SelectedProject?.Id == projectId ? SelectedProject.SearchGeneration : -1;
        try
        {
            await RefreshErrorsAsync(projectId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (SelectedProject is { } selected && selected.Id == projectId && selected.SearchGeneration == generation)
                    Notify("errors_refresh", $"Current issues could not be refreshed: {exception.Message}", projectId, isError: true);
            });
        }
    }

    private async Task RefreshSemanticIndexSafeAsync(Guid projectId, long generation, CancellationToken cancellationToken = default)
    {
        try
        {
            await RefreshSemanticIndexAsync(projectId, generation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (SelectedProject is { } selected && selected.Id == projectId && selected.SearchGeneration == generation)
                    Notify("semantic_refresh", $"Meaning-based coverage could not be refreshed: {exception.Message}", projectId, isError: true);
            });
        }
    }

    private async Task RefreshFileTypeCountsAsync((Guid Id, long Generation, int DocumentCount) refresh,
        CancellationToken cancellationToken)
    {
        bool IsCurrent() => SelectedProject is { } selected && selected.Id == refresh.Id &&
            selected.SearchGeneration == refresh.Generation && selected.DocumentCount == refresh.DocumentCount;
        try
        {
            var counts = await _store.ListProjectFileTypeCountsAsync(refresh.Id, cancellationToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsCurrent()) return;
                SelectedProject!.UpdateFileTypeCounts(counts);
                _fileTypeCountsProjectId = refresh.Id;
                _fileTypeCountsGeneration = refresh.Generation;
                _fileTypeCountsDocumentCount = refresh.DocumentCount;
                _nextFileTypeRefreshUtc = DateTimeOffset.UtcNow.AddSeconds(10);
                _presentation.Clear("file_types_refresh", refresh.Id);
                RefreshPresentation();
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (IsCurrent())
                    Notify("file_types_refresh", $"File types could not be refreshed: {exception.Message}", refresh.Id, isError: true);
            });
        }
    }

    private async Task RefreshErrorsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        // Selection changes, paging, and the poller can all request details. Serialize these reads
        // so an older result cannot restore an obsolete page after a newer one has been displayed.
        await _errorRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ProjectItemViewModel? requestedProject = null;
        var offset = 0;
        var expectedErrorCount = 0;
        var expectedGeneration = -1L;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (SelectedProject is not { } selected || selected.Id != projectId ||
                    !selected.HasErrors || !selected.IsErrorsExpanded)
                {
                    if (SelectedProject?.Id == projectId && !SelectedProject.HasErrors)
                    {
                        _presentation.Clear("errors_refresh", projectId);
                        RefreshPresentation();
                    }
                    return;
                }
                requestedProject = selected;
                offset = selected.ErrorPageOffset;
                expectedErrorCount = selected.ErrorCount;
                expectedGeneration = selected.SearchGeneration;
                selected.IsErrorsLoading = true;
            });
            if (requestedProject is null) return;
            var errors = await _store.ListProjectErrorsAsync(projectId, ProjectItemViewModel.ErrorPageSize,
                cancellationToken, offset).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(SelectedProject, requestedProject) && requestedProject.HasErrors &&
                    requestedProject.SearchGeneration == expectedGeneration &&
                    requestedProject.ErrorPageOffset == offset && requestedProject.ErrorCount == expectedErrorCount)
                {
                    requestedProject.UpdateErrors(errors);
                    _presentation.Clear("errors_refresh", projectId);
                    RefreshPresentation();
                }
            });
        }
        finally
        {
            if (requestedProject is not null)
                await Dispatcher.UIThread.InvokeAsync(() => requestedProject.IsErrorsLoading = false);
            _errorRefreshGate.Release();
        }
    }

    public async Task MoveErrorPageAsync(int direction)
    {
        if (SelectedProject is not { } selected || selected.IsErrorsLoading) return;
        selected.MoveErrorPage(direction);
        await RefreshErrorsAsync(selected.Id);
    }

    private async Task RefreshSemanticIndexAsync(Guid projectId, long generation,
        CancellationToken cancellationToken = default)
    {
        var policy = _embeddingGenerator.Policy;
        var modelAvailable = _embeddingGenerator.IsAvailable && policy is not null;
        var stamp = new UiRefreshStamp(projectId, generation, policy?.Key, modelAvailable,
            Interlocked.Increment(ref _semanticRefreshSequence));
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (IsCurrentSemanticRefresh(stamp)) SelectedProject!.BeginSemanticIndexRefresh();
        });
        try
        {
            var metadata = modelAvailable
                ? await _store.LoadVectorSnapshotMetadataAsync(projectId, policy!, cancellationToken).ConfigureAwait(false)
                : null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsCurrentSemanticRefresh(stamp) || metadata is not null && metadata.SearchGeneration != generation) return;
                SelectedProject!.UpdateSemanticIndex(metadata, modelAvailable);
                _semanticStatusProjectId = projectId;
                _semanticStatusGeneration = generation;
                _semanticStatusPolicyKey = policy?.Key;
                _semanticStatusModelAvailable = modelAvailable;
                _nextSemanticStatusRefreshUtc = DateTimeOffset.UtcNow.AddSeconds(10);
                _presentation.Clear("semantic_refresh", projectId);
                RefreshPresentation();
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsCurrentSemanticRefresh(stamp)) return;
                SelectedProject!.FailSemanticIndexRefresh(exception.Message);
                Notify("semantic_refresh", $"Meaning-based coverage could not be refreshed: {exception.Message}", projectId, isError: true);
            });
        }
    }

    private bool IsCurrentSemanticRefresh(UiRefreshStamp stamp) => stamp.IsCurrent(SelectedProject?.Id,
        SelectedProject?.SearchGeneration ?? -1, _embeddingGenerator.Policy?.Key,
        _embeddingGenerator.IsAvailable && _embeddingGenerator.Policy is not null, Volatile.Read(ref _semanticRefreshSequence));

    private async Task MutateAsync(Func<Task<Guid>> action, bool selectCreated = false)
    {
        var selectionBefore = SelectedProject?.Id;
        var id = await action();
        try
        {
            await RefreshAsync(selectCreated && SelectedProject?.Id == selectionBefore ? id : null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The write already committed. Keeping an editor open here invites duplicate creates.
            Notify("project_refresh", $"Project changes were saved, but their status could not be refreshed: {exception.Message}", id, isError: true);
        }
    }

    private void ReconcileProjects(IReadOnlyList<ProjectSummary> projects)
    {
        for (var targetIndex = 0; targetIndex < projects.Count; targetIndex++)
        {
            var incoming = projects[targetIndex];
            if (targetIndex < Projects.Count && Projects[targetIndex].Id == incoming.Id)
            {
                Projects[targetIndex].UpdateFrom(incoming);
                continue;
            }

            var existingIndex = IndexOfProject(incoming.Id, targetIndex + 1);
            if (existingIndex >= 0)
            {
                Projects.Move(existingIndex, targetIndex);
                Projects[targetIndex].UpdateFrom(incoming);
            }
            else
            {
                Projects.Insert(targetIndex, new ProjectItemViewModel(incoming));
            }
        }

        while (Projects.Count > projects.Count)
        {
            _presentation.RemoveProject(Projects[^1].Id);
            Projects.RemoveAt(Projects.Count - 1);
        }

        foreach (var project in Projects)
        {
            project.UpdateRuntime(_indexingActivities.GetSnapshot(project.Id),
                _indexingActivities.IsDiscovering(project.Id));
            project.UpdateFolderIssues(_indexingActivities.GetFolderIssues(project.Id));
            project.SetActionsBusy(IsProjectActionBusy);
        }
    }

    private void ReconcileIndexingActivities(IndexingTimingSnapshot snapshot)
    {
        if (SelectedProject is { } selected)
            selected.UpdateRuntime(snapshot, _indexingActivities.IsDiscovering(selected.Id));
        var hasAnyActiveIndexingItems = _indexingActivities.HasActiveItems;
        if (_hasAnyActiveIndexingItems != hasAnyActiveIndexingItems)
        {
            _hasAnyActiveIndexingItems = hasAnyActiveIndexingItems;
            OnPropertyChanged(nameof(CanRestartForUpdate));
            OnPropertyChanged(nameof(ApplicationUpdateMessage));
        }

        for (var targetIndex = 0; targetIndex < snapshot.ActiveItems.Count; targetIndex++)
        {
            var incoming = snapshot.ActiveItems[targetIndex];
            if (targetIndex < ActiveIndexingItems.Count && ActiveIndexingItems[targetIndex].JobId == incoming.JobId)
            {
                ActiveIndexingItems[targetIndex].UpdateFrom(incoming);
                continue;
            }

            var existingIndex = IndexOfActivity(incoming.JobId, targetIndex + 1);
            if (existingIndex >= 0)
            {
                ActiveIndexingItems.Move(existingIndex, targetIndex);
                ActiveIndexingItems[targetIndex].UpdateFrom(incoming);
            }
            else
            {
                ActiveIndexingItems.Insert(targetIndex, new IndexingActivityItemViewModel(incoming));
            }
        }

        while (ActiveIndexingItems.Count > snapshot.ActiveItems.Count)
            ActiveIndexingItems.RemoveAt(ActiveIndexingItems.Count - 1);

        var workParts = new List<string>(4);
        if (snapshot.ProcessingCount > 0)
        {
            var retrySuffix = snapshot.RetryingCount > 0
                ? $" ({snapshot.RetryingCount} {Pluralize(snapshot.RetryingCount, "retry", "retries")})"
                : string.Empty;
            workParts.Add($"{snapshot.ProcessingCount} processing{retrySuffix}");
        }
        if (snapshot.WaitingForCpuCount > 0)
            workParts.Add($"{snapshot.WaitingForCpuCount} waiting for CPU");

        var activeText = workParts.Count == 0 ? "No files active" : string.Join(" · ", workParts);
        var completedText = snapshot.AverageCompletedDuration is { } average
            ? $"completed processing average {IndexingActivityItemViewModel.FormatDuration(average)} ({snapshot.CompletedSampleCount} this session)"
            : "completed processing average —";
        IndexingTimingSummary = $"{activeText} · {completedText}";
        OnPropertyChanged(nameof(HasActiveIndexingItems));
        RefreshPresentation();
    }

    private static string Pluralize(int count, string singular, string plural) =>
        count == 1 ? singular : plural;

    private void OnApplicationUpdateSnapshotChanged(object? sender, ApplicationUpdateSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => ApplicationUpdate = snapshot);
    }

    private int IndexOfProject(Guid id, int startIndex)
    {
        for (var index = startIndex; index < Projects.Count; index++)
        {
            if (Projects[index].Id == id) return index;
        }

        return -1;
    }

    private int IndexOfActivity(Guid jobId, int startIndex)
    {
        for (var index = startIndex; index < ActiveIndexingItems.Count; index++)
        {
            if (ActiveIndexingItems[index].JobId == jobId) return index;
        }

        return -1;
    }
}
