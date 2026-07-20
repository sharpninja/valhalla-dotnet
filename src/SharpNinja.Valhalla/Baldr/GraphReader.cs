// Faithful C# port of Valhalla baldr GraphReader + the TileCache hierarchy
// (graphreader.h + src/baldr/graphreader.cc) @ 3.7.0.
// Sources:
//   F:/github/valhalla/valhalla/baldr/graphreader.h
//   F:/github/valhalla/src/baldr/graphreader.cc  (1105 LOC)
//
// GraphReader manages access to GraphTiles, keeping a cache of tiles loaded from a local tile
// directory. It exposes GetGraphTile(GraphId), opposing-edge lookups, edge/node helpers and the
// tile-set enumeration that thor/loki/sif consume.
//
// PORT-NOTES / OMISSIONS (per task instructions):
//   - HTTP / curler tile fetch is EXCLUDED. The whole tile_getter_ / curl_tile_getter_t /
//     tile_url_ / is_tar_url_ / remote_tar_offsets_ / load_remote_tar_offsets / load_id_txt_checksum
//     / _404s machinery, plus GraphTile::CacheTileURL, is NOT ported. GetGraphTile only reads from
//     the local tile_dir (uncompressed .gph then gzipped .gph.gz), exactly the C++ disk path.
//   - The mmap'd tar `tile_extract_` path is NOT ported (it relies on midgard::tar + mmap, which is
//     part of the excluded I/O surface). Tiles always come from the tile_dir.
//   - Live-traffic tar/mmap extraction is not ported. Runtime traffic instead comes from one
//     immutable content-addressed generation pinned by an optional ITrafficSnapshotLease.
//   - Incidents (incident_singleton_t / GetIncidentTile / GetIncidents / IncidentResult) are NOT
//     ported (transit-adjacent, excluded I/O).
//   - connectivity_map_t and shortcut_recovery_t (GetShortcut/RecoverShortcut depend on the latter)
//     are NOT ported. GetShortcut IS ported (it only walks the graph); RecoverShortcut is omitted
//     (needs shortcut_recovery_t's precomputed cache).
//   - encoded_edge_shape uses midgard::encode over the edge shape; ported (Encoded/EdgeInfo exist).
//   - boost::property_tree config is replaced by an explicit Config record (tile_dir + cache knobs).
//   - graph_tile_ptr ref-counting collapses to a managed GraphTile? reference.
//   - LimitedGraphReader is ported (trivial wrapper).

using System;
using System.Collections.Generic;
using System.IO;

using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Tile cache interface. Faithful port of C++ <c>class TileCache</c>.
/// </summary>
public interface ITileCache
{
    /// <summary>Reserves enough cache to hold (max_cache_size / tile_size) items.</summary>
    void Reserve(long tileSize);

    /// <summary>Checks if the tile exists in the cache.</summary>
    bool Contains(GraphId graphid);

    /// <summary>Puts a copy of a tile into the cache and returns it.</summary>
    GraphTile? Put(GraphId graphid, GraphTile? tile, long size);

    /// <summary>Gets the tile for a GraphId or null if not cached.</summary>
    GraphTile? Get(GraphId graphid);

    /// <summary>Lets you know if the cache is over committed with respect to the limit.</summary>
    bool OverCommitted();

    /// <summary>Clears the cache.</summary>
    void Clear();

    /// <summary>Reduces the cache size to remove the over-committed state.</summary>
    void Trim();
}

/// <summary>
/// Manages a flat tile cache without hash lookup. NOT thread-safe. Faithful port of C++
/// <c>class FlatTileCache</c>.
/// </summary>
public class FlatTileCache : ITileCache
{
    private const uint InvalidIndex = uint.MaxValue;

    // The actual cached GraphTile objects.
    private readonly List<GraphTile?> _cache = new();

    // Indices into the array of actual cached items.
    private readonly uint[] _cacheIndices;

    // Offsets in the indices list for where a set of tile indices begin (8 entries in C++).
    private readonly uint[] _indexOffsets = new uint[8];

    // The current cache size in bytes.
    private long _cacheSize;

    // The max cache size in bytes.
    private readonly long _maxCacheSize;

    /// <summary>Constructor. Faithful port of <c>FlatTileCache(size_t max_size)</c>.</summary>
    public FlatTileCache(long maxSize)
    {
        _cacheSize = 0;
        _maxCacheSize = maxSize;

        _indexOffsets[0] = 0;
        _indexOffsets[1] = _indexOffsets[0] + TileHierarchy.Levels()[0].Tiles.TileCount();
        _indexOffsets[2] = _indexOffsets[1] + TileHierarchy.Levels()[1].Tiles.TileCount();
        _indexOffsets[3] = _indexOffsets[2] + TileHierarchy.Levels()[2].Tiles.TileCount();

        long total = _indexOffsets[3] + TileHierarchy.GetTransitLevel().Tiles.TileCount();
        _cacheIndices = new uint[total];
        Array.Fill(_cacheIndices, InvalidIndex);
    }

    /// <summary>Gets the offset into the index array for a graphid. Faithful port of <c>get_offset</c>.</summary>
    protected uint GetOffset(GraphId graphid)
        => graphid.Level() < 4
            ? _indexOffsets[graphid.Level()] + graphid.Tileid()
            : (uint)_cacheIndices.Length;

    /// <summary>Gets the index into the tile array for a graphid. Faithful port of <c>get_index</c>.</summary>
    protected uint GetIndex(GraphId graphid)
    {
        uint offset = GetOffset(graphid);
        return offset < _cacheIndices.Length ? _cacheIndices[offset] : InvalidIndex;
    }

    private static bool IsValidIndex(uint index) => index != InvalidIndex;

    private static bool IsInvalidIndex(uint index) => index == InvalidIndex;

    /// <inheritdoc/>
    public void Reserve(long tileSize)
    {
        // List<T> has no fixed reserve; capacity hint only (matches the intent of vector::reserve).
        _cache.Capacity = (int)(_maxCacheSize / tileSize);
    }

    /// <inheritdoc/>
    public bool Contains(GraphId graphid) => IsValidIndex(GetIndex(graphid));

    /// <inheritdoc/>
    public bool OverCommitted() => _cacheSize > _maxCacheSize;

    /// <inheritdoc/>
    public void Clear()
    {
        _cacheSize = 0;
        _cache.Clear();
        Array.Fill(_cacheIndices, InvalidIndex);
    }

    /// <inheritdoc/>
    public GraphTile? Get(GraphId graphid)
    {
        uint index = GetIndex(graphid);
        return IsInvalidIndex(index) ? null : _cache[(int)index];
    }

