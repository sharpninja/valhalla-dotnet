using System.Collections;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;

using SharpNinja.Valhalla.Generation.Storage;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal sealed record ComplexRestrictionSequenceSetOptions(
    string WorkingDirectory,
    IntermediateStorageMode StorageMode,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    int SegmentSizeBytes = 64 * 1024 * 1024);

internal sealed class ComplexRestrictionSequenceSet : IDisposable
{
    private const string CleanupFailureKey =
        "ComplexRestrictionSequenceSet.CleanupFailure";

    private readonly string workingDirectory;
    private readonly ExternalSequenceSortResult<
        ComplexRestrictionProjectionRecord> forwardResult;
    private readonly ExternalSequenceSortResult<
        ReverseRestrictionProjectionRecord> reverseResult;
    private readonly LazyOsmRestrictionSequence<
        ComplexRestrictionProjectionRecord> forward;
    private readonly LazyOsmRestrictionSequence<
        ReverseRestrictionProjectionRecord> reverse;
    private bool disposed;

    private ComplexRestrictionSequenceSet(
        string workingDirectory,
        CompactOsmSemanticStore semanticStore,
        ExternalSequenceSortResult<ComplexRestrictionProjectionRecord>
            forwardResult,
        ExternalSequenceSortResult<ReverseRestrictionProjectionRecord>
            reverseResult)
    {
        this.workingDirectory = workingDirectory;
        this.forwardResult = forwardResult;
        this.reverseResult = reverseResult;
        forward = LazyOsmRestrictionSequence<
            ComplexRestrictionProjectionRecord>.CreateForward(
                forwardResult.Output,
                semanticStore);
        reverse = LazyOsmRestrictionSequence<
            ReverseRestrictionProjectionRecord>.CreateReverse(
                reverseResult.Output);
    }

