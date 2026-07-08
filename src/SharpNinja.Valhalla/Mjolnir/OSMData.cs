// Faithful C# port of Valhalla mjolnir OSMData (the in-memory OSM data container).
// Source: valhalla/mjolnir/osmdata.h + src/mjolnir/osmdata.cc @ 3.7.0
//
// OSMData is the normalized output of the PBF parser, fed into the graph builder. It
// holds counts, the restriction / access-restriction / bike-relation / lane-connectivity
// multimaps, the way-ref maps, and the two UniqueNames string tables (node_names and
// name_offset_map). The write/read temp-file machinery in osmdata.cc is an on-disk
// serialization detail of the C++ tile build; the front-end parser port keeps the data
// model + the add_to_name_map relation-direction logic, which is the load-bearing
// algorithm here.

using System;
using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>OSM record type. Faithful port of C++ <c>enum class OSMType : uint8_t</c>.</summary>
public enum OSMType : byte
{
    Node = 0,
    Way = 1,
    Relation = 2,
}

/// <summary>
/// Structure to store OSM node information and associate it to an OSM way. Faithful port
/// of C++ <c>struct OSMWayNode</c>. Written to the way_nodes sequence during parsing.
/// </summary>
public struct OSMWayNode
{
    /// <summary>The parsed OSM node.</summary>
    public OSMNode Node;

    /// <summary>Index of the owning way within the ways sequence.</summary>
    public uint WayIndex;

    /// <summary>Index of this node within the way's shape (node order along the way).</summary>
    public uint WayShapeNodeIndex;

    public OSMWayNode()
    {
        Node = new OSMNode();
        WayIndex = 0;
        WayShapeNodeIndex = 0;
    }
}

/// <summary>
/// Structure to store OSM node information for a bike-share station. Faithful port of C++
/// <c>struct OSMBSSNode</c>.
/// </summary>
public struct OSMBSSNode
{
    /// <summary>The parsed OSM node.</summary>
    public OSMNode Node;

    /// <summary>Index of the serialized BikeShareStationInfo within the node_names list.</summary>
    public uint BssInfoIndex;
}

/// <summary>
/// OSM bicycle relation data stored within OSMData. Faithful port of C++ <c>struct OSMBike</c>.
/// </summary>
public struct OSMBike
{
    /// <summary>Bike network mask (ncn/rcn/lcn/mcn).</summary>
    public byte BikeNetwork;

    /// <summary>Index of the network name within name_offset_map.</summary>
    public uint NameIndex;

    /// <summary>Index of the network ref within name_offset_map.</summary>
    public uint RefIndex;
}

/// <summary>
/// OSM lane connectivity stored within OSMData, indexed by the "to" way id. Faithful port
/// of C++ <c>struct OSMLaneConnectivity</c>.
/// </summary>
public struct OSMLaneConnectivity
{
    public uint ToWayId;

    public uint FromWayId;

    /// <summary>Index to the "to" lanes string in UniqueNames.</summary>
    public uint ToLanesIndex;

    /// <summary>Index to the "from" lanes string in UniqueNames.</summary>
    public uint FromLanesIndex;
}

/// <summary>
/// Simple container for OSM data. Populated by the PBF parser and sent into the graph
/// builder. Faithful port of the C++ <c>struct OSMData</c> from
/// <c>valhalla/mjolnir/osmdata.h</c>.
/// </summary>
public sealed class OSMData
{
    // ---- Counts ---------------------------------------------------------------

    /// <summary>MD5 of the PBF files as a 64-bit int.</summary>
    public ulong PbfChecksum { get; set; }

    /// <summary>The largest/newest changeset id encountered when parsing OSM data.</summary>
    public ulong MaxChangesetId { get; set; }

    /// <summary>Count of OSM nodes.</summary>
    public ulong OsmNodeCount { get; set; }

    /// <summary>Count of OSM ways.</summary>
    public ulong OsmWayCount { get; set; }

    /// <summary>Count of OSM nodes on OSM ways.</summary>
    public ulong OsmWayNodeCount { get; set; }

    /// <summary>Count of all nodes in the graph.</summary>
    public ulong NodeCount { get; set; }

    /// <summary>Estimated count of edges in the graph.</summary>
    public ulong EdgeCount { get; set; }

    /// <summary>Number of nodes with a ref.</summary>
    public ulong NodeRefCount { get; set; }

    /// <summary>Number of nodes with names.</summary>
    public ulong NodeNameCount { get; set; }

    /// <summary>Number of nodes with exit_to.</summary>
    public ulong NodeExitToCount { get; set; }

    /// <summary>Number of nodes with linguistic info.</summary>
    public ulong NodeLinguisticCount { get; set; }

    // ---- Multimaps and sets ---------------------------------------------------
    // C++ std::unordered_multimap keyed by way/node id is modeled as a Dictionary of
    // key -> List<value> to preserve the multi-value semantics and equal_range lookups.

    /// <summary>Simple restrictions, indexed by the "from" way id.</summary>
    public Dictionary<ulong, List<OSMRestriction>> Restrictions { get; } = new();

    /// <summary>Set of way ids that appear as a via in any complex restriction.</summary>
    public HashSet<ulong> ViaSet { get; } = new();

    /// <summary>Access restrictions, indexed by the "from" way id.</summary>
    public Dictionary<ulong, List<OSMAccessRestriction>> AccessRestrictions { get; } = new();

