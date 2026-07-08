// Faithful C# port of Valhalla thor PathAlgorithm base (valhalla @ 3.7.0).
// Source: F:/github/valhalla/valhalla/thor/pathalgorithm.h
//
// Pure virtual base defining the interface for the shortest-path algorithms (the concrete
// bidirectional / unidirectional A* algorithms derive from this). Also ports the free helper
// IsTrivial and the EdgeMetadata iterator struct used in the Expand* functions, plus the
// kBucketCount / kInterruptIterationsInterval constants and the ExpansionType enum.
//
// PORT-NOTES (per task scope: point-to-point auto/truck only):
//   - GetBestPath takes the loki-correlated origin/destination as C# baldr::PathLocation values
//     (which carry the correlated PathEdges) instead of the proto valhalla::Location. The sif
//     mode_costing_t is the ported ModeCosting; Options is the ported sif Options.
//   - The expansion-tracking callback (set_track_expansion) used proto Expansion_* enums purely for
//     the /expansion debug endpoint, which is EXCLUDED. The hook is preserved with plain C# enums
//     (ExpansionEdgeStatus / ExpansionAlgoType) so derived algorithms can still report progress, but
//     no proto wire type is referenced.
//   - tz_cache_ (baldr::DateTime::tz_sys_info_cache_t) is part of the DateTime/timezone slice that
//     is ported separately; it is represented here as an opaque object the derived algorithms own.

using System;
using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Sif;

// graph_tile_ptr alias to read like the C++ signatures.
using GraphTilePtr = SharpNinja.Valhalla.Baldr.GraphTile;

namespace SharpNinja.Valhalla.Thor;

/// <summary>Direction of expansion. Faithful port of <c>enum class ExpansionType</c>.</summary>
public enum ExpansionType
{
    /// <summary>Forward expansion (from the origin toward the destination).</summary>
    Forward = 0,

    /// <summary>Reverse expansion (from the destination toward the origin).</summary>
    Reverse = 1,

    /// <summary>Multimodal expansion (excluded from this slice; kept for enum parity).</summary>
    Multimodal = 2,
}

/// <summary>
/// Edge status reported to the expansion-tracking callback. C# stand-in for the proto
/// <c>Expansion_EdgeStatus</c> (the proto/expansion type is excluded; values mirror its semantics).
/// </summary>
public enum ExpansionEdgeStatus
{
    /// <summary>The edge was reached / relaxed (temporary label).</summary>
    Reached = 0,

    /// <summary>The edge was settled (permanent label).</summary>
    Settled = 1,

    /// <summary>The edge connected the two search trees (bidirectional).</summary>
    Connected = 2,
}

/// <summary>
/// Algorithm/expansion type reported to the expansion-tracking callback. C# stand-in for the proto
/// <c>Expansion_ExpansionType</c>.
/// </summary>
public enum ExpansionAlgoType
{
    /// <summary>Forward A* expansion.</summary>
    Forward = 0,

    /// <summary>Reverse A* expansion.</summary>
    Reverse = 1,
}

/// <summary>
/// Signature of the functor that tracks the algorithm's expansion. Faithful (semantic) port of the
/// C++ <c>expansion_callback_t</c> with the proto enums replaced by C# enums.
/// </summary>
public delegate void ExpansionCallback(
    GraphReader graphReader,
    GraphId edgeId,
    GraphId predEdgeId,
    string algorithmName,
    ExpansionEdgeStatus edgeStatus,
    float duration,
    uint pathDistance,
    float cost,
    ExpansionAlgoType expansionType,
    byte pathId,
    TravelMode mode);

/// <summary>
/// Pure virtual base defining the interface for PathAlgorithm - the algorithm to create a shortest
/// path. Faithful port of <c>valhalla::thor::PathAlgorithm</c>.
/// </summary>
public abstract class PathAlgorithm
{
    /// <summary>Default bucket count for the double-bucket queue. Faithful port of <c>kBucketCount</c>.</summary>
    public const uint BucketCount = 20000;

    /// <summary>How often (in iterations) to call the interrupt callback. Faithful port of <c>kInterruptIterationsInterval</c>.</summary>
    public const int InterruptIterationsInterval = 5000;

    /// <summary>Periodically-called abort hook (C++ <c>const std::function&lt;void()&gt;* interrupt</c>).</summary>
    protected Action? Interrupt;

    /// <summary>Indicates whether the path has a ferry. C++ <c>has_ferry_</c>.</summary>
    protected bool HasFerry_;

    /// <summary>Indicates whether to allow access into a not-thru region. C++ <c>not_thru_pruning_</c>.</summary>
    protected bool NotThruPruning_ = true;

    /// <summary>For tracking the expansion of the algorithm visually. C++ <c>expansion_callback_</c>.</summary>
    protected ExpansionCallback? ExpansionCallback_;

    /// <summary>Timezone cache to speed up timezone differencing. C++ <c>tz_cache_</c>.</summary>
    protected object? TzCache_;

