using Triviador.Domain.Map;
using Triviador.Domain.Questions;

namespace Triviador.Domain.Matches;

/// <summary>
/// One 3-player match. Server-authoritative and single-threaded by design:
/// the host (SignalR hub / match actor) must serialize calls per match.
///
/// Flow:
///   CastlePlacement → (ExpansionQuestion → ExpansionPicks)* until the map is full
///   → (BattleAttack → BattleDuel [→ BattleTiebreak])* for N rounds → Finished
///
/// Time is read from <see cref="TimeProvider"/>, so answer speed is measured on the
/// server and tests can control the clock.
/// </summary>
public sealed class Match
{
    public const int PlayerCount = 3;

    private readonly GameMap _map;
    private readonly IQuestionSource _questions;
    private readonly TimeProvider _time;
    private readonly MatchOptions _options;
    private readonly List<PlayerInfo> _players;

    private readonly Dictionary<int, PlayerId> _owners = new();
    private readonly Dictionary<PlayerId, Castle> _castles = new();
    private readonly List<PlayerId> _eliminationOrder = new();
    private readonly Queue<PlayerId> _turnQueue = new();
    private readonly List<MatchEvent> _pendingEvents = new();

    private ActiveQuestion? _question;
    private Duel? _duel;
    private List<Standing>? _standings;

    public Match(
        string id,
        IReadOnlyList<PlayerInfo> players,
        GameMap map,
        IQuestionSource questions,
        TimeProvider time,
        MatchOptions? options = null)
    {
        if (players.Count != PlayerCount)
            throw new ArgumentException($"A match needs exactly {PlayerCount} players.", nameof(players));
        if (players.Select(p => p.Id).Distinct().Count() != PlayerCount)
            throw new ArgumentException("Player ids must be unique.", nameof(players));

        Id = id;
        _players = players.ToList();
        _map = map;
        _questions = questions;
        _time = time;
        _options = options ?? MatchOptions.Default;

        if (_map.Count < PlayerCount * 2)
            throw new ArgumentException("Map is too small for three players.", nameof(map));
    }

    public string Id { get; }
    public MatchPhase Phase { get; private set; } = MatchPhase.NotStarted;
    public int BattleRound { get; private set; }
    public IReadOnlyList<PlayerInfo> Players => _players;
    public GameMap Map => _map;
    public IReadOnlyDictionary<int, PlayerId> Owners => _owners;
    public IReadOnlyList<Standing>? Standings => _standings;

    /// <summary>Whose move it is, in phases where exactly one player acts.</summary>
    public PlayerId? CurrentTurn =>
        Phase is MatchPhase.CastlePlacement or MatchPhase.ExpansionPicks or MatchPhase.BattleAttack
        && _turnQueue.Count > 0
            ? _turnQueue.Peek()
            : null;

    public PublicQuestion? CurrentQuestion => _question?.Question.ToPublic();
    public DateTimeOffset? QuestionDeadline => _question?.Deadline;

    // Internal so bots and tests can see the answer; never exposed to clients.
    internal ActiveQuestion? ActiveQuestion => _question;

    public bool IsEliminated(PlayerId player) => _eliminationOrder.Contains(player);

    public PlayerId? OwnerOf(int territoryId) =>
        _owners.TryGetValue(territoryId, out var owner) ? owner : null;

    public int ScoreOf(PlayerId player) =>
        _owners.Where(kv => kv.Value == player).Sum(kv => _map.Get(kv.Key).Value);

    public int? CastleHitPoints(PlayerId player) =>
        _castles.TryGetValue(player, out var c) ? c.HitPoints : null;

    /// <summary>Returns and clears the events produced since the last call.</summary>
    public IReadOnlyList<MatchEvent> DequeueEvents()
    {
        var events = _pendingEvents.ToList();
        _pendingEvents.Clear();
        return events;
    }

    // ------------------------------------------------------------------ commands

    public void Start()
    {
        EnsurePhase(MatchPhase.NotStarted);
        Raise(new MatchStarted(Id, _players));
        foreach (var p in _players) _turnQueue.Enqueue(p.Id);
        SetPhase(MatchPhase.CastlePlacement);
        Raise(new TurnStarted(_turnQueue.Peek(), Phase));
    }

    public void PlaceCastle(PlayerId player, int territoryId)
    {
        EnsurePhase(MatchPhase.CastlePlacement);
        EnsureTurn(player);
        if (!GetValidCastleSites().Contains(territoryId))
            throw new GameRuleException("A castle must go on a free territory that doesn't border another castle.");

        _owners[territoryId] = player;
        _castles[player] = new Castle(territoryId, _options.CastleHitPoints);
        _turnQueue.Dequeue();
        Raise(new CastlePlaced(player, territoryId));

        if (_turnQueue.Count == 0)
            AskExpansionQuestion();
        else
            Raise(new TurnStarted(_turnQueue.Peek(), Phase));
    }

