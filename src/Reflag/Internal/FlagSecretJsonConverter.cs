using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reflag.Internal;

/// <summary>Only an explicit JSON false permits a flag to leave the server.</summary>
internal sealed class FlagSecretJsonConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var value = JsonDocument.ParseValue(ref reader);
        return value.RootElement.ValueKind != JsonValueKind.False;
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        writer.WriteBooleanValue(value);
    }
}
