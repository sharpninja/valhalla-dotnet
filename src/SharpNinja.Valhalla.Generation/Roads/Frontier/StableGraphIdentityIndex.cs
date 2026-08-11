using System.Runtime.CompilerServices;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Storage;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal sealed record StableGraphIdentityIndexOptions(
    string WorkingDirectory,
    IntermediateStorageMode StorageMode,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    int SegmentSizeBytes = 64 * 1024 * 1024);

internal sealed class StableGraphIdentityIndex : IDisposable
{
    private readonly ExternalSequenceSortResult<GenerationGraphNodeCandidate>
        orderedCandidates;
    private readonly IntermediateSequenceStore<StableGraphNodeIdentity> identities;
    private readonly ExternalSequenceSortResult<StableGraphNodeIdentity> lookup;
    private bool disposed;

    private StableGraphIdentityIndex(
        ExternalSequenceSortResult<GenerationGraphNodeCandidate> orderedCandidates,
        IntermediateSequenceStore<StableGraphNodeIdentity> identities,
        ExternalSequenceSortResult<StableGraphNodeIdentity> lookup,
        IntermediateSequenceManifest identityManifest)
    {
        this.orderedCandidates = orderedCandidates;
        this.identities = identities;
        this.lookup = lookup;
        IdentityManifest = identityManifest;
    }

