using Microsoft.AspNetCore.SignalR;
using Triviador.Domain;
using Triviador.Domain.Conquest;
using Triviador.Domain.Questions;
using Triviador.Server.Contracts;
using Triviador.Server.Hubs;

namespace Triviador.Server.Rooms;

/// <summary>
/// Hosts one <see cref="ConquestGame"/>: serializes access with a per-room lock, drives bots with
/// human-like thinking time, and sends every human their own view (their wallet stays private).
/// The engine's own timers handle slow players: picks are made for them, questions time out and
/// planning ends with an empty plan. A player who leaves is replaced by a bot straight away.
/// </summary>
public sealed class ConquestRoom : IGameRoom
{
    private static readonly ConquestRulesDto Rules = BuildRules();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IHubContext<GameHub, IGameClient> _hub;
    private readonly TimeProvider _time;
    private readonly GameOptions _options;
    private readonly Random _rng;

    private readonly Dictionary<PlayerId, ConquestBot> _bots;
    private readonly Dictionary<PlayerId, ConquestBot> _standIns;
    private readonly HashSet<PlayerId> _left = [];
    private readonly Dictionary<PlayerId, DateTimeOffset> _botReadyAt = new();
    private (ConquestPhase, int, DateTimeOffset?, PlayerId?) _decision;

    public ConquestRoom(
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

        Game = new ConquestGame(id, players, new InMemoryQuestionSource(SampleQuestions.All, rng), time, rng);
        Humans = players.Where(p => !p.IsBot).Select(p => p.Id).ToList();
        _bots = players.Where(p => p.IsBot).ToDictionary(p => p.Id, p => new ConquestBot(p.Id, rng, botAccuracy[p.Id]));
        _standIns = Humans.ToDictionary(h => h, h => new ConquestBot(h, rng, accuracy: 0.3));
    }

    public string Id => Game.Id;
    public ConquestGame Game { get; }
    public IReadOnlyList<PlayerId> Humans { get; }
    public DateTimeOffset? FinishedAt { get; private set; }

    private IEnumerable<PlayerId> ConnectedHumans => Humans.Where(h => !_left.Contains(h));

    public Task StartAsync() => Locked(async () =>
    {
        Game.Start();
        Game.DequeueEvents(); // the opening events are covered by the joined state
        foreach (var h in ConnectedHumans)
            await _hub.Clients.User(h.Value).ConquestJoined(Rules, BuildState(h));
    });

    public Task SendJoinedAsync(IGameClient client, PlayerId viewer) =>
        Locked(() => client.ConquestJoined(Rules, BuildState(viewer)));

    public Task ExecuteAsync(Action<ConquestGame> command) => Locked(async () =>
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
            FinishedAt = _time.GetUtcNow(); // nobody human left: stop playing for no one
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
        var key = (Game.Phase, Game.Round, Game.QuestionDeadline, Game.CurrentPicker);
        if (key != _decision)
        {
            _decision = key;
            _botReadyAt.Clear();
        }

        var active = _bots.Values.Concat(_left.Select(p => _standIns[p]));
        foreach (var bot in active)
        {
            if (!bot.HasPendingAction(Game)) continue;

            if (!_botReadyAt.TryGetValue(bot.Id, out var readyAt))
            {
                _botReadyAt[bot.Id] = now + ThinkingTime();
                continue;
            }

            if (now < readyAt) continue;
            _botReadyAt.Remove(bot.Id);
            try
            {
                bot.TryAct(Game);
            }
            catch (GameRuleException)
            {
                // A bot's idea went stale (e.g. its plan no longer fits). It tries again next tick.
            }
        }
    }

    private TimeSpan ThinkingTime()
    {
        var seconds = Game.Phase switch
        {
            ConquestPhase.Planning => 4 + _rng.NextDouble() * 6,
            ConquestPhase.ColorPick or ConquestPhase.CapitalPick => 1 + _rng.NextDouble() * 2,
            ConquestPhase.BattleQuestion => 2.5 + _rng.NextDouble() * 4.5,
            _ => 3 + _rng.NextDouble() * 6,
        };
        return TimeSpan.FromSeconds(seconds * _options.BotSpeedFactor);
    }

    // ------------------------------------------------------------------ broadcasting

    private async Task FlushAsync()
    {
        var events = Game.DequeueEvents();
        if (events.Count == 0) return;
        if (Game.Phase == ConquestPhase.Finished) FinishedAt ??= _time.GetUtcNow();
        await BroadcastAsync(events.Select(e => new EventDto(e.GetType().Name, e)).ToList());
    }

    private async Task BroadcastAsync(IReadOnlyList<EventDto> events)
    {
        foreach (var h in ConnectedHumans)
            await _hub.Clients.User(h.Value).ConquestUpdated(events, BuildState(h));
    }

    private ConquestStateDto BuildState(PlayerId viewer)
    {
        var players = Game.Players.Select((p, seat) =>
        {
            var capital = Game.Regions.FirstOrDefault(r => r.CapitalOf == p.Id)?.Id;
            return new ConquestPlayerDto(
                p.Id, p.Name, p.IsBot, seat, Game.ColorOf(p.Id), capital, Game.RegionCount(p.Id), Game.ScoreOf(p.Id),
                Game.IsEliminated(p.Id), Game.IsReady(p.Id), _left.Contains(p.Id));
        }).ToList();

        var regions = Game.Phase == ConquestPhase.NotStarted
            ? []
            : Game.Regions.Select(r => new ConquestRegionDto(r.Id, r.Resource, r.Owner, r.Castle, r.Building, r.CapitalOf)).ToList();

        var you = Game.Players.Any(p => p.Id == viewer)
            ? new ConquestYouDto(Game.WalletOf(viewer), Game.IncomeOf(viewer), Game.IsReady(viewer), Game.RemovedOptionFor(viewer))
            : null;

        return new ConquestStateDto(
            Game.Id,
            Game.Phase,
            Game.Round,
            Game.Options.MaxRounds,
            players,
            regions,
            Game.CurrentPicker,
            Game.PickDeadline,
            Game.AvailableColors,
            Game.Phase == ConquestPhase.CapitalPick ? Game.ValidCapitals() : [],
            Game.CurrentQuestion,
            Game.QuestionDeadline,
            Game.Answerers,
            Game.PendingAnswerers,
            Game.PlanDeadline,
            Game.Initiative,
            you,
            Game.Standings,
            _time.GetUtcNow());
    }

    private static ConquestRulesDto BuildRules()
    {
        var o = ConquestOptions.Default;
        var map = WorldMap.Create();
        string Effect(Building b) => b switch
        {
            Building.Barracks => "+1 Manpower a round",
            Building.Workshop => "+1 Production a round",
            Building.Market => "+1 Gold a round",
            Building.University => "Removes a wrong option from your battle question (one per player)",
            _ => "",
        };

        Building[] buildings = [Building.Barracks, Building.Workshop, Building.Market, Building.University];
        Weapon[] weapons = [Weapon.Sword, Weapon.Catapult, Weapon.Trebuchet];
        return new ConquestRulesDto(
            ConquestOptions.Colors,
            weapons.Select(w => new WeaponRuleDto(w, ConquestOptions.CostOf(w), ConquestOptions.PowerOf(w))).ToList(),
            Enumerable.Range(0, o.MaxCastle).Select(ConquestOptions.FortifyCost).ToList(),
            o.MaxCastle,
            buildings.Select(b => new BuildingRuleDto(b, ConquestOptions.CostOf(b), Effect(b))).ToList(),
            o.MaxRounds,
            (int)Math.Ceiling(map.Count * o.DominationShare),
            map.Count);
    }

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
