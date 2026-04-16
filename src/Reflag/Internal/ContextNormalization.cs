using System.Collections;

namespace Reflag.Internal;

internal static class ReflagContextNormalizer
{
    public static ReflagContext NormalizeTypedContext(ReflagContext context)
    {
        ThrowHelpers.ThrowIfNull(context, nameof(context));

        return new ReflagContext
        {
            User = NormalizeTypedUser(context.User),
            Company = NormalizeTypedCompany(context.Company),
            Other = context.Other is null ? null : CloneObjectDictionary(context.Other),
        };
    }

    public static ReflagContext NormalizeLooseContext(object context)
    {
        if (context is ReflagContext typedContext)
        {
            return NormalizeTypedContext(typedContext);
        }

        if (!IsObjectLike(context))
        {
            throw new ArgumentException("context must be an object-like value.", nameof(context));
        }

        ReflagUserContext? user = null;
        ReflagCompanyContext? company = null;
        IReadOnlyDictionary<string, object?>? other = null;

        foreach (var (name, value) in EnumerateObjectEntries(context))
        {
            if (name.Equals("User", StringComparison.OrdinalIgnoreCase))
            {
                if (value is not null)
                {
                    user = NormalizeLooseUser(value);
                }

                continue;
            }

            if (name.Equals("Company", StringComparison.OrdinalIgnoreCase))
            {
                if (value is not null)
                {
                    company = NormalizeLooseCompany(value);
                }

                continue;
            }

            if (name.Equals("Other", StringComparison.OrdinalIgnoreCase))
            {
                if (value is not null)
                {
                    other = NormalizeObjectLikeDictionary(value, "Other");
                }
            }
        }

        return NormalizeTypedContext(new ReflagContext
        {
            User = user,
            Company = company,
            Other = other,
        });
    }

    public static ReflagContext MergeBoundContext(ReflagContext existing, ReflagContext update)
    {
        return new ReflagContext
        {
            User = MergeUser(existing.User, update.User),
            Company = MergeCompany(existing.Company, update.Company),
            Other = MergeOther(existing.Other, update.Other),
        };
    }

    private static ReflagUserContext? NormalizeTypedUser(ReflagUserContext? user)
    {
        if (user is null)
        {
            return null;
        }

        ValidateOptionalId(user.Id, "context.User.Id");
        ValidateOptionalString(user.Name, "context.User.Name");
        ValidateOptionalString(user.Email, "context.User.Email");
        ValidateOptionalString(user.Avatar, "context.User.Avatar");

        return new ReflagUserContext
        {
            Id = user.Id,
            Name = user.Name,
            Email = user.Email,
            Avatar = user.Avatar,
            Attributes = CloneObjectDictionary(user.Attributes),
        };
    }

    private static ReflagCompanyContext? NormalizeTypedCompany(ReflagCompanyContext? company)
    {
        if (company is null)
        {
            return null;
        }

        ValidateOptionalId(company.Id, "context.Company.Id");
        ValidateOptionalString(company.Name, "context.Company.Name");
        ValidateOptionalString(company.Avatar, "context.Company.Avatar");

        return new ReflagCompanyContext
        {
            Id = company.Id,
            Name = company.Name,
            Avatar = company.Avatar,
            Attributes = CloneObjectDictionary(company.Attributes),
        };
    }

    private static ReflagUserContext NormalizeLooseUser(object user)
    {
        if (!IsObjectLike(user))
        {
            throw new ArgumentException("context.User must be an object-like value.", nameof(user));
        }

        string? id = null;
        string? name = null;
        string? email = null;
        string? avatar = null;
        Dictionary<string, object?>? explicitAttributes = null;
        var inferredAttributes = new Dictionary<string, KeyValuePair<string, object?>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (propertyName, propertyValue) in EnumerateObjectEntries(user))
        {
            if (propertyName.Equals("Id", StringComparison.OrdinalIgnoreCase))
            {
                id = RequireStringOrNull(propertyValue, "context.User.Id");
                continue;
            }

            if (propertyName.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                name = RequireStringOrNull(propertyValue, "context.User.Name");
                continue;
            }

            if (propertyName.Equals("Email", StringComparison.OrdinalIgnoreCase))
            {
                email = RequireStringOrNull(propertyValue, "context.User.Email");
                continue;
            }

            if (propertyName.Equals("Avatar", StringComparison.OrdinalIgnoreCase))
            {
                avatar = RequireStringOrNull(propertyValue, "context.User.Avatar");
                continue;
            }

            if (propertyName.Equals("Attributes", StringComparison.OrdinalIgnoreCase))
            {
                explicitAttributes = propertyValue is null
                    ? new Dictionary<string, object?>()
                    : NormalizeObjectLikeDictionary(propertyValue, "context.User.Attributes");
                continue;
            }

            inferredAttributes[propertyName] = new KeyValuePair<string, object?>(propertyName, NormalizeNestedValue(propertyValue));
        }

