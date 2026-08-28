using ContextMole.Core;

using Microsoft.Extensions.Logging;

namespace ContextMole.App.UI;

internal sealed class ProjectOrderService
{
    private readonly object _gate = new();
    private readonly ILogger<ProjectOrderService> _logger;
    private readonly string _orderPath;
    private Guid[] _order;

    public ProjectOrderService(IAppPaths paths, ILogger<ProjectOrderService> logger)
    {
        _logger = logger;
        _orderPath = Path.Combine(paths.DataDirectory, "ui-state", "project-order.txt");
        _order = LoadOrder();
    }

    public IReadOnlyList<ProjectSummary> Apply(IReadOnlyList<ProjectSummary> projects)
    {
        lock (_gate) return Apply(projects, _order);
    }

    public void Save(IReadOnlyList<Guid> projectIds)
    {
        var normalized = projectIds.Distinct().ToArray();
        lock (_gate)
        {
            if (_order.SequenceEqual(normalized)) return;

            var directory = Path.GetDirectoryName(_orderPath)
                ?? throw new IOException("The UI state directory could not be resolved.");
            Directory.CreateDirectory(directory);
            var temporaryPath = _orderPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllLines(temporaryPath, normalized.Select(id => id.ToString("D")));
                File.Move(temporaryPath, _orderPath, overwrite: true);
                _order = normalized;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(exception, "Could not clean up the temporary project-order file");
                }
            }
        }
    }

    internal static IReadOnlyList<ProjectSummary> Apply(
        IReadOnlyList<ProjectSummary> projects,
        IReadOnlyList<Guid> order)
    {
        if (projects.Count < 2 || order.Count == 0) return projects;

        var projectsById = projects.ToDictionary(project => project.Id);
        var ordered = new List<ProjectSummary>(projects.Count);
        var added = new HashSet<Guid>();
        foreach (var projectId in order)
        {
            if (projectsById.TryGetValue(projectId, out var project) && added.Add(projectId))
                ordered.Add(project);
        }

        foreach (var project in projects)
        {
            if (added.Add(project.Id)) ordered.Add(project);
        }

        return ordered;
    }

    private Guid[] LoadOrder()
    {
        try
        {
            if (!File.Exists(_orderPath)) return [];
            return File.ReadAllLines(_orderPath)
                .Select(line => Guid.TryParse(line.Trim(), out var id) ? id : (Guid?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not load the saved project order");
            return [];
        }
    }
}
