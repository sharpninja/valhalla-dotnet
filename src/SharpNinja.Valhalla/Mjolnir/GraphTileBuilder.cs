// Faithful C# port of Valhalla mjolnir graphtilebuilder.h + src/mjolnir/graphtilebuilder.cc
// @ 3.8.3 commit a60c7cbfc83e073f50887cd27e0109d02e6b64e5
// (the WRITE side; the build/construct path plus the deserialize-existing-tile path + complex
// restriction serialization used by the RestrictionBuilder - excludes transit/bss/elevation/
// predicted-speed updates, JSON, and bin edges, which are out of scope for the auto/truck graph
// build).
// Sources:
//   F:/github/valhalla/valhalla/mjolnir/graphtilebuilder.h
//   F:/github/valhalla/src/mjolnir/graphtilebuilder.cc
//
// CRITICAL FIDELITY: StoreTileData emits the tile blob in EXACTLY the section order, with the
// same offsets and 8-byte word padding, that the ported Baldr GraphTile reader parses. The blob is:
//   [GraphTileHeader (272 bytes)]
//   [NodeInfo[]]            (32 bytes each)
//   [NodeTransition[]]      (8 bytes each)       - always empty in the initial build
//   [DirectedEdge[]]        (48 bytes each)
//   [DirectedEdgeExt[]]     (8 bytes each)       - only if present (not in the initial build)
//   [AccessRestriction[]]   (16 bytes each)      - sorted
//   [TransitDeparture/Stop/Route/Schedule[]]     - always empty (transit excluded)
//   [Sign[]]                (8 bytes each)        - stable-sorted
//   [TurnLanes[]]           (8 bytes each)
//   [Admin[]]               (16 bytes each)
//   [complex restriction forward/reverse]         - 24-byte record + via GraphIds (added by the
//                                                   RestrictionBuilder; empty in the initial build)
//   [EdgeInfo[]]            (variable, 4-byte aligned each)
//   [text list]            (null-terminated strings)
//   [padding to 8-byte boundary]
//   [LaneConnectivity[]]    (24 bytes each)       - sorted

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Builds a graph tile and serializes it to a byte-compatible baldr tile blob. Faithful port of the
/// build path of the C++ <c>class GraphTileBuilder</c> (the non-deserialize constructor +
/// <c>StoreTileData</c> + the Add* helpers used by the initial graph build).
/// </summary>
public sealed partial class GraphTileBuilder
{
    private const int GraphTileHeaderSize = GraphTileHeader.HeaderSize; // 272
    private const int NodeInfoSize = 32;
    private const int NodeTransitionSize = 8;
    private const int DirectedEdgeSize = DirectedEdge.SizeOf;        // 48
    private const int DirectedEdgeExtSize = DirectedEdgeExt.SizeOf;  // 8
    private const int AccessRestrictionSize = 16;
    private const int SignSize = 8;
    private const int TurnLanesSize = 8;
    private const int AdminSize = 16;
    private const int LaneConnectivitySize = 24;

    private readonly GraphTileHeader _headerBuilder = new();

    private readonly List<NodeInfo> _nodesBuilder = new();
    private readonly List<DirectedEdge> _directedEdgesBuilder = new();
    private readonly List<DirectedEdgeExt> _directedEdgesExtBuilder = new();
    private readonly List<NodeTransition> _transitionsBuilder = new();
    private readonly List<AccessRestriction> _accessRestrictionBuilder = new();
    private readonly List<Sign> _signsBuilder = new();
    private readonly List<Admin> _adminsBuilder = new();
    private readonly Dictionary<string, ulong> _adminInfoOffsetMap = new(StringComparer.Ordinal);
    private readonly List<TurnLanes> _turnlanesBuilder = new();
    private readonly List<LaneConnectivity> _laneConnectivityBuilder = new();

    // The forward / reverse complex restriction lists (empty in the initial build; populated by the
    // RestrictionBuilder over a deserialized tile).
    private readonly List<ComplexRestrictionBuilder> _complexRestrictionForwardBuilder = new();
    private readonly List<ComplexRestrictionBuilder> _complexRestrictionReverseBuilder = new();

    // Edge info offset and maps.
    private uint _edgeInfoOffset;
    private readonly Dictionary<(uint, ulong, ulong), uint> _edgeOffsetMap = new();
    private readonly List<EdgeInfoBuilder> _edgeinfoList = new();

    // Text list offset and map.
    private uint _textListOffset;
    private readonly Dictionary<string, uint> _textOffsetMap = new(StringComparer.Ordinal);
    private readonly List<string> _textListBuilder = new();

    // The source tile when deserializing (null for a fresh build). The C++ GraphTileBuilder derives
    // from GraphTile, so methods like edgeinfo(de) read the original tile data; here we keep the
    // original tile to serve those reads.
    private readonly GraphTile? _sourceTile;

