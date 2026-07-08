// Faithful C# port of Valhalla's gtest suite test/graphreader.cc.
//
// Covers the tile-cache behavior (SimpleTileCache over-commit/clear/trim and the full TileCacheLRU
// hard/soft eviction matrix) plus the out-of-range PointLL query on a directory-backed GraphReader.
//
// PORT-NOTES:
//   - TEST(ConnectivityMap, Basic) is NOT ported: connectivity_map_t is an EXCLUDED module.
//   - The C++ `test_cache` exposes SimpleTileCache::cache_size_ / max_cache_size_ via `using`
//     (friend-like access). The C# SimpleTileCache exposes those as protected members; the
//     TestCache subclass below re-exposes them, exactly mirroring the C++ test fixture.
//   - The C++ TestGraphTile fixture builds a header-only GraphTile (sets only graphid + end_offset,
//     bypassing the section-offset Initialize()). The C# GraphTile.CreateForTest factory reproduces
//     that header-only tile. CheckGraphTile asserts header().graphid() and header().end_offset().
//   - SimpleCache.QueryByPointOutOfRangeLL uses a directory-backed GraphReader; with an empty/missing
//     tile_dir GetGraphTile returns null for both out-of-range and (here) any LL, matching the C++
//     expectation that out-of-range LLs return null.

using System;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

