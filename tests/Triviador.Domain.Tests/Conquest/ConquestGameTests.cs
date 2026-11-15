using Triviador.Domain.Conquest;
using Triviador.Domain.Questions;
using static Triviador.Domain.Tests.Conquest.ConquestHarness;

namespace Triviador.Domain.Tests.Conquest;

public class ConquestSetupTests
{
    [Fact]
    public void Player_counts_outside_2_to_4_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new ConquestHarness(players: 1));

        var five = Enumerable.Range(1, 5).Select(i => new PlayerInfo(new PlayerId($"x{i}"), $"X{i}")).ToList();
        Assert.Throws<ArgumentException>(() => new ConquestGame(
            "c", five, new Triviador.Domain.Tests.TestSupport.FixedQuestionSource(), new Triviador.Domain.Tests.TestSupport.ManualTimeProvider(), new Random(1)));
    }

    [Fact]
    public void Start_deals_resources_and_neutral_garrisons_then_asks_everyone()
    {
        var h = new ConquestHarness();
        h.Game.Start();

        Assert.Equal(ConquestPhase.ColorQuestion, h.Game.Phase);
        Assert.Equal(3, h.Game.Answerers.Count);
        Assert.All(h.Game.Regions, r =>
        {
            Assert.Null(r.Owner);
            Assert.True(r.Castle is 0 or 1);
        });
    }

    [Fact]
    public void Closest_answer_picks_a_colour_first()
    {
        var h = new ConquestHarness();
        h.Game.Start();
        h.AnswerNumericInOrder(P3, P1, P2); // P3 closest

        Assert.Equal(ConquestPhase.ColorPick, h.Game.Phase);
        Assert.Equal(P3, h.Game.CurrentPicker);
        Assert.Throws<GameRuleException>(() => h.Game.PickColor(P1, "azure"));

        h.Game.PickColor(P3, "emerald");
        Assert.Equal(P1, h.Game.CurrentPicker);
        Assert.Throws<GameRuleException>(() => h.Game.PickColor(P1, "emerald")); // taken
        Assert.DoesNotContain("emerald", h.Game.AvailableColors);
    }

    [Fact]
    public void Slow_pickers_get_a_colour_automatically()
    {
        var h = new ConquestHarness();
        h.Game.Start();
        h.AnswerNumericInOrder(P1, P2, P3);
        h.Clock.Advance(TimeSpan.FromSeconds(21));

        Assert.True(h.Game.Tick());
        Assert.NotNull(h.Game.ColorOf(P1));
        Assert.Equal(P2, h.Game.CurrentPicker);
    }

    [Fact]
    public void Capitals_are_picked_in_the_same_order_and_cannot_touch()
    {
        var h = new ConquestHarness();
        h.Game.Start();
        h.AnswerNumericInOrder(P2, P1, P3);
        foreach (var p in new[] { P2, P1, P3 }) h.Game.PickColor(p, h.Game.AvailableColors[0]);

        Assert.Equal(ConquestPhase.CapitalPick, h.Game.Phase);
        Assert.Equal(P2, h.Game.CurrentPicker);
        h.Game.PickCapital(P2, R("caucasus"));

        Assert.Throws<GameRuleException>(() => h.Game.PickCapital(P1, R("russia")));   // borders P2's capital
        Assert.Throws<GameRuleException>(() => h.Game.PickCapital(P1, R("caucasus"))); // taken
        Assert.DoesNotContain(R("persia"), h.Game.ValidCapitals());

        h.Game.PickCapital(P1, R("brazil"));
        h.Game.PickCapital(P3, R("china"));

        var capital = h.Game.Region(R("caucasus"));
        Assert.Equal(P2, capital.Owner);
        Assert.Equal(P2, capital.CapitalOf);
        Assert.Equal(2, capital.Castle);
        Assert.Equal(ConquestPhase.SupplyQuestion, h.Game.Phase);
        Assert.Equal(1, h.Game.Round);
    }
}

public class ConquestRoundTests
{
    private static ConquestHarness ThreePlayers() => new ConquestHarness().SetUp("caucasus", "brazil", "china");

