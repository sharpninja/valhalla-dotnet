// Faithful C# port of Valhalla baldr DirectedEdge (valhalla @ 3.7.0).
// Sources: valhalla/baldr/directededge.h + src/baldr/directededge.cc
//
// DirectedEdge is a bit-packed edge record read directly from the on-disk tile blob. To preserve
// byte-for-byte tile fidelity, the C++ bitfield layout is reproduced EXACTLY:
//
//   The C++ class is six 8-byte words (48 bytes total). Each word is a set of `uint64_t : N`
//   bitfields packed least-significant-bit first (the layout produced by gcc/clang on
//   little-endian targets, which is how Valhalla writes its tiles). We back each word with a raw
//   integer field and expose the sub-fields through shift/mask accessors so that a tile byte buffer
//   reinterpreted as a DirectedEdge parses identically to the C++ struct.
//
//   Word layout (LSB-first within each word):
//     Word 1 (ulong): endnode_:46 | restrictions_:8 | opp_index_:7 | forward_:1 | leaves_tile_:1 | ctry_crossing_:1
//     Word 2 (ulong): edgeinfo_offset_:25 | access_restriction_:12 | start_restriction_:12 | end_restriction_:12 | complex_restriction_:1 | dest_only_:1 | not_thru_:1
//     Word 3 (ulong): speed_:8 | free_flow_speed_:8 | constrained_flow_speed_:8 | truck_speed_:8 | name_consistency_:8 | use_:6 | lanecount_:4 | density_:4 | classification_:3 | surface_:3 | toll_:1 | roundabout_:1 | truck_route_:1 | has_predicted_speed_:1
//     Word 4 (ulong): forwardaccess_:12 | reverseaccess_:12 | max_up_slope_:5 | max_down_slope_:5 | sac_scale_:3 | cycle_lane_:2 | bike_network_:1 | use_sidepath_:1 | dismount_:1 | sidewalk_left_:1 | sidewalk_right_:1 | shoulder_:1 | lane_conn_:1 | turnlanes_:1 | sign_:1 | internal_:1 | tunnel_:1 | bridge_:1 | traffic_signal_:1 | spare1_:1 | deadend_:1 | bss_connection_:1 | stop_sign_:1 | yield_sign_:1 | hov_type_:1 | indoor_:1 | lit_:1 | dest_only_hgv_:1 | spare4_:3
//     Word 5 (ulong): turntype_:24 | edge_to_left_:8 | length_:24 | weighted_grade_:4 | curvature_:4
//     Word 6 (uint stopimpact_ union + uint): [union StopOrLine] localedgeidx_:7 | opp_local_idx_:7 | shortcut_:7 | superseded_:7 | is_shortcut_:1 | speed_type_:1 | named_:1 | link_:1
//
//   The 6th word's `StopOrLine` union (stopimpact_) is a single 4-byte slot: either StopImpact
//   { stopimpact:24 | edge_to_right:8 } OR lineid (full uint). Both views share the same 4 bytes.
//
//   sizeof(DirectedEdge) == 48 bytes (verified by TestSizeof).
//
// PORT-NOTE: the C++ json(rapidjson::writer_wrapper_t&) method and the access_json() helper are
//            NOT ported (json/rapidjson serialization is an excluded module). All other behavior
//            (bit packing, slope encoding, clamping, OverwriteBit(s) semantics) is reproduced.
// PORT-NOTE: LOG_WARN/LOG_ERROR/LOG_DEBUG diagnostics are reproduced via clamping behavior; the
//            two catastrophic-error paths (set_edgeinfo_offset > max, set_length > max with
//            should_error) throw, matching the C++ std::runtime_error throws.

using System;
using System.Runtime.InteropServices;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Directed edge within the graph. Bit-packed 48-byte tile record. Faithful port of
/// <c>valhalla::baldr::DirectedEdge</c>; the field layout matches the C++ exactly so a raw tile
/// byte buffer reinterpreted as a <see cref="DirectedEdge"/> parses identically.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 48)]
public struct DirectedEdge
{
    /// <summary>Size in bytes of the on-disk DirectedEdge record. Must be 48.</summary>
    public const int SizeOf = 48;

    // ---- Raw backing words (little-endian, LSB-first bitfields) ----

    // 1st 8-byte word
    private ulong _word0;

    // 2nd 8-byte word
    private ulong _word1;

    // 3rd 8-byte word
    private ulong _word2;

    // 4th 8-byte word
    private ulong _word3;

    // 5th 8-byte word
    private ulong _word4;

    // 6th 8-byte word part A: StopOrLine union (4 bytes).
    private uint _stopOrLine;

    // 6th 8-byte word part B: localedgeidx/opp_local_idx/shortcut/superseded/flags (4 bytes).
    private uint _word5;

    // ---- Bit-field helpers (LSB-first, matching C++ packing order) ----

    private static ulong GetBits(ulong word, int shift, int width)
    {
        ulong mask = width == 64 ? ulong.MaxValue : ((1UL << width) - 1UL);
        return (word >> shift) & mask;
    }

    private static void SetBits(ref ulong word, int shift, int width, ulong value)
    {
        ulong mask = (width == 64 ? ulong.MaxValue : ((1UL << width) - 1UL)) << shift;
        word = (word & ~mask) | ((value << shift) & mask);
    }