    /// <summary>
    /// Constructs a fresh tile builder for the given GraphId (the C++ non-deserialize path). Adds
    /// the empty-string text entry at offset 0 and a dummy admin record at index 0.
    /// </summary>
    /// <param name="graphid">GraphId (tile base) for the tile being built.</param>
    public GraphTileBuilder(GraphId graphid)
    {
        _headerBuilder.SetGraphid(graphid);

        // Not deserializing: create builders for everything.
        _textListBuilder.Add(string.Empty);
        _textOffsetMap[string.Empty] = 0;
        _textListOffset = 1;

        // Add a dummy admin record at index 0 to be used if admin records are not used/created.
        AddAdmin("None", "None", string.Empty, string.Empty);
    }

    /// <summary>
    /// Constructs a tile builder from an existing tile, deserializing every record into builders so
    /// the tile can be modified (directed-edge flags updated, complex restrictions added) and then
    /// re-serialized with <see cref="StoreTileData()"/>. Faithful port of the C++
    /// <c>GraphTileBuilder(tile_dir, graphid, deserialize = true)</c> constructor.
    /// </summary>
    /// <param name="tile">The existing tile to deserialize.</param>
    public GraphTileBuilder(GraphTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);

        _sourceTile = tile;
        GraphId graphid = tile.Header().Graphid();

        // Copy tile header to the builder; always set the graphid.
        _headerBuilder.CopyFrom(tile.Header());
        _headerBuilder.SetGraphid(graphid);

        // Street name info. Unique set of offsets into the text list (sorted; offset 0 always present).
        var nameOffsets = new SortedSet<uint>();
        var taggedOffsets = new HashSet<uint>();
        nameOffsets.Add(0);

        // Copy nodes to the builder list.
        uint nodeCount = tile.Header().Nodecount();
        for (uint i = 0; i < nodeCount; i++)
        {
            _nodesBuilder.Add(tile.Node((int)i));
        }

        // Copy node transitions to the builder list.
        uint transCount = tile.Header().Transitioncount();
        for (uint i = 0; i < transCount; i++)
        {
            _transitionsBuilder.Add(tile.Transition(i));
        }

        // Copy directed edges to the builder list.
        uint edgeCount = tile.Header().Directededgecount();
        for (uint i = 0; i < edgeCount; i++)
        {
            _directedEdgesBuilder.Add(tile.DirectedEdge((int)i));
        }

        // Add extended directed edge attributes (if available).
        if (tile.Header().HasExtDirectededge())
        {
            for (uint i = 0; i < edgeCount; i++)
            {
                _directedEdgesExtBuilder.Add(tile.ExtDirectedEdge(new GraphId(graphid.Tileid(), graphid.Level(), i)));
            }
        }

        // Create access restriction list.
        _accessRestrictionBuilder.AddRange(tile.GetAllAccessRestrictions());

        // Create sign builders and add their text offsets to the set.
        foreach (Sign sign in tile.GetAllSigns())
        {
            nameOffsets.Add(sign.TextOffset);
            _signsBuilder.Add(sign);
        }

        // Create turn lane builders (serialize_turn_lanes = true: offsets are real text offsets).
        foreach (TurnLanes tl in tile.GetAllTurnLanes())
        {
            nameOffsets.Add(tl.TextOffset);
            _turnlanesBuilder.Add(tl);
        }

        // Create admin builders and add their text offsets to the set.
        uint adminCount = tile.Header().Admincount();
        for (int i = 0; i < adminCount; i++)
        {
            Admin admin = tile.Admin(i);
            _adminsBuilder.Add(new Admin(admin.CountryOffset, admin.StateOffset, admin.CountryIsoCode(), admin.StateIsoCode()));
            nameOffsets.Add(admin.CountryOffset);
            nameOffsets.Add(admin.StateOffset);
        }

        // Create an ordered map: edge info offset -> edge length (length needed to read elevation).
        var edgeInfoOffsets = new SortedDictionary<uint, uint>();
        foreach (DirectedEdge de in _directedEdgesBuilder)
        {
            edgeInfoOffsets[(uint)de.EdgeInfoOffset] = de.Length;
        }

        // EdgeInfo. Create the list of EdgeInfoBuilders and add their name offsets to the set.
        _edgeInfoOffset = 0;
        foreach (KeyValuePair<uint, uint> entry in edgeInfoOffsets)
        {
            uint offset = entry.Key;
            if (offset != _edgeInfoOffset)
            {
                throw new InvalidOperationException(
                    "EdgeInfo offsets incorrect when reading GraphTile: stored=" + offset +
                    " current=" + _edgeInfoOffset);
            }

            EdgeInfo ei = tile.EdgeInfoAtOffset(offset);
            var eib = new EdgeInfoBuilder();
            eib.SetWayId(ei.WayId);
            eib.SetMeanElevation(ei.MeanElevation);
            eib.SetBikeNetwork(ei.BikeNetwork);
            eib.SetSpeedLimit(ei.SpeedLimit);
            for (uint nm = 0; nm < ei.NameCount; nm++)
            {
                NameInfo info = ei.GetNameInfo((byte)nm);
                nameOffsets.Add(info.NameOffset);
                if (info.Tagged)
                {
                    taggedOffsets.Add(info.NameOffset);
                }

                eib.AddNameInfo(info);
            }

            eib.SetEncodedShape(ei.EncodedShape());

            if (ei.HasElevation)
            {
                List<sbyte> encoded = ei.EncodedElevation(entry.Value, out _);
                eib.SetEncodedElevation(encoded);
                eib.SetHasElevation(true);
            }

            _edgeInfoOffset += (uint)eib.SizeOf();
            _edgeinfoList.Add(eib);
        }

