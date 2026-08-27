using System.Numerics;

using ContextMole.Core;

namespace ContextMole.Search;

public sealed class FlatVectorIndex(VectorSnapshot snapshot) : IVectorIndex
{
    private readonly IReadOnlyList<VectorEntry> _entries = snapshot.Entries;
    public long SearchGeneration { get; } = snapshot.SearchGeneration;

    public IReadOnlyList<VectorMatch> Search(ReadOnlySpan<float> query, int count, SearchFilters? filters = null)
    {
        if (query.Length != 384)
            throw new ArgumentException("The semantic query vector must have 384 dimensions.", nameof(query));

        var best = new PriorityQueue<(Guid PassageId, double Score), double>();
        var preparedFilters = PreparedFilters.Create(filters);
        foreach (var entry in _entries)
        {
            if (!Matches(entry, preparedFilters)) continue;
            var score = Dot(query, entry.Vector);
            if (best.Count < count)
                best.Enqueue((entry.PassageId, score), score);
            else if (best.TryPeek(out _, out var minimum) && score > minimum)
            {
                best.Dequeue();
                best.Enqueue((entry.PassageId, score), score);
            }
        }

        return best.UnorderedItems.Select(item => item.Element)
            .OrderByDescending(item => item.Score).ThenBy(item => item.PassageId)
            .Select((item, index) => new VectorMatch(item.PassageId, item.Score, index + 1)).ToArray();
    }

    public static async Task<IReadOnlyList<VectorMatch>> SearchStreamingAsync(IAsyncEnumerable<VectorEntry> entries,
        ReadOnlyMemory<float> query, int count, CancellationToken cancellationToken)
    {
        var best = new PriorityQueue<(Guid PassageId, double Score), double>();
        await foreach (var entry in entries.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var score = Dot(query.Span, entry.Vector);
            if (best.Count < count) best.Enqueue((entry.PassageId, score), score);
            else if (best.TryPeek(out _, out var minimum) && score > minimum)
            {
                best.Dequeue();
                best.Enqueue((entry.PassageId, score), score);
            }
        }
        return best.UnorderedItems.Select(item => item.Element)
            .OrderByDescending(item => item.Score).ThenBy(item => item.PassageId)
            .Select((item, index) => new VectorMatch(item.PassageId, item.Score, index + 1)).ToArray();
    }

    private static float Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        var width = Vector<float>.Count;
        var sum = Vector<float>.Zero;
        var index = 0;
        for (; index <= left.Length - width; index += width)
            sum += new Vector<float>(left.Slice(index, width)) * new Vector<float>(right.Slice(index, width));
        var scalar = Vector.Dot(sum, Vector<float>.One);
        for (; index < left.Length; index++) scalar += left[index] * right[index];
        return scalar;
    }

    private static bool Matches(VectorEntry entry, PreparedFilters? filters)
    {
        if (filters is null) return true;
        if (filters.DocumentIds is not null && !filters.DocumentIds.Contains(entry.DocumentId)) return false;
        if (filters.PathPrefixes is not null && !filters.PathPrefixes.Any(prefix => PathMatches(entry.SourcePath, prefix))) return false;
        if (filters.Extensions is not null && !filters.Extensions.Contains(entry.Extension)) return false;
        if (filters.ModifiedFromUtc is { } from && entry.ModifiedUtc < from) return false;
        if (filters.ModifiedToUtc is { } to && entry.ModifiedUtc > to) return false;
        if (filters.AttachmentScope == AttachmentScope.RootOnly && entry.IsAttachment) return false;
        if (filters.AttachmentScope == AttachmentScope.AttachmentsOnly && !entry.IsAttachment) return false;
        return true;
    }

    private static string NormalizeExtension(string extension) => extension.StartsWith('.') ? extension.ToLowerInvariant() : $".{extension.ToLowerInvariant()}";
    private static bool PathMatches(string candidate, string prefix)
    {
        return string.Equals(candidate, prefix, PathComparison()) ||
               candidate.StartsWith(prefix + Path.DirectorySeparatorChar, PathComparison()) ||
               candidate.StartsWith(prefix + Path.AltDirectorySeparatorChar, PathComparison());
    }
    private static StringComparison PathComparison() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record PreparedFilters(
        HashSet<Guid>? DocumentIds,
        string[]? PathPrefixes,
        HashSet<string>? Extensions,
        DateTimeOffset? ModifiedFromUtc,
        DateTimeOffset? ModifiedToUtc,
        AttachmentScope AttachmentScope)
    {
        public static PreparedFilters? Create(SearchFilters? filters)
        {
            if (filters is null) return null;
            var paths = filters.PathPrefixes is { Count: > 0 }
                ? filters.PathPrefixes.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
                    .Distinct(PathComparer()).ToArray()
                : null;
            return new PreparedFilters(
                filters.DocumentIds is { Count: > 0 } ? filters.DocumentIds.ToHashSet() : null,
                paths,
                filters.Extensions is { Count: > 0 }
                    ? filters.Extensions.Select(NormalizeExtension).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : null,
                filters.ModifiedFromUtc,
                filters.ModifiedToUtc,
                filters.AttachmentScope);
        }

        private static StringComparer PathComparer() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }
}

public sealed class FlatVectorIndexFactory : IVectorIndexFactory
{
    public IVectorIndex Create(VectorSnapshot snapshot) => new FlatVectorIndex(snapshot);
}