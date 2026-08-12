using System.Runtime.CompilerServices;

// Faithful C# port of Valhalla mjolnir restrictionbuilder.h + src/mjolnir/restrictionbuilder.cc
// @ 3.8.3 commit a60c7cb (the upstream implementation is unchanged from 3.7.0).
// Sources:
//   F:/github/valhalla/valhalla/mjolnir/restrictionbuilder.h
//   F:/github/valhalla/src/mjolnir/restrictionbuilder.cc
//
// RestrictionBuilder reads the simple+complex turn restrictions parsed from OSM (two sorted
// sequences keyed by the "from" way id: complex_restrictions_from and complex_restrictions_to) and
// writes complex restrictions into the baldr tiles. For each directed edge that is marked as the
// start (or end) of a restriction, it walks the chain of vias (depth-first, following way ids
// through directed edges + node transitions, possibly across tiles/levels) to turn the OSM way ids
// into a sequence of edge GraphIds, then stores a forward complex restriction (in the "to" edge's
// tile) and a reverse complex restriction (in the "from"/walked tile), handling the special
// only_* (and only_probable) restriction types by expanding to the disallowed sibling edges.
//
// Multi-via complex restrictions ARE reproduced (the depth-first GetGraphIds expansion + the
// only-restriction sibling expansion that emits one restriction per disallowed branch).
//
// PORT-NOTE (consistent with the established mjolnir front-end + GraphBuilder port): the C++ runs a
// thread pool over a randomized tile queue with a shared GraphReader + mutex, spilling the from/to
// restrictions to mmapped midgard::sequence temp files. This on-device port runs single-threaded
// over the tile set (deterministic order) and takes the from/to restrictions as in-memory sorted
// lists; the std::random_device shuffle, std::promise/std::thread fan-out, the mutex, and the
// SCOPED_TIMER/build_stats/logging are dropped. Every restriction-walking algorithm (GetGraphIds,
// ExpandFromNode[Inner], GetOpposingEdge, IsEdgeAllowed, CreateComplexRestriction, the forward /
// reverse / only-restriction sibling expansion, the dedup via the per-tile temp multimaps, and
// HandleOnlyRestrictionProperties) is preserved EXACTLY.
//
// EXCLUDED: transit (the transit-connection/egress/platform use checks are kept since they gate
// edge walking, but no transit edges are produced by the auto/truck build).

using System.Collections.Generic;
using System.Runtime.InteropServices;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Class used to add complex turn restrictions to the graph tiles. Faithful port of the C++
/// <c>class RestrictionBuilder</c> + the restrictionbuilder.cc free functions.
/// </summary>
public static class RestrictionBuilder
{
    /// <summary>Maximum number of vias per restriction. Mirrors C++ <c>kMaxViasPerRestriction</c>.</summary>
    public const int MaxViasPerRestriction = ComplexRestriction.MaxViasPerRestriction;

    // (way_id, graph_id) pair accumulated during the depth-first way->edge resolution.
    private readonly struct EdgeId
    {
        public EdgeId(ulong wayId, GraphId graphId)
        {
            WayId = wayId;
            GraphId = graphId;
        }

        public ulong WayId { get; }

        public GraphId GraphId { get; }
    }

    private sealed class PlanContext(
        IRestrictionMutationPlanSink sink,
        PlanTraversalWorkspace workspace)
    {
        internal IRestrictionMutationPlanSink Sink { get; } = sink;

        internal PlanTraversalWorkspace Workspace { get; } = workspace;

        internal ulong NextOrdinal { get; set; }

        internal uint ForwardCount { get; set; }

        internal uint ReverseCount { get; set; }

        internal uint EdgePatchCount { get; set; }

        internal uint ProjectedCrossTileForwardCount { get; set; }

        internal uint ProjectedCrossTilePartOfEdgeCount { get; set; }

        internal int PeakTraversalDepth { get; set; }

        internal int PeakVisitedNodes { get; set; }

        internal int PeakTraversedEdges { get; set; }

        internal ulong TakeOrdinal() => NextOrdinal++;
    }

    private sealed class DiscardingPlanSink : IRestrictionMutationPlanSink
    {
        internal static DiscardingPlanSink Instance { get; } = new();

        public void EmitRestriction(
            RestrictionMutationDirection direction,
            GraphId tileId,
            GraphId from,
            GraphId to,
            ReadOnlySpan<GraphId> vias,
            RestrictionType type,
            uint modes,
            byte probability,
            ulong timeDomain,
            bool crossTile,
            ulong canonicalOrdinal)
        {
        }

        public void EmitEdgePatch(
            GraphId tileId,
            uint directedEdgeIndex,
            uint startRestrictionMask,
            uint endRestrictionMask,
            bool setComplexRestriction,
            bool crossTile,
            ulong canonicalOrdinal)
        {
        }
    }

    private readonly record struct PlanTileCatalog(
        byte Level,
        IReadOnlyList<GraphId> Tiles);


