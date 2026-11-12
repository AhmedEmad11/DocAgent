namespace DocAgent.Infrastructure.Agents;

/// <summary>Groups items in order into batches whose summed size stays within a budget (map-reduce sizing).</summary>
public static class TokenBatcher
{
    /// <summary>
    /// Greedy, order-preserving. An item larger than the budget gets a batch of its own; no batch is ever empty.
    /// </summary>
    public static List<List<T>> Group<T>(IEnumerable<T> items, Func<T, int> size, int budget)
    {
        var batches = new List<List<T>>();
        var current = new List<T>();
        var used = 0;
        foreach (var item in items)
        {
            var itemSize = Math.Max(1, size(item));
            if (current.Count > 0 && used + itemSize > budget)
            {
                batches.Add(current);
                current = [];
                used = 0;
            }

            current.Add(item);
            used += itemSize;
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }

    /// <summary>Rough token estimate for free text (about 3 characters per token; errs on the high side for Latin text).</summary>
    public static int EstimateTokens(string text) => Math.Max(1, text.Length / 3);
}
