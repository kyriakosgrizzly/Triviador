namespace Triviador.Domain.Questions;

/// <summary>
/// One question as it is stored in the Cosmos DB <c>questions</c> container (partition key <c>/category</c>).
/// Serialized camelCase, so a document looks like:
/// <code>
/// { "id": "mc-301", "category": "Azure", "type": "choice", "text": "...",
///   "options": ["a", "b", "c", "d"], "correct": 2 }
/// { "id": "num-56", "category": "History", "type": "number", "text": "...", "answer": 1912, "unit": null }
/// </code>
/// Set <c>"disabled": true</c> in Data Explorer to take a question out of play. Deleting it doesn't work
/// for seeded questions: the server would add it back from the embedded bank on its next start.
/// </summary>
public sealed record QuestionDocument(
    string Id,
    string Category,
    string Type,
    string Text,
    IReadOnlyList<string>? Options = null,
    int? Correct = null,
    double? Answer = null,
    string? Unit = null,
    bool? Disabled = null)
{
    public const string ChoiceType = "choice";
    public const string NumberType = "number";

    public static QuestionDocument From(Question question) => question switch
    {
        MultipleChoiceQuestion q => new QuestionDocument(q.Id, q.Category, ChoiceType, q.Text, q.Options.ToList(), q.CorrectIndex),
        NumericQuestion q => new QuestionDocument(q.Id, q.Category, NumberType, q.Text, Answer: q.Answer, Unit: q.Unit),
        _ => throw new ArgumentException($"Unknown question type {question.GetType().Name}.", nameof(question)),
    };

    /// <summary>The playable question, or null with a reason when the document is disabled or malformed.</summary>
    public Question? ToQuestion(out string? problem)
    {
        problem = null;
        if (Disabled == true) { problem = "disabled"; return null; }
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Category) || string.IsNullOrWhiteSpace(Text))
        {
            problem = "id, category and text are required";
            return null;
        }

        switch (Type)
        {
            case ChoiceType:
                if (Options is null || Options.Count is < 2 or > 4) { problem = "needs 2 to 4 options"; return null; }
                if (Options.Any(string.IsNullOrWhiteSpace) || Options.Distinct().Count() != Options.Count) { problem = "options must be distinct and non-empty"; return null; }
                if (Correct is not int correct || correct < 0 || correct >= Options.Count) { problem = "correct must index one of the options"; return null; }
                return new MultipleChoiceQuestion(Id, Category, Text, Options.ToList(), correct);

            case NumberType:
                if (Answer is not double answer || !double.IsFinite(answer)) { problem = "answer must be a number"; return null; }
                return new NumericQuestion(Id, Category, Text, answer, Unit);

            default:
                problem = $"type must be \"{ChoiceType}\" or \"{NumberType}\"";
                return null;
        }
    }
}
