// Faithful C# port of Valhalla midgard Tiles<coord_t>.
// Sources: valhalla/midgard/tiles.h and src/midgard/tiles.cc
// Self-contained engine port: does NOT reuse other TruckMate types.
//
// The C++ class is templated on coord_t (Point2 = PointXY<float> for the planar case, or
// PointLL = GeoPoint<double> for the spherical case). Here Tiles is generic over both the
// coordinate type TCoord and its scalar precision TPrecision, bridged by IMidgardCoord so
// the tile system can read x/y from a coordinate and construct coordinates of the concrete
// type (Base/Center/TileBounds), mirroring `coord_t(x, y)` in the C++.
//
// Tile id rules (verbatim from the C++ doc comment):
//   - Tile numbers start at 0 at the min y, x (lower left).
//   - Tile numbers increase by column (x/longitude) then by row (y/latitude).
//   - Tile numbers increase along each row by increasing x/longitude.
//
// Scope: the row/col/tileid/bbox/neighbor methods are ported in full. The following methods
// from tiles.cc are intentionally OMITTED because they depend on midgard modules that are not
// part of this port:
//   - TileList(Ellipse)          : needs Ellipse<coord_t> (not ported).
//   - ColorMap                   : connectivity-map coloring; not needed by the tile spike.
//   - Intersect(linestring)      : needs Polyline2, DistanceApproximator, resample_spherical_polyline
//                                  and the bresenham rasterizer (Polyline2 not ported).
//   - ClosestFirst               : needs the closest_first_generator_t priority-queue functor.
// Intersect(AABB2) IS ported because it only needs scalar arithmetic. BinBBox and the global
// GetNeighbor/GetNeighbors helpers are also ported since they only need integer/scalar math.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace SharpNinja.Valhalla.Midgard;

/// <summary>
/// Identifies the eight neighbors of a tile/bin, starting at lower left and moving clockwise.
/// Mirrors the C++ <c>enum class Neighbor</c>.
/// </summary>
public enum Neighbor : byte
{
    /// <summary>Bottom-left neighbor.</summary>
    BottomLeft = 0,

    /// <summary>Left neighbor.</summary>
    Left = 1,

    /// <summary>Top-left neighbor.</summary>
    TopLeft = 2,

    /// <summary>Top neighbor.</summary>
    Top = 3,

    /// <summary>Top-right neighbor.</summary>
    TopRight = 4,

    /// <summary>Right neighbor.</summary>
    Right = 5,

    /// <summary>Bottom-right neighbor.</summary>
    BottomRight = 6,

    /// <summary>Bottom neighbor.</summary>
    Bottom = 7,
}

