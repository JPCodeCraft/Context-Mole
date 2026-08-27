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
    private readonly GraniteModelInstaller _modelInstaller;
    private readonly AiConnectionsService _aiConnections;
    private readonly IndexingActivityTracker _indexingActivities;
    private readonly EmbeddingPolicyRefreshTracker _embeddingPolicyRefreshes;
    private readonly ApplicationUpdateService _applicationUpdates;
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

    public MainViewModel(
        IIndexWriter writer,
        ISearchStore store,
        IOcrEngine ocrEngine,
        IEmbeddingGenerator embeddingGenerator,
        IEmbeddingModelSettings embeddingModelSettings,
        ICpuUsageSettings cpuUsageSettings,
        WindowsStartupService windowsStartup,
        GraniteModelInstaller modelInstaller,
        AiConnectionsService aiConnections,
        IndexingActivityTracker indexingActivities,
        EmbeddingPolicyRefreshTracker embeddingPolicyRefreshes,
        ApplicationUpdateService applicationUpdates)
    {
        _writer = writer;
        _store = store;
        _ocrEngine = ocrEngine;
        _embeddingGenerator = embeddingGenerator;
        _embeddingModelSettings = embeddingModelSettings;
        _cpuUsageSettings = cpuUsageSettings;
        _windowsStartup = windowsStartup;
        _windowsStartup.Initialize();
        _modelInstaller = modelInstaller;
        _aiConnections = aiConnections;
        _indexingActivities = indexingActivities;
        _embeddingPolicyRefreshes = embeddingPolicyRefreshes;
        _applicationUpdates = applicationUpdates;
        _applicationUpdates.SnapshotChanged += OnApplicationUpdateSnapshotChanged;
        ApplicationUpdate = _applicationUpdates.Snapshot;
        SelectedCpuUsageProfile = _cpuUsageSettings.Profile;
        SelectedEmbeddingModel = GraniteEmbeddingModels.Get(_embeddingModelSettings.Model);
        StartWithWindowsEnabled = _windowsStartup.IsEnabled;
        foreach (var client in _aiConnections.Clients)
            AiConnections.Add(new AiConnectionItemViewModel(client));
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
    public partial MainSection CurrentSection { get; set; } = MainSection.Projects;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Starting local index…";

    [ObservableProperty]
    public partial string IndexingTimingSummary { get; set; } = "No files are currently active.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuUsageSummary))]
    public partial CpuUsageProfile SelectedCpuUsageProfile { get; set; } = CpuUsageProfile.Normal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmbeddingModelSummary))]
    [NotifyPropertyChangedFor(nameof(SemanticSearchStatusMessage))]
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
    public partial bool IsPreparingEmbeddingModel { get; set; } = true;

    [ObservableProperty]
    public partial bool StartWithWindowsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApplicationUpdateProgressVisible))]
    [NotifyPropertyChangedFor(nameof(IsApplicationUpdateReady))]
    [NotifyPropertyChangedFor(nameof(CanRestartForUpdate))]
    [NotifyPropertyChangedFor(nameof(ApplicationUpdateMessage))]
    [NotifyPropertyChangedFor(nameof(ApplicationUpdateStatusLabel))]
    public partial ApplicationUpdateSnapshot ApplicationUpdate { get; set; } = ApplicationUpdateSnapshot.Disabled;

    public bool IsOcrUnavailable => !_ocrEngine.IsAvailable;
    public bool IsOcrAvailable => !IsOcrUnavailable;
    public bool IsWindowsStartupSupported => _windowsStartup.IsSupported;
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
            return $"{SelectedCpuUsageProfile}: max {percentage}% — up to {_cpuUsageSettings.ThreadLimit} of " +
                   $"{_cpuUsageSettings.LogicalProcessorCount} logical threads globally across all projects.";
        }
    }
    public string OcrStatusMessage => _ocrEngine.UnavailableReason ?? "Local PP-OCRv6 medium OCR is ready.";
    public bool IsSemanticSearchUnavailable => !_embeddingGenerator.IsAvailable;
    public bool CanInstallSemanticModel => _modelInstaller.IsSupported;
    public bool CanSetUpSemanticSearch => IsSemanticSearchUnavailable && CanInstallSemanticModel &&
        !IsChangingEmbeddingModel && !IsPreparingEmbeddingModel;
    public bool CanChangeEmbeddingModel => CanInstallSemanticModel &&
        !IsChangingEmbeddingModel && !IsPreparingEmbeddingModel;
    public string EmbeddingModelSummary => $"{SelectedEmbeddingModel.Description}. Both choices support multilingual search and use the same 384-dimensional index format.";
    public string SemanticSearchStatusLabel => IsPreparingEmbeddingModel ? "Loading"
        : IsSemanticSearchUnavailable ? "Optional" : "Ready";
    public string SemanticSearchStatusMessage => IsPreparingEmbeddingModel
        ? $"Loading {SelectedEmbeddingModel.DisplayName} in the background."
        : !IsSemanticSearchUnavailable
        ? $"{SelectedEmbeddingModel.DisplayName} is ready for multilingual meaning-based search."
        : !_modelInstaller.IsSupported
            ? _embeddingGenerator.UnavailableReason ?? "Semantic search is unavailable on this platform."
            : $"Keyword search is ready. Download {SelectedEmbeddingModel.DisplayName} to add multilingual meaning-based search.";
    public bool HasSelection => SelectedProject is not null;
    public bool HasNoSelection => SelectedProject is null;
    public bool IsProjectsSection => CurrentSection == MainSection.Projects;
    public bool IsSettingsSection => CurrentSection == MainSection.Settings;
    public bool HasActiveIndexingItems => ActiveIndexingItems.Count > 0;
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
    }

    public void ShowProjects() => CurrentSection = MainSection.Projects;

    public void ShowSettings() => CurrentSection = MainSection.Settings;

    partial void OnSelectedProjectChanged(ProjectItemViewModel? value)
    {
        ReconcileIndexingActivities(_indexingActivities.GetSnapshot(value?.Id));
        if (value is not null) _ = RefreshErrorsSafeAsync(value.Id);
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
        await MutateAsync(async () =>
        {
            await _writer.SetProjectPausedAsync(selected.Id, selected.State != ProjectState.Paused);
            return selected.Id;
        });
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
        var queued = await _writer.RetryFailedFilesAsync(id);
        await RefreshAsync(id);
        StatusMessage = queued switch
        {
            0 => "No failed files needed to be queued.",
            1 => "Queued 1 failed file for retry.",
            _ => $"Queued {queued} failed files for retry."
        };
    }

    public Task SetCpuUsageProfileAsync(CpuUsageProfile profile)
    {
        _cpuUsageSettings.SetProfile(profile);
        SelectedCpuUsageProfile = _cpuUsageSettings.Profile;
        StatusMessage = $"CPU usage is now {SelectedCpuUsageProfile}. {CpuUsageSummary}";
        return Task.CompletedTask;
    }

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
                    var metadata = await _store.LoadVectorSnapshotMetadataAsync(project.Id);
                    if (!metadata.IsComplete ||
                        !string.Equals(metadata.Policy?.Key, policy.Key, StringComparison.Ordinal))
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
        try
        {
            var result = connection.IsConfigured
                ? await _aiConnections.DisconnectAsync(connection.Id).ConfigureAwait(false)
                : await _aiConnections.ConnectAsync(connection.Id).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => connection.Apply(result));
            return result;
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => connection.IsBusy = false);
        }
    }

    public async Task RefreshAsync(Guid? preferredProjectId = null, CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var projects = await _store.ListProjectsAsync(cancellationToken).ConfigureAwait(false);
            (Guid Id, long Generation, int DocumentCount)? fileTypeRefresh = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var selectedId = preferredProjectId ?? SelectedProject?.Id;
                ReconcileProjects(projects);
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
            });

            if (fileTypeRefresh is { } refresh)
                await RefreshFileTypeCountsAsync(refresh, cancellationToken).ConfigureAwait(false);
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
                    await Dispatcher.UIThread.InvokeAsync(RefreshOcrAvailability);
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

    private async Task PrepareOcrAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _ocrEngine.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
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
                await Dispatcher.UIThread.InvokeAsync(RefreshOcrAvailability);
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
                await Dispatcher.UIThread.InvokeAsync(() => connection.Apply(status));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                var status = new AiConnectionStatus(connection.Client, AiConnectionState.Conflict, exception.Message);
                await Dispatcher.UIThread.InvokeAsync(() => connection.Apply(status));
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => connection.IsBusy = false);
            }
        })).ConfigureAwait(false);
    }

    private void RefreshOcrAvailability()
    {
        var available = _ocrEngine.IsAvailable;
        var message = OcrStatusMessage;
        if (_reportedOcrAvailable == available && string.Equals(_reportedOcrMessage, message, StringComparison.Ordinal))
            return;

        _reportedOcrAvailable = available;
        _reportedOcrMessage = message;
        OnPropertyChanged(nameof(IsOcrUnavailable));
        OnPropertyChanged(nameof(IsOcrAvailable));
        OnPropertyChanged(nameof(OcrStatusMessage));
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
    }

    private void ReconcileIndexingActivities(IndexingTimingSnapshot snapshot)
    {
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

        var activeText = snapshot.ActiveItems.Count == 0
            ? "No files active"
            : $"{snapshot.ActiveItems.Count} active";
        var completedText = snapshot.AverageCompletedDuration is { } average
            ? $"completed average {IndexingActivityItemViewModel.FormatDuration(average)} ({snapshot.CompletedSampleCount} this session)"
            : "completed average —";
        IndexingTimingSummary = $"{activeText} · {completedText}";
        OnPropertyChanged(nameof(HasActiveIndexingItems));
    }

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