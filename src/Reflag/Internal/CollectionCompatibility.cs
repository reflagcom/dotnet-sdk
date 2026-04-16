namespace Reflag.Internal;

internal static class KeyValuePairExtensions
{
    public static void Deconstruct<TKey, TValue>(
        this KeyValuePair<TKey, TValue> pair,
        out TKey key,
        out TValue value)
    {
        key = pair.Key;
        value = pair.Value;
    }
}

internal static class CollectionHelpers
{
    public static Dictionary<TKey, TValue> ToDictionary<TKey, TValue>(
        IEnumerable<KeyValuePair<TKey, TValue>> values,
        IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        var result = new Dictionary<TKey, TValue>(comparer);
        foreach (var pair in values)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }
}
