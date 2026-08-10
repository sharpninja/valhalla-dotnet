// Faithful C# port of the Valhalla mjolnir build orchestration (build_tile_set).
// Sources:
//   F:/github/valhalla/src/mjolnir/util.cc      (build_tile_set, the BuildStage pipeline @ ~line 631)
//   F:/github/valhalla/valhalla/mjolnir/util.h  (BuildStage enum, build_tile_set declaration)
//
// build_tile_set runs the tile-building pipeline as an ordered sequence of BuildStage steps:
//   kInitialize    - purge / create the tile directory
//   kParseWays     - PBFGraphParser::ParseWays
//   kParseRelations- PBFGraphParser::ParseRelations
//   kParseNodes    - PBFGraphParser::ParseNodes
//   kConstructEdges- GraphBuilder::BuildEdges (+ tile manifest)
//   kBuild         - GraphBuilder::Build
//   kEnhance       - GraphEnhancer::Enhance
//   kFilter        - GraphFilter::Filter
//   kTransit       - TransitBuilder::Build              (EXCLUDED - transit out of scope)
//   kBss           - BssBuilder::Build                  (EXCLUDED - bike-share out of scope)
//   kHierarchy     - HierarchyBuilder::Build            (only if mjolnir.hierarchy == true)
//   kShortcuts     - ShortcutBuilder::Build             (only if hierarchy && mjolnir.shortcuts == true)
//   kElevation     - ElevationBuilder::Build            (EXCLUDED - elevation out of scope)
//   kRestrictions  - RestrictionBuilder::Build
//   kValidate      - GraphValidator::Validate           (sets opposing indices/densities/connectivity)
//   kCleanup       - remove temporary *.bin files        (no temp files in the in-memory port)
//
// PORT-NOTE: the C++ build spills the parser collections (ways / way_nodes / nodes / edges /
// complex restrictions / OSMData unique names) to mmapped midgard::sequence temp *.bin files inside
// the tile directory, and the build stages read those back. This on-device port keeps every
// intermediate collection in managed memory (the ported PbfGraphParser exposes Ways / WayNodes /
// ComplexRestrictionsFrom / ComplexRestrictionsTo, GraphBuilder.Build returns the tile blobs, and
// GraphEnhancer.Enhance transforms those blobs in memory). Because of that there is no temp-file
// round-trip and the kCleanup stage is a no-op: nothing is written to disk except the final .gph
// tiles. Every algorithm and stage ordering of build_tile_set is preserved exactly.
//
// The C++ build_tile_set reads tiles back from the tile directory through a baldr GraphReader for
// the kFilter / kHierarchy / kShortcuts / kRestrictions stages. To preserve that contract the
// Build + Enhance output blobs are flushed to the tile directory before the GraphReader-based
// stages run, exactly as build_tile_set leaves them on disk between stages.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Stages of the Valhalla tile building pipeline. Faithful port of the C++
/// <c>enum class BuildStage</c> in util.h.
/// </summary>
public enum BuildStage
{
    /// <summary>Invalid sentinel (C++ <c>kInvalid = -1</c>).</summary>
    Invalid = -1,

    /// <summary>Purge / create the tile directory (C++ <c>kInitialize</c>).</summary>
    Initialize = 0,

    /// <summary>Parse OSM ways (C++ <c>kParseWays</c>).</summary>
    ParseWays = 1,

    /// <summary>Parse OSM relations (C++ <c>kParseRelations</c>).</summary>
    ParseRelations = 2,

    /// <summary>Parse OSM nodes (C++ <c>kParseNodes</c>).</summary>
    ParseNodes = 3,

    /// <summary>Construct graph edges (C++ <c>kConstructEdges</c>).</summary>
    ConstructEdges = 4,

    /// <summary>Build the routing tiles (C++ <c>kBuild</c>).</summary>
    Build = 5,

    /// <summary>Enhance the local-level tiles (C++ <c>kEnhance</c>).</summary>
    Enhance = 6,

    /// <summary>Filter edges/nodes by access mode (C++ <c>kFilter</c>).</summary>
    Filter = 7,

    /// <summary>Add transit (C++ <c>kTransit</c>) - EXCLUDED from this port.</summary>
    Transit = 8,

    /// <summary>Build bike-share stations (C++ <c>kBss</c>) - EXCLUDED from this port.</summary>
    Bss = 9,

