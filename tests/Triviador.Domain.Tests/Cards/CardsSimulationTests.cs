using Triviador.Domain.Cards;
using Triviador.Domain.Questions;
using Triviador.Domain.Tests.TestSupport;

namespace Triviador.Domain.Tests.Cards;

public class CardsSimulationTests
{
    /// <summary>Bots play whole games with a ticking clock; every game must end without rule errors.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Bot_games_always_finish(int players)
    {
        for (var seed = 0; seed < 40; seed++)
        {
            var (game, _) = Simulate(players, seed);
            Assert.Equal(CardsPhase.Finished, game.Phase);
            Assert.Equal(players, game.Standings!.Count);
            Assert.Equal(1, game.Standings[0].Place);
        }
    }

    /// <summary>Plays a game where bots answer quickly and steal after the head start. Returns the game and how many turns it took.</summary>
    public static (CardsGame Game, int Turns) Simulate(int players, int seed)
    {
        var rng = new Random(seed);
        var clock = new ManualTimeProvider();
        var infos = Enumerable.Range(1, players).Select(i => new PlayerInfo(new PlayerId($"b{i}"), $"Bot {i}", IsBot: true)).ToList();
        var game = new CardsGame($"sim-{seed}", infos, new CardQuestionPool(SampleQuestions.All, rng), clock, rng);
        var bots = infos.Select(p => new CardsBot(p.Id, rng, accuracy: 0.5 + rng.NextDouble() * 0.3)).ToList();
        game.Start();

        var guard = 0;
        while (game.Phase != CardsPhase.Finished && guard++ < 100_000)
        {
            var acted = false;
            // Owners and turn players act first; stealers only after the head start.
            foreach (var bot in bots.OrderBy(_ => rng.Next()))
            {
                var intent = bot.Intent(game);
                if (intent is BotIntent.Play or BotIntent.Answer && rng.NextDouble() < 0.7
                    || intent == BotIntent.Steal && rng.NextDouble() < 0.4)
                {
                    bot.TryAct(game);
                    acted = true;
                    break;
                }
            }

            if (!acted)
            {
                clock.Advance(TimeSpan.FromMilliseconds(500));
                game.Tick();
            }
            game.DequeueEvents();
        }

        return (game, game.Turn);
    }
}
