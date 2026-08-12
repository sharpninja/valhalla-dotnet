// Faithful C# port of Valhalla mjolnir graphvalidator.cc + graphvalidator.h @ 3.7.0.
// Sources:
//   F:/github/valhalla/src/mjolnir/graphvalidator.cc  (671 LOC)
//   F:/github/valhalla/valhalla/mjolnir/graphvalidator.h
//
// GraphValidator is the final stage of build_tile_set (kValidate). It walks every tile (at all
// hierarchy levels) through a GraphReader and, for each directed edge:
//   - finds and sets the opposing-edge index at the edge's end node (GetOpposingEdgeIndex),
//   - sets the leaves_tile flag (whether the end node is in a different tile),
//   - sets the deadend / internal flags (the deadend flag comes from the end node, the internal flag
//     is inherited from a matching internal opposing edge),
//   - marks country crossings (begin/end node ISO mismatch),
//   - re-validates the complex restriction start/end modes against the actual stored restrictions,
//   - accumulates the road length per tile to compute and stamp the relative road density into the
//     tile header.
// The opposing-edge indexes + densities are exactly the data the bidirectional A* (thor) relies on:
// GraphReader.GetOpposingEdgeId walks de.OppIndex, and GetEdgeDensity reads node.Density. The updated
// nodes + directed edges are written back through the deserialize/Update path of GraphTileBuilder,
// producing byte-identical tiles the Baldr GraphTile reader parses.
//
// PORT-NOTES / OMISSIONS (consistent with the established mjolnir port scope):
//   - THREADING: validation supports bounded worker-local readers after the tile set and dataset
//     identity are frozen. Requested concurrency is capped at the profiled degree of four because a
//     Nashville DOP-16 probe exposed cross-tile read and binning contention. Worker caches divide the
//     configured reader budget and read the frozen source graph while each worker writes only to a
//     private validation tree. Per-worker results are merged deterministically; validated-tile
//     publication, tweener writes, and checksums remain behind explicit barriers. The legacy overload
//     stays serial.
//   - EDGE BINNING (tweeners / GraphTileBuilder::BinEdges / AddBins): NOT ported. Edge bins are the
//     spatial index used by loki edge-search-by-bin; this port snaps via the ported loki
//     ClosestFirstGenerator over the tile node/edge geometry, not the bins, so the bin section and
//     the tweener cross-tile bin merge are excluded (matching GraphTileBuilder, which never writes a
//     bin section). The opposing-index / density / connectivity validation that the router needs is
//     reproduced in full.
//   - TRANSIT: the transit level + transit-line / transit-connection / egress / platform opposing-edge
//     matching branches are preserved structurally but never fire in the auto/truck graph (transit is
//     excluded upstream), exactly like the rest of the mjolnir port.
//   - The SCOPED_TIMER / LOG_* diagnostics collapse into the returned ValidatorStats; the duplicate
//     and density bookkeeping the C++ logs is surfaced on the stats object so tests can assert it.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Class used to validate the graph. Creates opposing edge indexes (an excellent way to validate
/// proper connectivity), sets the deadend / internal / leaves-tile / country-crossing flags,
/// re-validates complex restriction modes, and stamps the relative road density into each tile.
/// Faithful port of the C++ <c>class GraphValidator</c> plus the graphvalidator.cc free functions.
/// </summary>
public static class GraphValidator
{
    private const int MaximumProfiledParallelValidationDegree = 4;

    // Custom comparator to sort by GraphId (level desc, tile_id asc, id asc). Faithful port of the
    // anonymous-namespace graphid_less (used by the excluded bin sort; reproduced for completeness).
    internal static bool GraphIdLess(GraphId a, GraphId b)
        => (a.Level() > b.Level()) ||
           (a.Level() == b.Level() &&
            (a.Tileid() < b.Tileid() || (a.Tileid() == b.Tileid() && a.Id() < b.Id())));

    /// <summary>
    /// Per-run statistics surfaced from the (otherwise logged) C++ validator bookkeeping: the
    /// per-level possible-duplicate counts and the per-level tile densities, plus a tile count.
    /// </summary>
    public sealed class ValidatorStats
    {
        private readonly Dictionary<string, TimeSpan> stageDurations =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, TimeSpan> tileStageDurations =
            new(StringComparer.Ordinal)
            {
                ["deserialize"] = TimeSpan.Zero,
                ["edges"] = TimeSpan.Zero,
                ["binning"] = TimeSpan.Zero,
                ["update"] = TimeSpan.Zero,
                ["add-bins"] = TimeSpan.Zero,
            };

