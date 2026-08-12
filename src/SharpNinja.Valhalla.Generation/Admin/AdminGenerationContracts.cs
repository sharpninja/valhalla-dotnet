using SharpNinja.Valhalla.Generation.Pbf;
using SharpNinja.Valhalla.Generation.Storage;

namespace SharpNinja.Valhalla.Generation.Admin;

public interface IAdminDatabaseBuilder
{
    ValueTask<AdminDatabaseBuildResult> BuildAsync(
        AdminDatabaseBuildRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AdminDatabaseBuildRequest(
    IReadOnlyList<string> OsmPbfPaths,
    string WorkingDirectory,
    string OutputPath,
    IntermediateStorageMode StorageMode,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes);

public sealed record AdminDatabaseBuildResult(
    string DatabasePath,
    int AdminCount,
    int AccessOverrideCount,
    int SpatialIndexCount,
    string Sha256,
    long BytesWritten,
    long ScratchDiskHighWaterBytes,
    StreamingOsmPbfReadMetrics PbfMetrics,
    IReadOnlyList<AdminDatabaseDiagnostic> Diagnostics);

public sealed record AdminDatabaseDiagnostic(
    AdminDatabaseDiagnosticCode Code,
    string Message,
    ulong? OsmRelationId = null);

public enum AdminDatabaseDiagnosticCode
{
    IncompleteBoundary = 0,
    DegenerateBoundary = 1,
    UnsupportedBoundary = 2,
}

public enum AdminDatabaseFailureCode
{
    InvalidConfiguration = 0,
    ScratchDiskBudgetExceeded = 1,
    InvalidBoundaryGeometry = 2,
    DatabaseWriteFailed = 3,
}

public sealed class AdminDatabaseBuildException : IOException
{
    public AdminDatabaseBuildException(
        AdminDatabaseFailureCode failureCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureCode = failureCode;
    }

    public AdminDatabaseFailureCode FailureCode { get; }
}
