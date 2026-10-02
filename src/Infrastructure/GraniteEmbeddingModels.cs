using ContextMole.Core;

namespace ContextMole.Infrastructure;

public sealed record GraniteEmbeddingModelDefinition(
    EmbeddingModelChoice Choice,
    string DisplayName,
    string Description,
    string ModelId,
    string Revision,
    string TokenizerSha,
    string QuantizedSha,
    string Fp32Sha,
    long BosTokenId,
    int SourceDimensions,
    int Dimensions,
    string Pooling,
    string Normalization,
    bool RequiresGemmaTerms,
    long? EosTokenId = null,
    string? TokenizationVersion = null)
{
    public override string ToString() => DisplayName;

    public EmbeddingPolicy CreatePolicy(bool quantized)
    {
        GraniteEmbeddingModels.EnsureSupported(this);
        return new(ModelId, Revision,
            quantized ? QuantizedSha : Fp32Sha, TokenizerSha, quantized ? "quint8-avx2" : "fp32",
            SourceDimensions, Dimensions, Pooling, Normalization, TokenizationVersion: TokenizationVersion);
    }
}

public static class GraniteEmbeddingModels
{
    public const EmbeddingModelChoice DefaultChoice = EmbeddingModelChoice.Granite97M;

    public static IReadOnlyList<GraniteEmbeddingModelDefinition> All { get; } =
    [
        new(
            EmbeddingModelChoice.Granite97M,
            "Granite Multilingual 97M",
            "Faster with lower memory use",
            "ibm-granite/granite-embedding-97m-multilingual-r2",
            "835ad14087e140460703cf0fae09f97d469d65c2",
            "4f2842d568e2724370aec203652a42ac783c7937f8347a1a2cc7506d71f1582f",
            "a6022dd8220ea6f6595562a1328ee216f4a94faa55362f2f4747c80f1e78772e",
            "68e592b160673d30250824c1116bc6ab33f70efb22b97c9e1d7ce1e69c1c9d70",
            179934,
            384,
            384,
            "cls",
            "l2",
            false,
            179938,
            "bos-eos-v1")
    ];

    public static GraniteEmbeddingModelDefinition Get(EmbeddingModelChoice choice) =>
        All.FirstOrDefault(model => model.Choice == choice)
        ?? throw new ArgumentOutOfRangeException(nameof(choice), choice,
            "Only Granite Multilingual 97M is supported. Legacy choices are retained only for settings decoding.");

    public static bool IsSupported(EmbeddingModelChoice choice) => choice == DefaultChoice;

    public static void EnsureSupported(GraniteEmbeddingModelDefinition model)
    {
        if (!IsSupported(model.Choice) || model != Get(DefaultChoice))
            throw new ContextMoleException("model_not_supported",
                "Only the pinned Granite Multilingual 97M model can be installed or used for embeddings.");
    }

    internal static void EnsureCurrentPolicy(EmbeddingPolicy policy)
    {
        var model = Get(DefaultChoice);
        if (policy.Key != model.CreatePolicy(false).Key && policy.Key != model.CreatePolicy(true).Key)
            throw new ContextMoleException("model_policy_mismatch",
                "The broker returned an unsupported embedding policy. Upgrade the app and broker together; only corrected Granite 97M embeddings are accepted.");
    }
}
