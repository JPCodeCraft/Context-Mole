using System.Text.Json;

using ContextMole.Broker.Protocol;
using ContextMole.Core;
using ContextMole.Infrastructure;

namespace ContextMole.Tests;

public sealed class EmbeddingBatchingTests
{
    private static readonly EmbeddingPolicy Policy =
        new("test", "1", "model", "tokenizer", "fp32", 384, 384, "cls", "l2");

    [Fact]
    public void LengthGroupingReducesPaddingAndPreservesEveryPassagesOutputOrder()
    {
        var encoded = Enumerable.Range(0, 64).Select(index =>
        {
            var tokens = new long[index % 2 == 0 ? 512 : 16];
            tokens[0] = index;
            return tokens;
        }).ToArray();
        var paddedTokens = 0;
        var batchSize = 8;
        var vectors = GraniteEmbeddingGenerator.RunBatches(encoded, batch =>
        {
            Assert.InRange(batch.Count, 1, 8);
            paddedTokens += batch.Count * batch.Max(tokens => tokens.Length);
            return batch.Select(tokens => new[] { (float)tokens[0] }).ToArray();
        }, ref batchSize, TestContext.Current.CancellationToken);

        Assert.Equal(32 * (512 + 16), paddedTokens);
        Assert.True(paddedTokens < encoded.Length * 512);
        Assert.Equal(Enumerable.Range(0, encoded.Length).Select(index => (float)index),
            vectors.Select(vector => vector[0]));
    }

    [Fact]
    public void MemoryPressureReducesBatchSizeAndNeverDropsOrDuplicatesPassages()
    {
        var encoded = Enumerable.Range(0, 17).Select(index => new[] { (long)index }).ToArray();
        var attemptedSizes = new List<int>();
        var batchSize = 8;
        var vectors = GraniteEmbeddingGenerator.RunBatches(encoded, batch =>
        {
            attemptedSizes.Add(batch.Count);
            if (batch.Count > 2) throw new OutOfMemoryException();
            return batch.Select(tokens => new[] { (float)tokens[0] }).ToArray();
        }, ref batchSize, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { 8, 4 }, attemptedSizes.Take(2));
        Assert.All(attemptedSizes.Skip(2), size => Assert.InRange(size, 1, 2));
        Assert.Equal(2, batchSize);
        Assert.Equal(Enumerable.Range(0, encoded.Length).Select(index => (float)index),
            vectors.Select(vector => vector[0]));
    }

    [Fact]
    public void CancellationStopsBeforeAnotherInferenceBatch()
    {
        using var cancellation = new CancellationTokenSource();
        var batchSize = 8;
        var calls = 0;
        Assert.Throws<OperationCanceledException>(() => GraniteEmbeddingGenerator.RunBatches(
            Enumerable.Range(0, 16).Select(index => new[] { (long)index }).ToArray(), batch =>
            {
                calls++;
                cancellation.Cancel();
                return batch.Select(tokens => new[] { (float)tokens[0] }).ToArray();
            }, ref batchSize, cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void EqualLengthPassagesKeepDeterministicBatchComposition()
    {
        var encoded = Enumerable.Range(0, 17).Select(index =>
        {
            var tokens = new long[16];
            tokens[0] = index;
            return tokens;
        }).ToArray();
        var batches = new List<long[]>();
        var batchSize = 8;
        GraniteEmbeddingGenerator.RunBatches(encoded, batch =>
        {
            batches.Add(batch.Select(tokens => tokens[0]).ToArray());
            return batch.Select(_ => new[] { 1f }).ToArray();
        }, ref batchSize, TestContext.Current.CancellationToken);

        Assert.Equal(new long[] { 0, 1, 2, 3, 4, 5, 6, 7 }, batches[0]);
        Assert.Equal(new long[] { 8, 9, 10, 11, 12, 13, 14, 15 }, batches[1]);
        Assert.Equal(new long[] { 16 }, batches[2]);
    }

    [Fact]
    public async Task BrokerBoundsLargeDocumentsAndPreservesVectorOrder()
    {
        var requestSizes = new List<int>();
        await WithBrokerAsync((request, _) =>
        {
            var passages = request.Payload.Deserialize<BrokerEmbedPassagesRequest>(BrokerJson.Options)!.Passages;
            requestSizes.Add(passages.Count);
            return Task.FromResult(JsonSerializer.SerializeToElement(new EmbeddingBatch(
                passages.Select(text => new[] { float.Parse(text, System.Globalization.CultureInfo.InvariantCulture) })
                    .ToArray(), Policy), BrokerJson.Options));
        }, async client =>
        {
            var passages = Enumerable.Range(0, 257).Select(index =>
                index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var result = await client.EmbedPassagesAsync(passages, TestContext.Current.CancellationToken);
            Assert.Equal(new[] { 64, 64, 64, 64, 1 }, requestSizes);
            Assert.Equal(Policy.Key, result.Policy.Key);
            Assert.Equal(Enumerable.Range(0, passages.Length).Select(index => (float)index),
                result.Vectors.Select(vector => vector[0]));
        });
    }

    [Fact]
    public async Task BrokerRejectsAModelChangeBetweenWindows()
    {
        var calls = 0;
        await WithBrokerAsync((request, _) =>
        {
            calls++;
            var passages = request.Payload.Deserialize<BrokerEmbedPassagesRequest>(BrokerJson.Options)!.Passages;
            return Task.FromResult(JsonSerializer.SerializeToElement(new EmbeddingBatch(
                passages.Select(_ => new[] { 1f }).ToArray(),
                calls == 1 ? Policy : Policy with { Revision = "2" }), BrokerJson.Options));
        }, async client =>
        {
            var exception = await Assert.ThrowsAsync<BrokerRpcException>(() => client.EmbedPassagesAsync(
                Enumerable.Repeat("passage", 129).ToArray(), TestContext.Current.CancellationToken));
            Assert.Equal("embedding_policy_changed", exception.Code);
            Assert.True(exception.Retryable);
            Assert.Equal(2, calls);
        });
    }

    [Fact]
    public async Task BrokerRejectsMissingVectorsInsteadOfReturningPartialEmbeddings()
    {
        await WithBrokerAsync((_, _) => Task.FromResult(JsonSerializer.SerializeToElement(
            new EmbeddingBatch([], Policy), BrokerJson.Options)), async client =>
        {
            var exception = await Assert.ThrowsAsync<BrokerRpcException>(() => client.EmbedPassagesAsync(
                Enumerable.Repeat("passage", 65).ToArray(), TestContext.Current.CancellationToken));
            Assert.Equal("embedding_response_invalid", exception.Code);
        });
    }

    private static async Task WithBrokerAsync(
        Func<BrokerRpcRequest, CancellationToken, Task<JsonElement>> dispatch,
        Func<BrokerRpcClient, Task> action)
    {
        using var paths = new StorageTestPaths();
        var endpoint = new BrokerEndpoint(paths.DataDirectory);
        using var stop = new CancellationTokenSource();
        await using var server = new BrokerPipeServer(endpoint, endpoint.GetOrCreateAuthenticationToken(),
            "1.0", "batch-test", DateTimeOffset.UtcNow, dispatch);
        var serverTask = server.RunAsync(stop.Token);
        var client = new BrokerRpcClient(paths.DataDirectory,
            () => throw new InvalidOperationException("The test broker should already be running."),
            clientVersion: "1.0", deploymentId: "batch-test");
        try
        {
            await action(client);
        }
        finally
        {
            await stop.CancelAsync();
            await serverTask;
        }
    }
}