    /// <summary>Maximum reserved edge-label count. C++ <c>max_reserved_labels_count_</c>.</summary>
    protected uint MaxReservedLabelsCount_;

    /// <summary>If true, clean reserved memory for edge labels. C++ <c>clear_reserved_memory_</c>.</summary>
    protected bool ClearReservedMemory_;

    /// <summary>
    /// Constructor. Faithful port of <c>PathAlgorithm(max_reserved_labels_count, clear_reserved_memory)</c>.
    /// </summary>
    /// <param name="maxReservedLabelsCount">Maximum number of edge labels to reserve.</param>
    /// <param name="clearReservedMemory">Whether to clear reserved label memory on Clear.</param>
    protected PathAlgorithm(uint maxReservedLabelsCount, bool clearReservedMemory)
    {
        Interrupt = null;
        HasFerry_ = false;
        NotThruPruning_ = true;
        ExpansionCallback_ = null;
        MaxReservedLabelsCount_ = maxReservedLabelsCount;
        ClearReservedMemory_ = clearReservedMemory;
    }

    /// <summary>
    /// Form path between an origin and destination location using the supplied costing method.
    /// Faithful port of <c>GetBestPath</c>.
    /// </summary>
    /// <param name="origin">Origin location (loki-correlated).</param>
    /// <param name="dest">Destination location (loki-correlated).</param>
    /// <param name="graphreader">Graph reader for accessing the routing graph.</param>
    /// <param name="modeCosting">Costing methods (indexed by travel mode).</param>
    /// <param name="mode">Travel mode to use.</param>
    /// <param name="options">Request options.</param>
    /// <returns>
    /// Returns the path edges (and elapsed time/modes at the end of each edge). The outer list is the
    /// set of paths (alternates); the inner list is the ordered edges of one path.
    /// </returns>
    public abstract List<List<PathInfo>> GetBestPath(
        PathLocation origin,
        PathLocation dest,
        GraphReader graphreader,
        ModeCosting modeCosting,
        TravelMode mode,
        Options? options = null);

    /// <summary>Returns the name of the algorithm. Faithful port of <c>name()</c>.</summary>
    public abstract string Name();

    /// <summary>Clear the temporary information generated during path construction. Faithful port of <c>Clear()</c>.</summary>
    public abstract void Clear();

    /// <summary>
    /// Set a callback that will throw when the path computation should be aborted. Faithful port of
    /// <c>set_interrupt</c>.
    /// </summary>
    /// <param name="interruptCallback">The function to periodically call to see if we should abort.</param>
    public void SetInterrupt(Action? interruptCallback) => Interrupt = interruptCallback;

    /// <summary>Does the path include a ferry? Faithful port of <c>has_ferry()</c>.</summary>
    public bool HasFerry() => HasFerry_;

    /// <summary>
    /// Set the not_thru_pruning_. Faithful port of <c>set_not_thru_pruning</c>. Only set on the second
    /// pass (allows entry into a not-thru region; see the not_thru_pruning_ gurka test).
    /// </summary>
    /// <param name="pruning">The not_thru_pruning value.</param>
    public void SetNotThruPruning(bool pruning) => NotThruPruning_ = pruning;

    /// <summary>Get the not thru pruning. Faithful port of <c>not_thru_pruning()</c>.</summary>
    public bool NotThruPruning() => NotThruPruning_;

    /// <summary>
    /// Sets the functor which will track the algorithm's expansion. Faithful port of
    /// <c>set_track_expansion</c>.
    /// </summary>
    /// <param name="expansionCallback">The functor to call back when the algorithm makes progress.</param>
    public void SetTrackExpansion(ExpansionCallback? expansionCallback) => ExpansionCallback_ = expansionCallback;

