using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Traffic;

/// <summary>Truthful origin classification for one configured traffic feed.</summary>
public enum TrafficSourceKind
{
    Unavailable = 0,
    Fixture = 1,
    Proxy = 2,
    DirectProvider = 3,
    Custom = 4,
}

/// <summary>
/// Per-provider, per-feed acquisition and normalization status. ConfiguredSource identifies
/// the intended origin; EffectiveSource becomes Unavailable only when no payload was acquired.
/// </summary>
public sealed record TrafficFeedSourceStatus
{
    public TrafficFeedSourceStatus(
        string providerId,
        TrafficFeedKind feedKind,
        TrafficSourceKind configuredSource,
        TrafficSourceKind effectiveSource,
        int payloadCount,
        int eventCount,
        IReadOnlyList<string> diagnosticCodes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(diagnosticCodes);
        if (!Enum.IsDefined(feedKind))
        {
            throw new ArgumentOutOfRangeException(nameof(feedKind));
        }

        if (!Enum.IsDefined(configuredSource))
        {
            throw new ArgumentOutOfRangeException(nameof(configuredSource));
        }

        if (!Enum.IsDefined(effectiveSource))
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveSource));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(payloadCount);
        ArgumentOutOfRangeException.ThrowIfNegative(eventCount);

        ProviderId = providerId.Trim();
        FeedKind = feedKind;
        ConfiguredSource = configuredSource;
        EffectiveSource = effectiveSource;
        PayloadCount = payloadCount;
        EventCount = eventCount;
        DiagnosticCodes = Array.AsReadOnly(diagnosticCodes.ToArray());
    }

    public string ProviderId { get; }

    public TrafficFeedKind FeedKind { get; }

    public TrafficSourceKind ConfiguredSource { get; }

    public TrafficSourceKind EffectiveSource { get; }

    public int PayloadCount { get; }

    public int EventCount { get; }

    public IReadOnlyList<string> DiagnosticCodes { get; }
}

/// <summary>One provider client plus the feeds and truthful source type it owns.</summary>
public sealed class TrafficDataSourceRegistration
{
    public TrafficDataSourceRegistration(
        ITrafficFeedClient client,
        TrafficSourceKind sourceKind,
        IReadOnlyCollection<TrafficFeedKind> feedKinds,
        bool allowNormalizedProxyExtensions = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(feedKinds);
        if (string.IsNullOrWhiteSpace(client.ProviderId))
        {
            throw new ArgumentException("A registered traffic client must expose a provider id.", nameof(client));
        }

        if (!Enum.IsDefined(sourceKind) || sourceKind == TrafficSourceKind.Unavailable)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceKind));
        }

        TrafficFeedKind[] copiedKinds = feedKinds.Distinct().ToArray();
        if (copiedKinds.Length == 0 || copiedKinds.Any(kind => !Enum.IsDefined(kind)))
        {
            throw new ArgumentException(
                "At least one valid traffic feed kind must be registered.",
                nameof(feedKinds));
        }

        Client = client;
        SourceKind = sourceKind;
        FeedKinds = Array.AsReadOnly(copiedKinds);
        AllowNormalizedProxyExtensions = allowNormalizedProxyExtensions;
    }

    public ITrafficFeedClient Client { get; }

    public TrafficSourceKind SourceKind { get; }

    public IReadOnlyList<TrafficFeedKind> FeedKinds { get; }

    /// <summary>
    /// Host-owned trust decision allowing the namespaced normalized-proxy extension envelope.
    /// This is never inferred from provider id or <see cref="SourceKind"/>.
    /// </summary>
    public bool AllowNormalizedProxyExtensions { get; }
}

/// <summary>Immutable provider-neutral factory output. All collections are defensive copies.</summary>
public sealed record NormalizedTrafficSnapshot
{
    public NormalizedTrafficSnapshot(
        DateTimeOffset createdAtUtc,
        IReadOnlyList<NormalizedTrafficEvent> events,
        IReadOnlyList<RouteModifierImpact> routeModifierImpacts,
        IReadOnlyList<TrafficRouteModifierSource> routeModifierSources,
        IReadOnlyList<ValhallaTrafficEdgeUpdate> valhallaEdgeUpdates,
        ValhallaTrafficWriteResult? valhallaWriteResult,
        IReadOnlyList<TrafficProviderDiagnostic> diagnostics,
        IReadOnlyList<TrafficFeedSourceStatus> sourceStatuses)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(routeModifierImpacts);
        ArgumentNullException.ThrowIfNull(routeModifierSources);
        ArgumentNullException.ThrowIfNull(valhallaEdgeUpdates);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(sourceStatuses);

        CreatedAtUtc = createdAtUtc;
        Events = Array.AsReadOnly(events.ToArray());
        RouteModifierImpacts = Array.AsReadOnly(routeModifierImpacts.ToArray());
        RouteModifierSources = Array.AsReadOnly(routeModifierSources.ToArray());
        ValhallaEdgeUpdates = Array.AsReadOnly(valhallaEdgeUpdates.ToArray());
        ValhallaWriteResult = CopyWriteResult(valhallaWriteResult);
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        SourceStatuses = Array.AsReadOnly(sourceStatuses.ToArray());
    }

    public DateTimeOffset CreatedAtUtc { get; }

    public IReadOnlyList<NormalizedTrafficEvent> Events { get; }

    public IReadOnlyList<RouteModifierImpact> RouteModifierImpacts { get; }

    public IReadOnlyList<TrafficRouteModifierSource> RouteModifierSources { get; }

    public IReadOnlyList<ValhallaTrafficEdgeUpdate> ValhallaEdgeUpdates { get; }

    public ValhallaTrafficWriteResult? ValhallaWriteResult { get; }

    public IReadOnlyList<TrafficProviderDiagnostic> Diagnostics { get; }

    public IReadOnlyList<TrafficFeedSourceStatus> SourceStatuses { get; }

    private static ValhallaTrafficWriteResult? CopyWriteResult(
        ValhallaTrafficWriteResult? result)
        => result is null
            ? null
            : new ValhallaTrafficWriteResult(
                result.Succeeded,
                result.UpdateCount,
                Array.AsReadOnly(result.Diagnostics.ToArray()))
            {
                Snapshot = result.Snapshot,
            };
}
