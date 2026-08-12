using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Generations;

/// <summary>Schema versions understood by the distributed-generation contract.</summary>
public static class ValhallaGenerationSchema
{
    public const int CurrentVersion = 1;
}

public enum ValhallaGenerationFailureCode
{
    InvalidManifest = 1,
    UnsupportedSchema = 2,
    ArtifactSourceUnavailable = 3,
    ArtifactIntegrityMismatch = 4,
    ArtifactAcquisitionFailed = 5,
    IncompatibleGenerationSet = 6,
    BaseGraphUnavailable = 7,
    TrafficUnavailable = 8,
    ClosureUnavailable = 9,
}

public sealed class ValhallaGenerationException : Exception
{
    public ValhallaGenerationException(
        ValhallaGenerationFailureCode code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public ValhallaGenerationFailureCode Code { get; }
}

/// <summary>
/// Immutable identity for one regional Valhalla base-graph artifact.
/// Artifact URIs are deliberately cloud-neutral and credential-free.
/// </summary>
public sealed record ValhallaGraphGenerationManifest
{
    public ValhallaGraphGenerationManifest(
        int schemaVersion,
        string regionId,
        string generationId,
        string graphSha256,
        Uri artifactUri,
        string artifactSha256,
        long byteLength,
        DateTimeOffset createdAtUtc,
        DateTimeOffset osmSourceTimestampUtc,
        DateTimeOffset freshnessDeadlineUtc)
    {
        ValhallaGenerationValidation.ValidateSchema(schemaVersion);
        RegionId = ValhallaGenerationValidation.RequireIdentity(regionId, nameof(regionId));
        GenerationId = ValhallaGenerationValidation.RequireIdentity(generationId, nameof(generationId));
        GraphSha256 = ValhallaGenerationValidation.RequireSha256(graphSha256, nameof(graphSha256));
        ArtifactUri = ValhallaGenerationValidation.RequireArtifactUri(artifactUri, nameof(artifactUri));
        ArtifactSha256 = ValhallaGenerationValidation.RequireSha256(artifactSha256, nameof(artifactSha256));
        ByteLength = ValhallaGenerationValidation.RequirePositiveLength(byteLength, nameof(byteLength));
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        OsmSourceTimestampUtc = osmSourceTimestampUtc.ToUniversalTime();
        FreshnessDeadlineUtc = freshnessDeadlineUtc.ToUniversalTime();

        if (OsmSourceTimestampUtc > CreatedAtUtc || FreshnessDeadlineUtc <= CreatedAtUtc)
        {
            throw ValhallaGenerationValidation.Invalid(
                "Base graph timestamps must satisfy source <= creation < freshness deadline.");
        }

        SchemaVersion = schemaVersion;
    }

    public int SchemaVersion { get; }

    public string RegionId { get; }

    public string GenerationId { get; }

    public string GraphSha256 { get; }

    public Uri ArtifactUri { get; }

    public string ArtifactSha256 { get; }