using Xunit;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class GraphReaderTests
{
    // Mirrors the C++ `class test_cache : public SimpleTileCache` which re-exposes the protected
    // cache_size_ / max_cache_size_ members.
    private sealed class TestCache : SimpleTileCache
    {
        public TestCache(long maxSize)
            : base(maxSize)
        {
        }

        public long CacheSizeAccess
        {
            get => CacheSize;
            set => CacheSize = value;
        }

        public long MaxCacheSizeAccess
        {
            get => MaxCacheSize;
            set => MaxCacheSize = value;
        }
    }

    private static TestCache MakeCache(long cacheSize) => new(cacheSize);

    private static GraphTile MakeTile(GraphId id, uint size) => GraphTile.CreateForTest(id, size);

    private static void CheckGraphTile(GraphTile? tile, GraphId expectedId, uint expectedSize)
    {
        Assert.NotNull(tile);
        Assert.Equal(expectedId.Value, tile!.Header().Graphid().Value);
        Assert.Equal(expectedSize, tile.Header().EndOffset());
    }

    [Fact]
    public void QueryByPointOutOfRangeLL()
    {
        var reader = new GraphReader(new GraphReader.Config { TileDir = "test/gphrdr_test" });

        // Latitude out of range.
        Assert.Null(reader.GetGraphTile(new PointLL(60.0, 100.0)));

        // Longitude out of range.
        Assert.Null(reader.GetGraphTile(new PointLL(460.0, 60.0)));
    }

    [Fact]
    public void CacheLimitsZeroSizeOvercommited()
    {
        Assert.False(MakeCache(0).OverCommitted());
    }

    [Fact]
    public void CacheLimitsMinSizeOvercommited()
    {
        Assert.False(MakeCache(1).OverCommitted());
    }

    [Fact]
    public void CacheLimitsOvercommitBasic()
    {
        TestCache cache = MakeCache(10);
        cache.CacheSizeAccess = 20;
        Assert.True(cache.OverCommitted());

        cache.CacheSizeAccess = 1;
        cache.MaxCacheSizeAccess = 0;
        Assert.True(cache.OverCommitted());
    }

    [Fact]
    public void CacheLimitsNoOvercommitAfterClear()
    {
        TestCache cache = MakeCache(10);
        cache.CacheSizeAccess = cache.MaxCacheSizeAccess + 1;
        Assert.True(cache.OverCommitted());
        cache.Clear();
        Assert.False(cache.OverCommitted());
    }

    [Fact]
    public void SimpleCacheClear()
    {
        var cache = new SimpleTileCache(400);

        var id1 = new GraphId(100, 2, 0);
        GraphTile? tile1 = cache.Put(id1, MakeTile(id1, 123), 123);
        Assert.Same(tile1, cache.Get(id1));
        CheckGraphTile(tile1, id1, 123);

        Assert.False(cache.OverCommitted());

        var id2 = new GraphId(300, 1, 0);
        GraphTile? tile2 = cache.Put(id2, MakeTile(id2, 200), 200);
        Assert.Same(tile2, cache.Get(id2));
        CheckGraphTile(tile2, id2, 200);

        Assert.False(cache.OverCommitted());

        var id3 = new GraphId(1000, 0, 0);
        GraphTile? tile3 = cache.Put(id3, MakeTile(id3, 500), 500);
        Assert.Same(tile3, cache.Get(id3));
        CheckGraphTile(tile3, id3, 500);

        Assert.True(cache.OverCommitted());

        CheckGraphTile(cache.Get(new GraphId(300, 1, 0)), id2, 200);
        CheckGraphTile(cache.Get(new GraphId(100, 2, 0)), id1, 123);
        CheckGraphTile(cache.Get(new GraphId(1000, 0, 0)), id3, 500);

        Assert.True(cache.Contains(id1));
        Assert.True(cache.Contains(id2));
        Assert.True(cache.Contains(id3));

        cache.Clear();

        Assert.False(cache.OverCommitted());

        Assert.False(cache.Contains(id1));
        Assert.False(cache.Contains(id2));
        Assert.False(cache.Contains(id3));

        Assert.Null(cache.Get(id1));
        Assert.Null(cache.Get(id2));
        Assert.Null(cache.Get(id3));
    }

    [Fact]
    public void SimpleCacheTrim()
    {
        var cache = new SimpleTileCache(400);

        var id1 = new GraphId(100, 2, 0);
        GraphTile? tile1 = cache.Put(id1, MakeTile(id1, 123), 123);
        Assert.Same(tile1, cache.Get(id1));
        CheckGraphTile(tile1, id1, 123);

        Assert.False(cache.OverCommitted());

        var id2 = new GraphId(300, 1, 0);
        GraphTile? tile2 = cache.Put(id2, MakeTile(id2, 200), 200);
        Assert.Same(tile2, cache.Get(id2));
        CheckGraphTile(tile2, id2, 200);

        Assert.False(cache.OverCommitted());

        var id3 = new GraphId(1000, 0, 0);
        GraphTile? tile3 = cache.Put(id3, MakeTile(id3, 500), 500);
        Assert.Same(tile3, cache.Get(id3));
        CheckGraphTile(tile3, id3, 500);

        Assert.True(cache.OverCommitted());

        cache.Trim();

        Assert.False(cache.OverCommitted());

        Assert.False(cache.Contains(id1));
        Assert.False(cache.Contains(id2));
        Assert.False(cache.Contains(id3));

        Assert.Null(cache.Get(id1));
        Assert.Null(cache.Get(id2));
        Assert.Null(cache.Get(id3));
    }

    [Fact]
    public void CacheLruHardCreation()
    {
        _ = new TileCacheLRU(0, TileCacheLRU.MemoryLimitControl.Hard);
        _ = new TileCacheLRU(1023, TileCacheLRU.MemoryLimitControl.Hard);
        _ = new TileCacheLRU(1073741824, TileCacheLRU.MemoryLimitControl.Hard);
    }

    [Fact]
    public void CacheLruHardInsertSingleItemBiggerThanCacheSize()
    {
        var cache = new TileCacheLRU(1023, TileCacheLRU.MemoryLimitControl.Hard);

        var id1 = new GraphId(100, 2, 0);

        Assert.Throws<InvalidOperationException>(() => cache.Put(id1, MakeTile(id1, 2000), 2000));
        Assert.Null(cache.Get(id1));
        Assert.False(cache.Contains(id1));
    }

    [Fact]
    public void CacheLruHardInsertCacheFullOneshot()
    {
        const uint tile1Size = 1234;

        var cache = new TileCacheLRU(tile1Size, TileCacheLRU.MemoryLimitControl.Hard);

        var tile1Id = new GraphId(1000, 1, 0);
        GraphTile? tile1 = cache.Put(tile1Id, MakeTile(tile1Id, tile1Size), tile1Size);
        Assert.Same(tile1, cache.Get(tile1Id));
        Assert.False(cache.OverCommitted());

        CheckGraphTile(tile1, tile1Id, tile1Size);
        Assert.True(cache.Contains(tile1Id));
    }

    [Fact]
    public void CacheLruHardInsertCacheFull()
    {
        var cache = new TileCacheLRU(10000, TileCacheLRU.MemoryLimitControl.Hard);

        const uint tile1Size = 4000;
        var tile1Id = new GraphId(1000, 1, 0);
        GraphTile? tile1 = cache.Put(tile1Id, MakeTile(tile1Id, tile1Size), tile1Size);
        Assert.Same(tile1, cache.Get(tile1Id));
        CheckGraphTile(tile1, tile1Id, tile1Size);

        const uint tile2Size = 6000;
        var tile2Id = new GraphId(33, 2, 0);
        GraphTile? tile2 = cache.Put(tile2Id, MakeTile(tile2Id, tile2Size), tile2Size);
        Assert.Same(tile2, cache.Get(tile2Id));
        CheckGraphTile(tile2, tile2Id, tile2Size);

        Assert.False(cache.OverCommitted());

        Assert.True(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));
    }

    [Fact]
    public void CacheLruHardInsertNoEviction()
    {
        var cache = new TileCacheLRU(1023, TileCacheLRU.MemoryLimitControl.Hard);

        var id1 = new GraphId(100, 2, 0);
        GraphTile? tile1 = cache.Put(id1, MakeTile(id1, 123), 123);
        Assert.Same(tile1, cache.Get(id1));
        CheckGraphTile(tile1, id1, 123);

        var id2 = new GraphId(300, 1, 0);
        GraphTile? tile2 = cache.Put(id2, MakeTile(id2, 200), 200);
        Assert.Same(tile2, cache.Get(id2));
        CheckGraphTile(tile2, id2, 200);

        var id3 = new GraphId(1000, 0, 0);
        GraphTile? tile3 = cache.Put(id3, MakeTile(id3, 500), 500);
        Assert.Same(tile3, cache.Get(id3));
        CheckGraphTile(tile3, id3, 500);

        CheckGraphTile(cache.Get(new GraphId(300, 1, 0)), id2, 200);
        CheckGraphTile(cache.Get(new GraphId(100, 2, 0)), id1, 123);
        CheckGraphTile(cache.Get(new GraphId(1000, 0, 0)), id3, 500);

        Assert.Null(cache.Get(new GraphId(1345, 1, 0)));
        Assert.Null(cache.Get(new GraphId(100, 1, 0)));
        Assert.Null(cache.Get(new GraphId(0, 0, 0)));
    }

    [Fact]
    public void CacheLruHardInsertWithEvictionBasic()
    {
        var cache = new TileCacheLRU(500, TileCacheLRU.MemoryLimitControl.Hard);

        var tile1Id = new GraphId(1000, 1, 0);
        const uint tile1Size = 200;
        cache.Put(tile1Id, MakeTile(tile1Id, tile1Size), tile1Size);

        var tile2Id = new GraphId(300, 2, 0);
        const uint tile2Size = 250;
        cache.Put(tile2Id, MakeTile(tile2Id, tile2Size), tile2Size);

        var tile3Id = new GraphId(1, 1, 0);
        const uint tile3Size = 45;
        cache.Put(tile3Id, MakeTile(tile3Id, tile3Size), tile3Size);

        Assert.True(cache.Contains(tile3Id));
        Assert.True(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));

        // Insertion requiring an eviction: tile1 (first inserted) should be evicted.
        var tile4Id = new GraphId(400, 2, 0);
        const uint tile4Size = 20;
        cache.Put(tile4Id, MakeTile(tile4Id, tile4Size), tile4Size);

        Assert.False(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));
        Assert.True(cache.Contains(tile3Id));
        Assert.True(cache.Contains(tile4Id));

        // Access tile2 to promote it; next eviction should be tile3.
        CheckGraphTile(cache.Get(tile2Id), tile2Id, tile2Size);

        var tile5Id = new GraphId(999, 1, 0);
        const uint tile5Size = 200;
        cache.Put(tile5Id, MakeTile(tile5Id, tile5Size), tile5Size);

        Assert.False(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));
        Assert.False(cache.Contains(tile3Id));
        Assert.True(cache.Contains(tile4Id));
        Assert.True(cache.Contains(tile5Id));

        Assert.Null(cache.Get(tile1Id));
        Assert.Null(cache.Get(tile3Id));

        CheckGraphTile(cache.Get(tile2Id), tile2Id, tile2Size);
        CheckGraphTile(cache.Get(tile4Id), tile4Id, tile4Size);
        CheckGraphTile(cache.Get(tile5Id), tile5Id, tile5Size);
    }

    [Fact]
    public void CacheLruHardOverwriteSameSize()
    {
        var cache = new TileCacheLRU(500, TileCacheLRU.MemoryLimitControl.Hard);

        var tile1Id = new GraphId(1000, 1, 0);
        const uint tile1Size = 200;
        cache.Put(tile1Id, MakeTile(tile1Id, tile1Size), tile1Size);

        var tile2Id = new GraphId(300, 2, 0);
        const uint tile2Size = 250;
        cache.Put(tile2Id, MakeTile(tile2Id, tile2Size), tile2Size);

        var tile3Id = new GraphId(1, 1, 0);
        const uint tile3Size = 45;
        cache.Put(tile3Id, MakeTile(tile3Id, tile3Size), tile3Size);

        cache.Put(tile2Id, MakeTile(tile2Id, tile2Size), tile2Size);

        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);
        CheckGraphTile(cache.Get(tile2Id), tile2Id, tile2Size);
        CheckGraphTile(cache.Get(tile3Id), tile3Id, tile3Size);
        Assert.True(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));
        Assert.True(cache.Contains(tile3Id));
    }

    [Fact]
    public void CacheLruHardOverwriteSmallerSize()
    {
        var cache = new TileCacheLRU(500, TileCacheLRU.MemoryLimitControl.Hard);

        var tile1Id = new GraphId(1000, 1, 0);
        const uint tile1Size = 200;
        cache.Put(tile1Id, MakeTile(tile1Id, tile1Size), tile1Size);

        var tile2Id = new GraphId(300, 2, 0);
        const uint tile2Size = 250;
        cache.Put(tile2Id, MakeTile(tile2Id, tile2Size), tile2Size);

        var tile3Id = new GraphId(1, 1, 0);
        const uint tile3Size = 45;
        cache.Put(tile3Id, MakeTile(tile3Id, tile3Size), tile3Size);

        const uint tile4Size = 8;
        cache.Put(tile3Id, MakeTile(tile3Id, tile4Size), tile4Size);

        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);
        CheckGraphTile(cache.Get(tile2Id), tile2Id, tile2Size);
        CheckGraphTile(cache.Get(tile3Id), tile3Id, tile4Size);
        Assert.True(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));
        Assert.True(cache.Contains(tile3Id));
    }

    [Fact]
    public void CacheLruHardOverwriteBiggerSizeNoEviction()
    {
        var cache = new TileCacheLRU(500, TileCacheLRU.MemoryLimitControl.Hard);

        var tile1Id = new GraphId(1000, 1, 0);
        const uint tile1Size = 200;
        cache.Put(tile1Id, MakeTile(tile1Id, tile1Size), tile1Size);

        var tile2Id = new GraphId(300, 2, 0);
        const uint tile2Size = 250;
        cache.Put(tile2Id, MakeTile(tile2Id, tile2Size), tile2Size);

        var tile3Id = new GraphId(1, 1, 0);
        const uint tile3Size = 20;
        cache.Put(tile3Id, MakeTile(tile3Id, tile3Size), tile3Size);

        const uint tile4Size = 45;
        cache.Put(tile3Id, MakeTile(tile3Id, tile4Size), tile4Size);

        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);
        CheckGraphTile(cache.Get(tile2Id), tile2Id, tile2Size);
        CheckGraphTile(cache.Get(tile3Id), tile3Id, tile4Size);
        Assert.True(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));
        Assert.True(cache.Contains(tile3Id));
    }

    [Fact]
    public void CacheLruHardOverwriteBiggerSizeEvictionOne()
    {
        var cache = new TileCacheLRU(500, TileCacheLRU.MemoryLimitControl.Hard);

        var tile1Id = new GraphId(1000, 1, 0);
        const uint tile1Size = 200;
        cache.Put(tile1Id, MakeTile(tile1Id, tile1Size), tile1Size);

        var tile2Id = new GraphId(300, 2, 0);
        const uint tile2Size = 250;
        cache.Put(tile2Id, MakeTile(tile2Id, tile2Size), tile2Size);

        var tile3Id = new GraphId(1, 1, 0);
        const uint tile3Size = 45;
        cache.Put(tile3Id, MakeTile(tile3Id, tile3Size), tile3Size);

        Assert.True(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));
        Assert.True(cache.Contains(tile3Id));

        const uint tile4Size = 260;
        cache.Put(tile2Id, MakeTile(tile2Id, tile4Size), tile4Size);

        Assert.False(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));
        Assert.True(cache.Contains(tile3Id));

        Assert.Null(cache.Get(tile1Id));
        CheckGraphTile(cache.Get(tile2Id), tile2Id, tile4Size);
        CheckGraphTile(cache.Get(tile3Id), tile3Id, tile3Size);
    }

    [Fact]
    public void CacheLruHardOverwriteBiggerSizeEvictionMultiple()
    {
        var cache = new TileCacheLRU(500, TileCacheLRU.MemoryLimitControl.Hard);

        var tile1Id = new GraphId(1000, 1, 0);
        const uint tile1Size = 200;
        cache.Put(tile1Id, MakeTile(tile1Id, tile1Size), tile1Size);

        var tile2Id = new GraphId(300, 2, 0);
        const uint tile2Size = 250;
        cache.Put(tile2Id, MakeTile(tile2Id, tile2Size), tile2Size);

        var tile3Id = new GraphId(1, 1, 0);
        const uint tile3Size = 45;
        cache.Put(tile3Id, MakeTile(tile3Id, tile3Size), tile3Size);

        Assert.True(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));
        Assert.True(cache.Contains(tile3Id));

        const uint tile4Size = 480;
        cache.Put(tile2Id, MakeTile(tile2Id, tile4Size), tile4Size);

        Assert.False(cache.Contains(tile1Id));
        Assert.False(cache.Contains(tile3Id));
        Assert.True(cache.Contains(tile2Id));

        Assert.Null(cache.Get(tile1Id));
        Assert.Null(cache.Get(tile3Id));
        CheckGraphTile(cache.Get(tile2Id), tile2Id, tile4Size);
    }

    [Fact]
    public void CacheLruHardInsertWithEvictionEntireCache()
    {
        var cache = new TileCacheLRU(1000, TileCacheLRU.MemoryLimitControl.Hard);

        var tile1Id = new GraphId(1000, 1, 0);
        const uint tile1Size = 200;
        cache.Put(tile1Id, MakeTile(tile1Id, tile1Size), tile1Size);

        var tile2Id = new GraphId(300, 2, 0);
        const uint tile2Size = 300;
        cache.Put(tile2Id, MakeTile(tile2Id, tile2Size), tile2Size);

        var tile3Id = new GraphId(1, 1, 0);
        const uint tile3Size = 900;
        cache.Put(tile3Id, MakeTile(tile3Id, tile3Size), tile3Size);

        Assert.True(cache.Contains(tile3Id));
        Assert.False(cache.Contains(tile1Id));
        Assert.False(cache.Contains(tile2Id));

        CheckGraphTile(cache.Get(tile3Id), tile3Id, tile3Size);

        Assert.Null(cache.Get(tile1Id));
        Assert.Null(cache.Get(tile2Id));
    }

    [Fact]
    public void CacheLruHardMixedInsertOverwrite()
    {
        var cache = new TileCacheLRU(4000, TileCacheLRU.MemoryLimitControl.Hard);

        var tile1Id = new GraphId(1000, 1, 0);
        const uint tile1Size = 1000;
        cache.Put(tile1Id, MakeTile(tile1Id, tile1Size), tile1Size);

        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);

        var tile2Id = new GraphId(300, 2, 0);
        const uint tile2Size = 300;
        cache.Put(tile2Id, MakeTile(tile2Id, tile2Size), tile2Size);

        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);

        var tile3Id = new GraphId(1, 1, 0);
        const uint tile3Size = 900;
        cache.Put(tile3Id, MakeTile(tile3Id, tile3Size), tile3Size);

        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);
        CheckGraphTile(cache.Get(tile3Id), tile3Id, tile3Size);

        const uint tile4Size = 123;
        cache.Put(tile3Id, MakeTile(tile3Id, tile4Size), tile4Size);

        var tile5Id = new GraphId(1234, 1, 0);
        const uint tile5Size = 200;
        cache.Put(tile5Id, MakeTile(tile5Id, tile5Size), tile5Size);

        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);
        CheckGraphTile(cache.Get(tile2Id), tile2Id, tile2Size);
        CheckGraphTile(cache.Get(tile3Id), tile3Id, tile4Size);
        CheckGraphTile(cache.Get(tile5Id), tile5Id, tile5Size);
    }

    [Fact]
    public void CacheLruHardMixedInsertOverwriteEvict()
    {
        var cache = new TileCacheLRU(500, TileCacheLRU.MemoryLimitControl.Hard);

        var tile1Id = new GraphId(1000, 1, 0);
        const uint tile1Size = 200;
        cache.Put(tile1Id, MakeTile(tile1Id, tile1Size), tile1Size);

        var tile2Id = new GraphId(300, 2, 0);
        const uint tile2Size = 250;
        cache.Put(tile2Id, MakeTile(tile2Id, tile2Size), tile2Size);

        // Bump tile1 (with Get).
        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);

        var tile3Id = new GraphId(1, 1, 0);
        const uint tile3Size = 45;
        cache.Put(tile3Id, MakeTile(tile3Id, tile3Size), tile3Size);

        Assert.True(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));
        Assert.True(cache.Contains(tile3Id));

        const uint tile4Size = 255;
        cache.Put(tile3Id, MakeTile(tile3Id, tile4Size), tile4Size);

        Assert.True(cache.Contains(tile1Id));
        Assert.False(cache.Contains(tile2Id));
        Assert.True(cache.Contains(tile3Id));

        Assert.Null(cache.Get(tile2Id));
        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);
        CheckGraphTile(cache.Get(tile3Id), tile3Id, tile4Size);
    }

    [Fact]
    public void CacheLruHardClearBasic()
    {
        var cache = new TileCacheLRU(2000, TileCacheLRU.MemoryLimitControl.Hard);

        var tile1Id = new GraphId(10, 1, 0);
        const uint tile1Size = 500;
        cache.Put(tile1Id, MakeTile(tile1Id, tile1Size), tile1Size);

        var tile2Id = new GraphId(300, 2, 0);
        const uint tile2Size = 123;
        cache.Put(tile2Id, MakeTile(tile2Id, tile2Size), tile2Size);

        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);
        CheckGraphTile(cache.Get(tile2Id), tile2Id, tile2Size);
        Assert.True(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));

        cache.Clear();

        Assert.False(cache.Contains(tile1Id));
        Assert.False(cache.Contains(tile2Id));
        Assert.Null(cache.Get(tile1Id));
        Assert.Null(cache.Get(tile2Id));
    }

    [Fact]
    public void CacheLruHardTrimBasic()
    {
        // Trim should not have any effect on a cache with hard memory limit policy.
        var cache = new TileCacheLRU(2000, TileCacheLRU.MemoryLimitControl.Hard);

        var tile1Id = new GraphId(10, 1, 0);
        const uint tile1Size = 500;
        cache.Put(tile1Id, MakeTile(tile1Id, tile1Size), tile1Size);

        var tile2Id = new GraphId(300, 2, 0);
        const uint tile2Size = 123;
        cache.Put(tile2Id, MakeTile(tile2Id, tile2Size), tile2Size);

        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);
        CheckGraphTile(cache.Get(tile2Id), tile2Id, tile2Size);
        Assert.True(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));

        cache.Trim();

        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);
        CheckGraphTile(cache.Get(tile2Id), tile2Id, tile2Size);
        Assert.True(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));
    }

    [Fact]
    public void CacheLruSoftInsertBecomeOvercommittedTrim()
    {
        var cache = new TileCacheLRU(2000, TileCacheLRU.MemoryLimitControl.Soft);

        var tile1Id = new GraphId(10, 1, 0);
        const uint tile1Size = 1500;
        cache.Put(tile1Id, MakeTile(tile1Id, tile1Size), tile1Size);

        Assert.False(cache.OverCommitted());

        var tile2Id = new GraphId(300, 2, 0);
        const uint tile2Size = 2000;
        cache.Put(tile2Id, MakeTile(tile2Id, tile2Size), tile2Size);

        Assert.True(cache.OverCommitted());

        var tile3Id = new GraphId(500, 1, 0);
        const uint tile3Size = 100;
        cache.Put(tile3Id, MakeTile(tile3Id, tile3Size), tile3Size);

        Assert.True(cache.OverCommitted());

        // With soft memory limit there should be no evictions yet.
        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);
        CheckGraphTile(cache.Get(tile2Id), tile2Id, tile2Size);
        CheckGraphTile(cache.Get(tile3Id), tile3Id, tile3Size);
        Assert.True(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));
        Assert.True(cache.Contains(tile3Id));

        cache.Trim();

        Assert.Null(cache.Get(tile1Id));
        Assert.Null(cache.Get(tile2Id));
        CheckGraphTile(cache.Get(tile3Id), tile3Id, tile3Size);
        Assert.False(cache.Contains(tile1Id));
        Assert.False(cache.Contains(tile2Id));
        Assert.True(cache.Contains(tile3Id));
    }

    [Fact]
    public void CacheLruSoftInsertBecomeOvercommittedClear()
    {
        var cache = new TileCacheLRU(2000, TileCacheLRU.MemoryLimitControl.Soft);

        var tile1Id = new GraphId(10, 1, 0);
        const uint tile1Size = 1500;
        cache.Put(tile1Id, MakeTile(tile1Id, tile1Size), tile1Size);

        Assert.False(cache.OverCommitted());

        var tile2Id = new GraphId(300, 2, 0);
        const uint tile2Size = 2000;
        cache.Put(tile2Id, MakeTile(tile2Id, tile2Size), tile2Size);

        Assert.True(cache.OverCommitted());

        var tile3Id = new GraphId(500, 1, 0);
        const uint tile3Size = 100;
        cache.Put(tile3Id, MakeTile(tile3Id, tile3Size), tile3Size);

        Assert.True(cache.OverCommitted());

        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);
        CheckGraphTile(cache.Get(tile2Id), tile2Id, tile2Size);
        CheckGraphTile(cache.Get(tile3Id), tile3Id, tile3Size);
        Assert.True(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));
        Assert.True(cache.Contains(tile3Id));

        cache.Clear();

        Assert.False(cache.OverCommitted());

        Assert.Null(cache.Get(tile1Id));
        Assert.Null(cache.Get(tile2Id));
        Assert.Null(cache.Get(tile3Id));
        Assert.False(cache.Contains(tile1Id));
        Assert.False(cache.Contains(tile2Id));
        Assert.False(cache.Contains(tile3Id));
    }

    [Fact]
    public void CacheLruSoftUndercommittedTrim()
    {
        var cache = new TileCacheLRU(5000, TileCacheLRU.MemoryLimitControl.Soft);

        var tile1Id = new GraphId(10, 1, 0);
        const uint tile1Size = 300;
        cache.Put(tile1Id, MakeTile(tile1Id, tile1Size), tile1Size);

        Assert.False(cache.OverCommitted());

        var tile2Id = new GraphId(300, 2, 0);
        const uint tile2Size = 2000;
        cache.Put(tile2Id, MakeTile(tile2Id, tile2Size), tile2Size);

        Assert.False(cache.OverCommitted());

        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);
        CheckGraphTile(cache.Get(tile2Id), tile2Id, tile2Size);
        Assert.True(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));

        cache.Trim();

        Assert.False(cache.OverCommitted());

        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);
        CheckGraphTile(cache.Get(tile2Id), tile2Id, tile2Size);
        Assert.True(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));
    }

    [Fact]
    public void CacheLruSoftInsertWithEvictionBasic()
    {
        var cache = new TileCacheLRU(500, TileCacheLRU.MemoryLimitControl.Soft);

        var tile1Id = new GraphId(1000, 1, 0);
        const uint tile1Size = 200;
        cache.Put(tile1Id, MakeTile(tile1Id, tile1Size), tile1Size);

        var tile2Id = new GraphId(300, 2, 0);
        const uint tile2Size = 250;
        cache.Put(tile2Id, MakeTile(tile2Id, tile2Size), tile2Size);

        var tile3Id = new GraphId(1, 1, 0);
        const uint tile3Size = 50;
        cache.Put(tile3Id, MakeTile(tile3Id, tile3Size), tile3Size);

        var tile4Id = new GraphId(400, 2, 0);
        const uint tile4Size = 270;
        cache.Put(tile4Id, MakeTile(tile4Id, tile4Size), tile4Size);

        Assert.True(cache.OverCommitted());

        // Access tile1 to promote it; eviction order changes.
        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);

        // Should evict tiles 2 and 3.
        cache.Trim();

        Assert.False(cache.OverCommitted());

        Assert.True(cache.Contains(tile1Id));
        Assert.False(cache.Contains(tile2Id));
        Assert.False(cache.Contains(tile3Id));
        Assert.True(cache.Contains(tile4Id));

        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);
        CheckGraphTile(cache.Get(tile4Id), tile4Id, tile4Size);
        Assert.Null(cache.Get(tile2Id));
        Assert.Null(cache.Get(tile3Id));
    }

    [Fact]
    public void CacheLruSoftTrimOnExactlyFullCache()
    {
        var cache = new TileCacheLRU(100000, TileCacheLRU.MemoryLimitControl.Soft);

        var tile1Id = new GraphId(10, 1, 0);
        const uint tile1Size = 60000;
        cache.Put(tile1Id, MakeTile(tile1Id, tile1Size), tile1Size);

        Assert.False(cache.OverCommitted());

        var tile2Id = new GraphId(300, 2, 0);
        const uint tile2Size = 40000;
        cache.Put(tile2Id, MakeTile(tile2Id, tile2Size), tile2Size);

        Assert.False(cache.OverCommitted());

        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);
        CheckGraphTile(cache.Get(tile2Id), tile2Id, tile2Size);
        Assert.True(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));

        cache.Trim();

        Assert.False(cache.OverCommitted());
        Assert.True(cache.Contains(tile1Id));
        Assert.True(cache.Contains(tile2Id));
        CheckGraphTile(cache.Get(tile1Id), tile1Id, tile1Size);
        CheckGraphTile(cache.Get(tile2Id), tile2Id, tile2Size);
    }
}
