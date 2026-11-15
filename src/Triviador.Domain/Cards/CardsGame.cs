using Triviador.Domain.Questions;

namespace Triviador.Domain.Cards;

/// <summary>
/// Quiz Cards: Uno where categories replace colours and every card you play is a question.
///
/// On your turn, play a card matching the pile's category (or number, or action), or a wild, or draw one.
/// The card's question opens with a timer. For the first 3 seconds only you may answer:
/// right lands the card (and its action), wrong takes it back and draws 2.
/// After that anyone may answer. A right steal still lands your card (its action is lost), you draw 1,
/// the stealer discards a random card and it becomes their turn. A wrong steal draws 2.
/// No answer by the deadline: your card comes back and you draw 1. First empty hand wins.
/// </summary>
public sealed class CardsGame
{
    public const int MinPlayers = 2;
    public const int MaxPlayers = 4;

    private readonly ICardQuestionSource _questions;
    private readonly TimeProvider _time;
    private readonly Random _rng;
    private readonly List<PlayerInfo> _players;
    private readonly Dictionary<PlayerId, List<Card>> _hands;
    private readonly List<Card> _drawPile;
    private readonly List<Card> _discard = [];
    private readonly List<CardsEvent> _events = [];

    // Open question bookkeeping.
    private readonly Dictionary<PlayerId, (ChoiceAnswer Answer, bool Correct, TimeSpan Elapsed)> _answers = new();
    private int _questionSerial;

    // Effects waiting for a player's next turn.
    private PlayerId? _lockedOutPlayer;
    private PlayerId? _hotSeatPlayer;
    private (PlayerId Target, CardCategory Category)? _pendingPopQuiz;

    // What happens when the reveal pause ends.
    private Action? _afterReveal;

    public CardsGame(string id, IReadOnlyList<PlayerInfo> players, ICardQuestionSource questions, TimeProvider time, Random rng, CardsOptions? options = null)
    {
        if (players.Count is < MinPlayers or > MaxPlayers)
            throw new ArgumentException($"Quiz Cards needs {MinPlayers}–{MaxPlayers} players.");
        if (players.Select(p => p.Id).Distinct().Count() != players.Count)
            throw new ArgumentException("Players must be distinct.");

        Id = id;
        _players = players.ToList();
        _questions = questions;
        _time = time;
        _rng = rng;
        Options = options ?? CardsOptions.Default;
        _hands = _players.ToDictionary(p => p.Id, _ => new List<Card>());
        _drawPile = CardDeck.Build();
    }

    public string Id { get; }
    public CardsOptions Options { get; }
    public IReadOnlyList<PlayerInfo> Players => _players;
    public CardsPhase Phase { get; private set; } = CardsPhase.NotStarted;
    public PlayerId? CurrentPlayer { get; private set; }

    /// <summary>+1 = seat order, −1 = reversed.</summary>
    public int Direction { get; private set; } = 1;

    public int Turn { get; private set; }
    public DateTimeOffset? TurnDeadline { get; private set; }

    /// <summary>During <see cref="CardsPhase.Reveal"/>: when the next turn starts.</summary>
    public DateTimeOffset? RevealUntil { get; private set; }
    public Card? TopCard => _discard.Count > 0 ? _discard[^1] : null;
    public CardCategory ActiveCategory { get; private set; }
    public int DrawPileCount => _drawPile.Count;
    public CardQuestion? OpenQuestion { get; private set; }
    public PlayerId? LockedOutPlayer => _lockedOutPlayer;
    public PlayerId? HotSeatPlayer => _hotSeatPlayer;
    public PlayerId? PendingPopQuizTarget => _pendingPopQuiz?.Target;
    public IReadOnlyList<CardsStanding>? Standings { get; private set; }

    /// <summary>Who has already answered the open question (right or wrong).</summary>
    public IReadOnlyCollection<PlayerId> Answered => _answers.Keys;

    public IReadOnlyList<Card> HandOf(PlayerId player) => _hands[player];
    public int HandCount(PlayerId player) => _hands[player].Count;

    public IReadOnlyList<CardsEvent> DequeueEvents()
    {
        var copy = _events.ToList();
        _events.Clear();
        return copy;
    }

    // ================================================================== setup

