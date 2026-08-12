using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Storage;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal sealed record PooledRoadRestrictionStageOptions(
    string WorkingDirectory,
    IntermediateStorageMode StorageMode,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    int SegmentSizeBytes = 64 * 1024 * 1024)
{
    internal Action<GraphId>? TileWrittenObserver { get; init; }

    internal Action<string>? BeforeFinalCohortSealObserver { get; init; }

    internal Action<string>? SourceManifestCreatedObserver { get; init; }

    internal Action<string, long>? SourceHashProgressObserver { get; init; }

    internal Action<string>? SourceManifestPathValidatedObserver { get; init; }

    internal Action<string, int>? SourceManifestPathLookupObserver { get; init; }

    internal long? MutationMemoryBudgetBytesOverride { get; init; }

    internal int TraversalDepthCapacity { get; init; } = 256;

    internal int VisitedNodeCapacity { get; init; } = 4096;

    internal int TraversedEdgeCapacity { get; init; } = 4096;
}

internal sealed record PooledRoadRestrictionStageReceipt(
    int ProjectedForwardCount,
    int ProjectedReverseCount,
    uint SerializedForwardCount,
    uint SerializedReverseCount)
{
    internal uint SerializedCrossTileForwardCount { get; init; }

    internal uint MarkedCrossTileEdgeCount { get; init; }

    internal uint MissingCrossTileDestinationCount { get; init; }

    internal long SequenceMemoryBudgetBytes { get; init; }

    internal long ReaderCacheBudgetBytes { get; init; }

    internal long BookkeepingMemoryBudgetBytes { get; init; }

    internal long MutationMemoryBudgetBytes { get; init; }

    internal long PeakTileMutationAllocatedBytes { get; init; }

    internal long CopyBufferBudgetBytes { get; init; }

    internal long SourceManifestMemoryBudgetBytes { get; init; }

    internal long ValidationMemoryBudgetBytes { get; init; }

    internal long RestampHashMemoryBudgetBytes { get; init; }

    internal ushort TilesetBuildId { get; init; }
    internal long StagedScratchBytes { get; init; }

    internal long ProjectionScratchBudgetBytes { get; init; }

    internal long MutationPlanScratchBudgetBytes { get; init; }

    internal long PeakMutationPlanMemoryBytes { get; init; }

    internal long PeakMutationPlanScratchBytes { get; init; }

    internal long PeakAggregateStageMemoryBytes { get; init; }

    internal long PeakAggregateStageScratchBytes { get; init; }

    internal long TraversalWorkspaceReservedBytes { get; init; }
}

internal static class PooledRoadRestrictionStage
{
    internal const int ValidationMemoryBytes =
        ValidationBufferBytes +
        GraphTileHeader.HeaderSize +
        MD5.HashSizeInBytes;

    private const long MinimumBookkeepingMemoryBytes = 64 * 1024;
    private const long MinimumReaderCacheBytes = 64 * 1024;
    private const long SourceManifestBaseMemoryBytes = 4 * 1024;
    private const long SourceManifestEntryMemoryBytes = 256;
    private const int SourceHashBufferBytes = 4 * 1024;
    private const int MinimumCopyBufferBytes = 4 * 1024;
    private const int MaximumCopyBufferBytes = 64 * 1024;
    private const int ValidationBufferBytes = 16 * 1024;
    private const int NodeInfoSize = 32;
    private const int NodeTransitionSize = 8;
    private const int DirectedEdgeSize = 48;
    private const int DirectedEdgeExtSize = 8;
    private const int AccessRestrictionSize = 16;
    private const int TransitDepartureSize = 24;
    private const int TransitStopSize = 8;
    private const int TransitRouteSize = 40;
    private const int TransitScheduleSize = 16;
    private const int TransitTransferSize = 12;
    private const int SignSize = 8;
    private const int TurnLanesSize = 8;
    private const int AdminSize = 16;
    private const int EstimatedDeferredRestrictionBytes = 512;
    private const int EstimatedPartOfEdgeBytes = 64;
    private const int EstimatedTileCatalogEntryBytes = 216;

    internal static long GetMutationPlanMemoryBudgetBytes(
        long mutationMemoryBudgetBytes,
        long copyBufferBytes,
        RestrictionBuilder.ExecutionOptions executionOptions)
    {
        long traversalWorkspaceBytes =
            RestrictionBuilder.GetPlanTraversalWorkspaceReservationBytes(
                executionOptions);
        long fixedApplicationWorkspaceBytes = checked(
            copyBufferBytes +
            ValidationMemoryBytes +
            Unsafe.SizeOf<PlannedRestrictionRecord>() +
            Unsafe.SizeOf<PlannedEdgePatchRecord>());
        long requiredBytes = checked(
            traversalWorkspaceBytes + fixedApplicationWorkspaceBytes);
        if (mutationMemoryBudgetBytes < requiredBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                $"Restriction-plan traversal and application require a " +
                $"reservation of {requiredBytes} bytes, but only " +
                $"{mutationMemoryBudgetBytes} bytes were configured.");
        }

