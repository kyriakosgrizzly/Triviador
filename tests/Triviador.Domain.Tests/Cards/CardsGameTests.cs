using Triviador.Domain.Cards;
using Triviador.Domain.Questions;
using Triviador.Domain.Tests.TestSupport;

namespace Triviador.Domain.Tests.Cards;

/// <summary>Every card question is FixedQuestionSource.Choice: option 0 is right.</summary>
public sealed class FixedCardQuestions : ICardQuestionSource
{
    public MultipleChoiceQuestion Next(CardCategory category) => FixedQuestionSource.Choice;
}

public sealed class CardsHarness
{
    public static readonly PlayerId P1 = new("p1");
    public static readonly PlayerId P2 = new("p2");
    public static readonly PlayerId P3 = new("p3");
    public static readonly ChoiceAnswer Right = new(0);
    public static readonly ChoiceAnswer Wrong = new(1);

    private readonly List<Card> _deck = CardDeck.Build();
    private readonly HashSet<int> _taken = [];

    public CardsGame Game { get; }
    public ManualTimeProvider Clock { get; } = new();

    public CardsHarness(int players = 3, CardsOptions? options = null)
    {
        var infos = new[] { P1, P2, P3 }.Take(players).Select(p => new PlayerInfo(p, p.Value)).ToList();
        // No reveal pauses by default, so tests can check the next turn right after an answer.
        Game = new CardsGame("k1", infos, new FixedCardQuestions(), Clock, new Random(3),
            options ?? new CardsOptions { RevealTime = TimeSpan.Zero, DrawPause = TimeSpan.Zero });
    }

    /// <summary>Takes a not-yet-used card from a fresh deck.</summary>
    public Card Take(CardKind kind, CardCategory? category = null, int? number = null)
    {
        var card = _deck.First(c => c.Kind == kind && (category is null || c.Category == category)
                                    && (number is null || c.Number == number) && !_taken.Contains(c.Id));
        _taken.Add(card.Id);
        return card;
    }

    public Card Num(CardCategory category, int n) => Take(CardKind.Number, category, n);

    /// <summary>Filler cards that never match Geography 5 (History numbers 1–4 and 6–9).</summary>
    public List<Card> Filler(int count) =>
        Enumerable.Range(0, count).Select(i => Num(CardCategory.History, new[] { 1, 2, 3, 4, 6, 7, 8, 9 }[i % 8])).ToList();

    /// <summary>Top card Geography 5, P1 to play. Each player gets the given cards plus two fillers.</summary>
    public CardsHarness Start(List<Card> p1, List<Card>? p2 = null, List<Card>? p3 = null, int fillers = 2)
    {
        var top = Num(CardCategory.Geography, 5);
        var hands = new Dictionary<PlayerId, List<Card>> { [P1] = p1, [P2] = [.. p2 ?? [], .. Filler(fillers)] };
        if (Game.Players.Count > 2) hands[P3] = [.. p3 ?? [], .. Filler(fillers)];
        Game.StartRigged(hands, top, P1);
        return this;
    }

    public void PassHeadStart() => Clock.Advance(Game.Options.HeadStart + TimeSpan.FromMilliseconds(1));
}

public class CardsGameTests
{
    private static readonly PlayerId P1 = CardsHarness.P1, P2 = CardsHarness.P2, P3 = CardsHarness.P3;

    [Fact]
    public void Start_deals_seven_cards_each_and_turns_up_a_number_card()
    {
        var game = new CardsGame("k", [new(P1, "A"), new(P2, "B"), new(P3, "C")], new FixedCardQuestions(), new ManualTimeProvider(), new Random(1));
        game.Start();

        Assert.All(game.Players, p => Assert.Equal(7, game.HandCount(p.Id)));
        Assert.Equal(CardKind.Number, game.TopCard!.Kind);
        Assert.Equal(CardsPhase.Turn, game.Phase);
        Assert.Equal(104 - 21 - 1, game.DrawPileCount);
    }

