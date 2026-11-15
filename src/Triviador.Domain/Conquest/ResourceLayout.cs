using Triviador.Domain.Map;

namespace Triviador.Domain.Conquest;

/// <summary>
/// Deals one resource to every region: equal amounts of each, at random, but spread out so that
/// bordering regions share a resource as rarely as possible.
/// </summary>
public static class ResourceLayout
{
    private static readonly Resource[] All = [Resource.Gold, Resource.Manpower, Resource.Production];

    public static IReadOnlyDictionary<int, Resource> Generate(GameMap map, Random rng, int attempts = 40)
    {
        var ids = map.Territories.Select(t => t.Id).Order().ToArray();
        Dictionary<int, Resource>? best = null;
        var bestConflicts = int.MaxValue;

        for (var attempt = 0; attempt < attempts && bestConflicts > 0; attempt++)
        {
            var layout = RandomBalanced(ids, rng);
            Improve(map, layout, rng);
            var conflicts = Conflicts(map, layout);
            if (conflicts < bestConflicts)
            {
                best = layout;
                bestConflicts = conflicts;
            }
        }

        return best!;
    }

    /// <summary>Number of borders whose two regions produce the same resource.</summary>
    public static int Conflicts(GameMap map, IReadOnlyDictionary<int, Resource> layout) =>
        map.Territories.Sum(t => map.Neighbors(t.Id).Count(n => n > t.Id && layout[n] == layout[t.Id]));

    /// <summary>Equal counts of each resource (the remainder goes to random resources), shuffled onto regions.</summary>
    private static Dictionary<int, Resource> RandomBalanced(int[] ids, Random rng)
    {
        var extra = All.ToArray();
        rng.Shuffle(extra);
        var pool = new List<Resource>(ids.Length);
        for (var i = 0; i < ids.Length; i++)
            pool.Add(i < ids.Length / 3 * 3 ? All[i % 3] : extra[i - ids.Length / 3 * 3]);

        var shuffled = pool.ToArray();
        rng.Shuffle(shuffled);
        return ids.Select((id, i) => (id, shuffled[i])).ToDictionary(x => x.id, x => x.Item2);
    }

    /// <summary>Hill-climbing: keep swapping two regions' resources while it lowers the conflict count. Swaps keep the counts balanced.</summary>
    private static void Improve(GameMap map, Dictionary<int, Resource> layout, Random rng)
    {
        var ids = layout.Keys.ToArray();
        int LocalConflicts(int id, Resource r) => map.Neighbors(id).Count(n => layout[n] == r);

        for (var pass = 0; pass < 50; pass++)
        {
            var improved = false;
            rng.Shuffle(ids);
            foreach (var a in ids)
            foreach (var b in ids)
            {
                var ra = layout[a];
                var rb = layout[b];
                if (a == b || ra == rb) continue;

                var before = LocalConflicts(a, ra) + LocalConflicts(b, rb);
                layout[a] = rb;
                layout[b] = ra;
                var after = LocalConflicts(a, rb) + LocalConflicts(b, ra);

                if (after < before) { improved = true; continue; }
                layout[a] = ra;
                layout[b] = rb;
            }

            if (!improved) return;
        }
    }
}
