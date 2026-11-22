using System.Text.Json;
using System.Text.Json.Serialization;
using Triviador.Domain.Cards;
using Triviador.Domain.Questions;

namespace Triviador.Domain.Tests;

public class QuestionCatalogTests
{
    // The same settings the server gives the Cosmos SDK.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void Starts_with_the_embedded_bank()
    {
        var catalog = new QuestionCatalog();
        Assert.Equal(QuestionOrigin.Embedded, catalog.Origin);
        Assert.Equal(SampleQuestions.All.Count, catalog.All.Count);
        Assert.Null(catalog.LoadedAt);
        Assert.Empty(QuestionCatalog.Problems(SampleQuestions.All));
    }

    [Fact]
    public void Replace_swaps_the_set_new_games_use()
    {
        var catalog = new QuestionCatalog();
        var smaller = SampleQuestions.All.Take(400).ToList();
        if (QuestionCatalog.Problems(smaller).Count > 0) smaller = [.. SampleQuestions.All.Where((_, i) => i % 2 == 0)];

        var at = DateTimeOffset.Parse("2026-11-22T10:00:00Z");
        catalog.Replace(smaller, QuestionOrigin.CosmosDb, at);

        Assert.Equal(QuestionOrigin.CosmosDb, catalog.Origin);
        Assert.Equal(smaller.Count, catalog.All.Count);
        Assert.Equal(at, catalog.LoadedAt);
        Assert.Equal(smaller.Count, catalog.Stats().Total);
    }

    [Fact]
    public void Refuses_a_set_that_would_starve_a_card_colour()
    {
        var catalog = new QuestionCatalog();
        var noTech = SampleQuestions.All.Where(q => CardDeck.CategoryOf(q.Category) != CardCategory.Tech).ToList();

        Assert.Throws<ArgumentException>(() => catalog.Replace(noTech, QuestionOrigin.CosmosDb, DateTimeOffset.UnixEpoch));
        Assert.Equal(QuestionOrigin.Embedded, catalog.Origin);
        Assert.Contains(QuestionCatalog.Problems(noTech), p => p.StartsWith("Tech"));
    }

    [Fact]
    public void Refuses_duplicate_ids()
    {
        var doubled = SampleQuestions.All.Concat(SampleQuestions.All.Take(1)).ToList();
        Assert.Contains(QuestionCatalog.Problems(doubled), p => p.StartsWith("duplicate ids"));
    }

    [Fact]
    public void Every_embedded_question_round_trips_through_a_cosmos_document()
    {
        foreach (var question in SampleQuestions.All)
        {
            var json = JsonSerializer.Serialize(QuestionDocument.From(question), Json);
            var back = JsonSerializer.Deserialize<QuestionDocument>(json, Json)!.ToQuestion(out var problem);

            Assert.Null(problem);
            Assert.Equal(question.Id, back!.Id);
            Assert.Equal(question.Category, back.Category);
            Assert.Equal(question.Text, back.Text);
            Assert.Equal(question.CorrectAnswerText, back.CorrectAnswerText);
            if (question is MultipleChoiceQuestion mc)
                Assert.Equal(mc.Options, ((MultipleChoiceQuestion)back).Options);
        }
    }

    [Fact]
    public void Documents_are_camel_case_with_id_and_partition_key()
    {
        var json = JsonSerializer.Serialize(QuestionDocument.From(SampleQuestions.All.OfType<MultipleChoiceQuestion>().First()), Json);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("id", out _));
        Assert.True(doc.RootElement.TryGetProperty("category", out _)); // the container's partition key path is /category
        Assert.Equal("choice", doc.RootElement.GetProperty("type").GetString());
        Assert.False(doc.RootElement.TryGetProperty("answer", out _)); // nulls are left out
    }

    [Fact]
    public void Reads_a_document_written_by_hand_in_data_explorer()
    {
        const string json = """
            { "id": "my-1", "category": "Azure", "type": "choice", "text": "Which service stores the questions?",
              "options": ["Cosmos DB", "Azure SQL", "Table Storage", "Blob Storage"], "correct": 0,
              "_rid": "abc", "_etag": "\"00\"", "_ts": 1763800000 }
            """;
        var q = JsonSerializer.Deserialize<QuestionDocument>(json, Json)!.ToQuestion(out var problem);
        Assert.Null(problem);
        Assert.Equal("Cosmos DB", q!.CorrectAnswerText);
    }

    [Theory]
    [InlineData("""{ "id": "x", "category": "Azure", "type": "choice", "text": "?", "options": ["a", "b"], "correct": 2 }""", "correct must index one of the options")]
    [InlineData("""{ "id": "x", "category": "Azure", "type": "choice", "text": "?", "options": ["a", "a"], "correct": 0 }""", "options must be distinct and non-empty")]
    [InlineData("""{ "id": "x", "category": "Azure", "type": "choice", "text": "?", "options": ["a"], "correct": 0 }""", "needs 2 to 4 options")]
    [InlineData("""{ "id": "x", "category": "Azure", "type": "number", "text": "?" }""", "answer must be a number")]
    [InlineData("""{ "id": "x", "category": "Azure", "type": "essay", "text": "?" }""", "type must be \"choice\" or \"number\"")]
    [InlineData("""{ "id": "x", "category": "", "type": "number", "text": "?", "answer": 1 }""", "id, category and text are required")]
    [InlineData("""{ "id": "x", "category": "Azure", "type": "number", "text": "?", "answer": 1, "disabled": true }""", "disabled")]
    public void Skips_bad_or_disabled_documents(string json, string expected)
    {
        var q = JsonSerializer.Deserialize<QuestionDocument>(json, Json)!.ToQuestion(out var problem);
        Assert.Null(q);
        Assert.Equal(expected, problem);
    }
}
