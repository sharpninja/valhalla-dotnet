// Faithful C# port of Valhalla mjolnir OSMNode.
// Source: valhalla/mjolnir/osmnode.h @ 3.7.0
//
// OSMNode is the result of parsing an OSM node. The C++ struct packs many fields
// into bit-fields across three 32/64-bit words plus a fixed-precision lat/lng pair.
// This port keeps the exact field widths/ranges and the exact lat/lng encoding so
// that the downstream graph builder sees identical values.

using System;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// OSM node information. Result of parsing an OSM node. Faithful port of the C++
/// <c>struct OSMNode</c> from <c>valhalla/mjolnir/osmnode.h</c>.
/// </summary>
public struct OSMNode
{
    /// <summary>Maximum value for the name/ref/exit_to/linguistic indices (21 bits).</summary>
    public const uint MaxNodeNameIndex = 2097151;

    private const uint U32Max = uint.MaxValue;

    // The osm id of the node.
    private ulong _osmid;

    // 21-bit name indices into OSMData.node_names.
    private uint _nameIndex;     // : 21
    private uint _refIndex;      // : 21
    private uint _exitToIndex;   // : 21
    private bool _namedIntersection; // : 1

    // 21-bit linguistic index plus a pile of single-bit flags.
    private uint _linguisticInfoIndex; // : 21
    private bool _trafficSignal;       // : 1
    private bool _forwardSignal;       // : 1
    private bool _backwardSignal;      // : 1
    private bool _stopSign;            // : 1
    private bool _forwardStop;         // : 1
    private bool _backwardStop;        // : 1
    private bool _yieldSign;           // : 1
    private bool _forwardYield;        // : 1
    private bool _backwardYield;       // : 1
    private bool _minor;               // : 1
    private bool _direction;           // : 1

    private uint _access;          // : 12
    private byte _type;            // : 4
    private bool _intersection;    // : 1
    private bool _nonLinkEdge;     // : 1
    private bool _linkEdge;        // : 1
    private bool _shortlink;       // : 1
    private bool _nonFerryEdge;    // : 1
    private bool _ferryEdge;       // : 1
    private bool _flatLoop;        // : 1
    private bool _urban;           // : 1
    private bool _taggedAccess;    // : 1
    private bool _privateAccess;   // : 1
    private bool _cashOnlyToll;    // : 1

    // Lat,lng of the node at fixed 7-digit precision.
    private uint _lng7;
    private uint _lat7;

    /// <summary>Constructs an empty node (all fields zeroed).</summary>
    public OSMNode()
    {
        _osmid = 0;
    }

    /// <summary>
    /// Constructs a node with the given OSM id and optional lat/lng. Matching the C++
    /// constructor, when lat/lng default to <see cref="double.MaxValue"/> the encoded
    /// coordinates become "invalid" (uint max).
    /// </summary>
    public OSMNode(ulong id, double lat = double.MaxValue, double lng = double.MaxValue)
    {
        _osmid = 0;
        SetId(id);
        SetLatLng(lng, lat);
    }

    /// <summary>Sets the OSM node id.</summary>
    public void SetId(ulong id) => _osmid = id;

    /// <summary>Gets the OSM node id (public field analogue of C++ <c>osmid_</c>).</summary>
    public ulong Osmid
    {
        readonly get => _osmid;
        set => _osmid = value;
    }

    /// <summary>
    /// Sets the lat,lng using the exact C++ fixed-precision encoding:
    /// round((lng + 180) * 1e7) clamped to [0, 360e7], else uint max; likewise for lat.
    /// </summary>
    public void SetLatLng(double lng, double lat)
    {
        lng = Math.Round((lng + 180.0) * 1e7);
        _lng7 = (lng >= 0 && lng <= 360.0 * 1e7) ? (uint)lng : U32Max;

        lat = Math.Round((lat + 90.0) * 1e7);
        _lat7 = (lat >= 0 && lat <= 180.0 * 1e7) ? (uint)lat : U32Max;
    }

    /// <summary>
    /// Gets the lat,lng. Returns an invalid <see cref="PointLL"/> (matching C++ default
    /// PointLL) if either coordinate is borked.
    /// </summary>
    public readonly PointLL LatLng()
    {
        if (_lng7 == U32Max || _lat7 == U32Max)
        {
            return new PointLL();
        }

        return new PointLL(_lng7 * 1e-7 - 180.0, _lat7 * 1e-7 - 90.0);
    }

