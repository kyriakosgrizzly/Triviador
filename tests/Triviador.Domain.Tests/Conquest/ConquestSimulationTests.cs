using Triviador.Domain.Conquest;
using Triviador.Domain.Questions;
using Triviador.Domain.Tests.TestSupport;

namespace Triviador.Domain.Tests.Conquest;

/// <summary>Bots play full Conquest games; invariants are checked after every action.</summary>
public class ConquestSimulationTests
{
    [Fact]
    public void Bot_games_with_2_to_4_players_all_finish_cleanly()
    {
        var rounds = new List<int>();
        for (var seed = 0; seed < 120; seed++)
            rounds.Add(PlayOne(seed, players: 2 + seed % 3));

        // Games should usually go the distance or end by domination, not stall early.
        Assert.True(rounds.Average() >= 8, $"average rounds {rounds.Average():F1}");
    }

    [Fact]
    public void Idle_players_never_stall_a_game()
    {
        // Nobody acts at all: timers must carry the game to the end.
        var clock = new ManualTimeProvider();
        var players = new[] { new PlayerInfo(new("a"), "A"), new PlayerInfo(new("b"), "B") };
        var game = new ConquestGame("idle", players, new InMemoryQuestionSource(SampleQuestions.All, new Random(1)), clock, new Random(1));
        game.Start();

        for (var i = 0; i < 2_000 && game.Phase != ConquestPhase.Finished; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            game.Tick();
        }

        Assert.Equal(ConquestPhase.Finished, game.Phase);
        Assert.Equal(game.Options.MaxRounds, game.Round);
    }

    private static int PlayOne(int seed, int players)
    {
        var rng = new Random(seed);
        var clock = new ManualTimeProvider();
        var bots = Enumerable.Range(0, players)
            .Select(i => new ConquestBot(new PlayerId($"bot{i}"), rng, accuracy: 0.4 + 0.15 * i))
            .ToList();
        var game = new ConquestGame(
            $"sim-{seed}",
            bots.Select(b => new PlayerInfo(b.Id, b.Id.Value, IsBot: true)).ToList(),
            new InMemoryQuestionSource(SampleQuestions.All, rng),
            clock,
            rng);

        game.Start();
        var steps = 0;
        while (game.Phase != ConquestPhase.Finished)
        {
            Assert.True(++steps < 20_000, $"seed {seed}: game did not finish (phase {game.Phase}, round {game.Round})");
            clock.Advance(TimeSpan.FromMilliseconds(rng.Next(200, 2_000)));

            var acted = false;
            foreach (var bot in bots.OrderBy(_ => rng.Next()))
                if (bot.HasPendingAction(game)) acted |= bot.TryAct(game);
            if (!acted) game.Tick();

            CheckInvariants(game, seed);
            game.DequeueEvents();
        }

        var standings = game.Standings!;
        Assert.Equal(players, standings.Count);
        Assert.Equal(Enumerable.Range(1, players).ToArray(), standings.Select(s => s.Place).ToArray());
        return game.Round;
    }

    private static void CheckInvariants(ConquestGame game, int seed)
    {
        foreach (var r in game.Regions)
        {
            Assert.True(r.Castle is >= 0 and <= 3, $"seed {seed}: castle level {r.Castle} in {r.Id}");
            if (r.Owner is { } owner)
                Assert.False(game.IsEliminated(owner), $"seed {seed}: eliminated player still owns {r.Id}");
        }

        foreach (var p in game.Players)
        {
            var w = game.WalletOf(p.Id);
            Assert.True(w.Gold >= 0 && w.Manpower >= 0 && w.Production >= 0, $"seed {seed}: {p.Id} went into debt: {w}");
        }

        if (game.Phase >= ConquestPhase.SupplyQuestion && game.Phase != ConquestPhase.Finished)
        {
            var colors = game.Players.Select(p => game.ColorOf(p.Id)).ToList();
            Assert.True(colors.All(c => c is not null) && colors.Distinct().Count() == colors.Count, $"seed {seed}: colours not unique");
        }
    }
}
