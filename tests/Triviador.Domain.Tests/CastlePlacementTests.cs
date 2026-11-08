using Triviador.Domain.Matches;
using Triviador.Domain.Questions;
using Triviador.Domain.Tests.TestSupport;
using static Triviador.Domain.Tests.TestSupport.TestMatch;

namespace Triviador.Domain.Tests;

public class CastlePlacementTests
{
    [Fact]
    public void Start_begins_castle_placement_with_seat_one()
    {
        var m = Create().Match;
        m.Start();

        Assert.Equal(MatchPhase.CastlePlacement, m.Phase);
        Assert.Equal(P1, m.CurrentTurn);
    }

    [Fact]
    public void Placing_out_of_turn_is_rejected()
    {
        var m = Create().Match;
        m.Start();

        Assert.Throws<GameRuleException>(() => m.PlaceCastle(P2, 5));
    }

    [Fact]
    public void Castle_cannot_border_another_castle()
    {
        var m = Create().Match;
        m.Start();
        m.PlaceCastle(P1, 1);

        Assert.Throws<GameRuleException>(() => m.PlaceCastle(P2, 2));
        Assert.Throws<GameRuleException>(() => m.PlaceCastle(P2, 1));
        Assert.DoesNotContain(2, m.GetValidCastleSites());
        Assert.DoesNotContain(6, m.GetValidCastleSites());
    }

    [Fact]
    public void After_three_castles_an_expansion_question_goes_to_everyone()
    {
        var m = Create().Match;
        m.Start();
        m.PlaceCastle(P1, 1);
        m.PlaceCastle(P2, 5);
        m.PlaceCastle(P3, 13);

        Assert.Equal(MatchPhase.ExpansionQuestion, m.Phase);
        Assert.Null(m.CurrentTurn);

        var asked = m.DequeueEvents().OfType<QuestionAsked>().Single();
        Assert.Equal(QuestionKind.Numeric, asked.Question.Kind);
        Assert.Equal(3, asked.Answerers.Count);
    }

    [Fact]
    public void Castle_owner_and_hit_points_are_tracked()
    {
        var m = Create().Match;
        m.Start();
        m.PlaceCastle(P1, 8);

        Assert.Equal(P1, m.OwnerOf(8));
        Assert.Equal(3, m.CastleHitPoints(P1));
        Assert.Equal(200, m.ScoreOf(P1));
    }
}
