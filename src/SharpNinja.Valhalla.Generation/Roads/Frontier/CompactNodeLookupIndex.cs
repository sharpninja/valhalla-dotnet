using System.Runtime.CompilerServices;

using SharpNinja.Valhalla.Generation.Storage;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal readonly record struct CompactNodeLookupRecord(
    GenerationNodeRecord Node,
    long CanonicalOrdinal);

internal sealed record CompactNodeLookupIndexOptions(
    string WorkingDirectory,
    IntermediateStorageMode StorageMode,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    int SegmentSizeBytes = 64 * 1024 * 1024);

internal sealed class CompactNodeLookupIndex : IDisposable
{
    private const int BudgetPartitionCount = 4;
    private readonly IntermediateSequenceStore<CompactNodeLookupRecord> input;
    private readonly ExternalSequenceSortResult<CompactNodeLookupRecord> sorted;
    private readonly IntermediateSequenceStore<CompactNodeLookupRecord> unique;
    private readonly IntermediateSequenceManifest manifest;
    private bool disposed;

    private CompactNodeLookupIndex(
        IntermediateSequenceStore<CompactNodeLookupRecord> input,
        ExternalSequenceSortResult<CompactNodeLookupRecord> sorted,
        IntermediateSequenceStore<CompactNodeLookupRecord> unique,
        IntermediateSequenceManifest manifest,
        long duplicateNodeCount)
    {
        this.input = input;
        this.sorted = sorted;
        this.unique = unique;
        this.manifest = manifest;
        DuplicateNodeCount = duplicateNodeCount;
        PeakMemoryBytes = checked(
            input.State.PeakMemoryBytes +
            sorted.Receipt.PeakMemoryBytes +
            sorted.Output.State.PeakMemoryBytes +
            unique.State.PeakMemoryBytes);
        ScratchHighWaterMarkBytes = checked(
            input.State.ScratchHighWaterMarkBytes +
            sorted.Receipt.ScratchHighWaterMarkBytes +
            sorted.Output.State.ScratchHighWaterMarkBytes +
            unique.State.ScratchHighWaterMarkBytes);
    }

