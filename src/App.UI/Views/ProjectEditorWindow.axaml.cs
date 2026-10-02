using System.Collections.ObjectModel;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

using ContextMole.Core;

using Microsoft.Extensions.DependencyInjection;

namespace ContextMole.App.UI.Views;

public sealed record ProjectEditorResult(string Name, IReadOnlyList<string> Folders);

public partial class ProjectEditorWindow : Window
{
    private readonly ObservableCollection<string> _folders = [];
    private readonly HashSet<string> _originalFolderKeys = new(PathComparer());
    private readonly Func<ProjectEditorResult, Task>? _persist;
    private bool _saving;
    private bool _confirmingSave;

    public ProjectEditorWindow() : this(null)
    {
    }

    public ProjectEditorWindow(ProjectSummary? project, Func<ProjectEditorResult, Task>? persist = null)
    {
        InitializeComponent();
        _persist = persist;
        Closing += (_, args) => args.Cancel = _saving;
        ProjectNameBox.Text = project?.Name ?? string.Empty;
        foreach (var folder in project?.Folders ?? [])
        {
            _folders.Add(folder.Path);
            _originalFolderKeys.Add(ProjectValidation.FolderKey(folder.Path));
        }
        FoldersList.ItemsSource = _folders;
        Title = project is null ? "Add project" : "Edit project";
        Opened += (_, _) =>
        {
            ProjectNameBox.Focus();
            ProjectNameBox.SelectAll();
        };
    }

    private async void AddFolders(object? sender, RoutedEventArgs args)
    {
        var selected = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select folders to index",
            AllowMultiple = true
        });
        foreach (var folder in selected)
        {
            var path = folder.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path) && !_folders.Contains(path, PathComparer())) _folders.Add(Path.GetFullPath(path));
        }
        ValidateInput();
    }

    private void RemoveFolder(object? sender, RoutedEventArgs args)
    {
        if (FoldersList.SelectedItem is string selected) _folders.Remove(selected);
        RemoveFolderButton.IsEnabled = FoldersList.SelectedItem is not null;
        ValidateInput();
    }

    private void FolderSelectionChanged(object? sender, SelectionChangedEventArgs args) =>
        RemoveFolderButton.IsEnabled = FoldersList.SelectedItem is not null;

    private void ProjectNameChanged(object? sender, TextChangedEventArgs args)
    {
        if (!string.IsNullOrEmpty(ValidationBlock.Text)) ValidateInput();
    }

    private void Cancel(object? sender, RoutedEventArgs args) => Close(null);

    private async void Save(object? sender, RoutedEventArgs args)
    {
        if (_saving || _confirmingSave) return;
        if (!ValidateInput()) return;
        var result = new ProjectEditorResult(ProjectValidation.NormalizeName(ProjectNameBox.Text!), _folders.ToArray());
        var retainedKeys = result.Folders.Select(ProjectValidation.FolderKey).ToHashSet(PathComparer());
        var removed = _originalFolderKeys.Where(key => !retainedKeys.Contains(key)).ToArray();
        if (removed.Length > 0)
        {
            _confirmingSave = true;
            try
            {
                if (!await ConfirmWindow.AskAsync(this, "Remove watched folders?",
                    $"Saving removes {removed.Length} watched {(removed.Length == 1 ? "folder" : "folders")} from this project and removes their cached indexed evidence from search. " +
                    "Original folders and files remain untouched. Other watched folders stay included.\n\n" + string.Join("\n", removed),
                    "Save and remove folders", destructive: true)) return;
            }
            finally { _confirmingSave = false; }
        }
        _saving = true;
        EditorForm.IsEnabled = false;
        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        SaveButton.Content = "Saving…";
        ValidationBlock.Text = string.Empty;
        try
        {
            if (_persist is not null) await _persist(result);
            _saving = false;
            Close(result);
        }
        catch (Exception exception)
        {
            ValidationBlock.Text = exception.Message;
        }
        finally
        {
            _saving = false;
            EditorForm.IsEnabled = true;
            SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            SaveButton.Content = "Save project";
        }
    }

    private bool ValidateInput()
    {
        var error = string.Empty;
        try
        {
            ProjectValidation.NormalizeName(ProjectNameBox.Text ?? string.Empty);
        }
        catch (ContextMoleException exception) { error = exception.Message; }
        if (error.Length == 0)
        {
            try
            {
                ProjectValidation.NormalizeFolders(_folders.ToArray(),
                    Program.Services.GetRequiredService<IAppPaths>().DataDirectory, _originalFolderKeys);
            }
            catch (ContextMoleException exception) { error = exception.Message; }
        }
        ValidationBlock.Text = error;
        return error.Length == 0;
    }

    private static StringComparer PathComparer() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
