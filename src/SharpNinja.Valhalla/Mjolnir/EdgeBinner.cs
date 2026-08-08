// Edge binning: builds the per-tile 5x5 spatial index (the "bins") that loki's
// edge-search-by-bin relies on (Loki.BinHandler reads GraphTile.GetBin). Faithful port of the
// binning half of valhalla 3.7.0's GraphValidator::Validate -> GraphTileBuilder::BinEdges /
// GraphTileBuilder::AddBins (src/mjolnir/graphtilebuilder.cc) plus the supporting geometry
// midgard::Tiles::Intersect / bresenham_line (src/midgard/tiles.cc) and
// midgard::resample_spherical_polyline (src/midgard/util.cc).
//
// PORT-NOTE: the original C# port skipped binning on the assumption that loki snapped via the
// ClosestFirstGenerator over raw geometry. In fact the ported Loki.Search/BinHandler resolves
// candidate edges through GraphTile.GetBin, so without bins every loki correlation returns zero
// edges and no route can be built. This class restores the spatial index so the C#-built tiles
// behave like the C++ valhalla_build_tiles output.

using System;
using System.Collections.Generic;
using System.IO;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Builds the edge bins (5x5 spatial index) for the local-level tiles. Faithful port of
/// <c>GraphTileBuilder::BinEdges</c> / <c>GraphTileBuilder::AddBins</c> and the supporting
/// <c>midgard::Tiles::Intersect</c> geometry.
/// </summary>
internal static class EdgeBinner
{
    private const int BinCount = GraphTileHeader.BinCount; // 25
    private const int BinsDim = GraphTileHeader.BinsDim;   // 5
    private const int GraphIdSize = 8;                     // sizeof(GraphId) / sizeof(uint64)

    // Tweeners: edges that pass through a tile without starting or ending in it. Keyed by the tile's
    // GraphId (base id at the local level). Faithful port of the C++ tweeners_t (map of GraphId ->
    // array<vector<GraphId>, kBinCount>).
    internal sealed class Tweeners : Dictionary<ulong, List<ulong>[]>
    {
    }

    // Custom comparator to sort bin contents by GraphId (level desc, tile_id asc, id asc). Faithful
    // port of graphvalidator.cc graphid_less.
    private static int GraphIdLess(ulong aVal, ulong bVal)
    {
        var a = new GraphId(aVal);
        var b = new GraphId(bVal);
        if (a.Level() != b.Level())
        {
            return a.Level() > b.Level() ? -1 : 1;
        }

        if (a.Tileid() != b.Tileid())
        {
            return a.Tileid() < b.Tileid() ? -1 : 1;
        }

        if (a.Id() != b.Id())
        {
            return a.Id() < b.Id() ? -1 : 1;
        }

        return 0;
    }

    /// <summary>
    /// Bins the edges of a tile into the 5x5 grid, returning this tile's own bins and accumulating
    /// cross-tile pass-through edges into <paramref name="tweeners"/>. Faithful port of
    /// <c>GraphTileBuilder::BinEdges</c>.
    /// </summary>
    internal static List<ulong>[] BinEdges(GraphTile tile, Tweeners tweeners)
    {
        var bins = NewBins();

        byte maxLevel = TileHierarchy.Levels()[^1].Level;
        GraphId tileGraphId = tile.Header().Graphid();

        // skip transit or other special levels and empty tiles.
        if (tileGraphId.Level() > maxLevel || tile.Header().Directededgecount() == 0)
        {
            return bins;
        }

        bool max = tileGraphId.Level() == maxLevel;
        Tiles<PointLL, double> tiles = TileHierarchy.Levels()[^1].Tiles;

        // avoid duplicates of edges that start and end in the same tile (dedup on edgeinfo offset).
        var ids = new HashSet<uint>();
        uint edgeCount = tile.Header().Directededgecount();
        for (uint e = 0; e < edgeCount; e++)
        {
            DirectedEdge edge = tile.DirectedEdge((int)e);

            // dont bin transit/platform/egress connections.
            Use use = edge.Use;
            if (use == Use.TransitConnection || use == Use.PlatformConnection || use == Use.EgressConnection)
            {
                continue;
            }

            // get the shape or bail if none.
            EdgeInfo info = tile.EdgeInfo(edge);
            IReadOnlyList<PointLL> shape = info.Shape();
            if (shape.Count == 0)
            {
                continue;
            }

            // writing the edge to the tile it originates in; not to the tile it terminates in; to
            // tweeners if originating < terminating or the edge leaves and comes back.
            PointLL front = shape[0];
            PointLL back = shape[^1];
            int startId = tiles.TileId(edge.Forward ? front : back);
            int endId = tiles.TileId(edge.Forward ? back : front);
            bool intermediate = startId < endId;

            // if this starts and ends in the same tile and we've seen it already we can skip it.
            if (startId == endId && !ids.Add((uint)edge.EdgeInfoOffset))
            {
                continue;
            }

            Dictionary<int, HashSet<ushort>> intersection = Intersect(tiles, shape);
            var edgeId = new GraphId(tileGraphId.Tileid(), tileGraphId.Level(), e);
            foreach (KeyValuePair<int, HashSet<ushort>> i in intersection)
            {
                bool originating = i.Key == startId;
                bool terminating = i.Key == endId;
                bool loopBack = i.Key != startId && i.Key != endId && startId == endId;
                if (originating || (intermediate && !terminating) || loopBack)
                {
                    // which set of bins, either this local set or tweeners to be added later.
                    List<ulong>[] outBins;
                    if (originating && max)
                    {
                        outBins = bins;
                    }
                    else
                    {
                        var key = new GraphId((uint)i.Key, maxLevel, 0).Value;
                        if (!tweeners.TryGetValue(key, out List<ulong>[]? tw))
                        {
                            tw = NewBins();
                            tweeners[key] = tw;
                        }

                        outBins = tw;
                    }

                    foreach (ushort bin in i.Value)
                    {
                        outBins[bin].Add(edgeId.Value);
                    }
                }
            }
        }

        return bins;
    }

