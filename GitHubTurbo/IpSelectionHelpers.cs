namespace GitHubTurbo;

public static class IpSelectionHelpers
{
    public static IReadOnlyList<ResolvedIpCandidate> Interleave(
        IReadOnlyList<IReadOnlyList<ResolvedIpCandidate>> sources,
        int limit)
    {
        var result = new List<ResolvedIpCandidate>(limit);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maxSourceLength = sources.Count == 0 ? 0 : sources.Max(x => x.Count);

        for (var index = 0; index < maxSourceLength && result.Count < limit; index++)
        {
            foreach (var source in sources)
            {
                if (index >= source.Count || !seen.Add(source[index].Ip))
                    continue;

                result.Add(source[index]);
                if (result.Count == limit)
                    break;
            }
        }

        return result;
    }

    public static long Median(IEnumerable<long> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
            return long.MaxValue;

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2;
    }
}
