namespace ContextMole.App.UI.ViewModels;

internal partial class MainViewModel
{
    private CancellationTokenSource? _ocrPreparation;
    private bool _ocrSetupCanceled;

    public void CancelOcrSetup()
    {
        _ocrPreparation?.Cancel();
        OnPropertyChanged(nameof(CanCancelOcrSetup));
    }

    public void ShowAiConnections()
    {
        IsAiConnectionsExpanded = true;
        ShowSettings();
    }

    public bool CanMoveSelectedProjectUp => SelectedProject is { } selected && Projects.IndexOf(selected) > 0;
    public bool CanMoveSelectedProjectDown => SelectedProject is { } selected &&
        Projects.IndexOf(selected) is var index && index >= 0 && index < Projects.Count - 1;

    public bool MoveSelectedProject(int direction)
    {
        if (SelectedProject is not { } selected || direction == 0) return false;
        var index = Projects.IndexOf(selected);
        var target = Math.Clamp(index + Math.Sign(direction), 0, Projects.Count - 1);
        if (index < 0 || index == target) return false;
        BeginProjectReorder();
        var moved = MoveProject(selected.Id, target);
        // Recycled ListBox containers can clear the two-way selection during a Move.
        if (moved) SelectedProject = selected;
        var saved = EndProjectReorder(moved);
        if (moved && saved) Notify("project_order", $"Moved {selected.Name} to position {target + 1} of {Projects.Count}.");
        NotifyProjectReorderAvailability();
        return moved;
    }

    private void NotifyProjectReorderAvailability()
    {
        OnPropertyChanged(nameof(CanMoveSelectedProjectUp));
        OnPropertyChanged(nameof(CanMoveSelectedProjectDown));
    }
}
