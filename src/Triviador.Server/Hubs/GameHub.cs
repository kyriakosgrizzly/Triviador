using Microsoft.AspNetCore.SignalR;
using Triviador.Domain;
using Triviador.Domain.Matches;
using Triviador.Domain.Questions;
using Triviador.Server.Rooms;

namespace Triviador.Server.Hubs;

/// <summary>
/// The only entry point for players. Every method maps to one domain command; the room
/// serializes commands per match and broadcasts the result to everyone in it.
/// </summary>
public sealed class GameHub(MatchRegistry registry, Matchmaker matchmaker) : Hub<IGameClient>
{
    private const string NameKey = "name";

    public override async Task OnConnectedAsync()
    {
        if (Context.UserIdentifier is null)
        {
            Context.Abort();
            return;
        }

        var name = Context.GetHttpContext()?.Request.Query["name"].ToString().Trim() ?? "";
        Context.Items[NameKey] = string.IsNullOrEmpty(name) ? "Player" : name[..Math.Min(name.Length, 20)];

        // Reconnect: put the player straight back into their running match.
        if (registry.FindActiveRoomFor(MeId) is { } room)
            await Clients.Caller.MatchJoined(room.Map, await room.GetStateAsync());
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.UserIdentifier is not null)
            await matchmaker.LeaveAsync(MeId);
    }

    public async Task PlayVsBots()
    {
        EnsureNotInMatch();
        await matchmaker.LeaveAsync(MeId);
        var room = registry.Create([Me], botCount: 2);
        await room.StartAsync();
    }

    public Task FindMatch()
    {
        EnsureNotInMatch();
        return matchmaker.JoinAsync(Me);
    }

    public Task CancelSearch() => matchmaker.LeaveAsync(MeId);

    /// <summary>Leave the current match. If it's still running, a bot plays your turns from now on.</summary>
    public void LeaveMatch() => registry.Leave(MeId);

    public Task PlaceCastle(int territoryId) => Play(m => m.PlaceCastle(MeId, territoryId));

    public Task ClaimTerritory(int territoryId) => Play(m => m.ClaimTerritory(MeId, territoryId));

    public Task Attack(int territoryId) => Play(m => m.Attack(MeId, territoryId));

    public Task AnswerChoice(int optionIndex) => Play(m => m.SubmitAnswer(MeId, new ChoiceAnswer(optionIndex)));

    public Task AnswerNumber(double value) => Play(m => m.SubmitAnswer(MeId, new NumericAnswer(value)));

    private PlayerId MeId => new(Context.UserIdentifier!);

    private PlayerInfo Me => new(MeId, (string)Context.Items[NameKey]!);

    private void EnsureNotInMatch()
    {
        if (registry.FindActiveRoomFor(MeId) is not null)
            throw new HubException("You are already in a match.");
    }

    private async Task Play(Action<Match> command)
    {
        var room = registry.FindActiveRoomFor(MeId) ?? throw new HubException("You are not in a match.");
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