    private static uint GetBits32(uint word, int shift, int width)
    {
        uint mask = width == 32 ? uint.MaxValue : ((1u << width) - 1u);
        return (word >> shift) & mask;
    }

    private static void SetBits32(ref uint word, int shift, int width, uint value)
    {
        uint mask = (width == 32 ? uint.MaxValue : ((1u << width) - 1u)) << shift;
        word = (word & ~mask) | ((value << shift) & mask);
    }

    // OverwriteBits / OverwriteBit: faithful port of the anonymous-namespace helpers in
    // directededge.cc, used to update a sub-element within a packed bit array.
    private static uint OverwriteBits(uint dst, uint src, uint pos, uint len)
    {
        int shift = (int)(pos * len);
        uint mask = ((1u << (int)len) - 1u) << shift;
        return (dst & ~mask) | (src << shift);
    }

    private static uint OverwriteBit(uint dst, uint src, uint pos)
    {
        uint mask = 1u << (int)pos;
        return (dst & ~mask) | (src << (int)pos);
    }

    /// <summary>
    /// Default constructor. Mirrors the C++ ctor which zeroes the struct then sets
    /// <c>weighted_grade_ = 6</c>.
    /// </summary>
    public static DirectedEdge Create()
    {
        var de = default(DirectedEdge);
        de.SetWeightedGradeRaw(6);
        return de;
    }

    // ============================ Word 0 ============================
    // endnode_:46 | restrictions_:8 | opp_index_:7 | forward_:1 | leaves_tile_:1 | ctry_crossing_:1

    /// <summary>Gets the end node of this directed edge.</summary>
    public GraphId EndNode => new GraphId(GetBits(_word0, 0, 46));

    /// <summary>Set the end node of this directed edge.</summary>
    public void SetEndNode(GraphId endnode) => SetBits(ref _word0, 0, 46, endnode.Value);

    /// <summary>
    /// Simple turn restrictions from the end of this directed edge (mask of local edge indexes).
    /// </summary>
    public uint Restrictions => (uint)GetBits(_word0, 46, 8);

    /// <summary>Set simple turn restrictions from the end of this directed edge.</summary>
    public void SetRestrictions(uint mask)
    {
        if (mask >= (1u << (int)GraphConstants.MaxTurnRestrictionEdges))
        {
            SetBits(ref _word0, 46, 8, mask & ((1u << (int)GraphConstants.MaxTurnRestrictionEdges) - 1u));
        }
        else
        {
            SetBits(ref _word0, 46, 8, mask);
        }
    }

    /// <summary>Index of the opposing directed edge at the end node of this directed edge.</summary>
    public uint OppIndex => (uint)GetBits(_word0, 54, 7);

    /// <summary>Set the index of the opposing directed edge at the end node.</summary>
    public void SetOppIndex(uint oppIndex) => SetBits(ref _word0, 54, 7, oppIndex);

    /// <summary>Is this directed edge stored forward in edgeinfo (true) or reverse (false).</summary>
    public bool Forward => GetBits(_word0, 61, 1) != 0;

    /// <summary>Set the forward flag.</summary>
    public void SetForward(bool forward) => SetBits(ref _word0, 61, 1, forward ? 1UL : 0UL);

    /// <summary>Does this directed edge end in a different tile.</summary>
    public bool LeavesTile => GetBits(_word0, 62, 1) != 0;

    /// <summary>Set the flag indicating whether the end node of this directed edge is in a different tile.</summary>
    public void SetLeavesTile(bool leavesTile) => SetBits(ref _word0, 62, 1, leavesTile ? 1UL : 0UL);

    /// <summary>Get the country crossing flag.</summary>
    public bool CtryCrossing => GetBits(_word0, 63, 1) != 0;

    /// <summary>Set the country crossing flag.</summary>
    public void SetCtryCrossing(bool crossing) => SetBits(ref _word0, 63, 1, crossing ? 1UL : 0UL);

    // ============================ Word 1 ============================
    // edgeinfo_offset_:25 | access_restriction_:12 | start_restriction_:12 | end_restriction_:12 | complex_restriction_:1 | dest_only_:1 | not_thru_:1

    /// <summary>Offset to the common edge data (from the start of the edge info within a tile).</summary>
    public ulong EdgeInfoOffset => GetBits(_word1, 0, 25);

    /// <summary>Set the offset to the common edge info.</summary>
    public void SetEdgeInfoOffset(uint offset)
    {
        if (offset > GraphConstants.MaxEdgeInfoOffset)
        {
            // Consider this a catastrophic error (matches C++ throw).
            throw new InvalidOperationException("DirectedEdge: exceeded maximum edgeinfo offset");
        }

        SetBits(ref _word1, 0, 25, offset);
    }

    /// <summary>General restriction or access condition (per mode) for this directed edge.</summary>
    public ulong AccessRestriction => GetBits(_word1, 25, 12);

    /// <summary>Set the modes which have access restrictions on this edge.</summary>
    public void SetAccessRestriction(uint access) => SetBits(ref _word1, 25, 12, access);

    /// <summary>Complex restriction (per mode) for this directed edge at the start.</summary>
    public uint StartRestriction => (uint)GetBits(_word1, 37, 12);

    /// <summary>Set the modes which have a complex restriction starting on this edge.</summary>
    public void SetStartRestriction(uint modes) => SetBits(ref _word1, 37, 12, modes);

