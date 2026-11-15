using Triviador.Domain.Map;
using Triviador.Domain.Questions;

namespace Triviador.Domain.Conquest;

/// <summary>
/// One Conquest game (rules: docs/CONQUEST.md). Server-authoritative and single-threaded:
/// the host must serialize calls per game, like <see cref="Matches.Match"/>.
///
/// Setup:  ColorQuestion → ColorPick (in answer order) → CapitalPick (same order)
/// Round:  SupplyQuestion → income → Planning (simultaneous) → [BattleQuestion] → resolution → next round
///
/// Regions have castles (level 0–3) instead of troops. An attack uses up a weapon; if the attacker wins
/// the battle question exchange, the weapon's power either captures the region (power above the castle
/// level, castle destroyed) or knocks that many levels off the castle.
/// </summary>
public sealed class ConquestGame
{
    public const int MinPlayers = 2;
    public const int MaxPlayers = 4;

    private readonly GameMap _map;
    private readonly IQuestionSource _questions;
    private readonly TimeProvider _time;
    private readonly ConquestOptions _options;
    private readonly Random _rng;

    private readonly List<PlayerSlot> _players;
    private readonly Dictionary<PlayerId, PlayerSlot> _byId;
    private readonly Dictionary<int, RegionSlot> _regions = new();
    private readonly List<ConquestEvent> _pendingEvents = new();
    private readonly List<PlayerId> _eliminationOrder = new();

    private OpenQuestion? _question;
    private readonly Queue<PlayerId> _pickQueue = new();
    private List<PlayerId> _pickOrder = [];
    private DateTimeOffset? _pickDeadline;
    private DateTimeOffset? _planDeadline;
    private List<PlayerId> _initiative = [];
    private readonly Dictionary<PlayerId, (bool Correct, TimeSpan Elapsed)> _battleAnswers = new();
    private readonly Dictionary<PlayerId, int> _removedOption = new();
    private List<ConquestStanding>? _standings;

    public ConquestGame(
        string id,
        IReadOnlyList<PlayerInfo> players,
        IQuestionSource questions,
        TimeProvider time,
        Random rng,
        ConquestOptions? options = null,
        GameMap? map = null)
    {
        if (players.Count is < MinPlayers or > MaxPlayers)
            throw new ArgumentException($"Conquest needs {MinPlayers}–{MaxPlayers} players.", nameof(players));
        if (players.Select(p => p.Id).Distinct().Count() != players.Count)
            throw new ArgumentException("Player ids must be unique.", nameof(players));

        Id = id;
        _map = map ?? WorldMap.Create();
        _questions = questions;
        _time = time;
        _rng = rng;
        _options = options ?? ConquestOptions.Default;
        _players = players.Select((p, seat) => new PlayerSlot(p, seat, _options.StartingWallet)).ToList();
        _byId = _players.ToDictionary(p => p.Info.Id);
    }

    public string Id { get; }
    public ConquestPhase Phase { get; private set; } = ConquestPhase.NotStarted;
    public int Round { get; private set; }
    public GameMap Map => _map;
    public ConquestOptions Options => _options;
    public IReadOnlyList<PlayerInfo> Players => _players.Select(p => p.Info).ToList();
    public IReadOnlyList<ConquestStanding>? Standings => _standings;
    public IReadOnlyList<PlayerId> Initiative => _initiative;

    /// <summary>Who picks now (colour or capital).</summary>
    public PlayerId? CurrentPicker =>
        Phase is ConquestPhase.ColorPick or ConquestPhase.CapitalPick && _pickQueue.Count > 0 ? _pickQueue.Peek() : null;

    public DateTimeOffset? PickDeadline => CurrentPicker is null ? null : _pickDeadline;
    public DateTimeOffset? PlanDeadline => Phase == ConquestPhase.Planning ? _planDeadline : null;
    public PublicQuestion? CurrentQuestion => _question?.Question.ToPublic();
    public DateTimeOffset? QuestionDeadline => _question?.Deadline;

    internal Question? ActiveQuestion => _question?.Question;

