using ContextMole.Core;

namespace ContextMole.Documents;

/// <summary>Repeatable transcription metrics for the extraction quality corpus.</summary>
public static class ExtractionQuality
{
    public static double CharacterErrorRate(string expected, string actual)
    {
        var reference = TextNormalization.ForSearch(expected).EnumerateRunes().Select(rune => rune.Value).ToArray();
        var observed = TextNormalization.ForSearch(actual).EnumerateRunes().Select(rune => rune.Value).ToArray();
        return EditDistance(reference, observed) / (double)Math.Max(1, reference.Length);
    }

    public static double WordErrorRate(string expected, string actual)
    {
        var reference = TextNormalization.ForSearch(expected).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var observed = TextNormalization.ForSearch(actual).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return EditDistance(reference, observed) / (double)Math.Max(1, reference.Length);
    }

    private static int EditDistance<T>(T[] reference, T[] observed)
    {
        var previous = Enumerable.Range(0, observed.Length + 1).ToArray();
        var current = new int[observed.Length + 1];
        for (var row = 1; row <= reference.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= observed.Length; column++)
                current[column] = Math.Min(Math.Min(previous[column] + 1, current[column - 1] + 1),
                    previous[column - 1] + (EqualityComparer<T>.Default.Equals(reference[row - 1], observed[column - 1]) ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[observed.Length];
    }
}
