using Triviador.Domain.Matches;
using Triviador.Domain.Questions;
using Triviador.Domain.Tests.TestSupport;
using static Triviador.Domain.Tests.TestSupport.TestMatch;

namespace Triviador.Domain.Tests;

public class BattleTests
{
    [Fact]
    public void Attack_targets_are_bordering_enemy_territories()
    {
        var m = RingInBattle().Match;
        Assert.Equal(new[] { 3, 5 }, m.GetValidAttackTargets(P1));
        Assert.Throws<GameRuleException>(() => m.Attack(P1, 4)); // P2's, but not bordering
        Assert.Throws<GameRuleException>(() => m.Attack(P1, 2)); // own territory
        Assert.Throws<GameRuleException>(() => m.Attack(P2, 2)); // not P2's turn
    }

    [Fact]
    public void Duel_goes_only_to_attacker_and_defender()
    {
        var m = RingInBattle().Match;
        m.Attack(P1, 3);

        var asked = m.DequeueEvents().OfType<QuestionAsked>().Single();
        Assert.Equal(new[] { P1, P2 }, asked.Answerers);
        Assert.Equal(QuestionKind.MultipleChoice, asked.Question.Kind);
        Assert.Throws<GameRuleException>(() => m.SubmitAnswer(P3, Right));
    }

    [Fact]
    public void Attacker_right_and_defender_wrong_conquers_a_plain_territory()
    {
        var t = RingInBattle();
        t.RepelledAttack(); // P1's turn passes; P2 attacks next

        // Territory 2 is P1's plain (non-castle) land bordering P2's castle at 3.
        Assert.Equal(P2, t.Match.CurrentTurn);
        t.WinningAttack(2);

        Assert.Equal(P2, t.Match.OwnerOf(2));
        Assert.Contains(t.Match.DequeueEvents(), e => e is TerritoryConquered { TerritoryId: 2 });
    }

    [Fact]
    public void Defender_right_and_attacker_wrong_repels()
    {
        var t = RingInBattle();
        t.RepelledAttack();

        Assert.Equal(P2, t.Match.OwnerOf(3));
        Assert.Equal(3, t.Match.CastleHitPoints(P2));
        Assert.Contains(t.Match.DequeueEvents(), e => e is AttackRepelled);
        Assert.Equal(P2, t.Match.CurrentTurn);
    }

    [Fact]
    public void Both_right_goes_to_a_numeric_tiebreak()
    {
        var t = RingInBattle();
        var m = t.Match;
        m.Attack(P1, 3);
        m.SubmitAnswer(P1, Right);
        m.SubmitAnswer(P2, Right);

        Assert.Equal(MatchPhase.BattleTiebreak, m.Phase);
        Assert.Equal(QuestionKind.Numeric, m.CurrentQuestion!.Kind);

        m.SubmitAnswer(P1, new NumericAnswer(99));
        m.SubmitAnswer(P2, new NumericAnswer(150));

        Assert.Equal(2, m.CastleHitPoints(P2));
        Assert.Equal(MatchPhase.BattleAttack, m.Phase);
    }

    [Fact]
    public void Both_wrong_also_goes_to_tiebreak_and_silence_favours_defender()
    {
        var t = RingInBattle();
        var m = t.Match;
        m.Attack(P1, 3);
        m.SubmitAnswer(P1, Wrong);
        m.SubmitAnswer(P2, Wrong);
        Assert.Equal(MatchPhase.BattleTiebreak, m.Phase);

        t.Clock.Advance(TimeSpan.FromMinutes(1));
        m.Tick();

        Assert.Contains(m.DequeueEvents(), e => e is AttackRepelled);
        Assert.Equal(3, m.CastleHitPoints(P2));
    }

    [Fact]
    public void Castle_falls_after_three_hits_and_its_owner_loses_everything()
    {
        var t = RingInBattle(new MatchOptions { BattleRounds = 4 });
        var m = t.Match;

        // Round 1: P1, P2, P3
        t.WinningAttack(5);            // P1 hits P3's castle (2 left)
        t.RepelledAttack();            // P2
        t.RepelledAttack();            // P3
        // Round 2: P2, P3, P1
        t.RepelledAttack();
        t.RepelledAttack();
        t.WinningAttack(5);            // 1 left
        // Round 3: P3, P1, P2
        t.RepelledAttack();
        Assert.Equal(P1, m.CurrentTurn);
        t.WinningAttack(5);            // castle destroyed

        Assert.True(m.IsEliminated(P3));
        Assert.Equal(P1, m.OwnerOf(5));
        var eliminated = m.DequeueEvents().OfType<PlayerEliminated>().Single();
        Assert.Equal(P3, eliminated.Player);
        Assert.Equal(P1, eliminated.By);

        // Eliminated players never get a turn again.
        Assert.Equal(P2, m.CurrentTurn);
    }

    [Fact]
    public void Match_ends_after_the_configured_rounds_with_placements()
    {
        var t = RingInBattle(new MatchOptions { BattleRounds = 1 });
        t.RepelledAttack();
        t.RepelledAttack();
        t.RepelledAttack();

        var m = t.Match;
        Assert.Equal(MatchPhase.Finished, m.Phase);
        Assert.Null(m.CurrentTurn);

        var standings = m.Standings!;
        Assert.Equal(new[] { P1, P2, P3 }, standings.Select(s => s.Player));
        Assert.Equal(new[] { 1, 2, 3 }, standings.Select(s => s.Place));
        Assert.Equal(new[] { 600, 400, 200 }, standings.Select(s => s.Score));
        Assert.Single(m.DequeueEvents().OfType<MatchFinished>());
    }

    [Fact]
    public void Last_player_standing_ends_the_match_early()
    {
        var t = RingInBattle(new MatchOptions { BattleRounds = 10, CastleHitPoints = 1 });
        var m = t.Match;

        // Round 1: P1 knocks out P3, P2 is repelled, P3's turn is skipped.
        t.WinningAttack(5);
        Assert.Equal(P2, m.CurrentTurn);
        t.RepelledAttack();

        // Round 2 starts at seat 2 (P2), then P3 (gone), then P1.
        Assert.Equal(2, m.BattleRound);
        Assert.Equal(P2, m.CurrentTurn);
        t.RepelledAttack();
        Assert.Equal(P1, m.CurrentTurn);
        t.WinningAttack(3); // P2's castle

        Assert.Equal(MatchPhase.Finished, m.Phase);
        Assert.Equal(new[] { P1, P2, P3 }, m.Standings!.Select(s => s.Player));
        Assert.All(m.Owners.Values, owner => Assert.Equal(P1, owner));
    }

    [Fact]
    public void Round_order_rotates()
    {
        var t = RingInBattle(new MatchOptions { BattleRounds = 3 });
        var order = new List<PlayerId>();
        while (t.Match.Phase != MatchPhase.Finished)
        {
            order.Add(t.Match.CurrentTurn!.Value);
            t.RepelledAttack();
        }

        Assert.Equal(new[] { P1, P2, P3, P2, P3, P1, P3, P1, P2 }, order);
    }
}