    /// <summary>
    /// Appends <paramref name="moreBins"/> to the bins of the on-disk tile, shifting the trailing
    /// header section offsets by the inserted byte size. Faithful port of
    /// <c>GraphTileBuilder::AddBins</c> (operates directly on the serialized blob - the bin section
    /// sits between the admins and the complex-restriction sections).
    /// </summary>
    internal static void AddBins(string tileDir, GraphTile tile, List<ulong>[] moreBins)
    {
        // read existing bins and append.
        var bins = new List<ulong>[BinCount];
        uint shiftCount = 0;
        for (int i = 0; i < BinCount; i++)
        {
            IReadOnlyList<GraphId> existing = tile.GetBin(i % BinsDim, i / BinsDim);
            var combined = new List<ulong>(existing.Count + moreBins[i].Count);
            foreach (GraphId g in existing)
            {
                combined.Add(g.Value);
            }

            combined.AddRange(moreBins[i]);
            bins[i] = combined;
            shiftCount += (uint)moreBins[i].Count;
        }

        uint shift = shiftCount * GraphIdSize;

        // update header bin offsets (cumulative counts) - mirrors set_edge_bin_offsets.
        var offsets = new uint[BinCount];
        offsets[0] = (uint)bins[0].Count;
        for (int i = 1; i < BinCount; i++)
        {
            offsets[i] = (uint)bins[i].Count + offsets[i - 1];
        }

        // The original tile image is laid out as: header, [...fixed sections...], existing bins,
        // [...trailing variable sections...]. We rebuild it by writing the (mutated) header, the
        // bytes between the header and the bin section verbatim, the new bins, then the trailing
        // bytes verbatim. Section offsets after the bins shift by the inserted byte count.
        byte[] original = tile.TileImage();
        int binSectionStart = tile.EdgeBinsImageOffset();      // byte offset of bins within the image
        uint oldBinBytes = tile.Header().BinOffset(BinCount - 1).End * GraphIdSize;
        int binSectionEnd = binSectionStart + (int)oldBinBytes;

        var header = new GraphTileHeader();
        header.CopyFrom(tile.Header());
        header.SetEdgeBinOffsets(offsets);
        header.SetComplexRestrictionForwardOffset(header.ComplexRestrictionForwardOffset() + shift);
        header.SetComplexRestrictionReverseOffset(header.ComplexRestrictionReverseOffset() + shift);
        header.SetEdgeinfoOffset(header.EdgeinfoOffset() + shift);
        header.SetTextlistOffset(header.TextlistOffset() + shift);
        header.SetLaneConnectivityOffset(header.LaneConnectivityOffset() + shift);
        if (header.PredictedspeedsOffset() != 0)
        {
            header.SetPredictedspeedsOffset(header.PredictedspeedsOffset() + shift);
        }

        header.SetEndOffset(header.EndOffset() + shift);

        int headerSize = GraphTileHeader.HeaderSize;
        int newBinBytes = (int)(offsets[BinCount - 1] * GraphIdSize);

        // pre-bins region (between header end and the bins) is unchanged.
        int preLen = binSectionStart - headerSize;
        // post-bins region (everything after the old bins to the end of the tile) is unchanged.
        int postLen = original.Length - binSectionEnd;

        var blob = new byte[headerSize + preLen + newBinBytes + postLen];
        int pos = 0;
        header.AsSpan().CopyTo(blob.AsSpan(pos, headerSize));
        pos += headerSize;
        Array.Copy(original, headerSize, blob, pos, preLen);
        pos += preLen;
        for (int i = 0; i < BinCount; i++)
        {
            foreach (ulong value in bins[i])
            {
                WriteUInt64(blob, pos, value);
                pos += GraphIdSize;
            }
        }

        Array.Copy(original, binSectionEnd, blob, pos, postLen);

        string path = Path.Combine(tileDir, GraphTile.FileSuffix(tile.Id()));
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        GraphTileChecksum.RefreshTileHash(blob);
        GraphTileChecksum.WriteTileAtomically(path, blob);
    }