    [Fact]
    public void Card_must_match_category_number_or_be_wild()
    {
        var h = new CardsHarness();
        var sameCategory = h.Num(CardCategory.Geography, 2);
        var sameNumber = h.Num(CardCategory.Science, 5);
        var neither = h.Num(CardCategory.Science, 3);
        var wild = h.Take(CardKind.PickTopic);
        h.Start([sameCategory, sameNumber, neither, wild]);

        Assert.True(h.Game.CanPlay(sameCategory));
        Assert.True(h.Game.CanPlay(sameNumber));
        Assert.False(h.Game.CanPlay(neither));
        Assert.True(h.Game.CanPlay(wild));
        Assert.Throws<GameRuleException>(() => h.Game.Play(P1, neither.Id));
        Assert.Throws<GameRuleException>(() => h.Game.Play(P2, sameCategory.Id)); // not P2's turn
    }

    [Fact]
    public void Right_answer_lands_the_card_and_passes_the_turn()
    {
        var h = new CardsHarness();
        var card = h.Num(CardCategory.Geography, 2);
        h.Start([card, h.Num(CardCategory.Tech, 1)]);

        h.Game.Play(P1, card.Id);
        Assert.Equal(CardsPhase.Question, h.Game.Phase);
        h.Game.SubmitAnswer(P1, CardsHarness.Right);

        Assert.Equal(card, h.Game.TopCard);
        Assert.Equal(1, h.Game.HandCount(P1));
        Assert.Equal(P2, h.Game.CurrentPlayer);
        Assert.Equal(CardsPhase.Turn, h.Game.Phase);
    }

    [Fact]
    public void Wrong_answer_spends_the_card_without_its_action_and_draws_two()
    {
        var h = new CardsHarness();
        var quiz = h.Take(CardKind.PopQuiz, CardCategory.Geography);
        h.Start([quiz, h.Num(CardCategory.Tech, 1)]);

        h.Game.Play(P1, quiz.Id);
        h.Game.SubmitAnswer(P1, CardsHarness.Wrong);

        Assert.Equal(3, h.Game.HandCount(P1)); // 2 - 1 + 2
        Assert.Equal(quiz, h.Game.TopCard);
        Assert.Null(h.Game.PendingPopQuizTarget);
        Assert.Equal(CardsPhase.Turn, h.Game.Phase);
        Assert.Equal(P2, h.Game.CurrentPlayer);
    }

    [Fact]
    public void Optional_rule_wrong_answer_takes_the_card_back()
    {
        var h = new CardsHarness(options: new CardsOptions { WrongAnswerReturnsCard = true });
        var card = h.Num(CardCategory.Geography, 2);
        h.Start([card, h.Num(CardCategory.Tech, 1)]);

        h.Game.Play(P1, card.Id);
        h.Game.SubmitAnswer(P1, CardsHarness.Wrong);

        Assert.Equal(4, h.Game.HandCount(P1));
        Assert.Contains(card, h.Game.HandOf(P1));
    }

    [Fact]
    public void Others_cannot_answer_during_the_head_start()
    {
        var h = new CardsHarness();
        var card = h.Num(CardCategory.Geography, 2);
        h.Start([card, h.Num(CardCategory.Tech, 1)]);
        h.Game.Play(P1, card.Id);

        Assert.False(h.Game.CanAnswer(P2));
        Assert.Throws<GameRuleException>(() => h.Game.SubmitAnswer(P2, CardsHarness.Right));
        h.PassHeadStart();
        Assert.True(h.Game.CanAnswer(P2));
    }

