using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Traffic;

public sealed record TrafficSnapshotCoordinatorOptions(
    ValhallaTrafficWriteOptions EnabledWriteOptions,
    ValhallaTrafficWriteOptions ClosureOnlyWriteOptions,
    TimeProvider? TimeProvider = null);

public sealed record TrafficSnapshotRefreshResult(
    NormalizedTrafficSnapshot Snapshot,
    TrafficSnapshotReference EnabledSnapshot,
    TrafficSnapshotReference ClosureOnlySnapshot);

public interface ITrafficSnapshotCoordinator
{
    Task<TrafficSnapshotRefreshResult> RefreshAsync(
        TrafficDataRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Fetches and normalizes once, preserves individually unexpired last-known events only for failed
/// provider/feed scopes, then independently validates and publishes enabled and closure-only native
/// generations. A failed second publication fails the refresh; this type does not claim a cross-file
/// atomic pair transaction.
/// </summary>
public sealed class TrafficSnapshotCoordinator : ITrafficSnapshotCoordinator
{
    private readonly object _sync = new();
    private readonly ITrafficDataFactory _factory;
    private readonly IValhallaTrafficSnapshotPairWriter _writer;
    private readonly TrafficSnapshotCoordinatorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, Task<TrafficSnapshotRefreshResult>> _inflightByRequest =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, NormalizedTrafficSnapshot> _lastKnownByRequest =
        new(StringComparer.Ordinal);

    public TrafficSnapshotCoordinator(
        ITrafficDataFactory factory,
        IValhallaTrafficSnapshotPairWriter writer,
        TrafficSnapshotCoordinatorOptions options)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(options.EnabledWriteOptions);
        ArgumentNullException.ThrowIfNull(options.ClosureOnlyWriteOptions);
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
    }

    public Task<TrafficSnapshotRefreshResult> RefreshAsync(
        TrafficDataRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        string requestKey = CreateRequestKey(request);
        Task<TrafficSnapshotRefreshResult> shared;
        lock (_sync)
        {
            if (!_inflightByRequest.TryGetValue(requestKey, out shared!))
            {
                shared = RefreshCoreAsync(requestKey, request);
                _inflightByRequest.Add(requestKey, shared);
                _ = RemoveCompletedRefreshAsync(requestKey, shared);
            }
        }

        return shared.WaitAsync(cancellationToken);
    }

    private async Task RemoveCompletedRefreshAsync(
        string requestKey,
        Task<TrafficSnapshotRefreshResult> shared)
    {
        try
        {
            await shared.ConfigureAwait(false);
        }
        catch
        {
            // Every caller observes the original shared task; cleanup must run for all outcomes.
        }
        finally
        {
            lock (_sync)
            {
                if (_inflightByRequest.TryGetValue(requestKey, out Task<TrafficSnapshotRefreshResult>? current)
                    && ReferenceEquals(current, shared))
                {
                    _inflightByRequest.Remove(requestKey);
                }
            }
        }
    }

    private async Task<TrafficSnapshotRefreshResult> RefreshCoreAsync(
        string requestKey,
        TrafficDataRequest request)
    {
        NormalizedTrafficSnapshot fetched = await _factory.CreateSnapshotAsync(
            request,
            CancellationToken.None).ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        NormalizedTrafficSnapshot? previous;
        lock (_sync)
        {
            _lastKnownByRequest.TryGetValue(requestKey, out previous);
        }

        NormalizedTrafficSnapshot effective = MergeLastKnownOnFailure(fetched, previous, now);

        DateTimeOffset enabledExpiry = SelectGenerationExpiry(
            effective.Events,
            now,
            includeDynamicTraffic: true);
        DateTimeOffset closureExpiry = SelectGenerationExpiry(
            effective.Events,
            now,
            includeDynamicTraffic: false);

        ValhallaTrafficSnapshotPairWriteResult pair = await _writer.WritePairAsync(
            effective.ValhallaEdgeUpdates
                .Where(static update => update.DirectionResolved)
                .ToArray(),
            _options.EnabledWriteOptions with
            {
                Policy = TrafficSnapshotPolicy.Enabled,
                CreatedAtUtc = now,
                ExpiresAtUtc = enabledExpiry,
            },
            effective.ValhallaEdgeUpdates
                .Where(static update => update.DirectionResolved && update.Closed)
                .ToArray(),
            _options.ClosureOnlyWriteOptions with
            {
                Policy = TrafficSnapshotPolicy.ClosureOnly,
                CreatedAtUtc = now,
                ExpiresAtUtc = closureExpiry,
            },
            CancellationToken.None).ConfigureAwait(false);
        EnsurePublished(pair.Enabled, TrafficSnapshotPolicy.Enabled);
        EnsurePublished(pair.ClosureOnly, TrafficSnapshotPolicy.ClosureOnly);

        lock (_sync)
        {
            _lastKnownByRequest[requestKey] = effective;
        }

        return new TrafficSnapshotRefreshResult(
            effective,
            pair.Enabled.Snapshot!,
            pair.ClosureOnly.Snapshot!);
    }

    private static NormalizedTrafficSnapshot MergeLastKnownOnFailure(
        NormalizedTrafficSnapshot fetched,
        NormalizedTrafficSnapshot? lastKnown,
        DateTimeOffset now)
    {
        NormalizedTrafficSnapshot current = FilterExpired(fetched, now);
        if (lastKnown is null)
        {
            return current;
        }

        HashSet<string> failedScopes = fetched.SourceStatuses
            .Where(static status => status.EffectiveSource == TrafficSourceKind.Unavailable)
            .Select(static status => SourceScope(status.ProviderId, status.FeedKind))
            .ToHashSet(StringComparer.Ordinal);
        if (failedScopes.Count == 0)
        {
            // A successful empty feed is authoritative; never resurrect older provider events merely
            // because an unrelated record diagnostic was emitted.
            return current;
        }

        var events = current.Events.ToDictionary(
            static trafficEvent => EventKey(trafficEvent),
            StringComparer.Ordinal);
        var fallbackEventKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (NormalizedTrafficEvent previous in lastKnown.Events.Where(item => IsUnexpired(item, now)))
        {
            TrafficFeedKind feedKind = EventFeedKind(previous);
            if (failedScopes.Contains(SourceScope(previous.ProviderId, feedKind))
                || failedScopes.Contains(SourceScope(previous.ProviderId, TrafficFeedKind.Composite)))
            {
                string eventKey = EventKey(previous);
                if (events.TryAdd(eventKey, previous))
                {
                    fallbackEventKeys.Add(eventKey);
                }
            }
        }

        HashSet<string> retainedKeys = events.Keys.ToHashSet(StringComparer.Ordinal);
        Dictionary<string, NormalizedTrafficEventKind> eventKinds = events
            .ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.Kind,
                StringComparer.Ordinal);
        ValhallaTrafficEdgeUpdate[] edges = current.ValhallaEdgeUpdates
            .Concat(lastKnown.ValhallaEdgeUpdates.Where(
                edge => fallbackEventKeys.Contains(EdgeEventKey(edge))))
            .Where(edge => retainedKeys.Contains(EdgeEventKey(edge)))
            .GroupBy(edge => (
                edge.CanonicalDirectedEdgeId,
                edge.Direction,
                Layer: ConflictLayer(eventKinds[EdgeEventKey(edge)])))
            .Select(static group => group
                .OrderByDescending(static edge => edge.Closed)
                .ThenByDescending(static edge => edge.Confidence)
                .ThenBy(static edge => edge.ProviderId, StringComparer.Ordinal)
                .ThenBy(static edge => edge.SourceEventId, StringComparer.Ordinal)
                .First())
            .ToArray();

        return RebuildSnapshot(
            now,
            events.Values.ToArray(),
            edges,
            current.Diagnostics,
            current.SourceStatuses);
    }

