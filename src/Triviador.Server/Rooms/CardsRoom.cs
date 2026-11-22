using Microsoft.AspNetCore.SignalR;
using Triviador.Domain;
using Triviador.Domain.Cards;
using Triviador.Domain.Questions;
using Triviador.Server.Contracts;
using Triviador.Server.Hubs;
using Triviador.Server.Telemetry;

namespace Triviador.Server.Rooms;

/// <summary>
/// Hosts one <see cref="CardsGame"/>: serializes access with a per-room lock, drives bots with
/// human-like thinking time and sends every human their own view (only you see your hand).
/// The engine's timers handle slow players (a card is drawn for them, questions time out).
/// A player who leaves is replaced by a bot straight away.
/// </summary>
public sealed class CardsRoom : IGameRoom
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IHubContext<GameHub, IGameClient> _hub;
    private readonly TimeProvider _time;
    private readonly GameOptions _options;
    private readonly Random _rng;
    private readonly GameMetrics _metrics;
    private DateTimeOffset _startedAt;

    private readonly Dictionary<PlayerId, CardsBot> _bots;
    private readonly Dictionary<PlayerId, CardsBot> _standIns;
    private readonly HashSet<PlayerId> _left = [];

    // When each bot will act on what it currently intends to do.
    private readonly Dictionary<PlayerId, (object Key, DateTimeOffset ReadyAt)> _botPlan = new();

    public CardsRoom(
        string id,
        IReadOnlyList<PlayerInfo> players,
        IReadOnlyDictionary<PlayerId, double> botAccuracy,
        IReadOnlyList<Question> questions,
        IHubContext<GameHub, IGameClient> hub,
        TimeProvider time,
        GameOptions options,
        GameMetrics metrics,
        Random rng)
    {
        _metrics = metrics;
        _hub = hub;
        _time = time;
        _options = options;
        _rng = rng;

        Game = new CardsGame(id, players, new CardQuestionPool(questions, rng), time, rng);
        Humans = players.Where(p => !p.IsBot).Select(p => p.Id).ToList();
        // Card questions are easier than battle numerics, so card bots are a bit sharper (0.6–0.9).
        _bots = players.Where(p => p.IsBot).ToDictionary(p => p.Id, p => new CardsBot(p.Id, rng, Math.Min(0.9, botAccuracy[p.Id] + 0.15)));
        _standIns = Humans.ToDictionary(h => h, h => new CardsBot(h, rng, accuracy: 0.6, stealChance: 0));
        Rules = BuildRules(Game.Options);
    }

    public string Id => Game.Id;
    public CardsGame Game { get; }
    public CardsRulesDto Rules { get; }
    public IReadOnlyList<PlayerId> Humans { get; }
    public DateTimeOffset? FinishedAt { get; private set; }

    private IEnumerable<PlayerId> ConnectedHumans => Humans.Where(h => !_left.Contains(h));

    public Task StartAsync() => Locked(async () =>
    {
        Game.Start();
        Game.DequeueEvents(); // the opening is covered by the joined state
        _startedAt = _time.GetUtcNow();
        _metrics.GameStarted(Humans.Count, Game.Players.Count);
        foreach (var h in ConnectedHumans)
            await _hub.Clients.User(h.Value).CardsJoined(Rules, BuildState(h));
    });

    public Task SendJoinedAsync(IGameClient client, PlayerId viewer) =>
        Locked(() => client.CardsJoined(Rules, BuildState(viewer)));

    public Task ExecuteAsync(Action<CardsGame> command) => Locked(async () =>
    {
        try
        {
            command(Game);
        }
        finally
        {
            await FlushAsync();
        }
    });

    public Task LeaveAsync(PlayerId player) => Locked(async () =>
    {
        if (FinishedAt is not null || !Humans.Contains(player) || !_left.Add(player)) return;

        if (_left.Count == Humans.Count)
        {
            FinishedAt = _time.GetUtcNow(); // nobody human left
            _metrics.GameAbandoned();
            return;
        }

        await BroadcastAsync([]);
    });

    public async Task TickAsync()
    {
        if (FinishedAt is not null) return;
        await Locked(async () =>
        {
            if (FinishedAt is not null) return;
            Game.Tick();
            DriveBots(_time.GetUtcNow());
            await FlushAsync();
        });
    }

    // ------------------------------------------------------------------ bots

    private void DriveBots(DateTimeOffset now)
    {
        var active = _bots.Values.Concat(_left.Select(p => _standIns[p]));
        foreach (var bot in active)
        {
            if (Game.Phase == CardsPhase.Finished) return;

            var intent = bot.Intent(Game);
            if (intent is BotIntent.None or BotIntent.WaitToSteal)
            {
                _botPlan.Remove(bot.Id);
                continue;
            }

            // A new decision (new turn, new question, or the head start just ended) gets a new delay.
            var key = (intent, Game.Turn, Game.OpenQuestion?.Serial ?? 0);
            if (!_botPlan.TryGetValue(bot.Id, out var plan) || !plan.Key.Equals(key))
            {
                _botPlan[bot.Id] = (key, now + ThinkingTime(intent));
                continue;
            }

            if (now < plan.ReadyAt) continue;
            _botPlan.Remove(bot.Id);
            try
            {
                bot.TryAct(Game);
            }
            catch (GameRuleException)
            {
                // Its idea went stale (someone answered first). It looks again next tick.
            }
        }
    }

    private TimeSpan ThinkingTime(BotIntent intent)
    {
        var seconds = intent switch
        {
            BotIntent.Play => 1.5 + _rng.NextDouble() * 2.5,
            // Usually inside the 2-second head start, sometimes slow enough to be stolen from.
            BotIntent.Answer => 0.8 + _rng.NextDouble() * 2.6,
            BotIntent.Steal => 0.6 + _rng.NextDouble() * 4,
            _ => 1,
        };
        return TimeSpan.FromSeconds(seconds * _options.BotSpeedFactor);
    }

    // ------------------------------------------------------------------ broadcasting

    private async Task FlushAsync()
    {
        var events = Game.DequeueEvents();
        if (events.Count == 0) return;
        Measure(events);
        if (Game.Phase == CardsPhase.Finished) FinishedAt ??= _time.GetUtcNow();
        await BroadcastAsync(events.Select(e => new EventDto(e.GetType().Name, e)).ToList());
    }

    private void Measure(IEnumerable<CardsEvent> events)
    {
        foreach (var e in events)
        {
            switch (e)
            {
                case CardsCardLanded landed:
                    _metrics.QuestionResolved(landed.Category, landed.StolenBy is not null ? "stolen" : landed.Fizzled ? "wrong" : "right");
                    break;
                case CardsCardReturned returned:
                    _metrics.QuestionResolved(returned.Card.Category ?? Game.ActiveCategory, "timeout");
                    break;
                case CardsFinished finished:
                    var winner = finished.Standings.FirstOrDefault(s => s.Place == 1)?.Player;
                    var humanWon = winner is { } w && Game.Players.Any(p => p.Id == w && !p.IsBot);
                    _metrics.GameFinished(humanWon, Game.Turn, _time.GetUtcNow() - _startedAt);
                    break;
            }
        }
    }

    private async Task BroadcastAsync(IReadOnlyList<EventDto> events)
    {
        foreach (var h in ConnectedHumans)
            await _hub.Clients.User(h.Value).CardsUpdated(events, BuildState(h));
    }

    private CardsStateDto BuildState(PlayerId viewer)
    {
        var players = Game.Players.Select((p, seat) => new CardsPlayerDto(
            p.Id, p.Name, p.IsBot, seat, Game.HandCount(p.Id), _left.Contains(p.Id),
            Game.LockedOutPlayer == p.Id, Game.HotSeatPlayer == p.Id)).ToList();

        var seated = Game.Players.Any(p => p.Id == viewer);
        var q = Game.OpenQuestion;
        var question = q is null
            ? null
            : new CardsQuestionDto(q.Serial, q.Purpose, q.Owner, q.Target, q.Card, q.Category, q.Question.ToPublic(),
                q.AskedAt, q.StealOpensAt, q.Deadline, q.Stealable, Game.Answered.ToList());

        return new CardsStateDto(
            Game.Id,
            Game.Phase,
            Game.Turn,
            Game.Options.MaxTurns,
            Game.Direction,
            Game.CurrentPlayer,
            Game.TurnDeadline,
            Game.RevealUntil,
            players,
            Game.TopCard,
            Game.ActiveCategory,
            Game.DrawPileCount,
            seated ? Game.HandOf(viewer).ToList() : [],
            seated ? Game.PlayableCards(viewer).Select(c => c.Id).ToList() : [],
            seated && Game.CurrentPlayer == viewer ? Game.DrawnCard?.Id : null,
            question,
            // Can answer now, or will be able to once the head start ends (the browser shows a countdown).
            seated && q is not null && !Game.Answered.Contains(viewer)
                && (Game.CanAnswer(viewer) || q.Purpose == CardsPhase.Question && q.Stealable && q.Owner != viewer),
            Game.PendingPopQuizTarget,
            Game.Standings,
            _time.GetUtcNow());
    }

    private static CardsRulesDto BuildRules(CardsOptions o) => new(
        o.HandSize,
        o.QuestionTime.TotalSeconds,
        o.HeadStart.TotalSeconds,
        o.TurnTime.TotalSeconds,
        o.WrongAnswerDraw,
        o.StolenDraw,
        o.NoAnswerDraw,
        o.WrongStealDraw,
        o.PopQuizDraw,
        o.DuelDraw,
        o.MaxTurns,
        Enum.GetValues<CardCategory>(),
        o.RevealTime.TotalSeconds,
        o.DrawPause.TotalSeconds,
        o.DrawnCardTime.TotalSeconds);

    private async Task Locked(Func<Task> action)
    {
        await _gate.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            _gate.Release();
        }
    }
}