    /// <inheritdoc/>
    public GraphTile? Put(GraphId graphid, GraphTile? tile, long size)
    {
        _cacheSize += size;
        _cacheIndices[GetOffset(graphid)] = (uint)_cache.Count;
        _cache.Add(tile);
        return _cache[^1];
    }

    /// <inheritdoc/>
    public void Trim() => Clear();
}

/// <summary>
/// Manages a simple hash-map tile cache. NOT thread-safe. Faithful port of C++
/// <c>class SimpleTileCache</c>.
/// </summary>
public class SimpleTileCache : ITileCache
{
    // The actual cached GraphTile objects (keyed by the 64-bit GraphId value, as C++ keys by uint64).
    private readonly Dictionary<ulong, GraphTile?> _cache = new();

    // The current cache size in bytes (test fixtures reach in via the test subclass).
    private long _cacheSize;

    // The max cache size in bytes.
    private long _maxCacheSize;

    /// <summary>Constructor. Faithful port of <c>SimpleTileCache(size_t max_size)</c>.</summary>
    public SimpleTileCache(long maxSize)
    {
        _cacheSize = 0;
        _maxCacheSize = maxSize;
    }

    /// <summary>Current cache size in bytes (protected to mirror the C++ test friend access).</summary>
    protected long CacheSize
    {
        get => _cacheSize;
        set => _cacheSize = value;
    }

    /// <summary>Max cache size in bytes (protected to mirror the C++ test friend access).</summary>
    protected long MaxCacheSize
    {
        get => _maxCacheSize;
        set => _maxCacheSize = value;
    }

    /// <inheritdoc/>
    public void Reserve(long tileSize)
    {
        _cache.EnsureCapacity((int)(_maxCacheSize / tileSize));
    }

    /// <inheritdoc/>
    public bool Contains(GraphId graphid) => _cache.ContainsKey(graphid.Value);

    /// <inheritdoc/>
    public bool OverCommitted() => _cacheSize > _maxCacheSize;

    /// <inheritdoc/>
    public void Clear()
    {
        _cacheSize = 0;
        _cache.Clear();
    }

    /// <inheritdoc/>
    public GraphTile? Get(GraphId graphid)
        => _cache.TryGetValue(graphid.Value, out GraphTile? tile) ? tile : null;

    /// <inheritdoc/>
    public GraphTile? Put(GraphId graphid, GraphTile? tile, long size)
    {
        _cacheSize += size;
        // C++ emplace does not overwrite an existing key; mirror that.
        if (!_cache.ContainsKey(graphid.Value))
        {
            _cache[graphid.Value] = tile;
        }

        return _cache[graphid.Value];
    }

    /// <inheritdoc/>
    public void Trim() => Clear();
}

/// <summary>
/// Manages a simple tile cache and makes sure it's never over-committed using a least-recently-used
/// eviction policy. NOT thread-safe. Faithful port of C++ <c>class TileCacheLRU</c>.
/// </summary>
public class TileCacheLRU : ITileCache
{
    /// <summary>Strategy used to control memory. Faithful port of <c>enum class MemoryLimitControl</c>.</summary>
    public enum MemoryLimitControl
    {
        /// <summary>No eviction is done by the cache; should be triggered by clients.</summary>
        Soft,

        /// <summary>Strict memory control on every Put operation.</summary>
        Hard,
    }

    private sealed class KeyValue
    {
        public KeyValue(GraphId id, GraphTile? tile)
        {
            Id = id;
            Tile = tile;
        }

        public GraphId Id { get; }

        public GraphTile? Tile { get; set; }
    }

    // The GraphId value -> node into the linked list which owns the cached objects.
    private readonly Dictionary<ulong, LinkedListNode<KeyValue>> _cache = new();

    // Linked list of <GraphId, Tile> pairs. Most-recently-used at the front, least at the back.
    private readonly LinkedList<KeyValue> _keyValLruList = new();

    private readonly MemoryLimitControl _memControl;

    private long _cacheSize;

    private readonly long _maxCacheSize;

    /// <summary>Constructor. Faithful port of <c>TileCacheLRU(size_t, MemoryLimitControl)</c>.</summary>
    public TileCacheLRU(long maxSize, MemoryLimitControl memControl)
    {
        _memControl = memControl;
        _cacheSize = 0;
        _maxCacheSize = maxSize;
    }

    /// <inheritdoc/>
    public void Reserve(long tileSize)
    {
        if (tileSize == 0)
        {
            throw new InvalidOperationException("tile_size must not be 0");
        }

        _cache.EnsureCapacity((int)(_maxCacheSize / tileSize));
    }

    /// <inheritdoc/>
    public bool Contains(GraphId graphid) => _cache.ContainsKey(graphid.Value);

    /// <inheritdoc/>
    public bool OverCommitted() => _cacheSize > _maxCacheSize;

    /// <inheritdoc/>
    public void Clear()
    {
        _cacheSize = 0;
        _cache.Clear();
        _keyValLruList.Clear();
    }

    /// <inheritdoc/>
    public void Trim() => TrimToFit(0);

    /// <inheritdoc/>
    public GraphTile? Get(GraphId graphid)
    {
        if (!_cache.TryGetValue(graphid.Value, out LinkedListNode<KeyValue>? node))
        {
            return null;
        }

        MoveToLruHead(node);
        return node.Value.Tile;
    }

    /// <summary>
    /// Deletes cache items (oldest first) until <paramref name="requiredSize"/> bytes are free.
    /// Faithful port of <c>TrimToFit</c>. Returns the number of bytes freed.
    /// </summary>
    private long TrimToFit(long requiredSize)
    {
        long freedSpace = 0;
        while ((OverCommitted() || (_maxCacheSize - _cacheSize) < requiredSize)
               && _keyValLruList.Count > 0)
        {
            KeyValue entryToEvict = _keyValLruList.Last!.Value;
            long tileSize = entryToEvict.Tile!.Header().EndOffset();
            _cacheSize -= tileSize;
            freedSpace += tileSize;
            _cache.Remove(entryToEvict.Id.Value);
            _keyValLruList.RemoveLast();
        }

        return freedSpace;
    }

    /// <summary>Marks a cache entry as most recently used. Faithful port of <c>MoveToLruHead</c>.</summary>
    private void MoveToLruHead(LinkedListNode<KeyValue> node)
    {
        _keyValLruList.Remove(node);
        _keyValLruList.AddFirst(node);
    }

