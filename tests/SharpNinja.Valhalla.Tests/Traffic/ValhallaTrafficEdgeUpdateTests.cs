using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class ValhallaTrafficEdgeUpdateTests
{
    [Fact]
    public async Task FlowEvent_MapsToDirectedEdgeSpeedUpdate()
    {
        var matcher = Matcher(new TrafficEdgeMatchCandidate(
            new ValhallaTrafficEdgeReference(
                TileId: 42,
                DirectedEdgeIndex: 7,
                GraphDirectedEdgeId: 0x1234),
            TrafficDirection.Forward,
            DistanceMeters: 3,
            DirectionResolved: true));
        NormalizedTrafficEvent trafficEvent = Event(
            NormalizedTrafficEventKind.Flow,
            currentSpeedKph: 35,
            freeFlowSpeedKph: 70,
            delaySeconds: 120,
            roadClosure: false);

        IReadOnlyList<ValhallaTrafficEdgeUpdate> updates = await matcher.MatchAsync(
            trafficEvent,
            new ValhallaGraphTrafficContext("graph-v1"),
            TestContext.Current.CancellationToken);

        ValhallaTrafficEdgeUpdate update = Assert.Single(updates);
        Assert.Equal((ulong)42, update.TileId);
        Assert.Equal((uint)7, update.DirectedEdgeIndex);
        Assert.Equal(35, update.CurrentSpeedKph);
        Assert.Equal(70, update.FreeFlowSpeedKph);
        Assert.Equal(120, update.DelaySeconds);
        Assert.Equal("event-1", update.SourceEventId);
        Assert.Equal("tomtom", update.ProviderId);
        Assert.Equal(0x1234UL, update.CanonicalDirectedEdgeId);
        Assert.False(update.Closed);
    }

    [Fact]
    public async Task ClosureEvent_MapsToClosedDirectedEdgeUpdate()
    {
        var matcher = Matcher(new TrafficEdgeMatchCandidate(
            new ValhallaTrafficEdgeReference(88, 4),
            TrafficDirection.Reverse,
            DistanceMeters: 1,
            DirectionResolved: true));

        IReadOnlyList<ValhallaTrafficEdgeUpdate> updates = await matcher.MatchAsync(
            Event(NormalizedTrafficEventKind.Closure, null, null, null, roadClosure: true),
            new ValhallaGraphTrafficContext("graph-v1"),
            TestContext.Current.CancellationToken);

        ValhallaTrafficEdgeUpdate update = Assert.Single(updates);
        Assert.True(update.Closed);
        Assert.True(update.DirectionResolved);
        Assert.Equal(TrafficDirection.Reverse, update.Direction);
    }

    [Fact]
    public async Task AmbiguousClosureDirection_DoesNotApplyUnsafeHardDeny()
    {
        var matcher = Matcher(new TrafficEdgeMatchCandidate(
            new ValhallaTrafficEdgeReference(88, 4),
            TrafficDirection.Unknown,
            DistanceMeters: 1,
            DirectionResolved: false));

        IReadOnlyList<ValhallaTrafficEdgeUpdate> updates = await matcher.MatchAsync(
            Event(NormalizedTrafficEventKind.Closure, null, null, null, roadClosure: true),
            new ValhallaGraphTrafficContext("graph-v1"),
            TestContext.Current.CancellationToken);

        ValhallaTrafficEdgeUpdate update = Assert.Single(updates);
        Assert.False(update.Closed);
        Assert.False(update.DirectionResolved);
        Assert.Equal(TrafficDirection.Unknown, update.Direction);
    }

    private static ValhallaTrafficEdgeMatcher Matcher(params TrafficEdgeMatchCandidate[] candidates)
        => new(new StubSpatialIndex(candidates));

    private static NormalizedTrafficEvent Event(
        NormalizedTrafficEventKind kind,
        double? currentSpeedKph,
        double? freeFlowSpeedKph,
        int? delaySeconds,
        bool roadClosure)
        => new(
            id: "event-1",
            providerId: "tomtom",
            kind: kind,
            geometry: new TrafficGeometry(
                TrafficGeometryKind.LineString,
                [new GeoCoordinate(36.12, -86.70), new GeoCoordinate(36.13, -86.71)]),
            currentSpeedKph: currentSpeedKph,
            freeFlowSpeedKph: freeFlowSpeedKph,
            currentTravelTimeSeconds: null,
            freeFlowTravelTimeSeconds: null,
            delaySeconds: delaySeconds,
            roadClosure: roadClosure,
            severity: roadClosure ? TrafficSeverity.Closed : TrafficSeverity.Heavy,
            confidence: 0.9,
            description: null,
            observedAtUtc: null,
            updatedAtUtc: null,
            fetchedAtUtc: DateTimeOffset.Parse("2026-07-18T12:00:00Z"),
            validFromUtc: null,
            validUntilUtc: null,
            sourceUri: new Uri("https://traffic.example.test/flow"),
            providerReferences: new Dictionary<string, string>());

    private sealed class StubSpatialIndex(IReadOnlyList<TrafficEdgeMatchCandidate> candidates)
        : IValhallaTrafficSpatialIndex
    {
        public ValueTask<IReadOnlyList<TrafficEdgeMatchCandidate>> MatchAsync(
            TrafficGeometry geometry,
            ValhallaGraphTrafficContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(candidates);
        }
    }
}
