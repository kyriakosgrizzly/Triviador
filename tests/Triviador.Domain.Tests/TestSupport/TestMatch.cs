using Triviador.Domain.Map;
using Triviador.Domain.Matches;
using Triviador.Domain.Questions;

namespace Triviador.Domain.Tests.TestSupport;

public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Always asks the same two questions: choice answer is option 0, numeric answer is 100.</summary>
public sealed class FixedQuestionSource : IQuestionSource
{
    public static readonly MultipleChoiceQuestion Choice =
        new("mc", "Test", "Pick A", ["A", "B", "C", "D"], CorrectIndex: 0);

    public static readonly NumericQuestion Number = new("num", "Test", "Say 100", 100);

    public MultipleChoiceQuestion NextMultipleChoice() => Choice;
    public NumericQuestion NextNumeric() => Number;
}

public sealed class TestMatch
{
    public static readonly PlayerId P1 = new("p1");
    public static readonly PlayerId P2 = new("p2");
    public static readonly PlayerId P3 = new("p3");

    public static readonly ChoiceAnswer Right = new(0);
    public static readonly ChoiceAnswer Wrong = new(1);

    private TestMatch(Match match, ManualTimeProvider clock)
    {
        Match = match;
        Clock = clock;
    }

    public Match Match { get; }
    public ManualTimeProvider Clock { get; }

    public static TestMatch Create(GameMap? map = null, MatchOptions? options = null)
    {
        var clock = new ManualTimeProvider();
        var players = new[] { new PlayerInfo(P1, "One"), new PlayerInfo(P2, "Two"), new PlayerInfo(P3, "Three") };
        var match = new Match("m1", players, map ?? Maps.Valley(), new FixedQuestionSource(), clock, options);
        return new TestMatch(match, clock);
    }

    /// <summary>
    /// Six territories in a ring: 1-2-3-4-5-6-1. Castles on 1, 3 and 5 leave exactly
    /// three free territories, so a single expansion question fills the map.
    /// </summary>
    public static GameMap Ring() => GameMap.Create(
        "Ring",
        Enumerable.Range(1, 6).Select(i => new Territory(i, $"T{i}", 200)),
        [(1, 2), (2, 3), (3, 4), (4, 5), (5, 6), (6, 1)]);

    /// <summary>
    /// Ring map driven to the battle phase.
    /// P1 owns {1 (castle), 2, 6}, P2 owns {3 (castle), 4}, P3 owns {5 (castle)}.
    /// </summary>
    public static TestMatch RingInBattle(MatchOptions? options = null)
    {
        var t = Create(Ring(), options);
        var m = t.Match;
        m.Start();
        m.PlaceCastle(P1, 1);
        m.PlaceCastle(P2, 3);
        m.PlaceCastle(P3, 5);

        m.SubmitAnswer(P1, new NumericAnswer(100));
        m.SubmitAnswer(P2, new NumericAnswer(90));
        m.SubmitAnswer(P3, new NumericAnswer(10));

        m.ClaimTerritory(P1, 2);
        m.ClaimTerritory(P1, 6);
        m.ClaimTerritory(P2, 4);
        m.DequeueEvents();
        return t;
    }

    /// <summary>The player whose turn it is attacks and loses (repelled).</summary>
    public void RepelledAttack()
    {
        var attacker = Match.CurrentTurn!.Value;
        var target = Match.GetValidAttackTargets(attacker)[0];
        var defender = Match.OwnerOf(target)!.Value;
        Match.Attack(attacker, target);
        Match.SubmitAnswer(attacker, Wrong);
        Match.SubmitAnswer(defender, Right);
    }

    /// <summary>The player whose turn it is attacks the given territory and wins outright.</summary>
    public void WinningAttack(int territoryId)
    {
        var attacker = Match.CurrentTurn!.Value;
        var defender = Match.OwnerOf(territoryId)!.Value;
        Match.Attack(attacker, territoryId);
        Match.SubmitAnswer(attacker, Right);
        Match.SubmitAnswer(defender, Wrong);
    }
}