    /// <inheritdoc/>
    public GraphTile? Put(GraphId graphid, GraphTile? tile, long newTileSize)
    {
        if (newTileSize > _maxCacheSize)
        {
            throw new InvalidOperationException("TileCacheLRU: tile size is bigger than max cache size");
        }

        if (!_cache.TryGetValue(graphid.Value, out LinkedListNode<KeyValue>? existing))
        {
            if (_memControl == MemoryLimitControl.Hard)
            {
                TrimToFit(newTileSize);
            }

            var node = new LinkedListNode<KeyValue>(new KeyValue(graphid, tile));
            _keyValLruList.AddFirst(node);
            _cache[graphid.Value] = node;
        }
        else
        {
            long oldTileSize = existing.Value.Tile!.Header().EndOffset();

            // Do it before TrimToFit to avoid its eviction freeing this entry.
            MoveToLruHead(existing);

            if (_memControl == MemoryLimitControl.Hard && newTileSize > oldTileSize)
            {
                long extraSizeRequired = newTileSize - oldTileSize;
                TrimToFit(extraSizeRequired);
            }

            existing.Value.Tile = tile;
            _cacheSize -= oldTileSize;
        }

        _cacheSize += newTileSize;
        return _keyValLruList.First!.Value.Tile;
    }
}

/// <summary>
/// TileCache wrapper synchronized using a lock. Thread-safe. Faithful port of C++
/// <c>class SynchronizedTileCache</c>.
/// </summary>
public class SynchronizedTileCache : ITileCache
{
    private readonly ITileCache _cache;
    private readonly object _mutex;

    /// <summary>Constructor. Faithful port of <c>SynchronizedTileCache(TileCache&amp;, std::mutex&amp;)</c>.</summary>
    public SynchronizedTileCache(ITileCache cache, object mutex)
    {
        _cache = cache;
        _mutex = mutex;
    }

    /// <inheritdoc/>
    public void Reserve(long tileSize)
    {
        lock (_mutex)
        {
            _cache.Reserve(tileSize);
        }
    }

    /// <inheritdoc/>
    public bool Contains(GraphId graphid)
    {
        lock (_mutex)
        {
            return _cache.Contains(graphid);
        }
    }

    /// <inheritdoc/>
    public bool OverCommitted()
    {
        lock (_mutex)
        {
            return _cache.OverCommitted();
        }
    }

    /// <inheritdoc/>
    public void Clear()
    {
        lock (_mutex)
        {
            _cache.Clear();
        }
    }

    /// <inheritdoc/>
    public void Trim()
    {
        lock (_mutex)
        {
            _cache.Trim();
        }
    }

    /// <inheritdoc/>
    public GraphTile? Get(GraphId graphid)
    {
        lock (_mutex)
        {
            return _cache.Get(graphid);
        }
    }

    /// <inheritdoc/>
    public GraphTile? Put(GraphId graphid, GraphTile? tile, long size)
    {
        lock (_mutex)
        {
            return _cache.Put(graphid, tile, size);
        }
    }
}

/// <summary>
/// Creates tile caches. Faithful port of C++ <c>class TileCacheFactory</c>.
/// </summary>
public static class TileCacheFactory
{
    private static readonly object GlobalCacheMutex = new();
    private static ITileCache? _globalTileCache;
    private static readonly object FactoryMutex = new();

    /// <summary>
    /// Constructs a tile cache from the reader config. Faithful port of <c>createTileCache(pt)</c>.
    /// </summary>
    public static ITileCache CreateTileCache(GraphReader.Config pt)
    {
        long maxCacheSize = pt.MaxCacheSize;

        bool useLruCache = pt.UseLruMemCache;
        TileCacheLRU.MemoryLimitControl lruMemControl = pt.LruMemCacheHardControl
            ? TileCacheLRU.MemoryLimitControl.Hard
            : TileCacheLRU.MemoryLimitControl.Soft;

        bool useSimpleCache = pt.UseSimpleMemCache;

        // wrap the tile cache with a thread-safe version
        if (pt.GlobalSynchronizedCache)
        {
            lock (FactoryMutex)
            {
                if (_globalTileCache is null)
                {
                    _globalTileCache = useLruCache
                        ? new TileCacheLRU(maxCacheSize, lruMemControl)
                        : new FlatTileCache(maxCacheSize);
                }

                return new SynchronizedTileCache(_globalTileCache, GlobalCacheMutex);
            }
        }

        if (useLruCache)
        {
            return new TileCacheLRU(maxCacheSize, lruMemControl);
        }

        if (useSimpleCache)
        {
            return new SimpleTileCache(maxCacheSize);
        }

        // by default a fixed-size vector of tiles (FlatTileCache).
        return new FlatTileCache(maxCacheSize);
    }
}

/// <summary>
/// Class that manages access to GraphTiles, using an <see cref="ITileCache"/> to keep a cache of
/// tiles loaded from a local tile directory. Faithful port of C++ <c>class GraphReader</c>
/// (local-directory subset; see file header for omissions).
/// </summary>
public class GraphReader
{
    private const long DefaultMaxCacheSize = 1073741824; // 1 gig
    private const long AverageTileSize = 2097152;        // 2 megs

    /// <summary>
    /// Reader configuration. Replaces the C++ <c>boost::property_tree::ptree</c>. Only the
    /// local-directory + cache knobs are modeled (HTTP/extract knobs are excluded).
    /// </summary>
    public sealed class Config
    {
        /// <summary>Directory where the tiles are kept (C++ <c>tile_dir</c>).</summary>
        public string TileDir { get; init; } = string.Empty;

        /// <summary>Max cache size in bytes (C++ <c>max_cache_size</c>).</summary>
        public long MaxCacheSize { get; init; } = DefaultMaxCacheSize;

        /// <summary>Use the LRU cache (C++ <c>use_lru_mem_cache</c>).</summary>
        public bool UseLruMemCache { get; init; }

        /// <summary>LRU hard memory control (C++ <c>lru_mem_cache_hard_control</c>).</summary>
        public bool LruMemCacheHardControl { get; init; }

        /// <summary>Use the simple hash-map cache (C++ <c>use_simple_mem_cache</c>).</summary>
        public bool UseSimpleMemCache { get; init; }

        /// <summary>Use the global synchronized cache (C++ <c>global_synchronized_cache</c>).</summary>
        public bool GlobalSynchronizedCache { get; init; }

        /// <summary>Max concurrent reader users (C++ <c>max_concurrent_reader_users</c>).</summary>
        public long MaxConcurrentReaderUsers { get; init; } = 1;

