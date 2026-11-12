namespace DocAgent.Infrastructure.Retrieval;

public static class ReciprocalRankFusion
{
    /// <summary>
    /// Merges ranked lists (best first). Each item scores the sum of 1 / (k + rank) over the lists that contain it
    /// (rank starts at 1). Ties keep the order of first appearance.
    /// </summary>
    public static IReadOnlyList<(T Item, double Score)> Fuse<T>(IEnumerable<IReadOnlyList<T>> rankings, int k = 60)
        where T : notnull
    {
        var scores = new Dictionary<T, double>();
        var firstSeen = new Dictionary<T, int>();
        foreach (var ranking in rankings)
        {
            for (var i = 0; i < ranking.Count; i++)
            {
                var item = ranking[i];
                scores[item] = scores.GetValueOrDefault(item) + 1.0 / (k + i + 1);
                firstSeen.TryAdd(item, firstSeen.Count);
            }
        }

        return scores
            .OrderByDescending(p => p.Value)
            .ThenBy(p => firstSeen[p.Key])
            .Select(p => (p.Key, p.Value))
            .ToList();
    }
}