    /// <summary>Complex restriction (per mode) for this directed edge at the end.</summary>
    public uint EndRestriction => (uint)GetBits(_word1, 49, 12);

    /// <summary>Set the modes which have a complex restriction ending on this edge.</summary>
    public void SetEndRestriction(uint modes) => SetBits(ref _word1, 49, 12, modes);

    /// <summary>Is this edge part of a complex restriction?</summary>
    public bool PartOfComplexRestriction => GetBits(_word1, 61, 1) != 0;

    /// <summary>Set the part-of-complex-restriction flag.</summary>
    public void SetComplexRestriction(bool partOf) => SetBits(ref _word1, 61, 1, partOf ? 1UL : 0UL);

    /// <summary>Is this edge destination only / private access?</summary>
    public bool DestOnly => GetBits(_word1, 62, 1) != 0;

    /// <summary>Set the destination only (private) flag.</summary>
    public void SetDestOnly(bool destonly) => SetBits(ref _word1, 62, 1, destonly ? 1UL : 0UL);

    /// <summary>Does this edge lead to a "no thru" region.</summary>
    public bool NotThru => GetBits(_word1, 63, 1) != 0;

    /// <summary>Set the not_thru flag.</summary>
    public void SetNotThru(bool notThru) => SetBits(ref _word1, 63, 1, notThru ? 1UL : 0UL);

    // ============================ Word 2 ============================
    // speed_:8 | free_flow_speed_:8 | constrained_flow_speed_:8 | truck_speed_:8 | name_consistency_:8
    //  | use_:6 | lanecount_:4 | density_:4 | classification_:3 | surface_:3 | toll_:1 | roundabout_:1
    //  | truck_route_:1 | has_predicted_speed_:1

    /// <summary>Average speed in KPH.</summary>
    public uint Speed => (uint)GetBits(_word2, 0, 8);

    /// <summary>Sets the average speed in KPH (clamped to kMaxAssumedSpeed).</summary>
    public void SetSpeed(uint speed)
        => SetBits(ref _word2, 0, 8, speed > GraphConstants.MaxAssumedSpeed ? GraphConstants.MaxAssumedSpeed : speed);

    /// <summary>Free flow speed in KPH (no traffic).</summary>
    public uint FreeFlowSpeed => (uint)GetBits(_word2, 8, 8);

    /// <summary>Sets the free flow speed in KPH (clamped to kMaxAssumedSpeed).</summary>
    public void SetFreeFlowSpeed(uint speed)
        => SetBits(ref _word2, 8, 8, speed > GraphConstants.MaxAssumedSpeed ? GraphConstants.MaxAssumedSpeed : speed);

    /// <summary>Constrained flow speed in KPH (with traffic).</summary>
    public uint ConstrainedFlowSpeed => (uint)GetBits(_word2, 16, 8);

    /// <summary>Sets the constrained flow speed in KPH (clamped to kMaxAssumedSpeed).</summary>
    public void SetConstrainedFlowSpeed(uint speed)
        => SetBits(ref _word2, 16, 8, speed > GraphConstants.MaxAssumedSpeed ? GraphConstants.MaxAssumedSpeed : speed);

    /// <summary>Truck speed in KPH.</summary>
    public uint TruckSpeed => (uint)GetBits(_word2, 24, 8);

    /// <summary>Sets the truck speed in KPH (clamped to kMaxAssumedSpeed).</summary>
    public void SetTruckSpeed(uint speed)
        => SetBits(ref _word2, 24, 8, speed > GraphConstants.MaxAssumedSpeed ? GraphConstants.MaxAssumedSpeed : speed);

    /// <summary>Name consistency mask (8 bits) at the start node with other local edges.</summary>
    public byte NameConsistency => (byte)GetBits(_word2, 32, 8);

    /// <summary>Are names consistent with the from edge (local edge index at the start node).</summary>
    public bool NameConsistencyAt(uint idx) => (NameConsistency & (1 << (int)idx)) != 0;

    /// <summary>Set the name consistency given the other edge's local index (first 8 indexes).</summary>
    public void SetNameConsistency(uint idx, bool c)
    {
        if (idx > GraphConstants.MaxLocalEdgeIndex)
        {
            // LOG_WARN: Local index exceeds max in set_name_consistency, skip.
            return;
        }

        uint updated = OverwriteBit(NameConsistency, c ? 1u : 0u, idx);
        SetBits(ref _word2, 32, 8, updated);
    }

    /// <summary>Set the name consistency mask.</summary>
    public void SetNameConsistency(byte mask) => SetBits(ref _word2, 32, 8, mask);

    /// <summary>Get the specialized use of this edge.</summary>
    public Use Use => (Use)(byte)GetBits(_word2, 40, 6);

    /// <summary>Sets the specialized use type of this edge.</summary>
    public void SetUse(Use use) => SetBits(ref _word2, 40, 6, (byte)use);

    /// <summary>Is the edge a road (includes generic service roads).</summary>
    public bool IsRoad => Use == Use.Road || Use == Use.ServiceRoad;

    /// <summary>Is this edge a transit line (bus or rail)?</summary>
    public bool IsTransitLine => Use == Use.Rail || Use == Use.Bus;

    /// <summary>
    /// Evaluates a basic set of conditions to determine if this directed edge is a valid potential
    /// member of a shortcut.
    /// </summary>
    public bool CanFormShortcut()
        => !IsShortcut && !BssConnection && Use != Use.TransitConnection &&
           Use != Use.EgressConnection && Use != Use.PlatformConnection &&
           Use != Use.Construction;