        // Text list. Reconstruct each entry by reading the bytes between consecutive unique offsets.
        byte[] textList = tile.TextListRaw();
        _textListBuilder.Clear();
        _textOffsetMap.Clear();
        _textListOffset = 0;
        var offsetArray = new List<uint>(nameOffsets);
        for (int idx = 0; idx < offsetArray.Count; idx++)
        {
            uint thisOffset = offsetArray[idx];
            int width;
            if (idx + 1 < offsetArray.Count)
            {
                // Non-last entry: width is the gap to the next offset.
                width = (int)(offsetArray[idx + 1] - thisOffset);
            }
            else if (taggedOffsets.Contains(thisOffset))
            {
                // Last tagged entry: use TaggedValueSize to skip alignment padding bytes.
                width = EdgeInfo.TaggedValueSize(textList, (int)thisOffset);
            }
            else
            {
                width = textList.Length - (int)thisOffset;
            }

            // Keep the bytes for this entry, removing the null terminator (added back in StoreTileData).
            string entryText = BytesToString(textList, (int)thisOffset, width - 1);
            _textListBuilder.Add(entryText);
            if (!_textOffsetMap.ContainsKey(entryText))
            {
                _textOffsetMap[entryText] = thisOffset;
            }

            _textListOffset += (uint)(width); // length-of-entry + null terminator
        }

        // Lane connectivity.
        _laneConnectivityBuilder.AddRange(tile.GetAllLaneConnectivity());

