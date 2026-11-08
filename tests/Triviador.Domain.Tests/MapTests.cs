using System.Text.Json;
using Triviador.Domain.Map;
using Triviador.Domain.Questions;

namespace Triviador.Domain.Tests;

public class MapTests
{
    [Fact]
    public void Valley_has_15_connected_territories_with_symmetric_borders()
    {
        var map = Maps.Valley();

        Assert.Equal(15, map.Count);
        foreach (var t in map.Territories)
        foreach (var n in map.Neighbors(t.Id))
            Assert.True(map.AreAdjacent(n, t.Id));
    }

    [Fact]
    public void Valley_hex_borders_are_correct()
    {
        // Layout (odd rows shifted right):
        //   1  2  3  4  5
        //    6  7  8  9  10
        //  11 12 13 14 15
        var map = Maps.Valley();

        Assert.Equal(new[] { 2, 6 }, map.Neighbors(1).Order());
        Assert.Equal(new[] { 2, 3, 6, 8, 12, 13 }, map.Neighbors(7).Order());
        Assert.Equal(new[] { 5, 9, 15 }, map.Neighbors(10).Order()); // right edge of a shifted row
        Assert.False(map.AreAdjacent(5, 6)); // rows don't wrap
        Assert.False(map.AreAdjacent(1, 7));
    }

    [Fact]
    public void Territories_carry_layout_coordinates()
    {
        var map = Maps.Valley();
        var t = map.Get(8);
        Assert.Equal(2, t.Column);
        Assert.Equal(1, t.Row);
    }

    [Fact]
    public void Disconnected_map_is_rejected()
    {
        var territories = Enumerable.Range(1, 4).Select(i => new Territory(i, $"T{i}", 100));
        Assert.Throws<ArgumentException>(() => GameMap.Create("Broken", territories, [(1, 2), (3, 4)]));
    }

    [Fact]
    public void Edge_to_unknown_territory_is_rejected()
    {
        var territories = Enumerable.Range(1, 2).Select(i => new Territory(i, $"T{i}", 100));
        Assert.Throws<ArgumentException>(() => GameMap.Create("Broken", territories, [(1, 99)]));
    }

    [Fact]
    public void Self_border_is_rejected()
    {
        var territories = Enumerable.Range(1, 2).Select(i => new Territory(i, $"T{i}", 100));
        Assert.Throws<ArgumentException>(() => GameMap.Create("Broken", territories, [(1, 1), (1, 2)]));
    }

    [Fact]
    public void Ids_and_answers_serialize_cleanly_for_clients()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal("\"p1\"", JsonSerializer.Serialize(new PlayerId("p1"), web));
        Assert.Equal("{\"kind\":\"choice\",\"optionIndex\":2}", JsonSerializer.Serialize<Answer>(new ChoiceAnswer(2), web));
        Assert.Equal(new PlayerId("p9"), JsonSerializer.Deserialize<PlayerId>("\"p9\"", web));
    }
}
