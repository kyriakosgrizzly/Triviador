using System.Globalization;
using System.Text.Json.Serialization;

namespace Triviador.Domain.Questions;

public enum QuestionKind
{
    MultipleChoice,
    Numeric,
}

/// <summary>A question as stored server-side, including its answer. Never send this to a client.</summary>
public abstract record Question(string Id, string Category, string Text)
{
    public abstract QuestionKind Kind { get; }

    /// <summary>The client-safe view: same question, no answer.</summary>
    public abstract PublicQuestion ToPublic();

    public abstract string CorrectAnswerText { get; }
}

public sealed record MultipleChoiceQuestion(
    string Id, string Category, string Text, IReadOnlyList<string> Options, int CorrectIndex)
    : Question(Id, Category, Text)
{
    public override QuestionKind Kind => QuestionKind.MultipleChoice;
    public override PublicQuestion ToPublic() => new(Id, Kind, Category, Text, Options, Unit: null);
    public override string CorrectAnswerText => Options[CorrectIndex];
}

public sealed record NumericQuestion(
    string Id, string Category, string Text, double Answer, string? Unit = null)
    : Question(Id, Category, Text)
{
    public override QuestionKind Kind => QuestionKind.Numeric;
    public override PublicQuestion ToPublic() => new(Id, Kind, Category, Text, Options: null, Unit);

    public override string CorrectAnswerText =>
        Unit is null
            ? Answer.ToString(CultureInfo.InvariantCulture)
            : $"{Answer.ToString(CultureInfo.InvariantCulture)} {Unit}";
}

/// <summary>What clients see. Deliberately has no answer field.</summary>
public sealed record PublicQuestion(
    string Id, QuestionKind Kind, string Category, string Text, IReadOnlyList<string>? Options, string? Unit);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ChoiceAnswer), "choice")]
[JsonDerivedType(typeof(NumericAnswer), "numeric")]
public abstract record Answer;
public sealed record ChoiceAnswer(int OptionIndex) : Answer;
public sealed record NumericAnswer(double Value) : Answer;

public sealed record AnswerResult(PlayerId Player, Answer? Given, bool Correct, double? Error, TimeSpan? Elapsed);