/// <summary>
/// A class that provides a uniform (square) tiling system for a specified bounding box and tile
/// size. Works with <see cref="PointXY{TPrecision}"/> (Euclidean x,y) or <see cref="PointLL"/>
/// (latitude/longitude). Mirrors the C++ template class <c>Tiles&lt;coord_t&gt;</c>.
/// </summary>
/// <typeparam name="TCoord">The coordinate type (Point2 or PointLL).</typeparam>
/// <typeparam name="TPrecision">The scalar precision type (float or double).</typeparam>
public sealed class Tiles<TCoord, TPrecision>
    where TCoord : IMidgardCoord<TCoord, TPrecision>
    where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
{
    // Does the tile bounds wrap in the x direction (e.g. at longitude = 180).
    private readonly bool _wrapx;

    // Tile size. Tiles are square (equal y and x size).
    private readonly float _tilesize;

    // Number of rows (y or latitude).
    private readonly int _nrows;

    // Number of columns (x or longitude).
    private readonly int _ncolumns;

    // Number of subdivisions within a single tile.
    private readonly ushort _nsubdivisions;

    // The size of a single subdivision (bin) within a tile.
    private readonly float _subdivisionSize;

    // Bounding box of the tiling system.
    private Aabb2T<TPrecision> _tilebounds;

    /// <summary>
    /// Constructor. A bounding box and tile size are specified. Computes the number of rows and
    /// columns based on the bounding box and tile size.
    /// </summary>
    /// <param name="bounds">Bounding box.</param>
    /// <param name="tilesize">Size of the tile in both dimensions.</param>
    /// <param name="subdivisions">Number of subtiles in both x and y axis of a single tile.</param>
    /// <param name="wrapx">Should neighbor operations wrap around the x axis extents.</param>
    public Tiles(Aabb2T<TPrecision> bounds, float tilesize, ushort subdivisions = 1, bool wrapx = true)
    {
        _wrapx = wrapx;
        _tilebounds = bounds;
        _tilesize = tilesize;
        _nsubdivisions = subdivisions;
        _subdivisionSize = _tilesize / _nsubdivisions;

        double columns = ToDouble(bounds.Width()) / _tilesize;
        double rows = ToDouble(bounds.Height()) / _tilesize;

        // NOTE: matches the C++ "unsafe" constructor: tilesize may not evenly divide into the
        // bounds dimensions, so we round.
        _ncolumns = (int)Math.Round(columns, MidpointRounding.AwayFromZero);
        _nrows = (int)Math.Round(rows, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Constructor. A bottom-left coord with tile size and number of rows and columns. Forces a
    /// tileset that conforms to an integer number of columns and rows.
    /// </summary>
    /// <param name="minPt">Bottom-left coord of the tileset.</param>
    /// <param name="tileSize">The size of a tile in both dimensions.</param>
    /// <param name="columns">Number of tiles in the x axis.</param>
    /// <param name="rows">Number of tiles in the y axis.</param>
    /// <param name="subdivisions">Number of subtiles in both x and y axis of a single tile.</param>
    /// <param name="wrapx">Should neighbor operations wrap around the x axis extents.</param>
    public Tiles(
        TCoord minPt,
        float tileSize,
        int columns,
        int rows,
        ushort subdivisions = 1,
        bool wrapx = true)
    {
        _wrapx = wrapx;
        TPrecision ts = TPrecision.CreateChecked(tileSize);
        _tilebounds = new Aabb2T<TPrecision>(
            new PointXY<TPrecision>(minPt.X, minPt.Y),
            new PointXY<TPrecision>(
                minPt.X + (TPrecision.CreateChecked(columns) * ts),
                minPt.Y + (TPrecision.CreateChecked(rows) * ts)));
        _tilesize = tileSize;
        _nrows = rows;
        _ncolumns = columns;
        _nsubdivisions = subdivisions;
        _subdivisionSize = _tilesize / _nsubdivisions;
    }

    /// <summary>Gets the tile size.</summary>
    public float TileSize() => _tilesize;

    /// <summary>Gets the tile subdivision size.</summary>
    public float SubdivisionSize() => _subdivisionSize;

    /// <summary>Gets the number of rows in the tiling system.</summary>
    public int Nrows() => _nrows;

    /// <summary>Gets the number of columns in the tiling system.</summary>
    public int Ncolumns() => _ncolumns;

    /// <summary>Gets the number of subdivisions in a tile in the tiling system.</summary>
    public ushort Nsubdivisions() => _nsubdivisions;

    /// <summary>Gets the bounding box of the tiling system.</summary>
    public Aabb2T<TPrecision> TileBounds() => _tilebounds;

    /// <summary>
    /// Shift the tilebounds - a special method used to nudge the tile bounds so a specific point
    /// stays centered in the grid.
    /// </summary>
    /// <param name="shift">Amount to shift the bounding box.</param>
    public void ShiftTileBounds(TCoord shift)
    {
        _tilebounds = new Aabb2T<TPrecision>(
            _tilebounds.Minx - shift.X,
            _tilebounds.Miny - shift.Y,
            _tilebounds.Maxx - shift.X,
            _tilebounds.Maxy - shift.Y);
    }

    /// <summary>
    /// Get the "row" based on y. Returns -1 if outside the tile system bounds.
    /// </summary>
    /// <param name="y">y coordinate.</param>
    public int Row(TPrecision y)
    {
        // Return -1 if outside the tile system bounds
        if (y < _tilebounds.Miny || y > _tilebounds.Maxy)
        {
            return -1;
        }

        // If equal to the max y return the largest row
        return y == _tilebounds.Maxy
            ? _nrows - 1
            : (int)ToDouble((y - _tilebounds.Miny) / TPrecision.CreateChecked(_tilesize));
    }

    /// <summary>
    /// Get the "column" based on x. Returns -1 if outside the tile system bounds.
    /// </summary>
    /// <param name="x">x coordinate.</param>
    public int Col(TPrecision x)
    {
        // Return -1 if outside the tile system bounds
        if (x < _tilebounds.Minx || x > _tilebounds.Maxx)
        {
            return -1;
        }

        // If equal to the max x return the largest column
        TPrecision col = (x - _tilebounds.Minx) / TPrecision.CreateChecked(_tilesize);
        if (col >= TPrecision.CreateChecked(_ncolumns))
        {
            return _ncolumns - 1;
        }

        return col >= TPrecision.Zero ? (int)ToDouble(col) : (int)ToDouble(col - TPrecision.One);
    }

    /// <summary>
    /// Convert a coordinate into a tile Id. The point is within the tile. Returns -1 if the
    /// coordinate is outside the tiling system extent.
    /// </summary>
    /// <param name="c">Coordinate / point.</param>
    public int TileId(TCoord c) => TileId(c.Y, c.X);

    /// <summary>
    /// Convert x,y to a tile Id. Returns -1 if the x,y is outside the bounding box of the tiling
    /// system.
    /// </summary>
    /// <param name="y">y (or lat).</param>
    /// <param name="x">x (or lng).</param>
    public int TileId(TPrecision y, TPrecision x)
    {
        // Return -1 if totally outside the extent.
        if (y < _tilebounds.Miny || x < _tilebounds.Minx
            || y > _tilebounds.Maxy || x > _tilebounds.Maxx)
        {
            return -1;
        }

        // Find the tileid by finding the latitude row and longitude column
        return (Row(y) * _ncolumns) + Col(x);
    }

    /// <summary>
    /// Get the tile Id given the column Id and row Id. Mirrors C++ <c>TileId(col, row)</c>.
    /// </summary>
    /// <param name="col">Tile column.</param>
    /// <param name="row">Tile row.</param>
    public int TileId(int col, int row) => (row * _ncolumns) + col;

    /// <summary>
    /// Get the tile row, col based on tile Id. Returns a pair indicating {row, col}.
    /// </summary>
    /// <param name="tileid">Tile Id.</param>
    public (int Row, int Col) GetRowColumn(int tileid) => (tileid / _ncolumns, tileid % _ncolumns);

    /// <summary>
    /// Get a maximum tileid given a bounds and a tile size.
    /// </summary>
    /// <param name="bounds">The region for which to compute the maximum tile id.</param>
    /// <param name="tileSize">The size of a tile within the region.</param>
    /// <returns>The highest tile number within the region.</returns>
    public static uint MaxTileId(Aabb2T<TPrecision> bounds, float tileSize)
    {
        uint cols = (uint)Math.Ceiling(ToDouble(bounds.Width()) / tileSize);
        uint rows = (uint)Math.Ceiling(ToDouble(bounds.Height()) / tileSize);
        return (cols * rows) - 1;
    }

    /// <summary>
    /// Get the base x,y of a specified tile.
    /// </summary>
    /// <param name="tileid">Tile Id.</param>
    /// <returns>The base x,y of the specified tile.</returns>
    public TCoord Base(int tileid)
    {
        int row = tileid / _ncolumns;
        int col = tileid - (row * _ncolumns);
        TPrecision ts = TPrecision.CreateChecked(_tilesize);
        return TCoord.Create(
            _tilebounds.Minx + (TPrecision.CreateChecked(col) * ts),
            _tilebounds.Miny + (TPrecision.CreateChecked(row) * ts));
    }

    /// <summary>
    /// Get the bounding box of the specified tile.
    /// </summary>
    /// <param name="tileid">Tile Id.</param>
    public Aabb2T<TPrecision> TileBounds(int tileid)
    {
        TCoord b = Base(tileid);
        TPrecision ts = TPrecision.CreateChecked(_tilesize);
        return new Aabb2T<TPrecision>(b.X, b.Y, b.X + ts, b.Y + ts);
    }

    /// <summary>
    /// Get the bounding box of the tile with specified column, row.
    /// </summary>
    /// <param name="col">Tile column.</param>
    /// <param name="row">Tile row.</param>
    public Aabb2T<TPrecision> TileBounds(int col, int row)
    {
        TPrecision ts = TPrecision.CreateChecked(_tilesize);
        TPrecision basex = _tilebounds.Minx + (TPrecision.CreateChecked(col) * ts);
        TPrecision basey = _tilebounds.Miny + (TPrecision.CreateChecked(row) * ts);
        return new Aabb2T<TPrecision>(basex, basey, basex + ts, basey + ts);
    }

    /// <summary>
    /// Get the center of the specified tile.
    /// </summary>
    /// <param name="tileid">Tile Id.</param>
    public TCoord Center(int tileid)
    {
        TCoord b = Base(tileid);
        TPrecision halfTile = TPrecision.CreateChecked(_tilesize * 0.5);
        return TCoord.Create(b.X + halfTile, b.Y + halfTile);
    }

    /// <summary>
    /// Get the tile offsets (row,column) between the previous tile Id and a new tileid. Offsets can
    /// be positive, negative, or 0.
    /// </summary>
    /// <param name="initialTileid">Original tile.</param>
    /// <param name="newtileid">Tile to which relative offset is desired.</param>
    /// <param name="deltaRows">Out: relative number of rows.</param>
    /// <param name="deltaCols">Out: relative number of columns.</param>
    public void TileOffsets(int initialTileid, int newtileid, out int deltaRows, out int deltaCols)
    {
        int deltaTile = newtileid - initialTileid;
        deltaRows = (newtileid / _ncolumns) - (initialTileid / _ncolumns);
        deltaCols = deltaTile - (deltaRows * _ncolumns);
    }

    /// <summary>
    /// Get the number of tiles in the tiling system.
    /// </summary>
    public uint TileCount()
    {
        float nrows = ToFloat(_tilebounds.Maxy - _tilebounds.Miny) / _tilesize;
        return (uint)(_ncolumns * (int)Math.Ceiling((double)nrows));
    }

    /// <summary>
    /// Get the neighboring tileid to the right/east.
    /// </summary>
    /// <param name="tileid">Tile Id.</param>
    public int RightNeighbor(int tileid)
        => tileid - ((tileid / _ncolumns) * _ncolumns) < _ncolumns - 1
            ? tileid + 1
            : _wrapx
                ? tileid - _ncolumns + 1
                : tileid;

    /// <summary>
    /// Get the neighboring tileid to the left/west.
    /// </summary>
    /// <param name="tileid">Tile Id.</param>
    public int LeftNeighbor(int tileid)
        => tileid - ((tileid / _ncolumns) * _ncolumns) > 0
            ? tileid - 1
            : _wrapx
                ? tileid + _ncolumns - 1
                : tileid;

    /// <summary>
    /// Get the neighboring tileid above or north. Returns tileid if on the top row.
    /// </summary>
    /// <param name="tileid">Tile Id.</param>
    public int TopNeighbor(int tileid)
        => tileid < (int)(TileCount() - _ncolumns) ? tileid + _ncolumns : tileid;

    /// <summary>
    /// Get the neighboring tileid below or south. Returns tileid if on the bottom row.
    /// </summary>
    /// <param name="tileid">Tile Id.</param>
    public int BottomNeighbor(int tileid)
        => tileid < _ncolumns ? tileid : tileid - _ncolumns;

    /// <summary>
    /// Get the neighbor of a global subdivision (bin) in a given direction. Returns the new
    /// {tileid, binid}. Mirrors the C++ <c>GetNeighbor</c>.
    /// </summary>
    public (uint TileId, ushort BinId) GetNeighbor(int globalX, int globalY, Neighbor which)
    {
        // starting at lower left, moving clockwise
        short[] dx = { -1, -1, -1, 0, 1, 1, 1, 0 };
        short[] dy = { -1, 0, 1, 1, 1, 0, -1, -1 };

        // new global
        int nx = _wrapx && ((globalX == _ncolumns * _nsubdivisions) || globalX == 0)
            ? globalX
            : globalX + dx[(byte)which];
        int ny = globalY + dy[(byte)which];

        // convert back to tile/bin ids
        int newTileId = (nx / _nsubdivisions) + ((ny / _nsubdivisions) * _ncolumns);
        int newBinId = (nx % _nsubdivisions) + ((ny % _nsubdivisions) * _nsubdivisions);
        return ((uint)newTileId, (ushort)newBinId);
    }

    /// <summary>
    /// Get the eight neighbors of a tile/bin (starting at lower left, moving clockwise). Mirrors
    /// the C++ <c>GetNeighbors</c>.
    /// </summary>
    public (uint TileId, ushort BinId)[] GetNeighbors(uint tileid, short binid)
    {
        var neighbors = new (uint TileId, ushort BinId)[8];

        // tile coords
        int tx = (int)(tileid % _ncolumns);
        int ty = (int)(tileid / _ncolumns);

        // bin coords within tile
        int bx = binid % _nsubdivisions;
        int by = binid / _nsubdivisions;

        // global coords
        int globalX = (tx * _nsubdivisions) + bx;
        int globalY = (ty * _nsubdivisions) + by;

        for (byte i = 0; i < 8; ++i)
        {
            neighbors[i] = GetNeighbor(globalX, globalY, (Neighbor)i);
        }

        return neighbors;
    }

    /// <summary>
    /// Checks if 2 tiles are neighbors (N,E,S,W). Does not support wrap around 180 longitude.
    /// </summary>
    /// <param name="id1">Tile Id 1.</param>
    /// <param name="id2">Tile Id 2.</param>
    public bool AreNeighbors(uint id1, uint id2)
        => id2 == id1 - 1 || id2 == id1 + 1 || id2 == id1 + (uint)_ncolumns || id2 == id1 - (uint)_ncolumns;

    /// <summary>
    /// Get the list of tiles that lie within the specified bounding box. Since tiles as well as the
    /// bounding box are both aligned to the axes we can simply find tiles by iterating over rows and
    /// columns of tiles from the minimum to maximum. Faithful port of <c>TileList(AABB2)</c>.
    /// </summary>
    /// <param name="bbox">Bounding box.</param>
    /// <returns>A list of tiles that are within or intersect the bounding box.</returns>
    public List<int> TileList(Aabb2T<TPrecision> bbox)
    {
        // Check if x range needs to be split
        var bboxes = new List<Aabb2T<TPrecision>>();
        if (_wrapx)
        {
            if (bbox.Minx < _tilebounds.Minx && bbox.Maxx > _tilebounds.Minx)
            {
                // Create 2 bounding boxes
                bboxes.Add(new Aabb2T<TPrecision>(_tilebounds.Minx, bbox.Miny, bbox.Maxx, bbox.Maxy));
                bboxes.Add(new Aabb2T<TPrecision>(
                    bbox.Minx + _tilebounds.Width(), bbox.Miny, _tilebounds.Maxx, bbox.Maxy));
            }
            else if (bbox.Minx < _tilebounds.Maxx && bbox.Maxx > _tilebounds.Maxx)
            {
                // Create 2 bounding boxes
                bboxes.Add(new Aabb2T<TPrecision>(bbox.Minx, bbox.Miny, _tilebounds.Maxx, bbox.Maxy));
                bboxes.Add(new Aabb2T<TPrecision>(
                    _tilebounds.Minx, bbox.Miny, bbox.Maxx - _tilebounds.Width(), bbox.Maxy));
            }
            else
            {
                bboxes.Add(bbox.Intersection(_tilebounds));
            }
        }
        else
        {
            bboxes.Add(bbox.Intersection(_tilebounds));
        }

        var tilelist = new List<int>();
        foreach (Aabb2T<TPrecision> bb in bboxes)
        {
            int minrow = Math.Max(Row(bb.Miny), 0);
            int maxrow = Math.Max(Row(bb.Maxy), 0);
            int mincol = Math.Max(Col(bb.Minx), 0);
            int maxcol = Math.Max(Col(bb.Maxx), 0);
            for (int row = minrow; row <= maxrow; ++row)
            {
                int tileid = TileId(mincol, row);
                for (int col = mincol; col <= maxcol; ++col, ++tileid)
                {
                    tilelist.Add(tileid);
                }
            }
        }

        return tilelist;
    }

    /// <summary>
    /// Intersect the bounding box with the tiles to see which tiles and sub-cells (a.k.a bins) it
    /// intersects with. Faithful port of <c>Intersect(AABB2)</c>.
    /// </summary>
    /// <param name="box">The bounding box to be tested.</param>
    /// <returns>A map of tile IDs to a set of bin IDs within that tile.</returns>
    public Dictionary<int, HashSet<ushort>> Intersect(Aabb2T<TPrecision> box)
    {
        var intersection = new Dictionary<int, HashSet<ushort>>();

        // to calculate the bounds within each tile, we first calculate all the subdivisions (bins)
        // which the bounding box covers in global space, and then iterate over them to fill in each
        // "pixel" or bin.
        int xPixels = _ncolumns * _nsubdivisions;
        int yPixels = _nrows * _nsubdivisions;

        // NOTE: multiply by pixels before dividing to keep as much precision as possible.
        int x0 = (int)Math.Floor(ToDouble((box.Minx - _tilebounds.Minx) * TPrecision.CreateChecked(xPixels)) / ToDouble(_tilebounds.Width()));
        int y0 = (int)Math.Floor(ToDouble((box.Miny - _tilebounds.Miny) * TPrecision.CreateChecked(yPixels)) / ToDouble(_tilebounds.Height()));
        int x1 = (int)Math.Floor(ToDouble((box.Maxx - _tilebounds.Minx) * TPrecision.CreateChecked(xPixels)) / ToDouble(_tilebounds.Width()));
        int y1 = (int)Math.Floor(ToDouble((box.Maxy - _tilebounds.Miny) * TPrecision.CreateChecked(yPixels)) / ToDouble(_tilebounds.Height()));

        // clamp ranges to within the bounds of the tile.
        if (x0 < 0)
        {
            x0 = 0;
        }

        if (y0 < 0)
        {
            y0 = 0;
        }

        if (x1 >= xPixels)
        {
            x1 = xPixels - 1;
        }

        if (y1 >= yPixels)
        {
            y1 = yPixels - 1;
        }

        for (int y = y0; y <= y1; ++y)
        {
            for (int x = x0; x <= x1; ++x)
            {
                int tileId = ((y / _nsubdivisions) * _ncolumns) + (x / _nsubdivisions);
                int bin = ((y % _nsubdivisions) * _nsubdivisions) + (x % _nsubdivisions);
                if (!intersection.TryGetValue(tileId, out HashSet<ushort>? bins))
                {
                    bins = new HashSet<ushort>();
                    intersection[tileId] = bins;
                }

                bins.Add((ushort)bin);
            }
        }

        return intersection;
    }

    /// <summary>
    /// Returns the bounding box of a bin given its tile and bin ID within the tile. Faithful port of
    /// <c>BinBBox</c>.
    /// </summary>
    public Aabb2T<TPrecision> BinBBox(int tile, ushort bin)
    {
        int binRow = bin / _nsubdivisions;
        int binCol = bin % _nsubdivisions;
        Aabb2T<TPrecision> tileBounds = TileBounds(tile);
        TPrecision ss = TPrecision.CreateChecked(_subdivisionSize);
        var lowerLeft = new PointXY<TPrecision>(
            tileBounds.Minx + (TPrecision.CreateChecked(binCol) * ss),
            tileBounds.Miny + (TPrecision.CreateChecked(binRow) * ss));
        var upperRight = new PointXY<TPrecision>(lowerLeft.X + ss, lowerLeft.Y + ss);
        return new Aabb2T<TPrecision>(lowerLeft, upperRight);
    }

    private static double ToDouble(TPrecision value) => double.CreateChecked(value);

    private static float ToFloat(TPrecision value) => float.CreateChecked(value);
}