    public IReadOnlyList<PlayerId> PendingAnswerers =>
        _question is null ? [] : _question.Answerers.Where(a => !_question.Answers.ContainsKey(a)).ToList();

    public IReadOnlyList<PlayerId> Answerers => _question?.Answerers ?? [];

    public bool IsAnswering(PlayerId p) => _question is not null && _question.Answerers.Contains(p) && !_question.Answers.ContainsKey(p);

    public IReadOnlyList<string> AvailableColors =>
        ConquestOptions.Colors.Where(c => _players.All(p => p.Color != c)).ToList();

    public ConquestRegionState Region(int id)
    {
        var r = RegionSlotOf(id);
        return new ConquestRegionState(id, r.Resource, r.Owner, r.Castle, r.Building, r.CapitalOf);
    }

    public IReadOnlyList<ConquestRegionState> Regions => _regions.Keys.Order().Select(Region).ToList();

    public Wallet WalletOf(PlayerId p) => Slot(p).Wallet;
    public string? ColorOf(PlayerId p) => Slot(p).Color;
    public bool IsEliminated(PlayerId p) => Slot(p).Eliminated;
    public bool IsReady(PlayerId p) => Slot(p).Ready;
    public int RegionCount(PlayerId p) => _regions.Values.Count(r => r.Owner == p);

    /// <summary>For the University: which option of the battle question is hidden from this player.</summary>
    public int? RemovedOptionFor(PlayerId p) => _removedOption.TryGetValue(p, out var i) ? i : null;

    public int ScoreOf(PlayerId p) =>
        _regions.Values.Where(r => r.Owner == p).Sum(r =>
            _options.PointsPerRegion
            + (r.Building != Building.None ? _options.PointsPerBuilding : 0)
            + r.Castle * _options.PointsPerCastleLevel
            + (r.CapitalOf is not null ? _options.PointsPerCapital : 0));

    public IReadOnlyList<ConquestEvent> DequeueEvents()
    {
        var events = _pendingEvents.ToList();
        _pendingEvents.Clear();
        return events;
    }

    // ================================================================== setup

    public void Start()
    {
        EnsurePhase(ConquestPhase.NotStarted);

        var layout = ResourceLayout.Generate(_map, _rng);
        foreach (var t in _map.Territories)
            _regions[t.Id] = new RegionSlot(layout[t.Id], _rng.NextDouble() < _options.NeutralCastleChance ? 1 : 0);

        Raise(new ConquestStarted(Id, Players));
        SetPhase(ConquestPhase.ColorQuestion);
        Ask(_questions.NextNumeric(), AlivePlayers().ToList());
    }

    public void PickColor(PlayerId player, string color)
    {
        EnsurePhase(ConquestPhase.ColorPick);
        EnsurePicker(player);
        if (!AvailableColors.Contains(color))
            throw new GameRuleException("That colour isn't available.");

        Slot(player).Color = color;
        Raise(new ColorPicked(player, color));
        NextPick();
    }

    public IReadOnlyList<int> ValidCapitals()
    {
        var capitals = _regions.Where(kv => kv.Value.CapitalOf is not null).Select(kv => kv.Key).ToHashSet();
        return _regions
            .Where(kv => kv.Value.Owner is null)
            .Select(kv => kv.Key)
            .Where(id => !_map.Neighbors(id).Any(capitals.Contains))
            .Order()
            .ToList();
    }

    public void PickCapital(PlayerId player, int region)
    {
        EnsurePhase(ConquestPhase.CapitalPick);
        EnsurePicker(player);
        if (!ValidCapitals().Contains(region))
            throw new GameRuleException("Pick an unclaimed region that doesn't border another capital.");

        var r = _regions[region];
        r.Owner = player;
        r.Castle = _options.CapitalCastle;
        r.CapitalOf = player;
        Slot(player).Capital = region;
        Raise(new CapitalPicked(player, region));
        NextPick();
    }

    private void BeginPicks(ConquestPhase phase)
    {
        SetPhase(phase);
        _pickQueue.Clear();
        foreach (var p in _pickOrder) _pickQueue.Enqueue(p);
        StartPickTurn();
    }

