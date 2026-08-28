using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

using ContextMole.App.UI.ViewModels;

namespace ContextMole.App.UI.Views;

public partial class MainWindow : Window
{
    private const double ProjectDragThreshold = 5;
    private static readonly TimeSpan ProjectGapAnimationDuration = TimeSpan.FromMilliseconds(150);
    private bool _addingProject;
    private Guid? _projectDragId;
    private Point _projectDragStart;
    private Point _projectDragPointerOffset;
    private IPointer? _projectDragPointer;
    private Control? _projectDragHandle;
    private Border? _projectDragCard;
    private ListBoxItem? _projectDragContainer;
    private RenderTargetBitmap? _projectDragSnapshot;
    private readonly List<ProjectDragItemLayout> _projectDragItems = [];
    private TranslateTransform? _projectDropSlotTransform;
    private int _projectDragSourceIndex = -1;
    private int _projectDragTargetIndex = -1;
    private double _projectDragSourceTop;
    private double _projectDragItemHeight;
    private bool _projectDragActive;

    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, args) =>
        {
            if (Application.Current is App app && app.ShouldHideOnClose)
            {
                args.Cancel = true;
                Hide();
            }
            else if (Application.Current is App fallbackApp && !fallbackApp.IsQuitting)
            {
                args.Cancel = true;
                WindowState = WindowState.Minimized;
            }
        };
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    private void ShowProjects(object? sender, RoutedEventArgs args) => ViewModel.ShowProjects();

    private void ShowSettings(object? sender, RoutedEventArgs args) => ViewModel.ShowSettings();

    private void ProjectSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (ViewModel.SelectedProject is not null) ViewModel.ShowProjects();
    }

    private void ProjectCardPressed(object? sender, PointerPressedEventArgs args) => ViewModel.ShowProjects();

    private void ProjectDragHandlePressed(object? sender, PointerPressedEventArgs args)
    {
        if (sender is not Control { DataContext: ProjectItemViewModel project } handle ||
            !args.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;

        var container = handle.FindAncestorOfType<ListBoxItem>(true);
        var sourceIndex = container is null ? -1 : ProjectList.IndexFromContainer(container);
        if (container is null || sourceIndex < 0) return;

        FinishProjectDrag(releaseCapture: true);
        _projectDragId = project.Id;
        _projectDragStart = args.GetPosition(ProjectDragOverlay);
        _projectDragPointer = args.Pointer;
        _projectDragHandle = handle;
        _projectDragCard = handle.FindAncestorOfType<Border>(true,
            border => border.Classes.Contains("projectCard"));
        _projectDragContainer = container;
        _projectDragSourceIndex = sourceIndex;
        _projectDragTargetIndex = sourceIndex;
    }

    private void ProjectDragHandleMoved(object? sender, PointerEventArgs args)
    {
        if (_projectDragId is null || args.Pointer != _projectDragPointer ||
            _projectDragHandle is not { } handle) return;
        if (!args.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
        {
            FinishProjectDrag(releaseCapture: true);
            return;
        }

        var position = args.GetPosition(ProjectDragOverlay);
        if (!_projectDragActive)
        {
            var delta = position - _projectDragStart;
            if (Math.Abs(delta.X) < ProjectDragThreshold && Math.Abs(delta.Y) < ProjectDragThreshold) return;

            if (!BeginProjectDrag(handle, position))
            {
                FinishProjectDrag(releaseCapture: true);
                return;
            }
        }

        UpdateProjectDragGhost(position);
        UpdateProjectDropTarget(args.GetPosition(ProjectList));
        args.Handled = true;
    }

    private void ProjectDragHandleReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (args.Pointer != _projectDragPointer) return;
        args.Handled = _projectDragActive;
        FinishProjectDrag(releaseCapture: true);
    }

    private void ProjectDragHandleCaptureLost(object? sender, PointerCaptureLostEventArgs args) =>
        FinishProjectDrag(releaseCapture: false);

    private void FinishProjectDrag(bool releaseCapture)
    {
        var pointer = _projectDragPointer;
        var wasActive = _projectDragActive;
        var projectId = _projectDragId;
        var targetIndex = _projectDragTargetIndex;

        ClearProjectDragVisuals();

        _projectDragId = null;
        _projectDragPointer = null;
        _projectDragHandle = null;
        _projectDragCard = null;
        _projectDragContainer = null;
        _projectDragActive = false;
        _projectDragSourceIndex = -1;
        _projectDragTargetIndex = -1;

        if (releaseCapture) pointer?.Capture(null);
        if (!wasActive) return;

        var orderChanged = projectId is { } id && ViewModel.MoveProject(id, targetIndex);
        ViewModel.EndProjectReorder(orderChanged);
    }

    private bool BeginProjectDrag(Control handle, Point pointerPosition)
    {
        if (_projectDragCard is not { } card || _projectDragContainer is not { } sourceContainer ||
            card.Bounds.Width <= 0 || card.Bounds.Height <= 0) return false;

        var cardPosition = card.TranslatePoint(default, ProjectDragOverlay);
        if (cardPosition is null) return false;

        _projectDragItems.Clear();
        for (var index = 0; index < ViewModel.Projects.Count; index++)
        {
            if (ProjectList.ContainerFromIndex(index) is not ListBoxItem container ||
                container.TranslatePoint(default, ProjectList) is not { } listPosition ||
                container.TranslatePoint(default, ProjectDragOverlay) is not { } overlayPosition) continue;

            var transform = new TranslateTransform();
            transform.Transitions =
            [
                new DoubleTransition
                {
                    Property = TranslateTransform.YProperty,
                    Duration = ProjectGapAnimationDuration,
                    Easing = new CubicEaseOut()
                }
            ];
            container.RenderTransform = transform;
            _projectDragItems.Add(new ProjectDragItemLayout(
                index, container, transform, listPosition.Y, overlayPosition.Y, container.Bounds.Height));
        }

        if (_projectDragItems.All(item => item.Index != _projectDragSourceIndex))
        {
            ClearProjectDragVisuals();
            return false;
        }

        _projectDragSnapshot = CreateProjectDragSnapshot(card);
        _projectDragPointerOffset = pointerPosition - cardPosition.Value;
        _projectDragSourceTop = cardPosition.Value.Y;
        _projectDragItemHeight = sourceContainer.Bounds.Height;

        ProjectDragGhostImage.Source = _projectDragSnapshot;
        ProjectDragGhost.Width = card.Bounds.Width;
        ProjectDragGhost.Height = card.Bounds.Height;
        Canvas.SetLeft(ProjectDragGhost, cardPosition.Value.X);
        Canvas.SetTop(ProjectDragGhost, cardPosition.Value.Y);

        _projectDropSlotTransform = CreateProjectGapTransform();
        ProjectDropSlot.RenderTransform = _projectDropSlotTransform;
        ProjectDropSlot.Width = card.Bounds.Width;
        ProjectDropSlot.Height = card.Bounds.Height;
        Canvas.SetLeft(ProjectDropSlot, cardPosition.Value.X);
        Canvas.SetTop(ProjectDropSlot, cardPosition.Value.Y);

        ProjectDropSlot.IsVisible = true;
        ProjectDragGhost.IsVisible = true;
        sourceContainer.Opacity = 0;

        _projectDragActive = true;
        ViewModel.BeginProjectReorder();
        _projectDragPointer?.Capture(handle);
        return true;
    }

    private void UpdateProjectDragGhost(Point pointerPosition)
    {
        var listPosition = ProjectList.TranslatePoint(default, ProjectDragOverlay);
        if (listPosition is null) return;

        var left = pointerPosition.X - _projectDragPointerOffset.X;
        var minimumLeft = listPosition.Value.X - 5;
        var maximumLeft = listPosition.Value.X + ProjectList.Bounds.Width - ProjectDragGhost.Width + 5;
        Canvas.SetLeft(ProjectDragGhost, Math.Clamp(left, minimumLeft, Math.Max(minimumLeft, maximumLeft)));
        Canvas.SetTop(ProjectDragGhost, pointerPosition.Y - _projectDragPointerOffset.Y);
    }

    private void UpdateProjectDropTarget(Point pointerPosition)
    {
        if (_projectDragItems.Count == 0) return;

        var candidates = _projectDragItems
            .Where(item => item.Index != _projectDragSourceIndex)
            .OrderBy(item => item.Index)
            .ToArray();
        if (candidates.Length == 0) return;

        var itemAfterPointer = candidates.FirstOrDefault(item =>
            pointerPosition.Y < item.ListTop + (item.Height / 2));
        var targetIndex = itemAfterPointer is not null
            ? itemAfterPointer.Index - (_projectDragSourceIndex < itemAfterPointer.Index ? 1 : 0)
            : candidates[^1].Index + (_projectDragSourceIndex > candidates[^1].Index ? 1 : 0);

        targetIndex = Math.Clamp(targetIndex, 0, ViewModel.Projects.Count - 1);
        if (targetIndex == _projectDragTargetIndex) return;
        _projectDragTargetIndex = targetIndex;

        foreach (var item in _projectDragItems)
        {
            var offset = 0d;
            if (targetIndex > _projectDragSourceIndex &&
                item.Index > _projectDragSourceIndex && item.Index <= targetIndex)
                offset = -_projectDragItemHeight;
            else if (targetIndex < _projectDragSourceIndex &&
                     item.Index >= targetIndex && item.Index < _projectDragSourceIndex)
                offset = _projectDragItemHeight;

            item.Transform.Y = offset;
        }

        var target = _projectDragItems.FirstOrDefault(item => item.Index == targetIndex);
        if (target is null || _projectDropSlotTransform is null) return;
        var targetTop = targetIndex <= _projectDragSourceIndex
            ? target.OverlayTop
            : target.OverlayTop + target.Height - _projectDragItemHeight;
        _projectDropSlotTransform.Y = targetTop - _projectDragSourceTop;
    }

    private void ClearProjectDragVisuals()
    {
        foreach (var item in _projectDragItems)
        {
            item.Transform.Transitions = null;
            item.Transform.Y = 0;
            item.Container.RenderTransform = null;
        }

        _projectDragItems.Clear();
        if (_projectDragContainer is not null) _projectDragContainer.Opacity = 1;

        ProjectDragGhost.IsVisible = false;
        ProjectDropSlot.IsVisible = false;
        ProjectDragGhostImage.Source = null;
        ProjectDropSlot.RenderTransform = null;
        _projectDropSlotTransform = null;

        _projectDragSnapshot?.Dispose();
        _projectDragSnapshot = null;
    }

    private RenderTargetBitmap CreateProjectDragSnapshot(Control card)
    {
        var scaling = RenderScaling;
        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(card.Bounds.Width * scaling)),
            Math.Max(1, (int)Math.Ceiling(card.Bounds.Height * scaling)));
        var snapshot = new RenderTargetBitmap(pixelSize, new Vector(96 * scaling, 96 * scaling));
        snapshot.Render(card);
        return snapshot;
    }

    private static TranslateTransform CreateProjectGapTransform()
    {
        var transform = new TranslateTransform();
        transform.Transitions =
        [
            new DoubleTransition
            {
                Property = TranslateTransform.YProperty,
                Duration = ProjectGapAnimationDuration,
                Easing = new CubicEaseOut()
            }
        ];
        return transform;
    }

    private sealed record ProjectDragItemLayout(
        int Index,
        ListBoxItem Container,
        TranslateTransform Transform,
        double ListTop,
        double OverlayTop,
        double Height);

    private async void AddProject(object? sender, RoutedEventArgs args) =>
        await AddProjectAsync(sender as Control);

    internal async Task AddProjectAsync(Control? actionControl)
    {
        if (_addingProject) return;
        _addingProject = true;
        try
        {
            var result = await new ProjectEditorWindow().ShowDialog<ProjectEditorResult?>(this);
            if (result is null) return;
            if (actionControl is not null)
            {
                actionControl.IsHitTestVisible = false;
                actionControl.Opacity = 0.65;
            }
            await RunUiActionAsync(() => ViewModel.CreateAsync(result.Name, result.Folders));
        }
        finally
        {
            if (actionControl is not null)
            {
                actionControl.IsHitTestVisible = true;
                actionControl.Opacity = 1;
            }
            _addingProject = false;
        }
    }

    private async void QuitApplication(object? sender, RoutedEventArgs args)
    {
        if (Application.Current is App app) await app.QuitAsync();
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            await ConfirmWindow.ShowErrorAsync(this, exception.Message);
        }
    }
}
