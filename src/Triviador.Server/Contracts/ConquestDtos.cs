using Triviador.Domain;
using Triviador.Domain.Conquest;
using Triviador.Domain.Questions;

namespace Triviador.Server.Contracts;

// What a Conquest player's browser receives. Each player gets their own copy of the state:
// your wallet and income are only in yours. Nothing here contains an unrevealed answer.

/// <summary>Sent once on join: the fixed rules the screen needs to show costs and effects.</summary>
public sealed record ConquestRulesDto(
    IReadOnlyList<string> Colors,
    IReadOnlyList<WeaponRuleDto> Weapons,
    IReadOnlyList<Wallet> FortifyCosts, // [i] = cost of raising a castle from level i to i+1
    int MaxCastle,
    IReadOnlyList<BuildingRuleDto> Buildings,
    int MaxRounds,
    int DominationRegions,
    int RegionCount);

public sealed record WeaponRuleDto(Weapon Weapon, Wallet Cost, int Power);

public sealed record BuildingRuleDto(Building Building, Wallet Cost, string Effect);

public sealed record ConquestPlayerDto(
    PlayerId Id,
    string Name,
    bool IsBot,
    int Seat,
    string? Color,
    int? Capital,
    int Regions,
    int Score,
    bool Eliminated,
    bool Ready,
    bool Left);

public sealed record ConquestRegionDto(int Id, Resource Resource, PlayerId? Owner, int Castle, Building Building, PlayerId? CapitalOf);

public sealed record ConquestYouDto(Wallet Wallet, Wallet Income, bool Ready, int? RemovedOption);

public sealed record ConquestStateDto(
    string GameId,
    ConquestPhase Phase,
    int Round,
    int MaxRounds,
    IReadOnlyList<ConquestPlayerDto> Players,
    IReadOnlyList<ConquestRegionDto> Regions,
    PlayerId? CurrentPicker,
    DateTimeOffset? PickDeadline,
    IReadOnlyList<string> AvailableColors,
    IReadOnlyList<int> ValidCapitals,
    PublicQuestion? Question,
    DateTimeOffset? QuestionDeadline,
    IReadOnlyList<PlayerId> Answerers,
    IReadOnlyList<PlayerId> PendingAnswerers,
    DateTimeOffset? PlanDeadline,
    IReadOnlyList<PlayerId> Initiative,
    ConquestYouDto? You,
    IReadOnlyList<ConquestStanding>? Standings,
    DateTimeOffset ServerNow);