    private void StartPickTurn()
    {
        _pickDeadline = _time.GetUtcNow() + _options.PickTime;
        Raise(new PickTurn(_pickQueue.Peek(), Phase, _pickDeadline.Value));
    }

    private void NextPick()
    {
        _pickQueue.Dequeue();
        if (_pickQueue.Count > 0)
        {
            StartPickTurn();
            return;
        }

        _pickDeadline = null;
        if (Phase == ConquestPhase.ColorPick)
            BeginPicks(ConquestPhase.CapitalPick);
        else
            StartRound();
    }

    private void AutoPick()
    {
        var p = _pickQueue.Peek();
        if (Phase == ConquestPhase.ColorPick)
        {
            var colors = AvailableColors;
            PickColor(p, colors[_rng.Next(colors.Count)]);
        }
        else
        {
            var valid = ValidCapitals();
            if (valid.Count == 0) // Can't happen on the world map with ≤4 players, but never get stuck.
                valid = _regions.Where(kv => kv.Value.Owner is null).Select(kv => kv.Key).ToList();
            PickCapitalUnchecked(p, valid[_rng.Next(valid.Count)]);
        }
    }

    private void PickCapitalUnchecked(PlayerId player, int region)
    {
        if (ValidCapitals().Contains(region))
        {
            PickCapital(player, region);
            return;
        }

        var r = _regions[region];
        r.Owner = player;
        r.Castle = _options.CapitalCastle;
        r.CapitalOf = player;
        Slot(player).Capital = region;
        Raise(new CapitalPicked(player, region));
        NextPick();
    }

    // ================================================================== rounds

    private void StartRound()
    {
        Round++;
        foreach (var p in _players)
        {
            p.Ready = false;
            p.Plan = [];
        }

        _battleAnswers.Clear();
        _removedOption.Clear();
        SetPhase(ConquestPhase.SupplyQuestion);
        Ask(_questions.NextNumeric(), AlivePlayers().ToList());
    }

    private void AwardSupply(IReadOnlyList<PlayerId> ranking)
    {
        var alive = AlivePlayers().Count();
        if (ranking.Count >= 1)
        {
            var bonus = Wallet.Each(_options.SupplyBonusFirst);
            Slot(ranking[0]).Wallet += bonus;
            Raise(new SupplyAwarded(ranking[0], 1, bonus));
        }

        if (ranking.Count >= 2 && alive >= 3)
        {
            var bonus = Wallet.Each(_options.SupplyBonusSecond);
            Slot(ranking[1]).Wallet += bonus;
            Raise(new SupplyAwarded(ranking[1], 2, bonus));
        }
    }

    public Wallet IncomeOf(PlayerId p)
    {
        var income = Wallet.Zero;
        foreach (var r in _regions.Values.Where(r => r.Owner == p))
        {
            income += Wallet.Of(r.Resource, 1);
            income += ConquestOptions.IncomeOf(r.Building);
            if (r.CapitalOf == p) income += Wallet.Each(1);
        }

        return income;
    }

    private void CollectIncome()
    {
        var alive = AlivePlayers().ToList();
        var scores = alive.ToDictionary(p => p, ScoreOf);
        var lowest = scores.Values.DefaultIfEmpty(0).Min();
        var catchUp = scores.Values.Distinct().Count() > 1
            ? scores.Where(kv => kv.Value == lowest).Select(kv => kv.Key).ToHashSet()
            : [];

        foreach (var p in alive)
        {
            var income = IncomeOf(p);
            if (catchUp.Contains(p)) income += Wallet.Each(_options.CatchUpBonus);
            Slot(p).Wallet += income;
            Raise(new IncomeCollected(p, income));
        }
    }

    private void BeginPlanning()
    {
        SetPhase(ConquestPhase.Planning);
        _planDeadline = _time.GetUtcNow() + _options.PlanningTime;
    }

    // ================================================================== planning

