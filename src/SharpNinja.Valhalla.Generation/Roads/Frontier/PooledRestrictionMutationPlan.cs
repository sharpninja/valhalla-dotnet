using System.Runtime.CompilerServices;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Storage;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal sealed record PooledRestrictionMutationPlanOptions(
    string WorkingDirectory,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    int SegmentSizeBytes = 64 * 1024 * 1024);

internal sealed record PooledRestrictionMutationPlanReceipt(
    long ProjectedRestrictionCount,
    long UniqueRestrictionCount,
    long ProjectedEdgePatchCount,
    long UniqueEdgePatchCount,
    long MissingDestinationCount,
    long RestrictionStoreBytes,
    long EdgePatchStoreBytes,
    long PeakSortMemoryBytes,
    long ScratchHighWaterMarkBytes)
{
    internal long MemoryBudgetBytes { get; init; }

    internal long ScratchDiskBudgetBytes { get; init; }

    internal long PeakAggregateMemoryBytes { get; init; }

    internal long PeakAggregateScratchBytes { get; init; }
}

internal sealed class BoundedRestrictionMutationPlan :
    IRestrictionMutationPlanReader,
    IDisposable
{
    private readonly string operationDirectory;
    private IIntermediateSequenceStore<PlannedRestrictionRecord>? restrictions;
    private IIntermediateSequenceStore<PlannedEdgePatchRecord>? edgePatches;

    internal BoundedRestrictionMutationPlan(
        string operationDirectory,
        IIntermediateSequenceStore<PlannedRestrictionRecord> restrictions,
        IIntermediateSequenceStore<PlannedEdgePatchRecord> edgePatches,
        PooledRestrictionMutationPlanReceipt receipt)
    {
        this.operationDirectory = operationDirectory;
        this.restrictions = restrictions;
        this.edgePatches = edgePatches;
        Receipt = receipt;
    }

    internal PooledRestrictionMutationPlanReceipt Receipt { get; }

    internal long RestrictionCount => restrictions?.State.RecordCount ?? 0;

    internal long EdgePatchCount => edgePatches?.State.RecordCount ?? 0;

    long IRestrictionMutationPlanReader.RestrictionCount => RestrictionCount;

    long IRestrictionMutationPlanReader.EdgePatchCount => EdgePatchCount;


    internal PlannedRestrictionRecord ReadRestriction(long index)
        => restrictions?.Read(index)
           ?? throw new ObjectDisposedException(nameof(BoundedRestrictionMutationPlan));

    internal PlannedEdgePatchRecord ReadEdgePatch(long index)
        => edgePatches?.Read(index)
           ?? throw new ObjectDisposedException(nameof(BoundedRestrictionMutationPlan));

    RestrictionMutationPlanPayload
        IRestrictionMutationPlanReader.ReadRestriction(long index)
    {
        PlannedRestrictionRecord record = ReadRestriction(index);
        return new RestrictionMutationPlanPayload(
            record.TileValue,
            record.Direction == PlannedRestrictionDirection.Forward
                ? RestrictionMutationDirection.Forward
                : RestrictionMutationDirection.Reverse,
            record.CanonicalOrdinal,
            record.PayloadLength);
    }

    void IRestrictionMutationPlanReader.CopyRestrictionPayload(
        long index,
        Span<byte> destination)
    {
        PlannedRestrictionRecord record = ReadRestriction(index);
        if (destination.Length < record.PayloadLength)
        {
            throw new ArgumentException(
                "The restriction payload destination is too small.",
                nameof(destination));
        }

        record.PayloadSpan().CopyTo(destination);
    }

    RestrictionMutationPlanEdgePatch
        IRestrictionMutationPlanReader.ReadEdgePatch(long index)
    {
        PlannedEdgePatchRecord record = ReadEdgePatch(index);
        return new RestrictionMutationPlanEdgePatch(
            record.TileValue,
            record.EdgeIndex,
            record.StartMaskOr,
            record.EndMaskOr,
            record.SetComplexRestriction != 0,
            record.CanonicalOrdinal);
    }

    internal long CountRestrictions(
        RestrictionMutationDirection direction)
    {
        long count = 0;
        for (long index = 0; index < RestrictionCount; index++)
        {
            PlannedRestrictionRecord record = ReadRestriction(index);
            bool matches =
                direction == RestrictionMutationDirection.Forward
                    ? record.Direction == PlannedRestrictionDirection.Forward
                    : record.Direction == PlannedRestrictionDirection.Reverse;
            if (matches)
            {
                count++;
            }
        }

        return count;
    }

    internal long CountCrossTileRestrictions(
        PlannedRestrictionDirection direction)
        => CountCrossTileRestrictions(
            direction == PlannedRestrictionDirection.Forward
                ? RestrictionMutationDirection.Forward
                : RestrictionMutationDirection.Reverse);

    internal long CountCrossTileRestrictions(
        RestrictionMutationDirection direction)
    {
        long count = 0;
        for (long index = 0; index < RestrictionCount; index++)
        {
            PlannedRestrictionRecord record = ReadRestriction(index);
            bool matches =
                direction == RestrictionMutationDirection.Forward
                    ? record.Direction == PlannedRestrictionDirection.Forward
                    : record.Direction == PlannedRestrictionDirection.Reverse;
            if (matches && record.CrossTile != 0)
            {
                count++;
            }
        }

        return count;
    }

    internal long CountCrossTileEdgePatches()
    {
        long count = 0;
        for (long index = 0; index < EdgePatchCount; index++)
        {
            if (ReadEdgePatch(index).CrossTile != 0)
            {
                count++;
            }
        }

        return count;
    }



    public void Dispose()
    {
        IIntermediateSequenceStore<PlannedRestrictionRecord>? restrictionStore =
            Interlocked.Exchange(ref restrictions, null);
        IIntermediateSequenceStore<PlannedEdgePatchRecord>? patchStore =
            Interlocked.Exchange(ref edgePatches, null);
        restrictionStore?.Dispose();
        patchStore?.Dispose();
        if (restrictionStore is not null &&
            Directory.Exists(operationDirectory))
        {
            Directory.Delete(operationDirectory, recursive: true);
        }
    }
}