    /// <summary>Number of lanes for this directed edge.</summary>
    public uint LaneCount => (uint)GetBits(_word2, 46, 4);

    /// <summary>Sets the number of lanes (clamped to kMaxLaneCount, minimum 1).</summary>
    public void SetLaneCount(uint lanecount)
    {
        if (lanecount > GraphConstants.MaxLaneCount)
        {
            SetBits(ref _word2, 46, 4, GraphConstants.MaxLaneCount);
        }
        else if (lanecount == 0)
        {
            SetBits(ref _word2, 46, 4, 1);
        }
        else
        {
            SetBits(ref _word2, 46, 4, lanecount);
        }
    }

    /// <summary>Relative road density along the edge.</summary>
    public uint Density => (uint)GetBits(_word2, 50, 4);

    /// <summary>Set the density along the edge (clamped to kMaxDensity).</summary>
    public void SetDensity(uint density)
        => SetBits(ref _word2, 50, 4, density > GraphConstants.MaxDensity ? GraphConstants.MaxDensity : density);

    /// <summary>Classification (importance) of the road/path.</summary>
    public RoadClass Classification => (RoadClass)(byte)GetBits(_word2, 54, 3);

    /// <summary>Sets the classification (importance) of this edge.</summary>
    public void SetClassification(RoadClass roadclass) => SetBits(ref _word2, 54, 3, (byte)roadclass);

    /// <summary>Surface type (general indication of smoothness).</summary>
    public Surface Surface => (Surface)(byte)GetBits(_word2, 57, 3);

    /// <summary>Sets the surface type.</summary>
    public void SetSurface(Surface surface) => SetBits(ref _word2, 57, 3, (byte)surface);

    /// <summary>Is this edge unpaved or bad surface?</summary>
    public bool Unpaved => Surface >= Surface.Compacted;

    /// <summary>Does this edge have a toll or is it part of a toll road?</summary>
    public bool Toll => GetBits(_word2, 60, 1) != 0;

    /// <summary>Sets the toll flag.</summary>
    public void SetToll(bool toll) => SetBits(ref _word2, 60, 1, toll ? 1UL : 0UL);

    /// <summary>Is this edge part of a roundabout?</summary>
    public bool Roundabout => GetBits(_word2, 61, 1) != 0;

    /// <summary>Sets the roundabout flag.</summary>
    public void SetRoundabout(bool roundabout) => SetBits(ref _word2, 61, 1, roundabout ? 1UL : 0UL);

    /// <summary>Is this edge part of a truck network/route?</summary>
    public bool TruckRoute => GetBits(_word2, 62, 1) != 0;

    /// <summary>Set the truck route flag.</summary>
    public void SetTruckRoute(bool truckRoute) => SetBits(ref _word2, 62, 1, truckRoute ? 1UL : 0UL);

    /// <summary>Flag indicating the edge has predicted speed records.</summary>
    public bool HasPredictedSpeed => GetBits(_word2, 63, 1) != 0;

    /// <summary>Set the flag indicating the edge has predicted speed records.</summary>
    public void SetHasPredictedSpeed(bool p) => SetBits(ref _word2, 63, 1, p ? 1UL : 0UL);

    /// <summary>Indicates whether the edge has either predicted, free or constrained flow speeds.</summary>
    public bool HasFlowSpeed => FreeFlowSpeed > 0 || ConstrainedFlowSpeed > 0 || HasPredictedSpeed;

    // ============================ Word 3 ============================
    // forwardaccess_:12 | reverseaccess_:12 | max_up_slope_:5 | max_down_slope_:5 | sac_scale_:3
    //  | cycle_lane_:2 | bike_network_:1 | use_sidepath_:1 | dismount_:1 | sidewalk_left_:1
    //  | sidewalk_right_:1 | shoulder_:1 | lane_conn_:1 | turnlanes_:1 | sign_:1 | internal_:1
    //  | tunnel_:1 | bridge_:1 | traffic_signal_:1 | spare1_:1 | deadend_:1 | bss_connection_:1
    //  | stop_sign_:1 | yield_sign_:1 | hov_type_:1 | indoor_:1 | lit_:1 | dest_only_hgv_:1 | spare4_:3

    /// <summary>Access modes in the forward direction (bit field).</summary>
    public uint ForwardAccess => (uint)GetBits(_word3, 0, 12);

    /// <summary>Set the access modes in the forward direction (clamped to kAllAccess).</summary>
    public void SetForwardAccess(uint modes)
        => SetBits(ref _word3, 0, 12, modes > GraphConstants.AllAccess ? (modes & GraphConstants.AllAccess) : modes);

    /// <summary>Access modes in the reverse direction (bit field).</summary>
    public uint ReverseAccess => (uint)GetBits(_word3, 12, 12);

    /// <summary>Set the access modes in the reverse direction (clamped to kAllAccess).</summary>
    public void SetReverseAccess(uint modes)
        => SetBits(ref _word3, 12, 12, modes > GraphConstants.AllAccess ? (modes & GraphConstants.AllAccess) : modes);

    /// <summary>Set all forward (and reverse) access modes to true (used for transition edges).</summary>
    public void SetAllForwardAccess()
    {
        SetBits(ref _word3, 0, 12, GraphConstants.AllAccess);
        SetBits(ref _word3, 12, 12, GraphConstants.AllAccess);
    }

