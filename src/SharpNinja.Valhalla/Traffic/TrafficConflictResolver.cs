using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Traffic;

/// <summary>One normalized event and the directed edges matched to it before conflict resolution.</summary>
public sealed record TrafficConflictCandidate
{
    public TrafficConflictCandidate(
        NormalizedTrafficEvent trafficEvent,
        IReadOnlyList<ValhallaTrafficEdgeUpdate> edgeUpdates)
    {
        ArgumentNullException.ThrowIfNull(trafficEvent);
        ArgumentNullException.ThrowIfNull(edgeUpdates);
        Event = trafficEvent;
        EdgeUpdates = Array.AsReadOnly(edgeUpdates.ToArray());
    }

    public NormalizedTrafficEvent Event { get; }

    public IReadOnlyList<ValhallaTrafficEdgeUpdate> EdgeUpdates { get; }
}

/// <summary>One surviving event and only its surviving edge updates.</summary>
public sealed record TrafficConflictResolutionEntry
{
    public TrafficConflictResolutionEntry(
        NormalizedTrafficEvent trafficEvent,
        IReadOnlyList<ValhallaTrafficEdgeUpdate> edgeUpdates)
    {
        ArgumentNullException.ThrowIfNull(trafficEvent);
        ArgumentNullException.ThrowIfNull(edgeUpdates);
        Event = trafficEvent;
        EdgeUpdates = Array.AsReadOnly(edgeUpdates.ToArray());
    }

    public NormalizedTrafficEvent Event { get; }

    public IReadOnlyList<ValhallaTrafficEdgeUpdate> EdgeUpdates { get; }
}

public sealed record TrafficConflictResolutionResult
{
    public TrafficConflictResolutionResult(
        IReadOnlyList<TrafficConflictResolutionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = Array.AsReadOnly(entries.ToArray());
    }

    public IReadOnlyList<TrafficConflictResolutionEntry> Entries { get; }
}

public interface ITrafficConflictResolver
{
    IReadOnlyList<string> ProviderPriority { get; }

    TrafficConflictResolutionResult Resolve(
        IReadOnlyList<TrafficConflictCandidate> candidates);
}

/// <summary>
/// Resolves traffic conflicts per direction-safe Valhalla edge. Active closures win first;
/// remaining conflicts use confidence, explicit update/observation freshness, and provider priority.
/// </summary>
public sealed class TrafficConflictResolver : ITrafficConflictResolver
{
    private readonly IReadOnlyDictionary<string, int> _priority;

    public TrafficConflictResolver(IReadOnlyList<string> providerPriority)
    {
        ArgumentNullException.ThrowIfNull(providerPriority);
        var priorities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>(providerPriority.Count);
        foreach (string providerId in providerPriority)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
            string canonical = providerId.Trim();
            if (!priorities.TryAdd(canonical, priorities.Count))
            {
                throw new ArgumentException(
                    $"Provider priority contains duplicate id '{canonical}'.",
                    nameof(providerPriority));
            }

            ordered.Add(canonical);
        }