    /// <summary>Bike relation info, indexed by the way id.</summary>
    public Dictionary<ulong, List<OSMBike>> BikeRelations { get; } = new();

    /// <summary>Updated ref for a way (relations update many ways at a time).</summary>
    public Dictionary<ulong, uint> WayRef { get; } = new();

    /// <summary>Updated reverse ref for a way.</summary>
    public Dictionary<ulong, uint> WayRefRev { get; } = new();

    /// <summary>Unique names and strings for nodes (kept separate to keep OSMNode small).</summary>
    public UniqueNames NodeNames { get; } = new();

    /// <summary>Unique names and strings (road names, references, turn-lane strings, etc.).</summary>
    public UniqueNames NameOffsetMap { get; } = new();

    /// <summary>Lane connectivity, indexed by the "to" way id.</summary>
    public Dictionary<ulong, List<OSMLaneConnectivity>> LaneConnectivityMap { get; } = new();

    /// <summary>Conditional speed limits ("maxspeed:conditional"), indexed by the way id.</summary>
    public Dictionary<ulong, List<ConditionalSpeedLimit>> ConditionalSpeeds { get; } = new();

    /// <summary>True once read_from_temp_files has succeeded (C++ <c>initialized</c>).</summary>
    public bool Initialized { get; set; }

    private static void AddToMultiMap<TValue>(
        IDictionary<ulong, List<TValue>> map,
        ulong key,
        TValue value)
    {
        if (!map.TryGetValue(key, out List<TValue>? list))
        {
            list = new List<TValue>();
            map[key] = list;
        }

        list.Add(value);
    }

    /// <summary>Adds a simple restriction to the restrictions multimap (keyed by from way).</summary>
    public void AddRestriction(ulong fromWayId, OSMRestriction restriction) =>
        AddToMultiMap(Restrictions, fromWayId, restriction);

    /// <summary>Adds an access restriction (keyed by from way).</summary>
    public void AddAccessRestriction(ulong fromWayId, OSMAccessRestriction restriction) =>
        AddToMultiMap(AccessRestrictions, fromWayId, restriction);

    /// <summary>Adds a bike relation (keyed by way).</summary>
    public void AddBikeRelation(ulong wayId, OSMBike bike) =>
        AddToMultiMap(BikeRelations, wayId, bike);

    /// <summary>Adds a lane connectivity record (keyed by to way).</summary>
    public void AddLaneConnectivity(ulong toWayId, OSMLaneConnectivity lane) =>
        AddToMultiMap(LaneConnectivityMap, toWayId, lane);

    /// <summary>Adds a conditional speed limit (keyed by way).</summary>
    public void AddConditionalSpeed(ulong wayId, ConditionalSpeedLimit limit) =>
        AddToMultiMap(ConditionalSpeeds, wayId, limit);

    /// <summary>
    /// Returns all restrictions for a from-way id (C++ equal_range), or an empty list.
    /// </summary>
    public IReadOnlyList<OSMRestriction> RestrictionsFor(ulong fromWayId) =>
        Restrictions.TryGetValue(fromWayId, out List<OSMRestriction>? list)
            ? list
            : Array.Empty<OSMRestriction>();

    /// <summary>Returns all bike relations for a way id, or an empty list.</summary>
    public IReadOnlyList<OSMBike> BikeRelationsFor(ulong wayId) =>
        BikeRelations.TryGetValue(wayId, out List<OSMBike>? list)
            ? list
            : Array.Empty<OSMBike>();

    /// <summary>
    /// Adds the direction information to the forward or reverse ref map for relations.
    /// Faithful port of <c>OSMData::add_to_name_map</c>: only North/South/East/West (or
    /// those followed by " (") are recorded, and existing refs are concatenated with ";".
    /// </summary>
    public void AddToNameMap(ulong memberId, string direction, string reference, bool forward = true)
    {
        // dir = lower(direction) with first char upper-cased.
        string lower = direction.ToLowerInvariant();
        string dir = lower.Length == 0
            ? lower
            : char.ToUpperInvariant(lower[0]) + lower.Substring(1);

        bool matches = dir.StartsWith("North (", StringComparison.Ordinal) ||
                       dir.StartsWith("South (", StringComparison.Ordinal) ||
                       dir.StartsWith("East (", StringComparison.Ordinal) ||
                       dir.StartsWith("West (", StringComparison.Ordinal) ||
                       dir == "North" || dir == "South" || dir == "East" || dir == "West";

        if (!matches)
        {
            return;
        }

        if (forward)
        {
            if (WayRef.TryGetValue(memberId, out uint existing))
            {
                string refName = NameOffsetMap.Name(existing);
                WayRef[memberId] = NameOffsetMap.Index(refName + ";" + reference + "|" + dir);
            }
            else
            {
                WayRef[memberId] = NameOffsetMap.Index(reference + "|" + dir);
            }
        }
        else
        {
            if (WayRefRev.TryGetValue(memberId, out uint existing))
            {
                string refName = NameOffsetMap.Name(existing);
                WayRefRev[memberId] = NameOffsetMap.Index(refName + ";" + reference + "|" + dir);
            }
            else
            {
                WayRefRev[memberId] = NameOffsetMap.Index(reference + "|" + dir);
            }
        }
    }
}