    private sealed class PlanningTileView(GraphTile tile)
        : IRestrictionTileMutation
    {
        public GraphTileHeader Header() => tile.Header();

        public NodeInfo NodeBuilder(int index) => tile.Node(index);

        public DirectedEdge DirectedEdgeBuilder(int index) =>
            tile.DirectedEdge(index);

        public void SetDirectedEdgeBuilder(int index, DirectedEdge edge) =>
            throw new InvalidOperationException(
                "Plan-only restriction traversal cannot mutate a graph tile.");

        public ulong EdgeInfoWayId(DirectedEdge edge) =>
            tile.EdgeInfoWayId(edge);

        public void AddForwardComplexRestriction(
            ComplexRestrictionBuilder restriction) =>
            throw new InvalidOperationException(
                "Plan-only restriction traversal cannot retain builders.");

        public void AddReverseComplexRestriction(
            ComplexRestrictionBuilder restriction) =>
            throw new InvalidOperationException(
                "Plan-only restriction traversal cannot retain builders.");

        public void StoreTileData(
            string tileDirectory,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Plan-only restriction traversal cannot write graph tiles.");
    }

    private enum PlanTraversalPhase : byte
    {
        ScanEdges = 0,
        AfterNextWay = 1,
        AfterSameWay = 2,
        ScanTransitions = 3,
    }

    private struct PlanTraversalFrame
    {
        internal GraphTile Tile;
        internal GraphId PreviousNode;
        internal GraphId CurrentNode;
        internal GraphId PendingEndNode;
        internal int WayIdIndex;
        internal int NextEdgeOffset;
        internal int NextTransitionOffset;
        internal PlanTraversalPhase Phase;
        internal bool AllowTransitions;
    }


    private sealed class PlanTraversalWorkspace
    {
        private readonly PlanTraversalFrame[] frames;
        private readonly GraphId[] visitedNodes;
        private readonly EdgeId[] edgeIds;
        private int frameCount;
        private int visitedCount;
        private int edgeCount;

        internal PlanTraversalWorkspace(
            int traversalDepthCapacity,
            int visitedNodeCapacity,
            int traversedEdgeCapacity)
        {
            if (traversalDepthCapacity <= 0)
            {
                throw new InvalidOperationException(
                    "The plan traversal-depth capacity must be positive.");
            }

            if (visitedNodeCapacity <= 0)
            {
                throw new InvalidOperationException(
                    "The plan visited-node capacity must be positive.");
            }

            if (traversedEdgeCapacity <= 0)
            {
                throw new InvalidOperationException(
                    "The plan traversed-edge capacity must be positive.");
            }

            frames = new PlanTraversalFrame[traversalDepthCapacity];
            visitedNodes = new GraphId[visitedNodeCapacity];
            edgeIds = new EdgeId[traversedEdgeCapacity];
        }

        internal int PeakDepth { get; private set; }

        internal int PeakVisitedNodes { get; private set; }

        internal int PeakTraversedEdges { get; private set; }

        internal int FrameCount => frameCount;

        internal int EdgeCount => edgeCount;

        internal ref PlanTraversalFrame TopFrame =>
            ref frames[frameCount - 1];

        internal EdgeId EdgeAt(int index) => edgeIds[index];

        internal void Reset(GraphId startNode)
        {
            frameCount = 0;
            visitedCount = 0;
            edgeCount = 0;
            AddVisited(startNode);
        }

        internal void PushFrame(PlanTraversalFrame frame)
        {
            if (frameCount == frames.Length)
            {
                throw new InvalidOperationException(
                    "The plan traversal-depth capacity was exceeded.");
            }

            frames[frameCount++] = frame;
            PeakDepth = Math.Max(PeakDepth, frameCount);
        }

        internal PlanTraversalFrame PopFrame()
        {
            PlanTraversalFrame frame = frames[--frameCount];
            frames[frameCount] = default;
            return frame;
        }

        internal void PushEdge(EdgeId edge)
        {
            if (edgeCount == edgeIds.Length)
            {
                throw new InvalidOperationException(
                    "The plan traversed-edge capacity was exceeded.");
            }

            edgeIds[edgeCount++] = edge;
            PeakTraversedEdges = Math.Max(PeakTraversedEdges, edgeCount);
        }

        internal void PopEdge()
        {
            edgeIds[--edgeCount] = default;
        }

        internal bool ContainsVisited(GraphId node)
        {
            for (int index = 0; index < visitedCount; index++)
            {
                if (visitedNodes[index] == node)
                {
                    return true;
                }
            }

            return false;
        }

        internal void AddVisited(GraphId node)
        {
            if (visitedCount == visitedNodes.Length)
            {
                throw new InvalidOperationException(
                    "The plan visited-node capacity was exceeded.");
            }

            visitedNodes[visitedCount++] = node;
            PeakVisitedNodes = Math.Max(PeakVisitedNodes, visitedCount);
        }

        internal void RemoveVisited(GraphId node)
        {
            if (visitedCount == 0 ||
                visitedNodes[visitedCount - 1] != node)
            {
                throw new InvalidOperationException(
                    "The plan traversal visited-node stack was inconsistent.");
            }

            visitedNodes[--visitedCount] = GraphId.Invalid;
        }
    }

    /// <summary>
    /// Per-thread (here, per-run) result accumulated during the build: the forward/reverse counts,
    /// the complex restrictions that need to be written into another tile (for only_* restrictions
    /// where the "to" edge is in a different tile), and the set of edges that are part of a
    /// restriction but live in another tile. Faithful port of the C++ <c>struct Result</c>.
    /// </summary>
    public sealed class Result
    {
        /// <summary>Number of forward complex restrictions added (in-tile).</summary>
        public uint ForwardRestrictionsCount { get; set; }

        /// <summary>Number of reverse complex restrictions added.</summary>
        public uint ReverseRestrictionsCount { get; set; }

        /// <summary>
        /// Number of forward restrictions serialized by the deferred
        /// cross-tile write phase.
        /// </summary>
        public uint CrossTileForwardRestrictionsCount { get; set; }

        /// <summary>
        /// Number of cross-tile edges marked as belonging to an
        /// only-turn restriction.
        /// </summary>
        public uint CrossTilePartOfEdgesMarkedCount { get; set; }

        /// <summary>
        /// Number of projected cross-tile restrictions whose destination
        /// tile was unavailable.
        /// </summary>
        public uint MissingCrossTileDestinationCount { get; set; }

        /// <summary>Complex restrictions whose "to" edge is in a different tile (written afterwards).</summary>
        public List<ComplexRestrictionBuilder> Restrictions { get; } = new();

        /// <summary>Edges that are part of an only_* restriction but live in another tile.</summary>
        public HashSet<GraphId> PartOfRestriction { get; } = new();

        internal int MaxDeferredRestrictions { get; init; } =
            int.MaxValue;

        internal int MaxPartOfRestrictionEdges { get; init; } =
            int.MaxValue;

        internal void AddDeferredRestriction(
            ComplexRestrictionBuilder restriction)
        {
            ArgumentNullException.ThrowIfNull(restriction);
            if (Restrictions.Count >= MaxDeferredRestrictions)
            {
                throw new InvalidOperationException(
                    "The restriction builder exceeded its bounded " +
                    "cross-tile restriction capacity.");
            }

            Restrictions.Add(restriction);
        }

        internal void AddPartOfRestriction(GraphId edgeId)
        {
            if (PartOfRestriction.Contains(edgeId))
            {
                return;
            }

            if (PartOfRestriction.Count >=
                MaxPartOfRestrictionEdges)
            {
                throw new InvalidOperationException(
                    "The restriction builder exceeded its bounded " +
                    "cross-tile part-of edge capacity.");
            }

            PartOfRestriction.Add(edgeId);
        }
    }

    internal sealed record ExecutionOptions(
        int MaxTilesPerLevel,
        int MaxDeferredRestrictions,
        int MaxPartOfRestrictionEdges,
        Action<GraphId>? TileWrittenObserver = null)
    {
        internal static ExecutionOptions Unbounded { get; } =
            new(
                int.MaxValue,
                int.MaxValue,
                int.MaxValue);

        internal Func<byte, IReadOnlyList<GraphId>>? TileCatalogProvider
        {
            get;
            init;
        }

        internal long MaxTileMutationAllocatedBytes
        {
            get;
            init;
        } = long.MaxValue;

        internal Action<long>? TileMutationAllocationObserver
        {
            get;
            init;
        }

        internal int MaxProjectedRestrictionsPerTile
        {
            get;
            init;
        }

        internal Func<GraphTile, IRestrictionTileMutation>? TileMutationFactory
        {
            get;
            init;
        }

        internal long RequiredTileMutationBytes
        {
            get;
            init;
        }

        internal int TraversalDepthCapacity { get; init; } = 256;

        internal int VisitedNodeCapacity { get; init; } = 4096;

        internal int TraversedEdgeCapacity { get; init; } = 4096;
    }

    internal static long GetPlanTraversalWorkspaceReservationBytes(
        ExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidatePlanTraversalCapacity(
            options.TraversalDepthCapacity,
            nameof(options.TraversalDepthCapacity));
        ValidatePlanTraversalCapacity(
            options.VisitedNodeCapacity,
            nameof(options.VisitedNodeCapacity));
        ValidatePlanTraversalCapacity(
            options.TraversedEdgeCapacity,
            nameof(options.TraversedEdgeCapacity));

        const int ArrayHeaderPointerCount = 4;
        const int WorkspaceObjectPointerCount = 8;
        long arrayHeaders = checked(
            3L * ArrayHeaderPointerCount * IntPtr.Size);
        long workspaceObject = checked(
            (long)WorkspaceObjectPointerCount * IntPtr.Size);
        return checked(
            arrayHeaders +
            workspaceObject +
            ((long)options.TraversalDepthCapacity *
                Unsafe.SizeOf<PlanTraversalFrame>()) +
            ((long)options.VisitedNodeCapacity *
                Unsafe.SizeOf<GraphId>()) +
            ((long)options.TraversedEdgeCapacity *
                Unsafe.SizeOf<EdgeId>()));
    }

    private static void ValidatePlanTraversalCapacity(
        int capacity,
        string parameterName)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Plan traversal capacities must be positive.");
        }
    }

    // ------------------------------------------------------------------
    // GetOpposingEdge / IsEdgeAllowed (anonymous-namespace helpers)
    // ------------------------------------------------------------------

    // Faithful port of the anonymous GetOpposingEdge: find the opposing directed edge of `edge`
    // (which starts at `node` and ends at edge.endnode()), matching classification, length, link/use
    // and way id. Returns an invalid GraphId if not found.
    private static GraphId GetOpposingEdge(GraphReader reader, GraphTile tile, GraphId node, DirectedEdge edge)
    {
        GraphId endNode = edge.EndNode;
        GraphTile? endNodeTile = tile;
        if (endNodeTile.Id() != endNode.TileBase())
        {
            endNodeTile = reader.GetGraphTile(endNode);
        }

        NodeInfo nodeinfo = endNodeTile!.Node(endNode);
        ulong wayId = tile.EdgeInfo(edge).WayId;

        // Get the directed edges and return when the end node matches the specified node and length.
        var oppId = new GraphId(endNode.Tileid(), endNode.Level(), nodeinfo.EdgeIndex);
        uint n = nodeinfo.EdgeCount;
        for (uint i = 0; i < n; i++, oppId += 1)
        {
            DirectedEdge oppEdge = endNodeTile.DirectedEdge((int)(nodeinfo.EdgeIndex + i));
            if (oppEdge.Use == Use.TransitConnection || oppEdge.Use == Use.EgressConnection ||
                oppEdge.Use == Use.PlatformConnection)
            {
                continue;
            }

            if (oppEdge.EndNode == node && oppEdge.Classification == edge.Classification &&
                oppEdge.Length == edge.Length &&
                ((oppEdge.Link && edge.Link) || (oppEdge.Use == edge.Use)) &&
                wayId == endNodeTile.EdgeInfo(oppEdge).WayId)
            {
                return oppId;
            }
        }

        return GraphId.Invalid;
    }

    // Faithful port of IsEdgeAllowed.
    private static bool IsEdgeAllowed(DirectedEdge de, uint access, bool forward)
    {
        bool accessible = ((forward ? de.ForwardAccess : de.ReverseAccess) & access) != 0;
        return accessible &&
               !(de.IsTransitLine || de.IsShortcut || de.Use == Use.TransitConnection ||
                 de.Use == Use.EgressConnection || de.Use == Use.PlatformConnection);
    }

    // ------------------------------------------------------------------
    // ExpandFromNode / GetGraphIds (the depth-first way -> edge resolver)
    // ------------------------------------------------------------------

    // Faithful port of ExpandFromNodeInner.
    private static bool ExpandFromNodeInner(
        GraphReader reader,
        uint access,
        bool forward,
        ref GraphId lastNode,
        HashSet<GraphId> visitedNodes,
        List<EdgeId> edgeIds,
        IReadOnlyList<ulong> wayIds,
        int wayIdIndex,
        GraphTile tile,
        GraphId prevNode,
        GraphId currentNode,
        NodeInfo nodeInfo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ulong wayId = wayIds[wayIdIndex];

        for (uint j = 0; j < nodeInfo.EdgeCount; ++j)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var edgeId = new GraphId(tile.Id().Tileid(), tile.Id().Level(), nodeInfo.EdgeIndex + j);
            DirectedEdge de = tile.DirectedEdge(edgeId);

            if (de.EndNode != prevNode && IsEdgeAllowed(de, access, forward))
            {
                ulong candidateWayId = tile.EdgeInfoWayId(de);
                if (candidateWayId == wayId)
                {
                    edgeIds.Add(new EdgeId(wayId, edgeId));

                    // Expand with the next way_id.
                    bool found = ExpandFromNode(
                        reader,
                        access,
                        forward,
                        ref lastNode,
                        visitedNodes,
                        edgeIds,
                        wayIds,
                        wayIdIndex + 1,
                        tile,
                        currentNode,
                        de.EndNode,
                        cancellationToken);
                    if (found)
                    {
                        return true;
                    }

                    if (!visitedNodes.Contains(de.EndNode))
                    {
                        visitedNodes.Add(de.EndNode);

                        // Expand with the same way_id.
                        found = ExpandFromNode(
                            reader,
                            access,
                            forward,
                            ref lastNode,
                            visitedNodes,
                            edgeIds,
                            wayIds,
                            wayIdIndex,
                            tile,
                            currentNode,
                            de.EndNode,
                            cancellationToken);
                        if (found)
                        {
                            return true;
                        }

                        visitedNodes.Remove(de.EndNode);
                    }

                    edgeIds.RemoveAt(edgeIds.Count - 1);
                }
            }
        }

        return false;
    }

    // Faithful port of ExpandFromNode (depth-first-search over directed edges + transition nodes).
    private static bool ExpandFromNode(
        GraphReader reader,
        uint access,
        bool forward,
        ref GraphId lastNode,
        HashSet<GraphId> visitedNodes,
        List<EdgeId> edgeIds,
        IReadOnlyList<ulong> wayIds,
        int wayIdIndex,
        GraphTile prevTile,
        GraphId prevNode,
        GraphId currentNode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (wayIdIndex == wayIds.Count)
        {
            // Assign the last node to use it for the reverse search later.
            lastNode = currentNode;
            return true;
        }

        GraphTile? tile = prevTile;
        if (tile.Id() != currentNode.TileBase())
        {
            tile = reader.GetGraphTile(currentNode);
        }

        NodeInfo nodeInfo = tile!.Node(currentNode);

        // Expand from the current node.
        bool found = ExpandFromNodeInner(
            reader,
            access,
            forward,
            ref lastNode,
            visitedNodes,
            edgeIds,
            wayIds,
            wayIdIndex,
            tile,
            prevNode,
            currentNode,
            nodeInfo,
            cancellationToken);
        if (found)
        {
            return true;
        }

        // Expand from the transition nodes.
        for (uint k = 0; k < nodeInfo.TransitionCount; ++k)
        {
            NodeTransition trans = tile.Transition(nodeInfo.TransitionIndex + k);

            GraphTile? transTile = tile;
            if (transTile.Id() != trans.EndNode().TileBase())
            {
                transTile = reader.GetGraphTile(trans.EndNode());
            }

            cancellationToken.ThrowIfCancellationRequested();
            found = ExpandFromNodeInner(
                reader,
                access,
                forward,
                ref lastNode,
                visitedNodes,
                edgeIds,
                wayIds,
                wayIdIndex,
                transTile!,
                prevNode,
                trans.EndNode(),
                transTile!.Node(trans.EndNode()),
                cancellationToken);
            if (found)
            {
                return true;
            }
        }

        return false;
    }

    // Faithful port of GetGraphIds: depth-first resolve the list of way ids into edge GraphIds,
    // dropping the duplicated way_ids in the prefix (so [1,1,1,2,54] => [1,2,54]).
    private static List<GraphId> GetGraphIds(
        ref GraphId startNode,
        GraphReader reader,
        IReadOnlyList<ulong> wayIds,
        uint access,
        bool forward,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GraphTile? tile = reader.GetGraphTile(startNode);

        var visitedNodes = new HashSet<GraphId> { startNode };
        var edgeIds = new List<EdgeId>();
        ExpandFromNode(
            reader,
            access,
            forward,
            ref startNode,
            visitedNodes,
            edgeIds,
            wayIds,
            0,
            tile!,
            GraphId.Invalid,
            startNode,
            cancellationToken);
        return SelectResolvedGraphIds(edgeIds);
    }

    private static List<GraphId> GetGraphIdsForPlan(
        ref GraphId startNode,
        GraphReader reader,
        IReadOnlyList<ulong> wayIds,
        uint access,
        bool forward,
        PlanTraversalWorkspace workspace,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GraphTile tile = reader.GetGraphTile(startNode) ??
            throw new InvalidDataException(
                $"Restriction traversal tile for node {startNode} was unavailable.");

        workspace.Reset(startNode);
        workspace.PushFrame(CreatePlanFrame(
            tile,
            GraphId.Invalid,
            startNode,
            wayIdIndex: 0,
            allowTransitions: true));

        while (workspace.FrameCount != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ref PlanTraversalFrame frame = ref workspace.TopFrame;
            if (frame.WayIdIndex == wayIds.Count)
            {
                startNode = frame.CurrentNode;
                return SelectResolvedGraphIds(workspace);
            }

            NodeInfo nodeInfo = frame.Tile.Node(frame.CurrentNode);
            switch (frame.Phase)
            {
                case PlanTraversalPhase.ScanEdges:
                    bool descended = false;
                    while (frame.NextEdgeOffset < nodeInfo.EdgeCount)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        uint edgeOffset = checked((uint)frame.NextEdgeOffset++);
                        var edgeId = new GraphId(
                            frame.Tile.Id().Tileid(),
                            frame.Tile.Id().Level(),
                            nodeInfo.EdgeIndex + edgeOffset);
                        DirectedEdge edge = frame.Tile.DirectedEdge(edgeId);
                        if (edge.EndNode == frame.PreviousNode ||
                            !IsEdgeAllowed(edge, access, forward) ||
                            frame.Tile.EdgeInfoWayId(edge) != wayIds[frame.WayIdIndex])
                        {
                            continue;
                        }

                        workspace.PushEdge(new EdgeId(
                            wayIds[frame.WayIdIndex],
                            edgeId));
                        frame.PendingEndNode = edge.EndNode;
                        frame.Phase = PlanTraversalPhase.AfterNextWay;
                        PushPlanTraversalFrame(
                            reader,
                            workspace,
                            frame.Tile,
                            frame.CurrentNode,
                            edge.EndNode,
                            frame.WayIdIndex + 1,
                            allowTransitions: true,
                            cancellationToken);
                        descended = true;
                        break;
                    }

                    if (!descended)
                    {
                        frame.Phase = PlanTraversalPhase.ScanTransitions;
                    }

                    break;

                case PlanTraversalPhase.AfterNextWay:
                    if (!workspace.ContainsVisited(frame.PendingEndNode))
                    {
                        workspace.AddVisited(frame.PendingEndNode);
                        frame.Phase = PlanTraversalPhase.AfterSameWay;
                        PushPlanTraversalFrame(
                            reader,
                            workspace,
                            frame.Tile,
                            frame.CurrentNode,
                            frame.PendingEndNode,
                            frame.WayIdIndex,
                            allowTransitions: true,
                            cancellationToken);
                    }
                    else
                    {
                        workspace.PopEdge();
                        frame.PendingEndNode = GraphId.Invalid;
                        frame.Phase = PlanTraversalPhase.ScanEdges;
                    }

                    break;

                case PlanTraversalPhase.AfterSameWay:
                    workspace.RemoveVisited(frame.PendingEndNode);
                    workspace.PopEdge();
                    frame.PendingEndNode = GraphId.Invalid;
                    frame.Phase = PlanTraversalPhase.ScanEdges;
                    break;

                case PlanTraversalPhase.ScanTransitions:
                    if (frame.AllowTransitions &&
                        frame.NextTransitionOffset < nodeInfo.TransitionCount)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        NodeTransition transition = frame.Tile.Transition(
                            nodeInfo.TransitionIndex +
                            checked((uint)frame.NextTransitionOffset++));
                        GraphId transitionNode = transition.EndNode();
                        GraphTile transitionTile =
                            transitionNode.TileBase() == frame.Tile.Id()
                                ? frame.Tile
                                : reader.GetGraphTile(transitionNode) ??
                                  throw new InvalidDataException(
                                      $"Restriction transition tile for node " +
                                      $"{transitionNode} was unavailable.");
                        workspace.PushFrame(CreatePlanFrame(
                            transitionTile,
                            frame.PreviousNode,
                            transitionNode,
                            frame.WayIdIndex,
                            allowTransitions: false));
                    }
                    else
                    {
                        workspace.PopFrame();
                    }

                    break;

                default:
                    throw new InvalidOperationException(
                        "The plan restriction traversal entered an unknown phase.");
            }
        }

        return new List<GraphId>();
    }

    private static PlanTraversalFrame CreatePlanFrame(
        GraphTile tile,
        GraphId previousNode,
        GraphId currentNode,
        int wayIdIndex,
        bool allowTransitions) =>
        new()
        {
            Tile = tile,
            PreviousNode = previousNode,
            CurrentNode = currentNode,
            PendingEndNode = GraphId.Invalid,
            WayIdIndex = wayIdIndex,
            Phase = PlanTraversalPhase.ScanEdges,
            AllowTransitions = allowTransitions,
        };

    private static void PushPlanTraversalFrame(
        GraphReader reader,
        PlanTraversalWorkspace workspace,
        GraphTile previousTile,
        GraphId previousNode,
        GraphId currentNode,
        int wayIdIndex,
        bool allowTransitions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GraphTile tile = currentNode.TileBase() == previousTile.Id()
            ? previousTile
            : reader.GetGraphTile(currentNode) ??
              throw new InvalidDataException(
                  $"Restriction traversal tile for node {currentNode} was unavailable.");
        workspace.PushFrame(CreatePlanFrame(
            tile,
            previousNode,
            currentNode,
            wayIdIndex,
            allowTransitions));
    }

    private static List<GraphId> SelectResolvedGraphIds(
        IReadOnlyList<EdgeId> edgeIds)
    {
        if (edgeIds.Count == 0)
        {
            return new List<GraphId>();
        }

        int first = FindResolvedGraphIdStart(
            edgeIds.Count,
            index => edgeIds[index].WayId);
        var result = new List<GraphId>(edgeIds.Count - first);
        for (; first < edgeIds.Count; first++)
        {
            result.Add(edgeIds[first].GraphId);
        }

        return result;
    }

    private static List<GraphId> SelectResolvedGraphIds(
        PlanTraversalWorkspace workspace)
    {
        if (workspace.EdgeCount == 0)
        {
            return new List<GraphId>();
        }

        int first = FindResolvedGraphIdStart(
            workspace.EdgeCount,
            index => workspace.EdgeAt(index).WayId);
        var result = new List<GraphId>(workspace.EdgeCount - first);
        for (; first < workspace.EdgeCount; first++)
        {
            result.Add(workspace.EdgeAt(first).GraphId);
        }

        return result;
    }

    private static int FindResolvedGraphIdStart(
        int count,
        Func<int, ulong> wayIdAt)
    {
        int first = count;
        for (int index = 1; index < count; index++)
        {
            if (wayIdAt(index) != wayIdAt(0))
            {
                first = index;
                break;
            }
        }

        return first - 1;
    }


    private static ComplexRestrictionBuilder CreateComplexRestriction(
        OSMRestriction restriction,
        GraphId from,
        GraphId to,
        List<GraphId> vias)
    {
        var complexRestriction = new ComplexRestrictionBuilder();
        complexRestriction.SetFromId(from);
        complexRestriction.SetViaList(vias);
        complexRestriction.SetToId(to);
        complexRestriction.SetType(restriction.TypeValue());
        complexRestriction.SetModes((ushort)restriction.Modes());
        complexRestriction.SetProbability(restriction.Probability());

        var td = new TimeDomain(restriction.TimeDomain());
        if (td.TdValue != 0)
        {
            complexRestriction.SetBeginDayDow(td.BeginDayDow);
            complexRestriction.SetBeginHrs(td.BeginHrs);
            complexRestriction.SetBeginMins(td.BeginMins);
            complexRestriction.SetBeginMonth(td.BeginMonth);
            complexRestriction.SetBeginWeek(td.BeginWeek);
            complexRestriction.SetDow(td.Dow);
            complexRestriction.SetDt(true);
            complexRestriction.SetDtType(td.Type != 0);
            complexRestriction.SetEndDayDow(td.EndDayDow);
            complexRestriction.SetEndHrs(td.EndHrs);
            complexRestriction.SetEndMins(td.EndMins);
            complexRestriction.SetEndMonth(td.EndMonth);
            complexRestriction.SetEndWeek(td.EndWeek);
        }

        return complexRestriction;
    }

    private static bool IsOnlyRestriction(RestrictionType type)
        => (type >= RestrictionType.OnlyRightTurn && type <= RestrictionType.OnlyStraightOn) ||
           type == RestrictionType.OnlyProbable;

    // ------------------------------------------------------------------
    // Sorted-sequence lower-bound helper (replaces sequence<OSMRestriction>::find on a "from" key).
    // ------------------------------------------------------------------

    // Returns the index of the first restriction whose from() is NOT less than `fromWayId`
    // (std::lower_bound by from()). The list MUST be sorted by from() (then to/vias/... per
    // OSMRestriction::operator<).
    private static int LowerBoundByFrom(IReadOnlyList<OSMRestriction> restrictions, ulong fromWayId)
    {
        int lo = 0;
        int hi = restrictions.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (restrictions[mid].From() < fromWayId)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    // ------------------------------------------------------------------
    // build (per-tile worker)
    // ------------------------------------------------------------------

    // Faithful port of the anonymous build() function: process every directed edge in the tile,
    // walk restrictions, and add forward/reverse complex restrictions to the tile builder. Returns
    // the per-run statistics (only_* cross-tile restrictions + part-of-restriction edges).
    private static void Build(
        IReadOnlyList<OSMRestriction> complexRestrictionsFrom,
        IReadOnlyList<OSMRestriction> complexRestrictionsTo,
        GraphReader reader,
        IReadOnlyCollection<GraphId> tileQueue,
        Result stats,
        ExecutionOptions options,
        CancellationToken cancellationToken,
        PlanContext? plan = null)
    {
        foreach (GraphId tileId in tileQueue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Get a readable tile; skip empty tiles.
            GraphTile? tile = reader.GetGraphTile(tileId);
            if (tile is null)
            {
                continue;
            }

            long mutationAllocationStart = 0;
            IRestrictionTileMutation tilebuilder;
            if (plan is null)
            {
                EnsureTileMutationFitsBudget(
                    options,
                    tile,
                    options.MaxProjectedRestrictionsPerTile);
                mutationAllocationStart =
                    GC.GetAllocatedBytesForCurrentThread();
                tilebuilder = CreateTileMutation(options, tile);
            }
            else
            {
                tilebuilder = new PlanningTileView(tile);
            }

            Dictionary<GraphId, List<ComplexRestrictionBuilder>>? forwardTmpCr =
                plan is null
                    ? new Dictionary<GraphId, List<ComplexRestrictionBuilder>>()
                    : null;
            Dictionary<GraphId, List<ComplexRestrictionBuilder>>? reverseTmpCr =
                plan is null
                    ? new Dictionary<GraphId, List<ComplexRestrictionBuilder>>()
                    : null;

            uint forwardCount = 0;
            uint reverseCount = 0;

            for (uint i = 0; i < tilebuilder.Header().Nodecount(); i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                NodeInfo nodeinfo = tilebuilder.NodeBuilder((int)i);

                for (uint j = 0; j < nodeinfo.EdgeCount; j++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int directedEdgeIndex = (int)(nodeinfo.EdgeIndex + j);
                    DirectedEdge directededge = tilebuilder.DirectedEdgeBuilder(directedEdgeIndex);

                    if (directededge.IsTransitLine || directededge.IsShortcut ||
                        directededge.Use == Use.TransitConnection ||
                        directededge.Use == Use.EgressConnection ||
                        directededge.Use == Use.PlatformConnection)
                    {
                        continue;
                    }

                    ulong wayId = tilebuilder.EdgeInfoWayId(directededge);

                    // Starting with the "from" wayid. If this edge's endnode has the via, save it as
                    // the "from" and walk the vias to the "to" wayid (may transition hierarchy levels).
                    if (directededge.StartRestriction != 0)
                    {
                        ProcessStartRestriction(
                            reader,
                            complexRestrictionsFrom,
                            tileId,
                            tilebuilder,
                            stats,
                            reverseTmpCr,
                            ref reverseCount,
                            directededge,
                            wayId,
                            cancellationToken,
                            plan);
                    }

                    if (directededge.EndRestriction != 0)
                    {
                        ProcessEndRestriction(
                            reader,
                            complexRestrictionsFrom,
                            complexRestrictionsTo,
                            tileId,
                            tilebuilder,
                            stats,
                            forwardTmpCr,
                            ref forwardCount,
                            directededge,
                            wayId,
                            cancellationToken,
                            plan);
                    }
                }
            }

            if (plan is null)
            {
                stats.ForwardRestrictionsCount += forwardCount;
                stats.ReverseRestrictionsCount += reverseCount;
                tilebuilder.StoreTileData(reader.TileDir(), cancellationToken);
                RecordTileMutationAllocation(
                    options,
                    mutationAllocationStart);
                reader.Clear();
                options.TileWrittenObserver?.Invoke(tileId);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static IRestrictionTileMutation CreateTileMutation(
        ExecutionOptions options,
        GraphTile tile)
    {
        Func<GraphTile, IRestrictionTileMutation>? factory =
            options.TileMutationFactory;
        return factory is null
            ? new GraphTileBuilderRestrictionTileMutation(tile)
            : factory(tile);
    }

    private static void EnsureTileMutationFitsBudget(
        ExecutionOptions options,
        GraphTile tile,
        int projectedRestrictionCount)
    {
        if (options.MaxTileMutationAllocatedBytes == long.MaxValue)
        {
            return;
        }

        long requiredBytes = options.RequiredTileMutationBytes;
        if (requiredBytes <= 0)
        {
            long tileBytes = tile.Header().EndOffset();
            long projectedRestrictionBytes = checked(
                (long)Math.Max(0, projectedRestrictionCount) * 1024);
            requiredBytes = checked(
                (tileBytes * 8) +
                projectedRestrictionBytes +
                (64L * 1024L));
        }
        if (requiredBytes > options.MaxTileMutationAllocatedBytes)
        {
            throw new InvalidOperationException(
                $"Restriction tile mutation requires a conservative " +
                $"working-set reservation of {requiredBytes} bytes, " +
                $"exceeding its bounded allowance of " +
                $"{options.MaxTileMutationAllocatedBytes} bytes.");
        }
    }

    private static void RecordTileMutationAllocation(
        ExecutionOptions options,
        long allocationStart)
    {
        long allocatedBytes = checked(
            GC.GetAllocatedBytesForCurrentThread() -
            allocationStart);
        options.TileMutationAllocationObserver?.Invoke(allocatedBytes);
        if (allocatedBytes > options.MaxTileMutationAllocatedBytes)
        {
            throw new InvalidOperationException(
                $"Restriction tile mutation allocated {allocatedBytes} " +
                $"bytes, exceeding its bounded allowance of " +
                $"{options.MaxTileMutationAllocatedBytes} bytes.");
        }
    }

    // The "from" (forward search / reverse store) branch of build(). Faithful port of the
    // directededge.start_restriction() block.
    private static void ProcessStartRestriction(
        GraphReader reader,
        IReadOnlyList<OSMRestriction> complexRestrictionsFrom,
        GraphId tileId,
        IRestrictionTileMutation tilebuilder,
        Result stats,
        Dictionary<GraphId, List<ComplexRestrictionBuilder>>? reverseTmpCr,
        ref uint reverseCount,
        DirectedEdge directededge,
        ulong fromWayId,
        CancellationToken cancellationToken,
        PlanContext? plan)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int resIt = LowerBoundByFrom(complexRestrictionsFrom, fromWayId);
        cancellationToken.ThrowIfCancellationRequested();
        while (resIt < complexRestrictionsFrom.Count && complexRestrictionsFrom[resIt].From() == fromWayId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OSMRestriction restriction = complexRestrictionsFrom[resIt];
            cancellationToken.ThrowIfCancellationRequested();
            GraphId currentNode = directededge.EndNode;

            var resWayIds = new List<ulong> { restriction.From() };

            List<ulong> vias = restriction.Vias();
            foreach (ulong v in vias)
            {
                resWayIds.Add(v);
            }

            // if via = restriction.to then don't add to the res_way_ids vector. This happens when we
            // have a restriction:<type> with a via as a node in the OSM data.
            if (vias.Count == 1 && vias[0] != restriction.To())
            {
                resWayIds.Add(restriction.To());
            }
            else if (vias.Count > 1)
            {
                resWayIds.Add(restriction.To());
            }

            // Walk in the forward direction.
            List<GraphId> tmpIdsFwd = plan is null
                ? GetGraphIds(
                    ref currentNode,
                    reader,
                    resWayIds,
                    restriction.Modes(),
                    true,
                    cancellationToken)
                : GetGraphIdsForPlan(
                    ref currentNode,
                    reader,
                    resWayIds,
                    restriction.Modes(),
                    true,
                    plan.Workspace,
                    cancellationToken);

            // Now walk in the reverse direction as this is really what needs to be stored in this tile.
            if (tmpIdsFwd.Count != 0)
            {
                resWayIds.Reverse();
                List<GraphId> tmpIds = plan is null
                    ? GetGraphIds(
                        ref currentNode,
                        reader,
                        resWayIds,
                        restriction.Modes(),
                        false,
                        cancellationToken)
                    : GetGraphIdsForPlan(
                        ref currentNode,
                        reader,
                        resWayIds,
                        restriction.Modes(),
                        false,
                        plan.Workspace,
                        cancellationToken);

                if (tmpIds.Count > 1 && tmpIds[^1].TileBase() == tileId)
                {
                    if (IsOnlyRestriction(restriction.TypeValue()))
                    {
                        ExpandOnlyReverseRestrictions(
                            reader,
                            tileId,
                            tilebuilder,
                            stats,
                            reverseTmpCr,
                            ref reverseCount,
                            restriction,
                            tmpIds,
                            cancellationToken,
                            plan);
                    }
                    else
                    {
                        AddReverseRestriction(tilebuilder, tileId, stats, reverseTmpCr, ref reverseCount,
                            restriction, tmpIds, plan);
                    }
                }
            }

            ++resIt;
        }
    }

    // The only_* sibling expansion for the reverse-store branch. Faithful port of the inner while
    // loop in build() that walks forward from the front edge's siblings.
    private static void ExpandOnlyReverseRestrictions(
        GraphReader reader,
        GraphId tileId,
        IRestrictionTileMutation tilebuilder,
        Result stats,
        Dictionary<GraphId, List<ComplexRestrictionBuilder>>? reverseTmpCr,
        ref uint reverseCount,
        OSMRestriction restriction,
        List<GraphId> tmpIds,
        CancellationToken cancellationToken,
        PlanContext? plan)
    {
        while (tmpIds.Count > 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphId lastEdgeId = tmpIds[0];
            GraphTile? lastTile = reader.GetGraphTile(tileId);
            if (lastTile!.Id() != lastEdgeId.TileBase())
            {
                lastTile = reader.GetGraphTile(lastEdgeId);
            }

            DirectedEdge lastDe = lastTile!.DirectedEdge(lastEdgeId);
            GraphId endNode = lastDe.EndNode;
            GraphTile? endNodeTile = lastTile;
            if (endNodeTile.Id() != endNode.TileBase())
            {
                endNodeTile = reader.GetGraphTile(endNode);
            }

            NodeInfo endNodeInfo = endNodeTile!.Node(endNode);
            for (uint k = 0; k < endNodeInfo.EdgeCount; ++k)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nextEdgeId = new GraphId(endNodeTile.Id().Tileid(), endNodeTile.Id().Level(),
                    endNodeInfo.EdgeIndex + k);
                DirectedEdge de = endNodeTile.DirectedEdge(nextEdgeId);
                GraphId oppId = GetOpposingEdge(reader, endNodeTile, endNode, de);
                if (oppId != lastEdgeId && IsEdgeAllowed(de, restriction.Modes(), true))
                {
                    tmpIds[0] = oppId;
                    AddReverseRestriction(tilebuilder, tileId, stats, reverseTmpCr, ref reverseCount,
                        restriction, tmpIds, plan);
                }
            }

            foreach (NodeTransition trans in endNodeTile.GetNodeTransitions(endNode))
            {
                cancellationToken.ThrowIfCancellationRequested();
                GraphId toNode = trans.EndNode();
                GraphTile? toTile = reader.GetGraphTile(toNode);
                NodeInfo toNodeInfo = toTile!.Node(toNode);
                var nextEdgeId = new GraphId(toTile.Id().Tileid(), toTile.Id().Level(), toNodeInfo.EdgeIndex);
                for (uint k = 0; k < toNodeInfo.EdgeCount; ++k, nextEdgeId += 1)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DirectedEdge de = toTile.DirectedEdge(nextEdgeId);
                    GraphId oppId = GetOpposingEdge(reader, toTile, toNode, de);
                    if (oppId != lastEdgeId && IsEdgeAllowed(de, restriction.Modes(), true))
                    {
                        tmpIds[0] = oppId;
                        AddReverseRestriction(tilebuilder, tileId, stats, reverseTmpCr, ref reverseCount,
                            restriction, tmpIds, plan);
                    }
                }
            }

            tmpIds.RemoveAt(0);
        }
    }

    // Faithful port of the AddReverseRestriction lambda.
    private static void AddReverseRestriction(
        IRestrictionTileMutation tilebuilder,
        GraphId tileId,
        Result stats,
        Dictionary<GraphId, List<ComplexRestrictionBuilder>>? reverseTmpCr,
        ref uint reverseCount,
        OSMRestriction restriction,
        List<GraphId> tmpIds,
        PlanContext? plan)
    {
        int viaCount = tmpIds.Count - 2;
        if (viaCount > MaxViasPerRestriction)
        {
            return;
        }

        GraphId from = tmpIds[^1];
        GraphId to = tmpIds[0];

        if (plan is not null)
        {
            Span<GraphId> vias = stackalloc GraphId[MaxViasPerRestriction];
            for (int v = 0; v < viaCount; v++)
            {
                vias[v] = tmpIds[tmpIds.Count - 2 - v];
            }

            if (IsOnlyRestriction(restriction.TypeValue()))
            {
                if (to.TileBase() != tileId)
                {
                    plan.ProjectedCrossTilePartOfEdgeCount = checked(
                        plan.ProjectedCrossTilePartOfEdgeCount + 1);
                }

                EmitPlanEdgePatch(
                    plan,
                    to.TileBase(),
                    checked((uint)to.Id()),
                    startRestrictionMask: 0,
                    endRestrictionMask: 0,
                    setComplexRestriction: true,
                    crossTile: to.TileBase() != tileId);
            }

            plan.Sink.EmitRestriction(
                RestrictionMutationDirection.Reverse,
                tileId.TileBase(),
                from,
                to,
                vias[..viaCount],
                restriction.TypeValue(),
                restriction.Modes(),
                restriction.Probability(),
                restriction.TimeDomain(),
                crossTile: false,
                plan.TakeOrdinal());
            plan.ReverseCount = checked(plan.ReverseCount + 1);
            return;
        }

        var legacyVias = new List<GraphId>(viaCount);
        for (int v = 1; v < tmpIds.Count - 1; v++)
        {
            legacyVias.Add(tmpIds[v]);
        }

        legacyVias.Reverse();

        if (IsOnlyRestriction(restriction.TypeValue()))
        {
            if (to.TileBase() == tileId)
            {
                DirectedEdge edge = tilebuilder.DirectedEdgeBuilder((int)to.Id());
                edge.SetComplexRestriction(true);
                tilebuilder.SetDirectedEdgeBuilder((int)to.Id(), edge);
            }
            else
            {
                stats.AddPartOfRestriction(to);
            }
        }

        ComplexRestrictionBuilder complexRestriction =
            CreateComplexRestriction(restriction, from, to, legacyVias);

        bool found = false;
        if (reverseTmpCr!.TryGetValue(
                to,
                out List<ComplexRestrictionBuilder>? existing))
        {
            foreach (ComplexRestrictionBuilder candidate in existing)
            {
                if (complexRestriction.Equals(candidate))
                {
                    found = true;
                    break;
                }
            }
        }

        if (!found)
        {
            if (existing is null)
            {
                existing = new List<ComplexRestrictionBuilder>();
                reverseTmpCr[to] = existing;
            }

            existing.Add(complexRestriction);
            tilebuilder.AddReverseComplexRestriction(complexRestriction);
            reverseCount++;
        }
    }

    private static void EmitPlanEdgePatch(
        PlanContext plan,
        GraphId tileId,
        uint directedEdgeIndex,
        uint startRestrictionMask,
        uint endRestrictionMask,
        bool setComplexRestriction,
        bool crossTile = false)
    {
        plan.Sink.EmitEdgePatch(
            tileId.TileBase(),
            directedEdgeIndex,
            startRestrictionMask,
            endRestrictionMask,
            setComplexRestriction,
            crossTile,
            plan.TakeOrdinal());
        plan.EdgePatchCount = checked(plan.EdgePatchCount + 1);
    }

    // The "to" (reverse search / forward store) branch of build(). Faithful port of the
    // directededge.end_restriction() block.
    private static void ProcessEndRestriction(
        GraphReader reader,
        IReadOnlyList<OSMRestriction> complexRestrictionsFrom,
        IReadOnlyList<OSMRestriction> complexRestrictionsTo,
        GraphId tileId,
        IRestrictionTileMutation tilebuilder,
        Result stats,
        Dictionary<GraphId, List<ComplexRestrictionBuilder>>? forwardTmpCr,
        ref uint forwardCount,
        DirectedEdge directededge,
        ulong fromWayId,
        CancellationToken cancellationToken,
        PlanContext? plan)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int resToIt = LowerBoundByFrom(complexRestrictionsTo, fromWayId);
        cancellationToken.ThrowIfCancellationRequested();
        while (resToIt < complexRestrictionsTo.Count && complexRestrictionsTo[resToIt].From() == fromWayId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OSMRestriction restrictionTo = complexRestrictionsTo[resToIt];
            cancellationToken.ThrowIfCancellationRequested();

            int resIt = LowerBoundByFrom(complexRestrictionsFrom, restrictionTo.To());
            cancellationToken.ThrowIfCancellationRequested();
            while (resIt < complexRestrictionsFrom.Count &&
                   complexRestrictionsFrom[resIt].From() == restrictionTo.To())
            {
                cancellationToken.ThrowIfCancellationRequested();
                OSMRestriction restriction = complexRestrictionsFrom[resIt];
                cancellationToken.ThrowIfCancellationRequested();
                GraphId currentNode = directededge.EndNode;

                var resWayIds = new List<ulong> { restriction.To() };

                List<ulong> vias = restriction.Vias();
                var tempVias = new List<ulong>(vias);
                tempVias.Reverse();

                // if via = restriction.to then don't add (restriction:<type> with a via as a node).
                if (vias.Count > 1 || (vias.Count == 1 && vias[0] != restriction.To()))
                {
                    foreach (ulong v in tempVias)
                    {
                        resWayIds.Add(v);
                    }
                }

                resWayIds.Add(restriction.From());

                // Walk in the forward direction (reverse in relation to the restriction).
                List<GraphId> tmpIdsRev = plan is null
                    ? GetGraphIds(
                        ref currentNode,
                        reader,
                        resWayIds,
                        restriction.Modes(),
                        false,
                        cancellationToken)
                    : GetGraphIdsForPlan(
                        ref currentNode,
                        reader,
                        resWayIds,
                        restriction.Modes(),
                        false,
                        plan.Workspace,
                        cancellationToken);

                // Now walk in the reverse direction (forward in relation to the restriction) as this
                // is really what needs to be stored in this tile.
                if (tmpIdsRev.Count != 0)
                {
                    resWayIds.Reverse();
                    List<GraphId> tmpIds = plan is null
                        ? GetGraphIds(
                            ref currentNode,
                            reader,
                            resWayIds,
                            restriction.Modes(),
                            true,
                            cancellationToken)
                        : GetGraphIdsForPlan(
                            ref currentNode,
                            reader,
                            resWayIds,
                            restriction.Modes(),
                            true,
                            plan.Workspace,
                            cancellationToken);

                    if (tmpIds.Count > 1 && tmpIds[^1].TileBase() == tileId)
                    {
                        if (!IsOnlyRestriction(restriction.TypeValue()))
                        {
                            AddForwardRestriction(tilebuilder, tileId, stats, forwardTmpCr,
                                ref forwardCount, restriction, tmpIds, plan);
                        }
                        else
                        {
                            ExpandOnlyForwardRestrictions(
                                reader,
                                tileId,
                                tilebuilder,
                                stats,
                                forwardTmpCr,
                                ref forwardCount,
                                restriction,
                                tmpIds,
                                cancellationToken,
                                plan);
                        }
                    }
                }

                ++resIt;
            }

            resToIt++;
        }
    }

    // The only_* sibling expansion for the forward-store branch. Faithful port of the inner while
    // loop that walks from the pre-last edge's siblings.
    private static void ExpandOnlyForwardRestrictions(
        GraphReader reader,
        GraphId tileId,
        IRestrictionTileMutation tilebuilder,
        Result stats,
        Dictionary<GraphId, List<ComplexRestrictionBuilder>>? forwardTmpCr,
        ref uint forwardCount,
        OSMRestriction restriction,
        List<GraphId> tmpIds,
        CancellationToken cancellationToken,
        PlanContext? plan)
    {
        while (tmpIds.Count > 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphId lastEdgeId = tmpIds[^1];
            GraphId preLastEdgeId = tmpIds[^2];

            GraphTile? preLastTile = reader.GetGraphTile(tileId);
            if (preLastEdgeId.TileBase() != preLastTile!.Id())
            {
                preLastTile = reader.GetGraphTile(preLastEdgeId);
            }

            DirectedEdge preLastEdge = preLastTile!.DirectedEdge(preLastEdgeId);
            GraphId endNode = preLastEdge.EndNode;
            GraphTile? nextTile = preLastTile;
            if (endNode.TileBase() != nextTile.Id())
            {
                nextTile = reader.GetGraphTile(endNode);
            }

            NodeInfo nodeInfo = nextTile!.Node(endNode);
            var edgeId = new GraphId(nextTile.Id().Tileid(), nextTile.Id().Level(), nodeInfo.EdgeIndex);
            for (uint k = 0; k < nodeInfo.EdgeCount; ++k, edgeId += 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DirectedEdge de = nextTile.DirectedEdge(edgeId);
                if (edgeId != lastEdgeId && IsEdgeAllowed(de, restriction.Modes(), true))
                {
                    tmpIds[^1] = edgeId;
                    AddForwardRestriction(tilebuilder, tileId, stats, forwardTmpCr, ref forwardCount,
                        restriction, tmpIds, plan);
                }
            }

            foreach (NodeTransition trans in nextTile.GetNodeTransitions(nodeInfo))
            {
                cancellationToken.ThrowIfCancellationRequested();
                GraphId toNode = trans.EndNode();
                GraphTile? toTile = reader.GetGraphTile(toNode);
                NodeInfo toNodeInfo = toTile!.Node(toNode);
                var toEdgeId = new GraphId(toTile.Id().Tileid(), toTile.Id().Level(), toNodeInfo.EdgeIndex);
                for (uint k = 0; k < toNodeInfo.EdgeCount; ++k, toEdgeId += 1)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DirectedEdge de = toTile.DirectedEdge(toEdgeId);
                    if (toEdgeId != lastEdgeId && IsEdgeAllowed(de, restriction.Modes(), true))
                    {
                        tmpIds[^1] = toEdgeId;
                        AddForwardRestriction(tilebuilder, tileId, stats, forwardTmpCr, ref forwardCount,
                            restriction, tmpIds, plan);
                    }
                }
            }

            tmpIds.RemoveAt(tmpIds.Count - 1);
        }
    }

    // Faithful port of the addForwardRestriction lambda.
    private static void AddForwardRestriction(
        IRestrictionTileMutation tilebuilder,
        GraphId tileId,
        Result stats,
        Dictionary<GraphId, List<ComplexRestrictionBuilder>>? forwardTmpCr,
        ref uint forwardCount,
        OSMRestriction restriction,
        List<GraphId> tmpIds,
        PlanContext? plan)
    {
        int viaCount = tmpIds.Count - 2;
        if (viaCount > MaxViasPerRestriction)
        {
            return;
        }

        GraphId from = tmpIds[0];
        GraphId to = tmpIds[^1];

        if (plan is not null)
        {
            Span<GraphId> vias = stackalloc GraphId[MaxViasPerRestriction];
            for (int v = 0; v < viaCount; v++)
            {
                vias[v] = tmpIds[tmpIds.Count - 2 - v];
            }

            if (to.TileBase() != tileId)
            {
                plan.ProjectedCrossTileForwardCount = checked(
                    plan.ProjectedCrossTileForwardCount + 1);
            }

            plan.Sink.EmitRestriction(
                RestrictionMutationDirection.Forward,
                to.TileBase(),
                from,
                to,
                vias[..viaCount],
                restriction.TypeValue(),
                restriction.Modes(),
                restriction.Probability(),
                restriction.TimeDomain(),
                crossTile: to.TileBase() != tileId,
                plan.TakeOrdinal());
            plan.ForwardCount = checked(plan.ForwardCount + 1);
            EmitPlanEdgePatch(
                plan,
                to.TileBase(),
                checked((uint)to.Id()),
                startRestrictionMask: 0,
                endRestrictionMask: restriction.Modes(),
                setComplexRestriction: false,
                crossTile: to.TileBase() != tileId);
            return;
        }

        var legacyVias = new List<GraphId>(viaCount);
        for (int v = 1; v < tmpIds.Count - 1; v++)
        {
            legacyVias.Add(tmpIds[v]);
        }

        legacyVias.Reverse();
        ComplexRestrictionBuilder complexRestriction =
            CreateComplexRestriction(restriction, from, to, legacyVias);

        bool found = false;
        if (forwardTmpCr!.TryGetValue(
                from,
                out List<ComplexRestrictionBuilder>? existing))
        {
            foreach (ComplexRestrictionBuilder candidate in existing)
            {
                if (complexRestriction.Equals(candidate))
                {
                    found = true;
                    break;
                }
            }
        }

        if (!found)
        {
            if (existing is null)
            {
                existing = new List<ComplexRestrictionBuilder>();
                forwardTmpCr[from] = existing;
            }

            existing.Add(complexRestriction);

            if (complexRestriction.ToGraphId().TileBase() != tileId)
            {
                stats.AddDeferredRestriction(complexRestriction);
            }
            else
            {
                DirectedEdge edge = tilebuilder.DirectedEdgeBuilder((int)to.Id());
                edge.SetEndRestriction(edge.EndRestriction | restriction.Modes());
                tilebuilder.SetDirectedEdgeBuilder((int)to.Id(), edge);

                tilebuilder.AddForwardComplexRestriction(complexRestriction);
                forwardCount++;
            }
        }
    }

    // ------------------------------------------------------------------
    // HandleOnlyRestrictionProperties
    // ------------------------------------------------------------------

    // Faithful port of HandleOnlyRestrictionProperties: write the cross-tile only_* restrictions and
    // mark the part-of-restriction edges, grouped by destination tile.
    internal sealed record DeferredWriteReceipt(
        uint SerializedCrossTileForwardCount,
        uint MarkedCrossTileEdgeCount,
        uint MissingDestinationTileCount);

    // Faithful port of HandleOnlyRestrictionProperties: write the cross-tile
    // only_* restrictions and mark the part-of-restriction edges, grouped by
    // destination tile. The upstream implementation reopens every mutation
    // source from disk instead of reusing GraphReader cache entries.
    internal static DeferredWriteReceipt HandleOnlyRestrictionProperties(
        IReadOnlyList<Result> results,
        GraphReader reader,
        CancellationToken cancellationToken,
        ExecutionOptions? executionOptions = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExecutionOptions options =
            executionOptions ?? ExecutionOptions.Unbounded;
        var restrictions =
            new Dictionary<GraphId, List<ComplexRestrictionBuilder>>();
        var partOfRestriction =
            new Dictionary<GraphId, List<GraphId>>();
        foreach (Result res in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (ComplexRestrictionBuilder restriction
                     in res.Restrictions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GraphId key = restriction.ToGraphId().TileBase();
                if (!restrictions.TryGetValue(
                        key,
                        out List<ComplexRestrictionBuilder>? list))
                {
                    list = new List<ComplexRestrictionBuilder>();
                    restrictions[key] = list;
                }

                list.Add(restriction);
            }

            foreach (GraphId edgeId in res.PartOfRestriction)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GraphId key = edgeId.TileBase();
                if (!partOfRestriction.TryGetValue(
                        key,
                        out List<GraphId>? list))
                {
                    list = new List<GraphId>();
                    partOfRestriction[key] = list;
                }

                list.Add(edgeId);
            }
        }

        reader.Clear();
        uint serializedCrossTileForwardCount = 0;
        uint missingDestinationTileCount = 0;
        foreach (KeyValuePair<
                     GraphId,
                     List<ComplexRestrictionBuilder>> entry
                 in restrictions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphTile? tile = GraphTile.Create(
                reader.TileDir(),
                entry.Key);
            if (tile is null)
            {
                missingDestinationTileCount = checked(
                    missingDestinationTileCount +
                    (uint)entry.Value.Count);
                continue;
            }

            EnsureTileMutationFitsBudget(
                options,
                tile,
                entry.Value.Count);
            long mutationAllocationStart =
                GC.GetAllocatedBytesForCurrentThread();
            IRestrictionTileMutation tileBuilder = CreateTileMutation(options, tile);
            foreach (ComplexRestrictionBuilder restriction
                     in entry.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tileBuilder.AddForwardComplexRestriction(restriction);
                DirectedEdge edge = tileBuilder.DirectedEdgeBuilder(
                    (int)restriction.ToGraphId().Id());
                edge.SetEndRestriction(
                    edge.EndRestriction | restriction.Modes());
                tileBuilder.SetDirectedEdgeBuilder(
                    (int)restriction.ToGraphId().Id(),
                    edge);
            }

            tileBuilder.StoreTileData(reader.TileDir(), cancellationToken);
            RecordTileMutationAllocation(
                options,
                mutationAllocationStart);
            serializedCrossTileForwardCount = checked(
                serializedCrossTileForwardCount +
                (uint)entry.Value.Count);
            reader.Clear();
        }

        uint markedCrossTileEdgeCount = 0;
        foreach (KeyValuePair<GraphId, List<GraphId>> entry
                 in partOfRestriction)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphTile? tile = GraphTile.Create(
                reader.TileDir(),
                entry.Key);
            if (tile is null)
            {
                continue;
            }

            EnsureTileMutationFitsBudget(
                options,
                tile,
                entry.Value.Count);
            long mutationAllocationStart =
                GC.GetAllocatedBytesForCurrentThread();
            IRestrictionTileMutation tileBuilder = CreateTileMutation(options, tile);
            foreach (GraphId edgeId in entry.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DirectedEdge edge = tileBuilder.DirectedEdgeBuilder(
                    (int)edgeId.Id());
                edge.SetComplexRestriction(true);
                tileBuilder.SetDirectedEdgeBuilder(
                    (int)edgeId.Id(),
                    edge);
            }

            tileBuilder.StoreTileData(reader.TileDir(), cancellationToken);
            RecordTileMutationAllocation(
                options,
                mutationAllocationStart);
            markedCrossTileEdgeCount = checked(
                markedCrossTileEdgeCount +
                (uint)entry.Value.Count);
            reader.Clear();
        }

        return new DeferredWriteReceipt(
            serializedCrossTileForwardCount,
            markedCrossTileEdgeCount,
            missingDestinationTileCount);
    }


    // ------------------------------------------------------------------
    // Public Build entry point
    // ------------------------------------------------------------------

    /// <summary>
    /// Adds complex turn restrictions to the graph tiles. Faithful port of
    /// <c>RestrictionBuilder::Build</c>. Iterates every hierarchy level (highest to lowest), reading
    /// each tile in the level, walking the from/to restrictions, and writing forward/reverse complex
    /// restrictions back to the tiles.
    /// </summary>
    /// <param name="reader">Graph reader bound to the tile directory being enhanced.</param>
    /// <param name="complexFromRestrictions">
    /// Restrictions keyed/sorted by the "from" way id (the parser output for complex-from). Must be
    /// sorted per <see cref="OSMRestriction"/> ordering.
    /// </param>
    /// <param name="complexToRestrictions">
    /// Restrictions keyed/sorted by the "to" way id stored as from() (the parser output for
    /// complex-to). Must be sorted per <see cref="OSMRestriction"/> ordering.
    /// </param>
    /// <returns>The aggregated per-level <see cref="Result"/>s.</returns>
    public static IReadOnlyList<Result> Build(
        GraphReader reader,
        IReadOnlyList<OSMRestriction> complexFromRestrictions,
        IReadOnlyList<OSMRestriction> complexToRestrictions,
        CancellationToken cancellationToken = default)
    {
        return Build(
            reader,
            complexFromRestrictions,
            complexToRestrictions,
            ExecutionOptions.Unbounded,
            cancellationToken);
    }

    internal static IReadOnlyList<Result> Build(
        GraphReader reader,
        IReadOnlyList<OSMRestriction> complexFromRestrictions,
        IReadOnlyList<OSMRestriction> complexToRestrictions,
        ExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(complexFromRestrictions);
        ArgumentNullException.ThrowIfNull(complexToRestrictions);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var allResults = new List<Result>();
        IReadOnlyList<TileLevel> levels = TileHierarchy.Levels();
        for (int li = levels.Count - 1; li >= 0; --li)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte level = levels[li].Level;

            IReadOnlyList<GraphId> tileQueue;
            if (options.TileCatalogProvider is not null)
            {
                tileQueue = options.TileCatalogProvider(level) ??
                    throw new InvalidOperationException(
                        "The restriction tile-catalog provider returned null.");
            }
            else
            {
                HashSet<GraphId> tileSet = reader.GetTileSet(level);
                var compatibilityQueue = new List<GraphId>(tileSet);
                compatibilityQueue.Sort(
                    static (left, right) =>
                        left.Value.CompareTo(right.Value));
                tileQueue = compatibilityQueue;
            }

            if (tileQueue.Count > options.MaxTilesPerLevel)
            {
                throw new InvalidOperationException(
                    "The restriction builder exceeded its bounded " +
                    "tile-catalog capacity.");
            }

            var stats = new Result
            {
                MaxDeferredRestrictions =
                    options.MaxDeferredRestrictions,
                MaxPartOfRestrictionEdges =
                    options.MaxPartOfRestrictionEdges,
            };
            Build(
                complexFromRestrictions,
                complexToRestrictions,
                reader,
                tileQueue,
                stats,
                options,
                cancellationToken);

            var results = new List<Result> { stats };
            DeferredWriteReceipt deferred =
                HandleOnlyRestrictionProperties(
                    results,
                    reader,
                    cancellationToken,
                    options);
            stats.CrossTileForwardRestrictionsCount = checked(
                stats.CrossTileForwardRestrictionsCount +
                deferred.SerializedCrossTileForwardCount);
            stats.CrossTilePartOfEdgesMarkedCount = checked(
                stats.CrossTilePartOfEdgesMarkedCount +
                deferred.MarkedCrossTileEdgeCount);
            stats.MissingCrossTileDestinationCount = checked(
                stats.MissingCrossTileDestinationCount +
                deferred.MissingDestinationTileCount);

            allResults.Add(stats);
        }

        return allResults;
    }
    internal static RestrictionMutationPlanReceipt BuildPlan(
        GraphReader reader,
        IReadOnlyList<OSMRestriction> complexFromRestrictions,
        IReadOnlyList<OSMRestriction> complexToRestrictions,
        IRestrictionMutationPlanSink sink,
        ExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(complexFromRestrictions);
        ArgumentNullException.ThrowIfNull(complexToRestrictions);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var workspace = new PlanTraversalWorkspace(
            options.TraversalDepthCapacity,
            options.VisitedNodeCapacity,
            options.TraversedEdgeCapacity);
        IReadOnlyList<PlanTileCatalog> catalogs =
            ResolvePlanTileCatalogs(reader, options, cancellationToken);

        RunPlanPass(
            reader,
            complexFromRestrictions,
            complexToRestrictions,
            DiscardingPlanSink.Instance,
            options,
            catalogs,
            workspace,
            cancellationToken);

        PlanContext plan = RunPlanPass(
            reader,
            complexFromRestrictions,
            complexToRestrictions,
            sink,
            options,
            catalogs,
            workspace,
            cancellationToken);

        return new RestrictionMutationPlanReceipt(
            plan.ForwardCount,
            plan.ReverseCount,
            plan.EdgePatchCount,
            plan.ProjectedCrossTileForwardCount,
            plan.ProjectedCrossTilePartOfEdgeCount,
            options.TraversalDepthCapacity,
            options.VisitedNodeCapacity,
            options.TraversedEdgeCapacity,
            workspace.PeakDepth,
            workspace.PeakVisitedNodes,
            workspace.PeakTraversedEdges,
            GetPlanTraversalWorkspaceReservationBytes(options));
    }

    private static IReadOnlyList<PlanTileCatalog> ResolvePlanTileCatalogs(
        GraphReader reader,
        ExecutionOptions options,
        CancellationToken cancellationToken)
    {
        var catalogs = new List<PlanTileCatalog>(
            TileHierarchy.Levels().Count);
        IReadOnlyList<TileLevel> levels = TileHierarchy.Levels();
        for (int levelIndex = levels.Count - 1;
             levelIndex >= 0;
             levelIndex--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte level = levels[levelIndex].Level;
            IReadOnlyList<GraphId> tileQueue;
            if (options.TileCatalogProvider is not null)
            {
                tileQueue = options.TileCatalogProvider(level) ??
                    throw new InvalidOperationException(
                        "The restriction tile-catalog provider returned null.");
            }
            else
            {
                HashSet<GraphId> tileSet = reader.GetTileSet(level);
                var compatibilityQueue = new List<GraphId>(tileSet);
                compatibilityQueue.Sort(
                    static (left, right) =>
                        left.Value.CompareTo(right.Value));
                tileQueue = compatibilityQueue;
            }

            if (tileQueue.Count > options.MaxTilesPerLevel)
            {
                throw new InvalidOperationException(
                    "The restriction builder exceeded its bounded " +
                    "tile-catalog capacity.");
            }

            catalogs.Add(new PlanTileCatalog(level, tileQueue));
        }

        return catalogs;
    }

    private static PlanContext RunPlanPass(
        GraphReader reader,
        IReadOnlyList<OSMRestriction> complexFromRestrictions,
        IReadOnlyList<OSMRestriction> complexToRestrictions,
        IRestrictionMutationPlanSink sink,
        ExecutionOptions options,
        IReadOnlyList<PlanTileCatalog> catalogs,
        PlanTraversalWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var plan = new PlanContext(sink, workspace);
        foreach (PlanTileCatalog catalog in catalogs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stats = new Result
            {
                MaxDeferredRestrictions =
                    options.MaxDeferredRestrictions,
                MaxPartOfRestrictionEdges =
                    options.MaxPartOfRestrictionEdges,
            };
            Build(
                complexFromRestrictions,
                complexToRestrictions,
                reader,
                catalog.Tiles,
                stats,
                options,
                cancellationToken,
                plan);
            reader.Clear();
        }

        return plan;
    }
}