    /// <summary>Build the hierarchy levels (C++ <c>kHierarchy</c>).</summary>
    Hierarchy = 10,

    /// <summary>Build shortcut edges (C++ <c>kShortcuts</c>).</summary>
    Shortcuts = 11,

    /// <summary>Build complex restrictions (C++ <c>kRestrictions</c>).</summary>
    Restrictions = 12,

    /// <summary>Add elevation (C++ <c>kElevation</c>) - EXCLUDED from this port.</summary>
    Elevation = 13,

    /// <summary>Validate the graph (C++ <c>kValidate</c>) - EXCLUDED from this port.</summary>
    Validate = 14,

    /// <summary>Clean up temporary files (C++ <c>kCleanup</c>).</summary>
    Cleanup = 15,
}

/// <summary>
/// Configuration for <see cref="TileBuilder.BuildTileSet(System.Collections.Generic.IReadOnlyList{string}, string, TileBuilderConfig)"/>.
/// Replaces the C++ <c>boost::property_tree::ptree</c> sub-tree <c>mjolnir.*</c> that build_tile_set
/// reads. Only the knobs the ported stages actually consume are modeled (HTTP/extract/transit/
/// elevation knobs are excluded with their respective stages).
/// </summary>
public sealed class TileBuilderConfig
{
    /// <summary>PBF parser options (C++ <c>mjolnir.*</c> parse toggles). Defaults match pbfgraphparser.cc.</summary>
    public PbfGraphParserOptions ParserOptions { get; init; } = new();

    /// <summary>
    /// nxn grid divisions for spatial node sorting within a tile (C++ <c>mjolnir.grid_divisions</c>;
    /// default 0 = no spatial sorting). Passed to <see cref="GraphBuilder.BuildEdges"/>.
    /// </summary>
    public uint GridDivisions { get; init; }

    /// <summary>
    /// Days from the pivot date used for the tile creation date stamp (C++ tile header creation date).
    /// </summary>
    public uint TileCreationDate { get; init; }

    /// <summary>Build additional hierarchies (C++ <c>mjolnir.hierarchy</c>, default true).</summary>
    public bool Hierarchy { get; init; } = true;

    /// <summary>
    /// Build shortcut edges (C++ <c>mjolnir.shortcuts</c>, default true). Only applied when
    /// <see cref="Hierarchy"/> is also true (shortcuts require the hierarchy).
    /// </summary>
    public bool Shortcuts { get; init; } = true;

    /// <summary>Include edges drivable (any vehicular) in either direction (C++ <c>mjolnir.include_driving</c>).</summary>
    public bool IncludeDriving { get; init; } = true;

    /// <summary>Include edges with bicycle access in either direction (C++ <c>mjolnir.include_bicycle</c>).</summary>
    public bool IncludeBicycle { get; init; } = true;

    /// <summary>Include edges with pedestrian/wheelchair access in either direction (C++ <c>mjolnir.include_pedestrian</c>).</summary>
    public bool IncludePedestrian { get; init; } = true;

    /// <summary>Max reader cache size in bytes for the GraphReader-based stages (filter/hierarchy/shortcuts/restrictions).</summary>
    public long MaxCacheSize { get; init; } = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// Maximum tile-local construction concurrency. Global graph identities and indexes are frozen
    /// before this bounded parallel stage begins.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = 1;
}

/// <summary>
/// The per-stage statistics returned by <see cref="TileBuilder.BuildTileSet(System.Collections.Generic.IReadOnlyList{string}, string, TileBuilderConfig)"/>.
/// Aggregates the (otherwise discarded) C++ <c>build_stats</c> / per-stage counters from the
/// individual ported stages so callers / tests can assert pipeline behavior.
/// </summary>
public sealed class TileBuilderResult
{
    /// <summary>True if the pipeline ran to completion without error (C++ build_tile_set return value).</summary>
    public bool Success { get; set; }

    /// <summary>The tile directory the .gph tiles were written to (always ends with a separator).</summary>
    public string TileDir { get; set; } = string.Empty;

    /// <summary>Number of OSM ways parsed.</summary>
    public int WayCount { get; set; }

    /// <summary>Number of way-node references parsed.</summary>
    public int WayNodeCount { get; set; }

    /// <summary>Number of local-level tiles produced by the build stage.</summary>
    public int TileCount { get; set; }

