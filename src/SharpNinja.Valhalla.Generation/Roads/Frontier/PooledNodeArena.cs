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
    private static readonly int ObjectHeaderBytes = 2 * IntPtr.Size;
    private static readonly int ArrayHeaderBytes =
        AlignToPointer(ObjectHeaderBytes + sizeof(int));
    private static readonly int ListObjectBytes =
        AlignToPointer(ObjectHeaderBytes + IntPtr.Size + (2 * sizeof(int)));
    private static readonly int SlabObjectBytes =
        AlignToPointer(ObjectHeaderBytes + (4 * IntPtr.Size) + (2 * sizeof(int)));
    private static readonly int OwnedPoolObjectBytes = ObjectHeaderBytes;
    private static int nextArenaId;

    private readonly int slabCapacity;
    private readonly long memoryBudgetBytes;
    private readonly ArrayPool<NodeWorkItem> itemPool;
    private readonly ArrayPool<uint> generationPool;
    private readonly ArrayPool<byte> statePool;
    private readonly ArrayPool<int> freeSlotPool;
    private readonly uint initialGeneration;
    private readonly List<Slab> slabs;
    private bool disposed;
    private long totalSlotRents;
    private long slotReuseCount;
    private int liveSlotCount;
    private int peakLiveSlotCount;
    private long retainedSlabBytes;
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
        this.initialGeneration = initialGeneration;

        long minimumLogicalSlabBytes = checked(
            SlabObjectBytes +
            ArrayBytes<NodeWorkItem>(slabCapacity) +
            ArrayBytes<uint>(slabCapacity) +
            ArrayBytes<byte>(slabCapacity) +
            ArrayBytes<int>(slabCapacity));
        long ownedPoolMetadataBytes = checked(4L * OwnedPoolObjectBytes);
        long slabTableOverheadBytes = checked(
            ListObjectBytes +
            ArrayHeaderBytes +
            ownedPoolMetadataBytes);
        long minimumPerSlabBytes = checked(minimumLogicalSlabBytes + IntPtr.Size);
        long availableForSlabs = memoryBudgetBytes - slabTableOverheadBytes;
        if (availableForSlabs < minimumPerSlabBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The node arena memory budget cannot fit one slab.");
        }

        int maximumSlabCount = checked((int)Math.Min(
            Array.MaxLength,
            availableForSlabs / minimumPerSlabBytes));
        slabs = new List<Slab>(maximumSlabCount);
        retainedSlabBytes = checked(
            ListObjectBytes +
            ArrayBytes<Slab>(maximumSlabCount) +
            ownedPoolMetadataBytes);

        this.itemPool = itemPool ?? new NonRetainingArrayPool<NodeWorkItem>();
        this.generationPool = generationPool ?? new NonRetainingArrayPool<uint>();
        this.statePool = statePool ?? new NonRetainingArrayPool<byte>();
        this.freeSlotPool = freeSlotPool ?? new NonRetainingArrayPool<int>();

        ArenaId = Interlocked.Increment(ref nextArenaId);
    }

    internal int ArenaId { get; }

    internal bool UsesStageOwnedPools =>
        !ReferenceEquals(itemPool, ArrayPool<NodeWorkItem>.Shared) &&
        !ReferenceEquals(generationPool, ArrayPool<uint>.Shared) &&
        !ReferenceEquals(statePool, ArrayPool<byte>.Shared) &&
        !ReferenceEquals(freeSlotPool, ArrayPool<int>.Shared);

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
        NodeWorkItem[]? items = null;
        uint[]? generations = null;
        byte[]? states = null;
        int[]? freeSlots = null;
        try
        {
            items = itemPool.Rent(slabCapacity);
            generations = generationPool.Rent(slabCapacity);
            states = statePool.Rent(slabCapacity);
            freeSlots = freeSlotPool.Rent(slabCapacity);

            long slabBytes = checked(
                SlabObjectBytes +
                ArrayBytes<NodeWorkItem>(items.Length) +
                ArrayBytes<uint>(generations.Length) +
                ArrayBytes<byte>(states.Length) +
                ArrayBytes<int>(freeSlots.Length));
            long nextRetainedBytes = checked(
                retainedSlabBytes + slabBytes);
            if (slabs.Count == slabs.Capacity ||
                nextRetainedBytes > memoryBudgetBytes)
            {
                throw new ValhallaGenerationResourceLimitException(
                    "The node arena memory budget is exhausted.");
            }

            long transientBytes = nextRetainedBytes;

            Array.Clear(items, 0, slabCapacity);
            Array.Clear(generations, 0, slabCapacity);
            Array.Clear(states, 0, slabCapacity);
            Array.Clear(freeSlots, 0, slabCapacity);
            var slab = new Slab(items, generations, states, freeSlots);
            slabs.Add(slab);
            retainedSlabBytes = nextRetainedBytes;
            peakSlabBytes = Math.Max(peakSlabBytes, transientBytes);
            return slab;
        }
        catch
        {
            if (items is not null)
            {
                itemPool.Return(items, clearArray: true);
            }

            if (generations is not null)
            {
                generationPool.Return(generations, clearArray: true);
            }

            if (states is not null)
            {
                statePool.Return(states, clearArray: true);
            }

            if (freeSlots is not null)
            {
                freeSlotPool.Return(freeSlots, clearArray: true);
            }

            throw;
        }
    }

    private static long ArrayBytes<T>(int length) =>
        AlignToPointer(checked(
            ArrayHeaderBytes + ((long)Unsafe.SizeOf<T>() * length)));

    private static long AlignToPointer(long value)
    {
        long alignment = IntPtr.Size;
        return checked((value + alignment - 1) / alignment * alignment);
    }

    private static int AlignToPointer(int value)
    {
        int alignment = IntPtr.Size;
        return checked((value + alignment - 1) / alignment * alignment);
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

    private sealed class NonRetainingArrayPool<T> : ArrayPool<T>
    {
        public override T[] Rent(int minimumLength) =>
            GC.AllocateUninitializedArray<T>(minimumLength);

        public override void Return(T[] array, bool clearArray = false)
        {
            if (clearArray &&
                RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                Array.Clear(array);
            }
        }
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