    internal long UniqueNodeCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return unique.State.RecordCount;
        }
    }

    internal long DuplicateNodeCount { get; }

    internal long PeakMemoryBytes { get; }

    internal long ScratchHighWaterMarkBytes { get; }

    internal IntermediateSequenceManifest Manifest
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return manifest;
        }
    }

    internal GenerationNodeRecord ReadNode(long ordinal)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return unique.Read(ordinal).Node;
    }

    internal bool TryGetNode(long osmNodeId, out GenerationNodeRecord node)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long low = 0;
        long high = unique.State.RecordCount;
        while (low < high)
        {
            long middle = low + ((high - low) / 2);
            CompactNodeLookupRecord candidate = unique.Read(middle);
            if (candidate.Node.OsmNodeId < osmNodeId)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (low < unique.State.RecordCount)
        {
            CompactNodeLookupRecord candidate = unique.Read(low);
            if (candidate.Node.OsmNodeId == osmNodeId)
            {
                node = candidate.Node;
                return true;
            }
        }

        node = default;
        return false;
    }

    internal static async ValueTask<CompactNodeLookupIndex> BuildAsync(
        CompactOsmSemanticStore semanticStore,
        CompactNodeLookupIndexOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(semanticStore);
        ValidateOptions(options);

        string root = Path.GetFullPath(options.WorkingDirectory);
        Directory.CreateDirectory(root);
        long memoryPartition = options.MemoryBudgetBytes / BudgetPartitionCount;
        long scratchPartition = options.ScratchDiskBudgetBytes / BudgetPartitionCount;

        IntermediateSequenceStore<CompactNodeLookupRecord>? input = null;
        ExternalSequenceSortResult<CompactNodeLookupRecord>? sorted = null;
        IntermediateSequenceStore<CompactNodeLookupRecord>? unique = null;
        try
        {
            input = CreateStore(
                root,
                "node-lookup-input",
                options,
                memoryPartition,
                scratchPartition);
            for (long ordinal = 0; ordinal < semanticStore.NodeCount; ordinal++)
            {
                if ((ordinal & 0x3FFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                input.Append(new CompactNodeLookupRecord(
                    semanticStore.ReadNode(ordinal),
                    ordinal));
            }

            await input.CompleteAsync(cancellationToken).ConfigureAwait(false);
            sorted = await ExternalSequenceSorter.SortAsync(
                    input,
                    StoreOptions(
                        root,
                        "node-lookup-sorted",
                        options,
                        memoryPartition,
                        scratchPartition),
                    new ExternalSequenceSortOptions(
                        root,
                        "node-lookup-sort",
                        memoryPartition,
                        scratchPartition),
                    Compare,
                    cancellationToken)
                .ConfigureAwait(false);

            unique = CreateStore(
                root,
                "node-lookup-unique",
                options,
                memoryPartition,
                scratchPartition);
            long duplicateCount = Deduplicate(
                sorted.Output,
                unique,
                cancellationToken);
            IntermediateSequenceManifest uniqueManifest =
                await unique.CompleteAsync(cancellationToken).ConfigureAwait(false);

            var result = new CompactNodeLookupIndex(
                input,
                sorted,
                unique,
                uniqueManifest,
                duplicateCount);
            input = null;
            sorted = null;
            unique = null;
            return result;
        }
        catch
        {
            unique?.Dispose();
            sorted?.Dispose();
            input?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        unique.Dispose();
        sorted.Dispose();
        input.Dispose();
        disposed = true;
    }

    private static long Deduplicate(
        IIntermediateSequenceStore<CompactNodeLookupRecord> sorted,
        IntermediateSequenceStore<CompactNodeLookupRecord> unique,
        CancellationToken cancellationToken)
    {
        long duplicateCount = 0;
        long ordinal = 0;
        while (ordinal < sorted.State.RecordCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompactNodeLookupRecord selected = sorted.Read(ordinal);
            long end = ordinal + 1;
            while (end < sorted.State.RecordCount)
            {
                CompactNodeLookupRecord duplicate = sorted.Read(end);
                if (duplicate.Node.OsmNodeId != selected.Node.OsmNodeId)
                {
                    break;
                }

                ValidateDuplicate(selected.Node, duplicate.Node);
                duplicateCount = checked(duplicateCount + 1);
                end++;
            }

            unique.Append(selected with { CanonicalOrdinal = 0 });
            ordinal = end;
        }

        return duplicateCount;
    }

    private static void ValidateDuplicate(
        in GenerationNodeRecord selected,
        in GenerationNodeRecord duplicate)
    {
        if (selected.LatitudeE7 != duplicate.LatitudeE7 ||
            selected.LongitudeE7 != duplicate.LongitudeE7 ||
            selected.Flags != duplicate.Flags)
        {
            throw new InvalidDataException(
                $"OSM node {selected.OsmNodeId} has conflicting canonical records.");
        }
    }

    private static int Compare(
        CompactNodeLookupRecord left,
        CompactNodeLookupRecord right)
    {
        int comparison = left.Node.OsmNodeId.CompareTo(right.Node.OsmNodeId);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Node.LatitudeE7.CompareTo(right.Node.LatitudeE7);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Node.LongitudeE7.CompareTo(right.Node.LongitudeE7);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Node.Flags.CompareTo(right.Node.Flags);
        return comparison != 0
            ? comparison
            : left.CanonicalOrdinal.CompareTo(right.CanonicalOrdinal);
    }

    private static IntermediateSequenceStore<CompactNodeLookupRecord> CreateStore(
        string root,
        string name,
        CompactNodeLookupIndexOptions options,
        long memoryBudgetBytes,
        long scratchDiskBudgetBytes) =>
        new(StoreOptions(
            root,
            name,
            options,
            memoryBudgetBytes,
            scratchDiskBudgetBytes));

    private static IntermediateSequenceStoreOptions StoreOptions(
        string root,
        string name,
        CompactNodeLookupIndexOptions options,
        long memoryBudgetBytes,
        long scratchDiskBudgetBytes) =>
        new(
            root,
            name,
            options.StorageMode,
            memoryBudgetBytes,
            scratchDiskBudgetBytes,
            options.SegmentSizeBytes);

    private static void ValidateOptions(CompactNodeLookupIndexOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MemoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ScratchDiskBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SegmentSizeBytes);

        int recordSize = Unsafe.SizeOf<CompactNodeLookupRecord>();
        if (options.MemoryBudgetBytes / BudgetPartitionCount < recordSize)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The compact node lookup memory budget cannot fit one record per partition.");
        }

        if (options.ScratchDiskBudgetBytes / BudgetPartitionCount < recordSize)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The compact node lookup scratch budget cannot fit one record per partition.");
        }
    }
}
