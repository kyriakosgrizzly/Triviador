using Microsoft.AspNetCore.SignalR;
using Triviador.Domain;
using Triviador.Domain.Cards;
using Triviador.Domain.Conquest;
using Triviador.Domain.Matches;
using Triviador.Server.Contracts;
using Triviador.Server.Hubs;

namespace Triviador.Server.Rooms;

/// <summary>
/// Private games: a host creates one and shares its link, friends join with the code,
/// and the host starts it. Empty seats get bots. Lobbies live in memory and expire if never started.
/// </summary>
public sealed class LobbyRegistry(
    MatchRegistry matches,
    IHubContext<GameHub, IGameClient> hub,
    ConnectionTracker connections,
    TimeProvider time)
{
    /// <summary>How long a player whose tabs all closed keeps their seat (enough for a page refresh).</summary>
    private static readonly TimeSpan ReconnectGrace = TimeSpan.FromSeconds(15);

    // No 0/O or 1/I, so codes survive being read out loud.
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 6;
    private static readonly TimeSpan Expiry = TimeSpan.FromMinutes(30);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Lobby> _lobbies = new();
    private readonly Dictionary<PlayerId, string> _lobbyOf = new();

    public bool IsInLobby(PlayerId player)
    {
        lock (_lock) return _lobbyOf.ContainsKey(player);
    }

    public async Task<string> CreateAsync(PlayerInfo host)
    {
        await LeaveAsync(host.Id);

        Lobby lobby;
        lock (_lock)
        {
            string code;
            do code = string.Concat(Enumerable.Range(0, CodeLength).Select(_ => CodeAlphabet[Random.Shared.Next(CodeAlphabet.Length)]));
            while (_lobbies.ContainsKey(code));

            lobby = new Lobby(code, time.GetUtcNow());
            lobby.Players.Add(host);
            _lobbies[code] = lobby;
            _lobbyOf[host.Id] = code;
        }

        await BroadcastAsync(lobby);
        return lobby.Code;
    }

    public async Task JoinAsync(PlayerInfo player, string code)
    {
        code = Normalize(code);
        Lobby lobby;
        lock (_lock)
        {
            if (!_lobbies.TryGetValue(code, out lobby!))
                throw new HubException("That game doesn't exist any more. Ask your friend for a new link.");

            if (_lobbyOf.TryGetValue(player.Id, out var current) && current == code)
            {
                // Already in it (e.g. page reload): just refresh the name.
                var i = lobby.Players.FindIndex(p => p.Id == player.Id);
                lobby.Players[i] = player;
            }
            else
            {
                if (lobby.Players.Count >= lobby.Seats)
                    throw new HubException("That game is full.");
                if (_lobbyOf.ContainsKey(player.Id))
                    RemoveLocked(player.Id);
                lobby.Players.Add(player);
                _lobbyOf[player.Id] = code;
            }
        }

        await BroadcastAsync(lobby);
    }

    public async Task LeaveAsync(PlayerId player)
    {
        Lobby? lobby;
        lock (_lock)
        {
            lobby = RemoveLocked(player);
        }

        if (lobby is null) return;
        await hub.Clients.User(player.Value).LobbyUpdated(null);
        if (lobby.Players.Count > 0) await BroadcastAsync(lobby);
    }

    /// <summary>Host only: choose Quick match (3 seats), Conquest (2–4 seats) or Quiz Cards (2–4 seats).</summary>
    public async Task SetModeAsync(PlayerId host, string mode, int seats)
    {
        Lobby lobby;
        lock (_lock)
        {
            if (!_lobbyOf.TryGetValue(host, out var code) || !_lobbies.TryGetValue(code, out lobby!))
                throw new HubException("You're not in a private game.");
            if (lobby.Host != host)
                throw new HubException("Only the host can change the game.");

            var chosen = mode?.Trim().ToLowerInvariant() switch
            {
                LobbyModes.Conquest => LobbyModes.Conquest,
                LobbyModes.Cards => LobbyModes.Cards,
                _ => LobbyModes.Quick,
            };
            var newSeats = chosen switch
            {
                LobbyModes.Conquest => Math.Clamp(seats, ConquestGame.MinPlayers, ConquestGame.MaxPlayers),
                LobbyModes.Cards => Math.Clamp(seats, CardsGame.MinPlayers, CardsGame.MaxPlayers),
                _ => Match.PlayerCount,
            };
            if (lobby.Players.Count > newSeats)
                throw new HubException($"There are already {lobby.Players.Count} players here.");

            lobby.Mode = chosen;
            lobby.Seats = newSeats;
        }

        await BroadcastAsync(lobby);
    }

    public async Task StartAsync(PlayerId host)
    {
        List<PlayerInfo> humans;
        string mode;
        int seats;
        lock (_lock)
        {
            if (!_lobbyOf.TryGetValue(host, out var code) || !_lobbies.TryGetValue(code, out var lobby))
                throw new HubException("You're not in a private game.");
            if (lobby.Host != host)
                throw new HubException("Only the host can start the game.");

            humans = lobby.Players.ToList();
            mode = lobby.Mode;
            seats = lobby.Seats;
            _lobbies.Remove(code);
            foreach (var p in humans) _lobbyOf.Remove(p.Id);
        }

        await hub.Clients.Users(humans.Select(h => h.Id.Value).ToList()).LobbyUpdated(null);
        IGameRoom room = mode switch
        {
            LobbyModes.Conquest => matches.CreateConquest(humans, seats),
            LobbyModes.Cards => matches.CreateCards(humans, seats),
            _ => matches.Create(humans, botCount: Match.PlayerCount - humans.Count),
        };
        await room.StartAsync();
    }

    /// <summary>Drops players who closed the page (after a grace period) and lobbies nobody started.</summary>
    public async Task TickAsync()
    {
        var now = time.GetUtcNow();
        List<Lobby> expired;
        List<PlayerId> gone;
        lock (_lock)
        {
            expired = _lobbies.Values.Where(l => now - l.CreatedAt > Expiry).ToList();
            foreach (var l in expired)
            {
                _lobbies.Remove(l.Code);
                foreach (var p in l.Players) _lobbyOf.Remove(p.Id);
            }

            gone = _lobbyOf.Keys.Where(p => connections.GoneFor(p, ReconnectGrace)).ToList();
        }

        foreach (var l in expired)
            await hub.Clients.Users(l.Players.Select(p => p.Id.Value).ToList()).LobbyUpdated(null);

        foreach (var p in gone)
            await LeaveAsync(p);
    }

    private Lobby? RemoveLocked(PlayerId player)
    {
        if (!_lobbyOf.Remove(player, out var code) || !_lobbies.TryGetValue(code, out var lobby))
            return null;

        lobby.Players.RemoveAll(p => p.Id == player);
        if (lobby.Players.Count == 0)
            _lobbies.Remove(code);
        return lobby;
    }

    private Task BroadcastAsync(Lobby lobby)
    {
        LobbyDto dto;
        List<string> ids;
        lock (_lock)
        {
            dto = new LobbyDto(
                lobby.Code,
                lobby.Host,
                lobby.Players.Select(p => new LobbyPlayerDto(p.Id, p.Name)).ToList(),
                lobby.Seats,
                lobby.Mode);
            ids = lobby.Players.Select(p => p.Id.Value).ToList();
        }
        return hub.Clients.Users(ids).LobbyUpdated(dto);
    }

    private static string Normalize(string? code) => (code ?? "").Trim().ToUpperInvariant();

    private sealed class Lobby(string code, DateTimeOffset createdAt)
    {
        public string Code { get; } = code;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public List<PlayerInfo> Players { get; } = [];
        public string Mode { get; set; } = LobbyModes.Quick;
        public int Seats { get; set; } = Match.PlayerCount;

        /// <summary>The first player is the host. If the host leaves, the next player takes over.</summary>
        public PlayerId Host => Players[0].Id;
    }
}

public static class LobbyModes
{
    public const string Quick = "quick";
    public const string Conquest = "conquest";
    public const string Cards = "cards";
}
