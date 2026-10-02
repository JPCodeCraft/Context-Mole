using ContextMole.Core;

using Tokenizers.HuggingFace.Tokenizer;

namespace ContextMole.Infrastructure;

/// <summary>Uses each pinned tokenizer's complete special-token template for every embedding consumer.</summary>
public static class GraniteEmbeddingInputEncoding
{
    public const int QueryMaximumTokens = 256;
    public const int PassageMaximumTokens = 512;

    public static int CountTokens(Tokenizer tokenizer, string text) =>
        tokenizer.Encode(text, true).First().Ids.Count;

    public static long[] Encode(Tokenizer tokenizer, string text, GraniteEmbeddingModelDefinition model,
        int maximumTokens, bool rejectOverlong) =>
        Limit(tokenizer.Encode(text, true).First().Ids, model, maximumTokens, rejectOverlong);

    internal static long[] Limit(IReadOnlyList<uint> completeTokens, GraniteEmbeddingModelDefinition model,
        int maximumTokens, bool rejectOverlong)
    {
        GraniteEmbeddingModels.EnsureSupported(model);
        var specialTokens = model.EosTokenId is null ? 1 : 2;
        if (maximumTokens < specialTokens) throw new ArgumentOutOfRangeException(nameof(maximumTokens));
        // The sole pinned 97M template is BOS + text + EOS.
        if (completeTokens.Count < specialTokens || completeTokens[0] != model.BosTokenId ||
            (model.EosTokenId is { } expectedEos && completeTokens[^1] != expectedEos))
            throw new ContextMoleException("model_input_invalid", "The installed tokenizer's special tokens do not match the pinned embedding model.");
        if (rejectOverlong && completeTokens.Count > maximumTokens)
            throw new ContextMoleException("embedding_input_too_long",
                "A prepared passage exceeds the complete embedding token budget; reindex with the current preparation pipeline.");

        var count = Math.Min(completeTokens.Count, maximumTokens);
        var ids = new long[count];
        for (var index = 0; index < count; index++) ids[index] = completeTokens[index];
        // Right-truncate the body, never the model's required suffix.
        if (model.EosTokenId is { } eos) ids[^1] = eos;
        return ids;
    }
}