        /// <summary>Pinned immutable traffic generation used for every tile loaded by this reader.</summary>
        public ITrafficSnapshotLease? TrafficSnapshot { get; init; }
    }

    private readonly string _tileDir;
    private readonly long _maxConcurrentUsers;
    private readonly ITileCache _cache;
    private readonly ITrafficSnapshotLease? _trafficSnapshot;

    /// <summary>
    /// Constructor using tiles as separate files. Faithful port of the C++ ctor (local-directory
    /// subset). HTTP/extract/incident initialization is omitted (see file header).
    /// </summary>
    public GraphReader(Config pt, ITileCache? cache = null)
    {
        _tileDir = pt.TileDir;
        _maxConcurrentUsers = pt.MaxConcurrentReaderUsers;
        _cache = cache ?? TileCacheFactory.CreateTileCache(pt);
        _trafficSnapshot = pt.TrafficSnapshot;

        // Reserve cache based on the average disk tile size.
        _cache.Reserve(AverageTileSize);
    }

    /// <summary>Returns the tile directory. Faithful port of <c>tile_dir()</c>.</summary>
    public string TileDir() => _tileDir;

    /// <summary>
    /// Returns the maximum number of threads that can use the reader concurrently without blocking.
    /// Faithful port of <c>MaxConcurrentUsers()</c>.
    /// </summary>
    public long MaxConcurrentUsers() => _maxConcurrentUsers;

    /// <summary>Clears the cache. Faithful port of <c>Clear()</c>.</summary>
    public virtual void Clear() => _cache.Clear();

    /// <summary>Tries to keep the cache footprint below the allowed maximum. Faithful port of <c>Trim()</c>.</summary>
    public virtual void Trim() => _cache.Trim();

    /// <summary>Lets you know if the cache is too large. Faithful port of <c>OverCommitted()</c>.</summary>
    public virtual bool OverCommitted() => _cache.OverCommitted();

    /// <summary>
    /// Tests if a tile exists. Faithful port of <c>DoesTileExist(graphid)</c> (local-dir subset).
    /// </summary>
    public virtual bool DoesTileExist(GraphId graphid)
    {
        if (!graphid.IsValid() || graphid.Level() > TileHierarchy.GetMaxLevel())
        {
            return false;
        }

        // otherwise check memory or disk
        if (_cache.Contains(graphid))
        {
            return true;
        }

        if (string.IsNullOrEmpty(_tileDir))
        {
            return false;
        }

        string fileLocation = _tileDir + Path.DirectorySeparatorChar
                              + GraphTile.FileSuffix(graphid.TileBase());
        return File.Exists(fileLocation) || File.Exists(fileLocation + ".gz");
    }

    /// <summary>
    /// Gets a pointer to a graph tile object given a GraphId. Returns null if not found/empty.
    /// Faithful port of <c>GetGraphTile(graphid)</c> (local-directory subset).
    /// </summary>
    public virtual GraphTile? GetGraphTile(GraphId graphid)
    {
        // Return null if not a valid tile.
        if (!graphid.IsValid())
        {
            return null;
        }

        // Check if the level/tileid combination is in the cache.
        GraphId @base = graphid.TileBase();
        GraphTile? cached = _cache.Get(@base);
        if (cached is not null)
        {
            return cached;
        }

        // PORT-NOTE: the mmap'd graph/traffic tar paths remain excluded. Graph bytes come from the
        // on-disk tile_dir, while traffic bytes come only from this reader's pinned immutable lease.

        // Load the graph tile with traffic from the one generation pinned by this reader.
        GraphMemory? trafficMemory = _trafficSnapshot?.OpenTrafficMemory(@base);
        GraphTile? tile = GraphTile.Create(_tileDir, @base, trafficMemory);
        if (tile is null)
        {
            // PORT-NOTE: the URL/tile_getter fallback is excluded; a missing disk tile is a miss.
            return null;
        }

        if (trafficMemory is not null)
        {
            TrafficTileHeader? trafficHeader = tile.GetTrafficTile().Header;
            long expectedLength = checked(
                TrafficTile.HeaderSize + ((long)tile.DirectedEdgeCount() * TrafficTile.SpeedSize));
            if (trafficHeader is null
                || trafficHeader.Value.TileId != @base.Value
                || trafficHeader.Value.DirectedEdgeCount != tile.DirectedEdgeCount()
                || trafficHeader.Value.TrafficTileVersion != TrafficTileConstants.TrafficTileVersion
                || trafficMemory.Size != expectedLength)
            {
                throw new InvalidDataException("Pinned traffic tile does not match the graph tile.");
            }
        }

        // Keep a copy in the cache and return it.
        long size = tile.Header().EndOffset();
        return _cache.Put(@base, tile, size);
    }

    /// <summary>
    /// Gets a tile given a GraphId, reusing <paramref name="tile"/> if it already holds the right
    /// tile (avoiding a cache lookup). Faithful port of the inline
    /// <c>GetGraphTile(graphid, graph_tile_ptr&amp; tile)</c>.
    /// </summary>
    public GraphTile? GetGraphTile(GraphId graphid, ref GraphTile? tile)
    {
        if (tile is null || tile.Id() != graphid.TileBase())
        {
            tile = GetGraphTile(graphid);
        }

        return tile;
    }

    /// <summary>
    /// Gets a tile given a lat,lng and a level. Faithful port of <c>GetGraphTile(pointll, level)</c>.
    /// </summary>
    public GraphTile? GetGraphTile(PointLL pointll, byte level)
    {
        GraphId id = TileHierarchy.GetGraphId(pointll, level);
        return id.IsValid() ? GetGraphTile(id) : null;
    }

    /// <summary>
    /// Gets a tile given a lat,lng using the highest level in the hierarchy. Faithful port of
    /// <c>GetGraphTile(pointll)</c>.
    /// </summary>
    public GraphTile? GetGraphTile(PointLL pointll)
        => GetGraphTile(pointll, TileHierarchy.Levels()[^1].Level);

    // ------------------------------------------------------------------
    // Opposing edge helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Convenience method to get the opposing directed edge graph Id. Faithful port of
    /// <c>GetOpposingEdgeId(edgeid)</c>.
    /// </summary>
    public GraphId GetOpposingEdgeId(GraphId edgeid)
    {
        GraphTile? noTile = null;
        return GetOpposingEdgeId(edgeid, ref noTile);
    }

