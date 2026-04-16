using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Reflag;

namespace Reflag.MinimalApiExample;

internal static class ExampleContextQueryParser
{
    public static ReflagContext Build(
        HttpRequest request,
        string? defaultUserId = null,
        string? defaultUserEmail = null,
        string? defaultEnvironment = null)
    {
        var root = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (defaultUserId is not null)
        {
            InsertPath(root, ["user", "id"], defaultUserId);
        }

        if (defaultEnvironment is not null)
        {
            InsertPath(root, ["other", "environment"], defaultEnvironment);
        }

        if (GetLastQueryValue(request.Query, "userId") is { } userId)
        {
            InsertPath(root, ["user", "id"], userId);
        }

        if (GetLastQueryValue(request.Query, "companyId") is { } companyId)
        {
            InsertPath(root, ["company", "id"], companyId);
        }

        foreach (var (key, values) in request.Query)
        {
            if (!key.StartsWith("context.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = key["context.".Length..]
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (path.Length == 0)
            {
                continue;
            }

            InsertPath(root, path, ToQueryValue(values));
        }

        if (defaultUserEmail is not null &&
            TryGetPath(root, ["user", "id"], out _) &&
            !TryGetPath(root, ["user", "email"], out _))
        {
            InsertPath(root, ["user", "email"], defaultUserEmail);
        }

        return ReflagContext.From(root);
    }

    private static void InsertPath(IDictionary<string, object?> target, IReadOnlyList<string> segments, object? value)
    {
        if (segments.Count == 0)
        {
            return;
        }

        var current = target;
        for (var index = 0; index < segments.Count - 1; index++)
        {
            var segment = segments[index];
            if (current.TryGetValue(segment, out var existing) && existing is Dictionary<string, object?> existingDictionary)
            {
                current = existingDictionary;
                continue;
            }

            var next = new Dictionary<string, object?>(StringComparer.Ordinal);
            current[segment] = next;
            current = next;
        }

        current[segments[^1]] = value;
    }

    private static bool TryGetPath(IReadOnlyDictionary<string, object?> source, IReadOnlyList<string> segments, out object? value)
    {
        value = null;
        if (segments.Count == 0)
        {
            return false;
        }

        IReadOnlyDictionary<string, object?> current = source;
        for (var index = 0; index < segments.Count - 1; index++)
        {
            if (!current.TryGetValue(segments[index], out var nested) || nested is not IReadOnlyDictionary<string, object?> nestedDictionary)
            {
                if (nested is Dictionary<string, object?> mutableDictionary)
                {
                    current = mutableDictionary;
                    continue;
                }

                return false;
            }

            current = nestedDictionary;
        }

        return current.TryGetValue(segments[^1], out value);
    }

    private static object? ToQueryValue(StringValues values)
    {
        return values.Count switch
        {
            <= 0 => null,
            1 => values[0],
            _ => values.ToArray(),
        };
    }

    private static string? GetLastQueryValue(IQueryCollection query, string key)
    {
        return GetLastValue(query[key]);
    }

    private static string? GetLastValue(StringValues values)
    {
        return values.Count switch
        {
            <= 0 => null,
            _ => values[^1],
        };
    }
}
