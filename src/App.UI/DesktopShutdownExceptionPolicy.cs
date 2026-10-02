namespace ContextMole.App.UI;

internal static class DesktopShutdownExceptionPolicy
{
    internal static bool IsExpectedTrayCancellation(bool isQuitting, bool isLinux, Exception exception) =>
        isQuitting && isLinux && exception is OperationCanceledException &&
        exception.StackTrace?.Contains("Avalonia.FreeDesktop.DBusTrayIconImpl.WatchAsync",
            StringComparison.Ordinal) == true;
}