    private uint MaxUpSlopeRaw => (uint)GetBits(_word3, 24, 5);

    private uint MaxDownSlopeRaw => (uint)GetBits(_word3, 29, 5);

    /// <summary>
    /// Maximum upward slope (0 to 76 degrees). 1 degree precision to 16 degrees, 4 degree
    /// precision afterwards.
    /// </summary>
    public int MaxUpSlope()
    {
        uint v = MaxUpSlopeRaw;
        return (v & 0x10) == 0 ? (int)v : 16 + (int)((v & 0xf) * 4);
    }

    /// <summary>Sets the maximum upward slope. If slope is negative, 0 is set.</summary>
    public void SetMaxUpSlope(float slope)
    {
        uint v;
        if (slope < 0.0f)
        {
            v = 0;
        }
        else if (slope < 16.0f)
        {
            v = (uint)(int)Math.Ceiling(slope);
        }
        else if (slope < 76.0f)
        {
            v = 0x10u | (uint)(int)Math.Ceiling((slope - 16.0f) * 0.25f);
        }
        else
        {
            v = 0x1f;
        }

        SetBits(ref _word3, 24, 5, v);
    }

    /// <summary>
    /// Maximum downward slope (0 to -76 degrees). 1 degree precision to -16 degrees, 4 degree
    /// precision afterwards.
    /// </summary>
    public int MaxDownSlope()
    {
        uint v = MaxDownSlopeRaw;
        return (v & 0x10) == 0 ? -(int)v : -(16 + (int)((v & 0xf) * 4));
    }

    /// <summary>Sets the maximum downward slope. If slope is positive, 0 is set.</summary>
    public void SetMaxDownSlope(float slope)
    {
        uint v;
        if (slope > 0.0f)
        {
            v = 0;
        }
        else if (slope > -16.0f)
        {
            v = (uint)(int)Math.Ceiling(-slope);
        }
        else if (slope > -76.0f)
        {
            v = 0x10u | (uint)(int)Math.Ceiling((-slope - 16.0f) * 0.25f);
        }
        else
        {
            v = 0x1f;
        }

        SetBits(ref _word3, 29, 5, v);
    }

    /// <summary>SAC scale (hiking difficulty).</summary>
    public SacScale SacScale => (SacScale)(byte)GetBits(_word3, 34, 3);

    /// <summary>Sets the sac scale.</summary>
    public void SetSacScale(SacScale sacScale) => SetBits(ref _word3, 34, 3, (byte)sacScale);

    /// <summary>Cycle lane type along this edge.</summary>
    public CycleLane CycleLane => (CycleLane)(byte)GetBits(_word3, 37, 2);

    /// <summary>Sets the type of cycle lane (if any) present on this edge.</summary>
    public void SetCycleLane(CycleLane cyclelane) => SetBits(ref _word3, 37, 2, (byte)cyclelane);

    /// <summary>Bike network flag for this directed edge.</summary>
    public bool BikeNetwork => GetBits(_word3, 39, 1) != 0;

    /// <summary>Sets the bike network flag.</summary>
    public void SetBikeNetwork(bool bikeNetwork) => SetBits(ref _word3, 39, 1, bikeNetwork ? 1UL : 0UL);

    /// <summary>Is there a cycling path to the side that should be preferred?</summary>
    public bool UseSidepath => GetBits(_word3, 40, 1) != 0;

    /// <summary>Set if a sidepath for bicycling should be preferred instead of this edge.</summary>
    public void SetUseSidepath(bool useSidepath) => SetBits(ref _word3, 40, 1, useSidepath ? 1UL : 0UL);

    /// <summary>Do you need to dismount when biking on this edge?</summary>
    public bool Dismount => GetBits(_word3, 41, 1) != 0;

    /// <summary>Set if cyclists should dismount their bikes along this edge.</summary>
    public void SetDismount(bool dismount) => SetBits(ref _word3, 41, 1, dismount ? 1UL : 0UL);

    /// <summary>Is there a sidewalk to the left of this directed edge?</summary>
    public bool SidewalkLeft => GetBits(_word3, 42, 1) != 0;

    /// <summary>Set the flag for a sidewalk to the left of this directed edge.</summary>
    public void SetSidewalkLeft(bool sidewalk) => SetBits(ref _word3, 42, 1, sidewalk ? 1UL : 0UL);

    /// <summary>Is there a sidewalk to the right of this directed edge?</summary>
    public bool SidewalkRight => GetBits(_word3, 43, 1) != 0;

    /// <summary>Set the flag for a sidewalk to the right of this directed edge.</summary>
    public void SetSidewalkRight(bool sidewalk) => SetBits(ref _word3, 43, 1, sidewalk ? 1UL : 0UL);

    /// <summary>Does the edge have a shoulder?</summary>
    public bool Shoulder => GetBits(_word3, 44, 1) != 0;

    /// <summary>Set if edge has a shoulder.</summary>
    public void SetShoulder(bool shoulder) => SetBits(ref _word3, 44, 1, shoulder ? 1UL : 0UL);

    /// <summary>Does this directed edge have lane connectivity?</summary>
    public bool LaneConnectivity => GetBits(_word3, 45, 1) != 0;

    /// <summary>Sets the lane connectivity flag.</summary>
    public void SetLaneConnectivity(bool lc) => SetBits(ref _word3, 45, 1, lc ? 1UL : 0UL);

