using Microsoft.AspNetCore.SignalR;
using Triviador.Server.Auth;

namespace Triviador.Server.Hubs;

/// <summary>
/// Tells SignalR who is on a connection: a Google-signed-in player (from the token /api/me issued) or a
/// guest (the random id the browser keeps in localStorage). See <see cref="PlayerIdentity"/>.
/// </summary>
public sealed class PlayerIdProvider(PlayerIdentity identity) : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        identity.ForConnection(connection.GetHttpContext())?.Id;
}
