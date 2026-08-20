using Gatherum.Core.Services;

namespace Gatherum.Tests;

public class RankFusionTests
{
    [Fact]
    public void A_result_both_lists_like_beats_one_either_list_likes_more()
    {
        var lexical = new[] { "only-lexical", "both" };
        var semantic = new[] { "only-semantic", "both" };

        Assert.Equal("both", RankFusion.Fuse([lexical, semantic], 3).First());
    }

    [Fact]
    public void One_list_alone_keeps_its_own_order() =>
        Assert.Equal(["a", "b", "c"], RankFusion.Fuse([new[] { "a", "b", "c" }], 5));

    [Fact]
    public void Nothing_in_means_nothing_out() =>
        Assert.Empty(RankFusion.Fuse<string>([[], []], 10));

    [Fact]
    public void The_limit_is_honoured() =>
        Assert.Equal(2, RankFusion.Fuse([new[] { "a", "b", "c", "d" }], 2).Count);

    [Fact]
    public void Ties_break_the_same_way_every_time()
    {
        // Two results nothing distinguishes: same rank, one list each.
        var once = RankFusion.Fuse([new[] { "first" }, new[] { "second" }], 2);
        var again = RankFusion.Fuse([new[] { "first" }, new[] { "second" }], 2);

        Assert.Equal(["first", "second"], once);
        Assert.Equal(once, again);
    }
}