    /// <summary>
    /// Submit this round's orders (replacing any earlier submission) and mark yourself ready.
    /// The whole plan is checked up front: it must be affordable and leave at least 1 troop everywhere.
    /// </summary>
    public void SubmitPlan(PlayerId player, IReadOnlyList<Order> orders)
    {
        EnsurePhase(ConquestPhase.Planning);
        var slot = Slot(player);
        if (slot.Eliminated) throw new GameRuleException("You have been eliminated.");

        ValidatePlan(player, orders);
        slot.Plan = orders.ToList();
        if (!slot.Ready)
        {
            slot.Ready = true;
            Raise(new PlayerReady(player));
        }

        if (AlivePlayers().All(p => Slot(p).Ready))
            EndPlanning();
    }

    /// <summary>Throws <see cref="GameRuleException"/> with the first problem found; returns what the plan would cost.</summary>
    public Wallet ValidatePlan(PlayerId player, IReadOnlyList<Order> orders)
    {
        if (orders.Count > 100) throw new GameRuleException("Too many orders.");

        var cost = Wallet.Zero;
        var building = new HashSet<int>();
        var fortifying = new HashSet<int>();
        var hasUniversity = _regions.Values.Any(r => r.Owner == player && r.Building == Building.University);

        foreach (var order in orders)
        {
            switch (order)
            {
                case BuildOrder b:
                {
                    var r = OwnedBy(player, b.Region);
                    if (b.Building == Building.None) throw new GameRuleException("Choose a building.");
                    if (r.Building != Building.None || !building.Add(b.Region))
                        throw new GameRuleException("A region can only hold one building.");
                    if (b.Building == Building.University)
                    {
                        if (hasUniversity) throw new GameRuleException("You can only have one University.");
                        hasUniversity = true;
                    }
                    cost += ConquestOptions.CostOf(b.Building);
                    break;
                }
                case FortifyOrder f:
                {
                    var r = OwnedBy(player, f.Region);
                    if (!fortifying.Add(f.Region)) throw new GameRuleException("A castle can only grow one level a round.");
                    if (r.Castle >= _options.MaxCastle) throw new GameRuleException("That castle is already at its highest level.");
                    cost += ConquestOptions.FortifyCost(r.Castle);
                    break;
                }
                case AttackOrder a:
                {
                    OwnedBy(player, a.From);
                    if (!_regions.TryGetValue(a.To, out var target) || !_map.AreAdjacent(a.From, a.To))
                        throw new GameRuleException("You can only attack a region that borders yours.");
                    if (target.Owner == player) throw new GameRuleException("That region is already yours.");
                    if (!Enum.IsDefined(a.Weapon)) throw new GameRuleException("Unknown weapon.");
                    cost += ConquestOptions.CostOf(a.Weapon);
                    break;
                }
                default:
                    throw new GameRuleException("Unknown order.");
            }
        }

        if (!Slot(player).Wallet.Covers(cost))
            throw new GameRuleException("You can't afford all of that.");
        return cost;
    }

    private void EndPlanning()
    {
        _planDeadline = null;
        var fighting = new HashSet<PlayerId>();

        // Pay for everything. Buildings and castle upgrades are finished before the battles.
        foreach (var p in AlivePlayers().Select(Slot))
        {
            foreach (var order in p.Plan)
            {
                switch (order)
                {
                    case BuildOrder b:
                        p.Wallet -= ConquestOptions.CostOf(b.Building);
                        _regions[b.Region].Building = b.Building;
                        Raise(new BuildingBuilt(p.Info.Id, b.Region, b.Building));
                        break;
                    case FortifyOrder f:
                        var r = _regions[f.Region];
                        p.Wallet -= ConquestOptions.FortifyCost(r.Castle);
                        r.Castle++;
                        Raise(new CastleFortified(p.Info.Id, f.Region, r.Castle));
                        break;
                    case AttackOrder a:
                        p.Wallet -= ConquestOptions.CostOf(a.Weapon);
                        fighting.Add(p.Info.Id);
                        if (_regions[a.To].Owner is { } defender) fighting.Add(defender);
                        break;
                }
            }
        }

        if (fighting.Count == 0)
        {
            Resolve();
            return;
        }

        // One battle question for everyone attacking or under attack.
        SetPhase(ConquestPhase.BattleQuestion);
        var question = _questions.NextMultipleChoice();
        var answerers = _initiative.Where(fighting.Contains).ToList();
        foreach (var p in answerers.Where(HasUniversity))
        {
            var wrong = Enumerable.Range(0, question.Options.Count).Where(i => i != question.CorrectIndex).ToList();
            _removedOption[p] = wrong[_rng.Next(wrong.Count)];
        }

        Ask(question, answerers);
    }