    [Fact]
    public void Right_steal_lands_the_card_owner_draws_one_stealer_discards_and_takes_the_turn()
    {
        var h = new CardsHarness();
        var card = h.Num(CardCategory.Geography, 2);
        h.Start([card, h.Num(CardCategory.Tech, 1)]);
        h.Game.Play(P1, card.Id);
        h.PassHeadStart();

        h.Game.SubmitAnswer(P3, CardsHarness.Right);

        Assert.Equal(card, h.Game.TopCard);
        Assert.Equal(2, h.Game.HandCount(P1)); // played one, drew one
        Assert.Equal(1, h.Game.HandCount(P3)); // discarded a random one
        Assert.Equal(P3, h.Game.CurrentPlayer);
        Assert.Equal(CardsPhase.Turn, h.Game.Phase);
    }

    [Fact]
    public void Wrong_steal_draws_two_and_the_question_stays_open()
    {
        var h = new CardsHarness();
        var card = h.Num(CardCategory.Geography, 2);
        h.Start([card, h.Num(CardCategory.Tech, 1)]);
        h.Game.Play(P1, card.Id);
        h.PassHeadStart();

        h.Game.SubmitAnswer(P2, CardsHarness.Wrong);
        Assert.Equal(4, h.Game.HandCount(P2));
        Assert.Equal(CardsPhase.Question, h.Game.Phase);
        Assert.Throws<GameRuleException>(() => h.Game.SubmitAnswer(P2, CardsHarness.Right)); // one try each

        h.Game.SubmitAnswer(P1, CardsHarness.Right);
        Assert.Equal(card, h.Game.TopCard);
        Assert.Equal(P2, h.Game.CurrentPlayer);
    }

    [Fact]
    public void No_answer_returns_the_card_and_draws_one()
    {
        var h = new CardsHarness();
        var card = h.Num(CardCategory.Geography, 2);
        h.Start([card, h.Num(CardCategory.Tech, 1)]);
        h.Game.Play(P1, card.Id);

        h.Clock.Advance(h.Game.Options.QuestionTime);
        h.Game.Tick();

        Assert.Equal(3, h.Game.HandCount(P1));
        Assert.Contains(card, h.Game.HandOf(P1));
        Assert.Equal(P2, h.Game.CurrentPlayer);
    }

    [Fact]
    public void Slow_player_draws_one_when_the_turn_runs_out()
    {
        var h = new CardsHarness();
        h.Start([h.Num(CardCategory.Geography, 2), h.Num(CardCategory.Tech, 1)]);

        h.Clock.Advance(h.Game.Options.TurnTime);
        h.Game.Tick();

        Assert.Equal(3, h.Game.HandCount(P1));
        Assert.Equal(P2, h.Game.CurrentPlayer);
    }

    [Fact]
    public void Drawing_ends_the_turn()
    {
        var h = new CardsHarness();
        h.Start([h.Num(CardCategory.Tech, 1)]);
        h.Game.Draw(P1);
        Assert.Equal(2, h.Game.HandCount(P1));
        Assert.Equal(P2, h.Game.CurrentPlayer);
    }

    [Fact]
    public void Lockout_stops_anyone_stealing_the_next_players_question()
    {
        var h = new CardsHarness();
        var lockout = h.Take(CardKind.Lockout, CardCategory.Geography);
        var p2Card = h.Num(CardCategory.Geography, 7);
        h.Start([lockout, h.Num(CardCategory.Tech, 1)], [p2Card]);

        h.Game.Play(P1, lockout.Id);
        h.Game.SubmitAnswer(P1, CardsHarness.Right);
        Assert.Equal(P2, h.Game.LockedOutPlayer);

        h.Game.Play(P2, p2Card.Id);
        h.PassHeadStart();
        Assert.False(h.Game.CanAnswer(P3));
        Assert.Throws<GameRuleException>(() => h.Game.SubmitAnswer(P3, CardsHarness.Right));
    }