    /// <summary>Does this edge have turn lanes at the end of the edge?</summary>
    public bool TurnLanes => GetBits(_word3, 46, 1) != 0;

    /// <summary>Set the flag indicating the edge has turn lanes at the end of the edge.</summary>
    public void SetTurnLanes(bool lanes) => SetBits(ref _word3, 46, 1, lanes ? 1UL : 0UL);

    /// <summary>Does this directed edge have signs?</summary>
    public bool Sign => GetBits(_word3, 47, 1) != 0;

    /// <summary>Sets the sign flag.</summary>
    public void SetSign(bool sign) => SetBits(ref _word3, 47, 1, sign ? 1UL : 0UL);

    /// <summary>Is the edge internal to an intersection?</summary>
    public bool Internal => GetBits(_word3, 48, 1) != 0;

    /// <summary>Sets the intersection internal flag.</summary>
    public void SetInternal(bool @internal) => SetBits(ref _word3, 48, 1, @internal ? 1UL : 0UL);

    /// <summary>Is this edge part of a tunnel?</summary>
    public bool Tunnel => GetBits(_word3, 49, 1) != 0;

    /// <summary>Sets the tunnel flag.</summary>
    public void SetTunnel(bool tunnel) => SetBits(ref _word3, 49, 1, tunnel ? 1UL : 0UL);

    /// <summary>Is this edge part of a bridge?</summary>
    public bool Bridge => GetBits(_word3, 50, 1) != 0;

    /// <summary>Sets the bridge flag.</summary>
    public void SetBridge(bool bridge) => SetBits(ref _word3, 50, 1, bridge ? 1UL : 0UL);

    /// <summary>Traffic signal at end of the directed edge.</summary>
    public bool TrafficSignal => GetBits(_word3, 51, 1) != 0;

    /// <summary>Sets the traffic signal flag.</summary>
    public void SetTrafficSignal(bool signal) => SetBits(ref _word3, 51, 1, signal ? 1UL : 0UL);

    // bit 52 = spare1_ (unused; "seasonal", was never used, can be reclaimed)

    /// <summary>Leads to a dead-end (no other drivable roads).</summary>
    public bool Deadend => GetBits(_word3, 53, 1) != 0;

    /// <summary>Set the dead end flag.</summary>
    public void SetDeadend(bool d) => SetBits(ref _word3, 53, 1, d ? 1UL : 0UL);

    /// <summary>Does this lead to (come out from) a bike share station?</summary>
    public bool BssConnection => GetBits(_word3, 54, 1) != 0;

    /// <summary>Set the bike share station connection flag.</summary>
    public void SetBssConnection(bool bssConnection) => SetBits(ref _word3, 54, 1, bssConnection ? 1UL : 0UL);

    /// <summary>Stop sign at end of the directed edge.</summary>
    public bool StopSign => GetBits(_word3, 55, 1) != 0;

    /// <summary>Sets the stop sign flag.</summary>
    public void SetStopSign(bool sign) => SetBits(ref _word3, 55, 1, sign ? 1UL : 0UL);

    /// <summary>Yield/give way sign at end of the directed edge.</summary>
    public bool YieldSign => GetBits(_word3, 56, 1) != 0;

    /// <summary>Sets the yield sign flag.</summary>
    public void SetYieldSign(bool sign) => SetBits(ref _word3, 56, 1, sign ? 1UL : 0UL);

    /// <summary>Get the HOV type. Only meaningful if <see cref="IsHovOnly"/> is true.</summary>
    public HovEdgeType HovType => (HovEdgeType)(byte)GetBits(_word3, 57, 1);

    /// <summary>Sets the HOV type.</summary>
    public void SetHovType(HovEdgeType hovType) => SetBits(ref _word3, 57, 1, (byte)hovType);

    /// <summary>Returns t/f if this edge is HOV only.</summary>
    public bool IsHovOnly()
        => (ForwardAccess & GraphConstants.HovAccess) != 0 && (ForwardAccess & GraphConstants.AutoAccess) == 0;

    /// <summary>Is this edge indoor?</summary>
    public bool Indoor => GetBits(_word3, 58, 1) != 0;

    /// <summary>Sets the indoor flag.</summary>
    public void SetIndoor(bool indoor) => SetBits(ref _word3, 58, 1, indoor ? 1UL : 0UL);

    /// <summary>Is the edge lit?</summary>
    public bool Lit => GetBits(_word3, 59, 1) != 0;

    /// <summary>Set the lit flag.</summary>
    public void SetLit(bool lit) => SetBits(ref _word3, 59, 1, lit ? 1UL : 0UL);

    /// <summary>Is this edge destination only / private access for HGV?</summary>
    public bool DestOnlyHgv => GetBits(_word3, 60, 1) != 0;

    /// <summary>Sets the destination only (private) flag for HGV.</summary>
    public void SetDestOnlyHgv(bool destonlyHgv) => SetBits(ref _word3, 60, 1, destonlyHgv ? 1UL : 0UL);

    // bits 61-63 = spare4_ (3 bits, unused)

    // ============================ Word 4 ============================
    // turntype_:24 | edge_to_left_:8 | length_:24 | weighted_grade_:4 | curvature_:4

    private uint TurnTypeRaw => (uint)GetBits(_word4, 0, 24);

    private uint EdgeToLeftRaw => (uint)GetBits(_word4, 24, 8);