    public long ByteLength { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset OsmSourceTimestampUtc { get; }

    public DateTimeOffset FreshnessDeadlineUtc { get; }
}

/// <summary>Immutable identity for one traffic-enabled or closure-only overlay artifact.</summary>
public sealed record ValhallaOverlayGenerationManifest
{
    public ValhallaOverlayGenerationManifest(
        int schemaVersion,
        string regionId,
        string generationId,
        string cohortId,
        string baseGenerationId,
        string graphSha256,
        TrafficSnapshotPolicy policy,
        Uri artifactUri,
        string artifactSha256,
        long byteLength,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset? trafficDataAsOfUtc,
        DateTimeOffset closureDataAsOfUtc,
        string trafficSourceVersion,
        string closureSourceVersion)
    {
        ValhallaGenerationValidation.ValidateSchema(schemaVersion);
        RegionId = ValhallaGenerationValidation.RequireIdentity(regionId, nameof(regionId));
        GenerationId = ValhallaGenerationValidation.RequireIdentity(generationId, nameof(generationId));
        CohortId = ValhallaGenerationValidation.RequireIdentity(cohortId, nameof(cohortId));
        BaseGenerationId = ValhallaGenerationValidation.RequireIdentity(
            baseGenerationId,
            nameof(baseGenerationId));
        GraphSha256 = ValhallaGenerationValidation.RequireSha256(graphSha256, nameof(graphSha256));
        ArtifactUri = ValhallaGenerationValidation.RequireArtifactUri(artifactUri, nameof(artifactUri));
        ArtifactSha256 = ValhallaGenerationValidation.RequireSha256(artifactSha256, nameof(artifactSha256));
        ByteLength = ValhallaGenerationValidation.RequirePositiveLength(byteLength, nameof(byteLength));
        TrafficSourceVersion = ValhallaGenerationValidation.RequireIdentity(
            trafficSourceVersion,
            nameof(trafficSourceVersion));
        ClosureSourceVersion = ValhallaGenerationValidation.RequireIdentity(
            closureSourceVersion,
            nameof(closureSourceVersion));
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        ExpiresAtUtc = expiresAtUtc.ToUniversalTime();
        TrafficDataAsOfUtc = trafficDataAsOfUtc?.ToUniversalTime();
        ClosureDataAsOfUtc = closureDataAsOfUtc.ToUniversalTime();

        if (ExpiresAtUtc <= CreatedAtUtc
            || ClosureDataAsOfUtc > CreatedAtUtc
            || TrafficDataAsOfUtc > CreatedAtUtc)
        {
            throw ValhallaGenerationValidation.Invalid(
                "Overlay timestamps must satisfy data-as-of <= creation < expiry.");
        }

        if (policy == TrafficSnapshotPolicy.Enabled && TrafficDataAsOfUtc is null)
        {
            throw ValhallaGenerationValidation.Invalid(
                "Traffic-enabled overlays require a traffic data-as-of timestamp.");
        }

        SchemaVersion = schemaVersion;
        Policy = policy;
    }

    public int SchemaVersion { get; }

    public string RegionId { get; }

    public string GenerationId { get; }

    public string CohortId { get; }

    public string BaseGenerationId { get; }

    public string GraphSha256 { get; }

    public TrafficSnapshotPolicy Policy { get; }

    public Uri ArtifactUri { get; }

    public string ArtifactSha256 { get; }

    public long ByteLength { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public DateTimeOffset? TrafficDataAsOfUtc { get; }

    public DateTimeOffset ClosureDataAsOfUtc { get; }

    public string TrafficSourceVersion { get; }

    public string ClosureSourceVersion { get; }
}

/// <summary>
/// One atomic generation cohort. Both overlay policies share the same base graph and closure source.
/// </summary>
public sealed record ValhallaGenerationCohortManifest
{
    public ValhallaGenerationCohortManifest(
        ValhallaGraphGenerationManifest baseGraph,
        ValhallaOverlayGenerationManifest trafficEnabled,
        ValhallaOverlayGenerationManifest closureOnly)
    {
        ArgumentNullException.ThrowIfNull(baseGraph);
        ArgumentNullException.ThrowIfNull(trafficEnabled);
        ArgumentNullException.ThrowIfNull(closureOnly);

        if (trafficEnabled.Policy != TrafficSnapshotPolicy.Enabled
            || closureOnly.Policy != TrafficSnapshotPolicy.ClosureOnly)
        {
            throw ValhallaGenerationValidation.Incompatible(
                "A cohort requires one traffic-enabled and one closure-only overlay.");
        }

        string[] regions = [baseGraph.RegionId, trafficEnabled.RegionId, closureOnly.RegionId];
        string[] baseIds =
        [
            baseGraph.GenerationId,
            trafficEnabled.BaseGenerationId,
            closureOnly.BaseGenerationId,
        ];
        string[] graphHashes =
        [
            baseGraph.GraphSha256,
            trafficEnabled.GraphSha256,
            closureOnly.GraphSha256,
        ];

        if (regions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1
            || baseIds.Distinct(StringComparer.Ordinal).Count() != 1
            || graphHashes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1
            || !string.Equals(
                trafficEnabled.CohortId,
                closureOnly.CohortId,
                StringComparison.Ordinal)
            || !string.Equals(
                trafficEnabled.ClosureSourceVersion,
                closureOnly.ClosureSourceVersion,
                StringComparison.Ordinal))
        {
            throw ValhallaGenerationValidation.Incompatible(
                "Generation cohort region, base generation, graph SHA, cohort, and closure source must match.");
        }

        BaseGraph = baseGraph;
        TrafficEnabled = trafficEnabled;
        ClosureOnly = closureOnly;
    }

