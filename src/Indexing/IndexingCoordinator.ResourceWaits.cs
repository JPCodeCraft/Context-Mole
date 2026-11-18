using System.Diagnostics;

using ContextMole.Core;

using Microsoft.Extensions.Logging;

namespace ContextMole.Indexing;

public sealed partial class IndexingCoordinator
{
    private const int ResourceWaitListedFiles = 10;
    private const long Mebibyte = 1024L * 1024;

    /// <summary>How long a file may wait for a CPU slot or source memory before the wait is logged.</summary>
    internal TimeSpan ResourceWaitWarningDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often a wait that goes on is logged again.</summary>
    internal TimeSpan ResourceWaitWarningRepeat { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The shortest gap between two wait warnings from any workers. Every worker usually waits on the same holders,
    /// and each warning already counts all waiting files, so one line covers them.
    /// </summary>
    internal TimeSpan ResourceWaitWarningSpacing { get; set; } = TimeSpan.FromMinutes(1);

    private long _lastResourceWaitWarning = long.MinValue;

    private ResourceWaitWatch WatchResourceWait(IndexJobLease job, long? sourceBytes) =>
        new(ResourceWaitWarningDelay, ResourceWaitWarningRepeat, waited => LogResourceWait(job, sourceBytes, waited));

    /// <summary>
    /// Names what holds the CPU slots and source memory a file has been waiting for, so a log from a stuck
    /// machine shows the cause and not only the symptom ("Waiting for CPU or memory").
    /// </summary>
    private void LogResourceWait(IndexJobLease job, long? sourceBytes, TimeSpan waited)
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastResourceWaitWarning);
        if (last != long.MinValue && Stopwatch.GetElapsedTime(last, now) < ResourceWaitWarningSpacing) return;
        if (Interlocked.CompareExchange(ref _lastResourceWaitWarning, now, last) != last) return;

        var items = _activities.GetActiveItems();
        var inProgress = items.Where(item => item.IsProcessing && item.JobId != job.JobId)
            .OrderByDescending(item => item.StageElapsed).ToArray();
        var listed = inProgress.Length == 0
            ? "none"
            : string.Join("; ", inProgress.Take(ResourceWaitListedFiles).Select(item =>
                  $"{item.SourcePath} ({item.Stage} for {FormatWait(item.StageElapsed)})")) +
              (inProgress.Length > ResourceWaitListedFiles
                  ? $"; and {inProgress.Length - ResourceWaitListedFiles} more"
                  : string.Empty);
        var needed = _sourceMemory.ReservationFor(sourceBytes ?? 0);
        _logger.LogWarning(
            "{Path} has waited {Waited} for a CPU slot or source memory. CPU: {Cpu}. " +
            "Source memory: {ReservedMiB} of {BudgetMiB} MiB reserved, this file needs {NeededMiB} MiB. " +
            "Files waiting: {WaitingFiles}. Files in progress ({InProgressCount}): {InProgress}",
            job.SourcePath, FormatWait(waited), DescribeCpu(_cpuBudget.DescribeUsage()),
            _sourceMemory.ReservedBytes / Mebibyte, _sourceMemory.BudgetBytes / Mebibyte, needed / Mebibyte,
            items.Count(item => item.IsWaitingForCpu), inProgress.Length, listed);
    }

    private static string DescribeCpu(CpuBudgetUsage? usage) => usage is null
        ? "usage not tracked"
        : $"{usage.SlotsInUse} of {usage.SlotLimit} slots in use" +
          (usage.FullCapacityLeases > 0 ? " (all taken by OCR or semantic embedding)" : string.Empty) +
          $", {usage.WaitingForSlot} files and {usage.WaitingForFullCapacity} OCR or embedding requests queued";

    private static string FormatWait(TimeSpan duration) => duration.TotalMinutes >= 1
        ? $"{duration.TotalMinutes:0.#} min"
        : $"{Math.Max(0, (int)duration.TotalSeconds)} s";
}
