using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Triviador.Domain;
using Triviador.Domain.Questions;
using Triviador.Server.Hubs;

namespace Triviador.Server.Rooms;

/// <summary>
/// All live games (quick matches, Conquest and Quiz Cards), in memory. This is why the Container App runs a
/// single replica for now: a second replica wouldn't see these rooms. Scaling out is a later milestone.
/// </summary>
public sealed class MatchRegistry(
    IHubContext<GameHub, IGameClient> hub,
    TimeProvider time,
    IOptions<GameOptions> options,
    QuestionCatalog questions,
    ILogger<MatchRegistry> logger)
{
    private static readonly string[] BotNames = ["Nino", "Giorgi", "Tamar", "Levan", "Ana", "Dato", "Mariam", "Luka"];

    private readonly ConcurrentDictionary<string, IGameRoom> _rooms = new();
    private readonly ConcurrentDictionary<PlayerId, string> _roomOfPlayer = new();
    private readonly Random _rng = new();

    public int ActiveRooms => _rooms.Count(r => r.Value.FinishedAt is null);

    /// <summary>A quick match: always 3 seats, bots fill the rest.</summary>
    public MatchRoom Create(IReadOnlyList<PlayerInfo> humans, int botCount)
    {
        var (seated, accuracy) = Seat(humans, botCount);
        var room = new MatchRoom(NewId(), seated, accuracy, hub, time, options.Value, new Random(_rng.Next()));
        return Register(room, humans, seated, "Match");
    }

    /// <summary>A Conquest game with <paramref name="seats"/> players in total; bots fill the empty seats.</summary>
    public ConquestRoom CreateConquest(IReadOnlyList<PlayerInfo> humans, int seats)
    {
        var (seated, accuracy) = Seat(humans, Math.Max(0, seats - humans.Count));
        var room = new ConquestRoom(NewId(), seated, accuracy, hub, time, options.Value, new Random(_rng.Next()));
        return Register(room, humans, seated, "Conquest");
    }

    /// <summary>A Quiz Cards game with <paramref name="seats"/> players in total; bots fill the empty seats.</summary>
    public CardsRoom CreateCards(IReadOnlyList<PlayerInfo> humans, int seats)
    {
        var (seated, accuracy) = Seat(humans, Math.Max(0, seats - humans.Count));
        var room = new CardsRoom(NewId(), seated, accuracy, questions.All, hub, time, options.Value, new Random(_rng.Next()));
        return Register(room, humans, seated, "Cards");
    }

    public IGameRoom? FindActiveRoomFor(PlayerId player) =>
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
                logger.LogError(ex, "Tick failed for game {GameId}", room.Id);
            }

            if (room.FinishedAt is { } finished && now - finished > keepFor)
            {
                _rooms.TryRemove(room.Id, out _);
                foreach (var h in room.Humans)
                    _roomOfPlayer.TryRemove(new KeyValuePair<PlayerId, string>(h, room.Id));
            }
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private (List<PlayerInfo> Seated, Dictionary<PlayerId, double> Accuracy) Seat(IReadOnlyList<PlayerInfo> humans, int botCount)
    {
        var accuracy = new Dictionary<PlayerId, double>();
        var players = humans.ToList();

        foreach (var name in BotNames.OrderBy(_ => _rng.Next()).Take(botCount))
        {
            var botId = new PlayerId($"bot-{Guid.NewGuid():N}"[..12]);
            players.Add(new PlayerInfo(botId, $"{name} (bot)", IsBot: true));
            accuracy[botId] = 0.45 + _rng.NextDouble() * 0.3;
        }

        // Random seats, so humans aren't always first.
        return (players.OrderBy(_ => _rng.Next()).ToList(), accuracy);
    }

    private T Register<T>(T room, IReadOnlyList<PlayerInfo> humans, IReadOnlyList<PlayerInfo> seated, string kind) where T : IGameRoom
    {
        _rooms[room.Id] = room;
        foreach (var h in humans) _roomOfPlayer[h.Id] = room.Id;
        logger.LogInformation("{Kind} {GameId} created: {Players}", kind, room.Id, string.Join(", ", seated.Select(p => p.Name)));
        return room;
    }
}
