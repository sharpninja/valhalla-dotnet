using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Applies the shared way acceptance and graph-tag semantics used by both legacy and bounded
/// generation pipelines. A transient PBF tag dictionary is owned by the current callback and may be
/// transformed in place; other dictionaries are copied before transformation.
/// </summary>
internal static class OsmWaySemanticTransformer
{
    internal static bool TryTransform(
        ReadOnlySpan<ulong> nodeRefs,
        IReadOnlyDictionary<string, string> rawTags,
        [NotNullWhen(true)] out IReadOnlyDictionary<string, string>? transformedTags)
    {
        transformedTags = null;
        if (nodeRefs.Length < 2)
        {
            return false;
        }

        if (nodeRefs[0] == nodeRefs[^1])
        {
            foreach (KeyValuePair<string, string> tag in rawTags)
            {
                if (tag.Key is "building" or "landuse" or "leisure" or "natural")
                {
                    return false;
                }
            }
        }

        Dictionary<string, string> tags =
            rawTags as OsmPbfTransientTagDictionary ??
            new Dictionary<string, string>(rawTags, StringComparer.Ordinal);
        if (WayTagTransform.Transform(tags) != 0 || tags.Count == 0)
        {
            return false;
        }

        transformedTags = tags;
        return true;
    }
}

/// <summary>
/// Applies the shared node graph-tag semantics while preserving the precomputed empty-node result.
/// </summary>
internal static class OsmNodeSemanticTransformer
{
    internal static IReadOnlyDictionary<string, string> CreateEmptyTransformedTags()
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        NodeTagTransform.Transform(tags);
        return tags;
    }

    internal static IReadOnlyDictionary<string, string> Transform(
        IReadOnlyDictionary<string, string> rawTags,
        IReadOnlyDictionary<string, string> emptyTransformedTags)
    {
        if (rawTags.Count == 0)
        {
            return emptyTransformedTags;
        }

        Dictionary<string, string> tags =
            rawTags as OsmPbfTransientTagDictionary ??
            new Dictionary<string, string>(rawTags, StringComparer.Ordinal);
        NodeTagTransform.Transform(tags);
        return tags;
    }
}

/// <summary>
/// Normalizes OSM restriction relation tags into the representation consumed by graph construction.
/// </summary>
internal static class OsmRelationSemanticTransformer
{
    private static readonly IReadOnlyDictionary<string, RestrictionType> RestrictionTypes =
        new Dictionary<string, RestrictionType>(StringComparer.Ordinal)
        {
            ["no_left_turn"] = RestrictionType.NoLeftTurn,
            ["no_right_turn"] = RestrictionType.NoRightTurn,
            ["no_straight_on"] = RestrictionType.NoStraightOn,
            ["no_u_turn"] = RestrictionType.NoUTurn,
            ["only_right_turn"] = RestrictionType.OnlyRightTurn,
            ["only_left_turn"] = RestrictionType.OnlyLeftTurn,
            ["only_straight_on"] = RestrictionType.OnlyStraightOn,
            ["no_entry"] = RestrictionType.NoEntry,
            ["no_exit"] = RestrictionType.NoExit,
            ["no_turn"] = RestrictionType.NoTurn,
        };

    private static readonly string[] TypeSpecificRestrictionKeys =
    [
        "restriction:hgv",
        "restriction:emergency",
        "restriction:taxi",
        "restriction:motorcar",
        "restriction:bus",
        "restriction:bicycle",
        "restriction:hazmat",
        "restriction:motorcycle",
        "restriction:foot",
    ];

