using Triviador.Domain.Ranking;

namespace Triviador.Domain.Tests;

public class EloRatingTests
{
    private static readonly PlayerId A = new("a"), B = new("b"), C = new("c");

    [Fact]
    public void Equal_ratings_winner_gains_last_loses_middle_stays()
    {
        var r = EloRating.Update(new[]
        {
            new RatedResult(A, 1200, 1),
            new RatedResult(B, 1200, 2),
            new RatedResult(C, 1200, 3),
        });

        Assert.Equal(1216, r[A], precision: 6);
        Assert.Equal(1200, r[B], precision: 6);
        Assert.Equal(1184, r[C], precision: 6);
    }

    [Fact]
    public void Rating_points_are_conserved()
    {
        var r = EloRating.Update(new[]
        {
            new RatedResult(A, 1500, 3),
            new RatedResult(B, 1100, 1),
            new RatedResult(C, 1320, 2),
        });

        Assert.Equal(1500 + 1100 + 1320, r.Values.Sum(), precision: 6);
    }

    [Fact]
    public void Upset_win_is_worth_more_than_expected_win()
    {
        var underdog = EloRating.Update(new[] { new RatedResult(A, 1000, 1), new RatedResult(B, 1400, 2) });
        var favourite = EloRating.Update(new[] { new RatedResult(A, 1400, 1), new RatedResult(B, 1000, 2) });

        Assert.True(underdog[A] - 1000 > favourite[A] - 1400);
    }
}