    [Fact]
    public void Hot_seat_reverses_and_the_next_player_gets_no_head_start()
    {
        var h = new CardsHarness();
        var hotSeat = h.Take(CardKind.HotSeat, CardCategory.Geography);
        var p3Card = h.Num(CardCategory.Geography, 7);
        h.Start([hotSeat, h.Num(CardCategory.Tech, 1)], p3: [p3Card]);

        h.Game.Play(P1, hotSeat.Id);
        h.Game.SubmitAnswer(P1, CardsHarness.Right);

        Assert.Equal(-1, h.Game.Direction);
        Assert.Equal(P3, h.Game.CurrentPlayer);
        h.Game.Play(P3, p3Card.Id);
        Assert.True(h.Game.CanAnswer(P2)); // open to steals from the first moment
    }

    [Fact]
    public void Pop_quiz_wrong_draws_two_then_the_target_plays_as_normal()
    {
        var h = new CardsHarness();
        var quiz = h.Take(CardKind.PopQuiz, CardCategory.Geography);
        h.Start([quiz, h.Num(CardCategory.Tech, 1)]);

        h.Game.Play(P1, quiz.Id);
        h.Game.SubmitAnswer(P1, CardsHarness.Right);

        Assert.Equal(CardsPhase.PopQuiz, h.Game.Phase);
        Assert.False(h.Game.CanAnswer(P3)); // nobody steals a pop quiz
        h.Game.SubmitAnswer(P2, CardsHarness.Wrong);

        Assert.Equal(4, h.Game.HandCount(P2));
        Assert.Equal(CardsPhase.Turn, h.Game.Phase);
        Assert.Equal(P2, h.Game.CurrentPlayer);
    }

    [Fact]
    public void Pop_quiz_right_costs_nothing()
    {
        var h = new CardsHarness();
        var quiz = h.Take(CardKind.PopQuiz, CardCategory.Geography);
        h.Start([quiz, h.Num(CardCategory.Tech, 1)]);
        h.Game.Play(P1, quiz.Id);
        h.Game.SubmitAnswer(P1, CardsHarness.Right);

        h.Game.SubmitAnswer(P2, CardsHarness.Right);
        Assert.Equal(2, h.Game.HandCount(P2));
        Assert.Equal(P2, h.Game.CurrentPlayer);
    }

    [Fact]
    public void Stolen_action_card_loses_its_effect()
    {
        var h = new CardsHarness();
        var quiz = h.Take(CardKind.PopQuiz, CardCategory.Geography);
        h.Start([quiz, h.Num(CardCategory.Tech, 1)]);
        h.Game.Play(P1, quiz.Id);
        h.PassHeadStart();
        h.Game.SubmitAnswer(P3, CardsHarness.Right);

        Assert.Null(h.Game.PendingPopQuizTarget);
        Assert.Equal(CardsPhase.Turn, h.Game.Phase);
        Assert.Equal(P3, h.Game.CurrentPlayer);
    }

    [Fact]
    public void Pick_topic_sets_the_category()
    {
        var h = new CardsHarness();
        var wild = h.Take(CardKind.PickTopic);
        h.Start([wild, h.Num(CardCategory.Tech, 1)]);

        Assert.Throws<GameRuleException>(() => h.Game.Play(P1, wild.Id)); // no category chosen
        h.Game.Play(P1, wild.Id, CardCategory.Science);
        Assert.Equal(CardCategory.Science, h.Game.OpenQuestion!.Category);
        h.Game.SubmitAnswer(P1, CardsHarness.Right);
        Assert.Equal(CardCategory.Science, h.Game.ActiveCategory);
    }

    [Fact]
    public void Duel_loser_draws_four()
    {
        var h = new CardsHarness();
        var duel = h.Take(CardKind.Duel);
        h.Start([duel, h.Num(CardCategory.Tech, 1)]);

        h.Game.Play(P1, duel.Id, CardCategory.History, P3);
        Assert.Equal(CardsPhase.Duel, h.Game.Phase);
        Assert.False(h.Game.CanAnswer(P2));
        h.Game.SubmitAnswer(P3, CardsHarness.Wrong);
        h.Game.SubmitAnswer(P1, CardsHarness.Right);

        Assert.Equal(6, h.Game.HandCount(P3));
        Assert.Equal(1, h.Game.HandCount(P1));
        Assert.Equal(CardCategory.History, h.Game.ActiveCategory);
        Assert.Equal(P2, h.Game.CurrentPlayer);
    }

