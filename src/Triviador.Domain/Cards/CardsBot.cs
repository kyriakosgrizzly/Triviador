using Triviador.Domain.Questions;

namespace Triviador.Domain.Cards;

/// <summary>
/// A simple Quiz Cards bot: answers its own questions with a set accuracy, sometimes tries to steal
/// (and only when it thinks it knows the answer), plays matching number cards first, saves wilds,
/// and uses attack cards when the next player is close to winning.
/// </summary>
public sealed class CardsBot(PlayerId id, Random rng, double accuracy = 0.6, double stealChance = 0.35)
{
    private int _decidedSerial = -1;
    private bool _willSteal;
    private bool _stealKnowsIt;

    public PlayerId Id { get; } = id;
    public double Accuracy { get; } = accuracy;

    /// <summary>What the bot would do right now, so the host can give it a matching "thinking" delay.</summary>
    public BotIntent Intent(CardsGame game)
    {
        if (game.Phase == CardsPhase.Finished) return BotIntent.None;
        if (game.IsAnswerer(Id)) return BotIntent.Answer;
        if (game.Phase == CardsPhase.Turn && game.CurrentPlayer == Id) return BotIntent.Play;

        if (game.OpenQuestion is { Purpose: CardsPhase.Question, Stealable: true } q && q.Owner != Id && !game.Answered.Contains(Id))
        {
            Decide(q.Serial);
            if (_willSteal) return game.CanAnswer(Id) ? BotIntent.Steal : BotIntent.WaitToSteal;
        }
        return BotIntent.None;
    }

    public bool TryAct(CardsGame game)
    {
        switch (Intent(game))
        {
            case BotIntent.Answer:
                game.SubmitAnswer(Id, Answer(game.OpenQuestion!.Question, rng.NextDouble() < Accuracy));
                return true;
            case BotIntent.Steal:
                game.SubmitAnswer(Id, Answer(game.OpenQuestion!.Question, _stealKnowsIt));
                return true;
            case BotIntent.Play:
                PlayTurn(game);
                return true;
            default:
                return false;
        }
    }

    private void Decide(int serial)
    {
        if (serial == _decidedSerial) return;
        _decidedSerial = serial;
        // Only steal when it "knows" the answer — but bots are sometimes overconfident.
        _stealKnowsIt = rng.NextDouble() < Accuracy;
        _willSteal = rng.NextDouble() < stealChance && (_stealKnowsIt || rng.NextDouble() < 0.15);
    }

    private ChoiceAnswer Answer(MultipleChoiceQuestion q, bool knowsIt)
    {
        if (knowsIt) return new ChoiceAnswer(q.CorrectIndex);
        var wrong = Enumerable.Range(0, q.Options.Count).Where(i => i != q.CorrectIndex).ToList();
        return new ChoiceAnswer(wrong[rng.Next(wrong.Count)]);
    }

    private void PlayTurn(CardsGame game)
    {
        var playable = game.PlayableCards(Id);
        if (playable.Count == 0)
        {
            game.Draw(Id);
            return;
        }

        var hand = game.HandOf(Id);
        var next = game.Next(Id);
        var threat = game.HandCount(next) <= 2;
        var favourite = hand.Where(c => c.Category is not null)
            .GroupBy(c => c.Category!.Value)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .DefaultIfEmpty(game.ActiveCategory)
            .First();

        int Score(Card c) => c.Kind switch
        {
            CardKind.Number => 10,
            CardKind.PopQuiz or CardKind.Lockout => threat ? 20 : 8,
            CardKind.HotSeat => threat ? 15 : 7,
            CardKind.PickTopic => hand.Count <= 2 ? 12 : 2,
            CardKind.Duel => threat || hand.Count <= 2 ? 18 : 1,
            _ => 0,
        };

        var card = playable
            .OrderByDescending(Score)
            .ThenByDescending(c => c.Category == favourite)
            .ThenBy(_ => rng.Next())
            .First();

        if (card.Kind == CardKind.Duel)
        {
            var target = game.Players.Select(p => p.Id).Where(p => p != Id).OrderBy(game.HandCount).ThenBy(_ => rng.Next()).First();
            game.Play(Id, card.Id, favourite, target);
        }
        else
        {
            game.Play(Id, card.Id, card.IsWild ? favourite : null);
        }
    }
}

public enum BotIntent
{
    None,
    Play,
    Answer,
    Steal,

    /// <summary>Planning to steal once the head start is over.</summary>
    WaitToSteal,
}
