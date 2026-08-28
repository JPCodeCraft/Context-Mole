using ContextMole.Core;

namespace ContextMole.UninstallHelper;

internal sealed record UninstallWorkflowRequest(
    string DataDirectory,
    Guid RequestId,
    bool DeleteData,
    TimeSpan CleanupTimeout);

internal sealed record UninstallWorkflowOperations(
    Func<Task<bool>> WaitForParentExitAsync,
    Func<Task<int>> RunUninstallerAsync,
    Func<Task> StopMarkerRefreshAsync,
    Func<bool> RefreshShutdownRequest,
    Func<string, Guid, bool> RemoveShutdownRequest,
    Action RemoveStartupRegistration,
    Func<string, TimeSpan, Task<ContextMoleDataDeletionResult>> DeleteDataAsync,
    Action<string> ShowError,
    Action ScheduleTemporaryDirectoryCleanup,
    Func<string, Guid, bool>? IsShutdownRequestActive = null);

/// <summary>
/// Coordinates the already-validated uninstall helper request. All operating-system actions are
/// supplied by the entry point so the production security checks stay at the process boundary and
/// orchestration can be exercised without launching or uninstalling anything.
/// </summary>
internal static class UninstallWorkflow
{
    public static async Task<int> ExecuteAsync(
        UninstallWorkflowRequest request,
        UninstallWorkflowOperations operations)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operations);

        try
        {
            if (!operations.RefreshShutdownRequest())
                return await AbortForLostShutdownRequestAsync(request, operations, uninstallCompleted: false)
                    .ConfigureAwait(false);

            if (!await operations.WaitForParentExitAsync().ConfigureAwait(false))
            {
                await operations.StopMarkerRefreshAsync().ConfigureAwait(false);
                return await CompleteAsync(request, operations, 2,
                    "Context Mole did not close in time. The application was not uninstalled and local data was kept.")
                    .ConfigureAwait(false);
            }

            if (!operations.RefreshShutdownRequest())
                return await AbortForLostShutdownRequestAsync(request, operations, uninstallCompleted: false)
                    .ConfigureAwait(false);

            var uninstallExitCode = await operations.RunUninstallerAsync().ConfigureAwait(false);
            if (uninstallExitCode != 0)
            {
                await operations.StopMarkerRefreshAsync().ConfigureAwait(false);
                return await CompleteAsync(request, operations, 3,
                    $"The Windows uninstaller exited with code {uninstallExitCode}. Local application data was kept.")
                    .ConfigureAwait(false);
            }

            string? startupCleanupError = null;
            try
            {
                operations.RemoveStartupRegistration();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               System.Security.SecurityException)
            {
                startupCleanupError = exception.Message;
            }

            await operations.StopMarkerRefreshAsync().ConfigureAwait(false);
            if (!operations.RefreshShutdownRequest())
                return await AbortForLostShutdownRequestAsync(request, operations, uninstallCompleted: true)
                    .ConfigureAwait(false);

            if (!request.DeleteData)
            {
                return await CompleteAsync(request, operations, startupCleanupError is null ? 0 : 4,
                    startupCleanupError is null
                        ? null
                        : $"Context Mole was uninstalled, but its Start with Windows entry could not be removed: {startupCleanupError}")
                    .ConfigureAwait(false);
            }

            var deletion = await operations.DeleteDataAsync(request.DataDirectory, request.CleanupTimeout)
                .ConfigureAwait(false);
            if (!deletion.Deleted)
            {
                return await CompleteAsync(request, operations, 5,
                    "Context Mole was uninstalled, but some local application data could not be deleted. " +
                    $"The remaining data was preserved at:\n\n{request.DataDirectory}\n\n" +
                    $"Close any program using that folder and remove it manually. Details: {deletion.Error}")
                    .ConfigureAwait(false);
            }

            if (startupCleanupError is not null)
            {
                return await CompleteAsync(request, operations, 4,
                    $"Context Mole and its local data were removed, but the Start with Windows entry could not be removed: {startupCleanupError}")
                    .ConfigureAwait(false);
            }

            return await CompleteAsync(request, operations, 0, null).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await StopMarkerRefreshBestEffortAsync(operations).ConfigureAwait(false);
            return await CompleteAsync(request, operations, 1,
                $"Context Mole uninstall could not be completed. Local data was kept.\n\n{exception.Message}")
                .ConfigureAwait(false);
        }
        finally
        {
            await StopMarkerRefreshBestEffortAsync(operations).ConfigureAwait(false);
            operations.ScheduleTemporaryDirectoryCleanup();
        }
    }

    private static async Task<int> AbortForLostShutdownRequestAsync(
        UninstallWorkflowRequest request,
        UninstallWorkflowOperations operations,
        bool uninstallCompleted)
    {
        await StopMarkerRefreshBestEffortAsync(operations).ConfigureAwait(false);
        return await CompleteAsync(request, operations, 6, uninstallCompleted
            ? "Context Mole was uninstalled, but shutdown coordination could not be renewed. Local application data was kept."
            : "Context Mole shutdown coordination could not be verified. The application was not uninstalled and local data was kept.")
            .ConfigureAwait(false);
    }

    private static async Task<int> CompleteAsync(
        UninstallWorkflowRequest request,
        UninstallWorkflowOperations operations,
        int exitCode,
        string? errorMessage)
    {
        var markerRemoved = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (operations.RemoveShutdownRequest(request.DataDirectory, request.RequestId))
                {
                    markerRemoved = true;
                    break;
                }
            }
            catch
            {
                // Verify the exact marker below. A failed removal is not silently treated as success.
            }

            try
            {
                var isActive = operations.IsShutdownRequestActive?.Invoke(
                                   request.DataDirectory, request.RequestId) ??
                               ContextMoleProcessCoordination.IsShutdownRequestActive(
                                   request.DataDirectory, request.RequestId);
                if (!isActive)
                {
                    markerRemoved = true;
                    break;
                }
            }
            catch
            {
                // Unreadable or unsafe marker state is treated as still active and retried.
            }

            if (attempt < 2) await Task.Delay(50).ConfigureAwait(false);
        }

        if (!markerRemoved)
        {
            var markerError = ContextMoleProcessCoordination.GetShutdownMarkerManualCleanupMessage(
                request.DataDirectory);
            errorMessage = errorMessage is null ? markerError : errorMessage + "\n\n" + markerError;
            exitCode = 7;
        }

        if (errorMessage is not null) operations.ShowError(errorMessage);
        return exitCode;
    }

    private static async Task StopMarkerRefreshBestEffortAsync(UninstallWorkflowOperations operations)
    {
        try
        {
            await operations.StopMarkerRefreshAsync().ConfigureAwait(false);
        }
        catch
        {
            // Marker renewal is best effort. It must not prevent marker removal, cleanup, or reporting.
        }
    }
}