    /// <summary>
    /// Check for path completion along the same edge. Faithful port of the free function
    /// <c>IsTrivial</c>. Edge id in question is along both an origin and destination, with the origin
    /// at the beginning of the edge and the destination at the end.
    /// </summary>
    /// <param name="edgeid">Edge id.</param>
    /// <param name="origin">Origin path location information.</param>
    /// <param name="destination">Destination path location information.</param>
    /// <returns>True if the path is trivial (same edge, origin before destination).</returns>
    public static bool IsTrivial(GraphId edgeid, PathLocation origin, PathLocation destination)
    {
        foreach (PathLocation.PathEdge destinationEdge in destination.Edges)
        {
            if (destinationEdge.Id == edgeid)
            {
                foreach (PathLocation.PathEdge originEdge in origin.Edges)
                {
                    if (originEdge.Id == edgeid && originEdge.PercentAlong <= destinationEdge.PercentAlong)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}

/// <summary>
/// Container for the data iterated over in the Expand* functions. Faithful port of the C++
/// <c>struct EdgeMetadata</c>. Because C# cannot return a raw <c>EdgeStatusInfo*</c>, the edge status
/// is represented by the backing array plus the current index; <see cref="EdgeStatusRef"/> reads it
/// and <see cref="SetEdgeStatus"/> mutates it in place.
/// </summary>
public struct EdgeMetadata
{
    /// <summary>The directed edge currently pointed at.</summary>
    public DirectedEdge Edge;

    /// <summary>The GraphId of the current edge.</summary>
    public GraphId EdgeId;

    // The edge-status backing array and the index of the current edge within it (the C# analogue of
    // the C++ EdgeStatusInfo* that advances alongside edge/edge_id).
    private EdgeStatusInfo[] _edgeStatusArray;
    private int _edgeStatusIndex;

    // The tile's directed edges and the index of the current edge (the C# analogue of advancing the
    // DirectedEdge* pointer). _valid mirrors the C++ "operator bool" (edge != nullptr).
    private GraphTilePtr _tile;
    private uint _edgeCountRemaining;
    private bool _valid;

    /// <summary>
    /// Creates the metadata for the first outbound edge of a node. Faithful port of
    /// <c>EdgeMetadata::make(node, nodeinfo, tile, edge_status)</c>.
    /// </summary>
    /// <param name="node">The node being expanded.</param>
    /// <param name="nodeinfo">The node info for <paramref name="node"/>.</param>
    /// <param name="tile">The tile owning the node.</param>
    /// <param name="edgeStatus">The edge-status map.</param>
    /// <returns>The metadata pointing at the node's first outbound directed edge.</returns>
    public static EdgeMetadata Make(GraphId node, NodeInfo nodeinfo, GraphTilePtr tile, EdgeStatus edgeStatus)
    {
        var edgeId = new GraphId(node.Tileid(), node.Level(), nodeinfo.EdgeIndex);
        (EdgeStatusInfo[] arr, int idx) = edgeStatus.GetPtr(edgeId, tile);
        DirectedEdge directededge = tile.DirectedEdge(edgeId);
        return new EdgeMetadata
        {
            Edge = directededge,
            EdgeId = edgeId,
            _edgeStatusArray = arr,
            _edgeStatusIndex = idx,
            _tile = tile,
            _edgeCountRemaining = nodeinfo.EdgeCount,
            _valid = nodeinfo.EdgeCount > 0,
        };
    }

    /// <summary>
    /// Creates metadata for a single, explicitly supplied directed edge (the C# analogue of the C++
    /// brace-initialized <c>EdgeMetadata{edge, edge_id, edge_status.GetPtr(...)}</c> used on the
    /// no-access opposing-edge path). The result is valid for one edge and is not meant to be
    /// incremented.
    /// </summary>
    /// <param name="edge">The directed edge.</param>
    /// <param name="edgeId">The GraphId of the edge.</param>
    /// <param name="edgeStatusArray">The edge-status backing array (from <c>EdgeStatus.GetPtr</c>).</param>
    /// <param name="edgeStatusIndex">The index of the edge within <paramref name="edgeStatusArray"/>.</param>
    /// <param name="tile">The tile owning the edge.</param>
    /// <returns>The metadata pointing at the supplied edge.</returns>
    public static EdgeMetadata MakeAt(
        DirectedEdge edge,
        GraphId edgeId,
        EdgeStatusInfo[] edgeStatusArray,
        int edgeStatusIndex,
        GraphTilePtr tile)
    {
        return new EdgeMetadata
        {
            Edge = edge,
            EdgeId = edgeId,
            _edgeStatusArray = edgeStatusArray,
            _edgeStatusIndex = edgeStatusIndex,
            _tile = tile,
            _edgeCountRemaining = 1,
            _valid = true,
        };
    }

    /// <summary>
    /// Advances to the next sequential directed edge. Faithful port of <c>operator++</c>. Returns the
    /// advanced metadata (use the returned value; this is a value type).
    /// </summary>
    public EdgeMetadata Increment()
    {
        ++_edgeStatusIndex;
        EdgeId = EdgeId + 1UL;
        if (_edgeCountRemaining > 0)
        {
            --_edgeCountRemaining;
        }

        if (_edgeCountRemaining == 0)
        {
            _valid = false;
        }
        else
        {
            Edge = _tile.DirectedEdge((int)EdgeId.Id());
        }

        return this;
    }

    /// <summary>True while pointing at a valid edge. Faithful port of the C++ <c>operator bool()</c>.</summary>
    public readonly bool IsValid => _valid;

    /// <summary>Reads the current edge status. The C# analogue of dereferencing <c>edge_status</c>.</summary>
    public readonly EdgeStatusInfo EdgeStatusRef => _edgeStatusArray[_edgeStatusIndex];

    /// <summary>Writes the current edge status in place (the C# analogue of <c>*edge_status = ...</c>).</summary>
    public readonly void SetEdgeStatus(EdgeStatusInfo info) => _edgeStatusArray[_edgeStatusIndex] = info;
}
