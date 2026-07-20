using System.Collections.ObjectModel;
using SharpNinja.Valhalla.Traffic.Providers;

namespace SharpNinja.Valhalla.Traffic;

/// <summary>Provider-neutral traffic event emitted by a registered feed adapter.</summary>
public sealed record NormalizedTrafficEvent
{
    public NormalizedTrafficEvent(
        string id,
        string providerId,
        NormalizedTrafficEventKind kind,
        TrafficGeometry geometry,
        double? currentSpeedKph,
        double? freeFlowSpeedKph,
        int? currentTravelTimeSeconds,
        int? freeFlowTravelTimeSeconds,
        int? delaySeconds,
        bool roadClosure,
        TrafficSeverity severity,
        double confidence,
        string? description,
        DateTimeOffset? observedAtUtc,
        DateTimeOffset? updatedAtUtc,
        DateTimeOffset fetchedAtUtc,
        DateTimeOffset? validFromUtc,
        DateTimeOffset? validUntilUtc,
        Uri? sourceUri,
        IReadOnlyDictionary<string, string> providerReferences,
        TrafficRestrictionApplicability restrictionApplicability =
            TrafficRestrictionApplicability.Unknown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(providerReferences);

        Id = id;
        ProviderId = providerId;
        Kind = kind;
        Geometry = geometry.Copy();
        CurrentSpeedKph = currentSpeedKph;
        FreeFlowSpeedKph = freeFlowSpeedKph;
        CurrentTravelTimeSeconds = currentTravelTimeSeconds;
        FreeFlowTravelTimeSeconds = freeFlowTravelTimeSeconds;
        DelaySeconds = delaySeconds;
        RoadClosure = roadClosure;
        Severity = severity;
        Confidence = confidence;
        Description = description;
        ObservedAtUtc = observedAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        FetchedAtUtc = fetchedAtUtc;
        ValidFromUtc = validFromUtc;
        ValidUntilUtc = validUntilUtc;
        SourceUri = RedactSourceUri(sourceUri);
        ProviderReferences = CopyReferences(providerReferences);
        RestrictionApplicability = restrictionApplicability;
    }

    public string Id { get; }
    public string ProviderId { get; }
    public NormalizedTrafficEventKind Kind { get; }
    public TrafficGeometry Geometry { get; }
    public double? CurrentSpeedKph { get; }
    public double? FreeFlowSpeedKph { get; }
    public int? CurrentTravelTimeSeconds { get; }
    public int? FreeFlowTravelTimeSeconds { get; }
    public int? DelaySeconds { get; }
    public bool RoadClosure { get; }
    public TrafficSeverity Severity { get; }
    public double Confidence { get; }
    public string? Description { get; }
    public DateTimeOffset? ObservedAtUtc { get; }
    public DateTimeOffset? UpdatedAtUtc { get; }
    public DateTimeOffset FetchedAtUtc { get; }
    public DateTimeOffset? ValidFromUtc { get; }
    public DateTimeOffset? ValidUntilUtc { get; }

    /// <summary>
    /// Provider endpoint provenance with query and fragment removed so credentials
    /// can never escape through a normalized event.
    /// </summary>
    public Uri? SourceUri { get; }

    public IReadOnlyDictionary<string, string> ProviderReferences { get; }

    /// <summary>
    /// Applicability of a normalized restriction. Only an explicitly unconditional
    /// all-vehicle restriction can block a route without vehicle context.
    /// </summary>
    public TrafficRestrictionApplicability RestrictionApplicability { get; }

    private static Uri? RedactSourceUri(Uri? sourceUri)
    {
        if (sourceUri is null || !sourceUri.IsAbsoluteUri)
        {
            return null;
        }

        return new Uri(
            TrafficDiagnosticRedaction.RedactUrl(sourceUri),
            UriKind.Absolute);
    }

