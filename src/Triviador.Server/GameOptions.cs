namespace Triviador.Server;

public sealed class GameOptions
{
    public const string Section = "Game";

    /// <summary>
    /// If above 0, players left waiting in a matchmaking queue this long get bots automatically.
    /// 0 (default): nobody is auto-filled — anyone waiting can press "Add bots and start" instead.
    /// </summary>
    public int BotFillAfterSeconds { get; set; }

    /// <summary>How long a human may take on their turn before a bot moves for them.</summary>
    public int TurnTimeoutSeconds { get; set; } = 20;

    /// <summary>How long finished matches stay in memory, so players can still see the result after reconnecting.</summary>
    public int KeepFinishedMatchMinutes { get; set; } = 10;

    /// <summary>Scales bot thinking time. 1 = human-like, 0.05 = near-instant (handy for testing).</summary>
    public double BotSpeedFactor { get; set; } = 1.0;
}
