namespace Triviador.Domain.Questions;

/// <summary>Where the engine gets questions from. Cosmos DB implementation comes in milestone 3.</summary>
public interface IQuestionSource
{
    MultipleChoiceQuestion NextMultipleChoice();
    NumericQuestion NextNumeric();
}

/// <summary>Shuffled in-memory bank. Avoids repeats until a pool runs out, then reshuffles.</summary>
public sealed class InMemoryQuestionSource : IQuestionSource
{
    private readonly ShuffledPool<MultipleChoiceQuestion> _choice;
    private readonly ShuffledPool<NumericQuestion> _numeric;

    public InMemoryQuestionSource(IEnumerable<Question> questions, Random rng)
    {
        var list = questions.ToList();
        foreach (var q in list.OfType<MultipleChoiceQuestion>())
        {
            if (q.Options.Count < 2)
                throw new ArgumentException($"Question {q.Id} needs at least two options.");
            if (q.CorrectIndex < 0 || q.CorrectIndex >= q.Options.Count)
                throw new ArgumentException($"Question {q.Id} has an out-of-range correct index.");
        }

        _choice = new ShuffledPool<MultipleChoiceQuestion>(list.OfType<MultipleChoiceQuestion>().ToList(), rng);
        _numeric = new ShuffledPool<NumericQuestion>(list.OfType<NumericQuestion>().ToList(), rng);
    }

    public MultipleChoiceQuestion NextMultipleChoice() => _choice.Next();
    public NumericQuestion NextNumeric() => _numeric.Next();

    private sealed class ShuffledPool<T>(IReadOnlyList<T> items, Random rng)
    {
        private readonly Queue<T> _queue = new();

        public T Next()
        {
            if (items.Count == 0)
                throw new InvalidOperationException($"No {typeof(T).Name} questions available.");
            if (_queue.Count == 0)
            {
                var copy = items.ToArray();
                rng.Shuffle(copy);
                foreach (var item in copy) _queue.Enqueue(item);
            }
            return _queue.Dequeue();
        }
    }
}
