using Triviador.Domain;
using Triviador.Domain.Cards;
using Triviador.Domain.Questions;

namespace Triviador.Server.Contracts;

// What a Quiz Cards player's browser receives. Each player gets their own copy: only you see your hand.
// The open question is sent without its answer.

public sealed record CardsRulesDto(
    int HandSize,
    double QuestionSeconds,
    double HeadStartSeconds,
    double TurnSeconds,
    int WrongAnswerDraw,
    int StolenDraw,
    int NoAnswerDraw,
    int WrongStealDraw,
    int PopQuizDraw,
    int DuelDraw,
    int MaxTurns,
    IReadOnlyList<CardCategory> Categories,
    double RevealSeconds,
    double DrawPauseSeconds);

public sealed record CardsPlayerDto(
    PlayerId Id,
    string Name,
    bool IsBot,
    int Seat,
    int Cards,
    bool Left,
    bool LockedOut,
    bool HotSeat);

public sealed record CardsQuestionDto(
    int Serial,
    CardsPhase Purpose,
    PlayerId Owner,
    PlayerId? Target,
    Card Card,
    CardCategory Category,
    PublicQuestion Question,
    DateTimeOffset AskedAt,
    DateTimeOffset StealOpensAt,
    DateTimeOffset Deadline,
    bool Stealable,
    IReadOnlyList<PlayerId> Answered);

public sealed record CardsStateDto(
    string GameId,
    CardsPhase Phase,
    int Turn,
    int MaxTurns,
    int Direction,
    PlayerId? CurrentPlayer,
    DateTimeOffset? TurnDeadline,
    DateTimeOffset? RevealUntil,
    IReadOnlyList<CardsPlayerDto> Players,
    Card? TopCard,
    CardCategory ActiveCategory,
    int DrawPile,
    IReadOnlyList<Card> Hand,
    IReadOnlyList<int> Playable,
    CardsQuestionDto? Question,
    bool CanAnswer,
    PlayerId? PendingPopQuiz,
    IReadOnlyList<CardsStanding>? Standings,
    DateTimeOffset ServerNow);
