// Faithful C# port of Valhalla midgard PointTileIndex.
// Sources: valhalla/midgard/point_tile_index.h and src/midgard/point_tile_index.cc
// Self-contained engine port: does NOT reuse other TruckMate types.
//
// Provided a search width (tile_width_degrees) and a bunch of points, this class bins
// the points into a gridded/tiled space at that width so that "what points are near me"
// can be answered in effectively O(1) (a hash lookup). It is used by the self-intersection
// avoiding Douglas-Peucker simplification in Polyline2. Because it tiles lat/lng space it
// only works with PointLL.

using System;
using System.Collections.Generic;

namespace SharpNinja.Valhalla.Midgard;

/// <summary>
/// Bins a set of <see cref="PointLL"/> points into a uniform tiled space so that nearby
/// points can be looked up quickly. Mirrors the C++ <c>PointTileIndex</c>. Used by the
/// self-intersection-avoiding Douglas-Peucker generalization in <see cref="Polyline2{TPrecision}"/>.
/// </summary>
public sealed class PointTileIndex
{
    /// <summary>
    /// A "special" value that means "this point is deleted" (an invalid lat/lon). Mirrors the
    /// C++ <c>PointTileIndex::kDeletedPoint = {1000.0, 1000.0}</c>.
    /// </summary>
    public static readonly PointLL DeletedPoint = new(1000.0, 1000.0);

    // Tells us which tile a point belongs to.
    private readonly Tiles<PointLL, double>? _tiles;

    // key: TileId; value: set of point indices that live in this tile.
    private readonly Dictionary<int, HashSet<int>> _tiledSpace = new();

    /// <summary>
    /// The given <paramref name="tileWidthDegrees"/> determines how the index subdivides space.
    /// All "near" queries are based on this distance. The given polyline points are binned/indexed
    /// into the tiled space.
    /// </summary>
    /// <param name="tileWidthDegrees">The tile width in degrees.</param>
    /// <param name="polyline">The polyline points to index.</param>
    public PointTileIndex(double tileWidthDegrees, IReadOnlyList<PointLL> polyline)
    {
        Points = new List<PointLL>();
        if (polyline.Count == 0)
        {
            return;
        }

        if (tileWidthDegrees <= 0.0)
        {
            return;
        }

        // Determine the extents of the points we want to index. This determines the size of our
        // tile space.
        double minLat = 1000.0;
        double maxLat = -1000.0;
        double minLng = 1000.0;
        double maxLng = -1000.0;
        foreach (PointLL p in polyline)
        {
            if (p.Lat < minLat)
            {
                minLat = p.Lat;
            }

            if (p.Lat > maxLat)
            {
                maxLat = p.Lat;
            }

            if (p.Lng < minLng)
            {
                minLng = p.Lng;
            }

            if (p.Lng > maxLng)
            {
                maxLng = p.Lng;
            }
        }

        // We need a tile buffer around our tiled-space on every side, hence the extra 2. This is
        // because our spatial search will query every tile around the given tile we are searching
        // and this prevents us from wrapping at the tiled-space boundaries.
        const int tileBuffer = 2;
        minLat -= tileBuffer * tileWidthDegrees;
        minLng -= tileBuffer * tileWidthDegrees;
        maxLat += 2 * tileBuffer * tileWidthDegrees;
        maxLng += 2 * tileBuffer * tileWidthDegrees;

        double deltax = maxLng - minLng;
        double deltay = maxLat - minLat;

        var minPt = new PointLL(minLng, minLat);

        int numXDivs = (int)Math.Ceiling(deltax / tileWidthDegrees);
        int numYDivs = (int)Math.Ceiling(deltay / tileWidthDegrees);

        // A square shape.
        int numDivs = (2 * tileBuffer) + Math.Max(numYDivs, numXDivs);

        // Cap how many divisions can be made to avoid overflowing the int32 TileId space (the
        // TileId is bounded by int32_t in the Tiles class).
        int maxDivs = (int)Math.Floor(Math.Sqrt(int.MaxValue));
        numDivs = Math.Min(maxDivs, numDivs);

        _tiles = new Tiles<PointLL, double>(minPt, (float)tileWidthDegrees, numDivs, numDivs);

        Points = new List<PointLL>(polyline.Count);
        int index = 0;
        foreach (PointLL p in polyline)
        {
            Points.Add(p);
            int tid = _tiles.TileId(p);
            if (!_tiledSpace.TryGetValue(tid, out HashSet<int>? set))
            {
                set = new HashSet<int>();
                _tiledSpace[tid] = set;
            }

            set.Add(index);
            index++;
        }
    }

