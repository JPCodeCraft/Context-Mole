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
    private CancellationTokenSource? _polling;
    private Task? _pollingTask;
    private bool? _reportedOcrAvailable;
    private string? _reportedOcrMessage;
    private bool _hasAnyActiveIndexingItems;
    private int _lastProjectCount = -1;
    private Guid? _fileTypeCountsProjectId;
    private long _fileTypeCountsGeneration = -1;
    private int _fileTypeCountsDocumentCount = -1;
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
        WindowsUninstallService windowsUninstall)
    {
        _writer = writer;
        _store = store;
        _ocrEngine = ocrEngine;
        _embeddingGenerator = embeddingGenerator;
        _embeddingModelSettings = embeddingModelSettings;
        _cpuUsageSettings = cpuUsageSettings;
        _windowsStartup = windowsStartup;
        _windowsStartup.Initialize();
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

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Starting local index…";

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
    public string SemanticSearchStatusLabel => IsPreparingEmbeddingModel ? "Loading"
        : IsSemanticSearchUnavailable && !_modelInstaller.IsSupported ? "Unavailable"
        : IsSemanticSearchUnavailable && _modelInstaller.HasModelAssets(SelectedEmbeddingModel.Choice)
            ? "Needs attention"
            : IsSemanticSearchUnavailable ? "Optional" : "Ready";
    public string SemanticSearchStatusMessage => IsPreparingEmbeddingModel
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
    public bool IsSemanticSearchReadyStatus => !IsPreparingEmbeddingModel && !IsSemanticSearchUnavailable;
    public bool IsSemanticSearchWarningStatus => !IsPreparingEmbeddingModel && IsSemanticSearchUnavailable &&
        CanInstallSemanticModel && _modelInstaller.HasModelAssets(SelectedEmbeddingModel.Choice);
    public bool IsSemanticSearchErrorStatus => !IsPreparingEmbeddingModel && IsSemanticSearchUnavailable &&
        !CanInstallSemanticModel;
    public bool HasSelection => SelectedProject is not null;
    public bool HasNoSelection => SelectedProject is null;
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
    }

    public void ShowProjects() => CurrentSection = MainSection.Projects;

    public void ShowSettings() => CurrentSection = MainSection.Settings;

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
            StatusMessage = $"The project order could not be saved: {exception.Message}";
        }
    }

    partial void OnSelectedProjectChanged(ProjectItemViewModel? value)
    {
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

    public async Task CreateAsync(string name, IReadOnlyList<string> folders) =>
        await MutateAsync(() => _writer.CreateProjectAsync(new CreateProjectRequest(name, folders)));

    public async Task UpdateAsync(Guid projectId, string name, IReadOnlyList<string> folders) =>
        await MutateAsync(async () => { await _writer.UpdateProjectAsync(new UpdateProjectRequest(projectId, name, folders)); return projectId; });

    public async Task TogglePauseAsync()
    {
        if (SelectedProject is null) return;
        var selected = SelectedProject;
        if (selected.State != ProjectState.Paused)
        {
            StatusMessage = $"Pausing {selected.Name} and stopping its active indexing work…";
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
            await RefreshAsync(selected.Id);
            StatusMessage = drain.IsCompletedSuccessfully
                ? $"{selected.Name} is paused. Interrupted files remain queued for resume."
                : $"{selected.Name} is paused. Active file cleanup is finishing in the background.";
            return;
        }

        if (_projectPauseDrains.TryGetValue(selected.Id, out var pendingDrain))
        {
            if (!pendingDrain.IsCompleted)
                StatusMessage = $"Finishing {selected.Name}’s pause cleanup before resuming…";
            await pendingDrain;
        }

        StatusMessage = $"Resuming {selected.Name}…";
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
        await RefreshAsync(selected.Id);
        StatusMessage = $"{selected.Name} resumed. Queued indexing work can continue.";
    }

    private async Task ObservePauseDrainAsync(Guid projectId, string projectName, Task drain)
    {
        try
        {
            await drain.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_projectPauseDrains.TryGetValue(projectId, out var current) &&
                    ReferenceEquals(current, drain) && SelectedProject is { Id: var selectedId, State: ProjectState.Paused } &&
                    selectedId == projectId)
                {
                    StatusMessage = $"{projectName} is paused, but its background cleanup needs attention: {exception.Message}";
                }
            });
        }
    }

    public async Task ReindexAsync()
    {
        if (SelectedProject is null) return;
        var id = SelectedProject.Id;
        await MutateAsync(async () => { await _writer.RequestReindexAsync(id); return id; });
    }

    public async Task RetryFailedFilesAsync()
    {
        if (SelectedProject is null) return;
        var id = SelectedProject.Id;
        var result = await _writer.RetryFailedFilesAsync(id);
        await RefreshAsync(id);
        StatusMessage = result switch
        {
            { QueuedCount: 0, AlreadyPendingCount: 1 } =>
                "The failed file is already queued or being processed. Its error will clear after a successful retry.",
            { QueuedCount: 0, AlreadyPendingCount: > 1 } =>
                $"All {result.AlreadyPendingCount} failed files are already queued or being processed. " +
                "Their errors will clear after successful retries.",
            { QueuedCount: 0 } => "No file-specific failures are currently available to retry.",
            { QueuedCount: 1, AlreadyPendingCount: 0 } => "Queued 1 failed file for retry.",
            { AlreadyPendingCount: 0 } => $"Queued {result.QueuedCount} failed files for retry.",
            _ => $"Queued {result.QueuedCount} failed files for retry; {result.AlreadyPendingCount} were already " +
                 "queued or being processed."
        };
    }

    public async Task RepairSemanticIndexAsync()
    {
        if (SelectedProject is not { CanRepairSemanticIndex: true } selected ||
            !_embeddingGenerator.IsAvailable || _embeddingGenerator.Policy is not { } policy) return;
        await _writer.RequestEmbeddingRefreshAsync(selected.Id, policy, retryFailed: true);
        _embeddingPolicyRefreshes.TryBeginRefresh(selected.Id, policy.Key);
        await RefreshSemanticIndexAsync(selected.Id, selected.SearchGeneration);
        await RefreshAsync(selected.Id);
        StatusMessage = $"Queued semantic-index repair for {selected.Name}. Meaning-based coverage will expand in the background.";
    }

    public Task SetCpuUsageProfileAsync(CpuUsageProfile profile)
    {
        _cpuUsageSettings.SetProfile(profile);
        SelectedCpuUsageProfile = _cpuUsageSettings.Profile;
        StatusMessage = $"CPU usage is now {SelectedCpuUsageProfile}. {CpuUsageSummary}";
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
            StatusMessage = $"{model.DisplayName} is active. Automatic re-embedding will retry shortly: {exception.Message}";
            return;
        }

        StatusMessage = queuedProjects == 0
            ? $"{model.DisplayName} is active."
            : $"{model.DisplayName} is active. Re-embedding {queuedProjects} project{(queuedProjects == 1 ? string.Empty : "s")} in the background.";
    }

    public void SetStartWithWindows(bool enabled)
    {
        _windowsStartup.SetEnabled(enabled);
        StartWithWindowsEnabled = _windowsStartup.IsEnabled;
        StatusMessage = StartWithWindowsEnabled
            ? "Context Mole will start automatically with Windows."
            : "Context Mole will not start automatically with Windows.";
    }

    public async Task RemoveAsync()
    {
        if (SelectedProject is null) return;
        var id = SelectedProject.Id;
        await MutateAsync(async () => { await _writer.RemoveProjectAsync(id); return id; });
    }

    public async Task<AiConnectionStatus> ToggleAiConnectionAsync(AiConnectionItemViewModel connection)
    {
        if (!connection.SupportsAutomaticSetup)
            return await _aiConnections.GetStatusAsync(connection.Id).ConfigureAwait(false);

        connection.IsBusy = true;
        NotifyAiConnectionsSummaryChanged();
        try
        {
            var result = connection.IsConfigured
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
            var semanticPolicyKey = _embeddingGenerator.Policy?.Key;
            var semanticModelAvailable = _embeddingGenerator.IsAvailable && semanticPolicyKey is not null;
            var semanticStatusRefreshDue = DateTimeOffset.UtcNow >= _nextSemanticStatusRefreshUtc;
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
                if (_lastProjectCount != Projects.Count)
                {
                    _lastProjectCount = Projects.Count;
                    StatusMessage = Projects.Count == 0
                        ? "Create a project to begin indexing."
                        : "Indexing runs locally in the background.";
                }

                if (SelectedProject is { } selected &&
                    (_fileTypeCountsProjectId != selected.Id ||
                     _fileTypeCountsGeneration != selected.SearchGeneration ||
                     _fileTypeCountsDocumentCount != selected.DocumentCount))
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

            if (fileTypeRefresh is { } refresh)
                await RefreshFileTypeCountsAsync(refresh, cancellationToken).ConfigureAwait(false);
            if (semanticRefresh is { } statusRefresh)
                await RefreshSemanticIndexAsync(statusRefresh.Id, statusRefresh.Generation, cancellationToken)
                    .ConfigureAwait(false);
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
                await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = exception.Message);
            }

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        ReconcileIndexingActivities(_indexingActivities.GetSnapshot(SelectedProject?.Id)));
                    if (++summaryTick % 4 != 0) continue;

                    await RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(RefreshAssetAvailability);
                    Guid? selectedId = null;
                    await Dispatcher.UIThread.InvokeAsync(() => selectedId = SelectedProject?.Id);
                    if (selectedId is { } id)
                        await RefreshErrorsAsync(id, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = exception.Message);
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
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"Semantic search setup: {exception.Message}");
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsPreparingEmbeddingModel = false;
                    RefreshAssetAvailability();
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
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"OCR setup: {exception.Message}");
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsPreparingOcr = false;
                    RefreshOcrAvailability();
                });
            }
        }
    }

    private async Task RefreshAiConnectionsAsync(CancellationToken cancellationToken)
    {
        var connections = AiConnections.ToArray();
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

    private async Task RefreshErrorsSafeAsync(Guid projectId)
    {
        try
        {
            await RefreshErrorsAsync(projectId).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = exception.Message);
        }
    }

    private async Task RefreshSemanticIndexSafeAsync(Guid projectId, long generation)
    {
        try
        {
            await RefreshSemanticIndexAsync(projectId, generation).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = exception.Message);
        }
    }

    private async Task RefreshFileTypeCountsAsync((Guid Id, long Generation, int DocumentCount) refresh,
        CancellationToken cancellationToken)
    {
        var counts = await _store.ListProjectFileTypeCountsAsync(refresh.Id, cancellationToken).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (SelectedProject is not { } selected || selected.Id != refresh.Id ||
                selected.SearchGeneration != refresh.Generation || selected.DocumentCount != refresh.DocumentCount)
                return;

            selected.UpdateFileTypeCounts(counts);
            _fileTypeCountsProjectId = refresh.Id;
            _fileTypeCountsGeneration = refresh.Generation;
            _fileTypeCountsDocumentCount = refresh.DocumentCount;
        });
    }

    private async Task RefreshErrorsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var errors = await _store.ListProjectErrorsAsync(projectId, 12, cancellationToken).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (SelectedProject is { Id: var selectedId } selected && selectedId == projectId)
            {
                selected.UpdateErrors(errors);
            }
        });
    }

    private async Task RefreshSemanticIndexAsync(Guid projectId, long generation,
        CancellationToken cancellationToken = default)
    {
        var policy = _embeddingGenerator.Policy;
        var modelAvailable = _embeddingGenerator.IsAvailable && policy is not null;
        var metadata = modelAvailable
            ? await _store.LoadVectorSnapshotMetadataAsync(projectId, policy!, cancellationToken).ConfigureAwait(false)
            : null;
        _semanticStatusProjectId = projectId;
        _semanticStatusGeneration = metadata?.SearchGeneration ?? generation;
        _semanticStatusPolicyKey = policy?.Key;
        _semanticStatusModelAvailable = modelAvailable;
        _nextSemanticStatusRefreshUtc = DateTimeOffset.UtcNow.AddSeconds(10);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (SelectedProject is { Id: var selectedId } selected && selectedId == projectId)
                selected.UpdateSemanticIndex(metadata, modelAvailable);
        });
    }

    private async Task MutateAsync(Func<Task<Guid>> action)
    {
        var id = await action();
        await RefreshAsync(id);
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

        while (Projects.Count > projects.Count) Projects.RemoveAt(Projects.Count - 1);

        foreach (var project in Projects)
            project.UpdateRuntime(_indexingActivities.GetSnapshot(project.Id));
    }

    private void ReconcileIndexingActivities(IndexingTimingSnapshot snapshot)
    {
        SelectedProject?.UpdateRuntime(snapshot);
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
            ? $"completed average {IndexingActivityItemViewModel.FormatDuration(average)} ({snapshot.CompletedSampleCount} this session)"
            : "completed average —";
        IndexingTimingSummary = $"{activeText} · {completedText}";
        OnPropertyChanged(nameof(HasActiveIndexingItems));
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
