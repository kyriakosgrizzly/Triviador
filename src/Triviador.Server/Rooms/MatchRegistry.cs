using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Triviador.Domain;
using Triviador.Server.Hubs;

namespace Triviador.Server.Rooms;

/// <summary>
/// All live matches, in memory. This is why the Container App runs a single replica for now:
/// a second replica wouldn't see these rooms. Scaling out is a later milestone.
/// </summary>
public sealed class MatchRegistry(
    IHubContext<GameHub, IGameClient> hub,
    TimeProvider time,
    IOptions<GameOptions> options,
    ILogger<MatchRegistry> logger)
{
    private static readonly string[] BotNames = ["Nino", "Giorgi", "Tamar", "Levan", "Ana", "Dato", "Mariam", "Luka"];

    private readonly ConcurrentDictionary<string, MatchRoom> _rooms = new();
    private readonly ConcurrentDictionary<PlayerId, string> _roomOfPlayer = new();
    private readonly Random _rng = new();

    public int ActiveRooms => _rooms.Count(r => r.Value.FinishedAt is null);

    public MatchRoom Create(IReadOnlyList<PlayerInfo> humans, int botCount)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var botAccuracy = new Dictionary<PlayerId, double>();
        var players = humans.ToList();

        foreach (var name in BotNames.OrderBy(_ => _rng.Next()).Take(botCount))
        {
            var botId = new PlayerId($"bot-{Guid.NewGuid():N}"[..12]);
            players.Add(new PlayerInfo(botId, $"{name} (bot)", IsBot: true));
            botAccuracy[botId] = 0.45 + _rng.NextDouble() * 0.3;
        }

        // Random seats, so the human isn't always the first to place a castle.
        var seated = players.OrderBy(_ => _rng.Next()).ToList();

        var room = new MatchRoom(id, seated, botAccuracy, hub, time, options.Value, new Random(_rng.Next()));
        _rooms[id] = room;
        foreach (var h in humans) _roomOfPlayer[h.Id] = id;

        logger.LogInformation("Match {MatchId} created: {Players}", id, string.Join(", ", seated.Select(p => p.Name)));
        return room;
    }

    public MatchRoom? FindActiveRoomFor(PlayerId player) =>
        _roomOfPlayer.TryGetValue(player, out var id)
        && _rooms.TryGetValue(id, out var room)
        && room.FinishedAt is null
            ? room
            : null;

    public void Leave(PlayerId player) => _roomOfPlayer.TryRemove(player, out _);

    public async Task TickAllAsync()
    {
        var now = time.GetUtcNow();
        var keepFor = TimeSpan.FromMinutes(options.Value.KeepFinishedMatchMinutes);

        foreach (var room in _rooms.Values)
        {
            try
            {
                await room.TickAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Tick failed for match {MatchId}", room.Id);
            }

            if (room.FinishedAt is { } finished && now - finished > keepFor)
            {
                _rooms.TryRemove(room.Id, out _);
                foreach (var h in room.Humans)
                    _roomOfPlayer.TryRemove(new KeyValuePair<PlayerId, string>(h, room.Id));
            }
        }
    }
}
