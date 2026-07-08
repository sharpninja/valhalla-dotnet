// Faithful C# port of Valhalla thor PathInfo (valhalla @ 3.7.0).
// Source: F:/github/valhalla/valhalla/thor/pathinfo.h
//
// Simple(ish) structure to pass path information from PathAlgorithm to TripLegBuilder: the travel
// mode, elapsed cost (with any turn cost at the start of the edge folded in), trip id, directed
// edge id, path distance, restriction index, the transition cost at the beginning of the edge, and
// a few shortcut / discontinuity flags.
//
// PORT-NOTE: the C++ operator<< stream output is not ported (diagnostic only). The struct is a
// reference type here (class) since it is collected in lists and the C++ aggregate has no special
// value semantics that routing relies on.

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Thor;

/// <summary>
/// Simple structure to pass path information from <see cref="PathAlgorithm"/> to the trip-leg
/// builder. Faithful port of <c>valhalla::thor::PathInfo</c>.
/// </summary>
public sealed class PathInfo
{
    /// <summary>
    /// Constructor with values. Faithful port of the C++ <c>PathInfo(...)</c> constructor.
    /// </summary>
    /// <param name="m">Travel mode along this edge.</param>
    /// <param name="c">Elapsed cost at the end of the edge including any turn cost at the start.</param>
    /// <param name="edge">Directed edge id.</param>
    /// <param name="tripid">Trip id (0 if not a transit edge).</param>
    /// <param name="pathDistance">Distance (in meters) from the start to the edge.</param>
    /// <param name="restrictionIdx">Record which restriction (default kInvalidRestriction).</param>
    /// <param name="tc">Turn cost at the beginning of the edge.</param>
    /// <param name="startNodeIsRecovered">Whether the start node is an inner node of a recovered shortcut.</param>
    /// <param name="isShortcut">Whether the edge is a shortcut edge.</param>
    public PathInfo(
        TravelMode m,
        Cost c,
        GraphId edge,
        uint tripid,
        float pathDistance,
        byte restrictionIdx = GraphConstants.InvalidRestriction,
        Cost tc = default,
        bool startNodeIsRecovered = false,
        bool isShortcut = false)
    {
        Mode = m;
        ElapsedCost = c;
        TripId = tripid;
        Edgeid = edge;
        PathDistance = pathDistance;
        RestrictionIndex = restrictionIdx;
        TransitionCost = tc;
        StartNodeIsRecovered = startNodeIsRecovered;
        IsShortcut = isShortcut;
        IsDisconnected = false;
    }

    /// <summary>Travel mode along this edge.</summary>
    public TravelMode Mode { get; set; }

    /// <summary>Elapsed cost at the end of the edge including any turn cost at the start of the edge.</summary>
    public Cost ElapsedCost { get; set; }

    /// <summary>Trip Id (0 if not a transit edge).</summary>
    public uint TripId { get; set; }

    /// <summary>Directed edge Id.</summary>
    public GraphId Edgeid { get; set; }

    /// <summary>Distance (in meters) from the start to the edge.</summary>
    public float PathDistance { get; set; }

    /// <summary>Record which restriction.</summary>
    public byte RestrictionIndex { get; set; }

    /// <summary>Turn cost at the beginning of the edge.</summary>
    public Cost TransitionCost { get; set; }

    /// <summary>
    /// Indicates if the start node of the edge is an inner node of a shortcut that was recovered.
    /// This flag is 'false' for the first and the last shortcut nodes.
    /// </summary>
    public bool StartNodeIsRecovered { get; set; }

    /// <summary>Whether or not the edge is a shortcut edge.</summary>
    public bool IsShortcut { get; set; }

    /// <summary>
    /// True when this edge is not connected to the previous edge (e.g. trace matching discontinuity).
    /// </summary>
    public bool IsDisconnected { get; set; }
}
