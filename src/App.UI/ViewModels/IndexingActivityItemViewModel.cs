using ContextMole.Core;
using ContextMole.Indexing;

namespace ContextMole.App.UI.ViewModels;

public sealed class IndexingActivityItemViewModel : ViewModelBase
{
    private string _sourcePath = string.Empty;
    private string _fileName = string.Empty;
    private string _stageDisplay = string.Empty;
    private string _elapsedDisplay = string.Empty;
    private string _stageElapsedDisplay = string.Empty;
    private double _pipelinePosition;
    private bool _isProgressIndeterminate;

    public IndexingActivityItemViewModel(IndexingActivitySnapshot activity)
    {
        JobId = activity.JobId;
        UpdateFrom(activity);
    }

    public Guid JobId { get; }
    public string SourcePath { get => _sourcePath; private set => SetProperty(ref _sourcePath, value); }
    public string FileName { get => _fileName; private set => SetProperty(ref _fileName, value); }
    public string StageDisplay { get => _stageDisplay; private set => SetProperty(ref _stageDisplay, value); }
    public string ElapsedDisplay { get => _elapsedDisplay; private set => SetProperty(ref _elapsedDisplay, value); }
    public string StageElapsedDisplay { get => _stageElapsedDisplay; private set => SetProperty(ref _stageElapsedDisplay, value); }
    public double PipelinePosition { get => _pipelinePosition; private set => SetProperty(ref _pipelinePosition, value); }
    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public void UpdateFrom(IndexingActivitySnapshot activity)
    {
        if (activity.JobId != JobId) throw new ArgumentException("An activity view model cannot change identity.", nameof(activity));
        SourcePath = activity.SourcePath;
        FileName = Path.GetFileName(activity.SourcePath);
        StageDisplay = StageName(activity);
        ElapsedDisplay = $"Total {FormatDuration(activity.Elapsed)}";
        StageElapsedDisplay = $"This stage {FormatDuration(activity.StageElapsed)}";
        PipelinePosition = StagePosition(activity.Stage);
        IsProgressIndeterminate = activity.IsWaitingForResources;
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
        return $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}.{duration.Milliseconds / 100}";
    }

    private static string StageName(IndexingActivitySnapshot activity) => activity.Stage switch
    {
        IndexingPipelineStage.InspectingSource => "Checking file availability",
        IndexingPipelineStage.QueuedForAdmission => QueueDescription(activity.MemoryWait),
        IndexingPipelineStage.WaitingForMemory => MemoryWaitDescription(activity.MemoryWait),
        IndexingPipelineStage.WaitingForCpu => "Memory reserved · waiting for processor capacity",
        IndexingPipelineStage.Hashing => "Calculating fingerprint",
        IndexingPipelineStage.PreparingRevision => "Preparing index revision",
        IndexingPipelineStage.ExtractingContent => "Extracting content, attachments, or OCR",
        IndexingPipelineStage.ChunkingText => "Normalizing and chunking text",
        IndexingPipelineStage.GeneratingEmbeddings => "Generating semantic embeddings",
        IndexingPipelineStage.VerifyingSource => "Verifying the source is unchanged",
        IndexingPipelineStage.WritingIndex => "Writing the searchable index",
        IndexingPipelineStage.RecordingError => "Recording an indexing error",
        _ => activity.Stage.ToString()
    };

    private static string QueueDescription(MemoryAdmissionWaitSnapshot? wait)
    {
        if (wait is null) return "Queued for resource admission";
        var estimate = FormatBytes(wait.RequestedBytes);
        return wait.Reason switch
        {
            MemoryAdmissionWaitReason.NestedSerialization =>
                $"Queued for the document parser · position {wait.QueuePosition} · estimated {estimate}",
            MemoryAdmissionWaitReason.Exclusive =>
                $"Queued while another file runs exclusively · position {wait.QueuePosition} · estimated {estimate}",
            _ => $"Queued for admission · position {wait.QueuePosition} · estimated {estimate}"
        };
    }

    private static string MemoryWaitDescription(MemoryAdmissionWaitSnapshot? wait)
    {
        if (wait is null) return "Waiting for available memory";
        if (wait.Reason == MemoryAdmissionWaitReason.ProcessSoftLimit)
        {
            return $"Would exceed Context Mole’s {FormatBytes(wait.ProcessSoftLimitBytes)} memory target · " +
                   $"currently {FormatBytes(wait.ProcessPrivateBytes)} in use · estimated {FormatBytes(wait.RequestedBytes)}";
        }

        return $"Needs {FormatBytes(wait.RequiredAvailableBytes)} available · " +
               $"{FormatBytes(wait.AvailablePhysicalBytes)} available · keeping {FormatBytes(wait.RequiredReserveBytes)} free";
    }

    private static string FormatBytes(long bytes)
    {
        const double mebibyte = 1024d * 1024d;
        const double gibibyte = 1024d * mebibyte;
        return bytes >= gibibyte
            ? $"{bytes / gibibyte:0.00} GiB"
            : $"{Math.Max(0, bytes) / mebibyte:0} MiB";
    }

    private static double StagePosition(IndexingPipelineStage stage) => stage switch
    {
        IndexingPipelineStage.InspectingSource => 5,
        IndexingPipelineStage.QueuedForAdmission => 8,
        IndexingPipelineStage.WaitingForMemory => 10,
        IndexingPipelineStage.WaitingForCpu => 12,
        IndexingPipelineStage.Hashing => 15,
        IndexingPipelineStage.PreparingRevision => 25,
        IndexingPipelineStage.ExtractingContent => 45,
        IndexingPipelineStage.ChunkingText => 60,
        IndexingPipelineStage.GeneratingEmbeddings => 75,
        IndexingPipelineStage.VerifyingSource => 88,
        IndexingPipelineStage.WritingIndex => 96,
        IndexingPipelineStage.RecordingError => 100,
        _ => 0
    };
}
