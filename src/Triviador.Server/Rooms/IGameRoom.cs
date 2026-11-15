using Triviador.Domain;
using Triviador.Server.Hubs;

namespace Triviador.Server.Rooms;

/// <summary>What the registry, hub and game loop need from any kind of game (quick match or Conquest).</summary>
public interface IGameRoom
{
    string Id { get; }
    IReadOnlyList<PlayerId> Humans { get; }

    /// <summary>Set once the game is over or every human has left.</summary>
    DateTimeOffset? FinishedAt { get; }

    Task StartAsync();
    Task TickAsync();
    Task LeaveAsync(PlayerId player);

    /// <summary>Sends a reconnecting player everything they need to redraw the game.</summary>
    Task SendJoinedAsync(IGameClient client, PlayerId viewer);
}
