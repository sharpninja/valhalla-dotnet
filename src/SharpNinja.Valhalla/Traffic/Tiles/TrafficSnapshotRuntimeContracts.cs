namespace SharpNinja.Valhalla.Traffic.Tiles;

/// <summary>Identifies the traffic policy materialized by an immutable native traffic generation.</summary>
public enum TrafficSnapshotPolicy
{
    Enabled = 0,
    ClosureOnly = 1,
}

/// <summary>
/// Immutable reference to one content-addressed native traffic generation. The generation directory
/// remains valid while a store or reader lease is active.
/// </summary>
public sealed record TrafficSnapshotReference
{
    public TrafficSnapshotReference(
        string graphSha256,
        string version,
        string generationDirectory,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        TrafficSnapshotPolicy policy = TrafficSnapshotPolicy.Enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphSha256);
        if (graphSha256.Length != 64 || graphSha256.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Graph SHA-256 must be exactly 64 hexadecimal characters.", nameof(graphSha256));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationDirectory);
        if (expiresAtUtc <= createdAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                "Traffic snapshot expiry must be after creation.");
        }

        GraphSha256 = graphSha256.Trim().ToUpperInvariant();
        Version = version.Trim().ToLowerInvariant();
        GenerationDirectory = Path.GetFullPath(generationDirectory);
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        Policy = policy;
    }

    public string GraphSha256 { get; }

    public string Version { get; }

    public string GenerationDirectory { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public TrafficSnapshotPolicy Policy { get; }

    public bool IsExpired(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return ExpiresAtUtc <= timeProvider.GetUtcNow();
    }
}

public enum TrafficSnapshotFailureCode
{
    Missing = 1,
    Unreadable = 2,
    Incomplete = 3,
    Expired = 4,
    GraphMismatch = 5,
}

public sealed record TrafficSnapshotFailure(
    TrafficSnapshotFailureCode Code,
    string Message,
    string? SnapshotVersion = null);