    private static uint CheckedNameIndex(uint index)
    {
        if (index > MaxNodeNameIndex)
        {
            throw new InvalidOperationException("OSMNode: exceeded maximum name index");
        }

        return index;
    }

    /// <summary>Sets the name index into the unique node names list (throws if &gt; max).</summary>
    public void SetNameIndex(uint index) => _nameIndex = CheckedNameIndex(index);

    /// <summary>Gets the name index into the unique node names list.</summary>
    public readonly uint NameIndex() => _nameIndex;

    /// <summary>Does the node have a name (name index is non-zero)?</summary>
    public readonly bool HasName() => _nameIndex > 0;

    /// <summary>Sets the ref index into the unique node names list (throws if &gt; max).</summary>
    public void SetRefIndex(uint index) => _refIndex = CheckedNameIndex(index);

    /// <summary>Gets the ref index into the unique node names list.</summary>
    public readonly uint RefIndex() => _refIndex;

    /// <summary>Does the node have ref information (ref index is non-zero)?</summary>
    public readonly bool HasRef() => _refIndex > 0;

    /// <summary>Sets the exit_to index into the unique node names list (throws if &gt; max).</summary>
    public void SetExitToIndex(uint index) => _exitToIndex = CheckedNameIndex(index);

    /// <summary>Gets the exit_to index into the unique node names list.</summary>
    public readonly uint ExitToIndex() => _exitToIndex;

    /// <summary>Does the node have exit_to information (exit_to index is non-zero)?</summary>
    public readonly bool HasExitTo() => _exitToIndex > 0;

    /// <summary>Sets the access mask (12 bits).</summary>
    public void SetAccess(uint mask) => _access = mask & 0xFFF;

    /// <summary>Gets the access mask.</summary>
    public readonly uint Access() => _access;

    /// <summary>Sets the node type.</summary>
    public void SetType(NodeType type) => _type = (byte)((byte)type & 0xF);

    /// <summary>Gets the node type.</summary>
    public readonly NodeType Type() => (NodeType)_type;

    /// <summary>
    /// Sets the intersection flag. True if this node is an end node of more than one way.
    /// </summary>
    public void SetIntersection(bool intersection) => _intersection = intersection;

    /// <summary>Gets the intersection flag.</summary>
    public readonly bool Intersection() => _intersection;

    /// <summary>Sets the traffic_signal flag.</summary>
    public void SetTrafficSignal(bool v) => _trafficSignal = v;

    /// <summary>Gets the traffic_signal flag.</summary>
    public readonly bool TrafficSignal() => _trafficSignal;

    /// <summary>Sets the forward_signal flag.</summary>
    public void SetForwardSignal(bool v) => _forwardSignal = v;

    /// <summary>Gets the forward_signal flag.</summary>
    public readonly bool ForwardSignal() => _forwardSignal;

    /// <summary>Sets the backward_signal flag.</summary>
    public void SetBackwardSignal(bool v) => _backwardSignal = v;

    /// <summary>Gets the backward_signal flag.</summary>
    public readonly bool BackwardSignal() => _backwardSignal;

    /// <summary>Sets the stop_sign flag.</summary>
    public void SetStopSign(bool v) => _stopSign = v;

    /// <summary>Gets the stop_sign flag.</summary>
    public readonly bool StopSign() => _stopSign;

    /// <summary>Sets the forward_stop flag.</summary>
    public void SetForwardStop(bool v) => _forwardStop = v;

    /// <summary>Gets the forward_stop flag.</summary>
    public readonly bool ForwardStop() => _forwardStop;

    /// <summary>Sets the backward_stop flag.</summary>
    public void SetBackwardStop(bool v) => _backwardStop = v;

    /// <summary>Gets the backward_stop flag.</summary>
    public readonly bool BackwardStop() => _backwardStop;

    /// <summary>Sets the yield_sign flag.</summary>
    public void SetYieldSign(bool v) => _yieldSign = v;

    /// <summary>Gets the yield_sign flag.</summary>
    public readonly bool YieldSign() => _yieldSign;