    public void Start()
    {
        if (Phase != CardsPhase.NotStarted) throw new GameRuleException("The game has already started.");

        _rng.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_drawPile));
        for (var i = 0; i < Options.HandSize; i++)
            foreach (var p in _players)
                _hands[p.Id].Add(TakeFromPile()!);

        // The first face-up card is always a number card, so nobody starts with an action against them.
        var first = _drawPile.FindIndex(c => c.Kind == CardKind.Number);
        var top = _drawPile[first];
        _drawPile.RemoveAt(first);
        _discard.Add(top);
        ActiveCategory = top.Category!.Value;

        var starter = _players[_rng.Next(_players.Count)].Id;
        Raise(new CardsStarted(top, ActiveCategory, starter));
        BeginTurn(starter);
    }

    /// <summary>Tests only: start from exact hands and a known top card instead of a shuffle.</summary>
    internal void StartRigged(IReadOnlyDictionary<PlayerId, List<Card>> hands, Card top, PlayerId first)
    {
        if (Phase != CardsPhase.NotStarted) throw new GameRuleException("The game has already started.");
        var used = hands.Values.SelectMany(h => h).Select(c => c.Id).Append(top.Id).ToHashSet();
        _drawPile.RemoveAll(c => used.Contains(c.Id));
        foreach (var (player, cards) in hands) _hands[player].AddRange(cards);
        _discard.Add(top);
        ActiveCategory = top.Category ?? CardCategory.Geography;
        BeginTurn(first);
    }

    // ================================================================== rules

    public bool CanPlay(Card card)
    {
        if (card.IsWild) return true;
        if (card.Category == ActiveCategory) return true;
        var top = TopCard;
        if (top is null || top.IsWild) return false;
        return card.Kind == CardKind.Number
            ? top.Kind == CardKind.Number && top.Number == card.Number
            : top.Kind == card.Kind;
    }

    public IReadOnlyList<Card> PlayableCards(PlayerId player) =>
        Phase == CardsPhase.Turn && CurrentPlayer == player ? _hands[player].Where(CanPlay).ToList() : [];

    /// <summary>True if <paramref name="player"/> may answer the open question right now.</summary>
    public bool CanAnswer(PlayerId player)
    {
        if (OpenQuestion is not { } q || _answers.ContainsKey(player) || !_hands.ContainsKey(player)) return false;
        return q.Purpose switch
        {
            CardsPhase.Question => player == q.Owner || (q.Stealable && _time.GetUtcNow() >= q.StealOpensAt),
            CardsPhase.PopQuiz => player == q.Target,
            CardsPhase.Duel => player == q.Owner || player == q.Target,
            _ => false,
        };
    }

    /// <summary>True if <paramref name="player"/> is one of the people the open question is really for (not a stealer).</summary>
    public bool IsAnswerer(PlayerId player) =>
        OpenQuestion is { } q && !_answers.ContainsKey(player)
        && (player == q.Owner && q.Purpose != CardsPhase.PopQuiz || player == q.Target);

    // ================================================================== commands

    /// <summary>
    /// Put a card forward. Wilds need <paramref name="category"/>; a Duel also needs <paramref name="opponent"/>.
    /// </summary>
    public void Play(PlayerId player, int cardId, CardCategory? category = null, PlayerId? opponent = null)
    {
        EnsureTurn(player);
        var hand = _hands[player];
        var card = hand.FirstOrDefault(c => c.Id == cardId) ?? throw new GameRuleException("That card isn't in your hand.");
        if (!CanPlay(card))
            throw new GameRuleException($"That card doesn't match — play {ActiveCategory}, the same number or action, or a wild.");

        var questionCategory = card.Category ?? category ?? throw new GameRuleException("Choose a category for your wild card.");

        if (card.Kind == CardKind.Duel)
        {
            if (opponent is not { } target || target == player || !_hands.ContainsKey(target))
                throw new GameRuleException("Choose an opponent to duel.");

            hand.Remove(card);
            _discard.Add(card);
            ActiveCategory = questionCategory;
            ClearTurnEffects(player);
            Raise(new CardsQuestionOpened(player, target, card, questionCategory, CardsPhase.Duel));
            OpenQuestionFor(CardsPhase.Duel, player, target, card, questionCategory, stealable: false, headStart: TimeSpan.Zero);
            return;
        }

        hand.Remove(card);
        var stealable = _lockedOutPlayer != player;
        var headStart = _hotSeatPlayer == player ? TimeSpan.Zero : Options.HeadStart;
        _lockedOutPlayer = null;
        _hotSeatPlayer = null;
        Raise(new CardsQuestionOpened(player, null, card, questionCategory, CardsPhase.Question));
        OpenQuestionFor(CardsPhase.Question, player, null, card, questionCategory, stealable, headStart);
    }

    /// <summary>Draw one card instead of playing. Your turn ends.</summary>
    public void Draw(PlayerId player)
    {
        EnsureTurn(player);
        DrawCards(player, 1, DrawReason.Draw);
        // Lockout / Hot Seat only protect or expose the turn they were aimed at.
        ClearTurnEffects(player);
        Then(Options.DrawPause, Next(player), () => BeginTurn(Next(player)));
    }

    public void SubmitAnswer(PlayerId player, Answer answer)
    {
        if (OpenQuestion is not { } q) throw new GameRuleException("There's no question to answer.");
        if (answer is not ChoiceAnswer choice) throw new GameRuleException("Pick one of the options.");
        if (choice.OptionIndex < 0 || choice.OptionIndex >= q.Question.Options.Count) throw new GameRuleException("That option doesn't exist.");
        if (_answers.ContainsKey(player)) throw new GameRuleException("You've already answered.");
        if (!CanAnswer(player))
        {
            if (q.Purpose == CardsPhase.Question && player != q.Owner && !q.Stealable)
                throw new GameRuleException("Locked out — nobody can steal this one.");
            if (q.Purpose == CardsPhase.Question && player != q.Owner)
                throw new GameRuleException("Wait — the owner has a head start.");
            throw new GameRuleException("This question isn't yours.");
        }

        var now = _time.GetUtcNow();
        if (now > q.Deadline) throw new GameRuleException("Too late.");
        var correct = choice.OptionIndex == q.Question.CorrectIndex;
        _answers[player] = (choice, correct, now - q.AskedAt);

        switch (q.Purpose)
        {
            case CardsPhase.Question:
                if (player == q.Owner)
                {
                    if (correct) ResolveLanded(q, stealer: null);
                    else if (Options.WrongAnswerReturnsCard) ResolveReturned(q, Options.WrongAnswerDraw, DrawReason.WrongAnswer);
                    else ResolveFizzled(q);
                }
                else if (correct)
                {
                    ResolveLanded(q, stealer: player);
                }
                else
                {
                    DrawCards(player, Options.WrongStealDraw, DrawReason.WrongSteal);
                }
                break;

            case CardsPhase.PopQuiz:
                ResolvePopQuiz(q);
                break;

            case CardsPhase.Duel:
                if (_answers.ContainsKey(q.Owner) && _answers.ContainsKey(q.Target!.Value)) ResolveDuel(q);
                break;
        }
    }

    /// <summary>Call regularly: handles question deadlines and players who don't move.</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        if (Phase == CardsPhase.Reveal && RevealUntil is { } until && now >= until)
        {
            var next = _afterReveal;
            _afterReveal = null;
            RevealUntil = null;
            next?.Invoke();
            return;
        }

        if (OpenQuestion is { } q && now >= q.Deadline)
        {
            switch (q.Purpose)
            {
                case CardsPhase.Question: ResolveReturned(q, Options.NoAnswerDraw, DrawReason.NoAnswer); break;
                case CardsPhase.PopQuiz: ResolvePopQuiz(q); break;
                case CardsPhase.Duel: ResolveDuel(q); break;
            }
            return;
        }

        if (Phase == CardsPhase.Turn && TurnDeadline is { } deadline && now >= deadline && CurrentPlayer is { } p)
        {
            DrawCards(p, 1, DrawReason.TurnTimeout);
            ClearTurnEffects(p);
            Then(Options.DrawPause, Next(p), () => BeginTurn(Next(p)));
        }
    }

    // ================================================================== resolution

    private void ResolveLanded(CardQuestion q, PlayerId? stealer)
    {
        CloseQuestion(q);
        _discard.Add(q.Card);
        ActiveCategory = q.Category;
        Raise(new CardsCardLanded(q.Owner, q.Card, q.Category, stealer, Fizzled: false));

        if (stealer is { } thief)
        {
            // Stolen: the card counts as played but its action is lost.
            DrawCards(q.Owner, Options.StolenDraw, DrawReason.TooSlow);
            var hand = _hands[thief];
            if (hand.Count > 0)
            {
                var dumped = hand[_rng.Next(hand.Count)];
                hand.Remove(dumped);
                _discard.Insert(Math.Max(0, _discard.Count - 1), dumped); // under the played card
                Raise(new CardsStealDiscard(thief, dumped));
            }
            if (TryFinish()) return;
            Then(Options.RevealTime, thief, () => BeginTurn(thief));
            return;
        }

        var owner = q.Owner;
        switch (q.Card.Kind)
        {
            case CardKind.Lockout:
                _lockedOutPlayer = Next(owner);
                Raise(new CardsLockout(_lockedOutPlayer.Value));
                break;
            case CardKind.HotSeat:
                Direction = -Direction;
                // With two players a reverse points back at the same opponent, which is fine.
                _hotSeatPlayer = Next(owner);
                Raise(new CardsReversed(Direction, _hotSeatPlayer.Value));
                break;
            case CardKind.PopQuiz:
                _pendingPopQuiz = (Next(owner), q.Category);
                break;
        }

        if (TryFinish()) return;
        var next = Next(owner);
        Then(Options.RevealTime, next, () => BeginTurn(next));
    }

    /// <summary>Wrong answer: the card is spent without its action, and the owner draws.</summary>
    private void ResolveFizzled(CardQuestion q)
    {
        CloseQuestion(q);
        _discard.Add(q.Card);
        ActiveCategory = q.Category;
        Raise(new CardsCardLanded(q.Owner, q.Card, q.Category, StolenBy: null, Fizzled: true));
        DrawCards(q.Owner, Options.WrongAnswerDraw, DrawReason.WrongAnswer);
        var next = Next(q.Owner);
        Then(Options.RevealTime, next, () => BeginTurn(next));
    }

    private void ResolveReturned(CardQuestion q, int draw, DrawReason reason)
    {
        CloseQuestion(q);
        _hands[q.Owner].Add(q.Card);
        Raise(new CardsCardReturned(q.Owner, q.Card));
        DrawCards(q.Owner, draw, reason);
        var next = Next(q.Owner);
        Then(Options.RevealTime, next, () => BeginTurn(next));
    }

    private void ResolvePopQuiz(CardQuestion q)
    {
        var target = q.Target!.Value;
        var correct = _answers.TryGetValue(target, out var a) && a.Correct;
        CloseQuestion(q);
        Raise(new CardsPopQuizResult(target, correct));
        if (!correct) DrawCards(target, Options.PopQuizDraw, DrawReason.PopQuiz);
        // The quiz doesn't cost the turn: the target now plays as normal.
        Then(Options.RevealTime, target, () => StartTurn(target));
    }

    private void ResolveDuel(CardQuestion q)
    {
        var owner = q.Owner;
        var target = q.Target!.Value;
        _answers.TryGetValue(owner, out var oa);
        _answers.TryGetValue(target, out var ta);
        var ownerRight = _answers.ContainsKey(owner) && oa.Correct;
        var targetRight = _answers.ContainsKey(target) && ta.Correct;
        CloseQuestion(q);

        (PlayerId? loser, string reason) = (ownerRight, targetRight) switch
        {
            (true, false) => (target, "wrong"),
            (false, true) => (owner, "wrong"),
            (true, true) => oa.Elapsed <= ta.Elapsed ? (target, "slower") : (owner, "slower"),
            _ => ((PlayerId?)null, "both wrong"),
        };

        Raise(new CardsDuelResult(owner, target, loser, reason));
        if (loser is { } l) DrawCards(l, Options.DuelDraw, DrawReason.Duel);
        if (TryFinish()) return;
        var next = Next(owner);
        Then(Options.RevealTime, next, () => BeginTurn(next));
    }

    private void CloseQuestion(CardQuestion q)
    {
        var results = _players
            .Where(p => _answers.ContainsKey(p.Id) || p.Id == q.Owner && q.Purpose != CardsPhase.PopQuiz || p.Id == q.Target)
            .Select(p => _answers.TryGetValue(p.Id, out var a)
                ? new AnswerResult(p.Id, a.Answer, a.Correct, null, a.Elapsed)
                : new AnswerResult(p.Id, null, false, null, null))
            .ToList();

        Raise(new CardsQuestionResolved(q.Question.Id, q.Question.CorrectAnswerText, q.Question.CorrectIndex, results));
        OpenQuestion = null;
        _answers.Clear();
    }

    // ================================================================== turn flow

    /// <summary>Runs <paramref name="next"/> after a pause (the Reveal phase), or straight away if the pause is 0.</summary>
    private void Then(TimeSpan pause, PlayerId upNext, Action next)
    {
        if (pause <= TimeSpan.Zero)
        {
            next();
            return;
        }

        Phase = CardsPhase.Reveal;
        CurrentPlayer = upNext;
        TurnDeadline = null;
        RevealUntil = _time.GetUtcNow() + pause;
        _afterReveal = next;
    }

    private void BeginTurn(PlayerId player)
    {
        Turn++;
        if (Turn > Options.MaxTurns)
        {
            Finish();
            return;
        }

        if (_pendingPopQuiz is { } quiz && quiz.Target == player)
        {
            _pendingPopQuiz = null;
            var question = TakeQuestion(quiz.Category);
            Raise(new CardsQuestionOpened(player, player, TopCard!, quiz.Category, CardsPhase.PopQuiz));
            CurrentPlayer = player;
            TurnDeadline = null;
            OpenQuestionWith(CardsPhase.PopQuiz, player, player, TopCard!, quiz.Category, question, stealable: false, TimeSpan.Zero);
            return;
        }

        StartTurn(player);
    }

    private void StartTurn(PlayerId player)
    {
        Phase = CardsPhase.Turn;
        CurrentPlayer = player;
        TurnDeadline = _time.GetUtcNow() + Options.TurnTime;
        Raise(new CardsTurnStarted(player, Turn));
    }

    private void OpenQuestionFor(CardsPhase purpose, PlayerId owner, PlayerId? target, Card card, CardCategory category, bool stealable, TimeSpan headStart) =>
        OpenQuestionWith(purpose, owner, target, card, category, TakeQuestion(category), stealable, headStart);

    private void OpenQuestionWith(CardsPhase purpose, PlayerId owner, PlayerId? target, Card card, CardCategory category,
        MultipleChoiceQuestion question, bool stealable, TimeSpan headStart)
    {
        var now = _time.GetUtcNow();
        _answers.Clear();
        Phase = purpose;
        TurnDeadline = null;
        OpenQuestion = new CardQuestion(
            ++_questionSerial, purpose, owner, target, card, category, question,
            now, now + headStart, now + Options.QuestionTime, stealable);
    }

    private MultipleChoiceQuestion TakeQuestion(CardCategory category) => _questions.Next(category);

    private void EnsureTurn(PlayerId player)
    {
        if (Phase != CardsPhase.Turn) throw new GameRuleException("Wait for the question to finish.");
        if (CurrentPlayer != player) throw new GameRuleException("It's not your turn.");
    }

    private void ClearTurnEffects(PlayerId player)
    {
        if (_lockedOutPlayer == player) _lockedOutPlayer = null;
        if (_hotSeatPlayer == player) _hotSeatPlayer = null;
    }

    public PlayerId Next(PlayerId player)
    {
        var i = _players.FindIndex(p => p.Id == player);
        var n = _players.Count;
        return _players[((i + Direction) % n + n) % n].Id;
    }

    // ================================================================== cards

    private void DrawCards(PlayerId player, int count, DrawReason reason)
    {
        var drawn = 0;
        for (var i = 0; i < count; i++)
        {
            var card = TakeFromPile();
            if (card is null) break;
            _hands[player].Add(card);
            drawn++;
        }
        Raise(new CardsDrew(player, drawn, reason));
    }

    private Card? TakeFromPile()
    {
        if (_drawPile.Count == 0 && _discard.Count > 1)
        {
            // Reshuffle everything under the top card.
            var top = _discard[^1];
            _drawPile.AddRange(_discard.Take(_discard.Count - 1));
            _discard.Clear();
            _discard.Add(top);
            _rng.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_drawPile));
        }

        if (_drawPile.Count == 0) return null;
        var card = _drawPile[^1];
        _drawPile.RemoveAt(_drawPile.Count - 1);
        return card;
    }

    // ================================================================== end

    private bool TryFinish()
    {
        if (!_players.Any(p => _hands[p.Id].Count == 0)) return false;
        Finish();
        return true;
    }

    private void Finish()
    {
        Phase = CardsPhase.Finished;
        CurrentPlayer = null;
        TurnDeadline = null;
        RevealUntil = null;
        _afterReveal = null;
        OpenQuestion = null;
        _answers.Clear();

        var order = _players.OrderBy(p => _hands[p.Id].Count).ToList();
        var standings = new List<CardsStanding>();
        for (var i = 0; i < order.Count; i++)
        {
            var count = _hands[order[i].Id].Count;
            // Equal hands share a place.
            var place = i > 0 && _hands[order[i - 1].Id].Count == count ? standings[i - 1].Place : i + 1;
            standings.Add(new CardsStanding(order[i].Id, place, count));
        }

        Standings = standings;
        Raise(new CardsFinished(standings));
    }

    private void Raise(CardsEvent e) => _events.Add(e);
}
