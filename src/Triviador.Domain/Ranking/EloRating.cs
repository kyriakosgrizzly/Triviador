namespace Triviador.Domain.Ranking;

public sealed record RatedResult(PlayerId Player, double Rating, int Place);

/// <summary>
/// Multiplayer Elo: a 3-player match is scored as every pairwise game
/// (1st beats 2nd and 3rd, 2nd beats 3rd). Each pair's change is divided by
/// (n - 1) so one match moves a rating about as much as one 1v1 game.
/// Swap for Glicko-2 later without touching the match engine.
/// </summary>
public static class EloRating
{
    public const double DefaultRating = 1200;
    public const double DefaultK = 32;

    public static IReadOnlyDictionary<PlayerId, double> Update(IReadOnlyList<RatedResult> results, double k = DefaultK)
    {
        if (results.Count < 2)
            throw new ArgumentException("Need at least two players to rate a match.", nameof(results));

        var deltas = results.ToDictionary(r => r.Player, _ => 0.0);
        var pairWeight = k / (results.Count - 1);

        for (var i = 0; i < results.Count; i++)
        for (var j = i + 1; j < results.Count; j++)
        {
            var a = results[i];
            var b = results[j];
            var expectedA = Expected(a.Rating, b.Rating);
            var actualA = a.Place < b.Place ? 1.0 : a.Place == b.Place ? 0.5 : 0.0;

            var change = pairWeight * (actualA - expectedA);
            deltas[a.Player] += change;
            deltas[b.Player] -= change;
        }

        return results.ToDictionary(r => r.Player, r => r.Rating + deltas[r.Player]);
    }

    public static double Expected(double rating, double opponent) =>
        1.0 / (1.0 + Math.Pow(10, (opponent - rating) / 400.0));
}