    [Fact]
    public void Supply_question_pays_bonuses_and_income_then_opens_planning()
    {
        var h = ThreePlayers();
        var caucasus = h.Game.Region(R("caucasus")).Resource;

        h.Supply(P1, P2, P3);

        Assert.Equal(ConquestPhase.Planning, h.Game.Phase);
        Assert.Equal(new[] { P1, P2, P3 }, h.Game.Initiative);

        // Start 4 each + 1st place 2 each + capital 1 each + the region's own resource.
        Assert.Equal(Wallet.Each(4 + 2 + 1) + Wallet.Of(caucasus, 1), h.Game.WalletOf(P1));
        Assert.Equal(Wallet.Each(4 + 1 + 1) + Wallet.Of(h.Game.Region(R("brazil")).Resource, 1), h.Game.WalletOf(P2));
    }

    [Fact]
    public void Two_player_games_only_reward_the_winner()
    {
        var h = new ConquestHarness(players: 2).SetUp("caucasus", "brazil");
        h.Supply(P2, P1);
        Assert.Equal(Wallet.Each(4 + 1) + Wallet.Of(h.Game.Region(R("caucasus")).Resource, 1), h.Game.WalletOf(P1));
    }

    [Fact]
    public void Plans_are_checked_before_they_are_accepted()
    {
        var h = ThreePlayers().Supply(P1, P2, P3);
        var cap = R("caucasus");

        Assert.Throws<GameRuleException>(() => h.Plan(P1, new AttackOrder(cap, R("brazil"), Weapon.Sword)));      // not bordering
        Assert.Throws<GameRuleException>(() => h.Plan(P1, new AttackOrder(R("russia"), cap, Weapon.Sword)));      // not yours
        Assert.Throws<GameRuleException>(() => h.Plan(P1, new AttackOrder(cap, cap, Weapon.Sword)));              // your own
        Assert.Throws<GameRuleException>(() => h.Plan(P1, new FortifyOrder(cap), new FortifyOrder(cap)));         // one level a round
        Assert.Throws<GameRuleException>(() => h.Plan(P1,                                                          // can't afford
            new AttackOrder(cap, R("russia"), Weapon.Trebuchet), new AttackOrder(cap, R("persia"), Weapon.Trebuchet)));
        Assert.Throws<GameRuleException>(() => h.Plan(P1, new BuildOrder(cap, Building.Market), new BuildOrder(cap, Building.Workshop)));
        Assert.False(h.Game.IsReady(P1));
    }

    [Fact]
    public void Quiet_rounds_skip_the_battle_question()
    {
        var h = ThreePlayers().Supply(P1, P2, P3);
        h.Plan(P1);
        h.Plan(P2);
        Assert.Equal(ConquestPhase.Planning, h.Game.Phase);
        h.Plan(P3);
        Assert.Equal(ConquestPhase.SupplyQuestion, h.Game.Phase);
        Assert.Equal(2, h.Game.Round);
    }

    [Fact]
    public void Fortifying_raises_the_castle_and_the_clock_ends_planning()
    {
        var h = ThreePlayers().Supply(P1, P2, P3);
        var before = h.Game.WalletOf(P1);
        h.Plan(P1, new FortifyOrder(R("caucasus")));
        h.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True(h.Game.Tick());

        Assert.Equal(2, h.Game.Round);
        Assert.Equal(3, h.Game.Region(R("caucasus")).Castle);
        Assert.True(h.Game.WalletOf(P1).Production < before.Production + 3); // paid 4 Production for level 3

        h.Supply(P1, P2, P3);
        Assert.Throws<GameRuleException>(() => h.Plan(P1, new FortifyOrder(R("caucasus")))); // already level 3
    }

    [Fact]
    public void A_right_answer_takes_a_neutral_region()
    {
        var h = ThreePlayers().Supply(P1, P2, P3);
        h.Plan(P1, new AttackOrder(R("caucasus"), R("russia"), Weapon.Catapult)); // power 2 beats any neutral castle
        h.Plan(P2);
        h.Plan(P3);

        Assert.Equal(ConquestPhase.BattleQuestion, h.Game.Phase);
        Assert.Equal(new[] { P1 }, h.Game.Answerers.ToArray());

        h.Game.SubmitAnswer(P1, Right);
        Assert.Equal(P1, h.Game.Region(R("russia")).Owner);
        Assert.Equal(0, h.Game.Region(R("russia")).Castle);
    }

