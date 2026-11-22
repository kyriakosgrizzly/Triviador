using Triviador.Domain.Cards;
using Triviador.Domain.Questions;

namespace Triviador.Domain.Tests;

public class QuestionTests
{
    [Fact]
    public void Public_question_type_has_no_answer_fields()
    {
        var names = typeof(PublicQuestion).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("CorrectIndex", names);
        Assert.DoesNotContain("Answer", names);
    }

    [Fact]
    public void Sample_bank_is_valid()
    {
        var source = new InMemoryQuestionSource(SampleQuestions.All, new Random(1));
        Assert.NotNull(source.NextMultipleChoice());
        Assert.NotNull(source.NextNumeric());
        Assert.Equal(SampleQuestions.All.Count, SampleQuestions.All.Select(q => q.Id).Distinct().Count());
    }

    [Fact]
    public void Embedded_bank_loads_and_covers_every_card_category()
    {
        var choice = SampleQuestions.All.OfType<MultipleChoiceQuestion>().ToList();
        Assert.True(choice.Count >= 750, $"only {choice.Count} multiple-choice questions");
        Assert.True(SampleQuestions.All.OfType<NumericQuestion>().Count() >= 70);
        foreach (var category in Enum.GetValues<CardCategory>())
        {
            var count = choice.Count(q => CardDeck.CategoryOf(q.Category) == category);
            Assert.True(count >= 150, $"{category} has only {count} questions");
        }
        Assert.All(choice, q => Assert.Equal(q.Options.Count, q.Options.Distinct().Count()));
        Assert.Equal(SampleQuestions.All.Count, SampleQuestions.All.Select(q => q.Text).Distinct().Count());
    }

    [Fact]
    public void Bank_does_not_repeat_until_exhausted()
    {
        var source = new InMemoryQuestionSource(SampleQuestions.All, new Random(7));
        var count = SampleQuestions.All.OfType<MultipleChoiceQuestion>().Count();
        var ids = Enumerable.Range(0, count).Select(_ => source.NextMultipleChoice().Id).ToList();
        Assert.Equal(count, ids.Distinct().Count());
    }

    [Fact]
    public void Invalid_correct_index_is_rejected()
    {
        var bad = new MultipleChoiceQuestion("x", "t", "?", new[] { "a", "b" }, 5);
        Assert.Throws<ArgumentException>(() => new InMemoryQuestionSource(new Question[] { bad }, new Random()));
    }
}
