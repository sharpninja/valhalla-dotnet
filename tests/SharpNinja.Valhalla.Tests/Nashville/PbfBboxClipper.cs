// THROWAWAY engine-validation helper (not a product feature). Clips a large .osm.pbf down to a
// bounding box and writes a fresh, valid .osm.pbf using only the project's own OsmPbfReader plus a
// hand-rolled protobuf/PBF writer. This exists solely so the Nashville end-to-end engine test can run
// TileBuilder.BuildTileSet over a small extract (no osmconvert/osmium available in this environment).
//
// Clip policy (routability-preserving): keep every way that has at least one node inside the bbox,
// then keep every node referenced by a kept way (so kept ways are geometrically complete and the road
// graph stays connected across the bbox edge), plus every node that falls inside the bbox. Relations
// are kept when all their referenced way/node members survive the clip (turn restrictions etc.). The
// output uses regular (non-dense) Nodes, Ways, and Relations in PrimitiveBlocks with granularity 100
// (1e-7 deg) and zlib-compressed blobs - exactly the shape OsmPbfReader/libosmium decode.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Nashville;

internal sealed class ClipStats
{
    public long SourceNodes;
    public long SourceWays;
    public long SourceRelations;
    public int KeptNodes;
    public int KeptWays;
    public int KeptRelations;
    public long OutputBytes;
}

internal static class PbfBboxClipper
{
    // Clip src -> dest by [minLon,minLat,maxLon,maxLat]. Returns stats.
    public static ClipStats Clip(
        string srcPath,
        string destPath,
        double minLon,
        double minLat,
        double maxLon,
        double maxLat)
    {
        var stats = new ClipStats();

        // ---- Pass 1: decide which ways qualify (any node in bbox) and gather their node refs. ----
        var pass1 = new Pass1Visitor(minLon, minLat, maxLon, maxLat);
        new OsmPbfReader(pass1).Parse(srcPath);
        stats.SourceNodes = pass1.NodeCount;
        stats.SourceWays = pass1.WayCount;
        stats.SourceRelations = pass1.RelationCount;

        // Nodes we must keep: every node referenced by a kept way, plus every node inside the bbox.
        HashSet<ulong> neededNodes = pass1.NeededNodeIds; // already includes refs of kept ways
        HashSet<ulong> keptWayIds = pass1.KeptWayIds;

        // ---- Pass 2: materialize kept nodes (with coords+tags), kept ways, kept relations. ----
        var pass2 = new Pass2Visitor(minLon, minLat, maxLon, maxLat, neededNodes, keptWayIds);
        new OsmPbfReader(pass2).Parse(srcPath);

        stats.KeptNodes = pass2.Nodes.Count;
        stats.KeptWays = pass2.Ways.Count;
        stats.KeptRelations = pass2.Relations.Count;

        // ---- Write the clipped PBF. ----
        using (FileStream fs = File.Create(destPath))
        {
            PbfWriter.Write(fs, minLon, minLat, maxLon, maxLat, pass2.Nodes, pass2.Ways, pass2.Relations);
        }

        stats.OutputBytes = new FileInfo(destPath).Length;
        return stats;
    }

    private sealed class Pass1Visitor : IOsmPbfVisitor
    {
        private readonly double _minLon, _minLat, _maxLon, _maxLat;
        public long NodeCount;
        public long WayCount;
        public long RelationCount;

        // Node ids that are inside the bbox (so a way touching them is kept).
        private readonly HashSet<ulong> _nodesInBbox = new();

        // Output of pass 1.
        public readonly HashSet<ulong> KeptWayIds = new();
        public readonly HashSet<ulong> NeededNodeIds = new();

        public Pass1Visitor(double minLon, double minLat, double maxLon, double maxLat)
        {
            _minLon = minLon;
            _minLat = minLat;
            _maxLon = maxLon;
            _maxLat = maxLat;
        }

        public void Header(double? a, double? b, double? c, double? d, IReadOnlyList<string> f) { }

