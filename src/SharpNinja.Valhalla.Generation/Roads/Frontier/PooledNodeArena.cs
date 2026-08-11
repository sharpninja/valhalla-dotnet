using System.Buffers;
using System.Runtime.CompilerServices;
using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal struct NodeWorkItem
{
    internal long OsmNodeId;
    internal GraphId StableGraphId;
    internal int RemainingIncidenceUses;
    internal int ActivePathReferences;
    internal int PendingFinalizers;
    internal NodeAnchorFlags AnchorFlags;
    internal NodeLifecycleFlags LifecycleFlags;
}

internal readonly record struct NodeHandle(
    int ArenaId,
    int SlabIndex,
    int SlotIndex,
    uint Generation);

internal readonly record struct PooledNodeArenaMetrics(
    long TotalSlotRents,
    long SlotReuseCount,
    int LiveSlotCount,
    int PeakLiveSlotCount,
    int TotalSlabsRented,
    long PeakSlabBytes,
    long StaleHandleRejections,
    long QuarantinedSlotCount);

internal sealed class StaleNodeHandleException(string message)
    : InvalidOperationException(message);

internal sealed class NodeWorkItemStillReferencedException(string message)
    : InvalidOperationException(message);

internal sealed class PooledNodeArena : IDisposable
{
    private const byte NeverUsed = 0;
    private const byte Live = 1;
    private const byte Free = 2;
    private const byte Quarantined = 3;
    private static int nextArenaId;

    private readonly int slabCapacity;
    private readonly long memoryBudgetBytes;
    private readonly long logicalSlabBytes;
    private readonly ArrayPool<NodeWorkItem> itemPool;
    private readonly ArrayPool<uint> generationPool;
    private readonly ArrayPool<byte> statePool;
    private readonly ArrayPool<int> freeSlotPool;
    private readonly uint initialGeneration;
    private readonly List<Slab> slabs = [];
    private bool disposed;
    private long totalSlotRents;
    private long slotReuseCount;
    private int liveSlotCount;
    private int peakLiveSlotCount;
    private long peakSlabBytes;
    private long staleHandleRejections;
    private long quarantinedSlotCount;

    internal PooledNodeArena(
        int slabCapacity,
        long memoryBudgetBytes,
        ArrayPool<NodeWorkItem>? itemPool = null,
        ArrayPool<uint>? generationPool = null,
        ArrayPool<byte>? statePool = null,
        ArrayPool<int>? freeSlotPool = null,
        uint initialGeneration = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slabCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfZero(initialGeneration);
        this.slabCapacity = slabCapacity;
        this.memoryBudgetBytes = memoryBudgetBytes;
        this.itemPool = itemPool ?? ArrayPool<NodeWorkItem>.Shared;
        this.generationPool = generationPool ?? ArrayPool<uint>.Shared;
        this.statePool = statePool ?? ArrayPool<byte>.Shared;
        this.freeSlotPool = freeSlotPool ?? ArrayPool<int>.Shared;
        this.initialGeneration = initialGeneration;
        logicalSlabBytes = checked((long)slabCapacity * (
            Unsafe.SizeOf<NodeWorkItem>() +
            sizeof(uint) +
            sizeof(byte) +
            sizeof(int)));
        if (logicalSlabBytes > memoryBudgetBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The node arena memory budget cannot fit one slab.");
        }

        ArenaId = Interlocked.Increment(ref nextArenaId);
    }

    internal int ArenaId { get; }

    internal PooledNodeArenaMetrics Metrics => new(
        totalSlotRents,
        slotReuseCount,
        liveSlotCount,
        peakLiveSlotCount,
        slabs.Count,
        peakSlabBytes,
        staleHandleRejections,
        quarantinedSlotCount);

    internal NodeHandle Rent(NodeWorkItem initialValue)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Slab? selected = null;
        int slabIndex = -1;
        for (int index = 0; index < slabs.Count; index++)
        {
            Slab candidate = slabs[index];
            if (candidate.FreeCount > 0 || candidate.NextUninitialized < slabCapacity)
            {
                selected = candidate;
                slabIndex = index;
                break;
            }
        }

        if (selected is null)
        {
            selected = RentSlab();
            slabIndex = slabs.Count - 1;
        }

        int slotIndex;
        bool reused;
        if (selected.FreeCount > 0)
        {
            slotIndex = selected.FreeSlots[--selected.FreeCount];
            reused = true;
        }
        else
        {
            slotIndex = selected.NextUninitialized++;
            selected.Generations[slotIndex] = initialGeneration;
            reused = false;
        }

        selected.Items[slotIndex] = initialValue;
        selected.States[slotIndex] = Live;
        totalSlotRents++;
        if (reused)
        {
            slotReuseCount++;
        }

