// Mutable in-memory tile model used by GraphEnhancer. Mirrors the role of the C++
// GraphTileBuilder when it is constructed with deserialize=true: it loads an existing tile blob
// (the GraphBuilder.Build output) into mutable NodeInfo / DirectedEdge arrays plus the variable
// sections, lets the enhancer mutate nodes/edges in place and replace the access-restriction and
// turn-lane sections, then reserializes to a byte-compatible blob the Baldr GraphTile reader parses.
//
// Source: F:/github/valhalla/src/mjolnir/graphtilebuilder.cc (the deserialize ctor + StoreTileData
// section ordering). The non-mutated sections (transitions, directed-edge ext, signs, admins, edge
// info, lane connectivity) are carried through verbatim from the original blob, and the text list is
// extended exactly as the C++ GraphTileBuilder::AddName does (appending only new turn-lane names the
// enhancer produces). This guarantees the enhanced tile is byte-compatible with the reader and
// preserves every offset that does not change.
//
// PORT-NOTE: GraphEnhancer is single-threaded here, so the model is a plain managed object. The
// NodeInfo / DirectedEdge structs are value types; NodeRef/DirectedEdgeRef expose them by reference
// out of the backing arrays so the enhancer's in-place mutation matches the C++ node_builder() /
// directededge_builder() references.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Mutable, reserializable view over a baldr tile blob, used by <see cref="GraphEnhancer"/>. Holds
/// the nodes and directed edges as mutable arrays and the remaining sections as carried-through
/// bytes, mirroring a deserialized C++ <c>GraphTileBuilder</c>.
/// </summary>
internal sealed partial class TileModel
{
    private const int GraphTileHeaderSize = GraphTileHeader.HeaderSize; // 272
    private const int NodeInfoSize = 32;
    private const int NodeTransitionSize = 8;
    private const int DirectedEdgeSize = DirectedEdge.SizeOf;       // 48
    private const int DirectedEdgeExtSize = DirectedEdgeExt.SizeOf; // 8
    private const int AccessRestrictionSize = 16;
    private const int SignSize = 8;
    private const int TurnLanesSize = 8;
    private const int AdminSize = 16;
    private const int LaneConnectivitySize = 24;

    // The parsed reader (for edge info / shapes / admins / access-restriction / turn-lane lookups).
    private readonly GraphTile _tile;

    // The original blob (for carrying non-mutated sections through verbatim).
    private readonly byte[] _blob;

    // Mutable node + directed edge arrays (the enhancer edits these by ref).
    private readonly NodeInfo[] _nodes;
    private readonly DirectedEdge[] _edges;

    // Replacement sections produced by the enhancer.
    private List<AccessRestriction>? _newAccessRestrictions;
    private List<TurnLanes>? _newTurnLanes;

    // Text-list extension: the enhancer's UpdateTurnLanes can AddName new turn-lane strings, which
    // append to the text list (exactly as the C++ builder). We keep the original text bytes plus an
    // append buffer + offset map so name offsets stay stable for existing names.
    private readonly List<byte> _appendedText = new();
    private readonly Dictionary<string, uint> _newTextOffsets = new(StringComparer.Ordinal);
    private readonly uint _originalTextListSize;

    /// <summary>
    /// Parses a tile blob into the mutable model. Faithful to the C++ deserialize constructor's
    /// section layout (the blob the Baldr reader parses).
    /// </summary>
    /// <param name="tileId">Tile base GraphId.</param>
    /// <param name="blob">The serialized tile blob.</param>
    public TileModel(GraphId tileId, byte[] blob)
    {
        _blob = blob ?? throw new ArgumentNullException(nameof(blob));
        _tile = GraphTile.Create(tileId, blob);

        int nodeCount = (int)_tile.Header().Nodecount();
        int edgeCount = (int)_tile.Header().Directededgecount();

        _nodes = new NodeInfo[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            _nodes[i] = _tile.Node(i);
        }

        _edges = new DirectedEdge[edgeCount];
        for (int i = 0; i < edgeCount; i++)
        {
            _edges[i] = _tile.DirectedEdge(i);
        }

        _originalTextListSize = _tile.Header().LaneConnectivityOffset() - _tile.Header().TextlistOffset();
    }

    /// <summary>Tile base GraphId. Faithful port of <c>id()</c>.</summary>
    public GraphId Id => _tile.Id();

    /// <summary>Number of nodes in the tile.</summary>
    public int NodeCount => _nodes.Length;

    /// <summary>Number of directed edges in the tile.</summary>
    public int EdgeCount => _edges.Length;

    /// <summary>Base (SW corner) lat/lon of the tile.</summary>
    public PointLL BaseLl => _tile.BaseLl();

    /// <summary>Bounding box of the tile. Faithful port of <c>BoundingBox()</c>.</summary>
    public Aabb2T<double> BoundingBox() => _tile.BoundingBox();