    /// <summary>
    /// Sorts each bin's GraphIds deterministically (graphid_less) before writing. Faithful port of
    /// the per-bin std::sort in graphvalidator.cc (Write the bins / bin_tweeners).
    /// </summary>
    internal static void SortBins(List<ulong>[] bins)
    {
        for (int i = 0; i < bins.Length; i++)
        {
            bins[i].Sort(GraphIdLess);
        }
    }

    private static List<ulong>[] NewBins()
    {
        var bins = new List<ulong>[BinCount];
        for (int i = 0; i < BinCount; i++)
        {
            bins[i] = new List<ulong>();
        }

        return bins;
    }

    // -----------------------------------------------------------------------------------------
    // Geometry: Intersect(linestring) + resample_spherical_polyline + bresenham_line.
    // Faithful port of midgard::Tiles<PointLL>::Intersect (src/midgard/tiles.cc) operating on the
    // local-level tiling (0.25 deg tiles, 5 subdivisions).
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Determines which tiles and bins (subdivisions) the polyline passes through. Faithful port of
    /// <c>Tiles&lt;coord_t&gt;::Intersect(const container_t&amp; linestring)</c>.
    /// </summary>
    internal static Dictionary<int, HashSet<ushort>> Intersect(Tiles<PointLL, double> tiles, IReadOnlyList<PointLL> linestring)
    {
        var intersection = new Dictionary<int, HashSet<ushort>>();
        if (linestring.Count == 0)
        {
            return intersection;
        }

        Aabb2T<double> bounds = tiles.TileBounds();
        int ncolumns = tiles.Ncolumns();
        int nrows = tiles.Nrows();
        int nsub = tiles.Nsubdivisions();
        double subdivisionSize = tiles.SubdivisionSize();
        double minx = bounds.Minx;
        double miny = bounds.Miny;
        double width = bounds.Width();
        double height = bounds.Height();
        int xMax = nsub * ncolumns;
        int yMax = nsub * nrows;

        // returns true if the pixel is outside the valid grid (records nothing), false if recorded.
        bool SetPixel(int x, int y)
        {
            if (x < 0 || y < 0 || x >= xMax || y >= yMax)
            {
                return true;
            }

            int tileColumn = x / nsub;
            int tileRow = y / nsub;
            int tile = (tileRow * ncolumns) + tileColumn;
            var subdivision = (ushort)(((y % nsub) * nsub) + (x % nsub));
            if (!intersection.TryGetValue(tile, out HashSet<ushort>? set))
            {
                set = new HashSet<ushort>();
                intersection[tile] = set;
            }

            set.Add(subdivision);
            return false;
        }

        // spherical resampling guard (PointLL::IsSpherical() is always true).
        IReadOnlyList<PointLL> line = linestring;
        double maxMeters = Math.Max(
            1.0,
            subdivisionSize * 0.25 * DistanceApproximator<PointLL, double>.MetersPerLngDegree(linestring[0].Lat));
        if (PointLL.IsSpherical() && PointLlPolyline2.Length(linestring) > maxMeters)
        {
            line = ResampleSphericalPolyline(linestring, maxMeters, true);
        }

        // walk each segment (N points -> N-1 segments; a single point still bins once).
        for (int idx = 0; idx < line.Count; idx++)
        {
            PointLL u = line[idx];
            PointLL v = u;
            if (idx + 1 < line.Count)
            {
                v = line[idx + 1];
            }
            else if (line.Count > 1)
            {
                // last point of a multi-point line: the trailing segment was already processed.
                break;
            }

            double x0 = (u.Lng - minx) / width * ncolumns * nsub;
            double y0 = (u.Lat - miny) / height * nrows * nsub;
            double x1 = (v.Lng - minx) / width * ncolumns * nsub;
            double y1 = (v.Lat - miny) / height * nrows * nsub;

            int ix0 = (int)Math.Floor(x0);
            int ix1 = (int)Math.Floor(x1);
            int iy0 = (int)Math.Floor(y0);
            int iy1 = (int)Math.Floor(y1);
            int dx = ix0 - ix1;
            int dy = iy0 - iy1;
            int ds = (dx * dx) + (dy * dy);
            if (ds == 0)
            {
                SetPixel(ix0, iy0);
            }
            else if (ds == 1)
            {
                SetPixel(ix0, iy0);
                SetPixel(ix1, iy1);
            }
            else
            {
                BresenhamLine(x0, y0, x1, y1, SetPixel);
            }
        }

        return intersection;
    }

