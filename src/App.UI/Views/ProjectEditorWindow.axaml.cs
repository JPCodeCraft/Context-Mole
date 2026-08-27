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

    public ProjectEditorWindow() : this(null)
    {
    }

    public ProjectEditorWindow(ProjectSummary? project)
    {
        InitializeComponent();
        ProjectNameBox.Text = project?.Name ?? string.Empty;
        foreach (var folder in project?.Folders ?? []) _folders.Add(folder.Path);
        FoldersList.ItemsSource = _folders;
        Title = project is null ? "Add project" : "Edit project";
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

    private void Cancel(object? sender, RoutedEventArgs args) => Close(null);

    private void Save(object? sender, RoutedEventArgs args)
    {
        if (!ValidateInput()) return;
        Close(new ProjectEditorResult(ProjectNameBox.Text!.Trim(), _folders.ToArray()));
    }

    private bool ValidateInput()
    {
        var error = string.Empty;
        if (string.IsNullOrWhiteSpace(ProjectNameBox.Text)) error = "Enter a project name.";
        else if (_folders.Count == 0) error = "Select at least one folder.";
        else
        {
            var canonical = _folders.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).ToArray();
            var appData = Path.TrimEndingDirectorySeparator(Program.Services.GetRequiredService<IAppPaths>().DataDirectory);
            if (canonical.Any(path => string.Equals(path, appData, PathComparison()) || IsWithin(path, appData) || IsWithin(appData, path)))
                error = "The application data directory and its parent folders cannot be indexed.";
            else if (canonical.Any(path => !Directory.Exists(path)))
                error = "Every selected folder must be available.";
            else if (canonical.Any(path =>
                     {
                         var info = new DirectoryInfo(path);
                         return (info.Attributes & FileAttributes.ReparsePoint) != 0 && !string.IsNullOrEmpty(info.LinkTarget);
                     }))
                error = "Symbolic-link roots cannot be indexed.";
            for (var left = 0; left < canonical.Length && error.Length == 0; left++)
                for (var right = left + 1; right < canonical.Length; right++)
                {
                    if (string.Equals(canonical[left], canonical[right], PathComparison()) || IsWithin(canonical[left], canonical[right]) || IsWithin(canonical[right], canonical[left]))
                    {
                        error = "Folders in one project cannot be duplicated or nested.";
                        break;
                    }
                }
        }
        ValidationBlock.Text = error;
        return error.Length == 0;
    }

    private static bool IsWithin(string child, string parent) => child.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison());
    private static StringComparer PathComparer() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}