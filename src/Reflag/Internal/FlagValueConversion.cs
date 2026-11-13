using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reflag.Internal;

internal static class FlagValueConversion
{
    /// <summary>
    /// Converts a flag's JSON value to <typeparamref name="T" />. Arbitrary-JSON types accept any value,
    /// including JSON null; other types reject null and values of a different JSON category.
    /// </summary>
    public static bool TryConvert<T>(JsonElement value, out T result)
    {
        result = default!;
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Null && !IsArbitraryJson(typeof(T)))
        {
            return false;
        }

        // Deserializing JSON null to JsonElement? yields no value; keep the explicit JSON null.
        if (typeof(T) == typeof(JsonElement) || typeof(T) == typeof(JsonElement?))
        {
            result = (T)(object)value.Clone();
            return true;
        }

        try
        {
            result = value.Deserialize<T>(ReflagJson.Options)!;
            return true;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    public static string DescribeKind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Number => "number",
        JsonValueKind.String => "string",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.Null => "null",
        _ => "undefined",
    };

    public static IReadOnlyDictionary<string, JsonElement> ToJsonOverrides(IReadOnlyDictionary<string, bool> overrides)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var pair in overrides)
        {
            result[pair.Key] = JsonSerializer.SerializeToElement(pair.Value);
        }

        return result;
    }

    public static IReadOnlyDictionary<string, JsonElement> ToJsonOverrides(IReadOnlyDictionary<string, object?> overrides)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var pair in overrides)
        {
            result[pair.Key] = pair.Value is JsonElement element
                ? element.Clone()
                : JsonSerializer.SerializeToElement(pair.Value, ReflagJson.Options);
        }

        return result;
    }

    public static Func<ReflagContext, IReadOnlyDictionary<string, JsonElement>> ToJsonOverridesFactory(
        Func<ReflagContext, IReadOnlyDictionary<string, bool>> factory)
    {
        return context => factory(context) is { } overrides ? ToJsonOverrides(overrides) : new Dictionary<string, JsonElement>();
    }

    public static Func<ReflagContext, IReadOnlyDictionary<string, JsonElement>> ToJsonOverridesFactory(
        Func<ReflagContext, IReadOnlyDictionary<string, object?>> factory)
    {
        return context => factory(context) is { } overrides ? ToJsonOverrides(overrides) : new Dictionary<string, JsonElement>();
    }

    private static bool IsArbitraryJson(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(object) ||
               underlying == typeof(JsonElement) ||
               underlying == typeof(JsonDocument) ||
               typeof(JsonNode).IsAssignableFrom(underlying);
    }
}
