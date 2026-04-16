using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Reflag.Internal;

internal sealed class RateLimiter(TimeSpan windowSize)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _entries = new(StringComparer.Ordinal);

    public bool IsAllowed(string key)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var lastSeen) && now - lastSeen < windowSize)
            {
                CleanupStaleEntriesNoLock(now);
                return false;
            }

            _entries[key] = now;
            CleanupStaleEntriesNoLock(now);
            return true;
        }
    }

    private void CleanupStaleEntriesNoLock(DateTimeOffset now)
    {
        if (RandomHelpers.NextDouble() >= 0.01)
        {
            return;
        }

        var cutoff = now - windowSize;
        var staleKeys = _entries
            .Where(pair => pair.Value < cutoff)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var key in staleKeys)
        {
            _entries.Remove(key);
        }
    }
}

internal static class HashObjectSerializer
{
    public static string HashObject(IReadOnlyDictionary<string, object?> value)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        UpdateHash(hash, value);
        return Convert.ToBase64String(hash.GetHashAndReset());
    }

    public static string HashObject(object value)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        UpdateHash(hash, value);
        return Convert.ToBase64String(hash.GetHashAndReset());
    }

    private static void UpdateHash(IncrementalHash hash, object? value)
    {
        switch (value)
        {
            case null:
                Append(hash, "null");
                return;
            case string stringValue:
                Append(hash, stringValue);
                return;
            case IDictionary<string, object?> stringDictionary:
                foreach (var key in stringDictionary.Keys.OrderBy(static key => key, StringComparer.Ordinal))
                {
                    Append(hash, key);
                    UpdateHash(hash, stringDictionary[key]);
                }
                return;
            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
                foreach (var key in readOnlyDictionary.Keys.OrderBy(static key => key, StringComparer.Ordinal))
                {
                    Append(hash, key);
                    UpdateHash(hash, readOnlyDictionary[key]);
                }
                return;
            case IDictionary dictionary:
                {
                    var entries = new List<(string Key, object? Value)>();
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        entries.Add((Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty, entry.Value));
                    }

                    foreach (var (key, nestedValue) in entries.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
                    {
                        Append(hash, key);
                        UpdateHash(hash, nestedValue);
                    }

                    return;
                }
            case IEnumerable enumerable when value is not string:
                foreach (var item in enumerable)
                {
                    UpdateHash(hash, item);
                }

                return;
            default:
                {
                    var type = value.GetType();
                    if (!ReflectionHelpers.IsScalarLike(value) && !type.IsPrimitive && !type.IsEnum)
                    {
                        foreach (var (key, nestedValue) in ReflectionHelpers.EnumerateReadableProperties(value)
                                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                        {
                            Append(hash, key);
                            UpdateHash(hash, nestedValue);
                        }

                        return;
                    }

                    Append(hash, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                    return;
                }
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
    }
}
