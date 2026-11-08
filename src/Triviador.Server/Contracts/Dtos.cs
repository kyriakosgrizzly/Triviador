using Triviador.Domain;
using Triviador.Domain.Matches;
using Triviador.Domain.Questions;

namespace Triviador.Server.Contracts;

// Everything the browser receives. Nothing here contains a correct answer before it is revealed.

public sealed record MapDto(string Name, IReadOnlyList<TerritoryDto> Territories);

public sealed record TerritoryDto(int Id, string Name, int Value, int Column, int Row, IReadOnlyList<int> Neighbors);

public sealed record PlayerStateDto(
    PlayerId Id,
    string Name,
    bool IsBot,
    int Seat,
    int Score,
    int Territories,
    int? CastleTerritoryId,
    int CastleHitPoints,
    bool Eliminated);

public sealed record GameStateDto(
    string MatchId,
    MatchPhase Phase,
    int BattleRound,
    int BattleRounds,
    PlayerId? CurrentTurn,
    IReadOnlyList<PlayerStateDto> Players,
    IReadOnlyDictionary<int, PlayerId> Owners,
    PublicQuestion? Question,
    DateTimeOffset? QuestionDeadline,
    IReadOnlyList<PlayerId> PendingAnswerers,
    IReadOnlyList<int> ValidTargets,
    IReadOnlyList<Standing>? Standings,
    DateTimeOffset ServerNow);

/// <summary>A domain event tagged with its type name, e.g. { type: "TerritoryConquered", data: {...} }.</summary>
public sealed record EventDto(string Type, object Data);

public sealed record QueueStatusDto(bool Searching, int PlayersWaiting, int SecondsUntilBots);