    /// <summary>Access restriction count before enhancement (header value). Mirrors <c>ar_before</c>.</summary>
    public uint AccessRestrictionCountBefore => _tile.Header().AccessRestrictionCount();

    /// <summary>Turn lane count before enhancement (header value). Mirrors <c>tl_before</c>.</summary>
    public uint TurnLaneCountBefore => _tile.Header().TurnlaneCount();

    /// <summary>Gets the node at the given index by reference (the enhancer mutates it in place).</summary>
    public ref NodeInfo NodeRef(int idx) => ref _nodes[idx];

    /// <summary>Gets the directed edge at the given index by reference (the enhancer mutates it).</summary>
    public ref DirectedEdge DirectedEdgeRef(int idx) => ref _edges[idx];

    /// <summary>
    /// Gets the shape of an edge (a fresh mutable list, so callers may reverse it). The shape is
    /// stored forward in edge info; callers reverse based on <c>directededge.Forward</c>.
    /// </summary>
    public List<PointLL> EdgeShape(DirectedEdge edge) => new(_tile.EdgeInfo(edge).Shape());

    /// <summary>Gets the edge info for a directed edge. Faithful port of <c>edgeinfo</c>.</summary>
    public EdgeInfo EdgeInfoFor(DirectedEdge edge) => _tile.EdgeInfo(edge);

    /// <summary>Gets the access restrictions for an edge index. Faithful port of <c>GetAccessRestrictions</c>.</summary>
    public (IReadOnlyList<AccessRestriction> Restrictions, int Start) GetAccessRestrictions(uint idx)
        => _tile.GetAccessRestrictions(idx);

    /// <summary>Gets the text-list offset for the turn lanes of an edge. Faithful port of <c>turnlanes_offset</c>.</summary>
    public uint TurnLanesOffset(uint idx) => _tile.TurnLanesOffset(idx);

    /// <summary>Gets the country iso code for an admin index.</summary>
    public string AdminCountryIso(int adminIndex) => _tile.Admin(adminIndex).CountryIsoCode();

    /// <summary>Gets the state iso code for an admin index.</summary>
    public string AdminStateIso(int adminIndex) => _tile.Admin(adminIndex).StateIsoCode();

    /// <summary>
    /// Reads the name (turn-lane tags string) at a text-list offset. Resolves into the original text
    /// list or the appended buffer. Faithful port of <c>osmdata.name_offset_map.name</c> as used for
    /// turn lanes after the builder rewrote it into the tile text list.
    /// </summary>
    public string NameAt(uint offset)
    {
        if (offset == 0)
        {
            return string.Empty;
        }

        if (offset < _originalTextListSize)
        {
            return _tile.GetName(offset);
        }

        // Offset into the appended-text buffer.
        int local = (int)(offset - _originalTextListSize);
        return ReadCString(_appendedText, local);
    }

    /// <summary>
    /// Adds a name to the text list and returns its offset. Faithful port of
    /// <c>GraphTileBuilder::AddName</c> (existing-name dedup against both the original text list and
    /// previously appended names).
    /// </summary>
    public uint AddName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return 0;
        }

        // Existing name in the original text list?
        if (TryFindOriginalText(name, out uint existing))
        {
            return existing;
        }

        if (_newTextOffsets.TryGetValue(name, out uint appendedOffset))
        {
            return appendedOffset;
        }

        uint offset = _originalTextListSize + (uint)_appendedText.Count;
        for (int i = 0; i < name.Length; i++)
        {
            _appendedText.Add((byte)name[i]);
        }

        _appendedText.Add(0); // null terminator
        _newTextOffsets[name] = offset;
        return offset;
    }

    /// <summary>Replaces the access restriction list (the enhancer rebuilds it). Faithful port of <c>AddAccessRestrictions</c>.</summary>
    public void SetAccessRestrictions(List<AccessRestriction> accessRestrictions) =>
        _newAccessRestrictions = accessRestrictions;

    /// <summary>Replaces the turn lane list (the enhancer rebuilds it). Faithful port of <c>AddTurnLanes</c>.</summary>
    public void SetTurnLanes(List<TurnLanes> turnLanes) => _newTurnLanes = turnLanes;

    private bool TryFindOriginalText(string name, out uint offset)
    {
        // Scan the original text list for an exact null-terminated match. The list is small for the
        // auto/truck build; this matches the C++ text-offset map semantics (existing names dedup).
        uint textlistOffset = _tile.Header().TextlistOffset();
        for (uint p = 0; p < _originalTextListSize;)
        {
            string s = _tile.GetName(p);
            if (s == name)
            {
                offset = p;
                return true;
            }

            p += (uint)(System.Text.Encoding.ASCII.GetByteCount(s) + 1);
        }

        _ = textlistOffset;
        offset = 0;
        return false;
    }

    private static string ReadCString(List<byte> buffer, int offset)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = offset; i < buffer.Count && buffer[i] != 0; i++)
        {
            sb.Append((char)buffer[i]);
        }

        return sb.ToString();
    }
}