        /// <summary>Number of tiles validated (all levels).</summary>
        public int TileCount { get; set; }

        /// <summary>Elapsed wall time for each validation sub-stage.</summary>
        public IReadOnlyDictionary<string, TimeSpan> StageDurations => stageDurations;

        /// <summary>Aggregate elapsed wall time for the measured per-tile validation stages.</summary>
        public IReadOnlyDictionary<string, TimeSpan> TileStageDurations => tileStageDurations;

        internal void RecordStageDuration(string stage, TimeSpan duration) =>
            stageDurations[stage] = duration;

        internal void AddTileStageDuration(string stage, TimeSpan duration) =>
            tileStageDurations[stage] += duration;

        /// <summary>Possible duplicate-edge counts per hierarchy level (index = level).</summary>
        public uint[] Duplicates { get; } = new uint[TileHierarchy.GetTransitLevel().Level + 1];

        /// <summary>Tile road densities (km/km^2) per hierarchy level (index = level).</summary>
        public List<float>[] Densities { get; } =
            CreateLevelLists(TileHierarchy.GetTransitLevel().Level + 1);

        private static List<float>[] CreateLevelLists(int count)
        {
            var arr = new List<float>[count];
            for (int i = 0; i < count; i++)
            {
                arr[i] = new List<float>();
            }

            return arr;
        }
    }

    /// <summary>
    /// Validates the graph tiles in <paramref name="tileDir"/>. Faithful port of
    /// <c>GraphValidator::Validate(const boost::property_tree::ptree&amp; pt)</c>. Reads each tile (at
    /// every level) through a GraphReader, sets opposing-edge indexes / densities / connectivity flags
    /// and rewrites the tiles in place.
    /// </summary>
    /// <param name="config">Reader configuration (tile dir + cache knobs).</param>
    /// <returns>Per-level duplicate / density statistics.</returns>
    public static ValidatorStats Validate(GraphReader.Config config) =>
        Validate(config, CancellationToken.None);

    /// <summary>
    /// Validates graph tiles while honoring cancellation between tiles, nodes, directed edges,
    /// cross-tile bin writes, and final checksum passes.
    /// </summary>
    public static ValidatorStats Validate(
        GraphReader.Config config,
        CancellationToken cancellationToken) =>
        Validate(
            config,
            maxDegreeOfParallelism: 1,
            cancellationToken);

    /// <summary>
    /// Validates graph tiles with bounded worker-local readers and deterministic result merging.
    /// </summary>
    public static ValidatorStats Validate(
        GraphReader.Config config,
        int maxDegreeOfParallelism,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        int effectiveDegree = ResolveParallelValidationDegree(
            maxDegreeOfParallelism);
        return effectiveDegree == 1
            ? Validate(new GraphReader(config), config.TileDir, cancellationToken)
            : ValidateParallel(
                config,
                effectiveDegree,
                cancellationToken);
    }

    /// <summary>
    /// Validates the graph tiles using the supplied reader and tile directory. Faithful port of
    /// <c>GraphValidator::Validate</c> (single-threaded; see file header).
    /// </summary>
    /// <param name="reader">Reader over the tile directory being validated.</param>
    /// <param name="tileDir">Tile directory the updated tiles are rewritten to.</param>
    /// <returns>Per-level duplicate / density statistics.</returns>
    public static ValidatorStats Validate(GraphReader reader, string tileDir) =>
        Validate(reader, tileDir, CancellationToken.None);