internal sealed class PooledRestrictionMutationPlanSink :
    IRestrictionMutationPlanSink,
    IDisposable

{
    private readonly PooledRestrictionMutationPlanOptions options;
    private readonly long restrictionInputMemory;
    private readonly long patchInputMemory;
    private readonly long sortMemory;
    private readonly long restrictionOutputMemory;
    private readonly long patchOutputMemory;
    private readonly long restrictionInputScratch;
    private readonly long patchInputScratch;
    private readonly long sortScratch;
    private readonly long restrictionOutputScratch;
    private readonly long patchOutputScratch;

    private readonly string operationDirectory;
    private IntermediateSequenceStore<PlannedRestrictionRecord>? restrictions;
    private IntermediateSequenceStore<PlannedEdgePatchRecord>? edgePatches;
    private long missingDestinationCount;
    private bool completed;
    private bool ownershipTransferred;
    private bool disposed;

    internal PooledRestrictionMutationPlanSink(
        PooledRestrictionMutationPlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        this.options = options;

        restrictionInputMemory = options.MemoryBudgetBytes / 8;
        patchInputMemory = options.MemoryBudgetBytes / 8;
        sortMemory = options.MemoryBudgetBytes / 4;
        restrictionOutputMemory = options.MemoryBudgetBytes / 8;
        patchOutputMemory = options.MemoryBudgetBytes / 8;

        restrictionInputScratch = options.ScratchDiskBudgetBytes / 8;
        patchInputScratch = options.ScratchDiskBudgetBytes / 8;
        sortScratch = options.ScratchDiskBudgetBytes / 4;
        restrictionOutputScratch = options.ScratchDiskBudgetBytes / 8;
        patchOutputScratch = options.ScratchDiskBudgetBytes / 8;

        EnsureAggregatePartitions();

        operationDirectory = Path.Combine(
            Path.GetFullPath(options.WorkingDirectory),
            $"restriction-plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(operationDirectory);

        restrictions = new IntermediateSequenceStore<PlannedRestrictionRecord>(
            CreateStoreOptions(
                "projected-restrictions",
                restrictionInputMemory,
                restrictionInputScratch));
        edgePatches = new IntermediateSequenceStore<PlannedEdgePatchRecord>(
            CreateStoreOptions(
                "projected-edge-patches",
                patchInputMemory,
                patchInputScratch));
    }

    internal void EmitRestriction(
        PlannedRestrictionDirection direction,
        GraphId hostTile,
        GraphId from,
        GraphId to,
        ReadOnlySpan<GraphId> vias,
        RestrictionType type,
        ushort modes,
        byte probability,
        ulong timeDomain,
        ulong canonicalOrdinal)
        => EmitRestriction(
            direction == PlannedRestrictionDirection.Forward
                ? RestrictionMutationDirection.Forward
                : RestrictionMutationDirection.Reverse,
            hostTile,
            from,
            to,
            vias,
            type,
            modes,
            probability,
            timeDomain,
            crossTile: false,
            canonicalOrdinal);
    internal void EmitRestriction(
        PlannedRestrictionDirection direction,
        GraphId hostTile,
        GraphId from,
        GraphId to,
        ReadOnlySpan<GraphId> vias,
        RestrictionType type,
        ushort modes,
        byte probability,
        ulong timeDomain,
        bool crossTile,
        ulong canonicalOrdinal)
        => EmitRestriction(
            direction == PlannedRestrictionDirection.Forward
                ? RestrictionMutationDirection.Forward
                : RestrictionMutationDirection.Reverse,
            hostTile,
            from,
            to,
            vias,
            type,
            modes,
            probability,
            timeDomain,
            crossTile,
            canonicalOrdinal);



    public void EmitRestriction(
        RestrictionMutationDirection direction,
        GraphId hostTile,
        GraphId from,
        GraphId to,
        ReadOnlySpan<GraphId> vias,
        RestrictionType type,
        uint modes,
        byte probability,
        ulong timeDomain,
        bool crossTile,
        ulong canonicalOrdinal)
    {
        EnsureWritable();
        var record = new PlannedRestrictionRecord
        {
            TileValue = hostTile.TileBase().Value,
            CanonicalOrdinal = canonicalOrdinal,
            Direction = direction == RestrictionMutationDirection.Forward
                ? PlannedRestrictionDirection.Forward
                : PlannedRestrictionDirection.Reverse,
            CrossTile = crossTile ? (byte)1 : (byte)0,
        };
        record.SetPayload(
            from,
            to,
            vias,
            type,
            checked((ushort)modes),
            probability,
            timeDomain);
        restrictions!.Append(record);
    }
    internal void EmitEdgePatch(
        GraphId tile,
        uint edgeIndex,
        uint startMaskOr,
        uint endMaskOr,
        bool setComplexRestriction,
        ulong canonicalOrdinal)
        => EmitEdgePatch(
            tile,
            edgeIndex,
            startMaskOr,
            endMaskOr,
            setComplexRestriction,
            crossTile: false,
            canonicalOrdinal);



    public void EmitEdgePatch(
        GraphId tile,
        uint edgeIndex,
        uint startMaskOr,
        uint endMaskOr,
        bool setComplexRestriction,
        bool crossTile,
        ulong canonicalOrdinal)
    {
        EnsureWritable();
        edgePatches!.Append(new PlannedEdgePatchRecord(
            tile.TileBase().Value,
            edgeIndex,
            startMaskOr,
            endMaskOr,
            setComplexRestriction ? (byte)1 : (byte)0,
            crossTile ? (byte)1 : (byte)0,
            canonicalOrdinal));
    }

    public void RecordMissingDestination(
        GraphId tile,
        ulong canonicalOrdinal)
    {
        _ = tile;
        _ = canonicalOrdinal;
        EnsureWritable();
        missingDestinationCount = checked(missingDestinationCount + 1);
    }

    internal async ValueTask<BoundedRestrictionMutationPlan> CompleteAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        completed = true;
        IntermediateSequenceStore<PlannedRestrictionRecord> restrictionInput =
            restrictions!;
        IntermediateSequenceStore<PlannedEdgePatchRecord> patchInput =
            edgePatches!;
        restrictions = null;
        edgePatches = null;

        ExternalSequenceSortResult<PlannedRestrictionRecord>? sortedRestrictions =
            null;
        ExternalSequenceSortResult<PlannedEdgePatchRecord>? sortedPatches = null;
        ExternalSequenceSortResult<PlannedRestrictionRecord>?
            applicationRestrictions = null;
        IntermediateSequenceStore<PlannedRestrictionRecord>? uniqueRestrictions =
            null;
        IntermediateSequenceStore<PlannedEdgePatchRecord>? uniquePatches = null;
        try
        {
            await restrictionInput.CompleteAsync(cancellationToken)
                .ConfigureAwait(false);
            await patchInput.CompleteAsync(cancellationToken)
                .ConfigureAwait(false);
            long projectedRestrictionCount =
                restrictionInput.State.RecordCount;
            long projectedPatchCount =
                patchInput.State.RecordCount;
            long patchInputPeakMemory = patchInput.State.PeakMemoryBytes;
            long patchInputPeakScratch =
                patchInput.State.ScratchHighWaterMarkBytes;

            long peakAggregateMemory = checked(
                restrictionInput.State.PeakMemoryBytes +
                patchInputPeakMemory);
            long peakAggregateScratch = checked(
                restrictionInput.State.ScratchHighWaterMarkBytes +
                patchInputPeakScratch);
            EnsureWithinAggregateBudget(
                peakAggregateMemory,
                peakAggregateScratch);

            sortedRestrictions =
                await ExternalSequenceSorter.SortAsync(
                        restrictionInput,
                        CreateStoreOptions(
                            "sorted-restrictions",
                            restrictionOutputMemory,
                            restrictionOutputScratch),
                        new ExternalSequenceSortOptions(
                            operationDirectory,
                            "restriction-sort",
                            sortMemory,
                            sortScratch),
                        CompareRestrictionsForDedupe,
                        cancellationToken)
                    .ConfigureAwait(false);
            restrictionInput.Dispose();

            uniqueRestrictions =
                new IntermediateSequenceStore<PlannedRestrictionRecord>(
                    CreateStoreOptions(
                        "unique-restrictions",
                        restrictionOutputMemory,
                        restrictionOutputScratch));
            DeduplicateRestrictions(
                sortedRestrictions.Output,
                uniqueRestrictions,
                cancellationToken);
            await uniqueRestrictions.CompleteAsync(cancellationToken)
                .ConfigureAwait(false);
            sortedRestrictions.Dispose();
            sortedRestrictions = null;

            applicationRestrictions =
                await ExternalSequenceSorter.SortAsync(
                        uniqueRestrictions,
                        CreateStoreOptions(
                            "application-restrictions",
                            restrictionOutputMemory,
                            restrictionOutputScratch),
                        new ExternalSequenceSortOptions(
                            operationDirectory,
                            "application-restriction-sort",
                            sortMemory,
                            sortScratch),
                        CompareRestrictionsForApplication,
                        cancellationToken)
                    .ConfigureAwait(false);
            uniqueRestrictions.Dispose();
            uniqueRestrictions = null;

            sortedPatches =
                await ExternalSequenceSorter.SortAsync(
                        patchInput,
                        CreateStoreOptions(
                            "sorted-edge-patches",
                            patchOutputMemory,
                            patchOutputScratch),
                        new ExternalSequenceSortOptions(
                            operationDirectory,
                            "edge-patch-sort",
                            sortMemory,
                            sortScratch),
                        CompareEdgePatches,
                        cancellationToken)
                    .ConfigureAwait(false);
            patchInput.Dispose();

            uniquePatches =
                new IntermediateSequenceStore<PlannedEdgePatchRecord>(
                    CreateStoreOptions(
                        "unique-edge-patches",
                        patchOutputMemory,
                        patchOutputScratch));
            DeduplicateEdgePatches(
                sortedPatches.Output,
                uniquePatches,
                cancellationToken);
            await uniquePatches.CompleteAsync(cancellationToken)
                .ConfigureAwait(false);
            sortedPatches.Dispose();
            sortedPatches = null;


            long restrictionBytes = checked(
                applicationRestrictions.Output.State.RecordCount *
                Unsafe.SizeOf<PlannedRestrictionRecord>());
            long patchBytes = checked(
                uniquePatches.State.RecordCount *
                Unsafe.SizeOf<PlannedEdgePatchRecord>());
            peakAggregateMemory = Math.Max(
                peakAggregateMemory,
                checked(
                    sortMemory +
                    restrictionOutputMemory +
                    patchInputPeakMemory));
            peakAggregateMemory = Math.Max(
                peakAggregateMemory,
                checked(
                    restrictionOutputMemory +
                    patchOutputMemory +
                    sortMemory));
            peakAggregateScratch = Math.Max(
                peakAggregateScratch,
                checked(
                    sortScratch +
                    restrictionOutputScratch +
                    patchInputPeakScratch));
            peakAggregateScratch = Math.Max(
                peakAggregateScratch,
                checked(
                    restrictionOutputScratch +
                    patchOutputScratch +
                    sortScratch));
            EnsureWithinAggregateBudget(
                peakAggregateMemory,
                peakAggregateScratch);

            var receipt = new PooledRestrictionMutationPlanReceipt(
                projectedRestrictionCount,
                applicationRestrictions.Output.State.RecordCount,
                projectedPatchCount,
                uniquePatches.State.RecordCount,
                missingDestinationCount,
                restrictionBytes,
                patchBytes,
                sortMemory,
                peakAggregateScratch)
            {
                MemoryBudgetBytes = options.MemoryBudgetBytes,
                ScratchDiskBudgetBytes = options.ScratchDiskBudgetBytes,
                PeakAggregateMemoryBytes = peakAggregateMemory,
                PeakAggregateScratchBytes = peakAggregateScratch,
            };
            var result = new BoundedRestrictionMutationPlan(
                operationDirectory,
                applicationRestrictions.Output,
                uniquePatches,
                receipt);
            applicationRestrictions = null;
            uniquePatches = null;
            ownershipTransferred = true;
            return result;
        }
        catch
        {
            restrictionInput.Dispose();
            patchInput.Dispose();
            sortedRestrictions?.Dispose();
            sortedPatches?.Dispose();
            applicationRestrictions?.Dispose();
            uniqueRestrictions?.Dispose();
            uniquePatches?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        restrictions?.Dispose();
        edgePatches?.Dispose();
        restrictions = null;
        edgePatches = null;
        if (!ownershipTransferred &&
            Directory.Exists(operationDirectory))
        {
            Directory.Delete(operationDirectory, recursive: true);
        }

        disposed = true;
    }

    private IntermediateSequenceStoreOptions CreateStoreOptions(
        string name,
        long memoryBytes,
        long scratchBytes)
        => new(
            operationDirectory,
            name,
            IntermediateStorageMode.MemoryMapped,
            memoryBytes,
            scratchBytes,
            options.SegmentSizeBytes);

    private static void DeduplicateRestrictions(
        IIntermediateSequenceStore<PlannedRestrictionRecord> input,
        IIntermediateSequenceStore<PlannedRestrictionRecord> output,
        CancellationToken cancellationToken)
    {
        PlannedRestrictionRecord currentGroup = default;
        bool hasGroup = false;
        for (long index = 0; index < input.State.RecordCount; index++)
        {
            if ((index & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            PlannedRestrictionRecord current = input.Read(index);
            if (!hasGroup)
            {
                currentGroup = current;
                hasGroup = true;
                continue;
            }

            if (RestrictionPayloadEquals(currentGroup, current))
            {
                currentGroup.CrossTile |= current.CrossTile;
                currentGroup.CanonicalOrdinal = Math.Min(
                    currentGroup.CanonicalOrdinal,
                    current.CanonicalOrdinal);
                continue;
            }

            output.Append(currentGroup);
            currentGroup = current;
        }

        if (hasGroup)
        {
            output.Append(currentGroup);
        }
    }
    private static void DeduplicateEdgePatches(
        IIntermediateSequenceStore<PlannedEdgePatchRecord> input,
        IIntermediateSequenceStore<PlannedEdgePatchRecord> output,
        CancellationToken cancellationToken)
    {
        long index = 0;
        while (index < input.State.RecordCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlannedEdgePatchRecord first = input.Read(index++);
            uint start = first.StartMaskOr;
            uint end = first.EndMaskOr;
            byte complex = first.SetComplexRestriction;
            byte crossTile = first.CrossTile;
            ulong ordinal = first.CanonicalOrdinal;
            while (index < input.State.RecordCount)
            {
                PlannedEdgePatchRecord next = input.Read(index);
                if (next.TileValue != first.TileValue ||
                    next.EdgeIndex != first.EdgeIndex)
                {
                    break;
                }

                start |= next.StartMaskOr;
                end |= next.EndMaskOr;
                crossTile |= next.CrossTile;
                complex |= next.SetComplexRestriction;
                ordinal = Math.Min(ordinal, next.CanonicalOrdinal);
                index++;
            }

            output.Append(new PlannedEdgePatchRecord(
                first.TileValue,
                first.EdgeIndex,
                start,
                end,
                complex,
                crossTile,
                ordinal));
        }
    }

    private static int CompareRestrictionsForDedupe(
        PlannedRestrictionRecord left,
        PlannedRestrictionRecord right)
    {
        int comparison = left.TileValue.CompareTo(right.TileValue);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Direction.CompareTo(right.Direction);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.PayloadSpan().SequenceCompareTo(right.PayloadSpan());
        return comparison != 0
            ? comparison
            : left.CanonicalOrdinal.CompareTo(right.CanonicalOrdinal);
    }

    private static int CompareRestrictionsForApplication(
        PlannedRestrictionRecord left,
        PlannedRestrictionRecord right)
    {
        int comparison = left.TileValue.CompareTo(right.TileValue);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Direction.CompareTo(right.Direction);
        return comparison != 0
            ? comparison
            : left.CanonicalOrdinal.CompareTo(right.CanonicalOrdinal);
    }

    private static bool RestrictionPayloadEquals(
        PlannedRestrictionRecord left,
        PlannedRestrictionRecord right)
        => left.TileValue == right.TileValue &&
           left.Direction == right.Direction &&
           left.PayloadSpan().SequenceEqual(right.PayloadSpan());

    private static int CompareEdgePatches(
        PlannedEdgePatchRecord left,
        PlannedEdgePatchRecord right)
    {
        int comparison = left.TileValue.CompareTo(right.TileValue);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.EdgeIndex.CompareTo(right.EdgeIndex);
        return comparison != 0
            ? comparison
            : left.CanonicalOrdinal.CompareTo(right.CanonicalOrdinal);
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed)
        {
            throw new InvalidOperationException(
                "A completed mutation plan is immutable.");
        }
    }

    private static void ValidateOptions(
        PooledRestrictionMutationPlanOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            throw new ArgumentException(
                "A working directory is required.",
                nameof(options));
        }

        long minimumMemory = checked(
            8L * Math.Max(
                Unsafe.SizeOf<ExternalSequenceStableRecord<PlannedRestrictionRecord>>(),
                Unsafe.SizeOf<ExternalSequenceStableRecord<PlannedEdgePatchRecord>>()));
        if (options.MemoryBudgetBytes < minimumMemory)
        {
            throw new ValhallaGenerationResourceLimitException(
                $"The plan requires at least {minimumMemory} bytes of memory.");
        }

        long minimumScratch = checked(8L * options.SegmentSizeBytes);
        if (options.SegmentSizeBytes <= 0 ||
            options.ScratchDiskBudgetBytes < minimumScratch)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The plan scratch budget cannot satisfy eight bounded partitions.");
        }
    }

    private void EnsureAggregatePartitions()
    {
        long stableRestrictionBytes =
            Unsafe.SizeOf<ExternalSequenceStableRecord<PlannedRestrictionRecord>>();
        long stablePatchBytes =
            Unsafe.SizeOf<ExternalSequenceStableRecord<PlannedEdgePatchRecord>>();
        long minimumStoreMemory = Math.Max(
            Unsafe.SizeOf<PlannedRestrictionRecord>(),
            Unsafe.SizeOf<PlannedEdgePatchRecord>());
        long minimumSortMemory = Math.Max(
            stableRestrictionBytes,
            stablePatchBytes);

        if (restrictionInputMemory < minimumStoreMemory ||
            patchInputMemory < minimumStoreMemory ||
            restrictionOutputMemory < minimumStoreMemory ||
            patchOutputMemory < minimumStoreMemory ||
            sortMemory < minimumSortMemory)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The mutation-plan memory budget cannot satisfy the disjoint " +
                "input, sort, output, and dedupe partitions.");
        }

        if (restrictionInputScratch < options.SegmentSizeBytes ||
            patchInputScratch < options.SegmentSizeBytes ||
            restrictionOutputScratch < options.SegmentSizeBytes ||
            patchOutputScratch < options.SegmentSizeBytes ||
            sortScratch < options.SegmentSizeBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The mutation-plan scratch budget cannot satisfy the disjoint " +
                "input, sort, output, and dedupe partitions.");
        }

        long assignedMemory = checked(
            restrictionInputMemory +
            patchInputMemory +
            sortMemory +
            restrictionOutputMemory +
            patchOutputMemory);
        long assignedScratch = checked(
            restrictionInputScratch +
            patchInputScratch +
            sortScratch +
            restrictionOutputScratch +
            patchOutputScratch);
        EnsureWithinAggregateBudget(assignedMemory, assignedScratch);
    }

    private void EnsureWithinAggregateBudget(
        long memoryBytes,
        long scratchBytes)
    {
        if (memoryBytes > options.MemoryBudgetBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                $"Mutation-plan aggregate memory {memoryBytes} bytes exceeds " +
                $"the configured {options.MemoryBudgetBytes}-byte budget.");
        }

        if (scratchBytes > options.ScratchDiskBudgetBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                $"Mutation-plan aggregate scratch {scratchBytes} bytes exceeds " +
                $"the configured {options.ScratchDiskBudgetBytes}-byte budget.");
        }
    }



    [InlineArray(1)]
    private struct ExternalSequenceStableRecord<T>
        where T : unmanaged
    {
        private T element0;
    }
}
