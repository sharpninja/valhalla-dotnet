using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class TrafficRouteModifierProjectionTests
{
    [Fact]
    public void FlowDelay_CreatesTrafficDelayImpact()
    {
        TrafficRouteModifierSource source = TrafficRouteModifierProjection.Project(
            Event(NormalizedTrafficEventKind.Flow, delaySeconds: 180, roadClosure: false),
            [Update(closed: false, directionResolved: true)],
            TrafficPolicy.Enabled);

        Assert.Equal(RouteModifierImpactKind.TrafficDelay, source.Impact.Kind);
        Assert.Equal(180, source.DelaySeconds);
        Assert.False(source.Impact.HardDeny);
    }

    [Fact]
    public void ProviderIncident_CreatesIncidentImpact()
    {
        TrafficRouteModifierSource source = TrafficRouteModifierProjection.Project(
            Event(NormalizedTrafficEventKind.Incident, delaySeconds: 60, roadClosure: false),
            [Update(closed: false, directionResolved: true)],
            TrafficPolicy.Enabled);

        Assert.Equal(RouteModifierImpactKind.Incident, source.Impact.Kind);
    }

    [Fact]
    public void ProviderClosure_CreatesHardDenyRoadClosureImpact()
    {
        TrafficRouteModifierSource source = TrafficRouteModifierProjection.Project(
            Event(NormalizedTrafficEventKind.Closure, delaySeconds: null, roadClosure: true),
            [Update(closed: true, directionResolved: true)],
            TrafficPolicy.Enabled);

        Assert.Equal(RouteModifierImpactKind.RoadClosure, source.Impact.Kind);
        Assert.True(source.Impact.HardDeny);
    }

    [Fact]
    public void AmbiguousClosure_RemainsAdvisory()
    {
        TrafficRouteModifierSource source = TrafficRouteModifierProjection.Project(
            Event(NormalizedTrafficEventKind.Closure, delaySeconds: null, roadClosure: true),
            [Update(closed: false, directionResolved: false)],
            TrafficPolicy.Enabled);

        Assert.Equal(RouteModifierImpactKind.RoadClosure, source.Impact.Kind);
        Assert.False(source.Impact.HardDeny);
        Assert.Contains("direction", source.Impact.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrafficDisabled_KeepsClosureHardDeny()
    {
        TrafficRouteModifierSource source = TrafficRouteModifierProjection.Project(
            Event(NormalizedTrafficEventKind.Closure, delaySeconds: null, roadClosure: true),
            [Update(closed: true, directionResolved: true)],
            TrafficPolicy.Disabled);

        Assert.True(source.Impact.HardDeny);
    }

    private static NormalizedTrafficEvent Event(
        NormalizedTrafficEventKind kind,
        int? delaySeconds,
        bool roadClosure)
        => new(
            id: "event-1",
            providerId: "here",
            kind: kind,
            geometry: new TrafficGeometry(
                TrafficGeometryKind.LineString,
                [new GeoCoordinate(36.1, -86.7), new GeoCoordinate(36.2, -86.8)]),
            currentSpeedKph: 30,
            freeFlowSpeedKph: 60,
            currentTravelTimeSeconds: null,
            freeFlowTravelTimeSeconds: null,
            delaySeconds: delaySeconds,
            roadClosure: roadClosure,
            severity: roadClosure ? TrafficSeverity.Closed : TrafficSeverity.Heavy,
            confidence: 0.8,
            description: "Provider event",
            observedAtUtc: null,
            updatedAtUtc: null,
            fetchedAtUtc: DateTimeOffset.Parse("2026-07-18T12:00:00Z"),
            validFromUtc: null,
            validUntilUtc: null,
            sourceUri: new Uri("https://traffic.example.test/feed"),
            providerReferences: new Dictionary<string, string>());

    private static ValhallaTrafficEdgeUpdate Update(bool closed, bool directionResolved)
        => new(
            TileId: 42,
            DirectedEdgeIndex: 7,
            Direction: directionResolved ? TrafficDirection.Forward : TrafficDirection.Unknown,
            CurrentSpeedKph: 30,
            FreeFlowSpeedKph: 60,
            DelaySeconds: 180,
            Closed: closed,
            HasIncident: false,
            DirectionResolved: directionResolved,
            Confidence: 0.8,
            SourceEventId: "event-1",
            ProviderId: "here");
}