    /// <summary>
    /// Validates graph tiles through an existing reader with bounded cancellation checks.
    /// </summary>
    public static ValidatorStats Validate(
        GraphReader reader,
        string tileDir,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrEmpty(tileDir);
        cancellationToken.ThrowIfCancellationRequested();

        var stats = new ValidatorStats();
        var stopwatch = Stopwatch.StartNew();
        byte transitLevel = TileHierarchy.GetTransitLevel().Level;

        // Vector to hold problem ways (TODO in C++ - "could be a useful list").
        var problemWays = new HashSet<ulong>();

        // Create the queue of tiles (at all levels). The C++ shuffles with a fixed seed for a
        // reproducible threaded build; the serial port processes in a deterministic GraphId order
        // (the per-tile work is order-independent, so the resulting tiles are identical).
        var tileSet = new List<GraphId>(reader.GetTileSet());
        tileSet.Sort((a, b) => a.Value.CompareTo(b.Value));
        stats.TileCount = tileSet.Count;

        // Match Valhalla 3.8.3: tween-only tiles inherit the generation's dataset identity from the
        // first existing tile instead of publishing a default zero identity.
        ulong datasetId = 0;
        if (tileSet.Count > 0)
        {
            GraphTile? firstTile = reader.GetGraphTile(tileSet[0]);
            if (firstTile is null)
            {
                throw new InvalidDataException(
                    "The graph tile set contains an unreadable first tile.");
            }

            datasetId = firstTile.Header().DatasetId();
        }

        // Accumulator for edges that pass through tiles they neither start nor end in. Faithful
        // port of the tweeners_t accumulated across the per-tile validate() workers.
        var tweeners = new EdgeBinner.Tweeners();

        foreach (GraphId tileId in tileSet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateTile(
                reader,
                tileDir,
                tileId,
                transitLevel,
                problemWays,
                stats,
                tweeners,
                cancellationToken);

            // Check if we need to clear the tile cache.
            if (reader.OverCommitted())
            {
                reader.Trim();
            }
        }

        stopwatch.Stop();
        stats.RecordStageDuration("tiles", stopwatch.Elapsed);
        stopwatch.Restart();

        // Run a pass to add the edges that binned to tweener tiles (faithful port of bin_tweeners).
        // The reader cache is dropped first so AddBins observes the just-rewritten local tiles.
        reader.Trim();
        byte localLevel = TileHierarchy.Levels()[^1].Level;
        foreach (KeyValuePair<ulong, List<EdgeBinner.BinEntry>[]> tw in tweeners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tweenTileId = new GraphId(tw.Key);
            GraphTile? tile = GraphTile.Create(tileDir, tweenTileId);
            if (tile is null)
            {
                // Some tiles only exist because an edge's shape passes through them (no nodes/edges);
                // create an empty tile to hold the spatial index. Faithful port of bin_tweeners.
                var empty = new GraphTileBuilder(tweenTileId);
                empty.HeaderBuilder.SetDatasetId(datasetId);
                empty.StoreTileData(tileDir);
                tile = GraphTile.Create(tileDir, tweenTileId);
                if (tile is null)
                {
                    continue;
                }
            }

            // Only the local (highest) level carries bins.
            if (tile.Id().Level() != localLevel)
            {
                continue;
            }

            EdgeBinner.SortBins(tw.Value);
            EdgeBinner.AddBins(tileDir, tile, tw.Value);
        }

        stopwatch.Stop();
        stats.RecordStageDuration("tweeners", stopwatch.Elapsed);
        stopwatch.Restart();

        GraphTileChecksum.RefreshTilesetFiles(tileDir, cancellationToken);
        stopwatch.Stop();
        stats.RecordStageDuration("checksums", stopwatch.Elapsed);
        return stats;
    }

