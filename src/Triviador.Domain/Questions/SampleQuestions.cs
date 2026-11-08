namespace Triviador.Domain.Questions;

/// <summary>A starter bank for local play and bots. Replaced by Cosmos DB in milestone 3.</summary>
public static class SampleQuestions
{
    public static IReadOnlyList<Question> All { get; } =
    [
        new MultipleChoiceQuestion("mc-01", "Azure", "Which Azure service hosts real-time ASP.NET Core SignalR hubs at scale?",
            ["Azure SignalR Service", "Azure Event Hubs", "Azure Relay", "Azure Queue Storage"], 0),
        new MultipleChoiceQuestion("mc-02", "Azure", "How many RU/s does the Azure Cosmos DB free tier include?",
            ["400", "1000", "5000", "25000"], 1),
        new MultipleChoiceQuestion("mc-03", "Geography", "What is the capital of Australia?",
            ["Sydney", "Melbourne", "Canberra", "Perth"], 2),
        new MultipleChoiceQuestion("mc-04", "Science", "What is the chemical symbol for gold?",
            ["Ag", "Au", "Gd", "Go"], 1),
        new MultipleChoiceQuestion("mc-05", "Science", "Which is the largest planet in the Solar System?",
            ["Saturn", "Neptune", "Earth", "Jupiter"], 3),
        new MultipleChoiceQuestion("mc-06", "Literature", "Who is traditionally credited as the author of the Odyssey?",
            ["Homer", "Virgil", "Sophocles", "Hesiod"], 0),
        new MultipleChoiceQuestion("mc-07", ".NET", "What does the C# compiler produce before the JIT runs?",
            ["Machine code", "Intermediate Language (IL)", "Java bytecode", "WebAssembly"], 1),
        new MultipleChoiceQuestion("mc-08", "Web", "Which HTTP status code means \"Not Found\"?",
            ["400", "401", "403", "404"], 3),
        new MultipleChoiceQuestion("mc-09", "Geography", "Which is the largest ocean?",
            ["Atlantic", "Pacific", "Indian", "Arctic"], 1),
        new MultipleChoiceQuestion("mc-10", "Science", "Which element has atomic number 1?",
            ["Helium", "Oxygen", "Hydrogen", "Carbon"], 2),

        new NumericQuestion("num-01", "History", "In what year did the Berlin Wall fall?", 1989),
        new NumericQuestion("num-02", "Geography", "How tall is Mount Everest?", 8849, "m"),
        new NumericQuestion("num-03", "Science", "How many bones are in the adult human body?", 206),
        new NumericQuestion("num-04", "History", "In what year did humans first land on the Moon?", 1969),
        new NumericQuestion("num-05", "Science", "Speed of light in a vacuum, in km/s (rounded)?", 299792, "km/s"),
        new NumericQuestion("num-06", "Politics", "How many member states does the European Union have?", 27),
        new NumericQuestion("num-07", ".NET", "In what year was .NET Framework 1.0 released?", 2002),
        new NumericQuestion("num-08", "Sport", "How long is a marathon?", 42.195, "km"),
        new NumericQuestion("num-09", "Music", "How many keys are on a standard piano?", 88),
        new NumericQuestion("num-10", "Azure", "In what year was Windows Azure first announced?", 2008),
    ];
}
