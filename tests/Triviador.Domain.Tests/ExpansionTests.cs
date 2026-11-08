using Triviador.Domain.Matches;
using Triviador.Domain.Questions;
using Triviador.Domain.Tests.TestSupport;
using static Triviador.Domain.Tests.TestSupport.TestMatch;

namespace Triviador.Domain.Tests;

public class ExpansionTests
{
    private static TestMatch InExpansion()
    {
        var t = Create();
        t.Match.Start();
        t.Match.PlaceCastle(P1, 1);
        t.Match.PlaceCastle(P2, 5);
        t.Match.PlaceCastle(P3, 13);
        t.Match.DequeueEvents();
        return t;
    }

    [Fact]
    public void Closest_guess_gets_two_picks_and_runner_up_gets_one()
    {
        var m = InExpansion().Match;

        m.SubmitAnswer(P1, new NumericAnswer(50));
        m.SubmitAnswer(P2, new NumericAnswer(101));
        m.SubmitAnswer(P3, new NumericAnswer(120));

        var picks = m.DequeueEvents().OfType<PicksAwarded>().Single();
        Assert.Equal(new[] { P2, P2, P3 }, picks.PickOrder);
        Assert.Equal(MatchPhase.ExpansionPicks, m.Phase);
        Assert.Equal(P2, m.CurrentTurn);
    }

    [Fact]
    public void Equal_guesses_are_split_by_speed()
    {
        var t = InExpansion();
        var m = t.Match;

        t.Clock.Advance(TimeSpan.FromSeconds(1));
        m.SubmitAnswer(P3, new NumericAnswer(90));
        t.Clock.Advance(TimeSpan.FromSeconds(1));
        m.SubmitAnswer(P1, new NumericAnswer(90));
        m.SubmitAnswer(P2, new NumericAnswer(0));

        var picks = m.DequeueEvents().OfType<PicksAwarded>().Single();
        Assert.Equal(new[] { P3, P3, P1 }, picks.PickOrder);
    }

    [Fact]
    public void Claims_must_border_your_own_land_when_possible()
    {
        var m = InExpansion().Match;
        m.SubmitAnswer(P1, new NumericAnswer(100));
        m.SubmitAnswer(P2, new NumericAnswer(80));
        m.SubmitAnswer(P3, new NumericAnswer(0));

        Assert.Equal(new[] { 2, 6 }, m.GetValidClaims(P1));
        Assert.Throws<GameRuleException>(() => m.ClaimTerritory(P1, 9));

        m.ClaimTerritory(P1, 2);
        Assert.Equal(P1, m.OwnerOf(2));
        Assert.Equal(new[] { 3, 6, 7 }, m.GetValidClaims(P1));
    }

    [Fact]
    public void Question_closes_at_deadline_and_only_answerers_get_picks()
    {
        var t = InExpansion();
        var m = t.Match;

        m.SubmitAnswer(P1, new NumericAnswer(5));
        Assert.False(m.Tick());

        t.Clock.Advance(TimeSpan.FromSeconds(21));
        Assert.True(m.Tick());

        var events = m.DequeueEvents();
        Assert.Equal(new[] { P1, P1 }, events.OfType<PicksAwarded>().Single().PickOrder);

        var results = events.OfType<QuestionResolved>().Single().Results;
        Assert.Null(results.Single(r => r.Player == P2).Given);
    }

    [Fact]
    public void Late_answer_is_rejected_and_closes_the_question()
    {
        var t = InExpansion();
        t.Clock.Advance(TimeSpan.FromSeconds(30));

        var outcome = t.Match.SubmitAnswer(P1, new NumericAnswer(100));

        Assert.Equal(AnswerOutcome.TooLate, outcome);
        // Nobody answered in time, so the question is re-asked.
        Assert.Equal(MatchPhase.ExpansionQuestion, t.Match.Phase);
        Assert.Single(t.Match.DequeueEvents().OfType<QuestionAsked>());
    }

    [Fact]
    public void Double_answers_and_wrong_answer_types_are_rejected()
    {
        var m = InExpansion().Match;
        Assert.Throws<GameRuleException>(() => m.SubmitAnswer(P1, new ChoiceAnswer(0)));
        Assert.Throws<GameRuleException>(() => m.SubmitAnswer(P1, new NumericAnswer(double.NaN)));

        m.SubmitAnswer(P1, new NumericAnswer(1));
        Assert.Throws<GameRuleException>(() => m.SubmitAnswer(P1, new NumericAnswer(2)));
    }

    [Fact]
    public void Map_full_moves_to_battle()
    {
        var t = RingInBattle();
        Assert.Equal(MatchPhase.BattleAttack, t.Match.Phase);
        Assert.Equal(1, t.Match.BattleRound);
        Assert.Equal(P1, t.Match.CurrentTurn);
        Assert.Equal(6, t.Match.Owners.Count);
    }

    [Fact]
    public void Picks_are_trimmed_when_fewer_territories_remain()
    {
        // Ring has 3 free after castles; winner takes 2, runner-up 1 — then a second
        // question must never be asked.
        var t = RingInBattle();
        Assert.Null(t.Match.CurrentQuestion);
    }
}