    internal static int ResolveParallelValidationDegree(
        int requestedDegree)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedDegree);
        return Math.Min(
            requestedDegree,
            MaximumProfiledParallelValidationDegree);
    }

    internal static GraphReader.Config CreateParallelWorkerConfig(
        GraphReader.Config config,
        int maxDegreeOfParallelism)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDegreeOfParallelism);
        return new GraphReader.Config
        {
            TileDir = config.TileDir,
            MaxCacheSize = Math.Max(
                1,
                config.MaxCacheSize / maxDegreeOfParallelism),
            UseLruMemCache = config.UseLruMemCache,
            LruMemCacheHardControl = config.LruMemCacheHardControl,
            UseSimpleMemCache = config.UseSimpleMemCache,
            GlobalSynchronizedCache = config.GlobalSynchronizedCache,
            MaxConcurrentReaderUsers = config.MaxConcurrentReaderUsers,
            TrafficSnapshot = config.TrafficSnapshot,
        };
    }

    private static ValidatorStats ValidateParallel(
        GraphReader.Config config,
        int maxDegreeOfParallelism,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string validationOutputDirectory = CreateParallelValidationOutputDirectory(
            config.TileDir);
        try
        {
            var stats = new ValidatorStats();
            var stopwatch = Stopwatch.StartNew();
            byte transitLevel = TileHierarchy.GetTransitLevel().Level;
            GraphReader.Config workerConfig = CreateParallelWorkerConfig(
                config,
                maxDegreeOfParallelism);
            var discoveryReader = new GraphReader(workerConfig);
            var tileSet = new List<GraphId>(discoveryReader.GetTileSet());
            tileSet.Sort((a, b) => a.Value.CompareTo(b.Value));
            stats.TileCount = tileSet.Count;

            ulong datasetId = 0;
            if (tileSet.Count > 0)
            {
                GraphTile? firstTile = discoveryReader.GetGraphTile(tileSet[0]);
                if (firstTile is null)
                {
                    throw new InvalidDataException(
                        "The graph tile set contains an unreadable first tile.");
                }

                datasetId = firstTile.Header().DatasetId();
            }

            discoveryReader.Clear();
            var completedWorkers = new ConcurrentBag<ValidationWorker>();
            Parallel.ForEach(
                tileSet,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = maxDegreeOfParallelism,
                },
                () => new ValidationWorker(workerConfig),
                (tileId, _, worker) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateTile(
                        worker.Reader,
                        validationOutputDirectory,
                        tileId,
                        transitLevel,
                        worker.ProblemWays,
                        worker.Stats,
                        worker.Tweeners,
                        cancellationToken);
                    if (worker.Reader.OverCommitted())
                    {
                        worker.Reader.Trim();
                    }

                    return worker;
                },
                completedWorkers.Add);

            var tweeners = new EdgeBinner.Tweeners();
            foreach (ValidationWorker worker in completedWorkers)
            {
                MergeWorkerStats(stats, worker.Stats);
                MergeTweeners(tweeners, worker.Tweeners);
                worker.Reader.Trim();
            }

            PublishValidatedTiles(
                validationOutputDirectory,
                config.TileDir,
                cancellationToken);

            for (var level = 0; level < stats.Densities.Length; level++)
            {
                stats.Densities[level].Sort();
            }

            stopwatch.Stop();
            stats.RecordStageDuration("tiles", stopwatch.Elapsed);
            stopwatch.Restart();

            discoveryReader.Trim();
            byte localLevel = TileHierarchy.Levels()[^1].Level;
            foreach (KeyValuePair<ulong, List<EdgeBinner.BinEntry>[]> tw in tweeners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tweenTileId = new GraphId(tw.Key);
                GraphTile? tile = GraphTile.Create(config.TileDir, tweenTileId);
                if (tile is null)
                {
                    var empty = new GraphTileBuilder(tweenTileId);
                    empty.HeaderBuilder.SetDatasetId(datasetId);
                    empty.StoreTileData(config.TileDir);
                    tile = GraphTile.Create(config.TileDir, tweenTileId);
                    if (tile is null)
                    {
                        continue;
                    }
                }

                if (tile.Id().Level() != localLevel)
                {
                    continue;
                }

                EdgeBinner.SortBins(tw.Value);
                EdgeBinner.AddBins(config.TileDir, tile, tw.Value);
            }

            stopwatch.Stop();
            stats.RecordStageDuration("tweeners", stopwatch.Elapsed);
            stopwatch.Restart();

            GraphTileChecksum.RefreshTilesetFiles(config.TileDir, cancellationToken);
            stopwatch.Stop();
            stats.RecordStageDuration("checksums", stopwatch.Elapsed);
            return stats;
        }
        finally
        {
            DeleteParallelValidationOutputDirectory(validationOutputDirectory);
        }
    }

    private static string CreateParallelValidationOutputDirectory(string tileDirectory)
    {
        string fullTileDirectory = Path.GetFullPath(tileDirectory);
        string? parentDirectory = Path.GetDirectoryName(fullTileDirectory);
        if (parentDirectory is null)
        {
            throw new InvalidDataException("The graph tile directory has no parent directory.");
        }

        string directoryName = Path.GetFileName(fullTileDirectory);
        string outputDirectory = Path.Combine(
            parentDirectory,
            $".{directoryName}.validation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        return outputDirectory;
    }

    private static void PublishValidatedTiles(
        string validationOutputDirectory,
        string tileDirectory,
        CancellationToken cancellationToken)
    {
        string[] validatedTilePaths = Directory
            .EnumerateFiles(validationOutputDirectory, "*.gph", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (string validatedTilePath in validatedTilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = Path.GetRelativePath(
                validationOutputDirectory,
                validatedTilePath);
            string targetPath = Path.Combine(tileDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Move(validatedTilePath, targetPath, overwrite: true);
        }
    }

    private static void DeleteParallelValidationOutputDirectory(string outputDirectory)
    {
        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static void MergeWorkerStats(
        ValidatorStats destination,
        ValidatorStats source)
    {
        for (var level = 0; level < destination.Duplicates.Length; level++)
        {
            destination.Duplicates[level] = checked(
                destination.Duplicates[level] + source.Duplicates[level]);
            destination.Densities[level].AddRange(source.Densities[level]);
        }

        foreach ((string stage, TimeSpan duration) in source.TileStageDurations)
        {
            destination.AddTileStageDuration(stage, duration);
        }
    }

    private static void MergeTweeners(
        EdgeBinner.Tweeners destination,
        EdgeBinner.Tweeners source)
    {
        foreach ((ulong tileId, List<EdgeBinner.BinEntry>[] sourceBins) in source)
        {
            if (!destination.TryGetValue(
                    tileId,
                    out List<EdgeBinner.BinEntry>[]? destinationBins))
            {
                destinationBins = new List<EdgeBinner.BinEntry>[sourceBins.Length];
                for (var bin = 0; bin < destinationBins.Length; bin++)
                {
                    destinationBins[bin] = new List<EdgeBinner.BinEntry>();
                }

                destination.Add(tileId, destinationBins);
            }

            for (var bin = 0; bin < sourceBins.Length; bin++)
            {
                destinationBins[bin].AddRange(sourceBins[bin]);
            }
        }
    }

    private sealed class ValidationWorker
    {
        internal ValidationWorker(GraphReader.Config config)
        {
            Reader = new GraphReader(config);
        }

        internal GraphReader Reader { get; }

        internal HashSet<ulong> ProblemWays { get; } = [];

        internal ValidatorStats Stats { get; } = new();

        internal EdgeBinner.Tweeners Tweeners { get; } = new();
    }

    // Faithful port of the per-tile body of the C++ validate() worker loop.
    private static void ValidateTile(
        GraphReader reader,
        string tileDir,
        GraphId tileId,
        byte transitLevel,
        HashSet<ulong> problemWays,
        ValidatorStats stats,
        EdgeBinner.Tweeners tweeners,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Point tiles to the set we need for current level.
        Tiles<PointLL, double> tiles = tileId.Level() == transitLevel
            ? TileHierarchy.GetTransitLevel().Tiles
            : TileHierarchy.Levels()[(int)tileId.Level()].Tiles;
        byte level = (byte)tileId.Level();
        uint tileid = tileId.Tileid();

        // Get this tile (read-only view through the reader).
        GraphTile? tile = reader.GetGraphTile(tileId);
        if (tile is null)
        {
            return;
        }

        // Get the tile builder (deserialize so it can be modified + re-stored).
        long tileStageStart = Stopwatch.GetTimestamp();
        var tilebuilder = new GraphTileBuilder(tile);
        stats.AddTileStageDuration("deserialize", Stopwatch.GetElapsedTime(tileStageStart));

        tileStageStart = Stopwatch.GetTimestamp();
        // Update nodes and directed edges as needed.
        var nodes = new List<NodeInfo>();
        var directededges = new List<DirectedEdge>();

        // Iterate through the nodes and the directed edges.
        uint dupcount = 0;
        float roadlength = 0.0f;
        uint nodecount = tilebuilder.Header().Nodecount();
        GraphId node = tileId;
        for (uint i = 0; i < nodecount; i++, node += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The node we will modify.
            NodeInfo nodeinfo = tilebuilder.Node((int)i);
            NodeInfo ni = tile.Node((int)i);

            // Validate signs.
            if (ni.NamedIntersection)
            {
                if (tile.GetSigns(i, true).Count == 0)
                {
                    // LOG_ERROR("Node marked as having signs but none found");
                }
            }

            ushort beginNodeIso = tile.Admin((int)nodeinfo.AdminIndex).CountryIsoCodeValue;

            // Go through directed edges and validate/update data.
            uint idx = ni.EdgeIndex;
            var edgeid = new GraphId(node.Tileid(), node.Level(), idx);
            for (uint j = 0, n = nodeinfo.EdgeCount; j < n; j++, idx++, edgeid += 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DirectedEdge de = tile.DirectedEdge((int)idx);

                // Validate signs.
                if (de.Sign)
                {
                    if (tile.GetSigns(idx).Count == 0)
                    {
                        // LOG_ERROR("Directed edge marked as having signs but none found");
                    }
                }

                // Validate lane connectivity.
                if (de.LaneConnectivity)
                {
                    if (tile.GetLaneConnectivity(idx).Count == 0)
                    {
                        // LOG_ERROR("Directed edge marked as having lane connectivity but none found");
                    }
                }

                // Validate access restrictions. TODO - should check modes as well.
                uint arModes = (uint)de.AccessRestriction;
                if (arModes != 0)
                {
                    // since only truck restrictions exist, we can still get all restrictions.
                    (IReadOnlyList<AccessRestriction> res, _) = tile.GetAccessRestrictions(idx);
                    if (res.Count == 0)
                    {
                        // LOG_ERROR("Directed edge marked as having access restriction but none found");
                    }
                    else
                    {
                        foreach (AccessRestriction r in res)
                        {
                            if (r.EdgeIndex() != idx)
                            {
                                // LOG_ERROR("Access restriction edge index does not match idx");
                            }
                        }
                    }
                }

                // The edge we will modify.
                DirectedEdge directededge = tilebuilder.DirectedEdgeBuilder((int)(nodeinfo.EdgeIndex + j));

                // Road Length and some variables for statistics.
                if (!directededge.IsShortcut)
                {
                    roadlength += directededge.Length;
                }

                // Check if end node is in a different tile.
                GraphTile? endnodeTile = tile;
                if (tileId.Value != directededge.EndNode.TileBase().Value)
                {
                    directededge.SetLeavesTile(true);

                    // Get the end node tile.
                    endnodeTile = reader.GetGraphTile(directededge.EndNode);
                }
                else
                {
                    // make sure this is set to false as access tag logic could have set this to true.
                    directededge.SetLeavesTile(false);
                }

                // Set the opposing edge index and get the country ISO at the end node. Set the deadend
                // flag and internal flag (if the opposing edge is internal then make sure this edge is
                // as well).
                ulong wayid = tile.EdgeInfoWayId(directededge);
                uint oppIndex = GetOpposingEdgeIndex(
                    node, ref directededge, wayid, tile, endnodeTile, problemWays,
                    ref dupcount, out ushort endNodeIso, transitLevel);
                directededge.SetOppIndex(oppIndex);
                if (directededge.Use == Use.TransitConnection ||
                    directededge.Use == Use.EgressConnection ||
                    directededge.Use == Use.PlatformConnection || directededge.BssConnection)
                {
                    directededge.SetOppLocalIdx(oppIndex);
                }

                // Mark a country crossing if country ISO codes do not match.
                if (beginNodeIso != 0 && endNodeIso != 0 && beginNodeIso != endNodeIso)
                {
                    directededge.SetCtryCrossing(true);
                }

                // Validate the complex restriction settings. If no restrictions are found that end at
                // this directed edge, set the end restriction modes to 0.
                if (de.EndRestriction != 0)
                {
                    uint modes = 0;
                    for (uint mode = 1; mode < GraphConstants.AllAccess; mode *= 2)
                    {
                        if ((de.EndRestriction & mode) != 0 &&
                            !tile.GetComplexRestrictions(true, edgeid, mode).Empty())
                        {
                            modes |= mode;
                        }
                    }

                    directededge.SetEndRestriction(modes);
                }

                if (de.StartRestriction != 0)
                {
                    uint modes = 0;
                    for (uint mode = 1; mode < GraphConstants.AllAccess; mode *= 2)
                    {
                        if ((de.StartRestriction & mode) != 0 &&
                            !tile.GetComplexRestrictions(false, edgeid, mode).Empty())
                        {
                            modes |= mode;
                        }
                    }

                    directededge.SetStartRestriction(modes);
                }

                // Add the directed edge to the local list.
                directededges.Add(directededge);
            }

            // Add the node to the list.
            nodes.Add(nodeinfo);
        }

        // Add density to return class. Approximate the tile area square km.
        Aabb2T<double> bb = tiles.TileBounds((int)tileid);
        double area = ((bb.Maxy - bb.Miny) * Constants.MetersPerDegreeLat * Constants.KmPerMeter) *
                      ((bb.Maxx - bb.Minx) *
                       DistanceApproximator<PointLL, double>.MetersPerLngDegree(bb.Center().Y) * Constants.KmPerMeter);
        float density = (float)((roadlength * 0.0005f) / area);
        stats.Densities[level].Add(density);

        // Set the relative road density within this tile.
        uint relativeDensity;
        if (tileId.Level() == 0)
        {
            relativeDensity = (uint)(density * 100.0f);
        }
        else if (tileId.Level() == 1)
        {
            relativeDensity = (uint)(density * 20.0f);
        }
        else
        {
            relativeDensity = (uint)(density * 2.0f);
        }

        tilebuilder.HeaderBuilder.SetDensity(relativeDensity);
        stats.AddTileStageDuration("edges", Stopwatch.GetElapsedTime(tileStageStart));

        // Bin the edges (compute this tile's own bins + accumulate cross-tile tweeners) BEFORE the
        // tile is rewritten, while the original shapes/edges are still readable. Faithful port of
        // GraphTileBuilder::BinEdges in graphvalidator.cc.
        tileStageStart = Stopwatch.GetTimestamp();
        List<EdgeBinner.BinEntry>[] bins = EdgeBinner.BinEdges(tile, tweeners);
        stats.AddTileStageDuration("binning", Stopwatch.GetElapsedTime(tileStageStart));

        // Write the new tile.
        tileStageStart = Stopwatch.GetTimestamp();
        tilebuilder.Update(tileDir, nodes, directededges);
        stats.AddTileStageDuration("update", Stopwatch.GetElapsedTime(tileStageStart));

        tileStageStart = Stopwatch.GetTimestamp();
        // Write this tile's own bins to it (only the local / highest level carries bins). Reload the
        // just-written tile and append the bins, sorting each bin for deterministic output. Faithful
        // port of the "Write the bins to it" block in graphvalidator.cc.
        if (tileId.Level() == TileHierarchy.Levels()[^1].Level)
        {
            GraphTile? reloaded = GraphTile.Create(tileDir, tileId);
            if (reloaded is not null)
            {
                EdgeBinner.SortBins(bins);
                EdgeBinner.AddBins(tileDir, reloaded, bins);
            }
        }

        stats.AddTileStageDuration("add-bins", Stopwatch.GetElapsedTime(tileStageStart));

        // Add possible duplicates to return class.
        stats.Duplicates[level] += dupcount;
    }

    /// <summary>
    /// Gets the index of the opposing directed edge at <paramref name="edge"/>'s end node and sets
    /// the deadend / internal flags on <paramref name="edge"/>; also returns the end node's country
    /// ISO. Faithful port of the anonymous-namespace <c>GetOpposingEdgeIndex</c>.
    /// </summary>
    /// <param name="startnode">The start node of <paramref name="edge"/>.</param>
    /// <param name="edge">The directed edge (modified: deadend / internal flags).</param>
    /// <param name="wayid">The OSM way id of <paramref name="edge"/>.</param>
    /// <param name="tile">The tile that owns <paramref name="edge"/>.</param>
    /// <param name="endTile">The tile that owns the end node (may equal <paramref name="tile"/>).</param>
    /// <param name="problemWays">Accumulator of way ids with potential duplicate edges.</param>
    /// <param name="dupcount">Running count of potential duplicates (incremented in place).</param>
    /// <param name="endnodeiso">Out: the end node's country ISO (empty if none / no connections).</param>
    /// <param name="transitLevel">The transit hierarchy level.</param>
    /// <returns>The opposing local-edge index, or kMaxEdgesPerNode if no match was found.</returns>
    internal static uint GetOpposingEdgeIndex(
        GraphId startnode,
        ref DirectedEdge edge,
        ulong wayid,
        GraphTile tile,
        GraphTile? endTile,
        HashSet<ulong> problemWays,
        ref uint dupcount,
        out ushort endnodeiso,
        byte transitLevel)
    {
        endnodeiso = 0;
        if (endTile is null)
        {
            // LOG_WARN("End tile invalid.");
            return NodeInfo.MaxEdgesPerNode;
        }

        // Get the tile at the end node and get the node info.
        GraphId endnode = edge.EndNode;
        NodeInfo nodeinfo = endTile.Node((int)endnode.Id());
        bool sametile = startnode.Tileid() == endnode.Tileid();

        // The following can happen for transit nodes that do not connect to osm data and have no
        // transit lines. This can happen when we are using a subset of transit data.
        if (nodeinfo.EdgeCount == 0)
        {
            // LOG_DEBUG("End node has no connections ...");
            return NodeInfo.MaxEdgesPerNode;
        }

        // Set the end node iso. Used for country crossings.
        endnodeiso = endTile.Admin((int)nodeinfo.AdminIndex).CountryIsoCodeValue;

        // Set the deadend flag on the edge.
        bool deadend = nodeinfo.Intersection == IntersectionType.DeadEnd;
        edge.SetDeadend(deadend);

        // Get the directed edges and return when the end node matches the specified node and length /
        // wayId, shape, use, and/or transit attributes matches. Check for duplicates.
        const uint absurdIndex = 777777;
        uint oppIndex = absurdIndex;
        for (uint i = 0; i < nodeinfo.EdgeCount; i++)
        {
            DirectedEdge directededge = endTile.DirectedEdge((int)(nodeinfo.EdgeIndex + i));

            // Reject edge if access does not match or the edge does not point back to the startnode.
            if (directededge.EndNode.Value != startnode.Value ||
                edge.ForwardAccess != directededge.ReverseAccess ||
                edge.ReverseAccess != directededge.ForwardAccess)
            {
                continue;
            }

            // Transit connections. Match opposing edge if same way Id.
            if (edge.Use == Use.TransitConnection && directededge.Use == Use.TransitConnection &&
                wayid == endTile.EdgeInfoWayId(directededge))
            {
                oppIndex = i;
                continue;
            }

            if (edge.Use == Use.TransitConnection || directededge.Use == Use.TransitConnection)
            {
                continue;
            }

            if ((edge.Use == Use.PlatformConnection && directededge.Use == Use.PlatformConnection) ||
                (edge.Use == Use.EgressConnection && directededge.Use == Use.EgressConnection))
            {
                IReadOnlyList<PointLL> shape1 = tile.EdgeInfo(edge).Shape();
                IReadOnlyList<PointLL> shape2 = endTile.EdgeInfo(directededge).Shape();
                if (MjolnirUtil.ShapesMatch(shape1, shape2))
                {
                    oppIndex = i;
                    continue;
                }
            }

            // After this point should just have regular edges, shortcut edges, and transit lines.
            if (startnode.Level() == transitLevel)
            {
                // Transit level - handle transit lines.
                if (edge.IsTransitLine && directededge.IsTransitLine)
                {
                    // For a transit edge the line Id must match.
                    if (edge.LineId == directededge.LineId)
                    {
                        if (oppIndex != absurdIndex)
                        {
                            // LOG_ERROR("Multiple transit edges have the same line Id ...");
                            dupcount++;
                        }

                        oppIndex = i;
                    }
                }
            }
            else
            {
                // Regular edges and shortcut edges remain. Lengths and shortcut flag must match.
                if (edge.Length != directededge.Length ||
                    edge.IsShortcut != directededge.IsShortcut)
                {
                    continue;
                }

                bool match = false;
                ulong wayid2 = 0;
                if (edge.IsShortcut)
                {
                    // Shortcut edges - use must match (or both are links).
                    if ((directededge.Link && edge.Link) || directededge.Use == edge.Use)
                    {
                        match = true;
                    }
                }
                else
                {
                    // Regular edges - match wayids and edge info offset (if in same tile) or shape (if
                    // not in same tile).
                    wayid2 = endTile.EdgeInfoWayId(directededge);
                    if (wayid == wayid2)
                    {
                        if (sametile && edge.EdgeInfoOffset == directededge.EdgeInfoOffset)
                        {
                            match = true;
                        }
                        else
                        {
                            IReadOnlyList<PointLL> shape1 = tile.EdgeInfo(edge).Shape();
                            IReadOnlyList<PointLL> shape2 = endTile.EdgeInfo(directededge).Shape();
                            if (MjolnirUtil.ShapesMatch(shape1, shape2))
                            {
                                match = true;
                            }
                        }
                    }
                }

                // Set opposing index if match found.
                if (match)
                {
                    // Check if multiple edges match - log any duplicates.
                    if (oppIndex != absurdIndex && startnode.Level() != transitLevel)
                    {
                        if (!edge.IsShortcut)
                        {
                            // LOG_DEBUG("Potential duplicate: wayids ...");
                            problemWays.Add(wayid);
                            problemWays.Add(wayid2);
                        }

                        dupcount++;
                    }

                    // Set the internal intersection flag if matching opposing edge is marked as an
                    // internal intersection edge.
                    if (directededge.Internal)
                    {
                        edge.SetInternal(true);
                    }

                    oppIndex = i;
                }
            }
        }

        // No matching opposing edge found - log error cases.
        if (oppIndex == absurdIndex)
        {
            // The diagnostic-only logging branches (transit/egress/platform, transit line, regular
            // edge "No opposing edge ...") collapse here; no opposing index could be assigned.
            return NodeInfo.MaxEdgesPerNode;
        }

        return oppIndex;
    }
}
