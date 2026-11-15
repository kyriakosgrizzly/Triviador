using Triviador.Domain.Questions;

namespace Triviador.Domain.Cards;

/// <summary>The four "colours" of the deck. Every card's question comes from its category.</summary>
public enum CardCategory
{
    Geography,
    Science,
    History,
    Tech,
}

/// <summary>Number cards plus the question-based versions of Uno's action cards.</summary>
public enum CardKind
{
    Number,

    /// <summary>Uno's Skip: nobody can steal the next player's question.</summary>
    Lockout,

    /// <summary>Uno's Reverse: direction flips and the new next player gets no 2-second head start.</summary>
    HotSeat,

    /// <summary>Uno's +2: the next player must answer a question; wrong (or too slow) draws 2.</summary>
    PopQuiz,

    /// <summary>Uno's Wild: you choose the next category.</summary>
    PickTopic,

    /// <summary>Uno's Wild +4: you and a chosen opponent answer the same question; the loser draws 4.</summary>
    Duel,
}

public sealed record Card(int Id, CardKind Kind, CardCategory? Category, int? Number)
{
    public bool IsWild => Kind is CardKind.PickTopic or CardKind.Duel;
}

public enum CardsPhase
{
    NotStarted,

    /// <summary>The current player plays a card or draws.</summary>
    Turn,

    /// <summary>A played card's question is open: the owner has the head start, then anyone may steal.</summary>
    Question,

    /// <summary>The victim of a Pop Quiz answers alone.</summary>
    PopQuiz,

    /// <summary>Two players answer the same question; the loser draws.</summary>
    Duel,

    /// <summary>A short pause after a question or a draw, so everyone sees what happened before the next turn.</summary>
    Reveal,

    Finished,
}

public enum DrawReason
{
    /// <summary>Chose (or had) to draw instead of playing.</summary>
    Draw,
    TurnTimeout,
    WrongAnswer,
    TooSlow,
    NoAnswer,
    WrongSteal,
    PopQuiz,
    Duel,
}

public sealed record CardsOptions
{
    public int HandSize { get; init; } = 7;

    /// <summary>The whole question, head start included.</summary>
    public TimeSpan QuestionTime { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long only the card's owner may answer before anyone can steal.</summary>
    public TimeSpan HeadStart { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Pause after a question is answered (the right answer is shown) before the next turn starts. 0 = none.</summary>
    public TimeSpan RevealTime { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Pause after someone draws instead of playing, before the next turn. 0 = none.</summary>
    public TimeSpan DrawPause { get; init; } = TimeSpan.FromSeconds(0.8);

    /// <summary>How long a player may think before the game draws a card for them.</summary>
    public TimeSpan TurnTime { get; init; } = TimeSpan.FromSeconds(20);

    public int WrongAnswerDraw { get; init; } = 2;

    /// <summary>
    /// False: a wrong answer still discards the card (without its action). True: it goes back to your hand.
    /// Taking it back makes games drag on — with ~65% right answers hands barely shrink.
    /// </summary>
    public bool WrongAnswerReturnsCard { get; init; }
    public int StolenDraw { get; init; } = 1;
    public int NoAnswerDraw { get; init; } = 1;
    public int WrongStealDraw { get; init; } = 2;
    public int PopQuizDraw { get; init; } = 2;
    public int DuelDraw { get; init; } = 4;

    /// <summary>
    /// Game length cap: after this many turns, fewest cards wins. With "wrong answer draws 2", hands
    /// only shrink reliably when players get ~80% right, so without a cap games can drag on.
    /// </summary>
    public int MaxTurns { get; init; } = 120;

    public static CardsOptions Default { get; } = new();
}

/// <summary>The open question, as the engine sees it (the question includes its answer).</summary>
public sealed record CardQuestion(
    int Serial,
    CardsPhase Purpose,
    PlayerId Owner,
    PlayerId? Target,
    Card Card,
    CardCategory Category,
    MultipleChoiceQuestion Question,
    DateTimeOffset AskedAt,
    DateTimeOffset StealOpensAt,
    DateTimeOffset Deadline,
    bool Stealable);

public sealed record CardsStanding(PlayerId Player, int Place, int CardsLeft);

// ------------------------------------------------------------------ events (for the log and animations)

public abstract record CardsEvent;

public sealed record CardsStarted(Card TopCard, CardCategory Category, PlayerId First) : CardsEvent;

public sealed record CardsTurnStarted(PlayerId Player, int Turn) : CardsEvent;

public sealed record CardsDrew(PlayerId Player, int Count, DrawReason Reason) : CardsEvent;

/// <summary>A card was put forward and its question is now open (or a Pop Quiz / Duel began).</summary>
public sealed record CardsQuestionOpened(PlayerId Owner, PlayerId? Target, Card Card, CardCategory Category, CardsPhase Purpose) : CardsEvent;

public sealed record CardsQuestionResolved(
    string QuestionId, string CorrectAnswer, int CorrectIndex, IReadOnlyList<AnswerResult> Results) : CardsEvent;

/// <summary>
/// The card reached the pile. <paramref name="StolenBy"/> is set when someone else answered first;
/// <paramref name="Fizzled"/> when the owner answered wrong (the card is spent, its action isn't).
/// </summary>
public sealed record CardsCardLanded(PlayerId Owner, Card Card, CardCategory Category, PlayerId? StolenBy, bool Fizzled) : CardsEvent;

public sealed record CardsCardReturned(PlayerId Owner, Card Card) : CardsEvent;

public sealed record CardsStealDiscard(PlayerId Stealer, Card Card) : CardsEvent;

public sealed record CardsLockout(PlayerId Player) : CardsEvent;

public sealed record CardsReversed(int Direction, PlayerId HotSeat) : CardsEvent;

public sealed record CardsPopQuizResult(PlayerId Target, bool Correct) : CardsEvent;

public sealed record CardsDuelResult(PlayerId Owner, PlayerId Target, PlayerId? Loser, string Reason) : CardsEvent;

public sealed record CardsFinished(IReadOnlyList<CardsStanding> Standings) : CardsEvent;
