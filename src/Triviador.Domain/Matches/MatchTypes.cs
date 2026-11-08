using Triviador.Domain.Questions;

namespace Triviador.Domain.Matches;

public enum MatchPhase
{
    NotStarted,
    CastlePlacement,
    ExpansionQuestion,
    ExpansionPicks,
    BattleAttack,
    BattleDuel,
    BattleTiebreak,
    Finished,
}

public enum AnswerOutcome
{
    Accepted,
    TooLate,
}

public sealed record MatchOptions
{
    public int CastleHitPoints { get; init; } = 3;
    public int BattleRounds { get; init; } = 4;
    public TimeSpan MultipleChoiceTime { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan NumericTime { get; init; } = TimeSpan.FromSeconds(20);

    public static MatchOptions Default { get; } = new();
}

public sealed record Standing(PlayerId Player, int Place, int Score, bool Eliminated);

public sealed record PlayerState(
    PlayerInfo Info, int Score, int TerritoryCount, int? CastleTerritoryId, int CastleHitPoints, bool Eliminated);

/// <summary>Everything a (re)connecting client needs to draw the board. Contains no answers.</summary>
public sealed record MatchSnapshot(
    string MatchId,
    MatchPhase Phase,
    int BattleRound,
    PlayerId? CurrentTurn,
    IReadOnlyList<PlayerState> Players,
    IReadOnlyDictionary<int, PlayerId> Owners,
    PublicQuestion? Question,
    DateTimeOffset? QuestionDeadline,
    IReadOnlyList<PlayerId> PendingAnswerers);

// ---- Domain events: the server broadcasts these over SignalR, and MatchFinished goes to Event Grid. ----

public abstract record MatchEvent;

public sealed record MatchStarted(string MatchId, IReadOnlyList<PlayerInfo> Players) : MatchEvent;
public sealed record PhaseChanged(MatchPhase Phase) : MatchEvent;
public sealed record TurnStarted(PlayerId Player, MatchPhase Phase) : MatchEvent;
public sealed record CastlePlaced(PlayerId Player, int TerritoryId) : MatchEvent;
public sealed record QuestionAsked(PublicQuestion Question, IReadOnlyList<PlayerId> Answerers, DateTimeOffset Deadline) : MatchEvent;
public sealed record AnswerReceived(PlayerId Player) : MatchEvent;
public sealed record QuestionResolved(string QuestionId, string CorrectAnswer, IReadOnlyList<AnswerResult> Results) : MatchEvent;
public sealed record PicksAwarded(IReadOnlyList<PlayerId> PickOrder) : MatchEvent;
public sealed record TerritoryClaimed(PlayerId Player, int TerritoryId) : MatchEvent;
public sealed record BattleRoundStarted(int Round) : MatchEvent;
public sealed record AttackDeclared(PlayerId Attacker, PlayerId Defender, int TerritoryId) : MatchEvent;
public sealed record TiebreakStarted(PlayerId Attacker, PlayerId Defender) : MatchEvent;
public sealed record TerritoryConquered(PlayerId Attacker, PlayerId Defender, int TerritoryId) : MatchEvent;
public sealed record AttackRepelled(PlayerId Attacker, PlayerId Defender, int TerritoryId) : MatchEvent;
public sealed record CastleDamaged(PlayerId Owner, PlayerId Attacker, int HitPointsLeft) : MatchEvent;
public sealed record PlayerEliminated(PlayerId Player, PlayerId By, int TerritoriesTransferred) : MatchEvent;
public sealed record MatchFinished(IReadOnlyList<Standing> Standings) : MatchEvent;
