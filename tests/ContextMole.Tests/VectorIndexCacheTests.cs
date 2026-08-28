using ContextMole.Core;
using ContextMole.Search;

using Microsoft.Extensions.DependencyInjection;

namespace ContextMole.Tests;

public sealed class VectorIndexCacheTests
{
    private const long Mebibyte = 1024L * 1024;
    private const long Gibibyte = 1024L * Mebibyte;

    [Fact]
    public void DefaultAndAdaptiveBudgetsPreserveTheDesktopLimit()
    {
        var cache = new VectorIndexCache();

        Assert.Equal(512L * Mebibyte, cache.ByteBudget);
        Assert.Equal(0, cache.CurrentBytes);
        Assert.Equal(0, cache.Count);
        Assert.Equal(8L * Gibibyte / 20, VectorIndexCache.CalculateAdaptiveBudget(8L * Gibibyte));
        Assert.Equal(512L * Mebibyte, VectorIndexCache.CalculateAdaptiveBudget(16L * Gibibyte));
    }

    [Fact]
    public void SearchRegistrationAcceptsAnExplicitCacheBudget()
    {
        const long budget = 64L * Mebibyte;
        using var provider = new ServiceCollection().AddContextMoleSearch(budget).BuildServiceProvider();

        Assert.Equal(budget, provider.GetRequiredService<VectorIndexCache>().ByteBudget);
    }

    [Fact]
    public void ClearDropsAllCachedIndexesAndResetsByteAccounting()
    {
        var first = Snapshot(1, "first.txt");
        var second = Snapshot(2, "other.txt");
        var entryBytes = EstimateEntryBytes(first.Entries[0]);
        var cache = new VectorIndexCache(entryBytes * 2);
        var factory = new RecordingFactory();
        var firstProject = Guid.NewGuid();
        var secondProject = Guid.NewGuid();
        cache.GetOrCreate(firstProject, first, factory);
        cache.GetOrCreate(secondProject, second, factory);

        Assert.Equal(2, cache.Count);
        Assert.Equal(entryBytes * 2, cache.CurrentBytes);

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CurrentBytes);
        Assert.False(cache.TryGet(firstProject, first.SearchGeneration, first.Policy!.Key, out _));
        Assert.False(cache.TryGet(secondProject, second.SearchGeneration, second.Policy!.Key, out _));
    }

    [Fact]
    public void BudgetPressureEvictsTheLeastRecentlyUsedProject()
    {
        var first = Snapshot(1, "first.txt");
        var second = Snapshot(2, "other.txt");
        var entryBytes = EstimateEntryBytes(first.Entries[0]);
        var cache = new VectorIndexCache(entryBytes);
        var factory = new RecordingFactory();
        var firstProject = Guid.NewGuid();
        var secondProject = Guid.NewGuid();
        cache.GetOrCreate(firstProject, first, factory);
        cache.GetOrCreate(secondProject, second, factory);

        Assert.Equal(1, cache.Count);
        Assert.Equal(entryBytes, cache.CurrentBytes);
        Assert.False(cache.TryGet(firstProject, first.SearchGeneration, first.Policy!.Key, out _));
        Assert.True(cache.TryGet(secondProject, second.SearchGeneration, second.Policy!.Key, out _));
    }

    [Fact]
    public void IndexLargerThanTheBudgetIsCreatedWithoutBeingRetained()
    {
        var snapshot = Snapshot(1, "large.txt");
        var entryBytes = EstimateEntryBytes(snapshot.Entries[0]);
        var cache = new VectorIndexCache(entryBytes - 1);
        var factory = new RecordingFactory();
        var projectId = Guid.NewGuid();

        var first = cache.GetOrCreate(projectId, snapshot, factory);
        var second = cache.GetOrCreate(projectId, snapshot, factory);

        Assert.NotSame(first, second);
        Assert.Equal(2, factory.Creations);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CurrentBytes);
        Assert.False(cache.TryGet(projectId, snapshot.SearchGeneration, snapshot.Policy!.Key, out _));
    }

    [Fact]
    public async Task ClearAndReadsAreSafeDuringConcurrentPressure()
    {
        var snapshot = Snapshot(1, "concurrent.txt");
        var cache = new VectorIndexCache(EstimateEntryBytes(snapshot.Entries[0]));
        var factory = new RecordingFactory();
        var projectId = Guid.NewGuid();
        var cancellationToken = TestContext.Current.CancellationToken;

        var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var iteration = 0; iteration < 100; iteration++)
                cache.GetOrCreate(projectId, snapshot, factory);
        }, cancellationToken));
        var clearer = Task.Run(() =>
        {
            for (var iteration = 0; iteration < 100; iteration++) cache.Clear();
        }, cancellationToken);

        await Task.WhenAll(writers.Append(clearer)).WaitAsync(cancellationToken);
        Assert.InRange(cache.CurrentBytes, 0, cache.ByteBudget);
        Assert.InRange(cache.Count, 0, 1);
    }

    private static VectorSnapshot Snapshot(long generation, string sourcePath)
    {
        var entry = new VectorEntry(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), sourcePath, ".txt",
            DateTimeOffset.UnixEpoch, false, new float[384]);
        return new VectorSnapshot(generation, Policy, [entry]);
    }

    private static long EstimateEntryBytes(VectorEntry entry) =>
        512L + entry.Vector.LongLength * sizeof(float) + 2L * (entry.SourcePath.Length + entry.Extension.Length);

    private static readonly EmbeddingPolicy Policy =
        new("test", "1", "model", "tokenizer", "fp32", 384, 384, "cls", "l2");

    private sealed class RecordingFactory : IVectorIndexFactory
    {
        private int _creations;
        public int Creations => Volatile.Read(ref _creations);

        public IVectorIndex Create(VectorSnapshot snapshot)
        {
            Interlocked.Increment(ref _creations);
            return new TestIndex(snapshot.SearchGeneration);
        }
    }

    private sealed class TestIndex(long generation) : IVectorIndex
    {
        public long SearchGeneration { get; } = generation;
        public IReadOnlyList<VectorMatch> Search(ReadOnlySpan<float> query, int count,
            SearchFilters? filters = null) => [];
    }
}
