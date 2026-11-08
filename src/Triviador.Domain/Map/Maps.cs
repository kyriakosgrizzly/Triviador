namespace Triviador.Domain.Map;

/// <summary>Built-in maps. Later these can move to JSON in Blob Storage / Cosmos.</summary>
public static class Maps
{
    /// <summary>
    /// 15 territories on a 3x5 hex grid (ids 1..15, row by row, "odd-r" layout:
    /// odd rows are shifted half a hex to the right). Each hex borders up to six others.
    /// </summary>
    public static GameMap Valley()
    {
        string[] names =
        [
            "Northwatch", "Frostmere", "Highcliff", "Stormkeep", "Ravenhold",
            "Greenvale", "Ironford", "Sunreach", "Ashmoor", "Mistwood",
            "Saltmarsh", "Redwater", "Goldfield", "Thornbury", "Eastgate",
        ];

        return HexGrid("Valley", rows: 3, cols: 5, names);
    }

    public static GameMap HexGrid(string mapName, int rows, int cols, IReadOnlyList<string> names, int value = 200)
    {
        if (names.Count != rows * cols)
            throw new ArgumentException("Need exactly one name per cell.", nameof(names));

        int IdOf(int r, int c) => r * cols + c + 1;

        var territories = new List<Territory>();
        var edges = new List<(int, int)>();

        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
        {
            var id = IdOf(r, c);
            territories.Add(new Territory(id, names[id - 1], value, Column: c, Row: r));

            // Right neighbour.
            if (c + 1 < cols) edges.Add((id, IdOf(r, c + 1)));

            // The two neighbours in the row below. Even rows reach down-left/down; odd rows reach down/down-right.
            if (r + 1 < rows)
            {
                var (left, right) = r % 2 == 0 ? (c - 1, c) : (c, c + 1);
                if (left >= 0) edges.Add((id, IdOf(r + 1, left)));
                if (right < cols) edges.Add((id, IdOf(r + 1, right)));
            }
        }

        return GameMap.Create(mapName, territories, edges);
    }
}
