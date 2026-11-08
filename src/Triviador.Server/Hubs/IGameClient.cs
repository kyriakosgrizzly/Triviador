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
}
