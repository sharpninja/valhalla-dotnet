namespace SharpNinja.Valhalla.Generation.TimeZones;

public interface ITimeZoneDatabaseBuilder
{
    ValueTask<TimeZoneDatabaseBuildResult> BuildAsync(
        TimeZoneDatabaseBuildRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record TimeZoneDatabaseBuildRequest(
    string SourceShapefilePath,
    string SourceVersion,
    string WorkingDirectory,
    string OutputPath,
    long ScratchDiskBudgetBytes);

public sealed record TimeZoneDatabaseBuildResult(
    string DatabasePath,
    string SourceVersion,
    int TimeZoneCount,
    int SpatialIndexCount,
    string Sha256,
    long BytesWritten,
    long ScratchDiskHighWaterBytes,
    IReadOnlyList<TimeZoneDatabaseDiagnostic> Diagnostics);

public sealed record TimeZoneDatabaseDiagnostic(
    TimeZoneDatabaseDiagnosticCode Code,
    string Message,
    int? SourceRecordNumber = null,
    string? TimeZoneId = null);

public enum TimeZoneDatabaseDiagnosticCode
{
    NullShape = 0,
    DegenerateBoundary = 1,
    UnsupportedBoundary = 2,
}

public enum TimeZoneDatabaseFailureCode
{
    InvalidConfiguration = 0,
    UnsupportedProjection = 1,
    InvalidShapefile = 2,
    ScratchDiskBudgetExceeded = 3,
    InvalidBoundaryGeometry = 4,
    DatabaseWriteFailed = 5,
}

public sealed class TimeZoneDatabaseBuildException : IOException
{
    public TimeZoneDatabaseBuildException(
        TimeZoneDatabaseFailureCode failureCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureCode = failureCode;
    }

    public TimeZoneDatabaseFailureCode FailureCode { get; }
}
