using SharpNinja.Valhalla.Traffic.Providers;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Traffic;

public interface ITrafficDataFactory
{
    Task<NormalizedTrafficSnapshot> CreateSnapshotAsync(
        TrafficDataRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>UI-agnostic dependencies and policy for normalized traffic snapshot creation.</summary>
public sealed class TrafficDataFactoryOptions
{
    public TrafficPolicy TrafficPolicy { get; init; } = TrafficPolicy.Disabled;

    public TimeProvider TimeProvider { get; init; } = System.TimeProvider.System;

    public ITrafficEdgeMatcher? EdgeMatcher { get; init; }

    public ValhallaGraphTrafficContext? GraphContext { get; init; }

    public bool WriteTrafficTiles { get; init; }

    public IValhallaTrafficTileWriter? TileWriter { get; init; }

    public ValhallaTrafficWriteOptions? TileWriteOptions { get; init; }
}

/// <summary>
/// Fetches registered feeds, delegates normalization by registration, resolves matched-edge
/// conflicts, projects route modifiers, and optionally writes Valhalla traffic tile data.
/// </summary>
public sealed class TrafficDataFactory : ITrafficDataFactory
{
    private static readonly HashSet<string> TrustedDiagnosticCodes = new(
        StringComparer.Ordinal)
    {
        "ExpiredTrafficEvent",
        "MalformedTrafficPayload",
        "MalformedTrafficRecord",
        "TrafficCredentialConfigurationInvalid",
        "TrafficCredentialProviderFailed",
        "TrafficCredentialTransportInsecure",
        "TrafficCredentialUnavailable",
        "TrafficHttpFailure",
        "TrafficPayloadTooLarge",
        "TrafficProviderMismatch",
        "TrafficRequestConfigurationFailed",
        "TrafficTransportFailure",
        "UnsupportedTrafficSpeedUnit",
        "ValhallaTileWriteFailed",
    };

    private readonly TrafficDataSourceRegistration[] _sources;
    private readonly TrafficFeedAdapterRegistry _adapters;
    private readonly ITrafficConflictResolver _conflictResolver;
    private readonly TrafficPolicy _trafficPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly ITrafficEdgeMatcher? _edgeMatcher;
    private readonly ValhallaGraphTrafficContext? _graphContext;
    private readonly bool _writeTrafficTiles;
    private readonly IValhallaTrafficTileWriter? _tileWriter;
    private readonly ValhallaTrafficWriteOptions? _tileWriteOptions;

    public TrafficDataFactory(
        IReadOnlyList<TrafficDataSourceRegistration> sources,
        TrafficFeedAdapterRegistry adapters,
        ITrafficConflictResolver conflictResolver,
        TrafficDataFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(conflictResolver);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.TrafficPolicy);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        if ((options.EdgeMatcher is null) != (options.GraphContext is null))
        {
            throw new ArgumentException(
                "EdgeMatcher and GraphContext must be configured together.",
                nameof(options));
        }

        if (options.WriteTrafficTiles
            && options.TileWriter is not null
            && options.TileWriteOptions is null)
        {
            throw new ArgumentException(
                "TileWriteOptions are required when tile output has a configured writer.",
                nameof(options));
        }

        var registeredFeeds = new HashSet<(string ProviderId, TrafficFeedKind FeedKind)>(
            ProviderFeedComparer.Instance);
        foreach (TrafficDataSourceRegistration source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            foreach (TrafficFeedKind feedKind in source.FeedKinds)
            {
                if (!registeredFeeds.Add((source.Client.ProviderId, feedKind)))
                {
                    throw new ArgumentException(
                        $"Duplicate traffic source registration for '{source.Client.ProviderId}' and '{feedKind}'.",
                        nameof(sources));
                }
            }
        }

        _sources = sources.ToArray();
        _adapters = adapters;
        _conflictResolver = conflictResolver;
        _trafficPolicy = options.TrafficPolicy;
        _timeProvider = options.TimeProvider;
        _edgeMatcher = options.EdgeMatcher;
        _graphContext = options.GraphContext;
        _writeTrafficTiles = options.WriteTrafficTiles;
        _tileWriter = options.TileWriter;
        _tileWriteOptions = options.TileWriteOptions;
    }

    public async Task<NormalizedTrafficSnapshot> CreateSnapshotAsync(
        TrafficDataRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset evaluationTime = _timeProvider.GetUtcNow();
        var diagnostics = new List<TrafficProviderDiagnostic>();
        var sourceStatuses = new List<TrafficFeedSourceStatus>();
        var candidates = new List<TrafficConflictCandidate>();

        foreach (TrafficDataSourceRegistration source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TrafficFeedKind[] requestedKinds = source.FeedKinds
                .Where(request.Includes)
                .ToArray();
            if (requestedKinds.Length == 0)
            {
                continue;
            }

            var sourceDiagnostics = new List<TrafficProviderDiagnostic>();
            TrafficFeedFetchResult? fetchResult = null;
            try
            {
                fetchResult = await source.Client.FetchAsync(
                    new TrafficDataRequest(requestedKinds.ToHashSet()),
                    cancellationToken).ConfigureAwait(false);
                if (fetchResult is null)
                {
                    AddFactoryDiagnostic(
                        sourceDiagnostics,
                        "TrafficFeedClientReturnedNull",
                        source.Client.ProviderId,
                        requestedKinds[0],
                        "The traffic feed client returned no result.");
                }
                else
                {
                    foreach (TrafficProviderDiagnostic diagnostic in fetchResult.Diagnostics)
                    {
                        sourceDiagnostics.Add(SanitizeDiagnostic(
                            diagnostic,
                            source.Client.ProviderId));
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                foreach (TrafficFeedKind feedKind in requestedKinds)
                {
                    AddFactoryDiagnostic(
                        sourceDiagnostics,
                        "TrafficFeedFetchFailed",
                        source.Client.ProviderId,
                        feedKind,
                        "The traffic feed could not be fetched.");
                }
            }

            var acceptedByFeed = requestedKinds.ToDictionary(
                static kind => kind,
                static _ => 0);
            if (fetchResult is not null)
            {
                await NormalizePayloadsAsync(
                    source,
                    requestedKinds,
                    fetchResult.Payloads,
                    evaluationTime,
                    acceptedByFeed,
                    candidates,
                    sourceDiagnostics,
                    cancellationToken).ConfigureAwait(false);
            }

            diagnostics.AddRange(sourceDiagnostics);
            foreach (TrafficFeedKind feedKind in requestedKinds)
            {
                int payloadCount = fetchResult?.Payloads.Count(payload =>
                    payload.FeedKind == feedKind
                    && string.Equals(
                        payload.ProviderId,
                        source.Client.ProviderId,
                        StringComparison.OrdinalIgnoreCase)) ?? 0;
                string[] diagnosticCodes = sourceDiagnostics
                    .Where(diagnostic => diagnostic.FeedKind == feedKind)
                    .Select(static diagnostic => diagnostic.Code)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                sourceStatuses.Add(new TrafficFeedSourceStatus(
                    source.Client.ProviderId,
                    feedKind,
                    source.SourceKind,
                    payloadCount == 0 ? TrafficSourceKind.Unavailable : source.SourceKind,
                    payloadCount,
                    acceptedByFeed[feedKind],
                    diagnosticCodes));
            }
        }

        TrafficConflictResolutionResult resolved = _conflictResolver.Resolve(candidates);
        TrafficConflictResolutionEntry[] entries = resolved.Entries.ToArray();
        ValhallaTrafficEdgeUpdate[] resolvedEdgeUpdates = entries
            .SelectMany(static entry => entry.EdgeUpdates)
            .ToArray();
        TrafficRouteModifierSource[] modifierSources =
            BuildModifierSources(entries, candidates);
        RouteModifierImpact[] modifierImpacts = modifierSources
            .Select(static source => source.Impact)
            .ToArray();
        ValhallaTrafficEdgeUpdate[] effectiveEdgeUpdates =
            ApplyTrafficPolicy(resolvedEdgeUpdates);
        ValhallaTrafficEdgeUpdate[] tileWritableEdgeUpdates = effectiveEdgeUpdates
            .Where(static update => update.DirectionResolved)
            .ToArray();

        ValhallaTrafficWriteResult? writeResult = await WriteTilesAsync(
            tileWritableEdgeUpdates,
            diagnostics,
            cancellationToken).ConfigureAwait(false);

        return new NormalizedTrafficSnapshot(
            evaluationTime,
            entries.Select(static entry => entry.Event).ToArray(),
            modifierImpacts,
            modifierSources,
            effectiveEdgeUpdates,
            writeResult,
            diagnostics,
            sourceStatuses);
    }

    private TrafficRouteModifierSource[] BuildModifierSources(
        IReadOnlyList<TrafficConflictResolutionEntry> entries,
        IReadOnlyList<TrafficConflictCandidate> candidates)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        int[] parents = Enumerable.Range(0, candidates.Count).ToArray();
        var edgeOwners = new Dictionary<
            (RouteModifierImpactKind Kind, ulong CanonicalEdgeId, TrafficDirection Direction),
            int>();

        for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            TrafficConflictCandidate candidate = candidates[candidateIndex];
            RouteModifierImpactKind impactKind = GetImpactKind(candidate.Event.Kind);
            foreach (ValhallaTrafficEdgeUpdate edge in candidate.EdgeUpdates)
            {
                if (!edge.DirectionResolved)
                {
                    continue;
                }

                var edgeKey = (impactKind, edge.CanonicalDirectedEdgeId, edge.Direction);
                if (edgeOwners.TryGetValue(edgeKey, out int ownerIndex))
                {
                    Union(parents, candidateIndex, ownerIndex);
                }
                else
                {
                    edgeOwners.Add(edgeKey, candidateIndex);
                }
            }
        }

        TrafficRouteModifierSource[] projections = entries
            .Select(entry => TrafficRouteModifierProjection.Project(
                entry.Event,
                entry.EdgeUpdates,
                _trafficPolicy))
            .ToArray();
        int[] componentIds = entries
            .Select((entry, entryIndex) =>
            {
                int candidateIndex = FindCandidateIndex(candidates, entry.Event);
                return candidateIndex >= 0
                    ? Find(parents, candidateIndex)
                    : candidates.Count + entryIndex;
            })
            .ToArray();

        var result = new List<TrafficRouteModifierSource>(projections.Length);
        var emitted = new bool[projections.Length];
        for (int index = 0; index < projections.Length; index++)
        {
            if (emitted[index])
            {
                continue;
            }

            TrafficRouteModifierSource source = projections[index];
            if (IsEdgeSpecificConstraint(source))
            {
                emitted[index] = true;
                result.Add(source);
                continue;
            }

            int[] componentIndexes = Enumerable.Range(0, projections.Length)
                .Where(candidateIndex =>
                    !emitted[candidateIndex]
                    && componentIds[candidateIndex] == componentIds[index]
                    && projections[candidateIndex].Impact.Kind == source.Impact.Kind
                    && !IsEdgeSpecificConstraint(projections[candidateIndex]))
                .ToArray();
            foreach (int componentIndex in componentIndexes)
            {
                emitted[componentIndex] = true;
            }

            result.Add(componentIndexes.Length == 1
                ? source
                : AggregateComponentSources(
                    componentIndexes.Select(componentIndex => projections[componentIndex])));
        }

        return result.ToArray();
    }

    private static TrafficRouteModifierSource AggregateComponentSources(
        IEnumerable<TrafficRouteModifierSource> componentSources)
    {
        TrafficRouteModifierSource[] sources = componentSources.ToArray();
        TrafficRouteModifierSource first = sources[0];
        string[] providerIds = sources
            .SelectMany(static source => source.ProviderIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] sourceEventIds = sources
            .SelectMany(static source => source.SourceEventIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var seenEdges = new HashSet<
            (ulong EdgeId, TrafficDirection Direction, bool Resolved, string ProviderId, string SourceEventId)>();
        ValhallaTrafficEdgeUpdate[] affectedEdges = sources
            .SelectMany(static source => source.AffectedEdges)
            .Where(edge => seenEdges.Add((
                edge.CanonicalDirectedEdgeId,
                edge.Direction,
                edge.DirectionResolved,
                edge.DirectionResolved ? string.Empty : edge.ProviderId,
                edge.DirectionResolved ? string.Empty : edge.SourceEventId)))
            .ToArray();
        int? delaySeconds = sources
            .Where(static source => source.DelaySeconds.HasValue)
            .Select(static source => source.DelaySeconds)
            .DefaultIfEmpty()
            .Max();
        TrafficSeverity severity = sources.Max(static source => source.Severity);
        var impact = new RouteModifierImpact(
            first.Impact.RouteKey,
            first.Impact.Kind,
            $"Aggregated {sources.Length} overlapping traffic events without duplicating delay.",
            HardDeny: false);

        return new TrafficRouteModifierSource(
            impact,
            providerIds,
            sourceEventIds,
            affectedEdges,
            delaySeconds,
            severity);
    }

    private static bool IsEdgeSpecificConstraint(TrafficRouteModifierSource source) =>
        source.Impact.HardDeny || source.Impact.Kind == RouteModifierImpactKind.Restriction;

    private static RouteModifierImpactKind GetImpactKind(
        NormalizedTrafficEventKind eventKind) =>
        eventKind switch
        {
            NormalizedTrafficEventKind.Flow => RouteModifierImpactKind.TrafficDelay,
            NormalizedTrafficEventKind.Incident => RouteModifierImpactKind.Incident,
            NormalizedTrafficEventKind.Closure => RouteModifierImpactKind.RoadClosure,
            NormalizedTrafficEventKind.Restriction => RouteModifierImpactKind.Restriction,
            _ => RouteModifierImpactKind.Unknown,
        };

    private static int FindCandidateIndex(
        IReadOnlyList<TrafficConflictCandidate> candidates,
        NormalizedTrafficEvent trafficEvent)
    {
        for (int index = 0; index < candidates.Count; index++)
        {
            NormalizedTrafficEvent candidateEvent = candidates[index].Event;
            if (ReferenceEquals(candidateEvent, trafficEvent)
                || (string.Equals(candidateEvent.ProviderId, trafficEvent.ProviderId, StringComparison.Ordinal)
                    && string.Equals(candidateEvent.Id, trafficEvent.Id, StringComparison.Ordinal)))
            {
                return index;
            }
        }

        return -1;
    }

    private static int Find(int[] parents, int index)
    {
        while (parents[index] != index)
        {
            parents[index] = parents[parents[index]];
            index = parents[index];
        }

        return index;
    }

    private static void Union(int[] parents, int first, int second)
    {
        int firstRoot = Find(parents, first);
        int secondRoot = Find(parents, second);
        if (firstRoot != secondRoot)
        {
            parents[secondRoot] = firstRoot;
        }
    }

    private ValhallaTrafficEdgeUpdate[] ApplyTrafficPolicy(
        IReadOnlyList<ValhallaTrafficEdgeUpdate> updates)
    {
        bool includeDynamicTraffic =
            _trafficPolicy.IncludeTrafficDelayInEta;
        if (includeDynamicTraffic)
        {
            return updates.ToArray();
        }

        if (!_trafficPolicy.KeepClosuresAsRouteConstraints)
        {
            return [];
        }

        return updates
            .Where(static update => update.Closed && update.DirectionResolved)
            .Select(static update => new ValhallaTrafficEdgeUpdate(
                update.TileId,
                update.DirectedEdgeIndex,
                update.Direction,
                CurrentSpeedKph: null,
                FreeFlowSpeedKph: null,
                DelaySeconds: null,
                Closed: true,
                HasIncident: false,
                DirectionResolved: true,
                update.Confidence,
                update.SourceEventId,
                update.ProviderId,
                update.GraphDirectedEdgeId))
            .ToArray();
    }

    private async Task NormalizePayloadsAsync(
        TrafficDataSourceRegistration source,
        IReadOnlyCollection<TrafficFeedKind> requestedKinds,
        IReadOnlyList<RawTrafficFeedPayload> payloads,
        DateTimeOffset evaluationTime,
        IDictionary<TrafficFeedKind, int> acceptedByFeed,
        ICollection<TrafficConflictCandidate> candidates,
        ICollection<TrafficProviderDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (RawTrafficFeedPayload payload in payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!requestedKinds.Contains(payload.FeedKind))
            {
                continue;
            }

            if (!string.Equals(
                    source.Client.ProviderId,
                    payload.ProviderId,
                    StringComparison.OrdinalIgnoreCase))
            {
                AddFactoryDiagnostic(
                    diagnostics,
                    "TrafficPayloadProviderMismatch",
                    source.Client.ProviderId,
                    payload.FeedKind,
                    "The payload provider did not match its registered client.");
                continue;
            }

            RawTrafficFeedPayload safePayload = SanitizeRawPayload(
                payload,
                source.Client.ProviderId);

            if (!_adapters.TryResolve(source.Client.ProviderId, out ITrafficFeedAdapter? adapter)
                || adapter is null)
            {
                AddFactoryDiagnostic(
                    diagnostics,
                    "TrafficAdapterNotRegistered",
                    source.Client.ProviderId,
                    payload.FeedKind,
                    "No traffic feed adapter is registered for the provider.");
                continue;
            }

            TrafficFeedNormalizationResult normalized;
            try
            {
                normalized = await adapter.NormalizeAsync(
                    safePayload,
                    new TrafficNormalizationContext(
                        evaluationTime,
                        source.AllowNormalizedProxyExtensions),
                    cancellationToken).ConfigureAwait(false);
                if (normalized is null)
                {
                    AddFactoryDiagnostic(
                        diagnostics,
                        "TrafficPayloadNormalizationFailed",
                        source.Client.ProviderId,
                        payload.FeedKind,
                        "The traffic feed adapter returned no normalization result.");
                    continue;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                AddFactoryDiagnostic(
                    diagnostics,
                    "TrafficPayloadNormalizationFailed",
                    source.Client.ProviderId,
                    payload.FeedKind,
                    "The traffic payload could not be normalized.");
                continue;
            }

            foreach (TrafficProviderDiagnostic diagnostic in normalized.Diagnostics)
            {
                diagnostics.Add(SanitizeDiagnostic(
                    diagnostic,
                    source.Client.ProviderId));
            }

            foreach (NormalizedTrafficEvent trafficEvent in normalized.Events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(
                        trafficEvent.ProviderId,
                        source.Client.ProviderId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    AddFactoryDiagnostic(
                        diagnostics,
                        "TrafficNormalizedEventProviderMismatch",
                        source.Client.ProviderId,
                        payload.FeedKind,
                        "A normalized event did not match its registered provider.");
                    continue;
                }

                NormalizedTrafficEvent safeEvent = SanitizeNormalizedEvent(trafficEvent);
                if (safeEvent.ValidFromUtc is DateTimeOffset validFrom
                    && validFrom > evaluationTime)
                {
                    diagnostics.Add(new TrafficProviderDiagnostic(
                        "TrafficEventNotYetActive",
                        source.Client.ProviderId,
                        safePayload.FeedKind,
                        "A traffic event outside its active window was excluded.",
                        safeEvent.SourceUri?.OriginalString ?? "[traffic-data-factory]"));
                    continue;
                }

                if (safeEvent.ValidUntilUtc is DateTimeOffset validUntil
                    && validUntil <= evaluationTime)
                {
                    diagnostics.Add(new TrafficProviderDiagnostic(
                        "TrafficEventExpired",
                        source.Client.ProviderId,
                        safePayload.FeedKind,
                        "An expired traffic event was excluded.",
                        safeEvent.SourceUri?.OriginalString ?? "[traffic-data-factory]"));
                    continue;
                }

                IReadOnlyList<ValhallaTrafficEdgeUpdate> matches =
                    await MatchEdgesAsync(
                        safeEvent,
                        safePayload.FeedKind,
                        diagnostics,
                        cancellationToken).ConfigureAwait(false);
                candidates.Add(new TrafficConflictCandidate(safeEvent, matches));
                acceptedByFeed[safePayload.FeedKind]++;
            }
        }
    }

    private async Task<IReadOnlyList<ValhallaTrafficEdgeUpdate>> MatchEdgesAsync(
        NormalizedTrafficEvent trafficEvent,
        TrafficFeedKind feedKind,
        ICollection<TrafficProviderDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (_edgeMatcher is null || _graphContext is null)
        {
            return Array.Empty<ValhallaTrafficEdgeUpdate>();
        }

        try
        {
            IReadOnlyList<ValhallaTrafficEdgeUpdate>? matches =
                await _edgeMatcher.MatchAsync(
                    trafficEvent,
                    _graphContext,
                    cancellationToken).ConfigureAwait(false);
            return matches is null
                ? Array.Empty<ValhallaTrafficEdgeUpdate>()
                : Array.AsReadOnly(matches.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            diagnostics.Add(new TrafficProviderDiagnostic(
                "ValhallaTrafficEdgeMatchFailed",
                trafficEvent.ProviderId,
                feedKind,
                "The traffic event could not be matched to Valhalla edges.",
                trafficEvent.SourceUri?.OriginalString ?? "[traffic-data-factory]"));
            return Array.Empty<ValhallaTrafficEdgeUpdate>();
        }
    }

    private async Task<ValhallaTrafficWriteResult?> WriteTilesAsync(
        IReadOnlyList<ValhallaTrafficEdgeUpdate> updates,
        ICollection<TrafficProviderDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!_writeTrafficTiles)
        {
            return null;
        }

        if (_tileWriter is null)
        {
            AddFactoryDiagnostic(
                diagnostics,
                "ValhallaTileWriterNotConfigured",
                "valhalla",
                TrafficFeedKind.Composite,
                "Traffic tile output was requested without a configured writer.");
            return null;
        }

        try
        {
            ValhallaTrafficWriteResult? result = await _tileWriter.WriteAsync(
                Array.AsReadOnly(updates.ToArray()),
                _tileWriteOptions!,
                cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                AddFactoryDiagnostic(
                    diagnostics,
                    "ValhallaTileWriteFailed",
                    "valhalla",
                    TrafficFeedKind.Composite,
                    "The traffic tile writer returned no result.");
                return null;
            }

            var safeDiagnostics = result.Diagnostics
                .Select(static diagnostic => SanitizeDiagnostic(diagnostic, "valhalla"))
                .ToList();
            if (!result.Succeeded
                && !safeDiagnostics.Any(static diagnostic =>
                    diagnostic.Code == "ValhallaTileWriteFailed"))
            {
                safeDiagnostics.Add(new TrafficProviderDiagnostic(
                    "ValhallaTileWriteFailed",
                    "valhalla",
                    TrafficFeedKind.Composite,
                    "The traffic tile writer reported an unsuccessful result.",
                    "[traffic-data-factory]"));
            }

            foreach (TrafficProviderDiagnostic diagnostic in safeDiagnostics)
            {
                diagnostics.Add(diagnostic);
            }

            return new ValhallaTrafficWriteResult(
                result.Succeeded,
                result.UpdateCount,
                Array.AsReadOnly(safeDiagnostics.ToArray()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            AddFactoryDiagnostic(
                diagnostics,
                "ValhallaTileWriteFailed",
                "valhalla",
                TrafficFeedKind.Composite,
                "The traffic tile writer failed.");
            return null;
        }
    }

    private static RawTrafficFeedPayload SanitizeRawPayload(
        RawTrafficFeedPayload payload,
        string trustedProviderId)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedProviderId);

        Uri? safeSourceUri = payload.SourceUri is { IsAbsoluteUri: true } sourceUri
            && (sourceUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || sourceUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                ? new Uri(
                    TrafficDiagnosticRedaction.RedactUrl(sourceUri),
                    UriKind.Absolute)
                : null;

        return new RawTrafficFeedPayload(
            trustedProviderId,
            payload.FeedKind,
            SanitizeContentType(payload.ContentType),
            payload.Content.ToArray(),
            payload.FetchedAtUtc,
            safeSourceUri,
            SanitizeProviderMetadata(payload.ProviderMetadata));
    }

    private static string SanitizeContentType(string? contentType)
    {
        return System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(
                contentType,
                out System.Net.Http.Headers.MediaTypeHeaderValue? parsed)
            && !string.IsNullOrWhiteSpace(parsed.MediaType)
                ? parsed.MediaType.ToLowerInvariant()
                : "application/octet-stream";
    }

    private static IReadOnlyDictionary<string, string> SanitizeProviderMetadata(
        IReadOnlyDictionary<string, string>? metadata)
    {
        var safe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (metadata is null)
        {
            return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(safe);
        }

        foreach ((string key, string value) in metadata)
        {
            if ((key.Equals("speedUnit", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("unit", StringComparison.OrdinalIgnoreCase))
                && (value.Equals("mph", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("kmph", StringComparison.OrdinalIgnoreCase)))
            {
                safe["speedUnit"] = value.ToLowerInvariant();
                continue;
            }

            string? canonicalDateKey = key.ToLowerInvariant() switch
            {
                "date" => "Date",
                "last-modified" => "Last-Modified",
                "observedatutc" => "observedAtUtc",
                "observedat" => "observedAt",
                "updatedatutc" => "updatedAtUtc",
                "updatedat" => "updatedAt",
                _ => null,
            };
            if (canonicalDateKey is not null
                && DateTimeOffset.TryParse(value, out DateTimeOffset parsedDate))
            {
                safe[canonicalDateKey] = parsedDate.ToUniversalTime().ToString("O");
                continue;
            }

            string? canonicalOpaqueKey = key.ToLowerInvariant() switch
            {
                "trafficmodelid" => "TrafficModelID",
                "etag" => "ETag",
                _ => null,
            };
            if (canonicalOpaqueKey is not null && !string.IsNullOrWhiteSpace(value))
            {
                byte[] digest = System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(value));
                safe[canonicalOpaqueKey] =
                    $"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
            }
        }

        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(safe);
    }

    private static IReadOnlyDictionary<string, string> SanitizeEventReferences(
        IReadOnlyDictionary<string, string> references)
    {
        var safe = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in references)
        {
            string? canonicalKey = key.ToLowerInvariant() switch
            {
                "source-id" => "source-id",
                "originalid" => "originalId",
                "trafficmodelid" => "TrafficModelID",
                "etag" => "ETag",
                "frc" => "frc",
                "traversability" => "traversability",
                "type" => "type",
                "eventtype" => "eventType",
                "iconcategory" => "iconCategory",
                _ => null,
            };
            if (canonicalKey is null || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (value.StartsWith("sha256:", StringComparison.Ordinal)
                && value.Length == 71
                && value.AsSpan(7).IndexOfAnyExcept(
                    "0123456789abcdefABCDEF".AsSpan()) < 0)
            {
                safe[canonicalKey] = value.ToLowerInvariant();
                continue;
            }

            byte[] digest = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(value));
            safe[canonicalKey] =
                $"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
        }

        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(safe);
    }

    private static NormalizedTrafficEvent SanitizeNormalizedEvent(
        NormalizedTrafficEvent trafficEvent)
    {
        ArgumentNullException.ThrowIfNull(trafficEvent);
        return new NormalizedTrafficEvent(
            trafficEvent.Id,
            trafficEvent.ProviderId,
            trafficEvent.Kind,
            trafficEvent.Geometry,
            trafficEvent.CurrentSpeedKph,
            trafficEvent.FreeFlowSpeedKph,
            trafficEvent.CurrentTravelTimeSeconds,
            trafficEvent.FreeFlowTravelTimeSeconds,
            trafficEvent.DelaySeconds,
            trafficEvent.RoadClosure,
            trafficEvent.Severity,
            trafficEvent.Confidence,
            trafficEvent.Description,
            trafficEvent.ObservedAtUtc,
            trafficEvent.UpdatedAtUtc,
            trafficEvent.FetchedAtUtc,
            trafficEvent.ValidFromUtc,
            trafficEvent.ValidUntilUtc,
            trafficEvent.SourceUri,
            SanitizeEventReferences(trafficEvent.ProviderReferences),
            trafficEvent.RestrictionApplicability);
    }

    private static TrafficProviderDiagnostic SanitizeDiagnostic(
        TrafficProviderDiagnostic diagnostic,
        string trustedProviderId)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedProviderId);
        string safeSource = Uri.TryCreate(
            diagnostic.RedactedSourceUrl,
            UriKind.Absolute,
            out Uri? sourceUri)
            && (sourceUri.Scheme == Uri.UriSchemeHttp
                || sourceUri.Scheme == Uri.UriSchemeHttps)
                ? TrafficDiagnosticRedaction.RedactUrl(sourceUri)
                : "[redacted-traffic-source]";
        string safeCode = TrustedDiagnosticCodes.Contains(diagnostic.Code)
            ? diagnostic.Code
            : "TrafficProviderDiagnostic";
        return new TrafficProviderDiagnostic(
            safeCode,
            trustedProviderId,
            diagnostic.FeedKind,
            $"Traffic provider reported diagnostic '{safeCode}'.",
            safeSource,
            diagnostic.HttpStatusCode);
    }

    private static void AddFactoryDiagnostic(
        ICollection<TrafficProviderDiagnostic> diagnostics,
        string code,
        string providerId,
        TrafficFeedKind feedKind,
        string message)
        => diagnostics.Add(new TrafficProviderDiagnostic(
            code,
            providerId,
            feedKind,
            message,
            "[traffic-data-factory]"));

    private sealed class ProviderFeedComparer
        : IEqualityComparer<(string ProviderId, TrafficFeedKind FeedKind)>
    {
        public static ProviderFeedComparer Instance { get; } = new();

        public bool Equals(
            (string ProviderId, TrafficFeedKind FeedKind) left,
            (string ProviderId, TrafficFeedKind FeedKind) right)
            => left.FeedKind == right.FeedKind
               && string.Equals(
                   left.ProviderId,
                   right.ProviderId,
                   StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string ProviderId, TrafficFeedKind FeedKind) value)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.ProviderId),
                value.FeedKind);
    }
}
