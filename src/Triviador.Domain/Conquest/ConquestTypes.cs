using System.Text.Json.Serialization;
using Triviador.Domain.Questions;

namespace Triviador.Domain.Conquest;

public enum Resource
{
    Gold,
    Manpower,
    Production,
}

public enum Building
{
    None,
    Barracks,
    Workshop,
    Market,
    University,
}

/// <summary>Bought for one attack and used up by it. Power above the castle level captures the region.</summary>
public enum Weapon
{
    Sword,
    Catapult,
    Trebuchet,
}

public enum ConquestPhase
{
    NotStarted,
    ColorQuestion,
    ColorPick,
    CapitalPick,
    SupplyQuestion,
    Planning,
    BattleQuestion,
    Finished,
}

/// <summary>An amount of each resource. Used for wallets, costs and income.</summary>
public readonly record struct Wallet(int Gold, int Manpower, int Production)
{
    public static Wallet Zero => default;
    public static Wallet Of(Resource r, int n) => r switch
    {
        Resource.Gold => new(n, 0, 0),
        Resource.Manpower => new(0, n, 0),
        _ => new(0, 0, n),
    };

    public static Wallet Each(int n) => new(n, n, n);

    public static Wallet operator +(Wallet a, Wallet b) => new(a.Gold + b.Gold, a.Manpower + b.Manpower, a.Production + b.Production);
    public static Wallet operator -(Wallet a, Wallet b) => new(a.Gold - b.Gold, a.Manpower - b.Manpower, a.Production - b.Production);
    public static Wallet operator *(Wallet a, int k) => new(a.Gold * k, a.Manpower * k, a.Production * k);

    public bool Covers(Wallet cost) => Gold >= cost.Gold && Manpower >= cost.Manpower && Production >= cost.Production;
    public int Total => Gold + Manpower + Production;
}

/// <summary>Every tunable number in one place.</summary>
public sealed record ConquestOptions
{
    public int MaxRounds { get; init; } = 12;
    public int MaxCastle { get; init; } = 3;
    public int CapitalCastle { get; init; } = 2;

    /// <summary>Chance that a neutral region starts with a level-1 castle (otherwise unwalled).</summary>
    public double NeutralCastleChance { get; init; } = 0.5;
    public Wallet StartingWallet { get; init; } = Wallet.Each(4);

    public int SupplyBonusFirst { get; init; } = 2;
    public int SupplyBonusSecond { get; init; } = 1;
    public int CatchUpBonus { get; init; } = 1;

    /// <summary>Holding this share of the map at the end of a round wins outright.</summary>
    public double DominationShare { get; init; } = 0.6;

    public int PointsPerRegion { get; init; } = 2;
    public int PointsPerBuilding { get; init; } = 1;
    public int PointsPerCastleLevel { get; init; } = 1;
    public int PointsPerCapital { get; init; } = 3;

    public TimeSpan PickTime { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan PlanningTime { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan MultipleChoiceTime { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan NumericTime { get; init; } = TimeSpan.FromSeconds(20);

    public static ConquestOptions Default { get; } = new();

    public static IReadOnlyList<string> Colors { get; } = ["crimson", "azure", "amber", "emerald", "violet", "copper"];

    public static Wallet CostOf(Building b) => b switch
    {
        Building.Barracks => new(2, 0, 2),
        Building.Workshop => new(2, 2, 0),
        Building.Market => new(0, 2, 2),
        Building.University => new(4, 0, 2),
        _ => throw new GameRuleException("Nothing to build."),
    };

    public static Wallet CostOf(Weapon w) => w switch
    {
        Weapon.Sword => new(1, 1, 0),
        Weapon.Catapult => new(2, 1, 2),
        Weapon.Trebuchet => new(3, 2, 4),
        _ => throw new GameRuleException("Unknown weapon."),
    };

    public static int PowerOf(Weapon w) => w switch
    {
        Weapon.Sword => 1,
        Weapon.Catapult => 2,
        Weapon.Trebuchet => 4,
        _ => 0,
    };

    /// <summary>Cost of raising a castle from <paramref name="currentLevel"/> to the next level.</summary>
    public static Wallet FortifyCost(int currentLevel) => currentLevel switch
    {
        0 => new(1, 0, 2),
        1 => new(1, 0, 3),
        2 => new(2, 0, 4),
        _ => throw new GameRuleException("That castle is already at its highest level."),
    };

    /// <summary>Extra income a building gives each round.</summary>
    public static Wallet IncomeOf(Building b) => b switch
    {
        Building.Barracks => Wallet.Of(Resource.Manpower, 1),
        Building.Workshop => Wallet.Of(Resource.Production, 1),
        Building.Market => Wallet.Of(Resource.Gold, 1),
        _ => Wallet.Zero,
    };
}

// ------------------------------------------------------------------ orders

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BuildOrder), "build")]
[JsonDerivedType(typeof(FortifyOrder), "fortify")]
[JsonDerivedType(typeof(AttackOrder), "attack")]
public abstract record Order;

