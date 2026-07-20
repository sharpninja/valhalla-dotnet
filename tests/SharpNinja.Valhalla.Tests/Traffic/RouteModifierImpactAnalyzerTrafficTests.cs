using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class RouteModifierImpactAnalyzerTrafficTests
{
    [Fact]
    public void ProviderTrafficModifier_ExplainsNormalRouteExcludedOrDeprioritized()
    {
        var normal = Candidate(0, "Normal I-40", [100, 101, 102], durationSeconds: 600);
        var surviving = Candidate(1, "I-440", [200, 201, 202], durationSeconds: 660);
        var impact = new RouteModifierImpact(
            RouteIdentity.Create(normal),
            RouteModifierImpactKind.TrafficDelay,
            "TomTom reports a 12 minute delay",
            HardDeny: false);

        IReadOnlyList<RouteModifierAdvisory> advisories = RouteModifierImpactAnalyzer.FindSuppressedNormalRoutes(
            [normal, surviving],
            [surviving],
            [impact],
            RoutePreferenceGoal.Fastest,
            RoutePreferenceWeights.Balanced);

        RouteModifierAdvisory advisory = Assert.Single(advisories);
        Assert.Contains("traffic delay", advisory.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TomTom", advisory.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RouteIdentity_UsesDirectedEdgeSignatureAcrossReorderingAndLabelChanges()
    {
        var normalSurvivor = Candidate(0, "I-40", [10, 11, 12], durationSeconds: 600);
        var normalRemoved = Candidate(1, "I-40", [20, 21, 22], durationSeconds: 620);
        var modifiedSurvivor = Candidate(7, "Renamed route", [10, 11, 12], durationSeconds: 610);
        var removedImpact = new RouteModifierImpact(
            RouteIdentity.Create(normalRemoved),
            RouteModifierImpactKind.RoadClosure,
            "verified closure",
            HardDeny: true);

        IReadOnlyList<RouteModifierAdvisory> advisories = RouteModifierImpactAnalyzer.FindSuppressedNormalRoutes(
            [normalSurvivor, normalRemoved],
            [modifiedSurvivor],
            [removedImpact],
            RoutePreferenceGoal.Fastest,
            RoutePreferenceWeights.Balanced);

        RouteModifierAdvisory advisory = Assert.Single(advisories);
        Assert.Equal(RouteIdentity.Create(normalRemoved), advisory.RouteKey);
        Assert.DoesNotContain(advisories, item => item.RouteKey == RouteIdentity.Create(normalSurvivor));
    }

    [Fact]
    public void ProviderTrafficModifier_ExplainsRouteThatDropsInRank()
    {
        var normalI40 = Candidate(0, "I-40", [100, 101, 102], durationSeconds: 600);
        var normalI440 = Candidate(1, "I-440", [200, 201, 202], durationSeconds: 620);
        var delayedI40 = Candidate(0, "I-40", [100, 101, 102], durationSeconds: 800);
        var modifiedI440 = Candidate(1, "I-440", [200, 201, 202], durationSeconds: 620);
        var impact = new RouteModifierImpact(
            RouteIdentity.Create(normalI40),
            RouteModifierImpactKind.TrafficDelay,
            "TomTom reports a delay",
            HardDeny: false);

        IReadOnlyList<RouteModifierAdvisory> advisories =
            RouteModifierImpactAnalyzer.FindSuppressedNormalRoutes(
                [normalI40, normalI440],
                [delayedI40, modifiedI440],
                [impact],
                RoutePreferenceGoal.Fastest,
                RoutePreferenceWeights.Balanced);

        RouteModifierAdvisory advisory = Assert.Single(advisories);
        Assert.Equal(1, advisory.NormalRank);
        Assert.Equal(2, advisory.ModifiedRank);
        Assert.Contains("deprioritized", advisory.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FactoryProjectedTrafficSource_MatchesRouteByAffectedDirectedEdges()
    {
        var normalI40 = Candidate(0, "I-40", [100, 101, 102], durationSeconds: 600);
        var normalI440 = Candidate(1, "I-440", [200, 201, 202], durationSeconds: 620);
        NormalizedTrafficEvent trafficEvent = TrafficEvent();
        TrafficRouteModifierSource source = TrafficRouteModifierProjection.Project(
            trafficEvent,
            [
                new ValhallaTrafficEdgeUpdate(
                    TileId: 42,
                    DirectedEdgeIndex: 7,
                    Direction: TrafficDirection.Forward,
                    CurrentSpeedKph: 20,
                    FreeFlowSpeedKph: 80,
                    DelaySeconds: 720,
                    Closed: false,
                    HasIncident: false,
                    DirectionResolved: true,
                    Confidence: 0.9,
                    SourceEventId: trafficEvent.Id,
                    ProviderId: trafficEvent.ProviderId,
                    GraphDirectedEdgeId: 101),
            ],
            TrafficPolicy.Enabled);

        IReadOnlyList<RouteModifierAdvisory> advisories =
            RouteModifierImpactAnalyzer.FindTrafficAffectedNormalRoutes(
                [normalI40, normalI440],
                [normalI440],
                [source],
                RoutePreferenceGoal.Fastest,
                RoutePreferenceWeights.Balanced);

        RouteModifierAdvisory advisory = Assert.Single(advisories);
        Assert.Equal(RouteIdentity.Create(normalI40), advisory.RouteKey);
        RouteModifierImpact routedImpact = Assert.Single(advisory.Impacts);
        Assert.Equal(RouteIdentity.Create(normalI40), routedImpact.RouteKey);
        Assert.Contains("TomTom", advisory.Message, StringComparison.Ordinal);
    }

    private static NormalizedTrafficEvent TrafficEvent()
        => new(
            id: "event-1",
            providerId: "TomTom",
            kind: NormalizedTrafficEventKind.Flow,
            geometry: new TrafficGeometry(
                TrafficGeometryKind.LineString,
                [new GeoCoordinate(36.12, -86.70), new GeoCoordinate(36.13, -86.71)]),
            currentSpeedKph: 20,
            freeFlowSpeedKph: 80,
            currentTravelTimeSeconds: 900,
            freeFlowTravelTimeSeconds: 180,
            delaySeconds: 720,
            roadClosure: false,
            severity: TrafficSeverity.Heavy,
            confidence: 0.9,
            description: "TomTom reports a 12 minute delay",
            observedAtUtc: null,
            updatedAtUtc: null,
            fetchedAtUtc: DateTimeOffset.Parse("2026-07-18T12:00:00Z"),
            validFromUtc: null,
            validUntilUtc: null,
            sourceUri: new Uri("https://traffic.example.test/flow"),
            providerReferences: new Dictionary<string, string>());

    private static RouteCandidateMetrics Candidate(
        int index,
        string label,
        IReadOnlyList<ulong> edges,
        int durationSeconds)
        => new(
            ProviderId: "valhalla",
            Index: index,
            DistanceMeters: 10_000 + index,
            DurationSeconds: durationSeconds,
            RouteLabels: [label],
            RouteKey: $"legacy-{index}",
            DirectedEdgeIds: edges);
}
