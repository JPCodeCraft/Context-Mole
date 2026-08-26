using System.Collections.ObjectModel;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using MCPIndexSearch.Core;
using MCPIndexSearch.Indexing;
using MCPIndexSearch.Infrastructure;

namespace MCPIndexSearch.App.UI.ViewModels;

internal partial class MainViewModel : ViewModelBase
{
    private readonly IIndexWriter _writer;
    private readonly ISearchStore _store;
    private readonly IOcrEngine _ocrEngine;
    private readonly IEmbeddingGenerator _embeddingGenerator;
    private readonly GraniteModelInstaller _modelInstaller;
    private readonly CodexMcpConfigurationService _codexConfiguration;
    private readonly IndexingActivityTracker _indexingActivities;
    private readonly ApplicationUpdateService _applicationUpdates;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CancellationTokenSource? _polling;
    private bool? _reportedOcrAvailable;
    private string? _reportedOcrMessage;
    private bool _hasAnyActiveIndexingItems;

    public MainViewModel(
        IIndexWriter writer,
        ISearchStore store,
        IOcrEngine ocrEngine,
        IEmbeddingGenerator embeddingGenerator,
        GraniteModelInstaller modelInstaller,
        CodexMcpConfigurationService codexConfiguration,
        IndexingActivityTracker indexingActivities,
        ApplicationUpdateService applicationUpdates)
    {
        _writer = writer;
        _store = store;
        _ocrEngine = ocrEngine;
        _embeddingGenerator = embeddingGenerator;
        _modelInstaller = modelInstaller;
        _codexConfiguration = codexConfiguration;
        _indexingActivities = indexingActivities;
        _applicationUpdates = applicationUpdates;
        _applicationUpdates.SnapshotChanged += OnApplicationUpdateSnapshotChanged;
        ApplicationUpdate = _applicationUpdates.Snapshot;
    }

