namespace SharpNinja.Valhalla.Generation.Elevation;

public interface IElevationDatasetBuilder
{
    ValueTask<ElevationDatasetBuildResult> BuildAsync(
        ElevationDatasetBuildRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ElevationDatasetBuildRequest(
    string GraphTileDirectory,
    string ElevationDirectory,
    int MaxDegreeOfParallelism,
    long ScratchDiskBudgetBytes,
    bool DeterministicOutput);

public sealed record ElevationDatasetBuildResult(
    string GraphTileDirectory,
    int TileCount,
    int NodeCount,
    int UniqueEdgeInfoCount,
    int EncodedElevationCount,
    long BytesWritten,
    long ScratchDiskHighWaterBytes,
    int PeakConcurrency,
    string OutputTreeSha256,
    IReadOnlyList<ElevationDatasetDiagnostic> Diagnostics);

public sealed record ElevationDatasetDiagnostic(
    ElevationDatasetDiagnosticCode Code,
    string Message,
    string? SourcePath = null,
    ulong? TileId = null);

public enum ElevationDatasetDiagnosticCode
{
    MissingElevationTile = 0,
    NoDataSample = 1,
    ExcessiveElevationDifference = 2,
}

public enum ElevationDatasetFailureCode
{
    InvalidConfiguration = 0,
    InvalidElevationTile = 1,
    ScratchDiskBudgetExceeded = 2,
    InvalidGraphTile = 3,
    GraphTileWriteFailed = 4,
}

public sealed class ElevationDatasetBuildException : IOException
{
    public ElevationDatasetBuildException(
        ElevationDatasetFailureCode failureCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureCode = failureCode;
    }

    public ElevationDatasetFailureCode FailureCode { get; }
}
