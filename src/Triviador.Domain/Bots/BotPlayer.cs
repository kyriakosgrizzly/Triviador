using Triviador.Domain.Matches;
using Triviador.Domain.Questions;

namespace Triviador.Domain.Bots;

public enum PendingAction
{
    None,
    Answer,
    Turn,
}

/// <summary>
/// A simple bot: picks random legal moves and answers correctly with a set probability.
/// Used to fill seats while testing, for empty matchmaking queues, and to take over
/// the turn of a human who has gone AFK.
/// </summary>
public sealed class BotPlayer(PlayerId id, Random rng, double accuracy = 0.6)
{
    public PlayerId Id { get; } = id;

    /// <summary>What this player is currently expected to do, without doing it.</summary>
    public static PendingAction PendingFor(Match match, PlayerId player)
    {
        if (match.ActiveQuestion is { } q && q.Answerers.Contains(player) && !q.Answers.ContainsKey(player))
            return PendingAction.Answer;

        if (match.CurrentTurn == player)
            return PendingAction.Turn;

        return PendingAction.None;
    }

    public PendingAction Pending(Match match) => PendingFor(match, Id);

    /// <summary>Performs this bot's pending action, if it has one. Returns false if there was nothing to do.</summary>
    public bool TryAct(Match match)
    {
        if (match.ActiveQuestion is { } q && Pending(match) == PendingAction.Answer)
        {
            match.SubmitAnswer(Id, ChooseAnswer(q.Question));
            return true;
        }

        return TryTakeTurn(match);
    }

    /// <summary>Makes a random legal move if it's this player's turn. Never answers questions.</summary>
    public bool TryTakeTurn(Match match)
    {
        if (match.CurrentTurn != Id)
            return false;

        switch (match.Phase)
        {
            case MatchPhase.CastlePlacement:
                match.PlaceCastle(Id, Pick(match.GetValidCastleSites()));
                return true;
            case MatchPhase.ExpansionPicks:
                match.ClaimTerritory(Id, Pick(match.GetValidClaims(Id)));
                return true;
            case MatchPhase.BattleAttack:
                match.Attack(Id, Pick(match.GetValidAttackTargets(Id)));
                return true;
            default:
                return false;
        }
    }

    private Answer ChooseAnswer(Question question)
    {
        var knowsIt = rng.NextDouble() < accuracy;
        switch (question)
        {
            case MultipleChoiceQuestion mc:
                if (knowsIt) return new ChoiceAnswer(mc.CorrectIndex);
                var wrong = Enumerable.Range(0, mc.Options.Count).Where(i => i != mc.CorrectIndex).ToList();
                return new ChoiceAnswer(wrong[rng.Next(wrong.Count)]);

            case NumericQuestion n:
                var spread = knowsIt ? 0.05 : 0.4;
                var noise = (rng.NextDouble() * 2 - 1) * spread;
                var guess = n.Answer == 0 ? noise * 10 : n.Answer * (1 + noise);
                return new NumericAnswer(Math.Round(guess, 2));

            default:
                throw new NotSupportedException(question.GetType().Name);
        }
    }

    private int Pick(IReadOnlyList<int> options) => options[rng.Next(options.Count)];
}