    /// <summary>Sets the forward_yield flag.</summary>
    public void SetForwardYield(bool v) => _forwardYield = v;

    /// <summary>Gets the forward_yield flag.</summary>
    public readonly bool ForwardYield() => _forwardYield;

    /// <summary>Sets the backward_yield flag.</summary>
    public void SetBackwardYield(bool v) => _backwardYield = v;

    /// <summary>Gets the backward_yield flag.</summary>
    public readonly bool BackwardYield() => _backwardYield;

    /// <summary>Sets the minor flag.</summary>
    public void SetMinor(bool v) => _minor = v;

    /// <summary>Gets the minor flag.</summary>
    public readonly bool Minor() => _minor;

    /// <summary>Sets the direction flag.</summary>
    public void SetDirection(bool v) => _direction = v;

    /// <summary>Gets the direction flag.</summary>
    public readonly bool Direction() => _direction;

    /// <summary>Sets the named intersection flag.</summary>
    public void SetNamedIntersection(bool named) => _namedIntersection = named;

    /// <summary>Gets the named intersection flag.</summary>
    public readonly bool NamedIntersection() => _namedIntersection;

    /// <summary>Sets the urban flag.</summary>
    public void SetUrban(bool urban) => _urban = urban;

    /// <summary>Gets the urban flag.</summary>
    public readonly bool Urban() => _urban;

    /// <summary>Sets the tagged_access flag (was access originally tagged?).</summary>
    public void SetTaggedAccess(bool v) => _taggedAccess = v;

    /// <summary>Gets the tagged_access flag.</summary>
    public readonly bool TaggedAccess() => _taggedAccess;

    /// <summary>Sets the private_access flag.</summary>
    public void SetPrivateAccess(bool v) => _privateAccess = v;

    /// <summary>Gets the private_access flag.</summary>
    public readonly bool PrivateAccess() => _privateAccess;

    /// <summary>Sets the cash_only_toll flag.</summary>
    public void SetCashOnlyToll(bool v) => _cashOnlyToll = v;

    /// <summary>Gets the cash_only_toll flag.</summary>
    public readonly bool CashOnlyToll() => _cashOnlyToll;

    /// <summary>Sets the intersection flag's companion non_link/link/ferry edge bookkeeping flags.</summary>
    public void SetNonLinkEdge(bool v) => _nonLinkEdge = v;

    /// <summary>Gets the non_link_edge flag.</summary>
    public readonly bool NonLinkEdge() => _nonLinkEdge;

    /// <summary>Sets the link_edge flag.</summary>
    public void SetLinkEdge(bool v) => _linkEdge = v;

    /// <summary>Gets the link_edge flag.</summary>
    public readonly bool LinkEdge() => _linkEdge;

    /// <summary>Sets the shortlink flag (link edge &lt; kMaxInternalLength).</summary>
    public void SetShortlink(bool v) => _shortlink = v;

    /// <summary>Gets the shortlink flag.</summary>
    public readonly bool Shortlink() => _shortlink;

    /// <summary>Sets the non_ferry_edge flag.</summary>
    public void SetNonFerryEdge(bool v) => _nonFerryEdge = v;

    /// <summary>Gets the non_ferry_edge flag.</summary>
    public readonly bool NonFerryEdge() => _nonFerryEdge;

    /// <summary>Sets the ferry_edge flag.</summary>
    public void SetFerryEdge(bool v) => _ferryEdge = v;

    /// <summary>Gets the ferry_edge flag.</summary>
    public readonly bool FerryEdge() => _ferryEdge;

    /// <summary>Sets the flat_loop flag.</summary>
    public void SetFlatLoop(bool v) => _flatLoop = v;

    /// <summary>Gets the flat_loop flag.</summary>
    public readonly bool FlatLoop() => _flatLoop;

    /// <summary>Sets the index for the linguistic info (throws if &gt; max).</summary>
    public void SetLinguisticInfoIndex(uint idx)
    {
        if (idx > MaxNodeNameIndex)
        {
            throw new InvalidOperationException("OSMNode: exceeded maximum linguistic info index");
        }

        _linguisticInfoIndex = idx;
    }

    /// <summary>Gets the linguistic info index.</summary>
    public readonly uint LinguisticInfoIndex() => _linguisticInfoIndex;
}
