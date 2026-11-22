using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Triviador.Server.Auth;

/// <summary>Settings section "Auth".</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// True when Azure Container Apps' built-in authentication ("Easy Auth") sits in front of the app.
    /// Only then are the X-MS-CLIENT-PRINCIPAL headers trusted: the platform strips any copy a client
    /// sends and writes its own. Locally there is no such proxy, so a browser could forge them. Bicep sets it.
    /// </summary>
    public bool EasyAuth { get; set; }

    /// <summary>How long a game token from /api/me stays valid. The browser fetches a fresh one on every (re)connect.</summary>
    public int TokenMinutes { get; set; } = 60;
}

/// <summary>A player signed in through Easy Auth (Google).</summary>
public sealed record SignedInUser(string PlayerId, string Name, string Provider, string? Email);

/// <summary>What /api/me returns. <see cref="Token"/> goes back to the hub as the SignalR access token.</summary>
public sealed record MeDto(bool AuthEnabled, bool SignedIn, string? PlayerId, string? Name, string? Email, string? Provider, string? Token);

/// <summary>
/// Works out who a request is.
///
/// Signed-in players: Easy Auth checks the Google login and passes the user to the app in the
/// X-MS-CLIENT-PRINCIPAL header. /api/me reads it and issues a short-lived token signed with ASP.NET Core
/// Data Protection; the browser hands that token to SignalR (accessTokenFactory), so the game connection
/// doesn't depend on headers being added to WebSocket requests. Player id: "google-&lt;Google subject id&gt;",
/// the same on every device. Name: the Google profile name.
///
/// Guests: the browser's random id and the name they typed, as before. Ids starting with "google-" are
/// refused for guests so nobody can pose as a signed-in player.
/// </summary>
public sealed partial class PlayerIdentity(IDataProtectionProvider protection, IOptions<AuthOptions> options, TimeProvider time)
{
    public const string SignedInPrefix = "google-";
    private const string PrincipalHeader = "X-MS-CLIENT-PRINCIPAL";
    private const string PrincipalIdHeader = "X-MS-CLIENT-PRINCIPAL-ID";
    private const string ProviderHeader = "X-MS-CLIENT-PRINCIPAL-IDP";

    private readonly ITimeLimitedDataProtector _tokens =
        protection.CreateProtector("Triviador.PlayerToken.v1").ToTimeLimitedDataProtector();

    public bool AuthEnabled => options.Value.EasyAuth;

    // ------------------------------------------------------------------ Easy Auth (HTTP requests)

    /// <summary>The Easy Auth user on this request, if auth is on and someone is signed in.</summary>
    public SignedInUser? FromEasyAuth(HttpRequest request)
    {
        if (!AuthEnabled) return null;
        var encoded = request.Headers[PrincipalHeader].ToString();
        if (string.IsNullOrEmpty(encoded)) return null;
        return ParsePrincipal(encoded, request.Headers[PrincipalIdHeader].ToString(), request.Headers[ProviderHeader].ToString());
    }

    /// <summary>Reads the base64 JSON principal Easy Auth sends: { auth_typ, claims: [{ typ, val }], name_typ, role_typ }.</summary>
    public static SignedInUser? ParsePrincipal(string base64Json, string? idHeader = null, string? providerHeader = null)
    {
        Dictionary<string, string> claims;
        string provider;
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(base64Json)));
            var root = doc.RootElement;
            provider = root.TryGetProperty("auth_typ", out var typ) ? typ.GetString() ?? "" : "";
            claims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("claims", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var c in list.EnumerateArray())
                {
                    var t = c.TryGetProperty("typ", out var tt) ? tt.GetString() : null;
                    var v = c.TryGetProperty("val", out var vv) ? vv.GetString() : null;
                    if (t is not null && v is not null) claims.TryAdd(t, v);
                }
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(providerHeader)) provider = providerHeader;
        string? Claim(params string[] types) => types.Select(t => claims.GetValueOrDefault(t)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var subject = !string.IsNullOrWhiteSpace(idHeader)
            ? idHeader
            : Claim("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier", "sub");
        if (string.IsNullOrWhiteSpace(subject)) return null;

        var safeSubject = NotIdChars().Replace(subject, "");
        if (safeSubject.Length == 0) return null;
        var playerId = SignedInPrefix + safeSubject[..Math.Min(safeSubject.Length, 64 - SignedInPrefix.Length)];

        var email = Claim("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress", "email");
        var name = Claim("name", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name", "given_name",
                       "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname")
                   ?? email?.Split('@')[0]
                   ?? "Player";
        return new SignedInUser(playerId, CleanName(name), string.IsNullOrWhiteSpace(provider) ? "google" : provider, email);
    }

    // ------------------------------------------------------------------ game tokens

    public string IssueToken(SignedInUser user) =>
        _tokens.Protect(user.PlayerId + "\n" + user.Name, time.GetUtcNow().AddMinutes(options.Value.TokenMinutes));

    public SignedInUser? FromToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var parts = _tokens.Unprotect(token, out var expires).Split('\n', 2);
            if (expires < time.GetUtcNow() || parts.Length != 2 || !parts[0].StartsWith(SignedInPrefix, StringComparison.Ordinal)) return null;
            return new SignedInUser(parts[0], parts[1], "google", null);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null; // tampered, expired, or signed before the server restarted
        }
    }

    // ------------------------------------------------------------------ SignalR connections

    /// <summary>The player behind a hub connection: signed in (valid token) or guest (query playerId + name).</summary>
    public (string Id, string Name, bool SignedIn)? ForConnection(HttpContext? http)
    {
        if (http is null) return null;
        var request = http.Request;

        // SignalR sends the access token as ?access_token= on WebSockets/SSE and as a Bearer header otherwise.
        var token = request.Query["access_token"].ToString();
        if (string.IsNullOrEmpty(token) && request.Headers.Authorization.ToString() is { } auth
            && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = auth["Bearer ".Length..].Trim();

        if (FromToken(token) is { } user) return (user.PlayerId, user.Name, true);

        var guestId = request.Query["playerId"].ToString();
        if (!IsValidGuestId(guestId)) return null;
        var name = CleanName(request.Query["name"].ToString());
        return (guestId, name, false);
    }

    public static bool IsValidGuestId(string? id) =>
        !string.IsNullOrEmpty(id) && GuestId().IsMatch(id) && !id.StartsWith(SignedInPrefix, StringComparison.OrdinalIgnoreCase);

    public static string CleanName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        return trimmed.Length == 0 ? "Player" : trimmed[..Math.Min(trimmed.Length, 20)];
    }

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex GuestId();

    [GeneratedRegex("[^A-Za-z0-9-]")]
    private static partial Regex NotIdChars();
}