    private bool HasUniversity(PlayerId p) => _regions.Values.Any(r => r.Owner == p && r.Building == Building.University);

    // ================================================================== resolution

    private void Resolve()
    {
        foreach (var p in _initiative.Where(p => !Slot(p).Eliminated).ToList())
        {
            foreach (var a in Slot(p).Plan.OfType<AttackOrder>())
            {
                if (Slot(p).Eliminated) break;

                var from = _regions[a.From];
                var to = _regions[a.To];
                if (from.Owner != p)
                {
                    CancelAttack(p, a, "the region you attacked from was lost");
                    continue;
                }
                if (to.Owner == p)
                {
                    CancelAttack(p, a, "you already took it");
                    continue;
                }

                Attack(p, a);
            }
        }

        if (!CheckVictory())
            StartRound();
    }

    /// <summary>An attack that can no longer happen: the weapon goes back to the armoury (refunded).</summary>
    private void CancelAttack(PlayerId p, AttackOrder a, string reason)
    {
        Slot(p).Wallet += ConquestOptions.CostOf(a.Weapon);
        Raise(new AttackCancelled(p, a.From, a.To, a.Weapon, reason));
    }

    private void Attack(PlayerId attacker, AttackOrder order)
    {
        var to = _regions[order.To];
        var defender = to.Owner;

        var (attackerCorrect, attackerTime) = _battleAnswers.TryGetValue(attacker, out var aa) ? aa : (false, TimeSpan.MaxValue);
        bool? defenderCorrect = null;
        bool hit;
        string reason;

        if (defender is null)
        {
            hit = attackerCorrect;
            reason = hit ? "right answer" : "wrong answer";
        }
        else
        {
            var (dc, dt) = _battleAnswers.TryGetValue(defender.Value, out var da) ? da : (false, TimeSpan.MaxValue);
            defenderCorrect = dc;
            (hit, reason) = (attackerCorrect, dc) switch
            {
                (false, _) => (false, dc ? "defender answered, attacker didn't" : "nobody answered right"),
                (true, false) => (true, "attacker answered, defender didn't"),
                (true, true) => attackerTime < dt ? (true, "both right, attacker faster") : (false, "both right, defender faster"),
            };
        }

        var before = to.Castle;
        var power = ConquestOptions.PowerOf(order.Weapon);
        var captured = false;

        if (hit)
        {
            if (power > before)
            {
                captured = true;
                to.Owner = attacker;
                to.Castle = 0;
            }
            else
            {
                to.Castle = before - power;
            }
        }

        Raise(new BattleFought(new BattleReport(
            attacker, defender, order.From, order.To, order.Weapon, before, to.Castle,
            attackerCorrect, defenderCorrect, hit, captured, reason)));

        if (!captured || defender is null) return;

        if (to.CapitalOf == defender)
            Raise(new CapitalCaptured(defender.Value, attacker, order.To));

        if (RegionCount(defender.Value) == 0)
        {
            Slot(defender.Value).Eliminated = true;
            _eliminationOrder.Add(defender.Value);
            Raise(new ConquestPlayerEliminated(defender.Value, attacker));
        }
    }

    private bool CheckVictory()
    {
        var alive = AlivePlayers().ToList();
        var domination = (int)Math.Ceiling(_map.Count * _options.DominationShare);

        if (alive.Count <= 1 || Round >= _options.MaxRounds || alive.Any(p => RegionCount(p) >= domination))
        {
            Finish();
            return true;
        }

        return false;
    }