    /// <summary>
    /// Convenience method to get the opposing directed edge graph Id, updating
    /// <paramref name="oppTile"/> to the tile of the opposing edge. Faithful port of
    /// <c>GetOpposingEdgeId(edgeid, opp_tile)</c>.
    /// </summary>
    public GraphId GetOpposingEdgeId(GraphId edgeid, ref GraphTile? oppTile)
    {
        // If you can't get the tile you get an invalid id.
        GraphTile? tile = oppTile;
        if (GetGraphTile(edgeid, ref tile) is null)
        {
            return GraphId.Invalid;
        }

        // For now return an invalid Id if this is a transit edge.
        DirectedEdge directededge = tile!.DirectedEdge(edgeid);
        if (directededge.IsTransitLine)
        {
            return GraphId.Invalid;
        }

        // If the edge leaves the tile get the end node's tile.
        GraphId id = directededge.EndNode;
        if (GetGraphTile(id, ref oppTile) is null)
        {
            return GraphId.Invalid;
        }

        // Get the opposing edge.
        id.SetId(oppTile!.Node(id).EdgeIndex + directededge.OppIndex);
        return id;
    }

    /// <summary>
    /// Helper method to get the opposing directed edge along with its id, updating
    /// <paramref name="oppTile"/>. Faithful port of the 3-argument
    /// <c>GetOpposingEdgeId(edgeid, opp_edge, opp_tile)</c>.
    /// </summary>
    public GraphId GetOpposingEdgeId(GraphId edgeid, out DirectedEdge? oppEdge, ref GraphTile? oppTile)
    {
        oppEdge = null;
        GraphId oppEdgeid = GetOpposingEdgeId(edgeid, ref oppTile);
        if (oppEdgeid.IsValid())
        {
            oppEdge = oppTile!.DirectedEdge(oppEdgeid);
        }

        return oppEdgeid;
    }

    /// <summary>
    /// Convenience method to get the opposing directed edge. Faithful port of <c>GetOpposingEdge(edgeid)</c>.
    /// </summary>
    public DirectedEdge? GetOpposingEdge(GraphId edgeid)
    {
        GraphTile? noTile = null;
        return GetOpposingEdge(edgeid, ref noTile);
    }

    /// <summary>
    /// Convenience method to get the opposing directed edge, updating <paramref name="oppTile"/>.
    /// Faithful port of <c>GetOpposingEdge(edgeid, opp_tile)</c>.
    /// </summary>
    public DirectedEdge? GetOpposingEdge(GraphId edgeid, ref GraphTile? oppTile)
    {
        GraphId oppedgeid = GetOpposingEdgeId(edgeid, ref oppTile);
        return oppedgeid.IsValid() ? oppTile!.DirectedEdge(oppedgeid) : null;
    }

    /// <summary>
    /// Convenience method to get the opposing directed edge given the edge, updating
    /// <paramref name="oppTile"/>. Faithful port of <c>GetOpposingEdge(edge, opp_tile)</c>.
    /// </summary>
    public DirectedEdge? GetOpposingEdge(DirectedEdge edge, ref GraphTile? oppTile)
    {
        if (GetGraphTile(edge.EndNode, ref oppTile) is not null)
        {
            NodeInfo node = oppTile!.Node(edge.EndNode);
            return oppTile.DirectedEdge((int)(node.EdgeIndex + edge.OppIndex));
        }

        return null;
    }

    /// <summary>
    /// Convenience method to get an end node, updating <paramref name="endNodeTile"/>. Faithful port
    /// of <c>GetEndNode(edge, end_node_tile)</c>.
    /// </summary>
    public NodeInfo? GetEndNode(DirectedEdge edge, ref GraphTile? endNodeTile)
        => GetGraphTile(edge.EndNode, ref endNodeTile) is not null
            ? endNodeTile!.Node(edge.EndNode)
            : null;

    /// <summary>
    /// Gets the begin node of an edge by using its opposing edge's end node. Faithful port of
    /// <c>GetBeginNodeId(edge, begin_node_tile)</c>.
    /// </summary>
    public GraphId GetBeginNodeId(DirectedEdge edge, ref GraphTile? beginNodeTile)
    {
        // Grab the end node, maybe in an adjacent tile.
        GraphTile? maybeOtherTile = beginNodeTile;
        if (GetGraphTile(edge.EndNode, ref maybeOtherTile) is null)
        {
            return GraphId.Invalid;
        }

        NodeInfo node = maybeOtherTile!.Node(edge.EndNode);

        // Grab the opp edge, could also be in this adjacent tile.
        DirectedEdge oppEdge = maybeOtherTile.DirectedEdge((int)(node.EdgeIndex + edge.OppIndex));

        // Grab the end node of the opp_edge; it should be in the original tile.
        GetGraphTile(oppEdge.EndNode, ref beginNodeTile); // no-op if the original tile is correct
        return oppEdge.EndNode;
    }

    // ------------------------------------------------------------------
    // Edge connectivity
    // ------------------------------------------------------------------

    /// <summary>
    /// Convenience method to determine if 2 directed edges are connected. Faithful port of
    /// <c>AreEdgesConnected(edge1, edge2)</c>.
    /// </summary>
    public bool AreEdgesConnected(GraphId edge1, GraphId edge2)
    {
        // Check if there is a transition edge between n1 and n2.
        bool IsTransition(GraphId n1, GraphId n2)
        {
            if (n1.Level() == n2.Level())
            {
                return false;
            }

            GraphTile? tile = GetGraphTile(n1);
            if (tile is null)
            {
                return false;
            }

            NodeInfo ni = tile.Node(n1);
            if (ni.TransitionCount == 0)
            {
                return false;
            }

            for (uint i = 0; i < ni.TransitionCount; ++i)
            {
                if (tile.Transition(ni.TransitionIndex + i).EndNode() == n2)
                {
                    return true;
                }
            }

            return false;
        }

        // Get both directed edges.
        GraphTile? t1 = GetGraphTile(edge1);
        GraphTile? t2 = t1;

        if (t1 is null || (t2 = GetGraphTile(edge2, ref t2)) is null)
        {
            return false;
        }

        DirectedEdge de1 = t1.DirectedEdge(edge1);
        DirectedEdge de2 = t2.DirectedEdge(edge2);

        if (de1.EndNode == de2.EndNode || IsTransition(de1.EndNode, de2.EndNode))
        {
            return true;
        }

        // Get the opposing edge to de1.
        DirectedEdge? de1Opp = GetOpposingEdge(edge1, ref t1);
        if (de1Opp is not null
            && (de1Opp.Value.EndNode == de2.EndNode || IsTransition(de1Opp.Value.EndNode, de2.EndNode)))
        {
            return true;
        }

        // Get the opposing edge to de2 and compare to both edge1 endnodes.
        DirectedEdge? de2Opp = GetOpposingEdge(edge2, ref t2);
        return de1Opp is not null && de2Opp is not null
            && (de2Opp.Value.EndNode == de1.EndNode
                || de2Opp.Value.EndNode == de1Opp.Value.EndNode
                || IsTransition(de2Opp.Value.EndNode, de1.EndNode)
                || IsTransition(de2Opp.Value.EndNode, de1Opp.Value.EndNode));
    }