    /// <summary>Statistics from the enhance stage (or null if enhance did not run).</summary>
    public GraphEnhancer.EnhancerStats? EnhancerStats { get; set; }

    /// <summary>Statistics from the filter stage (or null if filter did not run / was a no-op).</summary>
    public GraphFilter.FilterStats? FilterStats { get; set; }

    /// <summary>Statistics from the shortcut stage (or null if shortcuts did not run).</summary>
    public ShortcutBuilder.ShortcutStats? ShortcutStats { get; set; }

    /// <summary>Per-level results from the restriction stage (or null if restrictions did not run).</summary>
    public IReadOnlyList<RestrictionBuilder.Result>? RestrictionResults { get; set; }

    /// <summary>Statistics from the validate stage (or null if validation did not run).</summary>
    public GraphValidator.ValidatorStats? ValidatorStats { get; set; }

    /// <summary>Elapsed wall time for each tile construction stage.</summary>
    public IReadOnlyDictionary<string, TimeSpan> StageDurations =>
        stageDurations;

    private readonly Dictionary<string, TimeSpan> stageDurations =
        new(StringComparer.Ordinal);

    internal void RecordStageDuration(string stage, TimeSpan duration) =>
        stageDurations[stage] = duration;
}

/// <summary>
/// Orchestrates the full Valhalla mjolnir tile-build pipeline: parses OSM PBF extracts and writes
/// byte-compatible <c>.gph</c> tiles to a tile directory. Faithful port of the C++
/// <c>valhalla::mjolnir::build_tile_set</c> (the BuildStage pipeline). The excluded stages
/// (transit / bss / elevation / validate) are skipped exactly where build_tile_set would run them,
/// matching the established mjolnir port scope.
/// </summary>
public static class TileBuilder
{
    /// <summary>
    /// Builds an entire Valhalla tile set from the given OSM PBF extract(s) and writes the resulting
    /// <c>.gph</c> tiles into <paramref name="tileDir"/>. Runs the faithful build_tile_set pipeline:
    /// PbfGraphParser -&gt; GraphBuilder -&gt; GraphEnhancer -&gt; GraphFilter -&gt; Hierarchy / Shortcuts
    /// -&gt; RestrictionBuilder, persisting tiles through GraphTileBuilder between the GraphReader-based
    /// stages exactly as the C++ pipeline does.
    /// </summary>
    /// <param name="pbfPaths">OSM protocol-buffer files to build the tiles from (C++ <c>input_files</c>).</param>
    /// <param name="tileDir">Directory the tiles are written to (C++ <c>mjolnir.tile_dir</c>).</param>
    /// <param name="config">Build configuration (replaces the C++ ptree <c>mjolnir.*</c> sub-tree).</param>
    /// <returns>The aggregated per-stage <see cref="TileBuilderResult"/>.</returns>
    public static TileBuilderResult BuildTileSet(
        IReadOnlyList<string> pbfPaths,
        string tileDir,
        TileBuilderConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(pbfPaths);
        return BuildTileSet(
            new FileOsmPbfEntitySource(pbfPaths),
            tileDir,
            config,
            CancellationToken.None);
    }

    /// <summary>
    /// Builds a graph from a replayable entity source. Build-time tooling uses this overload to
    /// decode PBF blocks once and replay normalized entities from bounded intermediate storage.
    /// </summary>
    public static TileBuilderResult BuildTileSet(
        IOsmPbfEntitySource entitySource,
        string tileDir,
        TileBuilderConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entitySource);
        ArgumentException.ThrowIfNullOrEmpty(tileDir);
        cancellationToken.ThrowIfCancellationRequested();

        config ??= new TileBuilderConfig();
        string normalizedTileDir = NormalizeTileDir(tileDir);
        InitializeTileDir(normalizedTileDir);
        cancellationToken.ThrowIfCancellationRequested();

        var parser = new PbfGraphParser(config.ParserOptions);
        OSMData osmdata = parser.Parse(entitySource, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return BuildParsedTileSetCore(
            parser,
            osmdata,
            normalizedTileDir,
            config,
            cancellationToken);
    }