    private void Finish()
    {
        var initiative = _initiative.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        var alive = AlivePlayers()
            .OrderByDescending(ScoreOf)
            .ThenByDescending(RegionCount)
            .ThenBy(p => initiative.GetValueOrDefault(p, int.MaxValue));
        var knockedOut = Enumerable.Reverse(_eliminationOrder);

        _standings = alive.Concat(knockedOut)
            .Select((p, i) => new ConquestStanding(p, i + 1, ScoreOf(p), RegionCount(p), Slot(p).Eliminated))
            .ToList();

        _question = null;
        SetPhase(ConquestPhase.Finished);
        Raise(new ConquestFinished(_standings));
    }

    // ================================================================== questions

    public AnswerOutcomeKind SubmitAnswer(PlayerId player, Answer answer)
    {
        var q = _question ?? throw new GameRuleException("There is no open question.");
        if (!q.Answerers.Contains(player)) throw new GameRuleException("You are not answering this question.");
        if (q.Answers.ContainsKey(player)) throw new GameRuleException("You already answered.");

        switch (q.Question, answer)
        {
            case (MultipleChoiceQuestion mc, ChoiceAnswer c):
                if (c.OptionIndex < 0 || c.OptionIndex >= mc.Options.Count)
                    throw new GameRuleException("That option does not exist.");
                break;
            case (NumericQuestion, NumericAnswer n):
                if (double.IsNaN(n.Value) || double.IsInfinity(n.Value))
                    throw new GameRuleException("Answer must be a finite number.");
                break;
            default:
                throw new GameRuleException("Answer type does not match the question.");
        }

        var now = _time.GetUtcNow();
        if (now > q.Deadline)
        {
            ResolveQuestion();
            return AnswerOutcomeKind.TooLate;
        }

        q.Answers[player] = (answer, now - q.AskedAt);
        Raise(new ConquestAnswerReceived(player));
        if (q.Answers.Count == q.Answerers.Count)
            ResolveQuestion();
        return AnswerOutcomeKind.Accepted;
    }

    /// <summary>Called by the host's timer: question deadlines, pick turns and the planning clock.</summary>
    public bool Tick()
    {
        var now = _time.GetUtcNow();
        if (_question is not null && now >= _question.Deadline)
        {
            ResolveQuestion();
            return true;
        }

        if (CurrentPicker is not null && _pickDeadline is { } pick && now >= pick)
        {
            AutoPick();
            return true;
        }

        if (Phase == ConquestPhase.Planning && _planDeadline is { } plan && now >= plan)
        {
            EndPlanning();
            return true;
        }

        return false;
    }

    private void Ask(Question question, IReadOnlyList<PlayerId> answerers)
    {
        var now = _time.GetUtcNow();
        var limit = question.Kind == QuestionKind.MultipleChoice ? _options.MultipleChoiceTime : _options.NumericTime;
        _question = new OpenQuestion(question, answerers, now, now + limit);
        Raise(new ConquestQuestionAsked(question.ToPublic(), answerers, now + limit));
    }

    private void ResolveQuestion()
    {
        var q = _question!;
        _question = null;

        var ranking = q.Question switch
        {
            NumericQuestion n => q.Answers
                .Where(kv => kv.Value.Answer is NumericAnswer)
                .OrderBy(kv => Math.Abs(((NumericAnswer)kv.Value.Answer).Value - n.Answer))
                .ThenBy(kv => kv.Value.Elapsed)
                .ThenBy(kv => Slot(kv.Key).Seat)
                .Select(kv => kv.Key)
                .ToList(),
            MultipleChoiceQuestion mc => q.Answers
                .Where(kv => kv.Value.Answer is ChoiceAnswer c && c.OptionIndex == mc.CorrectIndex)
                .OrderBy(kv => kv.Value.Elapsed)
                .Select(kv => kv.Key)
                .ToList(),
            _ => [],
        };

        Raise(new ConquestQuestionResolved(q.Question.Id, q.Question.CorrectAnswerText, Results(q), ranking));

        // Players who didn't answer go last, in seat order.
        var order = ranking.Concat(q.Answerers.Where(a => !ranking.Contains(a)).OrderBy(a => Slot(a).Seat)).ToList();

        switch (Phase)
        {
            case ConquestPhase.ColorQuestion:
                _pickOrder = order;
                BeginPicks(ConquestPhase.ColorPick);
                break;

            case ConquestPhase.SupplyQuestion:
                _initiative = order;
                AwardSupply(ranking);
                CollectIncome();
                BeginPlanning();
                break;

            case ConquestPhase.BattleQuestion:
                foreach (var p in q.Answerers)
                {
                    var correct = ranking.Contains(p);
                    var elapsed = q.Answers.TryGetValue(p, out var given) ? given.Elapsed : TimeSpan.MaxValue;
                    _battleAnswers[p] = (correct, elapsed);
                }
                Resolve();
                break;
        }
    }

