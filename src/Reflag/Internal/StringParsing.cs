namespace Reflag.Internal;

internal static class StringParsing
{
    public static readonly char[] AmpersandSeparator = new[] { '&' };
    public static readonly char[] CommaSeparator = new[] { ',' };

    public static IEnumerable<string> SplitAndTrim(string value, char[] separator)
    {
        foreach (var segment in value.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = segment.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }
}

internal static class QueryStringHelpers
{
    public static List<KeyValuePair<string, string>> Parse(string query)
    {
        var normalized = StripLeadingQuestionMark(query);
        var result = new List<KeyValuePair<string, string>>();
        if (normalized.Length == 0)
        {
            return result;
        }

        foreach (var segment in normalized.Split(StringParsing.AmpersandSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            result.Add(ParseSegment(segment));
        }

        return result;
    }

    public static string? GetValue(string query, string key)
    {
        foreach (var parameter in Parse(query))
        {
            if (string.Equals(parameter.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return parameter.Value;
            }
        }

        return null;
    }

    private static KeyValuePair<string, string> ParseSegment(string segment)
    {
        var equalsIndex = segment.IndexOf('=');
        if (equalsIndex < 0)
        {
            return new KeyValuePair<string, string>(Uri.UnescapeDataString(segment), string.Empty);
        }

        var key = Uri.UnescapeDataString(segment.Substring(0, equalsIndex));
        var value = Uri.UnescapeDataString(segment.Substring(equalsIndex + 1));
        return new KeyValuePair<string, string>(key, value);
    }

    private static string StripLeadingQuestionMark(string value)
    {
        return value.StartsWith("?", StringComparison.Ordinal) ? value.Substring(1) : value;
    }
}
