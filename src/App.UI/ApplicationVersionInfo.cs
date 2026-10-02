using System.Reflection;

namespace ContextMole.App.UI;

internal static class ApplicationVersionInfo
{
    internal const string BuildVersionSourceMetadataKey = "ContextMole.BuildVersionSource";

    // EntryAssembly can be a test runner or host; always describe the desktop UI.
    internal static Assembly SourceAssembly => typeof(ApplicationVersionInfo).Assembly;

    public static string DisplayLabel { get; } = FormatDisplayLabel(SourceAssembly);

    internal static string FormatDisplayLabel(Assembly assembly) => FormatDisplayLabel(
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        assembly.GetName().Version,
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == BuildVersionSourceMetadataKey)?.Value);

    internal static string FormatDisplayLabel(
        string? informationalVersion, Version? assemblyVersion, string? buildVersionSource)
    {
        // SDK defaults (even Release and commit-stamped builds) aren't a supplied
        // application version. Missing provenance cannot establish one either.
        if (buildVersionSource is not ("Version" or "VersionPrefix"
            or "InformationalVersion" or "AssemblyVersion"))
        {
            return "Development build";
        }

        // The release build supplies Version+SourceRevisionId. Keep explicit
        // prerelease/dev labels while hiding long commit metadata. If only the
        // assembly version was supplied, ignore SDK-default informational 1.0.0.
        var version = buildVersionSource == "AssemblyVersion"
            ? null
            : informationalVersion?.Split('+', 2)[0].Trim();
        if (string.IsNullOrEmpty(version))
        {
            version = assemblyVersion?.Revision == 0
                ? assemblyVersion.ToString(3)
                : assemblyVersion?.ToString();
        }

        return string.IsNullOrEmpty(version) ? "Development build" : $"Version {version}";
    }
}