    /// <summary>
    /// Gets the turn type given the prior edge's local index (index of the inbound edge). 3 bits
    /// per index.
    /// </summary>
    public Turn.Type TurnType(uint localidx)
    {
        int shift = (int)(localidx * 3);
        return (Turn.Type)((TurnTypeRaw & (7u << shift)) >> shift);
    }

    /// <summary>Sets the turn type given the prior edge's local index.</summary>
    public void SetTurnType(uint localidx, Turn.Type turntype)
    {
        if (localidx > GraphConstants.MaxLocalEdgeIndex)
        {
            // LOG_WARN: Exceeding max local index in set_turntype. Skipping.
            return;
        }

        uint updated = OverwriteBits(TurnTypeRaw, (uint)(byte)turntype, localidx, 3);
        SetBits(ref _word4, 0, 24, updated);
    }

    /// <summary>Is there an edge to the left, in between the from edge and this edge.</summary>
    public bool EdgeToLeft(uint localidx) => (EdgeToLeftRaw & (1u << (int)localidx)) != 0;

    /// <summary>Set the flag indicating there is an edge to the left.</summary>
    public void SetEdgeToLeft(uint localidx, bool left)
    {
        if (localidx > GraphConstants.MaxLocalEdgeIndex)
        {
            // LOG_WARN: Exceeding max local index in set_edge_to_left. Skipping.
            return;
        }

        uint updated = OverwriteBits(EdgeToLeftRaw, left ? 1u : 0u, localidx, 1);
        SetBits(ref _word4, 24, 8, updated);
    }

    /// <summary>Length of the edge in meters.</summary>
    public uint Length => (uint)GetBits(_word4, 32, 24);

    /// <summary>
    /// Sets the length of the edge in meters. If length exceeds the max and <paramref name="shouldError"/>
    /// is true, throws (matching the C++ catastrophic error); otherwise clamps.
    /// </summary>
    public void SetLength(uint length, bool shouldError = true)
    {
        if (length > GraphConstants.MaxEdgeLength)
        {
            if (shouldError)
            {
                throw new InvalidOperationException("DirectedEdgeBuilder: exceeded maximum edge length");
            }

            SetBits(ref _word4, 32, 24, GraphConstants.MaxEdgeLength);
        }
        else
        {
            SetBits(ref _word4, 32, 24, length);
        }
    }

    /// <summary>Weighted grade factor (0-15), where 0 is a -10% grade and 15 is 15%.</summary>
    public uint WeightedGrade => (uint)GetBits(_word4, 56, 4);

    private void SetWeightedGradeRaw(uint factor) => SetBits(ref _word4, 56, 4, factor);

    /// <summary>Sets the weighted grade factor (0-15); defaults to 6 if exceeding the max.</summary>
    public void SetWeightedGrade(uint factor)
        => SetWeightedGradeRaw(factor > GraphConstants.MaxGradeFactor ? 6u : factor);

    /// <summary>Road curvature factor (0-15).</summary>
    public uint Curvature => (uint)GetBits(_word4, 60, 4);

    /// <summary>Sets the curvature factor (0-15); defaults to 0 if exceeding the max.</summary>
    public void SetCurvature(uint factor)
        => SetBits(ref _word4, 60, 4, factor > GraphConstants.MaxCurvatureFactor ? 0u : factor);

    // ============================ Word 5 part A: StopOrLine union ============================
    // StopImpact { stopimpact:24 | edge_to_right:8 }  OR  lineid (full uint)

    /// <summary>
    /// Get the stop impact when transitioning from the prior edge (local index of the inbound
    /// edge). 3 bits per index.
    /// </summary>
    public uint StopImpact(uint localidx)
    {
        int shift = (int)(localidx * 3);
        uint stopimpact = GetBits32(_stopOrLine, 0, 24);
        return (stopimpact & (7u << shift)) >> shift;
    }

    /// <summary>Set the stop impact when transitioning from the prior edge (clamped to kMaxStopImpact).</summary>
    public void SetStopImpact(uint localidx, uint stopimpact)
    {
        uint current = GetBits32(_stopOrLine, 0, 24);
        uint value = stopimpact > GraphConstants.MaxStopImpact ? GraphConstants.MaxStopImpact : stopimpact;
        uint updated = OverwriteBits(current, value, localidx, 3);
        SetBits32(ref _stopOrLine, 0, 24, updated);
    }

    /// <summary>Is there an edge to the right, in between the from edge and this edge.</summary>
    public bool EdgeToRight(uint localidx)
    {
        uint edgeToRight = GetBits32(_stopOrLine, 24, 8);
        return (edgeToRight & (1u << (int)localidx)) != 0;
    }

    /// <summary>Set the flag indicating there is an edge to the right.</summary>
    public void SetEdgeToRight(uint localidx, bool right)
    {
        if (localidx > GraphConstants.MaxLocalEdgeIndex)
        {
            // LOG_WARN: Exceeding max local index in set_edge_to_right. Skipping.
            return;
        }

        uint current = GetBits32(_stopOrLine, 24, 8);
        uint updated = OverwriteBits(current, right ? 1u : 0u, localidx, 1);
        SetBits32(ref _stopOrLine, 24, 8, updated);
    }

    /// <summary>Get the transit line Id (shares storage with the stop impact union).</summary>
    public uint LineId => _stopOrLine;

