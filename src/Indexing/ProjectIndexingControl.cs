using ContextMole.Core;

namespace ContextMole.Indexing;

/// <summary>
/// Coordinates project pause and resume with in-process indexing work.
/// </summary>
public interface IProjectIndexingControl
{
    /// <summary>Durably excludes an exact source path and cancels its admitted in-process work.</summary>
    Task<FileExclusionResult> ExcludeFileAsync(Guid projectId, string sourcePath,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>Includes an exact source path again and queues a fresh source-backed revision.</summary>
    Task<FileExclusionResult> IncludeFileAsync(Guid projectId, string sourcePath,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>
    /// Prevents new work for <paramref name="projectId"/> and immediately requests cancellation of work
    /// already admitted by this process.
    /// </summary>
    /// <remarks>
    /// Call this before atomically changing the project's durable state to paused and requeueing its running jobs.
    /// The durable operation must use an independent token rather than a canceled indexing-job token.
    /// </remarks>
    void BeginPause(Guid projectId);

    /// <summary>
    /// Completes after the paused project's affected workers and any lease claims already issued by storage
    /// have fully unwound.
    /// </summary>
    /// <remarks>
    /// Call this after the project's durable pause/requeue operation has completed. Canceling the wait does not
    /// remove the in-process pause marker; a later call can continue waiting for the same cleanup.
    /// </remarks>
    Task DrainPausedAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Allows this process to accept work for <paramref name="projectId"/> again.
    /// </summary>
    /// <remarks>
    /// Call this after <see cref="DrainPausedAsync"/> and before changing the project's durable state back to active.
    /// Also call this to roll back <see cref="BeginPause"/> when the durable pause operation fails.
    /// </remarks>
    void Resume(Guid projectId);
}
