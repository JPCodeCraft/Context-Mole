using ContextMole.Indexing;

namespace ContextMole.Tests;

public sealed class SourceMemoryAdmissionTests
{
    private const long Mebibyte = 1024L * 1024;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SmallSourcesNeverWaitOrReserve()
    {
        var admission = new SourceMemoryAdmission(100 * Mebibyte, thresholdBytes: 10 * Mebibyte);
        using var large = await admission.AcquireAsync(30 * Mebibyte, Token);
        using var small = await admission.AcquireAsync(9 * Mebibyte, Token);
        Assert.Equal(0, small.ReservedBytes);
        Assert.Equal(90 * Mebibyte, admission.ReservedBytes);
    }

    [Fact]
    public async Task LargeSourcesWaitUntilEarlierReservationsAreReturned()
    {
        var admission = new SourceMemoryAdmission(100 * Mebibyte, thresholdBytes: 10 * Mebibyte);
        var first = await admission.AcquireAsync(20 * Mebibyte, Token);
        var second = admission.AcquireAsync(20 * Mebibyte, Token).AsTask();
        await Task.Delay(50, Token);
        Assert.False(second.IsCompleted);

        first.Dispose();
        first.Dispose(); // A lease is returned exactly once.
        using var admitted = await second.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(60 * Mebibyte, admission.ReservedBytes);
    }

    [Fact]
    public async Task SourceLargerThanTheBudgetIsAdmittedAlone()
    {
        var admission = new SourceMemoryAdmission(100 * Mebibyte, thresholdBytes: 10 * Mebibyte);
        var huge = await admission.AcquireAsync(500 * Mebibyte, Token);
        Assert.Equal(100 * Mebibyte, huge.ReservedBytes);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var waiting = admission.AcquireAsync(10 * Mebibyte, waitCancellation.Token).AsTask();
        await Task.Delay(50, Token);
        Assert.False(waiting.IsCompleted);

        await waitCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        huge.Dispose();
        Assert.Equal(0, admission.ReservedBytes);
        using var next = await admission.AcquireAsync(10 * Mebibyte, Token);
        Assert.Equal(30 * Mebibyte, next.ReservedBytes);
    }

    [Theory]
    [InlineData(0L, 2L * 1024 * 1024 * 1024)]
    [InlineData(2L * 1024 * 1024 * 1024, 1L * 1024 * 1024 * 1024)]
    [InlineData(16L * 1024 * 1024 * 1024, 4L * 1024 * 1024 * 1024)]
    [InlineData(128L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024)]
    public void BudgetIsAQuarterOfAvailableMemoryWithinBounds(long available, long expected) =>
        Assert.Equal(expected, SourceMemoryAdmission.CalculateBudget(available));
}
