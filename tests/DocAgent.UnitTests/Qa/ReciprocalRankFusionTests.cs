using DocAgent.Infrastructure.Retrieval;

namespace DocAgent.UnitTests.Qa;

public sealed class ReciprocalRankFusionTests
{
    [Fact]
    public void Scores_are_the_sum_of_one_over_k_plus_rank()
    {
        var fused = ReciprocalRankFusion.Fuse<string>([["a", "b", "c"], ["b", "d"]], k: 60);

        Assert.Equal(["b", "a", "d", "c"], fused.Select(f => f.Item));
        Assert.Equal(1.0 / 62 + 1.0 / 61, fused[0].Score, 12);   // b: rank 2 in list 1, rank 1 in list 2
        Assert.Equal(1.0 / 61, fused[1].Score, 12);              // a: rank 1 in list 1 only
        Assert.Equal(1.0 / 62, fused[2].Score, 12);              // d: rank 2 in list 2 only
        Assert.Equal(1.0 / 63, fused[3].Score, 12);              // c: rank 3 in list 1 only
    }

    [Fact]
    public void Ties_keep_first_seen_order()
    {
        var fused = ReciprocalRankFusion.Fuse<string>([["x"], ["y"]], k: 60);

        Assert.Equal(["x", "y"], fused.Select(f => f.Item));
    }

    [Fact]
    public void Empty_lists_give_an_empty_result_and_a_single_list_keeps_its_order()
    {
        Assert.Empty(ReciprocalRankFusion.Fuse<int>([[], []]));
        Assert.Equal([3, 1, 2], ReciprocalRankFusion.Fuse<int>([[3, 1, 2]]).Select(f => f.Item));
    }

    [Fact]
    public void Smaller_k_gives_the_top_rank_more_weight()
    {
        var small = ReciprocalRankFusion.Fuse<string>([["a"]], k: 1)[0].Score;
        var large = ReciprocalRankFusion.Fuse<string>([["a"]], k: 100)[0].Score;

        Assert.True(small > large);
    }
}