    public AnswerOutcome SubmitAnswer(PlayerId player, Answer answer)
    {
        var q = _question ?? throw new GameRuleException("There is no open question.");
        if (!q.Answerers.Contains(player))
            throw new GameRuleException("You are not answering this question.");
        if (q.Answers.ContainsKey(player))
            throw new GameRuleException("You already answered.");

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
            return AnswerOutcome.TooLate;
        }

        q.Answers[player] = new SubmittedAnswer(answer, now - q.AskedAt);
        Raise(new AnswerReceived(player));

        if (q.Answers.Count == q.Answerers.Count)
            ResolveQuestion();

        return AnswerOutcome.Accepted;
    }

    /// <summary>Called by the host's timer. Closes the open question once its deadline has passed.</summary>
    public bool Tick()
    {
        if (_question is null || _time.GetUtcNow() < _question.Deadline)
            return false;
        ResolveQuestion();
        return true;
    }

    public void ClaimTerritory(PlayerId player, int territoryId)
    {
        EnsurePhase(MatchPhase.ExpansionPicks);
        EnsureTurn(player);
        if (!GetValidClaims(player).Contains(territoryId))
            throw new GameRuleException("You must claim a free territory next to your own (if one exists).");

        _owners[territoryId] = player;
        _turnQueue.Dequeue();
        Raise(new TerritoryClaimed(player, territoryId));

        if (_owners.Count == _map.Count)
        {
            _turnQueue.Clear();
            BeginBattle();
        }
        else if (_turnQueue.Count == 0)
        {
            AskExpansionQuestion();
        }
        else
        {
            Raise(new TurnStarted(_turnQueue.Peek(), Phase));
        }
    }

    public void Attack(PlayerId attacker, int territoryId)
    {
        EnsurePhase(MatchPhase.BattleAttack);
        EnsureTurn(attacker);
        if (!GetValidAttackTargets(attacker).Contains(territoryId))
            throw new GameRuleException("You can only attack an enemy territory that borders yours.");

        var defender = _owners[territoryId];
        _turnQueue.Dequeue();
        _duel = new Duel(attacker, defender, territoryId);
        Raise(new AttackDeclared(attacker, defender, territoryId));

        SetPhase(MatchPhase.BattleDuel);
        Ask(_questions.NextMultipleChoice(), QuestionPurpose.Duel, [attacker, defender]);
    }

    // ------------------------------------------------------------------ queries

    public IReadOnlyList<int> GetValidCastleSites()
    {
        var castleSites = _castles.Values.Select(c => c.TerritoryId).ToHashSet();
        return _map.Territories
            .Select(t => t.Id)
            .Where(id => !_owners.ContainsKey(id))
            .Where(id => !_map.Neighbors(id).Any(castleSites.Contains))
            .Order()
            .ToList();
    }

    public IReadOnlyList<int> GetValidClaims(PlayerId player)
    {
        var free = _map.Territories.Select(t => t.Id).Where(id => !_owners.ContainsKey(id)).Order().ToList();
        var bordering = free.Where(id => _map.Neighbors(id).Any(n => OwnerOf(n) == player)).ToList();
        return bordering.Count > 0 ? bordering : free;
    }

    public IReadOnlyList<int> GetValidAttackTargets(PlayerId player) =>
        _owners
            .Where(kv => kv.Value != player)
            .Select(kv => kv.Key)
            .Where(id => _map.Neighbors(id).Any(n => OwnerOf(n) == player))
            .Order()
            .ToList();

    public MatchSnapshot GetSnapshot() => new(
        Id,
        Phase,
        BattleRound,
        CurrentTurn,
        _players.Select(p => new PlayerState(
            p,
            ScoreOf(p.Id),
            _owners.Count(kv => kv.Value == p.Id),
            _castles.TryGetValue(p.Id, out var c) ? c.TerritoryId : null,
            c?.HitPoints ?? 0,
            IsEliminated(p.Id))).ToList(),
        new Dictionary<int, PlayerId>(_owners),
        CurrentQuestion,
        QuestionDeadline,
        _question is null ? [] : _question.Answerers.Where(a => !_question.Answers.ContainsKey(a)).ToList());

    // ------------------------------------------------------------------ expansion

    private void AskExpansionQuestion()
    {
        SetPhase(MatchPhase.ExpansionQuestion);
        Ask(_questions.NextNumeric(), QuestionPurpose.Expansion, AlivePlayers().ToList());
    }

    private void ResolveExpansion(ActiveQuestion q)
    {
        var ranked = RankNumeric(q);
        var picks = new List<PlayerId>();
        if (ranked.Count >= 1) picks.AddRange([ranked[0], ranked[0]]);
        if (ranked.Count >= 2) picks.Add(ranked[1]);

        var free = _map.Count - _owners.Count;
        picks = picks.Take(free).ToList();

        if (picks.Count == 0)
        {
            // Nobody answered. Ask again; the host is responsible for kicking AFK players.
            AskExpansionQuestion();
            return;
        }

        foreach (var p in picks) _turnQueue.Enqueue(p);
        Raise(new PicksAwarded(picks));
        SetPhase(MatchPhase.ExpansionPicks);
        Raise(new TurnStarted(_turnQueue.Peek(), Phase));
    }

    // ------------------------------------------------------------------ battle

    private void BeginBattle()
    {
        BattleRound = 0;
        AdvanceBattle();
    }

    private void AdvanceBattle()
    {
        while (true)
        {
            if (AlivePlayers().Count() <= 1)
            {
                Finish();
                return;
            }

            while (_turnQueue.Count > 0 && IsEliminated(_turnQueue.Peek()))
                _turnQueue.Dequeue();

            if (_turnQueue.Count > 0)
                break;

            BattleRound++;
            if (BattleRound > _options.BattleRounds)
            {
                Finish();
                return;
            }

            // Rotate who attacks first each round so seat 1 doesn't always lead.
            for (var i = 0; i < PlayerCount; i++)
            {
                var p = _players[(BattleRound - 1 + i) % PlayerCount].Id;
                if (!IsEliminated(p)) _turnQueue.Enqueue(p);
            }
            Raise(new BattleRoundStarted(BattleRound));
        }

        SetPhase(MatchPhase.BattleAttack);
        Raise(new TurnStarted(_turnQueue.Peek(), Phase));
    }

    private void ResolveDuel(ActiveQuestion q)
    {
        var duel = _duel!;
        var mc = (MultipleChoiceQuestion)q.Question;

        bool IsRight(PlayerId p) =>
            q.Answers.TryGetValue(p, out var a) && a.Answer is ChoiceAnswer c && c.OptionIndex == mc.CorrectIndex;

        var attackerRight = IsRight(duel.Attacker);
        var defenderRight = IsRight(duel.Defender);

        if (attackerRight != defenderRight)
        {
            ApplyAttackOutcome(attackerWins: attackerRight);
            return;
        }

        // Both right or both wrong: closest-guess tiebreak.
        Raise(new TiebreakStarted(duel.Attacker, duel.Defender));
        SetPhase(MatchPhase.BattleTiebreak);
        Ask(_questions.NextNumeric(), QuestionPurpose.Tiebreak, [duel.Attacker, duel.Defender]);
    }

    private void ResolveTiebreak(ActiveQuestion q)
    {
        var ranked = RankNumeric(q);
        // Nobody answered → defender holds.
        var attackerWins = ranked.Count > 0 && ranked[0] == _duel!.Attacker;
        ApplyAttackOutcome(attackerWins);
    }

    private void ApplyAttackOutcome(bool attackerWins)
    {
        var duel = _duel!;
        _duel = null;

        if (!attackerWins)
        {
            Raise(new AttackRepelled(duel.Attacker, duel.Defender, duel.TerritoryId));
        }
        else if (_castles.TryGetValue(duel.Defender, out var castle) && castle.TerritoryId == duel.TerritoryId)
        {
            castle.HitPoints--;
            Raise(new CastleDamaged(duel.Defender, duel.Attacker, castle.HitPoints));
            if (castle.HitPoints == 0)
                Eliminate(duel.Defender, by: duel.Attacker);
        }
        else
        {
            _owners[duel.TerritoryId] = duel.Attacker;
            Raise(new TerritoryConquered(duel.Attacker, duel.Defender, duel.TerritoryId));
        }

        AdvanceBattle();
    }

    private void Eliminate(PlayerId player, PlayerId by)
    {
        _eliminationOrder.Add(player);
        var taken = _owners.Where(kv => kv.Value == player).Select(kv => kv.Key).ToList();
        foreach (var t in taken) _owners[t] = by;
        Raise(new PlayerEliminated(player, by, taken.Count));
    }

    private void Finish()
    {
        _turnQueue.Clear();

        var alive = AlivePlayers()
            .OrderByDescending(ScoreOf)
            .ThenByDescending(p => _castles[p].HitPoints)
            .ThenBy(SeatOf);

        // Knocked out later = placed higher.
        var knockedOut = Enumerable.Reverse(_eliminationOrder);

        _standings = alive.Concat(knockedOut)
            .Select((p, i) => new Standing(p, i + 1, ScoreOf(p), IsEliminated(p)))
            .ToList();

        SetPhase(MatchPhase.Finished);
        Raise(new MatchFinished(_standings));
    }

    // ------------------------------------------------------------------ questions

    private void Ask(Question question, QuestionPurpose purpose, IReadOnlyList<PlayerId> answerers)
    {
        var now = _time.GetUtcNow();
        var limit = question.Kind == QuestionKind.MultipleChoice ? _options.MultipleChoiceTime : _options.NumericTime;
        _question = new ActiveQuestion(question, purpose, answerers, now, now + limit);
        Raise(new QuestionAsked(question.ToPublic(), answerers, now + limit));
    }

    private void ResolveQuestion()
    {
        var q = _question!;
        _question = null;

        Raise(new QuestionResolved(q.Question.Id, q.Question.CorrectAnswerText, BuildResults(q)));

        switch (q.Purpose)
        {
            case QuestionPurpose.Expansion: ResolveExpansion(q); break;
            case QuestionPurpose.Duel: ResolveDuel(q); break;
            case QuestionPurpose.Tiebreak: ResolveTiebreak(q); break;
        }
    }

    private static IReadOnlyList<AnswerResult> BuildResults(ActiveQuestion q) =>
        q.Answerers.Select(p =>
        {
            if (!q.Answers.TryGetValue(p, out var a))
                return new AnswerResult(p, null, false, null, null);

            return (q.Question, a.Answer) switch
            {
                (MultipleChoiceQuestion mc, ChoiceAnswer c) =>
                    new AnswerResult(p, c, c.OptionIndex == mc.CorrectIndex, null, a.Elapsed),
                (NumericQuestion n, NumericAnswer v) =>
                    new AnswerResult(p, v, v.Value == n.Answer, Math.Abs(v.Value - n.Answer), a.Elapsed),
                _ => new AnswerResult(p, a.Answer, false, null, a.Elapsed),
            };
        }).ToList();

    /// <summary>Closest first, then fastest, then seat order. Players who didn't answer are excluded.</summary>
    private List<PlayerId> RankNumeric(ActiveQuestion q)
    {
        var target = ((NumericQuestion)q.Question).Answer;
        return q.Answers
            .Where(kv => kv.Value.Answer is NumericAnswer)
            .OrderBy(kv => Math.Abs(((NumericAnswer)kv.Value.Answer).Value - target))
            .ThenBy(kv => kv.Value.Elapsed)
            .ThenBy(kv => SeatOf(kv.Key))
            .Select(kv => kv.Key)
            .ToList();
    }

    // ------------------------------------------------------------------ helpers

    private IEnumerable<PlayerId> AlivePlayers() => _players.Select(p => p.Id).Where(p => !IsEliminated(p));

    private int SeatOf(PlayerId p) => _players.FindIndex(x => x.Id == p);

    private void EnsurePhase(MatchPhase expected)
    {
        if (Phase != expected)
            throw new GameRuleException($"Not allowed during {Phase}.");
    }

    private void EnsureTurn(PlayerId player)
    {
        if (CurrentTurn != player)
            throw new GameRuleException("It's not your turn.");
    }

    private void SetPhase(MatchPhase phase)
    {
        if (Phase == phase) return;
        Phase = phase;
        Raise(new PhaseChanged(phase));
    }

    private void Raise(MatchEvent e) => _pendingEvents.Add(e);

    private sealed class Castle(int territoryId, int hitPoints)
    {
        public int TerritoryId { get; } = territoryId;
        public int HitPoints { get; set; } = hitPoints;
    }

    private sealed record Duel(PlayerId Attacker, PlayerId Defender, int TerritoryId);
}

internal enum QuestionPurpose
{
    Expansion,
    Duel,
    Tiebreak,
}

internal sealed record SubmittedAnswer(Answer Answer, TimeSpan Elapsed);

internal sealed class ActiveQuestion(
    Question question, QuestionPurpose purpose, IReadOnlyList<PlayerId> answerers,
    DateTimeOffset askedAt, DateTimeOffset deadline)
{
    public Question Question { get; } = question;
    public QuestionPurpose Purpose { get; } = purpose;
    public IReadOnlyList<PlayerId> Answerers { get; } = answerers;
    public DateTimeOffset AskedAt { get; } = askedAt;
    public DateTimeOffset Deadline { get; } = deadline;
    public Dictionary<PlayerId, SubmittedAnswer> Answers { get; } = new();
}
