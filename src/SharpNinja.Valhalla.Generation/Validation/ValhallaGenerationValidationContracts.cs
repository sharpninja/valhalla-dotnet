namespace SharpNinja.Valhalla.Generation.Validation;

/// <summary>
/// Deterministic aggregate statistics captured from one validated graph generation.
/// </summary>
public sealed record ValhallaGenerationGraphStatistics(
    int TileCount,
    long TileBytes,
    long NodeCount,
    long DirectedEdgeCount,
    long TransitionCount,
    long PredictedSpeedCount,
    long TransitDepartureCount,
    long TransitStopCount,
    long TransitRouteCount,
    long TransitScheduleCount,
    long TransitTransferCount,
    long SignCount,
    long AccessRestrictionCount,
    long AdminCount,
    IReadOnlyDictionary<byte, int> TilesByLevel,
    IReadOnlyDictionary<byte, uint> PossibleDuplicateEdgesByLevel,
    IReadOnlyDictionary<byte, ValhallaGenerationDensityStatistics> DensityByLevel);

/// <summary>
/// Distribution summary for the road-density values produced by the graph validator.
/// </summary>
public sealed record ValhallaGenerationDensityStatistics(
    int SampleCount,
    double Minimum,
    double Maximum,
    double Average);

/// <summary>
/// Immutable, secret-free evidence emitted after graph mutation and structural validation complete.
/// </summary>
public sealed record ValhallaGenerationValidationReceipt(
    int SchemaVersion,
    string UpstreamCompatibilityVersion,
    string RequestIdentity,
    ulong DatasetId,
    ushort BuildId,
    string OutputTreeSha256,
    ValhallaGenerationGraphStatistics Statistics,
    IReadOnlyDictionary<string, string> TileSha256);