    /// <summary>Set the unique transit line Id.</summary>
    public void SetLineId(uint lineid) => _stopOrLine = lineid;

    // ============================ Word 5 part B ============================
    // localedgeidx_:7 | opp_local_idx_:7 | shortcut_:7 | superseded_:7 | is_shortcut_:1
    //  | speed_type_:1 | named_:1 | link_:1

    /// <summary>Index of the directed edge on the local level of the graph hierarchy.</summary>
    public uint LocalEdgeIdx => GetBits32(_word5, 0, 7);

    /// <summary>Set the index of the directed edge on the local level (clamped to kMaxEdgesPerNode).</summary>
    public void SetLocalEdgeIdx(uint idx)
        => SetBits32(ref _word5, 0, 7, idx > GraphConstants.MaxEdgesPerNode ? GraphConstants.MaxEdgesPerNode : idx);

    /// <summary>Index of the opposing directed edge on the local hierarchy level at the end node.</summary>
    public uint OppLocalIdx => GetBits32(_word5, 7, 7);

    /// <summary>Set the opposing local edge index (clamped to kMaxEdgesPerNode).</summary>
    public void SetOppLocalIdx(uint idx)
        => SetBits32(ref _word5, 7, 7, idx > GraphConstants.MaxEdgesPerNode ? GraphConstants.MaxEdgesPerNode : idx);

    /// <summary>Mask of the superseded edge bypassed by a shortcut. 0 indicates not a shortcut.</summary>
    public uint Shortcut => GetBits32(_word5, 14, 7);

    /// <summary>
    /// Hijack the shortcut mask to move ferry edges to lower hierarchies. Sets the shortcut field
    /// to the road class and toggles is_shortcut_.
    /// </summary>
    public void SetHierarchyRoadClass(RoadClass rc, bool reset = false)
    {
        SetBits32(ref _word5, 14, 7, (byte)rc);
        SetBits32(ref _word5, 28, 1, reset ? 0u : 1u);
    }

    /// <summary>Set the mask for whether this edge represents a shortcut between 2 nodes.</summary>
    public void SetShortcut(uint shortcut)
    {
        // 0 is not a valid shortcut.
        if (shortcut == 0)
        {
            // LOG_WARN: Invalid shortcut mask = 0.
            return;
        }

        // Set the shortcut mask if within the max number of masked shortcut edges.
        if (shortcut <= GraphConstants.MaxShortcutsFromNode)
        {
            SetBits32(ref _word5, 14, 7, 1u << (int)(shortcut - 1));
        }

        // Set the is_shortcut flag.
        SetBits32(ref _word5, 28, 1, 1u);
    }

    /// <summary>Mask indicating the shortcut that supersedes this directed edge.</summary>
    public uint Superseded => GetBits32(_word5, 21, 7);

    /// <summary>
    /// The shortcut index that was originally passed to <see cref="SetSuperseded"/> (the set-bit
    /// position in the superseded mask, 1-based; 0 if none). Mirrors C++ ffs(superseded_).
    /// </summary>
    public uint SupersededIdx()
    {
        uint mask = Superseded;
        if (mask == 0)
        {
            return 0;
        }

        uint idx = 1;
        while ((mask & 1) == 0)
        {
            mask >>= 1;
            ++idx;
        }

        return idx;
    }

    /// <summary>Set the mask for whether this edge is superseded by a shortcut edge.</summary>
    public void SetSuperseded(uint superseded)
    {
        if (superseded > GraphConstants.MaxShortcutsFromNode)
        {
            // LOG_WARN: Exceeding max shortcut edges from a node.
        }
        else if (superseded == 0)
        {
            SetBits32(ref _word5, 21, 7, 0u);
        }
        else
        {
            SetBits32(ref _word5, 21, 7, 1u << (int)(superseded - 1));
        }
    }

    /// <summary>Is this edge a shortcut edge.</summary>
    public bool IsShortcut => GetBits32(_word5, 28, 1) != 0;

    /// <summary>Speed type (used in setting default speeds).</summary>
    public SpeedType SpeedType => (SpeedType)(byte)GetBits32(_word5, 29, 1);

    /// <summary>Set the speed type.</summary>
    public void SetSpeedType(SpeedType speedType) => SetBits32(ref _word5, 29, 1, (byte)speedType);

    /// <summary>Is this edge named?</summary>
    public bool Named => GetBits32(_word5, 30, 1) != 0;

    /// <summary>Sets the named flag.</summary>
    public void SetNamed(bool named) => SetBits32(ref _word5, 30, 1, named ? 1u : 0u);

    /// <summary>Is this edge a link/ramp?</summary>
    public bool Link => GetBits32(_word5, 31, 1) != 0;

    /// <summary>Sets the link flag (ramp or turn channel).</summary>
    public void SetLink(bool link) => SetBits32(ref _word5, 31, 1, link ? 1u : 0u);
}

/// <summary>
/// Extended directed edge attribution. Provides the ability to add extra attribution per directed
/// edge without breaking backward compatibility. Currently unused. Faithful port of C++
/// <c>class DirectedEdgeExt</c> (a single 64-bit spare word, 8 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 8)]
public struct DirectedEdgeExt
{
    /// <summary>Size in bytes of the on-disk DirectedEdgeExt record. Must be 8.</summary>
    public const int SizeOf = 8;

    // spare0_ : 64 (unused)
    private ulong _spare0;
}