public sealed record BuildOrder(int Region, Building Building) : Order;

/// <summary>Raise a region's castle by one level (once per region per round).</summary>
public sealed record FortifyOrder(int Region) : Order;

/// <summary>Attack a bordering region with a weapon bought this round.</summary>
public sealed record AttackOrder(int From, int To, Weapon Weapon) : Order;

// ------------------------------------------------------------------ state for clients

public sealed record ConquestRegionState(
    int Id, Resource Resource, PlayerId? Owner, int Castle, Building Building, PlayerId? CapitalOf);

public sealed record ConquestPlayerState(
    PlayerInfo Info,
    int Seat,
    string? Color,
    int? Capital,
    int Regions,
    int Score,
    bool Eliminated,
    bool Ready,
    Wallet? Wallet); // only filled in for the player looking

public sealed record BattleReport(
    PlayerId Attacker,
    PlayerId? Defender,
    int From,
    int To,
    Weapon Weapon,
    int CastleBefore,
    int CastleAfter,
    bool AttackerCorrect,
    bool? DefenderCorrect,
    bool HitLanded,
    bool Captured,
    string Reason);

public sealed record ConquestStanding(PlayerId Player, int Place, int Score, int Regions, bool Eliminated);

// ------------------------------------------------------------------ events

public abstract record ConquestEvent;

public sealed record ConquestStarted(string GameId, IReadOnlyList<PlayerInfo> Players) : ConquestEvent;
public sealed record ConquestPhaseChanged(ConquestPhase Phase, int Round) : ConquestEvent;
public sealed record ConquestQuestionAsked(PublicQuestion Question, IReadOnlyList<PlayerId> Answerers, DateTimeOffset Deadline) : ConquestEvent;
public sealed record ConquestAnswerReceived(PlayerId Player) : ConquestEvent;
public sealed record ConquestQuestionResolved(string QuestionId, string CorrectAnswer, IReadOnlyList<AnswerResult> Results, IReadOnlyList<PlayerId> Ranking) : ConquestEvent;
public sealed record PickTurn(PlayerId Player, ConquestPhase Phase, DateTimeOffset Deadline) : ConquestEvent;
public sealed record ColorPicked(PlayerId Player, string Color) : ConquestEvent;
public sealed record CapitalPicked(PlayerId Player, int Region) : ConquestEvent;
public sealed record SupplyAwarded(PlayerId Player, int Place, Wallet Bonus) : ConquestEvent;
public sealed record IncomeCollected(PlayerId Player, Wallet Income) : ConquestEvent;
public sealed record PlayerReady(PlayerId Player) : ConquestEvent;
public sealed record BattleFought(BattleReport Report) : ConquestEvent;
public sealed record CapitalCaptured(PlayerId From, PlayerId By, int Region) : ConquestEvent;
public sealed record BuildingBuilt(PlayerId Player, int Region, Building Building) : ConquestEvent;
public sealed record CastleFortified(PlayerId Player, int Region, int Level) : ConquestEvent;
public sealed record AttackCancelled(PlayerId Player, int From, int To, Weapon Weapon, string Reason) : ConquestEvent;
public sealed record ConquestPlayerEliminated(PlayerId Player, PlayerId By) : ConquestEvent;
public sealed record ConquestFinished(IReadOnlyList<ConquestStanding> Standings) : ConquestEvent;
