using System.Collections;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Reflag.Internal;

/// <summary>Flattens nested context into dotted paths, preserving arrays as normalized string arrays.</summary>
internal static class ContextFlattening
{
    private static readonly JsonSerializerOptions ArrayElementJsonOptions = new()
    {
        // Match JSON.stringify's escaping of Unicode and HTML-sensitive characters for
        // exact composite-array comparisons. This is an internal comparison string,
        // not JSON embedded in HTML; normal transport serialization still escapes it.
        // This does not guarantee JavaScript parity for number formatting or key ordering.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IReadOnlyDictionary<string, object?> FlattenContext(object? data)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (data is not null)
        {
            Recurse(data, string.Empty, result, preserveArrays: true);
        }

        return result;
    }

    private static void Recurse(object? value, string path, IDictionary<string, object?> result, bool preserveArrays)
    {
        if (value is null)
        {
            if (path.Length > 0)
            {
                result[path] = string.Empty;
            }

            return;
        }

        if (ReflectionHelpers.IsScalarLike(value))
        {
            if (path.Length > 0)
            {
                result[path] = ReflectionHelpers.ConvertToFlatString(value);
            }

            return;
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            RecurseDictionary(readOnlyDictionary, path, result, preserveArrays);
            return;
        }

        if (value is IDictionary<string, object?> dictionary)
        {
            RecurseDictionary(dictionary, path, result, preserveArrays);
            return;
        }

        if (value is IDictionary nonGenericDictionary)
        {
            var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in nonGenericDictionary)
            {
                if (entry.Key is null)
                {
                    continue;
                }

                normalized[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty] = entry.Value;
            }

            RecurseDictionary(normalized, path, result, preserveArrays);
            return;
        }

        if (value is IEnumerable enumerable and not string)
        {
            if (preserveArrays)
            {
                result[path] = enumerable.Cast<object?>().Select(item => item is null
                    ? string.Empty
                    : ReflectionHelpers.IsScalarLike(item)
                        ? ReflectionHelpers.ConvertToFlatString(item)
                        : JsonSerializer.Serialize(item, ArrayElementJsonOptions)).ToArray();
                return;
            }

            var index = 0;
            var hadAny = false;
            foreach (var item in enumerable)
            {
                hadAny = true;
                Recurse(item, path.Length == 0 ? index.ToString(CultureInfo.InvariantCulture) : $"{path}.{index.ToString(CultureInfo.InvariantCulture)}", result, preserveArrays);
                index++;
            }

            if (!hadAny && path.Length > 0)
            {
                result[path] = string.Empty;
            }

            return;
        }

        RecurseDictionary(
            ReflectionHelpers.EnumerateReadableProperties(value).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            path,
            result,
            preserveArrays);
    }

    private static void RecurseDictionary(
        IEnumerable<KeyValuePair<string, object?>> values,
        string path,
        IDictionary<string, object?> result,
        bool preserveArrays)
    {
        var hadAny = false;
        foreach (var (key, nestedValue) in values)
        {
            hadAny = true;
            Recurse(nestedValue, path.Length == 0 ? key : $"{path}.{key}", result, preserveArrays);
        }

        if (!hadAny && path.Length > 0)
        {
            result[path] = string.Empty;
        }
    }
}
