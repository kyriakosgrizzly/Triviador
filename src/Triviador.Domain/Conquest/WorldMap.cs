using Triviador.Domain.Map;

namespace Triviador.Domain.Conquest;

public sealed record RegionInfo(int Id, string Key, string Name, string Continent);

/// <summary>The Conquest world map. Data is generated into WorldMap.g.cs by tools/worldmap.</summary>
public static partial class WorldMap
{
    public static IReadOnlyList<RegionInfo> Regions => RegionData;

    public static RegionInfo Get(int id) => RegionData[id - 1];

    public static GameMap Create() => GameMap.Create(
        "World",
        RegionData.Select(r => new Territory(r.Id, r.Name, Value: 1)),
        BorderData.Select(b => (b.A, b.B)));
}
