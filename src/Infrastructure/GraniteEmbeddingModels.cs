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
    bool RequiresGemmaTerms)
{
    public override string ToString() => DisplayName;
}

public static class GraniteEmbeddingModels
{
    public const EmbeddingModelChoice DefaultChoice = EmbeddingModelChoice.Granite311M;

    public static IReadOnlyList<GraniteEmbeddingModelDefinition> All { get; } =
    [
        new(
            EmbeddingModelChoice.Granite311M,
            "Granite Multilingual 311M",
            "Best search quality",
            "ibm-granite/granite-embedding-311m-multilingual-r2",
            "44399559930365213510b1ee2eb15ded83374f0e",
            "0087c868b33bad550a78a08d19798cfd7f713cde4f020803b8f51f405503e15f",
            "f1fdd44e7e1ac51f12ab7957c7bd092e064d596c288513bf9d326842f669edee",
            "75f9f258bf5013f5fe8a4dad61dd0fd16ac0cbaa7a106e3d3f41c2d04a42d541",
            2,
            768,
            384,
            "cls",
            "l2-after-matryoshka",
            true),
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
            false)
    ];

    public static GraniteEmbeddingModelDefinition Get(EmbeddingModelChoice choice) =>
        All.FirstOrDefault(model => model.Choice == choice)
        ?? throw new ArgumentOutOfRangeException(nameof(choice));
}