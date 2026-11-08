namespace Triviador.Server;

public sealed class GameOptions
{
    public const string Section = "Game";

    /// <summary>How long a lone player waits in the queue before bots fill the empty seats.</summary>
    public int BotFillAfterSeconds { get; set; } = 12;

    /// <summary>How long a human may take on their turn before a bot moves for them.</summary>
    public int TurnTimeoutSeconds { get; set; } = 20;

    /// <summary>How long finished matches stay in memory, so players can still see the result after reconnecting.</summary>
    public int KeepFinishedMatchMinutes { get; set; } = 10;

    /// <summary>Scales bot thinking time. 1 = human-like, 0.05 = near-instant (handy for testing).</summary>
    public double BotSpeedFactor { get; set; } = 1.0;
}
