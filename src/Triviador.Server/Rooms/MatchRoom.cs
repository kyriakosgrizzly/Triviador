using Microsoft.AspNetCore.SignalR;
using Triviador.Domain;
using Triviador.Domain.Bots;
using Triviador.Domain.Map;
using Triviador.Domain.Matches;
using Triviador.Domain.Questions;
using Triviador.Server.Contracts;
using Triviador.Server.Hubs;

namespace Triviador.Server.Rooms;

/// <summary>
/// Hosts one <see cref="Domain.Matches.Match"/>. All access goes through a per-room async lock,
/// because the engine is single-threaded. Broadcasting happens inside the lock too, so clients
/// always receive updates in order.
/// </summary>
public sealed class MatchRoom
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IHubContext<GameHub, IGameClient> _hub;
    private readonly TimeProvider _time;
    private readonly GameOptions _options;
    private readonly Random _rng;
    private readonly IReadOnlyList<string> _humanIds;

    private readonly Dictionary<PlayerId, BotPlayer> _bots;
    private readonly Dictionary<PlayerId, BotPlayer> _afkStandIns;
    private readonly Dictionary<PlayerId, DateTimeOffset> _botReadyAt = new();

    private DecisionKey _decision;
    private DateTimeOffset _decisionSince;

    public MatchRoom(
        string id,
        IReadOnlyList<PlayerInfo> players,
        IReadOnlyDictionary<PlayerId, double> botAccuracy,
        IHubContext<GameHub, IGameClient> hub,
        TimeProvider time,
        GameOptions options,
        Random rng)
    {
        _hub = hub;
        _time = time;
        _options = options;
        _rng = rng;

        Match = new Match(id, players, Maps.Valley(), new InMemoryQuestionSource(SampleQuestions.All, rng), time);
        Map = ToDto(Match.Map);

        Humans = players.Where(p => !p.IsBot).Select(p => p.Id).ToList();
        _humanIds = Humans.Select(h => h.Value).ToList();
        _bots = players.Where(p => p.IsBot).ToDictionary(p => p.Id, p => new BotPlayer(p.Id, rng, botAccuracy[p.Id]));
        _afkStandIns = Humans.ToDictionary(h => h, h => new BotPlayer(h, rng, accuracy: 0));
        _decisionSince = time.GetUtcNow();
    }

    public string Id => Match.Id;
    public Match Match { get; }
    public MapDto Map { get; }
    public IReadOnlyList<PlayerId> Humans { get; }
    public DateTimeOffset? FinishedAt { get; private set; }

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Match.Start();
            await _hub.Clients.Users(_humanIds).MatchJoined(Map, BuildState());
            await FlushAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ExecuteAsync(Action<Match> command)
    {
        await _gate.WaitAsync();
        try
        {
            command(Match);
        }
        finally
        {
            // Flush even on a rule violation: a late answer can close the question before throwing.
            try
            {
                await FlushAsync();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public async Task<GameStateDto> GetStateAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return BuildState();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Called by the game loop a few times a second: deadlines, bots, AFK humans.</summary>
    public async Task TickAsync()
    {
        if (FinishedAt is not null) return;

        await _gate.WaitAsync();
        try
        {
            var now = _time.GetUtcNow();
            Match.Tick();
            TrackDecision(now);
            DriveBots(now);
            TrackDecision(now);
            CoverAfkHumans(now);
            await FlushAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ bots & AFK

    /// <summary>A "decision point" changes whenever someone new has to act. Bot timers and AFK timers reset then.</summary>
    private void TrackDecision(DateTimeOffset now)
    {
        var key = new DecisionKey(Match.Phase, Match.CurrentTurn, Match.QuestionDeadline, Match.Owners.Count, Match.BattleRound);
        if (key == _decision) return;
        _decision = key;
        _decisionSince = now;
        _botReadyAt.Clear();
    }

    private void DriveBots(DateTimeOffset now)
    {
        foreach (var bot in _bots.Values)
        {
            var pending = bot.Pending(Match);
            if (pending == PendingAction.None)
                continue;

            if (!_botReadyAt.TryGetValue(bot.Id, out var readyAt))
            {
                _botReadyAt[bot.Id] = now + ThinkingTime(pending);
                continue;
            }

            if (now < readyAt)
                continue;

            _botReadyAt.Remove(bot.Id);
            bot.TryAct(Match);
        }
    }

    private void CoverAfkHumans(DateTimeOffset now)
    {
        if (Match.CurrentTurn is not { } turn || !_afkStandIns.TryGetValue(turn, out var standIn))
            return;

        if (now - _decisionSince >= TimeSpan.FromSeconds(_options.TurnTimeoutSeconds))
            standIn.TryTakeTurn(Match);
    }

    private TimeSpan ThinkingTime(PendingAction pending)
    {
        var seconds = pending switch
        {
            PendingAction.Answer when Match.CurrentQuestion?.Kind == QuestionKind.Numeric => 3 + _rng.NextDouble() * 6,
            PendingAction.Answer => 2.5 + _rng.NextDouble() * 4.5,
            _ => 1 + _rng.NextDouble() * 1.5,
        };
        return TimeSpan.FromSeconds(seconds * _options.BotSpeedFactor);
    }

    // ------------------------------------------------------------------ broadcasting

    private async Task FlushAsync()
    {
        var events = Match.DequeueEvents();
        if (events.Count == 0) return;

        if (Match.Phase == MatchPhase.Finished)
            FinishedAt ??= _time.GetUtcNow();

        var dtos = events.Select(e => new EventDto(e.GetType().Name, e)).ToList();
        await _hub.Clients.Users(_humanIds).MatchUpdated(dtos, BuildState());
    }

    private GameStateDto BuildState()
    {
        var snapshot = Match.GetSnapshot();
        var turn = Match.CurrentTurn;

        IReadOnlyList<int> valid = (Match.Phase, turn) switch
        {
            (MatchPhase.CastlePlacement, not null) => Match.GetValidCastleSites(),
            (MatchPhase.ExpansionPicks, { } p) => Match.GetValidClaims(p),
            (MatchPhase.BattleAttack, { } p) => Match.GetValidAttackTargets(p),
            _ => [],
        };

        var players = snapshot.Players.Select((p, seat) => new PlayerStateDto(
            p.Info.Id, p.Info.Name, p.Info.IsBot, seat, p.Score, p.TerritoryCount,
            p.CastleTerritoryId, p.CastleHitPoints, p.Eliminated)).ToList();

        return new GameStateDto(
            snapshot.MatchId,
            snapshot.Phase,
            snapshot.BattleRound,
            MatchOptions.Default.BattleRounds,
            snapshot.CurrentTurn,
            players,
            snapshot.Owners,
            snapshot.Question,
            snapshot.QuestionDeadline,
            snapshot.PendingAnswerers,
            valid,
            Match.Standings,
            _time.GetUtcNow());
    }

    private static MapDto ToDto(GameMap map) => new(
        map.Name,
        map.Territories
            .OrderBy(t => t.Id)
            .Select(t => new TerritoryDto(t.Id, t.Name, t.Value, t.Column, t.Row, map.Neighbors(t.Id).Order().ToList()))
            .ToList());

    private readonly record struct DecisionKey(
        MatchPhase Phase, PlayerId? Turn, DateTimeOffset? Deadline, int OwnedCount, int Round);
}