    [Fact]
    public void Duel_both_right_slower_player_draws()
    {
        var h = new CardsHarness();
        var duel = h.Take(CardKind.Duel);
        h.Start([duel, h.Num(CardCategory.Tech, 1)]);

        h.Game.Play(P1, duel.Id, CardCategory.History, P2);
        h.Game.SubmitAnswer(P2, CardsHarness.Right);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Game.SubmitAnswer(P1, CardsHarness.Right);

        Assert.Equal(5, h.Game.HandCount(P1));
        Assert.Equal(2, h.Game.HandCount(P2));
    }

    [Fact]
    public void Reveal_pause_holds_the_next_turn_until_it_ends()
    {
        var h = new CardsHarness(options: new CardsOptions());
        var card = h.Num(CardCategory.Geography, 2);
        h.Start([card, h.Num(CardCategory.Tech, 1)]);

        h.Game.Play(P1, card.Id);
        h.Game.SubmitAnswer(P1, CardsHarness.Right);
        Assert.Equal(CardsPhase.Reveal, h.Game.Phase);
        Assert.Equal(P2, h.Game.CurrentPlayer); // shown as "up next"
        Assert.Throws<GameRuleException>(() => h.Game.Draw(P2));

        h.Clock.Advance(h.Game.Options.RevealTime - TimeSpan.FromMilliseconds(100));
        h.Game.Tick();
        Assert.Equal(CardsPhase.Reveal, h.Game.Phase);

        h.Clock.Advance(TimeSpan.FromMilliseconds(100));
        h.Game.Tick();
        Assert.Equal(CardsPhase.Turn, h.Game.Phase);
        Assert.Equal(P2, h.Game.CurrentPlayer);
        Assert.NotNull(h.Game.TurnDeadline); // the turn clock starts only now
    }

    [Fact]
    public void Head_start_is_three_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), CardsOptions.Default.HeadStart);
    }

    [Fact]
    public void Emptying_your_hand_wins()
    {
        var h = new CardsHarness();
        var last = h.Num(CardCategory.Geography, 2);
        h.Start([last]);

        h.Game.Play(P1, last.Id);
        h.Game.SubmitAnswer(P1, CardsHarness.Right);

        Assert.Equal(CardsPhase.Finished, h.Game.Phase);
        Assert.Equal(P1, h.Game.Standings![0].Player);
        Assert.Equal(1, h.Game.Standings[0].Place);
    }

    [Fact]
    public void Stealing_your_way_to_an_empty_hand_wins()
    {
        var h = new CardsHarness(players: 2);
        var card = h.Num(CardCategory.Geography, 2);
        h.Start([card, h.Num(CardCategory.Tech, 1)], fillers: 1);
        h.Game.Play(P1, card.Id);
        h.PassHeadStart();

        h.Game.SubmitAnswer(P2, CardsHarness.Right); // discards its only card

        Assert.Equal(CardsPhase.Finished, h.Game.Phase);
        Assert.Equal(P2, h.Game.Standings![0].Player);
    }

    [Fact]
    public void Public_state_never_needs_the_answer()
    {
        // The open question is only exposed to clients through PublicQuestion.
        var h = new CardsHarness();
        var card = h.Num(CardCategory.Geography, 2);
        h.Start([card]);
        h.Game.Play(P1, card.Id);
        var pub = h.Game.OpenQuestion!.Question.ToPublic();
        Assert.Equal(4, pub.Options!.Count);
    }
}
