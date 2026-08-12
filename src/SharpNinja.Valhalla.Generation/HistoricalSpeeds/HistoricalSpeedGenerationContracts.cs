namespace SharpNinja.Valhalla.Generation.HistoricalSpeeds;

/// <summary>
/// Applies upstream-compatible historical and predicted speed records to staged Valhalla graph
/// tiles.
/// </summary>
public interface IHistoricalSpeedDatasetBuilder
{
    ValueTask<HistoricalSpeedDatasetBuildResult> BuildAsync(
        HistoricalSpeedDatasetBuildRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Configures one bounded historical-speed ingestion pass over a staged graph generation.
/// </summary>
public sealed record HistoricalSpeedDatasetBuildRequest(
    string GraphTileDirectory,
    string HistoricalSpeedDirectory,
    int MaxDegreeOfParallelism,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    bool DeterministicOutput);

/// <summary>
/// Secret-free deterministic evidence produced by one historical-speed ingestion pass.
/// </summary>
public sealed record HistoricalSpeedDatasetBuildResult(
    string GraphTileDirectory,
    int TileCount,
    int UpdatedEdgeCount,
    int PredictedProfileCount,
    int FreeFlowSpeedCount,
    int ConstrainedFlowSpeedCount,
    long BytesRead,
    long BytesWritten,
    long ScratchDiskHighWaterBytes,
    int PeakConcurrency,
    string OutputTreeSha256,
    IReadOnlyDictionary<string, string> TileSha256);

public enum HistoricalSpeedDatasetFailureCode
{
    InvalidConfiguration = 0,
    InvalidTrafficRecord = 1,
    DuplicateGraphId = 2,
    TileIdentityMismatch = 3,
    GraphTileNotFound = 4,
    EdgeNotFound = 5,
    MemoryBudgetExceeded = 6,
    ScratchDiskBudgetExceeded = 7,
    GraphTileReadFailed = 8,
    GraphTileWriteFailed = 9,
}

/// <summary>
/// Typed, secret-safe failure emitted by managed historical-speed ingestion.
/// </summary>
public sealed class HistoricalSpeedDatasetBuildException : IOException
{
    public HistoricalSpeedDatasetBuildException(
        HistoricalSpeedDatasetFailureCode failureCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureCode = failureCode;
    }

    public HistoricalSpeedDatasetFailureCode FailureCode { get; }
}
