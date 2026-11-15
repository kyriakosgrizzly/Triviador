using Triviador.Domain;

namespace Triviador.Server.Hubs;

/// <summary>
/// Counts open connections per player, so "disconnected" means every tab is gone,
/// and remembers when that happened. A page refresh closes one connection and opens
/// another within a second or two, so callers give players a grace period before acting.
/// </summary>
public sealed class ConnectionTracker(TimeProvider time)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<PlayerId, int> _open = new();
    private readonly Dictionary<PlayerId, DateTimeOffset> _offlineSince = new();

    public void Connected(PlayerId player)
    {
        lock (_lock)
        {
            _open[player] = _open.GetValueOrDefault(player) + 1;
            _offlineSince.Remove(player);
        }
    }

    public void Disconnected(PlayerId player)
    {
        lock (_lock)
        {
            var left = _open.GetValueOrDefault(player) - 1;
            if (left > 0)
            {
                _open[player] = left;
                return;
            }

            _open.Remove(player);
            _offlineSince[player] = time.GetUtcNow();
        }
    }

    /// <summary>True if the player has had no open connection for at least <paramref name="grace"/>.</summary>
    public bool GoneFor(PlayerId player, TimeSpan grace)
    {
        lock (_lock)
        {
            return !_open.ContainsKey(player)
                && (!_offlineSince.TryGetValue(player, out var since) || time.GetUtcNow() - since >= grace);
        }
    }
}
