using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Triviador.Domain;
using Triviador.Domain.Cards;
using Triviador.Domain.Matches;
using Triviador.Server.Contracts;
using Triviador.Server.Hubs;

namespace Triviador.Server.Rooms;

/// <summary>
/// First-come queues, one per mode. A game starts as soon as a queue has a full table of humans.
/// Bots are never added on their own: anyone waiting can press "Add bots and start", which starts a game
/// with everyone in that queue and bots in the empty seats. (Setting <see cref="GameOptions.BotFillAfterSeconds"/>
/// above 0 brings back automatic filling after that many seconds.) A player waits in at most one queue.
/// Rating-based matching arrives with ranked play (milestone 5).
/// </summary>
public sealed class Matchmaker(
    MatchRegistry registry,
    IHubContext<GameHub, IGameClient> hub,
    TimeProvider time,
    IOptions<GameOptions> options)
{
    /// <summary>Quick match: always exactly 3 seats.</summary>
    public static readonly QueueMode Quick = new(LobbyModes.Quick, FullTable: Match.PlayerCount, MinTable: Match.PlayerCount);

    /// <summary>Quiz Cards: starts at once with 4 humans; "Add bots" fills the table up to 4.</summary>
    public static readonly QueueMode Cards = new(LobbyModes.Cards, FullTable: CardsGame.MaxPlayers, MinTable: CardsGame.MaxPlayers);

    private static readonly QueueMode[] Modes = [Quick, Cards];

    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<Waiting>> _queues = Modes.ToDictionary(m => m.Name, _ => new List<Waiting>());
    private readonly Dictionary<string, int> _lastAnnouncedSecond = Modes.ToDictionary(m => m.Name, _ => -1);

    public Task JoinAsync(PlayerInfo player) => JoinAsync(player, Quick);

    public async Task JoinAsync(PlayerInfo player, QueueMode mode)
    {
        string? leftMode = null;
        lock (_lock)
        {
            // Switching queues: drop out of the other one first.
            foreach (var (name, queue) in _queues)
                if (name != mode.Name && queue.RemoveAll(w => w.Player.Id == player.Id) > 0)
                    leftMode = name;

            var target = _queues[mode.Name];
            if (target.All(w => w.Player.Id != player.Id))
                target.Add(new Waiting(player, time.GetUtcNow()));
        }

        if (leftMode is not null) await AnnounceAsync(ModeNamed(leftMode), force: true);
        await AnnounceAsync(mode, force: true);
    }

    public async Task LeaveAsync(PlayerId player)
    {
        var removedFrom = new List<QueueMode>();
        lock (_lock)
        {
            foreach (var mode in Modes)
                if (_queues[mode.Name].RemoveAll(w => w.Player.Id == player) > 0)
                    removedFrom.Add(mode);
        }

        if (removedFrom.Count == 0) return;
        await hub.Clients.User(player.Value).QueueStatus(new QueueStatusDto(false, 0, 0, removedFrom[0].Name, removedFrom[0].FullTable));
        foreach (var mode in removedFrom) await AnnounceAsync(mode, force: true);
    }

    /// <summary>"Add bots and start": everyone in the caller's queue starts now; bots take the empty seats.</summary>
    public async Task StartWithBotsAsync(PlayerId player)
    {
        QueueMode? mode = null;
        List<PlayerInfo> humans = [];
        lock (_lock)
        {
            foreach (var m in Modes)
            {
                var queue = _queues[m.Name];
                if (queue.All(w => w.Player.Id != player)) continue;
                mode = m;
                humans = queue.Take(m.FullTable).Select(w => w.Player).ToList();
                queue.RemoveRange(0, humans.Count);
                break;
            }
        }

        if (mode is null) throw new HubException("You're not searching for a game.");
        await StartAsync(mode, humans);
        await AnnounceAsync(mode, force: true);
    }

    public async Task TickAsync()
    {
        foreach (var mode in Modes)
            await TickAsync(mode);
    }

    private async Task TickAsync(QueueMode mode)
    {
        var groups = new List<List<PlayerInfo>>();
        var now = time.GetUtcNow();
        var fillAfter = TimeSpan.FromSeconds(options.Value.BotFillAfterSeconds);

        lock (_lock)
        {
            var queue = _queues[mode.Name];
            while (queue.Count >= mode.FullTable)
            {
                groups.Add(queue.Take(mode.FullTable).Select(w => w.Player).ToList());
                queue.RemoveRange(0, mode.FullTable);
            }

            if (fillAfter > TimeSpan.Zero && queue.Count > 0 && now - queue[0].Since >= fillAfter)
            {
                groups.Add(queue.Select(w => w.Player).ToList());
                queue.Clear();
            }
        }

        foreach (var humans in groups)
            await StartAsync(mode, humans);

        await AnnounceAsync(mode, force: groups.Count > 0);
    }

    private async Task StartAsync(QueueMode mode, IReadOnlyList<PlayerInfo> humans)
    {
        var seats = Math.Max(humans.Count, mode.MinTable);
        IGameRoom room = mode.Name == LobbyModes.Cards
            ? registry.CreateCards(humans, seats)
            : registry.Create(humans, botCount: seats - humans.Count);
        await hub.Clients.Users(humans.Select(h => h.Id.Value).ToList())
            .QueueStatus(new QueueStatusDto(false, 0, 0, mode.Name, mode.FullTable));
        await room.StartAsync();
    }

    /// <summary>Tells everyone waiting in a queue how many are there and how long until bots join. At most once a second.</summary>
    private async Task AnnounceAsync(QueueMode mode, bool force)
    {
        List<string> ids;
        QueueStatusDto status;
        lock (_lock)
        {
            var queue = _queues[mode.Name];
            if (queue.Count == 0) return;

            var auto = options.Value.BotFillAfterSeconds > 0;
            var waited = time.GetUtcNow() - queue[0].Since;
            var left = auto ? Math.Max(0, (int)Math.Ceiling(options.Value.BotFillAfterSeconds - waited.TotalSeconds)) : -1;
            if (!force && left == _lastAnnouncedSecond[mode.Name]) return;

            _lastAnnouncedSecond[mode.Name] = left;
            ids = queue.Select(w => w.Player.Id.Value).ToList();
            var botsToAdd = Math.Max(0, Math.Max(mode.MinTable, Math.Min(queue.Count, mode.FullTable)) - Math.Min(queue.Count, mode.FullTable));
            status = new QueueStatusDto(true, queue.Count, left, mode.Name, mode.FullTable, botsToAdd);
        }

        await hub.Clients.Users(ids).QueueStatus(status);
    }

    private static QueueMode ModeNamed(string name) => Modes.First(m => m.Name == name);

    private sealed record Waiting(PlayerInfo Player, DateTimeOffset Since);
}

/// <param name="Name">Same names as private-game modes ("quick", "cards").</param>
/// <param name="FullTable">A queue this long starts a game straight away.</param>
/// <param name="MinTable">Seats when the game starts short of a full table; bots fill whatever humans don't.</param>
public sealed record QueueMode(string Name, int FullTable, int MinTable);
