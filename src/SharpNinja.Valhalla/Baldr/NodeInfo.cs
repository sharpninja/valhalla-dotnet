// Faithful C# port of Valhalla baldr NodeInfo (nodeinfo.h + src/baldr/nodeinfo.cc) @ 3.7.0.
// Sources: valhalla/baldr/nodeinfo.h and src/baldr/nodeinfo.cc
// Self-contained engine port: field widths, bit-packing order, and on-disk struct size
// are reproduced exactly so a tile byte buffer parses identically to the C++ engine.
//
// EXACT BIT LAYOUT (four little-endian uint64 words; must match the on-disk tile blob).
// The C++ class groups its bitfields "into 8-byte words so structure will align to 8 byte
// boundaries". Each word's fields are listed from least-significant bit upward.
//
//   Word 0 (_word0):
//     bits  0..21 (22 bits) : lat_offset_   (latitude offset, int 6-digit precision)
//     bits 22..25 ( 4 bits) : lat_offset7_  (latitude offset 7th digit)
//     bits 26..47 (22 bits) : lon_offset_   (longitude offset, int 6-digit precision)
//     bits 48..51 ( 4 bits) : lon_offset7_  (longitude offset 7th digit)
//     bits 52..63 (12 bits) : access_       (access bit mask through the node)
//
//   Word 1 (_word1):
//     bits  0..20 (21 bits) : edge_index_      (index of first outbound directed edge)
//     bits 21..27 ( 7 bits) : edge_count_      (number of outbound edges on this level)
//     bits 28..39 (12 bits) : admin_index_     (index into tile admin info list)
//     bits 40..48 ( 9 bits) : timezone_        (time zone)
//     bits 49..52 ( 4 bits) : intersection_    (IntersectionType)
//     bits 53..56 ( 4 bits) : type_            (NodeType)
//     bits 57..60 ( 4 bits) : density_         (relative road density)
//     bit  61      ( 1 bit) : traffic_signal_
//     bit  62      ( 1 bit) : mode_change_
//     bit  63      ( 1 bit) : named_
//
//   Word 2 (_word2):
//     bits  0..20 (21 bits) : transition_index_   (first transition; also transit stop index)
//     bits 21..23 ( 3 bits) : transition_count_   (number of transitions)
//     bits 24..39 (16 bits) : local_driveability_ (2 bits per local edge, up to 8)
//     bits 40..42 ( 3 bits) : local_edge_count_   (# regular edges across levels, minus 1)
//     bit  43      ( 1 bit) : drive_on_right_
//     bit  44      ( 1 bit) : tagged_access_
//     bit  45      ( 1 bit) : private_access_
//     bit  46      ( 1 bit) : cash_only_toll_
//     bits 47..61 (15 bits) : elevation_          (encoded elevation)
//     bit  62      ( 1 bit) : timezone_ext_1_
//     bit  63      ( 1 bit) : spare2_
//
//   Word 3 (_headings): headings_ (full 64 bits; 8 bits per local edge, or transit way id /
//                                  encoded lon-lat while building transit connection data).
//
// Total struct size: 32 bytes (matches the C++ kNodeInfoExpectedSize).
//
// PORT-NOTE: the rapidjson json() method and the access_json/admin_json helpers are
//            json serialization and are excluded. The connecting_wayid()/connecting_point()
//            accessors are transit-connection helpers (used only while building transit data);
//            they are ported here because they operate purely on the headings_ field bit
//            layout, but the PointLL/uint64 lon-lat encode/decode they rely on is
//            reproduced locally (midgard PointLL's C# port does not expose that operator).

using System;
using System.Globalization;
using System.Runtime.InteropServices;