        _priority = priorities;
        ProviderPriority = Array.AsReadOnly(ordered.ToArray());
    }

    public IReadOnlyList<string> ProviderPriority { get; }

    public TrafficConflictResolutionResult Resolve(
        IReadOnlyList<TrafficConflictCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            return new TrafficConflictResolutionResult([]);
        }

        var survivingUpdates = new List<ValhallaTrafficEdgeUpdate>[candidates.Count];
        for (int index = 0; index < survivingUpdates.Length; index++)
        {
            ArgumentNullException.ThrowIfNull(candidates[index]);
            survivingUpdates[index] = [];
        }

        var resolvedGroups = new Dictionary<EdgeConflictKey, List<IndexedUpdate>>();
        for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            TrafficConflictCandidate candidate = candidates[candidateIndex];
            foreach (ValhallaTrafficEdgeUpdate update in candidate.EdgeUpdates)
            {
                if (!update.DirectionResolved)
                {
                    survivingUpdates[candidateIndex].Add(update);
                    continue;
                }

                var key = new EdgeConflictKey(
                    update.CanonicalDirectedEdgeId,
                    update.Direction,
                    ConflictLayer(candidates[candidateIndex].Event.Kind));
                if (!resolvedGroups.TryGetValue(key, out List<IndexedUpdate>? group))
                {
                    group = [];
                    resolvedGroups.Add(key, group);
                }

                group.Add(new IndexedUpdate(candidateIndex, update));
            }
        }

        foreach (List<IndexedUpdate> group in resolvedGroups.Values)
        {
            IndexedUpdate winner = group[0];
            for (int index = 1; index < group.Count; index++)
            {
                if (Compare(group[index], winner, candidates) < 0)
                {
                    winner = group[index];
                }
            }

            survivingUpdates[winner.CandidateIndex].Add(winner.Update);
        }

        var entries = new List<TrafficConflictResolutionEntry>(candidates.Count);
        for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            TrafficConflictCandidate candidate = candidates[candidateIndex];
            if (candidate.EdgeUpdates.Count == 0 || survivingUpdates[candidateIndex].Count > 0)
            {
                entries.Add(new TrafficConflictResolutionEntry(
                    candidate.Event,
                    survivingUpdates[candidateIndex]));
            }
        }

        return new TrafficConflictResolutionResult(entries);
    }

    private int Compare(
        IndexedUpdate left,
        IndexedUpdate right,
        IReadOnlyList<TrafficConflictCandidate> candidates)
    {
        bool leftClosure = IsDirectionSafeClosure(left.Update);
        bool rightClosure = IsDirectionSafeClosure(right.Update);
        int comparison = rightClosure.CompareTo(leftClosure);
        if (comparison != 0)
        {
            return comparison;
        }

        NormalizedTrafficEvent leftEvent = candidates[left.CandidateIndex].Event;
        NormalizedTrafficEvent rightEvent = candidates[right.CandidateIndex].Event;

        comparison = rightEvent.Confidence.CompareTo(leftEvent.Confidence);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Freshness(rightEvent).CompareTo(Freshness(leftEvent));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Priority(leftEvent.ProviderId).CompareTo(Priority(rightEvent.ProviderId));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = string.Compare(
            leftEvent.ProviderId,
            rightEvent.ProviderId,
            StringComparison.OrdinalIgnoreCase);
        return comparison != 0
            ? comparison
            : string.Compare(leftEvent.Id, rightEvent.Id, StringComparison.Ordinal);
    }

    private static bool IsDirectionSafeClosure(ValhallaTrafficEdgeUpdate update)
        => update.DirectionResolved && update.Closed;


    private int Priority(string providerId)
        => _priority.TryGetValue(providerId, out int priority)
            ? priority
            : int.MaxValue;

    private static DateTimeOffset Freshness(NormalizedTrafficEvent trafficEvent)
    {
        if (trafficEvent.UpdatedAtUtc is DateTimeOffset updated
            && trafficEvent.ObservedAtUtc is DateTimeOffset observed)
        {
            return updated >= observed ? updated : observed;
        }

        return trafficEvent.UpdatedAtUtc
            ?? trafficEvent.ObservedAtUtc
            ?? trafficEvent.FetchedAtUtc;
    }

    private static TrafficConflictLayer ConflictLayer(
        NormalizedTrafficEventKind eventKind)
        => eventKind switch
        {
            NormalizedTrafficEventKind.Flow or NormalizedTrafficEventKind.Closure =>
                TrafficConflictLayer.DynamicSpeedOrClosure,
            NormalizedTrafficEventKind.Incident => TrafficConflictLayer.Incident,
            NormalizedTrafficEventKind.Restriction => TrafficConflictLayer.Restriction,
            _ => TrafficConflictLayer.Other,
        };

    private enum TrafficConflictLayer
    {
        DynamicSpeedOrClosure = 0,
        Incident = 1,
        Restriction = 2,
        Other = 3,
    }

    private readonly record struct EdgeConflictKey(
        ulong CanonicalDirectedEdgeId,
        TrafficDirection Direction,
        TrafficConflictLayer Layer);

    private readonly record struct IndexedUpdate(
        int CandidateIndex,
        ValhallaTrafficEdgeUpdate Update);
}
