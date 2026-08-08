namespace SharpNinja.Valhalla.Generation;

public interface IValhallaGenerationBuilder
{
    ValueTask<ValhallaGenerationBuildResult> BuildAsync(
        ValhallaGenerationBuildRequest request,
        IProgress<ValhallaGenerationBuildProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record ValhallaGenerationBuildRequest(
    IReadOnlyList<string> OsmPbfPaths,
    ValhallaGenerationInputSet Inputs,
    string WorkingDirectory,
    string OutputDirectory,
    ValhallaGenerationBuildOptions Options);

public sealed record ValhallaGenerationInputSet(
    string? AdminDatabasePath,
    string? TimeZoneDatabasePath,
    string? ElevationDirectory,
    IReadOnlyList<string> TransitFeedPaths,
    IReadOnlyList<string> BikeShareFeedPaths,
    string? HistoricalSpeedDataPath)
{
    public static ValhallaGenerationInputSet Empty { get; } =
        new(null, null, null, [], [], null);
}

public sealed record ValhallaGenerationBuildOptions(
    ValhallaGenerationProfile Profile,
    IntermediateStorageMode StorageMode,
    ResumePolicy ResumePolicy,
    int MaxDegreeOfParallelism,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    uint DatasetId,
    ulong BuildId,
    bool DeterministicOutput);

public enum ValhallaGenerationProfile
{
    Full = 0,
    RoadOnly = 1,
    Truck = 2,
    LegacyEmbedded = 3,
}

public enum IntermediateStorageMode
{
    Auto = 0,
    Memory = 1,
    MemoryMapped = 2,
}

public enum ResumePolicy
{
    Disabled = 0,
    ResumeIfCompatible = 1,
    RequireCompatible = 2,
}

public sealed record ValhallaGenerationBuildProgress(
    ValhallaGenerationStage Stage,
    int CompletedStageCount,
    int TotalStageCount,
    string Message);

public sealed record ValhallaGenerationBuildResult(
    bool Success,
    string? PublishedDirectory,
    ValhallaGenerationManifest? Manifest,
    IReadOnlyList<ValhallaGenerationStageReceipt> StageReceipts,
    ValhallaGenerationFailure? Failure);

public sealed record ValhallaGenerationManifest(
    int SchemaVersion,
    string GenerationId,
    string RequestIdentity,
    string UpstreamCompatibilityVersion,
    DateTimeOffset CreatedAtUtc,
    string PublishedDirectory,
    string OutputTreeSha256,
    IReadOnlyList<ValhallaGenerationStageReceipt> StageReceipts);

public sealed record ValhallaGenerationCheckpoint(
    int SchemaVersion,
    string RequestIdentity,
    string UpstreamCompatibilityVersion,
    IReadOnlyList<ValhallaGenerationStage> CompletedStages,
    IReadOnlyDictionary<string, string> StageOutputHashes);

public sealed record ValhallaGenerationStageResult(
    string OutputIdentity,
    long RecordsProcessed,
    long BytesRead,
    long BytesWritten,
    long AllocatedBytes,
    long PeakWorkingSetBytes,
    long ScratchDiskHighWaterMarkBytes,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ValhallaGenerationFailure> Failures,
    IReadOnlyDictionary<string, string> OutputHashes)
{
    public static ValhallaGenerationStageResult Empty(string outputIdentity) =>
        new(outputIdentity, 0, 0, 0, 0, 0, 0, [], [], new Dictionary<string, string>());
}

public interface IValhallaGenerationStageExecutor
{
    ValhallaGenerationStage Stage { get; }

    ValueTask<ValhallaGenerationStageResult> ExecuteAsync(
        ValhallaGenerationStageContext context,
        CancellationToken cancellationToken);
}

public sealed record ValhallaGenerationValidationResult(
    bool IsValid,
    IReadOnlyList<ValhallaGenerationFailure> Failures)
{
    public static ValhallaGenerationValidationResult Valid { get; } = new(true, []);
}

public interface IValhallaGenerationValidator
{
    ValueTask<ValhallaGenerationValidationResult> ValidateAsync(
        ValhallaGenerationStageContext context,
        CancellationToken cancellationToken);
}

public interface IValhallaGenerationResourceBudget
{
    IDisposable ReserveMemory(long bytes);

    IDisposable ReserveScratchDisk(long bytes);

    ValueTask<IAsyncDisposable> AcquireWorkerAsync(CancellationToken cancellationToken);
}

public sealed class ValhallaGenerationStageContext
{
    public ValhallaGenerationStageContext(
        ValhallaGenerationBuildRequest request,
        string requestIdentity,
        string stagingDirectory,
        IValhallaGenerationResourceBudget resources)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        RequestIdentity = requestIdentity ??
            throw new ArgumentNullException(nameof(requestIdentity));
        StagingDirectory = stagingDirectory ??
            throw new ArgumentNullException(nameof(stagingDirectory));
        Resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    public ValhallaGenerationBuildRequest Request { get; }

    public string RequestIdentity { get; }

    public string StagingDirectory { get; }

    public IValhallaGenerationResourceBudget Resources { get; }
}

public sealed class ValhallaGenerationBuilder : IValhallaGenerationBuilder
{
    public const string UpstreamCompatibilityVersion = "3.8.3+a60c7cb";

    public ValhallaGenerationBuilder(
        IEnumerable<IValhallaGenerationStageExecutor> stages,
        IValhallaGenerationValidator validator)
    {
        ArgumentNullException.ThrowIfNull(stages);
        Validator = validator ?? throw new ArgumentNullException(nameof(validator));
        Stages = stages.ToArray();
    }

    internal IReadOnlyList<IValhallaGenerationStageExecutor> Stages { get; }

    internal IValhallaGenerationValidator Validator { get; }

    public ValueTask<ValhallaGenerationBuildResult> BuildAsync(
        ValhallaGenerationBuildRequest request,
        IProgress<ValhallaGenerationBuildProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ValhallaGenerationLifecycleRunner.RunAsync(
            request,
            Stages,
            Validator,
            progress,
            cancellationToken);
}
