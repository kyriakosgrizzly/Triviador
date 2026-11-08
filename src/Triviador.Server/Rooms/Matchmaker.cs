using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Triviador.Domain;
using Triviador.Domain.Matches;
using Triviador.Server.Contracts;
using Triviador.Server.Hubs;

namespace Triviador.Server.Rooms;

/// <summary>
/// First-come queue: three humans start a match together; anyone left waiting longer than
/// <see cref="GameOptions.BotFillAfterSeconds"/> gets bots in the empty seats.
/// Rating-based matching arrives with ranked play (milestone 5).
/// </summary>
public sealed class Matchmaker(
    MatchRegistry registry,
    IHubContext<GameHub, IGameClient> hub,
    TimeProvider time,
    IOptions<GameOptions> options)
{
    private readonly Lock _lock = new();
    private readonly List<Waiting> _queue = [];
    private int _lastAnnouncedSecond = -1;

    public async Task JoinAsync(PlayerInfo player)
    {
        lock (_lock)
        {
            if (_queue.All(w => w.Player.Id != player.Id))
                _queue.Add(new Waiting(player, time.GetUtcNow()));
        }
        await AnnounceAsync(force: true);
    }

    public async Task LeaveAsync(PlayerId player)
    {
        bool removed;
        lock (_lock)
            removed = _queue.RemoveAll(w => w.Player.Id == player) > 0;

        if (removed)
        {
            await hub.Clients.User(player.Value).QueueStatus(new QueueStatusDto(false, 0, 0));
            await AnnounceAsync(force: true);
        }
    }

    public async Task TickAsync()
    {
        var groups = new List<List<PlayerInfo>>();
        var now = time.GetUtcNow();
        var fillAfter = TimeSpan.FromSeconds(options.Value.BotFillAfterSeconds);

        lock (_lock)
        {
            while (_queue.Count >= Match.PlayerCount)
            {
                groups.Add(_queue.Take(Match.PlayerCount).Select(w => w.Player).ToList());
                _queue.RemoveRange(0, Match.PlayerCount);
            }

            if (_queue.Count > 0 && now - _queue[0].Since >= fillAfter)
            {
                groups.Add(_queue.Select(w => w.Player).ToList());
                _queue.Clear();
            }
        }

        foreach (var humans in groups)
        {
            var room = registry.Create(humans, botCount: Match.PlayerCount - humans.Count);
            await hub.Clients.Users(humans.Select(h => h.Id.Value).ToList()).QueueStatus(new QueueStatusDto(false, 0, 0));
            await room.StartAsync();
        }

        await AnnounceAsync(force: groups.Count > 0);
    }

    /// <summary>Tells everyone waiting how many are queued and how long until bots join. At most once a second.</summary>
    private async Task AnnounceAsync(bool force)
    {
        List<string> ids;
        QueueStatusDto status;
        lock (_lock)
        {
            if (_queue.Count == 0) return;

            var waited = time.GetUtcNow() - _queue[0].Since;
            var left = Math.Max(0, (int)Math.Ceiling(options.Value.BotFillAfterSeconds - waited.TotalSeconds));
            if (!force && left == _lastAnnouncedSecond) return;

            _lastAnnouncedSecond = left;
            ids = _queue.Select(w => w.Player.Id.Value).ToList();
            status = new QueueStatusDto(true, _queue.Count, left);
        }

        await hub.Clients.Users(ids).QueueStatus(status);
    }

    private sealed record Waiting(PlayerInfo Player, DateTimeOffset Since);
}
