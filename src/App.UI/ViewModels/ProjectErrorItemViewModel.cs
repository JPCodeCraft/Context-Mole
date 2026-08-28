using ContextMole.Core;

namespace ContextMole.App.UI.ViewModels;

public sealed class ProjectErrorItemViewModel(ProjectErrorInfo source)
{
    public ProjectErrorInfo Source { get; } = source;
    public long Id => Source.Id;
    public string Message => Source.Message;
    public string SourcePath => Source.SourcePath ?? "No source path was recorded.";
    public string FileName => string.IsNullOrWhiteSpace(Source.SourcePath)
        ? "Project operation"
        : Path.GetFileName(Source.SourcePath);
    public string CodeDisplay => string.Join(' ', Source.Code
        .Split('_', StringSplitOptions.RemoveEmptyEntries)
        .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
    public string CreatedDisplay => Source.CreatedUtc.ToLocalTime().ToString("g");
    public string AttemptDisplay => Source.Attempt == 1
        ? "1 failed attempt"
        : $"{Source.Attempt} failed attempts";
    public bool HasFailedAttempts => Source.Attempt > 0;
    public bool IsRetryable => Source.Retryable;
    public bool IsPermanent => !Source.Retryable;
}
