using Microsoft.AspNetCore.SignalR;
using Triviador.Server.Auth;
using Triviador.Domain;
using Triviador.Domain.Cards;
using Triviador.Domain.Conquest;
using Triviador.Domain.Matches;
using Triviador.Domain.Questions;
using Triviador.Server.Rooms;

namespace Triviador.Server.Hubs;

/// <summary>
/// The only entry point for players. Every method maps to one domain command; the room
/// serializes commands per match and broadcasts the result to everyone in it.
/// </summary>
public sealed class GameHub(
    MatchRegistry registry,
    Matchmaker matchmaker,
    LobbyRegistry lobbies,
    ConnectionTracker connections,
    PlayerIdentity identity) : Hub<IGameClient>
{
    private const string NameKey = "name";

    public override async Task OnConnectedAsync()
    {
        if (Context.UserIdentifier is null)
        {
            Context.Abort();
            return;
        }

        // Signed-in players play under their Google name; guests under the name they typed.
        Context.Items[NameKey] = identity.ForConnection(Context.GetHttpContext())?.Name ?? "Player";
        connections.Connected(MeId);

        // Reconnect: put the player straight back into their running match.
        if (registry.FindActiveRoomFor(MeId) is { } room)
            await room.SendJoinedAsync(Clients.Caller, MeId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.UserIdentifier is null) return;

        connections.Disconnected(MeId);
        await matchmaker.LeaveAsync(MeId);
        // Private games keep the seat for a short grace period (LobbyRegistry.TickAsync), so a refresh doesn't lose it.
    }

    // ------------------------------------------------------------------ starting a game
    // Quiz Cards is the only game players can start. The Quick match and Conquest engines are still in the
    // code base (Domain/Matches, Domain/Conquest, MatchRoom, ConquestRoom) but nothing here starts them.

    /// <summary>Queue for a public Quiz Cards game: starts at once with 4 players, otherwise bots fill up after a short wait.</summary>
    public async Task FindCardsMatch()
    {
        EnsureNotInMatch();
        await lobbies.LeaveAsync(MeId);
        await matchmaker.JoinAsync(Me, Matchmaker.Cards);
    }

    public Task CancelSearch() => matchmaker.LeaveAsync(MeId);

    /// <summary>Anyone waiting in a public queue: start now with everyone who's there, bots in the empty seats.</summary>
    public Task StartQueueWithBots() => matchmaker.StartWithBotsAsync(MeId);

    /// <summary>Start Quiz Cards against bots. <paramref name="players"/> is the total, you included (2–4).</summary>
    public async Task PlayCardsVsBots(int players)
    {
        EnsureNotInMatch();
        await matchmaker.LeaveAsync(MeId);
        await lobbies.LeaveAsync(MeId);
        var room = registry.CreateCards([Me], Math.Clamp(players, CardsGame.MinPlayers, CardsGame.MaxPlayers));
        await room.StartAsync();
    }

    /// <summary>Create a private Quiz Cards game for 3. Returns its code; the browser turns it into a share link.</summary>
    public async Task<string> CreatePrivateGame()
    {
        EnsureNotInMatch();
        await matchmaker.LeaveAsync(MeId);
        var code = await lobbies.CreateAsync(Me);
        await lobbies.SetModeAsync(MeId, LobbyModes.Cards, 3);
        return code;
    }

    /// <summary>Create a private Quiz Cards game for <paramref name="seats"/> players (2–4). <paramref name="mode"/> is ignored: cards only.</summary>
    public async Task<string> CreatePrivateGameFor(string mode, int seats)
    {
        var code = await CreatePrivateGame();
        await lobbies.SetModeAsync(MeId, LobbyModes.Cards, seats);
        return code;
    }

    public async Task JoinPrivateGame(string code)
    {
        EnsureNotInMatch();
        await matchmaker.LeaveAsync(MeId);
        await lobbies.JoinAsync(Me, code);
    }

    public Task LeavePrivateGame() => lobbies.LeaveAsync(MeId);

    public Task StartPrivateGame() => lobbies.StartAsync(MeId);

    /// <summary>Host only: change the table size (2–4). Quiz Cards is the only mode now, whatever <paramref name="mode"/> says.</summary>
    public Task SetPrivateGameMode(string mode, int seats) => lobbies.SetModeAsync(MeId, LobbyModes.Cards, seats);

    /// <summary>Quit the current match. A bot takes over your seat; if nobody human is left, the match ends.</summary>
    public async Task LeaveMatch()
    {
        var room = registry.FindActiveRoomFor(MeId);
        registry.Leave(MeId);
        if (room is not null)
            await room.LeaveAsync(MeId);
    }

    // ------------------------------------------------------------------ playing

    public Task PlaceCastle(int territoryId) => Play(m => m.PlaceCastle(MeId, territoryId));

    public Task ClaimTerritory(int territoryId) => Play(m => m.ClaimTerritory(MeId, territoryId));

    public Task Attack(int territoryId) => Play(m => m.Attack(MeId, territoryId));

    public Task AnswerChoice(int optionIndex) => Answer(new ChoiceAnswer(optionIndex));

    public Task AnswerNumber(double value) => Answer(new NumericAnswer(value));

    // ------------------------------------------------------------------ Conquest

    public Task ConquestPickColor(string color) => PlayConquest(g => g.PickColor(MeId, color));

    public Task ConquestPickCapital(int region) => PlayConquest(g => g.PickCapital(MeId, region));

    /// <summary>Submit this round's orders and mark yourself ready. Sending again replaces them.</summary>
    public Task ConquestSubmitPlan(List<Order> orders) => PlayConquest(g => g.SubmitPlan(MeId, orders ?? []));

    // ------------------------------------------------------------------ Quiz Cards

    /// <summary>Play a card from your hand. Wilds need a category ("Geography"…); a Duel also needs an opponent.</summary>
    public Task CardsPlay(int cardId, CardCategory? category, string? opponent) =>
        PlayCards(g => g.Play(MeId, cardId, category, string.IsNullOrEmpty(opponent) ? null : new PlayerId(opponent)));

    public Task CardsDraw() => PlayCards(g => g.Draw(MeId));

    /// <summary>After drawing a card you could play: keep it and end your turn.</summary>
    public Task CardsPass() => PlayCards(g => g.Pass(MeId));

    private async Task PlayCards(Action<CardsGame> command)
    {
        if (registry.FindActiveRoomFor(MeId) is not CardsRoom room)
            throw new HubException("You are not in a Quiz Cards game.");
        try
        {
            await room.ExecuteAsync(command);
        }
        catch (GameRuleException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    private Task Answer(Answer answer) => registry.FindActiveRoomFor(MeId) switch
    {
        ConquestRoom => PlayConquest(g => g.SubmitAnswer(MeId, answer)),
        CardsRoom => PlayCards(g => g.SubmitAnswer(MeId, answer)),
        _ => Play(m => m.SubmitAnswer(MeId, answer)),
    };

    private async Task PlayConquest(Action<ConquestGame> command)
    {
        if (registry.FindActiveRoomFor(MeId) is not ConquestRoom room)
            throw new HubException("You are not in a Conquest game.");
        try
        {
            await room.ExecuteAsync(command);
        }
        catch (GameRuleException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    private PlayerId MeId => new(Context.UserIdentifier!);

    private PlayerInfo Me => new(MeId, (string)Context.Items[NameKey]!);

    private void EnsureNotInMatch()
    {
        if (registry.FindActiveRoomFor(MeId) is not null)
            throw new HubException("You are already in a match. Leave it first.");
    }

    private async Task Play(Action<Match> command)
    {
        if (registry.FindActiveRoomFor(MeId) is not MatchRoom room)
            throw new HubException("You are not in a match.");
        try
        {
            await room.ExecuteAsync(command);
        }
        catch (GameRuleException ex)
        {
            throw new HubException(ex.Message);
        }
    }
}
