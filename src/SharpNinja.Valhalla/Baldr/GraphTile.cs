// Faithful C# port of Valhalla baldr GraphTile (graphtile.h + src/baldr/graphtile.cc) @ 3.7.0.
// Sources:
//   F:/github/valhalla/valhalla/baldr/graphtile.h
//   F:/github/valhalla/src/baldr/graphtile.cc  (1287 LOC)
//
// GraphTile owns a tile blob (decompressed bytes) and exposes typed accessors over the
// bit-packed records it contains. The blob is partitioned, using the header's offsets and
// counts, into:
//   [GraphTileHeader (272 bytes)]
//   [NodeInfo[nodecount]]
//   [NodeTransition[transitioncount]]
//   [DirectedEdge[directededgecount]]
//   [DirectedEdgeExt[directededgecount]]            (only if header.HasExtDirectededge())
//   [AccessRestriction[access_restriction_count]]
//   [TransitDeparture[departurecount]]              (PORT-NOTE: transit, skipped over only)
//   [TransitStop[stopcount]]                        (PORT-NOTE: transit, skipped over only)
//   [TransitRoute[routecount]]                      (PORT-NOTE: transit, skipped over only)
//   [TransitSchedule[schedulecount]]                (PORT-NOTE: transit, skipped over only)
//   [TransitTransfer[transfercount]]                (PORT-NOTE: transit, skipped over only)
//   [Sign[signcount]]
//   [TurnLanes[turnlane_count]]
//   [Admin[admincount]]
//   [edge_bins (GraphId[])]
//   ... then offset-based variable sections:
//   complex_restriction_forward / complex_restriction_reverse / edgeinfo / textlist /
//   lane_connectivity / predictedspeeds
//
// The pointer arithmetic in Initialize() is reproduced exactly, including the well-known
// transit-struct sizes so the offsets line up for non-transit tiles too.
//
// FIDELITY: fixed-size records are read out of the blob with MemoryMarshal over the
// [StructLayout(Sequential, Pack=1)] structs (NodeInfo, DirectedEdge, ...), so a tile byte
// buffer parses byte-for-byte identically to the C++ reinterpret_cast<T*> views.
//
// PORT-NOTES / OMISSIONS (per task instructions):
//   - All transit accessors (GetNextDeparture / GetTransitDeparture[s] / GetTransitStop /
//     GetTransitRoute / GetTransitSchedule / GetStopOneStops / ... / AssociateOneStopIds) are
//     NOT ported: transit* is an excluded module. The transit *sections* are still skipped over
//     in Initialize() using the documented C++ struct sizes so all later offsets are correct.
//   - CacheTileURL / SaveTileToFile / store() are NOT ported: curler/HTTP tile fetch is excluded.
//   - The boost::intrusive_ptr / shared_ptr ref-counting (graph_tile_ptr) collapses to a managed
//     reference; the Create(...) factories return a GraphTile (or null).
//   - GetSpeed's live-traffic blending is ported; it depends on the (already ported) TrafficTile
//     / TrafficSpeed types when a traffic tile is supplied.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using SharpNinja.Valhalla.Midgard;

// Several GraphTile accessor methods share a name with the record type they return (e.g.
// `DirectedEdge DirectedEdge(GraphId)`), exactly as the C++ engine does. In C# a member method
// hides the type of the same name inside the class body, so we alias the record types here and use
// the aliases for every type reference inside GraphTile. The public method names are kept identical
// to the C++ accessors for fidelity.
using DirectedEdgeRec = SharpNinja.Valhalla.Baldr.DirectedEdge;
using DirectedEdgeExtRec = SharpNinja.Valhalla.Baldr.DirectedEdgeExt;
using EdgeInfoRec = SharpNinja.Valhalla.Baldr.EdgeInfo;
using AdminRec = SharpNinja.Valhalla.Baldr.Admin;
using AdminInfoRec = SharpNinja.Valhalla.Baldr.AdminInfo;
using TurnLanesRec = SharpNinja.Valhalla.Baldr.TurnLanes;
using TrafficSpeedRec = SharpNinja.Valhalla.Baldr.TrafficSpeed;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Graph information for a tile within the tiled, hierarchical graph. Owns the tile's byte blob and
/// exposes typed accessors over the bit-packed records. Faithful port of C++ <c>class GraphTile</c>.
/// </summary>
public sealed class GraphTile : IGraphTilePtr
{
    /// <summary>Non-compressed tile file suffix. Mirrors C++ <c>SUFFIX_NON_COMPRESSED</c>.</summary>
    public const string SuffixNonCompressed = ".gph";

    /// <summary>gzip-compressed tile file suffix. Mirrors C++ <c>SUFFIX_COMPRESSED</c>.</summary>
    public const string SuffixCompressed = ".gph.gz";

    /// <summary>Tile path pattern token. Mirrors C++ <c>kTilePathPattern</c>.</summary>
    public const string TilePathPattern = "{tilePath}";

    // Sizes (bytes) of fixed-size records. The non-transit struct sizes come from this port; the
    // transit struct sizes are the documented C++ sizes (transit* not ported, only skipped over).
    private const int NodeInfoSize = 32;       // sizeof(NodeInfo)
    private const int NodeTransitionSize = 8;  // sizeof(NodeTransition)
    private const int DirectedEdgeSize = DirectedEdgeRec.SizeOf;          // 48
    private const int DirectedEdgeExtSize = DirectedEdgeExtRec.SizeOf;    // 8
    private const int AccessRestrictionSize = 16; // sizeof(AccessRestriction)
    private const int SignSize = 8;            // sizeof(Sign)
    private const int TurnLanesSize = 8;       // sizeof(TurnLanes)
    private const int AdminSize = 16;          // sizeof(Admin)
    private const int GraphIdSize = 8;         // sizeof(GraphId)
    private const int BoundingCircleSize = DiscretizedBoundingCircle.SizeOf;

    // Valhalla 3.8.3 packed transit record sizes.
    private const int TransitDepartureSize = 24;
    private const int TransitStopSize = 8;
    private const int TransitRouteSize = 40;
    private const int TransitScheduleSize = 16;
    private const int TransitTransferSize = 12;

    // The owned tile blob.
    private readonly GraphMemory _memory;

    // Header (parsed from the first 272 bytes of the blob).
    private readonly GraphTileHeader _header;

    // Base lat/lon of the tile, cached from the header (matches C++ base_ll_).
    private readonly PointLL _baseLl;

    // Byte offsets (into the blob, absolute including GraphMemory.Offset) for each section.
    private readonly int _nodesOffset;
    private readonly int _transitionsOffset;
    private readonly int _directedEdgesOffset;
    private readonly int _extDirectedEdgesOffset; // -1 when absent
    private readonly int _accessRestrictionsOffset;
    private readonly int _departuresOffset;
    private readonly int _stopsOffset;
    private readonly int _routesOffset;
    private readonly int _schedulesOffset;
    private readonly int _transfersOffset;
    private readonly int _signsOffset;
    private readonly int _turnLanesOffset;
    private readonly int _adminsOffset;
    private readonly int _edgeBinsOffset;

    private readonly int _complexRestrictionForwardOffset;
    private readonly long _complexRestrictionForwardSize;
    private readonly int _complexRestrictionReverseOffset;
    private readonly long _complexRestrictionReverseSize;

    private readonly int _edgeInfoOffset;
    private readonly long _edgeInfoSize;
    private readonly int _textListOffset;
    private readonly long _textListSize;

    private readonly int _laneConnectivityOffset;
    private readonly long _laneConnectivitySize;

    // Predicted speeds view (only populated if predictedspeeds_count > 0).
    private readonly PredictedSpeeds _predictedSpeeds = new();

    // Live traffic tile (optional). PORT-NOTE: snapshot byte view; no const-volatile mmap semantics.
    private readonly TrafficTile _trafficTile;

    // Cached text list as a standalone byte[] (textlist_ region) so EdgeInfo/AdminInfo can take a
    // byte[] + offset-relative view exactly as the C++ takes a char* into the same blob.
    private readonly byte[] _blob;

