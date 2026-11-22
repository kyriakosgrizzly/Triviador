using System.Text.Json.Serialization;

namespace Triviador.Domain.Questions;

/// <summary>Where the questions currently being played came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QuestionOrigin>))]
public enum QuestionOrigin
{
    /// <summary>The bank compiled into Triviador.Domain (SampleQuestions). Used until Cosmos DB answers, or when it isn't configured.</summary>
    Embedded,

    /// <summary>The <c>questions</c> container in Azure Cosmos DB.</summary>
    CosmosDb,
}

/// <summary>
/// The live question set, shared by every new game. It starts with the embedded bank so the server can
/// host games straight away; <see cref="Replace"/> swaps in the Cosmos DB set once it has loaded.
/// Games already running keep the set they started with.
/// </summary>
public sealed class QuestionCatalog
{
    private volatile Snapshot _current;

    public QuestionCatalog() : this(SampleQuestions.All) { }

    public QuestionCatalog(IReadOnlyList<Question> initial) =>
        _current = new Snapshot(initial, QuestionOrigin.Embedded, null);

    public IReadOnlyList<Question> All => _current.Questions;
    public QuestionOrigin Origin => _current.Origin;
    public DateTimeOffset? LoadedAt => _current.LoadedAt;

    /// <summary>Swaps in a new question set. Throws if it couldn't run a card game (see <see cref="Problems"/>).</summary>
    public void Replace(IReadOnlyList<Question> questions, QuestionOrigin origin, DateTimeOffset loadedAt)
    {
        var problems = Problems(questions);
        if (problems.Count > 0)
            throw new ArgumentException("Refusing an unusable question set: " + string.Join("; ", problems), nameof(questions));
        _current = new Snapshot(questions, origin, loadedAt);
    }

    /// <summary>Minimum multiple-choice questions per card colour before a set is accepted.</summary>
    public const int MinPerCardCategory = 10;

    /// <summary>Why a question set can't be used; empty when it's fine.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<Question> questions)
    {
        var problems = new List<string>();
        var duplicates = questions.GroupBy(q => q.Id).Where(g => g.Count() > 1).Select(g => g.Key).Take(5).ToList();
        if (duplicates.Count > 0) problems.Add("duplicate ids " + string.Join(", ", duplicates));

        var choice = questions.OfType<MultipleChoiceQuestion>().ToList();
        foreach (var category in Enum.GetValues<Cards.CardCategory>())
        {
            var count = choice.Count(q => Cards.CardDeck.CategoryOf(q.Category) == category);
            if (count < MinPerCardCategory) problems.Add($"{category} has {count} multiple-choice questions (need {MinPerCardCategory})");
        }
        return problems;
    }

    /// <summary>Question counts for the /api/questions status endpoint.</summary>
    public CatalogStats Stats()
    {
        var snapshot = _current;
        var choice = snapshot.Questions.OfType<MultipleChoiceQuestion>().ToList();
        return new CatalogStats(
            snapshot.Origin,
            snapshot.LoadedAt,
            snapshot.Questions.Count,
            choice.Count,
            snapshot.Questions.OfType<NumericQuestion>().Count(),
            Enum.GetValues<Cards.CardCategory>().ToDictionary(
                c => c.ToString(),
                c => choice.Count(q => Cards.CardDeck.CategoryOf(q.Category) == c)));
    }

    private sealed record Snapshot(IReadOnlyList<Question> Questions, QuestionOrigin Origin, DateTimeOffset? LoadedAt);
}

public sealed record CatalogStats(
    QuestionOrigin Origin,
    DateTimeOffset? LoadedAt,
    int Total,
    int MultipleChoice,
    int Numeric,
    IReadOnlyDictionary<string, int> ByCardColour);
