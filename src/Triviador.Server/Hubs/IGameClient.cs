using Triviador.Server.Contracts;

namespace Triviador.Server.Hubs;

/// <summary>Messages the server pushes to browsers. Method names are what the JS client subscribes to.</summary>
public interface IGameClient
{
    /// <summary>Sent when a match starts, and again on reconnect, with everything needed to draw the board.</summary>
    Task MatchJoined(MapDto map, GameStateDto state);

    /// <summary>Sent after every change: what happened (for the log/animations) plus the new full state.</summary>
    Task MatchUpdated(IReadOnlyList<EventDto> events, GameStateDto state);

    Task QueueStatus(QueueStatusDto status);

    /// <summary>The private game you're in changed. Null means you're no longer in one.</summary>
    Task LobbyUpdated(LobbyDto? lobby);

    /// <summary>A Conquest game started (or you reconnected to one): the rules plus your view of the game.</summary>
    Task ConquestJoined(ConquestRulesDto rules, ConquestStateDto state);

    /// <summary>Something happened in your Conquest game.</summary>
    Task ConquestUpdated(IReadOnlyList<EventDto> events, ConquestStateDto state);

    /// <summary>A Quiz Cards game started (or you reconnected to one): the rules plus your view of the game.</summary>
    Task CardsJoined(CardsRulesDto rules, CardsStateDto state);

    /// <summary>Something happened in your Quiz Cards game.</summary>
    Task CardsUpdated(IReadOnlyList<EventDto> events, CardsStateDto state);
}
