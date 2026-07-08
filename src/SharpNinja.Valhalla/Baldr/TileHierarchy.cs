// Faithful C# port of Valhalla baldr tilehierarchy.h + src/baldr/tilehierarchy.cc (valhalla @ 3.7.0).
// Sources:
//   F:/github/valhalla/valhalla/baldr/tilehierarchy.h
//   F:/github/valhalla/src/baldr/tilehierarchy.cc
//
// Defines the static set of tile levels used by the tiled, hierarchical graph. GraphTile's
// FileSuffix / GetTileId / BoundingBox helpers all consult this static hierarchy (the C++
// TileHierarchy::levels() / GetTransitLevel()).
//
// PORT-NOTE: only the routing-relevant subset of TileHierarchy is ported here:
//   - levels() / GetTransitLevel() (the static level table)
//   - get_max_level()
//   - get_tiling(level)
//   - GetGraphId(pointll, level)
//   - GetGraphIdBoundingBox(id)
//   - GetGraphIds(bbox, level) / GetGraphIds(bbox)
//   - get_level(roadclass)
//   - parent(child_tile_id)

using System;
using System.Collections.Generic;

using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Defines a level in the hierarchy of tiles. Includes the hierarchy level, the minimum (largest
/// value) road class in this level, the level name, and the lat,lon tiling of this level. Faithful
/// port of C++ <c>struct TileLevel</c>.
/// </summary>
public sealed class TileLevel
{
    /// <summary>Constructs a tile level.</summary>
    /// <param name="level">Hierarchy level number.</param>
    /// <param name="importance">Minimum (largest value) road class included at this level.</param>
    /// <param name="name">Name for the level.</param>
    /// <param name="tiles">Lat,lon tiling system for this level.</param>
    public TileLevel(byte level, RoadClass importance, string name, Tiles<PointLL, double> tiles)
    {
        Level = level;
        Importance = importance;
        Name = name;
        Tiles = tiles;
    }

    /// <summary>Hierarchy level number.</summary>
    public byte Level { get; }

    /// <summary>Minimum (largest value) road class in this level.</summary>
    public RoadClass Importance { get; }

    /// <summary>Name for the level.</summary>
    public string Name { get; }

    /// <summary>Lat,lon tiling (in particular, tile size) of this level.</summary>
    public Tiles<PointLL, double> Tiles { get; }
}

/// <summary>
/// Static methods used to get information about the hierarchy of tiles. The tile hierarchy levels
/// are static. Faithful port of C++ <c>class TileHierarchy</c>.
/// </summary>
public static class TileHierarchy
{
    // World bounds for all levels: {{-180, -90}, {180, 90}}.
    private static readonly Aabb2T<double> WorldBounds = new(
        new PointXY<double>(-180, -90),
        new PointXY<double>(180, 90));

    // Static tile levels. Mirrors TileHierarchy::levels() in src/baldr/tilehierarchy.cc exactly:
    //   level 0 "highway"  Primary       tile size 4
    //   level 1 "arterial" Tertiary      tile size 1
    //   level 2 "local"    ServiceOther  tile size .25
    // The subdivision count is kBinsDim (5) for all levels.
    private static readonly List<TileLevel> Levels_ = new()
    {
        new TileLevel(
            0,
            GraphConstants.StringToRoadClass("Primary"),
            "highway",
            new Tiles<PointLL, double>(WorldBounds, 4, GraphTileHeader.BinsDim)),
        new TileLevel(
            1,
            GraphConstants.StringToRoadClass("Tertiary"),
            "arterial",
            new Tiles<PointLL, double>(WorldBounds, 1, GraphTileHeader.BinsDim)),
        new TileLevel(
            2,
            GraphConstants.StringToRoadClass("ServiceOther"),
            "local",
            new Tiles<PointLL, double>(WorldBounds, 0.25f, GraphTileHeader.BinsDim)),
    };

    // The transit level. Mirrors TileHierarchy::GetTransitLevel().
    private static readonly TileLevel TransitLevel_ = new(
        3,
        GraphConstants.StringToRoadClass("ServiceOther"),
        "transit",
        new Tiles<PointLL, double>(WorldBounds, 0.25f, GraphTileHeader.BinsDim));

    /// <summary>Get the set of levels in this hierarchy. Faithful port of C++ <c>levels()</c>.</summary>
    public static IReadOnlyList<TileLevel> Levels() => Levels_;

    /// <summary>Get the transit level in this hierarchy. Faithful port of C++ <c>GetTransitLevel()</c>.</summary>
    public static TileLevel GetTransitLevel() => TransitLevel_;

    /// <summary>Gets the maximum level supported in the hierarchy. Faithful port of C++ <c>get_max_level()</c>.</summary>
    public static byte GetMaxLevel() => TransitLevel_.Level;

    /// <summary>
    /// Get the tiling system for a specified level. Faithful port of C++ <c>get_tiling(level)</c>.
    /// </summary>
    public static Tiles<PointLL, double> GetTiling(byte level)
    {
        if (level < Levels_.Count)
        {
            return Levels_[level].Tiles;
        }

        if (level == TransitLevel_.Level)
        {
            return TransitLevel_.Tiles;
        }

        throw new InvalidOperationException("Invalid level Id for TileHierarchy::get_tiling");
    }

    /// <summary>
    /// Returns the GraphId of the requested tile based on a lat,lng and a level. If the level is not
    /// supported an invalid id will be returned. Faithful port of C++
    /// <c>GetGraphId(pointll, level)</c>.
    /// </summary>
    public static GraphId GetGraphId(PointLL pointll, byte level)
    {
        GraphId id = GraphId.Invalid;
        if (level < Levels_.Count)
        {
            int tileId = Levels_[level].Tiles.TileId(pointll);
            if (tileId >= 0)
            {
                id = new GraphId((uint)tileId, level, 0);
            }
        }

        return id;
    }

    /// <summary>
    /// Returns the bounding box for the given GraphId. Faithful port of C++
    /// <c>GetGraphIdBoundingBox(id)</c>.
    /// </summary>
    public static Aabb2T<double> GetGraphIdBoundingBox(GraphId id)
    {
        TileLevel tileLevel = Levels_[(int)id.Level()];
        return tileLevel.Tiles.TileBounds((int)id.Tileid());
    }

    /// <summary>
    /// Returns all the GraphIds of the tiles which intersect the given bounding box at that level.
    /// Faithful port of C++ <c>GetGraphIds(bbox, level)</c>.
    /// </summary>
    public static List<GraphId> GetGraphIds(Aabb2T<double> bbox, byte level)
    {
        var ids = new List<GraphId>();
        if (level < Levels_.Count)
        {
            List<int> tileIds = Levels_[level].Tiles.TileList(bbox);
            ids.Capacity = tileIds.Count;
            foreach (int tileId in tileIds)
            {
                ids.Add(new GraphId((uint)tileId, level, 0));
            }
        }

        return ids;
    }

    /// <summary>
    /// Returns all the GraphIds of the tiles which intersect the given bounding box at any level.
    /// Faithful port of C++ <c>GetGraphIds(bbox)</c>.
    /// </summary>
    public static List<GraphId> GetGraphIds(Aabb2T<double> bbox)
    {
        var ids = new List<GraphId>();
        foreach (TileLevel entry in Levels_)
        {
            ids.AddRange(GetGraphIds(bbox, entry.Level));
        }

        return ids;
    }

    /// <summary>
    /// Gets the hierarchy level given the road class. Faithful port of C++ <c>get_level(roadclass)</c>.
    /// </summary>
    public static byte GetLevel(RoadClass roadclass)
    {
        if (roadclass <= Levels_[0].Importance)
        {
            return 0;
        }

        if (roadclass <= Levels_[1].Importance)
        {
            return 1;
        }

        return 2;
    }

    /// <summary>
    /// Returns the parent (containing tile with lower tile level) of a given tile id. Returns an
    /// invalid GraphId if the tile is already at the lowest (level 0) tile level. Faithful port of
    /// C++ <c>parent(child_tile_id)</c>.
    /// </summary>
    public static GraphId Parent(GraphId childTileId)
    {
        // bail if there is no parent
        if (childTileId.Level() == 0)
        {
            return new GraphId(GraphId.InvalidGraphId);
        }

        // get the tilings so we can use coordinates to pick the parent for the child
        byte parentLevel = (byte)(childTileId.Level() - 1);
        Tiles<PointLL, double> parentTiling = GetTiling(parentLevel);
        Tiles<PointLL, double> childTiling = GetTiling((byte)childTileId.Level());

        // grab just off of the child's corner to avoid edge cases. C++:
        //   corner = child_tiling.Base(tileid) + Vector2d{parent_tiling.TileSize()/2, .../2}
        PointLL childBase = childTiling.Base((int)childTileId.Tileid());
        double half = parentTiling.TileSize() / 2.0;
        var corner = PointLL.Create(childBase.X + half, childBase.Y + half);

        // pick the parent from the child's coordinate
        int parentTileIndex = parentTiling.TileId(corner);
        return new GraphId((uint)parentTileIndex, parentLevel, 0);
    }
}