    private static IReadOnlyDictionary<string, string> CopyReferences(
        IReadOnlyDictionary<string, string> providerReferences)
    {
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in providerReferences)
        {
            if (IsSensitiveReferenceKey(key))
            {
                continue;
            }

            copy.Add(
                key,
                Uri.TryCreate(value, UriKind.Absolute, out Uri? referenceUri) &&
                (referenceUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 referenceUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    ? TrafficDiagnosticRedaction.RedactUrl(referenceUri)
                    : value);
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    private static bool IsSensitiveReferenceKey(string key)
    {
        string normalized = new(
            key.Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());

        return normalized.Contains("authorization", StringComparison.Ordinal) ||
            normalized.Contains("credential", StringComparison.Ordinal) ||
            normalized.Contains("password", StringComparison.Ordinal) ||
            normalized.Contains("secret", StringComparison.Ordinal) ||
            normalized.Contains("token", StringComparison.Ordinal) ||
            normalized.Contains("cookie", StringComparison.Ordinal) ||
            normalized.Contains("header", StringComparison.Ordinal) ||
            normalized.Contains("query", StringComparison.Ordinal) ||
            normalized.EndsWith("key", StringComparison.Ordinal) ||
            normalized.EndsWith("url", StringComparison.Ordinal) ||
            normalized.EndsWith("uri", StringComparison.Ordinal);
    }
}

/// <summary>Geometry carried by normalized traffic data in WGS84 decimal degrees.</summary>
public sealed record TrafficGeometry
{
    public TrafficGeometry(
        TrafficGeometryKind kind,
        IReadOnlyList<GeoCoordinate> points,
        TrafficGeometryDirection direction = TrafficGeometryDirection.Unknown)
    {
        ArgumentNullException.ThrowIfNull(points);
        Kind = kind;
        Points = Array.AsReadOnly(points.ToArray());
        Direction = direction;
    }

    public TrafficGeometryKind Kind { get; }

    public IReadOnlyList<GeoCoordinate> Points { get; }

    /// <summary>
    /// States whether coordinate order is authoritative for traffic direction. Unknown is the safe
    /// default because provider display geometry is not necessarily travel-direction geometry.
    /// </summary>
    public TrafficGeometryDirection Direction { get; }

    internal TrafficGeometry Copy() => new(Kind, Points, Direction);
}

public enum TrafficGeometryKind
{
    Point = 0,
    LineString = 1,
}

/// <summary>Direction semantics explicitly asserted by the provider adapter or host.</summary>
public enum TrafficGeometryDirection
{
    /// <summary>Coordinate order does not safely identify a directed carriageway.</summary>
    Unknown = 0,

    /// <summary>Coordinate order follows the affected travel direction.</summary>
    AlongCoordinates = 1,

    /// <summary>The event explicitly affects both travel directions represented by the shape.</summary>
    BothDirections = 2,
}

public enum NormalizedTrafficEventKind
{
    Flow = 0,
    Incident = 1,
    Closure = 2,
    Restriction = 3,
}

/// <summary>Provider-asserted restriction applicability used for safe route constraints.</summary>
public enum TrafficRestrictionApplicability
{
    /// <summary>The provider did not supply enough scope to block a route safely.</summary>
    Unknown = 0,

    /// <summary>The restriction is unconditional and applies to every vehicle.</summary>
    UnconditionalAllVehicles = 1,

    /// <summary>The restriction is conditional on time, weather, permit, or another predicate.</summary>
    Conditional = 2,

    /// <summary>The restriction applies only to a vehicle class such as trucks.</summary>
    VehicleSpecific = 3,
}

public enum TrafficSeverity
{
    Unknown = 0,
    FreeFlow = 1,
    Minor = 2,
    Moderate = 3,
    Heavy = 4,
    Major = 5,
    Critical = 6,
    Closed = 7,
}

/// <summary>
/// Deterministic time boundary used to exclude provider events that have already
/// expired. Callers choose the evaluation instant so normalization is testable.
/// </summary>
public sealed record TrafficNormalizationContext(
    DateTimeOffset EvaluationTimeUtc,
    bool AllowNormalizedProxyExtensions = false);

public sealed record TrafficFeedNormalizationResult
{
    public TrafficFeedNormalizationResult(
        IReadOnlyList<NormalizedTrafficEvent> events,
        IReadOnlyList<TrafficProviderDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(diagnostics);
        Events = Array.AsReadOnly(events.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public IReadOnlyList<NormalizedTrafficEvent> Events { get; }

    public IReadOnlyList<TrafficProviderDiagnostic> Diagnostics { get; }

    public static TrafficFeedNormalizationResult Empty { get; } = new(
        Array.Empty<NormalizedTrafficEvent>(),
        Array.Empty<TrafficProviderDiagnostic>());
}
