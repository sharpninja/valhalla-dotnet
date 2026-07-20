using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Traffic.Routing;

/// <summary>
/// Reads immutable lane topology directly from Valhalla graph tiles and caches directed-edge
/// entries under the caller-supplied graph signature.
/// </summary>
public sealed class GraphTileLaneTopologyIndex : IDisposable
{
    private readonly BoundedAsyncCache<string, GraphPartition> _graphs;
    private readonly BoundedAsyncCache<TileCacheKey, GraphTile?> _tiles;
    private readonly BoundedAsyncCache<TransitionCacheKey, LaneTransitionTopologyContext> _transitionContexts;
    private readonly BoundedAsyncCache<OverlayCacheKey, LaneTopologyOverlayLoadResult> _overlaySnapshots;
    private readonly Func<string, GraphId, GraphTile?> _tileLoader;
    private readonly GraphTileLaneTopologyIndexOptions _options;
    private readonly ILaneTopologyOverlaySource? _overlaySource;
    private int _disposed;

    public GraphTileLaneTopologyIndex()
        : this(
            static (directory, tileId) => GraphTile.Create(directory, tileId),
            null,
            null)
    {
    }

    public GraphTileLaneTopologyIndex(GraphTileLaneTopologyIndexOptions options)
        : this(
            static (directory, tileId) => GraphTile.Create(directory, tileId),
            options,
            null)
    {
    }

    public GraphTileLaneTopologyIndex(
        ILaneTopologyOverlaySource overlaySource,
        GraphTileLaneTopologyIndexOptions options)
        : this(
            static (directory, tileId) => GraphTile.Create(directory, tileId),
            options,
            overlaySource)
    {
    }

    internal GraphTileLaneTopologyIndex(
        Func<string, GraphId, GraphTile?> tileLoader)
        : this(tileLoader, null, null)
    {
    }

    internal GraphTileLaneTopologyIndex(
        Func<string, GraphId, GraphTile?> tileLoader,
        GraphTileLaneTopologyIndexOptions? options)
        : this(tileLoader, options, null)
    {
    }

    private GraphTileLaneTopologyIndex(
        Func<string, GraphId, GraphTile?> tileLoader,
        GraphTileLaneTopologyIndexOptions? options,
        ILaneTopologyOverlaySource? overlaySource)
    {
        _tileLoader = tileLoader ?? throw new ArgumentNullException(nameof(tileLoader));
        _options = options ?? GraphTileLaneTopologyIndexOptions.Default;
        _options.Validate();
        _overlaySource = overlaySource;
        _tiles = new BoundedAsyncCache<TileCacheKey, GraphTile?>(
            _options.MaximumTiles,
            Math.Min(_options.MaximumConcurrentBuilds, _options.MaximumTiles));
        _transitionContexts =
            new BoundedAsyncCache<TransitionCacheKey, LaneTransitionTopologyContext>(
                _options.MaximumTransitionContexts,
                Math.Min(
                    _options.MaximumConcurrentBuilds,
                    _options.MaximumTransitionContexts));
        _overlaySnapshots =
            new BoundedAsyncCache<OverlayCacheKey, LaneTopologyOverlayLoadResult>(
                _options.MaximumOverlaySnapshots,
                Math.Min(
                    _options.MaximumConcurrentBuilds,
                    _options.MaximumOverlaySnapshots));
        _graphs = new BoundedAsyncCache<string, GraphPartition>(
            _options.MaximumGraphSignatures,
            Math.Min(
                _options.MaximumConcurrentBuilds,
                _options.MaximumGraphSignatures),
            StringComparer.Ordinal,
            OnGraphSignatureEvicted);
    }

    public int CachedGraphSignatureCount => _graphs.Count;

    public int CachedDirectedEdgeCount =>
        _graphs.CompletedValues.Sum(static graph => graph.Edges.Count);

    public int CachedTileCount => _tiles.Count;

    public int CachedTransitionContextCount => _transitionContexts.Count;

    public int CachedOverlaySnapshotCount => _overlaySnapshots.Count;