        public void Node(ulong id, double lat, double lon, IReadOnlyDictionary<string, string> tags)
        {
            NodeCount++;
            if (lon >= _minLon && lon <= _maxLon && lat >= _minLat && lat <= _maxLat)
            {
                _nodesInBbox.Add(id);
                NeededNodeIds.Add(id);
            }
        }

        public void Way(ulong id, IReadOnlyList<ulong> nodeRefs, IReadOnlyDictionary<string, string> tags)
        {
            WayCount++;
            bool touches = false;
            for (int i = 0; i < nodeRefs.Count; i++)
            {
                if (_nodesInBbox.Contains(nodeRefs[i]))
                {
                    touches = true;
                    break;
                }
            }

            if (!touches)
            {
                return;
            }

            KeptWayIds.Add(id);
            for (int i = 0; i < nodeRefs.Count; i++)
            {
                NeededNodeIds.Add(nodeRefs[i]);
            }
        }

        public void Relation(ulong id, IReadOnlyList<OsmRelationMember> members, IReadOnlyDictionary<string, string> tags)
            => RelationCount++;
    }

    private sealed class Pass2Visitor : IOsmPbfVisitor
    {
        private readonly double _minLon, _minLat, _maxLon, _maxLat;
        private readonly HashSet<ulong> _neededNodes;
        private readonly HashSet<ulong> _keptWays;

        public readonly List<PbfWriter.NodeRec> Nodes = new();
        public readonly List<PbfWriter.WayRec> Ways = new();
        public readonly List<PbfWriter.RelationRec> Relations = new();

        // Track which node/way ids survived so relation membership can be validated.
        private readonly HashSet<ulong> _keptNodeIds = new();

        public Pass2Visitor(
            double minLon, double minLat, double maxLon, double maxLat,
            HashSet<ulong> neededNodes, HashSet<ulong> keptWays)
        {
            _minLon = minLon;
            _minLat = minLat;
            _maxLon = maxLon;
            _maxLat = maxLat;
            _neededNodes = neededNodes;
            _keptWays = keptWays;
        }

        public void Header(double? a, double? b, double? c, double? d, IReadOnlyList<string> f) { }

        public void Node(ulong id, double lat, double lon, IReadOnlyDictionary<string, string> tags)
        {
            if (!_neededNodes.Contains(id))
            {
                return;
            }

            _keptNodeIds.Add(id);
            Nodes.Add(new PbfWriter.NodeRec(id, lat, lon, ToList(tags)));
        }

        public void Way(ulong id, IReadOnlyList<ulong> nodeRefs, IReadOnlyDictionary<string, string> tags)
        {
            if (!_keptWays.Contains(id))
            {
                return;
            }

            Ways.Add(new PbfWriter.WayRec(id, new List<ulong>(nodeRefs), ToList(tags)));
        }

        public void Relation(ulong id, IReadOnlyList<OsmRelationMember> members, IReadOnlyDictionary<string, string> tags)
        {
            // Keep only relations whose node/way members all survived (way members must be kept ways;
            // node members must be kept nodes). Relation members are ignored if of type Relation
            // (we drop super-relations to keep the clip simple - turn restrictions reference way/node).
            foreach (OsmRelationMember m in members)
            {
                switch (m.Type)
                {
                    case OsmMemberType.Node when !_keptNodeIds.Contains(m.Id):
                        return;
                    case OsmMemberType.Way when !_keptWays.Contains(m.Id):
                        return;
                    case OsmMemberType.Relation:
                        return; // drop relations that reference other relations
                    default:
                        break;
                }
            }

            Relations.Add(new PbfWriter.RelationRec(id, new List<OsmRelationMember>(members), ToList(tags)));
        }

        private static List<KeyValuePair<string, string>> ToList(IReadOnlyDictionary<string, string> tags)
        {
            var list = new List<KeyValuePair<string, string>>(tags.Count);
            foreach (KeyValuePair<string, string> kv in tags)
            {
                list.Add(kv);
            }

            return list;
        }
    }
}