    /// <summary>Random-access list of every point. Deleted points are set to <see cref="DeletedPoint"/>.</summary>
    public List<PointLL> Points { get; private set; }

    /// <summary>
    /// Get all the points roughly within the tile width of the given point. Some returned points
    /// could be as far as 2x the tile width from the point; the caller decides what distance
    /// calculation to use for exact distances.
    /// </summary>
    public HashSet<int> GetPointsNear(PointLL pt)
        => GetPointsNearSegment(new LineSegment2d(new PointXY<double>(pt.X, pt.Y), new PointXY<double>(pt.X, pt.Y)));

    /// <summary>
    /// Get all the points roughly within the tile width of the given segment. Some returned points
    /// could be as far as 2x the tile width from the segment.
    /// </summary>
    public HashSet<int> GetPointsNearSegment(LineSegment2d seg)
    {
        var nearPts = new HashSet<int>();
        if (_tiles is null)
        {
            return nearPts;
        }

        PointXY<double> a = seg.A;
        PointXY<double> b = seg.B;

        // Stretch our min-point in the SW direction.
        double minx = Math.Min(a.X, b.X);
        double miny = Math.Min(a.Y, b.Y);
        var minpt = new PointLL(minx, miny);
        int mintid = _tiles.LeftNeighbor(_tiles.BottomNeighbor(_tiles.TileId(minpt)));
        Aabb2T<double> mintidbox = _tiles.TileBounds(mintid);

        // Stretch our max-point in the NE direction.
        double maxx = Math.Max(a.X, b.X);
        double maxy = Math.Max(a.Y, b.Y);
        var maxpt = new PointLL(maxx, maxy);
        int maxtid = _tiles.RightNeighbor(_tiles.TopNeighbor(_tiles.TileId(maxpt)));
        Aabb2T<double> maxtidbox = _tiles.TileBounds(maxtid);

        // Box from min-pt to max-pt. Determine the tiles covered by the box.
        var thebox = new Aabb2T<double>(
            mintidbox.Minx, mintidbox.Miny, mintidbox.Maxx, mintidbox.Maxy);
        thebox.Expand(maxtidbox);
        List<int> tilesCovered = _tiles.TileList(thebox);

        // Gather up all points in the covered tiles.
        foreach (int tid in tilesCovered)
        {
            if (_tiledSpace.TryGetValue(tid, out HashSet<int>? set))
            {
                nearPts.UnionWith(set);
            }
        }

        return nearPts;
    }

    /// <summary>Removes a point from the tiled space given its index (marks it deleted).</summary>
    public void RemovePoint(int idx)
    {
        if (_tiles is null)
        {
            return;
        }

        // Delete this entry from its tile.
        int tid = _tiles.TileId(Points[idx]);
        if (_tiledSpace.TryGetValue(tid, out HashSet<int>? tilePoints))
        {
            tilePoints.Remove(idx);
        }

        // Don't actually delete from the list, just mark as deleted.
        Points[idx] = DeletedPoint;
    }

    /// <summary>
    /// Remove a range of points by index from the tiled space. This includes the point from
    /// <paramref name="startIndex"/> (inclusive) to <paramref name="endIndex"/> (exclusive).
    /// </summary>
    public void RemovePoints(int startIndex, int endIndex)
    {
        for (int i = startIndex; i < endIndex; i++)
        {
            RemovePoint(i);
        }
    }
}
