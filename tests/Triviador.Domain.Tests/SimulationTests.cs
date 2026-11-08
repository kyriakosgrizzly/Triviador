using Triviador.Domain.Bots;
using Triviador.Domain.Map;
using Triviador.Domain.Matches;
using Triviador.Domain.Questions;
using Triviador.Domain.Tests.TestSupport;

namespace Triviador.Domain.Tests;

/// <summary>Plays many full bot-vs-bot matches and checks invariants hold at every step.</summary>
public class SimulationTests
{
    [Fact]
    public void Two_hundred_bot_matches_all_finish_cleanly()
    {
        for (var seed = 0; seed < 200; seed++)
            PlayOne(seed);
    }

    private static void PlayOne(int seed)
    {
        var rng = new Random(seed);
        var clock = new ManualTimeProvider();
        var bots = new[]
        {
            new BotPlayer(new PlayerId("a"), rng, accuracy: 0.8),
            new BotPlayer(new PlayerId("b"), rng, accuracy: 0.6),
            new BotPlayer(new PlayerId("c"), rng, accuracy: 0.4),
        };
        var players = bots.Select(b => new PlayerInfo(b.Id, b.Id.Value, IsBot: true)).ToList();
        var map = Maps.Valley();
        var match = new Match($"sim-{seed}", players, map, new InMemoryQuestionSource(SampleQuestions.All, rng), clock);

        match.Start();
        var steps = 0;
        while (match.Phase != MatchPhase.Finished)
        {
            Assert.True(++steps < 5_000, $"Seed {seed}: match did not finish.");

            clock.Advance(TimeSpan.FromMilliseconds(rng.Next(200, 3_000)));
            var acted = false;
            foreach (var bot in bots)
                acted |= bot.TryAct(match);

            Assert.True(acted, $"Seed {seed}: nobody could act in phase {match.Phase}.");
            CheckInvariants(match, seed);

            // Clients must never see answers.
            foreach (var e in match.DequeueEvents().OfType<QuestionAsked>())
                Assert.IsType<PublicQuestion>(e.Question);
        }

        Assert.Equal(map.Count, match.Owners.Count);
        var standings = match.Standings!;
        Assert.Equal(new[] { 1, 2, 3 }, standings.Select(s => s.Place));
        Assert.Equal(3, standings.Select(s => s.Player).Distinct().Count());
    }

    private static void CheckInvariants(Match match, int seed)
    {
        foreach (var p in match.Players.Where(p => !match.IsEliminated(p.Id)))
        {
            var hp = match.CastleHitPoints(p.Id);
            if (hp is not null)
                Assert.True(hp > 0, $"Seed {seed}: alive player {p.Id} has a destroyed castle.");
        }

        foreach (var owner in match.Owners.Values)
            Assert.False(match.IsEliminated(owner), $"Seed {seed}: eliminated player still owns land.");
    }
}
