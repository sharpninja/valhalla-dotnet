namespace SharpNinja.Valhalla.Traffic.Tiles;

public enum TrafficDirection
{
    Unknown = 0,
    Forward = 1,
    Reverse = 2,
}

/// <summary>
/// References a directed edge. Supply GraphDirectedEdgeId whenever available. When it is omitted,
/// TileId must be the packed Valhalla GraphId tile-base value, not an unpacked numeric tile index.
/// </summary>
public sealed record ValhallaTrafficEdgeReference(
    ulong TileId,
    uint DirectedEdgeIndex,
    ulong? GraphDirectedEdgeId = null)
{
    public ulong CanonicalDirectedEdgeId =>
        GraphDirectedEdgeId ?? ValhallaTrafficEdgeIdentity.Create(TileId, DirectedEdgeIndex);
}

public static class ValhallaTrafficEdgeIdentity
{
    private const ulong TileBaseMask = 0x1FFFFFFUL;

    public static ulong Create(ulong tileId, uint directedEdgeIndex)
        => (tileId & TileBaseMask) | ((ulong)directedEdgeIndex << 25);
}

public sealed record TrafficEdgeMatchCandidate(
    ValhallaTrafficEdgeReference Edge,
    TrafficDirection Direction,
    double DistanceMeters,
    bool DirectionResolved);

public sealed record ValhallaGraphTrafficContext(string GraphSignature, string? GraphTileDirectory = null)
{
    public string GraphSignature { get; } = string.IsNullOrWhiteSpace(GraphSignature)
        ? throw new ArgumentException("A graph signature is required.", nameof(GraphSignature))
        : GraphSignature;

    public string? GraphTileDirectory { get; } = string.IsNullOrWhiteSpace(GraphTileDirectory)
        ? null
        : Path.GetFullPath(GraphTileDirectory);
}

/// <summary>
/// Provider traffic projected onto one directed edge. GraphDirectedEdgeId is the canonical route
/// join key; the TileId fallback assumes a packed Valhalla GraphId tile-base value.
/// </summary>
public sealed record ValhallaTrafficEdgeUpdate(
    ulong TileId,
    uint DirectedEdgeIndex,
    TrafficDirection Direction,
    double? CurrentSpeedKph,
    double? FreeFlowSpeedKph,
    int? DelaySeconds,
    bool Closed,
    bool HasIncident,
    bool DirectionResolved,
    double Confidence,
    string SourceEventId,
    string ProviderId,
    ulong? GraphDirectedEdgeId = null)
{
    public ulong CanonicalDirectedEdgeId =>
        GraphDirectedEdgeId ?? ValhallaTrafficEdgeIdentity.Create(TileId, DirectedEdgeIndex);
}

public interface IValhallaTrafficSpatialIndex
{
    ValueTask<IReadOnlyList<TrafficEdgeMatchCandidate>> MatchAsync(
        TrafficGeometry geometry,
        ValhallaGraphTrafficContext context,
        CancellationToken cancellationToken);
}

public interface ITrafficEdgeMatcher
{
    Task<IReadOnlyList<ValhallaTrafficEdgeUpdate>> MatchAsync(
        NormalizedTrafficEvent trafficEvent,
        ValhallaGraphTrafficContext context,
        CancellationToken cancellationToken = default);
}

public interface IValhallaTrafficTileWriter
{
    Task<ValhallaTrafficWriteResult> WriteAsync(
        IReadOnlyList<ValhallaTrafficEdgeUpdate> updates,
        ValhallaTrafficWriteOptions options,
        CancellationToken cancellationToken);
}

/// <summary>
/// Publishes the enabled and closure-only views through one atomic current-set pointer.
/// </summary>
public interface IValhallaTrafficSnapshotPairWriter : IValhallaTrafficTileWriter
{
    Task<ValhallaTrafficSnapshotPairWriteResult> WritePairAsync(
        IReadOnlyList<ValhallaTrafficEdgeUpdate> enabledUpdates,
        ValhallaTrafficWriteOptions enabledOptions,
        IReadOnlyList<ValhallaTrafficEdgeUpdate> closureOnlyUpdates,
        ValhallaTrafficWriteOptions closureOnlyOptions,
        CancellationToken cancellationToken);
}

public sealed record ValhallaTrafficWriteOptions(string OutputPath)
{
    /// <summary>Directory containing the immutable Valhalla graph tiles.</summary>
    public string? GraphTileDirectory { get; init; }

    /// <summary>Expected SHA-256 fingerprint of the target graph.</summary>
    public string? GraphSha256 { get; init; }

    public TrafficSnapshotPolicy Policy { get; init; } = TrafficSnapshotPolicy.Enabled;

    public DateTimeOffset? CreatedAtUtc { get; init; }

    public DateTimeOffset? ExpiresAtUtc { get; init; }
}

public sealed record ValhallaTrafficWriteResult(
    bool Succeeded,
    int UpdateCount,
    IReadOnlyList<TrafficProviderDiagnostic> Diagnostics)
{
    public TrafficSnapshotReference? Snapshot { get; init; }
}

public sealed record ValhallaTrafficSnapshotPairWriteResult(
    ValhallaTrafficWriteResult Enabled,
    ValhallaTrafficWriteResult ClosureOnly)
{
    public bool Succeeded =>
        Enabled.Succeeded
        && Enabled.Snapshot is not null
        && ClosureOnly.Succeeded
        && ClosureOnly.Snapshot is not null;
}
