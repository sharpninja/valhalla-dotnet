// TileModel.Serialize: reserialize the enhanced tile to a byte-compatible baldr tile blob.
// Mirrors the section order + 8-byte word padding of GraphTileBuilder::StoreTileData, carrying the
// non-mutated sections (transitions, directed-edge ext, signs, admins, edge info, original text
// list) through verbatim and writing the mutated nodes/edges and the rebuilt access-restriction /
// turn-lane sections. New turn-lane names appended via AddName extend the text list, after which the
// lane-connectivity section is shifted (and the header offsets recomputed) exactly as the builder
// would. Section layout (matching GraphBuilder.Build output, which has no edge bins / transit /
// complex restrictions):
//   [header 272][nodes][transitions][directed edges][ext?][access restrictions][signs][turn lanes]
//   [admins][edge info][text list][pad to 8][lane connectivity]

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

internal sealed partial class TileModel
{
    /// <summary>
    /// Reserializes the enhanced tile to a byte-compatible blob. Faithful to
    /// <c>GraphTileBuilder::StoreTileData</c>'s section ordering, padding, and offset math.
    /// </summary>
    public byte[] Serialize()
    {
        GraphTileHeader header = _tile.Header();

        // Original section byte ranges (absolute offsets within _blob; tile base offset is 0).
        int nodeCount = _nodes.Length;
        int edgeCount = _edges.Length;
        int transitionCount = (int)header.Transitioncount();
        bool hasExt = header.HasExtDirectededge();

        int originalAccessRestrictionCount = (int)header.AccessRestrictionCount();
        int signCount = (int)header.Signcount();
        int adminCount = (int)header.Admincount();

        // Variable-section byte spans carried verbatim.
        int edgeInfoOffset = (int)header.EdgeinfoOffset();
        int textlistOffset = (int)header.TextlistOffset();
        int laneConnectivityOffset = (int)header.LaneConnectivityOffset();
        int endOffset = (int)header.EndOffset();

        int edgeInfoSize = textlistOffset - edgeInfoOffset;
        int laneConnectivitySize = endOffset - laneConnectivityOffset;

        // Offsets to the fixed sections we carry through (transitions / ext / signs / admins). These
        // appear, in order, after the directed-edge[+ext] section.
        int transitionsOff = GraphTileHeaderSize + (nodeCount * NodeInfoSize);
        int directedEdgesOff = transitionsOff + (transitionCount * NodeTransitionSize);
        int extOff = directedEdgesOff + (edgeCount * DirectedEdgeSize);
        int accessRestrictionsOff = extOff + (hasExt ? edgeCount * DirectedEdgeExtSize : 0);
        // (transit sections are zero-length in this build)
        int signsOff = accessRestrictionsOff + (originalAccessRestrictionCount * AccessRestrictionSize);
        int turnlanesOff = signsOff + (signCount * SignSize);
        int adminsOff = turnlanesOff + ((int)header.TurnlaneCount() * TurnLanesSize);

        List<AccessRestriction> accessRestrictions = _newAccessRestrictions ?? new List<AccessRestriction>();
        List<TurnLanes> turnLanes = _newTurnLanes ?? new List<TurnLanes>();

        // Sort access restrictions + turn lanes exactly as the builder does on store.
        accessRestrictions.Sort((a, b) => a.CompareTo(b));
        turnLanes.Sort((a, b) => a.CompareTo(b));

        using var body = new MemoryStream();

        // Nodes (mutated).
        header.SetNodecount((uint)nodeCount);
        foreach (NodeInfo node in _nodes)
        {
            WriteStruct(body, node);
        }

        // Transitions (verbatim).
        header.SetTransitioncount((uint)transitionCount);
        body.Write(_blob, transitionsOff, transitionCount * NodeTransitionSize);

        // Directed edges (mutated).
        header.SetDirectededgecount((uint)edgeCount);
        foreach (DirectedEdge de in _edges)
        {
            WriteStruct(body, de);
        }

        // Directed edge ext (verbatim, if present).
        if (hasExt)
        {
            body.Write(_blob, extOff, edgeCount * DirectedEdgeExtSize);
        }

        // Access restrictions (rebuilt, sorted).
        header.SetAccessRestrictionCount((uint)accessRestrictions.Count);
        foreach (AccessRestriction ar in accessRestrictions)
        {
            WriteStruct(body, ar);
        }

        // Transit sections excluded (always 0).
        header.SetDeparturecount(0);
        header.SetStopcount(0);
        header.SetRoutecount(0);
        header.SetSchedulecount(0);
        header.SetTransfercount(0);

        // Signs (verbatim).
        header.SetSigncount((uint)signCount);
        body.Write(_blob, signsOff, signCount * SignSize);

        // Turn lanes (rebuilt).
        header.SetTurnlaneCount((uint)turnLanes.Count);
        foreach (TurnLanes tl in turnLanes)
        {
            WriteStruct(body, tl);
        }

        // Admins (verbatim).
        header.SetAdmincount((uint)adminCount);
        body.Write(_blob, adminsOff, adminCount * AdminSize);

        // Complex restrictions are empty in this build; their offsets index the start of edge info.
        uint complexFwdOffset = (uint)(GraphTileHeaderSize +
            (nodeCount * NodeInfoSize) +
            (transitionCount * NodeTransitionSize) +
            (edgeCount * DirectedEdgeSize) +
            ((hasExt ? edgeCount * DirectedEdgeExtSize : 0)) +
            (accessRestrictions.Count * AccessRestrictionSize) +
            (signCount * SignSize) +
            (turnLanes.Count * TurnLanesSize) +
            (adminCount * AdminSize));
        header.SetComplexRestrictionForwardOffset(complexFwdOffset);
        header.SetComplexRestrictionReverseOffset(complexFwdOffset);

        // Edge info (verbatim).
        header.SetEdgeinfoOffset(complexFwdOffset);
        body.Write(_blob, edgeInfoOffset, edgeInfoSize);

        // Text list (original verbatim + appended new turn-lane names).
        header.SetTextlistOffset((uint)(header.EdgeinfoOffset() + edgeInfoSize));
        body.Write(_blob, textlistOffset, (int)_originalTextListSize);
        if (_appendedText.Count > 0)
        {
            body.Write(_appendedText.ToArray(), 0, _appendedText.Count);
        }

        // Padding to align to an 8-byte word.
        int tmp = (int)(body.Position % 8);
        int padding = tmp > 0 ? 8 - tmp : 0;
        for (int i = 0; i < padding; i++)
        {
            body.WriteByte(0);
        }

        // Lane connectivity (verbatim).
        uint newTextListSize = _originalTextListSize + (uint)_appendedText.Count;
        header.SetLaneConnectivityOffset((uint)(header.TextlistOffset() + newTextListSize + padding));
        body.Write(_blob, laneConnectivityOffset, laneConnectivitySize);

        // End offset.
        header.SetEndOffset((uint)(header.LaneConnectivityOffset() + laneConnectivitySize));

        // Assemble: header + body.
        byte[] bodyBytes = body.ToArray();
        var blob = new byte[GraphTileHeaderSize + bodyBytes.Length];
        header.AsSpan().CopyTo(blob);
        Array.Copy(bodyBytes, 0, blob, GraphTileHeaderSize, bodyBytes.Length);
        return blob;
    }

    private static void WriteStruct<T>(Stream output, T value)
        where T : unmanaged
    {
        Span<byte> buf = stackalloc byte[Marshal.SizeOf<T>()];
        MemoryMarshal.Write(buf, in value);
        output.Write(buf);
    }
}
