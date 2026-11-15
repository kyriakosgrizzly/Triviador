using Triviador.Domain.Questions;

namespace Triviador.Domain.Cards;

public static class CardDeck
{
    /// <summary>Per category: 1–9 twice, two of each action. Plus four of each wild. 104 cards.</summary>
    public static List<Card> Build()
    {
        var cards = new List<Card>();
        var id = 0;
        foreach (var category in Enum.GetValues<CardCategory>())
        {
            for (var copy = 0; copy < 2; copy++)
            {
                for (var n = 1; n <= 9; n++) cards.Add(new Card(id++, CardKind.Number, category, n));
                cards.Add(new Card(id++, CardKind.Lockout, category, null));
                cards.Add(new Card(id++, CardKind.HotSeat, category, null));
                cards.Add(new Card(id++, CardKind.PopQuiz, category, null));
            }
        }

        for (var i = 0; i < 4; i++)
        {
            cards.Add(new Card(id++, CardKind.PickTopic, null, null));
            cards.Add(new Card(id++, CardKind.Duel, null, null));
        }

        return cards;
    }

    /// <summary>Which card category a question bank category belongs to.</summary>
    public static CardCategory CategoryOf(string questionCategory) => questionCategory.Trim().ToLowerInvariant() switch
    {
        "geography" => CardCategory.Geography,
        "science" => CardCategory.Science,
        "azure" or ".net" or "web" or "tech" or "technology" => CardCategory.Tech,
        _ => CardCategory.History, // history, literature, music, sport, politics…
    };
}

/// <summary>Where card questions come from: multiple choice only, picked by card category.</summary>
public interface ICardQuestionSource
{
    MultipleChoiceQuestion Next(CardCategory category);
}

/// <summary>Shuffled per-category pools from a question bank. Falls back to any category if one is empty.</summary>
public sealed class CardQuestionPool : ICardQuestionSource
{
    private readonly Dictionary<CardCategory, List<MultipleChoiceQuestion>> _byCategory;
    private readonly Dictionary<CardCategory, Queue<MultipleChoiceQuestion>> _queues = new();
    private readonly List<MultipleChoiceQuestion> _all;
    private readonly Random _rng;

    public CardQuestionPool(IEnumerable<Question> questions, Random rng)
    {
        _rng = rng;
        _all = questions.OfType<MultipleChoiceQuestion>().ToList();
        if (_all.Count == 0) throw new ArgumentException("The card game needs multiple-choice questions.");
        _byCategory = _all.GroupBy(q => CardDeck.CategoryOf(q.Category)).ToDictionary(g => g.Key, g => g.ToList());
    }

    public MultipleChoiceQuestion Next(CardCategory category)
    {
        if (!_queues.TryGetValue(category, out var queue) || queue.Count == 0)
        {
            var source = _byCategory.TryGetValue(category, out var list) ? list : _all;
            var copy = source.ToArray();
            _rng.Shuffle(copy);
            _queues[category] = queue = new Queue<MultipleChoiceQuestion>(copy);
        }
        return queue.Dequeue();
    }
}
