namespace SharpNinja.Valhalla.Generation.BikeShare;

public interface IBikeShareTileBuilder
{
    ValueTask<BikeShareTileBuildResult> BuildAsync(
        BikeShareTileBuildRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record BikeShareTileBuildRequest(
    string GraphTileDirectory,
    IReadOnlyList<string> OsmPbfPaths,
    string WorkingDirectory,
    string OutputDirectory,
    BikeShareTileBuildOptions Options);

public sealed record BikeShareTileBuildOptions(
    int MaxDegreeOfParallelism,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    bool DeterministicOutput);

public sealed record BikeShareTileBuildResult(
    string OutputDirectory,
    int InputTileCount,
    int StationCount,
    int AddedNodeCount,
    int AddedDirectedEdgeCount,
    long BytesRead,
    long BytesWritten,
    int MaximumConcurrency,
    IReadOnlyDictionary<string, string> OutputSha256);

public enum BikeShareTileBuildFailureCode
{
    InvalidConfiguration = 0,
    MissingInput = 1,
    UnsafePath = 2,
    MalformedFeed = 3,
    NoStations = 4,
    GraphTileNotFound = 5,
    ProjectionFailed = 6,
    ResourceExhausted = 7,
    CorruptGraph = 8,
    PublicationFailed = 9,
}

public sealed class BikeShareTileBuildException : Exception
{
    public BikeShareTileBuildException(
        BikeShareTileBuildFailureCode code,
        string message)
        : base(message)
    {
        Code = code;
    }

    public BikeShareTileBuildException(
        BikeShareTileBuildFailureCode code,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public BikeShareTileBuildFailureCode Code { get; }
}