using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Information held for each node within the graph. The graph uses a forward star structure:
/// nodes point to the first outbound directed edge and each directed edge points to the other
/// end node of the edge. Faithful port of <c>valhalla::baldr::NodeInfo</c>.
/// </summary>
/// <remarks>
/// Tile-layout fidelity: this struct is bit-packed and read directly from the on-disk tile blob.
/// Size is exactly 32 bytes (four 64-bit words). See the file header for the full bit map.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NodeInfo
{
    /// <summary>Maximum edges per node.</summary>
    public const uint MaxEdgesPerNode = 127;

    /// <summary>Maximum Admins per tile.</summary>
    public const uint MaxAdminsPerTile = 4095;

    /// <summary>Maximum TimeZones index for the first extension level.</summary>
    public const uint MaxTimeZoneIdExt1 = 1023;

    /// <summary>Maximum index of edges on the local level.</summary>
    public const uint MaxLocalEdgeIndex = 7;

    // Elevation precision. Elevation is clamped to a range of -500 meters to 7683 meters.
    /// <summary>Maximum stored elevation (15 bits).</summary>
    public const uint NodeMaxStoredElevation = 32767;

    /// <summary>Elevation precision (meters per stored unit).</summary>
    public const float NodeElevationPrecision = 0.25f;

    /// <summary>Minimum representable node elevation in meters.</summary>
    public const float NodeMinElevation = -500.0f;

    /// <summary>Maximum representable node elevation in meters.</summary>
    public const float NodeMaxElevation = NodeMinElevation + (NodeElevationPrecision * NodeMaxStoredElevation);

    /// <summary>Heading shrink factor to reduce max heading of 359 to 255.</summary>
    public const float HeadingShrinkFactor = 255.0f / 359.0f;

    /// <summary>Heading expand factor to increase max heading of 255 to 359.</summary>
    public const float HeadingExpandFactor = 359.0f / 255.0f;

    // ---- Word 0 bit positions / masks ----
    private const int LatOffsetShift = 0;
    private const int LatOffset7Shift = 22;
    private const int LonOffsetShift = 26;
    private const int LonOffset7Shift = 48;
    private const int AccessShift = 52;
    private const ulong Offset22Mask = (1UL << 22) - 1UL; // lat_offset_ / lon_offset_ (22 bits)
    private const ulong Offset4Mask = (1UL << 4) - 1UL;   // lat_offset7_ / lon_offset7_ (4 bits)
    private const ulong AccessMask = (1UL << 12) - 1UL;   // access_ (12 bits)

    // ---- Word 1 bit positions / masks ----
    private const int EdgeIndexShift = 0;
    private const int EdgeCountShift = 21;
    private const int AdminIndexShift = 28;
    private const int TimezoneShift = 40;
    private const int IntersectionShift = 49;
    private const int TypeShift = 53;
    private const int DensityShift = 57;
    private const int TrafficSignalShift = 61;
    private const int ModeChangeShift = 62;
    private const int NamedShift = 63;
    private const ulong EdgeIndexMask = (1UL << 21) - 1UL;  // edge_index_ (21 bits)
    private const ulong EdgeCountMask = (1UL << 7) - 1UL;   // edge_count_ (7 bits)
    private const ulong AdminIndexMask = (1UL << 12) - 1UL; // admin_index_ (12 bits)
    private const ulong TimezoneMask = (1UL << 9) - 1UL;    // timezone_ (9 bits)
    private const ulong Field4Mask = (1UL << 4) - 1UL;      // intersection_/type_/density_ (4 bits)

    // ---- Word 2 bit positions / masks ----
    private const int TransitionIndexShift = 0;
    private const int TransitionCountShift = 21;
    private const int LocalDriveabilityShift = 24;
    private const int LocalEdgeCountShift = 40;
    private const int DriveOnRightShift = 43;
    private const int TaggedAccessShift = 44;
    private const int PrivateAccessShift = 45;
    private const int CashOnlyTollShift = 46;
    private const int ElevationShift = 47;
    private const int TimezoneExt1Shift = 62;
    private const int Spare2Shift = 63;
    private const ulong TransitionIndexMask = (1UL << 21) - 1UL;   // transition_index_ (21 bits)
    private const ulong TransitionCountMask = (1UL << 3) - 1UL;    // transition_count_ (3 bits)
    private const ulong LocalDriveabilityMask = (1UL << 16) - 1UL; // local_driveability_ (16 bits)
    private const ulong LocalEdgeCountMask = (1UL << 3) - 1UL;     // local_edge_count_ (3 bits)
    private const ulong ElevationMask = (1UL << 15) - 1UL;         // elevation_ (15 bits)

    // The four 8-byte words (see file header for the field map). Default(NodeInfo) zeroes all of
    // them, matching the C++ constructor which does memset(this, 0, sizeof(NodeInfo)).
    private ulong _word0;
    private ulong _word1;
    private ulong _word2;
    private ulong _headings;

    /// <summary>
    /// Constructor with arguments. Mirrors the C++ argument constructor which zero-initializes then
    /// sets lat/lng, access, type, traffic signal, tagged access, private access and cash-only toll.
    /// </summary>
    /// <param name="tileCorner">Lower left (SW) corner of the tile that contains the node.</param>
    /// <param name="ll">Lat,lng position of the node.</param>
    /// <param name="access">Access mask at this node.</param>
    /// <param name="type">The type of node.</param>
    /// <param name="trafficSignal">Has a traffic signal at this node?</param>
    /// <param name="taggedAccess">Was the access information originally tagged?</param>
    /// <param name="privateAccess">Is access private?</param>
    /// <param name="cashOnlyToll">Is this a cash-only toll?</param>
    public NodeInfo(
        PointLL tileCorner,
        PointLL ll,
        uint access,
        NodeType type,
        bool trafficSignal,
        bool taggedAccess,
        bool privateAccess,
        bool cashOnlyToll)
    {
        _word0 = 0;
        _word1 = 0;
        _word2 = 0;
        _headings = 0;
        SetLatLng(tileCorner, ll);
        SetAccess(access);
        SetType(type);
        SetTrafficSignal(trafficSignal);
        SetTaggedAccess(taggedAccess);
        SetPrivateAccess(privateAccess);
        SetCashOnlyToll(cashOnlyToll);
    }

    // ---- Word 0 raw bitfield accessors ----
    private readonly ulong LatOffset => (_word0 >> LatOffsetShift) & Offset22Mask;
    private readonly ulong LatOffset7 => (_word0 >> LatOffset7Shift) & Offset4Mask;
    private readonly ulong LonOffset => (_word0 >> LonOffsetShift) & Offset22Mask;
    private readonly ulong LonOffset7 => (_word0 >> LonOffset7Shift) & Offset4Mask;

    /// <summary>
    /// Get the latitude, longitude of the node.
    /// </summary>
    /// <param name="tileCorner">Lower left (SW) corner of the tile.</param>
    /// <returns>Returns the latitude and longitude of the node.</returns>
    public readonly PointLL LatLng(PointLL tileCorner)
        => new PointLL(
            tileCorner.Lng + ((LonOffset * 1e-6) + (LonOffset7 * 1e-7)),
            tileCorner.Lat + ((LatOffset * 1e-6) + (LatOffset7 * 1e-7)));

    /// <summary>
    /// Sets the latitude and longitude. Mirrors <c>NodeInfo::set_latlng</c> exactly, including the
    /// protection against a node being slightly outside the tile due to float roundoff.
    /// </summary>
    /// <param name="tileCorner">Lower left (SW) corner of the tile.</param>
    /// <param name="ll">Lat,lng position of the node.</param>
    public void SetLatLng(PointLL tileCorner, PointLL ll)
    {
        // Protect against a node being slightly outside the tile (due to float roundoff).
        ulong latOffset = 0;
        ulong latOffset7 = 0;
        if (ll.Lat > tileCorner.Lat)
        {
            double lat = Math.Round((ll.Lat - tileCorner.Lat) / 1e-7, MidpointRounding.AwayFromZero);
            latOffset = (ulong)(lat / 10.0);
            latOffset7 = (ulong)(lat - (latOffset * 10.0));
        }

        ulong lonOffset = 0;
        ulong lonOffset7 = 0;
        if (ll.Lng > tileCorner.Lng)
        {
            double lon = Math.Round((ll.Lng - tileCorner.Lng) / 1e-7, MidpointRounding.AwayFromZero);
            lonOffset = (ulong)(lon / 10.0);
            lonOffset7 = (ulong)(lon - (lonOffset * 10.0));
        }

        SetWord0Field(LatOffsetShift, Offset22Mask, latOffset);
        SetWord0Field(LatOffset7Shift, Offset4Mask, latOffset7);
        SetWord0Field(LonOffsetShift, Offset22Mask, lonOffset);
        SetWord0Field(LonOffset7Shift, Offset4Mask, lonOffset7);
    }

    /// <summary>
    /// Get the index of the first outbound edge from this node. Since all outbound edges are in the
    /// same tile/level as the node we only need an index within the tile.
    /// </summary>
    public readonly uint EdgeIndex => (uint)((_word1 >> EdgeIndexShift) & EdgeIndexMask);

    /// <summary>Set the index within the node's tile of its first outbound edge.</summary>
    public void SetEdgeIndex(uint edgeIndex)
    {
        if (edgeIndex > GraphConstants.MaxGraphId)
        {
            // Consider this a catastrophic error.
            throw new InvalidOperationException("NodeInfo: edge index exceeds max");
        }

        SetWord1Field(EdgeIndexShift, EdgeIndexMask, edgeIndex);
    }

    /// <summary>
    /// Get the number of outbound directed edges. This includes all edges present on the current
    /// hierarchy level.
    /// </summary>
    public readonly uint EdgeCount => (uint)((_word1 >> EdgeCountShift) & EdgeCountMask);

    /// <summary>Set the number of outbound directed edges (clamped to <see cref="MaxEdgesPerNode"/>).</summary>
    public void SetEdgeCount(uint edgeCount)
    {
        // C++ logs an error and clamps to max when edge_count > kMaxEdgesPerNode.
        uint clamped = edgeCount > MaxEdgesPerNode ? MaxEdgesPerNode : edgeCount;
        SetWord1Field(EdgeCountShift, EdgeCountMask, clamped);
    }

    /// <summary>
    /// Get the access modes (bit mask) allowed to pass through the node. See
    /// <see cref="GraphConstants"/> for access constants.
    /// </summary>
    public readonly ushort Access => (ushort)((_word0 >> AccessShift) & AccessMask);

    /// <summary>Set the access modes (bit mask) allowed to pass through the node.</summary>
    public void SetAccess(uint access)
    {
        // C++ logs an error and masks to kAllAccess if access exceeds the maximum allowed.
        uint stored = access > GraphConstants.AllAccess ? (access & GraphConstants.AllAccess) : access;
        SetWord0Field(AccessShift, AccessMask, stored);
    }

    /// <summary>Get the intersection type.</summary>
    public readonly IntersectionType Intersection
        => (IntersectionType)(byte)((_word1 >> IntersectionShift) & Field4Mask);

    /// <summary>Set the intersection type.</summary>
    public void SetIntersection(IntersectionType type)
        => SetWord1Field(IntersectionShift, Field4Mask, (uint)type);

    /// <summary>Get the index of the administrative information within this tile.</summary>
    public readonly uint AdminIndex => (uint)((_word1 >> AdminIndexShift) & AdminIndexMask);

    /// <summary>Set the index of the administrative information within this tile.</summary>
    public void SetAdminIndex(ushort adminIndex)
    {
        // C++ logs an error and clamps to max when admin_index > kMaxAdminsPerTile.
        uint clamped = adminIndex > MaxAdminsPerTile ? MaxAdminsPerTile : adminIndex;
        SetWord1Field(AdminIndexShift, AdminIndexMask, clamped);
    }

    /// <summary>
    /// Returns the timezone index. Combines the 9-bit <c>timezone_</c> field with the
    /// <c>timezone_ext_1_</c> extension bit (<c>timezone_ | (timezone_ext_1_ &lt;&lt; 9)</c>).
    /// </summary>
    public readonly uint Timezone()
    {
        uint tz = (uint)((_word1 >> TimezoneShift) & TimezoneMask);
        uint ext1 = (uint)((_word2 >> TimezoneExt1Shift) & 1UL);
        return tz | (ext1 << 9);
    }

    /// <summary>Set the timezone index.</summary>
    public void SetTimezone(uint timezone)
    {
        if (timezone > MaxTimeZoneIdExt1)
        {
            throw new InvalidOperationException(
                "NodeInfo: timezone index exceeds max: " + timezone.ToString(CultureInfo.InvariantCulture));
        }

        // First 9 bits for backwards compat; 10th bit for new timezones carved out of old ones in 2023.
        SetWord1Field(TimezoneShift, TimezoneMask, timezone & ((1u << 9) - 1u));
        SetWord2Field(TimezoneExt1Shift, 1UL, (timezone & (1u << 9)) >> 9);
    }

    /// <summary>
    /// Get the driveability of the local directed edge given a local edge index.
    /// </summary>
    /// <param name="localidx">Local edge index.</param>
    /// <returns>Returns traversability.</returns>
    public readonly Traversability LocalDriveability(uint localidx)
    {
        uint driveability = (uint)((_word2 >> LocalDriveabilityShift) & LocalDriveabilityMask);
        int s = (int)(localidx * 2); // 2 bits per index
        return (Traversability)((driveability & (3u << s)) >> s);
    }

    /// <summary>Set the auto driveability of the local directed edge given a local edge index.</summary>
    public void SetLocalDriveability(uint localidx, Traversability t)
    {
        // C++ logs a warning and skips when localidx exceeds the max local index.
        if (localidx > MaxLocalEdgeIndex)
        {
            return;
        }

        uint driveability = (uint)((_word2 >> LocalDriveabilityShift) & LocalDriveabilityMask);
        driveability = OverwriteBits(driveability, (uint)t, localidx, 2);
        SetWord2Field(LocalDriveabilityShift, LocalDriveabilityMask, driveability);
    }

    /// <summary>Get the relative road density at the node (0-15).</summary>
    public readonly uint Density => (uint)((_word1 >> DensityShift) & Field4Mask);

    /// <summary>Set the relative road density (clamped to <c>kMaxDensity</c>).</summary>
    public void SetDensity(uint density)
    {
        uint clamped = density > GraphConstants.MaxDensity ? GraphConstants.MaxDensity : density;
        SetWord1Field(DensityShift, Field4Mask, clamped);
    }

    /// <summary>Gets the node type.</summary>
    public readonly NodeType Type => (NodeType)(byte)((_word1 >> TypeShift) & Field4Mask);

    /// <summary>Set the node type.</summary>
    public void SetType(NodeType type) => SetWord1Field(TypeShift, Field4Mask, (uint)type);

    /// <summary>
    /// Evaluates a basic set of conditions to determine if this node is eligible for contraction.
    /// </summary>
    /// <returns>
    /// True if the node has at least 2 edges and does not represent a fork, gate, toll booth, toll
    /// gantry or sump buster.
    /// </returns>
    public readonly bool CanContract()
        => EdgeCount >= 2 && Intersection != IntersectionType.Fork &&
           Type != NodeType.Gate && Type != NodeType.TollBooth &&
           Type != NodeType.TollGantry && Type != NodeType.SumpBuster;

    /// <summary>Checks if this node is a transit node.</summary>
    public readonly bool IsTransit() => Type == NodeType.MultiUseTransitPlatform;

    /// <summary>
    /// Get the number of regular edges across all levels (up to <see cref="MaxLocalEdgeIndex"/>+1).
    /// Does not include shortcut edges, transit edges and transit connections, and transition edges.
    /// </summary>
    public readonly uint LocalEdgeCount => (uint)((_word2 >> LocalEdgeCountShift) & LocalEdgeCountMask) + 1;

    /// <summary>
    /// Set the number of edges on the local level (up to <see cref="MaxLocalEdgeIndex"/>+1). Subtracts
    /// 1 so a value up to kMaxLocalEdgeIndex+1 can be stored.
    /// </summary>
    public void SetLocalEdgeCount(uint n)
    {
        if (n > MaxLocalEdgeIndex + 1)
        {
            SetWord2Field(LocalEdgeCountShift, LocalEdgeCountMask, MaxLocalEdgeIndex);
        }
        else if (n == 0)
        {
            // C++ logs an error ("Node with 0 local edges found") and leaves the field unchanged.
        }
        else
        {
            SetWord2Field(LocalEdgeCountShift, LocalEdgeCountMask, n - 1);
        }
    }

    /// <summary>
    /// Is driving on the right hand side of the road along edges originating at this node?
    /// </summary>
    public readonly bool DriveOnRight => ((_word2 >> DriveOnRightShift) & 1UL) != 0UL;

    /// <summary>Set the flag indicating driving is on the right hand side of the road.</summary>
    public void SetDriveOnRight(bool rsd) => SetWord2Field(DriveOnRightShift, 1UL, rsd ? 1u : 0u);

    /// <summary>Get the elevation at this node in meters.</summary>
    public readonly float Elevation()
    {
        uint elev = (uint)((_word2 >> ElevationShift) & ElevationMask);
        return NodeMinElevation + (elev * NodeElevationPrecision);
    }

    /// <summary>Set the elevation at this node (in meters), clamped to the storable range.</summary>
    public void SetElevation(float elevation)
    {
        if (elevation < NodeMinElevation)
        {
            SetWord2Field(ElevationShift, ElevationMask, 0u);
        }
        else
        {
            uint elev = (uint)((elevation - NodeMinElevation) / NodeElevationPrecision);
            uint stored = elev > NodeMaxStoredElevation ? NodeMaxStoredElevation : elev;
            SetWord2Field(ElevationShift, ElevationMask, stored);
        }
    }

    /// <summary>Was the access information originally set in the data?</summary>
    public readonly bool TaggedAccess => ((_word2 >> TaggedAccessShift) & 1UL) != 0UL;

    /// <summary>Sets the flag indicating if the access information was specified.</summary>
    public void SetTaggedAccess(bool taggedAccess) => SetWord2Field(TaggedAccessShift, 1UL, taggedAccess ? 1u : 0u);

    /// <summary>Is access set as private?</summary>
    public readonly bool PrivateAccess => ((_word2 >> PrivateAccessShift) & 1UL) != 0UL;

    /// <summary>Sets the private_access flag (true when access is private for all travel modes).</summary>
    public void SetPrivateAccess(bool privateAccess) => SetWord2Field(PrivateAccessShift, 1UL, privateAccess ? 1u : 0u);

    /// <summary>Is this node a cash only toll (booth/barrier)?</summary>
    public readonly bool CashOnlyToll => ((_word2 >> CashOnlyTollShift) & 1UL) != 0UL;

    /// <summary>Sets the cash_only_toll flag.</summary>
    public void SetCashOnlyToll(bool cashOnlyToll) => SetWord2Field(CashOnlyTollShift, 1UL, cashOnlyToll ? 1u : 0u);

    /// <summary>Is a mode change allowed at this node?</summary>
    public readonly bool ModeChange => ((_word1 >> ModeChangeShift) & 1UL) != 0UL;

    /// <summary>Sets the flag indicating a mode change is allowed at this node.</summary>
    public void SetModeChange(bool mc) => SetWord1Field(ModeChangeShift, 1UL, mc ? 1u : 0u);

    /// <summary>Is this a named intersection?</summary>
    public readonly bool NamedIntersection => ((_word1 >> NamedShift) & 1UL) != 0UL;

    /// <summary>Sets the flag indicating if this is a named intersection.</summary>
    public void SetNamedIntersection(bool named) => SetWord1Field(NamedShift, 1UL, named ? 1u : 0u);

    /// <summary>Is there a traffic signal at this node?</summary>
    public readonly bool TrafficSignal => ((_word1 >> TrafficSignalShift) & 1UL) != 0UL;

    /// <summary>Set the traffic signal flag.</summary>
    public void SetTrafficSignal(bool trafficSignal) => SetWord1Field(TrafficSignalShift, 1UL, trafficSignal ? 1u : 0u);

    /// <summary>
    /// Gets the transit stop index. This is used for schedule lookups. NOTE: this reuses the
    /// transition_index_ field which is not used for transit-level data.
    /// </summary>
    public readonly uint StopIndex => (uint)((_word2 >> TransitionIndexShift) & TransitionIndexMask);

    /// <summary>Set the transit stop index (reuses the transition index field).</summary>
    public void SetStopIndex(uint stopIndex) => SetWord2Field(TransitionIndexShift, TransitionIndexMask, stopIndex);

    /// <summary>
    /// Get the connecting way id for a transit stop (stored in headings_ while transit data is
    /// connected to the road network). Returns 0 if unset or if used for lon lat.
    /// </summary>
    /// <remarks>
    /// PORT-NOTE: transit-connection helper. If the highest bit of headings_ is set this slot holds
    /// an encoded lon-lat connection point instead of a way id, so this returns 0 in that case.
    /// </remarks>
    public readonly ulong ConnectingWayId()
        => (_headings >> 63) != 0UL ? 0UL : _headings;

    /// <summary>Set the connecting way id for a transit stop.</summary>
    /// <remarks>PORT-NOTE: transit-connection helper; way ids larger than 63 bits are not allowed.</remarks>
    public void SetConnectingWayId(ulong wayid)
    {
        if ((wayid >> 63) != 0UL)
        {
            throw new InvalidOperationException("Way ids larger than 63 bits are not allowed for transit connections");
        }

        _headings = wayid;
    }

    /// <summary>
    /// Get the connection point location to be used for associating this transit station to the road
    /// network, or an invalid point if it is unset.
    /// </summary>
    /// <remarks>
    /// PORT-NOTE: transit-connection helper. The lon/lat are decoded from the 64-bit headings_ value
    /// using the same packing as midgard <c>GeoPoint(uint64_t)</c> (lat in low 31 bits, lon in next
    /// 32 bits). Reproduced locally because the C# PointLL port does not expose that conversion.
    /// </remarks>
    public readonly PointLL ConnectingPoint()
        => (_headings >> 63) != 0UL ? DecodeLonLat(_headings) : new PointLL();

    /// <summary>
    /// Sets the connection point location to be used for associating this transit in/egress to the
    /// road network.
    /// </summary>
    /// <remarks>PORT-NOTE: transit-connection helper; sets the high bit to flag a lon-lat encoding.</remarks>
    public void SetConnectingPoint(PointLL p)
    {
        if (!p.InRange())
        {
            throw new InvalidOperationException("Invalid coordinates are not allowed for transit connections");
        }

        _headings = EncodeLonLat(p) | (1UL << 63);
    }

    /// <summary>
    /// Get the heading of the local edge given its local index. Supports up to 8 local edges.
    /// Headings are stored rounded off to 2 degree values.
    /// </summary>
    /// <param name="localidx">Local edge index.</param>
    /// <returns>Returns heading relative to N (0-360 degrees), or 0 if the index is out of range.</returns>
    public readonly uint Heading(uint localidx)
    {
        if (localidx > MaxLocalEdgeIndex)
        {
            // C++ logs a debug message and returns a heading of 0.
            return 0;
        }

        // Make sure everything is 64 bit!
        int shift = (int)(localidx * 8); // 8 bits per index
        ulong raw = (_headings & ((ulong)255 << shift)) >> shift;
        return (uint)Math.Round(raw * HeadingExpandFactor, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Set the heading of the local edge given its local index. Supports up to 8 local edges.
    /// Headings are reduced to 8 bits.
    /// </summary>
    /// <param name="localidx">Local edge index.</param>
    /// <param name="heading">Heading relative to N (0-359 degrees).</param>
    public void SetHeading(uint localidx, uint heading)
    {
        // C++ logs a warning and skips when localidx exceeds the max local index.
        if (localidx > MaxLocalEdgeIndex)
        {
            return;
        }

        // Has to be 64 bit!
        ulong hdg = (ulong)Math.Round((heading % 360) * HeadingShrinkFactor, MidpointRounding.AwayFromZero);
        _headings |= hdg << (int)(localidx * 8);
    }

    /// <summary>Return the index of the first transition from this node.</summary>
    public readonly uint TransitionIndex => (uint)((_word2 >> TransitionIndexShift) & TransitionIndexMask);

    /// <summary>Set the index of the first transition from this node.</summary>
    public void SetTransitionIndex(uint index) => SetWord2Field(TransitionIndexShift, TransitionIndexMask, index);

    /// <summary>Return the number of transitions from this node.</summary>
    public readonly uint TransitionCount => (uint)((_word2 >> TransitionCountShift) & TransitionCountMask);

    /// <summary>Set the number of transitions from this node.</summary>
    public void SetTransitionCount(uint count) => SetWord2Field(TransitionCountShift, TransitionCountMask, count);

    // ---- Bitfield write helpers ----

    private void SetWord0Field(int shift, ulong mask, ulong value)
        => _word0 = (_word0 & ~(mask << shift)) | ((value & mask) << shift);

    private void SetWord1Field(int shift, ulong mask, ulong value)
        => _word1 = (_word1 & ~(mask << shift)) | ((value & mask) << shift);

    private void SetWord2Field(int shift, ulong mask, ulong value)
        => _word2 = (_word2 & ~(mask << shift)) | ((value & mask) << shift);

    /// <summary>
    /// Get the updated bit field. Faithful port of the anonymous-namespace helper
    /// <c>OverwriteBits(dst, src, pos, len)</c> in <c>src/baldr/nodeinfo.cc</c>.
    /// </summary>
    private static uint OverwriteBits(uint dst, uint src, uint pos, uint len)
    {
        int shift = (int)(pos * len);
        uint mask = ((1u << (int)len) - 1u) << shift;
        return (dst & ~mask) | (src << shift);
    }

    // PORT-NOTE: midgard GeoPoint operator uint64_t() / GeoPoint(uint64_t) equivalents.
    // Layout: lowest 31 bits hold lat, next 32 bits hold lon, highest bit is spare/flag.
    private static ulong EncodeLonLat(PointLL p)
        => ((((ulong)(p.First * 1e7) + (ulong)(180 * 1e7)) & ((1UL << 32) - 1UL)) << 31) |
           (((ulong)(p.Second * 1e7) + (ulong)(90 * 1e7)) & ((1UL << 31) - 1UL));

    private static PointLL DecodeLonLat(ulong encoded)
        => new PointLL(
            ((long)((encoded >> 31) & ((1UL << 32) - 1UL)) - (180 * 10000000L)) * 1e-7,
            ((long)(encoded & ((1UL << 31) - 1UL)) - (90 * 10000000L)) * 1e-7);
}