    public async Task<ValhallaLaneTopologySnapshot> ReadAsync(
        ValhallaGraphTrafficContext context,
        IReadOnlyList<ulong> canonicalDirectedEdgeIds,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(canonicalDirectedEdgeIds);
        if (context.GraphTileDirectory is null)
        {
            throw new ArgumentException(
                "GraphTileDirectory is required to read Valhalla lane topology.",
                nameof(context));
        }

        if (!Directory.Exists(context.GraphTileDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Valhalla graph tile directory '{context.GraphTileDirectory}' does not exist.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using BoundedAsyncCache<string, GraphPartition>.Lease graphLease =
            await _graphs.AcquireAsync(
                    context.GraphSignature,
                    _ => Task.FromResult(
                        new GraphPartition(
                            _options.MaximumDirectedEdgesPerGraph,
                            Math.Min(
                                _options.MaximumConcurrentBuilds,
                                _options.MaximumDirectedEdgesPerGraph))),
                    cancellationToken)
                .ConfigureAwait(false);
        GraphPartition graph = graphLease.Value;

        var ordered = new Dictionary<ulong, LaneTopologySegment>();
        foreach (ulong canonicalId in canonicalDirectedEdgeIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            LaneTopologySegment? topology = await graph.Edges.GetOrAddAsync(
                    canonicalId,
                    token => ReadEdgeAsync(
                        context.GraphSignature,
                        context.GraphTileDirectory,
                        canonicalId,
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
            if (topology is not null)
            {
                ordered.Add(canonicalId, topology);
            }
        }

        var transitionContexts = new Dictionary<LaneTransitionKey, LaneTransitionTopologyContext>();
        for (var index = 0; index + 1 < canonicalDirectedEdgeIds.Count; index++)
        {
            var transitionKey = new LaneTransitionKey(
                canonicalDirectedEdgeIds[index],
                canonicalDirectedEdgeIds[index + 1]);
            if (transitionContexts.ContainsKey(transitionKey) ||
                !ordered.ContainsKey(transitionKey.FromCanonicalDirectedEdgeId) ||
                !ordered.ContainsKey(transitionKey.ToCanonicalDirectedEdgeId))
            {
                continue;
            }

            var cacheKey = new TransitionCacheKey(
                context.GraphSignature,
                transitionKey.FromCanonicalDirectedEdgeId,
                transitionKey.ToCanonicalDirectedEdgeId);
            LaneTransitionTopologyContext transitionContext =
                await _transitionContexts.GetOrAddAsync(
                        cacheKey,
                        token => ReadTransitionContextAsync(
                            context.GraphSignature,
                            context.GraphTileDirectory,
                            ordered[transitionKey.FromCanonicalDirectedEdgeId],
                            ordered[transitionKey.ToCanonicalDirectedEdgeId],
                            token),
                        cancellationToken)
                    .ConfigureAwait(false);
            transitionContexts.Add(transitionKey, transitionContext);
        }

        LaneTopologyOverlayLoadResult overlayLoadResult =
            await LoadAndValidateOverlayAsync(
                    context.GraphSignature,
                    context.GraphTileDirectory,
                    canonicalDirectedEdgeIds,
                    ordered,
                    graph,
                    cancellationToken)
                .ConfigureAwait(false);

        return new ValhallaLaneTopologySnapshot(
            context.GraphSignature,
            new ReadOnlyDictionary<ulong, LaneTopologySegment>(ordered))
        {
            TransitionContexts =
                new ReadOnlyDictionary<LaneTransitionKey, LaneTransitionTopologyContext>(
                    transitionContexts),
            OverlayLoadResult = overlayLoadResult,
        };
    }

    public void Invalidate(string graphSignature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphSignature);
        _graphs.RemoveWhere(signature =>
            string.Equals(signature, graphSignature, StringComparison.Ordinal));
        _tiles.RemoveWhere(key =>
            string.Equals(key.GraphSignature, graphSignature, StringComparison.Ordinal));
        _transitionContexts.RemoveWhere(key =>
            string.Equals(key.GraphSignature, graphSignature, StringComparison.Ordinal));
        _overlaySnapshots.RemoveWhere(key =>
            string.Equals(key.GraphSignature, graphSignature, StringComparison.Ordinal));
    }

    public void Clear()
    {
        _graphs.Clear();
        _tiles.Clear();
        _transitionContexts.Clear();
        _overlaySnapshots.Clear();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _graphs.Dispose();
            _tiles.Dispose();
            _transitionContexts.Dispose();
            _overlaySnapshots.Dispose();
        }
    }

    private void OnGraphSignatureEvicted(
        string graphSignature,
        GraphPartition graph)
    {
        graph.Dispose();
        _tiles.RemoveWhere(key =>
            string.Equals(key.GraphSignature, graphSignature, StringComparison.Ordinal));
        _transitionContexts.RemoveWhere(key =>
            string.Equals(key.GraphSignature, graphSignature, StringComparison.Ordinal));
        _overlaySnapshots.RemoveWhere(key =>
            string.Equals(key.GraphSignature, graphSignature, StringComparison.Ordinal));
    }

    private async Task<LaneTopologyOverlayLoadResult> LoadAndValidateOverlayAsync(
        string graphSignature,
        string graphTileDirectory,
        IReadOnlyList<ulong> canonicalDirectedEdgeIds,
        IReadOnlyDictionary<ulong, LaneTopologySegment> graphSegments,
        GraphPartition graph,
        CancellationToken cancellationToken)
    {
        if (_overlaySource is null)
        {
            return LaneTopologyOverlayLoadResult.NotFound("not-configured");
        }

        ulong[] orderedEdgeIds = canonicalDirectedEdgeIds.ToArray();
        var request = new LaneTopologyOverlayRequest(
            graphSignature,
            Array.AsReadOnly(orderedEdgeIds));
        var cacheKey = new OverlayCacheKey(
            graphSignature,
            BuildOrderedRouteEdgeIdentity(orderedEdgeIds));
        LaneTopologyOverlayLoadResult loaded = await _overlaySnapshots.GetOrAddAsync(
                cacheKey,
                token => _overlaySource.LoadAsync(request, token).AsTask(),
                cancellationToken)
            .ConfigureAwait(false);
        if (loaded.Status != LaneTopologyOverlayLoadStatus.Loaded)
        {
            return loaded;
        }

        if (loaded.Overlay is null)
        {
            return LaneTopologyOverlayLoadResult.Invalid(
                loaded.SourceId,
                new LaneTopologyOverlayDiagnostic(
                    LaneTopologyOverlayDiagnosticCode.MalformedPayload,
                    "The lane topology overlay source returned Loaded without an overlay."));
        }

        ulong[] referencedEdgeIds = (loaded.Overlay.Edges ?? [])
            .Select(static edge => edge.CanonicalDirectedEdgeId)
            .Concat((loaded.Overlay.Transitions ?? [])
                .SelectMany(static transition => new[]
                {
                    transition.FromCanonicalDirectedEdgeId,
                    transition.ToCanonicalDirectedEdgeId,
                }))
            .Concat((loaded.Overlay.FrictionPoints ?? [])
                .Select(static point => point.CanonicalDirectedEdgeId))
            .Distinct()
            .ToArray();
        if (referencedEdgeIds.Length > _options.MaximumDirectedEdgesPerGraph)
        {
            return LaneTopologyOverlayLoadResult.Invalid(
                loaded.SourceId,
                new LaneTopologyOverlayDiagnostic(
                    LaneTopologyOverlayDiagnosticCode.PayloadTooLarge,
                    "The overlay references too many canonical directed edges."));
        }

        var referencedSegments = new Dictionary<ulong, LaneTopologySegment>();
        foreach (ulong edgeId in referencedEdgeIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LaneTopologySegment? segment = await graph.Edges.GetOrAddAsync(
                    edgeId,
                    token => ReadEdgeAsync(
                        graphSignature,
                        graphTileDirectory,
                        edgeId,
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
            if (segment is not null)
            {
                referencedSegments[edgeId] = segment;
            }
        }

        LaneTopologyOverlayValidationResult fullValidation =
            LaneTopologyOverlayValidator.Validate(
                loaded.Overlay,
                graphSignature,
                referencedSegments);
        if (!fullValidation.IsValid || fullValidation.Overlay is null)
        {
            return LaneTopologyOverlayLoadResult.Invalid(
                loaded.SourceId,
                fullValidation.Diagnostics.ToArray());
        }

        CanonicalLaneTopologyOverlay scoped = ScopeOverlay(
            fullValidation.Overlay,
            orderedEdgeIds);
        LaneTopologyOverlayValidationResult scopedValidation =
            LaneTopologyOverlayValidator.Validate(
                scoped,
                graphSignature,
                graphSegments);
        return scopedValidation.IsValid && scopedValidation.Overlay is not null
            ? LaneTopologyOverlayLoadResult.Loaded(
                scopedValidation.Overlay,
                loaded.SourceId)
            : LaneTopologyOverlayLoadResult.Invalid(
                loaded.SourceId,
                scopedValidation.Diagnostics.ToArray());
    }

    private static CanonicalLaneTopologyOverlay ScopeOverlay(
        CanonicalLaneTopologyOverlay overlay,
        IReadOnlyList<ulong> orderedEdgeIds)
    {
        var routeEdges = orderedEdgeIds.ToHashSet();
        var routeTransitions = new HashSet<LaneTransitionKey>();
        for (var index = 0; index + 1 < orderedEdgeIds.Count; index++)
        {
            routeTransitions.Add(new LaneTransitionKey(
                orderedEdgeIds[index],
                orderedEdgeIds[index + 1]));
        }

        return overlay with
        {
            Edges = Array.AsReadOnly((overlay.Edges ?? [])
                .Where(edge => routeEdges.Contains(edge.CanonicalDirectedEdgeId))
                .ToArray()),
            Transitions = Array.AsReadOnly((overlay.Transitions ?? [])
                .Where(transition => routeTransitions.Contains(new LaneTransitionKey(
                    transition.FromCanonicalDirectedEdgeId,
                    transition.ToCanonicalDirectedEdgeId)))
                .ToArray()),
            FrictionPoints = Array.AsReadOnly((overlay.FrictionPoints ?? [])
                .Where(point => routeEdges.Contains(point.CanonicalDirectedEdgeId))
                .ToArray()),
        };
    }

    private static string BuildOrderedRouteEdgeIdentity(
        IReadOnlyList<ulong> orderedEdgeIds)
        => string.Join(
            ",",
            orderedEdgeIds.Select(edgeId => edgeId.ToString(
                "X16",
                CultureInfo.InvariantCulture)));

    private async Task<LaneTopologySegment?> ReadEdgeAsync(
        string graphSignature,
        string graphTileDirectory,
        ulong canonicalDirectedEdgeId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var edgeId = new GraphId(canonicalDirectedEdgeId);
        GraphTile? tile = await GetCachedTileAsync(
                graphSignature,
                graphTileDirectory,
                edgeId.TileBase(),
                cancellationToken)
            .ConfigureAwait(false);
        if (tile is null || edgeId.Id() >= tile.DirectedEdgeCount())
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        DirectedEdge edge = tile.DirectedEdge(edgeId);
        int laneCount = Math.Max(1, checked((int)edge.LaneCount));
        IReadOnlyList<ushort> turnLaneMasks = edge.TurnLanes
            ? tile.TurnLanes(checked((uint)edgeId.Id()))
            : Array.Empty<ushort>();
        IReadOnlyList<LaneTurnIntent> laneIntents = MapLaneIntents(
            turnLaneMasks,
            laneCount);
        IReadOnlyList<LaneTopologyConnection> incoming = tile
            .GetLaneConnectivity(checked((uint)edgeId.Id()))
            .Select(static connection => new LaneTopologyConnection(
                connection.From.ToString(CultureInfo.InvariantCulture),
                ParseLanes(connection.FromLanes),
                ParseLanes(connection.ToLanes)))
            .ToArray();
        ulong wayId = tile.EdgeInfo(edge).WayId;
        string segmentId = canonicalDirectedEdgeId.ToString(
            "X16",
            CultureInfo.InvariantCulture);
        LaneTopologyGraphEvidence? evidence = await CreateGraphEvidenceAsync(
                graphSignature,
                graphTileDirectory,
                edgeId,
                tile,
                edge,
                turnLaneMasks,
                cancellationToken)
            .ConfigureAwait(false);
        return new LaneTopologySegment(
            segmentId,
            laneCount,
            edge.Length,
            laneIntents,
            incoming)
        {
            CanonicalDirectedEdgeId = canonicalDirectedEdgeId,
            OsmWayId = wayId,
            TruckSensitive = true,
            GraphEvidence = evidence,
        };
    }

    private async Task<LaneTopologyGraphEvidence?> CreateGraphEvidenceAsync(
        string graphSignature,
        string graphTileDirectory,
        GraphId edgeId,
        GraphTile tile,
        DirectedEdge edge,
        IReadOnlyList<ushort> turnLaneMasks,
        CancellationToken cancellationToken)
    {
        GraphId endNode = edge.EndNode;
        GraphTile? endTile = await GetCachedTileAsync(
                graphSignature,
                graphTileDirectory,
                endNode.TileBase(),
                cancellationToken)
            .ConfigureAwait(false);
        if (endTile is null || endNode.Id() >= endTile.NodeCount())
        {
            return null;
        }

        NodeInfo endNodeInfo = endTile.Node(endNode);
        uint opposingEdgeIndex = endNodeInfo.EdgeIndex + edge.OppIndex;
        if (opposingEdgeIndex >= endTile.DirectedEdgeCount())
        {
            return null;
        }

        var opposingEdgeId = new GraphId(
            endNode.Tileid(),
            endNode.Level(),
            opposingEdgeIndex);
        DirectedEdge opposingEdge = endTile.DirectedEdge(opposingEdgeId);
        GraphId startNode = opposingEdge.EndNode;
        GraphTile? startTile = await GetCachedTileAsync(
                graphSignature,
                graphTileDirectory,
                startNode.TileBase(),
                cancellationToken)
            .ConfigureAwait(false);
        if (startTile is null || startNode.Id() >= startTile.NodeCount())
        {
            return null;
        }

        NodeInfo startNodeInfo = startTile.Node(startNode);
        double startHeading = startNodeInfo.Heading(edge.LocalEdgeIdx);
        double endHeading = (endNodeInfo.Heading(edge.OppIndex) + 180d) % 360d;
        EdgeInfo edgeInfo = tile.EdgeInfo(edge);
        string[] references = edgeInfo
            .GetNamesAndTypes()
            .Where(static name => name.IsRouteNum)
            .Select(static name => name.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] destinations = tile
            .GetSigns(checked((uint)edgeId.Id()))
            .Where(static sign =>
                sign.Type is Sign.Type.ExitBranch or
                    Sign.Type.ExitToward or
                    Sign.Type.GuideBranch or
                    Sign.Type.GuideToward)
            .Select(static sign => sign.Text)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new LaneTopologyGraphEvidence(
            startNode.Value,
            endNode.Value,
            edge.LocalEdgeIdx,
            startHeading,
            endHeading,
            edge.Use,
            laneCountKnown: edge.LaneCount > 1 || turnLaneMasks.Count > 0,
            references,
            destinations);
    }

    private async Task<LaneTransitionTopologyContext> ReadTransitionContextAsync(
        string graphSignature,
        string graphTileDirectory,
        LaneTopologySegment from,
        LaneTopologySegment to,
        CancellationToken cancellationToken)
    {
        LaneTopologyGraphEvidence? fromEvidence = from.GraphEvidence;
        LaneTopologyGraphEvidence? toEvidence = to.GraphEvidence;
        if (fromEvidence is null ||
            toEvidence is null ||
            fromEvidence.CanonicalEndNodeId != toEvidence.CanonicalStartNodeId)
        {
            return new LaneTransitionTopologyContext(
                [to],
                [from],
                outboundEdgesComplete: false,
                inboundEdgesComplete: false,
                source: LaneTransitionTopologyContextSource.MissingGraphData);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var sharedNode = new GraphId(fromEvidence.CanonicalEndNodeId);
        GraphTile? sharedTile = await GetCachedTileAsync(
                graphSignature,
                graphTileDirectory,
                sharedNode.TileBase(),
                cancellationToken)
            .ConfigureAwait(false);
        if (sharedTile is null || sharedNode.Id() >= sharedTile.NodeCount())
        {
            return new LaneTransitionTopologyContext(
                [to],
                [from],
                outboundEdgesComplete: false,
                inboundEdgesComplete: false,
                source: LaneTransitionTopologyContextSource.MissingGraphData);
        }

        NodeInfo sharedNodeInfo = sharedTile.Node(sharedNode);
        var outbound = new List<LaneTopologySegment>();
        var inbound = new List<LaneTopologySegment>();
        bool outboundEdgesComplete = true;
        bool inboundEdgesComplete = true;
        for (uint offset = 0; offset < sharedNodeInfo.EdgeCount; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint edgeIndex = sharedNodeInfo.EdgeIndex + offset;
            if (edgeIndex >= sharedTile.DirectedEdgeCount())
            {
                outboundEdgesComplete = false;
                inboundEdgesComplete = false;
                continue;
            }

            var outboundId = new GraphId(
                sharedNode.Tileid(),
                sharedNode.Level(),
                edgeIndex);
            LaneTopologySegment? outboundSegment = await ReadEdgeAsync(
                    graphSignature,
                    graphTileDirectory,
                    outboundId.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            if (outboundSegment is not null)
            {
                outbound.Add(outboundSegment);
                if (outboundSegment.GraphEvidence is null)
                {
                    outboundEdgesComplete = false;
                }
            }
            else
            {
                outboundEdgesComplete = false;
            }

            DirectedEdge outboundEdge = sharedTile.DirectedEdge(outboundId);
            GraphId outboundEndNode = outboundEdge.EndNode;
            GraphTile? outboundEndTile = await GetCachedTileAsync(
                    graphSignature,
                    graphTileDirectory,
                    outboundEndNode.TileBase(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (outboundEndTile is null ||
                outboundEndNode.Id() >= outboundEndTile.NodeCount())
            {
                inboundEdgesComplete = false;
                continue;
            }

            NodeInfo outboundEndNodeInfo = outboundEndTile.Node(outboundEndNode);
            uint opposingIndex = outboundEndNodeInfo.EdgeIndex + outboundEdge.OppIndex;
            if (opposingIndex >= outboundEndTile.DirectedEdgeCount())
            {
                inboundEdgesComplete = false;
                continue;
            }

            var inboundId = new GraphId(
                outboundEndNode.Tileid(),
                outboundEndNode.Level(),
                opposingIndex);
            LaneTopologySegment? inboundSegment = await ReadEdgeAsync(
                    graphSignature,
                    graphTileDirectory,
                    inboundId.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            if (inboundSegment is not null)
            {
                inbound.Add(inboundSegment);
                if (inboundSegment.GraphEvidence is null)
                {
                    inboundEdgesComplete = false;
                }
            }
            else
            {
                inboundEdgesComplete = false;
            }
        }

        if (!outbound.Any(segment =>
                string.Equals(segment.SegmentId, to.SegmentId, StringComparison.Ordinal)))
        {
            outboundEdgesComplete = false;
            outbound.Add(to);
        }

        if (!inbound.Any(segment =>
                string.Equals(segment.SegmentId, from.SegmentId, StringComparison.Ordinal)))
        {
            inboundEdgesComplete = false;
            inbound.Add(from);
        }

        return new LaneTransitionTopologyContext(
            outbound.DistinctBy(static segment => segment.SegmentId, StringComparer.Ordinal),
            inbound.DistinctBy(static segment => segment.SegmentId, StringComparer.Ordinal),
            outboundEdgesComplete,
            inboundEdgesComplete,
            outboundEdgesComplete && inboundEdgesComplete
                ? LaneTransitionTopologyContextSource.GraphTile
                : LaneTransitionTopologyContextSource.IncompleteGraphTile);
    }

    private Task<GraphTile?> GetCachedTileAsync(
        string graphSignature,
        string graphTileDirectory,
        GraphId tileBase,
        CancellationToken cancellationToken)
    {
        var key = new TileCacheKey(graphSignature, tileBase.Value);
        return _tiles.GetOrAddAsync(
            key,
            token => Task.Run(
                () => _tileLoader(graphTileDirectory, tileBase),
                token),
            cancellationToken);
    }

    private static IReadOnlyList<int> ParseLanes(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<int>();
        }

        return text
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static lane => int.Parse(lane, NumberStyles.Integer, CultureInfo.InvariantCulture))
            .Where(static lane => lane > 0)
            .ToArray();
    }

    private static IReadOnlyList<LaneTurnIntent> MapLaneIntents(
        IReadOnlyList<ushort> masks,
        int laneCount)
    {
        var intents = new LaneTurnIntent[laneCount];
        for (var laneIndex = 0; laneIndex < laneCount; laneIndex++)
        {
            ushort mask = laneIndex < masks.Count ? masks[laneIndex] : TurnLaneConstants.TurnLaneEmpty;
            LaneTurnIntent intent = LaneTurnIntent.None;
            if ((mask & TurnLaneConstants.TurnLaneThrough) != 0)
            {
                intent |= LaneTurnIntent.Through;
            }

            if ((mask & TurnLaneConstants.TurnLaneSharpLeft) != 0)
            {
                intent |= LaneTurnIntent.SharpLeft;
            }

            if ((mask & TurnLaneConstants.TurnLaneLeft) != 0)
            {
                intent |= LaneTurnIntent.Left;
            }

            if ((mask & TurnLaneConstants.TurnLaneSlightLeft) != 0)
            {
                intent |= LaneTurnIntent.SlightLeft;
            }

            if ((mask & TurnLaneConstants.TurnLaneSlightRight) != 0)
            {
                intent |= LaneTurnIntent.SlightRight;
            }

            if ((mask & TurnLaneConstants.TurnLaneRight) != 0)
            {
                intent |= LaneTurnIntent.Right;
            }

            if ((mask & TurnLaneConstants.TurnLaneSharpRight) != 0)
            {
                intent |= LaneTurnIntent.SharpRight;
            }

            if ((mask & TurnLaneConstants.TurnLaneReverse) != 0)
            {
                intent |= LaneTurnIntent.Reverse;
            }

            if ((mask & TurnLaneConstants.TurnLaneMergeToLeft) != 0)
            {
                intent |= LaneTurnIntent.MergeToLeft;
            }

            if ((mask & TurnLaneConstants.TurnLaneMergeToRight) != 0)
            {
                intent |= LaneTurnIntent.MergeToRight;
            }

            intents[laneIndex] = intent;
        }

        return Array.AsReadOnly(intents);
    }

    private sealed class GraphPartition : IDisposable
    {
        public GraphPartition(
            int maximumDirectedEdges,
            int maximumConcurrentBuilds)
        {
            Edges = new BoundedAsyncCache<ulong, LaneTopologySegment?>(
                maximumDirectedEdges,
                maximumConcurrentBuilds);
        }

        public BoundedAsyncCache<ulong, LaneTopologySegment?> Edges { get; }

        public void Dispose()
            => Edges.Dispose();
    }

    private readonly record struct TileCacheKey(string GraphSignature, ulong TileId);

    private readonly record struct TransitionCacheKey(
        string GraphSignature,
        ulong FromCanonicalDirectedEdgeId,
        ulong ToCanonicalDirectedEdgeId);

    private readonly record struct OverlayCacheKey(
        string GraphSignature,
        string OrderedRouteEdgeIdentity);
}

public readonly record struct LaneTransitionKey(
    ulong FromCanonicalDirectedEdgeId,
    ulong ToCanonicalDirectedEdgeId);

public sealed record ValhallaLaneTopologySnapshot(
    string GraphSignature,
    IReadOnlyDictionary<ulong, LaneTopologySegment> Edges)
{
    public IReadOnlyDictionary<LaneTransitionKey, LaneTransitionTopologyContext> TransitionContexts
    {
        get;
        init;
    } = new ReadOnlyDictionary<LaneTransitionKey, LaneTransitionTopologyContext>(
        new Dictionary<LaneTransitionKey, LaneTransitionTopologyContext>());

    public LaneTopologyOverlayLoadResult OverlayLoadResult { get; init; } =
        LaneTopologyOverlayLoadResult.NotFound("not-configured");
}

/// <summary>
/// Projects ordered canonical edge IDs from a production route candidate into lane segments,
/// canonical graph-derived friction points, a scored lane profile, and actionable guidance.
/// </summary>
public sealed class ValhallaRouteLaneFrictionProjector
{
    private readonly GraphTileLaneTopologyIndex _topologyIndex;
    private readonly LaneFrictionProjectionOptions _options;
    private readonly ILaneTransitionDeriver _transitionDeriver;

    public ValhallaRouteLaneFrictionProjector(
        GraphTileLaneTopologyIndex topologyIndex,
        LaneFrictionProjectionOptions? options = null,
        ILaneTransitionDeriver? transitionDeriver = null)
    {
        _topologyIndex = topologyIndex ?? throw new ArgumentNullException(nameof(topologyIndex));
        _options = options ?? LaneFrictionProjectionOptions.Default;
        _transitionDeriver = transitionDeriver ?? new EvidenceBackedLaneTransitionDeriver();
        _options.Validate();
    }

    public async Task<RouteLaneFrictionProjection> ProjectAsync(
        OsmRouteCandidate route,
        LaneFrictionVehicleClass vehicleClass,
        ValhallaGraphTrafficContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(context);
        IReadOnlyList<ulong> routeEdgeIds = route.DirectedEdgeIds ?? Array.Empty<ulong>();
        if (routeEdgeIds.Count == 0)
        {
            return RouteLaneFrictionProjection.Empty;
        }

        ValhallaLaneTopologySnapshot snapshot = await _topologyIndex
            .ReadAsync(context, routeEdgeIds, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot.OverlayLoadResult.Status == LaneTopologyOverlayLoadStatus.Invalid)
        {
            return RouteLaneFrictionProjection.CanonicalOverlayMismatch(
                snapshot.OverlayLoadResult.Diagnostics);
        }

        CanonicalLaneTopologyOverlay? canonicalOverlay =
            snapshot.OverlayLoadResult.Status == LaneTopologyOverlayLoadStatus.Loaded
                ? snapshot.OverlayLoadResult.Overlay
                : null;
        ulong[] missing = routeEdgeIds
            .Where(id => !snapshot.Edges.ContainsKey(id))
            .Distinct()
            .ToArray();
        LaneTopologySegment[] topology = routeEdgeIds
            .Where(snapshot.Edges.ContainsKey)
            .Select(id => snapshot.Edges[id])
            .ToArray();
        if (topology.Length == 0 || missing.Length > 0)
        {
            return RouteLaneFrictionProjection.Unavailable(missing);
        }

        LaneTopologySegment[] uniqueTopology = topology
            .DistinctBy(static segment => segment.SegmentId, StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<CanonicalLaneFrictionPoint> canonicalPoints =
            MergeCanonicalPoints(
                LaneFrictionGraphBuilder.BuildCanonicalPoints(uniqueTopology),
                canonicalOverlay,
                snapshot.Edges);
        IReadOnlyDictionary<LaneTransitionKey, CanonicalLaneTransitionOverlay>
            overlayTransitions = (canonicalOverlay?.Transitions ??
                    Array.Empty<CanonicalLaneTransitionOverlay>())
                .ToDictionary(
                    transition => new LaneTransitionKey(
                        transition.FromCanonicalDirectedEdgeId,
                        transition.ToCanonicalDirectedEdgeId));
        var transitions = new List<IReadOnlyList<LaneTransitionOption>>();
        var derivations = new List<LaneTransitionDerivation>();
        for (var index = 0; index + 1 < topology.Length; index++)
        {
            var transitionKey = new LaneTransitionKey(
                routeEdgeIds[index],
                routeEdgeIds[index + 1]);
            LaneTransitionTopologyContext transitionContext =
                snapshot.TransitionContexts.TryGetValue(transitionKey, out LaneTransitionTopologyContext? found)
                    ? found
                    : new LaneTransitionTopologyContext(
                        [topology[index + 1]],
                        [topology[index]],
                        outboundEdgesComplete: false,
                        inboundEdgesComplete: false,
                        source: LaneTransitionTopologyContextSource.MissingGraphData);
            LaneTransitionDerivation derivation =
                canonicalOverlay is not null &&
                overlayTransitions.TryGetValue(
                    transitionKey,
                    out CanonicalLaneTransitionOverlay? overlayTransition)
                    ? CreateOverlayDerivation(
                        topology[index],
                        topology[index + 1],
                        overlayTransition,
                        canonicalOverlay.Descriptor)
                    : _transitionDeriver.Derive(
                        topology[index],
                        topology[index + 1],
                        transitionContext);
            derivations.Add(derivation);
            if (derivation.CanDriveGuidance)
            {
                transitions.Add(derivation.Options);
            }
        }

        if (derivations.Any(static derivation => !derivation.CanDriveGuidance))
        {
            LaneFrictionProfile overlayLowerBound =
                LaneFrictionAnalyzer.AnalyzeOverlayLowerBound(
                    canonicalPoints,
                    uniqueTopology.ToDictionary(
                        static segment => segment.SegmentId,
                        static segment => segment.LaneCount,
                        StringComparer.Ordinal),
                    vehicleClass);
            return RouteLaneFrictionProjection.UncertainConnectivity(
                Array.AsReadOnly(derivations.ToArray()),
                canonicalPoints,
                overlayLowerBound);
        }

        canonicalPoints = RemoveRouteCompatibleExitOnlyPoints(
            canonicalPoints,
            topology,
            transitions);
        IReadOnlyList<LaneTransitionOption>? selectedPath = SelectMinimumFrictionPath(
            transitions,
            topology,
            canonicalPoints,
            vehicleClass,
            _options);
        if (selectedPath is null)
        {
            return RouteLaneFrictionProjection.InfeasibleLaneChanges();
        }

        IReadOnlyList<LaneTransitionOption> selected = selectedPath;
        var routeSegments = new List<RouteLaneSegment>(topology.Length);
        double distanceAlongRoute = 0d;
        for (var index = 0; index < topology.Length; index++)
        {
            int entryLane;
            int exitLane;
            if (topology.Length == 1)
            {
                int selectedLane = Enumerable.Range(1, Math.Max(1, topology[index].LaneCount))
                    .OrderBy(lane => ScoreSegment(
                        topology[index],
                        canonicalPoints,
                        lane,
                        lane,
                        vehicleClass))
                    .ThenBy(static lane => lane)
                    .First();
                entryLane = selectedLane;
                exitLane = selectedLane;
            }
            else
            {
                entryLane = index == 0 ? selected[0].FromLane : selected[index - 1].ToLane;
                exitLane = index == topology.Length - 1 ? entryLane : selected[index].FromLane;
            }

            routeSegments.Add(new RouteLaneSegment(
                topology[index].SegmentId,
                entryLane,
                exitLane,
                distanceAlongRoute)
            {
                OccurrenceIndex = index,
                OverlaySource =
                    (index < derivations.Count
                        ? derivations[index].OverlaySource
                        : null) ??
                    (index > 0
                        ? derivations[index - 1].OverlaySource
                        : null),
            });
            distanceAlongRoute += Math.Max(0d, topology[index].LengthMeters);
        }

        IReadOnlyList<RouteLaneFrictionModifier> routeModifiers =
            BuildRouteModifiers(routeSegments, canonicalPoints);
        LaneFrictionProfile profile = LaneFrictionAnalyzer.Analyze(new LaneFrictionRequest(
            canonicalPoints,
            routeSegments,
            vehicleClass,
            routeModifiers));
        return new RouteLaneFrictionProjection(
            HasTopologyData: true,
            UsedFallbackConnectivity: false,
            RouteSegments: Array.AsReadOnly(routeSegments.ToArray()),
            CanonicalPoints: canonicalPoints,
            Profile: profile,
            MissingDirectedEdgeIds: Array.AsReadOnly(missing))
        {
            RouteModifiers = routeModifiers,
            TransitionDerivations = Array.AsReadOnly(derivations.ToArray()),
            FailureReason = LaneProjectionFailureReason.None,
        };
    }

    private static IReadOnlyList<CanonicalLaneFrictionPoint> MergeCanonicalPoints(
        IReadOnlyList<CanonicalLaneFrictionPoint> graphPoints,
        CanonicalLaneTopologyOverlay? overlay,
        IReadOnlyDictionary<ulong, LaneTopologySegment> graphSegments)
    {
        var merged = new Dictionary<(
            string SegmentId,
            int LaneNumber,
            long DistanceMillimeters,
            LaneFrictionContributionKind Kind), CanonicalLaneFrictionPoint>();
        foreach (CanonicalLaneFrictionPoint point in graphPoints)
        {
            merged[CanonicalPointKey(point)] = point;
        }

        if (overlay is not null)
        {
            foreach (CanonicalLaneFrictionOverlay point in overlay.FrictionPoints)
            {
                LaneTopologySegment segment = graphSegments[point.CanonicalDirectedEdgeId];
                var projected = new CanonicalLaneFrictionPoint(
                    segment.SegmentId,
                    point.LaneNumber,
                    point.DistanceAlongEdgeMeters,
                    point.Kind,
                    point.Severity,
                    point.Rationale,
                    point.TruckSensitive)
                {
                    OverlaySource = overlay.Descriptor,
                };
                merged[CanonicalPointKey(projected)] = projected;
            }
        }

        return Array.AsReadOnly(merged.Values
            .OrderBy(static point => point.SegmentId, StringComparer.Ordinal)
            .ThenBy(static point => point.DistanceAlongSegmentMeters)
            .ThenBy(static point => point.LaneNumber)
            .ThenBy(static point => point.Kind)
            .ToArray());
    }

    private static (
        string SegmentId,
        int LaneNumber,
        long DistanceMillimeters,
        LaneFrictionContributionKind Kind) CanonicalPointKey(
            CanonicalLaneFrictionPoint point)
        => (
            point.SegmentId,
            point.LaneNumber,
            checked((long)Math.Round(
                point.DistanceAlongSegmentMeters * 1_000d,
                MidpointRounding.AwayFromZero)),
            point.Kind);

    private static LaneTransitionDerivation CreateOverlayDerivation(
        LaneTopologySegment from,
        LaneTopologySegment to,
        CanonicalLaneTransitionOverlay transition,
        LaneTopologyOverlayDescriptor descriptor)
        => new(
            from.SegmentId,
            to.SegmentId,
            transition.Options,
            LaneTransitionProvenance.CanonicalOverlay,
            LaneTransitionConfidence.High,
            transition.ChangeKind,
            [
                new LaneTransitionEvidence(
                    LaneTransitionEvidenceKind.CanonicalOverlayDataset,
                    transition.FromCanonicalDirectedEdgeId,
                    transition.ToCanonicalDirectedEdgeId,
                    Array.Empty<LaneTurnIntent>(),
                    [
                        transition.FromCanonicalDirectedEdgeId,
                        transition.ToCanonicalDirectedEdgeId,
                    ]),
            ])
        {
            OverlaySource = descriptor,
            SourceRationale = transition.Rationale,
        };

    private static IReadOnlyList<CanonicalLaneFrictionPoint> RemoveRouteCompatibleExitOnlyPoints(
        IReadOnlyList<CanonicalLaneFrictionPoint> canonicalPoints,
        IReadOnlyList<LaneTopologySegment> topology,
        IReadOnlyList<IReadOnlyList<LaneTransitionOption>> transitions)
    {
        var routeCompatible = new HashSet<(string SegmentId, int LaneNumber)>();
        for (var index = 0; index < transitions.Count; index++)
        {
            foreach (LaneTransitionOption option in transitions[index])
            {
                routeCompatible.Add((topology[index].SegmentId, option.FromLane));
            }
        }

        LaneTopologySegment final = topology[^1];
        for (var lane = 1; lane <= final.LaneCount; lane++)
        {
            routeCompatible.Add((final.SegmentId, lane));
        }

        return canonicalPoints
            .Where(point =>
                point.Kind != LaneFrictionContributionKind.ExitOnlyLane ||
                !routeCompatible.Contains((point.SegmentId, point.LaneNumber)))
            .ToArray();
    }

    private static IReadOnlyList<RouteLaneFrictionModifier> BuildRouteModifiers(
        IReadOnlyList<RouteLaneSegment> routeSegments,
        IReadOnlyList<CanonicalLaneFrictionPoint> canonicalPoints)
    {
        var modifiers = new List<RouteLaneFrictionModifier>();
        foreach (RouteLaneSegment laneChange in routeSegments.Where(static segment =>
                     segment.EntryLane != segment.ExitLane))
        {
            int minimumLane = Math.Min(laneChange.EntryLane, laneChange.ExitLane);
            int maximumLane = Math.Max(laneChange.EntryLane, laneChange.ExitLane);
            CanonicalLaneFrictionPoint? mergePoint = canonicalPoints
                .Where(point =>
                    string.Equals(point.SegmentId, laneChange.SegmentId, StringComparison.Ordinal) &&
                    point.Kind == LaneFrictionContributionKind.AdjacentMerge &&
                    point.LaneNumber >= minimumLane &&
                    point.LaneNumber <= maximumLane)
                .OrderByDescending(static point => point.Severity)
                .ThenBy(static point => point.LaneNumber)
                .FirstOrDefault();
            if (mergePoint is null)
            {
                continue;
            }

            modifiers.Add(new RouteLaneFrictionModifier(
                laneChange.SegmentId,
                mergePoint.LaneNumber,
                mergePoint.DistanceAlongSegmentMeters,
                LaneFrictionContributionKind.Weave,
                Math.Max(8, mergePoint.Severity),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Route-specific lane change from lane {0} to lane {1} crosses a graph-derived merge at lane {2}.",
                    laneChange.EntryLane,
                    laneChange.ExitLane,
                    mergePoint.LaneNumber),
                TruckSensitive: true)
            {
                RouteSegmentOccurrenceIndex = laneChange.OccurrenceIndex,
                OverlaySource = mergePoint.OverlaySource,
            });
        }

        return Array.AsReadOnly(modifiers.ToArray());
    }

    private static IReadOnlyList<LaneTransitionOption>? SelectMinimumFrictionPath(
        IReadOnlyList<IReadOnlyList<LaneTransitionOption>> transitions,
        IReadOnlyList<LaneTopologySegment> topology,
        IReadOnlyList<CanonicalLaneFrictionPoint> canonicalPoints,
        LaneFrictionVehicleClass vehicleClass,
        LaneFrictionProjectionOptions options)
    {
        if (transitions.Count == 0)
        {
            return Array.Empty<LaneTransitionOption>();
        }

        var costs = new long[transitions.Count][];
        var previous = new int[transitions.Count][];
        costs[0] = transitions[0]
            .Select(option => (long)ScoreSegment(
                topology[0],
                canonicalPoints,
                option.FromLane,
                option.FromLane,
                vehicleClass))
            .ToArray();
        previous[0] = Enumerable.Repeat(-1, transitions[0].Count).ToArray();
        for (var transitionIndex = 1; transitionIndex < transitions.Count; transitionIndex++)
        {
            IReadOnlyList<LaneTransitionOption> current = transitions[transitionIndex];
            IReadOnlyList<LaneTransitionOption> prior = transitions[transitionIndex - 1];
            costs[transitionIndex] = Enumerable.Repeat(long.MaxValue, current.Count).ToArray();
            previous[transitionIndex] = Enumerable.Repeat(-1, current.Count).ToArray();
            for (var currentIndex = 0; currentIndex < current.Count; currentIndex++)
            {
                for (var priorIndex = 0; priorIndex < prior.Count; priorIndex++)
                {
                    if (costs[transitionIndex - 1][priorIndex] == long.MaxValue ||
                        !IsLaneChangeFeasible(
                            topology[transitionIndex],
                            prior[priorIndex].ToLane,
                            current[currentIndex].FromLane,
                            vehicleClass,
                            options))
                    {
                        continue;
                    }

                    long segmentScore = ScoreSegment(
                        topology[transitionIndex],
                        canonicalPoints,
                        prior[priorIndex].ToLane,
                        current[currentIndex].FromLane,
                        vehicleClass);
                    long cost = costs[transitionIndex - 1][priorIndex] + segmentScore;
                    if (cost < costs[transitionIndex][currentIndex])
                    {
                        costs[transitionIndex][currentIndex] = cost;
                        previous[transitionIndex][currentIndex] = priorIndex;
                    }
                }
            }
        }

        var totals = new long[transitions[^1].Count];
        for (var optionIndex = 0; optionIndex < transitions[^1].Count; optionIndex++)
        {
            LaneTransitionOption option = transitions[^1][optionIndex];
            totals[optionIndex] = costs[^1][optionIndex] == long.MaxValue
                ? long.MaxValue
                : costs[^1][optionIndex] + ScoreSegment(
                    topology[^1],
                    canonicalPoints,
                    option.ToLane,
                    option.ToLane,
                    vehicleClass);
        }

        long minimumTotal = totals.Min();
        if (minimumTotal == long.MaxValue)
        {
            return null;
        }

        int selectedIndex = Array.IndexOf(totals, minimumTotal);
        var selected = new LaneTransitionOption[transitions.Count];
        for (var transitionIndex = transitions.Count - 1; transitionIndex >= 0; transitionIndex--)
        {
            selected[transitionIndex] = transitions[transitionIndex][selectedIndex];
            selectedIndex = previous[transitionIndex][selectedIndex];
        }

        return Array.AsReadOnly(selected);
    }

    private static bool IsLaneChangeFeasible(
        LaneTopologySegment topology,
        int entryLane,
        int exitLane,
        LaneFrictionVehicleClass vehicleClass,
        LaneFrictionProjectionOptions options)
    {
        int laneChanges = Math.Abs(exitLane - entryLane);
        if (laneChanges == 0)
        {
            return true;
        }

        double minimumDistance = laneChanges * options.MinimumLaneChangeDistanceMeters(vehicleClass);
        return topology.LengthMeters >= minimumDistance;
    }

    private static int ScoreSegment(
        LaneTopologySegment topology,
        IReadOnlyList<CanonicalLaneFrictionPoint> canonicalPoints,
        int entryLane,
        int exitLane,
        LaneFrictionVehicleClass vehicleClass)
    {
        CanonicalLaneFrictionPoint[] points = canonicalPoints
            .Where(point => string.Equals(point.SegmentId, topology.SegmentId, StringComparison.Ordinal))
            .ToArray();
        LaneFrictionProfile profile = LaneFrictionAnalyzer.Analyze(new LaneFrictionRequest(
            points,
            [new RouteLaneSegment(topology.SegmentId, entryLane, exitLane, 0d)],
            vehicleClass));
        return profile.Score;
    }


}

public sealed record LaneFrictionProjectionOptions(
    double CarMinimumLaneChangeDistanceMeters = 50d,
    double TruckMinimumLaneChangeDistanceMeters = 90d)
{
    public static LaneFrictionProjectionOptions Default { get; } = new();

    internal double MinimumLaneChangeDistanceMeters(LaneFrictionVehicleClass vehicleClass)
        => vehicleClass == LaneFrictionVehicleClass.Truck
            ? TruckMinimumLaneChangeDistanceMeters
            : CarMinimumLaneChangeDistanceMeters;

    internal void Validate()
    {
        if (!double.IsFinite(CarMinimumLaneChangeDistanceMeters) ||
            CarMinimumLaneChangeDistanceMeters <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CarMinimumLaneChangeDistanceMeters),
                "Car minimum lane-change distance must be finite and positive.");
        }

        if (!double.IsFinite(TruckMinimumLaneChangeDistanceMeters) ||
            TruckMinimumLaneChangeDistanceMeters <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TruckMinimumLaneChangeDistanceMeters),
                "Truck minimum lane-change distance must be finite and positive.");
        }
    }
}

public enum LaneProjectionFailureReason
{
    None = 0,
    NoDirectedEdges = 1,
    MissingGraphEdges = 2,
    MissingLaneConnectivity = 3,
    InfeasibleLaneChanges = 4,
    CanonicalOverlayMismatch = 5,
}

public sealed record RouteLaneFrictionProjection(
    bool HasTopologyData,
    bool UsedFallbackConnectivity,
    IReadOnlyList<RouteLaneSegment> RouteSegments,
    IReadOnlyList<CanonicalLaneFrictionPoint> CanonicalPoints,
    LaneFrictionProfile Profile,
    IReadOnlyList<ulong> MissingDirectedEdgeIds)
{
    public static RouteLaneFrictionProjection Empty { get; } = CreateFailed(
        hasTopologyData: false,
        usedFallbackConnectivity: false,
        LaneProjectionFailureReason.NoDirectedEdges,
        []);

    public bool HasRouteLanePath => FailureReason == LaneProjectionFailureReason.None;

    public LaneProjectionFailureReason FailureReason { get; init; }

    public IReadOnlyList<RouteLaneFrictionModifier> RouteModifiers { get; init; } =
        Array.Empty<RouteLaneFrictionModifier>();

    public IReadOnlyList<LaneTransitionDerivation> TransitionDerivations { get; init; } =
        Array.Empty<LaneTransitionDerivation>();

    public IReadOnlyList<LaneTopologyOverlayDiagnostic> OverlayDiagnostics { get; init; } =
        Array.Empty<LaneTopologyOverlayDiagnostic>();

    internal static RouteLaneFrictionProjection CanonicalOverlayMismatch(
        IReadOnlyList<LaneTopologyOverlayDiagnostic> diagnostics)
        => CreateFailed(
            hasTopologyData: true,
            usedFallbackConnectivity: false,
            LaneProjectionFailureReason.CanonicalOverlayMismatch,
            []) with
        {
            OverlayDiagnostics = diagnostics,
        };

    internal static RouteLaneFrictionProjection Unavailable(IReadOnlyList<ulong> missing)
        => CreateFailed(
            hasTopologyData: false,
            usedFallbackConnectivity: false,
            LaneProjectionFailureReason.MissingGraphEdges,
            missing);

    internal static RouteLaneFrictionProjection UncertainConnectivity(
        IReadOnlyList<LaneTransitionDerivation> derivations,
        IReadOnlyList<CanonicalLaneFrictionPoint> canonicalPoints,
        LaneFrictionProfile overlayLowerBound)
        => CreateFailed(
            hasTopologyData: true,
            usedFallbackConnectivity: true,
            LaneProjectionFailureReason.MissingLaneConnectivity,
            []) with
        {
            CanonicalPoints = canonicalPoints,
            Profile = overlayLowerBound,
            TransitionDerivations = derivations,
        };

    internal static RouteLaneFrictionProjection InfeasibleLaneChanges()
        => CreateFailed(
            hasTopologyData: true,
            usedFallbackConnectivity: false,
            LaneProjectionFailureReason.InfeasibleLaneChanges,
            []);

    private static RouteLaneFrictionProjection CreateFailed(
        bool hasTopologyData,
        bool usedFallbackConnectivity,
        LaneProjectionFailureReason failureReason,
        IReadOnlyList<ulong> missing)
        => new(
            hasTopologyData,
            usedFallbackConnectivity,
            Array.Empty<RouteLaneSegment>(),
            Array.Empty<CanonicalLaneFrictionPoint>(),
            new LaneFrictionProfile(
                0,
                0,
                0,
                0,
                Array.Empty<LaneFrictionContribution>(),
                Array.Empty<LaneGuidancePoint>()),
            missing)
        {
            FailureReason = failureReason,
        };
}