    // Modified supercover Bresenham rasterizer. Faithful port of the anonymous-namespace
    // bresenham_line in src/midgard/tiles.cc.
    private static void BresenhamLine(double x0, double y0, double x1, double y1, Func<int, int, bool> setPixel)
    {
        bool outside = setPixel((int)Math.Floor(x0), (int)Math.Floor(y0));
        double sx = x0 < x1 ? 1 : -1;
        double dx = x1 - x0;
        double x = Math.Floor(x0) + 0.5;
        double sy = y0 < y1 ? 1 : -1;
        double dy = y1 - y0;
        double y = Math.Floor(y0) + 0.5;
        while (Math.Floor(x) != Math.Floor(x1) || Math.Floor(y) != Math.Floor(y1))
        {
            double tx = Math.Abs((dx * (y - y0)) - (dy * ((x + sx) - x0)));
            double ty = Math.Abs((dx * ((y + sy) - y0)) - (dy * (x - x0)));
            if (tx < ty || (tx == ty && y0 == y1))
            {
                x += sx;
            }
            else
            {
                y += sy;
            }

            bool o = setPixel((int)Math.Floor(x), (int)Math.Floor(y));
            if (!outside && o)
            {
                return;
            }

            outside = o;
        }
    }

    // Faithful port of midgard::resample_spherical_polyline (src/midgard/util.cc), double precision,
    // longitude negated per the C++ radian conversion. resolution is in meters.
    private static List<PointLL> ResampleSphericalPolyline(IReadOnlyList<PointLL> polyline, double resolution, bool preserve)
    {
        var resampled = new List<PointLL>();
        if (polyline.Count == 0)
        {
            return resampled;
        }

        const double radPerMeter = 1.0 / Constants.RadEarthMeters;
        resampled.Add(polyline[0]);
        resolution *= radPerMeter;
        double remaining = resolution;
        PointLL last = resampled[^1];
        for (int pi = 1; pi < polyline.Count; pi++)
        {
            PointLL p = polyline[pi];
            double lon2 = p.Lng * -Constants.RadPerDegD;
            double lat2 = p.Lat * Constants.RadPerDegD;

            double d = last.Equals(p)
                ? 0.0
                : Math.Acos((Math.Sin(last.Lat * Constants.RadPerDegD) * Math.Sin(lat2)) +
                            (Math.Cos(last.Lat * Constants.RadPerDegD) * Math.Cos(lat2) *
                             Math.Cos((last.Lng * -Constants.RadPerDegD) - lon2)));
            if (double.IsNaN(d))
            {
                d = 0.0;
            }

            while (d > remaining)
            {
                double lon1 = last.Lng * -Constants.RadPerDegD;
                double lat1 = last.Lat * Constants.RadPerDegD;
                double sd = Math.Sin(d);
                double a = Math.Sin(d - remaining) / sd;
                double acs1 = a * Math.Cos(lat1);
                double b = Math.Sin(remaining) / sd;
                double bcs2 = b * Math.Cos(lat2);
                double x = (acs1 * Math.Cos(lon1)) + (bcs2 * Math.Cos(lon2));
                double y = (acs1 * Math.Sin(lon1)) + (bcs2 * Math.Sin(lon2));
                double z = (a * Math.Sin(lat1)) + (b * Math.Sin(lat2));
                double newLng = Math.Atan2(y, x) * -Constants.DegPerRadD;
                double newLat = Math.Atan2(z, Math.Sqrt((x * x) + (y * y))) * Constants.DegPerRadD;
                last = new PointLL(newLng, newLat);
                resampled.Add(last);
                d -= remaining;
                remaining = resolution;
            }

            remaining -= d;
            last = p;
            if (preserve)
            {
                resampled.Add(last);
            }
        }

        return resampled;
    }

    private static void WriteUInt64(byte[] buffer, int offset, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            buffer[offset + i] = (byte)(value >> (8 * i));
        }
    }
}