    /// <summary>
    /// Convenience method to determine if 2 directed edges are connected from the end node of edge1
    /// to the start node of edge2, updating <paramref name="tile"/>. Faithful port of
    /// <c>AreEdgesConnectedForward(edge1, edge2, tile)</c>.
    /// </summary>
    public bool AreEdgesConnectedForward(GraphId edge1, GraphId edge2, ref GraphTile? tile)
    {
        // Get the end node of edge1.
        GraphId endnode = EdgeEndNode(edge1, ref tile);
        if (endnode.TileBase() != edge1.TileBase())
        {
            tile = GetGraphTile(endnode);
            if (tile is null)
            {
                return false;
            }
        }

        // If edge2 is on a different tile level, transition to the node on that level.
        if (edge2.Level() != endnode.Level())
        {
            foreach (NodeTransition trans in tile!.GetNodeTransitions(endnode))
            {
                if (trans.EndNode().Level() == edge2.Level())
                {
                    endnode = trans.EndNode();
                    tile = GetGraphTile(endnode);
                    if (tile is null)
                    {
                        return false;
                    }

                    break;
                }
            }
        }

        // Check if edge2's Id is an outgoing directed edge of the node.
        NodeInfo node = tile!.Node(endnode);
        return node.EdgeIndex <= edge2.Id() && edge2.Id() < node.EdgeIndex + node.EdgeCount;
    }

    /// <summary>
    /// Convenience method to determine if 2 directed edges are connected forward. Faithful port of
    /// the no-tile overload <c>AreEdgesConnectedForward(edge1, edge2)</c>.
    /// </summary>
    public bool AreEdgesConnectedForward(GraphId edge1, GraphId edge2)
    {
        GraphTile? noTile = null;
        return AreEdgesConnectedForward(edge1, edge2, ref noTile);
    }

    // ------------------------------------------------------------------
    // Shortcuts
    // ------------------------------------------------------------------

    /// <summary>
    /// Gets the shortcut edge that includes the specified edge. Returns an invalid GraphId if the
    /// edge is not part of a shortcut. Faithful port of <c>GetShortcut(id)</c>.
    /// </summary>
    public GraphId GetShortcut(GraphId id)
    {
        // Lambda to get the continuing edge at a node. Skips the specified edge Id, transition edges,
        // shortcut edges, and transit connections. Returns null if more than one edge remains or no
        // continuing edge is found. `shortcutAtNode` is captured by reference (out via a 1-cell).
        bool[] shortcutAtNodeCell = { false };

        DirectedEdge? ContinuingEdge(GraphTile tile, GraphId edgeid, NodeInfo nodeinfo, out int contIdx)
        {
            uint idx = nodeinfo.EdgeIndex;
            int continuingIdx = -1;
            int lastIdx = (int)idx; // index of the directededge pointer at loop end
            shortcutAtNodeCell[0] = false;
            for (uint i = 0; i < nodeinfo.EdgeCount; i++, idx++)
            {
                DirectedEdge de = tile.DirectedEdge((int)idx);
                shortcutAtNodeCell[0] = shortcutAtNodeCell[0] || de.IsShortcut;
                if (idx == edgeid.Id() || !de.CanFormShortcut())
                {
                    continue;
                }

                if (continuingIdx != -1)
                {
                    // C++ sets continuing_edge = directededge + (edge_count - i), which lands past the
                    // last edge => signals "more than one" and breaks the single-edge guarantee.
                    continuingIdx = (int)(idx + (nodeinfo.EdgeCount - i));
                    continue;
                }

                continuingIdx = (int)idx;
            }

            lastIdx = (int)idx; // points one past the last edge (matches C++ `directededge` after loop)
            contIdx = continuingIdx;
            return continuingIdx == lastIdx || continuingIdx == -1 ? null : tile.DirectedEdge(continuingIdx);
        }

        // No shortcuts on the local level or transit level.
        if (id.Level() >= TileHierarchy.Levels()[^1].Level)
        {
            return GraphId.Invalid;
        }

        // If this edge is a shortcut, return this edge Id.
        GraphTile? tile = GetGraphTile(id);
        DirectedEdge directededge = tile!.DirectedEdge(id);
        if (directededge.IsShortcut)
        {
            return id;
        }

        // Walk backwards along the opposing directed edge until a shortcut beginning is found, or get
        // the continuing edge until a node that starts the shortcut is found, or there are 2 or more
        // other regular edges at the node.
        GraphId edgeid = id;
        NodeInfo? node = null;
        DirectedEdge? contDe;
        DirectedEdge? firstDe = GetOpposingEdge(id);
        while (true)
        {
            // Get the continuing directed edge. The initial case uses the opposing directed edge.
            if (node is not null)
            {
                contDe = ContinuingEdge(tile!, edgeid, node.Value, out _);
                if (firstDe is not null && contDe is not null && DirectedEdgeEquals(contDe.Value, firstDe.Value))
                {
                    break;
                }
            }
            else
            {
                contDe = firstDe;
            }

            if (contDe is null)
            {
                break;
            }

            // Get the end node and end node tile.
            GraphId endnode = contDe.Value.EndNode;
            if (contDe.Value.LeavesTile)
            {
                tile = GetGraphTile(endnode.TileBase());
            }

            node = tile!.Node(endnode);

            // Get the opposing edge Id and its directed edge.
            uint idx = node.Value.EdgeIndex + contDe.Value.OppIndex;
            edgeid = new GraphId(endnode.Tileid(), endnode.Level(), idx);
            directededge = tile.DirectedEdge(edgeid);

            // If this edge is itself not the beginning of a shortcut, but we encountered another
            // shortcut, we must have started the traversal outside a shortcut's internal edges.
            if (directededge.Superseded == 0 && shortcutAtNodeCell[0])
            {
                break;
            }

            if (directededge.Superseded != 0)
            {
                // Get the shortcut edge Id that supersedes this edge.
                uint shortcutIdx = node.Value.EdgeIndex + directededge.SupersededIdx() - 1;
                return new GraphId(endnode.Tileid(), endnode.Level(), shortcutIdx);
            }
        }

        return GraphId.Invalid;
    }

