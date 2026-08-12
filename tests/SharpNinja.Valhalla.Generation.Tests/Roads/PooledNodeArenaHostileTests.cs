using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class PooledNodeArenaHostileTests
{
    [Fact]
    public void DefaultArena_UsesStageOwnedPoolsAndDoesNotRetainSharedPoolSlabs()
    {
        using var arena = new PooledNodeArena(
            slabCapacity: 4,
            memoryBudgetBytes: 4096);

        Assert.True(arena.UsesStageOwnedPools);

        NodeHandle handle = arena.Rent(CompletedNode(1));
        arena.Release(handle);

        Assert.Equal(1, arena.Metrics.TotalSlabsRented);
        Assert.Equal(0, arena.Metrics.LiveSlotCount);
    }


    [Fact]
    public void Release_DoubleReleaseIsRejectedAsStale()
    {
        using var arena = new PooledNodeArena(slabCapacity: 1, memoryBudgetBytes: 4096);
        NodeHandle handle = arena.Rent(CompletedNode(1));

        arena.Release(handle);

        Assert.Throws<StaleNodeHandleException>(() => arena.Release(handle));
        Assert.Equal(1, arena.Metrics.StaleHandleRejections);
    }

    [Fact]
    public void Resolve_CrossArenaHandleIsRejected()
    {
        using var first = new PooledNodeArena(slabCapacity: 1, memoryBudgetBytes: 4096);
        using var second = new PooledNodeArena(slabCapacity: 1, memoryBudgetBytes: 4096);
        NodeHandle handle = first.Rent(CompletedNode(1));

        Assert.Throws<StaleNodeHandleException>(() => second.Resolve(handle));
        Assert.Equal(1, second.Metrics.StaleHandleRejections);
    }

    [Fact]
    public void GenerationWrap_QuarantinesSlotInsteadOfAliasing()
    {
        using var arena = new PooledNodeArena(
            slabCapacity: 1,
            memoryBudgetBytes: 4096,
            initialGeneration: uint.MaxValue);
        NodeHandle handle = arena.Rent(CompletedNode(1));

        arena.Release(handle);
        NodeHandle replacement = arena.Rent(CompletedNode(2));

        Assert.NotEqual(handle.SlabIndex, replacement.SlabIndex);
        Assert.Equal(1, arena.Metrics.QuarantinedSlotCount);
        Assert.Equal(2, arena.Metrics.TotalSlabsRented);
    }

    [Fact]
    public void MemoryBudget_RejectsBudgetThatCannotFitSharedPoolSlab()
    {
        Assert.Throws<ValhallaGenerationResourceLimitException>(
            () => new PooledNodeArena(
                slabCapacity: 1,
                memoryBudgetBytes: 64));
    }

    [Fact]
    public void MemoryBudget_RejectsAndReturnsOversizedPoolRentals()
    {
        var itemPool = new OversizedArrayPool<NodeWorkItem>(64);
        var generationPool = new OversizedArrayPool<uint>(64);
        var statePool = new OversizedArrayPool<byte>(64);
        var freeSlotPool = new OversizedArrayPool<int>(64);
        using var arena = new PooledNodeArena(
            slabCapacity: 1,
            memoryBudgetBytes: 1024,
            itemPool,
            generationPool,
            statePool,
            freeSlotPool);

        Assert.Throws<ValhallaGenerationResourceLimitException>(
            () => arena.Rent(default));
        Assert.Equal(0, arena.Metrics.TotalSlabsRented);
        Assert.Equal(1, itemPool.ReturnCount);
        Assert.Equal(1, generationPool.ReturnCount);
        Assert.Equal(1, statePool.ReturnCount);
        Assert.Equal(1, freeSlotPool.ReturnCount);
    }

    [Fact]
    public void MillionRentReleaseCycles_ReuseOneClearedSlot()
    {
        const int iterations = 1_000_000;
        using var arena = new PooledNodeArena(slabCapacity: 1, memoryBudgetBytes: 4096);

        for (int index = 0; index < iterations; index++)
        {
            NodeHandle handle = arena.Rent(CompletedNode(index + 1L));
            arena.Release(handle);
        }

        Assert.Equal(iterations, arena.Metrics.TotalSlotRents);
        Assert.Equal(iterations - 1, arena.Metrics.SlotReuseCount);
        Assert.Equal(1, arena.Metrics.TotalSlabsRented);
        Assert.Equal(1, arena.Metrics.PeakLiveSlotCount);
        Assert.Equal(0, arena.Metrics.LiveSlotCount);
    }

    private static NodeWorkItem CompletedNode(long osmNodeId) => new()
    {
        OsmNodeId = osmNodeId,
        StableGraphId = GraphId.Invalid,
        LifecycleFlags = NodeLifecycleFlags.AllDurableStateWritten,
    };

    private sealed class OversizedArrayPool<T>(int length) : System.Buffers.ArrayPool<T>
    {
        internal int ReturnCount { get; private set; }

        public override T[] Rent(int minimumLength) =>
            new T[Math.Max(length, minimumLength)];

        public override void Return(T[] array, bool clearArray = false)
        {
            if (clearArray)
            {
                Array.Clear(array);
            }

            ReturnCount++;
        }
    }
}
