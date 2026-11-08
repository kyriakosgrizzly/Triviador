using System.Text.Json;
using System.Text.Json.Serialization;

namespace Triviador.Domain;

/// <summary>Stable identity of a player (later: the Entra External ID object id).</summary>
[JsonConverter(typeof(PlayerIdJsonConverter))]
public readonly record struct PlayerId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Serializes a <see cref="PlayerId"/> as a plain string so clients see "p1", not {"value":"p1"}.</summary>
public sealed class PlayerIdJsonConverter : JsonConverter<PlayerId>
{
    public override PlayerId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("PlayerId cannot be null."));

    public override void Write(Utf8JsonWriter writer, PlayerId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

public sealed record PlayerInfo(PlayerId Id, string Name, bool IsBot = false);

/// <summary>Thrown when a client tries something the rules don't allow. The hub turns this into an error message.</summary>
public sealed class GameRuleException(string message) : InvalidOperationException(message);