    // ------------------------------------------------------------------
    // Density / nodes / edges
    // ------------------------------------------------------------------

    /// <summary>
    /// Convenience method to get the relative edge density (from the begin node of an edge). Faithful
    /// port of <c>GetEdgeDensity(edgeid)</c>.
    /// </summary>
    public uint GetEdgeDensity(GraphId edgeid)
    {
        DirectedEdge? oppEdge = GetOpposingEdge(edgeid);
        if (oppEdge is not null)
        {
            GraphId id = oppEdge.Value.EndNode;
            GraphTile? tile = GetGraphTile(id);
            return tile is not null ? tile.Node(id).Density : 0;
        }

        return 0;
    }

    /// <summary>
    /// Gets node information for the specified node, updating <paramref name="nodeTile"/>. Faithful
    /// port of <c>nodeinfo(nodeid, node_tile)</c>.
    /// </summary>
    public NodeInfo? NodeInfo(GraphId nodeid, ref GraphTile? nodeTile)
        => GetGraphTile(nodeid, ref nodeTile) is not null ? nodeTile!.Node(nodeid) : null;

    /// <summary>Gets node information for the specified node. Faithful port of <c>nodeinfo(nodeid)</c>.</summary>
    public NodeInfo? NodeInfo(GraphId nodeid)
    {
        GraphTile? noTile = null;
        return NodeInfo(nodeid, ref noTile);
    }

    /// <summary>
    /// Gets the directed edge given its GraphId, updating <paramref name="edgeTile"/>. Faithful port
    /// of <c>directededge(edgeid, edge_tile)</c>.
    /// </summary>
    public DirectedEdge? Directededge(GraphId edgeid, ref GraphTile? edgeTile)
        => GetGraphTile(edgeid, ref edgeTile) is not null ? edgeTile!.DirectedEdge(edgeid) : null;

    /// <summary>Gets the directed edge given its GraphId. Faithful port of <c>directededge(edgeid)</c>.</summary>
    public DirectedEdge? Directededge(GraphId edgeid)
    {
        GraphTile? noTile = null;
        return Directededge(edgeid, ref noTile);
    }

    /// <summary>
    /// Gets the end nodes of a directed edge. Faithful port of
    /// <c>GetDirectedEdgeNodes(tile, edge)</c>. The first element is the start node, the second is the
    /// end node. The start node may be invalid in a regional extract.
    /// </summary>
    public (GraphId Start, GraphId End) GetDirectedEdgeNodes(GraphTile tile, DirectedEdge edge)
    {
        GraphId endNode = edge.EndNode;
        GraphId startNode = GraphId.Invalid;
        GraphTile? t2 = edge.LeavesTile ? GetGraphTile(endNode) : tile;
        if (t2 is not null)
        {
            int edgeIdx = (int)(t2.Node(endNode).EdgeIndex + edge.OppIndex);
            startNode = t2.DirectedEdge(edgeIdx).EndNode;
        }

        return (startNode, endNode);
    }

    /// <summary>
    /// Gets the end nodes of a directed edge given its id, updating <paramref name="edgeTile"/>.
    /// Faithful port of <c>GetDirectedEdgeNodes(edgeid, edge_tile)</c>.
    /// </summary>
    public (GraphId Start, GraphId End) GetDirectedEdgeNodes(GraphId edgeid, ref GraphTile? edgeTile)
    {
        if (edgeTile is not null && edgeTile.Id().TileBase() == edgeid.TileBase())
        {
            return GetDirectedEdgeNodes(edgeTile, edgeTile.DirectedEdge(edgeid));
        }

        edgeTile = GetGraphTile(edgeid);
        if (edgeTile is null)
        {
            return (GraphId.Invalid, GraphId.Invalid);
        }

        return GetDirectedEdgeNodes(edgeTile, edgeTile.DirectedEdge(edgeid));
    }

    /// <summary>Gets the end node of an edge. Faithful port of <c>edge_endnode(edgeid)</c>.</summary>
    public GraphId EdgeEndNode(GraphId edgeid)
    {
        GraphTile? noTile = null;
        return EdgeEndNode(edgeid, ref noTile);
    }

    /// <summary>
    /// Gets the end node of an edge, updating <paramref name="edgeTile"/>. Faithful port of
    /// <c>edge_endnode(edgeid, edge_tile)</c>.
    /// </summary>
    public GraphId EdgeEndNode(GraphId edgeid, ref GraphTile? edgeTile)
    {
        DirectedEdge? de = Directededge(edgeid, ref edgeTile);
        return de is not null ? de.Value.EndNode : GraphId.Invalid;
    }

    /// <summary>
    /// Gets the start node of an edge, updating <paramref name="tile"/>. Faithful port of
    /// <c>edge_startnode(edgeid, tile)</c>.
    /// </summary>
    public GraphId EdgeStartNode(GraphId edgeid, ref GraphTile? tile)
    {
        GraphId oppEdgeid = GetOpposingEdgeId(edgeid, ref tile);
        if (oppEdgeid.IsValid())
        {
            DirectedEdge? de = Directededge(oppEdgeid, ref tile);
            if (de is not null)
            {
                return de.Value.EndNode;
            }
        }

        return GraphId.Invalid;
    }

    /// <summary>Gets the start node of an edge. Faithful port of <c>edge_startnode(edgeid)</c>.</summary>
    public GraphId EdgeStartNode(GraphId edgeid)
    {
        GraphTile? noTile = null;
        return EdgeStartNode(edgeid, ref noTile);
    }

    /// <summary>
    /// Gets the edgeinfo of an edge, updating <paramref name="edgeTile"/>. Faithful port of
    /// <c>edgeinfo(edgeid, edge_tile)</c>.
    /// </summary>
    public EdgeInfo Edgeinfo(GraphId edgeid, ref GraphTile? edgeTile)
    {
        DirectedEdge? edge = Directededge(edgeid, ref edgeTile);
        if (edge is null)
        {
            throw new InvalidOperationException("Cannot find edgeinfo for edge: " + edgeid);
        }

        return edgeTile!.EdgeInfo(edge.Value);
    }

    /// <summary>Gets the edgeinfo of an edge. Faithful port of <c>edgeinfo(edgeid)</c>.</summary>
    public EdgeInfo Edgeinfo(GraphId edgeid)
    {
        GraphTile? noTile = null;
        return Edgeinfo(edgeid, ref noTile);
    }

