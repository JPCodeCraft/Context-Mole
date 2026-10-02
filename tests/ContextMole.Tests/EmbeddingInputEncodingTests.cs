using System.Text.Json;

using ContextMole.Broker.Protocol;
using ContextMole.Core;
using ContextMole.Infrastructure;

using Tokenizers.HuggingFace.Tokenizer;

namespace ContextMole.Tests;

public sealed class EmbeddingInputEncodingTests
{
    [Theory]
    [InlineData(EmbeddingModelChoice.Granite97M, 2)]
    public void ModelSpecificTemplatesAreUsedForCountingAndEncoding(EmbeddingModelChoice choice, int specialCount)
    {
        var model = GraniteEmbeddingModels.Get(choice);
        var path = Path.Combine(Path.GetTempPath(), $"context-mole-tokenizer-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, Fixture(model));
            using var tokenizer = Tokenizer.FromFile(path);
            var official = tokenizer.Encode("hello world", true).First().Ids.Select(id => (long)id).ToArray();
            var encoded = GraniteEmbeddingInputEncoding.Encode(tokenizer, "hello world", model, 512, true);
            Assert.Equal(official, encoded);
            Assert.Equal(2 + specialCount, encoded.Length);
            Assert.Equal(encoded.Length, GraniteEmbeddingInputEncoding.CountTokens(tokenizer, "hello world"));
            Assert.Equal(specialCount, GraniteEmbeddingInputEncoding.CountTokens(tokenizer, string.Empty));
            Assert.Equal(model.BosTokenId, encoded[0]);
            if (model.EosTokenId is { } eos) Assert.Equal(eos, encoded[^1]);
            else Assert.Equal(11, encoded[^1]);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(EmbeddingModelChoice.Granite97M)]
    public void QueryTruncationRetainsRequiredSuffixAndDocumentsNeverSilentlyTruncate(EmbeddingModelChoice choice)
    {
        var model = GraniteEmbeddingModels.Get(choice);
        var bodyCapacity = 512 - (model.EosTokenId is null ? 1 : 2);
        var exact = Complete(model, bodyCapacity);
        Assert.Equal(512, GraniteEmbeddingInputEncoding.Limit(exact, model, 512, true).Length);
        var over = Complete(model, bodyCapacity + 1);
        var error = Assert.Throws<ContextMoleException>(() => GraniteEmbeddingInputEncoding.Limit(over, model, 512, true));
        Assert.Equal("embedding_input_too_long", error.Code);
        var query = GraniteEmbeddingInputEncoding.Limit(over, model, 256, false);
        Assert.Equal(256, query.Length);
        Assert.Equal(model.BosTokenId, query[0]);
        Assert.Equal(model.EosTokenId ?? 10, query[^1]);
        Assert.All(query.Skip(1).Take(query.Length - (model.EosTokenId is null ? 1 : 2)), id => Assert.Equal(10, id));
    }

    [Fact]
    public void InvalidPinnedTemplateIsRejected()
    {
        var model = GraniteEmbeddingModels.Get(EmbeddingModelChoice.Granite97M);
        Assert.Throws<ContextMoleException>(() => GraniteEmbeddingInputEncoding.Limit([179934, 10], model, 512, true));
        Assert.Throws<ContextMoleException>(() => GraniteEmbeddingInputEncoding.Limit([2, 10, 179938], model, 512, true));
    }

    [Fact]
    public void LegacyPolicyKeyAndJsonRemainStableAndCorrectedPolicySurvivesBothSerializers()
    {
        var legacy = new EmbeddingPolicy("model", "revision", "sha", "tokenizer", "fp32", 384, 384,
            "cls", "l2", "old-preparation");
        const string expected = "model:revision:sha:tokenizer:fp32:384:384:cls:l2:old-preparation";
        Assert.Equal(expected, legacy.Key);
        foreach (var options in new[] { new JsonSerializerOptions(), BrokerJson.Options })
        {
            var json = JsonSerializer.Serialize(legacy, options);
            Assert.DoesNotContain("tokenization", json, StringComparison.OrdinalIgnoreCase);
            var restored = JsonSerializer.Deserialize<EmbeddingPolicy>(json, options)!;
            Assert.Null(restored.TokenizationVersion);
            Assert.Equal(expected, restored.Key);
            Assert.Equal(json, JsonSerializer.Serialize(restored, options));
            var corrected = legacy with { TokenizationVersion = "bos-eos-v1" };
            Assert.NotEqual(legacy.Key, corrected.Key);
            Assert.Equal(expected + ":bos-eos-v1", corrected.Key);
            Assert.Equal(corrected, JsonSerializer.Deserialize<EmbeddingPolicy>(JsonSerializer.Serialize(corrected, options), options));
        }
        Assert.Equal("bos-eos-v1", GraniteEmbeddingModels.Get(EmbeddingModelChoice.Granite97M).TokenizationVersion);
    }

    [Fact]
    public void StaleQuantizationValidationFallsBackUntilCorrected97MIsVerified()
    {
        var path = Path.Combine(Path.GetTempPath(), $"context-mole-validation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        var compact = GraniteEmbeddingModels.Get(EmbeddingModelChoice.Granite97M);
        try
        {
            File.WriteAllText(Path.Combine(path, "model_quint8_avx2.onnx"), "fixture");
            Assert.False(GraniteEmbeddingProfiles.CanUseQuantized(path, compact, true));
            File.WriteAllText(Path.Combine(path, "validation.json"), "{\"quantized_enabled\":true}");
            Assert.False(GraniteEmbeddingProfiles.CanUseQuantized(path, compact, true));
            Assert.True(GraniteEmbeddingProfiles.NeedsTokenizationValidation(path, compact, true));
            Assert.False(GraniteEmbeddingProfiles.NeedsTokenizationValidation(path, compact, false));
            var fallback = compact.CreatePolicy(GraniteEmbeddingProfiles.CanUseQuantized(path, compact, true));
            Assert.Equal("fp32", fallback.Precision);
            Assert.Equal(compact.Fp32Sha, fallback.ModelSha256);
            Assert.Equal("bos-eos-v1", fallback.TokenizationVersion);

            File.WriteAllText(Path.Combine(path, "validation.json"), "{\"quantized_enabled\":true,\"tokenization_version\":\"bos-eos-v1\"}");
            Assert.True(GraniteEmbeddingProfiles.CanUseQuantized(path, compact, true));
            Assert.False(GraniteEmbeddingProfiles.NeedsTokenizationValidation(path, compact, true));
            Assert.False(GraniteEmbeddingProfiles.CanUseQuantized(path, compact, false));
            var optimized = compact.CreatePolicy(true);
            Assert.Equal("quint8-avx2", optimized.Precision);
            Assert.NotEqual(fallback.Key, optimized.Key);

            File.WriteAllText(Path.Combine(path, "quantization-disabled"), "failed parity");
            Assert.False(GraniteEmbeddingProfiles.CanUseQuantized(path, compact, true));
            Assert.False(GraniteEmbeddingProfiles.NeedsTokenizationValidation(path, compact, true));
            File.Delete(Path.Combine(path, "quantization-disabled"));
            foreach (var invalid in new[] { "{", "[]", "{\"quantized_enabled\":false,\"tokenization_version\":\"bos-eos-v1\"}" })
            {
                File.WriteAllText(Path.Combine(path, "validation.json"), invalid);
                Assert.False(GraniteEmbeddingProfiles.CanUseQuantized(path, compact, true));
            }
        }
        finally { Directory.Delete(path, true); }
    }

    private static uint[] Complete(GraniteEmbeddingModelDefinition model, int bodyCount) =>
        new[] { (uint)model.BosTokenId }.Concat(Enumerable.Repeat(10u, bodyCount))
            .Concat(model.EosTokenId is { } eos ? [(uint)eos] : Array.Empty<uint>()).ToArray();

    internal static string Fixture(GraniteEmbeddingModelDefinition model)
    {
        var eos = model.EosTokenId;
        var single = new List<object>
        {
            new { SpecialToken = new { id = "<bos>", type_id = 0 } },
            new { Sequence = new { id = "A", type_id = 0 } }
        };
        var special = new Dictionary<string, object>
        {
            ["<bos>"] = new { id = "<bos>", ids = new[] { model.BosTokenId }, tokens = new[] { "<bos>" } }
        };
        var vocabulary = new Dictionary<string, long> { ["[UNK]"] = 0, ["hello"] = 10, ["world"] = 11, ["<bos>"] = model.BosTokenId };
        if (eos is { } id)
        {
            single.Add(new { SpecialToken = new { id = "<eos>", type_id = 0 } });
            special["<eos>"] = new { id = "<eos>", ids = new[] { id }, tokens = new[] { "<eos>" } };
            vocabulary["<eos>"] = id;
        }
        return JsonSerializer.Serialize(new
        {
            version = "1.0", added_tokens = Array.Empty<object>(),
            pre_tokenizer = new { type = "Whitespace" },
            post_processor = new { type = "TemplateProcessing", single, pair = single, special_tokens = special },
            model = new { type = "WordLevel", vocab = vocabulary, unk_token = "[UNK]" }
        });
    }
}

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class EmbeddingTokenizationMigrationTests
{
    [Fact]
    public async Task CorrectedQueriesCannotLoadLegacyVectorsAndRefreshPreservesCanonicalEvidence()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(token);
        var (project, folder) = await database.CreateProjectAsync("Special-token upgrade", token);
        var path = Path.Combine(database.Paths.SourceDirectory, "stable.txt");
        await File.WriteAllTextAsync(path, "Canonical evidence survives the tokenization upgrade.", token);
        var pending = await database.ObserveAndLeaseAsync(project, folder, path, false, token);
        var legacy = StorageTestDatabase.TestEmbeddingPolicy;
        var corrected = legacy with { TokenizationVersion = "bos-eos-v1" };
        var committed = await database.CommitAsync(pending.Job, pending.Sha256, pending.File.Length,
            new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero),
            "Canonical evidence survives the tokenization upgrade.", cancellationToken: token, embeddingPolicy: legacy);

        Assert.Single((await database.Store.LoadVectorSnapshotAsync(project, legacy, token)).Entries);
        Assert.Empty((await database.Store.LoadVectorSnapshotAsync(project, corrected, token)).Entries);
        var excluded = await database.Store.LoadVectorSnapshotMetadataAsync(project, corrected, token);
        Assert.Equal(1, excluded.ExcludedDocumentCount);
        await database.Writer.RequestEmbeddingRefreshAsync(project, corrected, false, token);
        var job = Assert.IsType<IndexJobLease>(await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), token));
        Assert.Equal(IndexJobKind.EmbeddingRefresh, job.Kind);
        var source = Assert.IsType<EmbeddingRefreshSource>(await database.Writer.LoadEmbeddingRefreshSourceAsync(job, token));
        Assert.Equal(committed.RevisionId, source.RevisionId);
        Assert.True(await database.Writer.CommitEmbeddingRefreshAsync(new EmbeddingRefreshCommitRequest(job.JobId,
            project, job.DocumentId, source.RevisionId, job.ExpectedObservationEpoch,
            source.Passages.Select(p => new PassageEmbedding(p.PassageId, StorageTestDatabase.TestVector())).ToArray(), corrected), token));
        var current = await database.Store.LoadVectorSnapshotMetadataAsync(project, corrected, token);
        Assert.True(current.IsComplete);
        Assert.Single((await database.Store.LoadVectorSnapshotAsync(project, corrected, token)).Entries);
        Assert.Empty((await database.Store.LoadVectorSnapshotAsync(project, legacy, token)).Entries);
        var evidence = Assert.Single(await database.Store.ReadPassagesAsync(project, [committed.PassageId], 0, 0, token));
        Assert.Equal("Canonical evidence survives the tokenization upgrade.", evidence.Text);
    }
}
