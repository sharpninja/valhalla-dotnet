namespace SharpNinja.Valhalla.Generation;

public enum ValhallaGenerationStage
{
    ValidateRequest = 0,
    BuildAdmins = 10,
    BuildTimeZones = 20,
    BuildElevationIndex = 30,
    IngestOsm = 40,
    BuildWays = 50,
    BuildNodes = 60,
    BuildEdges = 70,
    EnhanceGraph = 80,
    BuildRestrictions = 90,
    BuildHierarchy = 100,
    BuildShortcuts = 110,
    BuildTransit = 120,
    BuildBikeShare = 130,
    ApplyPredictedSpeeds = 140,
    ValidateGraph = 150,
    BuildTileExtract = 160,
    Publish = 170,
}

public enum ValhallaGenerationFailureCode
{
    Unknown = 0,
    Configuration = 1,
    InvalidInput = 2,
    IncompatibleCheckpoint = 3,
    Validation = 4,
    ResourceExhaustion = 5,
    Canceled = 6,
    InputOutput = 7,
    UpstreamParity = 8,
}

public sealed record ValhallaGenerationFailure(
    ValhallaGenerationFailureCode Code,
    string Message,
    ValhallaGenerationStage? Stage = null);

public sealed record ValhallaGenerationFrontierMetrics(
    long CanonicalNodesRead,
    long WayNodeOccurrencesProcessed,
    long GraphAnchorsCreated,
    long SecondaryNodesProcessed,
    long SecondarySlotsReleased,
    long TotalSlotRents,
    long SlotReuseCount,
    int PeakLiveSlots,
    int TotalSlabsRented,
    long PeakSlabBytes,
    int MaximumUnresolvedPathAnchors,
    long IncidenceStoreBytes,
    long NodeStoreBytes,
    long ShapeStoreBytes,
    long EdgeStoreBytes,
    int SelectedDegreeOfParallelism,
    long PerWorkerMemoryReservationBytes,
    long MappedStorageHighWaterMarkBytes,
    long StaleHandleRejections);

public sealed record ValhallaGenerationStageReceipt(
    ValhallaGenerationStage Stage,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    string InputIdentity,
    string OutputIdentity,
    long RecordsProcessed,
    long BytesRead,
    long BytesWritten,
    int MaximumConcurrency,
    long AllocatedBytes,
    long PeakWorkingSetBytes,
    long ScratchDiskHighWaterMarkBytes,
    string CheckpointIdentity,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ValhallaGenerationFailure> Failures,
    IReadOnlyDictionary<string, string> OutputHashes)
{
    public TimeSpan Duration => EndedAtUtc - StartedAtUtc;

    public ValhallaGenerationFrontierMetrics? FrontierMetrics { get; init; }
}