    [Fact]
    public void A_wrong_answer_wastes_the_weapon()
    {
        var h = ThreePlayers().Supply(P1, P2, P3);
        var before = h.Game.WalletOf(P1);
        h.Plan(P1, new AttackOrder(R("caucasus"), R("russia"), Weapon.Catapult));
        h.Plan(P2);
        h.Plan(P3);
        h.Game.SubmitAnswer(P1, Wrong);

        Assert.Null(h.Game.Region(R("russia")).Owner);
        Assert.Equal(before - ConquestOptions.CostOf(Weapon.Catapult), h.Game.WalletOf(P1)); // next round's income not paid yet
    }

    [Fact]
    public void Buildings_cost_resources_and_pay_out_next_round()
    {
        var h = ThreePlayers().Supply(P1, P2, P3);
        var before = h.Game.WalletOf(P1);
        h.Plan(P1, new BuildOrder(R("caucasus"), Building.Market));
        h.Plan(P2);
        h.Plan(P3);

        Assert.Equal(Building.Market, h.Game.Region(R("caucasus")).Building);
        Assert.Equal(before - ConquestOptions.CostOf(Building.Market), h.Game.WalletOf(P1));
        Assert.Equal(Wallet.Of(Resource.Gold, 1), h.Game.IncomeOf(P1) - Wallet.Of(h.Game.Region(R("caucasus")).Resource, 1) - Wallet.Each(1));
    }
}

public class ConquestBattleTests
{
    /// <summary>P1 (Brazil) takes West Africa in round 1, which borders P2's capital (Central Africa, castle 2).</summary>
    private static ConquestHarness Frontline(int players = 2)
    {
        var capitals = new[] { "brazil", "central-africa", "china" }.Take(players).ToArray();
        var h = new ConquestHarness(players).SetUp(capitals);
        var order = ConquestHarness.All.Take(players).ToArray();
        h.Supply(order);
        h.Plan(P1, new AttackOrder(R("brazil"), R("west-africa"), Weapon.Catapult));
        foreach (var p in order.Skip(1)) h.Plan(p);
        h.Game.SubmitAnswer(P1, Right);
        Assert.Equal(P1, h.Game.Region(R("west-africa")).Owner);

        // Round 2: rich enough for anything.
        h.Supply(order);
        return h;
    }

    private static BattleReport LastBattle(ConquestHarness h) => h.Game.DequeueEvents().OfType<BattleFought>().Last().Report;

    [Fact]
    public void Player_battles_ask_both_sides_and_nobody_else()
    {
        var h = Frontline(players: 3);
        h.Plan(P1, new AttackOrder(R("west-africa"), R("central-africa"), Weapon.Sword));
        h.Plan(P2);
        h.Plan(P3);

        Assert.Equal(ConquestPhase.BattleQuestion, h.Game.Phase);
        Assert.Equal(new[] { P1, P2 }, h.Game.Answerers.OrderBy(p => p.Value).ToArray());
        Assert.Throws<GameRuleException>(() => h.Game.SubmitAnswer(P3, Right));
    }

    [Fact]
    public void A_sword_that_lands_knocks_one_level_off_the_castle()
    {
        var h = Frontline();
        h.Plan(P1, new AttackOrder(R("west-africa"), R("central-africa"), Weapon.Sword));
        h.Plan(P2);
        h.Game.SubmitAnswer(P1, Right);
        h.Game.SubmitAnswer(P2, Wrong);

        var report = LastBattle(h);
        Assert.True(report.HitLanded);
        Assert.False(report.Captured);
        Assert.Equal(2, report.CastleBefore);
        Assert.Equal(1, h.Game.Region(R("central-africa")).Castle);
        Assert.Equal(P2, h.Game.Region(R("central-africa")).Owner);
    }