    /// <summary>
    /// Constructor given the graph Id and the tile memory (the decompressed/loaded blob). Faithful
    /// port of the primary C++ ctor; runs <c>Initialize</c> to set up section offsets.
    /// </summary>
    /// <param name="graphid">Tile Id.</param>
    /// <param name="memory">Owned tile memory (the blob).</param>
    /// <param name="trafficMemory">Optional live-traffic memory.</param>
    private GraphTile(GraphId graphid, GraphMemory memory, GraphMemory? trafficMemory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _trafficTile = new TrafficTile(trafficMemory is null
            ? ReadOnlyMemory<byte>.Empty
            : trafficMemory.Data.AsMemory(trafficMemory.Offset, checked((int)trafficMemory.Size)));

        _blob = _memory.Data;
        int tileBase = _memory.Offset;
        long tileSize = _memory.Size;

        if (tileSize < GraphTileHeader.HeaderSize)
        {
            throw new InvalidOperationException(
                "Invalid tile data size = " + tileSize + ". Tile file might me corrupted");
        }

        // Header is the first 272 bytes.
        _header = GraphTileHeader.FromBytes(_blob.AsSpan(tileBase, GraphTileHeader.HeaderSize));

        if (_header.EndOffset() != tileSize)
        {
            throw new InvalidOperationException(
                "Mismatch in end offset = " + _header.EndOffset() + " vs raw tile data size = " +
                tileSize + ". Tile file might me corrupted");
        }

        // Walk the fixed-size sections. `ptr` is an absolute byte offset into _blob.
        int ptr = tileBase + GraphTileHeader.HeaderSize;

        _nodesOffset = ptr;
        ptr += checked((int)_header.Nodecount() * NodeInfoSize);

        _transitionsOffset = ptr;
        ptr += checked((int)_header.Transitioncount() * NodeTransitionSize);

        _directedEdgesOffset = ptr;
        ptr += checked((int)_header.Directededgecount() * DirectedEdgeSize);

        if (_header.HasExtDirectededge())
        {
            _extDirectedEdgesOffset = ptr;
            ptr += checked((int)_header.Directededgecount() * DirectedEdgeExtSize);
        }
        else
        {
            _extDirectedEdgesOffset = -1;
        }

        _accessRestrictionsOffset = ptr;
        ptr += checked((int)_header.AccessRestrictionCount() * AccessRestrictionSize);

        _departuresOffset = ptr;
        ptr += checked((int)_header.Departurecount() * TransitDepartureSize);

        _stopsOffset = ptr;
        ptr += checked((int)_header.Stopcount() * TransitStopSize);

        _routesOffset = ptr;
        ptr += checked((int)_header.Routecount() * TransitRouteSize);

        _schedulesOffset = ptr;
        ptr += checked((int)_header.Schedulecount() * TransitScheduleSize);

        _transfersOffset = ptr;
        ptr += checked((int)_header.Transfercount() * TransitTransferSize);

        _signsOffset = ptr;
        ptr += checked((int)_header.Signcount() * SignSize);

        _turnLanesOffset = ptr;
        ptr += checked((int)_header.TurnlaneCount() * TurnLanesSize);

        _adminsOffset = ptr;
        ptr += checked((int)_header.Admincount() * AdminSize);

        // Edge bins follow the admins (the section size is derived from the bin offsets in the
        // header, so no fixed count is added here).
        _edgeBinsOffset = ptr;

        // Offset-based variable sections. The header offsets are relative to the tile base.
        _complexRestrictionForwardOffset = tileBase + (int)_header.ComplexRestrictionForwardOffset();
        _complexRestrictionForwardSize =
            _header.ComplexRestrictionReverseOffset() - _header.ComplexRestrictionForwardOffset();

        _complexRestrictionReverseOffset = tileBase + (int)_header.ComplexRestrictionReverseOffset();
        _complexRestrictionReverseSize =
            _header.EdgeinfoOffset() - _header.ComplexRestrictionReverseOffset();

        _edgeInfoOffset = tileBase + (int)_header.EdgeinfoOffset();
        _edgeInfoSize = _header.TextlistOffset() - _header.EdgeinfoOffset();

        _textListOffset = tileBase + (int)_header.TextlistOffset();
        _textListSize = _header.LaneConnectivityOffset() - _header.TextlistOffset();

        _laneConnectivityOffset = tileBase + (int)_header.LaneConnectivityOffset();

        // Predicted speed data. When present the lane connectivity section runs up to the
        // predicted speeds offset; otherwise it runs to the end of the tile.
        if (_header.PredictedspeedsCount() > 0)
        {
            int ptr1 = tileBase + (int)_header.PredictedspeedsOffset();
            int ptr2 = ptr1 + checked((int)_header.Directededgecount() * sizeof(int));

            uint dec = _header.Directededgecount();
            var offsets = new uint[dec];
            for (int i = 0; i < dec; i++)
            {
                offsets[i] = ReadUInt32(_blob, ptr1 + (i * sizeof(uint)));
            }

            // Remaining int16 coefficients run to the end of the tile.
            long profileBytes = tileSize + tileBase - ptr2;
            int profileCount = (int)(profileBytes / sizeof(short));
            var profiles = new short[profileCount];
            for (int i = 0; i < profileCount; i++)
            {
                profiles[i] = unchecked((short)ReadUInt16(_blob, ptr2 + (i * sizeof(short))));
            }

            _predictedSpeeds.SetOffset(offsets);
            _predictedSpeeds.SetProfiles(profiles);

            _laneConnectivitySize =
                _header.PredictedspeedsOffset() - _header.LaneConnectivityOffset();
        }
        else
        {
            _laneConnectivitySize = _header.EndOffset() - _header.LaneConnectivityOffset();
        }

        // base_ll() has some non-trivial calculations; cache it (matches C++).
        _baseLl = _header.BaseLl();
    }

    // Header-only test constructor. Mirrors the C++ test fixture `TestGraphTile` (graphreader.cc),
    // which constructs a GraphTile by allocating a header-sized buffer and setting only the graphid
    // and end_offset, deliberately bypassing the section-offset Initialize() validation. The cache
    // tests only ever read header()->graphid() and header()->end_offset() back, so no section
    // parsing is required. `forTest` differentiates this overload from the primary ctor.
    private GraphTile(GraphId graphid, uint endOffset, bool forTest)
    {
        var buf = new byte[GraphTileHeader.HeaderSize];
        _memory = new VectorGraphMemory(buf);
        _blob = buf;
        _trafficTile = new TrafficTile(ReadOnlyMemory<byte>.Empty);

        _header = GraphTileHeader.FromBytes(buf);
        _header.SetGraphid(graphid);
        _header.SetEndOffset(endOffset);

        // Leave all the section offsets at their defaults: this header-only tile is never used to
        // read records, exactly like the C++ TestGraphTile.
        _baseLl = _header.BaseLl();
    }

    /// <summary>
    /// Test-only factory mirroring the C++ <c>TestGraphTile</c> fixture: builds a header-only tile
    /// with the given id and end offset (size), bypassing section parsing. Used by the tile-cache
    /// tests. Not part of the production C++ API.
    /// </summary>
    internal static GraphTile CreateForTest(GraphId graphid, uint endOffset)
        => new(graphid, endOffset, forTest: true);

    /// <summary>
    /// Test-only factory mirroring the C++ <c>test_tile</c> friend used by <c>test/edgestatus.cc</c>:
    /// builds a header-only tile whose header reports the given directed-edge count (so
    /// <see cref="Thor.EdgeStatus"/> can size its per-tile arrays), bypassing section parsing.
    /// Not part of the production C++ API.
    /// </summary>
    internal static GraphTile CreateForTest(GraphId graphid, uint endOffset, uint directedEdgeCount)
    {
        var tile = new GraphTile(graphid, endOffset, forTest: true);
        tile._header.SetDirectededgecount(directedEdgeCount);
        return tile;
    }

    // ------------------------------------------------------------------
    // Factory methods (Create). PORT-NOTE: graph_tile_ptr collapses to a managed reference.
    // ------------------------------------------------------------------

    /// <summary>
    /// Constructs a tile from a graph Id and an in-memory blob. Faithful port of
    /// <c>GraphTile::Create(graphid, std::vector&lt;char&gt;&amp;&amp; memory)</c>.
    /// </summary>
    public static GraphTile Create(GraphId graphid, byte[] memory)
        => new(graphid, new VectorGraphMemory(memory), null);

    /// <summary>
    /// Constructs a tile from a graph Id and owned <see cref="GraphMemory"/>. Faithful port of
    /// <c>GraphTile::Create(graphid, unique_ptr&lt;GraphMemory&gt;, unique_ptr&lt;GraphMemory&gt;)</c>.
    /// </summary>
    public static GraphTile Create(GraphId graphid, GraphMemory memory, GraphMemory? trafficMemory = null)
        => new(graphid, memory, trafficMemory);