    internal long IdentityCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return identities.State.RecordCount;
        }
    }

    internal IntermediateSequenceManifest IdentityManifest { get; }

    internal IntermediateSequenceManifest LookupManifest
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return lookup.Receipt.OutputManifest;
        }
    }

    internal ExternalSequenceSortReceipt CandidateSortReceipt
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return orderedCandidates.Receipt;
        }
    }

    internal ExternalSequenceSortReceipt LookupSortReceipt
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return lookup.Receipt;
        }
    }

    internal StableGraphNodeIdentity ReadIdentity(long ordinal)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return identities.Read(ordinal);
    }

    internal bool TryGetIdentity(
        GraphId graphId,
        out StableGraphNodeIdentity identity)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long low = 0;
        long high = identities.State.RecordCount;
        while (low < high)
        {
            long middle = low + ((high - low) / 2);
            StableGraphNodeIdentity candidate = identities.Read(middle);
            if (candidate.GraphId < graphId)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (low < identities.State.RecordCount)
        {
            StableGraphNodeIdentity candidate = identities.Read(low);
            if (candidate.GraphId == graphId)
            {
                identity = candidate;
                return true;
            }
        }

        identity = default;
        return false;
    }

    internal bool TryGetGraphId(long osmNodeId, out GraphId graphId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long low = 0;
        long high = lookup.Output.State.RecordCount;
        while (low < high)
        {
            long middle = low + ((high - low) / 2);
            StableGraphNodeIdentity candidate = lookup.Output.Read(middle);
            if (candidate.OsmNodeId < osmNodeId)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (low < lookup.Output.State.RecordCount)
        {
            StableGraphNodeIdentity candidate = lookup.Output.Read(low);
            if (candidate.OsmNodeId == osmNodeId)
            {
                graphId = candidate.GraphId;
                return true;
            }
        }

        graphId = GraphId.Invalid;
        return false;
    }


    internal bool TryGetGraphId(
        long osmNodeId,
        long canonicalOrdinal,
        out GraphId graphId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long low = 0;
        long high = lookup.Output.State.RecordCount - 1;
        while (low <= high)
        {
            long middle = low + ((high - low) / 2);
            StableGraphNodeIdentity candidate = lookup.Output.Read(middle);
            int comparison = candidate.OsmNodeId.CompareTo(osmNodeId);
            if (comparison == 0)
            {
                comparison = candidate.CanonicalOrdinal.CompareTo(canonicalOrdinal);
            }

            if (comparison < 0)
            {
                low = middle + 1;
                continue;
            }

            if (comparison > 0)
            {
                high = middle - 1;
                continue;
            }

            graphId = candidate.GraphId;
            return true;
        }

        graphId = GraphId.Invalid;
        return false;
    }

    internal static async ValueTask<StableGraphIdentityIndex> BuildAsync(
        IIntermediateSequenceStore<GenerationGraphNodeCandidate> candidates,
        StableGraphIdentityIndexOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        if (!candidates.State.IsComplete)
        {
            throw new InvalidOperationException(
                "Graph node candidates must be complete before assigning identities.");
        }

        int candidateSize = Unsafe.SizeOf<GenerationGraphNodeCandidate>();
        int identitySize = Unsafe.SizeOf<StableGraphNodeIdentity>();
        BudgetPartition memory = BudgetPartition.Create(
            options.MemoryBudgetBytes,
            candidateSize * 2L,
            candidateSize,
            identitySize,
            identitySize * 2L,
            identitySize);
        BudgetPartition scratch = BudgetPartition.Create(
            options.ScratchDiskBudgetBytes,
            candidateSize * 8L,
            candidateSize,
            identitySize,
            identitySize * 8L,
            identitySize);

        string indexDirectory = Path.Combine(
            options.WorkingDirectory,
            "stable-graph-identity-index");
        Directory.CreateDirectory(indexDirectory);

        ExternalSequenceSortResult<GenerationGraphNodeCandidate>? orderedCandidates = null;
        IntermediateSequenceStore<StableGraphNodeIdentity>? identities = null;
        ExternalSequenceSortResult<StableGraphNodeIdentity>? lookup = null;
        try
        {
            orderedCandidates = await ExternalSequenceSorter.SortAsync(
                    candidates,
                    new IntermediateSequenceStoreOptions(
                        indexDirectory,
                        "ordered-candidates",
                        options.StorageMode,
                        memory.Second,
                        scratch.Second,
                        options.SegmentSizeBytes),
                    new ExternalSequenceSortOptions(
                        indexDirectory,
                        "candidate-sort",
                        memory.First,
                        scratch.First),
                    StableGraphIdentityOrdering.CompareCandidates,
                    cancellationToken)
                .ConfigureAwait(false);

            identities = new IntermediateSequenceStore<StableGraphNodeIdentity>(
                new IntermediateSequenceStoreOptions(
                    indexDirectory,
                    "assigned-identities",
                    options.StorageMode,
                    memory.Third,
                    scratch.Third,
                    options.SegmentSizeBytes));
            AssignIdentities(
                orderedCandidates.Output,
                identities,
                cancellationToken);
            IntermediateSequenceManifest identityManifest =
                await identities.CompleteAsync(cancellationToken).ConfigureAwait(false);

            lookup = await ExternalSequenceSorter.SortAsync(
                    identities,
                    new IntermediateSequenceStoreOptions(
                        indexDirectory,
                        "identity-lookup",
                        options.StorageMode,
                        memory.Fifth,
                        scratch.Fifth,
                        options.SegmentSizeBytes),
                    new ExternalSequenceSortOptions(
                        indexDirectory,
                        "lookup-sort",
                        memory.Fourth,
                        scratch.Fourth),
                    StableGraphIdentityOrdering.CompareLookup,
                    cancellationToken)
                .ConfigureAwait(false);

            var result = new StableGraphIdentityIndex(
                orderedCandidates,
                identities,
                lookup,
                identityManifest);
            orderedCandidates = null;
            identities = null;
            lookup = null;
            return result;
        }
        catch
        {
            lookup?.Dispose();
            identities?.Dispose();
            orderedCandidates?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        lookup.Dispose();
        identities.Dispose();
        orderedCandidates.Dispose();
        disposed = true;
    }

    private static void AssignIdentities(
        IIntermediateSequenceStore<GenerationGraphNodeCandidate> ordered,
        IIntermediateSequenceStore<StableGraphNodeIdentity> destination,
        CancellationToken cancellationToken)
    {
        GraphId currentTile = GraphId.Invalid;
        uint localId = 0;
        for (long ordinal = 0; ordinal < ordered.State.RecordCount; ordinal++)
        {
            if ((ordinal & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            GenerationGraphNodeCandidate candidate = ordered.Read(ordinal);
            ValidateCandidate(candidate);
            if (!currentTile.IsValid() ||
                currentTile.TileValue() != candidate.TileBase.TileValue())
            {
                currentTile = candidate.TileBase.TileBase();
                localId = 0;
            }
            else
            {
                if (localId == GraphConstants.MaxGraphId)
                {
                    throw new ValhallaGenerationResourceLimitException(
                        $"Tile {currentTile} exceeds the GraphId node capacity.");
                }

                localId++;
            }

            destination.Append(new StableGraphNodeIdentity(
                candidate.Node.OsmNodeId,
                candidate.CanonicalOrdinal,
                new GraphId(
                    candidate.TileBase.Tileid(),
                    candidate.TileBase.Level(),
                    localId),
                candidate.GridId));
        }
    }

    private static void ValidateCandidate(GenerationGraphNodeCandidate candidate)
    {
        if (!candidate.TileBase.IsValid() || candidate.TileBase.Id() != 0)
        {
            throw new InvalidDataException(
                $"OSM node {candidate.Node.OsmNodeId} has an invalid tile base.");
        }

        if (candidate.CanonicalOrdinal < 0)
        {
            throw new InvalidDataException(
                $"OSM node {candidate.Node.OsmNodeId} has a negative canonical ordinal.");
        }
    }

    private static void ValidateOptions(StableGraphIdentityIndexOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MemoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ScratchDiskBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SegmentSizeBytes);
    }

    private readonly record struct BudgetPartition(
        long First,
        long Second,
        long Third,
        long Fourth,
        long Fifth)
    {
        internal static BudgetPartition Create(
            long total,
            long firstMinimum,
            long secondMinimum,
            long thirdMinimum,
            long fourthMinimum,
            long fifthMinimum)
        {
            long minimum = checked(
                firstMinimum +
                secondMinimum +
                thirdMinimum +
                fourthMinimum +
                fifthMinimum);
            if (total < minimum)
            {
                throw new ValhallaGenerationResourceLimitException(
                    $"The graph identity budget of {total} bytes cannot fit " +
                    $"the required {minimum} bytes.");
            }

            long remainder = total - minimum;
            long firstExtra = remainder * 2 / 8;
            long secondExtra = remainder / 8;
            long thirdExtra = remainder * 2 / 8;
            long fourthExtra = remainder * 2 / 8;
            long fifthExtra =
                remainder - firstExtra - secondExtra - thirdExtra - fourthExtra;
            return new BudgetPartition(
                checked(firstMinimum + firstExtra),
                checked(secondMinimum + secondExtra),
                checked(thirdMinimum + thirdExtra),
                checked(fourthMinimum + fourthExtra),
                checked(fifthMinimum + fifthExtra));
        }
    }
}

internal static class StableGraphIdentityOrdering
{
    internal static int CompareCandidates(
        GenerationGraphNodeCandidate x,
        GenerationGraphNodeCandidate y)
    {
        int comparison = NodeEdgeIncidenceOrdering.CompareGraphIds(
            x.TileBase,
            y.TileBase);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = x.GridId.CompareTo(y.GridId);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = x.Node.OsmNodeId.CompareTo(y.Node.OsmNodeId);
        return comparison != 0
            ? comparison
            : x.CanonicalOrdinal.CompareTo(y.CanonicalOrdinal);
    }

    internal static int CompareLookup(
        StableGraphNodeIdentity x,
        StableGraphNodeIdentity y)
    {
        int comparison = x.OsmNodeId.CompareTo(y.OsmNodeId);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = x.CanonicalOrdinal.CompareTo(y.CanonicalOrdinal);
        return comparison != 0
            ? comparison
            : NodeEdgeIncidenceOrdering.CompareGraphIds(x.GraphId, y.GraphId);
    }
}