    internal IReadOnlyList<OSMRestriction> Forward
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return forward;
        }
    }

    internal IReadOnlyList<OSMRestriction> Reverse
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return reverse;
        }
    }

    internal long ForwardMaterializationCount =>
        forward.MaterializationCount;

    internal long ReverseMaterializationCount =>
        reverse.MaterializationCount;

    internal int PeakCachedRestrictionCount =>
        Math.Max(forward.PeakCachedCount, reverse.PeakCachedCount);

    internal IntermediateStorageMode ForwardStorageMode =>
        forwardResult.Output.State.ActiveStorageMode;

    internal IntermediateStorageMode ReverseStorageMode =>
        reverseResult.Output.State.ActiveStorageMode;

    internal static async ValueTask<ComplexRestrictionSequenceSet> BuildAsync(
        CompactOsmSemanticStore semanticStore,
        ComplexRestrictionSequenceSetOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(semanticStore);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        string root = Path.Combine(
            Path.GetFullPath(options.WorkingDirectory),
            $"complex-restrictions-{Guid.NewGuid():N}");

        long memoryPartition = options.MemoryBudgetBytes / 4;
        long scratchPartition = options.ScratchDiskBudgetBytes / 4;
        long minimumMemoryPartition = Math.Max(
            checked(
                Unsafe.SizeOf<ComplexRestrictionProjectionRecord>() +
                sizeof(long)),
            checked(
                Unsafe.SizeOf<ReverseRestrictionProjectionRecord>() +
                sizeof(long)));
        if (memoryPartition < minimumMemoryPartition)
        {
            throw new ValhallaGenerationResourceLimitException(
                $"Complex restriction projection requires at least " +
                $"{checked(minimumMemoryPartition * 4)} aggregate memory " +
                $"bytes, but {options.MemoryBudgetBytes} were configured.");
        }

        if (scratchPartition < options.SegmentSizeBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                $"Complex restriction projection requires at least " +
                $"{checked((long)options.SegmentSizeBytes * 4)} aggregate " +
                $"scratch bytes, but {options.ScratchDiskBudgetBytes} " +
                $"were configured.");
        }

        long storeMemoryBudget = memoryPartition;
        long storeScratchBudget = scratchPartition;
        long sortMemoryBudget = memoryPartition;
        long sortScratchBudget = scratchPartition;
        Directory.CreateDirectory(root);

        IntermediateSequenceStore<ComplexRestrictionProjectionRecord>?
            forwardInput = null;
        IntermediateSequenceStore<ReverseRestrictionProjectionRecord>?
            reverseInput = null;
        ExternalSequenceSortResult<ComplexRestrictionProjectionRecord>?
            sortedForward = null;
        ExternalSequenceSortResult<ReverseRestrictionProjectionRecord>?
            sortedReverse = null;

        try
        {
            forwardInput = new IntermediateSequenceStore<
                ComplexRestrictionProjectionRecord>(
                new IntermediateSequenceStoreOptions(
                    root,
                    "forward-input",
                    options.StorageMode,
                    storeMemoryBudget,
                    storeScratchBudget,
                    options.SegmentSizeBytes));
            reverseInput = new IntermediateSequenceStore<
                ReverseRestrictionProjectionRecord>(
                new IntermediateSequenceStoreOptions(
                    root,
                    "reverse-input",
                    options.StorageMode,
                    storeMemoryBudget,
                    storeScratchBudget,
                    options.SegmentSizeBytes));

            EmitProjectionRecords(
                semanticStore,
                forwardInput,
                reverseInput,
                cancellationToken);
            await forwardInput.CompleteAsync(cancellationToken)
                .ConfigureAwait(false);
            await reverseInput.CompleteAsync(cancellationToken)
                .ConfigureAwait(false);

            sortedForward = await ExternalSequenceSorter.SortAsync(
                    forwardInput,
                    new IntermediateSequenceStoreOptions(
                        root,
                        "forward-sorted",
                        options.StorageMode,
                        storeMemoryBudget,
                        storeScratchBudget,
                        options.SegmentSizeBytes),
                    new ExternalSequenceSortOptions(
                        root,
                        "forward-sort",
                        sortMemoryBudget,
                        sortScratchBudget),
                    (left, right) =>
                        CompareForward(semanticStore, left, right),
                    cancellationToken)
                .ConfigureAwait(false);
            forwardInput.Dispose();
            forwardInput = null;

            sortedReverse = await ExternalSequenceSorter.SortAsync(
                    reverseInput,
                    new IntermediateSequenceStoreOptions(
                        root,
                        "reverse-sorted",
                        options.StorageMode,
                        storeMemoryBudget,
                        storeScratchBudget,
                        options.SegmentSizeBytes),
                    new ExternalSequenceSortOptions(
                        root,
                        "reverse-sort",
                        sortMemoryBudget,
                        sortScratchBudget),
                    CompareReverse,
                    cancellationToken)
                .ConfigureAwait(false);

            reverseInput.Dispose();
            reverseInput = null;

            var result = new ComplexRestrictionSequenceSet(
                root,
                semanticStore,
                sortedForward,
                sortedReverse);
            sortedForward = null;
            sortedReverse = null;
            return result;
        }
        catch (Exception operationFailure)
        {
            Exception? cleanupFailure = ExecuteCleanupActions(
            [
                () => sortedReverse?.Dispose(),
                () => sortedForward?.Dispose(),
                () => reverseInput?.Dispose(),
                () => forwardInput?.Dispose(),
                () => DeleteWorkingDirectory(root),
            ]);
            if (cleanupFailure is not null)
            {
                operationFailure.Data[CleanupFailureKey] = cleanupFailure;
            }

            ExceptionDispatchInfo.Capture(operationFailure).Throw();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        Exception? cleanupFailure = ExecuteCleanupActions(
        [
            reverseResult.Dispose,
            forwardResult.Dispose,
            () => DeleteWorkingDirectory(workingDirectory),
        ]);
        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }

        disposed = true;
    }

    private static void EmitProjectionRecords(
        CompactOsmSemanticStore semanticStore,
        IIntermediateSequenceStore<ComplexRestrictionProjectionRecord>
            forward,
        IIntermediateSequenceStore<ReverseRestrictionProjectionRecord>
            reverse,
        CancellationToken cancellationToken)
    {
        for (long ordinal = 0;
             ordinal < semanticStore.RestrictionCount;
             ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GenerationRestrictionRecord source =
                semanticStore.ReadRestriction(ordinal);
            if (!ComplexRestrictionSemantics.TryProject(
                    semanticStore,
                    source,
                    out ComplexRestrictionSemanticProjection projection))
            {
                continue;
            }

            if (projection.Conditional)
            {
                throw new InvalidDataException(
                    $"OSM relation {source.OsmRelationId} contains a " +
                    "conditional restriction, but pooled generation cannot " +
                    "publish conditional restrictions until the official " +
                    "time-domain parser and runtime evaluator are available.");
            }

            RestrictionViaProjectionKind viaProjection =
                projection.ViaWay
                    ? RestrictionViaProjectionKind.SourceWays
                    : RestrictionViaProjectionKind.ToWaySentinel;
            int viaCount = projection.ViaWay ? source.ViaCount : 1;
            forward.Append(
                new ComplexRestrictionProjectionRecord(
                    checked((ulong)source.FromWayId),
                    checked((ulong)source.ToWayId),
                    source.ViaOffset,
                    viaCount,
                    viaProjection,
                    projection.Type,
                    projection.Modes,
                    projection.Probability,
                    TimeDomain: 0,
                    source.CanonicalOrdinal));
            reverse.Append(
                new ReverseRestrictionProjectionRecord(
                    checked((ulong)source.ToWayId),
                    checked((ulong)source.FromWayId),
                    projection.Modes,
                    source.CanonicalOrdinal));
        }
    }

    private static int CompareForward(
        CompactOsmSemanticStore semanticStore,
        ComplexRestrictionProjectionRecord left,
        ComplexRestrictionProjectionRecord right)
    {
        int result = left.FromWayId.CompareTo(right.FromWayId);
        if (result != 0)
        {
            return result;
        }

        result = left.ToWayId.CompareTo(right.ToWayId);
        if (result != 0)
        {
            return result;
        }

        for (int index = 0;
             index < OSMRestriction.MaxViasPerRestriction;
             index++)
        {
            ulong leftVia = GetProjectedVia(semanticStore, left, index);
            ulong rightVia = GetProjectedVia(semanticStore, right, index);
            result = leftVia.CompareTo(rightVia);
            if (result != 0)
            {
                return result;
            }
        }

        result = left.Modes.CompareTo(right.Modes);
        if (result != 0)
        {
            return result;
        }

        result = left.Probability.CompareTo(right.Probability);
        if (result != 0)
        {
            return result;
        }

        result = left.TimeDomain.CompareTo(right.TimeDomain);
        return result != 0
            ? result
            : left.CanonicalOrdinal.CompareTo(right.CanonicalOrdinal);
    }

    private static int CompareReverse(
        ReverseRestrictionProjectionRecord left,
        ReverseRestrictionProjectionRecord right)
    {
        int result = left.FromWayId.CompareTo(right.FromWayId);
        if (result != 0)
        {
            return result;
        }

        result = left.ToWayId.CompareTo(right.ToWayId);
        if (result != 0)
        {
            return result;
        }

        result = left.Modes.CompareTo(right.Modes);
        return result != 0
            ? result
            : left.CanonicalOrdinal.CompareTo(right.CanonicalOrdinal);
    }

    private static ulong GetProjectedVia(
        CompactOsmSemanticStore semanticStore,
        ComplexRestrictionProjectionRecord record,
        int index)
    {
        if (index >= record.ViaCount)
        {
            return 0;
        }

        if (record.ViaProjection ==
            RestrictionViaProjectionKind.ToWaySentinel)
        {
            return index == 0 ? record.ToWayId : 0;
        }

        GenerationRestrictionViaRecord via =
            semanticStore.ReadRestrictionVia(
                checked(record.ViaOffset + index));
        if (via.MemberType != OsmMemberType.Way)
        {
            throw new InvalidDataException(
                $"Restriction projection expected a way via but found " +
                $"{via.MemberType} for relation {via.OsmRelationId}.");
        }

        return checked((ulong)via.MemberId);
    }

    private static void ValidateOptions(
        ComplexRestrictionSequenceSetOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        if (options.MemoryBudgetBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MemoryBudgetBytes));
        }

        if (options.ScratchDiskBudgetBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.ScratchDiskBudgetBytes));
        }

        if (options.SegmentSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.SegmentSizeBytes));
        }
    }

    private static Exception? ExecuteCleanupActions(
        IReadOnlyList<Action> actions)
    {
        Exception? firstFailure = null;
        foreach (Action action in actions)
        {
            try
            {
                action();
            }
            catch (Exception cleanupFailure)
            {
                firstFailure ??= cleanupFailure;
            }
        }

        return firstFailure;
    }

    private static void DeleteWorkingDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class LazyOsmRestrictionSequence<T> :
        IReadOnlyList<OSMRestriction>
        where T : unmanaged
    {
        private readonly IIntermediateSequenceStore<T> store;
        private readonly CompactOsmSemanticStore? semanticStore;
        private readonly Func<T, OSMRestriction> materialize;
        private int cachedIndex = -1;
        private OSMRestriction cachedValue;

        private LazyOsmRestrictionSequence(
            IIntermediateSequenceStore<T> store,
            CompactOsmSemanticStore? semanticStore,
            Func<T, OSMRestriction> materialize)
        {
            this.store = store;
            this.semanticStore = semanticStore;
            this.materialize = materialize;
            if (store.State.RecordCount > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"Restriction sequence contains {store.State.RecordCount} " +
                    "records, exceeding IReadOnlyList capacity.");
            }
        }

        internal long MaterializationCount { get; private set; }

        internal int PeakCachedCount { get; private set; }

        public int Count => checked((int)store.State.RecordCount);

        public OSMRestriction this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                if (index >= Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                if (cachedIndex == index)
                {
                    return cachedValue;
                }

                cachedValue = materialize(store.Read(index));
                cachedIndex = index;
                MaterializationCount++;
                PeakCachedCount = 1;
                return cachedValue;
            }
        }

        internal static LazyOsmRestrictionSequence<
            ComplexRestrictionProjectionRecord> CreateForward(
            IIntermediateSequenceStore<
                ComplexRestrictionProjectionRecord> store,
            CompactOsmSemanticStore semanticStore)
        {
            ArgumentNullException.ThrowIfNull(semanticStore);
            return new LazyOsmRestrictionSequence<
                ComplexRestrictionProjectionRecord>(
                store,
                semanticStore,
                record => MaterializeForward(semanticStore, record));
        }

        internal static LazyOsmRestrictionSequence<
            ReverseRestrictionProjectionRecord> CreateReverse(
            IIntermediateSequenceStore<
                ReverseRestrictionProjectionRecord> store) =>
            new(
                store,
                semanticStore: null,
                MaterializeReverse);

        public IEnumerator<OSMRestriction> GetEnumerator()
        {
            for (int index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private static OSMRestriction MaterializeForward(
            CompactOsmSemanticStore semanticStore,
            ComplexRestrictionProjectionRecord record)
        {
            var restriction = new OSMRestriction();
            restriction.SetFrom(record.FromWayId);
            restriction.SetTo(record.ToWayId);
            restriction.SetType(record.Type);
            restriction.SetModes(record.Modes);
            restriction.SetProbability(record.Probability);
            restriction.SetTimeDomain(record.TimeDomain);

            var vias = new ulong[record.ViaCount];
            for (int index = 0; index < vias.Length; index++)
            {
                vias[index] = GetProjectedVia(
                    semanticStore,
                    record,
                    index);
            }

            restriction.SetVias(vias);
            return restriction;
        }

        private static OSMRestriction MaterializeReverse(
            ReverseRestrictionProjectionRecord record)
        {
            var restriction = new OSMRestriction();
            restriction.SetFrom(record.FromWayId);
            restriction.SetTo(record.ToWayId);
            restriction.SetModes(record.Modes);
            return restriction;
        }
    }
}