        // Complex restrictions (forward / reverse).
        _complexRestrictionForwardBuilder.AddRange(
            DeserializeRestrictions(tile.ComplexRestrictionForwardRaw()));
        _complexRestrictionReverseBuilder.AddRange(
            DeserializeRestrictions(tile.ComplexRestrictionReverseRaw()));
    }

    // Deserialize a complex restriction section: a sequence of [3 uint64 words][via_count GraphIds].
    // Faithful port of the anonymous DeserializeRestrictions in graphtilebuilder.cc.
    private static List<ComplexRestrictionBuilder> DeserializeRestrictions(byte[] restrictions)
    {
        var builders = new List<ComplexRestrictionBuilder>();
        int offset = 0;
        while (offset < restrictions.Length)
        {
            ulong word0 = ReadUInt64(restrictions, offset);
            ulong word1 = ReadUInt64(restrictions, offset + 8);
            ulong word2 = ReadUInt64(restrictions, offset + 16);
            ComplexRestriction cr = ComplexRestriction.FromRawWords(word0, word1, word2);
            var builder = new ComplexRestrictionBuilder(cr);
            byte viaCount = cr.ViaCount();
            if (viaCount > 0)
            {
                var vias = new List<GraphId>(viaCount);
                int viaPtr = offset + ComplexRestriction.SizeOfStruct;
                for (uint i = 0; i < viaCount; i++)
                {
                    vias.Add(new GraphId(ReadUInt64(restrictions, viaPtr + ((int)i * ComplexRestriction.SizeOfGraphId))));
                }

                builder.SetViaList(vias);
            }

            builders.Add(builder);
            offset += cr.SizeOf();
        }

        return builders;
    }

    private static ulong ReadUInt64(byte[] buffer, int offset)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++)
        {
            v |= (ulong)buffer[offset + i] << (8 * i);
        }

        return v;
    }

    private static string BytesToString(byte[] buffer, int offset, int length)
    {
        var sb = new System.Text.StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            sb.Append((char)buffer[offset + i]);
        }

        return sb.ToString();
    }

    /// <summary>Gets the header builder. Faithful port of <c>header_builder()</c>.</summary>
    public GraphTileHeader HeaderBuilder => _headerBuilder;

    /// <summary>Gets the header builder. Faithful port of the C++ <c>header()</c> on the builder.</summary>
    public GraphTileHeader Header() => _headerBuilder;

    /// <summary>Gets the current list of node builders. Faithful port of <c>nodes()</c>.</summary>
    public List<NodeInfo> Nodes => _nodesBuilder;

    /// <summary>Gets the current list of directed edge builders. Faithful port of <c>directededges()</c>.</summary>
    public List<DirectedEdge> DirectedEdges => _directedEdgesBuilder;

    /// <summary>
    /// Gets the current (mutable) list of node transition builders. Faithful port of
    /// <c>transitions()</c> (used by the HierarchyBuilder / ShortcutBuilder to add up/down
    /// transitions and to copy transitions from a base tile).
    /// </summary>
    public List<NodeTransition> Transitions => _transitionsBuilder;

    /// <summary>Gets the current list of signs (read-only view).</summary>
    public IReadOnlyList<Sign> Signs => _signsBuilder;

    /// <summary>Gets the current list of admins (read-only view).</summary>
    public IReadOnlyList<Admin> Admins => _adminsBuilder;

    /// <summary>Gets the current list of turn lanes (read-only view).</summary>
    public IReadOnlyList<TurnLanes> TurnLanesList => _turnlanesBuilder;

    /// <summary>Gets the current list of access restrictions (read-only view).</summary>
    public IReadOnlyList<AccessRestriction> AccessRestrictions => _accessRestrictionBuilder;

    /// <summary>Gets the admin builder at the given index. Faithful port of <c>admins_builder(idx)</c>.</summary>
    public Admin AdminsBuilder(int idx)
    {
        if (idx < _adminsBuilder.Count)
        {
            return _adminsBuilder[idx];
        }

        throw new InvalidOperationException("GraphTileBuilder admin index is out of bounds");
    }

    // ------------------------------------------------------------------
    // Builder element accessors (used by the RestrictionBuilder on a deserialized tile).
    // C# structs are value types in List<T>, so the get-mutate-set pattern is exposed explicitly
    // rather than via a returned reference (the C++ directededge_builder/node_builder return refs).
    // ------------------------------------------------------------------

    /// <summary>Gets a copy of the directed edge builder at the index. Faithful port of <c>directededge_builder(idx)</c> (read).</summary>
    public DirectedEdge DirectedEdgeBuilder(int idx)
    {
        if (idx < _directedEdgesBuilder.Count)
        {
            return _directedEdgesBuilder[idx];
        }

        throw new InvalidOperationException("GraphTile DirectedEdge id out of bounds");
    }

    /// <summary>Writes back a mutated directed edge builder at the index. Completes the get-mutate-set of <c>directededge_builder(idx)</c>.</summary>
    public void SetDirectedEdgeBuilder(int idx, DirectedEdge edge)
    {
        if (idx >= _directedEdgesBuilder.Count)
        {
            throw new InvalidOperationException("GraphTile DirectedEdge id out of bounds");
        }

        _directedEdgesBuilder[idx] = edge;
    }

    /// <summary>Gets a copy of the node builder at the index. Faithful port of <c>node_builder(idx)</c> (read).</summary>
    public NodeInfo NodeBuilder(int idx)
    {
        if (idx < _nodesBuilder.Count)
        {
            return _nodesBuilder[idx];
        }

        throw new InvalidOperationException("GraphTileBuilder NodeInfo index out of bounds");
    }

    /// <summary>Writes back a mutated node builder at the index. Completes the get-mutate-set of <c>node_builder(idx)</c>.</summary>
    public void SetNodeBuilder(int idx, NodeInfo node)
    {
        if (idx >= _nodesBuilder.Count)
        {
            throw new InvalidOperationException("GraphTileBuilder NodeInfo index out of bounds");
        }

        _nodesBuilder[idx] = node;
    }

    /// <summary>
    /// Gets the edge info for a directed edge from the original (deserialized) tile data. Faithful
    /// port of the C++ <c>tilebuilder.edgeinfo(&amp;directededge)</c> (inherited from GraphTile).
    /// </summary>
    public EdgeInfo EdgeInfoFor(DirectedEdge edge)
    {
        if (_sourceTile is null)
        {
            throw new InvalidOperationException("EdgeInfoFor requires a deserialized tile builder");
        }

        return _sourceTile.EdgeInfo(edge);
    }

    /// <summary>Adds a forward complex restriction. Faithful port of <c>AddForwardComplexRestriction</c>.</summary>
    public void AddForwardComplexRestriction(ComplexRestrictionBuilder res) =>
        _complexRestrictionForwardBuilder.Add(res);

    /// <summary>Adds a reverse complex restriction. Faithful port of <c>AddReverseComplexRestriction</c>.</summary>
    public void AddReverseComplexRestriction(ComplexRestrictionBuilder res) =>
        _complexRestrictionReverseBuilder.Add(res);

    /// <summary>Gets the current list of forward complex restrictions (read-only view).</summary>
    public IReadOnlyList<ComplexRestrictionBuilder> ComplexRestrictionForward => _complexRestrictionForwardBuilder;

    /// <summary>Gets the current list of reverse complex restrictions (read-only view).</summary>
    public IReadOnlyList<ComplexRestrictionBuilder> ComplexRestrictionReverse => _complexRestrictionReverseBuilder;

    /// <summary>Sets the tile creation date. Faithful port of <c>AddTileCreationDate</c>.</summary>
    public void AddTileCreationDate(uint tileCreationDate) => _headerBuilder.SetDateCreated(tileCreationDate);

    /// <summary>Adds an access restriction. Faithful port of <c>AddAccessRestriction</c>.</summary>
    public void AddAccessRestriction(AccessRestriction accessRestriction) =>
        _accessRestrictionBuilder.Add(accessRestriction);

    /// <summary>Adds lane connectivity records. Faithful port of <c>AddLaneConnectivity</c>.</summary>
    public void AddLaneConnectivity(List<LaneConnectivity> lc) => _laneConnectivityBuilder.AddRange(lc);

    /// <summary>
    /// Is there already edge info for this edge tuple. Faithful port of <c>HasEdgeInfo</c>.
    /// </summary>
    public bool HasEdgeInfo(uint edgeindex, GraphId nodea, GraphId nodeb, out uint edgeInfoOffset)
    {
        (uint, ulong, ulong) key = EdgeTuple(edgeindex, nodea, nodeb);
        if (_edgeOffsetMap.TryGetValue(key, out uint existing))
        {
            edgeInfoOffset = existing;
            return true;
        }

        edgeInfoOffset = 0;
        return false;
    }

    /// <summary>
    /// Adds a name to the text list and returns the offset (bytes) to it. Faithful port of
    /// <c>AddName</c>.
    /// </summary>
    public uint AddName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return 0;
        }

        if (_textOffsetMap.TryGetValue(name, out uint existing))
        {
            return existing;
        }

        uint offset = _textListOffset;
        _textListBuilder.Add(name);
        _textOffsetMap[name] = _textListOffset;
        _textListOffset += (uint)(ByteLength(name) + 1);
        return offset;
    }

    /// <summary>Adds admin info to the tile. Faithful port of <c>AddAdmin</c>.</summary>
    public uint AddAdmin(string countryName, string stateName, string countryIso, string stateIso)
    {
        string key = countryIso + stateIso + stateName;
        if (!_adminInfoOffsetMap.TryGetValue(key, out ulong existing))
        {
            uint countryOffset = AddName(countryName);
            uint stateOffset = AddName(stateName);
            _adminsBuilder.Add(new Admin(countryOffset, stateOffset, countryIso, stateIso));
            ulong index = (ulong)(_adminsBuilder.Count - 1);
            _adminInfoOffsetMap[key] = index;
            return (uint)index;
        }

        return (uint)existing;
    }

    /// <summary>
    /// Adds sign information. Faithful port of <c>AddSigns(idx, signs, linguistics)</c>. Linguistic
    /// records are written when a sign carries them (out of scope in the standard auto/truck build,
    /// where no signs have linguistic info; the logic is reproduced for completeness).
    /// </summary>
    public void AddSigns(uint idx, IReadOnlyList<SignInfo> signs, IReadOnlyList<string> linguistics)
    {
        for (int i = 0; i < signs.Count; ++i)
        {
            SignInfo sign = signs[i];
            if (!string.IsNullOrEmpty(sign.Text))
            {
                uint offset = AddName(sign.Text);
                _signsBuilder.Add(new Sign(idx, sign.Type, sign.IsRouteNum, sign.IsTagged, offset));

                if (sign.HasLinguistic)
                {
                    bool linguisticOnNode =
                        sign.Type == Sign.Type.JunctionName || sign.Type == Sign.Type.TollName;
                    uint count = (sign.LinguisticStartIndex + sign.LinguisticCount) - 1;
                    uint signOffset =
                        AddName(ProcessLinguisticHeader(sign.LinguisticStartIndex, count, linguistics, i));
                    _signsBuilder.Add(new Sign(idx, Sign.Type.Linguistic, linguisticOnNode, true, signOffset));
                }
            }
        }
    }

    /// <summary>Adds sign information. Faithful port of <c>AddSigns(idx, signs)</c>.</summary>
    public void AddSigns(uint idx, IReadOnlyList<SignInfo> signs)
    {
        foreach (SignInfo sign in signs)
        {
            if (!string.IsNullOrEmpty(sign.Text))
            {
                uint offset = AddName(sign.Text);
                _signsBuilder.Add(new Sign(idx, sign.Type, sign.IsRouteNum, sign.IsTagged, offset));
            }
        }
    }

    /// <summary>Adds turn lanes for a directed edge given a text string. Faithful port of <c>AddTurnLanes(idx, str)</c>.</summary>
    public void AddTurnLanes(uint idx, string str)
    {
        if (!string.IsNullOrEmpty(str))
        {
            uint offset = AddName(str);
            _turnlanesBuilder.Add(new TurnLanes(idx, offset));
        }
    }

    /// <summary>Adds turn lanes for a directed edge given a name-list index. Faithful port of <c>AddTurnLanes(idx, tl_idx)</c>.</summary>
    public void AddTurnLanes(uint idx, uint tlIdx) => _turnlanesBuilder.Add(new TurnLanes(idx, tlIdx));

    /// <summary>
    /// Adds the edge info to the tile, returning the offset (bytes) to it. Faithful port of the
    /// shape-container overload of <c>AddEdgeInfo</c>.
    /// </summary>
    public uint AddEdgeInfo(
        uint edgeindex,
        GraphId nodea,
        GraphId nodeb,
        ulong wayid,
        float elev,
        uint bn,
        uint spd,
        IReadOnlyList<Midgard.PointLL> lls,
        IReadOnlyList<string> names,
        IReadOnlyList<string> taggedValues,
        IReadOnlyList<string> linguistics,
        ushort types,
        out bool added,
        bool diffNames = false)
    {
        (uint, ulong, ulong) key = EdgeTuple(edgeindex, nodea, nodeb);
        if (diffNames || !_edgeOffsetMap.ContainsKey(key))
        {
            var edgeinfo = new EdgeInfoBuilder();
            edgeinfo.SetWayId(wayid);
            edgeinfo.SetMeanElevation(elev);
            edgeinfo.SetBikeNetwork(bn);
            edgeinfo.SetSpeedLimit(spd);
            edgeinfo.SetShape(lls);

            var nameInfoList = new List<NameInfo>(Math.Min(names.Count, EdgeInfoBuilder.MaxNamesPerEdge));
            int nameCount = 0;
            int location = 0;
            foreach (string name in names)
            {
                if (nameCount == EdgeInfoBuilder.MaxNamesPerEdge)
                {
                    break;
                }

                if (!string.IsNullOrEmpty(name))
                {
                    // ni.is_route_num_ = (types bit set); ni.tagged_ = 0
                    var info = new NameInfo(AddName(name), 0, (types & (1u << location)) != 0, false, 0);
                    nameInfoList.Add(info);
                    ++nameCount;
                }

                location++;
            }

            foreach (string name in taggedValues)
            {
                if (nameCount == EdgeInfoBuilder.MaxNamesPerEdge)
                {
                    break;
                }

                if (!string.IsNullOrEmpty(name))
                {
                    var info = new NameInfo(AddName(name), 0, false, true, 0);
                    nameInfoList.Add(info);
                    ++nameCount;
                }
            }

            ProcessTaggedValues(edgeindex, linguistics, ref nameCount, nameInfoList);

            edgeinfo.SetNameInfoList(nameInfoList);

            _edgeOffsetMap[key] = _edgeInfoOffset;
            uint currentEdgeOffset = _edgeInfoOffset;
            _edgeInfoOffset += (uint)edgeinfo.SizeOf();
            _edgeinfoList.Add(edgeinfo);

            added = true;
            return currentEdgeOffset;
        }

        added = false;
        return _edgeOffsetMap[key];
    }

    /// <summary>
    /// Process tagged (linguistic) values for the edge, appending a single combined linguistic
    /// tagged value. Faithful port of <c>ProcessTaggedValues</c>.
    /// </summary>
    public void ProcessTaggedValues(
        uint edgeindex,
        IReadOnlyList<string> names,
        ref int nameCount,
        List<NameInfo> nameInfoList)
    {
        char encodeTag = (char)(byte)TaggedValue.Linguistic;
        if (names.Count > 0)
        {
            if (nameCount != EdgeInfoBuilder.MaxNamesPerEdge)
            {
                var sb = new System.Text.StringBuilder();
                foreach (string name in names)
                {
                    sb.Append(name);
                }

                var ni = new NameInfo(AddName(encodeTag + sb.ToString()), 0, false, true, 0);
                nameInfoList.Add(ni);
                ++nameCount;
            }
        }
    }

    /// <summary>
    /// Serializes the tile to a byte blob (the in-memory equivalent of <c>StoreTileData</c>). Returns
    /// the complete, byte-compatible tile blob the Baldr reader can parse.
    /// </summary>
    public byte[] StoreTileData()
    {
        int serializedSize = GetSerializedSize();
        byte[] blob = GC.AllocateUninitializedArray<byte>(serializedSize);
        using var inMem = new MemoryStream(blob, writable: true);
        inMem.Position = GraphTileHeaderSize;

        // Write the nodes.
        _headerBuilder.SetNodecount((uint)_nodesBuilder.Count);
        foreach (NodeInfo node in _nodesBuilder)
        {
            WriteStruct(inMem, node);
        }

        // Write the node transitions.
        _headerBuilder.SetTransitioncount((uint)_transitionsBuilder.Count);
        foreach (NodeTransition t in _transitionsBuilder)
        {
            WriteStruct(inMem, t);
        }

        // Write the directed edges.
        _headerBuilder.SetDirectededgecount((uint)_directedEdgesBuilder.Count);
        foreach (DirectedEdge de in _directedEdgesBuilder)
        {
            WriteStruct(inMem, de);
        }

        // Write extended directed edge attributes if they exist.
        if (_directedEdgesExtBuilder.Count > 0 &&
            _directedEdgesExtBuilder.Count == _directedEdgesBuilder.Count)
        {
            _headerBuilder.SetHasExtDirectededge(true);
            foreach (DirectedEdgeExt ext in _directedEdgesExtBuilder)
            {
                WriteStruct(inMem, ext);
            }
        }

        // Sort and write the access restrictions.
        _headerBuilder.SetAccessRestrictionCount((uint)_accessRestrictionBuilder.Count);
        _accessRestrictionBuilder.Sort((a, b) => a.CompareTo(b));
        foreach (AccessRestriction ar in _accessRestrictionBuilder)
        {
            WriteStruct(inMem, ar);
        }

        // Transit departures / stops / routes / schedules are excluded (always 0).
        _headerBuilder.SetDeparturecount(0);
        _headerBuilder.SetStopcount(0);
        _headerBuilder.SetRoutecount(0);
        _headerBuilder.SetSchedulecount(0);
        _headerBuilder.SetTransfercount(0);

        // Write the signs (stable sort by index then type).
        StableSortSigns(_signsBuilder);
        _headerBuilder.SetSigncount((uint)_signsBuilder.Count);
        foreach (Sign sign in _signsBuilder)
        {
            WriteStruct(inMem, sign);
        }

        // Write turn lanes.
        _headerBuilder.SetTurnlaneCount((uint)_turnlanesBuilder.Count);
        foreach (TurnLanes tl in _turnlanesBuilder)
        {
            WriteStruct(inMem, tl);
        }

        // Write the admins.
        _headerBuilder.SetAdmincount((uint)_adminsBuilder.Count);
        foreach (Admin admin in _adminsBuilder)
        {
            WriteStruct(inMem, admin);
        }

        // Write the forward complex restriction data. The offset is the byte position past all the
        // fixed-size sections written so far (relative to the tile base, i.e. including the header).
        uint complexFwdOffset =
            (uint)(GraphTileHeaderSize +
                   (_nodesBuilder.Count * NodeInfoSize) +
                   (_transitionsBuilder.Count * NodeTransitionSize) +
                   (_directedEdgesBuilder.Count * DirectedEdgeSize) +
                   (_directedEdgesExtBuilder.Count * DirectedEdgeExtSize) +
                   (_accessRestrictionBuilder.Count * AccessRestrictionSize) +
                   (_signsBuilder.Count * SignSize) +
                   (_turnlanesBuilder.Count * TurnLanesSize) +
                   (_adminsBuilder.Count * AdminSize));
        _headerBuilder.SetComplexRestrictionForwardOffset(complexFwdOffset);

        uint forwardRestrictionSize = 0;
        foreach (ComplexRestrictionBuilder cr in _complexRestrictionForwardBuilder)
        {
            cr.Serialize(inMem);
            forwardRestrictionSize += (uint)cr.SizeOf();
        }

        // Write the reverse complex restriction data.
        _headerBuilder.SetComplexRestrictionReverseOffset(complexFwdOffset + forwardRestrictionSize);
        uint reverseRestrictionSize = 0;
        foreach (ComplexRestrictionBuilder cr in _complexRestrictionReverseBuilder)
        {
            cr.Serialize(inMem);
            reverseRestrictionSize += (uint)cr.SizeOf();
        }

        // Write the edge info.
        long currentSize = inMem.Position;
        _headerBuilder.SetEdgeinfoOffset(
            _headerBuilder.ComplexRestrictionReverseOffset() + reverseRestrictionSize);
        foreach (EdgeInfoBuilder edgeinfo in _edgeinfoList)
        {
            edgeinfo.Serialize(inMem);
        }

        long edgeInfoSize = inMem.Position - currentSize;

        // Write the names.
        _headerBuilder.SetTextlistOffset((uint)(_headerBuilder.EdgeinfoOffset() + edgeInfoSize));
        foreach (string text in _textListBuilder)
        {
            for (int i = 0; i < text.Length; i++)
            {
                inMem.WriteByte((byte)text[i]);
            }

            inMem.WriteByte(0); // null terminator
        }

        // Add padding (if needed) to align to an 8-byte word.
        int tmp = (int)(inMem.Position % 8);
        int padding = tmp > 0 ? 8 - tmp : 0;
        for (int i = 0; i < padding; i++)
        {
            inMem.WriteByte(0);
        }

        // Write lane connections.
        _headerBuilder.SetLaneConnectivityOffset(
            (uint)(_headerBuilder.TextlistOffset() + _textListOffset + padding));
        _laneConnectivityBuilder.Sort((a, b) => a.CompareTo(b));
        foreach (LaneConnectivity lc in _laneConnectivityBuilder)
        {
            WriteStruct(inMem, lc);
        }

        // Set the end offset.
        _headerBuilder.SetEndOffset(
            (uint)(_headerBuilder.LaneConnectivityOffset() +
                   (_laneConnectivityBuilder.Count * LaneConnectivitySize)));

        if (inMem.Position != blob.Length)
        {
            throw new InvalidOperationException(
                $"Graph tile serialization wrote {inMem.Position} of {blob.Length} bytes.");
        }

        ulong buildIdBits = (ulong)_headerBuilder.BuildId() << GraphTileHeader.TileHashBits;
        ulong tileHash = GraphTileChecksum.ComputeTileHash(blob.AsSpan(GraphTileHeaderSize));
        _headerBuilder.SetRawChecksum(buildIdBits | tileHash);
        _headerBuilder.AsSpan().CopyTo(blob);
        return blob;
    }

    private int GetSerializedSize()
    {
        int extendedEdgeCount =
            _directedEdgesExtBuilder.Count > 0 &&
            _directedEdgesExtBuilder.Count == _directedEdgesBuilder.Count
                ? _directedEdgesExtBuilder.Count
                : 0;
        checked
        {
            long bodySize =
                ((long)_nodesBuilder.Count * NodeInfoSize) +
                ((long)_transitionsBuilder.Count * NodeTransitionSize) +
                ((long)_directedEdgesBuilder.Count * DirectedEdgeSize) +
                ((long)extendedEdgeCount * DirectedEdgeExtSize) +
                ((long)_accessRestrictionBuilder.Count * AccessRestrictionSize) +
                ((long)_signsBuilder.Count * SignSize) +
                ((long)_turnlanesBuilder.Count * TurnLanesSize) +
                ((long)_adminsBuilder.Count * AdminSize);

            foreach (ComplexRestrictionBuilder restriction in
                     _complexRestrictionForwardBuilder)
            {
                bodySize += restriction.SizeOf();
            }

            foreach (ComplexRestrictionBuilder restriction in
                     _complexRestrictionReverseBuilder)
            {
                bodySize += restriction.SizeOf();
            }

            foreach (EdgeInfoBuilder edgeInfo in _edgeinfoList)
            {
                bodySize += edgeInfo.SizeOf();
            }

            foreach (string text in _textListBuilder)
            {
                bodySize += text.Length + 1L;
            }

            long padding = (8 - (bodySize % 8)) % 8;
            bodySize += padding +
                ((long)_laneConnectivityBuilder.Count * LaneConnectivitySize);
            return checked((int)(GraphTileHeaderSize + bodySize));
        }
    }
    /// <summary>
    /// Serializes the tile and writes it to disk under <paramref name="tileDir"/> as an uncompressed
    /// <c>.gph</c> file at the path derived from the tile's GraphId. Faithful port of the disk-writing
    /// side of the C++ <c>StoreTileData()</c> (atomic temp-file write + rename so concurrent readers
    /// never observe a partial tile).
    /// </summary>
    public void StoreTileData(string tileDir)
    {
        byte[] blob = StoreTileData();
        string filename = Path.Combine(tileDir, GraphTile.FileSuffix(_headerBuilder.Graphid()));
        string? dir = Path.GetDirectoryName(filename);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmp = filename + "_" + Environment.CurrentManagedThreadId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, filename, overwrite: true);
    }

    // Edge tuple for sharing edges that have common nodes and edgeindex (orders nodea/nodeb).
    private static (uint, ulong, ulong) EdgeTuple(uint edgeindex, GraphId nodea, GraphId nodeb)
        => nodea < nodeb
            ? (edgeindex, nodea.Value, nodeb.Value)
            : (edgeindex, nodeb.Value, nodea.Value);

    // std::stable_sort over signs by (index, type) with original order preserved on ties.
    private static void StableSortSigns(List<Sign> signs)
    {
        var indexed = new List<(Sign Sign, int Order)>(signs.Count);
        for (int i = 0; i < signs.Count; i++)
        {
            indexed.Add((signs[i], i));
        }

        indexed.Sort((a, b) =>
        {
            int c = a.Sign.CompareTo(b.Sign);
            return c != 0 ? c : a.Order.CompareTo(b.Order);
        });

        for (int i = 0; i < signs.Count; i++)
        {
            signs[i] = indexed[i].Sign;
        }
    }

    // process_linguistic_header: rebuild the linguistic records that belong to a given sign name
    // index. Faithful port of the lambda in AddSigns.
    private static string ProcessLinguisticHeader(
        uint lingStartIndex,
        uint lingCount,
        IReadOnlyList<string> linguistics,
        int index)
    {
        var sb = new System.Text.StringBuilder();
        for (uint x = lingStartIndex; x <= lingCount; x++)
        {
            string s = linguistics[(int)x];
            int p = 0;
            while (p < s.Length && s[p] != '\0')
            {
                var header = new LinguisticTextHeader(ReadUInt32FromString(s, p));
                if (header.NameIndex == index)
                {
                    // Append the 3 stored header bytes + the pronunciation text.
                    foreach (byte b in header.ToStoredBytes())
                    {
                        sb.Append((char)b);
                    }

                    for (int k = 0; k < header.Length; k++)
                    {
                        sb.Append(s[p + LinguisticConstants.HeaderSize + k]);
                    }
                }

                p += header.Length + LinguisticConstants.HeaderSize;
            }
        }

        return sb.ToString();
    }

    private static uint ReadUInt32FromString(string s, int offset)
    {
        uint v = 0;
        for (int i = 0; i < 4 && offset + i < s.Length; i++)
        {
            v |= (uint)(byte)s[offset + i] << (8 * i);
        }

        return v;
    }

    // Number of raw bytes a name occupies in the text list (1 byte per char, matching C++ string).
    private static int ByteLength(string name) => name.Length;

    private static void WriteStruct<T>(Stream output, T value)
        where T : unmanaged
    {
        Span<byte> buf = stackalloc byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(buf, in value);
        output.Write(buf);
    }
}
