using Triviador.Domain.Conquest;
using Triviador.Domain.Map;
using static Triviador.Domain.Tests.Conquest.ConquestHarness;

namespace Triviador.Domain.Tests.Conquest;

public class WorldMapAndLayoutTests
{
    [Fact]
    public void World_map_has_30_connected_regions()
    {
        var map = WorldMap.Create(); // GameMap.Create rejects disconnected maps
        Assert.Equal(30, map.Count);
        Assert.Equal(30, WorldMap.Regions.Select(r => r.Key).Distinct().Count());
    }

    [Fact]
    public void Land_and_sea_borders_are_where_you_would_expect()
    {
        var map = WorldMap.Create();
        Assert.True(map.AreAdjacent(R("caucasus"), R("russia")));          // Georgia–Russia
        Assert.True(map.AreAdjacent(R("caucasus"), R("persia")));          // Armenia–Iran
        Assert.True(map.AreAdjacent(R("brazil"), R("west-africa")));       // sea crossing
        Assert.True(map.AreAdjacent(R("usa"), R("russia")));               // Bering Strait
        Assert.True(map.AreAdjacent(R("north-africa"), R("middle-east"))); // Egypt–Israel
        Assert.False(map.AreAdjacent(R("brazil"), R("australia")));
        Assert.Equal(new[] { R("archipelago") }, map.Neighbors(R("australia")).ToArray());
    }

    [Fact]
    public void Resources_are_balanced_on_every_deal()
    {
        var map = WorldMap.Create();
        for (var seed = 0; seed < 30; seed++)
        {
            var layout = ResourceLayout.Generate(map, new Random(seed));
            var counts = layout.Values.GroupBy(r => r).Select(g => g.Count()).ToList();
            Assert.Equal(3, counts.Count);
            Assert.True(counts.Max() - counts.Min() <= 1, $"seed {seed}: {string.Join(",", counts)}");
        }
    }

    [Fact]
    public void Resources_are_spread_out_not_clustered()
    {
        var map = WorldMap.Create();
        var worst = 0;
        for (var seed = 0; seed < 30; seed++)
            worst = Math.Max(worst, ResourceLayout.Conflicts(map, ResourceLayout.Generate(map, new Random(seed))));

        // A purely random deal averages ~17 same-resource borders out of 53.
        Assert.True(worst <= 2, $"worst layout had {worst} same-resource borders");
    }

    [Fact]
    public void Each_game_deals_differently()
    {
        var map = WorldMap.Create();
        var a = ResourceLayout.Generate(map, new Random(1));
        var b = ResourceLayout.Generate(map, new Random(2));
        Assert.True(a.Any(kv => b[kv.Key] != kv.Value));
    }

    [Fact]
    public void A_triangle_gets_three_different_resources()
    {
        var map = GameMap.Create("T", Enumerable.Range(1, 3).Select(i => new Territory(i, $"T{i}", 1)), [(1, 2), (2, 3), (3, 1)]);
        var layout = ResourceLayout.Generate(map, new Random(5));
        Assert.Equal(0, ResourceLayout.Conflicts(map, layout));
    }
}