    internal static bool TryNormalizeRestrictionTags(
        IReadOnlyDictionary<string, string> rawTags,
        out IReadOnlyDictionary<string, string> normalizedTags)
    {
        normalizedTags = rawTags;

        rawTags.TryGetValue("type", out string? relationType);
        bool hasRestrictionTag = rawTags.ContainsKey("restriction") ||
                                 rawTags.ContainsKey("restriction:conditional") ||
                                 rawTags.ContainsKey("restriction:probable") ||
                                 TypeSpecificRestrictionKeys.Any(rawTags.ContainsKey);
        if (!hasRestrictionTag)
        {
            return true;
        }

        if (relationType is not ("restriction" or "route"))
        {
            return false;
        }

        bool hasConditional =
            rawTags.TryGetValue("restriction:conditional", out string? conditionalValue);
        bool hasProbable =
            rawTags.TryGetValue("restriction:probable", out string? probableValue);
        if (relationType != "restriction" && !hasConditional && !hasProbable)
        {
            return false;
        }

        var tags = new Dictionary<string, string>(rawTags, StringComparer.Ordinal);
        if (hasProbable && (tags.ContainsKey("restriction") || hasConditional))
        {
            tags.Remove("restriction:probable");
            hasProbable = false;
            probableValue = null;
        }

        RestrictionType? genericType = null;
        if (tags.TryGetValue("restriction", out string? restrictionValue) &&
            TryParseRestrictionType(restrictionValue, out RestrictionType parsedGenericType))
        {
            genericType = parsedGenericType;
        }
        else if (hasConditional &&
                 TrySplitQualifiedRestriction(
                     conditionalValue!,
                     out string conditionalPrefix,
                     out _) &&
                 TryParseRestrictionType(conditionalPrefix, out parsedGenericType))
        {
            genericType = parsedGenericType;
        }
        else if (hasProbable &&
                 TrySplitQualifiedRestriction(
                     probableValue!,
                     out string probablePrefix,
                     out _) &&
                 TryParseRestrictionType(probablePrefix, out parsedGenericType))
        {
            genericType = parsedGenericType;
        }

        RestrictionType? typeSpecificType = null;
        foreach (string key in TypeSpecificRestrictionKeys)
        {
            if (!tags.TryGetValue(key, out string? value))
            {
                continue;
            }

            if (TryParseRestrictionType(value, out RestrictionType parsedType))
            {
                tags[key] = ((byte)parsedType).ToString(CultureInfo.InvariantCulture);
                typeSpecificType ??= parsedType;
            }
            else
            {
                tags.Remove(key);
            }
        }

        RestrictionType? effectiveType = typeSpecificType ?? genericType;
        if (effectiveType is null)
        {
            return false;
        }

        if (hasConditional)
        {
            if (!TrySplitQualifiedRestriction(
                    conditionalValue!,
                    out _,
                    out string conditionalSuffix))
            {
                return false;
            }

            tags["restriction:conditional"] = conditionalSuffix;
        }

        if (hasProbable)
        {
            if (!TrySplitQualifiedRestriction(
                    probableValue!,
                    out _,
                    out string probableSuffix))
            {
                return false;
            }

            tags["restriction:probable"] = probableSuffix;
        }

        if (typeSpecificType is null)
        {
            tags["restriction"] =
                ((byte)effectiveType.Value).ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            tags.Remove("restriction");
        }

        normalizedTags = tags;
        return true;
    }

    private static bool TryParseRestrictionType(string value, out RestrictionType type)
    {
        string candidate = value.Trim();
        if (RestrictionTypes.TryGetValue(candidate, out type))
        {
            return true;
        }

        if (byte.TryParse(
                candidate,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out byte numericValue) &&
            numericValue <= (byte)RestrictionType.NoTurn)
        {
            type = (RestrictionType)numericValue;
            return true;
        }

        type = default;
        return false;
    }

    private static bool TrySplitQualifiedRestriction(
        string value,
        out string restriction,
        out string qualifier)
    {
        int separator = value.IndexOf('@', StringComparison.Ordinal);
        if (separator <= 0 || separator >= value.Length - 1)
        {
            restriction = string.Empty;
            qualifier = string.Empty;
            return false;
        }

        restriction = value[..separator].Trim();
        qualifier = value[(separator + 1)..].Trim();
        return restriction.Length != 0 && qualifier.Length != 0;
    }
}