    public ObservableCollection<ProjectItemViewModel> Projects { get; } = [];
    public ObservableCollection<IndexingActivityItemViewModel> ActiveIndexingItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(HasNoSelection))]
    public partial ProjectItemViewModel? SelectedProject { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Starting local index…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCodexConnected))]
    [NotifyPropertyChangedFor(nameof(CanChangeCodexConnection))]
    [NotifyPropertyChangedFor(nameof(CodexConnectionAction))]
    public partial CodexMcpConnectionState CodexConnectionState { get; set; } = CodexMcpConnectionState.Disconnected;

    [ObservableProperty]
    public partial string CodexConnectionMessage { get; set; } = "Checking the Codex connection…";

    [ObservableProperty]
    public partial string IndexingTimingSummary { get; set; } = "No files are currently active.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApplicationUpdateVisible))]
    [NotifyPropertyChangedFor(nameof(IsApplicationUpdateProgressVisible))]
    [NotifyPropertyChangedFor(nameof(IsApplicationUpdateReady))]
    [NotifyPropertyChangedFor(nameof(CanRestartForUpdate))]
    [NotifyPropertyChangedFor(nameof(ApplicationUpdateMessage))]
    public partial ApplicationUpdateSnapshot ApplicationUpdate { get; set; } = ApplicationUpdateSnapshot.Disabled;

    public bool IsOcrUnavailable => !_ocrEngine.IsAvailable;
    public string OcrStatusMessage => _ocrEngine.UnavailableReason ?? "Local PP-OCRv6 medium OCR is ready.";
    public bool IsSemanticSearchUnavailable => !_embeddingGenerator.IsAvailable;
    public bool CanInstallSemanticModel => _modelInstaller.IsSupported;
    public string SemanticSearchStatusMessage => !_modelInstaller.IsSupported
        ? _embeddingGenerator.UnavailableReason ?? "Semantic search is unavailable on this platform."
        : "Keyword search is ready. Install the optional local Granite model to add multilingual meaning-based search.";
    public bool HasSelection => SelectedProject is not null;
    public bool HasNoSelection => SelectedProject is null;
    public bool HasActiveIndexingItems => ActiveIndexingItems.Count > 0;
    public bool HasNoActiveIndexingItems => ActiveIndexingItems.Count == 0;
    public bool IsApplicationUpdateVisible => ApplicationUpdate.State is
        ApplicationUpdateState.Checking or
        ApplicationUpdateState.Downloading or
        ApplicationUpdateState.Ready or
        ApplicationUpdateState.Error;
    public bool IsApplicationUpdateProgressVisible => ApplicationUpdate.State == ApplicationUpdateState.Downloading;
    public bool IsApplicationUpdateReady => ApplicationUpdate.State == ApplicationUpdateState.Ready;
    public bool CanRestartForUpdate => IsApplicationUpdateReady && !_hasAnyActiveIndexingItems;
    public string ApplicationUpdateMessage => IsApplicationUpdateReady && _hasAnyActiveIndexingItems
        ? $"{ApplicationUpdate.Message} Restart will be available when indexing is idle."
        : ApplicationUpdate.Message;
    public bool IsCodexConnected => CodexConnectionState == CodexMcpConnectionState.Connected;
    public bool CanChangeCodexConnection => CodexConnectionState is CodexMcpConnectionState.Connected
        or CodexMcpConnectionState.Disconnected or CodexMcpConnectionState.UpdateRequired;
    public string CodexConnectionAction => CodexConnectionState switch
    {
        CodexMcpConnectionState.Connected => "Disconnect Codex",
        CodexMcpConnectionState.UpdateRequired => "Update Codex connection",
        _ => "Connect to Codex"
    };

    public void RefreshAssetAvailability()
    {
        RefreshOcrAvailability();
        OnPropertyChanged(nameof(IsSemanticSearchUnavailable));
        OnPropertyChanged(nameof(CanInstallSemanticModel));
        OnPropertyChanged(nameof(SemanticSearchStatusMessage));
    }

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
        _ = PrepareOcrAsync(_polling.Token);
        _ = RefreshCodexConnectionAsync(_polling.Token);
        _ = PollAsync(_polling.Token);
    }

    public void StopPolling()
    {
        var polling = Interlocked.Exchange(ref _polling, null);
        if (polling is null) return;
        polling.Cancel();
        polling.Dispose();
        _applicationUpdates.Stop();
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

    public async Task RemoveAsync()
    {
        if (SelectedProject is null) return;
        var id = SelectedProject.Id;
        await MutateAsync(async () => { await _writer.RemoveProjectAsync(id); return id; });
    }

    public async Task<CodexMcpConnectionStatus> ToggleCodexConnectionAsync()
    {
        var result = IsCodexConnected
            ? await _codexConfiguration.DisconnectAsync().ConfigureAwait(false)
            : await _codexConfiguration.ConnectAsync().ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => ApplyCodexConnectionStatus(result));
        return result;
    }

    public async Task RefreshAsync(Guid? preferredProjectId = null)
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var projects = await _store.ListProjectsAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var selectedId = preferredProjectId ?? SelectedProject?.Id;
                ReconcileProjects(projects);
                SelectedProject = selectedId is null
                    ? Projects.FirstOrDefault()
                    : Projects.FirstOrDefault(project => project.Id == selectedId) ?? Projects.FirstOrDefault();
                ReconcileIndexingActivities(_indexingActivities.GetSnapshot(SelectedProject?.Id));
                StatusMessage = Projects.Count == 0 ? "Create a project to begin indexing." : "Indexing runs locally in the background.";
            });
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var errorTick = 0;
        try
        {
            await RefreshAsync().ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await RefreshAsync().ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(RefreshOcrAvailability);
                    if (++errorTick % 4 == 0)
                    {
                        Guid? selectedId = null;
                        await Dispatcher.UIThread.InvokeAsync(() => selectedId = SelectedProject?.Id);
                        if (selectedId is { } id) await RefreshErrorsAsync(id).ConfigureAwait(false);
                    }
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

    private async Task RefreshCodexConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var status = await _codexConfiguration.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyCodexConnectionStatus(status));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => CodexConnectionMessage = exception.Message);
        }
    }

    private void ApplyCodexConnectionStatus(CodexMcpConnectionStatus status)
    {
        CodexConnectionState = status.State;
        CodexConnectionMessage = status.Message;
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

    private async Task RefreshErrorsAsync(Guid projectId)
    {
        var errors = await _store.ListProjectErrorsAsync(projectId, 25).ConfigureAwait(false);
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

        TimeSpan? activeAverage = snapshot.ActiveItems.Count == 0
            ? null
            : TimeSpan.FromMilliseconds(snapshot.ActiveItems.Average(item => item.Elapsed.TotalMilliseconds));
        var activeText = snapshot.ActiveItems.Count == 0
            ? "No files active"
            : $"{snapshot.ActiveItems.Count} active · average active time {IndexingActivityItemViewModel.FormatDuration(activeAverage!.Value)}";
        var completedText = snapshot.AverageCompletedDuration is { } average
            ? $"completed average {IndexingActivityItemViewModel.FormatDuration(average)} ({snapshot.CompletedSampleCount} this session)"
            : "completed average —";
        IndexingTimingSummary = $"{activeText} · {completedText}";
        OnPropertyChanged(nameof(HasActiveIndexingItems));
        OnPropertyChanged(nameof(HasNoActiveIndexingItems));
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