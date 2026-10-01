using ContextMole.Core;

namespace ContextMole.Tests;

public sealed class ProjectEditorValidationTests
{
    [Fact]
    public void NormalizedProjectNameUsesTheSameBackendLengthRule()
    {
        Assert.Equal("Research", ProjectValidation.NormalizeName("  Research\u200B  "));
        Assert.Equal(ProjectValidation.MaximumNameLength,
            ProjectValidation.NormalizeName(new string('a', ProjectValidation.MaximumNameLength)).Length);
        Assert.Equal("invalid_project_name", Assert.Throws<ContextMoleException>(() =>
            ProjectValidation.NormalizeName(new string('a', ProjectValidation.MaximumNameLength + 1))).Code);
    }

    [Fact]
    public void WholeDriveAncestorAndDirectoryBoundariesAreHandledCorrectly()
    {
        var data = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ContextMole-validation", "data"));
        var root = Path.GetPathRoot(data)!;
        Assert.True(ProjectValidation.IsSameOrChild(data, root));
        Assert.False(ProjectValidation.IsSameOrChild(data + "-other", data));
        Assert.Equal("unsafe_folder", Assert.Throws<ContextMoleException>(() =>
            ProjectValidation.NormalizeFolders([root], data)).Code);
    }

    [Fact]
    public void ExistingUnavailableRootsCanBeRetainedButNewUnavailableRootsAreRejected()
    {
        var taskRoot = Path.Combine(Path.GetTempPath(), "ContextMole-validation-" + Guid.NewGuid().ToString("N"));
        var unavailable = Path.Combine(taskRoot, "unavailable-source");
        var data = Path.Combine(taskRoot, "data");
        var permitted = new HashSet<string> { ProjectValidation.FolderKey(unavailable) };
        Assert.Equal(unavailable, Assert.Single(ProjectValidation.NormalizeFolders([unavailable], data, permitted)));
        Assert.Equal("folder_unavailable", Assert.Throws<ContextMoleException>(() =>
            ProjectValidation.NormalizeFolders([unavailable], data)).Code);
        Assert.Equal("nested_folder", Assert.Throws<ContextMoleException>(() =>
            ProjectValidation.NormalizeFolders([unavailable, unavailable], data, permitted)).Code);
    }
}