        var mergedAttributes = MergeAttributes(explicitAttributes, inferredAttributes, "context.User.Attributes");
        ValidateOptionalId(id, "context.User.Id");

        return new ReflagUserContext
        {
            Id = id,
            Name = name,
            Email = email,
            Avatar = avatar,
            Attributes = mergedAttributes,
        };
    }

    private static ReflagCompanyContext NormalizeLooseCompany(object company)
    {
        if (!IsObjectLike(company))
        {
            throw new ArgumentException("context.Company must be an object-like value.", nameof(company));
        }

        string? id = null;
        string? name = null;
        string? avatar = null;
        Dictionary<string, object?>? explicitAttributes = null;
        var inferredAttributes = new Dictionary<string, KeyValuePair<string, object?>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (propertyName, propertyValue) in EnumerateObjectEntries(company))
        {
            if (propertyName.Equals("Id", StringComparison.OrdinalIgnoreCase))
            {
                id = RequireStringOrNull(propertyValue, "context.Company.Id");
                continue;
            }

            if (propertyName.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                name = RequireStringOrNull(propertyValue, "context.Company.Name");
                continue;
            }

            if (propertyName.Equals("Avatar", StringComparison.OrdinalIgnoreCase))
            {
                avatar = RequireStringOrNull(propertyValue, "context.Company.Avatar");
                continue;
            }

            if (propertyName.Equals("Attributes", StringComparison.OrdinalIgnoreCase))
            {
                explicitAttributes = propertyValue is null
                    ? new Dictionary<string, object?>()
                    : NormalizeObjectLikeDictionary(propertyValue, "context.Company.Attributes");
                continue;
            }

            inferredAttributes[propertyName] = new KeyValuePair<string, object?>(propertyName, NormalizeNestedValue(propertyValue));
        }

        var mergedAttributes = MergeAttributes(explicitAttributes, inferredAttributes, "context.Company.Attributes");
        ValidateOptionalId(id, "context.Company.Id");

        return new ReflagCompanyContext
        {
            Id = id,
            Name = name,
            Avatar = avatar,
            Attributes = mergedAttributes,
        };
    }

    private static Dictionary<string, object?> MergeAttributes(
        Dictionary<string, object?>? explicitAttributes,
        Dictionary<string, KeyValuePair<string, object?>> inferredAttributes,
        string paramName)
    {
        var merged = explicitAttributes is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(explicitAttributes, StringComparer.Ordinal);

        foreach (var (normalizedKey, pair) in inferredAttributes)
        {
            if (merged.Keys.Any(key => key.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(
                    $"{paramName} contains a case-insensitive collision between explicit and inferred attributes for key '{pair.Key}'.",
                    paramName);
            }

            merged[pair.Key] = pair.Value;
        }

        return new Dictionary<string, object?>(merged, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, object?> MergeDictionary(
        IReadOnlyDictionary<string, object?>? existing,
        IReadOnlyDictionary<string, object?>? update)
    {
        if (update is null)
        {
            return existing is null ? new Dictionary<string, object?>() : CloneObjectDictionary(existing);
        }

        var merged = existing is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : CollectionHelpers.ToDictionary(existing, StringComparer.Ordinal);

        foreach (var (key, value) in update)
        {
            merged[key] = NormalizeNestedValue(value);
        }

        return new Dictionary<string, object?>(merged, StringComparer.Ordinal);
    }

    private static ReflagUserContext? MergeUser(ReflagUserContext? existing, ReflagUserContext? update)
    {
        if (update is null)
        {
            return existing is null ? null : NormalizeTypedUser(existing);
        }

        return new ReflagUserContext
        {
            Id = update.Id ?? existing?.Id,
            Name = update.Name ?? existing?.Name,
            Email = update.Email ?? existing?.Email,
            Avatar = update.Avatar ?? existing?.Avatar,
            Attributes = MergeDictionary(existing?.Attributes, update.Attributes),
        };
    }

    private static IReadOnlyDictionary<string, object?>? MergeOther(
        IReadOnlyDictionary<string, object?>? existing,
        IReadOnlyDictionary<string, object?>? update)
    {
        if (existing is null && update is null)
        {
            return null;
        }

        if (update is null)
        {
            return CloneObjectDictionary(existing!);
        }

        if (existing is null)
        {
            return CloneObjectDictionary(update);
        }

        return MergeDictionary(existing, update);
    }

    private static ReflagCompanyContext? MergeCompany(ReflagCompanyContext? existing, ReflagCompanyContext? update)
    {
        if (update is null)
        {
            return existing is null ? null : NormalizeTypedCompany(existing);
        }

        return new ReflagCompanyContext
        {
            Id = update.Id ?? existing?.Id,
            Name = update.Name ?? existing?.Name,
            Avatar = update.Avatar ?? existing?.Avatar,
            Attributes = MergeDictionary(existing?.Attributes, update.Attributes),
        };
    }

    internal static IReadOnlyDictionary<string, object?> ToEvaluationObject(ReflagContext context)
    {
        var root = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (context.User is not null)
        {
            var user = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (context.User.Id is not null)
            {
                user["id"] = context.User.Id;
            }

            if (context.User.Name is not null)
            {
                user["name"] = context.User.Name;
            }

            if (context.User.Email is not null)
            {
                user["email"] = context.User.Email;
            }

            if (context.User.Avatar is not null)
            {
                user["avatar"] = context.User.Avatar;
            }

            foreach (var (key, value) in context.User.Attributes)
            {
                user[key] = value;
            }

            if (user.Count > 0)
            {
                root["user"] = user;
            }
        }

        if (context.Company is not null)
        {
            var company = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (context.Company.Id is not null)
            {
                company["id"] = context.Company.Id;
            }

            if (context.Company.Name is not null)
            {
                company["name"] = context.Company.Name;
            }

            if (context.Company.Avatar is not null)
            {
                company["avatar"] = context.Company.Avatar;
            }

            foreach (var (key, value) in context.Company.Attributes)
            {
                company[key] = value;
            }

            if (company.Count > 0)
            {
                root["company"] = company;
            }
        }

        if (context.Other is not null)
        {
            root["other"] = CloneObjectDictionary(context.Other);
        }

        return root;
    }

    private static string? RequireStringOrNull(object? value, string paramName)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string stringValue)
        {
            return stringValue;
        }

        throw new ArgumentException($"{paramName} must be a string when provided.", paramName);
    }

    private static void ValidateOptionalId(string? value, string paramName)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length == 0)
        {
            throw new ArgumentException($"{paramName} must be a non-empty string when provided.", paramName);
        }
    }

    private static void ValidateOptionalString(string? value, string paramName)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length == 0)
        {
            return;
        }
    }

    private static Dictionary<string, object?> CloneObjectDictionary(IReadOnlyDictionary<string, object?> values)
    {
        var clone = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            clone[key] = NormalizeNestedValue(value);
        }

        return clone;
    }

    private static Dictionary<string, object?> NormalizeObjectLikeDictionary(object value, string paramName)
    {
        if (!IsObjectLike(value))
        {
            throw new ArgumentException($"{paramName} must be an object-like value.", paramName);
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, nestedValue) in EnumerateObjectEntries(value))
        {
            result[key] = NormalizeNestedValue(nestedValue);
        }

        return result;
    }

    private static object? NormalizeNestedValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (ReflectionHelpers.IsScalarLike(value))
        {
            return value;
        }

        if (value is IEnumerable enumerable && value is not string && value is not IDictionary)
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
            {
                list.Add(NormalizeNestedValue(item));
            }

            return list;
        }

        if (IsObjectLike(value))
        {
            return NormalizeObjectLikeDictionary(value, nameof(value));
        }

        return value;
    }

    private static bool IsObjectLike(object? value)
    {
        if (value is null)
        {
            return false;
        }

        if (ReflectionHelpers.IsScalarLike(value))
        {
            return false;
        }

        if (value is IDictionary || value is IReadOnlyDictionary<string, object?> || value is IDictionary<string, object?>)
        {
            return true;
        }

        if (value is IEnumerable && value is not string)
        {
            return false;
        }

        return true;
    }

    private static IEnumerable<KeyValuePair<string, object?>> EnumerateObjectEntries(object value)
    {
        if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            return readOnlyDictionary;
        }

        if (value is IDictionary<string, object?> dictionary)
        {
            return dictionary;
        }

        if (value is IDictionary nonGenericDictionary)
        {
            var list = new List<KeyValuePair<string, object?>>();
            foreach (DictionaryEntry entry in nonGenericDictionary)
            {
                if (entry.Key is null)
                {
                    continue;
                }

                list.Add(new KeyValuePair<string, object?>(Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture)!, entry.Value));
            }

            return list;
        }

        return ReflectionHelpers.EnumerateReadableProperties(value);
    }
}
