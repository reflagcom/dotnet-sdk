using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reflag.Internal;

internal sealed class FlagContextFilterDefinitionJsonConverter : JsonConverter<FlagContextFilterDefinition>
{
    public override FlagContextFilterDefinition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var operatorName = root.TryGetProperty("operator", out var operatorElement)
            ? operatorElement.ValueKind switch
            {
                JsonValueKind.String => operatorElement.GetString()!,
                // Do not copy malformed object/array payloads into diagnostic messages.
                JsonValueKind.Object => "[object Object]",
                JsonValueKind.Array => "[object Array]",
                _ => operatorElement.GetRawText(),
            }
            : "undefined";
        var @operator = FlagContextFilterOperatorJsonConverter.Parse(operatorName);

        return new FlagContextFilterDefinition
        {
            Field = root.TryGetProperty("field", out var fieldElement) ? fieldElement.GetString() ?? string.Empty : string.Empty,
            Operator = @operator,
            UnknownOperator = @operator == FlagContextFilterOperator.Unknown ? operatorName : null,
            Values = root.TryGetProperty("values", out var valuesElement) && valuesElement.ValueKind != JsonValueKind.Null
                ? JsonSerializer.Deserialize<string[]>(valuesElement.GetRawText(), options) ?? Array.Empty<string>()
                : Array.Empty<string>(),
        };
    }

    public override void Write(Utf8JsonWriter writer, FlagContextFilterDefinition value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);
        writer.WriteString("field", value.Field);
        writer.WriteString("operator", value.UnknownOperator ?? FlagContextFilterOperatorJsonConverter.GetName(value.Operator));
        writer.WritePropertyName("values");
        JsonSerializer.Serialize(writer, value.Values, options);
        writer.WriteEndObject();
    }
}
