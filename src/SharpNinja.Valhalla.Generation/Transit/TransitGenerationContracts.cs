namespace SharpNinja.Valhalla.Generation.Transit;

/// <summary>Builds Valhalla transit graph tiles from one or more GTFS feeds.</summary>
public interface ITransitTileBuilder
{
    ValueTask<TransitTileBuildResult> BuildAsync(
        TransitTileBuildRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Inputs and resource locations for one deterministic transit build.</summary>
public sealed record TransitTileBuildRequest(
    IReadOnlyList<string> FeedPaths,
    string WorkingDirectory,
    string OutputDirectory,
    string? TimeZoneDatabasePath,
    TransitTileBuildOptions Options);

/// <summary>Resource and identity controls for transit generation.</summary>
public sealed record TransitTileBuildOptions(
    int MaxDegreeOfParallelism,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    DateOnly BuildDate,
    uint DatasetId,
    ulong BuildId,
    bool DeterministicOutput);

/// <summary>Result and semantic counts from a completed transit build.</summary>
public sealed record TransitTileBuildResult(
    string OutputDirectory,
    int FeedCount,
    int TileCount,
    int NodeCount,
    int DirectedEdgeCount,
    int StopCount,
    int RouteCount,
    int DepartureCount,
    int ScheduleCount,
    int TransferCount,
    long BytesWritten,
    IReadOnlyDictionary<string, string> OutputSha256,
    IReadOnlyList<string> Warnings);

/// <summary>Stable typed failures for transit feed ingestion and publication.</summary>
public enum TransitTileBuildFailureCode
{
    InvalidConfiguration,
    UnsafePath,
    MissingRequiredFile,
    InvalidCsv,
    InvalidValue,
    ReferentialIntegrity,
    ResourceExhausted,
    UnsupportedFeed,
    OutputValidationFailed,
}

/// <summary>Typed transit generation failure.</summary>
public sealed class TransitTileBuildException : Exception
{
    public TransitTileBuildException(
        TransitTileBuildFailureCode code,
        string message)
        : base(message)
    {
        Code = code;
    }

    public TransitTileBuildException(
        TransitTileBuildFailureCode code,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public TransitTileBuildFailureCode Code { get; }
}
