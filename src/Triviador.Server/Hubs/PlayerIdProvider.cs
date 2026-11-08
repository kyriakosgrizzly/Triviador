using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;

namespace Triviador.Server.Hubs;

/// <summary>
/// Dev-only identity: the browser generates a player id, keeps it in localStorage and sends it
/// in the query string. Anyone can claim any id, so this is replaced by Entra External ID
/// (a real signed-in user) before ranked play matters.
/// </summary>
public sealed partial class PlayerIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
    {
        var id = connection.GetHttpContext()?.Request.Query["playerId"].ToString();
        return IsValid(id) ? id : null;
    }

    public static bool IsValid(string? id) => !string.IsNullOrEmpty(id) && ValidId().IsMatch(id);

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex ValidId();
}