    private static NormalizedTrafficSnapshot FilterExpired(
        NormalizedTrafficSnapshot snapshot,
        DateTimeOffset now)
    {
        NormalizedTrafficEvent[] events = snapshot.Events.Where(item => IsUnexpired(item, now)).ToArray();
        HashSet<string> keys = events.Select(EventKey).ToHashSet(StringComparer.Ordinal);
        ValhallaTrafficEdgeUpdate[] edges = snapshot.ValhallaEdgeUpdates
            .Where(edge => keys.Contains(EdgeEventKey(edge)))
            .ToArray();
        return RebuildSnapshot(
            snapshot.CreatedAtUtc,
            events,
            edges,
            snapshot.Diagnostics,
            snapshot.SourceStatuses);
    }

    private static NormalizedTrafficSnapshot RebuildSnapshot(
        DateTimeOffset createdAtUtc,
        IReadOnlyList<NormalizedTrafficEvent> events,
        IReadOnlyList<ValhallaTrafficEdgeUpdate> edges,
        IReadOnlyList<TrafficProviderDiagnostic> diagnostics,
        IReadOnlyList<TrafficFeedSourceStatus> statuses)
    {
        Dictionary<string, ValhallaTrafficEdgeUpdate[]> edgesByEvent = edges
            .GroupBy(EdgeEventKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.Ordinal);
        TrafficRouteModifierSource[] sources = events
            .Select(trafficEvent => TrafficRouteModifierProjection.Project(
                trafficEvent,
                edgesByEvent.GetValueOrDefault(EventKey(trafficEvent)) ?? [],
                TrafficPolicy.Enabled))
            .ToArray();
        return new NormalizedTrafficSnapshot(
            createdAtUtc,
            events,
            sources.Select(static source => source.Impact).ToArray(),
            sources,
            edges,
            null,
            diagnostics,
            statuses);
    }

