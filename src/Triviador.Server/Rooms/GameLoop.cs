namespace Triviador.Server.Rooms;

/// <summary>Drives time in the game: question deadlines, bot moves, AFK takeovers and matchmaking.</summary>
public sealed class GameLoop(MatchRegistry registry, Matchmaker matchmaker, ILogger<GameLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await matchmaker.TickAsync();
                await registry.TickAllAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Game loop tick failed");
            }
        }
    }
}
