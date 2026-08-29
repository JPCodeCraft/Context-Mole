namespace ContextMole.Core;

public readonly record struct SystemMemorySnapshot(
    long TotalPhysicalBytes,
    long AvailablePhysicalBytes,
    long ProcessPrivateBytes);

public interface ISystemMemorySnapshotProvider
{
    SystemMemorySnapshot Capture();
}

/// <summary>
/// Thresholds used only to release disposable caches under memory pressure.
/// These values do not gate or limit indexing concurrency.
/// </summary>
public static class MemoryPressurePolicy
{
    private const long Mebibyte = 1024L * 1024;
    private const long Gibibyte = 1024L * Mebibyte;

    public static long CalculateProcessCleanupThreshold(long totalPhysicalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalPhysicalBytes);
        return Math.Clamp(totalPhysicalBytes / 4, 1536L * Mebibyte, 4L * Gibibyte);
    }

    public static long CalculateSystemReserve(long totalPhysicalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalPhysicalBytes);
        return Math.Max(SaturatingMultiply(totalPhysicalBytes, 15) / 100, 2L * Gibibyte);
    }

    private static long SaturatingMultiply(long value, long multiplier) =>
        value > long.MaxValue / multiplier ? long.MaxValue : value * multiplier;
}