    /// <summary>
    /// Reads a tile from a directory (uncompressed <c>.gph</c> first, then gzipped <c>.gph.gz</c>).
    /// Faithful port of <c>GraphTile::Create(tile_dir, graphid, traffic_memory)</c>. Returns
    /// <c>null</c> if the tile cannot be loaded (invalid id, bad level, missing files).
    /// </summary>
    public static GraphTile? Create(string tileDir, GraphId graphid, GraphMemory? trafficMemory = null)
    {
        if (!graphid.IsValid())
        {
            return null;
        }

        if (graphid.Level() > TileHierarchy.GetMaxLevel())
        {
            return null;
        }

        if (string.IsNullOrEmpty(tileDir))
        {
            return null;
        }

        string fileLocation = Path.Combine(tileDir, FileSuffix(graphid.TileBase()));

        // First try uncompressed.
        if (File.Exists(fileLocation))
        {
            byte[] data = File.ReadAllBytes(fileLocation);
            return new GraphTile(graphid, new VectorGraphMemory(data), trafficMemory);
        }

        // Then try the gzipped tile.
        string gzLocation = Path.ChangeExtension(fileLocation, null); // strip ".gph"
        gzLocation = fileLocation.Substring(0, fileLocation.Length - SuffixNonCompressed.Length)
                     + SuffixCompressed;
        if (File.Exists(gzLocation))
        {
            byte[] compressed = File.ReadAllBytes(gzLocation);
            return DecompressTile(graphid, compressed, trafficMemory);
        }

        return null;
    }

    /// <summary>
    /// Decompresses gzip tile bytes into a tile. Faithful port of <c>GraphTile::DecompressTile</c>.
    /// Returns <c>null</c> if the bytes cannot be gunzipped.
    /// </summary>
    public static GraphTile? DecompressTile(GraphId graphid, byte[] compressed, GraphMemory? trafficMemory = null)
    {
        // Drive the (already ported) inflate callbacks exactly as the C++ DecompressTile does:
        // src_func presents the whole compressed buffer; dst_func grows the output buffer by
        // COMPRESSION_HINT * compressed.size() each time it runs out of space.
        const float compressionHint = 3.5f;

        byte[] data = Array.Empty<byte>();

        void SrcFunc(ZStream s)
        {
            s.NextIn = compressed;
            s.NextInOffset = 0;
            s.AvailIn = (uint)compressed.Length;
        }

        int DstFunc(ZStream s)
        {
            long size = data.Length;
            if (s.TotalOut < (ulong)size)
            {
                // The whole buffer wasn't used: trim to what was produced.
                Array.Resize(ref data, (int)s.TotalOut);
            }
            else
            {
                // Need more space: assume 3.5x the compressed size.
                int grow = (int)(compressed.Length * compressionHint);
                int oldLen = (int)size;
                Array.Resize(ref data, oldLen + grow);
                s.NextOut = data;
                s.NextOutOffset = oldLen;
                s.AvailOut = (uint)grow;
            }

            return CompressionUtils.ZNoFlush;
        }

        if (!CompressionUtils.Inflate(SrcFunc, DstFunc))
        {
            return null;
        }

        return new GraphTile(graphid, new VectorGraphMemory(data), trafficMemory);
    }

    // ------------------------------------------------------------------
    // Identity / header / bounding box
    // ------------------------------------------------------------------

    /// <summary>Gets the graph id of the tile (pointing to the first node). Faithful port of <c>id()</c>.</summary>
    public GraphId Id() => _header.Graphid();

    /// <summary>Gets the tile header. Faithful port of <c>header()</c>.</summary>
    public GraphTileHeader Header() => _header;

    /// <summary>
    /// Returns the historical/predicted speed for a directed edge and local second of week.
    /// </summary>
    public float PredictedSpeed(uint directedEdgeIndex, uint secondsOfWeek)
    {
        if (directedEdgeIndex >= _header.Directededgecount())
        {
            throw new ArgumentOutOfRangeException(nameof(directedEdgeIndex));
        }

        if (!DirectedEdge((int)directedEdgeIndex).HasPredictedSpeed)
        {
            throw new InvalidOperationException(
                $"Directed edge {directedEdgeIndex} has no predicted speed profile.");
        }

        return _predictedSpeeds.Speed(directedEdgeIndex, secondsOfWeek);
    }

    internal uint[] CopyPredictedSpeedOffsets() => _predictedSpeeds.CopyOffsets();

    internal short[] CopyPredictedSpeedProfiles() => _predictedSpeeds.CopyProfiles();

    /// <summary>
    /// Returns a fresh copy of the entire tile image (header through end offset). Used by the
    /// mjolnir edge binner to re-emit the tile with an inserted bin section (mirrors the C++
    /// <c>GraphTileBuilder::AddBins</c> which reads the raw tile bytes around the bin section).
    /// </summary>
    public byte[] TileImage()
    {
        long size = _memory.Size;
        var copy = new byte[size];
        Array.Copy(_blob, _memory.Offset, copy, 0, (int)size);
        return copy;
    }

    /// <summary>
    /// Byte offset (relative to the start of <see cref="TileImage"/>) at which the edge-bin section
    /// begins (immediately after the admins section). Mirrors the address C++ uses as
    /// <c>tile->GetBin(0, 0).data()</c> in <c>AddBins</c>.
    /// </summary>
    public int EdgeBinsImageOffset() => _edgeBinsOffset - _memory.Offset;

    /// <summary>Gets the cached base (SW corner) lat/lon of the tile.</summary>
    public PointLL BaseLl() => _baseLl;

    /// <summary>Gets the bounding box of this graph tile. Faithful port of <c>BoundingBox()</c>.</summary>
    public Aabb2T<double> BoundingBox()
    {
        Tiles<PointLL, double> tiles = _header.Graphid().Level() == TileHierarchy.GetTransitLevel().Level
            ? TileHierarchy.GetTransitLevel().Tiles
            : TileHierarchy.Levels()[(int)_header.Graphid().Level()].Tiles;
        return tiles.TileBounds((int)_header.Graphid().Tileid());
    }

    // ------------------------------------------------------------------
    // Nodes
    // ------------------------------------------------------------------

    /// <summary>Gets the node with the given GraphId. Faithful port of <c>node(const GraphId&amp;)</c>.</summary>
    public NodeInfo Node(GraphId node)
    {
        if (node.Id() < _header.Nodecount())
        {
            return ReadNode((int)node.Id());
        }

        throw new InvalidOperationException(
            "GraphTile NodeInfo index out of bounds: " + node.Tileid() + "," + node.Level() + "," +
            node.Id() + " nodecount= " + _header.Nodecount());
    }

    /// <summary>Gets the node at the given index. Faithful port of <c>node(size_t)</c>.</summary>
    public NodeInfo Node(int idx)
    {
        if (idx < _header.Nodecount())
        {
            return ReadNode(idx);
        }

        throw new InvalidOperationException(
            "GraphTile NodeInfo index out of bounds: " + _header.Graphid().Tileid() + "," +
            _header.Graphid().Level() + "," + idx + " nodecount= " + _header.Nodecount());
    }

    /// <summary>Convenience method to get the lat,lon of a node. Faithful port of <c>get_node_ll</c>.</summary>
    public PointLL GetNodeLl(GraphId nodeid) => Node(nodeid).LatLng(_baseLl);

    /// <summary>Gets the count of nodes in the tile.</summary>
    public uint NodeCount() => _header.Nodecount();

    /// <summary>Gets all nodes in the tile. Faithful port of <c>GetNodes()</c>.</summary>
    public IReadOnlyList<NodeInfo> GetNodes()
    {
        int count = (int)_header.Nodecount();
        var list = new List<NodeInfo>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(ReadNode(i));
        }