    public ValhallaGraphGenerationManifest BaseGraph { get; }

    public ValhallaOverlayGenerationManifest TrafficEnabled { get; }

    public ValhallaOverlayGenerationManifest ClosureOnly { get; }

    public string CohortId => TrafficEnabled.CohortId;

    public string ClosureSourceVersion => TrafficEnabled.ClosureSourceVersion;
}

public sealed record ValhallaRouteGenerationStamp(
    string RegionId,
    string BaseGenerationId,
    string OverlayGenerationId,
    TrafficSnapshotPolicy OverlayPolicy,
    string CohortId,
    string GraphSha256,
    string TrafficSourceVersion,
    string ClosureSourceVersion);

internal static class ValhallaGenerationValidation
{
    private static readonly string[] SensitiveQueryNames =
    [
        "access_token",
        "api_key",
        "apikey",
        "auth",
        "authorization",
        "credential",
        "key",
        "password",
        "secret",
        "signature",
        "sig",
        "token",
        "x-goog-credential",
        "x-goog-signature",
    ];

    public static void ValidateSchema(int schemaVersion)
    {
        if (schemaVersion != ValhallaGenerationSchema.CurrentVersion)
        {
            throw new ValhallaGenerationException(
                ValhallaGenerationFailureCode.UnsupportedSchema,
                $"Unsupported Valhalla generation schema version {schemaVersion}.");
        }
    }

    public static string RequireIdentity(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(static character => char.IsControl(character)))
        {
            throw Invalid($"Required generation identity '{parameterName}' is invalid.");
        }

        return value.Trim();
    }

    public static string RequireSha256(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != 64
            || value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw Invalid($"'{parameterName}' must be exactly 64 hexadecimal characters.");
        }

        return value.ToUpperInvariant();
    }

    public static long RequirePositiveLength(long value, string parameterName)
    {
        if (value <= 0)
        {
            throw Invalid($"'{parameterName}' must be positive.");
        }

        return value;
    }

    public static Uri RequireArtifactUri(Uri value, string parameterName)
    {
        if (value is null || !value.IsAbsoluteUri)
        {
            throw Invalid($"'{parameterName}' must be an absolute URI.");
        }

        if (!string.IsNullOrEmpty(value.UserInfo) || ContainsCredentialQuery(value))
        {
            throw Invalid($"'{parameterName}' must not contain credentials.");
        }

        return value;
    }

    public static ValhallaGenerationException Invalid(string message) =>
        new(ValhallaGenerationFailureCode.InvalidManifest, message);

    public static ValhallaGenerationException Incompatible(string message) =>
        new(ValhallaGenerationFailureCode.IncompatibleGenerationSet, message);

    private static bool ContainsCredentialQuery(Uri value)
    {
        string query = value.Query.TrimStart('?');
        if (query.Length == 0)
        {
            return false;
        }

        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string name = Uri.UnescapeDataString(pair.Split('=', 2)[0]);
            if (SensitiveQueryNames.Any(
                    sensitive => name.Contains(sensitive, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
