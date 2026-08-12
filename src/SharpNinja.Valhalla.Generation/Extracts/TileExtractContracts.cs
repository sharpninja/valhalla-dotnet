namespace SharpNinja.Valhalla.Generation.Extracts;

public interface ITileExtractBuilder
{
    ValueTask<TileExtractBuildResult> BuildAsync(
        TileExtractBuildRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record TileExtractBuildRequest(
    string GraphTileDirectory,
    string OutputPath,
    string RegionId,
    uint DatasetId,
    ulong BuildId,
    bool DeterministicOutput);

public sealed record TileExtractBuildResult(
    string OutputPath,
    string RegionId,
    int TileCount,
    long ByteLength,
    string ArchiveSha256,
    string ManifestSha256);

public sealed record TileExtractTileReceipt(
    string Path,
    long ByteLength,
    string Sha256);

public enum TileExtractFailureCode
{
    InvalidConfiguration = 0,
    OutputAlreadyExists = 1,
    InvalidGraphTile = 2,
    UnsafePath = 3,
    WriteFailed = 4,
}

public sealed class TileExtractBuildException : Exception
{
    public TileExtractBuildException(
        TileExtractFailureCode code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public TileExtractFailureCode Code { get; }
}
