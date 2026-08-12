using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal sealed class BoundedRestrictionTileCatalog
{
    private readonly IReadOnlyDictionary<
        byte,
        IReadOnlyList<GraphId>> levels;

    private BoundedRestrictionTileCatalog(
        IReadOnlyDictionary<byte, IReadOnlyList<GraphId>> levels,
        int tileCount)
    {
        this.levels = levels;
        TileCount = tileCount;
    }

    internal int TileCount { get; }

    internal IReadOnlyList<GraphId> GetLevel(byte level) =>
        levels.TryGetValue(
            level,
            out IReadOnlyList<GraphId>? tiles)
            ? tiles
            : Array.Empty<GraphId>();

    internal IEnumerable<GraphId> EnumerateAll()
    {
        foreach (byte level in levels.Keys.Order())
        {
            foreach (GraphId tileId in levels[level])
            {
                yield return tileId;
            }
        }
    }

    internal bool HasSameTiles(
        BoundedRestrictionTileCatalog other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (TileCount != other.TileCount)
        {
            return false;
        }

        using IEnumerator<GraphId> left = EnumerateAll().GetEnumerator();
        using IEnumerator<GraphId> right = other.EnumerateAll().GetEnumerator();
        while (left.MoveNext())
        {
            if (!right.MoveNext() || left.Current != right.Current)
            {
                return false;
            }
        }

        return !right.MoveNext();
    }

    internal static BoundedRestrictionTileCatalog Build(
        string tileDirectory,
        int maxTileCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tileDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTileCount);

        string fullTileDirectory = Path.GetFullPath(tileDirectory);
        if (!Directory.Exists(fullTileDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Tile catalog directory '{fullTileDirectory}' " +
                "does not exist.");
        }

        var mutableLevels = new Dictionary<byte, List<GraphId>>();
        int tileCount = 0;
        foreach (string tilePath in Directory.EnumerateFiles(
                     fullTileDirectory,
                     "*.gph",
                     SearchOption.AllDirectories))
        {
            if (tileCount >= maxTileCount)
            {
                throw new InvalidOperationException(
                    "The restriction tile catalog exceeded its bounded " +
                    "capacity.");
            }

            GraphId tileId = GraphTile.GetTileId(tilePath).TileBase();
            byte level = checked((byte)tileId.Level());
            if (!mutableLevels.TryGetValue(
                    level,
                    out List<GraphId>? levelTiles))
            {
                levelTiles = new List<GraphId>();
                mutableLevels.Add(level, levelTiles);
            }

            levelTiles.Add(tileId);
            tileCount++;
        }

        var immutableLevels =
            new Dictionary<byte, IReadOnlyList<GraphId>>();
        foreach (KeyValuePair<byte, List<GraphId>> entry
                 in mutableLevels)
        {
            entry.Value.Sort(
                static (left, right) =>
                    left.Value.CompareTo(right.Value));
            for (var index = 1;
                 index < entry.Value.Count;
                 index++)
            {
                if (entry.Value[index - 1] == entry.Value[index])
                {
                    throw new InvalidDataException(
                        $"Duplicate graph tile " +
                        $"{entry.Value[index]} was discovered.");
                }
            }

            immutableLevels.Add(
                entry.Key,
                entry.Value.AsReadOnly());
        }

        return new BoundedRestrictionTileCatalog(
            immutableLevels,
            tileCount);
    }
}