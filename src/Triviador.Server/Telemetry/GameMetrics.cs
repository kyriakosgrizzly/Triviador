using System.Diagnostics.Metrics;
using Triviador.Domain.Cards;
using Triviador.Server.Hubs;
using Triviador.Server.Rooms;

namespace Triviador.Server.Telemetry;

/// <summary>
/// Game numbers as .NET metrics (System.Diagnostics.Metrics). OpenTelemetry exports them to Application
/// Insights, where they land in the <c>customMetrics</c> table (see docs/INSIGHTS.md for KQL). Without
/// Application Insights configured they cost next to nothing: nobody listens to the meter.
/// </summary>
public sealed class GameMetrics
{
    public const string MeterName = "Triviador.Game";

    private readonly Meter _meter;
    private readonly Counter<long> _gamesStarted;
    private readonly Counter<long> _gamesEnded;
    private readonly Counter<long> _questions;
    private readonly Histogram<double> _gameMinutes;
    private readonly Histogram<int> _gameTurns;

    public GameMetrics(IMeterFactory meters)
    {
        _meter = meters.Create(MeterName);
        _gamesStarted = _meter.CreateCounter<long>("triviador.games.started", unit: "{game}", description: "Quiz Cards games started");
        _gamesEnded = _meter.CreateCounter<long>("triviador.games.ended", unit: "{game}", description: "Games that ended, by how (finished/abandoned) and who won (human/bot)");
        _questions = _meter.CreateCounter<long>("triviador.questions", unit: "{question}", description: "Card questions by colour and outcome (right, stolen, wrong, timeout)");
        _gameMinutes = _meter.CreateHistogram<double>("triviador.game.duration", unit: "min", description: "Length of finished games");
        _gameTurns = _meter.CreateHistogram<int>("triviador.game.turns", unit: "{turn}", description: "Turns in finished games");
    }

    /// <summary>Live gauges, read whenever metrics are exported (once a minute).</summary>
    public void Observe(MatchRegistry registry, ConnectionTracker connections)
    {
        _meter.CreateObservableGauge("triviador.games.active", () => registry.ActiveRooms, unit: "{game}", description: "Games in progress");
        _meter.CreateObservableGauge("triviador.players.online", () => connections.OnlineCount, unit: "{player}", description: "Players with the page open");
    }

    public void GameStarted(int humans, int seats) =>
        _gamesStarted.Add(1, new("humans", humans), new("seats", seats));

    public void QuestionResolved(CardCategory colour, string outcome) =>
        _questions.Add(1, new("colour", colour.ToString()), new("outcome", outcome));

    public void GameFinished(bool humanWon, int turns, TimeSpan length)
    {
        _gamesEnded.Add(1, new("how", "finished"), new("winner", humanWon ? "human" : "bot"));
        _gameTurns.Record(turns);
        _gameMinutes.Record(length.TotalMinutes);
    }

    public void GameAbandoned() => _gamesEnded.Add(1, new("how", "abandoned"), new("winner", "none"));
}