        return checked(
            mutationMemoryBudgetBytes - traversalWorkspaceBytes);
    }

    internal static async ValueTask<PooledRoadRestrictionStageReceipt>
        ApplyAsync(
            string sourceTileDirectory,
            string destinationTileDirectory,
            CompactOsmSemanticStore semanticStore,
            PooledRoadRestrictionStageOptions options,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTileDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationTileDirectory);
        ArgumentNullException.ThrowIfNull(semanticStore);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        string fullSourceDirectory = Path.GetFullPath(sourceTileDirectory);
        string fullDestinationDirectory =
            Path.GetFullPath(destinationTileDirectory);
        string fullWorkingDirectory =
            Path.GetFullPath(options.WorkingDirectory);
        ValidateIndependentDirectories(
            fullSourceDirectory,
            fullDestinationDirectory,
            fullWorkingDirectory);

        if (!Directory.Exists(fullSourceDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Unpublished source tile directory " +
                $"'{fullSourceDirectory}' does not exist.");
        }

        if (Directory.Exists(fullDestinationDirectory) ||
            File.Exists(fullDestinationDirectory))
        {
            throw new IOException(
                $"Restriction-stage destination " +
                $"'{fullDestinationDirectory}' already exists.");
        }

        long minimumManifestPhaseMemoryBytes = checked(
            MinimumBookkeepingMemoryBytes + SourceHashBufferBytes);
        if (options.MemoryBudgetBytes < minimumManifestPhaseMemoryBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The restriction-stage memory budget cannot fit the " +
                "minimum source-manifest and hashing workspace.");
        }

        long sourceManifestMemoryBudgetBytes =
            GetSourceManifestMemoryBudget(options.MemoryBudgetBytes);
        SourceTileTreeManifest sourceManifest =
            BuildSourceManifest(
                fullSourceDirectory,
                sourceManifestMemoryBudgetBytes,
                options.SourceHashProgressObserver,
                cancellationToken);
        options.SourceManifestCreatedObserver?.Invoke(fullSourceDirectory);
        StageBudget budget = CreateBudget(
            sourceManifest,
            options);

        bool workingDirectoryExisted =
            Directory.Exists(fullWorkingDirectory);
        string operationDirectory = Path.Combine(
            fullWorkingDirectory,
            $"pooled-restrictions-{Guid.NewGuid():N}");
        string incomingDirectory = Path.Combine(
            operationDirectory,
            "incoming");
        string sealedDirectory = Path.Combine(
            operationDirectory,
            "sealed");
        BoundedRestrictionTileCatalog? tileCatalog = null;
        ComplexRestrictionSequenceSet? restrictions = null;
        PooledRestrictionMutationPlanSink? planSink = null;
        BoundedRestrictionMutationPlan? mutationPlan = null;
        GraphReader? reader = null;
        PooledRoadRestrictionStageReceipt? receipt = null;
        long peakTileMutationAllocatedBytes = 0;
        int projectedForwardCount = 0;
        int projectedReverseCount = 0;
        Exception? operationFailure = null;
        Exception? cleanupFailure;
        bool destinationCreatedByOperation = false;

        try
        {
            Directory.CreateDirectory(incomingDirectory);
            await CopyDirectoryAsync(
                    fullSourceDirectory,
                    incomingDirectory,
                    sourceManifest,
                    budget.CopyBufferBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSourceManifestPaths(
                fullSourceDirectory,
                sourceManifest,
                options.SourceManifestPathValidatedObserver,
                options.SourceManifestPathLookupObserver,
                cancellationToken);

            tileCatalog = BoundedRestrictionTileCatalog.Build(
                incomingDirectory,
                budget.MaxTilesPerLevel);
            ValidateReadableTileTree(
                incomingDirectory,
                tileCatalog,
                cancellationToken,
                requireDerivedBuildId: true);

            restrictions =
                await ComplexRestrictionSequenceSet.BuildAsync(
                        semanticStore,
                        new ComplexRestrictionSequenceSetOptions(
                            operationDirectory,
                            options.StorageMode,
                            budget.SequenceMemoryBytes,
                            budget.ProjectionScratchBytes,
                            options.SegmentSizeBytes),
                        cancellationToken)
                    .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            reader = CreateBoundedReader(
                incomingDirectory,
                budget.ReaderCacheBytes);
            var executionOptions =
                new RestrictionBuilder.ExecutionOptions(
                    budget.MaxTilesPerLevel,
                    budget.MaxDeferredRestrictions,
                    budget.MaxPartOfRestrictionEdges,
                    options.TileWrittenObserver)
                {
                    TileCatalogProvider = tileCatalog.GetLevel,
                    TraversalDepthCapacity = options.TraversalDepthCapacity,
                    VisitedNodeCapacity = options.VisitedNodeCapacity,
                    TraversedEdgeCapacity = options.TraversedEdgeCapacity,
                };
            long traversalWorkspaceBytes =
                RestrictionBuilder.GetPlanTraversalWorkspaceReservationBytes(
                    executionOptions);
            long planMemoryBudgetBytes =
                GetMutationPlanMemoryBudgetBytes(
                    budget.MutationMemoryBytes,
                    budget.CopyBufferBytes,
                    executionOptions);

            planSink = new PooledRestrictionMutationPlanSink(
                new PooledRestrictionMutationPlanOptions(
                    operationDirectory,
                    planMemoryBudgetBytes,
                    budget.MutationPlanScratchBytes,
                    options.SegmentSizeBytes));

            projectedForwardCount = restrictions.Forward.Count;
            projectedReverseCount = restrictions.Reverse.Count;
            _ = RestrictionBuilder.BuildPlan(
                    reader,
                    restrictions.Forward,
                    restrictions.Reverse,
                    planSink,
                    executionOptions,
                    cancellationToken);
            restrictions.Dispose();
            restrictions = null;
            reader.Clear();
            mutationPlan =
                await planSink.CompleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            planSink.Dispose();
            planSink = null;

            foreach (GraphId tileId in tileCatalog.EnumerateAll())
            {
                cancellationToken.ThrowIfCancellationRequested();
                GraphTile sourceTile =
                    reader.GetGraphTile(tileId) ??
                    throw new InvalidDataException(
                        $"Restriction-plan tile {tileId} disappeared before application.");
                StreamingRestrictionPlanApplier.Apply(
                    incomingDirectory,
                    sourceTile,
                    mutationPlan,
                    checked((int)budget.CopyBufferBytes),
                    cancellationToken);
                reader.Clear();
                options.TileWrittenObserver?.Invoke(tileId);
            }

            uint serializedForwardCount = checked((uint)
                mutationPlan.CountRestrictions(
                    RestrictionMutationDirection.Forward));
            uint serializedReverseCount = checked((uint)
                mutationPlan.CountRestrictions(
                    RestrictionMutationDirection.Reverse));
            uint serializedCrossTileForwardCount = checked((uint)
                mutationPlan.CountCrossTileRestrictions(
                    RestrictionMutationDirection.Forward));
            uint markedCrossTileEdgeCount = checked((uint)
                mutationPlan.CountCrossTileEdgePatches());
            uint missingCrossTileDestinationCount = checked((uint)
                mutationPlan.Receipt.MissingDestinationCount);
            peakTileMutationAllocatedBytes = checked(
                budget.CopyBufferBytes +
                PooledRoadRestrictionStage.ValidationMemoryBytes);

            reader.Clear();
            ushort tilesetBuildId = BoundedTilesetRestamper.Restamp(
                incomingDirectory,
                tileCatalog,
                cancellationToken,
                hashMemoryBudgetBytes:
                    budget.RestampHashMemoryBytes);
            BoundedRestrictionTileCatalog finalCatalog =
                BoundedRestrictionTileCatalog.Build(
                    incomingDirectory,
                    budget.MaxTilesPerLevel);
            if (!tileCatalog.HasSameTiles(finalCatalog))
            {
                throw new InvalidDataException(
                    "The restriction-stage tile cohort changed before " +
                    "final validation.");
            }

            ValidateReadableTileTree(
                incomingDirectory,
                finalCatalog,
                cancellationToken,
                requireDerivedBuildId: true);
            cancellationToken.ThrowIfCancellationRequested();
            options.BeforeFinalCohortSealObserver?.Invoke(incomingDirectory);
            BoundedRestrictionTileCatalog publishCatalog =
                BoundedRestrictionTileCatalog.Build(
                    incomingDirectory,
                    budget.MaxTilesPerLevel);
            if (!finalCatalog.HasSameTiles(publishCatalog))
            {
                throw new InvalidDataException(
                    "The restriction-stage tile cohort changed at the " +
                    "publication boundary.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            string? destinationParent =
                Path.GetDirectoryName(fullDestinationDirectory);
            if (!string.IsNullOrEmpty(destinationParent))
            {
                Directory.CreateDirectory(destinationParent);
            }

            Directory.CreateDirectory(sealedDirectory);
            foreach (GraphId tileId in publishCatalog.EnumerateAll())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativePath = GraphTile.FileSuffix(tileId);
                string sourcePath = Path.Combine(
                    incomingDirectory,
                    relativePath);
                string destinationPath = Path.Combine(
                    sealedDirectory,
                    relativePath);
                string? tileParent = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(tileParent))
                {
                    Directory.CreateDirectory(tileParent);
                }

                File.Move(sourcePath, destinationPath);
            }

            BoundedRestrictionTileCatalog promotedCatalog =
                BoundedRestrictionTileCatalog.Build(
                    sealedDirectory,
                    budget.MaxTilesPerLevel);
            if (!publishCatalog.HasSameTiles(promotedCatalog))
            {
                throw new InvalidDataException(
                    "The restriction-stage promoted tile cohort does not " +
                    "match the sealed publication catalog.");
            }

            ValidateReadableTileTree(
                sealedDirectory,
                promotedCatalog,
                cancellationToken,
                requireDerivedBuildId: true);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(
                sealedDirectory,
                fullDestinationDirectory);
            destinationCreatedByOperation = true;
            receipt = new PooledRoadRestrictionStageReceipt(
                projectedForwardCount,
                projectedReverseCount,
                serializedForwardCount,
                serializedReverseCount)
            {
                SerializedCrossTileForwardCount =
                    serializedCrossTileForwardCount,
                MarkedCrossTileEdgeCount = markedCrossTileEdgeCount,
                MissingCrossTileDestinationCount =
                    missingCrossTileDestinationCount,
                SequenceMemoryBudgetBytes =
                    budget.SequenceMemoryBytes,
                ReaderCacheBudgetBytes =
                    budget.ReaderCacheBytes,
                BookkeepingMemoryBudgetBytes =
                    budget.BookkeepingMemoryBytes,
                MutationMemoryBudgetBytes =
                    budget.MutationMemoryBytes,
                PeakTileMutationAllocatedBytes =
                    peakTileMutationAllocatedBytes,
                CopyBufferBudgetBytes = budget.CopyBufferBytes,
                SourceManifestMemoryBudgetBytes = checked(
                    sourceManifest.RetainedMemoryBytes +
                    SourceHashBufferBytes),
                RestampHashMemoryBudgetBytes =
                    budget.RestampHashMemoryBytes,
                TilesetBuildId = tilesetBuildId,
                ValidationMemoryBudgetBytes = ValidationMemoryBytes,
                StagedScratchBytes = budget.SourceBytes,
                ProjectionScratchBudgetBytes =
                    budget.ProjectionScratchBytes,
                MutationPlanScratchBudgetBytes =
                    budget.MutationPlanScratchBytes,
                PeakMutationPlanMemoryBytes =
                    mutationPlan.Receipt.PeakAggregateMemoryBytes,
                PeakMutationPlanScratchBytes =
                    mutationPlan.Receipt.PeakAggregateScratchBytes,
                PeakAggregateStageMemoryBytes = checked(
                    budget.CopyBufferBytes +
                    ValidationMemoryBytes +
                    sourceManifest.RetainedMemoryBytes +
                    SourceHashBufferBytes +
                    budget.SequenceMemoryBytes +
                    budget.ReaderCacheBytes +
                    budget.BookkeepingMemoryBytes +
                    budget.RestampHashMemoryBytes +
                    traversalWorkspaceBytes +
                    mutationPlan.Receipt.PeakAggregateMemoryBytes),
                PeakAggregateStageScratchBytes = checked(
                    budget.SourceBytes +
                    budget.ProjectionScratchBytes +
                    mutationPlan.Receipt.PeakAggregateScratchBytes),
                TraversalWorkspaceReservedBytes = traversalWorkspaceBytes,
            };
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }
        finally
        {
            cleanupFailure =
                BoundedRoadTileWriter.ExecuteCleanupActions(
                    () => reader?.Clear(),
                    () => mutationPlan?.Dispose(),
                    () => planSink?.Dispose(),
                    () => restrictions?.Dispose(),
                    () =>
                    {
                        if (operationFailure is not null &&
                            destinationCreatedByOperation)
                        {
                            DeleteDirectoryIfPresent(
                                fullDestinationDirectory);
                        }
                    },
                    () => DeleteDirectoryIfPresent(
                        operationDirectory),
                    () => DeleteEmptyDirectoryIfCreated(
                        fullWorkingDirectory,
                        workingDirectoryExisted));
        }

        return ResolveStageOutcome(
            receipt,
            operationFailure,
            cleanupFailure);
    }

    internal static PooledRoadRestrictionStageReceipt ResolveStageOutcome(
        PooledRoadRestrictionStageReceipt? receipt,
        Exception? operationFailure,
        Exception? cleanupFailure)
    {
        if (operationFailure is not null)
        {
            if (cleanupFailure is not null)
            {
                operationFailure.Data[
                    "PooledRoadRestrictionStage.CleanupFailure"] =
                    cleanupFailure;
            }

            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }

        return receipt ??
            throw new InvalidOperationException(
                "The pooled restriction stage produced no receipt.");
    }

    private static GraphReader CreateBoundedReader(
        string tileDirectory,
        long cacheBudgetBytes) =>
        new(
            new GraphReader.Config
            {
                TileDir = tileDirectory,
                MaxCacheSize = cacheBudgetBytes,
                UseLruMemCache = true,
                LruMemCacheHardControl = true,
            });

    internal static void ValidateReadableTileTree(
        string tileDirectory,
        BoundedRestrictionTileCatalog tileCatalog,
        CancellationToken cancellationToken,
        bool requireDerivedBuildId = false)
    {
        byte[] headerBytes =
            GC.AllocateUninitializedArray<byte>(
                GraphTileHeader.HeaderSize);
        byte[] validationBuffer =
            GC.AllocateUninitializedArray<byte>(
                ValidationBufferBytes);
        Span<byte> digest = stackalloc byte[MD5.HashSizeInBytes];
        ulong checksumAccumulator = 0;
        ulong? datasetId = null;
        ushort? storedBuildId = null;
        int validatedTileCount = 0;

        foreach (GraphId tileId in tileCatalog.EnumerateAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string tilePath = Path.Combine(
                tileDirectory,
                GraphTile.FileSuffix(tileId.TileBase()));
            var tileFile = new FileInfo(tilePath);
            if (!tileFile.Exists ||
                tileFile.Length < GraphTileHeader.HeaderSize ||
                tileFile.Length > uint.MaxValue)
            {
                throw new InvalidDataException(
                    $"Restriction-stage tile {tileId} has an invalid " +
                    $"file length.");
            }

            using var handle = File.OpenHandle(
                tilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.RandomAccess);
            ReadExactly(handle, headerBytes);

            GraphTileHeader header =
                GraphTileHeader.FromBytes(headerBytes);
            if (header.Graphid().TileBase() != tileId.TileBase())
            {
                throw new InvalidDataException(
                    $"Restriction-stage tile {tileId} has a mismatched " +
                    $"header identity {header.Graphid()}.");
            }

            if (header.EndOffset() != tileFile.Length)
            {
                throw new InvalidDataException(
                    $"Restriction-stage tile {tileId} reports end offset " +
                    $"{header.EndOffset()} for {tileFile.Length} bytes.");
            }

            ValidateFixedSectionBoundary(tileId, header);
            using IncrementalHash bodyHash =
                IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            long bodyOffset = GraphTileHeader.HeaderSize;
            while (bodyOffset < tileFile.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = (int)Math.Min(
                    validationBuffer.Length,
                    tileFile.Length - bodyOffset);
                int read = RandomAccess.Read(
                    handle,
                    validationBuffer.AsSpan(0, requested),
                    bodyOffset);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "The graph tile ended before its declared body.");
                }

                bodyHash.AppendData(validationBuffer.AsSpan(0, read));
                bodyOffset = checked(bodyOffset + read);
            }

            if (!bodyHash.TryGetHashAndReset(
                    digest,
                    out int digestBytesWritten) ||
                digestBytesWritten != MD5.HashSizeInBytes)
            {
                throw new InvalidOperationException(
                    "The graph tile body checksum could not be finalized.");
            }

            ulong computedTileHash =
                GraphTileChecksum.FoldMd5Digest(digest);
            if (computedTileHash != header.TileChecksum())
            {
                throw new InvalidDataException(
                    $"Restriction-stage tile {tileId} checksum does " +
                    "not match its body.");
            }

            if (datasetId is null)
            {
                datasetId = header.DatasetId();
                storedBuildId = header.BuildId();
            }
            else if (datasetId.Value != header.DatasetId())
            {
                throw new InvalidDataException(
                    $"Restriction-stage tile {tileId} belongs to dataset " +
                    $"{header.DatasetId()}, not {datasetId.Value}.");
            }
            else if (storedBuildId != header.BuildId())
            {
                throw new InvalidDataException(
                    $"Restriction-stage tile {tileId} has build ID " +
                    $"{header.BuildId()}, not {storedBuildId}.");
            }

            checksumAccumulator = unchecked(
                checksumAccumulator +
                (computedTileHash & GraphTileHeader.TileHashMask));
            validatedTileCount = checked(validatedTileCount + 1);

            uint previousOffset = GraphTileHeader.HeaderSize;
            ValidateSectionOffset(
                tileId,
                "forward complex restrictions",
                header.ComplexRestrictionForwardOffset(),
                ref previousOffset,
                header.EndOffset());
            ValidateSectionOffset(
                tileId,
                "reverse complex restrictions",
                header.ComplexRestrictionReverseOffset(),
                ref previousOffset,
                header.EndOffset());
            ValidateSectionOffset(
                tileId,
                "edge info",
                header.EdgeinfoOffset(),
                ref previousOffset,
                header.EndOffset());
            ValidateSectionOffset(
                tileId,
                "text list",
                header.TextlistOffset(),
                ref previousOffset,
                header.EndOffset());
            ValidateSectionOffset(
                tileId,
                "lane connectivity",
                header.LaneConnectivityOffset(),
                ref previousOffset,
                header.EndOffset());
            if (header.PredictedspeedsCount() > 0)
            {
                ValidateSectionOffset(
                    tileId,
                    "predicted speeds",
                    header.PredictedspeedsOffset(),
                    ref previousOffset,
                    header.EndOffset());
            }
        }

        if (validatedTileCount != tileCatalog.TileCount)
        {
            throw new InvalidDataException(
                "The restriction-stage tile catalog changed during validation.");
        }

        if (requireDerivedBuildId && validatedTileCount > 0)
        {
            ushort computedBuildId = ComputeTilesetBuildId(
                checksumAccumulator);
            if (storedBuildId != computedBuildId)
            {
                throw new InvalidDataException(
                    $"Restriction-stage tileset build ID {storedBuildId} " +
                    $"does not match derived build ID {computedBuildId}.");
            }
        }
    }

    private static ushort ComputeTilesetBuildId(
        ulong checksumAccumulator) =>
        unchecked(
            (ushort)(
                checksumAccumulator ^
                (checksumAccumulator >> 16) ^
                (checksumAccumulator >> 32) ^
                (checksumAccumulator >> 48)));

    private static void ValidateFixedSectionBoundary(
        GraphId tileId,
        GraphTileHeader header)
    {
        long fixedSectionEnd = GraphTileHeader.HeaderSize;
        checked
        {
            fixedSectionEnd += (long)header.Nodecount() * NodeInfoSize;
            fixedSectionEnd +=
                (long)header.Transitioncount() * NodeTransitionSize;
            fixedSectionEnd +=
                (long)header.Directededgecount() * DirectedEdgeSize;
            if (header.HasExtDirectededge())
            {
                fixedSectionEnd +=
                    (long)header.Directededgecount() *
                    DirectedEdgeExtSize;
            }

            fixedSectionEnd +=
                (long)header.AccessRestrictionCount() *
                AccessRestrictionSize;
            fixedSectionEnd +=
                (long)header.Departurecount() *
                TransitDepartureSize;
            fixedSectionEnd +=
                (long)header.Stopcount() * TransitStopSize;
            fixedSectionEnd +=
                (long)header.Routecount() * TransitRouteSize;
            fixedSectionEnd +=
                (long)header.Schedulecount() * TransitScheduleSize;
            fixedSectionEnd +=
                (long)header.Transfercount() * TransitTransferSize;
            fixedSectionEnd +=
                (long)header.Signcount() * SignSize;
            fixedSectionEnd +=
                (long)header.TurnlaneCount() * TurnLanesSize;
            fixedSectionEnd +=
                (long)header.Admincount() * AdminSize;
        }

        if (fixedSectionEnd >
            header.ComplexRestrictionForwardOffset())
        {
            throw new InvalidDataException(
                $"Restriction-stage tile {tileId} fixed sections end " +
                $"at {fixedSectionEnd}, after the forward complex " +
                $"restriction offset " +
                $"{header.ComplexRestrictionForwardOffset()}.");
        }
    }

    private static void ReadExactly(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        Span<byte> destination)
    {
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int read = RandomAccess.Read(
                handle,
                destination[totalRead..],
                totalRead);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "The graph tile ended before its complete header.");
            }

            totalRead = checked(totalRead + read);
        }
    }

    private static void ValidateSectionOffset(
        GraphId tileId,
        string section,
        uint offset,
        ref uint previousOffset,
        uint endOffset)
    {
        if (offset < previousOffset || offset > endOffset)
        {
            throw new InvalidDataException(
                $"Restriction-stage tile {tileId} has invalid {section} " +
                $"offset {offset}; expected {previousOffset} through " +
                $"{endOffset}.");
        }

        previousOffset = offset;
    }

    private static StageBudget CreateBudget(
        SourceTileTreeManifest sourceManifest,
        PooledRoadRestrictionStageOptions options)
    {
        long sourceBytes = sourceManifest.TotalBytes;
        long largestTileBytes = Math.Max(
            MinimumReaderCacheBytes,
            sourceManifest.LargestTileBytes);
        int tileCount = sourceManifest.TileCount;

        long copyBufferBytes = Math.Clamp(
            options.MemoryBudgetBytes / 16,
            MinimumCopyBufferBytes,
            MaximumCopyBufferBytes);
        long fixedMemoryBytes = checked(
            copyBufferBytes +
            ValidationMemoryBytes +
            sourceManifest.RetainedMemoryBytes +
            SourceHashBufferBytes);
        if (options.MemoryBudgetBytes <=
            fixedMemoryBytes +
            MinimumReaderCacheBytes +
            MinimumBookkeepingMemoryBytes)
        {
            throw new InvalidOperationException(
                "The restriction stage memory budget cannot fit " +
                "bounded copy, validation, reader, and bookkeeping " +
                "state.");
        }

        long distributableMemoryBytes = checked(
            options.MemoryBudgetBytes - fixedMemoryBytes);
        long readerCacheBytes = Math.Max(
            MinimumReaderCacheBytes,
            largestTileBytes);
        long mutationMemoryBytes =
            options.MutationMemoryBudgetBytesOverride ??
            Math.Max(
                checked(
                    (largestTileBytes * 8) +
                    MinimumBookkeepingMemoryBytes),
                distributableMemoryBytes / 4);
        if (mutationMemoryBytes <= 0)
        {
            throw new InvalidOperationException(
                "The restriction stage mutation memory allowance must be " +
                "positive.");
        }
        long remainingAfterTileState = checked(
            distributableMemoryBytes -
            readerCacheBytes -
            mutationMemoryBytes);
        if (remainingAfterTileState <=
            MinimumBookkeepingMemoryBytes)
        {
            throw new InvalidOperationException(
                "The restriction stage memory budget cannot fit one " +
                "deserialized and serialized graph tile within its " +
                "bounded mutation allowance.");
        }

        long sequenceMemoryBytes = remainingAfterTileState / 2;
        long totalBookkeepingMemoryBytes = checked(
            remainingAfterTileState - sequenceMemoryBytes);
        long restampHashMemoryBytes =
            BoundedTilesetRestamper.GetHashReservationBytes(tileCount);
        long bookkeepingMemoryBytes = checked(
            totalBookkeepingMemoryBytes -
            restampHashMemoryBytes);
        if (bookkeepingMemoryBytes <
            MinimumBookkeepingMemoryBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The restriction stage memory budget cannot fit " +
                "projection storage, one graph tile, its exact tileset " +
                "hash reservation, and bounded bookkeeping.");
        }

        long availableScratchBytes = checked(
            options.ScratchDiskBudgetBytes - sourceBytes);
        long minimumProjectionScratchBytes = checked(
            (long)options.SegmentSizeBytes * 4);
        long minimumPlanScratchBytes = checked(
            (long)options.SegmentSizeBytes * 8);
        long minimumConcurrentScratchBytes = checked(
            minimumProjectionScratchBytes +
            minimumPlanScratchBytes);
        if (availableScratchBytes < minimumConcurrentScratchBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The restriction stage scratch budget cannot fit the " +
                "immutable tile clone plus disjoint projection and " +
                "mutation-plan stores.");
        }

        long projectionScratchBytes = Math.Max(
            minimumProjectionScratchBytes,
            availableScratchBytes / 3);
        long mutationPlanScratchBytes = checked(
            availableScratchBytes - projectionScratchBytes);
        if (mutationPlanScratchBytes < minimumPlanScratchBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The restriction stage mutation-plan scratch partition " +
                "cannot satisfy its bounded stores and sorting workspace.");
        }

        long catalogBytes = bookkeepingMemoryBytes / 4;
        int maxTilesPerLevel = checked(
            (int)Math.Min(
                int.MaxValue,
                catalogBytes /
                EstimatedTileCatalogEntryBytes));
        if (tileCount > maxTilesPerLevel)
        {
            throw new InvalidOperationException(
                "The restriction stage memory budget cannot fit its " +
                "bounded tile catalog.");
        }

        long deferredBytes = bookkeepingMemoryBytes / 2;
        long partOfBytes = checked(
            bookkeepingMemoryBytes -
            catalogBytes -
            deferredBytes);
        int maxDeferredRestrictions = checked(
            (int)Math.Min(
                int.MaxValue,
                deferredBytes /
                EstimatedDeferredRestrictionBytes));
        int maxPartOfRestrictionEdges = checked(
            (int)Math.Min(
                int.MaxValue,
                partOfBytes /
                EstimatedPartOfEdgeBytes));
        if (maxDeferredRestrictions < 1 ||
            maxPartOfRestrictionEdges < 1)
        {
            throw new InvalidOperationException(
                "The restriction stage memory budget cannot fit " +
                "bounded deferred restriction state.");
        }

        return new StageBudget(
            sourceBytes,
            sequenceMemoryBytes,
            projectionScratchBytes,
            mutationPlanScratchBytes,
            readerCacheBytes,
            bookkeepingMemoryBytes,
            restampHashMemoryBytes,
            mutationMemoryBytes,
            copyBufferBytes,
            maxTilesPerLevel,
            maxDeferredRestrictions,
            maxPartOfRestrictionEdges);
    }


    private static SourceTileTreeManifest BuildSourceManifest(
        string sourceDirectory,
        long memoryBudgetBytes,
        Action<string, long>? hashProgressObserver,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryBudgetBytes);
        string fullRoot = Path.GetFullPath(sourceDirectory);
        RejectReparsePoint(fullRoot);
        var pending = new Stack<string>();
        var files = new List<SourceTileTreeManifestEntry>();
        long retainedMemoryBytes = SourceManifestBaseMemoryBytes;
        ReserveManifestMemory(
            ref retainedMemoryBytes,
            fullRoot,
            memoryBudgetBytes);
        pending.Push(fullRoot);
        long totalBytes = 0;
        long largestTileBytes = 0;
        int tileCount = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            RejectReparsePoint(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                         directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectReparsePoint(entry);
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    ReserveManifestMemory(
                        ref retainedMemoryBytes,
                        entry,
                        memoryBudgetBytes);
                    pending.Push(entry);
                    continue;
                }

                string relativePath = Path.GetRelativePath(fullRoot, entry);
                ValidateManifestRelativePath(relativePath);
                long length = new FileInfo(entry).Length;
                string contentSha256 = ComputeFileSha256(
                    entry,
                    hashProgressObserver,
                    cancellationToken);
                ReserveManifestMemory(
                    ref retainedMemoryBytes,
                    relativePath,
                    memoryBudgetBytes);
                totalBytes = checked(totalBytes + length);
                if (Path.GetExtension(entry).Equals(
                        ".gph",
                        StringComparison.OrdinalIgnoreCase))
                {
                    tileCount = checked(tileCount + 1);
                    largestTileBytes = Math.Max(largestTileBytes, length);
                }

                files.Add(new SourceTileTreeManifestEntry(
                    relativePath,
                    length,
                    contentSha256));
            }
        }

        files.Sort(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(
                left.RelativePath,
                right.RelativePath));
        return new SourceTileTreeManifest(
            files,
            totalBytes,
            largestTileBytes,
            tileCount,
            retainedMemoryBytes);
    }

    private static long GetSourceManifestMemoryBudget(
        long stageMemoryBudgetBytes) =>
        Math.Min(
            checked(stageMemoryBudgetBytes - SourceHashBufferBytes),
            Math.Max(
                MinimumBookkeepingMemoryBytes,
                stageMemoryBudgetBytes / 8));

    private static void ReserveManifestMemory(
        ref long retainedMemoryBytes,
        string path,
        long memoryBudgetBytes)
    {
        retainedMemoryBytes = checked(
            retainedMemoryBytes +
            SourceManifestEntryMemoryBytes +
            (path.Length * sizeof(char)));
        if (retainedMemoryBytes > memoryBudgetBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The restriction-stage source manifest exceeds its " +
                "bounded memory partition.");
        }
    }

    private static string ComputeFileSha256(
        string path,
        Action<string, long>? progressObserver,
        CancellationToken cancellationToken)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            SourceHashBufferBytes,
            FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(
            SourceHashBufferBytes);
        long processedBytes = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(buffer);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer.AsSpan(0, read));
            processedBytes = checked(processedBytes + read);
            progressObserver?.Invoke(path, processedBytes);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task CopyDirectoryAsync(
        string sourceDirectory,
        string destinationDirectory,
        SourceTileTreeManifest manifest,
        long copyBufferBytes,
        CancellationToken cancellationToken)
    {
        int bufferLength = checked((int)copyBufferBytes);
        byte[] buffer =
            GC.AllocateUninitializedArray<byte>(bufferLength);
        long remainingBytes = manifest.TotalBytes;

        foreach (SourceTileTreeManifestEntry entry in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateManifestRelativePath(entry.RelativePath);
            string sourcePath = Path.GetFullPath(Path.Combine(
                sourceDirectory,
                entry.RelativePath));
            string destinationPath = Path.GetFullPath(Path.Combine(
                destinationDirectory,
                entry.RelativePath));
            EnsurePathWithinRoot(sourceDirectory, sourcePath);
            EnsurePathWithinRoot(destinationDirectory, destinationPath);
            ValidateSourcePathComponents(
                sourceDirectory,
                entry.RelativePath);

            string? destinationParent =
                Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationParent))
            {
                Directory.CreateDirectory(destinationParent);
            }

            using var source = File.OpenHandle(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan);
            long openedLength = RandomAccess.GetLength(source);
            if (openedLength != entry.Length ||
                entry.Length > remainingBytes)
            {
                throw new InvalidDataException(
                    $"Restriction-stage source file '{entry.RelativePath}' " +
                    "changed after the immutable manifest was created.");
            }

            using var destination = File.OpenHandle(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan);

            using var contentHash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            while (offset < entry.Length)
            {
                int requested = checked((int)Math.Min(
                    buffer.LongLength,
                    entry.Length - offset));
                int read = await RandomAccess.ReadAsync(
                        source,
                        buffer.AsMemory(0, requested),
                        offset,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        $"Restriction-stage source file " +
                        $"'{entry.RelativePath}' was truncated during copy.");
                }

                contentHash.AppendData(buffer.AsSpan(0, read));
                await RandomAccess.WriteAsync(
                        destination,
                        buffer.AsMemory(0, read),
                        offset,
                        cancellationToken)
                    .ConfigureAwait(false);
                offset = checked(offset + read);
            }

            if (RandomAccess.GetLength(source) != entry.Length ||
                !Convert.ToHexString(contentHash.GetHashAndReset()).Equals(
                    entry.ContentSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Restriction-stage source file '{entry.RelativePath}' " +
                    "changed during copy.");
            }

            RandomAccess.FlushToDisk(destination);
            if (RandomAccess.GetLength(destination) != entry.Length)
            {
                throw new IOException(
                    $"Restriction-stage clone '{entry.RelativePath}' has an " +
                    "unexpected length.");
            }

            remainingBytes = checked(remainingBytes - entry.Length);
        }

        if (remainingBytes != 0)
        {
            throw new InvalidDataException(
                "The restriction-stage source manifest byte budget did not " +
                "reconcile after cloning.");
        }
    }

    private static void ValidateSourceManifestPaths(
        string sourceDirectory,
        SourceTileTreeManifest manifest,
        Action<string>? pathValidatedObserver,
        Action<string, int>? pathLookupObserver,
        CancellationToken cancellationToken)
    {
        string fullRoot = Path.GetFullPath(sourceDirectory);
        RejectReparsePoint(fullRoot);
        int observedFiles = 0;
        var pending = new Stack<string>();
        pending.Push(fullRoot);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            RejectReparsePoint(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                         directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectReparsePoint(entry);
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                string relativePath = Path.GetRelativePath(fullRoot, entry);
                ValidateManifestRelativePath(relativePath);
                bool expected = FindManifestEntry(
                    manifest.Files,
                    relativePath,
                    out int comparisonCount);
                pathLookupObserver?.Invoke(relativePath, comparisonCount);
                if (!expected)
                {
                    throw new InvalidDataException(
                        "The restriction-stage source tree gained or " +
                        "renamed a file while its immutable clone was " +
                        "being created.");
                }

                observedFiles++;
                pathValidatedObserver?.Invoke(relativePath);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (observedFiles != manifest.Files.Count)
        {
            throw new InvalidDataException(
                "The restriction-stage source tree lost a file while its " +
                "immutable clone was being created.");
        }
    }

    private static bool FindManifestEntry(
        IReadOnlyList<SourceTileTreeManifestEntry> files,
        string relativePath,
        out int comparisonCount)
    {
        int low = 0;
        int high = files.Count - 1;
        comparisonCount = 0;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            int comparison = StringComparer.OrdinalIgnoreCase.Compare(
                files[middle].RelativePath,
                relativePath);
            comparisonCount++;
            if (comparison == 0)
            {
                return true;
            }

            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return false;
    }


    private static void ValidateSourcePathComponents(
        string sourceRoot,
        string relativePath)
    {
        RejectReparsePoint(sourceRoot);
        string? relativeDirectory = Path.GetDirectoryName(relativePath);
        if (string.IsNullOrEmpty(relativeDirectory))
        {
            RejectReparsePoint(Path.Combine(sourceRoot, relativePath));
            return;
        }

        string current = sourceRoot;
        foreach (string component in relativeDirectory.Split(
                     Path.DirectorySeparatorChar,
                     Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            RejectReparsePoint(current);
        }

        RejectReparsePoint(Path.Combine(sourceRoot, relativePath));
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Restriction-stage source path '{path}' is a reparse point.");
        }
    }

    private static void ValidateManifestRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) ||
            relativePath.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Restriction-stage source path '{relativePath}' is unsafe.");
        }
    }

    private static void EnsurePathWithinRoot(
        string root,
        string candidate)
    {
        string fullRoot = Path.GetFullPath(root)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(
                fullRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Restriction-stage path '{candidate}' escapes " +
                $"'{fullRoot}'.");
        }
    }

    private static void ValidateOptions(
        PooledRoadRestrictionStageOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            options.WorkingDirectory);
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

    private static void ValidateIndependentDirectories(
        string sourceDirectory,
        string destinationDirectory,
        string workingDirectory)
    {
        if (PathEquals(sourceDirectory, destinationDirectory) ||
            PathEquals(sourceDirectory, workingDirectory) ||
            PathEquals(destinationDirectory, workingDirectory) ||
            IsDescendant(sourceDirectory, destinationDirectory) ||
            IsDescendant(destinationDirectory, sourceDirectory) ||
            IsDescendant(sourceDirectory, workingDirectory) ||
            IsDescendant(destinationDirectory, workingDirectory))
        {
            throw new ArgumentException(
                "Source, destination, and working directories must be " +
                "independent.");
        }
    }

    private static bool PathEquals(
        string left,
        string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsDescendant(
        string parent,
        string candidate)
    {
        string relative = Path.GetRelativePath(parent, candidate);
        return !relative.Equals(".", StringComparison.Ordinal) &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private static void DeleteEmptyDirectoryIfCreated(
        string path,
        bool existedBeforeOperation)
    {
        if (existedBeforeOperation || !Directory.Exists(path))
        {
            return;
        }

        if (!Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record SourceTileTreeManifestEntry(
        string RelativePath,
        long Length,
        string ContentSha256);

    private sealed record SourceTileTreeManifest(
        IReadOnlyList<SourceTileTreeManifestEntry> Files,
        long TotalBytes,
        long LargestTileBytes,
        int TileCount,
        long RetainedMemoryBytes);


    private sealed record StageBudget(
        long SourceBytes,
        long SequenceMemoryBytes,
        long ProjectionScratchBytes,
        long MutationPlanScratchBytes,
        long ReaderCacheBytes,
        long BookkeepingMemoryBytes,
        long RestampHashMemoryBytes,
        long MutationMemoryBytes,
        long CopyBufferBytes,
        int MaxTilesPerLevel,
        int MaxDeferredRestrictions,
        int MaxPartOfRestrictionEdges);
}