    /// <summary>
    /// Gets the encoded shape (string) of an edge. Faithful port of <c>encoded_edge_shape(edgeid)</c>.
    /// </summary>
    public string EncodedEdgeShape(GraphId edgeid)
    {
        GraphTile? tDebug = GetGraphTile(edgeid);
        if (tDebug is null)
        {
            return string.Empty;
        }

        DirectedEdge directedEdge = tDebug.DirectedEdge(edgeid);
        var shape = new List<PointLL>(tDebug.EdgeInfo(directedEdge).Shape());
        if (!directedEdge.Forward)
        {
            shape.Reverse();
        }

        return Encoded.Encode(shape);
    }

    // ------------------------------------------------------------------
    // Tile set enumeration
    // ------------------------------------------------------------------

    /// <summary>
    /// Gets back a set of available tiles (all road and transit tiles). Faithful port of
    /// <c>GetTileSet()</c> (local-directory subset).
    /// </summary>
    public HashSet<GraphId> GetTileSet()
    {
        var tiles = new HashSet<GraphId>();
        if (string.IsNullOrEmpty(_tileDir))
        {
            return tiles;
        }

        // for each level (0..transit), crack open the level directory and enumerate the files.
        for (byte level = 0; level <= TileHierarchy.GetTransitLevel().Level; ++level)
        {
            EnumerateLevel(level, tiles);
        }

        return tiles;
    }

    /// <summary>
    /// Gets back a set of available tiles on the specified level. Faithful port of
    /// <c>GetTileSet(level)</c> (local-directory subset).
    /// </summary>
    public HashSet<GraphId> GetTileSet(byte level)
    {
        var tiles = new HashSet<GraphId>();
        if (!string.IsNullOrEmpty(_tileDir))
        {
            EnumerateLevel(level, tiles);
        }

        return tiles;
    }

    private void EnumerateLevel(byte level, HashSet<GraphId> tiles)
    {
        string rootDir = Path.Combine(_tileDir, level.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!Directory.Exists(rootDir))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories))
        {
            // add it if it can be parsed as a valid tile file name.
            try
            {
                tiles.Add(GraphTile.GetTileId(file));
            }
            catch
            {
                // silently skip files that can't be parsed by GetTileId (matches the C++ catch(...)).
            }
        }
    }

    /// <summary>
    /// Given an input bounding box (in lat,lng), returns the minimum bounding box which entirely
    /// encloses all the edges whose begin nodes are in the input bounding box. Faithful port of
    /// <c>GetMinimumBoundingBox(bb)</c>.
    /// </summary>
    /// <remarks>
    /// PORT-NOTE: the C++ uses <c>AABB2&lt;PointLL&gt;</c> and tests validity via
    /// <c>min_bb.minpt().IsValid()</c>. The C# <see cref="Aabb2T{T}"/> is parameterized on the scalar
    /// precision and stores plain <see cref="PointXY{T}"/> corners (no PointLL.IsValid), so we track
    /// the "uninitialized" state explicitly with a flag and feed PointLL values in as PointXY&lt;double&gt;.
    /// The geometry is otherwise identical to the engine.
    /// </remarks>
    public Aabb2T<double> GetMinimumBoundingBox(Aabb2T<double> bb)
    {
        List<GraphId> ids = TileHierarchy.GetGraphIds(bb);
        var minBb = new Aabb2T<double>();
        bool initialized = false;
        foreach (GraphId tileId in ids)
        {
            // Don't take too much ram.
            if (OverCommitted())
            {
                Trim();
            }

            // Look at every node in the tile.
            GraphTile? tile = GetGraphTile(tileId);
            for (uint i = 0; tile is not null && i < tile.Header().Nodecount(); i++)
            {
                NodeInfo node = tile.Node((int)i);
                PointLL nodeLl = node.LatLng(tile.Header().BaseLl());
                var nodePt = new PointXY<double>(nodeLl.X, nodeLl.Y);
                if (bb.Contains(nodePt))
                {
                    // If we haven't done anything with our bbox yet, initialize it.
                    if (!initialized)
                    {
                        minBb = new Aabb2T<double>(nodePt, nodePt);
                        initialized = true;
                    }

                    // Look at the shape of each edge leaving the node.
                    for (uint e = 0; e < node.EdgeCount; e++)
                    {
                        DirectedEdge diredge = tile.DirectedEdge((int)(node.EdgeIndex + e));
                        foreach (PointLL p in tile.EdgeInfo(diredge).Shape())
                        {
                            minBb.Expand(new PointXY<double>(p.X, p.Y));
                        }
                    }
                }
            }
        }

        return minBb;
    }

    /// <summary>
    /// Convenience method to get the timezone index at a node, updating <paramref name="tile"/>.
    /// Faithful port of <c>GetTimezone(node, tile)</c>.
    /// </summary>
    public int GetTimezone(GraphId node, ref GraphTile? tile)
    {
        GetGraphTile(node, ref tile);
        return tile is null ? 0 : (int)tile.Node(node).Timezone();
    }

    /// <summary>
    /// Convenience method to get the timezone index from an edge (preferring the start node's
    /// timezone), updating <paramref name="tile"/>. Faithful port of <c>GetTimezoneFromEdge(edge, tile)</c>.
    /// </summary>
    public int GetTimezoneFromEdge(GraphId edge, ref GraphTile? tile)
    {
        (GraphId first, GraphId second) = GetDirectedEdgeNodes(edge, ref tile);
        NodeInfo? node = NodeInfo(first, ref tile);
        if (node is not null)
        {
            return (int)node.Value.Timezone();
        }

        node = NodeInfo(second, ref tile);
        return node is not null ? (int)node.Value.Timezone() : 0;
    }

    private static bool DirectedEdgeEquals(DirectedEdge a, DirectedEdge b)
        => a.EndNode == b.EndNode && a.OppIndex == b.OppIndex;
}

/// <summary>
/// Limited graph reader wrapper. Faithful port of C++ <c>class LimitedGraphReader</c>.
/// </summary>
public class LimitedGraphReader
{
    private readonly GraphReader _reader;

    /// <summary>Constructor. Faithful port of <c>LimitedGraphReader(GraphReader&amp;)</c>.</summary>
    public LimitedGraphReader(GraphReader reader) => _reader = reader;

    /// <summary>Gets a tile given a GraphId. Faithful port of <c>GetGraphTile(graphid)</c>.</summary>
    public virtual GraphTile? GetGraphTile(GraphId graphid) => _reader.GetGraphTile(graphid);
}
