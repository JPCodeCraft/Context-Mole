using ContextMole.Core;

namespace ContextMole.Indexing;

/// <summary>Removes semantic distractors without modifying canonical reading or lexical evidence.</summary>
public static partial class SemanticTextPreparation
{
    public static int? SignatureStart(string text)
    {
        var offset = 0;
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim() == "--" && !string.IsNullOrWhiteSpace(text[..offset])) return offset;
            offset += line.Length + 1;
        }
        return null;
    }

    public static string CleanBody(string text, bool boilerplate = false)
    {
        if (boilerplate) return string.Empty;
        var lines = text.Split('\n');
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inDisclaimer = false;
        foreach (var line in lines)
        {
            // An explicit RFC-style signature delimiter is stronger evidence than contact words.
            if (line.Trim() == "--" && result.Any(value => !string.IsNullOrWhiteSpace(value))) break;
            var normalized = TextNormalization.ForSearch(line).TrimStart('>').Trim();
            if (StartsDisclaimer(normalized)) inDisclaimer = true;
            if (inDisclaimer)
            {
                if (normalized.Length == 0) inDisclaimer = false;
                continue;
            }
            if (normalized.Length > 80 && !seen.Add(normalized)) continue;
            result.Add(line);
        }
        return TextNormalization.ForSearch(string.Join('\n', result), dehyphenateLineBreaks: true);
    }

    private static bool StartsDisclaimer(string text) =>
        text.StartsWith("This email and any attachments are confidential", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("This message and any attachments are confidential", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("Esta mensagem e seus anexos são confidenciais", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("Este e-mail e seus anexos são confidenciais", StringComparison.OrdinalIgnoreCase);

    public static string Compose(string body, IEnumerable<(string Label, string? Value)> context,
        Func<string, int> countTokens)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var remaining = 64;
        foreach (var (label, value) in context)
        {
            var normalized = TextNormalization.ForSearch(value);
            if (normalized.Length == 0 || !seen.Add(TextNormalization.NameKey(normalized))) continue;
            var length = Math.Min(normalized.Length, 200);
            while (length > 0 && countTokens($"{label}: {normalized[..length]}") > remaining) length--;
            if (length == 0) continue;
            var line = $"{label}: {normalized[..length]}";
            remaining -= countTokens(line);
            lines.Add(line);
            if (remaining <= 0) break;
        }
        // Body is first, so any consumer with a smaller budget still sees the evidence first.
        return TextNormalization.ForSearch(string.Join('\n', new[] { body }.Concat(lines)));
    }
}
