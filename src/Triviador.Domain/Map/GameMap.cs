namespace Triviador.Domain.Map;

/// <param name="Column">Layout column, for drawing only.</param>
/// <param name="Row">Layout row, for drawing only.</param>
public sealed record Territory(int Id, string Name, int Value, int Column = 0, int Row = 0);

/// <summary>
/// An immutable map: territories plus an undirected adjacency graph.
/// Validated on creation so the engine can assume a sane, connected board.
/// </summary>
public sealed class GameMap
{
    private readonly Dictionary<int, Territory> _territories;
    private readonly Dictionary<int, HashSet<int>> _neighbors;

    private GameMap(string name, Dictionary<int, Territory> territories, Dictionary<int, HashSet<int>> neighbors)
    {
        Name = name;
        _territories = territories;
        _neighbors = neighbors;
    }

    public string Name { get; }
    public IReadOnlyCollection<Territory> Territories => _territories.Values;
    public int Count => _territories.Count;

    public Territory Get(int id) =>
        _territories.TryGetValue(id, out var t) ? t : throw new GameRuleException($"Territory {id} does not exist.");

    public bool Contains(int id) => _territories.ContainsKey(id);

    public IReadOnlySet<int> Neighbors(int id) =>
        _neighbors.TryGetValue(id, out var n) ? n : throw new GameRuleException($"Territory {id} does not exist.");

    public bool AreAdjacent(int a, int b) => Neighbors(a).Contains(b);

    public static GameMap Create(string name, IEnumerable<Territory> territories, IEnumerable<(int A, int B)> edges)
    {
        var byId = new Dictionary<int, Territory>();
        foreach (var t in territories)
        {
            if (!byId.TryAdd(t.Id, t))
                throw new ArgumentException($"Duplicate territory id {t.Id}.");
            if (t.Value <= 0)
                throw new ArgumentException($"Territory {t.Id} must have a positive value.");
        }

        if (byId.Count == 0)
            throw new ArgumentException("A map needs at least one territory.");

        var neighbors = byId.Keys.ToDictionary(id => id, _ => new HashSet<int>());
        foreach (var (a, b) in edges)
        {
            if (a == b) throw new ArgumentException($"Territory {a} cannot border itself.");
            if (!byId.ContainsKey(a) || !byId.ContainsKey(b))
                throw new ArgumentException($"Edge {a}-{b} references an unknown territory.");
            neighbors[a].Add(b);
            neighbors[b].Add(a);
        }

        // Connectivity check (BFS) — a disconnected map could leave a player with no attack targets.
        var seen = new HashSet<int>();
        var queue = new Queue<int>();
        var start = byId.Keys.First();
        queue.Enqueue(start);
        seen.Add(start);
        while (queue.Count > 0)
        {
            foreach (var n in neighbors[queue.Dequeue()])
                if (seen.Add(n)) queue.Enqueue(n);
        }

        if (seen.Count != byId.Count)
            throw new ArgumentException("Map must be connected.");

        return new GameMap(name, byId, neighbors);
    }
}
