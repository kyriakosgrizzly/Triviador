using Triviador.Domain.Conquest;
using Triviador.Domain.Questions;
using Triviador.Domain.Tests.TestSupport;

namespace Triviador.Domain.Tests.Conquest;

/// <summary>A Conquest game with a controllable clock and fixed questions (numeric answer 100, choice answer option 0).</summary>
public sealed class ConquestHarness
{
    public static readonly PlayerId P1 = new("p1");
    public static readonly PlayerId P2 = new("p2");
    public static readonly PlayerId P3 = new("p3");
    public static readonly PlayerId P4 = new("p4");
    public static readonly PlayerId[] All = [P1, P2, P3, P4];

    public static readonly ChoiceAnswer Right = new(0);
    public static readonly ChoiceAnswer Wrong = new(1);

    // Region ids from the generated world map.
    public static int R(string key) => WorldMap.Regions.Single(r => r.Key == key).Id;

    public ConquestGame Game { get; }
    public ManualTimeProvider Clock { get; } = new();

    public ConquestHarness(int players = 3, int seed = 1, ConquestOptions? options = null)
    {
        var infos = All.Take(players).Select(p => new PlayerInfo(p, p.Value.ToUpperInvariant())).ToList();
        Game = new ConquestGame("c1", infos, new FixedQuestionSource(), Clock, new Random(seed), options);
    }

    /// <summary>Everyone answers the open numeric question; earlier players in the list are closer.</summary>
    public void AnswerNumericInOrder(params PlayerId[] order)
    {
        for (var i = 0; i < order.Length; i++)
            Game.SubmitAnswer(order[i], new NumericAnswer(100 + i * 10));
    }

    /// <summary>Runs setup: colour question in seat order, colours in palette order, capitals as given.</summary>
    public ConquestHarness SetUp(params string[] capitals)
    {
        var players = Game.Players.Select(p => p.Id).ToArray();
        Game.Start();
        AnswerNumericInOrder(players);
        foreach (var p in players) Game.PickColor(p, Game.AvailableColors[0]);
        for (var i = 0; i < players.Length; i++) Game.PickCapital(players[i], R(capitals[i]));
        return this;
    }

    /// <summary>Answers the supply question with the given ranking, landing in Planning.</summary>
    public ConquestHarness Supply(params PlayerId[] ranking)
    {
        AnswerNumericInOrder(ranking.Length > 0 ? ranking : Game.Players.Select(p => p.Id).Where(p => !Game.IsEliminated(p)).ToArray());
        return this;
    }

    public void Plan(PlayerId p, params Order[] orders) => Game.SubmitPlan(p, orders);
}
