using System.Runtime.CompilerServices;
using SharpNinja.Valhalla.Generation.Storage;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal sealed record NodeIncidenceIndexOptions(
    string WorkingDirectory,
    IntermediateStorageMode StorageMode,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    int SegmentSizeBytes = 64 * 1024 * 1024);

internal sealed class NodeIncidenceIndex : IDisposable
{
    private readonly ExternalSequenceSortResult<NodeIncidenceRecord> sorted;
    private readonly IntermediateSequenceStore<NodeIncidenceSummary> summaries;
    private bool disposed;

    private NodeIncidenceIndex(
        ExternalSequenceSortResult<NodeIncidenceRecord> sorted,
        IntermediateSequenceStore<NodeIncidenceSummary> summaries,
        IntermediateSequenceManifest summaryManifest)
    {
        this.sorted = sorted;
        this.summaries = summaries;
        SummaryManifest = summaryManifest;
    }

    internal long IncidenceCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return sorted.Output.State.RecordCount;
        }
    }

    internal long SummaryCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return summaries.State.RecordCount;
        }
    }

    internal ExternalSequenceSortReceipt SortReceipt
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return sorted.Receipt;
        }
    }

    internal IntermediateSequenceManifest IncidenceManifest => SortReceipt.OutputManifest;

    internal IntermediateSequenceManifest SummaryManifest { get; }

    internal NodeIncidenceRecord ReadIncidence(long ordinal)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return sorted.Output.Read(ordinal);
    }

    internal NodeIncidenceSummary ReadSummary(long ordinal)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return summaries.Read(ordinal);
    }

    internal bool TryFindSummary(long osmNodeId, out NodeIncidenceSummary summary)
    {
        long ordinal = FindSummaryOrdinalAtOrAfter(osmNodeId);
        if (ordinal < SummaryCount)
        {
            NodeIncidenceSummary candidate = ReadSummary(ordinal);
            if (candidate.OsmNodeId == osmNodeId)
            {
                summary = candidate;
                return true;
            }
        }

        summary = default;
        return false;
    }

    internal long FindSummaryOrdinalAtOrAfter(long osmNodeId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long low = 0;
        long high = summaries.State.RecordCount;
        while (low < high)
        {
            long middle = low + ((high - low) / 2);
            if (summaries.Read(middle).OsmNodeId < osmNodeId)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    internal static async ValueTask<NodeIncidenceIndex> BuildAsync(
        IIntermediateSequenceStore<NodeIncidenceRecord> input,
        NodeIncidenceIndexOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MemoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ScratchDiskBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SegmentSizeBytes);
        if (!input.State.IsComplete)
        {
            throw new InvalidOperationException(
                "The node incidence input must be complete before indexing.");
        }

        int incidenceSize = Unsafe.SizeOf<NodeIncidenceRecord>();
        int summarySize = Unsafe.SizeOf<NodeIncidenceSummary>();
        long sortMemoryBudget = Math.Max(
            incidenceSize * 2L,
            options.MemoryBudgetBytes / 2);
        long outputMemoryBudget = Math.Max(
            incidenceSize,
            options.MemoryBudgetBytes / 4);
        long summaryMemoryBudget = Math.Max(
            summarySize,
            options.MemoryBudgetBytes - sortMemoryBudget - outputMemoryBudget);
        long sortScratchBudget = Math.Max(
            incidenceSize * 8L,
            options.ScratchDiskBudgetBytes * 3 / 4);
        long storeScratchBudget = Math.Max(
            Math.Max(incidenceSize, summarySize),
            options.ScratchDiskBudgetBytes - sortScratchBudget);
        if (checked(sortMemoryBudget + outputMemoryBudget + summaryMemoryBudget) >
            options.MemoryBudgetBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The node incidence memory budget cannot fit the required stores.");
        }

        string indexDirectory = Path.Combine(
            options.WorkingDirectory,
            "node-incidence-index");
        Directory.CreateDirectory(indexDirectory);
        ExternalSequenceSortResult<NodeIncidenceRecord>? sorted = null;
        IntermediateSequenceStore<NodeIncidenceSummary>? summaries = null;
        try
        {
            sorted = await ExternalSequenceSorter.SortAsync(
                    input,
                    new IntermediateSequenceStoreOptions(
                        indexDirectory,
                        "ordered-incidences",
                        options.StorageMode,
                        outputMemoryBudget,
                        storeScratchBudget,
                        options.SegmentSizeBytes),
                    new ExternalSequenceSortOptions(
                        indexDirectory,
                        "incidence-sort",
                        sortMemoryBudget,
                        sortScratchBudget),
                    NodeIncidenceIndexBuilder.Compare,
                    cancellationToken)
                .ConfigureAwait(false);

            summaries = new IntermediateSequenceStore<NodeIncidenceSummary>(
                new IntermediateSequenceStoreOptions(
                    indexDirectory,
                    "summaries",
                    options.StorageMode,
                    summaryMemoryBudget,
                    storeScratchBudget,
                    options.SegmentSizeBytes));

            long offset = 0;
            while (offset < sorted.Output.State.RecordCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long end = FindNodeEnd(sorted.Output, offset, cancellationToken);
                summaries.Append(Summarize(sorted.Output, offset, end));
                offset = end;
            }

            IntermediateSequenceManifest summaryManifest =
                await summaries.CompleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            var result = new NodeIncidenceIndex(
                sorted,
                summaries,
                summaryManifest);
            sorted = null;
            summaries = null;
            return result;
        }
        catch
        {
            summaries?.Dispose();
            sorted?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        summaries.Dispose();
        sorted.Dispose();
        disposed = true;
    }

    private static long FindNodeEnd(
        IIntermediateSequenceStore<NodeIncidenceRecord> incidences,
        long start,
        CancellationToken cancellationToken)
    {
        long osmNodeId = incidences.Read(start).OsmNodeId;
        long end = start + 1;
        while (end < incidences.State.RecordCount)
        {
            if ((end & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (incidences.Read(end).OsmNodeId != osmNodeId)
            {
                break;
            }

            end++;
        }

        return end;
    }

    private static NodeIncidenceSummary Summarize(
        IIntermediateSequenceStore<NodeIncidenceRecord> incidences,
        long start,
        long end)
    {
        NodeAnchorFlags flags = NodeAnchorFlags.None;
        int distinctWayCount = 0;
        long lastWayId = long.MinValue;
        int occurrencesForWay = 0;
        for (long index = start; index < end; index++)
        {
            NodeIncidenceRecord incidence = incidences.Read(index);
            flags |= NodeIncidenceIndexBuilder.ToAnchorFlags(incidence.Roles);
            if (!NodeIncidenceIndexBuilder.IsWayRole(incidence.Roles))
            {
                continue;
            }

            if (incidence.OwnerId != lastWayId)
            {
                if (occurrencesForWay > 1)
                {
                    flags |= NodeAnchorFlags.SelfIntersection;
                }

                distinctWayCount = checked(distinctWayCount + 1);
                lastWayId = incidence.OwnerId;
                occurrencesForWay = 1;
            }
            else
            {
                occurrencesForWay = checked(occurrencesForWay + 1);
            }
        }

        if (occurrencesForWay > 1)
        {
            flags |= NodeAnchorFlags.SelfIntersection;
        }

        if (distinctWayCount > 1)
        {
            flags |= NodeAnchorFlags.SharedWay;
        }

        int count = checked((int)(end - start));
        return new NodeIncidenceSummary(
            incidences.Read(start).OsmNodeId,
            start,
            count,
            distinctWayCount,
            flags,
            count);
    }
}
