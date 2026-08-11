using System.Globalization;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal readonly record struct ComplexRestrictionSemanticProjection(
    RestrictionType Type,
    uint Modes,
    byte Probability,
    bool ViaWay,
    bool IncludeFromWay,
    bool Conditional);

internal static class ComplexRestrictionSemantics
{
    private const uint DefaultModes =
        (uint)(GraphConstants.AutoAccess |
               GraphConstants.MopedAccess |
               GraphConstants.TaxiAccess |
               GraphConstants.BusAccess |
               GraphConstants.BicycleAccess |
               GraphConstants.TruckAccess |
               GraphConstants.EmergencyAccess |
               GraphConstants.MotorcycleAccess);

    private static readonly (string Key, uint Modes)[] TypeSpecificModes =
    [
        (
            "restriction:hgv",
            GraphConstants.TruckAccess),
        (
            "restriction:emergency",
            GraphConstants.EmergencyAccess),
        (
            "restriction:taxi",
            GraphConstants.TaxiAccess),
        (
            "restriction:motorcar",
            (uint)(GraphConstants.AutoAccess | GraphConstants.MopedAccess)),
        (
            "restriction:bus",
            GraphConstants.BusAccess),
        (
            "restriction:bicycle",
            GraphConstants.BicycleAccess),
        (
            "restriction:hazmat",
            GraphConstants.TruckAccess),
        (
            "restriction:motorcycle",
            GraphConstants.MotorcycleAccess),
        (
            "restriction:foot",
            (uint)(GraphConstants.PedestrianAccess |
                   GraphConstants.WheelchairAccess)),
    ];

    internal static bool TryProject(
        CompactOsmSemanticStore semanticStore,
        GenerationRestrictionRecord restriction,
        out ComplexRestrictionSemanticProjection projection)
    {
        ArgumentNullException.ThrowIfNull(semanticStore);

        IReadOnlyDictionary<string, string> tags =
            semanticStore.ReadTags(restriction.TagReference);
        bool viaWay = false;
        for (long viaOrdinal = restriction.ViaOffset;
             viaOrdinal < restriction.ViaOffset + restriction.ViaCount;
             viaOrdinal++)
        {
            viaWay |= semanticStore.ReadRestrictionVia(viaOrdinal).MemberType ==
                      OsmMemberType.Way;
        }

        bool conditional =
            tags.ContainsKey("restriction:conditional") ||
            tags.ContainsKey("hour_on") ||
            tags.ContainsKey("hour_off") ||
            tags.ContainsKey("day_on") ||
            tags.ContainsKey("day_off");
        bool probable = tags.ContainsKey("restriction:probable");
        bool excepted =
            tags.TryGetValue("except", out string? except) &&
            !string.IsNullOrWhiteSpace(except);

        uint specificModes = 0;
        RestrictionType? typeSpecificType = null;
        foreach ((string key, uint modes) in TypeSpecificModes)
        {
            if (!tags.TryGetValue(key, out string? value) ||
                !TryParseRestrictionType(value, out RestrictionType type))
            {
                continue;
            }

            specificModes |= modes;
            if (typeSpecificType.HasValue &&
                typeSpecificType.Value != type)
            {
                throw new InvalidDataException(
                    $"Restriction contains conflicting type-specific values " +
                    $"{typeSpecificType.Value} and {type}.");
            }

            typeSpecificType ??= type;
        }

        bool qualified =
            conditional ||
            probable ||
            typeSpecificType.HasValue ||
            excepted;
        if (!viaWay && !qualified)
        {
            projection = default;
            return false;
        }

        if (!TryResolveRestrictionType(
                tags,
                typeSpecificType,
                probable,
                out RestrictionType restrictionType,
                out byte probability))
        {
            projection = default;
            return false;
        }

        uint modesMask = specificModes != 0
            ? specificModes
            : ApplyExceptions(DefaultModes, except);
        if (modesMask == 0)
        {
            projection = default;
            return false;
        }

        projection = new ComplexRestrictionSemanticProjection(
            restrictionType,
            modesMask,
            probability,
            viaWay,
            IncludeFromWay: viaWay || !conditional,
            conditional);
        return true;
    }

    private static bool TryResolveRestrictionType(
        IReadOnlyDictionary<string, string> tags,
        RestrictionType? typeSpecificType,
        bool probable,
        out RestrictionType restrictionType,
        out byte probability)
    {
        restrictionType = default;
        probability = 0;
        if (typeSpecificType.HasValue)
        {
            restrictionType = typeSpecificType.Value;
        }
        else if (!tags.TryGetValue("restriction", out string? value) ||
                 !TryParseRestrictionType(value, out restrictionType))
        {
            return false;
        }

        if (!probable)
        {
            return true;
        }

        if (!tags.TryGetValue("restriction:probable", out string? probableValue))
        {
            return false;
        }

        string[] probabilityTokens = probableValue.Split('=');
        if (probabilityTokens.Length != 2 ||
            !byte.TryParse(
                probabilityTokens[1].Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out probability) ||
            probability == 0)
        {
            return false;
        }

        restrictionType = restrictionType is
            RestrictionType.OnlyRightTurn or
            RestrictionType.OnlyLeftTurn or
            RestrictionType.OnlyStraightOn
                ? RestrictionType.OnlyProbable
                : RestrictionType.NoProbable;
        return true;
    }

    private static bool TryParseRestrictionType(
        string value,
        out RestrictionType type)
    {
        if (byte.TryParse(
                value.Trim(),
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

    private static uint ApplyExceptions(uint modes, string? except)
    {
        if (string.IsNullOrWhiteSpace(except))
        {
            return modes;
        }

        foreach (string token in except.Split(';'))
        {
            modes = token.Trim() switch
            {
                "motorcar" => modes & ~(uint)(
                    GraphConstants.AutoAccess | GraphConstants.MopedAccess),
                "motorcycle" =>
                    modes & ~(uint)GraphConstants.MotorcycleAccess,
                "psv" => modes & ~(uint)(
                    GraphConstants.TaxiAccess | GraphConstants.BusAccess),
                "taxi" => modes & ~(uint)GraphConstants.TaxiAccess,
                "bus" => modes & ~(uint)GraphConstants.BusAccess,
                "bicycle" => modes & ~(uint)GraphConstants.BicycleAccess,
                "hgv" => modes & ~(uint)GraphConstants.TruckAccess,
                "emergency" => modes & ~(uint)GraphConstants.EmergencyAccess,
                "foot" => modes & ~(uint)(
                    GraphConstants.PedestrianAccess |
                    GraphConstants.WheelchairAccess),
                _ => modes,
            };
        }

        return modes;
    }
}