    /// <summary>
    /// Builds tiles from an already parsed OSM graph. This consumes the parser's large build
    /// sequences and releases their backing arrays immediately after local graph construction.
    /// </summary>
    public static TileBuilderResult BuildParsedTileSet(
        PbfGraphParser parser,
        OSMData osmdata,
        string tileDir,
        TileBuilderConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(osmdata);
        ArgumentException.ThrowIfNullOrEmpty(tileDir);
        cancellationToken.ThrowIfCancellationRequested();

        config ??= new TileBuilderConfig();
        string normalizedTileDir = NormalizeTileDir(tileDir);
        InitializeTileDir(normalizedTileDir);
        cancellationToken.ThrowIfCancellationRequested();

        return BuildParsedTileSetCore(
            parser,
            osmdata,
            normalizedTileDir,
            config,
            cancellationToken);
    }

    private static TileBuilderResult BuildParsedTileSetCore(
        PbfGraphParser parser,
        OSMData osmdata,
        string tileDir,
        TileBuilderConfig config,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<OSMRestriction> complexRestrictionsFrom = parser.ComplexRestrictionsFrom;
        IReadOnlyList<OSMRestriction> complexRestrictionsTo = parser.ComplexRestrictionsTo;
        var result = new TileBuilderResult
        {
            TileDir = tileDir,
            WayCount = parser.Ways.Count,
            WayNodeCount = parser.WayNodes.Count,
        };

        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<GraphId, byte[]> tiles;
        try
        {
            tiles = BuildLocalTiles(
                parser,
                osmdata,
                config,
                result,
                cancellationToken);
        }
        finally
        {
            parser.ReleaseBuildSequences();
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ---- kEnhance ---------------------------------------------------------
        var stageStopwatch = Stopwatch.StartNew();
        var enhancer = new GraphEnhancer();
        Dictionary<GraphId, byte[]> enhanced = enhancer.Enhance(
            tiles,
            config.ParserOptions.InferInternalIntersections,
            config.ParserOptions.InferTurnChannels,
            config.MaxDegreeOfParallelism,
            cancellationToken);
        stageStopwatch.Stop();
        result.RecordStageDuration("enhance", stageStopwatch.Elapsed);
        result.EnhancerStats = enhancer.Stats;
        cancellationToken.ThrowIfCancellationRequested();

        stageStopwatch.Restart();
        FlushTilesToDisk(tileDir, enhanced);
        stageStopwatch.Stop();
        result.RecordStageDuration("flush", stageStopwatch.Elapsed);
        result.TileCount = enhanced.Count;
        cancellationToken.ThrowIfCancellationRequested();

        // ---- kFilter ----------------------------------------------------------
        if (!(config.IncludeDriving && config.IncludeBicycle && config.IncludePedestrian))
        {
            result.FilterStats = GraphFilter.Filter(new GraphFilter.FilterConfig
            {
                TileDir = tileDir,
                IncludeDriving = config.IncludeDriving,
                IncludeBicycle = config.IncludeBicycle,
                IncludePedestrian = config.IncludePedestrian,
            });
            cancellationToken.ThrowIfCancellationRequested();
        }

        // ---- kTransit / kBss --------------------------------------------------
        // The dedicated generation package composes these ancillary stages after the road graph.

        // ---- kHierarchy / kShortcuts -----------------------------------------
        if (config.Hierarchy)
        {
            stageStopwatch.Restart();
            HierarchyBuildResult hierarchyResult =
                HierarchyBuilder.Build(
                    MakeReaderConfig(tileDir, config),
                    config.MaxDegreeOfParallelism,
                    cancellationToken);
            stageStopwatch.Stop();
            result.RecordStageDuration("hierarchy", stageStopwatch.Elapsed);
            foreach ((string hierarchyStage, TimeSpan duration) in hierarchyResult.StageDurations)
            {
                result.RecordStageDuration($"hierarchy.{hierarchyStage}", duration);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (config.Shortcuts)
            {
                stageStopwatch.Restart();
                result.ShortcutStats = ShortcutBuilder.Build(
                    MakeReaderConfig(tileDir, config));
                stageStopwatch.Stop();
                result.RecordStageDuration("shortcuts", stageStopwatch.Elapsed);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        // ---- kElevation -------------------------------------------------------
        // The dedicated generation package composes elevation after the road graph.

        // ---- kRestrictions ----------------------------------------------------
        stageStopwatch.Restart();
        var restrictionReader = new GraphReader(MakeReaderConfig(tileDir, config));
        result.RestrictionResults = RestrictionBuilder.Build(
            restrictionReader,
            complexRestrictionsFrom,
            complexRestrictionsTo);
        stageStopwatch.Stop();
        result.RecordStageDuration("restrictions", stageStopwatch.Elapsed);
        cancellationToken.ThrowIfCancellationRequested();

        // ---- kValidate --------------------------------------------------------
        stageStopwatch.Restart();
        result.ValidatorStats = GraphValidator.Validate(
            MakeReaderConfig(tileDir, config),
            cancellationToken);
        stageStopwatch.Stop();
        result.RecordStageDuration("validate", stageStopwatch.Elapsed);
        foreach ((string validationStage, TimeSpan duration) in
                 result.ValidatorStats.StageDurations)
        {
            result.RecordStageDuration($"validate.{validationStage}", duration);
        }

        foreach ((string tileStage, TimeSpan duration) in
                 result.ValidatorStats.TileStageDurations)
        {
            result.RecordStageDuration($"validate.tile.{tileStage}", duration);
        }

        cancellationToken.ThrowIfCancellationRequested();

        result.Success = true;
        return result;
    }

    private static Dictionary<GraphId, byte[]> BuildLocalTiles(
        PbfGraphParser parser,
        OSMData osmdata,
        TileBuilderConfig config,
        TileBuilderResult result,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<OSMWay> ways = parser.Ways;
        IReadOnlyList<OSMWayNode> wayNodes = parser.WayNodes;
        var stageStopwatch = Stopwatch.StartNew();

        // ---- kConstructEdges --------------------------------------------------
        GraphBuilder.Graph graph = GraphBuilder.BuildEdges(
            ways,
            wayNodes,
            config.GridDivisions,
            config.ParserOptions.InferTurnChannels);
        stageStopwatch.Stop();
        result.RecordStageDuration("constructEdges", stageStopwatch.Elapsed);
        cancellationToken.ThrowIfCancellationRequested();

        // ---- kBuild -----------------------------------------------------------
        stageStopwatch.Restart();
        Dictionary<GraphId, byte[]> tiles = GraphBuilder.Build(
            osmdata,
            ways,
            wayNodes,
            graph,
            config.TileCreationDate,
            config.MaxDegreeOfParallelism,
            cancellationToken);
        stageStopwatch.Stop();
        result.RecordStageDuration("build", stageStopwatch.Elapsed);
        return tiles;
    }

    private static string NormalizeTileDir(string tileDir)
    {
        if (!tileDir.EndsWith(Path.DirectorySeparatorChar) &&
            !tileDir.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return tileDir + Path.DirectorySeparatorChar;
        }

        return tileDir;
    }

    /// <summary>
    /// Purges existing tiles (per hierarchy level) and (re)creates the tile directory. Faithful port
    /// of the kInitialize block of build_tile_set.
    /// </summary>
    private static void InitializeTileDir(string tileDir)
    {
        // Purge the level directories if non-empty (C++ removes the whole level_dir).
        foreach (TileLevel level in TileHierarchy.Levels())
        {
            PurgeLevelDir(Path.Combine(tileDir, level.Level.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        // The transit level directory is purged too.
        PurgeLevelDir(Path.Combine(
            tileDir,
            TileHierarchy.GetTransitLevel().Level.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        // Create the directory if it does not exist.
        Directory.CreateDirectory(tileDir);
    }

    private static void PurgeLevelDir(string levelDir)
    {
        if (Directory.Exists(levelDir) && (Directory.GetFiles(levelDir).Length > 0 || Directory.GetDirectories(levelDir).Length > 0))
        {
            Directory.Delete(levelDir, recursive: true);
        }
    }

    /// <summary>
    /// Writes each tile blob to disk under the standard <c>&lt;level&gt;/&lt;id-path&gt;.gph</c> layout
    /// (via <see cref="GraphTile.FileSuffix(GraphId)"/>) so the GraphReader-based stages can read them.
    /// </summary>
    private static void FlushTilesToDisk(string tileDir, IReadOnlyDictionary<GraphId, byte[]> tiles)
    {
        foreach (KeyValuePair<GraphId, byte[]> kv in tiles)
        {
            string path = Path.Combine(tileDir, GraphTile.FileSuffix(kv.Key));
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(path, kv.Value);
        }
    }

    private static GraphReader.Config MakeReaderConfig(string tileDir, TileBuilderConfig config)
        => new() { TileDir = tileDir, MaxCacheSize = config.MaxCacheSize };
}
