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

    public void UpdateFrom(IndexingActivitySnapshot activity)
    {
        if (activity.JobId != JobId) throw new ArgumentException("An activity view model cannot change identity.", nameof(activity));
        SourcePath = activity.SourcePath;
        FileName = Path.GetFileName(activity.SourcePath);
        StageDisplay = StageName(activity.Stage);
        ElapsedDisplay = $"Total {FormatDuration(activity.Elapsed)}";
        StageElapsedDisplay = $"This stage {FormatDuration(activity.StageElapsed)}";
        PipelinePosition = StagePosition(activity.Stage);
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
        return $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}.{duration.Milliseconds / 100}";
    }

    private static string StageName(IndexingPipelineStage stage) => stage switch
    {
        IndexingPipelineStage.InspectingSource => "Checking file availability",
        IndexingPipelineStage.Hashing => "Calculating fingerprint",
        IndexingPipelineStage.PreparingRevision => "Preparing index revision",
        IndexingPipelineStage.ExtractingContent => "Extracting content, attachments, or OCR",
        IndexingPipelineStage.ChunkingText => "Normalizing and chunking text",
        IndexingPipelineStage.GeneratingEmbeddings => "Generating semantic embeddings",
        IndexingPipelineStage.VerifyingSource => "Verifying the source is unchanged",
        IndexingPipelineStage.WritingIndex => "Writing the searchable index",
        IndexingPipelineStage.RecordingError => "Recording an indexing error",
        _ => stage.ToString()
    };

    private static double StagePosition(IndexingPipelineStage stage) => stage switch
    {
        IndexingPipelineStage.InspectingSource => 5,
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