    private static IReadOnlyList<AnswerResult> Results(OpenQuestion q) =>
        q.Answerers.Select(p =>
        {
            if (!q.Answers.TryGetValue(p, out var a)) return new AnswerResult(p, null, false, null, null);
            return (q.Question, a.Answer) switch
            {
                (MultipleChoiceQuestion mc, ChoiceAnswer c) => new AnswerResult(p, c, c.OptionIndex == mc.CorrectIndex, null, a.Elapsed),
                (NumericQuestion n, NumericAnswer v) => new AnswerResult(p, v, v.Value == n.Answer, Math.Abs(v.Value - n.Answer), a.Elapsed),
                _ => new AnswerResult(p, a.Answer, false, null, a.Elapsed),
            };
        }).ToList();

    // ================================================================== helpers

    private IEnumerable<PlayerId> AlivePlayers() => _players.Where(p => !p.Eliminated).Select(p => p.Info.Id);

    private PlayerSlot Slot(PlayerId p) =>
        _byId.TryGetValue(p, out var s) ? s : throw new GameRuleException("You are not in this game.");

    private RegionSlot RegionSlotOf(int id) =>
        _regions.TryGetValue(id, out var r) ? r : throw new GameRuleException($"Region {id} does not exist.");

    private RegionSlot OwnedBy(PlayerId p, int region)
    {
        var r = RegionSlotOf(region);
        if (r.Owner != p) throw new GameRuleException($"You don't hold {_map.Get(region).Name}.");
        return r;
    }

    private void EnsurePhase(ConquestPhase expected)
    {
        if (Phase != expected) throw new GameRuleException($"Not allowed during {Phase}.");
    }

    private void EnsurePicker(PlayerId p)
    {
        if (CurrentPicker != p) throw new GameRuleException("It's not your turn to pick.");
    }

    private void SetPhase(ConquestPhase phase)
    {
        Phase = phase;
        Raise(new ConquestPhaseChanged(phase, Round));
    }

    private void Raise(ConquestEvent e) => _pendingEvents.Add(e);

    private sealed class PlayerSlot(PlayerInfo info, int seat, Wallet wallet)
    {
        public PlayerInfo Info { get; } = info;
        public int Seat { get; } = seat;
        public string? Color { get; set; }
        public int? Capital { get; set; }
        public Wallet Wallet { get; set; } = wallet;
        public bool Eliminated { get; set; }
        public bool Ready { get; set; }
        public List<Order> Plan { get; set; } = [];
    }

    private sealed class RegionSlot(Resource resource, int castle)
    {
        public Resource Resource { get; } = resource;
        public PlayerId? Owner { get; set; }
        public int Castle { get; set; } = castle;
        public Building Building { get; set; }
        public PlayerId? CapitalOf { get; set; }
    }

    private sealed class OpenQuestion(Question question, IReadOnlyList<PlayerId> answerers, DateTimeOffset askedAt, DateTimeOffset deadline)
    {
        public Question Question { get; } = question;
        public IReadOnlyList<PlayerId> Answerers { get; } = answerers;
        public DateTimeOffset AskedAt { get; } = askedAt;
        public DateTimeOffset Deadline { get; } = deadline;
        public Dictionary<PlayerId, (Answer Answer, TimeSpan Elapsed)> Answers { get; } = new();
    }
}

public enum AnswerOutcomeKind
{
    Accepted,
    TooLate,
}