    [Fact]
    public void A_trebuchet_takes_a_full_castle_in_one_hit()
    {
        var h = Frontline();
        h.Plan(P1, new AttackOrder(R("west-africa"), R("central-africa"), Weapon.Trebuchet));
        h.Plan(P2);
        h.Game.SubmitAnswer(P1, Right);
        h.Game.SubmitAnswer(P2, Wrong);

        var events = h.Game.DequeueEvents();
        Assert.Contains(events, e => e is CapitalCaptured { From: var f } && f == P2);
        Assert.Contains(events, e => e is ConquestPlayerEliminated { Player: var p } && p == P2);
        Assert.Equal(ConquestPhase.Finished, h.Game.Phase);
        Assert.Equal(new[] { P1, P2 }, h.Game.Standings!.Select(s => s.Player).ToArray());
    }

    [Fact]
    public void Both_right_the_faster_answer_wins()
    {
        var h = Frontline();
        h.Plan(P1, new AttackOrder(R("west-africa"), R("central-africa"), Weapon.Sword));
        h.Plan(P2);
        h.Game.SubmitAnswer(P2, Right);                 // defender first
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Game.SubmitAnswer(P1, Right);

        var report = LastBattle(h);
        Assert.False(report.HitLanded);
        Assert.Equal("both right, defender faster", report.Reason);
        Assert.Equal(2, h.Game.Region(R("central-africa")).Castle);
    }

    [Fact]
    public void Faster_attacker_wins_when_both_are_right()
    {
        var h = Frontline();
        h.Plan(P1, new AttackOrder(R("west-africa"), R("central-africa"), Weapon.Sword));
        h.Plan(P2);
        h.Game.SubmitAnswer(P1, Right);
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Game.SubmitAnswer(P2, Right);

        Assert.True(LastBattle(h).HitLanded);
        Assert.Equal(1, h.Game.Region(R("central-africa")).Castle);
    }

    [Fact]
    public void Two_hits_in_one_round_add_up()
    {
        var h = Frontline();
        h.Plan(P1,
            new AttackOrder(R("west-africa"), R("central-africa"), Weapon.Catapult),  // 2 → 0
            new AttackOrder(R("west-africa"), R("central-africa"), Weapon.Sword));    // 1 > 0: captured
        h.Plan(P2);
        h.Game.SubmitAnswer(P1, Right);
        h.Game.SubmitAnswer(P2, Wrong);

        Assert.Equal(P1, h.Game.Region(R("central-africa")).Owner);
    }

    [Fact]
    public void Attacks_from_a_region_lost_earlier_are_cancelled_and_refunded()
    {
        // P2 has initiative and takes West Africa back before P1's attack from there happens.
        var h = new ConquestHarness(players: 2).SetUp("brazil", "central-africa");
        h.Supply(P1, P2);
        h.Plan(P1, new AttackOrder(R("brazil"), R("west-africa"), Weapon.Catapult));
        h.Plan(P2);
        h.Game.SubmitAnswer(P1, Right);

        h.Supply(P2, P1);
        var walletBefore = h.Game.WalletOf(P1);
        h.Plan(P1, new AttackOrder(R("west-africa"), R("north-africa"), Weapon.Sword));
        h.Plan(P2, new AttackOrder(R("central-africa"), R("west-africa"), Weapon.Catapult));
        h.Game.SubmitAnswer(P2, Right);
        h.Game.SubmitAnswer(P1, Wrong);

        var events = h.Game.DequeueEvents();
        Assert.Equal(P2, h.Game.Region(R("west-africa")).Owner);
        Assert.Contains(events, e => e is AttackCancelled { Player: var p } && p == P1);
        Assert.Equal(walletBefore, h.Game.WalletOf(P1)); // the sword was paid for, then refunded
    }

    [Fact]
    public void Game_ends_after_the_last_round_with_scores()
    {
        var h = new ConquestHarness(players: 2, seed: 4, new ConquestOptions { MaxRounds = 2 }).SetUp("brazil", "china");
        for (var round = 0; round < 2; round++)
        {
            h.Supply(P1, P2);
            h.Plan(P1);
            h.Plan(P2);
        }

        Assert.Equal(ConquestPhase.Finished, h.Game.Phase);
        // One capital each: 2 (region) + 2 (castle level 2) + 3 (capital) = 7.
        Assert.All(h.Game.Standings!, s => Assert.Equal(7, s.Score));
        Assert.Equal(P1, h.Game.Standings![0].Player); // tie broken by initiative
    }
}