    private static TrafficFeedKind EventFeedKind(NormalizedTrafficEvent trafficEvent) =>
        trafficEvent.Kind switch
        {
            NormalizedTrafficEventKind.Flow => TrafficFeedKind.Flow,
            NormalizedTrafficEventKind.Incident => TrafficFeedKind.Incident,
            NormalizedTrafficEventKind.Closure => TrafficFeedKind.Closure,
            NormalizedTrafficEventKind.Restriction => TrafficFeedKind.Restriction,
            _ => TrafficFeedKind.Composite,
        };

    private static string SourceScope(string providerId, TrafficFeedKind feedKind) =>
        providerId + "|" + ((int)feedKind).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string EdgeEventKey(ValhallaTrafficEdgeUpdate edge) =>
        edge.ProviderId + "|" + edge.SourceEventId;

    private static bool IsUnexpired(NormalizedTrafficEvent trafficEvent, DateTimeOffset now) =>
        EventExpiry(trafficEvent) > now;

    private static DateTimeOffset EventExpiry(NormalizedTrafficEvent trafficEvent)
    {
        if (trafficEvent.ValidUntilUtc is DateTimeOffset explicitExpiry)
        {
            return explicitExpiry;
        }

        TimeSpan lifetime = trafficEvent.Kind switch
        {
            NormalizedTrafficEventKind.Flow => TimeSpan.FromMinutes(2),
            NormalizedTrafficEventKind.Incident => TimeSpan.FromMinutes(5),
            NormalizedTrafficEventKind.Closure => TimeSpan.FromMinutes(15),
            NormalizedTrafficEventKind.Restriction => TimeSpan.FromMinutes(15),
            _ => TimeSpan.FromMinutes(2),
        };
        DateTimeOffset freshness = trafficEvent.UpdatedAtUtc
            ?? trafficEvent.ObservedAtUtc
            ?? trafficEvent.FetchedAtUtc;
        return freshness + lifetime;
    }

    private static DateTimeOffset SelectGenerationExpiry(
        IReadOnlyList<NormalizedTrafficEvent> events,
        DateTimeOffset now,
        bool includeDynamicTraffic)
    {
        DateTimeOffset[] expiries = events
            .Where(item => includeDynamicTraffic
                || item.Kind is NormalizedTrafficEventKind.Closure or NormalizedTrafficEventKind.Restriction)
            .Select(EventExpiry)
            .Where(expiry => expiry > now)
            .ToArray();
        return expiries.Length == 0
            ? now.Add(includeDynamicTraffic ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(15))
            : expiries.Min();
    }

    private static void EnsurePublished(
        ValhallaTrafficWriteResult result,
        TrafficSnapshotPolicy expectedPolicy)
    {
        if (!result.Succeeded
            || result.Snapshot is null
            || result.Snapshot.Policy != expectedPolicy)
        {
            throw new TrafficSnapshotStoreException(
                TrafficSnapshotFailureCode.Incomplete,
                $"The {expectedPolicy} traffic generation was not published.");
        }
    }

    private static string EventKey(NormalizedTrafficEvent trafficEvent) =>
        trafficEvent.ProviderId + "|" + trafficEvent.Id;

    private enum TrafficConflictLayer
    {
        DynamicSpeedOrClosure = 0,
        Incident = 1,
        Restriction = 2,
        Other = 3,
    }

    private static TrafficConflictLayer ConflictLayer(NormalizedTrafficEventKind eventKind)
        => eventKind switch
        {
            NormalizedTrafficEventKind.Flow or NormalizedTrafficEventKind.Closure =>
                TrafficConflictLayer.DynamicSpeedOrClosure,
            NormalizedTrafficEventKind.Incident => TrafficConflictLayer.Incident,
            NormalizedTrafficEventKind.Restriction => TrafficConflictLayer.Restriction,
            _ => TrafficConflictLayer.Other,
        };

    private static string CreateRequestKey(TrafficDataRequest request) =>
        request.FeedKinds is null || request.FeedKinds.Count == 0
            ? "*"
            : string.Join(
                ",",
                request.FeedKinds
                    .OrderBy(static kind => kind)
                    .Select(static kind => ((int)kind).ToString(
                        System.Globalization.CultureInfo.InvariantCulture)));
}