        return list;
    }

    // ------------------------------------------------------------------
    // Directed edges
    // ------------------------------------------------------------------

    /// <summary>Gets the directed edge with the given GraphId. Faithful port of <c>directededge(const GraphId&amp;)</c>.</summary>
    public DirectedEdgeRec DirectedEdge(GraphId edge)
    {
        if (edge.Id() < _header.Directededgecount())
        {
            return ReadDirectedEdge((int)edge.Id());
        }

        throw new InvalidOperationException(
            "GraphTile DirectedEdge index out of bounds: " + _header.Graphid().Tileid() + "," +
            _header.Graphid().Level() + "," + edge.Id() +
            " directededgecount= " + _header.Directededgecount());
    }

    /// <summary>Gets the directed edge at the given index. Faithful port of <c>directededge(size_t)</c>.</summary>
    public DirectedEdgeRec DirectedEdge(int idx)
    {
        if (idx < _header.Directededgecount())
        {
            return ReadDirectedEdge(idx);
        }

        throw new InvalidOperationException(
            "GraphTile DirectedEdge index out of bounds: " + _header.Graphid().Tileid() + "," +
            _header.Graphid().Level() + "," + idx +
            " directededgecount= " + _header.Directededgecount());
    }

    /// <summary>Gets the directed edge extension with the given GraphId. Faithful port of <c>ext_directededge(const GraphId&amp;)</c>.</summary>
    public DirectedEdgeExtRec ExtDirectedEdge(GraphId edge)
    {
        if (_extDirectedEdgesOffset < 0)
        {
            throw new InvalidOperationException("GraphTile has no extended directed edges");
        }

        if (edge.Id() < _header.Directededgecount())
        {
            return ReadDirectedEdgeExt((int)edge.Id());
        }

        throw new InvalidOperationException(
            "GraphTile DirectedEdgeExt index out of bounds: " + _header.Graphid().Tileid() + "," +
            _header.Graphid().Level() + "," + edge.Id() +
            " directededgecount= " + _header.Directededgecount());
    }

    /// <summary>Gets the count of directed edges in the tile.</summary>
    public uint DirectedEdgeCount() => _header.Directededgecount();

    /// <summary>Gets all directed edges in the tile. Faithful port of <c>GetDirectedEdges()</c>.</summary>
    public IReadOnlyList<DirectedEdge> GetDirectedEdges()
    {
        int count = (int)_header.Directededgecount();
        var list = new List<DirectedEdgeRec>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(ReadDirectedEdge(i));
        }

        return list;
    }

    /// <summary>
    /// Gets the directed edges leaving the node at <paramref name="nodeIndex"/>, returning the
    /// first edge plus the count and edge index via out parameters. Faithful port of
    /// <c>GetDirectedEdges(node_index, count, edge_index)</c>.
    /// </summary>
    public IReadOnlyList<DirectedEdge> GetDirectedEdges(uint nodeIndex, out uint count, out uint edgeIndex)
    {
        NodeInfo nodeinfo = Node((int)nodeIndex);
        count = nodeinfo.EdgeCount;
        edgeIndex = nodeinfo.EdgeIndex;

        var list = new List<DirectedEdgeRec>((int)count);
        for (uint i = 0; i < count; i++)
        {
            list.Add(DirectedEdge((int)(edgeIndex + i)));
        }

        return list;
    }

    /// <summary>
    /// Gets the directed edges leaving the given node. Faithful port of
    /// <c>GetDirectedEdges(const NodeInfo*)</c> (returns the span of edge_count edges starting at
    /// edge_index).
    /// </summary>
    public IReadOnlyList<DirectedEdge> GetDirectedEdges(NodeInfo node)
    {
        uint edgeIndex = node.EdgeIndex;
        uint count = node.EdgeCount;
        var list = new List<DirectedEdgeRec>((int)count);
        for (uint i = 0; i < count; i++)
        {
            list.Add(ReadDirectedEdge((int)(edgeIndex + i)));
        }

        return list;
    }

    /// <summary>
    /// Convenience method to get the opposing edge id given a directed edge whose end node is in
    /// this tile. Faithful port of <c>GetOpposingEdgeId</c>.
    /// </summary>
    public GraphId GetOpposingEdgeId(DirectedEdgeRec edge)
    {
        GraphId endnode = edge.EndNode;
        return new GraphId(endnode.Tileid(), endnode.Level(), Node((int)endnode.Id()).EdgeIndex + edge.OppIndex);
    }

    // ------------------------------------------------------------------
    // Node transitions
    // ------------------------------------------------------------------

    /// <summary>Gets the node transition at the given index. Faithful port of <c>transition(uint32_t)</c>.</summary>
    public NodeTransition Transition(uint idx)
    {
        if (idx < _header.Transitioncount())
        {
            return ReadTransition((int)idx);
        }

        throw new InvalidOperationException(
            "GraphTile NodeTransition index out of bounds: " + _header.Graphid().Tileid() + "," +
            _header.Graphid().Level() + "," + idx + " transitioncount= " + _header.Transitioncount());
    }

    /// <summary>
    /// Gets the node transitions leaving the given node. Faithful port of
    /// <c>GetNodeTransitions(const NodeInfo*)</c>.
    /// </summary>
    public IReadOnlyList<NodeTransition> GetNodeTransitions(NodeInfo node)
    {
        uint start = node.TransitionIndex;
        uint count = node.TransitionCount;
        var list = new List<NodeTransition>((int)count);
        for (uint i = 0; i < count; i++)
        {
            list.Add(ReadTransition((int)(start + i)));
        }

        return list;
    }

    /// <summary>
    /// Gets the node transitions leaving the given node id. Faithful port of
    /// <c>GetNodeTransitions(const GraphId&amp;)</c>.
    /// </summary>
    public IReadOnlyList<NodeTransition> GetNodeTransitions(GraphId node)
    {
        if (node.Id() >= _header.Nodecount())
        {
            throw new InvalidOperationException(
                "GraphTile NodeInfo index out of bounds: " + node.Tileid() + "," + node.Level() + "," +
                node.Id() + " nodecount= " + _header.Nodecount());
        }

        return GetNodeTransitions(ReadNode((int)node.Id()));
    }

    // ------------------------------------------------------------------
    // Transit
    // ------------------------------------------------------------------

    /// <summary>Gets a transit departure by tile-local index.</summary>
    public TransitDeparture TransitDeparture(int index)
    {
        if ((uint)index >= _header.Departurecount())
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return ReadTransitDeparture(index);
    }

    /// <summary>Gets a transit stop by tile-local index.</summary>
    public TransitStop TransitStop(int index)
    {
        if ((uint)index >= _header.Stopcount())
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return ReadTransitStop(index);
    }

    /// <summary>Gets a transit route by tile-local index.</summary>
    public TransitRoute TransitRoute(int index)
    {
        if ((uint)index >= _header.Routecount())
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return ReadTransitRoute(index);
    }

    /// <summary>Gets a transit schedule by tile-local index.</summary>
    public TransitSchedule TransitSchedule(int index)
    {
        if ((uint)index >= _header.Schedulecount())
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return ReadTransitSchedule(index);
    }

    /// <summary>Gets a transit transfer by tile-local index.</summary>
    public TransitTransfer TransitTransfer(int index)
    {
        if ((uint)index >= _header.Transfercount())
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return ReadTransitTransfer(index);
    }

    /// <summary>Gets every transit departure in on-disk order.</summary>
    public IReadOnlyList<TransitDeparture> GetTransitDepartures()
        => ReadTransitRecords((int)_header.Departurecount(), ReadTransitDeparture);

    /// <summary>Gets every transit stop in on-disk order.</summary>
    public IReadOnlyList<TransitStop> GetTransitStops()
        => ReadTransitRecords((int)_header.Stopcount(), ReadTransitStop);

    /// <summary>Gets every transit route in on-disk order.</summary>
    public IReadOnlyList<TransitRoute> GetTransitRoutes()
        => ReadTransitRecords((int)_header.Routecount(), ReadTransitRoute);

    /// <summary>Gets every transit schedule in on-disk order.</summary>
    public IReadOnlyList<TransitSchedule> GetTransitSchedules()
        => ReadTransitRecords((int)_header.Schedulecount(), ReadTransitSchedule);

    /// <summary>Gets every transit transfer in on-disk order.</summary>
    public IReadOnlyList<TransitTransfer> GetTransitTransfers()
        => ReadTransitRecords((int)_header.Transfercount(), ReadTransitTransfer);

    // ------------------------------------------------------------------
    // Edge info / names / signs
    // ------------------------------------------------------------------

    /// <summary>
    /// Gets the edge info located at a given byte offset (relative to the edge-info section start).
    /// Used by <see cref="Mjolnir.GraphTileBuilder"/> when deserializing a tile (the C++ builder reads
    /// <c>EdgeInfo(edgeinfo_ + offset, textlist_, textlist_size_)</c>).
    /// </summary>
    public EdgeInfoRec EdgeInfoAtOffset(uint offset)
    {
        byte[] namesList = TextListBuffer();
        return new EdgeInfoRec(_blob, _edgeInfoOffset + (int)offset, namesList, namesList.Length);
    }

    /// <summary>
    /// Returns a copy of the raw text-list section bytes (the null-terminated names table). Used by
    /// <see cref="Mjolnir.GraphTileBuilder"/> to reconstruct the text list when deserializing.
    /// </summary>
    public byte[] TextListRaw() => (byte[])TextListBuffer().Clone();

    /// <summary>
    /// Returns a copy of the raw forward complex-restriction section bytes. Used by
    /// <see cref="Mjolnir.GraphTileBuilder"/> to deserialize the restrictions.
    /// </summary>
    public byte[] ComplexRestrictionForwardRaw()
        => CopySection(_complexRestrictionForwardOffset, _complexRestrictionForwardSize);

    /// <summary>
    /// Returns a copy of the raw reverse complex-restriction section bytes. Used by
    /// <see cref="Mjolnir.GraphTileBuilder"/> to deserialize the restrictions.
    /// </summary>
    public byte[] ComplexRestrictionReverseRaw()
        => CopySection(_complexRestrictionReverseOffset, _complexRestrictionReverseSize);

    private byte[] CopySection(int offset, long size)
    {
        int len = checked((int)size);
        var buf = new byte[len];
        if (len > 0)
        {
            Array.Copy(_blob, offset, buf, 0, len);
        }

        return buf;
    }

    /// <summary>Gets the edge info for a directed edge. Faithful port of <c>edgeinfo(const DirectedEdge*)</c>.</summary>
    public EdgeInfoRec EdgeInfo(DirectedEdgeRec edge)
    {
        // Names are addressed by offsets relative to the text-list start, so the names buffer must
        // begin at the text-list section (matching C++ EdgeInfo(edgeinfo_+off, textlist_, size)).
        // Passing the whole tile blob here corrupted name/tagged-value lookups (port audit finding).
        byte[] namesList = TextListBuffer();
        return new EdgeInfoRec(
            _blob,
            _edgeInfoOffset + (int)edge.EdgeInfoOffset,
            namesList,
            namesList.Length);
    }

    /// <summary>
    /// Convenience method to get the (untagged) names for an edge. Faithful port of <c>GetNames</c>.
    /// </summary>
    public List<string> GetNames(DirectedEdge edge) => EdgeInfoForNames(edge).GetNames();

    /// <summary>
    /// Convenience method to get the route-number type mask for an edge's names. Faithful port of
    /// <c>GetTypes</c>.
    /// </summary>
    public ushort GetTypes(DirectedEdge edge) => EdgeInfoForNames(edge).GetTypes();

    /// <summary>
    /// Convenience method to get the text/name for a given offset into the text list. Faithful port
    /// of <c>GetName(uint32_t)</c>.
    /// </summary>
    public string GetName(uint textlistOffset)
    {
        if (textlistOffset < _textListSize)
        {
            return ReadCString(_blob, _textListOffset + (int)textlistOffset);
        }

        throw new InvalidOperationException("GetName: offset exceeds size of text list");
    }

    /// <summary>
    /// Returns the signs for a directed edge or node index. Faithful port of
    /// <c>GetSigns(idx, signs_on_node)</c>. Linguistic records are concatenated into the sign text
    /// using the 3-byte stored header, exactly as the engine does.
    /// </summary>
    public List<SignInfo> GetSigns(uint idx, bool signsOnNode = false)
    {
        var signs = new List<SignInfo>();
        int count = (int)_header.Signcount();
        if (count == 0)
        {
            return signs;
        }

        // Signs are sorted by edge index. Binary search to find the first matching index.
        int found = LowerBoundSignIndex(idx, count);

        for (; found < count && ReadSign(found).Index == idx; ++found)
        {
            Sign sign = ReadSign(found);
            if (sign.TextOffset < _textListSize)
            {
                int textStart = _textListOffset + (int)sign.TextOffset;
                bool isLinguistic = sign.GetSignType() == Sign.Type.Linguistic;
                bool isNodeSignType = sign.GetSignType() == Sign.Type.JunctionName ||
                                      sign.GetSignType() == Sign.Type.TollName;

                if (((isNodeSignType || (isLinguistic && sign.IsRouteNumType())) && signsOnNode) ||
                    (((!isNodeSignType && !isLinguistic) ||
                      (isLinguistic && !sign.IsRouteNumType())) && !signsOnNode))
                {
                    string signText;
                    if (isLinguistic)
                    {
                        var sb = new StringBuilder();
                        int text = textStart;
                        while (_blob[text] != 0)
                        {
                            LinguisticTextHeader header = ReadLinguisticHeader(_blob, text);
                            foreach (byte b in header.ToStoredBytes())
                            {
                                sb.Append((char)b);
                            }

                            for (int k = 0; k < header.Length; k++)
                            {
                                sb.Append((char)_blob[text + LinguisticConstants.HeaderSize + k]);
                            }

                            text += header.Length + LinguisticConstants.HeaderSize;
                        }

                        signText = sb.ToString();
                    }
                    else
                    {
                        signText = ReadCString(_blob, textStart);
                    }

                    signs.Add(new SignInfo(
                        sign.GetSignType(), sign.IsRouteNumType(), sign.Tagged(), false, 0, 0, signText));
                }
            }
            else
            {
                throw new InvalidOperationException("GetSigns: offset exceeds size of text list");
            }
        }

        return signs;
    }

    // ------------------------------------------------------------------
    // Admins
    // ------------------------------------------------------------------

    /// <summary>Gets the admin record at the given index. Faithful port of <c>admin(size_t)</c>.</summary>
    public AdminRec Admin(int idx)
    {
        if (idx < _header.Admincount())
        {
            return ReadAdmin(idx);
        }

        throw new InvalidOperationException("GraphTile Admin index out of bounds");
    }

    /// <summary>
    /// Gets the admin info at the given index, resolving the country/state names from the text
    /// list. Faithful port of <c>admininfo(size_t)</c>.
    /// </summary>
    public AdminInfoRec AdminInfo(int idx)
    {
        if (idx < _header.Admincount())
        {
            AdminRec admin = ReadAdmin(idx);
            return new AdminInfoRec(
                ReadCString(_blob, _textListOffset + (int)admin.CountryOffset),
                ReadCString(_blob, _textListOffset + (int)admin.StateOffset),
                admin.CountryIsoCode(),
                admin.StateIsoCode());
        }

        throw new InvalidOperationException("GraphTile AdminInfo index out of bounds");
    }

    // ------------------------------------------------------------------
    // Access restrictions
    // ------------------------------------------------------------------

    /// <summary>
    /// Convenience method to get the access restrictions for an edge given the directed edge id.
    /// Returns the matching restrictions and the start index. Faithful port of
    /// <c>GetAccessRestrictions(uint32_t)</c>.
    /// </summary>
    public (IReadOnlyList<AccessRestriction> Restrictions, int Start) GetAccessRestrictions(uint idx)
    {
        int count = (int)_header.AccessRestrictionCount();
        if (count == 0)
        {
            return (Array.Empty<AccessRestriction>(), 0);
        }

        // Restrictions are sorted by edge id. Binary search to find the first matching index.
        int found = count;
        int low = 0;
        int high = count - 1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            uint resIdx = ReadAccessRestriction(mid).EdgeIndex();
            if (idx == resIdx)
            {
                found = mid;
                high = mid - 1;
            }
            else if (idx < resIdx)
            {
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        int start = found;
        var list = new List<AccessRestriction>();
        while (found < count && ReadAccessRestriction(found).EdgeIndex() == idx)
        {
            list.Add(ReadAccessRestriction(found));
            ++found;
        }

        return (list, start);
    }

    /// <summary>
    /// Convenience method to get the access restrictions for an edge filtered by access mode.
    /// Faithful port of <c>GetAccessRestrictions(edgeid, access)</c>.
    /// </summary>
    public IEnumerable<AccessRestriction> GetAccessRestrictions(uint edgeid, uint access)
    {
        (IReadOnlyList<AccessRestriction> all, _) = GetAccessRestrictions(edgeid);
        foreach (AccessRestriction r in all)
        {
            if ((r.Modes() & access) != 0)
            {
                yield return r;
            }
        }
    }

    /// <summary>
    /// Gets a specific access restriction by index from the mode-filtered results. Faithful port of
    /// <c>GetAccessRestrictionAtIndex(edgeid, access, index)</c>.
    /// </summary>
    public AccessRestriction? GetAccessRestrictionAtIndex(uint edgeid, uint access, int index)
    {
        int i = 0;
        foreach (AccessRestriction r in GetAccessRestrictions(edgeid, access))
        {
            if (i == index)
            {
                return r;
            }

            i++;
        }

        return null;
    }

    /// <summary>
    /// Returns all access restriction records in the tile, in stored (sorted) order. Used by
    /// <see cref="Mjolnir.GraphTileBuilder"/> when deserializing.
    /// </summary>
    public IReadOnlyList<AccessRestriction> GetAllAccessRestrictions()
    {
        int count = (int)_header.AccessRestrictionCount();
        var list = new List<AccessRestriction>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(ReadAccessRestriction(i));
        }

        return list;
    }

    /// <summary>
    /// Returns all sign records in the tile, in stored order. Used by
    /// <see cref="Mjolnir.GraphTileBuilder"/> when deserializing.
    /// </summary>
    public IReadOnlyList<Sign> GetAllSigns()
    {
        int count = (int)_header.Signcount();
        var list = new List<Sign>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(ReadSign(i));
        }

        return list;
    }

    /// <summary>
    /// Returns all turn lane records in the tile, in stored order. Used by
    /// <see cref="Mjolnir.GraphTileBuilder"/> when deserializing.
    /// </summary>
    public IReadOnlyList<TurnLanesRec> GetAllTurnLanes()
    {
        int count = (int)_header.TurnlaneCount();
        var list = new List<TurnLanesRec>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(ReadTurnLanes(i));
        }

        return list;
    }

    /// <summary>
    /// Returns all lane connectivity records in the tile, in stored order. Used by
    /// <see cref="Mjolnir.GraphTileBuilder"/> when deserializing.
    /// </summary>
    public IReadOnlyList<LaneConnectivity> GetAllLaneConnectivity()
    {
        int count = (int)(_laneConnectivitySize / 24);
        var list = new List<LaneConnectivity>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(ReadLaneConnectivity(i));
        }

        return list;
    }

    // ------------------------------------------------------------------
    // Complex restrictions
    // ------------------------------------------------------------------

    /// <summary>
    /// Gets the complex restrictions (forward or reverse) for an edge id and access modes, as a
    /// lazy filtered view. Faithful port of <c>GetComplexRestrictions(forward, id, modes)</c>.
    /// </summary>
    public ComplexRestrictionView GetComplexRestrictions(bool forward, GraphId id, ulong modes)
    {
        return forward
            ? new ComplexRestrictionView(
                _blob, _complexRestrictionForwardOffset, _complexRestrictionForwardSize, id, modes, true)
            : new ComplexRestrictionView(
                _blob, _complexRestrictionReverseOffset, _complexRestrictionReverseSize, id, modes, false);
    }

    // ------------------------------------------------------------------
    // Edge bins
    // ------------------------------------------------------------------

    /// <summary>
    /// Gets the GraphIds for the bin at (column, row) in the 5x5 grid. Faithful port of
    /// <c>GetBin(column, row)</c>.
    /// </summary>
    public IReadOnlyList<GraphId> GetBin(int column, int row) => GetBin((row * GraphTileHeader.BinsDim) + column);

    /// <summary>
    /// Gets the GraphIds for the bin at the given row-major index. Faithful port of
    /// <c>GetBin(index)</c>.
    /// </summary>
    public IReadOnlyList<GraphId> GetBin(int index)
    {
        (uint begin, uint end) = _header.BinOffset(index);
        var list = new List<GraphId>((int)(end - begin));
        for (uint i = begin; i < end; i++)
        {
            ulong value = ReadUInt64(_blob, _edgeBinsOffset + ((int)i * GraphIdSize));
            list.Add(new GraphId(value));
        }

        return list;
    }

    /// <summary>
    /// Gets the Valhalla 3.8.3 discretized bounding circles aligned one-for-one with a bin's
    /// GraphIds. Legacy tiles return an empty collection.
    /// </summary>
    public IReadOnlyList<DiscretizedBoundingCircle> GetBoundingCircles(
        int column,
        int row)
        => GetBoundingCircles((row * GraphTileHeader.BinsDim) + column);

    /// <summary>Gets the discretized bounding circles for a row-major bin index.</summary>
    public IReadOnlyList<DiscretizedBoundingCircle> GetBoundingCircles(int index)
    {
        if (!_header.HasBoundingCircles())
        {
            return [];
        }

        (uint begin, uint end) = _header.BinOffset(index);
        var circles = new List<DiscretizedBoundingCircle>((int)(end - begin));
        int sectionOffset =
            _memory.Offset + checked((int)_header.BoundingCircleOffset());
        for (uint circleIndex = begin; circleIndex < end; circleIndex++)
        {
            uint rawValue = ReadUInt32(
                _blob,
                sectionOffset + checked((int)circleIndex * BoundingCircleSize));
            circles.Add(DiscretizedBoundingCircle.FromRaw(rawValue));
        }

        return circles;
    }

    // ------------------------------------------------------------------
    // Lane connectivity
    // ------------------------------------------------------------------

    /// <summary>
    /// Gets the lane connections ending on the directed edge at <paramref name="idx"/>. Faithful
    /// port of <c>GetLaneConnectivity(uint32_t)</c>.
    /// </summary>
    public IReadOnlyList<LaneConnectivity> GetLaneConnectivity(uint idx)
    {
        int count = (int)(_laneConnectivitySize / 24); // sizeof(LaneConnectivity) == 24
        if (count == 0)
        {
            return Array.Empty<LaneConnectivity>();
        }

        // Lane connections are sorted by (destination) edge index. Binary search for the first match.
        int found = count;
        int low = 0;
        int high = count - 1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            uint to = ReadLaneConnectivity(mid).To;
            if (idx == to)
            {
                found = mid;
                high = mid - 1;
            }
            else if (idx < to)
            {
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        var list = new List<LaneConnectivity>();
        while (found < count && ReadLaneConnectivity(found).To == idx)
        {
            list.Add(ReadLaneConnectivity(found));
            ++found;
        }

        return list;
    }

    // ------------------------------------------------------------------
    // Turn lanes
    // ------------------------------------------------------------------

    /// <summary>
    /// Convenience method to get the turn lane masks for an edge. Faithful port of
    /// <c>turnlanes(uint32_t)</c>.
    /// </summary>
    public List<ushort> TurnLanes(uint idx)
    {
        uint offset = TurnLanesOffset(idx);
        return offset > 0
            ? TurnLanesRec.LaneMasks(ReadCString(_blob, _textListOffset + (int)offset))
            : new List<ushort>();
    }

    /// <summary>
    /// Convenience method to get the text-table offset for the turn lanes of an edge. Faithful port
    /// of <c>turnlanes_offset(uint32_t)</c>.
    /// </summary>
    public uint TurnLanesOffset(uint idx)
    {
        int count = (int)_header.TurnlaneCount();
        if (count == 0)
        {
            return 0;
        }

        // std::lower_bound(&turnlanes_[0], &turnlanes_[count], TurnLanes(idx, 0)): first record whose
        // edge index is NOT less than idx.
        int low = 0;
        int high = count;
        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            if (ReadTurnLanes(mid).EdgeIndex < idx)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low != count ? ReadTurnLanes(low).TextOffset : 0;
    }

    // ------------------------------------------------------------------
    // Speed
    // ------------------------------------------------------------------

    /// <summary>
    /// Gets the speed (KPH) for a directed edge given a flow mask and the time of week. Faithful
    /// port of <c>GetSpeed(...)</c>, including live-traffic blending when a traffic tile is present.
    /// </summary>
    /// <param name="de">The directed edge.</param>
    /// <param name="deIndex">The directed edge's index within this tile (the engine derives this from pointer arithmetic).</param>
    /// <param name="flowMask">Which traffic sources may be used.</param>
    /// <param name="seconds">Seconds of week since Monday midnight (defaults to invalid).</param>
    /// <param name="isTruck">Whether to apply truck speed clamping.</param>
    /// <param name="secondsFromNow">Absolute seconds from now until the edge is traversed.</param>
    public uint GetSpeed(
        DirectedEdgeRec de,
        uint deIndex,
        byte flowMask = GraphConstants.ConstrainedFlowMask,
        ulong seconds = GraphConstants.InvalidSecondsOfWeek,
        bool isTruck = false,
        ulong secondsFromNow = 0)
    {
        const double liveSpeedFade = 1.0 / 3600.0;
        float liveTrafficMultiplier = (float)(1.0 - Math.Min(secondsFromNow * liveSpeedFade, 1.0));
        uint partialLiveSpeed = 0;
        float partialLivePct = 0;
        bool invalidTime = seconds == GraphConstants.InvalidSecondsOfWeek;

        if (!invalidTime && (flowMask & GraphConstants.CurrentFlowMask) != 0 && _trafficTile.IsValid
            && liveTrafficMultiplier != 0.0f)
        {
            TrafficSpeedRec liveSpeed = _trafficTile.TrafficSpeed(deIndex);
            if (liveSpeed.SpeedValid() && (partialLiveSpeed = liveSpeed.GetOverallSpeed()) > 0)
            {
                if (liveSpeed.Breakpoint1 == 255)
                {
                    partialLivePct = 1.0f;
                }
                else
                {
                    partialLivePct =
                        ((liveSpeed.EncodedSpeed1 != TrafficTileConstants.UnknownTrafficSpeedRaw
                                ? liveSpeed.Breakpoint1
                                : 0)
                         + (liveSpeed.EncodedSpeed2 != TrafficTileConstants.UnknownTrafficSpeedRaw
                                ? (liveSpeed.Breakpoint2 - liveSpeed.Breakpoint1)
                                : 0)
                         + (liveSpeed.EncodedSpeed3 != TrafficTileConstants.UnknownTrafficSpeedRaw
                                ? (255 - liveSpeed.Breakpoint2)
                                : 0)) / 255.0f;
                }

                partialLivePct *= liveTrafficMultiplier;
                if (partialLivePct == 1.0f)
                {
                    return partialLiveSpeed;
                }
            }
        }

        if (!invalidTime && (flowMask & GraphConstants.PredictedFlowMask) != 0 && de.HasPredictedSpeed)
        {
            seconds %= Constants.SecondsPerWeek;
            float speed = _predictedSpeeds.Speed(deIndex, (uint)seconds);
            return (uint)((partialLiveSpeed * partialLivePct)
                          + ((1 - partialLivePct) * (Math.Max(speed, 0.5f) + 0.5f)));
        }

        seconds %= Constants.SecondsPerDay;
        bool isDaytime = 25200 < seconds && seconds < 68400;
        if ((invalidTime || isDaytime) && (flowMask & GraphConstants.ConstrainedFlowMask) != 0
            && GraphConstants.ValidSpeed(de.ConstrainedFlowSpeed))
        {
            return (uint)((partialLiveSpeed * partialLivePct)
                          + ((1 - partialLivePct) * de.ConstrainedFlowSpeed));
        }

        if ((invalidTime || !isDaytime) && (flowMask & GraphConstants.FreeFlowMask) != 0
            && GraphConstants.ValidSpeed(de.FreeFlowSpeed))
        {
            return (uint)((partialLiveSpeed * partialLivePct)
                          + ((1 - partialLivePct) * de.FreeFlowSpeed));
        }

        uint fallback = (uint)((partialLiveSpeed * partialLivePct) + ((1 - partialLivePct) * de.Speed));
        return isTruck && de.TruckSpeed > 0 ? Math.Min(de.TruckSpeed, fallback) : fallback;
    }

    /// <summary>
    /// Overload of <see cref="GetSpeed(DirectedEdgeRec,uint,byte,ulong,bool,ulong)"/> that also
    /// reports which traffic source (flow mask) the returned speed came from. Faithful port of the
    /// C++ <c>GetSpeed</c> <c>uint8_t* flow_sources</c> output parameter.
    /// </summary>
    public uint GetSpeed(
        DirectedEdgeRec de,
        uint deIndex,
        byte flowMask,
        ulong seconds,
        bool isTruck,
        out byte flowSources,
        ulong secondsFromNow)
    {
        flowSources = GraphConstants.NoFlowMask;

        const double liveSpeedFade = 1.0 / 3600.0;
        float liveTrafficMultiplier = (float)(1.0 - Math.Min(secondsFromNow * liveSpeedFade, 1.0));
        uint partialLiveSpeed = 0;
        float partialLivePct = 0;
        bool invalidTime = seconds == GraphConstants.InvalidSecondsOfWeek;

        if (!invalidTime && (flowMask & GraphConstants.CurrentFlowMask) != 0 && _trafficTile.IsValid
            && liveTrafficMultiplier != 0.0f)
        {
            TrafficSpeedRec liveSpeed = _trafficTile.TrafficSpeed(deIndex);
            if (liveSpeed.SpeedValid() && (partialLiveSpeed = liveSpeed.GetOverallSpeed()) > 0)
            {
                flowSources |= GraphConstants.CurrentFlowMask;
                if (liveSpeed.Breakpoint1 == 255)
                {
                    partialLivePct = 1.0f;
                }
                else
                {
                    partialLivePct =
                        ((liveSpeed.EncodedSpeed1 != TrafficTileConstants.UnknownTrafficSpeedRaw
                                ? liveSpeed.Breakpoint1
                                : 0)
                         + (liveSpeed.EncodedSpeed2 != TrafficTileConstants.UnknownTrafficSpeedRaw
                                ? (liveSpeed.Breakpoint2 - liveSpeed.Breakpoint1)
                                : 0)
                         + (liveSpeed.EncodedSpeed3 != TrafficTileConstants.UnknownTrafficSpeedRaw
                                ? (255 - liveSpeed.Breakpoint2)
                                : 0)) / 255.0f;
                }

                partialLivePct *= liveTrafficMultiplier;
                if (partialLivePct == 1.0f)
                {
                    return partialLiveSpeed;
                }
            }
        }

        if (!invalidTime && (flowMask & GraphConstants.PredictedFlowMask) != 0 && de.HasPredictedSpeed)
        {
            seconds %= Constants.SecondsPerWeek;
            float speed = _predictedSpeeds.Speed(deIndex, (uint)seconds);
            flowSources |= GraphConstants.PredictedFlowMask;
            return (uint)((partialLiveSpeed * partialLivePct)
                          + ((1 - partialLivePct) * (Math.Max(speed, 0.5f) + 0.5f)));
        }

        seconds %= Constants.SecondsPerDay;
        bool isDaytime = 25200 < seconds && seconds < 68400;
        if ((invalidTime || isDaytime) && (flowMask & GraphConstants.ConstrainedFlowMask) != 0
            && GraphConstants.ValidSpeed(de.ConstrainedFlowSpeed))
        {
            flowSources |= GraphConstants.ConstrainedFlowMask;
            return (uint)((partialLiveSpeed * partialLivePct)
                          + ((1 - partialLivePct) * de.ConstrainedFlowSpeed));
        }

        if ((invalidTime || !isDaytime) && (flowMask & GraphConstants.FreeFlowMask) != 0
            && GraphConstants.ValidSpeed(de.FreeFlowSpeed))
        {
            flowSources |= GraphConstants.FreeFlowMask;
            return (uint)((partialLiveSpeed * partialLivePct)
                          + ((1 - partialLivePct) * de.FreeFlowSpeed));
        }

        uint fallback = (uint)((partialLiveSpeed * partialLivePct) + ((1 - partialLivePct) * de.Speed));
        return isTruck && de.TruckSpeed > 0 ? Math.Min(de.TruckSpeed, fallback) : fallback;
    }

    /// <summary>
    /// Convenience method to determine whether an edge is currently closed due to traffic. Faithful
    /// port of <c>IsClosed(const DirectedEdge*)</c>.
    /// </summary>
    public bool IsClosed(uint deIndex) => _trafficTile.TrafficSpeed(deIndex).Closed();

    /// <summary>Gets the live traffic tile (may be an empty/invalid tile).</summary>
    public TrafficTile GetTrafficTile() => _trafficTile;

    // ------------------------------------------------------------------
    // FileSuffix / GetTileId (static path helpers)
    // ------------------------------------------------------------------

    /// <summary>
    /// Gets the directory-like filename suffix for a graph id. Faithful port of
    /// <c>FileSuffix(graphid, suffix, is_file_path, tiles)</c>.
    /// </summary>
    public static string FileSuffix(
        GraphId graphid,
        string suffix = SuffixNonCompressed,
        bool isFilePath = true,
        TileLevel? tiles = null)
    {
        // Validate the level.
        if ((tiles is not null && tiles.Level != graphid.Level()) ||
            (tiles is null && graphid.Level() >= TileHierarchy.Levels().Count &&
             graphid.Level() != TileHierarchy.GetTransitLevel().Level))
        {
            throw new InvalidOperationException(
                "Could not compute FileSuffix for GraphId with invalid level: " + graphid);
        }

        TileLevel level = tiles ?? (graphid.Level() == TileHierarchy.GetTransitLevel().Level
            ? TileHierarchy.GetTransitLevel()
            : TileHierarchy.Levels()[(int)graphid.Level()]);

        uint maxId = (uint)((level.Tiles.Ncolumns() * level.Tiles.Nrows()) - 1);

        if (graphid.Tileid() > maxId)
        {
            throw new InvalidOperationException(
                "Could not compute FileSuffix for GraphId with invalid tile id:" + graphid);
        }

        int maxLength = (int)Math.Log10(Math.Max(1u, maxId)) + 1;
        int remainder = maxLength % 3;
        if (remainder != 0)
        {
            maxLength += 3 - remainder;
        }

        // tile-id string length including the '/' separators (one per group of 3).
        int tileIdStrLen = maxLength + (maxLength / 3);

        char separator = isFilePath ? Path.DirectorySeparatorChar : '/';

        var tileIdStr = new char[tileIdStrLen];
        for (int i = 0; i < tileIdStrLen; i++)
        {
            tileIdStr[i] = '0';
        }

        int ind = tileIdStrLen - 1;
        for (uint tileId = graphid.Tileid(); tileId != 0; tileId /= 10)
        {
            tileIdStr[ind--] = (char)('0' + (char)(tileId % 10));
            if ((tileIdStrLen - ind) % 4 == 0)
            {
                ind--; // skip a slot for the separator
            }
        }

        for (int sepInd = 0; sepInd < tileIdStrLen; sepInd += 4)
        {
            tileIdStr[sepInd] = separator;
        }

        return graphid.Level().ToString(System.Globalization.CultureInfo.InvariantCulture)
               + new string(tileIdStr) + suffix;
    }

    /// <summary>
    /// Gets the tile id from a full file path. Faithful port of <c>GetTileId(const std::string&amp;)</c>.
    /// </summary>
    public static GraphId GetTileId(string fname)
    {
        char sep = Path.DirectorySeparatorChar;
        bool IsAllowed(char c, bool includeSep)
            => (includeSep && c == sep) || (c >= '0' && c <= '9');

        // We require slashes.
        int pos = fname.LastIndexOf(sep);
        if (pos < 0)
        {
            throw new InvalidOperationException("Invalid tile path: " + fname);
        }

        // Swallow numbers until the end or a dot.
        for (; pos < fname.Length; ++pos)
        {
            if (!IsAllowed(fname[pos], true))
            {
                break;
            }
        }

        // If we didn't reach the end and it wasn't a dot then this isn't valid.
        if (pos != fname.Length && fname[pos] != '.')
        {
            throw new InvalidOperationException("Invalid tile path: " + fname);
        }

        // Run backwards while allowed chars (no separator now), stopping per group of 3 (or 1) digits.
        var digits = new List<uint>();
        int last = pos;
        while (--pos < last)
        {
            if (pos < 0)
            {
                break;
            }

            char c = fname[pos];
            if (!IsAllowed(c, false))
            {
                throw new InvalidOperationException("Invalid tile path: " + fname);
            }

            if (pos == 0 || fname[pos - 1] == sep)
            {
                int dist = last - pos;
                if (dist != 3 && dist != 1)
                {
                    throw new InvalidOperationException("Invalid tile path: " + fname);
                }

                uint i = uint.Parse(fname.Substring(pos, dist), System.Globalization.CultureInfo.InvariantCulture);
                digits.Add(i);
                if (dist == 1)
                {
                    break;
                }

                last = --pos;
            }
        }

        // If the first thing isn't a valid level, bail.
        uint levelDigit = digits[digits.Count - 1];
        if (levelDigit >= TileHierarchy.Levels().Count &&
            levelDigit != TileHierarchy.GetTransitLevel().Level)
        {
            throw new InvalidOperationException("Invalid tile path: " + fname);
        }

        uint levelVal = digits[digits.Count - 1];
        digits.RemoveAt(digits.Count - 1);
        TileLevel tileLevel = levelVal == TileHierarchy.GetTransitLevel().Level
            ? TileHierarchy.GetTransitLevel()
            : TileHierarchy.Levels()[(int)levelVal];

        uint maxId = (uint)((tileLevel.Tiles.Ncolumns() * tileLevel.Tiles.Nrows()) - 1);
        int parts = (int)Math.Log10(Math.Max(1u, maxId)) + 1;
        if (parts % 3 != 0)
        {
            parts += 3 - (parts % 3);
        }

        parts /= 3;

        if (digits.Count != parts)
        {
            throw new InvalidOperationException("Invalid tile path: " + fname);
        }

        int multiplier = 1;
        uint id = 0;
        foreach (uint digit in digits)
        {
            id += digit * (uint)multiplier;
            multiplier *= 1000;
        }

        if (id > maxId)
        {
            throw new InvalidOperationException("Invalid tile path: " + fname);
        }

        return new GraphId(id, levelVal, 0);
    }

    // ------------------------------------------------------------------
    // Private record readers (MemoryMarshal over the blittable structs)
    // ------------------------------------------------------------------

    private NodeInfo ReadNode(int idx)
        => MemoryMarshal.Read<NodeInfo>(_blob.AsSpan(_nodesOffset + (idx * NodeInfoSize), NodeInfoSize));

    private NodeTransition ReadTransition(int idx)
        => MemoryMarshal.Read<NodeTransition>(
            _blob.AsSpan(_transitionsOffset + (idx * NodeTransitionSize), NodeTransitionSize));

    private DirectedEdgeRec ReadDirectedEdge(int idx)
        => MemoryMarshal.Read<DirectedEdgeRec>(
            _blob.AsSpan(_directedEdgesOffset + (idx * DirectedEdgeSize), DirectedEdgeSize));

    private DirectedEdgeExtRec ReadDirectedEdgeExt(int idx)
        => MemoryMarshal.Read<DirectedEdgeExtRec>(
            _blob.AsSpan(_extDirectedEdgesOffset + (idx * DirectedEdgeExtSize), DirectedEdgeExtSize));

    private AccessRestriction ReadAccessRestriction(int idx)
        => MemoryMarshal.Read<AccessRestriction>(
            _blob.AsSpan(_accessRestrictionsOffset + (idx * AccessRestrictionSize), AccessRestrictionSize));

    private TransitDeparture ReadTransitDeparture(int idx)
        => MemoryMarshal.Read<TransitDeparture>(
            _blob.AsSpan(_departuresOffset + (idx * TransitDepartureSize), TransitDepartureSize));

    private TransitStop ReadTransitStop(int idx)
        => MemoryMarshal.Read<TransitStop>(
            _blob.AsSpan(_stopsOffset + (idx * TransitStopSize), TransitStopSize));

    private TransitRoute ReadTransitRoute(int idx)
        => MemoryMarshal.Read<TransitRoute>(
            _blob.AsSpan(_routesOffset + (idx * TransitRouteSize), TransitRouteSize));

    private TransitSchedule ReadTransitSchedule(int idx)
        => MemoryMarshal.Read<TransitSchedule>(
            _blob.AsSpan(_schedulesOffset + (idx * TransitScheduleSize), TransitScheduleSize));

    private TransitTransfer ReadTransitTransfer(int idx)
        => MemoryMarshal.Read<TransitTransfer>(
            _blob.AsSpan(_transfersOffset + (idx * TransitTransferSize), TransitTransferSize));

    private static IReadOnlyList<T> ReadTransitRecords<T>(int count, Func<int, T> reader)
    {
        var records = new List<T>(count);
        for (int index = 0; index < count; index++)
        {
            records.Add(reader(index));
        }

        return records;
    }

    private Sign ReadSign(int idx)
        => MemoryMarshal.Read<Sign>(_blob.AsSpan(_signsOffset + (idx * SignSize), SignSize));

    private TurnLanesRec ReadTurnLanes(int idx)
        => MemoryMarshal.Read<TurnLanesRec>(_blob.AsSpan(_turnLanesOffset + (idx * TurnLanesSize), TurnLanesSize));

    private AdminRec ReadAdmin(int idx)
        => MemoryMarshal.Read<AdminRec>(_blob.AsSpan(_adminsOffset + (idx * AdminSize), AdminSize));

    private LaneConnectivity ReadLaneConnectivity(int idx)
        => MemoryMarshal.Read<LaneConnectivity>(_blob.AsSpan(_laneConnectivityOffset + (idx * 24), 24));

    // EdgeInfo for the "names only" convenience accessors. The names list buffer passed to EdgeInfo
    // is a copy starting at the textlist offset, so name offsets (relative to textlist start) index
    // it correctly; the edge-info record itself is read from the blob at its absolute offset.
    private EdgeInfo EdgeInfoForNames(DirectedEdge edge)
    {
        byte[] namesList = TextListBuffer();
        return new EdgeInfoRec(_blob, _edgeInfoOffset + (int)edge.EdgeInfoOffset, namesList, namesList.Length);
    }

    private byte[]? _textListBufferCache;

    private byte[] TextListBuffer()
    {
        if (_textListBufferCache is null)
        {
            int len = checked((int)_textListSize);
            var buf = new byte[len];
            Array.Copy(_blob, _textListOffset, buf, 0, len);
            _textListBufferCache = buf;
        }

        return _textListBufferCache;
    }

    private int LowerBoundSignIndex(uint idx, int count)
    {
        int found = count;
        int low = 0;
        int high = count - 1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            uint signIdx = ReadSign(mid).Index;
            if (idx == signIdx)
            {
                found = mid;
                high = mid - 1;
            }
            else if (idx < signIdx)
            {
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        return found;
    }

    private static LinguisticTextHeader ReadLinguisticHeader(byte[] buffer, int offset)
        => new(ReadUInt32(buffer, offset));

    private static string ReadCString(byte[] buffer, int offset)
    {
        int len = 0;
        while (buffer[offset + len] != 0)
        {
            len++;
        }

        var sb = new StringBuilder(len);
        for (int k = 0; k < len; k++)
        {
            sb.Append((char)buffer[offset + k]);
        }

        return sb.ToString();
    }

    private static ushort ReadUInt16(byte[] buffer, int offset)
        => (ushort)(buffer[offset] | (buffer[offset + 1] << 8));

    private static uint ReadUInt32(byte[] buffer, int offset)
        => (uint)(buffer[offset]
                  | (buffer[offset + 1] << 8)
                  | (buffer[offset + 2] << 16)
                  | (buffer[offset + 3] << 24));

    private static ulong ReadUInt64(byte[] buffer, int offset)
        => ReadUInt32(buffer, offset) | ((ulong)ReadUInt32(buffer, offset + 4) << 32);

    /// <summary>
    /// Concrete <see cref="GraphMemory"/> backed by an owned byte array. Mirrors the C++
    /// <c>VectorGraphMemory</c> (which owns a <c>std::vector&lt;char&gt;</c>).
    /// </summary>
    private sealed class VectorGraphMemory : GraphMemory
    {
        public VectorGraphMemory(byte[] memory)
            : base(memory, 0, memory.Length)
        {
        }
    }
}
