using Triviador.Domain.Questions;

namespace Triviador.Domain.Conquest;

/// <summary>
/// A simple Conquest bot: answers with a set accuracy, expands into the weakest neighbour it can
/// beat comfortably, pushes idle troops from the interior to the front, recruits with what's left
/// and builds the building that fixes its weakest resource.
/// </summary>
public sealed class ConquestBot(PlayerId id, Random rng, double accuracy = 0.6)
{
    public PlayerId Id { get; } = id;

    /// <summary>True if the bot has something to do right now.</summary>
    public bool HasPendingAction(ConquestGame game) =>
        game.IsAnswering(Id)
        || game.CurrentPicker == Id
        || (game.Phase == ConquestPhase.Planning && !game.IsEliminated(Id) && !game.IsReady(Id));

    public bool TryAct(ConquestGame game)
    {
        if (game.IsAnswering(Id) && game.ActiveQuestion is { } q)
        {
            game.SubmitAnswer(Id, Answer(q, game.RemovedOptionFor(Id)));
            return true;
        }

        if (game.CurrentPicker == Id)
        {
            if (game.Phase == ConquestPhase.ColorPick)
            {
                var colors = game.AvailableColors;
                game.PickColor(Id, colors[rng.Next(colors.Count)]);
            }
            else
            {
                game.PickCapital(Id, ChooseCapital(game));
            }
            return true;
        }

        if (game.Phase == ConquestPhase.Planning && !game.IsEliminated(Id) && !game.IsReady(Id))
        {
            game.SubmitPlan(Id, Plan(game));
            return true;
        }

        return false;
    }

    private Answer Answer(Question question, int? removed)
    {
        var knowsIt = rng.NextDouble() < accuracy;
        switch (question)
        {
            case MultipleChoiceQuestion mc:
                if (knowsIt) return new ChoiceAnswer(mc.CorrectIndex);
                var wrong = Enumerable.Range(0, mc.Options.Count).Where(i => i != mc.CorrectIndex && i != removed).ToList();
                return new ChoiceAnswer(wrong[rng.Next(wrong.Count)]);
            case NumericQuestion n:
                var spread = knowsIt ? 0.05 : 0.4;
                var guess = n.Answer == 0 ? rng.NextDouble() : n.Answer * (1 + (rng.NextDouble() * 2 - 1) * spread);
                return new NumericAnswer(Math.Round(guess, 2));
            default:
                throw new NotSupportedException();
        }
    }

    /// <summary>A capital with plenty of unclaimed neighbours to grow into.</summary>
    private int ChooseCapital(ConquestGame game)
    {
        var valid = game.ValidCapitals();
        return valid
            .OrderByDescending(id => game.Map.Neighbors(id).Count(n => game.Region(n).Owner is null))
            .ThenBy(_ => rng.Next())
            .First();
    }

    public IReadOnlyList<Order> Plan(ConquestGame game)
    {
        var orders = new List<Order>();
        var wallet = game.WalletOf(Id);
        var mine = game.Regions.Where(r => r.Owner == Id).ToList();
        var mineIds = mine.Select(r => r.Id).ToHashSet();

        // 1. Attacks: the weakest bordering regions first, with the cheapest weapon that captures.
        var targets = mine
            .SelectMany(r => game.Map.Neighbors(r.Id).Select(n => (From: r.Id, To: game.Region(n))))
            .Where(x => !mineIds.Contains(x.To.Id))
            .GroupBy(x => x.To.Id)
            .Select(g => g.First())
            .OrderBy(x => x.To.Castle)
            .ThenBy(x => x.To.Owner is null ? 0 : 1)
            .ThenBy(_ => rng.Next())
            .ToList();

        var maxAttacks = 1 + mine.Count / 3;
        foreach (var (from, to) in targets)
        {
            if (orders.Count >= maxAttacks) break;
            var weapon = new[] { Weapon.Sword, Weapon.Catapult, Weapon.Trebuchet }
                .Where(w => ConquestOptions.PowerOf(w) > to.Castle)
                .Select(w => (Weapon?)w)
                .FirstOrDefault(w => wallet.Covers(ConquestOptions.CostOf(w!.Value)));
            if (weapon is null) continue;
            orders.Add(new AttackOrder(from, to.Id, weapon.Value));
            wallet -= ConquestOptions.CostOf(weapon.Value);
        }

        // 2. One building a round, aimed at the weakest resource.
        var spot = mine.Where(r => r.Building == Building.None).OrderBy(_ => rng.Next()).FirstOrDefault();
        if (spot is not null && mine.Count >= 2)
        {
            var wanted = new[]
            {
                (Building.Market, wallet.Gold),
                (Building.Barracks, wallet.Manpower),
                (Building.Workshop, wallet.Production),
            }.OrderBy(x => x.Item2).Select(x => x.Item1).First();

            var cost = ConquestOptions.CostOf(wanted);
            if (wallet.Covers(cost))
            {
                orders.Add(new BuildOrder(spot.Id, wanted));
                wallet -= cost;
            }
        }

        // 3. Fortify the most threatened region (enemy players next door count most).
        var threatened = mine
            .Where(r => r.Castle < game.Options.MaxCastle)
            .OrderByDescending(r => game.Map.Neighbors(r.Id).Count(n => game.Region(n).Owner is { } o && o != Id) * 2
                                    + (r.CapitalOf == Id ? 1 : 0))
            .ThenBy(r => r.Castle)
            .FirstOrDefault();
        if (threatened is not null && wallet.Covers(ConquestOptions.FortifyCost(threatened.Castle)))
            orders.Add(new FortifyOrder(threatened.Id));

        return orders;
    }

}
