using System.Globalization;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal readonly record struct PooledWayEdgeSemantics(
    EdgeSemanticFlags Flags,
    uint ForwardAccess,
    uint ReverseAccess,
    long AttributeReference,
    byte Importance,
    bool HasNames)
{
    private static readonly (string Key, uint Mode)[] ForwardAccessKeys =
    [
        ("auto_forward", GraphConstants.AutoAccess),
        ("truck_forward", GraphConstants.TruckAccess),
        ("bus_forward", GraphConstants.BusAccess),
        ("bike_forward", GraphConstants.BicycleAccess),
        ("emergency_forward", GraphConstants.EmergencyAccess),
        ("hov_forward", GraphConstants.HovAccess),
        ("taxi_forward", GraphConstants.TaxiAccess),
        ("moped_forward", GraphConstants.MopedAccess),
        ("motorcycle_forward", GraphConstants.MotorcycleAccess),
        ("pedestrian_forward", GraphConstants.PedestrianAccess),
    ];

    private static readonly (string Key, uint Mode)[] ReverseAccessKeys =
    [
        ("auto_backward", GraphConstants.AutoAccess),
        ("truck_backward", GraphConstants.TruckAccess),
        ("bus_backward", GraphConstants.BusAccess),
        ("bike_backward", GraphConstants.BicycleAccess),
        ("emergency_backward", GraphConstants.EmergencyAccess),
        ("hov_backward", GraphConstants.HovAccess),
        ("taxi_backward", GraphConstants.TaxiAccess),
        ("moped_backward", GraphConstants.MopedAccess),
        ("motorcycle_backward", GraphConstants.MotorcycleAccess),
        ("pedestrian_backward", GraphConstants.PedestrianAccess),
    ];

    internal static PooledWayEdgeSemantics Project(
        IReadOnlyDictionary<string, string> transformedTags,
        long attributeReference)
    {
        ArgumentNullException.ThrowIfNull(transformedTags);
        ArgumentOutOfRangeException.ThrowIfNegative(attributeReference);

        EdgeSemanticFlags flags = EdgeSemanticFlags.None;
        AddFlag(transformedTags, "ferry", EdgeSemanticFlags.Ferry, ref flags);
        AddFlag(transformedTags, "link", EdgeSemanticFlags.Link, ref flags);
        AddFlag(transformedTags, "rail", EdgeSemanticFlags.Rail, ref flags);
        AddFlag(transformedTags, "oneway", EdgeSemanticFlags.Oneway, ref flags);
        AddFlag(
            transformedTags,
            "oneway_reverse",
            EdgeSemanticFlags.OnewayReverse,
            ref flags);
        AddFlag(
            transformedTags,
            "roundabout",
            EdgeSemanticFlags.Roundabout,
            ref flags);
        AddFlag(
            transformedTags,
            "private",
            EdgeSemanticFlags.DestinationOnly,
            ref flags);
        AddFlag(
            transformedTags,
            "private_hgv",
            EdgeSemanticFlags.DestinationOnlyHgv,
            ref flags);
        AddFlag(
            transformedTags,
            "no_thru_traffic",
            EdgeSemanticFlags.NoThruTraffic,
            ref flags);

        byte importance = ProjectImportance(transformedTags, flags);
        return new PooledWayEdgeSemantics(
            flags,
            ProjectAccess(transformedTags, ForwardAccessKeys),
            ProjectAccess(transformedTags, ReverseAccessKeys),
            attributeReference,
            importance,
            HasStructuralNames(transformedTags));
    }

    private static uint ProjectAccess(
        IReadOnlyDictionary<string, string> tags,
        IReadOnlyList<(string Key, uint Mode)> keys)
    {
        uint result = 0;
        foreach ((string key, uint mode) in keys)
        {
            if (IsTrue(tags, key))
            {
                result |= mode;
            }
        }

        return result;
    }

    private static byte ProjectImportance(
        IReadOnlyDictionary<string, string> tags,
        EdgeSemanticFlags flags)
    {
        if ((flags & (EdgeSemanticFlags.Ferry | EdgeSemanticFlags.Rail)) != 0)
        {
            return (byte)RoadClass.Primary;
        }

        if (!tags.TryGetValue("road_class", out string? value) ||
            !byte.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out byte parsed) ||
            parsed >= (byte)RoadClass.Invalid)
        {
            return (byte)RoadClass.ServiceOther;
        }

        return parsed;
    }

    private static bool HasStructuralNames(
        IReadOnlyDictionary<string, string> tags)
    {
        foreach ((string key, string value) in tags)
        {
            if (value.Length == 0)
            {
                continue;
            }

            if (key is "name" or "ref" or "alt_name" or "official_name" ||
                key.StartsWith("name:", StringComparison.Ordinal) ||
                key.Contains("pronunciation", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddFlag(
        IReadOnlyDictionary<string, string> tags,
        string key,
        EdgeSemanticFlags flag,
        ref EdgeSemanticFlags flags)
    {
        if (IsTrue(tags, key))
        {
            flags |= flag;
        }
    }

    private static bool IsTrue(
        IReadOnlyDictionary<string, string> tags,
        string key) =>
        tags.TryGetValue(key, out string? value) &&
        string.Equals(value, "true", StringComparison.Ordinal);
}
