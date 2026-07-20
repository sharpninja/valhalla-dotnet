using System.Net.Http;

namespace SharpNinja.Valhalla.Traffic;

/// <summary>Provider-neutral traffic-feed categories.</summary>
public enum TrafficFeedKind
{
    Flow,
    Incident,
    Closure,
    Restriction,
    Composite,
}

/// <summary>
/// Selects the feed kinds to fetch. A null or empty set fetches every configured feed.
/// </summary>
public sealed record TrafficDataRequest(IReadOnlySet<TrafficFeedKind>? FeedKinds = null)
{
    internal bool Includes(TrafficFeedKind feedKind)
        => FeedKinds is null || FeedKinds.Count == 0 || FeedKinds.Contains(feedKind);
}

/// <summary>
/// Raw bytes fetched from an exact provider endpoint, including non-secret provenance.
/// </summary>
public sealed record RawTrafficFeedPayload(
    string ProviderId,
    TrafficFeedKind FeedKind,
    string ContentType,
    ReadOnlyMemory<byte> Content,
    DateTimeOffset FetchedAtUtc,
    Uri? SourceUri,
    IReadOnlyDictionary<string, string> ProviderMetadata);

/// <summary>Result of fetching every endpoint selected for one provider.</summary>
public sealed record TrafficFeedFetchResult(
    IReadOnlyList<RawTrafficFeedPayload> Payloads,
    IReadOnlyList<TrafficProviderDiagnostic> Diagnostics);

/// <summary>Provider-neutral asynchronous feed client.</summary>
public interface ITrafficFeedClient
{
    /// <summary>Provider registration id owned by this client.</summary>
    string ProviderId { get; }

    /// <summary>Fetches the selected configured feeds.</summary>
    Task<TrafficFeedFetchResult> FetchAsync(
        TrafficDataRequest request,
        CancellationToken cancellationToken = default);
}