        liveSlotCount++;
        peakLiveSlotCount = Math.Max(peakLiveSlotCount, liveSlotCount);
        return new NodeHandle(
            ArenaId,
            slabIndex,
            slotIndex,
            selected.Generations[slotIndex]);
    }

    internal ref NodeWorkItem Resolve(NodeHandle handle)
    {
        Slab slab = ValidateLiveHandle(handle);
        return ref slab.Items[handle.SlotIndex];
    }

    internal void Release(NodeHandle handle)
    {
        Slab slab = ValidateLiveHandle(handle);
        ref NodeWorkItem item = ref slab.Items[handle.SlotIndex];
        if (item.RemainingIncidenceUses != 0 ||
            item.ActivePathReferences != 0 ||
            item.PendingFinalizers != 0 ||
            item.LifecycleFlags != NodeLifecycleFlags.AllDurableStateWritten)
        {
            throw new NodeWorkItemStillReferencedException(
                $"Node {item.OsmNodeId} still has unresolved lifetime state.");
        }

        ReleaseCore(slab, handle.SlotIndex);
    }

    internal void Abandon(NodeHandle handle)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (handle.ArenaId != ArenaId ||
            handle.SlabIndex < 0 ||
            handle.SlabIndex >= slabs.Count ||
            handle.SlotIndex < 0 ||
            handle.SlotIndex >= slabCapacity)
        {
            return;
        }

        Slab slab = slabs[handle.SlabIndex];
        if (slab.States[handle.SlotIndex] != Live ||
            slab.Generations[handle.SlotIndex] != handle.Generation)
        {
            return;
        }

        ReleaseCore(slab, handle.SlotIndex);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        foreach (Slab slab in slabs)
        {
            itemPool.Return(slab.Items, clearArray: true);
            generationPool.Return(slab.Generations, clearArray: true);
            statePool.Return(slab.States, clearArray: true);
            freeSlotPool.Return(slab.FreeSlots, clearArray: true);
        }

        slabs.Clear();
        liveSlotCount = 0;
        disposed = true;
    }

    private Slab RentSlab()
    {
        long nextBytes = checked((slabs.Count + 1L) * logicalSlabBytes);
        if (nextBytes > memoryBudgetBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The node arena memory budget is exhausted.");
        }

        NodeWorkItem[] items = itemPool.Rent(slabCapacity);
        uint[] generations = generationPool.Rent(slabCapacity);
        byte[] states = statePool.Rent(slabCapacity);
        int[] freeSlots = freeSlotPool.Rent(slabCapacity);
        Array.Clear(items, 0, slabCapacity);
        Array.Clear(generations, 0, slabCapacity);
        Array.Clear(states, 0, slabCapacity);
        Array.Clear(freeSlots, 0, slabCapacity);
        var slab = new Slab(items, generations, states, freeSlots);
        slabs.Add(slab);
        peakSlabBytes = Math.Max(peakSlabBytes, nextBytes);
        return slab;
    }

    private Slab ValidateLiveHandle(NodeHandle handle)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (handle.ArenaId != ArenaId ||
            handle.SlabIndex < 0 ||
            handle.SlabIndex >= slabs.Count ||
            handle.SlotIndex < 0 ||
            handle.SlotIndex >= slabCapacity)
        {
            staleHandleRejections++;
            throw new StaleNodeHandleException("The node handle does not belong to this arena.");
        }

        Slab slab = slabs[handle.SlabIndex];
        if (slab.States[handle.SlotIndex] != Live ||
            slab.Generations[handle.SlotIndex] != handle.Generation)
        {
            staleHandleRejections++;
            throw new StaleNodeHandleException(
                "The node handle no longer identifies a live pooled slot.");
        }

        return slab;
    }

    private void ReleaseCore(Slab slab, int slotIndex)
    {
        slab.Items[slotIndex] = default;
        liveSlotCount--;
        if (slab.Generations[slotIndex] == uint.MaxValue)
        {
            slab.States[slotIndex] = Quarantined;
            quarantinedSlotCount++;
            return;
        }

        slab.Generations[slotIndex]++;
        slab.States[slotIndex] = Free;
        slab.FreeSlots[slab.FreeCount++] = slotIndex;
    }

    private sealed class Slab(
        NodeWorkItem[] items,
        uint[] generations,
        byte[] states,
        int[] freeSlots)
    {
        internal NodeWorkItem[] Items { get; } = items;

        internal uint[] Generations { get; } = generations;

        internal byte[] States { get; } = states;

        internal int[] FreeSlots { get; } = freeSlots;

        internal int FreeCount { get; set; }

        internal int NextUninitialized { get; set; }
    }
}
