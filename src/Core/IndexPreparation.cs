namespace ContextMole.Core;

public static class IndexPreparation
{
    public const string ExtractionVersion = "layout-v2";
    public const string ChunkingVersion = "spans-v2";
    public const string SemanticVersion = "body-context-v2";
    public const string Version = ExtractionVersion + "/" + ChunkingVersion + "/" + SemanticVersion;
}

public sealed record SectionDraft(
    Guid Id, Guid ContentId, int Ordinal, string Text, string? Heading,
    IReadOnlyList<string> HeadingPath, string Kind, SourceLocation Location);

public static class ProjectValidation
{
    public const int MaximumNameLength = 120;

    public static string NormalizeName(string name)
    {
        var normalized = TextNormalization.ForDisplay(name);
        if (normalized.Length is < 1 or > MaximumNameLength)
            throw new ContextMoleException("invalid_project_name", $"Project name must contain 1–{MaximumNameLength} characters.");
        return normalized;
    }

    public static string CanonicalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
    }

    public static string FolderKey(string path)
    {
        var canonical = CanonicalPath(path);
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? canonical.ToUpperInvariant() : canonical;
    }

    public static bool IsSameOrChild(string candidate, string parent)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(candidate, parent, comparison)) return true;
        // Filesystem roots retain their ending separator; adding another loses the boundary match.
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, comparison);
    }

    public static IReadOnlyList<string> NormalizeFolders(IReadOnlyList<string> folders, string appDataDirectory,
        IReadOnlySet<string>? allowedUnavailableFolderKeys = null)
    {
        if (folders.Count == 0)
            throw new ContextMoleException("folders_required", "Select at least one folder.");
        string[] canonical;
        try { canonical = folders.Select(CanonicalPath).ToArray(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ContextMoleException("invalid_folder", "One of the selected folder paths is invalid.", innerException: exception);
        }
        var appDataKey = FolderKey(appDataDirectory);
        for (var index = 0; index < canonical.Length; index++)
        {
            var key = FolderKey(canonical[index]);
            if (IsSameOrChild(key, appDataKey) || IsSameOrChild(appDataKey, key))
                throw new ContextMoleException("unsafe_folder", "A project folder cannot contain or be contained by the application data directory.");
            for (var other = index + 1; other < canonical.Length; other++)
            {
                var otherKey = FolderKey(canonical[other]);
                if (IsSameOrChild(key, otherKey) || IsSameOrChild(otherKey, key))
                    throw new ContextMoleException("nested_folder", "A project cannot contain duplicate or nested folder roots.");
            }
            var exists = Directory.Exists(canonical[index]);
            if (!exists && !(allowedUnavailableFolderKeys?.Contains(key) ?? false))
                throw new ContextMoleException("folder_unavailable", $"Folder does not exist or is unavailable: {canonical[index]}", true);
            if (!exists) continue;
            try
            {
                var info = new DirectoryInfo(canonical[index]);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 && !string.IsNullOrEmpty(info.LinkTarget))
                    throw new ContextMoleException("unsafe_folder", $"Folder roots cannot be symbolic links: {canonical[index]}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ContextMoleException("unsafe_folder", $"The folder root could not be checked: {canonical[index]}", innerException: exception);
            }
        }
        return canonical;
    }
}
