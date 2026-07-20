namespace SharpNinja.Valhalla.Traffic.Tiles;

/// <summary>Projects direction-aware spatial matches into Valhalla directed-edge traffic updates.</summary>
public sealed class ValhallaTrafficEdgeMatcher(IValhallaTrafficSpatialIndex spatialIndex)
    : ITrafficEdgeMatcher
{
    private readonly IValhallaTrafficSpatialIndex _spatialIndex =
        spatialIndex ?? throw new ArgumentNullException(nameof(spatialIndex));

    public async Task<IReadOnlyList<ValhallaTrafficEdgeUpdate>> MatchAsync(
        NormalizedTrafficEvent trafficEvent,
        ValhallaGraphTrafficContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trafficEvent);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<TrafficEdgeMatchCandidate> matches = await _spatialIndex
            .MatchAsync(trafficEvent.Geometry, context, cancellationToken)
            .ConfigureAwait(false);
        var updates = new ValhallaTrafficEdgeUpdate[matches.Count];
        for (int index = 0; index < matches.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TrafficEdgeMatchCandidate match = matches[index];
            bool directionSafeConstraint =
                match.DirectionResolved &&
                match.Direction != TrafficDirection.Unknown &&
                (trafficEvent.RoadClosure ||
                 (trafficEvent.Kind == NormalizedTrafficEventKind.Restriction &&
                  trafficEvent.RestrictionApplicability ==
                  TrafficRestrictionApplicability.UnconditionalAllVehicles));
            updates[index] = new ValhallaTrafficEdgeUpdate(
                match.Edge.TileId,
                match.Edge.DirectedEdgeIndex,
                match.Direction,
                trafficEvent.CurrentSpeedKph,
                trafficEvent.FreeFlowSpeedKph,
                trafficEvent.DelaySeconds,
                Closed: directionSafeConstraint,
                HasIncident: trafficEvent.Kind == NormalizedTrafficEventKind.Incident,
                DirectionResolved: match.DirectionResolved,
                Confidence: Math.Clamp(trafficEvent.Confidence, 0d, 1d),
                SourceEventId: trafficEvent.Id,
                ProviderId: trafficEvent.ProviderId,
                GraphDirectedEdgeId: match.Edge.CanonicalDirectedEdgeId);
        }

        return Array.AsReadOnly(updates);
    }
}
